using Tickshift.Core.Models;
using Tickshift.Diagnostics;
using Tickshift.Diagnostics.Hardware;

namespace Tickshift.Tests;

public class CpuModelParserTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz", "Core i3", 7, "7020", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Core(TM) i3-5005U CPU @ 2.00GHz", "Core i3", 5, "5005", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Core(TM) i3-2350M CPU @ 2.30GHz", "Core i3", 2, "2350", CpuSegment.Mobile)]
    [InlineData("Intel(R) Core(TM) i3-1005G1 CPU @ 1.20GHz", "Core i3", 10, "1005", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Core(TM) i3-10110U CPU @ 2.10GHz", "Core i3", 10, "10110", CpuSegment.LowPowerMobile)]
    [InlineData("11th Gen Intel(R) Core(TM) i3-1115G4 @ 3.00GHz", "Core i3", 11, "1115", CpuSegment.LowPowerMobile)]
    [InlineData("12th Gen Intel(R) Core(TM) i3-1215U", "Core i3", 12, "1215", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Core(TM) i3-4000M CPU @ 2.40GHz", "Core i3", 4, "4000", CpuSegment.Mobile)]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", "Core i5", 8, "8250", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", "Core i7", 8, "8750", CpuSegment.HighPerformanceMobile)]
    [InlineData("Intel(R) Core(TM) i3-10100 CPU @ 3.60GHz", "Core i3", 10, "10100", CpuSegment.Desktop)]
    [InlineData("Intel(R) Core(TM) i3-7100T CPU @ 3.40GHz", "Core i3", 7, "7100", CpuSegment.DesktopLowPower)]
    public void Parses_intel_core_models(string brand, string family, int generation, string model, CpuSegment segment)
    {
        var cpu = CpuModelParser.Parse(brand);
        Assert.Equal(CpuVendor.Intel, cpu.Vendor);
        Assert.Equal(family, cpu.Family);
        Assert.Equal(generation, cpu.Generation);
        Assert.Equal(model, cpu.ModelNumber);
        Assert.Equal(segment, cpu.Segment);
    }

    [Fact]
    public void Two_core_i3_chips_are_not_treated_as_identical()
    {
        var old = CpuModelParser.Parse("Intel(R) Core(TM) i3 CPU M 350 @ 2.27GHz");
        var modern = CpuModelParser.Parse("12th Gen Intel(R) Core(TM) i3-1215U");
        Assert.Equal(1, old.Generation);
        Assert.Equal(12, modern.Generation);
        Assert.NotEqual(CpuSegment.Unknown, old.Segment);
        Assert.NotEqual(old.Describe(), modern.Describe());
    }

    [Fact]
    public void Parses_n_series_core_i3() =>
        Assert.Equal(("N305", CpuSegment.LowPowerMobile), (CpuModelParser.Parse("Intel(R) Core(TM) i3-N305").ModelNumber, CpuModelParser.Parse("Intel(R) Core(TM) i3-N305").Segment));

    [Theory]
    [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", "Celeron", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Pentium(R) Silver N5000 CPU @ 1.10GHz", "Pentium", CpuSegment.LowPowerMobile)]
    [InlineData("Intel(R) Pentium(R) CPU 4415U @ 2.30GHz", "Pentium", CpuSegment.LowPowerMobile)]
    public void Parses_entry_level_intel(string brand, string family, CpuSegment segment)
    {
        var cpu = CpuModelParser.Parse(brand);
        Assert.Equal(family, cpu.Family);
        Assert.True(cpu.IsEntryLevel);
        Assert.Equal(segment, cpu.Segment);
    }

    [Theory]
    [InlineData("AMD Ryzen 3 3250U with Radeon Graphics", 3, 3, CpuSegment.LowPowerMobile)]
    [InlineData("AMD Ryzen 5 5500U with Radeon Graphics", 5, 5, CpuSegment.LowPowerMobile)]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics", 7, 5, CpuSegment.HighPerformanceMobile)]
    public void Parses_ryzen(string brand, int tier, int series, CpuSegment segment)
    {
        var cpu = CpuModelParser.Parse(brand);
        Assert.Equal(CpuVendor.Amd, cpu.Vendor);
        Assert.Equal(tier, cpu.Tier);
        Assert.Equal(series, cpu.Generation);
        Assert.Equal(segment, cpu.Segment);
    }

    [Fact]
    public void Parses_core_ultra() => Assert.Equal("Core Ultra", CpuModelParser.Parse("Intel(R) Core(TM) Ultra 5 125U").Family);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Some Future Processor 9000")]
    public void Unknown_strings_are_reported_honestly(string? brand)
    {
        var cpu = CpuModelParser.Parse(brand);
        Assert.Equal(CpuSegment.Unknown, cpu.Segment);
        Assert.Null(cpu.Generation);
    }

    [Theory]
    [InlineData("330", 1)]
    [InlineData("2350", 2)]
    [InlineData("8130", 8)]
    [InlineData("1005", 10)]
    [InlineData("1115", 11)]
    [InlineData("10110", 10)]
    [InlineData("12100", 12)]
    public void Intel_generation_rules(string model, int expected) => Assert.Equal(expected, CpuModelParser.IntelGeneration(model));
}

public class HardwareInterpreterTests
{
    private static readonly CpuInfo I3 = new("Intel(R) Core(TM) i3-7020U CPU @ 2.30GHz", "Intel", 2, 4, 2300);

    private static MemoryStatus Memory(double gib) => new((ulong)(gib * 1024 * 1024 * 1024), 1UL << 30, 60, 1, 2);

    [Theory]
    [InlineData(3.9, MemoryTier.VeryLow)]
    [InlineData(4.0, MemoryTier.VeryLow)]
    [InlineData(8.0, MemoryTier.Low)]
    [InlineData(16.0, MemoryTier.Adequate)]
    [InlineData(32.0, MemoryTier.Plenty)]
    public void Classifies_memory(double gib, MemoryTier tier) => Assert.Equal(tier, HardwareInterpreter.ClassifyMemory(gib));

    [Fact]
    public void Four_gb_recommends_upgrade_as_hardware_limitation()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(4), []);
        var insight = Assert.Single(profile.Insights, i => i.Title.Contains("memory"));
        Assert.True(insight.IsHardwareLimitation);
        Assert.Contains("8 GB", insight.UpgradeSuggestion);
    }

    [Fact]
    public void Hdd_system_drive_explains_ssd_upgrade()
    {
        var hdd = new StorageVolume("C:\\", "OS", "NTFS", 500UL << 30, 100UL << 30, true, StorageMediaKind.Rotational, "SATA");
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), [hdd]);
        var insight = Assert.Single(profile.Insights, i => i.Title.Contains("HDD"));
        Assert.Contains("SSD", insight.UpgradeSuggestion);
        Assert.Contains("cannot reproduce", insight.Explanation);
    }

    [Fact]
    public void Unknown_drive_type_is_not_guessed()
    {
        var unknown = new StorageVolume("C:\\", "OS", "NTFS", 500UL << 30, 100UL << 30, true, StorageMediaKind.Unknown, null);
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), [unknown]);
        Assert.Equal(StorageMediaKind.Unknown, profile.SystemDriveMedia);
        Assert.Contains(profile.Insights, i => i.Title.Contains("could not be confirmed"));
    }

    private static ObservationSummary Obs(double cpu, double p95, double mem, double commit, double? disk) =>
        new(20, TimeSpan.FromSeconds(20), cpu, p95, p95, mem, mem, commit, disk, null, null, [], []);

    [Fact]
    public void Identifies_memory_as_primary_limitation()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(4), []);
        var result = HardwareInterpreter.AssessLimitation(Obs(30, 45, 93, 95, 20), profile, null);
        Assert.Equal(PrimaryLimitation.Memory, result.Primary);
    }

    [Fact]
    public void Identifies_processor_as_primary_limitation()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), []);
        var result = HardwareInterpreter.AssessLimitation(Obs(92, 100, 50, 50, 10), profile, null);
        Assert.Equal(PrimaryLimitation.Processor, result.Primary);
    }

    [Fact]
    public void Identifies_thermal_limiting_from_passive_limit()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), []);
        var thermal = ThermalSnapshot.Unavailable("x", "y", new ThrottlingIndicators(60, 55));
        var result = HardwareInterpreter.AssessLimitation(Obs(40, 60, 40, 40, 5), profile, thermal);
        Assert.Equal(PrimaryLimitation.Thermal, result.Primary);
    }

    [Fact]
    public void Reports_no_bottleneck_when_idle()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), []);
        Assert.Equal(PrimaryLimitation.None, HardwareInterpreter.AssessLimitation(Obs(8, 15, 45, 40, 2), profile, null).Primary);
    }

    [Fact]
    public void Too_few_samples_is_undetermined()
    {
        var profile = HardwareInterpreter.Interpret(I3, Memory(8), []);
        var obs = Obs(95, 100, 95, 95, 95) with { SampleCount = 1 };
        Assert.Equal(PrimaryLimitation.Undetermined, HardwareInterpreter.AssessLimitation(obs, profile, null).Primary);
    }
}
