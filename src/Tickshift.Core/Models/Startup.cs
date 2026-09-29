namespace Tickshift.Core.Models;

public enum StartupLocation
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserStartupFolder,
    CommonStartupFolder,
}

public enum SignatureState { NotChecked, Valid, NotSigned, Invalid, Unknown }

public sealed record StartupEntry
{
    /// <summary>Stable identifier: "{Location}|{Name}".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Publisher { get; init; }
    public required StartupLocation Location { get; init; }
    public bool IsEnabled { get; init; }
    public SignatureState Signature { get; init; }
    public bool IsMicrosoftComponent { get; init; }
    public bool ExecutableExists { get; init; }
    public long? RunningMemoryBytes { get; init; }

    public bool RequiresElevation => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonStartupFolder;

    public static string MakeId(StartupLocation location, string name) => $"{location}|{name}";

    public string LocationDescription => Location switch
    {
        StartupLocation.UserRun => "Registry · current user",
        StartupLocation.MachineRun => "Registry · all users",
        StartupLocation.MachineRun32 => "Registry · all users (32-bit)",
        StartupLocation.UserStartupFolder => "Startup folder · current user",
        StartupLocation.CommonStartupFolder => "Startup folder · all users",
        _ => Location.ToString(),
    };
}
