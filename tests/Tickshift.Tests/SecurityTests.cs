using System.Text;
using Tickshift.Core.Models;
using Tickshift.Core.Settings;
using Tickshift.Hardware;
using Tickshift.Security;
using Tickshift.Tests.Fakes;

namespace Tickshift.Tests;

public class SafePathPolicyTests
{
    [Fact]
    public void Allows_files_inside_the_root()
    {
        using var dir = new TempDir();
        var file = dir.File("a/b.tmp");
        Assert.True(new SafePathPolicy([dir.Path], []).IsDeletionAllowed(file, out _));
    }

    [Fact]
    public void Rejects_the_root_itself_and_traversal()
    {
        using var dir = new TempDir();
        var policy = new SafePathPolicy([dir.Path], []);
        Assert.False(policy.IsDeletionAllowed(dir.Path, out _));
        Assert.False(policy.IsDeletionAllowed(Path.Combine(dir.Path, "..", "escape.txt"), out var reason));
        Assert.Contains("Outside", reason);
    }

    [Fact]
    public void Rejects_forbidden_personal_folders_even_inside_a_root()
    {
        using var dir = new TempDir();
        var documents = Path.Combine(dir.Path, "Documents");
        var file = dir.File("Documents/cv.docx");
        Assert.False(new SafePathPolicy([dir.Path], [documents]).IsDeletionAllowed(file, out var reason));
        Assert.Contains("personal", reason);
    }

    [Fact]
    public void Rejects_paths_through_symbolic_links()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        outside.File("x.txt");
        var link = Path.Combine(dir.Path, "link");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        Assert.False(new SafePathPolicy([dir.Path], []).IsDeletionAllowed(Path.Combine(link, "x.txt"), out var reason));
        Assert.Contains("reparse", reason);
    }
}

public class ProtectionTests
{
    private static ProtectedApplicationMatcher Matcher(params ProtectedApplication[] apps) => new(apps);

    private static readonly ProtectedApplication Mt5 = new() { Id = "mt5", DisplayName = "MetaTrader 5", ExecutableName = "terminal64.exe" };

    [Theory]
    [InlineData("terminal64")]
    [InlineData("terminal64.exe")]
    [InlineData("TERMINAL64.EXE")]
    public void Matches_protected_apps_by_executable_name(string name) =>
        Assert.True(Matcher(Mt5).IsProtected(name, @"C:\Program Files\Some Broker MT5\terminal64.exe"));

    [Fact]
    public void Path_specific_entries_only_match_that_path()
    {
        var custom = new ProtectedApplication { Id = "c", DisplayName = "Broker", ExecutableName = "broker.exe", ExecutablePath = @"C:\Broker\broker.exe" };
        var m = Matcher(custom);
        Assert.True(m.IsProtected("broker", @"C:\Broker\broker.exe"));
        Assert.False(m.IsProtected("broker", @"C:\Other\broker.exe"));
        Assert.True(m.IsProtected("broker", null)); // path unreadable → err on the side of protection
    }

    [Fact]
    public void Disabled_entries_are_not_protected() =>
        Assert.False(Matcher(Mt5 with { Enabled = false }).IsProtected("terminal64", null));

    [Theory]
    [InlineData("csrss", ProcessCategory.Critical)]
    [InlineData("lsass", ProcessCategory.Critical)]
    [InlineData("explorer", ProcessCategory.Critical)]
    [InlineData("MsMpEng", ProcessCategory.Security)]
    [InlineData("terminal64", ProcessCategory.ProtectedTrading)]
    [InlineData("chrome", ProcessCategory.Browser)]
    [InlineData("spotify", ProcessCategory.Application)]
    public void Classifies_processes(string name, ProcessCategory expected) =>
        Assert.Equal(expected, ProcessClassifier.Classify(name, $@"C:\Apps\{name}.exe", 1, 1, 1234, Matcher(Mt5)));

    [Theory]
    [InlineData(ProcessCategory.Critical, false)]
    [InlineData(ProcessCategory.Security, false)]
    [InlineData(ProcessCategory.ProtectedTrading, false)]
    [InlineData(ProcessCategory.WindowsComponent, false)]
    [InlineData(ProcessCategory.Self, false)]
    [InlineData(ProcessCategory.Application, true)]
    [InlineData(ProcessCategory.Browser, true)]
    public void Only_ordinary_apps_may_be_offered_for_closing(ProcessCategory category, bool expected)
    {
        var p = FakeProcessProvider.Proc("someapp", category: category);
        Assert.Equal(expected, ProcessClassifier.MayOfferClose(p));
    }

    [Fact]
    public void Security_process_names_are_never_closeable_even_if_misclassified()
    {
        var p = FakeProcessProvider.Proc("MsMpEng", category: ProcessCategory.Application);
        Assert.False(ProcessClassifier.MayOfferClose(p));
    }
}

public class ElevatedContractTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Accepts_the_restore_point_request() =>
        Assert.True(ElevatedArgumentValidator.TryParse(["restore-point"], out var r, out _) && r!.Kind == ElevatedOperationKind.CreateRestorePoint);

    [Fact]
    public void Round_trips_a_startup_approval_request()
    {
        var value = StartupApprovalValue.Create(false, DateTimeOffset.UnixEpoch.AddYears(50));
        var op = ElevatedOperation.SetMachineStartupApproval("Run", "OneDrive Setup", value);
        Assert.True(ElevatedArgumentValidator.TryParse(op.Arguments, out var parsed, out _));
        Assert.Equal("OneDrive Setup", parsed!.ValueName);
        Assert.Equal(value, parsed.Value);
    }

    [Theory]
    [InlineData("cmd|/c|whoami")]
    [InlineData("restore-point|extra")]
    [InlineData("startup-approval|RunOnce|YQ==|delete")]
    [InlineData("startup-approval|Run|not base64!|delete")]
    [InlineData("startup-approval|Run|YQ==|0300000000000000000000")]
    [InlineData("startup-approval|Run|YQ==|FF0000000000000000000000")]
    [InlineData("")]
    public void Rejects_anything_outside_the_contract(string joined) =>
        Assert.False(ElevatedArgumentValidator.TryParse(joined.Length == 0 ? [] : joined.Split('|'), out _, out _));

    [Fact]
    public void Rejects_names_with_path_separators_or_control_characters()
    {
        Assert.False(ElevatedArgumentValidator.TryParse(["startup-approval", "Run", B64(@"..\..\evil"), "delete"], out _, out _));
        Assert.False(ElevatedArgumentValidator.TryParse(["startup-approval", "Run", B64("a\nb"), "delete"], out _, out _));
    }

    [Fact]
    public void Approval_encoding_matches_task_manager_semantics()
    {
        Assert.True(StartupApprovalValue.IsEnabled(null));
        Assert.True(StartupApprovalValue.IsEnabled(StartupApprovalValue.Create(true, DateTimeOffset.Now)));
        Assert.False(StartupApprovalValue.IsEnabled(StartupApprovalValue.Create(false, DateTimeOffset.Now)));
        Assert.False(StartupApprovalValue.IsEnabled([0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(12, StartupApprovalValue.Create(false, DateTimeOffset.Now).Length);
    }
}

public class LogRedactorTests
{
    [Fact]
    public void Removes_urls_emails_and_secrets()
    {
        var text = LogRedactor.Redact("Opened https://www.tradingview.com/chart/abc?symbol=EURUSD for trader@example.com password=hunter2");
        Assert.DoesNotContain("chart/abc", text);
        Assert.DoesNotContain("EURUSD", text);
        Assert.DoesNotContain("trader@example.com", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("www.tradingview.com", text);
    }

    [Fact]
    public void Removes_the_user_profile_path()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return;
        Assert.DoesNotContain(profile, LogRedactor.Redact($"Deleted {profile}/AppData/Local/Temp/x.tmp"));
    }
}

public class CommandLineParserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --min", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /background", "C:\\Tools\\tool.exe")]
    [InlineData("C:\\Tools\\thing", "C:\\Tools\\thing")]
    [InlineData("", null)]
    public void Extracts_the_executable(string command, string? expected) => Assert.Equal(expected, CommandLineParser.ExtractExecutable(command));
}
