using System.Diagnostics;

namespace ViennaAtomicClock.AutoClicker;

public enum RunEnd { Stopped, Completed, EmergencyStop, Faulted }

/// <summary>Outcome of one run (raised from the engine thread).</summary>
public sealed record AutoClickerRunResult(RunEnd End, StatisticsSnapshot Statistics, string? Error, bool TestMode);

/// <summary>
/// The click engine. Runs on its own background thread with a high-resolution scheduler, never touches the UI,
/// works on an immutable configuration and guarantees that every pressed button is released again – on stop,
/// emergency stop, error or when the app closes.
/// States: Idle → Starting (start delay) → Running ⇄ Paused → Stopping → Idle (or Faulted → Idle).
/// </summary>
public sealed class AutoClickerEngine : IDisposable
{
    private const int MaxConsecutiveFailures = 25;

    private readonly IMouseInputService _mouse;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly HashSet<MouseButtonKind> _pressed = new();
    private int _state = (int)AutoClickerState.Idle;
    private CancellationTokenSource? _cancel;
    private Thread? _thread;
    private AutoClickerRunConfiguration? _config;
    private volatile bool _emergency;
    private long _countdownEnd;

    public AutoClickerEngine(IMouseInputService mouse) => _mouse = mouse;

    public AutoClickerRunStatistics Statistics { get; } = new();

    public AutoClickerState State => (AutoClickerState)Volatile.Read(ref _state);

    public AutoClickerRunConfiguration? Configuration => _config;

    /// <summary>Remaining start delay while <see cref="AutoClickerState.Starting"/>.</summary>
    public TimeSpan CountdownRemaining
    {
        get
        {
            var end = Interlocked.Read(ref _countdownEnd);
            var left = end - Stopwatch.GetTimestamp();
            return State == AutoClickerState.Starting && left > 0 ? TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency) : TimeSpan.Zero;
        }
    }

    /// <summary>Raised on the engine thread; listeners must marshal to their UI themselves.</summary>
    public event Action<AutoClickerState>? StateChanged;

    public event Action<AutoClickerRunResult>? Finished;

    private bool Transition(AutoClickerState from, AutoClickerState to)
    {
        if (Interlocked.CompareExchange(ref _state, (int)to, (int)from) != (int)from) return false;
        StateChanged?.Invoke(to);
        return true;
    }

    private void ForceState(AutoClickerState to)
    {
        Volatile.Write(ref _state, (int)to);
        StateChanged?.Invoke(to);
    }

    /// <summary>Starts a run. Ignored (false) unless the engine is idle.</summary>
    public bool Start(AutoClickerRunConfiguration config)
    {
        lock (_gate)
        {
            if (!Transition(AutoClickerState.Idle, AutoClickerState.Starting)) return false;
            _config = config;
            _emergency = false;
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            _resume.Set();
            var token = _cancel.Token;
            _thread = new Thread(() => Run(config, token))
            {
                IsBackground = true,
                Name = "Freedom click engine",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            return true;
        }
    }

    /// <summary>Stops a running, paused or starting run. Ignored when idle.</summary>
    public bool Stop() => RequestStop(emergency: false);

    /// <summary>Stops at once from any state and releases every mouse button.</summary>
    public void EmergencyStop()
    {
        RequestStop(emergency: true);
        // If the thread does not finish quickly (e.g. Windows hangs in SendInput), release from here too.
        if (_thread is { } thread && !thread.Join(150)) ReleaseAll();
    }

    private bool RequestStop(bool emergency)
    {
        lock (_gate)
        {
            var state = State;
            if (state is not (AutoClickerState.Starting or AutoClickerState.Running or AutoClickerState.Paused)) return false;
            if (emergency) _emergency = true;
            ForceState(AutoClickerState.Stopping);
            _cancel?.Cancel();
            _resume.Set();
            return true;
        }
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (!Transition(AutoClickerState.Running, AutoClickerState.Paused)) return false;
            _resume.Reset();
            return true;
        }
    }

    public bool Resume()
    {
        lock (_gate)
        {
            if (!Transition(AutoClickerState.Paused, AutoClickerState.Running)) return false;
            _resume.Set();
            return true;
        }
    }

    /// <summary>Waits until the engine thread has ended (used when the app closes).</summary>
    public bool WaitForIdle(TimeSpan timeout) => _thread is not { } thread || thread.Join(timeout);

    public void Dispose()
    {
        EmergencyStop();
        WaitForIdle(TimeSpan.FromSeconds(1));
        ReleaseAll();
        _cancel?.Dispose();
        _resume.Dispose();
    }

    // ───────────────────────── engine thread ─────────────────────────

    private void Run(AutoClickerRunConfiguration config, CancellationToken token)
    {
        var mouse = config.TestMode ? NullMouseInputService.Instance : _mouse;
        string? error = null;
        var end = RunEnd.Stopped;
        var cpuStart = ThreadCpu.Current();
        using var scheduler = new HighResolutionClickScheduler();
        var context = new RunContext(config, mouse, scheduler, token, this);
        try
        {
            // Visible start delay; cancellable at any moment.
            if (config.StartDelay > TimeSpan.Zero)
            {
                var countdownEnd = Stopwatch.GetTimestamp() + (long)(config.StartDelay.TotalSeconds * Stopwatch.Frequency);
                Interlocked.Exchange(ref _countdownEnd, countdownEnd);
                if (!scheduler.WaitUntil(countdownEnd, token)) return;
            }
            if (!Transition(AutoClickerState.Starting, AutoClickerState.Running)) return;

            Statistics.Start(Stopwatch.GetTimestamp());
            var completed = config.Kind == RunKind.Playback ? context.Playback() : context.Click();
            if (completed) end = RunEnd.Completed;
        }
        catch (ClickFailedException failure)
        {
            error = failure.Message;
            end = RunEnd.Faulted;
        }
        catch (Exception unexpected)
        {
            error = unexpected.Message;
            end = RunEnd.Faulted;
        }
        finally
        {
            // Whatever happened: no button stays pressed.
            ReleaseAll(mouse);
            Statistics.Finish(Stopwatch.GetTimestamp());
            if (cpuStart is { } before && ThreadCpu.Current() is { } after) Statistics.SetCpu(after - before);
            Interlocked.Exchange(ref _countdownEnd, 0);
            if (_emergency && end != RunEnd.Faulted) end = RunEnd.EmergencyStop;
            if (end == RunEnd.Faulted) ForceState(AutoClickerState.Faulted);
            ForceState(AutoClickerState.Idle);
            Finished?.Invoke(new AutoClickerRunResult(end, Statistics.Snapshot(Stopwatch.GetTimestamp()), error, config.TestMode));
        }
    }

    private void MarkPressed(MouseButtonKind button, bool pressed)
    {
        lock (_pressed)
        {
            if (pressed) _pressed.Add(button);
            else _pressed.Remove(button);
        }
    }

    private void ReleaseAll(IMouseInputService? mouse = null)
    {
        MouseButtonKind[] held;
        lock (_pressed)
        {
            held = _pressed.ToArray();
            _pressed.Clear();
        }
        foreach (var button in held) (mouse ?? _mouse).Send(stackalloc[] { MouseAction.Up(button) });
    }

    private sealed class ClickFailedException : Exception
    {
        public ClickFailedException(string message) : base(message) { }
    }

    /// <summary>State of one run on the engine thread.</summary>
    private sealed class RunContext
    {
        private readonly AutoClickerRunConfiguration _c;
        private readonly IMouseInputService _mouse;
        private readonly HighResolutionClickScheduler _scheduler;
        private readonly CancellationToken _token;
        private readonly AutoClickerEngine _engine;
        private readonly Random _random = new();
        private readonly long _runStart = Stopwatch.GetTimestamp();
        private long _deadline = long.MaxValue;
        private int _failuresInRow;
        private int _pointIndex;
        private long _lastCpuUpdate;
        private TimeSpan? _cpuStart = ThreadCpu.Current();

        public RunContext(AutoClickerRunConfiguration config, IMouseInputService mouse, HighResolutionClickScheduler scheduler, CancellationToken token, AutoClickerEngine engine)
        {
            _c = config;
            _mouse = mouse;
            _scheduler = scheduler;
            _token = token;
            _engine = engine;
        }

        private AutoClickerRunStatistics Stats => _engine.Statistics;

        /// <summary>True once a stop condition (time, held key, limits) is reached.</summary>
        private bool LimitReached()
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= _deadline) return true;
            if (_c.MaxRuntime is { } max && now - _runStart >= max.TotalSeconds * Stopwatch.Frequency) return true;
            if (_c.MaxClicks is { } maxClicks && Stats.Successful >= maxClicks) return true;
            if (_c.ClickLimit is { } limit && Stats.Successful >= limit) return true;
            if (_c.IsHeld is { } held && !held()) return true;
            return false;
        }

        private bool Paused => !_engine._resume.IsSet;

        /// <summary>Wakes up early for cancel, pause and every stop condition.</summary>
        private bool Wait(long target) => _scheduler.WaitUntil(Math.Min(target, _deadline), _token, () => Paused || LimitReached());

        /// <summary>Blocks while paused (buttons are released during a pause).</summary>
        private bool WaitWhilePaused()
        {
            if (_engine._resume.IsSet) return true;
            _engine.ReleaseAll(_mouse);
            WaitHandle.WaitAny(new[] { _engine._resume.WaitHandle, _token.WaitHandle });
            return !_token.IsCancellationRequested;
        }

        private void UpdateCpu(long now)
        {
            if (now - _lastCpuUpdate < Stopwatch.Frequency / 4) return;
            _lastCpuUpdate = now;
            Stats.SampleWindow(now);
            if (_cpuStart is { } start && ThreadCpu.Current() is { } current) Stats.SetCpu(current - start);
        }

        private bool Send(ReadOnlySpan<MouseAction> actions)
        {
            if (_mouse.Send(actions) == actions.Length)
            {
                _failuresInRow = 0;
                return true;
            }
            Stats.AddFailed();
            if (++_failuresInRow >= MaxConsecutiveFailures)
                throw new ClickFailedException(WindowsMouseInputService.ErrorText(_mouse.LastError) is { Length: > 0 } text ? text : "SendInput");
            return false;
        }

        private (int X, int Y)? NextTarget()
        {
            switch (_c.Position)
            {
                case PositionMode.Fixed:
                    return (_c.FixedX, _c.FixedY);
                case PositionMode.Sequence:
                    var point = _c.Points[_pointIndex % _c.Points.Length];
                    return (point.X, point.Y);
                case PositionMode.RandomArea when _c.Area is { } area:
                    return (area.X + _random.Next(area.Width), area.Y + _random.Next(area.Height));
                default:
                    return null;
            }
        }

        private double NextIntervalMs()
        {
            if (_c.Position == PositionMode.Sequence && _c.Points.Length > 0 && _c.Points[_pointIndex % _c.Points.Length].DelayMs is { } own)
                return Math.Max(ClickMath.MinIntervalMs, own);
            return ClickMath.Jittered(_c.ActionIntervalMs, _c.JitterMs, _random);
        }

        /// <summary>The clicker loop. Returns true when a limit (count, duration, route end) completed the run.</summary>
        public bool Click()
        {
            if (_c.Duration is { } duration) _deadline = _runStart + (long)(duration.TotalSeconds * Stopwatch.Frequency);

            if (_c.ClickKind == ClickKind.Hold) return HoldButton();

            var next = Stopwatch.GetTimestamp();
            while (true)
            {
                if (_token.IsCancellationRequested) return false;
                if (Paused)
                {
                    if (!WaitWhilePaused()) return false;
                    next = Stopwatch.GetTimestamp(); // continue from now, no catch-up after a pause
                    continue;
                }
                // Count, duration, route, time and click limits and a released hold key end the run normally.
                if (LimitReached()) return true;
                if (!Wait(next) || Stopwatch.GetTimestamp() >= _deadline) continue; // the checks above decide

                var now = Stopwatch.GetTimestamp();
                var lateness = now - next;
                var intervalMs = NextIntervalMs();
                var intervalTicks = ClickMath.MsToTicks(intervalMs);
                if (lateness > intervalTicks)
                {
                    // Far behind (system was busy): skip the missed times instead of firing a burst.
                    Stats.AddSkipped(lateness / intervalTicks);
                    next = now;
                    lateness = 0;
                }

                PerformAction(next, lateness);
                UpdateCpu(now);

                if (_c.Position == PositionMode.Sequence)
                {
                    _pointIndex++;
                    if (!_c.LoopPoints && _pointIndex >= _c.Points.Length) return true;
                }
                next += intervalTicks;
            }
        }

        private void PerformAction(long planned, long lateness)
        {
            var target = NextTarget();
            (int X, int Y)? origin = null;
            if (_c.MoveBack && target is not null && _mouse.TryGetCursor(out var ox, out var oy)) origin = (ox, oy);

            Span<MouseAction> batch = stackalloc MouseAction[3];
            Span<MouseAction> release = stackalloc MouseAction[1];
            var holdTicks = ClickMath.MsToTicks(_c.HoldMs);
            for (var i = 0; i < _c.ClicksPerAction; i++)
            {
                if (_c.ClickLimit is { } limit && Stats.Successful >= limit) break; // never more than the requested clicks
                if (_c.MaxClicks is { } max && Stats.Successful >= max) break;
                if (_token.IsCancellationRequested) break;
                var n = 0;
                if (target is { } t && i == 0) batch[n++] = MouseAction.Move(t.X, t.Y);
                var clickStart = Stopwatch.GetTimestamp();

                switch (_c.ClickKind)
                {
                    case ClickKind.DownOnly:
                        batch[n++] = MouseAction.Down(_c.Button);
                        if (Send(batch[..n]))
                        {
                            _engine.MarkPressed(_c.Button, true); // released when the run ends
                            Stats.AddClick(clickStart, lateness);
                        }
                        break;
                    case ClickKind.UpOnly:
                        batch[n++] = MouseAction.Up(_c.Button);
                        if (Send(batch[..n])) Stats.AddClick(clickStart, lateness);
                        break;
                    default:
                        if (holdTicks <= 0)
                        {
                            // Down and up in one SendInput call: nothing can come between them.
                            batch[n++] = MouseAction.Down(_c.Button);
                            batch[n++] = MouseAction.Up(_c.Button);
                            if (Send(batch[..n])) Stats.AddClick(clickStart, lateness);
                        }
                        else
                        {
                            batch[n++] = MouseAction.Down(_c.Button);
                            if (!Send(batch[..n])) break;
                            _engine.MarkPressed(_c.Button, true);
                            _scheduler.WaitUntil(clickStart + holdTicks, CancellationToken.None); // short; always finish the click
                            release[0] = MouseAction.Up(_c.Button);
                            var up = Send(release) || Send(release); // one retry, then the final release takes over
                            if (up)
                            {
                                _engine.MarkPressed(_c.Button, false);
                                Stats.AddClick(clickStart, lateness);
                            }
                        }
                        break;
                }

                lateness = 0;
                if (i < _c.ClicksPerAction - 1 && _c.GapMs > 0)
                    _scheduler.WaitUntil(Stopwatch.GetTimestamp() + ClickMath.MsToTicks(_c.GapMs), _token);
            }

            if (origin is { } back) Send(stackalloc[] { MouseAction.Move(back.X, back.Y) });
        }

        /// <summary>"Gedrückt halten": press once, keep it until a stop condition, release for sure.</summary>
        private bool HoldButton()
        {
            Span<MouseAction> press = stackalloc MouseAction[2];
            var n = 0;
            if (NextTarget() is { } t) press[n++] = MouseAction.Move(t.X, t.Y);
            press[n++] = MouseAction.Down(_c.Button);
            var pressedAt = Stopwatch.GetTimestamp();
            if (!Send(press[..n])) throw new ClickFailedException(WindowsMouseInputService.ErrorText(_mouse.LastError));
            _engine.MarkPressed(_c.Button, true);
            Stats.AddClick(pressedAt, 0);

            while (!_token.IsCancellationRequested)
            {
                if (Paused)
                {
                    if (!WaitWhilePaused()) return false; // the button is released during the pause
                    if (!Send(stackalloc[] { MouseAction.Down(_c.Button) })) return false;
                    _engine.MarkPressed(_c.Button, true);
                }
                if (LimitReached()) return true;
                Wait(Stopwatch.GetTimestamp() + ClickMath.MsToTicks(15));
                UpdateCpu(Stopwatch.GetTimestamp());
            }
            return false;
        }

        /// <summary>Replays a recorded sequence with its relative times (scaled by the speed factor).</summary>
        public bool Playback()
        {
            Span<MouseAction> batch = stackalloc MouseAction[2];
            Span<MouseAction> release = stackalloc MouseAction[1];
            for (var round = 0; round < _c.SequenceRepeat; round++)
            {
                var clock = Stopwatch.GetTimestamp();
                foreach (var e in _c.Sequence)
                {
                    clock += ClickMath.MsToTicks(e.DelayMs / _c.SequenceSpeed);
                    while (!Wait(clock))
                    {
                        if (_token.IsCancellationRequested) return false;
                        if (LimitReached()) return true;
                        if (!Paused) continue;
                        var pausedAt = Stopwatch.GetTimestamp();
                        if (!WaitWhilePaused()) return false;
                        clock += Stopwatch.GetTimestamp() - pausedAt; // the pause does not eat the delay
                    }
                    var now = Stopwatch.GetTimestamp();
                    batch[0] = MouseAction.Move(e.X, e.Y);
                    switch (e.Kind)
                    {
                        case SequenceEventKind.Wheel:
                            batch[1] = MouseAction.Wheel(e.WheelDelta);
                            Send(batch);
                            break;
                        case SequenceEventKind.Down:
                            batch[1] = MouseAction.Down(e.Button);
                            if (Send(batch)) { _engine.MarkPressed(e.Button, true); Stats.AddClick(now, 0); }
                            break;
                        case SequenceEventKind.Up:
                            batch[1] = MouseAction.Up(e.Button);
                            if (Send(batch)) _engine.MarkPressed(e.Button, false);
                            break;
                        default:
                            batch[1] = MouseAction.Down(e.Button);
                            if (!Send(batch)) break;
                            _engine.MarkPressed(e.Button, true);
                            _scheduler.WaitUntil(now + ClickMath.MsToTicks(Math.Min(e.HoldMs / _c.SequenceSpeed, 2000)), CancellationToken.None);
                            release[0] = MouseAction.Up(e.Button);
                            if (Send(release))
                            {
                                _engine.MarkPressed(e.Button, false);
                                Stats.AddClick(now, 0);
                            }
                            break;
                    }
                    UpdateCpu(now);
                }
            }
            return true;
        }
    }
}
