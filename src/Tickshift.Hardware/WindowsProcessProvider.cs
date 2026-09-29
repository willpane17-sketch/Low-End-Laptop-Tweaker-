using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core;
using Tickshift.Core.Abstractions;
using Tickshift.Core.Errors;
using Tickshift.Core.Models;
using Tickshift.Hardware.Native;
using Tickshift.Security;

namespace Tickshift.Hardware;

/// <summary>
/// Process snapshotting with per-process CPU and I/O deltas. Uses PROCESS_QUERY_LIMITED_INFORMATION
/// only, so it works as a standard user; protected processes simply report fewer details.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessProvider : IProcessProvider
{
    private readonly Func<ProtectedApplicationMatcher> _matcher;
    private readonly ILogger _logger;
    private readonly Dictionary<int, (long CpuTime, ulong IoBytes, long Stamp)> _previous = new();
    private readonly Dictionary<string, string?> _publisherCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly int _ownPid = Environment.ProcessId;

    public WindowsProcessProvider(Func<ProtectedApplicationMatcher> matcher, ILogger<WindowsProcessProvider>? logger = null)
    {
        _matcher = matcher;
        _logger = logger ?? NullLogger<WindowsProcessProvider>.Instance;
    }

    public int GetProcessCount()
    {
        var processes = Process.GetProcesses();
        foreach (var p in processes) p.Dispose();
        return processes.Length;
    }

    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var windowed = GetPidsWithVisibleWindows();
            var matcher = _matcher();
            var result = new List<ProcessSnapshot>(256);
            var seen = new HashSet<int>();
            var logicalCpus = Environment.ProcessorCount;

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var pid = process.Id;
                        seen.Add(pid);
                        var name = process.ProcessName;
                        var sessionId = SafeGet(() => process.SessionId, -1);
                        var workingSet = SafeGet(() => process.WorkingSet64, 0L);
                        var privateBytes = SafeGet(() => process.PrivateMemorySize64, 0L);

                        string? path = null;
                        long cpuTime = -1;
                        ulong ioBytes = 0;
                        var hasIo = false;

                        if (pid > 4)
                        {
                            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
                            if (handle != IntPtr.Zero)
                            {
                                try
                                {
                                    path = QueryPath(handle);
                                    if (NativeMethods.GetProcessTimes(handle, out _, out _, out var kernel, out var user)) cpuTime = kernel + user;
                                    if (NativeMethods.GetProcessIoCounters(handle, out var io))
                                    {
                                        ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                                        hasIo = true;
                                    }
                                }
                                finally
                                {
                                    NativeMethods.CloseHandle(handle);
                                }
                            }
                        }

                        double cpuPercent = 0;
                        double? diskRate = null;
                        if (_previous.TryGetValue(pid, out var prev) && cpuTime >= 0)
                        {
                            var elapsedSeconds = (now - prev.Stamp) / (double)Stopwatch.Frequency;
                            if (elapsedSeconds > 0.05)
                            {
                                // FILETIME units are 100 ns.
                                cpuPercent = Math.Clamp((cpuTime - prev.CpuTime) / 1e7 / elapsedSeconds / logicalCpus * 100.0, 0, 100);
                                if (hasIo && ioBytes >= prev.IoBytes) diskRate = (ioBytes - prev.IoBytes) / elapsedSeconds;
                            }
                        }

                        if (cpuTime >= 0) _previous[pid] = (cpuTime, ioBytes, now);

                        var category = ProcessClassifier.Classify(name, path, sessionId, _ownPid, pid, matcher);
                        result.Add(new ProcessSnapshot(pid, name, path, path is null ? null : GetPublisher(path), Math.Round(cpuPercent, 1),
                            workingSet, privateBytes, diskRate, windowed.Contains(pid), sessionId, category));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Process exited during enumeration; ignore.
                    }
                }
            }

            foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(stale);
            return result;
        }
    }

    public OperationResult RequestGracefulClose(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var snapshot = GetProcesses().FirstOrDefault(p => p.Pid == pid);
            if (snapshot is null) return OperationResult.NotApplicable("The application has already closed.");
            if (!ProcessClassifier.MayOfferClose(snapshot))
                return OperationResult.Skipped($"{snapshot.Name} is protected and will not be closed by TICKSHIFT.");

            // CloseMainWindow sends the same request as clicking the window's close button, so the
            // application can prompt the user to save unsaved work. We never force-terminate.
            if (!process.CloseMainWindow())
                return OperationResult.Skipped($"{snapshot.Name} has no window that can be asked to close. Close it from the app itself.");

            if (process.WaitForExit(TimeSpan.FromSeconds(5)))
                return OperationResult.Success($"{snapshot.Name} closed.");

            return OperationResult.Skipped($"{snapshot.Name} was asked to close and may be waiting for you (for example, to save work).");
        }
        catch (ArgumentException)
        {
            return OperationResult.NotApplicable("The application has already closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Close request failed for pid {Pid}", pid);
            return OperationResult.FromException(ex, "Close application");
        }
    }

    private string? GetPublisher(string path)
    {
        if (_publisherCache.TryGetValue(path, out var cached)) return cached;
        string? publisher = null;
        try
        {
            publisher = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            if (string.IsNullOrEmpty(publisher)) publisher = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException) { }

        if (_publisherCache.Count > 4000) _publisherCache.Clear();
        _publisherCache[path] = publisher;
        return publisher;
    }

    private static string? QueryPath(IntPtr handle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>One EnumWindows pass instead of the expensive per-process MainWindowHandle lookup.</summary>
    private static HashSet<int> GetPidsWithVisibleWindows()
    {
        var pids = new HashSet<int>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) == IntPtr.Zero
                && NativeMethods.GetWindowTextLengthW(hwnd) > 0)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add(pid);
            }

            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return fallback; }
    }
}
