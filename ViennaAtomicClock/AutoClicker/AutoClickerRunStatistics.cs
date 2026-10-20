using System.Diagnostics;

namespace ViennaAtomicClock.AutoClicker;

/// <summary>Values the page shows (read at most ten times per second).</summary>
public sealed record StatisticsSnapshot(
    long Successful, long Failed, long Skipped, TimeSpan Elapsed,
    double ActualCps, double AverageCps,
    double LastDeviationMs, double MaxDeviationMs,
    double MinIntervalMs, double MaxIntervalMs, double AverageIntervalMs,
    double MinWindowCps, double MaxWindowCps,
    double? CpuPercent)
{
    public static readonly StatisticsSnapshot Empty = new(0, 0, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// Counters of one run. The engine thread writes, the UI reads snapshots: counters use Interlocked, the
/// short timestamp ring (rolling one-second CPS window) is guarded by a tiny lock.
/// </summary>
public sealed class AutoClickerRunStatistics
{
    private const int RingSize = 2048;
    private readonly long[] _ring = new long[RingSize];
    private readonly object _gate = new();
    private int _ringCount;
    private int _ringHead;

    private long _successful;
    private long _failed;
    private long _skipped;
    private long _start;
    private long _end;
    private long _lastClick;
    private long _intervalCount;
    private long _intervalSum;
    private long _minInterval = long.MaxValue;
    private long _maxInterval;
    private long _lastDeviation;
    private long _maxDeviation;
    private double _minWindowCps = double.MaxValue;
    private double _maxWindowCps;
    private long _cpuTicks = -1;

    public long Successful => Interlocked.Read(ref _successful);
    public long Failed => Interlocked.Read(ref _failed);
    public long Skipped => Interlocked.Read(ref _skipped);

    public void Start(long timestamp)
    {
        lock (_gate)
        {
            _start = timestamp;
            _end = 0;
            _ringCount = _ringHead = 0;
            _lastClick = 0;
            _intervalCount = _intervalSum = _maxInterval = _lastDeviation = _maxDeviation = 0;
            _minInterval = long.MaxValue;
            _minWindowCps = double.MaxValue;
            _maxWindowCps = 0;
            _cpuTicks = -1;
        }
        Interlocked.Exchange(ref _successful, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _skipped, 0);
    }

    public void Finish(long timestamp) => Interlocked.Exchange(ref _end, timestamp);

    public void AddSkipped(long count) => Interlocked.Add(ref _skipped, count);

    public void AddFailed() => Interlocked.Increment(ref _failed);

    public void SetCpu(TimeSpan cpu) => Interlocked.Exchange(ref _cpuTicks, cpu.Ticks);

    /// <summary>A click that Windows accepted, with how late it was against its planned time.</summary>
    public void AddClick(long timestamp, long lateness)
    {
        Interlocked.Increment(ref _successful);
        lock (_gate)
        {
            if (_lastClick != 0)
            {
                var interval = timestamp - _lastClick;
                _intervalSum += interval;
                _intervalCount++;
                if (interval < _minInterval) _minInterval = interval;
                if (interval > _maxInterval) _maxInterval = interval;
            }
            _lastClick = timestamp;
            _lastDeviation = lateness;
            if (lateness > _maxDeviation) _maxDeviation = lateness;
            _ring[_ringHead] = timestamp;
            _ringHead = (_ringHead + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }
    }

    private double WindowCps(long until, long elapsedTicks)
    {
        // Rolling one-second window (or the whole run while it is younger than a second).
        var window = Math.Min(Stopwatch.Frequency, Math.Max(1, elapsedTicks));
        var from = until - window;
        var inWindow = 0;
        for (var i = 0; i < _ringCount; i++)
        {
            if (_ring[(_ringHead - 1 - i + RingSize) % RingSize] <= from) break;
            inWindow++;
        }
        return elapsedTicks == 0 ? 0 : inWindow * (double)Stopwatch.Frequency / window;
    }

    /// <summary>Called by the engine a few times per second: remembers the lowest and highest one-second rate.</summary>
    public void SampleWindow(long now)
    {
        lock (_gate)
        {
            var elapsed = now - _start;
            if (_start == 0 || elapsed < Stopwatch.Frequency) return; // the first second is still ramping up
            var cps = WindowCps(now, elapsed);
            _minWindowCps = Math.Min(_minWindowCps, cps);
            _maxWindowCps = Math.Max(_maxWindowCps, cps);
        }
    }

    public StatisticsSnapshot Snapshot(long now)
    {
        lock (_gate)
        {
            var end = Interlocked.Read(ref _end);
            var until = end != 0 ? end : now;
            var elapsedTicks = _start == 0 ? 0 : Math.Max(0, until - _start);
            var elapsed = TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
            var successful = Interlocked.Read(ref _successful);

            var actual = WindowCps(until, elapsedTicks);

            var cpu = Interlocked.Read(ref _cpuTicks);
            return new StatisticsSnapshot(
                successful, Interlocked.Read(ref _failed), Interlocked.Read(ref _skipped), elapsed,
                actual, elapsed.TotalSeconds > 0 ? successful / elapsed.TotalSeconds : 0,
                ClickMath.TicksToMs(_lastDeviation), ClickMath.TicksToMs(_maxDeviation),
                _intervalCount > 0 ? ClickMath.TicksToMs(_minInterval) : 0,
                ClickMath.TicksToMs(_maxInterval),
                _intervalCount > 0 ? ClickMath.TicksToMs(_intervalSum) / _intervalCount : 0,
                _minWindowCps == double.MaxValue ? actual : _minWindowCps,
                Math.Max(_maxWindowCps, actual),
                cpu < 0 || elapsedTicks == 0 ? null : Math.Clamp(TimeSpan.FromTicks(cpu).TotalSeconds / elapsed.TotalSeconds * 100, 0, 100));
        }
    }
}
