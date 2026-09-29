using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tickshift.Core.Abstractions;
using Tickshift.Hardware.Native;

namespace Tickshift.Hardware;

/// <summary>
/// Cheap, low-overhead counters. CPU uses GetSystemTimes deltas (a single syscall); disk activity uses
/// the PhysicalDisk "% Idle Time" counter, created lazily and disabled permanently if unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemSampler : ISystemSampler, IDisposable
{
    private readonly ILogger _logger;
    private long _lastIdle, _lastKernel, _lastUser;
    private bool _hasBaseline;
    private PerformanceCounter? _diskIdle;
    private bool _diskUnavailable;
    private readonly Lock _gate = new();

    public WindowsSystemSampler(ILogger<WindowsSystemSampler>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsSystemSampler>.Instance;
    }

    public double SampleCpuPercent()
    {
        lock (_gate)
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            if (!_hasBaseline)
            {
                (_lastIdle, _lastKernel, _lastUser, _hasBaseline) = (idle, kernel, user, true);
                return 0;
            }

            var idleDelta = idle - _lastIdle;
            var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
            (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
            if (totalDelta <= 0) return 0;
            return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
        }
    }

    public double? SampleDiskActivePercent()
    {
        lock (_gate)
        {
            if (_diskUnavailable) return null;
            try
            {
                if (_diskIdle is null)
                {
                    _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
                    _diskIdle.NextValue(); // first read establishes the baseline
                    return null;
                }

                return Math.Clamp(100.0 - _diskIdle.NextValue(), 0, 100);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                _logger.LogInformation("Disk activity counter unavailable: {Message}", ex.Message);
                _diskUnavailable = true;
                _diskIdle?.Dispose();
                _diskIdle = null;
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _diskIdle?.Dispose();
            _diskIdle = null;
        }
    }
}
