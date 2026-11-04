using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>One step of a model open and how long it took.</summary>
    public sealed class OpenStep
    {
        public string Step { get; set; }
        public long Ms { get; set; }
        /// <summary>True for a step that ran beside the others (its time is not part of the total's sum).</summary>
        public bool Alongside { get; set; }
    }

    /// <summary>Times each step of a model open and writes one line per step to the output log, then one total line.
    /// Kane's open of a workspace model took about two minutes on 3 Nov and the log did not say where the time went
    /// (cx1). Steps run one after another: <see cref="Mark"/> ends the step that ran since the last mark. The timer is
    /// ambient (<see cref="Current"/>), so a shared helper the open calls (the session swap) can name its own steps
    /// without a parameter on every signature. Only one open runs at a time per call chain; a nested Begin is ignored.</summary>
    internal sealed class OpenTimer : IDisposable
    {
        private static readonly AsyncLocal<OpenTimer> current = new AsyncLocal<OpenTimer>();
        /// <summary>Where the lines go. Null means the engine's stderr, which the extension writes to the Semanticus output log.</summary>
        internal static Action<string> LogForTests;
        /// <summary>The last finished open, for tests and for a status read.</summary>
        internal static OpenStep[] LastSteps = Array.Empty<OpenStep>();

        private readonly string _what;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly List<OpenStep> _steps = new List<OpenStep>();
        private readonly OpenTimer _outer;
        private long _markAt;
        private bool _done;

        // cx2 (Kane, 3 Nov; option A of the Connect mock-up): the window shows each step while the open runs ("Step 3 of 5 · Reading the
        // model definition") and its Cancel stops the open in the engine. Before cx2 the steps came back only on the open's result.
        /// <summary>The open's id on its window events, and what the window's Cancel names.</summary>
        internal string RunId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        private string _origin;
        private Action<OperationProgress> _progress;
        private int _phases;
        private int _phase;
        private string _phaseWords;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        // 0 running, 1 stopped by Cancel, 2 past the point of no return (the new model is open, so a Cancel could no longer change nothing).
        private int _state;
        private static readonly ConcurrentDictionary<string, OpenTimer> running = new ConcurrentDictionary<string, OpenTimer>(StringComparer.Ordinal);

        private OpenTimer(string what, OpenTimer outer) { _what = what; _outer = outer; }

        internal static OpenTimer Current => current.Value;

        /// <summary>Starts timing an open. A Begin inside an open already being timed returns that open's timer, so its
        /// steps land in one list and one total.</summary>
        internal static OpenTimer Begin(string what) => Begin(what, null, null, 0);

        /// <summary>Starts timing an open that tells the window its steps (<paramref name="progress"/>, the engine's progress/didChange)
        /// and can be stopped by the window's Cancel (<see cref="Cancel"/>). <paramref name="phases"/> is how many steps the window counts.</summary>
        internal static OpenTimer Begin(string what, string origin, Action<OperationProgress> progress, int phases)
        {
            var outer = current.Value;
            if (outer != null && !outer._done) return new OpenTimer(what, outer) { _done = true };
            var t = new OpenTimer(what, null) { _origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin.Trim().ToLowerInvariant(), _progress = progress, _phases = phases };
            current.Value = t;
            if (progress != null) running[t.RunId] = t;
            t.Log($"[open] start {what}");
            return t;
        }

        /// <summary>The step the window shows now starts: "Step n of the phases · words". Steps never go back.</summary>
        internal static void Phase(int n, string words)
        {
            var t = current.Value;
            if (t?._outer != null) t = t._outer;
            if (t == null || t._done || t._progress == null || n < t._phase) return;
            t._phase = n; t._phaseWords = words;
            t.Publish(words);
        }

        /// <summary>Stops the open here when the window's Cancel came in. Called between steps, before anything is written.</summary>
        internal static void ThrowIfCancelled()
        {
            var t = current.Value;
            if (t?._outer != null) t = t._outer;
            if (t != null && Volatile.Read(ref t._state) == 1) throw t.Stopped();
        }

        /// <summary>The token a step that waits (the sign-in, the read of the published model) passes on, so Cancel ends the wait itself.</summary>
        internal static CancellationToken Token
        {
            get { var t = current.Value; if (t?._outer != null) t = t._outer; return t?._cts.Token ?? CancellationToken.None; }
        }

        /// <summary>True when this open was stopped by the window's Cancel: its failure is the person's choice, not a fault, so it writes no
        /// failed-open row to the history (the Cancel promises nothing changed).</summary>
        internal static bool CancelledNow
        {
            get { var t = current.Value; if (t?._outer != null) t = t._outer; return t != null && Volatile.Read(ref t._state) == 1; }
        }

        /// <summary>The point of no return of a model swap (the session-intent check, c2). After it the new model is open, so the window's
        /// Cancel can no longer keep "nothing changed": it is refused as too late from here on, and the window is told. A Cancel that came
        /// first stops the swap here, before the old model is touched.</summary>
        internal static void Commit()
        {
            var t = current.Value;
            if (t?._outer != null) t = t._outer;
            if (t == null) return;
            var was = Interlocked.CompareExchange(ref t._state, 2, 0);
            if (was == 1) throw t.Stopped();
            if (was == 0 && t._progress != null && t._phase > 0) t.Publish(t._phaseWords);
        }

        /// <summary>The window's Cancel. A null id stops every running open a person started. "stopped": the open ends and nothing changes.
        /// "opened": too late, the model is already open and the open finishes. "none": no such open runs.</summary>
        internal static OpenCancelResult Cancel(string runId)
        {
            var targets = string.IsNullOrWhiteSpace(runId)
                ? running.Values.Where(t => t._origin == "human").ToArray()
                : running.TryGetValue(runId.Trim(), out var one) ? new[] { one } : Array.Empty<OpenTimer>();
            if (targets.Length == 0) return new OpenCancelResult { Stopped = false, State = "none" };
            var stopped = false;
            foreach (var t in targets)
            {
                if (Interlocked.CompareExchange(ref t._state, 1, 0) != 0) continue;
                stopped = true;
                t.Log($"[open] cancel {t._what} at step {t._phase} ({t._phaseWords ?? "starting"}) ms={t._sw.ElapsedMilliseconds}");
                try { t._cts.Cancel(); } catch { /* a callback on the token must not fail the Cancel */ }
            }
            return stopped ? new OpenCancelResult { Stopped = true, State = "stopped" } : new OpenCancelResult { Stopped = false, State = "opened" };
        }

        private OpenCancelledException Stopped() => new OpenCancelledException(_target);
        private string _target;
        /// <summary>The model's name for the stopped sentence ("Stopped. Sales did not open, and nothing changed.").</summary>
        internal static void Target(string name) { var t = current.Value; if (t?._outer != null) t = t._outer; if (t != null && !string.IsNullOrWhiteSpace(name)) t._target = name.Trim(); }

        private void Publish(string words)
        {
            try
            {
                _progress?.Invoke(new OperationProgress
                {
                    OpKey = "open", RunId = RunId, Done = _phase, Total = _phases, Note = words, Origin = _origin,
                    Cancellable = words == null ? (bool?)null : Volatile.Read(ref _state) == 0,
                });
            }
            catch { /* the window's line is observational: it must never fail an open */ }
        }

        /// <summary>Ends the step that ran since the last mark (or the start) and names it.</summary>
        internal static void Step(string name) => current.Value?.Mark(name);

        internal void Mark(string name)
        {
            if (_outer != null) { _outer.Mark(name); return; }
            if (_done) return;
            var now = _sw.ElapsedMilliseconds;
            var ms = Math.Max(0, now - _markAt);
            _markAt = now;
            _steps.Add(new OpenStep { Step = name, Ms = ms });
            Log($"[open] step={name} ms={ms}");
        }

        /// <summary>A step that ran beside the timed ones (on another task), with the time it took there. It does not move the mark.</summary>
        internal static void Alongside(string name, long ms)
        {
            var t = current.Value;
            if (t == null) return;
            if (t._outer != null) t = t._outer;
            if (t._done) return;
            t._steps.Add(new OpenStep { Step = name, Ms = ms, Alongside = true });
            t.Log($"[open] step={name} ms={ms} alongside");
        }

        /// <summary>Puts this open's steps and total on its result, so the extension logs them whichever process owns the engine.</summary>
        internal OpenResult Stamp(OpenResult result)
        {
            if (result == null || _outer != null) return result;
            result.Steps = _steps.ToArray();
            result.TotalMs = _sw.ElapsedMilliseconds;
            return result;
        }

        internal OpenStep[] Steps => _steps.ToArray();
        internal long TotalMs => _sw.ElapsedMilliseconds;

        /// <summary>The total line: the whole open and its three slowest steps, so one line says where the time went.</summary>
        internal void Finish(bool ok)
        {
            if (_outer != null || _done) return;
            _done = true;
            var slow = _steps.Where(s => !s.Alongside).OrderByDescending(s => s.Ms).Take(3).Select(s => $"{s.Step} {s.Ms}");
            Log($"[open] {(ok ? "done" : "failed")} {_what} total ms={_sw.ElapsedMilliseconds} slowest: {string.Join(", ", slow)}");
            LastSteps = _steps.ToArray();
            running.TryRemove(RunId, out _);
            if (_progress != null) Publish(null);   // the open is over: the window's bar stops showing a step
            if (ReferenceEquals(current.Value, this)) current.Value = null;
        }

        public void Dispose() => Finish(ok: false);

        private void Log(string line)
        {
            var sink = LogForTests;
            if (sink != null) { try { sink(line); } catch { } return; }
            try { Console.Error.WriteLine(line); } catch { /* a closed stderr must not fail an open */ }
        }
    }

    /// <summary>An open the window's Cancel stopped before the new model was open: the model that was open, the saved-connections list
    /// and the history are as they were. Its message is the sentence the Connections bar shows.</summary>
    public sealed class OpenCancelledException : InvalidOperationException
    {
        public OpenCancelledException(string target)
            : base($"Stopped. {(string.IsNullOrWhiteSpace(target) ? "The model" : target)} did not open, and nothing changed.") { }
    }
}
