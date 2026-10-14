namespace Semanticus.Engine.Entitlement
{
    /// <summary>The free Pro month's tally, as both doors return it (getTrialTally / get_trial_tally). TRUE counts, made
    /// on this computer from what really happened since the trial token was issued. All zeros when the licence is not a
    /// trial token; after a trial ends the numbers stay readable, for the end card.</summary>
    public sealed class TrialTally
    {
        /// <summary>When the trial started: the token's issued-at (unix seconds). 0 when this is not a trial.</summary>
        public long StartedAt { get; set; }
        /// <summary>Objects changed by applied edits, by anyone (a person, their assistant, a workflow). Each object an edit
        /// changed counts once for that edit.</summary>
        public long ChangesApplied { get; set; }
        /// <summary>The edits on the undo timeline that made those changes. An undo or a redo is a walk, not an edit.</summary>
        public long Steps { get; set; }
        /// <summary>Choices the hosted decision service made (each answered question). A reuse of an answer it already gave
        /// is not a new choice.</summary>
        public long JevChoices { get; set; }
        /// <summary>Workflow runs that finished.</summary>
        public long WorkflowsRun { get; set; }
    }
}

namespace Semanticus.Engine.Entitlement
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Where the free Pro month's tally is kept: one small file in the per-user home, because nothing that exists
    /// already counts across models and restarts. The edit ledger is per model and the change journal is a short memory in
    /// one process, so neither can say "since the trial started". The file holds ONE trial, keyed by the token's
    /// subscription id (else its issued-at); a different trial starts it over.
    ///
    /// Counts arrive on the dispatcher thread, so <see cref="Add"/> only adds to memory and a pool thread writes. A write
    /// adds this process's DELTAS to what the file holds, under a lock file, so two engines on this computer (two windows,
    /// or a separate engine --mcp) each add their own and none overwrites another. <see cref="Read"/> is the file plus what
    /// this process has not written yet, so a read is never behind this process's own counting.</summary>
    internal sealed class TrialTallyStore
    {
        public const string FileName = "trial-tally.json";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly string _path;
        private readonly object _gate = new object();     // the pending deltas
        private readonly object _ioGate = new object();   // one read or write of the file at a time in this process
        private Dictionary<string, Record> _pending = new Dictionary<string, Record>(StringComparer.Ordinal);
        private bool _flushQueued;

        internal sealed class Record
        {
            public string Key { get; set; }
            public long StartedAt { get; set; }
            public long ChangesApplied { get; set; }
            public long Steps { get; set; }
            public long JevChoices { get; set; }
            public long WorkflowsRun { get; set; }

            internal void AddFrom(Record d)
            {
                ChangesApplied += d.ChangesApplied; Steps += d.Steps; JevChoices += d.JevChoices; WorkflowsRun += d.WorkflowsRun;
            }
        }

        public TrialTallyStore(string path) { _path = path; }

        public string Path => _path;

        public void Add(string key, long startedAt, long changesApplied = 0, long steps = 0, long jevChoices = 0, long workflowsRun = 0)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = new Record { Key = key, StartedAt = startedAt };
                p.AddFrom(new Record { ChangesApplied = changesApplied, Steps = steps, JevChoices = jevChoices, WorkflowsRun = workflowsRun });
                if (_flushQueued) return;
                _flushQueued = true;
            }
            Task.Run(() => { try { Flush(); } catch { /* kept for the next write */ } });
        }

        /// <summary>Write what this process has counted. Also called when the engine stops, so nothing counted is lost.</summary>
        public void Flush()
        {
            lock (_ioGate)
            {
                Dictionary<string, Record> take;
                lock (_gate)
                {
                    take = _pending;
                    _pending = new Dictionary<string, Record>(StringComparer.Ordinal);
                    _flushQueued = false;
                }
                if (take.Count == 0) return;
                try
                {
                    WithFileLock(() =>
                    {
                        var held = ReadFile();
                        foreach (var d in take.Values)
                        {
                            if (held == null || held.Key != d.Key) held = new Record { Key = d.Key, StartedAt = d.StartedAt };
                            held.AddFrom(d);
                        }
                        WriteFile(held);
                    });
                }
                catch
                {
                    // Keep the counts for the next write rather than lose them; the tally is a ride-along and never fails anything.
                    lock (_gate)
                        foreach (var d in take.Values)
                        {
                            if (!_pending.TryGetValue(d.Key, out var p)) _pending[d.Key] = p = new Record { Key = d.Key, StartedAt = d.StartedAt };
                            p.AddFrom(d);
                        }
                }
            }
        }

        public TrialTally Read(string key, long startedAt)
        {
            var tally = new TrialTally { StartedAt = startedAt };
            if (string.IsNullOrEmpty(key)) return tally;
            lock (_ioGate)
            {
                Record held = null;
                try { held = ReadFile(); } catch { /* unreadable reads as nothing written yet */ }
                var sum = new Record();
                if (held != null && held.Key == key) sum.AddFrom(held);
                lock (_gate) if (_pending.TryGetValue(key, out var p)) sum.AddFrom(p);
                tally.ChangesApplied = sum.ChangesApplied;
                tally.Steps = sum.Steps;
                tally.JevChoices = sum.JevChoices;
                tally.WorkflowsRun = sum.WorkflowsRun;
            }
            return tally;
        }

        private Record ReadFile()
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(_path), Json);
        }

        // Write beside it and move over it, so a reader in another process sees the old file or the new one, never half.
        private void WriteFile(Record r)
        {
            var tmp = _path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
            File.Move(tmp, _path, overwrite: true);
        }

        private void WithFileLock(Action body)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            for (var attempt = 0; ; attempt++)
            {
                FileStream held = null;
                try { held = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 40) { Thread.Sleep(25); continue; }
                using (held) body();
                return;
            }
        }
    }
}
