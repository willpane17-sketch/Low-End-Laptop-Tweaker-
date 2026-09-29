using Tickshift.Core.Models;

namespace Tickshift.Diagnostics.Monitoring;

public enum HistoryWindow { FiveMinutes, ThirtyMinutes, OneHour, Session }

/// <summary>
/// Bounded in-memory ring buffer of live samples for the current session. Charts request a
/// downsampled view so rendering cost stays constant regardless of session length.
/// </summary>
public sealed class PerformanceHistory
{
    private readonly MetricSample[] _buffer;
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public PerformanceHistory(int capacity = 14_400)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 10);
        _buffer = new MetricSample[capacity];
    }

    public DateTimeOffset SessionStart { get; } = DateTimeOffset.Now;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public event EventHandler? Updated;

    public void Add(MetricSample sample)
    {
        lock (_gate)
        {
            var index = (_start + _count) % _buffer.Length;
            _buffer[index] = sample;
            if (_count < _buffer.Length) _count++;
            else _start = (_start + 1) % _buffer.Length;
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MetricSample> GetWindow(HistoryWindow window, DateTimeOffset now) =>
        GetSince(window switch
        {
            HistoryWindow.FiveMinutes => now.AddMinutes(-5),
            HistoryWindow.ThirtyMinutes => now.AddMinutes(-30),
            HistoryWindow.OneHour => now.AddHours(-1),
            _ => DateTimeOffset.MinValue,
        });

    public IReadOnlyList<MetricSample> GetSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            var result = new List<MetricSample>(Math.Min(_count, 4096));
            for (var i = 0; i < _count; i++)
            {
                var s = _buffer[(_start + i) % _buffer.Length];
                if (s.Timestamp >= since) result.Add(s);
            }

            return result;
        }
    }

    /// <summary>Averages samples into at most <paramref name="maxPoints"/> buckets, preserving time order.</summary>
    public static IReadOnlyList<MetricSample> Downsample(IReadOnlyList<MetricSample> samples, int maxPoints)
    {
        if (samples.Count <= maxPoints || maxPoints < 2) return samples;
        var result = new List<MetricSample>(maxPoints);
        var bucketSize = (double)samples.Count / maxPoints;
        for (var b = 0; b < maxPoints; b++)
        {
            var from = (int)(b * bucketSize);
            var to = Math.Min(samples.Count, (int)((b + 1) * bucketSize));
            if (to <= from) continue;
            var n = to - from;
            double cpu = 0, mem = 0, disk = 0, temp = 0;
            int diskN = 0, tempN = 0;
            ulong avail = 0;
            for (var i = from; i < to; i++)
            {
                var s = samples[i];
                cpu += s.CpuPercent;
                mem += s.MemoryPercent;
                avail += s.AvailableMemoryBytes / (ulong)n;
                if (s.DiskActivePercent is { } d) { disk += d; diskN++; }
                if (s.TemperatureCelsius is { } t) { temp += t; tempN++; }
            }

            result.Add(new MetricSample(samples[to - 1].Timestamp, cpu / n, mem / n, avail,
                diskN > 0 ? disk / diskN : null, tempN > 0 ? temp / tempN : null));
        }

        return result;
    }
}

/// <summary>Aggregate statistics over a set of samples (an "observation window").</summary>
public static class SampleStatistics
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var rank = percentile / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    /// <summary>Fraction (0–1) of samples at or above a threshold — used for "sustained" checks.</summary>
    public static double FractionAtOrAbove(IReadOnlyList<MetricSample> samples, Func<MetricSample, double> selector, double threshold) =>
        samples.Count == 0 ? 0 : samples.Count(s => selector(s) >= threshold) / (double)samples.Count;

    /// <summary>Least-squares slope in units per minute, or null when there is too little data.</summary>
    public static double? SlopePerMinute(IReadOnlyList<(DateTimeOffset Time, double Value)> points)
    {
        if (points.Count < 5) return null;
        var t0 = points[0].Time;
        var xs = points.Select(p => (p.Time - t0).TotalMinutes).ToArray();
        var ys = points.Select(p => p.Value).ToArray();
        var xMean = xs.Average();
        var yMean = ys.Average();
        double num = 0, den = 0;
        for (var i = 0; i < xs.Length; i++)
        {
            num += (xs[i] - xMean) * (ys[i] - yMean);
            den += (xs[i] - xMean) * (xs[i] - xMean);
        }

        return den < 1e-9 ? null : num / den;
    }
}
