using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>One step of a live publish and how long it took. The assistant reads these to say where the time went.</summary>
    public sealed class DeployStep
    {
        public string Step { get; set; }     // short key: sign-in, connect, read, check, compare, restore, send, recalc
        public string Words { get; set; }    // the plain sentence the person saw while it ran
        public long Ms { get; set; }
    }

    /// <summary>Times each step of a live publish, writes one log line per step, and tells the page which step is running.
    /// Steps run one after another: <see cref="Begin"/> ends the step before it. A publish took minutes with the engine idle
    /// and nothing said where, so every step is named and measured (Kane, 13 Oct).</summary>
    internal sealed class DeployTimer
    {
        private readonly Action<string, string, int> _onStart;   // key, words, index of the step (0 based)
        private readonly Func<long> _nowMs;
        private readonly Action<string> _log;
        private readonly List<DeployStep> _steps = new List<DeployStep>();
        private string _key, _words;
        private long _startedAt, _runStartedAt;
        private bool _runStarted;

        public DeployTimer(Action<string, string, int> onStart = null, Func<long> nowMs = null, Action<string> log = null)
        {
            _onStart = onStart;
            var sw = Stopwatch.StartNew();
            _nowMs = nowMs ?? (() => sw.ElapsedMilliseconds);
            _log = log ?? (line => { try { Console.Error.WriteLine(line); } catch { /* a closed stderr must not fail a publish */ } });
        }

        public DeployStep[] Steps => _steps.ToArray();
        public long TotalMs => _steps.Sum(s => s.Ms);

        public void Begin(string key, string words)
        {
            End();
            var now = _nowMs();
            if (!_runStarted) { _runStarted = true; _runStartedAt = now; }
            _key = key; _words = words; _startedAt = now;
            try { _onStart?.Invoke(key, words, _steps.Count); } catch { /* progress is only a view; a broken listener never fails the publish */ }
        }

        public void End()
        {
            if (_key == null) return;
            var ms = Math.Max(0, _nowMs() - _startedAt);
            _steps.Add(new DeployStep { Step = _key, Words = _words, Ms = ms });
            _log($"[deploy] step={_key} ms={ms}");
            _key = null; _words = null;
        }

        /// <summary>A step that ran outside the timer (the disconnect after the result): one log line, the same shape.</summary>
        public static void LogLate(string key, long ms, Action<string> log = null)
        {
            var line = $"[deploy] step={key} ms={ms}";
            if (log != null) { log(line); return; }
            try { Console.Error.WriteLine(line); } catch { /* ignore */ }
        }

        /// <summary>One line a person or an assistant can read: "Took 6.2 s: read the live model 4.1 s, send 3 changes 1.0 s."</summary>
        public string Summary()
        {
            if (_steps.Count == 0) return null;
            string Sec(long ms) => (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
            var sb = new StringBuilder("Took ").Append(Sec(TotalMs)).Append(": ");
            sb.Append(string.Join(", ", _steps.Select(s => $"{Lower(s.Words)} {Sec(s.Ms)}")));
            return sb.Append('.').ToString();
        }

        private static string Lower(string w) => string.IsNullOrEmpty(w) ? "" : char.ToLowerInvariant(w[0]) + w.Substring(1);
    }
}
