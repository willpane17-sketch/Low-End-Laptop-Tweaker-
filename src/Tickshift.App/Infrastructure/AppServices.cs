using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tickshift.Core.Abstractions;

namespace Tickshift.App.Infrastructure;

/// <summary>Marshals work to the UI thread at background priority so updates never block input.</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}

public sealed record ToastItem(string Title, string Body, NotificationKind Kind, DateTimeOffset At);

/// <summary>
/// Respects the user's notification settings, rate-limits each category, shows an in-app toast and,
/// when TICKSHIFT is not in the foreground, a Windows notification via the tray icon.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);
    private readonly ISettingsStore _settings;
    private readonly Dictionary<string, DateTimeOffset> _last = new();
    private readonly Lock _gate = new();

    public NotificationService(ISettingsStore settings) => _settings = settings;

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public TrayIcon? Tray { get; set; }

    public Func<bool>? IsAppInForeground { get; set; }

    public bool Notify(NotificationMessage message)
    {
        var n = _settings.Current.Notifications;
        var allowed = n.Enabled && message.Category switch
        {
            "memory" => n.MemoryPressure,
            "thermal" => n.Thermal,
            "storage" => n.Storage,
            "trading" => n.TradingMode,
            "recommendations" or "automation" => n.Recommendations,
            _ => true,
        };

        // In-app results of user actions ("general") are always shown; background alerts obey settings.
        if (!allowed && message.Category != "general") return false;

        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (message.Category != "general" && _last.TryGetValue(message.Category, out var last) && now - last < MinimumInterval) return false;
            _last[message.Category] = now;
        }

        Ui.Post(() =>
        {
            Toasts.Insert(0, new ToastItem(message.Title, message.Body, message.Kind, DateTimeOffset.Now));
            while (Toasts.Count > 3) Toasts.RemoveAt(Toasts.Count - 1);
            if (message.Category != "general" && IsAppInForeground?.Invoke() != true)
                Tray?.ShowBalloon(message.Title, message.Body, message.Kind is NotificationKind.Warning or NotificationKind.Error);
        });
        return true;
    }

    public void Dismiss(ToastItem item) => Ui.Post(() => Toasts.Remove(item));
}

public sealed record ConfirmRequest(
    string Title,
    string Message,
    IReadOnlyList<string>? Details = null,
    string ConfirmText = "Continue",
    string CancelText = "Cancel",
    bool Destructive = false,
    string? Acknowledgement = null);

public interface IDialogService
{
    bool Confirm(ConfirmRequest request);
    string? SaveFile(string title, string defaultName, string filter);
    string? OpenExecutable(string title);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(ConfirmRequest request)
    {
        var dialog = new Views.ConfirmDialog(request) { Owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null };
        return dialog.ShowDialog() == true;
    }

    public string? SaveFile(string title, string defaultName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultName,
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenExecutable(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

/// <summary>Ensures one instance; a second launch brings the existing window to the front.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TICKSHIFT.SingleInstance";
    private const string EventName = @"Local\TICKSHIFT.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Ui.Post(onActivate), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static SingleInstance? TryAcquire(Action onActivate)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!created)
        {
            activate.Set(); // ask the running instance to show itself
            activate.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

public static class ClipboardHelper
{
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50); // clipboard briefly locked by another app
            }
        }

        return false;
    }
}

public sealed class StartupArguments
{
    public bool Autostart { get; init; }
    public bool Minimised { get; init; }
    public bool DryRun { get; init; }

    public static StartupArguments Parse(IEnumerable<string> args)
    {
        var set = args.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
        return new StartupArguments
        {
            Autostart = set.Contains("--autostart"),
            Minimised = set.Contains("--minimised") || set.Contains("--minimized"),
            DryRun = set.Contains("--dry-run"),
        };
    }
}

public static class PathHelper
{
    public static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
