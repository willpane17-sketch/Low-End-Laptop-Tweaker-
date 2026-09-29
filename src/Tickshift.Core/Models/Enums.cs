namespace Tickshift.Core.Models;

public enum RiskLevel { Low, Moderate, Elevated }

public enum PrivilegeLevel { StandardUser, Administrator }

public enum Reversibility { FullyReversible, PartiallyReversible, NotReversible }

[Flags]
public enum OptimisationPreset
{
    None = 0,
    Safe = 1,
    Balanced = 2,
    Trading = 4,
    All = Safe | Balanced | Trading,
}

public enum Severity { Info, Low, Medium, High, Critical }

public enum ThemePreference { Dark, Light, System }

public enum PrimaryUse { Trading, Productivity, Browsing, Balanced }

public enum SystemCondition { Unknown, Good, Fair, Strained, Critical }

public enum ThermalState { Unknown, Normal, Elevated, High, Critical }
