using System.Globalization;
using System.Text;
using System.Text.Json;
using Tickshift.Core;
using Tickshift.Core.Settings;
using Tickshift.Diagnostics;
using Tickshift.Persistence;
using Tickshift.Security;

namespace Tickshift.Optimization.Reports;

/// <summary>
/// Produces a human-readable diagnostic report for support or personal records. Personal data is
/// redacted: no user name, machine name, browsing activity, window titles or account information.
/// </summary>
public sealed class DiagnosticReportExporter(IChangeJournal journal, RecordStore<OptimisationRunRecord> runs)
{
    public string BuildText(DiagnosticReport? report)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine($"{Brand.ProductName} diagnostic report");
        sb.AppendLine($"Version {Brand.Version} · generated {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", c)}");
        sb.AppendLine(new string('=', 60));

        if (report is not null)
        {
            var s = report.Snapshot;
            sb.AppendLine();
            sb.AppendLine("SYSTEM");
            sb.AppendLine($"  Processor: {s.Cpu.Name} ({s.Cpu.PhysicalCores} cores / {s.Cpu.LogicalProcessors} threads)");
            sb.AppendLine($"  Interpreted: {report.Hardware.Cpu.Describe()}");
            sb.AppendLine($"  Memory: {Format.Bytes(s.Memory.TotalPhysicalBytes)} ({report.Hardware.Memory})");
            sb.AppendLine($"  Windows: {s.Os.FriendlyName} {s.Os.Edition}");
            foreach (var v in s.Storage)
                sb.AppendLine($"  Drive {v.RootPath} {v.MediaKind} {v.BusType}: {Format.Bytes(v.FreeBytes)} free of {Format.Bytes(v.TotalBytes)}");
            sb.AppendLine($"  Power: {s.Power.Describe()} · plan {s.Power.ActivePlan?.Name ?? "unknown"}" + (s.Power.Health?.HealthPercent is { } h ? $" · battery health {h:0}%" : string.Empty));
            sb.AppendLine($"  Temperature: {(s.Thermal.PrimaryCelsius is { } t ? $"{t:0} °C ({s.Thermal.Primary?.SensorName})" : "Sensor unavailable")}");
            sb.AppendLine($"  Uptime: {Format.Duration(s.Uptime)} · processes: {s.Processes.Count} · enabled third-party startup apps: {s.EnabledThirdPartyStartupCount}");

            var o = report.Observation;
            sb.AppendLine();
            sb.AppendLine($"OBSERVATION ({o.SampleCount} samples over {o.Duration.TotalSeconds:0}s)");
            sb.AppendLine($"  CPU avg {o.AverageCpu:0}% · p95 {o.P95Cpu:0}% · memory avg {o.AverageMemory:0}% · commit {o.CommitPercent:0}%" +
                          (o.AverageDiskActive is { } d ? $" · disk busy {d:0}%" : string.Empty));
            sb.AppendLine($"  Primary limitation: {report.Limitation.Primary} — {report.Limitation.Explanation}");
            sb.AppendLine($"  Condition: {report.Condition.Condition} — {report.Condition.Summary}");

            sb.AppendLine();
            sb.AppendLine("RECOMMENDATIONS");
            foreach (var r in report.Recommendations)
            {
                sb.AppendLine($"  • {r.Issue} [{r.Severity}]");
                sb.AppendLine($"    Impact: {r.Impact}");
                sb.AppendLine($"    Suggested action: {r.SuggestedAction}");
                sb.AppendLine($"    Why: {r.Evidence}");
                sb.AppendLine($"    Risk: {r.RiskText} · Reversible: {r.ReversibleText}");
            }

            if (report.Limitations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("NOT MEASURED");
                foreach (var l in report.Limitations) sb.AppendLine($"  - {l}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("CHANGE HISTORY (most recent 50)");
        foreach (var r in journal.GetAll().OrderByDescending(r => r.Timestamp).Take(50))
        {
            sb.AppendLine($"  {r.Timestamp.ToString("yyyy-MM-dd HH:mm", c)}  {r.Status,-18} {r.Description}");
            sb.AppendLine($"      target: {r.Target} · {r.OriginalValue ?? "—"} → {r.NewValue ?? "—"} · reversible: {(r.Reversible ? "yes" : "no")}");
            if (r.ErrorMessage is not null) sb.AppendLine($"      note: {r.ErrorMessage}");
        }

        sb.AppendLine();
        sb.AppendLine("OPTIMISATION RUNS (most recent 10)");
        foreach (var run in runs.GetAll().Take(10))
        {
            sb.AppendLine($"  {run.StartedAt.ToString("yyyy-MM-dd HH:mm", c)}  {run.Source} · preset {run.Preset}{(run.DryRun ? " · DRY RUN" : string.Empty)}{(run.Cancelled ? " · cancelled" : string.Empty)}");
            foreach (var item in run.Items) sb.AppendLine($"      {item.Status,-18} {item.Title}: {item.Message}");
        }

        return LogRedactor.Redact(sb.ToString());
    }

    public async Task<string> ExportAsync(string path, DiagnosticReport? report, CancellationToken ct)
    {
        var text = BuildText(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(new { generated = DateTimeOffset.Now, version = Brand.Version, text }, SettingsJson.Options);
            await File.WriteAllTextAsync(path, LogRedactor.Redact(json), ct).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(path, text, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        return path;
    }
}
