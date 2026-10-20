using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ViennaAtomicClock.AutoClicker;

/// <summary>
/// Precise waiting on the engine thread with the monotonic Stopwatch (QueryPerformanceCounter) as clock:
/// longer waits sleep on a high-resolution waitable timer (Windows 10 1803+) or a 1 ms system timer, only the
/// last fraction of a millisecond is bridged with a short, yielding spin. Every wait wakes up immediately when the
/// run is cancelled, so stopping stays well below 50 ms even at 500 clicks per second.
/// </summary>
public sealed class HighResolutionClickScheduler : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    /// <summary>Longest single sleep, so abort conditions (key released, time over) are checked often enough.</summary>
    private static readonly long SliceTicks = ClickMath.MsToTicks(15);

    private readonly SafeWaitHandle? _timer;
    private readonly WaitHandle? _timerHandle;
    private readonly bool _highResolution;
    private readonly long _spinTicks;
    private bool _periodRaised;
    private WaitHandle[]? _waitSet;
    private CancellationToken _waitSetToken;

    public HighResolutionClickScheduler()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        _highResolution = _timer is { IsInvalid: false };
        if (!_highResolution)
        {
            _timer?.Dispose();
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
            // Without the high-resolution timer a 1 ms system timer period keeps sleeps short.
            _periodRaised = timeBeginPeriod(1) == 0;
        }
        if (_timer is { IsInvalid: false }) _timerHandle = new TimerWaitHandle(_timer);
        _spinTicks = ClickMath.MsToTicks(_highResolution ? 0.75 : 1.6);
    }

    public bool HighResolution => _highResolution;

    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Waits until <paramref name="target"/>. Returns false if cancelled or <paramref name="abort"/> said so.</summary>
    public bool WaitUntil(long target, CancellationToken cancellation, Func<bool>? abort = null)
    {
        while (true)
        {
            if (cancellation.IsCancellationRequested || (abort?.Invoke() ?? false)) return false;
            var remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;

            if (remaining > _spinTicks)
            {
                var sleep = Math.Min(remaining - _spinTicks, SliceTicks);
                Sleep(sleep, cancellation);
                continue;
            }

            // Final stretch: spin, but give the CPU away whenever there is still time left.
            var spinner = new SpinWait();
            while (Stopwatch.GetTimestamp() < target)
            {
                if (cancellation.IsCancellationRequested) return false;
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            return true;
        }
    }

    private void Sleep(long ticks, CancellationToken cancellation)
    {
        var hundredNs = Math.Max(1, ticks * 10_000_000 / Stopwatch.Frequency);
        if (_timer is not null && _timerHandle is not null)
        {
            var due = -hundredNs; // negative = relative
            if (SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                // One array per run instead of one per wait (no garbage at 500 waits per second).
                if (_waitSet is null || !_waitSetToken.Equals(cancellation))
                {
                    _waitSet = new[] { _timerHandle, cancellation.WaitHandle };
                    _waitSetToken = cancellation;
                }
                WaitHandle.WaitAny(_waitSet);
                return;
            }
        }
        cancellation.WaitHandle.WaitOne(TimeSpan.FromTicks(Math.Max(10_000, hundredNs)));
    }

    public void Dispose()
    {
        _timerHandle?.Dispose();
        _timer?.Dispose();
        if (_periodRaised)
        {
            timeEndPeriod(1);
            _periodRaised = false;
        }
    }

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;

        protected override void Dispose(bool explicitDisposing)
        {
            // The scheduler owns the handle; do not close it twice.
            SafeWaitHandle = new SafeWaitHandle(IntPtr.Zero, false);
            base.Dispose(explicitDisposing);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}

/// <summary>CPU time of the current thread (for the engine's own load measurement).</summary>
internal static class ThreadCpu
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Kernel + user time of the calling thread, or null if Windows does not tell.</summary>
    public static TimeSpan? Current() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? TimeSpan.FromTicks(kernel + user) : null;
}
