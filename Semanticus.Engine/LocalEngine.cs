using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;
using TabularEditor.TOMWrapper.Utils;
using Semanticus.Analysis;
using RawTom = Microsoft.AnalysisServices.Tabular;
using RawAs = Microsoft.AnalysisServices;

// Expose internals to the smoke harnesses so they can fixture-test BuildLiveStats (the COLUMNSTATISTICS parse),
// the Fabric schema->spec mapper, GitCli, and ModelCompare.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Semanticus.AirSmoke")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Semanticus.CicdSmoke")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Semanticus.Tests")]

namespace Semanticus.Engine
{
    /// <summary>In-process <see cref="IEngine"/> over the local <see cref="SessionManager"/>.</summary>
    public sealed partial class LocalEngine : IEngine, IDisposable
    {
        private readonly SessionManager _sessions;
        // Attached-readonly live connection, independent of the loaded TMDL model. Both doors reach the same
        // LocalEngine on different threads, so it's volatile and EVERY consumer snapshots it into a local
        // before use (check-then-act on the field would race a concurrent disconnect → NRE).
        private volatile LiveConnection _live;
        // Serializes _live SWAPS (connect/disconnect/open-rebind) so replacing the connection is atomic: the new
        // connection is opened+authenticated FIRST, then published under this gate, then the OLD one disposed. A
        // failed connect never touches _live. Queries never take this gate — they read the volatile field directly.
        private readonly object _liveGate = new object();
        // Monotonic connection-INTENT ticket: every op that intends to change _live (connect_xmla / connect_local /
        // disconnect / a model open's implicit drop+rebind) mints a ticket at its PUBLIC OPERATION ENTRY — before
        // any await, gate, export or build — and NEVER re-mints mid-operation. Intent order is therefore the order
        // the user ISSUED the operations, not the order their internals reached the swap: a disconnect issued after
        // an open began beats that open's later-landing rebind. SwapLive publishes a candidate ONLY while its
        // ticket is still the newest intent — a slow older connect/rebind can never displace or resurrect over it.
        private long _liveIntent;
        // Terminal: set under _liveGate by Dispose. Once true, SwapLive rejects EVERY candidate regardless of
        // ticket order — a connect that MINTED after Dispose would otherwise hold the newest ticket, beat the
        // "terminal" swap, and leave a live connection attached after Dispose returned.
        private bool _liveDisposed;
        // Monotonic SESSION-intent ticket — a SECOND counter, deliberately separate from _liveIntent (two
        // counters, two meanings): _liveIntent orders who owns the live CONNECTION; _sessionIntent orders which
        // open/create gets to be the current MODEL. Minted at the PUBLIC entry of every session-replacing op
        // (open / create / open_live / open_local), BEFORE the lifecycle gate, alongside the live ticket.
        // SwapSessionCoreAsync checks it at the point of no return: a stale open self-aborts instead of
        // committing. Mixing the two meanings into one counter is exactly what created the round-4 P1 — an open
        // whose _live drop lost to a newer CONNECT would still commit its session (correct), but the same "just
        // keep going when the live swap loses" behaviour let an open superseded by a newer OPEN commit too,
        // publishing model B while the PREVIOUS model's connection stayed attached (queries answered from the
        // wrong model). Newer connects must survive an open; newer opens must supersede it.
        private long _sessionIntent;
        // Serializes the SESSION LIFECYCLE (open / create) against itself, so two concurrent opens can't interleave
        // their dispose/swap (the swap itself is already a single atomic assignment in SessionManager). This is the
        // scoped fix; full per-op generation-checking (SessionContext) is a larger, separate refactor.
        private readonly System.Threading.SemaphoreSlim _lifecycleGate = new System.Threading.SemaphoreSlim(1, 1);
        // The %TEMP% dir holding THIS session's live/local snapshot (open_live / open_local write the target's full
        // metadata there and adopt it as the editable copy). Owned by the engine and deleted when the session it backs
        // is replaced or the engine is disposed — nothing used to remove them (#122). Exchanged atomically: both doors
        // reach this engine on different threads, and the field authorises a recursive delete.
        private string _snapshotDir;
        private string _snapshotBim;             // the .bim inside it, absolute
        private (DateTime, long) _snapshotStamp; // as WE wrote it — a later edit means the user saved into it
        private readonly PlanStore _plans = new PlanStore();   // the session-held change plan (Change-Plan engine)
        private readonly System.Threading.SemaphoreSlim _planGate = new System.Threading.SemaphoreSlim(1, 1); // serialize plan ops across both doors
        private readonly SpecStore _spec = new SpecStore();    // the session-held model spec (spec-driven authoring)
        private readonly System.Threading.SemaphoreSlim _specGate = new System.Threading.SemaphoreSlim(1, 1); // serialize spec ops across both doors
        private readonly BaselineStore _baselines = new BaselineStore();   // frozen edit-start baselines (Verified Edits, RESTRUCTURE)
        private readonly System.Threading.SemaphoreSlim _baselineGate = new System.Threading.SemaphoreSlim(1, 1);
        private static readonly System.Text.Json.JsonSerializerOptions SpecJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // The Pro entitlement (offline-verified license). Consulted ONLY at the bulk-apply chokepoints below; the
        // free tier stays fully usable one-edit-at-a-time. Injected for tests/smokes; defaults to the ambient license.
        private readonly Entitlement.IEntitlement _entitlement;

        // Verified Mode (Pro): a HUMAN-CONTROLLED runtime toggle. OFF by default → normal fast editing (no ceremony
        // on a basic SUM). ON → single-edit DAX ops are STRICTLY validated before they commit (v1 coverage: set_dax +
        // create measure/calc column/calc table/calc item/function; invalid syntax OR unknown refs refused — the
        // probe/blast-radius/regression wrap, and gating the bulk script/plan paths, are the roadmap). Validity only,
        // not an equivalence proof. In-memory + session-scoped (resets on reconnect — persistence is roadmap). One
        // shared state on the OWNER engine; an attaching MCP proxy sets/reads it over RPC, so both doors see the same
        // mode. Turning it ON requires Pro.
        private volatile bool _verifiedMode;

        public LocalEngine(SessionManager sessions, Entitlement.IEntitlement entitlement = null, string workspaceDir = null)
        {
            _sessions = sessions;
            _entitlement = entitlement ?? Entitlement.LicenseEntitlement.FromEnvironment();
            _workspaceDir = string.IsNullOrWhiteSpace(workspaceDir) ? null : workspaceDir;
            // Health delta (feature #4): PRO with a SOFT gate (Kane's locked call 2026-07-07) — the probe is
            // installed on EVERY session and reads the entitlement LAZILY per commit, so free edits stay plain
            // "rev N" at the cost of one bool check and a mid-session (headless) license activation starts
            // reporting on the next edit. The probe publishes its Phase-0 evidence record through this engine's
            // activity chokepoint so the L0 tee sees it, and stashes agent deltas into the ENGINE-level mailbox
            // stamped with the producing session + the originating tool call (correlation-true attribution).
            sessions.ObserverFactory = s => new HealthDeltaProbe(
                GetBpaRules,
                e => { try { PublishActivityAsync(e); } catch { /* ride-along */ } },
                (correlationId, delta) => _agentHealth.Stash(correlationId, s.Id, delta),
                () => _entitlement?.IsPro == true);
        }

        // Agent-health mailbox: ENGINE-level (not session-level) so a model swap mid-call can neither lose a
        // stashed delta nor hand it to the wrong caller — see AgentHealthMailbox for the attribution contract.
        private readonly AgentHealthMailbox _agentHealth = new AgentHealthMailbox();

        /// <summary>Drain-and-return the AGENT-origin health delta(s) stashed for <paramref name="correlationId"/>
        /// — the calling tool call's identity (plus any identity-less commits; null drains only those) — as one
        /// merged block (take-once: a second pull is null until the next agent commit). This is the MCP success
        /// filter's data path — NOT an MCP tool (tool-surface economy): the filter appends the block to the
        /// mutating tool's own result. Null on the free tier, with no open model, after read-only calls, and
        /// below threshold. Never throws.</summary>
        public Task<HealthDelta> PullAgentHealthAsync(string correlationId = null)
            => Task.FromResult(_agentHealth.Take(correlationId));

        // Host workspace (the sidecar fallback anchor for live/unsaved sessions — same rule as ExperienceTee).
        private readonly string _workspaceDir;

        // Truly terminal: detach under the gate AND set _liveDisposed, so even a connect that MINTS its intent
        // after this point (the newest ticket) can never publish. The detached connection is disposed OUTSIDE the
        // gate (ADOMD dispose can block) and log-only (the dispose outcome is already decided).
        public void Dispose()
        {
            LiveConnection old;
            lock (_liveGate) { _liveDisposed = true; old = _live; _live = null; }
            SafeDispose(old);
            ReleaseSnapshotDir();
        }

        /// <summary>Adopt the temp dir backing a just-opened live/local snapshot, so it is deleted when this session is
        /// replaced or the engine goes away. REFUSES anything outside <see cref="LiveModelExport.TempRoot"/>: this
        /// field's only job is to authorise a recursive delete, so it must never be able to name a user's own folder.
        /// Also stamps the snapshot, so a copy the user later EDITED is recognised and never discarded.</summary>
        internal void TrackSnapshotDir(string bimPath)
        {
            var dir = Path.GetDirectoryName(LiveModelExport.Norm(bimPath));
            var rootPrefix = LiveModelExport.Norm(LiveModelExport.TempRoot) + Path.DirectorySeparatorChar;
            if (dir == null || !(dir + Path.DirectorySeparatorChar).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A tracked snapshot dir must live under the snapshot root: " + bimPath);

            _snapshotDir = dir;
            _snapshotBim = LiveModelExport.Norm(bimPath);
            _snapshotStamp = StampOf(_snapshotBim);
            LiveModelExport.MarkInUse(dir);   // another engine process must not sweep this while we hold it
        }

        // (last write, size) of the snapshot as WE wrote it. Cheap, and enough to tell "untouched" from "edited".
        private static (DateTime, long) StampOf(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : default; }
            catch { return default; }
        }

        /// <summary>
        /// Delete the tracked snapshot dir. <paramref name="unless"/> keeps it when the path we're about to use lives
        /// inside it — otherwise re-opening a snapshot by its own path would delete the file mid-open.
        ///
        /// A snapshot the user has SAVED INTO is never deleted. `save_model` with no argument writes back to this .bim,
        /// so discarding it on close would destroy work the user was told had been saved. Untouched snapshots — the
        /// overwhelming majority, "open live, look, close" — are reclaimed at once; an edited one is left behind, and
        /// the real answer for it is a durable working folder the user names (the connections track).
        /// </summary>
        internal void ReleaseSnapshotDir(string unless = null)
        {
            var dir = System.Threading.Interlocked.Exchange(ref _snapshotDir, null);
            if (dir == null) return;

            if (!string.IsNullOrEmpty(unless))
            {
                var full = LiveModelExport.Norm(unless);
                if (full.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { _snapshotDir = dir; return; }   // the path we're opening IS this snapshot — keep owning it
            }

            var edited = _snapshotBim != null && StampOf(_snapshotBim) != _snapshotStamp;
            _snapshotBim = null; _snapshotStamp = default;
            // Release the claim either way: we no longer hold it, so a later sweep may reclaim it once it goes stale.
            try { File.Delete(Path.Combine(dir, LiveModelExport.InUseMarker)); } catch { }
            if (edited) return;

            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* held open elsewhere — the sweep gets it */ }
        }

        /// <summary>Read-only: the current Pro entitlement (tier + who it's licensed to + why-free). Both doors so the
        /// UI can show the tier and pre-gate the bulk buttons, and the agent knows what it may bulk-apply.</summary>
        public Task<Entitlement.EntitlementInfo> GetEntitlementAsync() => Task.FromResult(_entitlement.Info);

        // ---- Verified Mode (Pro toggle) --------------------------------------------------------------
        public Task<VerifiedModeState> GetVerifiedModeAsync() =>
            Task.FromResult(new VerifiedModeState { Enabled = _verifiedMode, Available = _entitlement?.IsPro ?? false,
                Note = _verifiedMode
                    ? "ON — single-edit DAX (set_dax + create measure/calc column/calc table/calc item/function) is strictly validated before it commits: invalid syntax OR an unknown table/column/measure reference is refused. Validity only, not an equivalence/drift proof. Session-scoped (resets on reconnect)."
                    : "OFF — normal editing." });

        // Turning Verified Mode ON is a Pro feature (thrown before the flip, so free stays intact). Turning it OFF is
        // always allowed. This is the human's switch — the agent operates under whatever mode the human set.
        public Task<VerifiedModeState> SetVerifiedModeAsync(bool on, string origin)
        {
            if (on)
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "Verified Mode",
                    "Verified Mode (every AI edit auto-verified before it commits) is a Pro feature. On free you can still verify manually with verify_dax_equivalence / probe_measure.");
            _verifiedMode = on;
            return GetVerifiedModeAsync();
        }

        // When Verified Mode is ON, refuse a mutating DAX edit unless it passes STRICT validation — not merely
        // balanced brackets (v.Valid) but ZERO diagnostics, so an unknown table/column/measure reference is refused
        // too (e.g. SUM(Sales[NoSuchColumn]) — brackets balance but the column doesn't exist). The validator is
        // conservative (masks strings/comments; skips unquoted Word[Col] that may be a VAR table; resolves a bare
        // [name] against every measure AND column), so a genuinely clean expression yields no diagnostics and passes;
        // the false-refusal risk is low and this is an opt-in mode the human can turn off. This is VALIDITY only —
        // NOT an equivalence/drift proof (that's optimize_measure / verify_dax_equivalence). No-op when OFF or the
        // expression is empty. Deterministic, offline. v1 covers the single-edit DAX ops that call it (below).
        // Returns whether validation actually RAN AND PASSED — the audit record keys off this return value,
        // not a re-read of the (volatile, other-door-flippable) mode, so a mid-op toggle can never mint a
        // "validated" record for an expression that was never checked.
        private async Task<bool> VerifiedGuardAsync(string expression, string what)
        {
            if (!_verifiedMode || string.IsNullOrWhiteSpace(expression)) return false;
            var v = await ValidateDaxAsync(expression);
            var diags = v?.Diagnostics ?? Array.Empty<DaxDiagnostic>();
            if (v == null || diags.Length > 0)
            {
                // Surface the most serious first (a structural error over a reference warning), then how many more.
                var lead = diags.FirstOrDefault(d => d.Severity == "error") ?? diags.FirstOrDefault();
                var msg = lead?.Message ?? "invalid DAX";
                var more = diags.Length > 1 ? $" (+{diags.Length - 1} more)" : "";
                throw new InvalidOperationException($"Verified Mode (strict DAX): refusing {what} — {msg}{more}. Fix it, or turn Verified Mode off.");
            }
            return true;
        }

        // ---- Verified Edits: the append-only audit trail ----------------------------------------------
        // Append a record to the model's audit chain (VerifiedEditsStore — non-undoable, so undo_change from
        // either door can't erase it). Runs on the dispatcher thread AFTER the mutation it records, so a
        // rolled-back edit never leaves a phantom record; marks the session audit-dirty (the non-undoable
        // write is invisible to the checkpoint arithmetic behind HasUnsavedChanges, and an unsaved record is
        // a silently-lost record). Deliberately NOT wrapped in try/catch: an audit layer that fails silently
        // is worse than a failed op. The trailing activity event is a UI nudge only — the non-undoable write
        // emits no didChange, so without it a no-mutation record (a deploy override) never refreshes the tab.
        private async Task<VerifiedEditRecord> RecordVerifiedEditAsync(Session s, VerifiedEditRecord rec, string[] vitalsChangedRefs = null)
        {
            // Under a dry-run scope the recorded edit was rolled back and never broadcast — it must never leave an
            // audit record either (append-only means append-only for REAL edits). This is the SINGLE append
            // chokepoint (RecordVerifiedModeEditAsync + apply_plan + optimize_measure + deploy all route here), so
            // the guard suppresses every audit writer without a per-site check. The DryRunScope AsyncLocal flows
            // down the op's async chain to this continuation, so it is visible here.
            if (DryRunScope.Current != null) return rec;
            // Weld the record to the git commit the model currently sits on — the durable-time-travel anchor (the
            // chain hashes bodies, never stores them, so a REVERT must come from git, not the chain). Resolved HERE,
            // off the dispatcher thread, because it spawns a git child process: the dispatcher is the single-writer
            // thread that owns TOM, so blocking it on a subprocess would serialize every read and write in the
            // process. Fail-soft — "" for a non-git / unsaved / no-commits-yet model, so an edit never depends on git.
            rec.BaseCommit = await TryResolveHeadAsync(s);
            var r = await s.Dispatcher.RunAsync(() => { var x = VerifiedEditsStore.Append(s.Model, rec); s.MarkAuditDirty(); return x; });
            try { await PublishActivityAsync(new ActivityEvent { Kind = "verified_edit", Origin = rec.Origin, Label = rec.Op, Ok = true }); } catch { /* nudge only */ }
            // Number time-machine (feature #3): the audit chokepoint IS the checkpoint moment — an applied
            // plan/optimize or a committed deploy ambiently snapshots the vital signs (Pro + host-enabled +
            // never-throws inside). VALUES are observed only when the live model provably reflects the
            // session (the committed deploy, which then reads ALL top-N — every live number can move);
            // an edit checkpoint's live connection still serves the PREVIOUS deployed state, so it records
            // ValuesSkippedStale instead of pairing fresh expressions with stale numbers.
            if (IsVitalsCheckpoint(rec))
                await CaptureVitalsAsync(s, rec.Op, rec.Origin,
                    vitalsChangedRefs ?? (rec.ObjectRef != null ? new[] { rec.ObjectRef } : Array.Empty<string>()),
                    liveReflectsSession: VitalsLiveReflectsSession(rec));
            return r;
        }

        // Resolve the git HEAD the model's on-disk state PROVABLY sits on, for the audit git-anchor. FAIL-SOFT by
        // contract: returns "" (never throws, never hangs) when there is no on-disk path (a live/XMLA/unsaved
        // session), the path isn't in a git repo, there are no commits yet (unborn HEAD), git isn't installed, or the
        // probe blows a short (~2s) budget. A missing anchor must never break — or stall — an edit.
        //
        // ANCHOR ONLY A TRACKED + CLEAN MODEL. `git rev-parse HEAD` names the repo commit even when the model's files
        // are untracked/ignored or already differ from HEAD — in which case HEAD does NOT contain this state, so
        // `git checkout <sha>` could not restore it and the "durable revert anchor" would be a lie. So we gate on a
        // `git status --porcelain` over the model path FIRST: any output ⇒ dirty or untracked ⇒ return "" (honest —
        // an empty anchor is fully supported by the presence-implied basis). This costs ONE git call in the common
        // editing case (a dirty tree short-circuits before rev-parse). Note: it runs on EVERY verified-edit append; a
        // per-session memoization (invalidated on save / git ops) is a clean follow-up, not built here.
        //
        // Spawns a git child process, so it MUST run OFF the dispatcher thread (the caller awaits it before entering
        // RunAsync). The 2s budget is a CancellationToken that KILLS the child (see GitCli.RunAsync), not a bare
        // Task.WaitAsync that would abandon a stalled git.
        private static async Task<string> TryResolveHeadAsync(Session s)
        {
            try
            {
                var src = s?.SourcePath;
                if (ExperienceStore.IsEphemeralAnchor(src)) return "";   // live / XMLA / never-saved — nothing on disk to anchor
                var full = Path.GetFullPath(src);
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                // Scope the status to the model path itself (not the whole repo, and not the engine's own untracked
                // sidecars beside it) — the question is precisely "does HEAD contain THIS model's on-disk state".
                var status = await GitCli.RunAsync(dir, cts.Token, "status", "--porcelain", "--untracked-files=all", "--", full);
                if (!status.Ok || !string.IsNullOrWhiteSpace(status.Stdout)) return "";   // not a repo / dirty / untracked ⇒ no honest anchor
                var sha = await GitCli.HeadCommitAsync(dir, cts.Token);
                return sha ?? "";
            }
            catch { return ""; }   // no git, not a repo, unborn HEAD, timeout, odd state — all fold to "no anchor"
        }

        // The Verified-Mode wing of the trail: an intercepted single-edit DAX op passed strict validation and
        // committed — record it with the honest verdict "validated" (validity only; NEVER "proven", that's
        // optimize_measure's equivalence pipeline). `guardValidated` is VerifiedGuardAsync's OWN decision for
        // THIS op — never re-read _verifiedMode here: the toggle can flip mid-op from the other door, and a
        // re-read would record "strict validation passed" for an expression the guard never saw.
        private Task RecordVerifiedModeEditAsync(bool guardValidated, Session s, string op, string objectRef, string expression, long revision, string origin)
        {
            if (!guardValidated) return Task.CompletedTask;
            // A rehearsed edit is rolled back — skip the audit append (also guarded at RecordVerifiedEditAsync;
            // explicit here so the no-record intent is local to the op path).
            if (DryRunScope.Current != null) return Task.CompletedTask;
            return RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = revision, Origin = origin, Op = op, ObjectRef = objectRef,
                Verdict = "validated",
                Summary = "Verified Mode: strict validation passed (zero diagnostics) — validity only, not an equivalence proof",
                Evidence = System.Text.Json.JsonSerializer.Serialize(new { mode = "verified", check = "strict-validate" }),
                BodyHash = VerifiedEditsStore.BodyHash(expression),
            });
        }

        /// <summary>The persisted audit trail + its hash-chain self-check. Free to read (the Pro value is the
        /// recording pipelines + export); an empty model simply has no records.</summary>
        public async Task<VerifiedEditsChain> ListVerifiedEditsAsync()
        {
            var s = _sessions.Require();
            return await s.ReadAsync(m => VerifiedEditsStore.Load(m));
        }

        /// <summary>Export the audit trail for a boss/auditor (markdown) or CI (json). Pro.</summary>
        public async Task<string> ExportVerifiedEditsAsync(string format)
        {
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Exporting the verified-edits audit trail",
                "Read it in place with list_verified_edits.");
            var chain = await ListVerifiedEditsAsync();
            // camelCase to match what list_verified_edits already emits over both doors — a CI consumer must be
            // able to parse either surface with one shape.
            return string.Equals(format?.Trim(), "json", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Json.JsonSerializer.Serialize(chain, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })
                : VerifiedEditsStore.ToMarkdown(chain);
        }

        // ---- live connectivity --------------------------------------------------------------

        private long NewLiveIntent() => System.Threading.Interlocked.Increment(ref _liveIntent);
        private long NewSessionIntent() => System.Threading.Interlocked.Increment(ref _sessionIntent);

        // Mint a session-replacing op's ticket PAIR atomically with respect to other pair mints: PAIR ORDERING IS
        // THE C2 SOUNDNESS PREMISE — a newer session ticket must ALWAYS carry a newer live ticket. Minted as two
        // independent increments, open A could take its live ticket, get preempted, and take its session ticket
        // AFTER open B minted both: A would then hold the NEWER session ticket with the OLDER live ticket, so B
        // aborts at c2 while A passes c2 — and A's SwapLive(null, olderLiveTicket) drop LOSES to B's abandoned
        // newer live intent, committing A's model with the previous model's connection still attached (the
        // round-3 P1 reborn through the gap between the increments). Explicit connects/disconnects keep minting
        // their single live ticket unserialized — their semantics need only the live counter's own monotonicity,
        // and a connect landing between a pair's two increments cannot break the pair's RELATIVE order.
        private readonly object _sessionIntentMintGate = new object();
        private (long Live, long Session) NewSessionSwapIntents() { lock (_sessionIntentMintGate) return (NewLiveIntent(), NewSessionIntent()); }
        internal (long Live, long Session) MintSessionSwapIntentsForTest() => NewSessionSwapIntents();

        // Atomically publish <paramref name="next"/> (a fully-opened connection, or null to detach) as _live —
        // but ONLY while <paramref name="ticket"/> is still the newest connection intent (and the optional
        // <paramref name="guard"/> holds; both are evaluated UNDER the gate, and neither awaits). Returns:
        //   Won  = true  ⇒ Displaced is the connection this one replaced — Dispose it AFTER the gate
        //                  (disposing an ADOMD connection can block; no query should wait on the swap);
        //   Won  = false ⇒ a newer intent superseded this op mid-flight — Displaced is `next` itself,
        //                  UN-published, for the caller to dispose. _live is untouched.
        // Readers never take this gate (they read the volatile field); they see whole-old or whole-new, never
        // torn state. Open-first + latest-intent-wins together guarantee: a failed connect can't destroy a
        // working connection, and a slow stale connect/rebind can't resurrect an older endpoint over a newer one.
        private (bool Won, LiveConnection Displaced) SwapLive(LiveConnection next, long ticket, Func<bool> guard = null)
        {
            lock (_liveGate)
            {
                // NEVER hand back the instance that remains attached as Displaced (every caller disposes what we
                // return): a self-swap (the test seams re-publishing the same stub; a retry with the same object)
                // would win and return `old == next == _live`, and a LOSING swap of the already-attached instance
                // would return `next == _live` — either way the caller disposes a connection that is still live.
                // Terminal first, independent of ticket order: after Dispose, NOTHING may publish — not even the
                // newest intent (a connect that started after Dispose would otherwise win and stay attached).
                if (_liveDisposed) return (false, ReferenceEquals(next, _live) ? null : next);
                if (System.Threading.Interlocked.Read(ref _liveIntent) != ticket || (guard != null && !guard()))
                    return (false, ReferenceEquals(next, _live) ? null : next);
                var old = _live; _live = next; return (true, ReferenceEquals(old, next) ? null : old);
            }
        }

        // Dispose a connection whose op OUTCOME IS ALREADY DECIDED — an unpublished loser, or the displaced old
        // connection after a won swap. A dispose failure must not overwrite that outcome with a throw (a superseded
        // connect must still return its honest SupersededStatus; the best-effort rebind must not throw post-commit):
        // log-only, never propagate.
        private static void SafeDispose(LiveConnection c)
        {
            if (c == null) return;
            try { c.Dispose(); }
            catch (Exception ex) { try { Console.Error.WriteLine("[live] disposing a superseded/displaced connection failed (outcome already decided): " + ex.Message); } catch { } }
        }

        // The honest tool-result for a connect/disconnect that completed its work but LOST the intent race:
        // report what is actually attached now, and say why the caller's op didn't take effect.
        private ConnectionStatus SupersededStatus(string what)
        {
            var cur = _live;
            return cur != null
                ? new ConnectionStatus { Connected = true, Kind = cur.Kind, DataSource = cur.DataSource,
                    Message = what + " completed but was superseded by a newer connection request mid-flight — the newer connection remains active. Re-issue the request if you still want this target." }
                : new ConnectionStatus { Connected = false,
                    Message = what + " completed but was superseded by a newer connection/disconnect request mid-flight; not connected. Re-issue the request if you still want this target." };
        }

        // Test-only seams: attach a stub connection / exercise the intent-race primitive without a real endpoint
        // (the resurrection + terminal-dispose pins). All route through SwapLive like the real callers.
        internal void SetLiveConnectionForTest(LiveConnection c) { var (_, displaced) = SwapLive(c, NewLiveIntent()); SafeDispose(displaced); }
        internal long MintLiveIntentForTest() => NewLiveIntent();
        internal bool TrySwapLiveForTest(LiveConnection c, long ticket) { var (won, displaced) = SwapLive(c, ticket); SafeDispose(displaced); return won; }

        // Model-scoped state that must NOT survive a model replacement — an old plan/spec/baseline applied to a
        // NEWLY opened model is a data-integrity bug (wrong-model edits, false regressions). Invoked on the
        // lifecycle-gated open/create path AFTER the replacement is fully built AND its observer attached, but
        // BEFORE it is published (SwapSessionCoreAsync step d): a concurrent reader mid-open sees the OLD session with stores
        // that are each either still-old (a correct old-model pairing) or already-empty — the chosen lesser evil —
        // and never the NEW session paired with the OLD model's state. The gates are taken ONE AT A TIME in a
        // single documented order (alphabetical by store field: _baselines → _plans → _spec → _workflowAux) — we
        // deliberately do NOT hold them all at once: every other code path is single-gate, and introducing the
        // process's only multi-gate hold for a cosmetic tightening (the partial combinations are all
        // old-or-empty, never wrong-model) is not worth the deadlock surface. DELIBERATELY does NOT reset:
        //   • Verified Mode — user-level strictness; silently disabling enforcement on a model open would be
        //     FAIL-OPEN (an AI edit that should be validated would commit unchecked).
        //   • the entitlement, the workflow-enforcement/agent policy (user/project-level, disk-backed), and the
        //     knowledge/experience stores (machine/user-level) — none are scoped to the open model.
        //   • the workflow RUNS themselves — a run isn't bound to one model at start (a workflow may include the
        //     open/create step), its steps are RE-VERIFIED against the current model at submit, and clearing runs
        //     would silently drop in-flight progress. Only the model-specific START-OF-RUN scan baselines
        //     (_workflowAux — the BPA/readiness snapshot the *_clean/_rescan verifies diff against) are cleared, so
        //     after a swap those verifies fail LOUD ("no start-of-run snapshot") instead of diffing the new model
        //     against the old one's baseline — the actual finding for workflows.
        // The live query connection (_live) is handled separately by the caller (dropped unless the open rebinds it).
        private async Task ResetModelScopedStateOnSwapAsync()
        {
            await _baselineGate.WaitAsync();
            try { _baselines.Clear(); } finally { _baselineGate.Release(); }
            await _planGate.WaitAsync();
            try { _plans.Clear(); } finally { _planGate.Release(); }
            await _specGate.WaitAsync();
            try { _spec.Clear(); } finally { _specGate.Release(); }
            await _workflowGate.WaitAsync();
            try { _workflowAux.Clear(); } finally { _workflowGate.Release(); }   // stale-baseline snapshots only; runs survive (re-verified live)
            _lastReadinessGrade = null;   // volatile — the cached activation fact; the next scan recomputes it for the new model
        }

        public async Task<ConnectionStatus> ConnectXmlaAsync(string endpoint, string database, string authMode, string rawToken)
        {
            var ticket = NewLiveIntent();   // intent minted at op START — anything newer supersedes this connect
            // The engine acquires an Entra token and injects it (the netcore AS client can't do its own AAD).
            // interactive pops a browser via Azure.Identity; serviceprincipal/azcli/devicecode/token as named.
            // Use BuildCredentialAsync (the SAME persistent-cache path open_live uses) so an interactive sign-in PERSISTS
            // its AuthenticationRecord to the encrypted cache — a later cold Compare then reuses it silently instead of
            // prompting a SECOND time (the "Sign in and retry" banner is meant to be exactly one prompt). token mode has
            // no credential (the caller supplies the raw token). Open the NEW connection fully (token + ADOMD open)
            // BEFORE touching _live — a failed reconnect must leave the existing working connection untouched.
            var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
            var cred = mode == "token" ? null : await EntraToken.BuildCredentialAsync(mode, null, System.Threading.CancellationToken.None);
            var token = cred != null
                ? (await EntraToken.GetTokenAsync(cred, System.Threading.CancellationToken.None)).Token
                : await EntraToken.AcquireAsync(mode, rawToken, System.Threading.CancellationToken.None);
            var cs = LiveConnection.XmlaConnectionString(endpoint, database, token);
            var conn = await LiveConnection.OpenAsync("xmla", endpoint, cs);
            // Status is captured BEFORE the swap decision (never read from a possibly-displaced conn after) — and
            // until SwapLive takes ownership the candidate is OURS to clean up: a throw here must not leak it.
            // SafeDispose, not Dispose: a throwing connection teardown must not REPLACE the Status failure.
            ConnectionStatus status;
            try { status = conn.Status(); }
            catch { SafeDispose(conn); throw; }
            var (won, displaced) = SwapLive(conn, ticket);
            SafeDispose(displaced);         // on a win: the replaced connection; on a loss: our own superseded candidate — outcome decided either way
            if (!won) return SupersededStatus("connect_xmla");
            var record = RememberConnection("xmla", endpoint, conn.Database, conn.Database, null, authMode);   // resolved identity, only after the swap won
            status.Database = conn.Database; status.ConnectionId = record?.Id;
            await SafeRebroadcastWorkflowLibraryAsync();   // connection.* changed — activation may re-curate the menu (§10.6)
            return status;
        }

        public async Task<ConnectionStatus> ConnectLocalAsync(string dataSource, string database)
        {
            var ticket = NewLiveIntent();   // intent minted at op START
            if (string.IsNullOrWhiteSpace(dataSource))
            {
                var inst = LocalDiscovery.List().FirstOrDefault()
                    ?? throw new InvalidOperationException("No local Power BI Desktop instance found. Open a .pbix, or pass a Data Source like 'localhost:51234'.");
                dataSource = inst.DataSource;
            }
            var cs = LiveConnection.LocalConnectionString(dataSource, database);
            var conn = await LiveConnection.OpenAsync("local", dataSource, cs);   // open first — a failed connect keeps the old one
            ConnectionStatus status;
            try { status = conn.Status(); }     // ownership is still ours until SwapLive — don't leak on a throw
            catch { SafeDispose(conn); throw; } // ...and a throwing teardown must not replace the Status failure
            var (won, displaced) = SwapLive(conn, ticket);
            SafeDispose(displaced);
            if (!won) return SupersededStatus("connect_local");
            var record = RememberConnection("localDesktop", dataSource, conn.Database, conn.Database);
            status.Database = conn.Database; status.ConnectionId = record?.Id;
            await SafeRebroadcastWorkflowLibraryAsync();   // connection.* changed — activation may re-curate the menu (§10.6)
            return status;
        }

        // Recording history must never be able to fail a connection that already succeeded — the user is connected
        // either way, and a read-only convenience does not get to throw over the thing it is a convenience for.
        private static ModelConnectionRecord RememberConnection(string kind, string endpoint, string database,
            string modelName = null, string tenantId = null, string authMode = null)
        {
            try { return ConnectionRegistry.Remember(kind, endpoint, database, modelName, tenantId, authMode); } catch { return null; }
        }

        // A loopback endpoint is the user's own Power BI Desktop, never a shared server. HOST-exact via
        // LiveDeploy.IsLocalEndpoint — NOT a substring test: "powerbi://.../myorg/prod-localhost-mirror" contains
        // "localhost" but is a cloud workspace (any scheme ⇒ remote), and mis-reading it as local would ungate prod.
        private static bool IsLoopbackEndpoint(string endpoint)
        {
            var e = endpoint?.Trim() ?? "";
            if (e.StartsWith("data source=", StringComparison.OrdinalIgnoreCase)) e = e.Substring("data source=".Length).Split(';')[0].Trim();
            return LiveDeploy.IsLocalEndpoint(e);
        }

        // The target label a policy gates on. Precedence: an EXPLICIT registry label (a human declaration) beats
        // everything, including the loopback inference — if the user says a target is prod, it is prod; then loopback ⇒
        // local (the only inference we make besides fail-closed, and it is host-exact); then UNLABELLED ⇒ prod.
        internal static string ResolveTargetLabel(string endpoint, string database)
        {
            var rec = ConnectionRegistry.FindByEndpoint(endpoint, database);
            if (!string.IsNullOrWhiteSpace(rec?.Label)) return ConnectionRegistry.EffectiveLabel(rec);
            return IsLoopbackEndpoint(endpoint) ? "local" : ConnectionRegistry.EffectiveLabel(rec);
        }

        /// <summary>
        /// THE agent-permissions chokepoint. Returns null when the action may proceed, else a human-readable refusal.
        /// It sits in the engine (not the MCP tool wrapper) because every gated op is reachable from both doors and
        /// from dry_run. On an <see cref="GateOutcome.Ask"/> it consults the approval ledger: a human-granted, matching,
        /// unexpired approval is CONSUMED and the action proceeds; otherwise the request is registered on the queue and
        /// the action is refused with an actionable message. A denial or a registered ask is audited by the caller.
        /// </summary>
        internal string GuardAgent(AgentCapability cap, string endpoint, string database, string origin, bool isCommit, string summary,
            string intentBasis = null, bool consumeGrant = true)
            => GuardAgent(cap, endpoint, database, origin, isCommit, summary, out _, intentBasis, consumeGrant);

        internal string GuardAgent(AgentCapability cap, string endpoint, string database, string origin, bool isCommit, string summary,
            out string approvalId, string intentBasis = null, bool consumeGrant = true)
        {
            approvalId = null;
            var policy = AgentPolicyStore.Get();
            var label = ResolveTargetLabel(endpoint, database);
            var decision = AgentPolicyGuard.Decide(cap, label, origin, isCommit, policy);
            if (decision.Outcome == GateOutcome.Allow) return null;

            // Intent binding: (capability, endpoint, database) PLUS the caller's canonical plan basis — the refs a push
            // will touch, a restore-point id, a partition+refresh type. What the human read in the summary is what the
            // grant authorises: an agent that re-plans (different refs, different restore point) hashes to a DIFFERENT
            // intent and must re-ask. Approving one action never approves a sibling action on the same target.
            var intent = ApprovalLedger.IntentHash(cap.ToString(), endpoint ?? "", database ?? "", intentBasis ?? "");
            var target = string.IsNullOrWhiteSpace(database) ? endpoint : $"{database} on {endpoint}";

            if (decision.Outcome == GateOutcome.Ask)
            {
                // Ledger throws only if it cannot take the cross-process lock — treat that as "not approved" (fail
                // closed), never as approved. A lock we can't take must never wave an action through.
                // consumeGrant=false is the QueryData mode: the grant is a time-boxed session ("read rows from this
                // target until it expires"), checked without being spent — one-shot consumption would demand an
                // approval per query and teach the user to rubber-stamp.
                try
                {
                    if (consumeGrant ? ApprovalLedger.TryConsume(cap, label, intent) : ApprovalLedger.HasLiveGrant(cap, label, intent))
                        return null;
                }
                catch { /* fail closed → refuse below */ }
                try { approvalId = ApprovalLedger.Request(cap, label, intent, summary, target).Id; } catch { /* queueing is best-effort; the refusal still stands */ }
            }
            return decision.Reason;
        }

        // ---- Agent policy + approvals: the ops behind the permissions page. Reads are free + both doors; every mutation
        // is human-only (enforced in the store/ledger, not just here) and configuring the matrix is Pro (the guardrail
        // is free, customising it is not).
        public Task<AgentPolicy> GetAgentPolicyAsync() => Task.FromResult(AgentPolicyStore.Get());
        public Task<AgentPolicy> SetAgentPolicyPresetAsync(string preset, string origin) => Task.FromResult(AgentPolicyStore.SetPreset(preset, origin, _entitlement?.IsPro ?? false));
        public Task<AgentPolicy> SetAgentPolicyCellAsync(string capability, string label, string action, string origin) => Task.FromResult(AgentPolicyStore.SetCell(capability, label, action, origin, _entitlement?.IsPro ?? false));
        public Task<AgentPolicy> SetAgentPolicyEnabledAsync(bool enabled, string origin) => Task.FromResult(AgentPolicyStore.SetEnabled(enabled, origin));
        public Task<ApprovalRecord[]> ListPendingApprovalsAsync() => Task.FromResult(ApprovalLedger.List().ToArray());
        public Task<ApprovalRecord> ApproveAgentActionAsync(string id, string origin) => Task.FromResult(ApprovalLedger.Approve(id, origin));
        public Task<bool> DenyAgentActionAsync(string id, string origin) => Task.FromResult(ApprovalLedger.Deny(id, origin));

        /// <summary>Every model source this machine has connected to, newest first. Read-only, free, both doors.</summary>
        public Task<ModelConnectionRecord[]> ListConnectionsAsync() => Task.FromResult(ConnectionRegistry.List()
            .Where(r => !string.Equals(r.Kind, "file", StringComparison.OrdinalIgnoreCase)).ToArray());

        public async Task<ModelConnectionRecord> RememberXmlaConnectionAsync(string endpoint, string database, string modelName, string authMode, string origin = "human")
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("An XMLA endpoint is required.", nameof(endpoint));
            var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
            if (mode != "azcli" && mode != "interactive" && mode != "serviceprincipal")
                throw new ArgumentException("Authentication must be azcli, interactive, or serviceprincipal.", nameof(authMode));
            var record = ConnectionRegistry.Remember("xmla", endpoint.Trim(), string.IsNullOrWhiteSpace(database) ? null : database.Trim(),
                string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim(), authMode: mode);
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "remember_xmla_connection", Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = $"Remembered XMLA connection {record.ModelName ?? record.Database ?? record.Endpoint}", Target = record.Id, Ok = true
            });
            return record;
        }

        /// <summary>Declare what a target IS (<c>local|dev|uat|prod</c>). Refused from the agent door: the agent's own
        /// permissions are gated on this label. Clearing it returns the target to unlabelled, i.e. treated as prod.</summary>
        public async Task<ModelConnectionRecord> LabelConnectionAsync(string id, string label, string origin = "agent")
        {
            var activityOrigin = string.IsNullOrWhiteSpace(origin) ? "unknown" : origin;
            ModelConnectionRecord record;
            try
            {
                record = ConnectionRegistry.SetLabel(id, label, origin);
            }
            catch (Exception ex)
            {
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "label_connection", Origin = activityOrigin, Label = "Connection label was not changed",
                    Target = string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase)
                        ? ConnectionRegistry.Find(id)?.Id ?? id
                        : id,
                    Ok = false, Error = ex.Message,
                });
                throw;
            }
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "label_connection", Origin = activityOrigin,
                Label = string.IsNullOrWhiteSpace(record.Label)
                    ? $"Cleared the label for connection {record.ModelName ?? record.Database ?? record.Endpoint}"
                    : $"Labelled connection {record.ModelName ?? record.Database ?? record.Endpoint} as {record.Label}",
                Target = record.Id, Ok = true, Result = record,
            });
            return record;
        }

        /// <summary>Point a connection at a durable local folder for its editable copy (the query-live / edit-local
        /// pattern), instead of a transient snapshot.</summary>
        public Task<ModelConnectionRecord> SetConnectionWorkingFolderAsync(string id, string folder) =>
            Task.FromResult(ConnectionRegistry.SetWorkingFolder(id, folder));

        public async Task<bool> ForgetConnectionAsync(string id, string origin = "agent")
        {
            var activityOrigin = string.IsNullOrWhiteSpace(origin) ? "unknown" : origin;
            var record = ConnectionRegistry.Find(id);
            bool forgotten;
            try
            {
                forgotten = ConnectionRegistry.Forget(id, origin);
            }
            catch (Exception ex)
            {
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "forget_connection", Origin = activityOrigin, Label = "Connection was not forgotten",
                    Target = record?.Id ?? id, Ok = false, Error = ex.Message,
                });
                throw;
            }
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "forget_connection", Origin = activityOrigin,
                Label = forgotten
                    ? $"Forgot connection {record?.ModelName ?? record?.Database ?? record?.Endpoint ?? id}"
                    : "Connection was not found",
                Target = forgotten ? record?.Id ?? id : id, Ok = forgotten,
                Error = forgotten ? null : "No remembered connection with that id. Open Connections to see what is known.",
                Result = new { forgotten },
            });
            return forgotten;
        }

        public Task<LocalInstance[]> ListLocalInstancesAsync() => Task.FromResult(LocalDiscovery.List());

        public async Task<VpaqReport> VertiPaqScanAsync(int topN)
        {
            var live = _live;
            if (live == null) return VpaqReport.FromError("Not connected. Connect to a live model (connect_xmla / connect_local) first.");
            var seg = await live.ExecuteAsync("SELECT DIMENSION_NAME, COLUMN_ID, USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", 5000000, 180);
            var col = await live.ExecuteAsync("SELECT DIMENSION_NAME, ATTRIBUTE_NAME, COLUMN_ID, COLUMN_ENCODING, DICTIONARY_SIZE, STRING_INDEX_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS", 5000000, 180);
            var tbl = await live.ExecuteAsync("SELECT DIMENSION_NAME, ROWS_COUNT FROM $SYSTEM.DISCOVER_STORAGE_TABLES", 100000, 180);
            // Flag Direct Lake from the open model (if any) so the storage figures get labelled resident-only.
            // Best-effort: a connect_xmla-only session has no file model — then we simply don't flag (no false data).
            var isDirectLake = false;
            var sess = _sessions.Current;
            if (sess != null) { try { isDirectLake = await sess.ReadAsync(m => DirectLakeInfo.IsModelDirectLake(m)); } catch { /* detection is best-effort */ } }
            return VertiPaq.Compute(seg, col, tbl, topN, isDirectLake);
        }

        public async Task<VpaxExportResult> ExportVpaxAsync(string path)
        {
            var s = _sessions.Require();
            if (string.IsNullOrWhiteSpace(path)) return new VpaxExportResult { Error = "A target .vpax file path is required." };
            var full = System.IO.Path.GetFullPath(path);

            // When the session is bound to a LIVE source (open_live), enrich the .vpax with real VertiPaq storage
            // statistics (sizes / cardinality / segments) — the primary value of a .vpax. We re-auth from the stored
            // LiveOrigin (endpoint/database/authMode/tenant — no secret) and the extractor opens its OWN short-lived
            // connection, so it never contends with the engine's single-threaded live connection. ANY failure to read
            // stats degrades gracefully to the metadata-only export below (the export never fails for missing stats).
            var origin = s.LiveOrigin;
            if (origin != null && !string.IsNullOrEmpty(origin.Endpoint))
            {
                try
                {
                    var token = await EntraToken.AcquireAsync(origin.AuthMode, null, System.Threading.CancellationToken.None, origin.TenantId);
                    var connStr = LiveConnection.XmlaConnectionString(origin.Endpoint, origin.Database, token);
                    var tables = await s.Dispatcher.RunAsync(() => VpaxExport.WriteWithStats(full, s.TomDatabase, connStr, 0));
                    return new VpaxExportResult
                    {
                        Exported = true, Path = full, Tables = tables,
                        Note = "Includes LIVE VertiPaq storage statistics (column sizes / cardinality / segments).",
                    };
                }
                catch (System.Exception ex)
                {
                    // Stats unavailable (auth / capacity / endpoint) — fall through to a metadata-only export, noting why.
                    try
                    {
                        var tables = await s.Dispatcher.RunAsync(() => VpaxExport.Write(full, s.TomDatabase));
                        return new VpaxExportResult { Exported = true, Path = full, Tables = tables, Note = "Metadata only — live storage statistics unavailable: " + ex.Message.Split('\n')[0] };
                    }
                    catch (System.Exception ex2) { return new VpaxExportResult { Error = ex2.Message }; }
                }
            }

            try
            {
                // Offline (no live source): metadata only (tables/columns/measures/relationships). Connect a live model
                // (open_live) before exporting to include storage statistics.
                var tables = await s.Dispatcher.RunAsync(() => VpaxExport.Write(full, s.TomDatabase));
                return new VpaxExportResult
                {
                    Exported = true, Path = full, Tables = tables,
                    Note = "Metadata only (tables/columns/measures/relationships). Open the model live (open_live) to include storage statistics.",
                };
            }
            catch (System.Exception ex) { return new VpaxExportResult { Error = ex.Message }; }
        }

        public Task<ConnectionStatus> ConnectionStatusAsync() =>
            Task.FromResult(_live?.Status() ?? new ConnectionStatus { Connected = false, Message = "Not connected." });

        public async Task<ConnectionStatus> DisconnectAsync()
        {
            var (won, displaced) = SwapLive(null, NewLiveIntent());   // detach atomically under the intent ticket
            SafeDispose(displaced);
            if (!won) return SupersededStatus("disconnect");          // a newer connect/open raced in — leave its connection be
            await SafeRebroadcastWorkflowLibraryAsync();   // connection → offline: activation may re-curate the menu (§10.6)
            return new ConnectionStatus { Connected = false, Message = "Disconnected." };
        }

        public Task<ResultSet> RunDaxAsync(string query, int maxRows, string origin = "human")
        {
            var live = _live;
            if (live == null) return Task.FromResult(ResultSet.FromError("Not connected. Call connect_xmla or connect_local first."));
            // #129 agent-permissions gate. run_dax maps to QueryCalc (a calculation is allowed everywhere), but a
            // row-returning `EVALUATE <table>` reads real source rows — the SAME QueryData exfiltration surface
            // preview_table gates. Classify the shape and route a row-returning query through the identical
            // QueryData guard: same session-grant semantics (consumeGrant:false, intentBasis "querydata"), so ONE
            // approval covers preview_table AND row-returning run_dax on a target until it expires. Fail closed —
            // anything not confidently `EVALUATE ROW(...)` / `EVALUATE { … }` is gated. A genuinely scalar probe (the
            // verified-measure/probe shape) stays ungated so those flows keep running everywhere, prod included.
            if (DaxQueryClassifier.IsRowReturning(query))
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"run a row-returning DAX query against {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (gate != null) return Task.FromResult(ResultSet.FromRefusal(gate, approvalId));
            }
            return live.ExecuteAsync(query, maxRows, 120);
        }

        public Task<ResultSet> RunDmvAsync(string query, int maxRows)
        {
            var live = _live;
            if (live == null) return Task.FromResult(ResultSet.FromError("Not connected. Call connect_xmla or connect_local first."));
            return live.ExecuteAsync(query, maxRows, 120);
        }

        public Task<ResultSet> PreviewTableAsync(string table, int topN, string origin = "human")
        {
            var live = _live;
            if (live == null) return Task.FromResult(ResultSet.FromError("Not connected. Call connect_xmla or connect_local first."));
            if (string.IsNullOrWhiteSpace(table)) return Task.FromResult(ResultSet.FromError("A table name (or table: ref) is required."));
            // Agent-permissions gate — returning rows IS the action (there is no dry run of a data read), so the
            // preview exemption never applies to QueryData. The grant is a time-boxed session on the TARGET
            // (consumeGrant:false): one approval covers reads from this endpoint until it expires — one-shot here
            // would demand an approval per query and teach the user to rubber-stamp.
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"preview rows of '{table}' from {live.Database} on {live.DataSource}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (gate != null) return Task.FromResult(ResultSet.FromRefusal(gate, approvalId));
            }
            var name = table.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? table.Substring("table:".Length) : table;
            name = name.Trim().Replace("'", "''");
            var n = topN <= 0 ? 200 : topN;
            return live.ExecuteAsync($"EVALUATE TOPN({n}, '{name}')", n, 120);
        }

        public Task<ResultSet> PivotMeasureAsync(string measureExpr, string[] rowFields, string colField, string[] filters, int maxRows, string origin = "human")
        {
            var live = _live;
            if (live == null) return Task.FromResult(ResultSet.FromError("Not connected. Call connect_xmla or connect_local first."));
            if (string.IsNullOrWhiteSpace(measureExpr)) return Task.FromResult(ResultSet.FromError("A measure expression is required."));
            // #129 follow-up: the pivot is a row-returner BY CONSTRUCTION (EVALUATE SUMMARIZECOLUMNS over source
            // grouping columns) — no shape to classify — so every agent call takes the same QueryData gate as
            // preview_table / run_dax, on the shared "querydata" session grant (one approval covers them all).
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"pivot a measure over source grouping columns on {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (gate != null) return Task.FromResult(ResultSet.FromRefusal(gate, approvalId));
            }
            var args = new List<string>();
            foreach (var r in (rowFields ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))) args.Add(r.Trim());
            if (!string.IsNullOrWhiteSpace(colField)) args.Add(colField.Trim());
            foreach (var f in (filters ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))) args.Add(f.Trim());
            args.Add("\"__v\", " + DaxBench.InlineScalar(measureExpr));   // comment-proof: a trailing "-- note" must not eat the query
            var q = "EVALUATE\nSUMMARIZECOLUMNS(\n    " + string.Join(",\n    ", args) + "\n)";
            return live.ExecuteAsync(q, maxRows <= 0 ? 100000 : maxRows, 120);
        }

        public Task<BenchmarkResult> BenchmarkDaxAsync(string query, int runs)
        {
            var live = _live;
            if (live == null) return Task.FromResult(new BenchmarkResult { Error = "Not connected. Call connect_xmla or connect_local first." });
            return DaxBench.BenchmarkAsync(live, query, runs);
        }

        public Task<ServerTimings> ProfileDaxAsync(string query)
        {
            var live = _live;
            if (live == null) return Task.FromResult(new ServerTimings { Error = "Not connected. Call connect_xmla or connect_local first." });
            return DaxTrace.ProfileAsync(live, query);
        }

        public Task<EvalLogResult> EvaluateAndLogAsync(string query, int maxRows, string origin = "human")
        {
            var live = _live;
            if (live == null) return Task.FromResult(new EvalLogResult { Error = "Not connected. Call connect_xmla or connect_local first." });
            // #129 follow-up: gated UNCONDITIONALLY at agent origin — a row-returner by construction, like
            // pivot_measure. The op's whole purpose is surfacing the EVALUATEANDLOG log channel, and that channel can
            // carry capped row samples of an arbitrary attacker-chosen table REGARDLESS of the outer query shape
            // (EVALUATE ROW("v", COUNTROWS(EVALUATEANDLOG('Sales'))) is scalar-shaped yet logs Sales rows), and the
            // per-event cap doesn't bound multi-call pagination — so shape classification is the wrong tool here;
            // QueryData applies to every agent call. Shares the "querydata" session grant (one approval covers the
            // family); humans are never gated (GuardAgent's origin precedence).
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"run a DAX query with EVALUATEANDLOG row-sample tracing against {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (gate != null) return Task.FromResult(new EvalLogResult { Error = gate, ApprovalId = approvalId });
            }
            return DaxTrace.EvaluateAndLogAsync(live, query, maxRows);
        }

        public Task<EquivalenceResult> VerifyEquivalenceAsync(string exprA, string exprB, string[] groupBy, string[] filters, int maxRows)
        {
            var live = _live;
            if (live == null) return Task.FromResult(new EquivalenceResult { Error = "Not connected. Call connect_xmla or connect_local first." });
            return DaxBench.VerifyEquivalenceAsync(live, exprA, exprB, groupBy, filters, maxRows);
        }

        // Clear the Storage-Engine cache (the cold/warm-benchmark primitive). SAFETY: cache-clear evicts the cache
        // for ALL users of a SHARED model, so on a non-local endpoint it refuses unless confirm=true. This engine-side
        // gate is authoritative for BOTH doors (the UI and the agent share this one IEngine path). Non-destructive.
        public async Task<ClearCacheResult> ClearCacheAsync(bool confirm)
        {
            var live = _live;
            if (live == null) return new ClearCacheResult { Error = "Not connected. Call connect_xmla or connect_local first." };
            var local = LiveDeploy.IsLocalEndpoint(live.DataSource);
            if (!local && !confirm)
                return new ClearCacheResult
                {
                    Cleared = false, Local = false, DataSource = live.DataSource, Database = live.Database,
                    Error = "Refusing to clear the Storage-Engine cache on a SHARED/cloud endpoint without confirmation — it evicts the cache for ALL users of '"
                            + (string.IsNullOrEmpty(live.Database) ? "this model" : live.Database) + "' (their next queries run cold). Pass confirm=true to proceed.",
                };
            var r = await DaxCache.ClearAsync(live);
            r.Local = local;
            return r;
        }

        // Cold/warm benchmark (the DAX-Studio "Run Benchmark"). Same cache-clear gate as ClearCacheAsync: don't clear
        // a SHARED cache without confirm — but DEGRADE (warm-only) rather than refuse, since the benchmark still
        // produces useful numbers. Authoritative for both doors.
        public async Task<ColdWarmBenchmark> BenchmarkColdWarmAsync(string query, int runs, bool clearForCold, bool confirm)
        {
            var live = _live;
            if (live == null) return new ColdWarmBenchmark { Error = "Not connected. Call connect_xmla or connect_local first." };
            var local = LiveDeploy.IsLocalEndpoint(live.DataSource);
            var allowClear = clearForCold && (local || confirm);
            var r = await DaxBench.BenchmarkColdWarmAsync(live, query, runs, allowClear);
            if (clearForCold && !allowClear && !local && string.IsNullOrEmpty(r.Error))
                r.Note = "Cold runs skipped — clearing the cache on a shared/cloud endpoint needs confirm=true (it affects all users). Showing warm-only. " + (r.Note ?? "");
            return r;
        }

        public Task<QueryPlanResult> CaptureQueryPlanAsync(string query)
        {
            var live = _live;
            if (live == null) return Task.FromResult(new QueryPlanResult { Error = "Not connected. Call connect_xmla or connect_local first." });
            return DaxTrace.CaptureQueryPlanAsync(live, query);
        }

        // The spread (max - min) of a benchmark's WARM runs — the empirical noise floor a rewrite must beat before
        // we call it "faster". RunsMs holds ALL runs incl. the first (cold-ish) at index 0; skip it to match how
        // WarmMin/WarmMedian are computed (fall back to all runs when there's only one).
        private static long WarmSpread(BenchmarkResult b)
        {
            var runs = b?.RunsMs ?? Array.Empty<long>();
            var warm = runs.Length > 1 ? runs.Skip(1).ToArray() : runs;
            return warm.Length == 0 ? 0L : warm.Max() - warm.Min();
        }

        // ---- Verified Edits: optimize_measure --------------------------------------------------------
        // The enforced "author variants → prove equivalent → benchmark → apply the fastest" workflow. The engine does
        // the deterministic work (no inference): it RACES the caller's >=2 candidate rewrites, proves each returns the
        // SAME values as the current body across a filter-context matrix (correctness), benchmarks ONLY the proven set
        // (so speed can never buy incorrectness), and applies the fastest that beats the baseline beyond a noise band.
        // It REFUSES to finalize without >=2 candidates / a live connection / a proven winner — so a measure cannot be
        // "optimized" without evidence. Auto-apply is the Pro value; free returns the full evidence (paused) so the
        // human/agent can apply the winner manually with update_measure (degrade, don't disappear).
        public async Task<OptimizeMeasureResult> OptimizeMeasureAsync(string measureRef, string[] candidates, string[] verifyGroupBy, string[] verifyFilters, bool apply, string origin)
        {
            // 1) ENFORCE >=2 candidates — the workflow precondition. Thrown before any resolve/mutate.
            var cands = (candidates ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToArray();
            if (cands.Length < 2)
                throw new InvalidOperationException("optimize_measure requires >=2 candidate expressions — author independent rewrites so the engine can prove them equivalent and race them for speed.");

            var s = _sessions.Require();

            // 2) Resolve + read the CURRENT body as the baseline (candidates are proven against it).
            string baseline;
            try { baseline = await s.ReadAsync(m => CurrentDax(ObjectRefs.Resolve(m, measureRef))); }
            catch (Exception ex) { return new OptimizeMeasureResult { Verdict = "error", Error = ex.Message }; }
            if (baseline == null)
                return new OptimizeMeasureResult { Verdict = "error", Error = $"optimize_measure: {measureRef} has no DAX expression to optimize." };

            // 3) Validate each candidate offline; invalid ones are excluded from the race (not fatal unless <2 remain).
            var evid = new List<CandidateEvidence>();
            for (int i = 0; i < cands.Length; i++)
            {
                var val = await ValidateDaxAsync(cands[i]);
                var bad = val == null || !val.Valid;
                evid.Add(new CandidateEvidence { Index = i, Expression = cands[i], VerifyState = bad ? "invalid" : "pending", Note = bad ? "invalid DAX" : null });
            }
            var valid = evid.Where(e => e.VerifyState != "invalid").ToList();
            if (valid.Count < 2)
                return new OptimizeMeasureResult { Verdict = "insufficient-valid", BaselineExpression = baseline, Candidates = evid.ToArray(), Note = "fewer than 2 candidates are valid DAX — nothing raced." };

            // 4) Prove + benchmark need a live endpoint. Offline → DEGRADE (evidence-only), never mutate.
            var live = _live;
            if (live == null)
                return new OptimizeMeasureResult { Verdict = "unproven-offline", BaselineExpression = baseline, Candidates = evid.ToArray(),
                    Note = "No live connection to prove equivalence / benchmark. Connect with open_live or open_local, or apply a rewrite manually with update_measure." };

            // 5) Prove equivalence candidate-vs-baseline. Same not-proven downgrade ladder as apply_change_plan
            //    (Truncated / RowsCompared<=0 / empty group-by ⇒ NOT a proof) so a thin matrix can't green a rewrite.
            var gb = verifyGroupBy ?? Array.Empty<string>();
            foreach (var e in valid)
            {
                var v = await DaxBench.VerifyEquivalenceAsync(live, baseline, e.Expression, gb, verifyFilters, 100000);
                e.Equivalence = v;
                if (!string.IsNullOrEmpty(v.Error)) { e.VerifyState = "unverified"; e.Note = "equivalence check failed to run — " + v.Error; }
                else if (!v.AllMatch) { e.VerifyState = "failed"; e.Note = $"changes results in {v.MismatchCount} context(s)"; }
                else if (v.RowsCompared <= 0) { e.VerifyState = "unverified"; e.Note = "equivalence check compared 0 rows (nothing to prove)"; }
                else if (v.Truncated) { e.VerifyState = "unverified"; e.Note = $"equivalence matrix exceeded the row cap ({v.RowsCompared}+ rows) — coverage incomplete"; }
                else if (gb.Length == 0) { e.VerifyState = "unverified"; e.Note = "grand-total match only — not a per-context equivalence proof (pass verifyGroupBy)"; }
                else e.VerifyState = "proven";
            }

            // Audit-trail wing: a REAL Pro attempt (apply=true) is recorded whatever the outcome — "2 candidates,
            // identical, no gain" is exactly the memory the referee sells. Dry-runs and free-tier evidence runs stay
            // transient (no annotation side effect from exploration). The compact evidence keeps the honesty flags
            // (rows compared / truncated / mismatches) so a persisted "proven" can be re-audited later.
            var recordOutcome = apply && (_entitlement?.IsPro ?? false);
            string EvidenceJson(double? baselineMs, double? bandMs) => System.Text.Json.JsonSerializer.Serialize(new
            {
                measureRef,
                grid = new { groupBy = gb, filters = verifyFilters ?? Array.Empty<string>() },
                baselineWarmMedianMs = baselineMs,
                noiseBandMs = bandMs,
                candidates = evid.Select(e => new
                {
                    e.Index, e.VerifyState,
                    rows = e.Equivalence?.RowsCompared, truncated = e.Equivalence?.Truncated,
                    mismatches = e.Equivalence?.MismatchCount, warmMedianMs = e.Benchmark?.WarmMedianMs, e.Note,
                    // top mismatch contexts persist so the evidence GRID survives across sessions (counts alone can't re-render it)
                    mismatchSamples = (object)(e.Equivalence?.Mismatches ?? Array.Empty<EquivalenceMismatch>()).Take(8).Select(m => new { m.Context, m.ValueA, m.ValueB }).ToArray(),
                }).ToArray(),
            });

            var proven = valid.Where(e => e.VerifyState == "proven").ToList();
            if (proven.Count == 0)
            {
                if (recordOutcome)
                    // Revision 0: no mutation backs this record — stamping the CURRENT session revision would
                    // weld the verdict badge onto whatever unrelated edit happens to hold that revision.
                    await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                    {
                        SessionId = s.Id, Revision = 0, Origin = origin, Op = "optimize_measure", ObjectRef = measureRef,
                        Verdict = "needs-review", Summary = "no candidate proved equivalent over the supplied matrix — nothing applied",
                        Evidence = EvidenceJson(null, null), BodyHash = VerifiedEditsStore.BodyHash(baseline),
                    });
                return new OptimizeMeasureResult { Verdict = "none-proven", BaselineExpression = baseline, Candidates = evid.ToArray(),
                    Note = "No candidate proved equivalent over the supplied matrix — nothing applied. Widen verifyGroupBy or fix the rewrites." };
            }

            // 6) Benchmark ONLY the proven set + the baseline over the SAME grid the equivalence was proven on — a
            //    grand-total EVALUATE ROW would reward a rewrite that only wins at the total while losing per row.
            //    gb is guaranteed non-empty here: an empty group-by downgrades every candidate to "unverified" above,
            //    so proven.Count would be 0 and we'd have returned "none-proven" already. (correctness gates speed.)
            string BenchQuery(string expr) => DaxBench.BuildProbeQuery(expr, gb, verifyFilters);
            var baseBench = await DaxBench.BenchmarkAsync(live, BenchQuery(baseline), 5);
            foreach (var e in proven) e.Benchmark = await DaxBench.BenchmarkAsync(live, BenchQuery(e.Expression), 5);

            // 7) Pick the fastest proven candidate that beats the baseline beyond a NOISE BAND, ranked on the warm
            //    MEDIAN (robust to a single lucky-fast run that WarmMin would reward). The band is the larger of 5%
            //    of the baseline median and the baseline's own warm run spread (max-min) — so a "win" must clear the
            //    noise we actually measured, not a fixed guess. Else keep the original.
            CandidateEvidence winner = null;
            foreach (var e in proven.Where(e => e.Benchmark != null && string.IsNullOrEmpty(e.Benchmark.Error)))
                if (winner == null || e.Benchmark.WarmMedianMs < winner.Benchmark.WarmMedianMs) winner = e;
            var baseOk = baseBench != null && string.IsNullOrEmpty(baseBench.Error);
            var baseMs = baseOk ? baseBench.WarmMedianMs : long.MaxValue;
            var band = baseOk ? Math.Max(baseMs * 0.05, WarmSpread(baseBench)) : 0d;   // ms the winner must beat baseline by
            var improves = winner != null && baseOk && winner.Benchmark.WarmMedianMs < baseMs - band;
            if (!improves)
            {
                if (recordOutcome)
                    await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                    {
                        SessionId = s.Id, Revision = 0, Origin = origin, Op = "optimize_measure", ObjectRef = measureRef,   // 0: nothing mutated
                        Verdict = "proven", Summary = "no proven-equivalent candidate beat the baseline beyond the noise band — kept the original",
                        Evidence = EvidenceJson(baseOk ? baseMs : (double?)null, baseOk ? band : (double?)null),
                        BodyHash = VerifiedEditsStore.BodyHash(baseline),
                    });
                return new OptimizeMeasureResult { Verdict = "no-improvement", BaselineExpression = baseline, Candidates = evid.ToArray(),
                    WinnerIndex = winner?.Index ?? -1, WinnerExpression = winner?.Expression,
                    Note = "No proven-equivalent candidate beat the current body beyond the noise band — kept the original." };
            }

            // 8) Dry-run / free-tier gate: never mutate. Return the full evidence so the winner can be applied manually.
            if (!apply)
                return new OptimizeMeasureResult { Verdict = "dry-run", BaselineExpression = baseline, Candidates = evid.ToArray(),
                    WinnerIndex = winner.Index, WinnerExpression = winner.Expression, Note = "Dry run — fastest proven-equivalent candidate identified, not applied." };
            if (_entitlement == null || !_entitlement.IsPro)
                return new OptimizeMeasureResult { Verdict = "paused-free", BaselineExpression = baseline, Candidates = evid.ToArray(),
                    WinnerIndex = winner.Index, WinnerExpression = winner.Expression,
                    Note = $"Auto-apply is a Pro feature. Candidate {winner.Index} is the fastest proven-equivalent rewrite ({baseMs}ms → {winner.Benchmark.WarmMedianMs}ms warm-median over the verify grid) — apply it with update_measure, or upgrade to auto-apply." };

            // 9) Apply the winner as one undoable revision (broadcasts model/didChange to both doors).
            var applied = false;
            var rev = await s.MutateAsync(origin, "optimize measure", m =>
            {
                var obj = ObjectRefs.Resolve(m, measureRef);
                switch (obj)
                {
                    case Measure me: if (me.Expression != winner.Expression) { me.Expression = winner.Expression; applied = true; } break;
                    case CalculatedColumn cc: if (cc.Expression != winner.Expression) { cc.Expression = winner.Expression; applied = true; } break;
                    case CalculatedTable ct: if (ct.Expression != winner.Expression) { ct.Expression = winner.Expression; applied = true; } break;
                    case CalculationItem ci: if (ci.Expression != winner.Expression) { ci.Expression = winner.Expression; applied = true; } break;
                    case Function f: if (f.Expression != winner.Expression) { f.Expression = winner.Expression; applied = true; } break;
                    default: throw new InvalidOperationException($"optimize_measure: {measureRef} has no DAX expression — it targets a measure; run list_measures to find one.");
                }
            });
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = rev, Origin = origin, Op = "optimize_measure", ObjectRef = measureRef,
                Verdict = "proven",
                Summary = $"applied candidate {winner.Index}: {baseMs}ms → {winner.Benchmark.WarmMedianMs}ms warm-median, proven-equivalent over {winner.Equivalence.RowsCompared} rows of the verify grid",
                Evidence = EvidenceJson(baseMs, band), BodyHash = VerifiedEditsStore.BodyHash(winner.Expression),
            });
            return new OptimizeMeasureResult { Verdict = "applied", Applied = applied, Revision = rev, BaselineExpression = baseline, Candidates = evid.ToArray(),
                WinnerIndex = winner.Index, WinnerExpression = winner.Expression,
                Note = $"Applied candidate {winner.Index}: {baseMs}ms → {winner.Benchmark.WarmMedianMs}ms (warm-median over the verify grid), proven-equivalent over {winner.Equivalence.RowsCompared} contexts." };
        }

        // ---- Verified Edits: probe_measure -----------------------------------------------------------
        // Run a candidate measure across a SCENARIO MATRIX (outer filter × grain) and report BEHAVIOR — value/BLANK/
        // ERROR per member, coverage, additivity — never "correct". Replicates a real visual via
        // SUMMARIZECOLUMNS(axis, filter args, "v", expr, "__present",1) so context-sensitive functions (ALLSELECTED,
        // SELECTEDVALUE, ISINSCOPE) resolve correctly. Read-only, needs a live connection, no engine inference (the
        // engine only executes DAX + reads results). Ungated (a read) — the Pro value is the enforced Verified-Mode loop.
        private static ProbeFidelity ProbeManifest() => new ProbeFidelity
        {
            Modeled = new[] { "outer slicer/page filters (as filter args)", "the visual axis (group-by)", "ALLSELECTED shadow context at the leaf grain" },
            NotModeled = new[] { "measure-level / visual-level filters (Top-N, filter-on-measure)", "cross-highlight between visuals", "subtotal/matrix shadow context beyond supplied scenarios", "RLS / OLS" },
            Note = "Replicates a single visual's query shape (outer + axis layers, ~2 of ~6 real filter layers). Evidence of BEHAVIOR across contexts, NOT a correctness verdict.",
        };
        private static bool IsNum(object o) => o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint || o is long || o is ulong || o is float || o is double || o is decimal;

        public async Task<ProbeResult> ProbeMeasureAsync(string expr, string primaryAxis, ProbeScenario[] scenarios, bool includeDefaults, int rowCap)
        {
            var fid = ProbeManifest();
            if (string.IsNullOrWhiteSpace(expr))
                return new ProbeResult { Status = "error", Message = "probe_measure needs a measure expression.", Fidelity = fid };
            // Refuse, don't fake: rank/TOPN depend on the report's FULL member set + ordering — a filtered scenario
            // changes the domain, so the probe would emit silently-wrong ranks. Better to refuse than mislead.
            if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"\b(RANKX|TOPN|RANK|SAMPLE)\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return new ProbeResult { Status = "unfaithful", Fidelity = fid,
                    Message = "Cannot faithfully probe a rank/TOPN measure: its result depends on the report's full member set + ordering, which a filtered scenario changes — the probe would report wrong values. Verify this one in a real visual." };

            var live = _live;
            if (live == null)
                return new ProbeResult { Status = "no-connection", Fidelity = fid,
                    Message = "probe_measure needs a live model (open_live / open_local). It runs the measure across filter contexts to gather evidence." };

            var scen = new List<ProbeScenario>();
            if (scenarios != null) scen.AddRange(scenarios.Where(s => s != null));
            if ((scen.Count == 0 || includeDefaults) && !string.IsNullOrWhiteSpace(primaryAxis))
                scen.AddRange(await BuildDefaultScenariosAsync(live, primaryAxis.Trim(), rowCap));
            if (scen.Count == 0)
                return new ProbeResult { Status = "error", Fidelity = fid, Message = "No scenarios: pass scenarios[] or a primaryAxis to auto-generate them." };

            var cap = rowCap <= 0 ? 5000 : rowCap;
            var ev = new List<ScenarioEvidence>();
            foreach (var sc in scen) ev.Add(await RunScenarioAsync(live, expr, sc, cap));

            var blanks = new List<int>(); var nonAdd = new List<int>(); var errs = 0;
            for (int i = 0; i < ev.Count; i++)
            {
                if (ev[i].Status == "error") errs++;
                if (ev[i].Coverage != null && ev[i].Coverage.NonBlank < ev[i].Coverage.Total) blanks.Add(i);
                if (ev[i].Additivity == "non-additive") nonAdd.Add(i);
            }
            return new ProbeResult { Status = "ok", Fidelity = fid, Scenarios = ev.ToArray(), ScenariosRun = ev.Count,
                ErrorCount = errs, BlankScenarios = blanks.ToArray(), NonAdditiveScenarios = nonAdd.ToArray() };
        }

        // Engine-generated defaults from one axis: grand, by-axis, single-select, multi-select, empty-select, boundary.
        // (Cross-column and subtotal scenarios are agent-supplied — the engine can't infer which second dimension matters.)
        private static async Task<List<ProbeScenario>> BuildDefaultScenariosAsync(LiveConnection live, string axis, int rowCap)
        {
            var list = new List<ProbeScenario>
            {
                new ProbeScenario { Name = "grand total" },
                new ProbeScenario { Name = "by " + axis + " (unfiltered)", GroupBy = new[] { axis } },
            };
            string[] members = Array.Empty<string>();
            try
            {
                var rs = await live.ExecuteAsync("EVALUATE TOPN(50, VALUES(" + axis + "))", 50, 60);
                if (rs != null && string.IsNullOrEmpty(rs.Error) && rs.Rows != null)
                    members = rs.Rows.Where(r => r != null && r.Length > 0 && r[0] != null)
                        .Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            }
            catch { /* members optional — grand + empty still run */ }
            if (members.Length >= 1)
                list.Add(new ProbeScenario { Name = "single-select " + axis + "=" + members[0], GroupBy = new[] { axis }, Filters = new[] { new ProbeFilter { Column = axis, Members = new[] { members[0] } } } });
            if (members.Length >= 2)
                list.Add(new ProbeScenario { Name = "multi-select " + axis, GroupBy = new[] { axis }, Filters = new[] { new ProbeFilter { Column = axis, Members = members.Take(Math.Min(3, members.Length)).ToArray() } } });
            list.Add(new ProbeScenario { Name = "empty-select " + axis, GroupBy = new[] { axis }, Filters = new[] { new ProbeFilter { Column = axis, Empty = true } } });
            if (members.Length >= 1)
                list.Add(new ProbeScenario { Name = "boundary " + axis + "=" + members[members.Length - 1], GroupBy = new[] { axis }, Filters = new[] { new ProbeFilter { Column = axis, Members = new[] { members[members.Length - 1] } } } });
            return list;
        }

        private static async Task<ScenarioEvidence> RunScenarioAsync(LiveConnection live, string expr, ProbeScenario sc, int cap)
        {
            var axis = (sc.GroupBy ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();
            var filterArgs = (sc.Filters ?? Array.Empty<ProbeFilter>()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Column))
                .Select(f => f.Empty ? DaxBench.CompileEmptyFilter(f.Column) : DaxBench.CompileMemberFilter(f.Column, f.Members)).ToArray();
            var query = DaxBench.BuildProbeQuery(expr, axis, filterArgs);
            var e = new ScenarioEvidence { Name = sc.Name, Dax = query, Status = "ok" };
            ResultSet rs;
            try { rs = await live.ExecuteAsync(query, cap, 120); }
            catch (Exception ex) { e.Status = "error"; e.Error = ex.Message; return e; }
            if (rs == null || !string.IsNullOrEmpty(rs.Error)) { e.Status = "error"; e.Error = rs?.Error ?? "no result"; return e; }
            e.Truncated = rs.Truncated;
            var vIdx = axis.Length;   // result columns: [axis…], v, __present
            var rows = new List<ProbeRow>(); var nonBlank = 0; double sumLeaf = 0; var allNumeric = true; var anyValue = false;
            foreach (var r in rs.Rows ?? Array.Empty<object[]>())
            {
                if (r == null || r.Length <= vIdx) continue;
                var v = r[vIdx]; var blank = v == null;
                var pr = new ProbeRow { V = v, Blank = blank };
                for (var i = 0; i < axis.Length && i < r.Length; i++) pr.Members[axis[i]] = r[i];
                rows.Add(pr);
                if (!blank) { nonBlank++; anyValue = true; if (IsNum(v)) sumLeaf += Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); else allNumeric = false; }
            }
            e.Rows = rows.ToArray();
            e.Coverage = new ProbeCoverage { Total = rows.Count, NonBlank = nonBlank, BlankPct = rows.Count == 0 ? 0 : Math.Round((rows.Count - nonBlank) / (double)rows.Count, 4) };
            e.Additivity = "undefined";
            if (axis.Length > 0 && anyValue && allNumeric)
            {
                try
                {
                    var grs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(expr, Array.Empty<string>(), filterArgs), 2, 60);
                    if (grs != null && string.IsNullOrEmpty(grs.Error) && grs.Rows != null && grs.Rows.Length > 0 && grs.Rows[0].Length > 0 && grs.Rows[0][0] != null && IsNum(grs.Rows[0][0]))
                    {
                        var grand = Convert.ToDouble(grs.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
                        var diff = Math.Abs(grand - sumLeaf); var scale = Math.Max(Math.Abs(grand), Math.Abs(sumLeaf));
                        e.Additivity = (diff <= 1e-9 || diff <= scale * 1e-7) ? "additive" : "non-additive";
                    }
                }
                catch { /* additivity optional */ }
            }
            var flags = new List<string>();
            if (rows.Count > 0 && nonBlank < rows.Count) flags.Add($"blank in {rows.Count - nonBlank} of {rows.Count} rows");
            if (e.Additivity == "non-additive") flags.Add("Σ(parts) ≠ grand total — non-additive (legitimate for distinct-count/semi-additive; else a bug)");
            if (e.Truncated) flags.Add($"row cap hit ({rows.Count}) — coverage incomplete");
            e.Flags = flags.ToArray();
            return e;
        }

        // Live activity: stamp a monotonic Seq (ordering across the broadcast) and fan out on THIS engine's
        // ChangeBus → RpcServer re-broadcasts model/activity to every connected client (the human's Studio).
        // Called from the agent door (McpTools) after a read op; in the cross-process case it arrives here via
        // the RemoteEngine proxy (RPC "publishActivity"), so it always runs on the OWNER that the UI is attached to.
        private static long _activitySeq;
        public Task PublishActivityAsync(ActivityEvent e)
        {
            if (e != null)
            {
                e.Seq = System.Threading.Interlocked.Increment(ref _activitySeq);
                // Freeze the session identity at EMIT (the call site's own thread), so the experience tee attributes
                // the record to the session that was current when the op ran — NOT to whatever is current when the
                // (possibly RPC-forwarded, async) event is finally handled. ??= so a call site that already stamped a
                // specific session wins. (A direct Bus.PublishActivity emit stays null ⇒ the tee falls back to Current,
                // which is correct for those synchronous in-op publishes.)
                e.SessionId ??= _sessions.Current?.Id;
                _sessions.Bus.PublishActivity(e);
            }
            return Task.CompletedTask;
        }

        // ---- value-capture-at-edit-start (Verified Edits, RESTRUCTURE's load-bearing primitive) ----

        /// <summary>Freeze the MEASURED values of an object's blast radius (its downstream measures) over a
        /// SUMMARIZECOLUMNS grid, BEFORE an edit. FREE (Kane 2026-07-07: manual capture/compare are the free
        /// safety net — only the future ambient auto-capture + blame_value are Pro). Live-required: baselines
        /// are numbers, not metadata.</summary>
        public async Task<BaselineCaptureResult> CaptureBaselineAsync(string objRef, string[] groupBy, string[] filters, bool includeDependents, int maxMeasures, int rowCap, string label, string origin)
        {
            var s = _sessions.Require();
            if (string.IsNullOrWhiteSpace(objRef))
                return new BaselineCaptureResult { Status = "error", Message = "objRef is required — the object you are about to change." };
            var live = _live;
            if (live == null)
                return new BaselineCaptureResult { Status = "no-connection", Message = "capture_baseline needs a live model (open_live / open_local) — a baseline is measured values, not static metadata." };

            var (targets, skipped) = await s.ReadAsync(m => Baseline.Targets(m, objRef, includeDependents, maxMeasures));
            if (targets.Count == 0)
                return new BaselineCaptureResult { Status = "error", Skipped = skipped.ToArray(), Message = $"The blast radius of '{objRef}' contains no measures — nothing to baseline (impact_of shows what it does contain)." };

            groupBy = groupBy ?? Array.Empty<string>();
            filters = filters ?? Array.Empty<string>();
            var state = new BaselineState
            {
                Id = "bl-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                WhenUtc = DateTime.UtcNow, Revision = s.Revision, Root = objRef, GroupBy = groupBy, Filters = filters,
            };
            foreach (var t in targets)
            {
                var tag = await s.ReadAsync(m => (ObjectRefs.Resolve(m, t.Ref) as Measure)?.LineageTag);
                var e = new BaselineEntryState { Ref = t.Ref, Name = t.Name, LineageTag = tag, Rows = new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal) };
                try
                {
                    var rs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(t.Name), groupBy, filters), rowCap, 120);
                    if (!string.IsNullOrEmpty(rs.Error)) e.Error = rs.Error;   // kept — compare reports it not-comparable
                    else { e.Rows = Baseline.KeyRows(rs, groupBy.Length); e.Truncated = rs.Truncated; }
                }
                catch (Exception ex) { e.Error = ex.Message; }
                state.Entries.Add(e);
            }

            string dropped;
            await _baselineGate.WaitAsync();
            try { dropped = _baselines.Add(state); }
            finally { _baselineGate.Release(); }

            // CERTIFIED TOTALS: a `label` promotes this capture to a PERSISTED, immutable, tamper-evident,
            // model-bound certified baseline (month-end close). Fail-closed and honest — see CertifyAsync.
            string certifiedNote = null;
            if (!string.IsNullOrWhiteSpace(label))
                certifiedNote = await CertifyAsync(label.Trim(), groupBy, filters, state, live, s, origin);

            return new BaselineCaptureResult
            {
                Status = "ok", CaptureId = state.Id, When = state.WhenUtc.ToString("o"), Revision = state.Revision,
                Root = objRef, GroupBy = groupBy, Filters = filters,
                Entries = state.Entries.Select(e => new BaselineEntryInfo { Ref = e.Ref, Name = e.Name, RowCount = e.Rows.Count, Truncated = e.Truncated, Error = e.Error }).ToArray(),
                Skipped = skipped.ToArray(),
                // The grand-total-only warning is thin evidence for a RESTRUCTURE compare — but for a certified
                // total (a labeled capture) a single number at its stated context is exactly the intent, so the
                // warning would misguide; suppress it there.
                Message = (groupBy.Length == 0 && string.IsNullOrWhiteSpace(label) ? "Grand-total-only grid — thin coverage; pass groupBy columns for real evidence. " : null)
                        + (dropped != null ? $"Oldest capture '{dropped}' was dropped (the store holds {BaselineStore.MaxHeld})." : null)
                        + certifiedNote,
            };
        }

        /// <summary>The persisted certified-baseline sidecar (`.semanticus/certified-baselines.json`), or null when
        /// there is nowhere to persist (no model, no workspace). Shares the SidecarDir root with workflows/layout.</summary>
        private string CertifiedFilePath()
        {
            var sidecar = SidecarDir();
            return sidecar == null ? null : System.IO.Path.Combine(sidecar, "certified-baselines.json");
        }

        /// <summary>Persist a labeled capture as a CERTIFIED baseline (month-end close) — fail-closed and honest.
        /// A figure that could not be evaluated at its context, or whose context is time-volatile (TODAY/NOW/…),
        /// is NOT certified and is named in the note. The baseline is stamped with the model+context it was captured
        /// on (P1-5), records each measure's durable LineageTag (P1-4), is immutable + tamper-evident (P1-3), and the
        /// certification is recorded to the Verified Edits audit trail (P2-8). Returns the note the capture appends.</summary>
        private async Task<string> CertifyAsync(string label, string[] groupBy, string[] filters,
            BaselineState state, LiveConnection live, Session s, string origin)
        {
            var file = CertifiedFilePath();
            if (file == null)
                return " NOT CERTIFIED: no .semanticus sidecar to persist the certified figures (open/save the model first).";

            // P2-F: a LITERAL time-volatile function (TODAY/NOW/…) re-evaluates to a different window — never certifiable.
            var volatileFn = CertifiedStore.VolatileContext(filters);
            if (volatileFn != null)
                return $" NOT CERTIFIED: the context uses the literal {volatileFn}() function, which re-evaluates to a different window over time — a certified figure must be a fixed number at a FIXED context. Restate the context with explicit bounds (a fixed date/period), then re-capture.";

            // P1-2: only figures that actually EVALUATED are certified; name the ones that failed (fail closed).
            var failed = state.Entries.Where(e => !string.IsNullOrEmpty(e.Error) || e.Rows.Count == 0).ToList();
            var good = state.Entries.Where(e => string.IsNullOrEmpty(e.Error) && e.Rows.Count > 0).ToList();
            string FailList() => string.Join("; ", failed.Select(e => e.Name + (e.Error != null ? " (" + e.Error + ")" : " (no value at its context)")));
            if (good.Count == 0)
                return $" NOT CERTIFIED: no figure evaluated at its context — {FailList()}. Fix the context and re-capture.";

            // P1-4: durable LineageTag per figure. P2-F: a context that REFERENCES A MEASURE cannot be proven
            // non-volatile (indirect volatility we cannot see through) — certify it but mark it StabilityProvable=false
            // so compare treats it as not-checkable rather than HELD.
            var (tags, measureNames) = await s.ReadAsync(m => (
                good.ToDictionary(e => e.Ref,
                    e => { try { return (ObjectRefs.Resolve(m, e.Ref) as TabularEditor.TOMWrapper.ILineageTagObject)?.LineageTag; } catch { return null; } },
                    StringComparer.Ordinal),
                (ISet<string>)m.AllMeasures.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            var ctx = await GatherCertifiedContextAsync(live, s);
            bool StabilityProvable(string[] fs) => !CertifiedStore.BareBracketRefs(fs).Any(measureNames.Contains);

            var certEntries = good.Select(e => new CertifiedEntry
            {
                Ref = e.Ref, Name = e.Name, LineageTag = tags.TryGetValue(e.Ref, out var tg) ? tg : null,
                GroupBy = groupBy, Filters = filters, Truncated = e.Truncated, StabilityProvable = StabilityProvable(filters),
                Cells = e.Rows.Select(kv => CertifiedStore.CellOf(kv.Key, kv.Value)).ToArray(),
            }).ToList();

            CertifiedUpsertResult res;
            try { res = await Task.Run(() => CertifiedStore.Upsert(file, label, s.Revision, ctx, certEntries)); }
            catch (Exception ex) { return $" NOT CERTIFIED: the figures could not be persisted ({ex.Message})."; }
            if (res.Refused != null)
                return " NOT CERTIFIED — " + res.Refused;   // immutability / context mismatch / tamper: loud, never a silent overwrite

            // P2-8: anchor the certification (label, contexts, refs, content hash) in the append-only audit trail.
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "capture_baseline", ObjectRef = label,
                Verdict = "certified",
                Summary = $"certified {res.Added} figure(s) under '{label}' (baseline holds {res.Baseline.Entries.Count})",
                Evidence = System.Text.Json.JsonSerializer.Serialize(new
                {
                    certifiedLabel = label, added = res.Added, total = res.Baseline.Entries.Count,
                    contentHash = res.Baseline.ContentHash, context = res.Baseline.Context,
                    figures = res.Baseline.Entries.Select(e => new { e.Ref, e.LineageTag, e.Filters }).ToArray(),
                }),
                BodyHash = res.Baseline.ContentHash,
            });

            var note = $" Certified {res.Added} figure(s) under '{label}' at their stated context (baseline holds {res.Baseline.Entries.Count}; hash {res.Baseline.ContentHash.Substring(0, 12)}…). A certification is IMMUTABLE — re-certify under a new label (e.g. \"{label} r2\"). compare_baseline(label:\"{label}\") re-checks them after any refresh or edit.";
            var notProvable = certEntries.Where(e => !e.StabilityProvable).Select(e => e.Name).ToList();
            if (notProvable.Count > 0)
                note += $" NOTE: {string.Join(", ", notProvable)} — the context references a measure, so its stability cannot be proven; it is certified but re-checked as NOT CHECKABLE, not HELD.";
            if (failed.Count > 0)
                note += $" NOT certified (excluded, fix and re-capture): {FailList()}.";
            return note;
        }

        /// <summary>The model + connection identity a certified baseline is bound to (P1-5). For a LOCAL source (Power
        /// BI Desktop) the server (localhost:port) and database (a per-session GUID) change every open, so they are
        /// left UNSTAMPED and identity rests on the model name + fingerprint — otherwise an honest re-open would flip
        /// every compare to NOT CHECKABLE (P2-G). Effective-user / RLS role is not observable here, so it is not claimed.</summary>
        private async Task<CertifiedContext> GatherCertifiedContextAsync(LiveConnection live, Session s)
        {
            var mi = await s.ReadAsync(m => (Name: m.Database?.Name ?? m.Name,
                Fp: KnowledgeStore.ComputeFingerprint(m).FingerprintKey, Culture: m.Culture));
            var isLocal = string.Equals(live?.Kind, "local", StringComparison.OrdinalIgnoreCase);
            return new CertifiedContext
            {
                Server = isLocal ? null : live?.DataSource,     // localhost:port is per-session on a local source
                Database = isLocal ? null : live?.Database,     // a per-session GUID on a local source — the fingerprint identifies it
                ModelName = mi.Name, Fingerprint = mi.Fp, Culture = mi.Culture,
            };
        }

        /// <summary>Re-evaluate a captured baseline on the CURRENT model and report exactly which numbers
        /// moved. A vanished measure is an IMPACT ("missing"), never a silent skip. Records the verdict
        /// (safe/impact) to the Verified Edits audit trail — a real compare is real evidence.</summary>
        public async Task<BaselineCompareResult> CompareBaselineAsync(string captureId, string label, string origin)
        {
            // CERTIFIED TOTALS: a `label` re-checks the PERSISTED certified baseline (month-end close) rather than a
            // session capture — "do the certified figures still hold?" It reuses the SAME diff engine, re-evaluating
            // EACH entry at ITS OWN stated context, so held/moved/missing/not-checkable mean exactly what they mean
            // for a session compare. This is the drift-detection surface; it names what moved, it cannot prevent it.
            if (!string.IsNullOrWhiteSpace(label))
                return await CompareCertifiedAsync(label.Trim(), origin);

            var s = _sessions.Require();

            BaselineState state;
            await _baselineGate.WaitAsync();
            try { state = _baselines.Get(captureId); }
            finally { _baselineGate.Release(); }
            if (state == null)
                return new BaselineCompareResult { Status = "not-found", CaptureId = captureId, Message = "No such capture (captures are session-held; run capture_baseline first). For a signed-off month-end baseline, pass label:." };

            var live = _live;
            if (live == null)
                return new BaselineCompareResult { Status = "no-connection", CaptureId = state.Id, Message = "compare_baseline needs the live model the baseline was captured from." };

            var diffs = new System.Collections.Generic.List<BaselineDiff>();
            foreach (var entry in state.Entries)
                diffs.Add(await DiffOneAsync(live, s, entry, state.GroupBy, state.Filters, 2000 + entry.Rows.Count));

            var moved = diffs.Count(d => d.Verdict == "moved");
            var missing = diffs.Count(d => d.Verdict == "missing");
            var unclean = diffs.Count(d => d.Verdict == "error" || d.Verdict == "not-comparable");
            var truncated = diffs.Any(d => d.Truncated);
            var result = new BaselineCompareResult
            {
                Status = "ok", CaptureId = state.Id, Root = state.Root,
                CapturedWhen = state.WhenUtc.ToString("o"), CapturedRevision = state.Revision, ComparedRevision = s.Revision,
                Safe = moved == 0 && missing == 0 && unclean == 0 && !truncated,
                MovedCount = moved, MissingCount = missing, Diffs = diffs.ToArray(),
                Message = moved == 0 && missing == 0
                    ? (unclean > 0 ? $"{unclean} measure(s) not comparable — NOT safe by default."
                       : truncated ? "nothing moved on the compared rows, but coverage was truncated — evidence, not proof."
                       : $"SAFE — {diffs.Count} downstream measure(s) unchanged across the captured grid.")
                    : $"IMPACT — {moved} measure(s) moved, {missing} missing, of {diffs.Count} baselined.",
            };
            // False-safe guard: this compare reads the LIVE model. A session edit made since capture is only
            // reflected once deployed — "unchanged" must never be misread as validating an undeployed edit.
            if (s.Revision > state.Revision)
                result.Message += $" NOTE: {s.Revision - state.Revision} session edit(s) since capture — the live model reflects them only if deployed (deploy_live); an undeployed local edit is NOT validated by this compare.";

            // Audit: a real compare is real evidence (Revision=0 — no backing mutation; the badge must not
            // weld to an unrelated row). Recorded for BOTH verdicts: "safe" is the claim someone ships on.
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "compare_baseline", ObjectRef = state.Root,
                Verdict = result.Safe ? "safe" : "impact", Summary = result.Message,
                Evidence = System.Text.Json.JsonSerializer.Serialize(new
                {
                    captureId = state.Id, capturedRevision = state.Revision, comparedRevision = s.Revision,
                    grid = new { groupBy = state.GroupBy, filters = state.Filters },
                    measures = diffs.Count, moved, missing, unclean, truncated,
                    diffs = diffs.Where(d => d.Verdict != "unchanged").Take(20).Select(d => new
                    {
                        d.Ref, d.Verdict, d.MismatchCount,
                        // top mismatch contexts persist so the evidence GRID survives across sessions (counts alone can't re-render it)
                        mismatchSamples = d.Mismatches.Take(8).Select(m => new { m.Context, m.ValueA, m.ValueB }).ToArray(),
                    }).ToArray(),
                }),
            });
            return result;
        }

        /// <summary>Re-evaluate ONE captured entry at ITS grid on the live model and diff it — the shared body behind
        /// both the session compare (one grid for all entries) and the certified compare (a stated context per
        /// entry). A capture-time error is not-comparable; a vanished measure is missing (an impact, never a skip);
        /// a live error is error. Reuses Baseline.Diff verbatim so "moved" means one thing everywhere.</summary>
        private async Task<BaselineDiff> DiffOneAsync(LiveConnection live, Session s, BaselineEntryState entry, string[] groupBy, string[] filters, int rowCap)
        {
            if (!string.IsNullOrEmpty(entry.Error))
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "not-comparable", Note = "capture-time evaluation failed: " + entry.Error };
            var binding = await s.ReadAsync(m => Baseline.ResolveMeasure(m, entry));
            if (binding.Error != null)
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "not-comparable", Note = binding.Error };
            var currentName = binding.Measure?.Name;
            if (currentName == null)
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "missing", RowsCompared = entry.Rows.Count, MismatchCount = entry.Rows.Count, Note = "the measure no longer resolves (deleted or renamed) — every certified number is gone" };
            try
            {
                var rs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(currentName), groupBy ?? Array.Empty<string>(), filters ?? Array.Empty<string>()), rowCap, 120);
                if (!string.IsNullOrEmpty(rs.Error))
                    return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "error", Note = "could not evaluate at the certified context: " + rs.Error };
                return Baseline.Diff(entry, Baseline.KeyRows(rs, (groupBy ?? Array.Empty<string>()).Length), rs.Truncated);
            }
            catch (Exception ex) { return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "error", Note = ex.Message }; }
        }

        // The float-noise tolerance HELD is judged within, echoed from the baseline's own stored value so the
        // record is self-describing (P2-D / P1-6): HELD = unchanged BEYOND rounding noise, not a business threshold.
        private static string ToleranceNoteFor(double tol) =>
            $"Numbers are compared within the certified float-noise tolerance ({tol.ToString("0.0e0", System.Globalization.CultureInfo.InvariantCulture)} relative); HELD means unchanged beyond rounding noise, not within a business threshold.";
        // RLS / effective-user is not captured (P2-G) — so a moved figure is not necessarily a refresh or an edit.
        private const string RlsNote = "Effective user / RLS role is not captured, so a figure that moved could also reflect a different security context, not only a refresh or an out-of-tool edit.";

        /// <summary>Do the certified figures for `label` still hold? Load the persisted certified baseline, VERIFY it
        /// has not been tampered with (content hash) and that the CURRENT model+context matches the one it was
        /// captured on, then re-evaluate each figure AT ITS STATED CONTEXT, resolving the measure BY ITS DURABLE
        /// LINEAGETAG so an impostor at the old name is caught. Reports per-figure held / moved (old→new) / missing /
        /// not-checkable. Detection, not prevention: it names what moved, it cannot stop a refresh or an out-of-tool edit.</summary>
        private async Task<BaselineCompareResult> CompareCertifiedAsync(string label, string origin)
        {
            // The store + live checks need no session (the file lives beside the model / in the workspace), so the
            // honest refusals reach the caller even offline. Require() only when we actually re-evaluate.
            var file = CertifiedFilePath();
            var cf = CertifiedStore.Load(file, out var corrupt);
            if (corrupt)
                return new BaselineCompareResult { Status = "error", Root = label, Message = "The certified-figures store (.semanticus/certified-baselines.json) is present but unreadable — the certified baseline cannot be checked. Repair or move that file, then re-capture the close's figures." };
            var bl = CertifiedStore.Find(cf, label);
            if (bl == null || bl.Entries.Count == 0)
                return new BaselineCompareResult { Status = "not-found", Root = label, Message = $"No certified figures under '{label}'. Capture the close's control totals and headline at their stated contexts with capture_baseline(label:\"{label}\") first — that is what a later compare checks against." };
            // P1-3: tamper evidence. A file edited after capture without recomputing the hash is a LOUD refusal.
            if (!CertifiedStore.HashMatches(bl))
                return new BaselineCompareResult { Status = "error", Root = label, CapturedWhen = bl.CapturedUtc, Message = $"The certified baseline '{label}' has been MODIFIED since it was captured (content hash mismatch) — refusing to report it as held or moved against a tampered record. Investigate .semanticus/certified-baselines.json (a prior capture is anchored in the Verified Edits audit trail)." };

            var live = _live;
            if (live == null)
                return new BaselineCompareResult { Status = "no-connection", Root = label, CapturedWhen = bl.CapturedUtc, Message = $"compare_baseline(label:\"{label}\") needs a live model (open_live / open_local) to re-evaluate the certified figures at their contexts." };

            var s = _sessions.Require();

            // P1-5: the certified figures are bound to the model+context they were captured on. If the current
            // connection is a DIFFERENT model/context, re-checking would compare unlike things — not-checkable, never HELD.
            var curCtx = await GatherCertifiedContextAsync(live, s);
            var ctxDiff = bl.Context?.DiffFrom(curCtx);
            if (ctxDiff != null)
            {
                var ncMsg = $"NOT CHECKABLE — {ctxDiff}. The certified figures for '{label}' were captured on a different model/context; re-checking them on this connection would compare unlike things. Reconnect to the model they were certified on. " + ToleranceNoteFor(bl.Tolerance);
                await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                {
                    SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "compare_baseline", ObjectRef = label,
                    Verdict = "impact", Summary = ncMsg,
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new { certifiedLabel = label, notCheckable = "context-mismatch", detail = ctxDiff, capturedContext = bl.Context, currentContext = curCtx }),
                });
                return new BaselineCompareResult { Status = "ok", Root = label, CapturedWhen = bl.CapturedUtc, CapturedRevision = bl.Revision, ComparedRevision = s.Revision, Safe = false, Message = ncMsg };
            }

            // A model SHAPE change since certification is EXPECTED after edits and does NOT block the check (P2-1) —
            // it is surfaced as an informational note, while each figure is still checked by its own identity.
            var shapeNote = bl.Context?.ShapeChangeNote(curCtx);

            var diffs = new System.Collections.Generic.List<BaselineDiff>();
            foreach (var entry in bl.Entries)
                diffs.Add(await CertifiedDiffOneAsync(live, s, entry));

            var moved = diffs.Count(d => d.Verdict == "moved");
            var missing = diffs.Count(d => d.Verdict == "missing");
            var unclean = diffs.Count(d => d.Verdict == "error" || d.Verdict == "not-comparable");
            var truncated = diffs.Any(d => d.Truncated);
            var held = diffs.Count(d => d.Verdict == "unchanged");
            var result = new BaselineCompareResult
            {
                Status = "ok", CaptureId = null, Root = label, CapturedWhen = bl.CapturedUtc,
                CapturedRevision = bl.Revision, ComparedRevision = s.Revision,
                Safe = moved == 0 && missing == 0 && unclean == 0 && !truncated,
                MovedCount = moved, MissingCount = missing, Diffs = diffs.ToArray(),
                Message = (moved == 0 && missing == 0
                    ? (unclean > 0 ? $"NOT CHECKABLE — {unclean} of {diffs.Count} certified figure(s) for '{label}' could not be re-checked (identity changed/ambiguous, not durably identified, stability not provable, or errored); {held} held. Not safe by default — see the per-figure notes."
                       : truncated ? $"the certified figures for '{label}' held on the compared rows, but coverage was truncated — evidence, not proof."
                       : $"HELD — all {held} certified figure(s) for '{label}' still match what was signed off (captured {bl.CapturedUtc}).")
                    : $"DRIFT — {moved} certified figure(s) MOVED and {missing} went missing, {held} held, of {diffs.Count} signed off for '{label}'. Investigate each moved number (old→new is in the diff); a refresh, a different security role, or an out-of-tool edit are the usual causes. Detection, not prevention.")
                    + " " + ToleranceNoteFor(bl.Tolerance) + " " + RlsNote + (shapeNote != null ? " " + shapeNote : ""),
            };

            // Audit (P2-8): reconstructable — records held/moved/missing/unclean COUNTS plus the moved/missing detail.
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "compare_baseline", ObjectRef = label,
                Verdict = result.Safe ? "safe" : "impact", Summary = result.Message,
                Evidence = System.Text.Json.JsonSerializer.Serialize(new
                {
                    certifiedLabel = label, contentHash = bl.ContentHash, capturedUtc = bl.CapturedUtc,
                    capturedRevision = bl.Revision, comparedRevision = s.Revision,
                    figures = diffs.Count, held, moved, missing, unclean, truncated,
                    diffs = diffs.Where(d => d.Verdict != "unchanged").Take(20).Select(d => new
                    {
                        d.Ref, d.Verdict, d.MismatchCount, d.Note,
                        mismatchSamples = d.Mismatches.Take(8).Select(m => new { m.Context, m.ValueA, m.ValueB }).ToArray(),
                    }).ToArray(),
                }),
            });
            return result;
        }

        /// <summary>Re-evaluate ONE certified figure. First: a figure whose stability could not be proven (its context
        /// references a measure, P2-F) is not-checkable outright. Then resolve the measure by its DURABLE, UNIQUE
        /// LineageTag whose NAME still matches (P1-B) — a clone/impostor/rename is not-checkable, never HELD; an
        /// untagged legacy figure cannot certify identity and is not-checkable. Only a uniquely-tag-and-name-resolved
        /// figure is evaluated and compared.</summary>
        internal async Task<BaselineDiff> CertifiedDiffOneAsync(LiveConnection live, Session s, CertifiedEntry entry)
        {
            if (!entry.StabilityProvable)
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "not-comparable", Note = "stability not provable — the certified context references a measure, whose own value can move, so this figure cannot be proven HELD" };
            var (name, status, idNote) = await s.ReadAsync(m => ResolveCertifiedIdentity(m, entry));
            if (status == "missing")
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "missing", RowsCompared = entry.Cells.Length, MismatchCount = entry.Cells.Length, Note = idNote };
            if (status != "ok")   // identity-changed | ambiguous | not-durable → never HELD
                return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "not-comparable", Note = idNote };
            var es = CertifiedStore.ToEntryState(entry);
            try
            {
                var rs = await live.ExecuteAsync(DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(name), entry.GroupBy ?? Array.Empty<string>(), entry.Filters ?? Array.Empty<string>()), 2000 + es.Rows.Count, 120);
                if (!string.IsNullOrEmpty(rs.Error))
                    return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "error", Note = "could not evaluate at the certified context: " + rs.Error };
                return Baseline.Diff(es, Baseline.KeyRows(rs, (entry.GroupBy ?? Array.Empty<string>()).Length), rs.Truncated);
            }
            catch (Exception ex) { return new BaselineDiff { Ref = entry.Ref, Name = entry.Name, Verdict = "error", Note = ex.Message }; }
        }

        /// <summary>Resolve the certified figure's measure on the CURRENT model by DURABLE identity (P1-B). A LineageTag
        /// is unique PER-COLLECTION and is COPIED by clone/duplicate, so a bare "first tag match" is unsafe. Requires
        /// EXACTLY ONE measure with the captured tag AND that its name still matches: zero ⇒ identity-changed (impostor
        /// at the old name) or missing; more than one ⇒ ambiguous (a clone copied the tag); one but renamed ⇒
        /// identity-changed. Untagged (legacy/CL&lt;1540) ⇒ not-durable (identity cannot be certified). Runs inside a read.</summary>
        internal static (string name, string status, string note) ResolveCertifiedIdentity(Model m, CertifiedEntry entry)
        {
            Measure ByName() { try { return ObjectRefs.Resolve(m, entry.Ref) as Measure; } catch { return null; } }
            if (!string.IsNullOrEmpty(entry.LineageTag))
            {
                var matches = m.AllMeasures.Where(mm => string.Equals((mm as TabularEditor.TOMWrapper.ILineageTagObject)?.LineageTag, entry.LineageTag, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0)
                    return ByName() != null
                        ? (null, "identity-changed", $"the certified measure '{entry.Name}' is gone; a measure at that name now carries a DIFFERENT LineageTag — an impostor at the old name is NOT the certified figure")
                        : (null, "missing", "the certified measure no longer exists (its durable LineageTag resolves to nothing)");
                if (matches.Count > 1)
                    return (null, "ambiguous", $"the certified LineageTag now resolves to {matches.Count} measures ({string.Join(", ", matches.Take(3).Select(x => x.Name))}) — a clone copied the tag, so identity is ambiguous and cannot be proven");
                var only = matches[0];
                return string.Equals(only.Name, entry.Name, StringComparison.Ordinal)
                    ? (only.Name, "ok", null)
                    : (null, "identity-changed", $"the certified LineageTag now belongs to a measure named '{only.Name}', not the certified '{entry.Name}' — identity cannot be proven the same");
            }
            return ByName() != null
                ? (null, "not-durable", "no LineageTag was captured (this model predates lineage tags / CL<1540) — the certified figure's identity cannot be durably proven, so it is not-checkable")
                : (null, "missing", "the measure no longer resolves (deleted or renamed)");
        }

        // BOTH intent tickets are minted HERE, at the public operation entry — before the lifecycle gate, before
        // the build — so anything the user issues WHILE this open runs is a newer intent: a newer connect/
        // disconnect wins the LIVE race, a newer open/create wins the SESSION race (this open then self-aborts).
        // Always minted as ONE ATOMIC PAIR (NewSessionSwapIntents — pair ordering is the c2 soundness premise).
        public async Task<OpenResult> OpenAsync(string path)
        {
            var (liveTicket, sessionTicket) = NewSessionSwapIntents();
            return (await SwapSessionCoreAsync(() => _sessions.BuildOpenAsync(path), path, liveTicket, sessionTicket)).Result;
        }

        public async Task<OpenResult> CreateModelAsync(string name, int compatibilityLevel)
        {
            var (liveTicket, sessionTicket) = NewSessionSwapIntents();   // one atomic pair at PUBLIC entry (see OpenAsync)
            // Default to a Direct-Lake-capable, modern compatibility level (the blank ctor defaults to 1200).
            var cl = compatibilityLevel <= 0 ? 1604 : compatibilityLevel;
            return (await SwapSessionCoreAsync(() => _sessions.BuildCreateAsync(name, cl), releaseUnless: null, liveTicket, sessionTicket)).Result;
        }

        // Test-only: invoked between INVALIDATE (d) and COMMIT (e) — the last observable instant of the swap —
        // so the ordering pin can assert "old session still current, stores already cleared, _live dropped"
        // deterministically. null in production: no fallible step exists between (d) and (e). Consumed ONE-SHOT
        // and exception-swallowed at the invocation site: this seam is compiled into production, so a stale or
        // throwing hook must never be able to gut the old session (stores cleared, _live dropped, replacement
        // disposed) on every later swap — arming it affects exactly one swap, and failing it affects none.
        internal Action BetweenInvalidateAndCommitForTest;

        // THE SESSION-SWAP ORDER (session/connection lifecycle audit rounds 1–4 — do not reorder; each step's
        // placement closes a specific race). Runs under _lifecycleGate so two swaps can't interleave (scoped fix —
        // full per-op generation-checking is the separate SessionContext refactor). BOTH intent tickets arrive
        // from the PUBLIC operation entry and are never re-minted here (intent order = the order ops were issued).
        //   a. BUILD the replacement completely off to the side (new dispatcher + model + Session; SessionManager.
        //      Build*Async). Failure disposes only the new dispatcher — the live session, its unsaved work, the
        //      stores and _live are all untouched.
        //   b. READ the OpenResult from the NEW session (its own dispatcher; it need not be Current).
        //   c. CREATE + REGISTER the ambient observer on the still-UNPUBLISHED session (SessionManager.
        //      AttachObserver). This is the LAST fallible step: a failure anywhere in a–c disposes the replacement
        //      exactly once (Session.Dispose also detaches the just-registered observer) and leaves the old
        //      session Current WITH its stores AND its live connection fully intact — a failed open destroys
        //      nothing.
        //   c2. THE POINT OF NO RETURN — the session-intent check: if a NEWER session-replacing op was issued
        //      while this one built (sessionTicket is no longer the newest _sessionIntent), THIS op is the stale
        //      one — dispose the never-published replacement and throw an honest "superseded" error. Without this
        //      check the stale open would proceed: its _live drop at (d) would LOSE to the newer op's live ticket
        //      (correct for a newer CONNECT, catastrophic here) and it would then commit its model with the
        //      PREVIOUS model's connection still attached — run_dax/run_dmv/preview_table read only _live and
        //      would silently answer from the wrong model. Deliberately checked against the SESSION counter, not
        //      the live one: a newer connect must survive an open (the F1 pin), a newer open must supersede it —
        //      two counters, two meanings. NOTE the lifecycle gate is a SemaphoreSlim with NO FIFO promise: the
        //      newer-ticketed op may acquire the gate FIRST (it sees its own ticket as newest and commits; this
        //      older op later aborts here) or SECOND (this op would commit only if it still held the newest
        //      ticket — impossible once the newer op minted — so it aborts here too). Either acquisition order
        //      ends with the newest intent's model current and never a committed stale open.
        //   d. INVALIDATE model-scoped state while the OLD session is still Current: drop _live under the entry
        //      ticket (a connect/disconnect issued after this op began is a NEWER intent — the drop loses to it,
        //      as will the open's rebind), then clear the stores (ResetModelScopedStateOnSwapAsync — one
        //      documented gate order). A concurrent reader mid-swap sees the OLD session with old-or-EMPTY stores
        //      (benign reads during a user-initiated open — the documented lesser evil), NEVER the NEW session
        //      paired with the OLD model's plan/spec/baseline/connection.
        //   e. COMMIT (SessionManager.Commit): a no-throw method containing ONLY the volatile Current flip + the
        //      caught old-session disposal. No fallible step exists between d and e (invalidate-before-publish
        //      holds), and nothing after e may throw: the snapshot-dir release is wrapped, the library rebroadcast
        //      swallows by contract, and the result returned was pre-built in step b.
        // Returns the published session so the open_live/open_local rebind binds LiveOrigin/auth to the session
        // the swap produced (never a re-read of Current) and can refuse to attach if it is no longer Current.
        private async Task<(OpenResult Result, Session Session)> SwapSessionCoreAsync(
            Func<Task<Session>> build, string releaseUnless, long liveTicket, long sessionTicket)
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                var next = await build();                                   // a. build (fails clean)
                OpenResult result;
                try
                {
                    result = await next.ReadAsync(m => new OpenResult      // b. result from the new session
                    {
                        SessionId = next.Id,
                        Revision = next.Revision,
                        ModelName = m.Database?.Name ?? m.Name,
                        Tables = m.Tables.Count,
                        Measures = m.AllMeasures.Count(),
                        Source = next.SourcePath                            // null for create — unsaved until save_model(path)
                    });
                    _sessions.AttachObserver(next);                         // c. LAST fallible step — before any destruction
                    if (System.Threading.Interlocked.Read(ref _sessionIntent) != sessionTicket)   // c2. point of no return
                        throw new InvalidOperationException(
                            "This open was superseded by a newer open or create issued while it was loading; the current model is unchanged. Re-issue this open if you still want that model.");
                    var (_, displaced) = SwapLive(null, liveTicket);        // d. drop the old model's query connection...
                    SafeDispose(displaced);
                    await ResetModelScopedStateOnSwapAsync();               //    ...and clear model-scoped stores
                    // Test-only observation point (null in prod) — consumed ONE-SHOT and swallowed: a seam that is
                    // compiled into production must never be able to break the commit path or re-fire on a later swap.
                    var hook = System.Threading.Interlocked.Exchange(ref BetweenInvalidateAndCommitForTest, null);
                    if (hook != null) { try { hook(); } catch { /* a test seam must never break the commit path */ } }
                }
                catch
                {
                    next.Dispose();                                         // exactly once — nothing below can throw
                    throw;
                }
                var published = _sessions.Commit(next);                     // e. volatile flip + caught old disposal ONLY
                // Post-commit: nothing below may throw (a committed swap must never report as a failed open).
                try { ReleaseSnapshotDir(unless: releaseUnless); }
                catch (Exception ex) { try { Console.Error.WriteLine("[open] releasing the previous snapshot dir failed (the swap is committed): " + ex.Message); } catch { } }
                await SafeRebroadcastWorkflowLibraryAsync();                // model.* facts changed — re-curate the menu (§10.6)
                return (result, published);
            }
            finally { _lifecycleGate.Release(); }
        }

        public async Task<OpenResult> OpenLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool forceReauth = false)
        {
            // Both intent tickets minted at PUBLIC operation entry — before auth, before the snapshot export —
            // as ONE ATOMIC PAIR (pair ordering is the c2 soundness premise). The live ticket is reused for BOTH
            // the swap's drop and the post-open rebind (a disconnect/connect the user issues while this slow open
            // runs is a newer intent: the rebind must lose to it, never override it); the session ticket makes a
            // newer open/create issued meanwhile supersede THIS open.
            var (liveTicket, sessionTicket) = NewSessionSwapIntents();
            // Read the deployed model's metadata from the live endpoint (TOM Server + the token via
            // Server.AccessToken) into a local .bim snapshot, then open it through the proven file path.
            // Edits are in-memory + undoable; the only persistence is Save() to disk — nothing is pushed
            // back to the server (the engine has no deploy), so loading + editing never mutates the model.
            // Acquire ONE Entra token (the netcore AS client can't do its own AAD) and inject it into both the
            // AMO snapshot (Server.AccessToken) and the ADOMD live connection (Password=) — one auth, both halves.
            // interactive pops a browser via Azure.Identity; serviceprincipal/azcli/devicecode/token as named.
            // Build the credential HERE and reuse the SAME instance for the session's later live ops (deploy /
            // refresh) — Azure.Identity keeps its token cache in this instance, so interactive prompts once, then
            // renews silently (the fix for "refresh re-prompts"). token mode has no credential (caller-supplied).
            var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
            // Lowercase the tenant too (Entra tenant ids/domains are case-insensitive), so the same identity always
            // lands on the same cache key — a differently-cased re-supply must still hit reuse, not re-prompt.
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim().ToLowerInvariant();
            var authKey = mode + "|" + (tenant ?? "");
            // BuildCredentialAsync (vs the sync BuildCredential) also persists the interactive sign-in to the
            // encrypted on-disk cache the first time, so later reconnects/restarts acquire silently (no re-prompt).
            // forceReauth ("use a different account") ignores the saved record, shows the account picker, and
            // overwrites the saved identity — the recovery path when the one cache slot holds the wrong account.
            Azure.Core.TokenCredential cred = mode == "token" ? null : await EntraToken.BuildCredentialAsync(mode, tenant, System.Threading.CancellationToken.None, forceReauth);
            var tok = cred != null
                ? await EntraToken.GetTokenAsync(cred, System.Threading.CancellationToken.None)
                : await EntraToken.AcquireFullAsync(mode, rawToken, System.Threading.CancellationToken.None, tenant);
            var snap = await LiveModelExport.ToBimAsync(endpoint, database, tok.Token, tok.ExpiresOn);
            var liveCs = LiveConnection.XmlaConnectionString(endpoint, snap.DatabaseName, tok.Token);
            var (open, session) = await OpenSnapshotAsync(snap, liveTicket, sessionTicket);
            open.Source = $"xmla:{endpoint} -> {snap.DatabaseName} (local snapshot: {snap.BimPath})";
            // Bind the session to its live source (non-secret coordinates only) so deploy_live can push back
            // "to source" without re-supplying endpoint/database. Record the dataset actually resolved at open
            // (snap.DatabaseName), not the caller's possibly-empty `database` arg. Deliberately the SESSION the
            // swap returned — never a re-read of Current, which another door may have replaced by now.
            session.LiveOrigin = new LiveOrigin(endpoint, snap.DatabaseName, tenantId, authMode);
            // Record the dataset actually resolved, not the caller's possibly-empty argument — the registry is keyed on
            // it, so an empty `database` must not mint a second record for the same model on the next open.
            RememberConnection("xmla", endpoint, snap.DatabaseName, snap.DatabaseName, tenantId, authMode);
            // Seed the session's live-auth cache with the credential + token we just used, so the first deploy /
            // refresh reuses them with no second prompt and no re-acquisition.
            session.CacheLiveToken(authKey, tok);
            if (cred != null) session.SeedLiveCredential(authKey, cred);
            // UNIFIED OPEN: the same model the tree now edits is also the one Studio queries — bind the live query
            // connection too, REUSING the one token from above (so interactive prompts the browser exactly once).
            // Best-effort: the editable session is the primary outcome; if the ADOMD attach fails, leave _live null
            // (Studio offers to attach) rather than failing the whole open. Rides the OPEN's intent ticket + session.
            open.LiveConnected = await TryBindLiveAsync("xmla", endpoint, liveCs, liveTicket, session);
            await SafeRebroadcastWorkflowLibraryAsync();   // model + connection.* now exist — re-curate the menu (§10.6)
            return open;
        }

        public async Task<OpenResult> OpenLocalAsync(string dataSource, string database)
        {
            var (liveTicket, sessionTicket) = NewSessionSwapIntents();   // one atomic pair at PUBLIC entry (see OpenLiveAsync)
            // UNIFIED OPEN for a running Power BI Desktop (local Analysis Services): make the SAME instance both
            // editable in the tree (snapshot its metadata into the session) AND queryable in Studio (bind _live).
            // Localhost AS uses integrated Windows auth — no Entra token, no auth prompt (the reliable path).
            if (string.IsNullOrWhiteSpace(dataSource))
            {
                var inst = LocalDiscovery.List().FirstOrDefault()
                    ?? throw new InvalidOperationException("No local Power BI Desktop instance found. Open a .pbix, or pass a Data Source like 'localhost:51234'.");
                dataSource = inst.DataSource;
            }
            var snap = await LiveModelExport.ToBimLocalAsync(dataSource, database);
            var (open, session) = await OpenSnapshotAsync(snap, liveTicket, sessionTicket);
            open.Source = $"local:{dataSource} -> {snap.DatabaseName} (local snapshot: {snap.BimPath})";
            // Bind the origin (local coordinates) so "Save to live model" can push edits back to THIS instance.
            // deploy_live recognises a loopback endpoint and writes with integrated Windows auth (no token), so this
            // no longer risks a confusing service-principal-against-localhost attempt. The SESSION the swap
            // returned — never a re-read of Current (another door may have replaced it).
            session.LiveOrigin = new LiveOrigin(dataSource, snap.DatabaseName, null);
            RememberConnection("localDesktop", dataSource, snap.DatabaseName, snap.DatabaseName);
            open.LiveConnected = await TryBindLiveAsync("local", dataSource, LiveConnection.LocalConnectionString(dataSource, snap.DatabaseName), liveTicket, session);
            await SafeRebroadcastWorkflowLibraryAsync();   // model + connection.* now exist — re-curate the menu (§10.6)
            return open;
        }

        // Open a just-written live snapshot; if the open fails, delete the orphaned temp snapshot dir before
        // rethrowing (the snapshot lives in its own guid dir under LiveModelExport.TempRoot). BOTH intent
        // tickets come from the CALLER's public operation entry — never minted here (the rebind must stay part
        // of the SAME intent the user expressed by starting the open, and a newer open must supersede this one).
        private async Task<(OpenResult Result, Session Session)> OpenSnapshotAsync(LiveModelExport.Snapshot snap, long liveTicket, long sessionTicket)
        {
            try
            {
                // The swap core releases the PREVIOUS snapshot dir (this one is not tracked yet), then we adopt this one.
                var r = await SwapSessionCoreAsync(() => _sessions.BuildOpenAsync(snap.BimPath), releaseUnless: snap.BimPath, liveTicket, sessionTicket);
                TrackSnapshotDir(snap.BimPath);
                return r;
            }
            catch
            {
                try { var dir = Path.GetDirectoryName(snap.BimPath); if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort cleanup */ }
                throw;
            }
        }

        // Attach the live query connection for a just-opened model. Best-effort: a failure here must not fail the
        // open (the editable session already loaded), so swallow and report false — Studio then offers to attach.
        // Open the new connection FIRST, then publish it and dispose the old one — so a failure leaves _live intact
        // and there's no transient window where a half-built connection is visible to a concurrent reader.
        // The publish is gated twice: on the OPEN's intent ticket (a connect/disconnect issued after this open
        // started is a newer intent — the rebind must lose to it, never displace it) AND on the session still being
        // Current (a model swapped in behind us must not get the previous model's endpoint attached).
        private async Task<bool> TryBindLiveAsync(string kind, string dataSource, string connectionString, long ticket, Session forSession)
        {
            LiveConnection conn;
            try { conn = await LiveConnection.OpenAsync(kind, dataSource, connectionString); }
            catch { return false; }
            var (won, displaced) = SwapLive(conn, ticket, () => ReferenceEquals(_sessions.Current, forSession));
            SafeDispose(displaced);   // best-effort contract: a loser/displaced dispose failure must not throw post-commit
            return won;
        }

        // Renew tokens never within 5 min of expiry, so a multi-minute live write (deploy/refresh) can't have its
        // token lapse mid-operation; also forces re-acquisition (silent, via the cached credential) just before then.
        private static readonly TimeSpan TokenSkew = TimeSpan.FromMinutes(5);

        /// <summary>Acquire an Entra token for a live op, REUSING the session's cached credential/token so a
        /// session that already authenticated (open_live) doesn't re-prompt on deploy/refresh. Fast-path returns a
        /// still-valid cached token; otherwise it reuses the one credential instance (interactive renews silently
        /// via its refresh token). Honours an explicit auth mode (e.g. the MCP agent forcing serviceprincipal):
        /// a different identity simply builds + caches its own credential.</summary>
        private async Task<Azure.Core.AccessToken> AcquireLiveTokenAsync(Session s, string authMode, string rawToken, string tenantId, System.Threading.CancellationToken ct)
        {
            var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim().ToLowerInvariant();   // case-insensitive; match the open-time seed key
            var key = mode + "|" + (tenant ?? "");

            // An explicitly supplied raw token always wins (the caller wants THAT token) — use and cache it.
            if (mode == "token" && !string.IsNullOrWhiteSpace(rawToken))
            {
                var t = await EntraToken.AcquireFullAsync(mode, rawToken, ct, tenant);
                s.CacheLiveToken(key, t);
                return t;
            }
            // Fast path: a still-valid token for the same identity — no network, no prompt.
            var cached = s.TryReuseLiveToken(key, TokenSkew);
            if (cached.Token != null) return cached;
            // token mode with no fresh token and no valid cache: there's no credential to renew from — surface the
            // real "auth mode 'token' requires a raw access token" error rather than silently doing something else.
            if (mode == "token") return await EntraToken.AcquireFullAsync(mode, rawToken, ct, tenant);
            // Reuse ONE credential per identity (built at open, or lazily here) → interactive renews silently.
            var cred = s.GetOrBuildLiveCredential(key, () => EntraToken.BuildCredential(mode, tenant));
            var tok = await EntraToken.GetTokenAsync(cred, ct);
            s.CacheLiveToken(key, tok);
            return tok;
        }

        // #141: is an agent-authored override of a RED deploy gate refused? A gate that RAN and returned RED
        // (gate != null && !gate.Pass) is HUMAN-only to clear (Kane's ruling). A scanner FAILURE (gate == null) is NOT
        // this gate — it proceeds+records elsewhere; a PASSING gate needs no override. Non-human origin fails closed
        // (only an exact "human" is the authority — see AgentPolicyGuard.IsHuman). Pure + offline-unit-testable.
        internal static bool IsAgentRedOverrideRefused(DeployGate gate, string origin) =>
            gate != null && !gate.Pass && !AgentPolicyGuard.IsHuman(origin);

        public async Task<DeployReport> DeployLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human", string overrideReason = null)
        {
            // Push the OPEN SESSION's metadata back to the live model (metadata-only, LineageTag-matched). We
            // serialize the edited session to a temp .bim WITHOUT resetting the undo checkpoint (deploying is not
            // "saving to file"), then sync it to the live model via Model.SaveChanges. Dry-run unless commit=true.
            var s = _sessions.Require();
            // Deploy-to-source: when no endpoint is given, push back to the live model this session was opened
            // from (open_live/open_local bound the non-secret coordinates). All-or-nothing — an explicit endpoint
            // uses the explicit args verbatim, so a new endpoint is never silently paired with the bound database.
            // authMode/tenant fall back to the bound values: the WRITE reuses the SAME identity the model was opened
            // with (no re-prompt — like Tabular Editor), unless the caller passes an explicit authMode (the MCP door).
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                var src = s.LiveOrigin ?? throw new InvalidOperationException(
                    "deploy_live: no endpoint given and this session is not live-bound. Open the model with open_live first, or pass an endpoint + database explicitly.");
                endpoint = src.Endpoint;
                database = src.Database;
                if (string.IsNullOrWhiteSpace(tenantId)) tenantId = src.TenantId;
                // Reuse the SAME auth the model was opened with when the caller gave none (the Save-to-Live UI passes
                // null) — no re-prompt, like Tabular Editor: the token comes from the session's cached credential
                // (see AcquireLiveTokenAsync), which renews silently. An explicit authMode (e.g. the MCP agent
                // forcing serviceprincipal) is still honoured.
                if (string.IsNullOrWhiteSpace(authMode)) authMode = src.AuthMode;
            }
            // An explicit endpoint requires an explicit dataset — deploying to an inferred "first dataset" is
            // riskier than reading one (open_live can guess; a WRITE must not), so fail clearly instead of letting
            // FindByName surface a bare "Database '' not found". The deploy-to-source branch always set a concrete
            // database above, so this only guards the explicit-endpoint call. (Runs before any auth/network.)
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException(
                    "deploy_live: a database (dataset) name is required when you pass an explicit endpoint (only deploy-to-source with an empty endpoint can infer it).");
            // ---- Agent-permissions gate (the deploy_live governance hole, now closed). deploy_stage forbade an agent
            // promoting to prod; deploy_live never did — an agent could commit an irreversible prod overwrite and clear
            // a RED readiness gate with its own overrideReason. The connection registry now gives this endpoint the
            // label the guard needs. Human deploys are never gated here; runs before auth/network (fail fast).
            if (commit)
            {
                // intentBasis = the op name: a deploy_live grant cannot be spent on apply_model_diff (or any sibling
                // DeployLive op) against the same target. The finer refinement — binding the session fingerprint so an
                // approve-then-edit-then-push re-asks — waits until the deploy pipeline exposes a cheap revision.
                var refusal = GuardAgent(AgentCapability.DeployLive, endpoint, database, origin, isCommit: true,
                    summary: $"deploy the open model to {database} on {endpoint}", intentBasis: "deploy_live");
                if (refusal != null) throw new InvalidOperationException("deploy_live: " + refusal);
            }
            // ---- Accountable checkpoint (Verified Edits). A live COMMIT runs the deploy gate (readiness hard-
            // gates + blocking BPA errors) — this was THE "enforcement is theater" hole: deploy_live used to bypass
            // the gate entirely. A RED gate PAUSES the deploy with the reasons; shipping anyway takes an explicit
            // overrideReason — never a hard wall (Kane's call 2026-07-01) — and the override is appended to the
            // model's append-only audit chain BEFORE the session is serialized below, so the record travels inside
            // the very artifact it authorized. Dry-runs are never gated; runs before any auth/network (fail fast).
            DeployGate gate = null; string gateScanError = null;
            if (commit)
            {
                // The scan failing is NOT a pass — but hard-failing every deploy on a scanner bug would be a
                // wall. Middle path: proceed, and carry the scrubbed scan error into the deploy's audit record
                // so a gate-less commit is visible, never silent.
                try { gate = await DeployGateAsync(null); } catch (Exception ex) { gateScanError = ex.Message; }
                if (gate != null && !gate.Pass)
                {
                    // #141 (Kane's ruling): a gate that RAN and returned RED is HUMAN-only to clear. Even inside a
                    // granted deploy window an agent-authored overrideReason is refused — the grant approved a PLAN
                    // ("push these changes"), never "ship even if the gate turns red". Checked BEFORE the missing-reason
                    // branch so an agent gets ONE clear teaching refusal, not "pass a reason" then "reason refused". A
                    // scanner FAILURE (gate == null) never enters this block — it still proceeds+records below, unchanged.
                    if (IsAgentRedOverrideRefused(gate, origin))
                        throw new InvalidOperationException(
                            "deploy_live: the deploy gate is RED (" + string.Join("; ", gate.Blockers)
                            + ") and clearing a red readiness gate is HUMAN-only — an agent cannot override it. The permission "
                            + "to deploy does not authorize clearing a failed readiness gate. Fix the blockers (apply_safe_fixes "
                            + "/ apply_fix, then re-run ai_readiness_scan), or ask the user to run deploy_live with overrideReason themselves.");
                    if (string.IsNullOrWhiteSpace(overrideReason))
                        throw new InvalidOperationException(
                            "deploy_live: blocked by the deploy gate — " + string.Join("; ", gate.Blockers)
                            + ". Fix the blockers, or pass overrideReason to ship anyway (the override is recorded in the model's audit trail).");
                    await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                    {
                        SessionId = s.Id, Revision = 0, Origin = origin, Op = "deploy_live",   // 0: a deploy is not a model mutation
                        Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                        Summary = $"gate RED ({string.Join("; ", gate.Blockers)}) — override accepted to deploy to {endpoint}/{database}",
                        Evidence = System.Text.Json.JsonSerializer.Serialize(new { gate.Grade, gate.BpaViolations, gate.BpaBlocking, gate.Blockers }),
                    });
                }
            }
            // A LOCAL instance (Power BI Desktop, loopback endpoint) deploys with integrated Windows auth — no
            // token, no secret. A cloud XMLA endpoint needs a bearer token: acquire it FIRST (fail fast — before
            // writing any temp file). A service principal is the reliable write principal for Fabric.
            var local = LiveDeploy.IsLocalEndpoint(endpoint);
            var tok = local
                ? default
                : await AcquireLiveTokenAsync(s, authMode, rawToken, tenantId, System.Threading.CancellationToken.None);
            var bim = Path.Combine(Path.GetTempPath(), "semanticus-deploy", Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            Directory.CreateDirectory(Path.GetDirectoryName(bim));
            try
            {
                // Serialize the edited session WITHOUT resetting the undo checkpoint (deploying is not "saving to file").
                await s.Dispatcher.RunAsync(() =>
                {
                    s.Save(bim, SaveFormat.ModelSchemaOnly, SerializeOptions.Default, resetCheckpoint: false);
                    return true;
                });
                var rep = await Task.Run(() => LiveDeploy.SyncSessionToLive(bim, endpoint, database, tok.Token, tok.ExpiresOn, commit));
                // Outcome record (local only — the NEXT deploy carries it): a committed ship is an audit event
                // whether or not the gate was red. Written after the push so a failed sync never logs "deployed".
                // Caught: the deploy SUCCEEDED — throwing here (e.g. the session was replaced mid-push and its
                // dispatcher is gone) would misreport a completed live write as a failure; surface it on the
                // report instead.
                if (rep != null && rep.Committed)
                    try
                    {
                        await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                        {
                            SessionId = s.Id, Revision = 0, Origin = origin, Op = "deploy_live",   // 0: a deploy is not a model mutation
                            Verdict = "deployed",
                            Summary = $"deployed to {endpoint}/{database} — {rep.TotalChanges} change(s); gate {(gate == null ? "unavailable" : gate.Pass ? "pass" : "RED (overridden)")}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { endpoint, database, rep.TotalChanges, gatePass = gate?.Pass, blockers = gate?.Blockers, gateScanError }),
                        });
                    }
                    catch (Exception ex) { rep.Changes = rep.Changes.Append("audit: deployed, but the outcome record could not be written — " + ex.Message).ToArray(); }
                return rep;
            }
            finally { try { File.Delete(bim); } catch { /* temp */ } }
        }

        // ---- Partition refresh (process) — the data counterpart of deploy_live. Dry-run by default; a live write on commit.

        // The catalog of refresh ("process") options with plain-language explanations. PartitionLevel=false means the
        // type only makes sense at the table/model level and is refused for a single partition. Full is the default.
        private static readonly RefreshTypeInfo[] RefreshTypes = new[]
        {
            new RefreshTypeInfo { Name = "Full", PartitionLevel = true, Recommended = true,
                Explanation = "Reload the partition's data from source and recalculate everything that depends on it. The complete, safe option — and the default for a single partition." },
            new RefreshTypeInfo { Name = "DataOnly", PartitionLevel = true,
                Explanation = "Reload the partition's data from source but do NOT recalculate — calculated columns, relationships and hierarchies are left stale until a Calculate. Faster; usually paired with a Calculate afterward." },
            new RefreshTypeInfo { Name = "Calculate", PartitionLevel = true,
                Explanation = "Recalculate calculated columns/tables, relationships and hierarchies where needed, WITHOUT reloading data. Run after a DataOnly refresh or a formula change. Normally issued at table/model level." },
            new RefreshTypeInfo { Name = "ClearValues", PartitionLevel = true,
                Explanation = "Empty the partition — drop all its data (and dependents), leaving it unprocessed. Frees memory; queries return blank until it is refreshed again. Destructive." },
            new RefreshTypeInfo { Name = "Automatic", PartitionLevel = true,
                Explanation = "Refresh and recalculate only if the partition is not already up to date (not in a Ready state) — a no-op if it is current ('Process Default')." },
            new RefreshTypeInfo { Name = "Add", PartitionLevel = true,
                Explanation = "Append new rows to the partition without reprocessing the existing data, then recalculate dependents. Regular (non-calculated) partitions only; a niche/advanced option for incremental-append (push/streaming) scenarios." },
            new RefreshTypeInfo { Name = "Defragment", PartitionLevel = false,
                Explanation = "Defragment the TABLE's column dictionaries (clean out values no longer present after partitions were processed independently). A table-level optimization — not valid for a single partition." },
            new RefreshTypeInfo { Name = "Indexes", PartitionLevel = false,
                Explanation = "Rebuild indexes only. Preview-compatibility databases only; rarely needed. Not offered for a single-partition refresh." },
        };

        public Task<RefreshTypeInfo[]> ListRefreshTypesAsync() => Task.FromResult(RefreshTypes);

        private static RefreshTypeInfo ResolveRefreshType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Full";
            var info = RefreshTypes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown refresh type '{name}'. Valid for a partition: Full, DataOnly, Calculate, ClearValues, Automatic, Add.");
            if (!info.PartitionLevel)
                throw new InvalidOperationException($"'{info.Name}' is a table-level operation, not valid for a single partition — run it on the table instead.");
            return info;
        }

        public async Task<RefreshReport> RefreshPartitionAsync(string partitionRef, string refreshType, string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human")
        {
            var s = _sessions.Require();
            var type = ResolveRefreshType(refreshType);   // validates the name + partition-level validity (throws on bad input)

            // Resolve the partition in the session (read-only) → table + partition names; confirm it exists locally.
            var (tableName, partName) = await s.ReadAsync(m =>
            {
                if (!(ObjectRefs.Resolve(m, partitionRef) is Partition p))
                    throw new InvalidOperationException($"{partitionRef} is not a partition — pass a 'partition:Table/Name' ref; run list_partitions on the table to see its partitions.");
                return (p.Table?.Name, p.Name);
            });

            // Deploy-to-source: default the endpoint/database to the live model this session was opened from. A file
            // model (no live origin and no explicit endpoint) has no data engine to refresh — refuse with a clear message.
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                var src = s.LiveOrigin ?? throw new InvalidOperationException(
                    "refresh_partition: this session is not connected to a live model, so there is nothing to refresh (a file model has no data engine). Open it with open_live / open_local first, or pass an endpoint + database explicitly.");
                endpoint = src.Endpoint; database = src.Database;
                if (string.IsNullOrWhiteSpace(tenantId)) tenantId = src.TenantId;
                if (string.IsNullOrWhiteSpace(authMode)) authMode = src.AuthMode;
            }
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("refresh_partition: a database (dataset) name is required when you pass an explicit endpoint.");

            var rep = new RefreshReport
            {
                Partition = $"partition:{tableName}/{partName}", Table = tableName,
                RefreshType = type.Name, Explanation = type.Explanation,
                Endpoint = endpoint, Database = database, Live = s.LiveOrigin != null, Committed = false,
                Plan = $"{(commit ? "Refreshing" : "Would refresh")} partition '{partName}' on table '{tableName}' with {type.Name} ({type.Explanation}) against {endpoint} / {database}."
            };
            if (!commit) return rep;   // DRY RUN — report only; never connects, never executes.

            // Agent-permissions gate — a refresh is a live data write (ClearValues literally empties the partition).
            // The matrix advertises a Refresh row in every preset; without this call the cell would be a lie. Fail
            // fast before auth/network; intentBasis pins the partition AND type so approving a Calculate can never
            // authorise a ClearValues, nor this partition's grant spend on another. Refusal on the DTO (door-safe).
            {
                var refusal = GuardAgent(AgentCapability.Refresh, endpoint, database, origin, isCommit: true,
                    summary: $"refresh partition '{partName}' on table '{tableName}' ({type.Name}) against {database} on {endpoint}",
                    intentBasis: $"refresh:{tableName}/{partName}|{type.Name}");
                if (refusal != null) { rep.Error = refusal; return rep; }
            }

            // COMMIT (live write): acquire a token (a local loopback instance uses Windows auth — no token), then
            // connect + RequestRefresh + SaveChanges on a background thread. Any failure is reported via rep.Error.
            var local = LiveDeploy.IsLocalEndpoint(endpoint);
            var tok = local ? default : await AcquireLiveTokenAsync(s, authMode, rawToken, tenantId, System.Threading.CancellationToken.None);
            var (committed, error) = await Task.Run(() => LiveRefresh.RefreshPartition(tableName, partName, type.Name, endpoint, database, tok.Token, tok.ExpiresOn));
            rep.Committed = committed; rep.Error = error;
            return rep;
        }

        public Task<TreeNode[]> ListTreeAsync(string parentRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => BuildTree(m, parentRef));
        }

        private static TreeNode[] BuildTree(Model m, string parentRef)
        {
            // Roots: tables (calc groups distinguished), then virtual collection folders for the model-level
            // object sets that have no natural table parent (relationships / roles / perspectives), then functions.
            // The folders carry a non-resolvable "folder:*" ref used only to fan their children — never for menus.
            if (string.IsNullOrEmpty(parentRef))
            {
                var nodes = new List<TreeNode>();
                nodes.AddRange(m.Tables
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new TreeNode
                    {
                        Ref = ObjectRefs.For(t),
                        Name = t.Name,
                        Kind = t is CalculationGroupTable ? "calcgroup" : "table",
                        HasChildren = true,
                    }));
                if (m.Relationships.Count > 0)
                    nodes.Add(new TreeNode { Ref = "folder:relationships", Name = "Relationships", Kind = "folderRelationships", HasChildren = true });
                if (m.Roles.Count > 0)
                    nodes.Add(new TreeNode { Ref = "folder:roles", Name = "Roles", Kind = "folderRoles", HasChildren = true });
                if (m.Perspectives.Count > 0)
                    nodes.Add(new TreeNode { Ref = "folder:perspectives", Name = "Perspectives", Kind = "folderPerspectives", HasChildren = true });
                nodes.AddRange(m.Functions
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new TreeNode { Ref = ObjectRefs.For(f), Name = f.Name, Kind = "function", HasChildren = false }));
                return nodes.ToArray();
            }

            switch (parentRef)
            {
                case "folder:relationships":
                    return m.Relationships.OfType<SingleColumnRelationship>()
                        .OrderBy(r => r.FromTable?.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new TreeNode
                        {
                            Ref = ObjectRefs.For(r),
                            Name = $"{r.FromTable?.Name}[{r.FromColumn?.Name}] → {r.ToTable?.Name}[{r.ToColumn?.Name}]" + (r.IsActive ? "" : " (inactive)"),
                            Kind = "relationship",
                            HasChildren = false,
                        }).ToArray();
                case "folder:roles":
                    return m.Roles
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new TreeNode { Ref = ObjectRefs.For(r), Name = r.Name, Kind = "role", HasChildren = false })
                        .ToArray();
                case "folder:perspectives":
                    return m.Perspectives
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(p => new TreeNode { Ref = ObjectRefs.For(p), Name = p.Name, Kind = "perspective", HasChildren = false })
                        .ToArray();
            }

            var obj = ObjectRefs.Resolve(m, parentRef);
            if (obj is CalculationGroupTable cg)
            {
                var items = cg.CalculationItems
                    .OrderBy(x => x.Ordinal)
                    .Select(x => new TreeNode { Ref = ObjectRefs.For(x), Name = x.Name, Kind = "calcitem", HasChildren = false });
                var cgMeasures = cg.Measures
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new TreeNode { Ref = ObjectRefs.For(x), Name = x.Name, Kind = "measure", HasChildren = false, DisplayFolder = x.DisplayFolder });
                return items.Concat(cgMeasures).ToArray();
            }
            if (obj is Table table)
            {
                var measures = table.Measures
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new TreeNode { Ref = ObjectRefs.For(x), Name = x.Name, Kind = "measure", HasChildren = false, DisplayFolder = x.DisplayFolder });
                var columns = table.Columns
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new TreeNode
                    {
                        Ref = ObjectRefs.For(x),
                        Name = x.Name,
                        Kind = x is CalculatedColumn ? "calcColumn" : "column",
                        HasChildren = false,
                        DisplayFolder = x.DisplayFolder,
                    });
                var hierarchies = table.Hierarchies
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new TreeNode { Ref = ObjectRefs.For(x), Name = x.Name, Kind = "hierarchy", HasChildren = x.Levels.Count > 0, DisplayFolder = x.DisplayFolder });
                var partitions = table.Partitions
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new TreeNode { Ref = ObjectRefs.For(x), Name = x.Name, Kind = "partition", HasChildren = false });
                return measures.Concat(columns).Concat(hierarchies).Concat(partitions).ToArray();
            }
            if (obj is Hierarchy hier)
            {
                return hier.Levels
                    .OrderBy(lv => lv.Ordinal)
                    .Select(lv => new TreeNode { Ref = ObjectRefs.For(lv), Name = lv.Name, Kind = "level", HasChildren = false })
                    .ToArray();
            }

            return Array.Empty<TreeNode>();
        }

        public Task<ObjectInfo> GetObjectAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef);
                if (obj == null) throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
                var info = new ObjectInfo { Ref = ObjectRefs.For(obj), Name = obj.Name, Kind = ObjectRefs.KindOf(obj) };
                switch (obj)
                {
                    case Measure me:
                        info.Properties["expression"] = me.Expression;
                        info.Properties["formatString"] = me.FormatString;
                        info.Properties["formatStringExpression"] = me.FormatStringExpression;   // dynamic ("Format = Dynamic"); null when static
                        info.Properties["displayFolder"] = me.DisplayFolder;
                        info.Properties["description"] = me.Description;
                        info.Properties["isHidden"] = me.IsHidden;
                        break;
                    case Column c:
                        info.Properties["dataType"] = c.DataType.ToString();
                        info.Properties["displayFolder"] = c.DisplayFolder;
                        info.Properties["isHidden"] = c.IsHidden;
                        info.Properties["description"] = c.Description;
                        info.Properties["formatString"] = c.FormatString;
                        info.Properties["summarizeBy"] = c.SummarizeBy.ToString();
                        info.Properties["dataCategory"] = c.DataCategory;
                        info.Properties["isKey"] = c.IsKey;
                        info.Properties["sortByColumn"] = c.SortByColumn?.Name;
                        break;
                    case Table t:
                        // Counts alone made "does this table have a description?" unanswerable through this op
                        // (a real result-shape gap hit live) — carry the authored metadata, not just the shape.
                        info.Properties["description"] = t.Description;
                        info.Properties["isHidden"] = t.IsHidden;
                        info.Properties["measures"] = t.Measures.Count;
                        info.Properties["columns"] = t.Columns.Count;
                        break;
                    default:
                        if (obj is IDescriptionObject d) info.Properties["description"] = d.Description;
                        break;
                }
                return info;
            });
        }

        public Task<string> GetDaxAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef);
                switch (obj)
                {
                    case Measure me: return me.Expression;
                    case CalculatedColumn cc: return cc.Expression;
                    case CalculatedTable ct: return ct.Expression;
                    case CalculationItem ci: return ci.Expression;
                    case Function f: return f.Expression;
                    default: throw new InvalidOperationException($"getDax: {objRef} has no DAX expression — get_dax reads measures, calculated columns/tables, calculation items and functions. Use get_object for a plain column/table's properties, or list_objects to find a DAX object.");
                }
            });
        }

        public async Task<SetResult> SetDaxAsync(string objRef, string expression, string origin)
        {
            // §9c — update_measure (MCP) and the RPC set_dax BOTH funnel through here, so binding "update_measure"
            // covers the measure-edit path on both doors (the op name Claude knows + what stock workflows declare).
            await EnforceBindingAsync("update_measure", origin);
            var verified = await VerifiedGuardAsync(expression, "this DAX edit");   // Verified Mode: refuse invalid DAX before it commits
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set DAX", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef);
                switch (obj)
                {
                    case Measure me:
                        if (me.Expression != expression) { me.Expression = expression; changed = true; }
                        break;
                    case CalculatedColumn cc:
                        if (cc.Expression != expression) { cc.Expression = expression; changed = true; }
                        break;
                    case CalculatedTable ct:
                        if (ct.Expression != expression) { ct.Expression = expression; changed = true; }
                        break;
                    case CalculationItem ci:
                        if (ci.Expression != expression) { ci.Expression = expression; changed = true; }
                        break;
                    case Function f:
                        if (f.Expression != expression) { f.Expression = expression; changed = true; }
                        break;
                    default:
                        throw new InvalidOperationException($"setDax: {objRef} has no DAX expression — set_dax targets measures, calculated columns/tables, calculation items and functions. To change a data column's type use set_column_data_type; run list_objects to find a DAX object.");
                }
            });
            if (changed) await RecordVerifiedModeEditAsync(verified, s, "set_dax", objRef, expression, rev, origin);
            return new SetResult { Revision = rev, Changed = changed };
        }

        // ---- Documentation narrative (additional context for the Documentation export, SEPARATE from the model's
        // first-class Descriptions). Stored as a per-object TOM annotation "Semanticus.Doc.Context" — a JSON envelope
        // of named sections — so it travels with the model in TMDL/.bim and BOTH doors (the UI + the user's Claude)
        // edit it on one shared, undoable timeline. setDocSection goes through MutateAsync, so it broadcasts
        // model/didChange and the human's Documentation tab updates live with the agent's additions.
        private const string DocAnnotation = "Semanticus.Doc.Context";

        private static IAnnotationObject ResolveDocTarget(Model m, string objRef)
        {
            if (string.IsNullOrWhiteSpace(objRef) || objRef == "model") return m;
            // Narrative is surfaced (read back by getDocModel, listed by the outline, rendered + exported) only for the
            // model, tables and measures. Gate the WRITE surface to the same set so a column/hierarchy/… narrative can't
            // be written into a black hole no door can read — see the outline (model/tables/measures) and the renderer.
            var obj = ObjectRefs.Resolve(m, objRef);
            return obj is Table or Measure ? obj as IAnnotationObject : null;
        }

        // Guarded string read of a JSON node: a non-string section value (number/array/hand-edited annotation) must
        // degrade to null, never throw out of a dispatcher read. Shared by ReadNarrative + getDocSection.
        private static string DocStr(System.Text.Json.Nodes.JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }

        private static System.Text.Json.Nodes.JsonObject ReadDocEnvelope(IAnnotationObject ao)
        {
            var raw = ao?.GetAnnotation(DocAnnotation);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(raw) is System.Text.Json.Nodes.JsonObject o)
                    {
                        if (o["sections"] is not System.Text.Json.Nodes.JsonObject) o["sections"] = new System.Text.Json.Nodes.JsonObject();
                        return o;
                    }
                }
                catch { /* corrupt annotation → start fresh */ }
            }
            return new System.Text.Json.Nodes.JsonObject { ["v"] = 1, ["sections"] = new System.Text.Json.Nodes.JsonObject() };
        }

        public Task<DocOutline> GetDocOutlineAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var items = new List<DocOutlineItem> { DocOutlineItemFor("model", m.Database?.Name ?? m.Name ?? "Model", "model", m) };
                foreach (var t in m.Tables)
                {
                    items.Add(DocOutlineItemFor("table:" + t.Name, t.Name, "table", t as IAnnotationObject));
                    foreach (var me in t.Measures)
                        items.Add(DocOutlineItemFor("measure:" + t.Name + "/" + me.Name, me.Name, "measure", me as IAnnotationObject));
                }
                return new DocOutline { Items = items.ToArray() };
            });
        }

        private static DocOutlineItem DocOutlineItemFor(string objRef, string name, string kind, IAnnotationObject ao)
        {
            var sections = ao == null ? Array.Empty<string>()
                : (ReadDocEnvelope(ao)["sections"] as System.Text.Json.Nodes.JsonObject)?.Select(kv => kv.Key).ToArray() ?? Array.Empty<string>();
            return new DocOutlineItem { Ref = objRef, Name = name, Kind = kind, Sections = sections };
        }

        public Task<string> GetDocSectionAsync(string objRef, string sectionKey)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var ao = ResolveDocTarget(m, objRef);
                if (ao == null || string.IsNullOrWhiteSpace(sectionKey)) return null;
                return DocStr((ReadDocEnvelope(ao)["sections"] as System.Text.Json.Nodes.JsonObject)?[sectionKey]);
            });
        }

        public async Task<SetResult> SetDocSectionAsync(string objRef, string sectionKey, string markdown, string origin)
        {
            var s = _sessions.Require();
            if (string.IsNullOrWhiteSpace(sectionKey)) throw new InvalidOperationException("setDocSection: a section key is required.");
            var newVal = markdown ?? "";
            // Decide on the dispatcher thread whether anything will actually change. A genuine no-op (re-saving identical
            // text, or clearing an already-empty section) must NOT mutate — otherwise MutateAsync→TrackAsync still bumps
            // the revision and broadcasts model/didChange with empty deltas, contradicting Changed=false and storming the
            // UI with pointless re-reads. So skip the mutate entirely when nothing changes.
            var willChange = await s.ReadAsync(m =>
            {
                var ao = ResolveDocTarget(m, objRef) ?? throw new InvalidOperationException($"setDocSection: {objRef} cannot carry documentation narrative.");
                var old = DocStr((ReadDocEnvelope(ao)["sections"] as System.Text.Json.Nodes.JsonObject)?[sectionKey]);
                return string.IsNullOrEmpty(newVal) ? old != null : old != newVal;
            });
            if (!willChange) return new SetResult { Revision = s.Revision, Changed = false };
            var rev = await s.MutateAsync(origin, "set doc narrative", m =>
            {
                var ao = ResolveDocTarget(m, objRef) ?? throw new InvalidOperationException($"setDocSection: {objRef} cannot carry documentation narrative.");
                var env = ReadDocEnvelope(ao);
                var sections = (System.Text.Json.Nodes.JsonObject)env["sections"];
                if (string.IsNullOrEmpty(newVal)) sections.Remove(sectionKey);
                else sections[sectionKey] = newVal;
                env["author"] = origin;
                env["updatedUtc"] = DateTime.UtcNow.ToString("o");
                // public SetAnnotation(name,value) is undoable + change-tracked (broadcasts); value=null removes it.
                ao.SetAnnotation(DocAnnotation, sections.Count == 0 ? null : env.ToJsonString());
            });
            return new SetResult { Revision = rev, Changed = true };
        }

        // Project an object's narrative annotation into a DTO (null when nothing has been authored). Reuses the same
        // JSON envelope ReadDocEnvelope/SetDocSection write, so what the renderer shows is exactly what the doors edit.
        private static DocNarrative ReadNarrative(IAnnotationObject ao)
        {
            if (ao == null) return null;
            var env = ReadDocEnvelope(ao);
            if (!(env["sections"] is System.Text.Json.Nodes.JsonObject sections) || sections.Count == 0) return null;
            return new DocNarrative
            {
                Author = DocStr(env["author"]),
                UpdatedUtc = DocStr(env["updatedUtc"]),
                Sections = sections.Select(kv => new DocSection { Key = kv.Key, Markdown = DocStr(kv.Value) }).ToArray(),
            };
        }

        public async Task<DocModelDto> GetDocModelAsync(int topN)
        {
            var s = _sessions.Require();
            var live = _live;                                  // snapshot the volatile once (a concurrent disconnect must not race us)
            var generatedUtc = DateTime.UtcNow.ToString("o");
            // One consistent dispatcher-thread read for everything model-derived (structure + narrative + the
            // deterministic analyzers). Storage stats need the live connection's async DMVs — fetched separately below.
            var dto = await s.ReadAsync(m => BuildDocModelCore(m, s.SourcePath, live?.Kind, generatedUtc));
            if (live != null)
            {
                try
                {
                    var vp = await VertiPaqScanAsync(topN <= 0 ? 50 : topN);
                    if (vp != null && string.IsNullOrEmpty(vp.Error))
                    {
                        dto.Storage = vp;
                        dto.StorageAvailable = true;
                        // Fold per-table row counts into the per-table detail (match by name). On Direct Lake the
                        // count is null (resident-only) — leave DocTable.RowCount null so the doc shows "unknown",
                        // not a misleading "0", rather than folding it in.
                        var rows = vp.Tables?.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                                             .ToDictionary(g => g.Key, g => g.First().Rows, StringComparer.OrdinalIgnoreCase);
                        if (rows != null)
                            foreach (var dt in dto.Tables)
                                if (dt.Name != null && rows.TryGetValue(dt.Name, out var rc) && rc.HasValue) dt.RowCount = rc.Value;
                    }
                }
                catch { /* storage is best-effort — a doc without VertiPaq stats is still valid */ }
            }
            return dto;
        }

        // Assemble the structural documentation snapshot from the live TOM tree (dispatcher thread). Composes the
        // existing readers (graph / measures / columns / roles) with new projections (calc groups, hierarchies, KPIs,
        // partitions, data sources, shared expressions) + the Documentation narrative. The deterministic analyzers
        // (readiness / BPA / Prep-for-AI) run here too, each guarded so a single analyzer fault can't sink the doc.
        private DocModelDto BuildDocModelCore(Model m, string source, string liveKind, string generatedUtc)
        {
            var graph = BuildGraph(m);
            var tables = m.Tables
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(BuildDocTable)
                .ToArray();

            var kpis = m.Tables.SelectMany(t => t.Measures)
                .Where(me => me.KPI != null)
                .Select(me => new DocKpi
                {
                    MeasureRef = ObjectRefs.For(me),
                    Measure = me.Name,
                    TargetExpression = me.KPI.TargetExpression,
                    StatusExpression = me.KPI.StatusExpression,
                    StatusGraphic = me.KPI.StatusGraphic,
                    TrendExpression = me.KPI.TrendExpression,
                    TrendGraphic = me.KPI.TrendGraphic,
                })
                .OrderBy(k => k.Measure, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var dataSources = m.DataSources
                .Select(ds => new DocDataSource { Name = ds.Name, Type = ds.Type.ToString() })
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expressions = m.Expressions
                .Select(e => new DocExpression { Name = e.Name, Kind = e.Kind.ToString(), Expression = e.Expression, Description = e.Description })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Deterministic analyzers — best-effort (a corrupt rule/model still yields a structural doc).
            Scorecard readiness = null;
            try { readiness = new ReadinessAnalyzer().Analyze(m); } catch { /* readiness best-effort */ }
            BpaScorecard bpa = null;
            try { bpa = BpaAnalyzer.Analyze(m, GetBpaRules(m)); } catch { /* BPA best-effort */ }
            PrepForAiConfig prep = null;
            try { prep = PrepForAiReader.Read(m); } catch { /* prep-for-AI best-effort */ }

            return new DocModelDto
            {
                Header = new DocModelHeader
                {
                    Name = m.Database?.Name ?? m.Name,
                    Description = m.Description,
                    Culture = m.Culture,
                    DefaultMode = m.DefaultMode.ToString(),
                    CompatibilityLevel = m.Database?.CompatibilityLevel ?? 0,
                    Source = source,
                    TableCount = m.Tables.Count,
                    MeasureCount = m.AllMeasures.Count(),
                    ColumnCount = m.Tables.Sum(t => t.Columns.Count(c => c.Type != ColumnType.RowNumber)),
                    RelationshipCount = graph.Relationships.Length,
                    LiveConnected = liveKind != null,
                    LiveKind = liveKind,
                    GeneratedUtc = generatedUtc,
                },
                Graph = graph,
                Tables = tables,
                Measures = WithMeasureNarrative(m, BuildMeasureRows(m)),
                Columns = BuildColumnRows(m),
                Roles = m.Roles.Select(BuildRoleInfo).ToArray(),
                Kpis = kpis,
                DataSources = dataSources,
                Expressions = expressions,
                ModelNarrative = ReadNarrative(m),
                Readiness = readiness,
                Bpa = bpa,
                PrepForAi = prep,
            };
        }

        // Attach each measure's authored narrative (the outline offers narrative on the model, tables AND measures).
        // Done only in the doc path so list_measures stays a pure metadata projection.
        private static MeasureRow[] WithMeasureNarrative(Model m, MeasureRow[] rows)
        {
            foreach (var r in rows)
                r.Narrative = ReadNarrative(ObjectRefs.Resolve(m, r.Ref) as IAnnotationObject);
            return rows;
        }

        // A Direct Lake EntityPartitionSource carries no expression text — describe its lakehouse/warehouse binding
        // ("Entity: schema.table") so the documentation shows a real source instead of an empty/undefined partition.
        private static string DescribeEntitySource(EntityPartition ep)
        {
            var name = string.IsNullOrEmpty(ep.SchemaName) ? ep.EntityName
                       : ep.SchemaName + "." + (ep.EntityName ?? "");
            if (string.IsNullOrEmpty(name)) name = ep.ExpressionSource?.Name;
            return string.IsNullOrEmpty(name) ? null : "Entity: " + name;
        }

        // internal (not private) so the smoke harness can fixture-test the Direct Lake partition projection
        // (an Entity source has no expression text — it must surface the entity binding, not null).
        internal static DocTable BuildDocTable(Table t)
        {
            var cg = t as CalculationGroupTable;
            return new DocTable
            {
                Ref = ObjectRefs.For(t),
                Name = t.Name,
                Description = t.Description,
                IsHidden = t.IsHidden,
                IsCalculated = t is CalculatedTable,
                IsDateTable = string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase),
                IsCalculationGroup = cg != null,
                CalcGroupPrecedence = cg?.CalculationGroupPrecedence ?? 0,
                DataCategory = t.DataCategory,
                Hierarchies = t.Hierarchies
                    .Select(h => new DocHierarchy
                    {
                        Name = h.Name,
                        Description = h.Description,
                        IsHidden = h.IsHidden,
                        Levels = h.Levels.OrderBy(l => l.Ordinal)
                            .Select(l => new DocLevel { Name = l.Name, Ordinal = l.Ordinal, Column = l.Column?.Name })
                            .ToArray(),
                    })
                    .ToArray(),
                Partitions = t.Partitions
                    .Select(p =>
                    {
                        var ep = p as EntityPartition;         // Direct Lake: EntityPartitionSource has no M/SQL/DAX expression
                        return new DocPartition
                        {
                            Name = p.Name,
                            Mode = p.Mode.ToString(),
                            SourceType = p.SourceType.ToString(),
                            // p.Expression is null for an Entity (Direct Lake) source — surface the lakehouse/
                            // warehouse entity binding so the doc doesn't imply an undefined partition.
                            Source = ep != null ? DescribeEntitySource(ep) : p.Expression,
                            EntityName = ep?.EntityName,
                            SchemaName = ep?.SchemaName,
                            DataSource = p.DataSource?.Name,   // null for M / Calculated sources
                            RefreshedTime = p.RefreshedTime.Year < 1900 ? null : p.RefreshedTime.ToString("o"),
                        };
                    })
                    .ToArray(),
                CalcItems = cg == null ? System.Array.Empty<DocCalcItem>()
                    : cg.CalculationItems.OrderBy(ci => ci.Ordinal)
                        .Select(ci => new DocCalcItem
                        {
                            Name = ci.Name,
                            Ordinal = ci.Ordinal,
                            Expression = ci.Expression,
                            FormatStringExpression = ci.FormatStringExpression,
                            Description = ci.Description,
                        })
                        .ToArray(),
                Narrative = ReadNarrative(t as IAnnotationObject),
            };
        }

        public async Task<SaveResult> SaveAsync(string path, string format)
        {
            var s = _sessions.Require();
            var fmt = ParseFormat(format);
            // Resolve an explicit save target the SAME way open does: a PBIP entry point (.pbip / project folder /
            // .SemanticModel folder) redirects to its inner definition/ TMDL root, so a save lands in definition/ and
            // never litters the .SemanticModel envelope. A null path reuses s.SourcePath (already the resolved
            // definition/ folder from open). Non-PBIP paths (a flat folder, a new export dir, a .bim file) pass through.
            var target = string.IsNullOrEmpty(path) ? s.SourcePath : ModelPathResolver.Resolve(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("This model has never been saved — pass a path to save_model (a folder for TMDL, or a .bim file).");
            // Write-corruption guard: a folder/JSON ("TabularEditorFolder") save into an EXISTING TMDL tree runs
            // SaveToFolder.RemoveUnusedFiles (recursive delete) and writes database.json + tables/*.json alongside the
            // .tmdl — a corrupt mixed model (verified). Coerce any non-TMDL format to TMDL when the target is an existing
            // TMDL root — covers a PBIP definition/ folder WITH or WITHOUT definition.pbism (so protection never lags the
            // open-resolver) and a flat TMDL folder the engine itself saved. A new/empty export folder isn't a TMDL root,
            // so genuine folder exports are unaffected.
            if (fmt != SaveFormat.TMDL && ModelPathResolver.IsTmdlRoot(target))
                fmt = SaveFormat.TMDL;
            using (await s.AcquireModelFileWriteLeaseAsync())
            {
                await s.Dispatcher.RunAsync(() =>
                {
                    s.Save(target, fmt, SerializeOptions.Default, resetCheckpoint: true);
                    return true;
                });
                // Anchor a created (path-less) session to its first explicit save target, so a later save with no
                // path overwrites the same file (matching file-opened session semantics).
                if (!string.IsNullOrEmpty(path)) s.SourcePath = target;
            }
            // Number time-machine (feature #3): saving is a checkpoint moment, but NOT a tracked mutation
            // (no ChangeNotification, no audit record — gap 4), so it gets its own explicit ambient hook.
            // Expressions only — a save freezes the formula history durably; values ride the deploy
            // checkpoints (the only moment the live model provably reflects this session's edits).
            await CaptureVitalsAsync(s, "save_model", "system", Array.Empty<string>(), liveReflectsSession: false);
            var count = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*.tmdl", SearchOption.AllDirectories).Count()
                : 0;
            return new SaveResult { Revision = s.Revision, Path = target, Format = fmt.ToString(), FileCount = count };
        }

        public Task<SessionInfo> SessionInfoAsync()
        {
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(new SessionInfo { SessionId = null, Revision = 0 });
            var origin = s.LiveOrigin;
            var live = _live;   // snapshot the volatile field once (a concurrent disconnect must not race us)
            return s.ReadAsync(m => new SessionInfo
            {
                SessionId = s.Id,
                Revision = s.Revision,
                ModelName = m.Database?.Name ?? m.Name,
                Source = s.SourcePath,
                HasUnsavedChanges = s.HasUnsavedChanges,
                Tables = m.Tables.Count,
                Measures = m.AllMeasures.Count(),
                LiveBound = origin != null,
                LiveEndpoint = origin?.Endpoint,
                LiveDatabase = origin?.Database,
                // The live QUERY connection (drives Studio). Distinct from LiveBound (deploy coordinates): a file
                // model can attach a live engine (LiveConnected, not LiveBound), and a unified open sets both.
                LiveConnected = live != null,
                LiveKind = live?.Kind,
                LiveDataSource = live?.DataSource,
                QueryDatabase = live?.Database,
                QueryConnectionId = live == null ? null : ConnectionRegistry.FindByEndpoint(live.DataSource, live.Database)?.Id
            });
        }

        public async Task<ConnectionContext> ConnectionContextAsync()
        {
            var session = _sessions.Current;
            var live = _live;
            if (session == null) return ConnectionContextBuilder.Build(null, null, live);
            var modelName = await session.ReadAsync(m => m.Database?.Name ?? m.Name);
            return ConnectionContextBuilder.Build(session, modelName, live);
        }

        public Task<ModelGraph> GetModelGraphAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(BuildGraph);
        }

        private static ModelGraph BuildGraph(Model m)
        {
            var tables = m.Tables
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new GraphTable
                {
                    Ref = ObjectRefs.For(t),
                    Name = t.Name,
                    IsHidden = t.IsHidden,
                    IsDateTable = string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase),
                    IsCalculated = t is CalculatedTable,
                    Columns = t.Columns.Count(c => c.Type != ColumnType.RowNumber),
                    Measures = t.Measures.Count,
                    KeyColumns = t.Columns.Where(c => c.IsKey && c.Type != ColumnType.RowNumber).Select(c => c.Name).ToArray(),
                    HasDescription = !string.IsNullOrWhiteSpace(t.Description),
                })
                .ToArray();

            var rels = m.Relationships.OfType<SingleColumnRelationship>()
                .Select(r => new GraphRelationship
                {
                    Name = r.Name,
                    FromTable = r.FromColumn?.Table?.Name,
                    FromColumn = r.FromColumn?.Name,
                    ToTable = r.ToColumn?.Table?.Name,
                    ToColumn = r.ToColumn?.Name,
                    FromCardinality = r.FromCardinality.ToString(),
                    ToCardinality = r.ToCardinality.ToString(),
                    CrossFilter = r.CrossFilteringBehavior.ToString(),
                    IsActive = r.IsActive,
                })
                .Where(r => r.FromTable != null && r.ToTable != null)
                .ToArray();

            return new ModelGraph { Tables = tables, Relationships = rels };
        }

        // ---- Diagram-layout sidecar (D1) -----------------------------------------------------------------------
        // Engine-owned positions in .semanticus/layout.json, keyed by LineageTag (Name fallback), reconciled to the
        // current model so a position survives a rename (tag-stable) and a deleted table is pruned. Read file IO runs
        // off the dispatcher; only the model read (for tags/names) is dispatched. See LayoutStore for the why/format.

        public async Task<LayoutData> GetLayoutAsync()
        {
            var s = _sessions.Require();
            var stored = LayoutStore.Read(s.SourcePath);                       // engine sidecar (writable overlay)
            var native = LayoutStore.ReadNativePbip(s.SourcePath);             // Power BI Desktop's diagramLayout.json (read-only base; empty if not a PBIP)
            if (stored.Count == 0 && native.Count == 0) return new LayoutData();
            // Native is the base layer (so a freshly-opened PBIP's diagram matches Desktop); the engine sidecar overlays
            // it per object (a reposition in Semanticus wins and persists). Both layers key the same way (LineageTag, else
            // Name). The sidecar pass de-dups first-wins (matching SaveLayoutAsync + the old behaviour on a hand-edited dup).
            var byKey = new Dictionary<string, LayoutStore.Entry>();
            foreach (var e in native) byKey[LayoutStore.Key(e.LineageTag, e.Name)] = e;            // native (already de-duped)
            foreach (var g in stored.GroupBy(e => LayoutStore.Key(e.LineageTag, e.Name)))
                byKey[g.Key] = g.First();                                                          // sidecar overrides native; first-wins among its own dups
            return await s.ReadAsync(m => new LayoutData
            {
                // Look up by the table's LineageTag key, then FALL BACK to its Name key — Power BI Desktop sometimes
                // leaves a node untagged (e.g. a measures table), so a tagless native position must still match a
                // modern (tagged) live table by name.
                Tables = m.Tables
                    .Select(t => new { t, e = byKey.TryGetValue(LayoutStore.Key(t.LineageTag, t.Name), out var e1) ? e1
                                                : byKey.TryGetValue(LayoutStore.Key(null, t.Name), out var e2) ? e2 : null })
                    .Where(x => x.e != null)
                    .Select(x => new LayoutNode
                    {
                        Ref = ObjectRefs.For(x.t),
                        Name = x.t.Name,
                        LineageTag = x.t.LineageTag,
                        X = x.e.X, Y = x.e.Y, Width = x.e.Width, Height = x.e.Height,
                    })
                    .ToArray()
            });
        }

        public async Task<SaveLayoutResult> SaveLayoutAsync(LayoutNode[] tables, string origin)
        {
            var s = _sessions.Require();
            if (string.IsNullOrEmpty(s.SourcePath))
                return new SaveLayoutResult { Error = "Save the model to disk first — diagram layout is stored beside the model (.semanticus/layout.json)." };

            var existing = LayoutStore.Read(s.SourcePath);
            var incoming = tables ?? Array.Empty<LayoutNode>();

            // Compute the new entry set on the dispatcher (needs the live model for authoritative LineageTag/Name).
            // Merge: an incoming position upserts; a current table not in the payload keeps its existing position; a
            // table that no longer exists is pruned (never re-emitted). Layout is NOT a model mutation — read only.
            var entries = await s.ReadAsync(m =>
            {
                var prior = existing.GroupBy(e => LayoutStore.Key(e.LineageTag, e.Name))
                                    .ToDictionary(g => g.Key, g => g.First());
                // Resolve each incoming node to a live table (by Ref, then Name) → its authoritative key.
                var sent = new Dictionary<string, LayoutNode>();
                foreach (var n in incoming)
                {
                    var t = (n.Ref != null ? ObjectRefs.Resolve(m, n.Ref) as Table : null)
                            ?? m.Tables.FirstOrDefault(x => x.Name == n.Name);
                    if (t != null) sent[LayoutStore.Key(t.LineageTag, t.Name)] = n;
                }
                var result = new List<LayoutStore.Entry>();
                foreach (var t in m.Tables)
                {
                    var key = LayoutStore.Key(t.LineageTag, t.Name);
                    LayoutStore.Entry e;
                    if (sent.TryGetValue(key, out var n))
                        e = new LayoutStore.Entry { X = n.X, Y = n.Y, Width = n.Width, Height = n.Height };
                    else if (prior.TryGetValue(key, out var p))
                        e = new LayoutStore.Entry { X = p.X, Y = p.Y, Width = p.Width, Height = p.Height };
                    else continue;                                              // no position for this table → omit
                    e.LineageTag = t.LineageTag; e.Name = t.Name;              // re-stamp with current tag/name
                    result.Add(e);
                }
                return result;
            });

            var path = LayoutStore.Write(s.SourcePath, entries);              // off-thread file write

            // Broadcast on layout's OWN channel so the other door's diagram moves live (dual-drive). Carries origin
            // so the saver can ignore its own echo. Not a model edit → never model/didChange (no needless reloads).
            _sessions.Bus.PublishLayout(new LayoutChange
            {
                Origin = origin,
                Tables = entries.Select(e => new LayoutNode
                {
                    Name = e.Name, LineageTag = e.LineageTag, X = e.X, Y = e.Y, Width = e.Width, Height = e.Height
                }).ToArray()
            });
            return new SaveLayoutResult { Saved = true, Path = path, Count = entries.Count };
        }

        // Legacy 2-arg entry — kept back-compatible: no filters means the legacy surface (name + description + DAX),
        // case-insensitive substring, exactly as before. Both doors' existing callers (RPC searchModel, the MCP
        // search_model 2-arg call) route here unchanged.
        public Task<SearchResult> SearchModelAsync(string query, int max)
            => SearchModelAsync(new SearchOptions { Query = query, Max = max });

        // Detailed search: match modes (case / whole-word / regex), scope + kind + field filters, a wider indexed
        // surface, and rich per-match results (MatchClass + raw-offset spans). All the work lives in ModelSearch;
        // this is the dispatcher-thread read seam. Read-only.
        public Task<SearchResult> SearchModelAsync(SearchOptions opts)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => ModelSearch.Run(m, opts));
        }

        public Task<MeasureRow[]> ListMeasuresAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(BuildMeasureRows);
        }

        // Shared measure projection — used by list_measures AND the documentation reader so both see the same shape.
        private static MeasureRow[] BuildMeasureRows(Model m) => m.Tables
            .SelectMany(t => t.Measures.Select(me => new MeasureRow
            {
                Ref = ObjectRefs.For(me),
                Name = me.Name,
                Table = t.Name,
                DisplayFolder = me.DisplayFolder,
                FormatString = me.FormatString,
                IsHidden = me.IsHidden,
                HasDescription = !string.IsNullOrWhiteSpace(me.Description),
                Description = me.Description,
                Expression = me.Expression,
            }))
            .OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public Task<ColumnRow[]> ListColumnsAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(BuildColumnRows);
        }

        // Shared column projection — used by list_columns AND the documentation reader.
        private static ColumnRow[] BuildColumnRows(Model m) => m.Tables
            .SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber).Select(c => new ColumnRow
            {
                Ref = ObjectRefs.For(c),
                Name = c.Name,
                Table = t.Name,
                DataType = c.DataType.ToString(),
                DisplayFolder = c.DisplayFolder,
                FormatString = c.FormatString,
                SummarizeBy = c.SummarizeBy.ToString(),
                DataCategory = c.DataCategory,
                IsKey = c.IsKey,
                IsHidden = c.IsHidden,
                IsCalculated = c is CalculatedColumn,
                HasDescription = !string.IsNullOrWhiteSpace(c.Description),
                Description = c.Description,
                Expression = (c as CalculatedColumn)?.Expression,
                SortByColumn = c.SortByColumn?.Name,
            }))
            .OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Backbone-B object browser read: tables + measures/columns/hierarchies in one pass, reusing the SAME
        // measure/column projections list_measures/list_columns use (so the browser and the audit grids never drift),
        // plus a hierarchy projection (with its levels) that neither of those carries. Read-only; works offline.
        public Task<ModelObjects> GetModelObjectsAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => new ModelObjects
            {
                Tables = m.Tables
                    .Select(t => new TableRow { Ref = ObjectRefs.For(t), Name = t.Name, IsHidden = t.IsHidden, IsCalculationGroup = t is CalculationGroupTable })
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                Measures = BuildMeasureRows(m),
                Columns = BuildColumnRows(m),
                Hierarchies = m.Tables
                    .SelectMany(t => t.Hierarchies.Select(h => new HierarchyRow
                    {
                        Ref = ObjectRefs.For(h),
                        Name = h.Name,
                        Table = t.Name,
                        DisplayFolder = h.DisplayFolder,
                        IsHidden = h.IsHidden,
                        Levels = h.Levels.OrderBy(l => l.Ordinal).Select(l => l.Column?.Name ?? l.Name).ToArray(),
                    }))
                    .OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            });
        }

        public async Task<UndoState> UndoAsync(string origin)
        {
            var s = _sessions.Require();
            await s.UndoAsync(origin);
            return await s.ReadAsync(_ => s.UndoStateNow());
        }

        public async Task<UndoState> RedoAsync(string origin)
        {
            var s = _sessions.Require();
            await s.RedoAsync(origin);
            return await s.ReadAsync(_ => s.UndoStateNow());
        }

        public async Task<string> CreateMeasureAsync(string tableRef, string name, string expression, string origin, string displayFolder = null)
        {
            // §9c op→workflow binding — the deliberate bindable-op set v1 is the six dual-drive authoring
            // chokepoints BOTH doors funnel through: create_measure, update_measure (SetDaxAsync),
            // create_calculated_column, create_calculation_item, create_table, create_relationship. An op that
            // isn't wired here cannot be bound — honest by design (§9.6): a binding that silently did nothing
            // would be worse than none. Called at the TOP so a hard-binding refusal precedes any mutation.
            await EnforceBindingAsync("create_measure", origin);
            var verified = await VerifiedGuardAsync(expression, "this new measure");   // Verified Mode: refuse invalid DAX before it commits
            var s = _sessions.Require();
            string newRef = null;
            var folder = NormalizeFolderPath(displayFolder);   // optional: born filed — create + folder is ONE undo step
            var rev = await s.MutateAsync(origin, $"create measure {name}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t)) throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                var me = CreateMeasureCore(t, name, expression);
                if (folder.Length > 0) me.DisplayFolder = folder;
                newRef = ObjectRefs.For(me);
            });
            await RecordVerifiedModeEditAsync(verified, s, "create_measure", newRef, expression, rev, origin);
            return newRef;
        }

        private static Measure CreateMeasureCore(Table t, string name, string expression) => t.AddMeasure(name, expression ?? string.Empty);

        /// <summary>Raise the model's compatibility level (one-way upgrade). Required for newer
        /// features like DAX user-defined functions (CL >= 1702). Refuses to lower it.</summary>
        public async Task<SetResult> SetCompatibilityLevelAsync(int level, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"set compatibility level {level}", m =>
            {
                if (m.Database == null) throw new InvalidOperationException("Model has no database.");
                var current = m.Database.CompatibilityLevel;
                if (current == level) return;
                if (level < current) throw new InvalidOperationException($"Compatibility level can only be raised. It cannot be lowered from {current} to {level}.");
                m.Database.CompatibilityLevel = level;
                changed = true;
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<string> CreateFunctionAsync(string name, string expression, string origin)
        {
            var verified = await VerifiedGuardAsync(expression, "this new function");   // Verified Mode: refuse invalid DAX before it commits
            var s = _sessions.Require();
            string newRef = null;
            var rev = await s.MutateAsync(origin, $"create function {name}", m =>
            {
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Function name is required.");
                if (m.Functions.Contains(name)) throw new InvalidOperationException($"A function named '{name}' already exists.");
                var f = m.AddFunction(name);
                if (!string.IsNullOrEmpty(expression)) f.Expression = expression;
                newRef = ObjectRefs.For(f);
            });
            await RecordVerifiedModeEditAsync(verified, s, "create_function", newRef, expression, rev, origin);
            return newRef;
        }

        // ---- Object authoring: create / delete / duplicate (the tree's New ▸ / Delete / Duplicate) ----------
        // Each is a single undoable MutateAsync that broadcasts model/didChange, so the human UI and the user's
        // Claude both get create/delete identically (golden rule #2). The TOM Add*/Delete*/Clone* already exist
        // under TOMWrapper; these just surface them as typed ops returning the new object's stable ref.

        public async Task<string> CreateTableAsync(string name, string origin)
        {
            await EnforceBindingAsync("create_table", origin);   // §9c op→workflow binding
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name is required.");
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create table {name}", m =>
            {
                if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
                newRef = ObjectRefs.For(m.AddTable(name));
            });
            return newRef;
        }

        public async Task<string> CreateColumnAsync(string tableRef, string name, string dataType, string sourceColumn, string origin)
        {
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create column {name}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t)) throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                newRef = ObjectRefs.For(CreateColumnCore(t, name, dataType, sourceColumn));
            });
            return newRef;
        }

        private static DataColumn CreateColumnCore(Table t, string name, string dataType, string sourceColumn)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A column name is required.");
            if (t.Columns.Contains(name)) throw new InvalidOperationException($"Table '{t.Name}' already has a column '{name}'.");
            var src = string.IsNullOrWhiteSpace(sourceColumn) ? name : sourceColumn;
            return t.AddDataColumn(name, src, null, ParseDataType(dataType));
        }

        // ---- Fabric / from-scratch authoring: data sources, shared M, partitions, calc tables ----------------
        // The primitives a model built from scratch needs (a table created via create_table has a placeholder
        // partition but no real source). create_import_table / create_directlake_table bundle a table with its
        // source so there is never an orphaned placeholder partition.

        // Each Create*Async wraps a sync *Core in one MutateAsync; build_model_from_spec calls the cores directly
        // inside ONE MutateAsync so the whole build is a single undo step (see BuildModelFromSpecAsync).

        public async Task<string> CreateDataSourceAsync(string name, string server, string database, string origin)
        {
            var s = _sessions.Require();
            string result = null;
            await s.MutateAsync(origin, $"create data source {name}", m => result = CreateDataSourceCore(m, name, server, database));
            return result;
        }

        private static string CreateDataSourceCore(Model m, string name, string server, string database)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A data source name is required.");
            if (m.Database.CompatibilityLevel < 1400)
                throw new InvalidOperationException("A structured (M) data source needs compatibility level 1400 or higher.");
            if (m.DataSources.Any(d => d.Name == name)) throw new InvalidOperationException($"A data source named '{name}' already exists.");
            var ds = m.AddStructuredDataSource(name);   // CL>=1400; adds to m.DataSources itself
            ds.Protocol = "tds";
            if (!string.IsNullOrWhiteSpace(server)) ds.Server = server;
            if (!string.IsNullOrWhiteSpace(database)) ds.Database = database;
            return ds.Name;                             // referenced by name (no datasource: ref kind)
        }

        public async Task<string> CreateNamedExpressionAsync(string name, string expression, string origin)
        {
            var s = _sessions.Require();
            string result = null;
            await s.MutateAsync(origin, $"create expression {name}", m => result = CreateNamedExpressionCore(m, name, expression));
            return result;
        }

        private static string CreateNamedExpressionCore(Model m, string name, string expression)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An expression name is required.");
            if (m.Expressions.Contains(name)) throw new InvalidOperationException($"A shared expression named '{name}' already exists.");   // TOM name match is case-insensitive (matches AddExpression)
            var e = m.AddExpression(name);              // Kind defaults to M; the 2nd arg is ignored by the wrapper
            if (!string.IsNullOrEmpty(expression)) e.Expression = expression;
            return e.Name;
        }

        // ---- M authoring: partition M + shared expressions (read/write) -----------------------------
        // Partition M is the source query behind an import/DirectQuery table; shared expressions are model-level M
        // (parameters + reusable queries). Neither flows through get/set_dax (those are DAX-only) — these are the M
        // door, used by the Studio M Code tab AND the agent. All writes go through MutateAsync (broadcast + undo).

        public Task<PartitionInfo[]> ListPartitionsAsync(string tableRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var t = ObjectRefs.Resolve(m, tableRef) as Table
                    ?? throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                return t.Partitions.Select(p => new PartitionInfo
                {
                    Ref = ObjectRefs.For(p), Table = t.Name, Name = p.Name,
                    Mode = p.Mode.ToString(), SourceType = p.SourceType.ToString(), DataSource = p.DataSource?.Name,
                }).ToArray();
            });
        }

        public Task<string> GetPartitionMAsync(string partitionRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var p = ObjectRefs.Resolve(m, partitionRef) as Partition
                    ?? throw new InvalidOperationException($"{partitionRef} is not a partition — pass a 'partition:Table/Name' ref; run list_partitions on the table to see its partitions.");
                if (p.SourceType != PartitionSourceType.M)
                    throw new InvalidOperationException($"{partitionRef} is a {p.SourceType} partition, not an M (structured) partition.");
                return p.Expression ?? "";
            });
        }

        public async Task<SetResult> SetPartitionMAsync(string partitionRef, string mExpression, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"set partition M {partitionRef}", m =>
            {
                var p = ObjectRefs.Resolve(m, partitionRef) as Partition
                    ?? throw new InvalidOperationException($"{partitionRef} is not a partition — pass a 'partition:Table/Name' ref; run list_partitions on the table to see its partitions.");
                if (p.SourceType != PartitionSourceType.M)
                    throw new InvalidOperationException($"{partitionRef} is a {p.SourceType} partition — only M partitions have an editable M expression.");
                if (string.IsNullOrWhiteSpace(mExpression))
                    throw new ArgumentException("The M expression cannot be empty.");
                if (p.Expression != mExpression) { p.Expression = mExpression; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public Task<DocExpression[]> ListNamedExpressionsAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => m.Expressions
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new DocExpression { Name = e.Name, Kind = e.Kind.ToString(), Expression = e.Expression, Description = e.Description })
                .ToArray());
        }

        public Task<string> GetNamedExpressionAsync(string name)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var e = m.Expressions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No shared expression named '{name}'.");
                return e.Expression ?? "";
            });
        }

        public async Task<SetResult> UpdateNamedExpressionAsync(string name, string expression, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"update expression {name}", m =>
            {
                var e = m.Expressions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No shared expression named '{name}'. Create it with create_named_expression first.");
                if (string.IsNullOrEmpty(expression)) throw new ArgumentException("The expression cannot be empty.");
                if (e.Expression != expression) { e.Expression = expression; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // AddTable always materialises one placeholder partition and you cannot delete a table's LAST partition, so
        // these ops add the REAL partition under a temp name, drop the placeholder, then rename it to the table.
        private const string TmpPartitionName = "__semanticus_tmp_partition__";

        public async Task<string> CreateImportTableAsync(string name, string mExpression, string origin)
        {
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create import table {name}", m => newRef = ObjectRefs.For(CreateImportTableCore(m, name, mExpression)));
            return newRef;
        }

        private static Table CreateImportTableCore(Model m, string name, string mExpression)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name is required.");
            if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
            var t = m.AddTable(name);
            var p = t.AddMPartition(TmpPartitionName, mExpression ?? "");   // the real M (Import) partition
            p.Mode = ModeType.Import;
            DropPlaceholderPartitions(t, p);
            p.Name = name;
            return t;
        }

        public async Task<string> CreateDirectLakeTableAsync(string name, string entityName, string schemaName, string sourceName, string origin)
        {
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create direct lake table {name}", m => newRef = ObjectRefs.For(CreateDirectLakeTableCore(m, name, entityName, schemaName, sourceName)));
            return newRef;
        }

        private static Table CreateDirectLakeTableCore(Model m, string name, string entityName, string schemaName, string sourceName)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name is required.");
            if (m.Database.CompatibilityLevel < 1604)
                throw new InvalidOperationException("Direct Lake needs compatibility level 1604 or higher (create_model defaults to 1604; or raise it with set_compatibility_level).");
            if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
            var entity = string.IsNullOrWhiteSpace(entityName) ? name : entityName;
            var t = m.AddTable(name);
            var p = t.AddEntityPartition(TmpPartitionName, entity);
            if (!string.IsNullOrWhiteSpace(schemaName)) p.SchemaName = schemaName;
            // Bind the source by name: a shared M expression (ExpressionSource) or a structured data source.
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var expr = m.Expressions.FirstOrDefault(e => e.Name == sourceName);
                if (expr != null) p.ExpressionSource = expr;
                else if (m.DataSources.FirstOrDefault(d => d.Name == sourceName) is StructuredDataSource ds) p.DataSource = ds;
                else throw new InvalidOperationException($"No shared expression or structured data source named '{sourceName}' was found — create one first (create_named_expression / create_data_source).");
            }
            p.Mode = ModeType.DirectLake;                                  // per-partition mode (does not throw)
            DropPlaceholderPartitions(t, p);
            p.Name = name;
            return t;
        }

        public async Task<string> CreateCalculatedTableAsync(string name, string expression, string origin)
        {
            var verified = await VerifiedGuardAsync(expression, "this new calculated table");   // Verified Mode: refuse invalid DAX before it commits
            var s = _sessions.Require();
            string newRef = null;
            var rev = await s.MutateAsync(origin, $"create calculated table {name}", m => newRef = ObjectRefs.For(CreateCalculatedTableCore(m, name, expression)));
            await RecordVerifiedModeEditAsync(verified, s, "create_calculated_table", newRef, expression, rev, origin);
            return newRef;
        }

        private static Table CreateCalculatedTableCore(Model m, string name, string expression)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name is required.");
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("A calculated table needs a DAX expression.");
            if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
            return CalculatedTable.CreateNew(m, name, expression);   // honors the DAX (the AddCalculatedTable helper ignores it)
        }

        // --- Field parameters (Studio v2 Advanced Modelling) ---------------------------------------------
        // A field parameter is a calculated table of NAMEOF(...) rows that drives a slicer swapping which
        // measures/columns a visual shows. Power BI recognizes it by a magic extended property on the middle
        // column, plus a GroupByColumns "field switch" key. We declare the 3 columns explicitly because an
        // OFFLINE model has no AS engine to derive them — this mirrors a saved field-parameter's TMDL. The
        // marker + the [Value1..3] source columns were verified against Power BI Desktop's own output.
        public async Task<string> CreateFieldParameterAsync(string name, FieldParameterItem[] items, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A field-parameter table name is required.");
            if (items == null || items.Length == 0) throw new ArgumentException("A field parameter needs at least one field (a measure or column).");
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create field parameter {name}", m =>
            {
                // Guard up-front (before any mutation): the GroupByColumns field-switch key is null unless this is
                // a Power BI-mode model at CL>=1400 (TOMWrapper Column.GroupByColumns), and the marker only
                // round-trips at CL>=1400. Fail loudly rather than emit an inert, half-wired table.
                if ((m.Database?.CompatibilityLevel ?? 0) < 1400)
                    throw new InvalidOperationException($"Field parameters require compatibility level 1400 or higher (this model is {m.Database?.CompatibilityLevel ?? 0}; raise it with set_compatibility_level).");
                // Compare by name: the Tabular CompatibilityMode enum is distinct from the core one (== won't bind).
                if (m.Database.CompatibilityMode.ToString() != "PowerBI")
                    throw new InvalidOperationException("Field parameters are a Power BI feature — they require a Power BI-mode model.");

                // Resolve each field and build its NAMEOF row: a MEASURE is bare ([Measure], model-unique, the
                // form Desktop emits); a COLUMN is table-qualified ('Table'[Column]). Order = array position.
                var rows = new List<string>();
                for (int i = 0; i < items.Length; i++)
                {
                    var it = items[i];
                    if (it == null || string.IsNullOrWhiteSpace(it.ObjectRef)) throw new ArgumentException($"Field {i + 1} is missing an object ref.");
                    var obj = ObjectRefs.Resolve(m, it.ObjectRef) ?? throw new InvalidOperationException($"{it.ObjectRef} not found — a field parameter's fields are measures or columns; run list_measures / list_columns to get their refs.");
                    string nameOf, label = string.IsNullOrWhiteSpace(it.Label) ? null : it.Label.Trim();
                    switch (obj)
                    {
                        // Use the wrapper's escaped DAX identifiers (']'→']]', '''→''''') — a name like "Sales [USD]"
                        // or a table "O'Brien" would otherwise emit malformed/injectable DAX. Measure = bare [Name].
                        case Measure me: nameOf = $"NAMEOF({me.DaxObjectName})"; label ??= me.Name; break;
                        case Column c: nameOf = $"NAMEOF({c.DaxObjectFullName})"; label ??= c.Name; break;
                        default: throw new InvalidOperationException($"{it.ObjectRef} ({ObjectRefs.KindOf(obj)}) can't be a field-parameter field — only measures and columns can.");
                    }
                    rows.Add($"\t(\"{label.Replace("\"", "\"\"")}\", {nameOf}, {i})");   // DAX escapes a quote by doubling it
                }
                var dax = $"{name} =\n{{\n{string.Join(",\n", rows)}\n}}";

                var ct = (CalculatedTable)CreateCalculatedTableCore(m, name, dax);

                // Declare the 3 columns over the constructor's positional [Value1..3] result columns.
                var labelCol = ct.AddCalculatedTableColumn(name, "[Value1]", null, DataType.String);
                var fieldCol = ct.AddCalculatedTableColumn(name + " Fields", "[Value2]", null, DataType.String);
                var orderCol = ct.AddCalculatedTableColumn(name + " Order", "[Value3]", null, DataType.Int64);

                labelCol.SortByColumn = orderCol;                 // the visible label sorts by the order column
                fieldCol.SortByColumn = orderCol;
                fieldCol.IsHidden = true;                         // hide the plumbing; the label stays visible
                orderCol.IsHidden = true;
                // The marker that makes Power BI treat this table as a field parameter (Fields column ONLY).
                fieldCol.SetExtendedProperty("ParameterMetadata", "{\"version\":3,\"kind\":2}", ExtendedPropertyType.Json);
                labelCol.GroupByColumns.Add(fieldCol);            // the field-switch composite key

                newRef = ObjectRefs.For(ct);
            });
            return newRef;
        }

        public async Task<SetResult> SetColumnDataTypeAsync(string columnRef, string dataType, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set column data type", m =>
            {
                if (!(ObjectRefs.Resolve(m, columnRef) is DataColumn c))
                    throw new InvalidOperationException($"{columnRef} is not a data column — only physical/source columns have a settable data type (a calculated column derives its type from its DAX).");
                var dt = ParseDataType(dataType);
                if (c.DataType != dt) { c.DataType = dt; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        private static void DropPlaceholderPartitions(Table t, Partition keep)
        {
            foreach (var other in t.Partitions.Where(p => !ReferenceEquals(p, keep)).ToList())
                other.Delete();
        }

        // ---- Model SPEC: the session-held spec document, edited by both doors (spec-driven authoring) --------
        // Mirrors the Change-Plan store: the read/JSON work is done outside the gate; the gate only brackets the
        // store mutation, then PublishSpec broadcasts the new snapshot (spec/didChange) so the Spec tab + Claude
        // stay live. The spec is NOT model content — it is an authoring artifact that build_model_from_spec
        // materialises, so its ops do not go through MutateAsync / the model undo timeline.

        private async Task<SpecView> PublishSpec(Action mutate)
        {
            await _specGate.WaitAsync();
            SpecView view;
            try { mutate(); view = _spec.View(); } finally { _specGate.Release(); }
            _sessions.Bus.PublishSpec(view);
            return view;
        }

        public async Task<SpecView> GetSpecAsync()
        {
            await _specGate.WaitAsync();
            try { return _spec.View(); } finally { _specGate.Release(); }
        }

        public Task<SpecView> SetSpecAsync(string specJson, string origin)
        {
            var spec = ParseSpec(specJson, "spec JSON");
            return PublishSpec(() => _spec.Set(spec, "manual"));
        }

        public Task<SpecView> ClearSpecAsync(string origin) => PublishSpec(() => _spec.Clear());

        public async Task<SpecView> SaveSpecAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required to save the spec.");
            var full = Path.GetFullPath(path);
            await _specGate.WaitAsync();
            SpecView view;
            try
            {
                var spec = _spec.Require();
                File.WriteAllText(full, System.Text.Json.JsonSerializer.Serialize(spec, SpecJson));
                view = _spec.View();
            }
            finally { _specGate.Release(); }
            return view;
        }

        public Task<SpecView> LoadSpecAsync(string path, string origin)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required to load a spec.");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("Spec file not found: " + full);
            var spec = ParseSpec(File.ReadAllText(full), "spec file " + full);
            return PublishSpec(() => _spec.Set(spec, "file"));
        }

        private static ModelSpec ParseSpec(string json, string what)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException($"A {what} is required.");
            ModelSpec spec;
            try { spec = System.Text.Json.JsonSerializer.Deserialize<ModelSpec>(json, SpecJson); }
            catch (Exception ex) { throw new InvalidOperationException($"Invalid {what}: {ex.Message}"); }
            return spec ?? throw new InvalidOperationException($"The {what} deserialised to null.");
        }

        // ---- build_model_from_spec: materialise the spec into the open model in ONE undoable transaction --------
        // Composes the Phase-2 *Core helpers inside a single MutateAsync (= one undo step; rollback-on-throw on an
        // escaping error). Per-item failures are caught and recorded so a bad row doesn't sink the whole build, and
        // existing objects are skipped (light re-runnability). Build INTO the current session — create_model first
        // for from-scratch.
        public async Task<SpecBuildReport> BuildModelFromSpecAsync(string origin)
        {
            ModelSpec spec;
            await _specGate.WaitAsync();
            try { spec = _spec.Require(); } finally { _specGate.Release(); }

            var s = _sessions.Require();
            // Pro gate: "build the whole model from a spec in one shot" is the spec→build bulk primitive (the Pro
            // value); free authors objects individually. Thrown before the mutate, so a refusal leaves the model intact.
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Building a model from a spec",
                "Author objects individually (create_table / create_measure / …).");
            var created = new System.Collections.Generic.List<string>();
            var skipped = new System.Collections.Generic.List<string>();
            var errors = new System.Collections.Generic.List<string>();
            int tBefore = 0, tAfter = 0, mBefore = 0, mAfter = 0;

            var rev = await s.MutateAsync(origin, "build model from spec", m =>
            {
                tBefore = m.Tables.Count; mBefore = m.AllMeasures.Count();
                BuildSpecInto(m, spec, created, skipped, errors);
                tAfter = m.Tables.Count; mAfter = m.AllMeasures.Count();
            });

            return new SpecBuildReport
            {
                Revision = rev,
                Created = created.ToArray(), Skipped = skipped.ToArray(), Errors = errors.ToArray(),
                TablesBefore = tBefore, TablesAfter = tAfter, MeasuresBefore = mBefore, MeasuresAfter = mAfter,
                Note = $"Created {created.Count}, skipped {skipped.Count}" + (errors.Count > 0 ? $", {errors.Count} error(s)" : "") + ".",
            };
        }

        private static void BuildSpecInto(Model m, ModelSpec spec, System.Collections.Generic.List<string> created,
            System.Collections.Generic.List<string> skipped, System.Collections.Generic.List<string> errors)
        {
            // Raise the compatibility level if the spec needs more (one-way upgrade) — e.g. Direct Lake needs 1604.
            if (spec.CompatibilityLevel > (m.Database?.CompatibilityLevel ?? 0)) m.Database.CompatibilityLevel = spec.CompatibilityLevel;
            var storage = (spec.StorageMode ?? "import").Trim().ToLowerInvariant();

            // 1. Source — a shared M expression to the Fabric SQL endpoint (the Direct Lake ExpressionSource target /
            //    the import-M reference) + a structured data source. Created once, named so the tables can bind by name.
            string sourceExprName = null;
            if (spec.Source != null && (!string.IsNullOrWhiteSpace(spec.Source.Server) || !string.IsNullOrWhiteSpace(spec.Source.Database)))
            {
                var srvr = spec.Source.Server ?? ""; var db = spec.Source.Database ?? "";
                sourceExprName = "FabricSource";
                if (!m.Expressions.Any(e => e.Name == sourceExprName))
                {
                    try { CreateNamedExpressionCore(m, sourceExprName, $"let Source = Sql.Database(\"{MEsc(srvr)}\", \"{MEsc(db)}\") in Source"); created.Add("expression:" + sourceExprName); }
                    catch (Exception ex) { errors.Add($"source expression: {ex.Message}"); }
                }
                else skipped.Add("expression:" + sourceExprName);
                if ((m.Database?.CompatibilityLevel ?? 0) >= 1400 && !m.DataSources.Any(d => d.Name == "FabricSql"))
                {
                    try { CreateDataSourceCore(m, "FabricSql", srvr, db); created.Add("datasource:FabricSql"); }
                    catch (Exception ex) { errors.Add($"data source: {ex.Message}"); }
                }
            }

            // 2. Tables (+ their columns).
            foreach (var st in spec.Tables ?? Array.Empty<SpecTable>())
            {
                if (string.IsNullOrWhiteSpace(st.Name)) { errors.Add("table: missing name"); continue; }
                if (m.Tables.Contains(st.Name)) { skipped.Add("table:" + st.Name); continue; }
                Table t;
                try
                {
                    var role = (st.Role ?? "").Trim().ToLowerInvariant();
                    // Route on the MODEL storage mode (Entity is a physical-table binding valid in BOTH modes — for
                    // Direct Lake it's the entity partition; for Import it's the Item= in the M — so it must NOT by
                    // itself force Direct Lake, or a fabric-import spec would silently build all-Direct-Lake).
                    if (!string.IsNullOrWhiteSpace(st.CalculatedExpression) || role == "calculated")
                        t = CreateCalculatedTableCore(m, st.Name, string.IsNullOrWhiteSpace(st.CalculatedExpression) ? "ROW(\"_\", BLANK())" : st.CalculatedExpression);
                    else if (storage == "directlake")
                        t = CreateDirectLakeTableCore(m, st.Name, st.Entity ?? st.Name, st.Schema ?? spec.Source?.Schema, st.SourceName ?? sourceExprName);
                    else
                        t = CreateImportTableCore(m, st.Name, string.IsNullOrWhiteSpace(st.MExpression) ? DefaultImportM(st, spec, sourceExprName) : st.MExpression);
                    created.Add("table:" + st.Name);
                }
                catch (Exception ex) { errors.Add($"table {st.Name}: {ex.Message}"); continue; }

                if (t is CalculatedTable) continue; // calc-table columns are inferred from the DAX
                foreach (var sc in st.Columns ?? Array.Empty<SpecColumn>())
                {
                    if (string.IsNullOrWhiteSpace(sc.Name)) { errors.Add($"column on {st.Name}: missing name"); continue; }
                    if (t.Columns.Contains(sc.Name)) { skipped.Add($"column:{st.Name}/{sc.Name}"); continue; }
                    try
                    {
                        var c = CreateColumnCore(t, sc.Name, sc.DataType, sc.SourceColumn);
                        if (sc.IsKey) c.IsKey = true;
                        if (sc.Hidden) c.IsHidden = true;
                        if (!string.IsNullOrWhiteSpace(sc.SummarizeBy) && Enum.TryParse<AggregateFunction>(sc.SummarizeBy.Trim(), true, out var agg)) c.SummarizeBy = agg;
                        created.Add($"column:{st.Name}/{sc.Name}");
                    }
                    catch (Exception ex) { errors.Add($"column {st.Name}/{sc.Name}: {ex.Message}"); }
                }
            }

            // 3. Relationships (default FK→PK = many→one, or sr.Cardinality; skip if one already exists on the pair).
            foreach (var sr in spec.Relationships ?? Array.Empty<SpecRelationship>())
            {
                var from = ResolveSpecColumn(m, sr.FromTable, sr.FromColumn);
                var to = ResolveSpecColumn(m, sr.ToTable, sr.ToColumn);
                if (from == null || to == null) { errors.Add($"relationship {sr.FromTable}[{sr.FromColumn}] -> {sr.ToTable}[{sr.ToColumn}]: column not found"); continue; }
                if (m.Relationships.OfType<SingleColumnRelationship>().Any(r => (r.FromColumn == from && r.ToColumn == to) || (r.FromColumn == to && r.ToColumn == from)))
                { skipped.Add($"relationship:{sr.FromTable}->{sr.ToTable}"); continue; }
                try { var rel = CreateRelationshipCore(m, from, to, sr.CrossFilter, sr.IsActive, sr.Cardinality); created.Add("relationship:" + rel.Name); }
                catch (Exception ex) { errors.Add($"relationship {sr.FromTable}->{sr.ToTable}: {ex.Message}"); }
            }

            // 4. Date table (a CALENDAR calculated table) + mark as date.
            if (spec.DateTable != null && !string.IsNullOrWhiteSpace(spec.DateTable.Name))
            {
                var dt = spec.DateTable;
                if (m.Tables.Contains(dt.Name)) skipped.Add("table:" + dt.Name);
                else
                {
                    try
                    {
                        var start = string.IsNullOrWhiteSpace(dt.StartExpr) ? "DATE(YEAR(TODAY())-5,1,1)" : dt.StartExpr;
                        var end = string.IsNullOrWhiteSpace(dt.EndExpr) ? "DATE(YEAR(TODAY()),12,31)" : dt.EndExpr;
                        var ct = CreateCalculatedTableCore(m, dt.Name, $"CALENDAR({start}, {end})");
                        if (dt.MarkAsDate) ct.DataCategory = "Time";
                        created.Add("table:" + dt.Name);
                    }
                    catch (Exception ex) { errors.Add($"date table {dt.Name}: {ex.Message}"); }
                }
            }

            // 5. Measures.
            foreach (var sm in spec.Measures ?? Array.Empty<SpecMeasure>())
            {
                if (string.IsNullOrWhiteSpace(sm.Name) || string.IsNullOrWhiteSpace(sm.Table)) { errors.Add("measure: missing name/table"); continue; }
                var mt = m.Tables.FirstOrDefault(x => x.Name == sm.Table);
                if (mt == null) { errors.Add($"measure {sm.Name}: table '{sm.Table}' not found"); continue; }
                if (mt.Measures.Contains(sm.Name)) { skipped.Add($"measure:{sm.Table}/{sm.Name}"); continue; }
                try
                {
                    var me = CreateMeasureCore(mt, sm.Name, sm.Dax);
                    if (!string.IsNullOrWhiteSpace(sm.FormatString)) me.FormatString = sm.FormatString;
                    if (!string.IsNullOrWhiteSpace(sm.DisplayFolder)) me.DisplayFolder = sm.DisplayFolder;
                    created.Add($"measure:{sm.Table}/{sm.Name}");
                }
                catch (Exception ex) { errors.Add($"measure {sm.Table}/{sm.Name}: {ex.Message}"); }
            }

            // 6. Time-intelligence over the named base measures (or all spec measures), against the date column.
            if (spec.TimeIntelligence != null && spec.TimeIntelligence.Length > 0)
            {
                var dateColDax = FindDateColumnDax(m, spec);
                if (dateColDax == null) errors.Add("time intelligence: no date column found (add a dateTable to the spec)");
                else
                {
                    var bases = (spec.TimeIntelligenceBaseMeasures != null && spec.TimeIntelligenceBaseMeasures.Length > 0)
                        ? spec.TimeIntelligenceBaseMeasures
                        : (spec.Measures ?? Array.Empty<SpecMeasure>()).Select(x => x.Name).ToArray();
                    foreach (var baseName in bases)
                    {
                        var baseMeasure = m.AllMeasures.FirstOrDefault(x => x.Name == baseName);
                        if (baseMeasure == null) continue;
                        foreach (var variant in spec.TimeIntelligence)
                        {
                            var tiName = $"{baseName} {variant}";
                            if (baseMeasure.Table.Measures.Contains(tiName)) { skipped.Add($"measure:{baseMeasure.Table.Name}/{tiName}"); continue; }
                            var dax = SpecTiDax(variant, $"[{baseName}]", dateColDax, out var fmt);
                            if (dax == null) continue;
                            try
                            {
                                var me = CreateMeasureCore(baseMeasure.Table, tiName, dax);
                                me.DisplayFolder = "Time Intelligence";
                                me.FormatString = fmt ?? (string.IsNullOrWhiteSpace(baseMeasure.FormatString) ? me.FormatString : baseMeasure.FormatString);
                                created.Add($"measure:{baseMeasure.Table.Name}/{tiName}");
                            }
                            catch (Exception ex) { errors.Add($"TI {tiName}: {ex.Message}"); }
                        }
                    }
                }
            }
        }

        private static Column ResolveSpecColumn(Model m, string table, string column)
        {
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column)) return null;
            var t = m.Tables.FirstOrDefault(x => x.Name == table);
            return t?.Columns.FirstOrDefault(c => c.Name == column);
        }

        private static string FindDateColumnDax(Model m, ModelSpec spec)
        {
            if (spec.DateTable != null && !string.IsNullOrWhiteSpace(spec.DateTable.Name) && m.Tables.Contains(spec.DateTable.Name))
                return $"'{spec.DateTable.Name}'[Date]";   // CALENDAR(...) yields a single [Date] column
            var dateTbl = m.Tables.FirstOrDefault(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase));
            var dcol = dateTbl?.Columns.FirstOrDefault(c => c.DataType == DataType.DateTime);
            return dcol != null ? $"'{dateTbl.Name}'[{dcol.Name}]" : null;
        }

        private static string DefaultImportM(SpecTable st, ModelSpec spec, string sourceExprName)
        {
            var entity = string.IsNullOrWhiteSpace(st.Entity) ? st.Name : st.Entity;
            var schema = st.Schema ?? spec?.Source?.Schema ?? "dbo";
            var src = sourceExprName ?? "Source";
            return $"let Source = #\"{MEsc(src)}\", Data = Source{{[Schema=\"{MEsc(schema)}\",Item=\"{MEsc(entity)}\"]}}[Data] in Data";
        }

        // Escape a value for embedding in an M string literal or #"..." quoted identifier (M doubles a " to "").
        // The engine never EVALUATES this M (it's authoring text), but an unescaped quote produces malformed Power
        // Query that would only fail later at refresh — so escape so any identifier with a quote round-trips.
        private static string MEsc(string s) => (s ?? "").Replace("\"", "\"\"");

        // The time-intelligence DAX for a spec variant (decoupled from generate_time_intelligence's variant set).
        private static string SpecTiDax(string variant, string baseRef, string dateCol, out string format)
        {
            format = null;
            switch ((variant ?? "").Trim().ToUpperInvariant())
            {
                case "YTD": return $"TOTALYTD({baseRef}, {dateCol})";
                case "QTD": return $"TOTALQTD({baseRef}, {dateCol})";
                case "MTD": return $"TOTALMTD({baseRef}, {dateCol})";
                case "PY": return $"CALCULATE({baseRef}, DATEADD({dateCol}, -1, YEAR))";
                case "YOY": return $"{baseRef} - CALCULATE({baseRef}, DATEADD({dateCol}, -1, YEAR))";
                case "YOY%": case "YOYPCT": format = "0.0%"; return $"DIVIDE({baseRef} - CALCULATE({baseRef}, DATEADD({dateCol}, -1, YEAR)), CALCULATE({baseRef}, DATEADD({dateCol}, -1, YEAR)))";
                default: return null;
            }
        }

        // ---- autogenerate_spec_from_model: propose a starter spec from the OPEN model (read-only) ---------------
        public async Task<SpecView> AutogenerateSpecFromModelAsync(string origin)
        {
            var s = _sessions.Require();
            var spec = await s.ReadAsync(AutogenerateSpecFromModel);
            return await PublishSpec(() => _spec.Set(spec, "autogenerate-model"));
        }

        // from = many (FK), to = one (lookup). Mirrors diagram.tsx roleOf: prefer the explicit Many end.
        private static (string many, string one) RoleOf(GraphRelationship r)
        {
            if (string.Equals(r.FromCardinality, "Many", StringComparison.OrdinalIgnoreCase)) return (r.FromTable, r.ToTable);
            if (string.Equals(r.ToCardinality, "Many", StringComparison.OrdinalIgnoreCase)) return (r.ToTable, r.FromTable);
            return (r.FromTable, r.ToTable);
        }

        private static ModelSpec AutogenerateSpecFromModel(Model m)
        {
            var graph = BuildGraph(m);
            var cols = BuildColumnRows(m);
            var measures = BuildMeasureRows(m);
            var directLake = DirectLakeInfo.IsModelDirectLake(m);

            // Kimball role classification (mirrors diagram.tsx busMatrixPositions): a table on the ONE side of any
            // relationship is a dimension (incl. snowflake outriggers); a table only ever on the MANY side is a fact;
            // unrelated tables are isolated. Date/calculated tables are tagged from their metadata.
            var oneSide = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manySide = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in graph.Relationships) { var (many, one) = RoleOf(r); manySide.Add(many); oneSide.Add(one); }
            string RoleFor(GraphTable t)
            {
                if (t.IsDateTable) return "date";
                if (t.IsCalculated) return "calculated";
                if (oneSide.Contains(t.Name)) return "dimension";
                if (manySide.Contains(t.Name)) return "fact";
                return "isolated";
            }
            var roleByTable = graph.Tables.ToDictionary(t => t.Name, RoleFor, StringComparer.OrdinalIgnoreCase);
            var colsByTable = cols.GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

            var specTables = graph.Tables.Select(t => new SpecTable
            {
                Name = t.Name,
                Role = roleByTable.TryGetValue(t.Name, out var rr) ? rr : "isolated",
                Columns = (colsByTable.TryGetValue(t.Name, out var cc) ? cc : System.Array.Empty<ColumnRow>())
                    .Where(c => !c.IsCalculated)   // calc columns derive from DAX; the spec carries data/source columns
                    .Select(c => new SpecColumn { Name = c.Name, DataType = c.DataType, SourceColumn = c.Name, IsKey = c.IsKey, Hidden = c.IsHidden, SummarizeBy = c.SummarizeBy })
                    .ToArray(),
            }).ToArray();

            // Emit each relationship LITERALLY — From/To ends as TOM has them, plus the real Cardinality token — so a
            // rebuild reproduces the exact same relationship. (The old code swapped ends to force From=the Many side
            // because the pre-cardinality builder ALWAYS built many→one; now CreateRelationshipCore honors Cardinality,
            // so a literal emit round-trips 1:1 / 1:many / m:m too, not just the common many→one.) The Many-side FK
            // orientation still drives the fact/dimension roleByTable above via RoleOf — that's a separate concern.
            var specRels = graph.Relationships.Select(r => new SpecRelationship
            {
                FromTable = r.FromTable, FromColumn = r.FromColumn,
                ToTable = r.ToTable, ToColumn = r.ToColumn,
                // Null for the common many→one keeps a star spec byte-clean; an explicit token otherwise so it survives.
                Cardinality = SpecCardinalityToken(r.FromCardinality, r.ToCardinality),
                CrossFilter = r.CrossFilter, IsActive = r.IsActive,
            }).ToArray();

            // Existing measures (so the spec faithfully describes the model) PLUS proposed additive measures for
            // numeric, non-key, aggregating fact columns that don't already have a "Total <col>" measure.
            var measureNames = new System.Collections.Generic.HashSet<string>(measures.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var specMeasures = new System.Collections.Generic.List<SpecMeasure>(measures.Select(x =>
                new SpecMeasure { Table = x.Table, Name = x.Name, Dax = x.Expression, FormatString = x.FormatString, DisplayFolder = x.DisplayFolder }));
            var proposedBases = new System.Collections.Generic.List<string>();   // additive 'Total <col>' — the only sensible TI bases
            foreach (var c in cols)
            {
                var role = roleByTable.TryGetValue(c.Table, out var rr2) ? rr2 : "isolated";
                var numeric = c.DataType == "Int64" || c.DataType == "Decimal" || c.DataType == "Double";
                if (role == "fact" && numeric && c.SummarizeBy != "None" && !c.IsKey)
                {
                    var name = "Total " + c.Name;
                    if (measureNames.Add(name))
                    {
                        specMeasures.Add(new SpecMeasure { Table = c.Table, Name = name, Dax = $"SUM ( '{c.Table}'[{c.Name}] )", DisplayFolder = "Base Measures" });
                        proposedBases.Add(name);
                    }
                }
            }

            var dateTbl = graph.Tables.FirstOrDefault(t => t.IsDateTable);
            return new ModelSpec
            {
                Name = m.Database?.Name ?? m.Name,
                CompatibilityLevel = m.Database?.CompatibilityLevel ?? 1604,
                StorageMode = directLake ? "directLake" : "import",
                Tables = specTables,
                Relationships = specRels,
                Measures = specMeasures.ToArray(),
                // Suggest time-intelligence ONLY over the additive base measures (not ratios/averages/percentages).
                TimeIntelligence = proposedBases.Count > 0 ? new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" } : System.Array.Empty<string>(),
                TimeIntelligenceBaseMeasures = proposedBases.ToArray(),
                DateTable = dateTbl != null ? new SpecDateTable { Name = dateTbl.Name, MarkAsDate = true } : new SpecDateTable { Name = "Date", MarkAsDate = true },
            };
        }

        // ---- autogenerate_spec_from_fabric: introspect a Fabric SQL endpoint (TDS) and propose a starter spec -----
        public async Task<SpecView> AutogenerateSpecFromFabricAsync(string server, string database, string authMode, string storageMode, string origin, string tenantId = null)
        {
            var modeLabel = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim();
            // What "no explicit tenant" resolves to depends on the mode — only azcli defaults to the *az CLI's* tenant.
            var tenantLabel = string.IsNullOrWhiteSpace(tenantId) ? DefaultTenantLabel(modeLabel) : tenantId.Trim();

            // Acquire a SQL-scoped Entra token (same credential as XMLA, different audience). tenantId lets us target a
            // tenant the current sign-in isn't the home of (the common failure: az CLI mints a token for whatever tenant
            // `az login` last used, so a workspace in ANOTHER tenant rejects it). A token-acquisition failure (not signed
            // in / cancelled sign-in / bad tenant) would otherwise throw a raw Azure.Identity error across the door —
            // catch it and return the SAME plain-language guidance as a login rejection, inside the introspection wrapper.
            string token;
            try
            {
                token = await EntraToken.AcquireSqlAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            }
            catch (Exception ex)
            {
                // Keep the original as innerException so its type/stack survives for debugging (don't drop it).
                throw new InvalidOperationException("Fabric SQL introspection failed: could not get a sign-in token. "
                    + FabricAuthHint(modeLabel, tenantLabel) + " (" + ScrubSchemaError(ex.Message) + ")", ex);
            }

            var schema = await FabricSqlSchema.ReadAsync(server, database, token, System.Threading.CancellationToken.None);
            if (!string.IsNullOrEmpty(schema.Error))
            {
                // Keep the underlying SQL message (don't swallow it) but, when it reads like an auth rejection, append the
                // actionable fix — a wrong-tenant token is by far the most common cause of "Could not login" here.
                var msg = "Fabric SQL introspection failed: " + schema.Error;
                if (LooksLikeAuthFailure(schema.Error)) msg += " " + FabricAuthHint(modeLabel, tenantLabel);
                throw new InvalidOperationException(msg);
            }
            var spec = FabricSchemaToSpec(schema, server, database, storageMode);
            return await PublishSpec(() => _spec.Set(spec, "autogenerate-fabric"));
        }

        // Plain-language fix for a Fabric SQL auth failure (harness doctrine: the tool result must teach the fix). Names
        // the wrong-tenant case first (the top cause), then the fallbacks, and echoes the mode + tenant it actually used.
        // Accurate on BOTH doors: the UI has a Tenant field; over MCP/CLI the same input is the tenantId parameter.
        private static string FabricAuthHint(string mode, string tenantLabel) =>
            "Authentication failed. If your Fabric workspace is in a different tenant than your sign-in, set the Tenant field "
            + "(or pass the tenantId parameter over MCP) to the workspace's tenant (its id or domain, e.g. contoso.com). Or "
            + "sign in with the right account (az login), or choose Interactive or Device code sign-in. (authMode=" + mode + ", tenant=" + tenantLabel + ")";

        // What "no explicit tenantId" resolves to, per auth mode — so the echoed hint never claims "az" for a non-az mode.
        private static string DefaultTenantLabel(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "azcli" => "az default",
            "interactive" or "entra" or "entramfa" or "mfa" or "devicecode" => "your sign-in's home tenant",
            "serviceprincipal" or "sp" => "the service principal's tenant",
            _ => "default",
        };

        // Does a scrubbed SQL/connection error read like an authentication rejection (vs. a genuine schema/network error)?
        private static bool LooksLikeAuthFailure(string e) =>
            !string.IsNullOrEmpty(e) && (
                e.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
                || e.Contains("login failed", StringComparison.OrdinalIgnoreCase)
                || e.Contains("could not login", StringComparison.OrdinalIgnoreCase)
                || e.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
                || e.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || e.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                || (e.Contains("token", StringComparison.OrdinalIgnoreCase) && e.Contains("expired", StringComparison.OrdinalIgnoreCase)));

        // Pure (no connection) — testable. Maps an introspected Fabric schema to a starter spec.
        internal static ModelSpec FabricSchemaToSpec(FabricSchema schema, string server, string database, string storageMode)
        {
            var directLake = string.Equals(storageMode?.Trim(), "directLake", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(storageMode?.Trim(), "directlake", StringComparison.OrdinalIgnoreCase);

            // Role from declared FKs: a referenced (PK) table is a dimension; a referencing-only table is a fact;
            // unrelated tables are isolated. (Fabric keys are often absent -> everything isolated; still a valid draft.)
            var referenced = new System.Collections.Generic.HashSet<string>(schema.ForeignKeys.Select(f => SchemaKey(f.ToSchema, f.ToTable)), StringComparer.OrdinalIgnoreCase);
            var referencing = new System.Collections.Generic.HashSet<string>(schema.ForeignKeys.Select(f => SchemaKey(f.FromSchema, f.FromTable)), StringComparer.OrdinalIgnoreCase);

            // TOM table names are flat (no schema), so a multi-schema warehouse with the same table name in two
            // schemas (e.g. dbo.Sales + staging.Sales) would collide. Qualify the name ONLY when it collides, and
            // route every table-identity + relationship endpoint through the same map so they stay consistent.
            var nameCounts = schema.Tables.GroupBy(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            string DisplayName(string sch, string tbl) => (nameCounts.TryGetValue(tbl ?? "", out var n) && n > 1) ? ((sch ?? "") + "_" + tbl) : tbl;

            var tables = schema.Tables.Select(t =>
            {
                var k = SchemaKey(t.Schema, t.Name);
                var role = referenced.Contains(k) ? "dimension" : referencing.Contains(k) ? "fact" : "isolated";
                var keyset = new System.Collections.Generic.HashSet<string>(t.KeyColumns ?? System.Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                return new SpecTable
                {
                    Name = DisplayName(t.Schema, t.Name), Role = role, Entity = t.Name, Schema = t.Schema,  // Entity stays the bare physical name
                    SourceName = directLake ? "FabricSource" : null,
                    Columns = (t.Columns ?? System.Array.Empty<FabricColumn>()).Select(c => new SpecColumn
                    {
                        Name = c.Name, DataType = c.DataType, SourceColumn = c.Name,
                        IsKey = keyset.Contains(c.Name),
                        Hidden = keyset.Contains(c.Name),    // key columns hidden by default (AI-readiness best practice)
                        SummarizeBy = IsNumericSpecType(c.DataType) && !keyset.Contains(c.Name) ? "Sum" : "None",
                    }).ToArray(),
                };
            }).ToArray();

            var rels = schema.ForeignKeys.Select(f => new SpecRelationship
            {
                FromTable = DisplayName(f.FromSchema, f.FromTable), FromColumn = f.FromColumn,
                ToTable = DisplayName(f.ToSchema, f.ToTable), ToColumn = f.ToColumn,
            }).ToArray();

            var measures = new System.Collections.Generic.List<SpecMeasure>();
            foreach (var t in tables.Where(t => t.Role == "fact"))
                foreach (var c in t.Columns.Where(c => IsNumericSpecType(c.DataType) && !c.IsKey))
                    measures.Add(new SpecMeasure { Table = t.Name, Name = "Total " + c.Name, Dax = $"SUM ( '{t.Name}'[{c.Name}] )", DisplayFolder = "Base Measures" });

            return new ModelSpec
            {
                Name = database,
                CompatibilityLevel = 1604,
                StorageMode = directLake ? "directLake" : "import",
                Source = new SpecSource { Kind = "fabric-sql", Server = server, Database = database },
                Tables = tables,
                Relationships = rels,
                Measures = measures.ToArray(),
                // Suggest time-intelligence ONLY over the additive base measures (never over ratios/averages).
                TimeIntelligence = measures.Count > 0 ? new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" } : System.Array.Empty<string>(),
                TimeIntelligenceBaseMeasures = measures.Select(x => x.Name).ToArray(),
                DateTable = new SpecDateTable { Name = "Date", MarkAsDate = true },
            };
        }

        private static string SchemaKey(string schema, string table) => (schema ?? "") + "." + (table ?? "");
        private static bool IsNumericSpecType(string dataType) => dataType == "Int64" || dataType == "Decimal" || dataType == "Double";

        // ---- Source control (local git via the git CLI) — the ALM lane -----------------------------------------
        // Operates on the open model's working directory (the folder it was opened from / saved to). Read verbs are
        // safe; commit/push are confirm-gated and a commit saves the dirty session to disk first so it captures the
        // current edits. Auth/remotes come from the user's git config — the engine never handles git credentials.

        private string GitWorkingDirOrNull() => GitWorkingDirOrNull(_sessions.Current);

        private static string GitWorkingDirOrNull(Session session)
        {
            var src = session?.SourcePath;
            if (string.IsNullOrEmpty(src)) return null;
            var full = System.IO.Path.GetFullPath(src);
            return System.IO.Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full);
        }

        private string GitWorkingDir() => GitWorkingDirOrNull()
            ?? throw new InvalidOperationException("Open or save a model on disk first — source control operates on the open model's folder.");

        public async Task<GitStatus> GitStatusAsync()
        {
            var dir = GitWorkingDirOrNull();
            if (dir == null) return new GitStatus { IsRepo = false, Note = "No model is open on disk." };
            var st = await GitCli.StatusAsync(dir);
            var s = _sessions.Current;
            if (s != null) st.ModelDirty = s.HasUnsavedChanges;
            return st;
        }

        public async Task<GitDiffResult> GitDiffAsync(string path, bool staged)
            => await GitCli.DiffAsync(GitWorkingDir(), path, staged);

        public async Task<GitLogEntry[]> GitLogAsync(int max)
            => await GitCli.LogAsync(GitWorkingDir(), max);

        public async Task<GitCommitResult> GitCommitAsync(string message, string[] files, bool commit, string origin)
        {
            var dir = GitWorkingDir();
            var s = _sessions.Require();
            if (!await GitCli.IsRepoAsync(dir)) return new GitCommitResult { Error = "Not a git repository." };
            var dirty = s.HasUnsavedChanges;
            if (!commit)
            {
                var pst = await GitCli.StatusAsync(dir);
                return new GitCommitResult
                {
                    Committed = false, Message = message, Files = pst.Files.Select(f => f.Path).ToArray(), SavedModelFirst = dirty,
                    Note = dirty ? "Preview — the open model has unsaved edits; commit will save them to disk first." : "Preview — nothing committed.",
                };
            }
            if (string.IsNullOrWhiteSpace(message)) return new GitCommitResult { Error = "A commit message is required." };
            var saved = false;
            if (dirty)
            {
                var fmt = System.IO.Directory.Exists(s.SourcePath) ? "TMDL"
                        : (s.SourcePath != null && s.SourcePath.EndsWith(".bim", StringComparison.OrdinalIgnoreCase) ? "BIM" : "TMDL");
                await SaveAsync(s.SourcePath, fmt);
                saved = true;
            }
            var add = (files != null && files.Length > 0)
                ? await GitCli.RunAsync(dir, new[] { "add", "--" }.Concat(files).ToArray())
                : await GitCli.RunAsync(dir, "add", "-A", ".");
            if (!add.Ok) return new GitCommitResult { Error = GitCli.Combine(add), SavedModelFirst = saved };
            var cached = await GitCli.RunAsync(dir, "diff", "--cached", "--name-only");
            var stagedFiles = cached.Ok ? cached.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries) : System.Array.Empty<string>();
            if (stagedFiles.Length == 0) return new GitCommitResult { Committed = false, SavedModelFirst = saved, Note = "Nothing to commit (working tree clean)." };
            var cr = await GitCli.RunAsync(dir, "commit", "-m", message);
            if (!cr.Ok) return new GitCommitResult { Error = GitCli.Combine(cr), SavedModelFirst = saved };
            return new GitCommitResult { Committed = true, Hash = await GitCli.HeadHashAsync(dir), Message = message, Files = stagedFiles, SavedModelFirst = saved };
        }

        public async Task<GitActionResult> GitBranchAsync(string name, bool create, bool checkout, string origin)
        {
            var guard = checkout ? await GitStateChangeGuardAsync() : (null, null, null);
            if (guard.Error != null) return GitActionError(guard.Error);
            using var stateLease = guard.Lease;
            var context = await GitActionContextAsync(refuseUnsaved: checkout, guard.Session);
            if (context.Error != null) return GitActionError(context.Error);

            if (string.IsNullOrWhiteSpace(name))
            {
                if (create || checkout) return GitActionError("A branch name is required when create or checkout is selected.");
                try
                {
                    var listed = await GitCli.RunAsync(context.Dir, "branch", "--format=%(refname:short)");
                    return new GitActionResult
                    {
                        Ok = listed.Ok,
                        Output = GitCli.Combine(listed),
                        Error = listed.Ok ? null : GitCli.Combine(listed),
                        Branch = listed.Ok ? await GitCurrentBranchAsync(context.Dir) : null,
                    };
                }
                catch (Exception ex) { return GitActionError("Git branch failed: " + GitCli.Scrub(ex.Message)); }
            }

            name = name.Trim();
            if (!create && !checkout) return GitActionError("Set create=true, checkout=true, or omit the name to list branches.");
            var unsafeName = GitRefInputError(name, create ? "branch name" : "git ref");
            if (unsafeName != null) return GitActionError(unsafeName);

            try
            {
                if (create)
                {
                    var valid = await GitCli.RunAsync(context.Dir, "check-ref-format", "--branch", name);
                    if (!valid.Ok) return GitActionError("That branch name is not valid: " + GitCli.Combine(valid));
                }
                else if (checkout && !await GitCommitRefExistsAsync(context.Dir, name))
                    return GitActionError("That git ref does not resolve to a commit or branch.");

                var before = checkout ? ModelDiskStamp() : null;
                if (checkout) await InvokeGitStateChangeHookForTestAsync();
                GitCli.GitRun r;
                if (create && checkout) r = await GitCli.RunAsync(context.Dir, "checkout", "-b", name);
                else if (create) r = await GitCli.RunAsync(context.Dir, "branch", name);
                else r = await GitCli.RunAsync(context.Dir, "checkout", name);
                if (r.Ok && checkout) await InvokeGitStateChangedHookForTestAsync();
                var changed = r.Ok && checkout && ModelDiskChanged(before);
                return new GitActionResult
                {
                    Ok = r.Ok,
                    Output = GitCli.Combine(r),
                    Error = r.Ok ? null : GitCli.Combine(r),
                    Branch = r.Ok ? await GitCurrentBranchAsync(context.Dir) : null,
                    ModelPath = changed ? _sessions.Current?.SourcePath : null,
                    ModelReloadNeeded = changed,
                };
            }
            catch (Exception ex) { return GitActionError("Git branch failed: " + GitCli.Scrub(ex.Message)); }
        }

        public async Task<GitActionResult> GitCheckoutAsync(string @ref, string origin)
        {
            var guard = await GitStateChangeGuardAsync();
            if (guard.Error != null) return GitActionError(guard.Error);
            using var stateLease = guard.Lease;
            var context = await GitActionContextAsync(refuseUnsaved: true, guard.Session);
            if (context.Error != null) return GitActionError(context.Error);
            @ref = @ref?.Trim();
            var unsafeRef = GitRefInputError(@ref, "git ref");
            if (unsafeRef != null) return GitActionError(unsafeRef);
            var before = ModelDiskStamp();
            try
            {
                if (!await GitCommitRefExistsAsync(context.Dir, @ref))
                    return GitActionError("That git ref does not resolve to a commit or branch.");
                await InvokeGitStateChangeHookForTestAsync();
                var r = await GitCli.RunAsync(context.Dir, "checkout", @ref);
                if (r.Ok) await InvokeGitStateChangedHookForTestAsync();
                var changed = r.Ok && ModelDiskChanged(before);
                return new GitActionResult
                {
                    Ok = r.Ok,
                    Output = GitCli.Combine(r),
                    Error = r.Ok ? null : GitCli.Combine(r),
                    Branch = r.Ok ? await GitCurrentBranchAsync(context.Dir) : null,
                    ModelPath = changed ? _sessions.Current?.SourcePath : null,
                    ModelReloadNeeded = changed,
                };
            }
            catch (Exception ex) { return GitActionError("Git checkout failed: " + GitCli.Scrub(ex.Message)); }
        }

        public async Task<GitActionResult> GitPullAsync(string origin)
        {
            var guard = await GitStateChangeGuardAsync();
            if (guard.Error != null) return GitActionError(guard.Error);
            using var stateLease = guard.Lease;
            var context = await GitActionContextAsync(refuseUnsaved: true, guard.Session);
            if (context.Error != null) return GitActionError(context.Error);
            var before = ModelDiskStamp();
            try
            {
                await InvokeGitStateChangeHookForTestAsync();
                var r = await GitCli.RunAsync(context.Dir, "pull", "--ff-only");
                if (r.Ok) await InvokeGitStateChangedHookForTestAsync();
                var changed = r.Ok && ModelDiskChanged(before);
                return new GitActionResult
                {
                    Ok = r.Ok,
                    Output = GitCli.Combine(r),
                    Error = r.Ok ? null : GitCli.Combine(r),
                    Branch = r.Ok ? await GitCurrentBranchAsync(context.Dir) : null,
                    ModelPath = changed ? _sessions.Current?.SourcePath : null,
                    ModelReloadNeeded = changed,
                };
            }
            catch (Exception ex) { return GitActionError("Git pull failed: " + GitCli.Scrub(ex.Message)); }
        }

        public async Task<GitActionResult> GitPushAsync(string remote, string branch, bool confirm, string origin)
        {
            var dir = GitWorkingDir();
            if (!confirm)
            {
                var st = await GitCli.StatusAsync(dir);
                return new GitActionResult { Ok = true, Output = $"Preview — would push {st.Ahead} commit(s) on '{st.Branch}'" + (st.Upstream != null ? $" to {st.Upstream}" : "") + ". Pass confirm=true to push." };
            }
            var args = new System.Collections.Generic.List<string> { "push" };
            if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch);
            var r = await GitCli.RunAsync(dir, args.ToArray());
            return new GitActionResult { Ok = r.Ok, Output = GitCli.Combine(r), Error = r.Ok ? null : GitCli.Combine(r) };
        }

        public async Task<GitActionResult> GitCloneAsync(string url, string directory, string origin)
        {
            if (string.IsNullOrWhiteSpace(url)) return new GitActionResult { Ok = false, Error = "A repository URL is required." };
            if (string.IsNullOrWhiteSpace(directory)) return new GitActionResult { Ok = false, Error = "A target directory is required." };
            url = url.Trim();
            if (url.StartsWith("-", StringComparison.Ordinal) || url.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return GitActionError("The repository URL is not valid.");

            var target = GitCloneTarget(directory);
            if (target.Error != null) return GitActionError(target.Error);
            var stagingParent = target.Anchor ?? target.Parent;
            var staging = Path.Combine(stagingParent, ".semanticus-clone-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (target.Anchor != null && HasLinkedCloneParent(target.Anchor, target.Parent))
                    return GitActionError("A relative clone target cannot pass through a linked workspace directory.");

                var r = await GitCli.RunAsync(stagingParent, "clone", "--", url, staging);
                if (!r.Ok)
                {
                    DeleteCloneStaging(staging);
                    return GitActionError(GitCli.Combine(r));
                }

                // Inspect while the clone is still private. Nothing is published at the requested path until every
                // post-clone read has succeeded, so a failed inspection cannot leave a partial target behind.
                var stagedModel = LocateModelInRepo(staging);
                var modelRelative = CloneModelRelativePath(staging, stagedModel);
                var branch = await GitCurrentBranchAsync(staging);
                if (target.Anchor != null && HasLinkedCloneParent(target.Anchor, target.Parent))
                {
                    DeleteCloneStaging(staging);
                    return GitActionError("The relative clone target became linked before publication. Nothing was published.");
                }
                Directory.Move(staging, target.Full);
                var model = modelRelative == null ? null : Path.GetFullPath(modelRelative, target.Full);
                return new GitActionResult
                {
                    Ok = true,
                    Branch = branch,
                    ModelPath = model,
                    Output = model != null
                        ? "Cloned. Open the model at: " + model
                        : "Cloned to " + target.Full + ". No semantic model folder was detected. Open the model path manually.",
                };
            }
            catch (Exception ex)
            {
                DeleteCloneStaging(staging);
                return GitActionError("Git clone failed: " + GitCli.Scrub(ex.Message));
            }
        }

        internal Func<Task> GitStateChangeReadyForTest;
        internal Func<Task> GitStateChangedForTest;

        private async Task<(Session Session, IDisposable Lease, string Error)> GitStateChangeGuardAsync()
        {
            Session session = null;
            var lifecycleHeld = false;
            try
            {
                await _lifecycleGate.WaitAsync();
                lifecycleHeld = true;
                session = _sessions.Current;
                if (session == null)
                    return (null, null, "Open or save a model on disk first. Source control operates on the open model's folder.");
                var sessionLease = await session.AcquireSourceControlStateLeaseAsync();
                var lease = new GitStateChangeLease(sessionLease, _lifecycleGate);
                lifecycleHeld = false; // the composite lease now owns both releases
                return (session, lease, null);
            }
            catch (Exception ex) { return (session, null, GitCli.Scrub(ex.Message)); }
            finally { if (lifecycleHeld) _lifecycleGate.Release(); }
        }

        private sealed class GitStateChangeLease : IDisposable
        {
            private IDisposable _sessionLease;
            private System.Threading.SemaphoreSlim _lifecycleGate;
            internal GitStateChangeLease(IDisposable sessionLease, System.Threading.SemaphoreSlim lifecycleGate)
            {
                _sessionLease = sessionLease;
                _lifecycleGate = lifecycleGate;
            }
            public void Dispose()
            {
                System.Threading.Interlocked.Exchange(ref _sessionLease, null)?.Dispose();
                System.Threading.Interlocked.Exchange(ref _lifecycleGate, null)?.Release();
            }
        }

        private async Task InvokeGitStateChangeHookForTestAsync()
        {
            var hook = System.Threading.Interlocked.Exchange(ref GitStateChangeReadyForTest, null);
            if (hook != null) await hook();
        }

        private async Task InvokeGitStateChangedHookForTestAsync()
        {
            var hook = System.Threading.Interlocked.Exchange(ref GitStateChangedForTest, null);
            if (hook != null) await hook();
        }

        private async Task<(string Dir, string Error)> GitActionContextAsync(bool refuseUnsaved, Session expectedSession = null)
        {
            var current = _sessions.Current;
            var session = expectedSession ?? current;
            if (expectedSession != null && !ReferenceEquals(current, expectedSession))
                return (null, "The open model changed while source control was preparing. Retry against the current model.");
            var dir = GitWorkingDirOrNull(session);
            if (session == null || string.IsNullOrWhiteSpace(dir))
                return (null, "Open or save a model on disk first. Source control operates on the open model's folder.");
            if (refuseUnsaved && session.HasUnsavedChanges)
                return (null, "Save or undo the open model's unsaved edits before switching source-control state.");
            if (!Directory.Exists(dir)) return (null, "The open model's folder no longer exists on disk.");
            try
            {
                if (!await GitCli.IsRepoAsync(dir)) return (null, "The open model is not inside a git repository.");
                return (dir, null);
            }
            catch (Exception ex) { return (null, "Git could not inspect the repository: " + GitCli.Scrub(ex.Message)); }
        }

        private static GitActionResult GitActionError(string error)
            => new GitActionResult { Ok = false, Error = error, Output = error };

        private static string GitRefInputError(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return "A " + label + " is required.";
            if (value.StartsWith("-", StringComparison.Ordinal) || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return "The " + label + " is not valid.";
            return null;
        }

        private static async Task<string> GitCurrentBranchAsync(string dir)
        {
            var r = await GitCli.RunAsync(dir, "branch", "--show-current");
            return r.Ok && !string.IsNullOrWhiteSpace(r.Stdout) ? r.Stdout.Trim() : null;
        }

        private static async Task<bool> GitCommitRefExistsAsync(string dir, string @ref)
        {
            var r = await GitCli.RunAsync(dir, "rev-parse", "--verify", "--quiet", @ref + "^{commit}");
            return r.Ok && !string.IsNullOrWhiteSpace(r.Stdout);
        }

        private string ModelDiskStamp()
        {
            var source = _sessions.Current?.SourcePath;
            if (string.IsNullOrWhiteSpace(source)) return null;
            try
            {
                var full = Path.GetFullPath(source);
                string[] files;
                if (File.Exists(full)) files = new[] { full };
                else if (Directory.Exists(full))
                {
                    if (ModelPathResolver.IsTmdlRoot(full))
                        files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmd", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    else if (File.Exists(Path.Combine(full, "model.bim"))) files = new[] { Path.Combine(full, "model.bim") };
                    else files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                        .Where(f => !IsEngineOrGitSidecar(Path.GetRelativePath(full, f)))
                        .ToArray();
                }
                else return null;

                var ordered = files.Select(f => (Full: f, Relative: File.Exists(full) ? Path.GetFileName(full) : Path.GetRelativePath(full, f).Replace('\\', '/')))
                    .OrderBy(x => x.Relative, StringComparer.Ordinal).ToArray();
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var separator = new byte[] { 0 };
                var buffer = new byte[64 * 1024];
                foreach (var file in ordered)
                {
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.Relative));
                    hash.AppendData(separator);
                    try
                    {
                        hash.AppendData(new byte[] { 1 });
                        using var stream = File.OpenRead(file.Full);
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // A stable unreadable file must not turn a no-op checkout into a false reload. Git cannot
                        // safely replace a locked file on Windows; metadata still detects the usual replace case.
                        hash.AppendData(new byte[] { 2 });
                        try
                        {
                            var info = new FileInfo(file.Full);
                            info.Refresh();
                            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{info.Exists}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{(int)info.Attributes}"));
                        }
                        catch { hash.AppendData(System.Text.Encoding.UTF8.GetBytes("unreadable")); }
                    }
                    hash.AppendData(separator);
                }
                return Convert.ToHexString(hash.GetHashAndReset());
            }
            catch { return null; }
        }

        private bool ModelDiskChanged(string before)
        {
            var after = ModelDiskStamp();
            return before == null || after == null || !string.Equals(before, after, StringComparison.Ordinal);
        }

        private static bool IsEngineOrGitSidecar(string relative)
        {
            var first = relative.Replace('\\', '/').Split('/')[0];
            return string.Equals(first, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, ".semanticus", StringComparison.OrdinalIgnoreCase);
        }

        private (string Full, string Parent, string Anchor, string Error) GitCloneTarget(string directory)
        {
            try
            {
                string full;
                string anchor = null;
                if (Path.IsPathFullyQualified(directory)) full = Path.GetFullPath(directory);
                else
                {
                    anchor = !string.IsNullOrWhiteSpace(_workspaceDir) ? Path.GetFullPath(_workspaceDir) : GitWorkingDirOrNull();
                    if (string.IsNullOrWhiteSpace(anchor))
                        return (null, null, null, "Use an absolute target directory when no workspace or disk-backed model is available.");
                    anchor = Path.GetFullPath(anchor);
                    full = Path.GetFullPath(directory, anchor);
                    var relative = Path.GetRelativePath(anchor, full);
                    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        return (null, null, null, "A relative clone target must stay inside the current workspace.");
                }

                var parent = Path.GetDirectoryName(full);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    return (null, null, null, "The clone target's parent directory does not exist.");
                if (anchor != null && HasLinkedCloneParent(anchor, parent))
                    return (null, null, null, "A relative clone target cannot pass through a linked workspace directory.");
                if (Directory.Exists(full) || File.Exists(full))
                    return (null, null, null, "The clone target already exists. Choose a new empty path.");
                return (full, parent, anchor, null);
            }
            catch (Exception ex) { return (null, null, null, "The clone target is not valid: " + GitCli.Scrub(ex.Message)); }
        }

        private static bool HasLinkedCloneParent(string anchor, string parent)
        {
            if ((File.GetAttributes(anchor) & FileAttributes.ReparsePoint) != 0) return true;
            var relative = Path.GetRelativePath(anchor, parent);
            if (relative == ".") return false;
            var current = anchor;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }

        private static void DeleteCloneStaging(string staging)
        {
            try
            {
                if (Directory.Exists(staging) && Path.GetFileName(staging).StartsWith(".semanticus-clone-", StringComparison.Ordinal))
                    Directory.Delete(staging, recursive: true);
            }
            catch { }
        }

        // A repository may contain a symlink/reparse point that makes a lexically nested model resolve outside the
        // private clone. Validate both the reported model path and the storage the model loader will actually read
        // before publication; a PBIP item folder is not sufficient containment when definition/ is itself linked.
        internal static string CloneModelRelativePath(string staging, string modelPath)
        {
            if (modelPath == null) return null;
            var root = Path.GetFullPath(staging);
            var full = Path.GetFullPath(modelPath);
            ValidateClonePath(root, full);
            ValidateCloneModelStorage(root, full);
            return Path.GetRelativePath(root, full);
        }

        private static void ValidateCloneModelStorage(string root, string modelPath)
        {
            if (File.Exists(modelPath)) return; // ValidateClonePath already covered the concrete .bim file.

            var definition = Path.Combine(modelPath, "definition");
            if (Directory.Exists(definition))
            {
                ValidateCloneTree(root, definition);
                return;
            }

            var modelBim = Path.Combine(modelPath, "model.bim");
            if (File.Exists(modelBim))
            {
                ValidateClonePath(root, modelBim);
                return;
            }

            // A bare TMDL model is rooted beside model.tmdl. Reject links anywhere in that model tree, while
            // ignoring repository and engine sidecars that the TMDL loader does not consume.
            if (File.Exists(Path.Combine(modelPath, "model.tmdl"))) ValidateCloneTree(root, modelPath, skipSidecars: true);
        }

        private static void ValidateCloneTree(string root, string treeRoot, bool skipSidecars = false)
        {
            ValidateClonePath(root, treeRoot);
            var pending = new Stack<string>();
            pending.Push(treeRoot);
            while (pending.Count > 0)
            {
                var currentDirectory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
                {
                    if (skipSidecars && string.Equals(currentDirectory, treeRoot, StringComparison.OrdinalIgnoreCase)
                        && IsEngineOrGitSidecar(Path.GetFileName(entry))) continue;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The detected semantic model passes through a linked repository path. Nothing was published.");
                    ValidateClonePath(root, entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }

        private static void ValidateClonePath(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
                throw new InvalidOperationException("The detected semantic model resolves outside the cloned repository. Nothing was published.");

            var current = root;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The detected semantic model passes through a linked repository path. Nothing was published.");
            }
        }

        // Find a model to open inside a cloned repo: a *.SemanticModel item folder, a bare TMDL folder, or a .bim.
        private static string LocateModelInRepo(string root)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var d in System.IO.Directory.EnumerateDirectories(root, "*.SemanticModel", options))
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(d, "definition")) || System.IO.File.Exists(System.IO.Path.Combine(d, "model.bim"))) return d;
                foreach (var f in System.IO.Directory.EnumerateFiles(root, "model.tmdl", options)) return System.IO.Path.GetDirectoryName(f);
                foreach (var f in System.IO.Directory.EnumerateFiles(root, "*.bim", options)) return f;
            }
            catch { }
            return null;
        }

        // ---- Model compare (ALM Toolkit / BISM Normalizer grade) + the deploy gate -----------------------------
        // Resolve a ModelRef to a raw TOM Database (NOT the wrapper — avoids clobbering the session's wrapper
        // Singleton). session = snapshot the open edits to a temp .bim; file = load from disk; gitref = check the
        // model out at a commit via `git worktree`.
        // origin is REQUIRED (no default) so no caller can silently let an agent look human: the workspace branch's
        // interactive sign-in is gated on it, and DeployGuard.IsAgent treats null/empty/unknown as an agent (fail-closed).
        private async Task<(RawTom.Database db, string label, Action cleanup)> ResolveModelRefAsync(ModelRef r, string origin)
        {
            var kind = (r?.Kind ?? "session").Trim().ToLowerInvariant();
            if (kind == "session")
            {
                // Snapshot the open edits to a temp TMDL folder (NOT a .bim) so a session-vs-disk/gitref compare is
                // TMDL-vs-TMDL — the same serializer both sides, which avoids false "updates" from format asymmetry.
                var s = _sessions.Require();
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus_cmp_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
                var sessionCompatibilityLevel = await s.Dispatcher.RunAsync(() =>
                {
                    // Capture the authoritative value before writing: model-only TMDL may omit database metadata.
                    var level = s.TomDatabase.CompatibilityLevel;
                    s.Save(tmp, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                    return level;
                });
                Action sclean = () => { try { System.IO.Directory.Delete(tmp, true); } catch { } };
                try { return (ModelCompare.LoadRawModelDb(tmp, sessionCompatibilityLevel), r?.Label ?? "working copy (open model)", sclean); }
                catch { sclean(); throw; }   // a load failure after Save must not orphan the temp snapshot (mirror gitref/workspace)
            }
            if (kind == "file")
            {
                if (string.IsNullOrWhiteSpace(r.Path)) throw new InvalidOperationException("A 'file' model ref needs a path.");
                return (ModelCompare.LoadRawModelDb(r.Path), r?.Label ?? r.Path, () => { });
            }
            if (kind == "gitref")
            {
                if (string.IsNullOrWhiteSpace(r.GitRef)) throw new InvalidOperationException("A 'gitref' model ref needs a gitRef (commit / branch / tag).");
                var dir = GitWorkingDir();
                var root = await GitCli.RepoRootAsync(dir) ?? throw new InvalidOperationException("Not a git repository.");
                var wt = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus_wt_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
                var add = await GitCli.RunAsync(dir, "worktree", "add", "--detach", wt, r.GitRef);
                if (!add.Ok) throw new InvalidOperationException("git worktree failed: " + GitCli.Combine(add));
                Action cleanup = () => { try { GitCli.RunAsync(dir, "worktree", "remove", "--force", wt).GetAwaiter().GetResult(); } catch { } try { System.IO.Directory.Delete(wt, true); } catch { } };
                try
                {
                    // Resolve relative to the ACTUAL source path (a TMDL folder OR a bare-named .bim FILE) so the
                    // worktree copy keeps the filename — using just the directory loses a `Foo.bim` name.
                    var src = _sessions.Current?.SourcePath;
                    var srcFull = string.IsNullOrEmpty(src) ? dir : System.IO.Path.GetFullPath(src);
                    var rel = System.IO.Path.GetRelativePath(root, srcFull);
                    var modelInWt = System.IO.Path.Combine(wt, rel);
                    if (!System.IO.Directory.Exists(modelInWt) && !System.IO.File.Exists(modelInWt)) modelInWt = LocateModelInRepo(wt) ?? modelInWt;
                    var db = ModelCompare.LoadRawModelDb(modelInWt);
                    return (db, r?.Label ?? ("git:" + r.GitRef), cleanup);
                }
                catch { cleanup(); throw; }
            }
            if (kind == "workspace")
            {
                // A PUBLISHED model on an XMLA endpoint (Power BI / Fabric / AAS): acquire an Entra token, snapshot the
                // deployed metadata to a temp .bim (read-only — never writes back), then load it as raw TOM like a file.
                // This is what lets Compare diff / cherry_pick / deploy run against a live published model.
                if (string.IsNullOrWhiteSpace(r.Endpoint)) throw new InvalidOperationException("A 'workspace' model ref needs an XMLA endpoint (e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace).");
                // Auth mirrors connect_xmla/open_live: reuse the persistent encrypted sign-in cache (silent when a saved
                // sign-in exists) and, on a COLD auth rejection, sign a HUMAN in interactively and retry once — an AGENT
                // never pops UI (it gets a teaching refusal). See SnapshotWorkspaceMetadataAsync.
                var (snap, _) = await SnapshotWorkspaceMetadataAsync(r.Endpoint, r.Database, r.AuthMode, origin);   // compare reads only the snapshot; the write path (apply/rollback) is what reuses the effective mode
                var snapDir = System.IO.Path.GetDirectoryName(snap.BimPath);
                Action wclean = () => { try { System.IO.Directory.Delete(snapDir, true); } catch { } };
                try { return (ModelCompare.LoadRawModelDb(snap.BimPath), r?.Label ?? (string.IsNullOrWhiteSpace(r.Database) ? r.Endpoint : r.Database), wclean); }
                catch { wclean(); throw; }   // a load failure must not orphan the live snapshot dir (mirror gitref)
            }
            throw new InvalidOperationException($"Unsupported model ref kind '{kind}' — use session | file | gitref | workspace.");
        }

        // origin steers the workspace-target sign-in fallback ONLY: a "human" compare may open an interactive sign-in;
        // an agent-origin compare against a not-signed-in workspace gets a teaching refusal instead of popping UI.
        public async Task<ModelDiff> CompareModelsAsync(ModelRef left, ModelRef right, bool includeEqual = false, string origin = "human")
        {
            var (ldb, llabel, lclean) = await ResolveModelRefAsync(left ?? new ModelRef { Kind = "session" }, origin);
            try
            {
                var (rdb, rlabel, rclean) = await ResolveModelRefAsync(right, origin);
                try { return ModelCompare.Diff(ldb.Model, rdb.Model, llabel, rlabel, includeEqual); }
                finally { rclean(); }
            }
            finally { lclean(); }
        }

        public async Task<ApplyDiffResult> ApplyDiffAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason = null)
        {
            var rkind = (right?.Kind ?? "").Trim().ToLowerInvariant();
            if (rkind == "session") return await ApplyDiffIntoSessionAsync(left, selectedRefs, commit, origin);   // undoable merge into the open model
            if (rkind == "workspace") return await ApplyDiffToWorkspaceAsync(left, right, selectedRefs, commit, origin, overrideReason);   // selective push to a published model
            if (rkind == "gitref")
                return new ApplyDiffResult { Error = "apply_diff can't target a 'gitref' — a git ref is history, not a writable target. Check it out (git_checkout), merge the change into the working tree, then git_commit." };
            if (rkind != "file")
                return new ApplyDiffResult { Error = "apply_diff target must be 'file' (write to disk), 'session' (merge into the open model), or 'workspace' (push to a published XMLA model)." };
            if (string.IsNullOrWhiteSpace(right.Path)) return new ApplyDiffResult { Error = "The target 'file' ref needs a path." };
            var (ldb, llabel, lclean) = await ResolveModelRefAsync(left ?? new ModelRef { Kind = "session" }, origin);
            try
            {
                var rdb = ModelCompare.LoadRawModelDb(right.Path);
                var diff = ModelCompare.Diff(ldb.Model, rdb.Model, llabel, right.Label ?? right.Path);
                var selected = selectedRefs != null && selectedRefs.Length > 0 ? new System.Collections.Generic.HashSet<string>(selectedRefs) : null;
                var applicable = diff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref))).Select(i => i.Ref).ToArray();
                if (!commit)
                    return new ApplyDiffResult { Applied = false, Count = applicable.Length, AppliedRefs = applicable, Target = right.Path, Note = "Preview — pass commit=true to write the selected change(s) into the target file." };

                // Pro gate — same rule as the session path (:3162): merging MULTIPLE objects in one commit is the
                // bulk/atomic primitive. Without this, bulk merge-to-file was a free route around the session gate
                // (write the file, reopen it). A single-ref commit stays free; the commit=false preview above is
                // read-only + free. Thrown before Apply touches the in-memory target, so a refusal writes nothing.
                if (applicable.Length > 1)
                    Entitlement.EntitlementGuard.RequirePro(_entitlement, "Merging multiple objects into a model file at once",
                        "Merge one object at a time (pass a single ref in selectedRefs); previewing all changes stays free.");

                var outcome = ModelCompare.Apply(ldb.Model, rdb.Model, diff, selected);
                var failedRefs = outcome.Failed.Select(f => f.Ref).ToArray();
                // Validate the MERGED model in memory BEFORE touching the target file: a corrupt merge (e.g. an
                // orphaned reference from a partial selection) throws here, and we refuse to write — so a
                // partial/invalid model is never persisted over the target.
                try { _ = RawTom.JsonSerializer.SerializeDatabase(rdb); }
                catch (Exception vex)
                {
                    var m = vex.Message; if (m.Length > 300) m = m.Substring(0, 300) + "…";
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = outcome.Applied.ToArray(), FailedRefs = failedRefs, Target = right.Path,
                        Error = "The merge produced an invalid model (" + m + ") — the target was NOT written. Re-run with a selection whose dependencies are included." };
                }
                ModelCompare.SaveRawModel(rdb, right.Path);
                var note = $"Applied {outcome.Applied.Count} change(s) into {right.Path}.";
                if (outcome.Failed.Count > 0) note += $" {outcome.Failed.Count} could not be applied: " + string.Join("; ", outcome.Failed.Select(f => f.Ref + " (" + f.Reason + ")"));
                return new ApplyDiffResult { Applied = true, Count = outcome.Applied.Count, AppliedRefs = outcome.Applied.ToArray(), FailedRefs = failedRefs, Target = right.Path, Note = note };
            }
            finally { lclean(); }
        }

        // apply_diff into the LIVE session (undoable): merge the selected diff items (source = left) INTO the open
        // model — Create/Update copy the source object in via the wrapper API, Delete removes the open-model object —
        // as ONE MutateAsync (one undo step). Reuses the same wrapper-API copy path as cherry_pick (so it's
        // change-tracked + undoable, unlike a raw-TOM apply). Unsupported types surface in FailedRefs, never dropped.
        private async Task<ApplyDiffResult> ApplyDiffIntoSessionAsync(ModelRef left, string[] selectedRefs, bool commit, string origin)
        {
            var (ldb, llabel, lclean) = await ResolveModelRefAsync(left ?? new ModelRef { Kind = "session" }, origin);
            try
            {
                var (rdb, _, rclean) = await ResolveModelRefAsync(new ModelRef { Kind = "session" }, origin);   // point-in-time snapshot of the open model
                ModelDiff diff;
                try { diff = ModelCompare.Diff(ldb.Model, rdb.Model, llabel, "open model"); }
                finally { rclean(); }
                var selected = selectedRefs != null && selectedRefs.Length > 0 ? new System.Collections.Generic.HashSet<string>(selectedRefs) : null;
                var items = diff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref)))
                    // copies before deletes; within copies parents (table-level) first, within deletes children (table-bearing) first
                    .OrderBy(i => i.Action == "Delete" ? 1 : 0)
                    .ThenBy(i => i.Action == "Delete" ? (string.IsNullOrEmpty(i.Table) ? 1 : 0) : (string.IsNullOrEmpty(i.Table) ? 0 : 1)).ToList();
                var s = _sessions.Require();
                if (!commit)
                {
                    var pv = await s.ReadAsync(m =>
                    {
                        var ok = new System.Collections.Generic.List<string>(); var fail = new System.Collections.Generic.List<string>();
                        foreach (var it in items)
                        {
                            var reason = it.Action == "Delete" ? PreviewDelete(m, it.ObjectType.ToLowerInvariant(), it.Table, it.Name)
                                                               : PreviewCopy(m, ldb.Model, it.ObjectType.ToLowerInvariant(), it.Table, it.Name).reason;
                            if (reason == null) ok.Add(it.Ref); else fail.Add(it.Ref + " (" + reason + ")");
                        }
                        return (ok, fail);
                    });
                    return new ApplyDiffResult { Applied = false, Count = pv.ok.Count, AppliedRefs = pv.ok.ToArray(), FailedRefs = pv.fail.ToArray(), Target = "open model",
                        Note = $"Preview — {pv.ok.Count} change(s) would merge into the open model (undoable)" + (pv.fail.Count > 0 ? $"; {pv.fail.Count} cannot" : "") + ". Pass commit=true to apply." };
                }
                // Pro gate: merging MULTIPLE objects into the open model in one undoable batch is the bulk/atomic
                // primitive (the Pro value); a single-ref merge stays free, and the commit=false preview above is
                // read-only + free. Thrown before the mutate, so a refusal leaves the model intact.
                if (items.Count > 1)
                    Entitlement.EntitlementGuard.RequirePro(_entitlement, "Merging multiple objects into the open model at once",
                        "Merge one object at a time (pass a single ref in selectedRefs), or use the individual edit tools.");
                var applied = new System.Collections.Generic.List<string>(); var failed = new System.Collections.Generic.List<string>();
                await s.MutateAsync(origin, $"merge {items.Count} change(s) into the open model", m =>
                {
                    foreach (var it in items)
                    {
                        // Per-item try/catch INSIDE the lambda: a wrapper throw the preview can't foresee (e.g. a
                        // governed model where AllowCreate is false) must land in FailedRefs, NOT escape and trip
                        // MutateAsync's rollback — which would discard every item already merged in this batch.
                        try
                        {
                            var reason = it.Action == "Delete" ? DeleteFromSession(m, it.ObjectType.ToLowerInvariant(), it.Table, it.Name)
                                                               : CopyIntoSession(m, ldb.Model, it.ObjectType.ToLowerInvariant(), it.Table, it.Name);
                            if (reason == null) applied.Add(it.Ref); else failed.Add(it.Ref + " (" + reason + ")");
                        }
                        catch (Exception ex) { failed.Add(it.Ref + " (" + FabricRest.Scrub(ex.Message) + ")"); }
                    }
                });
                return new ApplyDiffResult { Applied = true, Count = applied.Count, AppliedRefs = applied.ToArray(), FailedRefs = failed.ToArray(), Target = "open model",
                    Note = $"Merged {applied.Count} change(s) into the open model." + (failed.Count > 0 ? $" {failed.Count} could not apply: " + string.Join("; ", failed) : "") };
            }
            finally { lclean(); }
        }

        // --- Test seams for the workspace (selective-push) target. Live XMLA is not exercised offline, so tests
        // inject in-memory snapshots (A then B, for the drift check) and a fake push — exercising the merge / validate
        // / drift / gate / audit legs WITHOUT a real endpoint. Null (production) = the real live path. ---
        internal Func<Task<RawTom.Database>> WorkspaceSnapshotHook;
        internal Func<string, System.Collections.Generic.IReadOnlyCollection<LiveDeleteTarget>, DeployReport> WorkspacePushHook;

        // apply_diff into a PUBLISHED model on an XMLA endpoint — the ALM-Toolkit-style selective push. Merge the
        // chosen diff items from `left` onto a snapshot of the live target, then push ONLY those objects. Preview + a
        // single-object push are FREE; a multi-object atomic push is Pro (IDENTICAL rule to the file/session paths).
        // Deployment itself is never surcharged. A drift guard runs for BOTH tiers (safety is never paywalled): if the
        // target changed under us between the diff and the commit, we REFUSE unless overrideReason is supplied (the
        // accountable override, recorded on the audit trail). Explicitly-selected Delete refs ARE pushed (real removals
        // over XMLA — ALM Toolkit / Tabular Editor do the same; absence still never deletes).
        private async Task<ApplyDiffResult> ApplyDiffToWorkspaceAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason)
        {
            if (string.IsNullOrWhiteSpace(right.Endpoint))
                return new ApplyDiffResult { Error = "The target 'workspace' ref needs an XMLA endpoint (e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace)." };
            if (string.IsNullOrWhiteSpace(right.Database))
                return new ApplyDiffResult { Error = "The target 'workspace' ref needs a database (dataset) name — a push must name its target dataset explicitly." };

            var cleanups = new System.Collections.Generic.List<Action>();
            void CleanupAll() { foreach (var c in cleanups) { try { c(); } catch { } } }
            var (ldb, llabel, lclean) = await ResolveModelRefAsync(left ?? new ModelRef { Kind = "session" }, origin);
            cleanups.Add(lclean);
            try
            {
                var authMode = string.IsNullOrWhiteSpace(right.AuthMode) ? "azcli" : right.AuthMode;
                var tlabel = right.Label ?? right.Database;

                // Snapshot the CURRENT deployed metadata (read-only), loaded as raw TOM like a file — "snapshot A".
                // Capture the EFFECTIVE auth mode it authenticated with, so the eventual push reuses that same proven
                // credential instead of re-acquiring the mode the endpoint already rejected (see PushWorkspaceAsync).
                var (adb, effectiveMode) = await SnapshotWorkspaceAsync(right.Endpoint, right.Database, authMode, cleanups, origin);

                var diff = ModelCompare.Diff(ldb.Model, adb.Model, llabel, tlabel);
                var selected = selectedRefs != null && selectedRefs.Length > 0 ? new System.Collections.Generic.HashSet<string>(selectedRefs) : null;
                var selectedItems = diff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref))).ToList();
                // RETAG/REPUBLISH: a selected item that exists on both sides under a DIFFERENT lineage tag — Match emits a
                // Delete + Create pair (sharing a name-based ref). Deleting/replacing would drop the LIVE object and its
                // data (usually the model was republished under us). REFUSED (both halves) — held OUT of the push pipeline
                // (so they never count toward the Pro gate / drift / apply) and reported. Deduped by ref.
                var republishDeleteRefs = selectedItems.Where(i => i.LikelyRepublished).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();
                string RepublishReason(string r) => r + " (refused as a likely republish — a same-named object exists on the target with a DIFFERENT lineage tag; deleting or replacing it would drop the live object and its data. This usually means the model was republished under you. Re-diff against the current model, or push the update instead.)";
                var applicableItems = selectedItems.Where(i => !i.LikelyRepublished).ToList();
                var applicable = applicableItems.Select(i => i.Ref).ToArray();
                var deleteRefs = applicableItems.Where(i => i.Action == "Delete").Select(i => i.Ref).ToArray();
                var pushRefs = applicableItems.Where(i => i.Action != "Delete").Select(i => i.Ref).ToArray();

                // The preview MUST disclose deletes distinctly — a destructive, irreversible act on a published model
                // must never be discovered after the fact.
                string DeleteNote() => deleteRefs.Length == 0 ? "" : $" {deleteRefs.Length} object(s) will be DELETED from the published model: {string.Join(", ", deleteRefs)}.";
                string RepublishNote() => republishDeleteRefs.Length == 0 ? "" : $" {republishDeleteRefs.Length} selected item(s) will be REFUSED as a likely republish (same name, different lineage tag — deleting would drop live data): {string.Join(", ", republishDeleteRefs)}.";

                if (!commit)
                {
                    var note = $"Preview — {pushRefs.Length} object(s) would be pushed to {right.Database} on {right.Endpoint}." + DeleteNote() + RepublishNote() + " Pass commit=true to push.";
                    return new ApplyDiffResult { Applied = false, Count = applicable.Length, AppliedRefs = applicable, FailedRefs = republishDeleteRefs, Target = tlabel, Note = note };
                }

                // NOTHING TO PUSH: the selection matched no pending difference the push can carry. If the ONLY selection
                // was a refused republish, say THAT (not "nothing to push"). Either way, no live write happened.
                if (applicable.Length == 0)
                {
                    if (republishDeleteRefs.Length > 0)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = republishDeleteRefs.Select(RepublishReason).ToArray(), Target = tlabel,
                            Note = "Nothing was pushed." + RepublishNote() };
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel,
                        Note = $"Nothing to push — the selection matched no pending difference between the source and {right.Database} on {right.Endpoint}." };
                }

                // Pro gate — IDENTICAL rule to file/session (applicable.Length > 1): the atomic multi-object push is the
                // bulk primitive; a single-object push stays free and the preview above is read-only + free. Deletes
                // gate the same as any object — no extra gate. Thrown before any snapshot mutation → a refusal writes nothing.
                if (applicable.Length > 1)
                    Entitlement.EntitlementGuard.RequirePro(_entitlement, "Pushing multiple objects to a published model at once",
                        "Push one object at a time (pass a single ref in selectedRefs); previewing all changes stays free.");

                // ---- Agent-permissions gate. Same governance the deploy_live hole exposed, on the selective-push path.
                // A push that DELETES escalates to the delete capability, so a policy can forbid an agent deleting from
                // prod even where it would permit an update. Runs before the drift snapshot → a refusal writes nothing.
                {
                    var cap = deleteRefs.Length > 0 ? AgentCapability.DeployDelete : AgentCapability.DeployLive;
                    var what = deleteRefs.Length > 0
                        ? $"push {applicable.Length} change(s) incl. {deleteRefs.Length} delete(s) to {right.Database} on {right.Endpoint}"
                        : $"push {applicable.Length} change(s) to {right.Database} on {right.Endpoint}";
                    // intentBasis = the exact ref set (deletes distinguished): the grant authorises THESE objects. An
                    // agent that re-plans to a different selection — same count, different objects — must re-ask.
                    var basis = "apply:" + string.Join("\n", applicable.OrderBy(r => r, StringComparer.Ordinal))
                        + "\n#del:" + string.Join("\n", deleteRefs.OrderBy(r => r, StringComparer.Ordinal));
                    var refusal = GuardAgent(cap, right.Endpoint, right.Database, origin, isCommit: true, summary: what, intentBasis: basis);
                    if (refusal != null)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel, Error = refusal };
                }

                // ---- Drift guard (ALWAYS ON, both tiers). Between the diff and this commit the live target can change
                // under us. Re-snapshot ("snapshot B" = the CURRENT live state) and compare each ref we're about to
                // overwrite (adds/updates AND deletes) A-vs-B, restricted to the SELECTED refs; any non-Equal ⇒ someone
                // edited a ref we intend to change since we diffed. Deletes are irreversible on a published model, so
                // they are guarded too. Snapshot A stays only the drift BASELINE — we no longer push it (see below).
                var (bdb, _) = await SnapshotWorkspaceAsync(right.Endpoint, right.Database, authMode, cleanups, origin);
                // The RESTORE POINT is snapshot B, and it must be captured HERE: ModelCompare.Apply below merges the
                // selected changes INTO bdb in place, so serializing it any later would persist the post-push state and
                // silently make rollback a no-op. Cheap (in-memory) and it also works under the offline test hook.
                var restoreJson = RawTom.JsonSerializer.SerializeDatabase(bdb);
                var applicableSet = new System.Collections.Generic.HashSet<string>(applicable, StringComparer.Ordinal);
                var driftRefs = ModelCompare.Diff(adb.Model, bdb.Model, "before", "now").Items
                    .Where(i => i.Action != "Equal" && applicableSet.Contains(i.Ref)).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();

                if (driftRefs.Length > 0 && string.IsNullOrWhiteSpace(overrideReason))
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel,
                        Error = "Drift guard: the target changed under you since the diff — " + string.Join(", ", driftRefs)
                            + " differ on the live model now. Nothing was pushed. Re-run to diff against the current state, or pass overrideReason to push anyway (the override is recorded in the audit trail)." };

                // CORRECTNESS: merge into snapshot B (the CURRENT live), NEVER the stale A. SyncSessionToLive diffs the
                // pushed model against the current live and syncs EVERY difference (it is selection-unaware), so pushing
                // A+selection would REVERT any UNSELECTED object a colleague changed since A — a silent lost update the
                // drift guard can't catch (it only inspects the selected refs). Merging into B makes "pushed =
                // current-live + selected changes" literally true, so SyncSessionToLive emits changes ONLY for the
                // selected objects and unrelated concurrent edits are PRESERVED — the per-object last-writer-wins policy
                // of docs/op-routing-map.md, with the drift guard as the explicit revision-guard on the objects we touch.
                // The A-diff drives the preview + the Pro gate count (what the user previewed is what they're gated on);
                // the B-diff drives what is actually applied.
                var bdiff = ModelCompare.Diff(ldb.Model, bdb.Model, llabel, tlabel);
                var bItems = bdiff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref))).ToList();
                // REFUSE retag/republish items (same name, different lineage tag ⇒ Match emits a Delete + Create pair
                // that SHARE a name-based ref). Both halves are excluded from the push: deleting would drop the live
                // object + its data, and the paired Create can't add a same-named object while the live one exists. The
                // safe move is to push an UPDATE or re-diff. Reported (deduped by ref) in FailedRefs.
                var republishRefsB = bItems.Where(i => i.LikelyRepublished).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();
                var deleteItemsB = bItems.Where(i => i.Action == "Delete" && !i.LikelyRepublished).ToList();
                var deleteRefsB = deleteItemsB.Select(i => i.Ref).ToArray();   // ref strings — for the preview/summary/evidence only
                // IDENTITY-carrying delete targets — what the live-delete channel actually resolves by (tag-terminal),
                // NOT the ref strings (a ref is for reporting, never a resolver). Captured from the B-diff items, whose
                // Target* identity was recorded at diff time, so the removal lands on the RIGHT object on the third live
                // state SyncSessionToLive loads — never a same-named impostor.
                var deleteTargetsB = deleteItemsB.Select(LiveDeleteTarget.FromDiffItem).ToArray();
                var pushRefsB = bItems.Where(i => i.Action != "Delete" && !i.LikelyRepublished).Select(i => i.Ref).ToArray();
                // A selected ref that is now Equal on B (a colleague made the same change, or it converged) has no
                // B-diff entry — it drops out as a NO-OP; report it, don't count it as applied.
                var bApplicableSet = new System.Collections.Generic.HashSet<string>(bItems.Select(i => i.Ref), StringComparer.Ordinal);
                var noOpRefs = applicable.Where(r => !bApplicableSet.Contains(r)).ToArray();

                // Merge the selected ADDS/UPDATES into snapshot B (deletes go via the explicit channel, never merged).
                // ModelCompare.Apply's contract is now null ⇒ all, EMPTY ⇒ none (the old "empty == apply-all" loaded gun
                // is gone), so a delete-only push (pushRefsB empty) with an empty applySelected would apply nothing
                // anyway. We still skip the call when there are no adds/updates — no point building the diff apply.
                var outcome = new ModelCompare.ApplyOutcome();
                if (pushRefsB.Length > 0)
                {
                    var applySelected = new System.Collections.Generic.HashSet<string>(pushRefsB, StringComparer.Ordinal);
                    outcome = ModelCompare.Apply(ldb.Model, bdb.Model, bdiff, applySelected);
                }
                var failed = new System.Collections.Generic.List<string>(outcome.Failed.Select(f => f.Ref + " (" + f.Reason + ")"));
                // Retag/republish items are refused with a clear, actionable reason (routed to FailedRefs). Never pushed.
                foreach (var r in republishRefsB) failed.Add(RepublishReason(r));

                // Validate the MERGED model in memory BEFORE any live write — the invariant the file branch holds: a
                // corrupt merge (an orphaned reference from a partial selection) throws here and we refuse to write.
                try { _ = RawTom.JsonSerializer.SerializeDatabase(bdb); }
                catch (Exception vex)
                {
                    var m = vex.Message; if (m.Length > 300) m = m.Substring(0, 300) + "…";
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = outcome.Applied.ToArray(), FailedRefs = failed.ToArray(), Target = tlabel,
                        Error = "The merge produced an invalid model (" + m + ") — the target was NOT written. Re-run with a selection whose dependencies are included." };
                }

                // Serialize the merged model (current-live + selected changes) to a temp .bim and push. SyncSessionToLive
                // now emits ADDS/UPDATES only for the selected objects (everything else already matches live); the
                // explicit delete refs remove exactly the ticked objects — all inside one SaveChanges.
                var bim = CreatePushStagingPath(cleanups);
                System.IO.File.WriteAllText(bim, RawTom.JsonSerializer.SerializeDatabase(bdb));

                // ---- Accountable drift override (recorded BEFORE the push). Kane ratified the override as ACCOUNTABLE:
                // you cannot ship past the drift guard without its reason on the audit trail. This MATCHES deploy_live,
                // which appends the override record BEFORE it deploys, awaited and un-swallowed, so a failed append means
                // nothing ships. The prior post-success record here could be silently dropped — no session, or the audit
                // write threw — leaving an override with no record. So: if an override is in play (driftRefs.Length > 0)
                // we require an open session to carry the trail and write the "overridden" record NOW; if there's no
                // session, or the record can't be written, we REFUSE the push rather than mutate production
                // un-accountably. (Trade-off vs the old "no phantom override": if the push then fails, a recorded-but-
                // unshipped override can exist — the same property deploy_live accepts. An unrecorded SHIPPED override is
                // the worse outcome, so we prefer this. The non-override "deployed" record still lands post-success below,
                // where a failed audit write can't misreport an already-succeeded push. A non-override push needs no
                // session — nothing accountable to record before it — and proceeds as before.)
                if (driftRefs.Length > 0)
                {
                    var osess = _sessions.Current;
                    if (osess == null)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                            Error = "Drift override refused: an accountable override needs an open session to record its reason on the audit trail, and none is open. Nothing was pushed. Open the model (open_live / open_local) and retry, or re-run to diff against the current state." };
                    try
                    {
                        await RecordVerifiedEditAsync(osess, new VerifiedEditRecord
                        {
                            SessionId = osess.Id, Revision = 0, Origin = origin, Op = "apply_model_diff",   // 0: a push is not a local model mutation
                            Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                            Summary = $"drift override — pushing {pushRefsB.Length + deleteRefsB.Length} selected change(s) to {right.Endpoint}/{right.Database} despite drift on: {string.Join(", ", driftRefs)}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { right.Endpoint, right.Database, drifted = driftRefs, willPush = pushRefsB, willDelete = deleteRefsB }),
                        });
                    }
                    catch (Exception ex)
                    {
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                            Error = "Drift override refused: its reason could not be recorded on the audit trail (" + FabricRest.Scrub(ex.Message) + ") — refusing to push an unrecorded override. Nothing was pushed." };
                    }
                }

                // ---- Pre-push RESTORE POINT. A live delete is PERMANENT: RemoveExplicit removes 11 object kinds and
                // SyncModels can only ever add back measures / calc columns / calc tables / named expressions, so a
                // relationship, role, perspective, hierarchy, partition, culture, datasource or data table is otherwise
                // gone for good. Kane's rule (2026-07-09): NO RESTORE POINT, NO DELETE — fail closed. A push with no
                // deletes is recoverable by re-pushing, so there a failed restore point is a warning, not a refusal.
                RestorePointRecord restorePoint = null;
                string restoreError = null;
                try
                {
                    restorePoint = RestorePointStore.Write(right.Endpoint, right.Database, restoreJson, "apply_model_diff",
                        $"{pushRefsB.Length} change(s), {deleteRefsB.Length} delete(s)", pushRefsB, deleteRefsB);
                }
                catch (Exception ex) { restoreError = FabricRest.Scrub(ex.Message); }

                if (restorePoint == null && deleteTargetsB.Length > 0)
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                        Error = "Delete refused: a restore point could not be written (" + (restoreError ?? "unknown error")
                            + "), and a delete on a published model cannot be undone without one. Nothing was pushed. Free some disk space under ~/.semanticus/restore and retry, or de-select the deletes to push the remaining changes." };

                var rep = await PushWorkspaceAsync(bim, right.Endpoint, right.Database, effectiveMode, deleteTargetsB);   // reuse the credential the snapshot proved (P2-5)
                // NOTHING committed (SaveChanges wrote nothing) AND an error ⇒ a true failure; never claim success
                // (surface it verbatim, incl. a rejected delete-of-a-table-with-dependents, which SaveChanges refuses
                // atomically). CRITICAL: this branches on rep.Committed, NOT rep.Error. SyncSessionToLive commits in TWO
                // steps — the metadata SaveChanges (sets Committed=true), then a SECOND SaveChanges that recalcs new
                // calc-tables. If the recalc fails, rep.Error is set BUT rep.Committed stays TRUE and the metadata is
                // ALREADY LIVE. Returning "nothing applied" there would understate a production mutation (the worst
                // tool-result-contract violation) and skip the audit record. So a partial success (Committed==true WITH
                // an Error) falls THROUGH to the normal reconciliation + audit below; its recalc warning is surfaced on
                // the result at the end. Only a genuinely-nothing-written failure returns here.
                if (rep != null && !rep.Committed && rep.Error != null)
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel, Error = rep.Error };

                // RECONCILE the local merge against what the live deploy ACTUALLY synced. outcome.Applied is only what
                // merged into the temp .bim; SyncSessionToLive carries a SUBSET of object types (e.g. relationships /
                // roles / perspectives aren't pushed by a metadata deploy) and reports others as Unmatched / Conflicts.
                // A merged ref counts as APPLIED only if the deploy synced it (rep.SyncedRefs) AND it didn't surface as
                // unsynced. Everything else the merge claimed moves to FailedRefs with the deploy's own reason — the
                // result must describe the LIVE model, never overstate success (docs/harness-engineering.md contract).
                var syncedSet = new System.Collections.Generic.HashSet<string>(rep?.SyncedRefs ?? Array.Empty<string>(), StringComparer.Ordinal);
                var unsyncedReports = (rep?.Unmatched ?? Array.Empty<string>()).Concat(rep?.Conflicts ?? Array.Empty<string>()).ToArray();
                var confirmed = new System.Collections.Generic.List<string>();
                foreach (var appliedRef in outcome.Applied)
                {
                    var reportEntry = unsyncedReports.FirstOrDefault(e => DeployEntryConcernsRef(e, appliedRef));
                    if (reportEntry != null)
                        failed.Add(appliedRef + " (live deploy did not sync it: " + reportEntry + ")");
                    else if (syncedSet.Contains(appliedRef))
                        confirmed.Add(appliedRef);
                    else
                        failed.Add(appliedRef + " (merged locally but the live deploy did not carry it — a metadata deploy doesn't push this object type; deploy it via TMDL/XMLA)");
                }

                // A REFUSED delete (the ticked object's identity is gone AND a different object now bears its name — the
                // live-delete channel refused to delete the wrong object) is surfaced as a Failed ref with a clear,
                // actionable reason. It is NOT an applied change and NOT a silent no-op.
                foreach (var rr in rep?.DeletesRefused ?? Array.Empty<string>())
                    failed.Add(rr + " (delete refused — the object's lineage identity no longer resolves on the live model and a different object now bears its name; re-diff before deleting)");

                var appliedCount = confirmed.Count + (rep?.Deleted ?? 0);
                var appliedRefs = confirmed.Concat(rep?.DeletedRefs ?? Array.Empty<string>()).ToArray();

                // Outcome record for a NON-override push (Verdict=deployed). The override's accountable "overridden"
                // record was written BEFORE the push (above); this post-success record is SKIPPED for the override path,
                // so a failed override push leaves no phantom "deployed". A failed audit write here can't misreport the
                // push — it already succeeded — so it's swallowed. Evidence records what CONFIRMED-synced, not what
                // merely merged locally.
                var sess = _sessions.Current;
                if (driftRefs.Length == 0 && sess != null && rep != null && rep.Committed)
                    try
                    {
                        await RecordVerifiedEditAsync(sess, new VerifiedEditRecord
                        {
                            SessionId = sess.Id, Revision = 0, Origin = origin, Op = "apply_model_diff",   // 0: a push is not a local model mutation
                            Verdict = "deployed",
                            Summary = $"pushed {rep.TotalChanges} change(s) to {right.Endpoint}/{right.Database}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { right.Endpoint, right.Database, rep.TotalChanges, pushed = confirmed, deleted = rep.DeletedRefs, deletesRefused = rep.DeletesRefused, noOp = noOpRefs, notSynced = failed }),
                        });
                    }
                    catch { /* the push succeeded; a failed audit write must not misreport it */ }

                // APPLIED is truthful — tied to the deploy: either it committed real change(s), or (nothing was left to
                // change AND nothing failed) it's a clean, verified no-op. A push where everything the merge claimed
                // failed to reach live is NOT a success.
                var applied = appliedCount > 0 ? (rep?.Committed == true) : failed.Count == 0;
                string noteOut;
                if (appliedCount > 0)
                    noteOut = $"Pushed {appliedCount} change(s) ({rep?.TotalChanges ?? 0} live edit(s)) to {right.Database} on {right.Endpoint}.";
                else if (failed.Count > 0)
                    noteOut = $"Nothing reached {right.Database} on {right.Endpoint}.";
                else
                    noteOut = $"No changes were needed on {right.Database} on {right.Endpoint} (already reconciled).";
                if ((rep?.DeletedRefs.Length ?? 0) > 0) noteOut += $" Deleted: {string.Join(", ", rep.DeletedRefs)}.";
                if ((rep?.DeletesAlreadyAbsent.Length ?? 0) > 0) noteOut += $" Already absent (no-op): {string.Join(", ", rep.DeletesAlreadyAbsent)}.";
                if ((rep?.DeletesRefused.Length ?? 0) > 0) noteOut += $" DELETE REFUSED — identity gone + a different object now bears the name (re-diff): {string.Join(", ", rep.DeletesRefused)}.";
                if (noOpRefs.Length > 0) noteOut += $" Already reconciled on the target since the diff — no-op ({noOpRefs.Length}): {string.Join(", ", noOpRefs)}.";
                if (failed.Count > 0) noteOut += " Not synced: " + string.Join("; ", failed);
                if (driftRefs.Length > 0) noteOut += $" Drift override accepted for: {string.Join(", ", driftRefs)}.";
                // Tell the caller how to undo what they just did — a restore point nobody knows about is not a safety net.
                if (restorePoint != null && applied) noteOut += $" Undo this with rollback_push('{restorePoint.Id}').";
                else if (restoreError != null) noteOut += $" No restore point was written ({restoreError}) — this push cannot be rolled back.";
                // PARTIAL SUCCESS (A1): metadata committed but the calc-table recalc failed — the metadata IS live, so
                // Applied stays truthful, but the recalc warning is surfaced PROMINENTLY (Note + Error) so the caller
                // knows a new calc table exists-but-is-empty and needs a refresh. Never dropped.
                var partialErr = (rep != null && rep.Committed && rep.Error != null) ? rep.Error : null;
                if (partialErr != null) noteOut += " Warning: " + partialErr;
                return new ApplyDiffResult { Applied = applied, Count = appliedCount, AppliedRefs = appliedRefs, FailedRefs = failed.ToArray(), Target = tlabel, Note = noteOut, Error = partialErr,
                    RestorePointId = applied ? restorePoint?.Id : null };
            }
            finally { CleanupAll(); }
        }

        // Does a DeployReport Unmatched/Conflicts entry concern this object ref? Entries read "<ref> (reason…)",
        // "<prefix> <ref> (reason…)" (e.g. "source-type-mismatch partition:T/P (…)") or "<ref>: reason" (a Conflicts
        // rename-skip). Object NAMES can contain spaces, so we can't tokenise on whitespace — match the ref literally
        // with a boundary check on both sides so "table:Sales" never matches "table:SalesOrders".
        private static bool DeployEntryConcernsRef(string entry, string refStr)
        {
            if (string.IsNullOrEmpty(entry) || string.IsNullOrEmpty(refStr)) return false;
            int i = 0;
            while ((i = entry.IndexOf(refStr, i, StringComparison.Ordinal)) >= 0)
            {
                var leftOk = i == 0 || entry[i - 1] == ' ';
                var end = i + refStr.Length;
                var rightOk = end == entry.Length || entry[end] == ' ' || entry[end] == '(' || entry[end] == ':';
                if (leftOk && rightOk) return true;
                i = end;
            }
            return false;
        }

        /// <summary>List the pre-push snapshots a live push can be rolled back to. Read-only, always free.</summary>
        public Task<RestorePointRecord[]> ListRestorePointsAsync(string endpoint = null, string database = null) =>
            Task.FromResult(RestorePointStore.List(endpoint, database).ToArray());

        /// <summary>Preview or delete restore points by one selector. A token binds confirmation to the reviewed set.</summary>
        public async Task<RestorePointPurgeResult> PurgeRestorePointsAsync(string id = null, int? olderThanDays = null,
            bool confirm = false, string confirmToken = null, string origin = "human")
        {
            var result = RestorePointStore.Purge(id, olderThanDays, confirm, confirmToken);
            var ok = string.IsNullOrEmpty(result.Error) && result.FailedIds.Length == 0;
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "purge_restore_points",
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = confirm
                    ? (ok ? $"Purged {result.Removed} restore point(s)" : "Restore-point purge refused or incomplete")
                    : $"Previewed restore-point purge: {result.Matched} match(es)",
                Target = id ?? (olderThanDays.HasValue ? $"older than {olderThanDays.Value} day(s)" : null),
                Ok = ok,
                Error = result.Error,
                Result = result,
            });
            return result;
        }

        /// <summary>
        /// Restore a published model to a pre-push snapshot. This is the ONLY way back from a live delete: SyncModels
        /// can re-create measures / calc columns / calc tables / named expressions, but a relationship, role, perspective,
        /// hierarchy, partition, culture, datasource or data table removed from a published model is otherwise gone.
        ///
        /// DRY RUN by default. The dry run IS the guard: rather than trusting a stored fingerprint to tell us whether
        /// the target moved since the push (server-side normalisation makes that comparison flap), we diff the restore
        /// point against the CURRENT live model and show exactly what would change. An object a colleague legitimately
        /// added after the push shows up as a Remove — visibly, before the user confirms, not as a surprise afterwards.
        ///
        /// HONEST LIMIT: a restore point is the target as it stood when the pre-push snapshot was taken, NOT at the
        /// instant of the write. An edit landing in that window is captured by neither, so rolling back reverts it. The
        /// push's drift guard narrows the window and the dry run names every object it would touch, so the revert is
        /// disclosed rather than silent — but "restore point" means "that snapshot", not "the world immediately before".
        ///
        /// FREE at both tiers, deliberately: this is the undo for an irreversible write, and safety is never paywalled.
        /// </summary>
        public async Task<RollbackResult> RollbackPushAsync(string restorePointId, bool commit, string authMode = null, string origin = "human")
        {
            var rp = RestorePointStore.Find(restorePointId);
            if (rp == null)
                return new RollbackResult { RestorePointId = restorePointId, Error = "No restore point with that id. List restore points to see what is available." };
            if (!string.IsNullOrEmpty(rp.IntegrityError))
                return new RollbackResult { RestorePointId = rp.Id, Target = rp.Database,
                    Error = "That restore point failed its local integrity check and cannot be loaded: " + rp.IntegrityError };
            // Re-check on the path itself, not just the manifest: List() nulls a missing path, but a concurrent purge
            // can delete the snapshot between that read and the load below. Fail with the reason, not a raw IO error.
            if (string.IsNullOrEmpty(rp.BimPath) || !System.IO.File.Exists(rp.BimPath))
                return new RollbackResult { RestorePointId = rp.Id, Target = rp.Database, Error = "That restore point's snapshot file is missing from disk, so there is nothing to restore from. It may have been pruned (only the newest " + RestorePointStore.MaxPerTarget + " per target are kept) or purged." };

            var tlabel = string.IsNullOrWhiteSpace(rp.Database) ? rp.Endpoint : rp.Database;
            var cleanups = new System.Collections.Generic.List<Action>();
            void CleanupAll() { foreach (var c in cleanups) { try { c(); } catch { } } }

            try
            {
                var rdb = ModelCompare.LoadRawModelDb(rp.BimPath);                                     // the state to restore
                var (ldb, effectiveMode) = await SnapshotWorkspaceAsync(rp.Endpoint, rp.Database, authMode, cleanups, origin);  // the state right now; capture the mode it proved for the push

                // Direction: make the LIVE model match the RESTORE POINT. A "Delete" item is therefore an object that
                // exists live but not in the snapshot — added by the push we are undoing, or by someone since.
                var diff = ModelCompare.Diff(rdb.Model, ldb.Model, "restore point " + rp.Id, tlabel);
                var changed = diff.Items.Where(i => i.Action != "Equal").ToList();
                // A retag/republish emits a Delete+Create pair sharing a name-based ref: we can neither delete the live
                // object nor add a same-named one beside it. Excluded from the rollback — and REPORTED, never dropped.
                var republished = changed.Where(i => i.LikelyRepublished).Select(i => i.Ref + " (republished under us — re-run rollback_push to diff against the current state)")
                    .Distinct(StringComparer.Ordinal).ToList();
                var items = changed.Where(i => !i.LikelyRepublished).ToList();
                var removeItems = items.Where(i => i.Action == "Delete").ToList();
                var restoreRefs = items.Where(i => i.Action != "Delete").Select(i => i.Ref).ToArray();
                var removeRefs = removeItems.Select(i => i.Ref).ToArray();

                if (restoreRefs.Length == 0 && removeRefs.Length == 0 && republished.Count == 0)
                    return new RollbackResult { Applied = false, RestorePointId = rp.Id, Target = tlabel,
                        Note = $"{tlabel} already matches this restore point — nothing to roll back." };

                if (!commit)
                    return new RollbackResult
                    {
                        Applied = false, Restored = restoreRefs.Length, Removed = removeRefs.Length,
                        RestoredRefs = restoreRefs, RemovedRefs = removeRefs, FailedRefs = republished.ToArray(),
                        RestorePointId = rp.Id, Target = tlabel,
                        Note = $"Dry run. Rolling back to the snapshot taken {rp.CapturedUtc} would restore {restoreRefs.Length} object(s) and REMOVE {removeRefs.Length} object(s) that exist on {tlabel} but not in the snapshot. "
                             + "Anything added to the target since that snapshot — by this push or by anyone else — is listed under RemovedRefs and WILL be deleted. Review it, then pass commit=true.",
                    };

                // Agent-permissions gate — a rollback is a live write. The dry run above is never gated (preview).
                // intentBasis = the restore-point id: approving a roll back to THIS point never authorises an older one.
                var rollbackRefusal = GuardAgent(AgentCapability.Rollback, rp.Endpoint, rp.Database, origin, isCommit: true,
                    summary: $"roll {tlabel} back to the restore point taken {rp.CapturedUtc}", intentBasis: "rollback:" + rp.Id);
                if (rollbackRefusal != null)
                    return new RollbackResult { Applied = false, RestorePointId = rp.Id, Target = tlabel, Error = rollbackRefusal };

                var outcome = new ModelCompare.ApplyOutcome();
                if (restoreRefs.Length > 0)
                    outcome = ModelCompare.Apply(rdb.Model, ldb.Model, diff, new System.Collections.Generic.HashSet<string>(restoreRefs, StringComparer.Ordinal));
                var failed = outcome.Failed.Select(f => f.Ref + " (" + f.Reason + ")").ToList();
                failed.AddRange(republished);

                // Same invariant the push holds: never write a merge that doesn't serialize.
                try { _ = RawTom.JsonSerializer.SerializeDatabase(ldb); }
                catch (Exception vex)
                {
                    var m = vex.Message; if (m.Length > 300) m = m.Substring(0, 300) + "…";
                    return new RollbackResult { RestorePointId = rp.Id, Target = tlabel, FailedRefs = failed.ToArray(),
                        Error = "The restored model is not valid (" + m + ") — the target was NOT written." };
                }

                var bim = CreatePushStagingPath(cleanups);
                System.IO.File.WriteAllText(bim, RawTom.JsonSerializer.SerializeDatabase(ldb));

                // identityStrict: the restore point IS a snapshot of this same target, so a non-empty tag that no longer
                // resolves means the object was republished under us — refuse rather than mutate a same-named impostor.
                var deleteTargets = removeItems.Select(LiveDeleteTarget.FromDiffItem).ToArray();
                var rep = await PushWorkspaceAsync(bim, rp.Endpoint, rp.Database, effectiveMode, deleteTargets);   // reuse the credential the snapshot proved (P2-5)

                if (rep?.Committed != true)
                    return new RollbackResult { RestorePointId = rp.Id, Target = tlabel, FailedRefs = failed.ToArray(),
                        Error = "Rollback failed — nothing was written to " + tlabel + ". " + (rep?.Error ?? "The target refused the write.") };

                var sess = _sessions.Current;
                if (sess != null)
                    try
                    {
                        await RecordVerifiedEditAsync(sess, new VerifiedEditRecord
                        {
                            SessionId = sess.Id, Revision = 0, Origin = origin, Op = "rollback_push",
                            Verdict = "deployed",
                            Summary = $"rolled {rp.Endpoint}/{rp.Database} back to restore point {rp.Id} — restored {restoreRefs.Length}, removed {removeRefs.Length}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { rp.Endpoint, rp.Database, restorePoint = rp.Id, rp.CapturedUtc, restored = restoreRefs, removed = rep.DeletedRefs, failed }),
                        });
                    }
                    catch { /* the rollback succeeded; a failed audit write must not misreport it */ }

                // `Removed` counts what LIVE actually deleted, not what we planned. A removal the endpoint REFUSED must
                // therefore land in FailedRefs, or automation comparing the dry run's plan against the commit's result
                // would read "3 of 5 removed" as a clean success. Surfaced structurally, not only in the note.
                foreach (var r in rep.DeletesRefused) failed.Add(r + " (remove refused — republished under us; re-run rollback_push)");

                var note = $"Rolled {tlabel} back to the snapshot taken {rp.CapturedUtc}: restored {restoreRefs.Length} object(s), removed {rep.DeletedRefs.Length}.";
                if (failed.Count > 0) note += " Not restored: " + string.Join("; ", failed);
                var partialErr = rep.Error;   // Committed==true with an Error ⇒ the metadata landed, a recalc did not
                if (partialErr != null) note += " Warning: " + partialErr;

                return new RollbackResult
                {
                    Applied = true, Restored = restoreRefs.Length, Removed = rep.DeletedRefs.Length,
                    RestoredRefs = restoreRefs, RemovedRefs = rep.DeletedRefs, FailedRefs = failed.ToArray(),
                    RestorePointId = rp.Id, Target = tlabel, Note = note, Error = partialErr,
                };
            }
            catch (Exception ex)
            {
                return new RollbackResult { RestorePointId = rp.Id, Target = tlabel, Error = "Rollback failed: " + FabricRest.Scrub(ex.Message) };
            }
            finally { CleanupAll(); }
        }

        // Snapshot the live target's metadata to raw TOM. Test seam: WorkspaceSnapshotHook returns an in-memory
        // snapshot (A then B on successive calls) so offline tests never touch a real endpoint.
        private async Task<(RawTom.Database db, string mode)> SnapshotWorkspaceAsync(string endpoint, string database, string authMode, System.Collections.Generic.List<Action> cleanups, string origin)
        {
            if (WorkspaceSnapshotHook != null) return (await WorkspaceSnapshotHook(), XmlaAuthHint.SafeMode(authMode));
            var (snap, effectiveMode) = await SnapshotWorkspaceMetadataAsync(endpoint, database, authMode, origin);
            var dir = System.IO.Path.GetDirectoryName(snap.BimPath);
            cleanups.Add(() => { try { System.IO.Directory.Delete(dir, true); } catch { } });   // a load failure must not orphan the snapshot dir
            try { return (ModelCompare.LoadRawModelDb(snap.BimPath), effectiveMode); }
            catch { try { System.IO.Directory.Delete(dir, true); } catch { } throw; }
        }

        // Test seam: stands in for "acquire an Entra token in <mode> and snapshot the live model to a .bim". Lets the
        // no-token -> interactive-fallback and agent-refusal paths be exercised offline (a live browser sign-in can't
        // run in a test). Receives the auth MODE actually attempted (so a test can reject 'azcli' but accept
        // 'interactive', proving the fallback fires); null in production -> the real acquire+export path runs.
        internal Func<string, Task<LiveModelExport.Snapshot>> WorkspaceTokenExportForTests;

        // Single-flight guard for the INTERACTIVE sign-in: two concurrent cold compares must not open two browser
        // prompts or race the shared on-disk AuthenticationRecord. The first flow signs in + persists the record; a
        // waiter, once inside the gate, re-acquires and BuildCredentialAsync serves the now-saved record silently.
        private readonly System.Threading.SemaphoreSlim _interactiveAuthGate = new(1, 1);

        // Read a published XMLA model's metadata to a local .bim, mirroring connect_xmla / open_live's auth exactly:
        // reuse the persistent encrypted sign-in cache (silent when a saved sign-in exists — the SAME EntraToken
        // acquisition), and on a COLD auth rejection (fresh install / expired token / different tenant / an azcli token
        // the endpoint rejects behind its first-party-appid wall) route a HUMAN through the SAME interactive sign-in and
        // retry EXACTLY once. An AGENT-origin caller must NEVER pop a browser (an interactive sign-in is a human action)
        // — it gets a teaching refusal telling it to ask the user to Connect. Every auth failure is rewritten to teach
        // the fix; only sanitized values (mode label, tenant label, scrubbed endpoint) reach the message, never a token.
        // Returns the auth MODE that actually succeeded, so a subsequent WRITE reuses the credential the snapshot proved.
        private async Task<(LiveModelExport.Snapshot snap, string mode)> SnapshotWorkspaceMetadataAsync(string endpoint, string database, string authMode, string origin)
        {
            var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
            var ct = System.Threading.CancellationToken.None;

            // First attempt: honour the requested mode, reusing the persistent sign-in cache (the same acquisition
            // connect_xmla / open_live use — a saved interactive/device-code record is served silently).
            try
            {
                return (await AcquireAndExportWorkspaceAsync(mode, endpoint, database, ct), mode);
            }
            catch (Exception ex)
            {
                // A NON-auth failure (network / dataset-not-found / unreachable) must NOT trigger a sign-in and must NOT
                // leak the raw AMO/token text — scrub and rethrow with context. (IsAuthFailure is broad here on purpose:
                // a bare 401 / lifetime-expiry without the narrow tokens must still route to the fallback, not fall
                // through unscrubbed — the very bug this PR fixes.)
                if (!XmlaAuthHint.IsAuthFailure(ex))
                    throw new InvalidOperationException("Could not read the published model at " + XmlaAuthHint.SafeEndpoint(endpoint) + ": " + XmlaAuthHint.Scrub(ex.Message));

                // No live token for this endpoint. Both doors are handled HONESTLY: an agent never gets silent UI. The
                // raw exception is NOT attached as an inner (a consumer serializing ToString() would re-expose the AMO
                // text) — the teaching message is self-contained.
                if (DeployGuard.IsAgent(origin))
                    throw new InvalidOperationException(XmlaAuthHint.AgentRefusal(endpoint, mode, XmlaAuthHint.DefaultTenantLabel(mode)));

                // A HUMAN: fall back to the interactive sign-in connect_xmla uses (the first-party Power BI client the
                // endpoint accepts) and retry once. serviceprincipal / caller-token modes have no interactive stand-in,
                // so they teach immediately WITHOUT claiming a sign-in was attempted.
                var fbMode = XmlaAuthHint.InteractiveFallbackMode(mode);
                if (fbMode == null)
                    throw new InvalidOperationException(XmlaAuthHint.TeachingErrorNoInteractive(endpoint, mode, XmlaAuthHint.DefaultTenantLabel(mode)));

                // Serialize the browser sign-in so concurrent cold compares can't double-prompt or clobber the record.
                await _interactiveAuthGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return (await AcquireAndExportWorkspaceAsync(fbMode, endpoint, database, ct), fbMode);
                }
                catch (Exception ex2)
                {
                    // A non-auth failure after signing in (network / dataset) — scrub and surface it, don't mislabel it.
                    if (!XmlaAuthHint.IsAuthFailure(ex2))
                        throw new InvalidOperationException("Could not read the published model at " + XmlaAuthHint.SafeEndpoint(endpoint) + " after signing in: " + XmlaAuthHint.Scrub(ex2.Message));
                    // The interactive sign-in itself failed (cancelled, wrong tenant, no consent) — teach, never leak AMO.
                    throw new InvalidOperationException(XmlaAuthHint.TeachingErrorAfterSignIn(endpoint, fbMode, XmlaAuthHint.DefaultTenantLabel(fbMode)));
                }
                finally { _interactiveAuthGate.Release(); }
            }
        }

        // One acquire+export attempt in a single auth mode. Uses BuildCredentialAsync (the persistent-cache credential
        // open_live uses), so a saved interactive/device-code sign-in is reused silently and an interactive fallback
        // prompts + persists on first use. The test seam short-circuits both halves so no endpoint is touched offline.
        private async Task<LiveModelExport.Snapshot> AcquireAndExportWorkspaceAsync(string mode, string endpoint, string database, System.Threading.CancellationToken ct)
        {
            if (WorkspaceTokenExportForTests != null) return await WorkspaceTokenExportForTests(mode);
            var cred = mode == "token" ? null : await EntraToken.BuildCredentialAsync(mode, null, ct);
            var tok = cred != null
                ? await EntraToken.GetTokenAsync(cred, ct)
                : await EntraToken.AcquireFullAsync(mode, null, ct);
            return await LiveModelExport.ToBimAsync(endpoint, database, tok.Token, tok.ExpiresOn);
        }

        // Test-only observer of the EFFECTIVE auth mode the push acquires with — lets a test prove the snapshot->push
        // credential reuse (P2-5) without changing WorkspacePushHook's signature (used by ~30 existing call sites).
        internal Action<string> PushAuthModeForTests;

        // Push the merged .bim to the live model (adds/updates + the explicit deletes) in one SaveChanges. Test seam:
        // WorkspacePushHook stands in for the live write offline. authMode is the EFFECTIVE mode the pre-push snapshot
        // proved (see SnapshotWorkspaceMetadataAsync): if the snapshot fell back to interactive, this re-acquires with
        // that mode. NOTE (Windows-only silent reuse): the reuse is silent only where the AuthenticationRecord persists
        // to the encrypted on-disk cache — Windows (EntraToken.PersistenceSupported). On Linux/macOS the record is not
        // persisted, so AcquireAsync("interactive") re-prompts here just as open_live's first sign-in does; the write
        // still uses the RIGHT mode (never the rejected azcli), it just prompts once more on those platforms.
        private async Task<DeployReport> PushWorkspaceAsync(string bimPath, string endpoint, string database, string authMode, System.Collections.Generic.IReadOnlyCollection<LiveDeleteTarget> deleteTargets)
        {
            PushAuthModeForTests?.Invoke(authMode);
            if (WorkspacePushHook != null) return WorkspacePushHook(bimPath, deleteTargets);
            var token = await EntraToken.AcquireAsync(authMode, null, System.Threading.CancellationToken.None);
            // identityStrict: TRUE for the selective push — src is snapshot B (the live target seconds ago), so a
            // non-empty tag miss means the object was republished/retagged under us and must NOT be name-mutated.
            // deploy_live (whole-model) leaves it FALSE (its src is an arbitrary session, often untagged).
            return await Task.Run(() => LiveDeploy.SyncSessionToLive(bimPath, endpoint, database, token, System.DateTimeOffset.UtcNow.AddMinutes(50), commit: true, explicitDeleteTargets: deleteTargets, identityStrict: true));
        }

        private static string PreviewDelete(Model m, string kind, string table, string name)
        {
            if (kind != "measure") return $"removing a '{kind}' from the open model isn't supported yet (measures for now)";
            var tt = m.Tables.FirstOrDefault(t => t.Name == table);
            if (tt == null || !tt.Measures.Any(x => x.Name == name)) return $"the open model has no measure {table}[{name}]";
            return null;
        }
        private static string DeleteFromSession(Model m, string kind, string table, string name)
        {
            var reason = PreviewDelete(m, kind, table, name);
            if (reason != null) return reason;
            m.Tables.First(t => t.Name == table).Measures.First(x => x.Name == name).Delete();
            return null;
        }

        // --- cherry_pick: copy objects FROM another model INTO the open session, undoably (one batch). Measures for
        // now (the common cross-model copy — "copy this measure into my model"); other kinds report a clear
        // unsupported reason rather than silently no-op. The source is loaded as RAW TOM (a second wrapper can't
        // coexist with the session's process-global wrapper singleton); its properties are then re-created via the
        // wrapper API so the change is change-tracked, undoable, and broadcast like every other edit. ---
        public async Task<CherryPickResult> CherryPickAsync(ModelRef source, string[] refs, bool includeDependencies, bool commit, string origin)
        {
            if (refs == null || refs.Length == 0) return new CherryPickResult { Error = "cherry_pick: no object refs supplied." };
            var (sdb, slabel, sclean) = await ResolveModelRefAsync(source ?? new ModelRef { Kind = "session" }, origin);
            try
            {
                var s = _sessions.Require();
                var parsed = refs.Select(ParseChildRef).ToList();
                if (!commit)
                {
                    var pv = await s.ReadAsync(m =>
                    {
                        var ok = new System.Collections.Generic.List<string>(); var conflicts = new System.Collections.Generic.List<string>(); var fail = new System.Collections.Generic.List<string>();
                        foreach (var p in parsed)
                        {
                            var (applyable, overwrite, reason) = PreviewCopy(m, sdb.Model, p.kind, p.table, p.name);
                            if (!applyable) { fail.Add(p.raw + " (" + reason + ")"); continue; }
                            ok.Add(p.raw); if (overwrite) conflicts.Add(p.raw);
                        }
                        return (ok, conflicts, fail);
                    });
                    return new CherryPickResult { Applied = false, Count = pv.ok.Count, AppliedRefs = pv.ok.ToArray(), Conflicts = pv.conflicts.ToArray(), FailedRefs = pv.fail.ToArray(), Source = slabel,
                        Note = $"Preview — {pv.ok.Count} object(s) would copy into the open model" + (pv.conflicts.Count > 0 ? $", {pv.conflicts.Count} overwriting an existing object" : "") + (pv.fail.Count > 0 ? $"; {pv.fail.Count} cannot" : "") + ". Pass commit=true to apply." };
                }
                // Pro gate: copying MULTIPLE objects from another model in one undoable batch is the bulk/atomic
                // primitive (the Pro value); a single-object copy stays free, and the commit=false preview above is
                // read-only + free. Thrown before the mutate, so a refusal leaves the model intact.
                if (parsed.Count > 1)
                    Entitlement.EntitlementGuard.RequirePro(_entitlement, "Copying multiple objects into the open model at once",
                        "Copy one object at a time (pass a single ref), or use the individual edit tools.");
                var applied = new System.Collections.Generic.List<string>(); var failed = new System.Collections.Generic.List<string>();
                await s.MutateAsync(origin, $"copy {parsed.Count} object(s) from {slabel}", m =>
                {
                    foreach (var p in parsed)
                    {
                        // Per-item try/catch: a throw the PreviewCopy precheck can't foresee (e.g. AllowCreate=false on a
                        // governed model) must surface in FailedRefs, not escape and roll back the items already copied.
                        try
                        {
                            var reason = CopyIntoSession(m, sdb.Model, p.kind, p.table, p.name);
                            if (reason == null) applied.Add(p.raw); else failed.Add(p.raw + " (" + reason + ")");
                        }
                        catch (Exception ex) { failed.Add(p.raw + " (" + FabricRest.Scrub(ex.Message) + ")"); }
                    }
                });
                return new CherryPickResult { Applied = true, Count = applied.Count, AppliedRefs = applied.ToArray(), FailedRefs = failed.ToArray(), Source = slabel,
                    Note = $"Copied {applied.Count} object(s) into the open model from {slabel}." + (failed.Count > 0 ? $" {failed.Count} failed: " + string.Join("; ", failed) : "") };
            }
            finally { sclean(); }
        }

        // "measure:Sales/Margin %" -> (measure, Sales, Margin %, raw). Top-level refs (no '/') -> table = null. The
        // table|name '/' delimiter is structural; a '/' (or '~') that legally occurs INSIDE a table/object name is
        // JSON-Pointer-escaped (~1 / ~0) by the ref producer (ListReferenceTreeAsync) so it round-trips. UnescRef
        // reverses it — a no-op for ordinary names (and for hand-written MCP refs) that contain neither sequence.
        private static (string kind, string table, string name, string raw) ParseChildRef(string r)
        {
            var c = (r ?? "").IndexOf(':');
            var kind = c < 0 ? "" : r.Substring(0, c).ToLowerInvariant();
            var rest = c < 0 ? (r ?? "") : r.Substring(c + 1);
            var slash = rest.IndexOf('/');
            return slash < 0 ? (kind, null, UnescRef(rest), r) : (kind, UnescRef(rest.Substring(0, slash)), UnescRef(rest.Substring(slash + 1)), r);
        }
        private static string EscRef(string s) => (s ?? "").Replace("~", "~0").Replace("/", "~1");   // ~ first, then /
        private static string UnescRef(string s) => (s ?? "").Replace("~1", "/").Replace("~0", "~");  // / first, then ~

        // Dry-run classify (no mutation): is the copy applyable, would it overwrite an existing object, else why not.
        // Supported kinds: measure, column (calculated only), calcitem (into an existing group), table (calculation
        // GROUP only — a whole regular table isn't reconstructable via the wrapper here; copy its parts or apply-to-file).
        private static (bool applyable, bool overwrite, string reason) PreviewCopy(Model m, RawTom.Model src, string kind, string table, string name)
        {
            switch (kind)
            {
                case "measure":
                {
                    if (string.IsNullOrEmpty(table)) return (false, false, "a measure ref needs a table");
                    var sm = src.Tables.Find(table)?.Measures.Find(name);
                    if (sm == null) return (false, false, $"the source has no measure {table}[{name}]");
                    var tt = m.Tables.FirstOrDefault(t => t.Name == table);
                    if (tt == null) return (false, false, $"the open model has no table '{table}' — copy the table first");
                    // Measures and columns share ONE name namespace per table: a same-named column would make
                    // AddMeasure silently auto-rename to "<name> 2", so refuse rather than create a mis-named copy.
                    if (tt.Columns.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return (false, false, $"the open model already has a COLUMN {table}[{name}] — a measure can't share that name");
                    return (true, tt.Measures.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)), null);
                }
                case "column":
                {
                    if (string.IsNullOrEmpty(table)) return (false, false, "a column ref needs a table");
                    var sc = src.Tables.Find(table)?.Columns.Find(name);
                    if (sc == null) return (false, false, $"the source has no column {table}[{name}]");
                    if (!(sc is RawTom.CalculatedColumn)) return (false, false, "only CALCULATED columns can be copied between models (data columns come from the source query)");
                    var tt = m.Tables.FirstOrDefault(t => t.Name == table);
                    if (tt == null) return (false, false, $"the open model has no table '{table}' — copy the table first");
                    var ex = tt.Columns.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (ex != null && !(ex is CalculatedColumn)) return (false, false, $"the open model already has a non-calculated column {table}[{name}]");
                    // Same shared namespace the other way: a same-named measure would make AddCalculatedColumn
                    // silently auto-rename to "<name> 2", so refuse rather than create a mis-named copy.
                    if (ex == null && tt.Measures.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return (false, false, $"the open model already has a MEASURE {table}[{name}] — a column can't share that name");
                    return (true, ex != null, null);
                }
                case "calcitem":
                {
                    if (string.IsNullOrEmpty(table)) return (false, false, "a calculation-item ref needs its calculation group");
                    var sci = src.Tables.Find(table)?.CalculationGroup?.CalculationItems.Find(name);
                    if (sci == null) return (false, false, $"the source has no calculation item {table}[{name}]");
                    var tt = m.Tables.FirstOrDefault(t => t.Name == table) as CalculationGroupTable;
                    if (tt == null) return (false, false, $"the open model has no calculation group '{table}' — create it first, then copy its items");
                    return (true, tt.CalculationItems.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)), null);
                }
                default:
                    // A whole TABLE (incl. a calculation group) isn't copied into the live session: reconstructing a
                    // table via the wrapper + undoing it trips a vendored-wrapper OLS-on-delete bug. Use "apply to
                    // file" (which clones whole tables via raw TOM) for a whole table/group; copy its sub-objects here.
                    return (false, false, $"copying a '{kind}' into the open model isn't supported — measures, calculated columns & calculation items copy here; for a whole table or calculation group use 'apply to file' (a file Target) or copy its parts");
            }
        }

        // The actual copy via the wrapper API (must run inside MutateAsync → tracked + undoable). null = success.
        private static string CopyIntoSession(Model m, RawTom.Model src, string kind, string table, string name)
        {
            var (applyable, _, reason) = PreviewCopy(m, src, kind, table, name);
            if (!applyable) return reason;
            switch (kind)
            {
                case "measure":
                {
                    var sm = src.Tables.Find(table).Measures.Find(name);
                    var tt = m.Tables.First(t => t.Name == table);
                    var dm = tt.Measures.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? tt.AddMeasure(name, sm.Expression ?? string.Empty);
                    dm.Expression = sm.Expression ?? string.Empty; dm.DisplayFolder = sm.DisplayFolder; dm.Description = sm.Description; dm.IsHidden = sm.IsHidden;
                    // Dynamic format string + detail rows are first-class measure metadata the old 5-prop copy DROPPED
                    // (and left stale on overwrite). Setting FormatStringExpression non-blank auto-clears FormatString in
                    // the wrapper; setting it null clears any stale dynamic format — so copy whichever the source uses.
                    dm.FormatStringExpression = sm.FormatStringDefinition?.Expression;
                    dm.FormatString = string.IsNullOrEmpty(dm.FormatStringExpression) ? sm.FormatString : null;
                    dm.DetailRowsExpression = sm.DetailRowsDefinition?.Expression;
                    return null;
                }
                case "column":
                {
                    var sc = (RawTom.CalculatedColumn)src.Tables.Find(table).Columns.Find(name);
                    var tt = m.Tables.First(t => t.Name == table);
                    var dc = tt.Columns.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) as CalculatedColumn ?? tt.AddCalculatedColumn(name, sc.Expression ?? string.Empty);
                    dc.Expression = sc.Expression ?? string.Empty; dc.FormatString = sc.FormatString; dc.DisplayFolder = sc.DisplayFolder; dc.Description = sc.Description; dc.IsHidden = sc.IsHidden;
                    // Calc-column semantics the 5-prop copy dropped: declared data type, default aggregation, key/category
                    // flags, and the sort-by (resolved in the TARGET table — null if that column isn't present here).
                    dc.DataType = (DataType)sc.DataType; dc.SummarizeBy = (AggregateFunction)sc.SummarizeBy; dc.DataCategory = sc.DataCategory; dc.IsKey = sc.IsKey;
                    var sortName = sc.SortByColumn?.Name;
                    dc.SortByColumn = string.IsNullOrEmpty(sortName) ? null : tt.Columns.FirstOrDefault(x => string.Equals(x.Name, sortName, StringComparison.OrdinalIgnoreCase));
                    return null;
                }
                case "calcitem":
                {
                    var sci = src.Tables.Find(table).CalculationGroup.CalculationItems.Find(name);
                    var tt = (CalculationGroupTable)m.Tables.First(t => t.Name == table);
                    var di = tt.CalculationItems.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? tt.AddCalculationItem(name, sci.Expression ?? string.Empty);
                    di.Expression = sci.Expression ?? string.Empty; di.Description = sci.Description;
                    di.FormatStringExpression = sci.FormatStringDefinition?.Expression;   // calc items carry a dynamic format string too
                    return null;
                }
                default: return reason ?? "unsupported";
            }
        }

        // List a REFERENCE model's copyable objects (a second model the UI browses to copy FROM). Returns ONE flat
        // array — tables + their measures / calculated columns / calculation items — so the tree provider resolves the
        // model once (a file re-parse / workspace snapshot is expensive) and builds the hierarchy from the refs. The
        // refs match cherry_pick's expectations (measure:T/N, column:T/N, calcitem:T/N), so a node copies in directly.
        // origin threads the real caller: the Studio Reference Model picker offers an XMLA/workspace source, so a HUMAN
        // browsing a not-signed-in workspace gets the interactive sign-in (RPC defaults "human"); an agent (MCP
        // get_reference_tree) is refused with the teaching message and never pops UI.
        public async Task<TreeNode[]> ListReferenceTreeAsync(ModelRef reference, string origin = "human")
        {
            var (db, _, cleanup) = await ResolveModelRefAsync(reference ?? new ModelRef { Kind = "session" }, origin);
            try
            {
                var nodes = new System.Collections.Generic.List<TreeNode>();
                foreach (var t in db.Model.Tables.Cast<RawTom.Table>().OrderBy(t => t.Name))
                {
                    var measures = t.Measures.Cast<RawTom.Measure>().OrderBy(x => x.Name).ToList();
                    var calcCols = t.Columns.Cast<RawTom.Column>().OfType<RawTom.CalculatedColumn>().OrderBy(x => x.Name).ToList();
                    var calcItems = t.CalculationGroup?.CalculationItems.Cast<RawTom.CalculationItem>().OrderBy(x => x.Name).ToList() ?? new System.Collections.Generic.List<RawTom.CalculationItem>();
                    var et = EscRef(t.Name);   // escape any '/' in the table name so the table|name delimiter stays unambiguous
                    nodes.Add(new TreeNode { Ref = "table:" + et, Name = t.Name, Kind = t.CalculationGroup != null ? "calcgroup" : "table", HasChildren = measures.Count + calcCols.Count + calcItems.Count > 0 });
                    foreach (var me in measures) nodes.Add(new TreeNode { Ref = "measure:" + et + "/" + EscRef(me.Name), Name = me.Name, Kind = "measure", HasChildren = false });
                    foreach (var c in calcCols) nodes.Add(new TreeNode { Ref = "column:" + et + "/" + EscRef(c.Name), Name = c.Name, Kind = "calcColumn", HasChildren = false });
                    foreach (var ci in calcItems) nodes.Add(new TreeNode { Ref = "calcitem:" + et + "/" + EscRef(ci.Name), Name = ci.Name, Kind = "calcitem", HasChildren = false });
                }
                return nodes.ToArray();
            }
            finally { cleanup(); }
        }

        // The deploy gate (Semanticus's wedge): BPA + AI-readiness + (optionally) the pending-change count vs a
        // target. Read-only. A blocked gate is what the cloud deploy ops refuse over unless explicitly overridden.
        public async Task<DeployGate> DeployGateAsync(ModelRef compareTarget)
        {
            _sessions.Require();
            var bpa = await BpaScanAsync();
            var card = await AiReadinessScanAsync();
            var blockers = new System.Collections.Generic.List<string>();
            if (card.GatedBy != null && card.GatedBy.Length > 0) blockers.AddRange(card.GatedBy);
            // Error-severity BPA violations that can't be auto-fixed also block — a hard error shipped is a real risk.
            // WAIVED violations don't count: a waiver is an audited, reasoned acceptance (surfaced, never hidden), and a
            // gate that re-litigates it defeats the waiver lane. Readiness hard-gates (GatedBy above) stay RAW on
            // purpose — those are physical floors the waiver doctrine says you can't accept your way past.
            var blocking = bpa.Violations?.Count(v => v.Severity >= 2 && !v.CanAutoFix && !v.Waived) ?? 0;
            var waived = bpa.Violations?.Count(v => v.Severity >= 2 && !v.CanAutoFix && v.Waived) ?? 0;
            if (blocking > 0) blockers.Add($"{blocking} blocking BPA error(s)");
            var changes = 0;
            if (compareTarget != null)
            {
                // origin "human": a compareTarget only reaches DeployGate from the Studio (the MCP deploy_gate passes
                // null); a not-signed-in workspace target may therefore sign in interactively. Any failure is swallowed
                // — the gate still reports BPA/readiness — so an agent could never be popped UI here regardless.
                try { var d = await CompareModelsAsync(new ModelRef { Kind = "session" }, compareTarget, false, "human"); changes = d.Created + d.Updated + d.Deleted; }
                catch { /* compare target unavailable — gate still reports BPA/readiness */ }
            }
            // The interview leg is ADVISORY by contract: it replays the saved question pack (offline-honest —
            // Unverified is the ceiling, never a fabricated pass) and reports per-question deltas vs the last
            // recorded outcomes, but it NEVER lands in Pass/Blockers and its failure never breaks the gate.
            InterviewGateAdvisory interview = null;
            try { interview = await InterviewGateAdvisoryAsync(); }
            catch { /* advisory only — a broken pack/store must not block or distort the gate verdict */ }
            return new DeployGate
            {
                Pass = blockers.Count == 0,
                Grade = card.Grade,
                BpaViolations = bpa.ViolationCount,
                BpaBlocking = blocking,
                BpaWaivedBlocking = waived,
                Blockers = blockers.ToArray(),
                Changes = changes,
                Interview = interview,
                Note = (blockers.Count == 0 ? "Gate passed." : "Gate blocked: " + string.Join("; ", blockers))
                     + (waived > 0 ? $" ({waived} error-severity BPA finding(s) waived — accepted decisions, not blockers; list_waivers shows the reasons.)" : ""),
            };
        }

        // ---- Fabric REST (the cloud ALM lane — read-only discovery) --------------------------------------------
        // Read-only GETs against api.fabric.microsoft.com with the user's own Entra identity (azcli default; SP is
        // the reliable headless path). No live write. Errors (incl. permission gates) surface scrubbed via FabricRest.
        public async Task<FabricWorkspace[]> ListWorkspacesAsync(string authMode, string tenantId)
        {
            var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            return await FabricRest.ListWorkspacesAsync(token, System.Threading.CancellationToken.None);
        }

        public async Task<DeploymentPipeline[]> ListDeploymentPipelinesAsync(string authMode, string tenantId)
        {
            var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            return await FabricRest.ListDeploymentPipelinesAsync(token, System.Threading.CancellationToken.None);
        }

        public async Task<PipelineStage[]> GetPipelineStagesAsync(string pipelineId, string authMode, string tenantId)
        {
            var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            return await FabricRest.GetPipelineStagesAsync(pipelineId, token, System.Threading.CancellationToken.None);
        }

        public async Task<StageItem[]> GetStageItemsAsync(string pipelineId, string stageId, string authMode, string tenantId)
        {
            var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            return await FabricRest.GetStageItemsAsync(pipelineId, stageId, token, System.Threading.CancellationToken.None);
        }

        // ---- Deployment pipeline: preview + the GATED deploy (write lane) + history --------------------------
        // Per-process secret mixed into the prod confirm token — never exposed; a restart invalidates outstanding
        // prod tokens (fine: a prod deploy is a fresh, human-confirmed act).
        private static readonly string DeploySecret = System.Guid.NewGuid().ToString("N");

        // Compute the best-effort New/Update diff from the SOURCE stage's items: an item with a bound target
        // (TargetItemId present) is an Update, else New. (Fabric has no preview endpoint; Different-vs-NoDifference
        // is only knowable post-deploy from the executionPlan.)
        private static DeployItemDiff[] DiffFromSource(StageItem[] srcItems)
            => srcItems.Select(i => new DeployItemDiff { ItemId = i.ItemId, ItemDisplayName = i.ItemDisplayName, ItemType = i.ItemType, State = i.TargetItemId != null ? "Update" : "New" }).ToArray();

        internal static (bool isProd, string name) ProdInfo(PipelineStage[] stages, string stageId)
        {
            var st = stages.FirstOrDefault(s => string.Equals(s.Id, stageId, System.StringComparison.OrdinalIgnoreCase));
            if (st == null) return (false, stageId);
            var maxOrder = stages.Length > 0 ? stages.Max(s => s.Order) : 0;
            // Treat the public stage, the highest-order stage, or anything named like prod as production (errs toward
            // MORE confirmation, never less).
            var isProd = st.IsPublic || st.Order == maxOrder
                || System.Text.RegularExpressions.Regex.IsMatch(st.DisplayName ?? "", "prod", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return (isProd, st.DisplayName ?? stageId);
        }

        private static string StageName(PipelineStage[] stages, string stageId)
            => stages.FirstOrDefault(s => string.Equals(s.Id, stageId, System.StringComparison.OrdinalIgnoreCase))?.DisplayName ?? stageId;

        internal static string BuildDeployBody(string src, string tgt, DeployItemDiff[] selectiveItems, string note)
        {
            var body = new System.Collections.Generic.Dictionary<string, object> { ["sourceStageId"] = src, ["targetStageId"] = tgt };
            if (selectiveItems != null) body["items"] = selectiveItems.Select(i => new { sourceItemId = i.ItemId, itemType = i.ItemType }).ToArray();
            if (!string.IsNullOrWhiteSpace(note)) body["note"] = note.Length > 1024 ? note.Substring(0, 1024) : note;
            return System.Text.Json.JsonSerializer.Serialize(body);
        }

        public async Task<DeployPreview> PreviewDeployAsync(string pipelineId, string sourceStageId, string targetStageId, string authMode, string tenantId)
        {
            string token; PipelineStage[] stages; StageItem[] srcItems;
            try
            {
                // The setup reads (token + stage/item GETs) throw on a non-2xx (401/403/404) — catch so the failure
                // is reported on the DTO's .Error, never thrown across the door (golden rule #2).
                token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                stages = await FabricRest.GetPipelineStagesAsync(pipelineId, token, System.Threading.CancellationToken.None);
                srcItems = await FabricRest.GetStageItemsAsync(pipelineId, sourceStageId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new DeployPreview { PipelineId = pipelineId, SourceStageId = sourceStageId, TargetStageId = targetStageId, Error = FabricRest.Scrub(ex.Message) }; }
            var (isProd, tgtName) = ProdInfo(stages, targetStageId);
            var diffs = DiffFromSource(srcItems);
            DeployGate gate = null; try { gate = await DeployGateAsync(null); } catch { /* no open model — gate skipped */ }
            return new DeployPreview
            {
                PipelineId = pipelineId, SourceStageId = sourceStageId, SourceStageName = StageName(stages, sourceStageId),
                TargetStageId = targetStageId, TargetStageName = tgtName, TargetIsProd = isProd,
                Items = diffs, NewCount = diffs.Count(d => d.State == "New"), UpdateCount = diffs.Count(d => d.State == "Update"),
                Gate = gate, Note = "Diff is by source→target pairing; an 'Update' item may be identical — the exact change is confirmed at deploy.",
            };
        }

        public async Task<DeployStageReport> DeployStageAsync(string pipelineId, string sourceStageId, string targetStageId,
            string[] items, string note, bool commit, string confirmToken, bool forceOverride, string authMode, string tenantId, string origin, string overrideReason = null)
        {
            if (string.IsNullOrWhiteSpace(pipelineId) || string.IsNullOrWhiteSpace(sourceStageId) || string.IsNullOrWhiteSpace(targetStageId))
                return new DeployStageReport { Error = "pipelineId, sourceStageId and targetStageId are all required." };

            string token; PipelineStage[] stages; StageItem[] srcItems;
            try
            {
                // Setup reads throw on a non-2xx — catch so the failure is reported on the DTO (golden rule #2), so the
                // "any failure is reported on the DTO, never thrown" guarantee below holds for the WHOLE method.
                token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                stages = await FabricRest.GetPipelineStagesAsync(pipelineId, token, System.Threading.CancellationToken.None);
                srcItems = await FabricRest.GetStageItemsAsync(pipelineId, sourceStageId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new DeployStageReport { PipelineId = pipelineId, SourceStageId = sourceStageId, TargetStageId = targetStageId, Error = FabricRest.Scrub(ex.Message), Plan = "Could not read the pipeline stages/items." }; }
            var (isProd, tgtName) = ProdInfo(stages, targetStageId);
            var srcName = StageName(stages, sourceStageId);

            var selected = items != null && items.Length > 0 ? new System.Collections.Generic.HashSet<string>(items) : null;
            var chosen = selected == null ? srcItems : srcItems.Where(i => selected.Contains(i.ItemId)).ToArray();
            var diffs = DiffFromSource(chosen);

            var report = new DeployStageReport
            {
                PipelineId = pipelineId, SourceStageId = sourceStageId, SourceStageName = srcName,
                TargetStageId = targetStageId, TargetStageName = tgtName, TargetIsProd = isProd,
                ItemCount = chosen.Length, Items = diffs, NewCount = diffs.Count(d => d.State == "New"), UpdateCount = diffs.Count(d => d.State == "Update"),
            };
            if (chosen.Length > 300) { report.Error = "Fabric caps a single deploy at 300 items — narrow the selection."; report.Plan = "Refused: over the 300-item cap."; return report; }

            // The gate evaluates the locally-OPEN working model (BPA + AI-readiness) — a separate artifact from the
            // Fabric source-stage bytes a pipeline promotes. With no model open there is nothing local to gate, so it
            // defaults to pass (and a note says so); a prod target is still independently blocked for agents.
            // Capture the exact session the gate scans NOW: the override below must be recorded on THIS model, not
            // on whatever the other door may have swapped Current to across the network awaits in between (TOCTOU).
            var gateSession = _sessions.Current;
            DeployGate gate = null; try { gate = await DeployGateAsync(null); } catch { /* no open model — gate is advisory only */ }
            report.Gate = gate;
            var gatePass = gate?.Pass ?? true;
            var gateNote = gate == null ? " (readiness gate skipped — no local model open; promoting source-stage content as-is)" : "";
            var expected = DeployGuard.MintToken(pipelineId, sourceStageId, targetStageId, chosen.Select(i => i.ItemId), DeploySecret);

            if (!commit)
            {
                // Dry-run preview. The prod confirm token is surfaced ONLY to the human door — an agent never receives it.
                report.ConfirmToken = DeployGuard.SurfaceConfirmToken(isProd, origin) ? expected : null;
                report.Plan = $"DRY RUN — would deploy {chosen.Length} item(s) {srcName}→{tgtName}; gate {(gatePass ? "pass" : "BLOCKED")}{gateNote}."
                    + (isProd ? (DeployGuard.IsAgent(origin)
                        ? " Target is PRODUCTION — an agent cannot complete this; a human must deploy it from the Deploy tab."
                        : " Target is PRODUCTION — re-run with commit=true and the confirmToken above to proceed.") : "");
                return report;
            }

            var refusal = DeployGuard.Refusal(isProd, commit, origin, confirmToken, expected, gatePass, forceOverride, !string.IsNullOrWhiteSpace(overrideReason));
            if (refusal != null) { report.Committed = false; report.Error = refusal; report.Plan = "Refused: " + refusal; return report; }

            // Accountable override (Verified Edits): a reasoned forceOverride past a RED gate is recorded on the
            // open model's audit chain BEFORE the promotion — the decision is the auditable act, whatever the LRO
            // then does. gate != null implies a session was open at scan time (captured as gateSession); require
            // REFERENCE EQUALITY with Current before recording — if the other door opened/replaced the model since,
            // Current is a DIFFERENT session and recording here would append the override to the WRONG model's chain.
            // REFUSE (fail closed): the record-then-push order is the whole point of the checkpoint.
            if (!gatePass && forceOverride && gate != null)
            {
                if (gateSession == null || !ReferenceEquals(_sessions.Current, gateSession))
                {
                    report.Committed = false;
                    report.Error = "the override cannot be recorded (the local model changed mid-deploy — the session that was gated is no longer current) — promotion refused; re-open the model and retry.";
                    report.Plan = "Refused: " + report.Error;
                    return report;
                }
                await RecordVerifiedEditAsync(gateSession, new VerifiedEditRecord
                {
                    SessionId = gateSession.Id, Revision = 0, Origin = origin, Op = "deploy_stage",   // 0: a promotion is not a model mutation
                    Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                    Summary = $"gate RED ({string.Join("; ", gate.Blockers ?? Array.Empty<string>())}) — override accepted to promote {srcName}→{tgtName}",
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new { pipelineId, sourceStageId, targetStageId, gate.Grade, gate.Blockers }),
                });
            }

            // The single live write: POST the deploy, poll the LRO. Any failure is reported on the DTO, never thrown.
            var body = BuildDeployBody(sourceStageId, targetStageId, selected == null ? null : diffs, note);
            var outcome = await FabricRest.DeployAsync(pipelineId, body, token, System.Threading.CancellationToken.None);
            report.Committed = outcome.Status == "Succeeded";
            report.Status = outcome.Status;
            report.OperationId = outcome.OperationId;
            report.Error = outcome.Error;
            report.Plan = report.Committed
                ? $"Deployed {chosen.Length} item(s) {srcName}→{tgtName} ({outcome.Status})."
                : $"Deploy {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            return report;
        }

        public async Task<DeploymentHistoryEntry[]> DeploymentHistoryAsync(string pipelineId, string authMode, string tenantId)
        {
            var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
            return await FabricRest.ListDeploymentOperationsAsync(pipelineId, token, System.Threading.CancellationToken.None);
        }

        // ---- Fabric Git (workspace ⇄ git) — reads + GATED writes (commit/update/connect/disconnect) -----------
        // Door-safety (golden rule #2): the REST helpers throw on any non-2xx (401/403/404 — sign-in expired, no
        // workspace role, workspace-not-git-connected) and on a failed LRO; EntraToken.AcquireFabricAsync throws on
        // an auth failure. Every method below CATCHES those and reports the (scrubbed) message on the result DTO's
        // .Error, so a failure is NEVER thrown across the RPC/MCP door (and the MCP Emit still fires with Ok=false).
        public async Task<FabricGitConnection> FabricGitConnectionAsync(string workspaceId, string authMode, string tenantId)
        {
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                return await FabricRest.GetGitConnectionAsync(workspaceId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new FabricGitConnection { Error = FabricRest.Scrub(ex.Message) }; }
        }

        public async Task<FabricGitStatus> FabricGitStatusAsync(string workspaceId, string authMode, string tenantId)
        {
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                return await FabricRest.GetGitStatusAsync(workspaceId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new FabricGitStatus { Error = FabricRest.Scrub(ex.Message) }; }
        }

        public async Task<FabricGitResult> FabricGitCommitAsync(string workspaceId, string comment, string[] items, bool commit, string authMode, string tenantId, string origin)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) return new FabricGitResult { Action = "commit", Error = "A workspaceId is required." };
            string token; FabricGitStatus status;
            try
            {
                token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                status = await FabricRest.GetGitStatusAsync(workspaceId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new FabricGitResult { Action = "commit", Direction = "workspace→git", Error = FabricRest.Scrub(ex.Message), Plan = "Could not read git status." }; }
            var wsChanges = status.Changes.Count(c => !string.IsNullOrEmpty(c.WorkspaceChange));
            var report = new FabricGitResult { Action = "commit", Direction = "workspace→git", ChangeCount = wsChanges, Conflicts = status.Conflicts };
            if (!commit) { report.Plan = $"DRY RUN — would commit {wsChanges} workspace change(s) to git."; return report; }
            if (wsChanges == 0) { report.Plan = "Nothing to commit — the workspace matches git."; report.Committed = false; report.Status = "Succeeded"; return report; }
            // Agent-permissions gate — committing workspace state INTO the team's repo is a shared-state write (and can
            // trigger the team's own CD from that repo). Basis pins the workspace head the human saw approve.
            {
                var refusal = GuardAgent(AgentCapability.DeployLive, workspaceId, null, origin, isCommit: true,
                    summary: $"commit {wsChanges} workspace change(s) to git from workspace {workspaceId}",
                    intentBasis: "fabric_git_commit:" + (status.WorkspaceHead ?? ""));
                if (refusal != null) { report.Error = refusal; report.Plan = "Refused by the agent policy."; return report; }
            }
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["mode"] = items != null && items.Length > 0 ? "Selective" : "All",
                ["comment"] = (comment ?? "").Length > 300 ? comment.Substring(0, 300) : (comment ?? ""),
                ["workspaceHead"] = status.WorkspaceHead,
            };
            if (items != null && items.Length > 0) body["items"] = items.Select(id => new { objectId = id }).ToArray();
            var outcome = await FabricRest.GitCommitAsync(workspaceId, System.Text.Json.JsonSerializer.Serialize(body), token, System.Threading.CancellationToken.None);
            report.Committed = outcome.Status == "Succeeded"; report.Status = outcome.Status; report.OperationId = outcome.OperationId; report.Error = outcome.Error;
            report.Plan = report.Committed ? $"Committed {wsChanges} change(s) to git." : $"Commit {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            return report;
        }

        public async Task<FabricGitResult> FabricGitUpdateAsync(string workspaceId, string conflictPolicy, bool allowOverride, bool commit, string authMode, string tenantId, string origin)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) return new FabricGitResult { Action = "update", Error = "A workspaceId is required." };
            // Safety gate (mirrors the deploy lane's agent-can't-do-the-destructive-thing rule, DeployGuard): an agent
            // must never FORCE-overwrite uncommitted workspace changes from git. allowOverride is the one irreversible
            // data-loss path here — the discarded workspace edits were never committed, so git cannot restore them. A
            // human confirms this from the Deploy tab. (A plain update conflicts-and-stops, so it stays agent-allowed.)
            if (commit && allowOverride && DeployGuard.IsAgent(origin))
                return new FabricGitResult { Action = "update", Direction = "git→workspace", Plan = "Refused: agent + allowOverride.",
                    Error = "An agent cannot force-overwrite workspace changes from git (allowOverride). A human must confirm this from the Deploy tab." };
            string token; FabricGitStatus status;
            try
            {
                token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                status = await FabricRest.GetGitStatusAsync(workspaceId, token, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { return new FabricGitResult { Action = "update", Direction = "git→workspace", Error = FabricRest.Scrub(ex.Message), Plan = "Could not read git status." }; }
            var remoteChanges = status.Changes.Count(c => !string.IsNullOrEmpty(c.RemoteChange));
            var report = new FabricGitResult { Action = "update", Direction = "git→workspace", ChangeCount = remoteChanges, Conflicts = status.Conflicts };
            if (!commit) { report.Plan = $"DRY RUN — would update {remoteChanges} item(s) from git into the workspace" + (status.Conflicts ? " (CONFLICTS present — set a conflict policy)" : "") + "."; return report; }
            if (remoteChanges == 0) { report.Plan = "Nothing to update — the workspace matches git."; report.Status = "Succeeded"; return report; }
            // Agent-permissions gate — a PLAIN update rewrites workspace items from git: a live deploy by another door.
            // (The allowOverride data-loss path is hard-refused for agents above, policy or no policy.) Basis pins the
            // git commit being applied, so approving "update to abc123" never authorises a later push to the repo.
            {
                var refusal = GuardAgent(AgentCapability.DeployLive, workspaceId, null, origin, isCommit: true,
                    summary: $"update {remoteChanges} item(s) in workspace {workspaceId} from git ({status.RemoteCommitHash})",
                    intentBasis: "fabric_git_update:" + (status.RemoteCommitHash ?? "") + "|" + (conflictPolicy ?? ""));
                if (refusal != null) { report.Error = refusal; report.Plan = "Refused by the agent policy."; return report; }
            }
            var body = new System.Collections.Generic.Dictionary<string, object> { ["remoteCommitHash"] = status.RemoteCommitHash };
            if (!string.IsNullOrEmpty(status.WorkspaceHead)) body["workspaceHead"] = status.WorkspaceHead;
            var policy = string.Equals(conflictPolicy, "PreferWorkspace", System.StringComparison.OrdinalIgnoreCase) ? "PreferWorkspace" : "PreferRemote";
            body["conflictResolution"] = new { conflictResolutionType = "Workspace", conflictResolutionPolicy = policy };
            if (allowOverride) body["options"] = new { allowOverrideItems = true };
            var outcome = await FabricRest.GitUpdateAsync(workspaceId, System.Text.Json.JsonSerializer.Serialize(body), token, System.Threading.CancellationToken.None);
            report.Committed = outcome.Status == "Succeeded"; report.Status = outcome.Status; report.OperationId = outcome.OperationId; report.Error = outcome.Error;
            report.Plan = report.Committed ? $"Updated {remoteChanges} item(s) from git into the workspace ({policy})." : $"Update {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            return report;
        }

        public async Task<FabricGitResult> FabricGitConnectAsync(string workspaceId, string provider, string organization, string project, string repository, string branch, string directory, string connectionId, bool commit, string authMode, string tenantId, string origin)
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(repository))
                return new FabricGitResult { Action = "connect", Error = "workspaceId, provider (AzureDevOps|GitHub) and repository are required." };
            var isGh = string.Equals(provider, "GitHub", System.StringComparison.OrdinalIgnoreCase);
            // GitHub MUST use a configured-connection id — Fabric blocks Automatic credentials for GitHub. Refuse
            // locally (in dry-run too) rather than make a doomed live POST that returns an opaque provider error.
            if (isGh && string.IsNullOrWhiteSpace(connectionId))
                return new FabricGitResult { Action = "connect", Error = "GitHub requires a configured-connection id (connectionId); GitHub cannot use Automatic credentials." };
            // Binding a workspace to a repo is an Admin op, exactly like disconnecting it (which is already agent-
            // refused below) — the two must gate symmetrically or an agent could re-point source control it cannot cut.
            if (commit && DeployGuard.IsAgent(origin))
                return new FabricGitResult { Action = "connect", Plan = "Refused: agent connect.",
                    Error = "An agent cannot connect a workspace to a git repository. A human must confirm this from the Deploy tab." };
            var report = new FabricGitResult { Action = "connect" };
            if (!commit) { report.Plan = $"DRY RUN — would connect workspace {workspaceId} to {(isGh ? "GitHub" : "AzureDevOps")} {repository}/{branch}."; return report; }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                var details = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["gitProviderType"] = isGh ? "GitHub" : "AzureDevOps",
                    ["repositoryName"] = repository,
                    ["branchName"] = branch ?? "main",
                    ["directoryName"] = string.IsNullOrWhiteSpace(directory) ? "/" : directory,
                };
                if (isGh) details["ownerName"] = organization;
                else { details["organizationName"] = organization; details["projectName"] = project; }
                var bodyD = new System.Collections.Generic.Dictionary<string, object> { ["gitProviderDetails"] = details };
                if (!string.IsNullOrWhiteSpace(connectionId)) bodyD["myGitCredentials"] = new { source = "ConfiguredConnection", connectionId };
                else if (!isGh) bodyD["myGitCredentials"] = new { source = "Automatic" };
                var outcome = await FabricRest.GitConnectAsync(workspaceId, System.Text.Json.JsonSerializer.Serialize(bodyD), token, System.Threading.CancellationToken.None);
                report.Committed = outcome.Status == "Succeeded"; report.Status = outcome.Status; report.OperationId = outcome.OperationId; report.Error = outcome.Error;
                report.Plan = report.Committed ? $"Connected the workspace to {(isGh ? "GitHub" : "AzureDevOps")} {repository}/{branch}." : $"Connect {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            }
            catch (Exception ex) { report.Error = FabricRest.Scrub(ex.Message); report.Plan = "Connect failed."; }
            return report;
        }

        public async Task<FabricGitResult> FabricGitDisconnectAsync(string workspaceId, bool commit, string authMode, string tenantId, string origin)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) return new FabricGitResult { Action = "disconnect", Error = "A workspaceId is required." };
            // Tearing a workspace off source control is an Admin op, not routine authoring — an agent must not
            // self-serve it; a human confirms from the Deploy tab. (Reconnect is possible, but the linkage +
            // initialized sync state are lost.) Refusal is reported on the DTO, never thrown.
            if (commit && DeployGuard.IsAgent(origin))
                return new FabricGitResult { Action = "disconnect", Plan = "Refused: agent disconnect.",
                    Error = "An agent cannot disconnect a workspace from git. A human must confirm this from the Deploy tab." };
            var report = new FabricGitResult { Action = "disconnect" };
            if (!commit) { report.Plan = $"DRY RUN — would disconnect workspace {workspaceId} from git."; return report; }
            try
            {
                var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                var outcome = await FabricRest.GitDisconnectAsync(workspaceId, token, System.Threading.CancellationToken.None);
                report.Committed = outcome.Status == "Succeeded"; report.Status = outcome.Status; report.OperationId = outcome.OperationId; report.Error = outcome.Error;
                report.Plan = report.Committed ? "Disconnected the workspace from git." : $"Disconnect {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            }
            catch (Exception ex) { report.Error = FabricRest.Scrub(ex.Message); report.Plan = "Disconnect failed."; }
            return report;
        }

        // ---- CI/CD publish (Items API) + emit-CI (fabric-cicd scaffold) — the final ALM lane -------------------
        // cicd_publish: enumerate the open model's on-disk PBIP/TMDL folder into base64 InlineBase64 parts and POST
        // them to the Fabric Items API updateDefinition (a FULL OVERRIDE). DRY-RUN by default (enumerate + report,
        // POST nothing). Gated like the other destructive cloud writes: an agent must not self-serve the live
        // publish (full override, no recovery, no prod marker to gate finely) — a human confirms. Door-safe: any
        // failure is reported on the DTO, never thrown.
        public async Task<CicdPublishResult> CicdPublishAsync(string workspaceId, string itemId, bool commit, string authMode, string tenantId, string origin)
        {
            var report = new CicdPublishResult { Action = "publish", WorkspaceId = workspaceId, ItemId = itemId };
            if (commit && DeployGuard.IsAgent(origin))
            {
                report.Error = "An agent cannot publish a model definition to a workspace (a full overwrite). A human must confirm this from the Deploy tab.";
                report.Plan = "Refused: agent publish.";
                return report;
            }
            var s = _sessions.Current;
            if (s == null) { report.Error = "Open a model first."; return report; }
            // The WHOLE body is door-safe (golden rule #2): the dry-run reads files off disk (ReadAllbytes can throw
            // IOException/UnauthorizedAccess if a .SemanticModel file is locked by Power BI Desktop / held by OneDrive),
            // and the commit path acquires a token + POSTs. Every failure is reported on .Error, never thrown across a door.
            try
            {
                var root = ResolveModelFolderOrNull();
                if (root == null) { report.Error = "cicd_publish needs the open model saved on disk as a Fabric .SemanticModel folder (a definition/ folder or model.bim, PLUS definition.pbism). Save it to a .SemanticModel folder first."; return report; }
                report.ModelPath = root;
                var dirty = s.HasUnsavedChanges;
                if (!commit)
                {
                    // Path-only enumeration for the preview — do NOT read/base64 every file just to show counts + 8 paths
                    // (that wasted work on a large model AND made a harmless preview throw on any one locked file).
                    var paths = EnumeratePartPaths(root);
                    report.PartCount = paths.Count;
                    report.SampleParts = paths.Take(8).ToArray();
                    report.Plan = $"DRY RUN — would publish {paths.Count} part(s) from {System.IO.Path.GetFileName(root)}"
                        + (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(itemId) ? " (set workspaceId + itemId to target a model)" : $" to workspace {workspaceId} / model {itemId}")
                        + (dirty ? " — NOTE: the open model has unsaved edits NOT yet on disk; save the model first so they're included." : "") + ".";
                    return report;
                }
                if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(itemId)) { report.Error = "workspaceId and itemId are required to publish."; report.Plan = "Refused: missing target."; return report; }
                // Publish the ON-DISK definition as-is. We deliberately do NOT auto-save: the engine's TMDL save writes a
                // flat raw-TOM folder, which would CORRUPT a PBIP .SemanticModel layout (definition/ + definition.pbism).
                // So a dirty model is refused — the user saves first (preserving their on-disk PBIP structure), then publishes.
                if (dirty) { report.Error = "The open model has unsaved edits. Save the model to disk first, then publish (so the on-disk definition is current)."; report.Plan = "Refused: unsaved edits."; return report; }
                var parts = EnumerateModelParts(root);
                report.PartCount = parts.Count;
                report.SampleParts = parts.Take(8).Select(p => p.path).ToArray();
                if (parts.Count == 0) { report.Error = "No definition files found under the model folder to publish."; return report; }
                var token = await EntraToken.AcquireFabricAsync(authMode, null, System.Threading.CancellationToken.None, tenantId);
                var body = System.Text.Json.JsonSerializer.Serialize(new { definition = new { parts = parts.Select(p => new { p.path, p.payload, p.payloadType }) } });
                var outcome = await FabricRest.UpdateSemanticModelDefinitionAsync(workspaceId, itemId, body, token, System.Threading.CancellationToken.None);
                report.Committed = outcome.Status == "Succeeded"; report.Status = outcome.Status; report.OperationId = outcome.OperationId; report.Error = outcome.Error;
                report.Plan = report.Committed
                    ? $"Published {parts.Count} part(s) to workspace {workspaceId} / model {itemId} ({outcome.Status})."
                    : $"Publish {outcome.Status}" + (string.IsNullOrEmpty(outcome.Error) ? "." : ": " + outcome.Error);
            }
            catch (Exception ex) { report.Error = FabricRest.Scrub(ex.Message); report.Plan = "Publish failed."; }
            return report;
        }

        // cicd_generate: emit a fabric-cicd scaffold (parameter.yml + a CI workflow + a deploy script) that runs the
        // REAL fabric-cicd publish_all_items in CI. PURE FILE AUTHORING — no live write, no token, no gate. Returns
        // the file contents; when write=true, also writes them into the repo (the natural place for parameter.yml +
        // the workflow). The engine adds NO Python dependency — it only emits the YAML/py that CI executes.
        public async Task<CicdScaffold> CicdGenerateAsync(string target, string workspaceId, string environment, bool write)
        {
            var ado = string.Equals(target, "ado", StringComparison.OrdinalIgnoreCase) || string.Equals(target, "azuredevops", StringComparison.OrdinalIgnoreCase);
            var env = string.IsNullOrWhiteSpace(environment) ? "PROD" : environment.Trim();
            // The env name becomes a YAML key (parameter.yml) + a CI scalar — restrict it to a safe charset so it can't
            // corrupt the emitted file or, worse, silently mis-parse (e.g. a bare "2026" / "true" coerces to a non-string
            // key that fabric-cicd's string lookup never matches → the find/replace silently skips and the dev id ships).
            if (!System.Text.RegularExpressions.Regex.IsMatch(env, @"^[A-Za-z0-9_.\-]+$"))
                return new CicdScaffold { Error = "Invalid environment name — use letters, digits, '_', '.', '-' only (it becomes a YAML key)." };
            var wsId = string.IsNullOrWhiteSpace(workspaceId) ? "00000000-0000-0000-0000-000000000000" : workspaceId.Trim();
            var files = new System.Collections.Generic.List<CicdFile>
            {
                new CicdFile { Path = "parameter.yml", Content = CicdTemplates.ParameterYml(env, wsId) },
                new CicdFile { Path = ".deploy/deploy.py", Content = CicdTemplates.DeployPy(env) },
                ado ? new CicdFile { Path = "azure-pipelines.yml", Content = CicdTemplates.AdoPipeline(env) }
                    : new CicdFile { Path = ".github/workflows/fabric-cicd.yml", Content = CicdTemplates.GithubWorkflow(env) },
            };
            var scaffold = new CicdScaffold
            {
                Files = files.ToArray(),
                Note = $"fabric-cicd scaffold for {(ado ? "Azure DevOps" : "GitHub Actions")}. Edit parameter.yml's find/replace + set the workspace id/secrets, commit, and CI runs the real fabric-cicd publish_all_items. No live write was made.",
            };
            if (write)
            {
                var rootDir = await ResolveRepoRootForWriteAsync();
                if (rootDir == null) { scaffold.Error = "Open a model that lives on disk (ideally a git repo) to write the scaffold."; return scaffold; }
                var written = new System.Collections.Generic.List<string>();
                var skipped = new System.Collections.Generic.List<string>();
                try
                {
                    foreach (var f in files)
                    {
                        var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootDir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                        // Never clobber an existing file — parameter.yml especially is the one the user hand-edits. Skip
                        // it and report so a second "Write to repo" can't silently destroy a tuned config (no undo here).
                        if (System.IO.File.Exists(abs)) { skipped.Add(abs); continue; }
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs));
                        await System.IO.File.WriteAllTextAsync(abs, f.Content);
                        written.Add(abs);
                    }
                    scaffold.Written = written.Count > 0; scaffold.WrittenPaths = written.ToArray(); scaffold.SkippedPaths = skipped.ToArray();
                    if (written.Count > 0) scaffold.Note += $" Wrote {written.Count} file(s) into {rootDir}.";
                    if (skipped.Count > 0) scaffold.Note += $" Skipped {skipped.Count} existing file(s) (not overwritten): {string.Join(", ", skipped.Select(System.IO.Path.GetFileName))}.";
                }
                catch (Exception ex) { scaffold.Error = FabricRest.Scrub(ex.Message); }
            }
            return scaffold;
        }

        // The publishable model FOLDER: the open session's on-disk folder if it carries a Fabric definition
        // (a definition/ folder or model.bim), else a *.SemanticModel folder located within the repo, else null.
        private string ResolveModelFolderOrNull()
        {
            var src = _sessions.Current?.SourcePath;
            if (string.IsNullOrEmpty(src)) return null;
            var full = System.IO.Path.GetFullPath(src);
            var dir = System.IO.Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full);
            if (HasDefinition(dir)) return dir;
            var found = LocateModelInRepo(dir);
            var foundDir = found == null ? null : (System.IO.Directory.Exists(found) ? found : System.IO.Path.GetDirectoryName(found));
            return HasDefinition(foundDir) ? foundDir : null;
        }

        // A publishable Fabric .SemanticModel folder: definition.pbism (REQUIRED for both formats) PLUS exactly one of
        // a definition/ folder (TMDL) or model.bim (TMSL). Requiring pbism pre-flights the most common CorruptedPayload
        // (a folder missing it would otherwise pass the gate and fail server-side AFTER the destructive override).
        private static bool HasDefinition(string dir)
            => dir != null
               && System.IO.File.Exists(System.IO.Path.Combine(dir, "definition.pbism"))
               && (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "definition")) || System.IO.File.Exists(System.IO.Path.Combine(dir, "model.bim")));

        // Which on-disk files are NOT Fabric definition parts and must be excluded from updateDefinition:
        //  • the editor-local .pbi cache (never part of the item);
        //  • .platform — item-folder ENVELOPE metadata (type/displayName), not a definition part; it only applies with
        //    ?updateMetadata=true, which we don't set (publish is definition-only, never renames the target). fabric-cicd
        //    excludes it too.
        //  • model.bim WHEN a definition/ folder is also present — TMDL and TMSL are mutually exclusive; shipping both
        //    is a guaranteed CorruptedPayload, so prefer the TMDL definition/ tree and drop the stale .bim.
        private static bool SkipPart(string rel, bool hasDefinitionFolder)
            => rel.StartsWith(".pbi/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/.pbi/")
               || rel.StartsWith(".semanticus/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/.semanticus/") // engine-owned diagram-layout sidecar — never a Fabric definition part
               || rel.Equals(".platform", StringComparison.OrdinalIgnoreCase)
               || (hasDefinitionFolder && rel.Equals("model.bim", StringComparison.OrdinalIgnoreCase));

        // Path-only enumeration of the definition parts (relative POSIX paths) — for the dry-run preview (no file reads).
        internal static System.Collections.Generic.List<string> EnumeratePartPaths(string root)
        {
            var hasDef = System.IO.Directory.Exists(System.IO.Path.Combine(root, "definition"));
            var list = new System.Collections.Generic.List<string>();
            foreach (var f in System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!SkipPart(rel, hasDef)) list.Add(rel);
            }
            return list;
        }

        // Enumerate the model folder into Fabric definition parts (relative POSIX path + base64 of the RAW file bytes +
        // InlineBase64). Reads bytes — used for the live POST only. Honours SkipPart (the .pbi cache / .platform / a
        // stale model.bim beside definition/).
        internal static System.Collections.Generic.List<(string path, string payload, string payloadType)> EnumerateModelParts(string root)
        {
            var hasDef = System.IO.Directory.Exists(System.IO.Path.Combine(root, "definition"));
            var list = new System.Collections.Generic.List<(string, string, string)>();
            foreach (var f in System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                if (SkipPart(rel, hasDef)) continue;
                list.Add((rel, Convert.ToBase64String(System.IO.File.ReadAllBytes(f)), "InlineBase64"));
            }
            return list;
        }

        // Where to land a generated scaffold: the git repo root (so parameter.yml + .github/azure-pipelines sit at
        // the top), falling back to the open model's working dir.
        private async Task<string> ResolveRepoRootForWriteAsync()
        {
            var dir = GitWorkingDirOrNull();
            if (dir == null) return null;
            try { var rr = await GitCli.RepoRootAsync(dir); if (!string.IsNullOrEmpty(rr)) return rr; } catch { }
            return dir;
        }

        public async Task<string> CreateCalculatedColumnAsync(string tableRef, string name, string expression, string origin)
        {
            await EnforceBindingAsync("create_calculated_column", origin);   // §9c op→workflow binding
            var verified = await VerifiedGuardAsync(expression, "this new calculated column");   // Verified Mode: refuse invalid DAX before it commits
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A column name is required.");
            var s = _sessions.Require();
            string newRef = null;
            var rev = await s.MutateAsync(origin, $"create calculated column {name}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t)) throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                if (t.Columns.Contains(name)) throw new InvalidOperationException($"Table '{t.Name}' already has a column '{name}'.");
                newRef = ObjectRefs.For(t.AddCalculatedColumn(name, expression ?? string.Empty));
            });
            await RecordVerifiedModeEditAsync(verified, s, "create_calculated_column", newRef, expression, rev, origin);
            return newRef;
        }

        public async Task<string> CreateRelationshipAsync(string fromColumnRef, string toColumnRef, string crossFilter, bool? isActive, string origin)
        {
            await EnforceBindingAsync("create_relationship", origin);   // §9c op→workflow binding
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, "create relationship", m =>
            {
                if (!(ObjectRefs.Resolve(m, fromColumnRef) is Column from)) throw new InvalidOperationException($"{fromColumnRef} is not a column (the many / foreign-key side) — pass a 'column:Table/Name' ref; run list_columns on the table to see its columns.");
                if (!(ObjectRefs.Resolve(m, toColumnRef) is Column to)) throw new InvalidOperationException($"{toColumnRef} is not a column (the one / lookup side) — pass a 'column:Table/Name' ref; run list_columns on the table to see its columns.");
                newRef = ObjectRefs.For(CreateRelationshipCore(m, from, to, crossFilter, isActive));
            });
            return newRef;
        }

        private static SingleColumnRelationship CreateRelationshipCore(Model m, Column from, Column to, string crossFilter, bool? isActive, string cardinality = null)
        {
            if (from == to) throw new InvalidOperationException("A relationship needs two different columns.");
            CrossFilteringBehavior? cf = null;
            if (!string.IsNullOrWhiteSpace(crossFilter))
            {
                if (!Enum.TryParse<CrossFilteringBehavior>(crossFilter.Trim(), true, out var parsed))
                    throw new ArgumentException($"Unknown crossFilter '{crossFilter}'. Use OneDirection or BothDirections.");
                cf = parsed;
            }
            var (fromCard, toCard) = ParseSpecCardinality(cardinality);   // null/manyToOne = today's Many→One default
            // Two ACTIVE relationships on the same column pair is an invalid model — require the new one be inactive.
            if ((isActive ?? true) && m.Relationships.OfType<SingleColumnRelationship>().Any(r => r.IsActive &&
                    ((r.FromColumn == from && r.ToColumn == to) || (r.FromColumn == to && r.ToColumn == from))))
                throw new InvalidOperationException("An active relationship already exists between these columns; pass isActive=false to add an inactive one.");
            var rel = m.AddRelationship();
            rel.FromColumn = from;   // From end (FK side under the default many→one)
            rel.ToColumn = to;       // To end (lookup side under the default many→one)
            rel.FromCardinality = fromCard;
            rel.ToCardinality = toCard;
            if (cf.HasValue) rel.CrossFilteringBehavior = cf.Value;
            if (isActive.HasValue) rel.IsActive = isActive.Value;
            return rel;
        }

        // Maps a spec Cardinality token (reads From→To) to TOM end cardinalities. Null/empty = manyToOne, which is
        // today's FK→PK behavior, so pre-cardinality specs (and create_relationship, which passes null) are unchanged.
        private static (RelationshipEndCardinality from, RelationshipEndCardinality to) ParseSpecCardinality(string cardinality)
        {
            switch ((cardinality ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "manytoone": return (RelationshipEndCardinality.Many, RelationshipEndCardinality.One);
                case "onetoone": return (RelationshipEndCardinality.One, RelationshipEndCardinality.One);
                case "onetomany": return (RelationshipEndCardinality.One, RelationshipEndCardinality.Many);
                case "manytomany": return (RelationshipEndCardinality.Many, RelationshipEndCardinality.Many);
                default: throw new ArgumentException($"Unknown cardinality '{cardinality}'. Use manyToOne, oneToOne, oneToMany, or manyToMany.");
            }
        }

        // Inverse of ParseSpecCardinality for autogenerate: null for the common many→one (kept implicit for a clean
        // spec), an explicit token otherwise; null too when an end is unspecified, so the build falls back to the default.
        private static string SpecCardinalityToken(string fromCardinality, string toCardinality)
        {
            string End(string c) => string.Equals(c, "Many", StringComparison.OrdinalIgnoreCase) ? "many"
                                  : string.Equals(c, "One", StringComparison.OrdinalIgnoreCase) ? "one" : null;
            var f = End(fromCardinality); var t = End(toCardinality);
            if (f == null || t == null) return null;              // an end isn't One/Many — leave the default many→one
            if (f == "many" && t == "one") return null;          // the default, left implicit
            if (f == "one" && t == "one") return "oneToOne";
            if (f == "one" && t == "many") return "oneToMany";
            return "manyToMany";                                 // many & many
        }

        public async Task<string> CreateHierarchyAsync(string tableRef, string name, string[] levelColumns, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A hierarchy name is required.");
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create hierarchy {name}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t)) throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                var cols = levelColumns ?? Array.Empty<string>();
                foreach (var c in cols) if (!t.Columns.Contains(c)) throw new InvalidOperationException($"Table '{t.Name}' has no column '{c}' for a hierarchy level — run list_columns on '{t.Name}' to see its columns, then pass existing column names.");
                newRef = ObjectRefs.For(t.AddHierarchy(name, null, cols));
            });
            return newRef;
        }

        public async Task<string> CreateCalculationGroupAsync(string name, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A calculation group name is required.");
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create calculation group {name}", m =>
            {
                if ((m.Database?.CompatibilityLevel ?? 0) < 1470)
                    throw new InvalidOperationException($"Calculation groups require compatibility level 1470 or higher (this model is {m.Database?.CompatibilityLevel ?? 0}; raise it with set_compatibility_level).");
                if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
                newRef = ObjectRefs.For(m.AddCalculationGroup(name));
            });
            return newRef;
        }

        public async Task<string> CreateCalculationItemAsync(string calcGroupRef, string name, string expression, string origin)
        {
            await EnforceBindingAsync("create_calculation_item", origin);   // §9c op→workflow binding
            var verified = await VerifiedGuardAsync(expression, "this new calculation item");   // Verified Mode: refuse invalid DAX before it commits
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A calculation item name is required.");
            var s = _sessions.Require();
            string newRef = null;
            var rev = await s.MutateAsync(origin, $"create calculation item {name}", m =>
            {
                if (!(ObjectRefs.Resolve(m, calcGroupRef) is CalculationGroupTable cg))
                    throw new InvalidOperationException($"{calcGroupRef} is not a calculation group — run list_calculation_groups to see them; create one with create_calculation_group first.");
                if (cg.CalculationItems.Contains(name)) throw new InvalidOperationException($"Calculation group '{cg.Name}' already has an item '{name}'.");
                newRef = ObjectRefs.For(cg.AddCalculationItem(name, expression ?? string.Empty));
            });
            await RecordVerifiedModeEditAsync(verified, s, "create_calculation_item", newRef, expression, rev, origin);
            return newRef;
        }

        // List the model's calculation groups + their ordered items (with DAX + format-string), for the
        // Advanced-Modelling calc-group editor. Read-only; the write ops (create group/item, precedence,
        // format) already exist.
        public Task<CalcGroupInfo[]> ListCalculationGroupsAsync() =>
            _sessions.Require().ReadAsync(m => m.Tables.OfType<CalculationGroupTable>()
                .OrderBy(cg => cg.Name, StringComparer.OrdinalIgnoreCase)
                .Select(cg => new CalcGroupInfo
                {
                    Ref = ObjectRefs.For(cg),
                    Name = cg.Name,
                    Precedence = cg.CalculationGroupPrecedence,
                    Items = cg.CalculationItems.OrderBy(ci => ci.Ordinal).Select(ci => new CalcItemInfo
                    {
                        Ref = ObjectRefs.For(ci),
                        Name = ci.Name,
                        Ordinal = ci.Ordinal,
                        Expression = ci.Expression,
                        FormatStringExpression = ci.FormatStringExpression,
                    }).ToArray(),
                }).ToArray());

        // --- Perspectives (Studio v2 Advanced Modelling) -------------------------------------------------
        // A perspective is a named, curated subset of the model's objects (a focused Q&A / report view). The
        // TOMWrapper models membership as a per-object InPerspective[perspective] indexer on every
        // ITabularPerspectiveObject (Table/Column/Measure/Hierarchy). Create + membership are single-object
        // ops (free); delete/rename reuse delete_object / rename_object (Perspective is a TabularNamedObject).

        public async Task<string> CreatePerspectiveAsync(string name, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A perspective name is required.");
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"create perspective {name}", m =>
            {
                if (m.Perspectives.Contains(name)) throw new InvalidOperationException($"A perspective named '{name}' already exists.");
                newRef = ObjectRefs.For(m.AddPerspective(name));
            });
            return newRef;
        }

        public async Task<SetResult> SetPerspectiveMemberAsync(string perspectiveRef, string objRef, bool include, string origin)
        {
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, $"{(include ? "add to" : "remove from")} perspective {perspectiveRef}", m =>
            {
                if (!(ObjectRefs.Resolve(m, perspectiveRef) is Perspective p))
                    throw new InvalidOperationException($"{perspectiveRef} is not a perspective — run get_perspectives to see them; create one with create_perspective.");
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");
                if (!(obj is ITabularPerspectiveObject po))
                    throw new InvalidOperationException($"{objRef} ({ObjectRefs.KindOf(obj)}) can't be put in a perspective — only tables, columns, measures and hierarchies can.");
                // Setting a table cascades to its columns/measures/hierarchies (PerspectiveTableIndexer).
                po.InPerspective[p] = include;
            });
            return new SetResult { Revision = rev, Changed = true };
        }

        public Task<PerspectiveInfo[]> GetPerspectivesAsync() =>
            _sessions.Require().ReadAsync(m => m.Perspectives.Select(p => new PerspectiveInfo
            {
                Ref = ObjectRefs.For(p),
                Name = p.Name,
                Description = p.Description,
                Members = PerspectiveObjects(m).Where(o => o.InPerspective[p]).Select(o => ObjectRefs.For((ITabularObject)o)).ToArray(),
            }).ToArray());

        // Every object that can be a perspective member: each table + its columns/measures/hierarchies.
        private static IEnumerable<ITabularPerspectiveObject> PerspectiveObjects(Model m)
        {
            foreach (var t in m.Tables)
            {
                yield return t;
                foreach (var c in t.Columns) yield return c;
                foreach (var me in t.Measures) yield return me;
                foreach (var h in t.Hierarchies) yield return h;
            }
        }

        /// <summary>Set (or clear) a calculation item's DYNAMIC format-string expression — the DAX that overrides the
        /// format of measures evaluated under this item (e.g. a "% of Total" item rendering 0.0%). Empty clears it
        /// (the item falls back to the base/model format). Change-tracked + undoable via the wrapper setter.</summary>
        public async Task<SetResult> SetCalcItemFormatStringAsync(string calcItemRef, string formatExpression, string origin)
        {
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, $"set calc-item format string {calcItemRef}", m =>
            {
                if (!(ObjectRefs.Resolve(m, calcItemRef) is CalculationItem ci))
                    throw new InvalidOperationException($"{calcItemRef} is not a calculation item — run list_calculation_groups to see items; create one with create_calculation_item.");
                ci.FormatStringExpression = string.IsNullOrWhiteSpace(formatExpression) ? null : formatExpression.Trim();
            });
            return new SetResult { Revision = rev, Changed = true };
        }

        /// <summary>Set a calculation group's precedence (an integer; a HIGHER precedence is applied first when
        /// several calc groups combine). Distinct precedences make the combination order deterministic. Undoable.</summary>
        public async Task<SetResult> SetCalcGroupPrecedenceAsync(string calcGroupRef, int precedence, string origin)
        {
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, $"set calc-group precedence {calcGroupRef}", m =>
            {
                if (!(ObjectRefs.Resolve(m, calcGroupRef) is CalculationGroupTable cg))
                    throw new InvalidOperationException($"{calcGroupRef} is not a calculation group — run list_calculation_groups to see them; create one with create_calculation_group.");
                cg.CalculationGroupPrecedence = precedence;
            });
            return new SetResult { Revision = rev, Changed = true };
        }

        public async Task<SetResult> DeleteObjectAsync(string objRef, string origin)
        {
            var s = _sessions.Require();
            // Resolve first: an absent/unresolvable ref is a true net-zero — no revision bump, no broadcast (the
            // mutate path always increments + publishes, even on an empty delta set).
            var exists = await s.ReadAsync(m => ObjectRefs.Resolve(m, objRef) != null);
            if (!exists) return new SetResult { Revision = s.Revision, Changed = false };
            var rev = await s.MutateAsync(origin, $"delete {objRef}", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef);
                DeleteResolvedObject(m, obj, objRef);
            });
            return new SetResult { Revision = rev, Changed = true };
        }

        private static string CreatePushStagingPath(System.Collections.Generic.List<Action> cleanups)
        {
            // A process-wide parent lets a concurrent cleanup invalidate another push between write and SaveChanges.
            // Give every operation ownership of one directory so cleanup can never cross that boundary.
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus-push-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            cleanups.Add(() => { try { System.IO.Directory.Delete(dir, true); } catch { } });
            return System.IO.Path.Combine(dir, "model.bim");
        }

        private static void DeleteResolvedObject(Model m, ITabularNamedObject obj, string objRef)
        {
            if (obj == null)
                throw new InvalidOperationException($"Object not found: {objRef} - run list_objects or search_model to find the exact ref, then stage the removal again.");
            // Both the direct delete and a reviewed Change Plan removal must share this TOMWrapper guard.
            if (obj is Table && m.Database.CompatibilityLevel >= 1400 && m.Roles.Any(r => r.MetadataPermission == null))
                throw new InvalidOperationException(
                    "Can't delete this table: the model was upgraded across compatibility level 1400 while it had existing roles, " +
                    "and TOMWrapper left those roles' object-level security uninitialized (a known vendored limitation). " +
                    "Reopen/reload the model, then delete it.");
            obj.Delete();
        }

        public async Task<string> DuplicateObjectAsync(string objRef, string newName, string targetRef, string origin)
        {
            var s = _sessions.Require();
            string newRef = null;
            await s.MutateAsync(origin, $"duplicate {objRef}", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");

                // Optional paste-target CONTAINER (the tree's copy/paste). Resolving to the source's own container
                // collapses to the classic beside-the-original duplicate, so a caller can always pass its selection.
                ITabularNamedObject target = null;
                if (!string.IsNullOrWhiteSpace(targetRef))
                    target = ObjectRefs.Resolve(m, targetRef) ?? throw new InvalidOperationException(
                        $"{targetRef} not found — pass a table ref (e.g. 'table:Sales') to copy into, or omit targetRef to duplicate beside the original.");

                ITabularObject copy;
                switch (obj)
                {
                    case Measure me:
                    {
                        var tt = CrossTableTarget(target, me.Table, "a measure");
                        if (tt == null) { copy = me.Clone(newName); break; }
                        // Measure names are model-wide unique and must not collide with a column on the landing
                        // table. Clone() names against the SOURCE collection, so cross-table we name against the
                        // TARGET ourselves: clone under a throwaway name, rename inside the same undo batch.
                        var final = UniqueCopyName(string.IsNullOrEmpty(newName) ? me.Name : newName,
                            n => tt.Model.AllMeasures.Any(x => NameEq(x.Name, n)) || tt.Columns.Any(x => NameEq(x.Name, n)));
                        var mc = me.Clone(TempCloneName(), true, tt);
                        mc.Name = final;
                        copy = mc;
                        break;
                    }
                    // Columns and hierarchies stay on their own table: a column's data (source binding / row
                    // context) and a hierarchy's levels only exist there — a cross-table clone would dangle.
                    case CalculatedTableColumn ctc:
                        RefuseCrossTable(target, ctc.Table, "a calc-table column", "it is produced by its table's DAX expression — duplicate it on its own table, or copy the expression into the target table's DAX");
                        copy = ctc.Clone(newName); break;
                    case CalculatedColumn cc:
                        RefuseCrossTable(target, cc.Table, "a column", "a column's data lives in its own table — duplicate it there, or create a new calculated column on the target table");
                        copy = cc.Clone(newName); break;
                    case DataColumn dc:
                        RefuseCrossTable(target, dc.Table, "a column", "a data column is bound to its own table's source query — duplicate it there, or add the column in the target table's source");
                        copy = dc.Clone(newName); break;
                    case Hierarchy h:
                        RefuseCrossTable(target, h.Table, "a hierarchy", "its levels are built from its own table's columns — duplicate it there, or create a new hierarchy on the target table from that table's columns");
                        copy = h.Clone(newName); break;
                    case CalculationItem ci:
                    {
                        var tg = target == null || ReferenceEquals(target, ci.CalculationGroupTable) ? null
                            : target as CalculationGroupTable ?? throw new InvalidOperationException(
                                $"Can't paste a calculation item onto {ObjectRefs.KindOf(target)} '{target.Name}' — the target must be a calculation group (pass its 'table:' ref).");
                        if (tg == null) { copy = ci.Clone(newName); break; }
                        var final = UniqueCopyName(string.IsNullOrEmpty(newName) ? ci.Name : newName,
                            n => tg.CalculationItems.Any(x => NameEq(x.Name, n)));
                        var cic = ci.Clone(TempCloneName(), tg.CalculationGroup);
                        cic.Name = final;
                        copy = cic;
                        break;
                    }
                    case Table t:   // Table, CalculatedTable (incl. field parameters), CalculationGroupTable — Clone dispatches internally
                        if (target != null && !ReferenceEquals(target, t)) throw new InvalidOperationException(
                            "Tables, calculation groups and field parameters duplicate at model scope — omit targetRef.");
                        copy = t.Clone(newName); break;
                    default:
                        throw new InvalidOperationException($"Duplicate is not supported for {ObjectRefs.KindOf(obj)} (supported: measure, calculated/data/calc-table column, hierarchy, calculation item, table / calculated table / field parameter, calculation group).");
                }
                newRef = ObjectRefs.For(copy);
            });
            return newRef;
        }

        // ---- duplicate-object helpers (cross-container paste) ------------------------------------------

        /// <summary>The landing table when the duplicate crosses containers; null for the in-place duplicate.</summary>
        private static Table CrossTableTarget(ITabularNamedObject target, Table source, string what)
        {
            if (target == null || ReferenceEquals(target, source)) return null;
            if (target is Table t) return t;
            throw new InvalidOperationException($"Can't paste {what} onto {ObjectRefs.KindOf(target)} '{target.Name}' — the target must be a table (pass a 'table:' ref).");
        }

        private static void RefuseCrossTable(ITabularNamedObject target, Table source, string what, string why)
        {
            if (target == null || ReferenceEquals(target, source)) return;
            throw new InvalidOperationException($"Can't paste {what} onto '{target.Name}': {why}.");
        }

        private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>First free name: the desired one, else "desired 2", "desired 3", … (case-insensitive, AS name semantics).</summary>
        private static string UniqueCopyName(string desired, Func<string, bool> taken)
        {
            if (!taken(desired)) return desired;
            for (var i = 2; ; i++) { var c = desired + " " + i; if (!taken(c)) return c; }
        }

        /// <summary>A collision-proof scratch name for clone-then-rename: Clone() names against the SOURCE collection,
        /// so a cross-container copy is created under this throwaway and renamed to its target-safe name in-batch.</summary>
        private static string TempCloneName() => "__paste_" + Guid.NewGuid().ToString("N");

        /// <summary>The column data types set_column_data_type accepts — THE single source for the property grid's
        /// Data Type dropdown (get_properties Options), so the pick list can never drift from what the engine parses.</summary>
        internal static readonly string[] SettableColumnDataTypes = { "String", "Int64", "Decimal", "Double", "DateTime", "Boolean" };

        // Allow-list (not raw Enum.TryParse, which would accept internal/unsupported DataType members + numeric strings).
        private static DataType ParseDataType(string s)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) return DataType.String;
            var match = SettableColumnDataTypes.FirstOrDefault(n => n.Equals(t, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown data type '{s}'. Use {string.Join(", ", SettableColumnDataTypes)}.");
            return (DataType)Enum.Parse(typeof(DataType), match);
        }

        /// <summary>What references this object (its DAX dependents) — so a caller can check before delete/rename.
        /// Reads the wrapper's ReferencedBy graph; empty for objects that aren't DAX-dependable.</summary>
        public Task<DependentInfo[]> GetDependentsAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");
                if (!(obj is IDaxObject dep)) return System.Array.Empty<DependentInfo>();
                return dep.ReferencedBy.OfType<ITabularNamedObject>()
                    .Select(o => new DependentInfo { Ref = ObjectRefs.For(o), Name = o.Name, Kind = ObjectRefs.KindOf(o) })
                    .ToArray();
            });
        }

        /// <summary>Read-only: the objects this object's DAX DEPENDS ON (its callees) — the inverse direction of the dependents read.
        /// Works for any DAX-bearing object (measure / calculated column / calc item / UDF / table partition). Use it
        /// to see what a function or measure consumes before editing it.</summary>
        public Task<DependentInfo[]> GetDependenciesAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");
                if (!(obj is IDaxDependantObject dep)) return System.Array.Empty<DependentInfo>();
                return dep.DependsOn.Keys.OfType<ITabularNamedObject>()
                    .Select(o => new DependentInfo { Ref = ObjectRefs.For(o), Name = o.Name, Kind = ObjectRefs.KindOf(o) })
                    .ToArray();
            });
        }

        // ---- Lineage & Impact ("Measure Killer") — Phase 1: OFFLINE, model-internal lineage --------------------
        // Assembly over the shipped TOM dependency surface (ReferencedBy/DependsOn + relationships/partitions). All
        // three run on the dispatcher via ReadAsync (they walk live wrapper objects). The unused sweep is MODEL-ONLY
        // until the published-report edges land (Phases 2-3) — the result carries that caveat. See Lineage/.

        /// <summary>Read-only: the whole model's lineage graph — source→table→column/measure + the DAX dependency
        /// graph + relationship edges (offline, from TOM). Powers the Studio Lineage tab and the agent's lineage tools.</summary>
        public Task<LineageResult> GetLineageAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(Lineage.LineageGraph.Build);
        }

        /// <summary>Read-only forward impact — everything (transitively) that breaks if <paramref name="objRef"/>
        /// changes/disappears (DAX dependents + a column's relationships/hierarchies/sort-by), with BFS depth.</summary>
        public Task<ImpactResult> ImpactOfAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => Lineage.LineageGraph.Impact(m, objRef));
        }

        /// <summary>Read-only reverse "safe-to-remove" sweep over every measure/column — the Measure-Killer headline.
        /// Conservative + tri-state (safe / used-only-by-an-unused-object / caution); MODEL-ONLY in Phase 1.</summary>
        public Task<UnusedResult> UnusedObjectsAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(Lineage.LineageGraph.Unused);
        }

        /// <summary>Read-only report-aware lineage: parse local PBIR report definition(s), reconcile their field
        /// usage to the open model, and recompute safe-to-remove EXCLUDING report-used fields. PBIR is parsed off the
        /// dispatcher (file IO); reconciliation + the sweep run on it. The cloud getDefinition path reuses the parser.</summary>
        public async Task<ReportAnalysisResult> AnalyzeReportsAsync(string[] paths)
        {
            var s = _sessions.Require();
            var parsed = (paths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => (path: p, result: Lineage.ReportDefinitionReader.ReadLocalPbir(p)))
                .ToList();
            return await s.ReadAsync(m => Lineage.LineageGraph.AnalyzeReports(m, parsed));
        }

        // ---- Lineage Phase 3: the CLOUD report layer (published reports, read-only behaviour) -------------------
        // Discovery uses the NON-ADMIN Power BI REST per-workspace list (it carries each report's datasetId, which the
        // Fabric item list omits); the report definition uses Fabric getDefinition (PBIR). The two use DIFFERENT token
        // audiences (Power BI / XMLA vs Fabric), so each acquires its own. No model mutation happens — but getDefinition
        // requires a write-CAPABLE Fabric scope, surfaced as one-time consent (see AnalyzeCloudReportsAsync). A model
        // must be open: report field usage is reconciled to it BY NAME (same as the local path).

        /// <summary>Read-only: list the published reports in a Fabric/Power BI workspace (id, name, datasetId,
        /// reportType, webUrl) so a caller can pick the ones that bind to the open model's dataset. Non-admin path.</summary>
        public async Task<CloudReport[]> ListReportsAsync(string workspaceId, string authMode, string tenantId)
        {
            try
            {
                var token = await EntraToken.AcquireAsync(authMode, null, System.Threading.CancellationToken.None, tenantId).ConfigureAwait(false);
                return await PowerBiReports.ListReportsAsync(workspaceId, token, System.Threading.CancellationToken.None).ConfigureAwait(false);
            }
            // Golden Rule #1: an Azure.Identity auth failure (or any REST throw) must NOT cross a door un-scrubbed —
            // its message can carry a token. PowerBiReports/FabricRest already scrub their own errors; this guards the
            // token-acquisition leg, matching the deploy/git methods.
            catch (Exception ex) when (ex is not ArgumentException) { throw new InvalidOperationException(FabricRest.Scrub(ex.Message)); }
        }

        /// <summary>Read-only report-aware safe-to-remove over CLOUD reports: fetch each report's PBIR via Fabric
        /// getDefinition, reconcile its field usage to the open model, and recompute safe-to-remove EXCLUDING
        /// report-used fields. <paramref name="reportIds"/> empty = every non-paginated report in the workspace.
        /// Behaviour is read-only, but getDefinition needs a write-capable Fabric scope (Item.ReadWrite.All /
        /// Report.ReadWrite.All) + Contributor; <paramref name="consent"/> must be true to acknowledge that. Per-report
        /// failures (paginated/RDL, a sensitivity-label block, a fetch error) are recorded as unreadable, never thrown —
        /// the verdict degrades to a model-aware-but-incomplete caveat rather than silently overstating "safe".</summary>
        public async Task<ReportAnalysisResult> AnalyzeCloudReportsAsync(string workspaceId, string[] reportIds, bool consent, string authMode, string tenantId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("A workspace id is required.");
            if (!consent)
                throw new InvalidOperationException(
                    "Reading a published report's definition uses the Fabric Get Report Definition API, which requires a " +
                    "WRITE-capable scope (Item.ReadWrite.All / Report.ReadWrite.All) and the Contributor role on the workspace — " +
                    "even though Semanticus only READS the definition and never modifies the report. Re-run with consent=true to " +
                    "acknowledge and proceed.");

            var s = _sessions.Require();   // a model must be open (we reconcile report field usage to it by name)
            var ct = System.Threading.CancellationToken.None;

            // Discover the workspace's reports (Power BI scope) to resolve names, skip paginated/RDL, and validate ids,
            // then mint the Fabric token getDefinition needs. Scrub-guard this leg too (Golden Rule #1) — an
            // Azure.Identity auth failure here would otherwise cross a door un-scrubbed (the per-report loop below is
            // already guarded). A duplicate-id listing must not abort the batch, so group rather than ToDictionary.
            Dictionary<string, CloudReport> byId;
            List<string> wanted;
            string fabricToken;
            try
            {
                var pbiToken = await EntraToken.AcquireAsync(authMode, null, ct, tenantId).ConfigureAwait(false);
                var all = await PowerBiReports.ListReportsAsync(workspaceId, pbiToken, ct).ConfigureAwait(false);
                byId = all.Where(r => !string.IsNullOrEmpty(r.Id))
                    .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // The target set: explicit ids (preserving the caller's order), else every NON-paginated report in the
                // workspace (empty = "analyze all PBIR reports" — RDL is skipped so it can't inflate the unreadable count).
                wanted = (reportIds != null && reportIds.Any(x => !string.IsNullOrWhiteSpace(x)))
                    ? reportIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : byId.Values.Where(r => !string.Equals(r.ReportType, "PaginatedReport", StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList();

                fabricToken = await EntraToken.AcquireFabricAsync(authMode, null, ct, tenantId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ArgumentException) { throw new InvalidOperationException(FabricRest.Scrub(ex.Message)); }

            var parsed = new List<(string path, string error, Lineage.ReportDefinitionReader.ParseResult result)>();
            foreach (var id in wanted)
            {
                byId.TryGetValue(id, out var meta);
                var name = string.IsNullOrEmpty(meta?.Name) ? id : meta.Name;
                var empty = new Lineage.ReportDefinitionReader.ParseResult();

                if (meta == null) { parsed.Add((name, "Report id not found in this workspace.", empty)); continue; }
                if (string.Equals(meta.ReportType, "PaginatedReport", StringComparison.OrdinalIgnoreCase))
                { parsed.Add((name, "Paginated (RDL) report — its field usage is not parsed.", empty)); continue; }

                try
                {
                    var parts = await FabricRest.GetReportDefinitionAsync(workspaceId, id, "PBIR", fabricToken, ct).ConfigureAwait(false);
                    var pr = Lineage.ReportDefinitionReader.Parse(parts.Select(p => (p.Path, p.Content)));   // path carries page/visual ids
                    pr.DefinitionFound = parts.Length > 0;
                    parsed.Add(parts.Length > 0
                        ? (name, (string)null, pr)
                        : (name, "getDefinition returned no PBIR parts (a PBIRLegacy report, or download disabled).", pr));
                }
                catch (Exception ex)
                {
                    parsed.Add((name, FabricRest.Scrub(ex.Message), empty));   // sensitivity-label block / 403 / transport — fail-loud, keep the batch going
                }
            }

            return await s.ReadAsync(m => Lineage.LineageGraph.AnalyzeReports(m, parsed)).ConfigureAwait(false);
        }

        /// <summary>Script one or many objects to text (read-only — never mutates). format: 'dax' (annotated
        /// expressions), 'tmsl' (the TE2 JSON object container), or 'tmdl' (per-object TMDL). Powers the tree's
        /// Script ▸ action incl. multi-select. Unresolvable refs are skipped; throws only if nothing resolves.</summary>
        public Task<string> ScriptObjectsAsync(string[] refs, string format)
        {
            var s = _sessions.Require();
            var fmt = (format ?? "dax").Trim().ToLowerInvariant();
            return s.ReadAsync(m =>
            {
                var resolved = (refs ?? System.Array.Empty<string>())
                    .Select(r => ObjectRefs.Resolve(m, r))
                    .Where(o => o != null)
                    .ToList();
                if (resolved.Count == 0)
                    throw new InvalidOperationException("script_objects: no resolvable object refs supplied.");

                switch (fmt)
                {
                    case "tmsl":
                    case "json":
                    {
                        // TMSL createOrReplace only supports TOP-LEVEL objects (tables, roles) — not child objects like
                        // measures/columns. So map each selection to its smallest scriptable container (its table, or
                        // itself when already top-level), dedupe, and script those. Anything JsonScripter still rejects
                        // degrades to a comment rather than throwing — so a mixed multi-select never fails the batch.
                        var containers = new List<ITabularNamedObject>();
                        foreach (var o in resolved)
                        {
                            var c = ScriptContainer(o);
                            if (c != null && !containers.Any(x => ReferenceEquals(x, c))) containers.Add(c);
                        }
                        var sb = new System.Text.StringBuilder();
                        foreach (var c in containers.OfType<TabularNamedObject>())
                        {
                            try { sb.AppendLine(Scripter.ScriptCreateOrReplace(c)); }
                            catch (Exception ex) { sb.AppendLine($"// {ObjectRefs.For(c)}: not individually scriptable as TMSL ({ex.Message})"); }
                            sb.AppendLine();
                        }
                        return sb.ToString().TrimEnd() + "\n";
                    }

                    case "tmdl":
                    {
                        // Per-object TMDL (the model's native, readable on-disk format) via TmdlScripter, which lives in
                        // Core so it can reach the wrapper's protected-internal MetadataObject. TMDL is more granular
                        // than TMSL — it scripts a measure/column on its own — so no container mapping is needed.
                        var sb = new System.Text.StringBuilder();
                        foreach (var o in resolved.OfType<TabularNamedObject>())
                        {
                            try
                            {
                                // Ownership makes an individually scripted child safely round-trippable: apply_tmdl
                                // resolves the ORIGINAL ref, then supplies TOM with the required parent-table wrapper.
                                sb.AppendLine($"// @object {ObjectRefs.For(o)}");
                                sb.AppendLine(TmdlScripter.ScriptTmdl(o));
                            }
                            catch (Exception ex) { sb.AppendLine($"// {ObjectRefs.For(o)}: not scriptable as TMDL ({ex.Message})"); }
                            sb.AppendLine();
                        }
                        return sb.ToString().TrimEnd() + "\n";
                    }

                    case "dax":
                    default:
                    {
                        // The "// @object <ref>" header is a parseable sentinel: this DAX script round-trips through
                        // apply_dax_script (edit the expressions, apply them back). A normal DAX // comment can't collide.
                        var sb = new System.Text.StringBuilder();
                        foreach (var o in resolved)
                        {
                            sb.AppendLine($"// @object {ObjectRefs.For(o)}");
                            var expr = DaxExpr(o);
                            sb.AppendLine(string.IsNullOrWhiteSpace(expr) ? "// (no DAX expression)" : expr);
                            sb.AppendLine();
                        }
                        return sb.ToString().TrimEnd() + "\n";
                    }
                }
            });
        }

        private static string DaxExpr(ITabularNamedObject o)
        {
            switch (o)
            {
                case Measure me: return me.Expression;
                case CalculatedColumn cc: return cc.Expression;
                case CalculatedTable ct: return ct.Expression;
                case CalculationItem ci: return ci.Expression;
                case Function f: return f.Expression;
                default: return null;
            }
        }

        /// <summary>Apply an edited DAX script (the 'dax' output of script_objects, with "// @object &lt;ref&gt;" headers)
        /// back to the model: each block's expression is set on its object in ONE undoable, broadcast batch. Refs that
        /// don't resolve or aren't DAX-expression objects are skipped and reported (never silently dropped).</summary>
        public async Task<ApplyScriptResult> ApplyDaxScriptAsync(string script, string origin)
        {
            var blocks = ParseDaxScript(script);
            if (blocks.Count == 0)
                throw new InvalidOperationException("apply_dax_script: no '// @object <ref>' blocks found — script with format='dax' first, then edit and apply.");

            // Pro gate: applying a MULTI-object DAX script in one atomic batch is the bulk primitive; a single-block
            // script (one object) stays free. Thrown before the mutate, so a refusal leaves the model intact.
            if (blocks.Count > 1)
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "Applying a multi-object DAX script at once",
                    "Apply one object's DAX at a time (update_measure / set_dax).");

            var applied = new List<string>();
            var skipped = new List<string>();
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, "apply DAX script", m =>
            {
                foreach (var (objRef, expr) in blocks)
                {
                    switch (ObjectRefs.Resolve(m, objRef))
                    {
                        case Measure me: me.Expression = expr; applied.Add(objRef); break;
                        case CalculatedColumn cc: cc.Expression = expr; applied.Add(objRef); break;
                        case CalculatedTable ct: ct.Expression = expr; applied.Add(objRef); break;
                        case CalculationItem ci: ci.Expression = expr; applied.Add(objRef); break;
                        case Function f: f.Expression = expr; applied.Add(objRef); break;
                        default: skipped.Add($"{objRef}: unresolved or not a DAX-expression object — only measures, calculated columns/tables, calculation items and functions carry DAX. Fix the '// @object <ref>' header (list_objects shows valid refs) and re-apply."); break;
                    }
                }
            });
            return new ApplyScriptResult { Revision = rev, Applied = applied.ToArray(), Skipped = skipped.ToArray() };
        }

        /// <summary>Parse a DAX script into (ref, expression) blocks. A header line "// @object &lt;ref&gt;" starts a block;
        /// every following line up to the next header is the expression (multi-line preserved, surrounding blanks trimmed).</summary>
        private static List<(string objRef, string expr)> ParseDaxScript(string script)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(script)) return result;
            var header = new System.Text.RegularExpressions.Regex(@"^//\s*@object\s+(.+?)\s*$");   // ref may contain spaces (object names)
            string curRef = null;
            var buf = new System.Text.StringBuilder();
            void Flush()
            {
                if (curRef != null) result.Add((curRef, buf.ToString().Trim('\n', '\r').Trim()));
                buf.Clear();
            }
            foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                var m = header.Match(raw);
                if (m.Success) { Flush(); curRef = m.Groups[1].Value; }
                else if (curRef != null) { buf.Append(raw); buf.Append('\n'); }
            }
            Flush();
            return result;
        }

        /// <summary>Apply an edited TMDL script (the 'tmdl' output of script_objects) back to the model. Top-level
        /// documents apply directly; sentinel-owned measure blocks are grouped beneath partial parent-table documents
        /// because TOM does not accept a bare child object. Everything runs in ONE undoable broadcast batch.</summary>
        public async Task<ApplyScriptResult> ApplyTmdlScriptAsync(string script, string origin)
        {
            var docs = ParseTmdlDocuments(script);
            if (docs.Count == 0)
                throw new InvalidOperationException("apply_tmdl: no top-level TMDL object or sentinel-owned measure found. Use Script > TMDL from the Model tree, edit that document, then apply.");

            // Pro gate: applying MULTIPLE selected objects in one batch is the bulk primitive; a single table/role or
            // measure stays free. Thrown before the mutate, so a refusal leaves the model intact.
            if (docs.Count > 1)
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "Applying a multi-object TMDL script at once",
                    "Apply one object at a time, or use the individual edit tools.");

            var applied = new List<string>();
            var skipped = new List<string>();
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, "apply TMDL script", m =>
            {
                // Resolve + validate every child before the first write. Store strings only: each top-level apply
                // Reinit invalidates wrapper instances, but the prepared partial documents remain safe.
                var measures = new List<(string objRef, string table, string content)>();
                foreach (var doc in docs.Where(d => d.ObjectRef != null))
                {
                    if (TryPrepareMeasureTmdl(m, doc.ObjectRef, doc.Content, out var table, out var content, out var error))
                        measures.Add((doc.ObjectRef, table, content));
                    else
                        skipped.Add($"{doc.ObjectRef}: {error}");
                }

                var topLevel = docs.Where(d => d.LogicalPath != null).ToList();
                var topPaths = new HashSet<string>(topLevel.Select(d => d.LogicalPath), StringComparer.OrdinalIgnoreCase);
                foreach (var doc in topLevel)
                {
                    try { TmdlApplier.Apply(m, doc.LogicalPath, doc.Content); applied.Add(doc.LogicalPath); }
                    catch (Exception ex) { skipped.Add($"{doc.LogicalPath}: {OneLine(ex.Message)}"); }
                }

                foreach (var group in measures.GroupBy(x => x.table, StringComparer.OrdinalIgnoreCase))
                {
                    var path = "./tables/" + group.Key;
                    if (topPaths.Contains(path))
                    {
                        foreach (var item in group) skipped.Add($"{item.objRef}: the same script also contains its whole table; apply one scope at a time.");
                        continue;
                    }
                    var partial = BuildPartialTableTmdl(group.Key, group.Select(x => x.content));
                    try
                    {
                        TmdlApplier.Apply(m, path, partial);
                        applied.AddRange(group.Select(x => x.objRef));
                    }
                    catch (Exception ex)
                    {
                        foreach (var item in group) skipped.Add($"{item.objRef}: {OneLine(ex.Message)}");
                    }
                }
            });
            return new ApplyScriptResult { Revision = rev, Applied = applied.ToArray(), Skipped = skipped.ToArray() };
        }

        private sealed class TmdlScriptDocument
        {
            public string ObjectRef { get; set; }
            public string LogicalPath { get; set; }
            public string Content { get; set; }
        }

        /// <summary>Split current sentinel-owned script output, while retaining the legacy/manual top-level form.</summary>
        private static List<TmdlScriptDocument> ParseTmdlDocuments(string script)
        {
            var blocks = ParseTmdlObjectBlocks(script);
            if (blocks.Count == 0) return ParseTopLevelTmdlDocuments(script);

            var result = new List<TmdlScriptDocument>();
            foreach (var (objRef, content) in blocks)
            {
                var top = ParseTopLevelTmdlDocuments(content);
                if (top.Count == 1) result.Add(top[0]);
                else result.Add(new TmdlScriptDocument { ObjectRef = objRef, Content = content });
            }
            return result;
        }

        private static List<(string objRef, string content)> ParseTmdlObjectBlocks(string script)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(script)) return result;
            var header = new System.Text.RegularExpressions.Regex(@"^//\s*@object\s+(.+?)\s*$");
            string curRef = null;
            var buf = new System.Text.StringBuilder();
            void Flush()
            {
                if (curRef != null) result.Add((curRef, buf.ToString().Trim('\n', '\r') + "\n"));
                buf.Clear();
            }
            foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                var match = header.Match(raw);
                if (match.Success) { Flush(); curRef = match.Groups[1].Value; }
                else if (curRef != null) { buf.Append(raw); buf.Append('\n'); }
            }
            Flush();
            return result;
        }

        /// <summary>Map column-0 table/role declarations to the logical paths TOM expects.</summary>
        private static List<TmdlScriptDocument> ParseTopLevelTmdlDocuments(string script)
        {
            var result = new List<TmdlScriptDocument>();
            if (string.IsNullOrWhiteSpace(script)) return result;
            var header = new System.Text.RegularExpressions.Regex(@"^(table|role)\s+(.+?)\s*$");
            string curPath = null;
            var buf = new System.Text.StringBuilder();
            void Flush()
            {
                if (curPath != null) result.Add(new TmdlScriptDocument { LogicalPath = curPath, Content = buf.ToString().TrimEnd('\n') + "\n" });
                buf.Clear();
            }
            foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
            {
                // Only a COLUMN-0 (non-indented) table/role keyword is a new top-level document; indented lines and
                // "/// " description comments belong to the current document.
                if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
                {
                    var m = header.Match(raw);
                    if (m.Success)
                    {
                        Flush();
                        var kind = m.Groups[1].Value == "table" ? "tables" : "roles";
                        curPath = $"./{kind}/{StripTmdlName(m.Groups[2].Value)}";
                    }
                }
                if (curPath != null) { buf.Append(raw); buf.Append('\n'); }
            }
            Flush();
            return result;
        }

        private static bool TryPrepareMeasureTmdl(Model model, string objRef, string content,
            out string table, out string normalized, out string error)
        {
            table = null; normalized = null; error = null;
            if (!(ObjectRefs.Resolve(model, objRef) is Measure measure))
            {
                error = "the sentinel no longer resolves to an existing measure; re-script it instead of guessing a replacement.";
                return false;
            }

            var declaration = new System.Text.RegularExpressions.Regex(
                @"^[\t ]*(measure|column|hierarchy|partition|calculationItem|table|role)\s+('(?:''|[^'])*'|[^=\s]+)(?:\s*=|\s*$)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            var matches = declaration.Matches((content ?? string.Empty).Replace("\r\n", "\n"));
            if (matches.Count != 1 || !string.Equals(matches[0].Groups[1].Value, "measure", StringComparison.Ordinal))
            {
                error = "a partial TMDL block must contain exactly one measure declaration; use typed create/delete operations for structural changes.";
                return false;
            }
            var scriptedName = StripTmdlName(matches[0].Groups[2].Value);
            if (!string.Equals(scriptedName, measure.Name, StringComparison.Ordinal))
            {
                error = $"the measure header changed from '{measure.Name}' to '{scriptedName}'. Use rename_object so references are fixed up safely.";
                return false;
            }

            var parent = new System.Text.RegularExpressions.Regex(@"^ref table\s+(.+?)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline).Matches(content ?? string.Empty);
            if (parent.Count != 1 || !string.Equals(StripTmdlName(parent[0].Groups[1].Value), measure.Table.Name, StringComparison.Ordinal))
            {
                error = "the measure's parent-table reference changed; re-script the measure instead of applying it to another table.";
                return false;
            }

            table = measure.Table.Name;
            normalized = (content ?? string.Empty).Trim('\n', '\r') + "\n";
            return true;
        }

        private static string BuildPartialTableTmdl(string table, IEnumerable<string> children)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ref table '").Append(table.Replace("'", "''")).AppendLine("'").AppendLine();
            var parent = new System.Text.RegularExpressions.Regex(@"^ref table\s+.+?\s*$");
            foreach (var child in children)
            {
                foreach (var line in child.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                    if (!parent.IsMatch(line)) sb.AppendLine(line);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string OneLine(string value) => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");

        // A TMDL object name is bare (Sales) or single-quoted when it has spaces ('Sales Data', with '' for a literal quote).
        private static string StripTmdlName(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') s = s.Substring(1, s.Length - 2).Replace("''", "'");
            return s;
        }

        /// <summary>The smallest TMSL-scriptable container for an object: its table for table-children, or the
        /// object itself when it's already a top-level scriptable type. TMSL createOrReplace can't target a measure.</summary>
        private static ITabularNamedObject ScriptContainer(ITabularNamedObject o)
        {
            switch (o)
            {
                case Table t: return t;
                case ModelRole r: return r;
                case Measure me: return me.Table;
                case Column col: return col.Table;
                case Hierarchy h: return h.Table;
                case Partition p: return p.Table;
                case CalculationItem ci: return ci.CalculationGroupTable;
                case Level lv: return lv.Table;
                default: return o;   // relationship/function/perspective: try directly, else fall back to a comment
            }
        }

        // ---- Property grid: reflect TOM property metadata -> editable descriptors (get) + typed set ---------
        // The property grid's foundation. Rather than port the WinForms PropertyGrid (rule #3), we reflect each
        // wrapper object's ComponentModel attributes ([Browsable]/[Category]/[DisplayName]/[Description]) + its
        // dynamic IsBrowsable(name) gate (governance / compatibility-level / perspective) into JSON descriptors.
        // Editing flows back through the wrapper's own setter, so it's change-tracked, undoable, and dual-drive.

        public Task<ObjectProperty[]> GetObjectPropertiesAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");
                var props = ReflectProperties(obj);
                EnrichProperties(m, obj, props);
                return props;
            });
        }

        public async Task<SetResult> SetObjectPropertyAsync(string objRef, string propertyName, string value, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"set {propertyName}", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found — check the ref with get_object, or run list_objects / search_model to find it.");
                var p = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new InvalidOperationException($"{ObjectRefs.KindOf(obj)} has no property '{propertyName}' — run get_properties on {objRef} to see its settable property names.");
                if (!p.CanWrite) throw new InvalidOperationException($"Property '{propertyName}' is read-only.");
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var next = ConvertPropertyValue(pt, value, propertyName);
                var current = p.GetValue(obj);
                if (!Equals(current, next)) { p.SetValue(obj, next); changed = true; }   // wrapper setter tracks + (for Name) FormulaFixup
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        /// <summary>Set ONE property to ONE value across N objects as a SINGLE atomic gesture — the multi-select
        /// property-grid write (#140). Generalizes <see cref="SetObjectPropertyAsync"/>: the whole batch is one
        /// <see cref="Session.MutateAsync"/> (one undo entry, one model/didChange broadcast) so a single undo
        /// restores every object; a failure at ANY object throws, which rolls the WHOLE batch back via MutateAsync's
        /// rollback-on-throw (state is exactly pre-gesture) and the error names the object that failed and why. This
        /// is the SAME atomic-batch mechanism apply_dax_script / set_display_folder use — a foreach inside one
        /// MutateAsync — not a parallel path. Objects may be heterogeneous kinds (a mixed measure/column selection),
        /// so the property is resolved + converted PER object. A genuine batch (>=2 objects, >=1 actually changed)
        /// appends ONE "batch" audit record — never N — mirroring apply_plan's batch verdict, written NON-undoably
        /// AFTER the mutation so a rolled-back or no-op batch leaves no phantom record; Summary/Evidence state
        /// attempted vs actually-changed, never claiming more changes than occurred. Exactly ONE ref delegates to
        /// SetObjectPropertyAsync outright, so single-object behavior (message shapes included) is byte-identical.</summary>
        public async Task<SetResult> SetObjectPropertiesAsync(string[] objRefs, string propertyName, string value, string origin)
        {
            if (objRefs == null || objRefs.Length == 0)
                throw new InvalidOperationException("set_properties: pass at least one object ref — run list_objects / search_model to find refs, then get_properties on one to see its settable property names.");
            // One ref is not a batch — delegate so behavior (errors, exception types, no audit record) is
            // byte-identical to set_property; the batch machinery below only ever engages at N >= 2.
            if (objRefs.Length == 1)
                return await SetObjectPropertyAsync(objRefs[0], propertyName, value, origin);
            if (string.IsNullOrEmpty(propertyName))
                throw new InvalidOperationException("set_properties: propertyName is required — run get_properties on one of the objects to see the exact settable property names.");
            var s = _sessions.Require();
            var changedRefs = new List<string>();
            var rev = await s.MutateAsync(origin, $"set {propertyName} on {objRefs.Length} objects", m =>
            {
                // PASS 1 — resolve EVERY ref against the pre-gesture model and refuse duplicates by RESOLVED OBJECT
                // IDENTITY (never string compare: refs are name concats and name lookup is case-insensitive, so two
                // spellings can be one object). A duplicate would double-apply the write — and a Name change on the
                // first occurrence would leave the second spelling resolving stale mid-batch. Resolving everything
                // up front also means a pass-2 rename can never invalidate a later ref's resolution.
                var targets = new List<(string r, ITabularNamedObject obj)>(objRefs.Length);
                var seen = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
                foreach (var r in objRefs)
                {
                    var obj = ObjectRefs.Resolve(m, r) ?? throw new InvalidOperationException($"{r} not found — run list_objects / search_model to find the exact ref. Nothing was changed (the batch is all-or-nothing).");
                    if (!seen.TryAdd(obj, r))
                        throw new InvalidOperationException($"{r} resolves to the same object as '{seen[obj]}' — each object may appear ONCE per batch (a duplicate would double-apply the write). Remove the duplicate ref and re-run. Nothing was changed (the batch is all-or-nothing).");
                    targets.Add((r, obj));
                }
                // PASS 2 — reflect + convert + set PER object; any failure names THIS object and aborts the batch —
                // MutateAsync's rollback-on-throw then reverts every object already set, so partial failure =
                // exactly-pre-gesture state (never a half-applied write, never an orphan undo). The get/set
                // reflection calls are guarded too: a setter's own validation throw (e.g. renaming to a duplicate
                // Name) must surface its REAL reason with the failing ref, never a bare TargetInvocationException.
                foreach (var (r, obj) in targets)
                {
                    var p = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                            ?? throw new InvalidOperationException($"{r}: {ObjectRefs.KindOf(obj)} has no property '{propertyName}' — run get_properties on {r} to see its settable property names. Nothing was changed (the batch is all-or-nothing).");
                    if (!p.CanWrite) throw new InvalidOperationException($"{r}: property '{propertyName}' is read-only. Nothing was changed (the batch is all-or-nothing).");
                    var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    object next;
                    try { next = ConvertPropertyValue(pt, value, propertyName); }
                    catch (Exception ex) { throw new InvalidOperationException($"{r}: '{value}' is not a valid value for '{propertyName}' ({pt.Name}) — {ex.Message} Nothing was changed (the batch is all-or-nothing)."); }
                    object current;
                    try { current = p.GetValue(obj); }
                    catch (Exception ex)
                    {
                        var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                        throw new InvalidOperationException($"{r}: reading '{propertyName}' failed — {real.Message} Nothing was changed (the batch is all-or-nothing).", real);
                    }
                    if (Equals(current, next)) continue;   // already the target value — an honest no-op for THIS object
                    try { p.SetValue(obj, next); }         // wrapper setter tracks + (for Name) FormulaFixup
                    catch (Exception ex)
                    {
                        var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                        throw new InvalidOperationException($"{r}: setting '{propertyName}' failed — {real.Message} Nothing was changed (the batch is all-or-nothing).", real);
                    }
                    changedRefs.Add(r);
                }
            });
            // ONE "batch" audit record for a genuine multi-object gesture that ACTUALLY changed something (attempted
            // >= 2 is guaranteed by the delegation above; a no-op writes nothing; a rolled-back batch never reaches
            // here). The record stays honest: Summary/Evidence state attempted vs actually-changed, so an
            // [already-equal, changed] pair certifies "1 of 2" — never a 2-object change that didn't happen.
            // RecordVerifiedEditAsync writes NON-undoably (survives undo_change) and is itself dry-run-suppressed.
            if (changedRefs.Count > 0)
                await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                {
                    SessionId = s.Id, Revision = rev, Origin = origin, Op = "set_properties", Verdict = "batch",
                    Summary = changedRefs.Count == objRefs.Length
                        ? $"set {propertyName} = '{value}' on {objRefs.Length} objects in one atomic change"
                        : $"set {propertyName} = '{value}' on {changedRefs.Count} of {objRefs.Length} objects in one atomic change (the rest already had the value)",
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new { property = propertyName, value, attempted = objRefs.Length, refs = objRefs, changed = changedRefs.Count, changedRefs }),
                }, vitalsChangedRefs: changedRefs.ToArray());
            return new SetResult { Revision = rev, Changed = changedRefs.Count > 0 };
        }

        private static ObjectProperty[] ReflectProperties(object obj)
        {
            var t = obj.GetType();
            var gate = t.GetMethod("IsBrowsable", BindingFlags.NonPublic | BindingFlags.Instance);   // dynamic visibility gate
            var list = new List<ObjectProperty>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;                        // skip indexers / write-only
                var browsable = p.GetCustomAttribute<System.ComponentModel.BrowsableAttribute>();
                if (browsable != null && !browsable.Browsable) continue;                              // [Browsable(false)]
                if (gate != null) { try { if (gate.Invoke(obj, new object[] { p.Name }) is bool b && !b) continue; } catch { /* show it */ } }

                var nt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                string kind; string[] options = System.Array.Empty<string>();
                if (nt == typeof(bool)) kind = "bool";
                else if (nt.IsEnum) { kind = "enum"; options = Enum.GetNames(nt); }
                else if (nt == typeof(string)) kind = "string";
                else if (nt == typeof(int) || nt == typeof(long) || nt == typeof(double) || nt == typeof(decimal)) kind = "number";
                else continue;                                                                        // skip complex/ref/collection types (v1)

                object val; try { val = p.GetValue(obj); } catch { continue; }                        // some getters are state-dependent
                var readOnly = !p.CanWrite || (p.GetCustomAttribute<System.ComponentModel.ReadOnlyAttribute>()?.IsReadOnly ?? false);
                list.Add(new ObjectProperty
                {
                    Name = p.Name,
                    DisplayName = p.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName ?? p.Name,
                    Category = p.GetCustomAttribute<System.ComponentModel.CategoryAttribute>()?.Category ?? "Misc",
                    Description = p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description,
                    Kind = kind,
                    Value = val?.ToString() ?? "",
                    Options = options,
                    ReadOnly = readOnly,
                });
            }
            return list.OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>Post-reflection PREFILLS: decorate specific descriptors with model-derived pick lists and honest
        /// gating, so the grid offers real choices instead of blank text boxes. Same payload over RPC and MCP
        /// (get_properties) — no new op, agents keep their list-op parity.</summary>
        private static void EnrichProperties(Model m, object obj, ObjectProperty[] props)
        {
            // Display folder: the folders already in use — the object's own table first, then the rest of the model —
            // so filing a measure/column autocompletes to existing structure instead of near-duplicate spellings.
            var folder = System.Array.Find(props, p => p.Name == "DisplayFolder");
            var home = (obj as Measure)?.Table ?? (obj as Column)?.Table ?? (obj as Hierarchy)?.Table;
            if (folder != null && home != null)
            {
                var sug = DisplayFolderSuggestions(m, home);
                if (sug.Length > 0) folder.Suggestions = sug;
            }

            var dt = System.Array.Find(props, p => p.Name == "DataType");
            if (dt != null && obj is Column)
            {
                if (obj is DataColumn)
                {
                    // Only the engine-settable types (SettableColumnDataTypes — the set_column_data_type allow-list);
                    // the raw enum carries members a set would refuse (Unknown/Variant/Binary/…). If the CURRENT value
                    // is outside the list (e.g. Binary from the source), keep showing the truth at the top.
                    var opts = new List<string>(SettableColumnDataTypes);
                    if (!string.IsNullOrEmpty(dt.Value) && !opts.Contains(dt.Value, StringComparer.OrdinalIgnoreCase)) opts.Insert(0, dt.Value);
                    dt.Options = opts.ToArray();
                }
                else
                {
                    // Calculated / calc-table columns derive their type from DAX — an editable dropdown here would be
                    // a lie (set_column_data_type refuses them). Lock the row and say why, analyst-plain.
                    dt.ReadOnly = true;
                    dt.Hint = "This column's data type comes from its DAX expression and can't be set directly.";
                }
            }

            // A measure's dynamic format slot gets its own editor kind (status + multi-line DAX editor in the grid).
            // Below CL 1601 the slot cannot exist — surface the requirement plainly instead of a dead control.
            var fx = System.Array.Find(props, p => p.Name == "FormatStringExpression");
            if (fx != null && obj is Measure)
            {
                fx.Kind = "formatExpression";
                fx.DisplayName = "Format expression";
                fx.Description = "A DAX expression that returns the format string for the current filter context (a dynamic format). When set, it replaces the static Format String.";
                var cl = m.Database?.CompatibilityLevel ?? 0;
                if (cl < 1601)
                {
                    fx.ReadOnly = true;
                    fx.Hint = $"Dynamic format expressions need model compatibility level 1601 or higher; this model is {cl}.";
                }
            }
        }

        /// <summary>Distinct display folders in use: the home table's first (alphabetical), then the rest of the
        /// model's. The cap holds across BOTH scopes (home-table entries take priority within it) so a sprawling
        /// model can't bloat the payload (token economy — the grid and the agent both read this).</summary>
        private static string[] DisplayFolderSuggestions(Model m, Table home)
        {
            const int cap = 60;
            static IEnumerable<string> FoldersOf(Table t) =>
                t.Measures.Select(x => x.DisplayFolder)
                    .Concat(t.Columns.Select(x => x.DisplayFolder))
                    .Concat(t.Hierarchies.Select(x => x.DisplayFolder))
                    .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var f in FoldersOf(home).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= cap) return result.ToArray();
                if (seen.Add(f)) result.Add(f);
            }
            foreach (var f in m.Tables.Where(t => !ReferenceEquals(t, home)).SelectMany(FoldersOf).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= cap) break;
                if (seen.Add(f)) result.Add(f);
            }
            return result.ToArray();
        }

        // ---- Display folders: batch move + folder rename ------------------------------------------------
        // A display folder has no existence of its own in TOM — it is just the DisplayFolder string on each
        // measure/column/hierarchy ("Parent\Child" for nesting), so an empty folder cannot persist. These two
        // ops give both doors the ATOMIC gestures over that reality: file/clear many objects at once, and
        // rename a folder (prefix rewrite across a table, nested paths included) — each ONE MutateAsync, so a
        // single undo restores every member (per-object set_property would fragment the timeline into N steps,
        // and a mid-batch failure rolls the WHOLE batch back — MutateAsync's rollback-on-throw).

        /// <summary>Trim whitespace + stray leading/trailing separators so paths compare reliably. Null → "".</summary>
        private static string NormalizeFolderPath(string path) => (path ?? string.Empty).Trim().Trim('\\').Trim();

        public async Task<SetResult> SetDisplayFolderAsync(string[] refs, string folder, string origin)
        {
            if (refs == null || refs.Length == 0)
                throw new InvalidOperationException("set_display_folder: pass at least one measure/column/hierarchy ref — run list_measures or list_columns to find refs (each row shows its current display folder).");
            var target = NormalizeFolderPath(folder);
            var s = _sessions.Require();
            var changed = false;
            var label = target.Length == 0
                ? (refs.Length == 1 ? "clear display folder" : $"clear display folder on {refs.Length} objects")
                : (refs.Length == 1 ? $"move to folder {target}" : $"move {refs.Length} objects to folder {target}");
            var rev = await s.MutateAsync(origin, label, m =>
            {
                foreach (var r in refs)
                {
                    var obj = ObjectRefs.Resolve(m, r) ?? throw new InvalidOperationException($"{r} not found — run list_objects or search_model to find the exact ref. Nothing was moved (the batch is all-or-nothing).");
                    switch (obj)
                    {
                        case Measure me: if (me.DisplayFolder != target) { me.DisplayFolder = target; changed = true; } break;
                        case Column c: if (c.DisplayFolder != target) { c.DisplayFolder = target; changed = true; } break;
                        case Hierarchy h: if (h.DisplayFolder != target) { h.DisplayFolder = target; changed = true; } break;
                        default: throw new InvalidOperationException($"{r} is a {ObjectRefs.KindOf(obj)} — only measures, columns and hierarchies have a display folder. Pick refs from list_measures / list_columns. Nothing was moved (the batch is all-or-nothing).");
                    }
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<FolderRenameResult> RenameDisplayFolderAsync(string tableRef, string fromPath, string toPath, string origin)
        {
            var from = NormalizeFolderPath(fromPath);
            var to = NormalizeFolderPath(toPath);
            if (from.Length == 0)
                throw new InvalidOperationException("rename_display_folder: fromPath is required — the folder path exactly as it appears on the members' display folder (list_measures shows each measure's folder).");
            var s = _sessions.Require();
            var members = 0;
            var rev = await s.MutateAsync(origin, $"rename folder {from} to {(to.Length == 0 ? "(no folder)" : to)}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    throw new InvalidOperationException($"{tableRef} is not a table — pass 'table:Name'; run list_objects to see the model's tables.");

                // "Fin" matches "Fin" and "Fin\Sub" but never "Finance" — the separator guards the prefix.
                // Members' STORED paths get the SAME normalization as the inputs (trim + strip stray leading/
                // trailing separators — the tree's grouping rule): a "\Fin\" stored via set_property or an older
                // edit renders inside the folder, so the rename must catch it too, never claim "no members".
                var nested = from + "\\";
                string Rewrite(string df)
                {
                    df = NormalizeFolderPath(df);
                    if (df.Equals(from, StringComparison.OrdinalIgnoreCase)) return to;
                    if (df.StartsWith(nested, StringComparison.OrdinalIgnoreCase))
                        return to.Length == 0 ? df.Substring(nested.Length) : to + "\\" + df.Substring(nested.Length);
                    return null;   // not in this folder
                }
                foreach (var me in t.Measures) { var next = Rewrite(me.DisplayFolder); if (next != null) { me.DisplayFolder = next; members++; } }
                foreach (var c in t.Columns) { var next = Rewrite(c.DisplayFolder); if (next != null) { c.DisplayFolder = next; members++; } }
                foreach (var h in t.Hierarchies) { var next = Rewrite(h.DisplayFolder); if (next != null) { h.DisplayFolder = next; members++; } }
                if (members == 0)
                    throw new InvalidOperationException($"No measures, columns or hierarchies on '{t.Name}' are in folder '{from}' — a folder exists only through its members' display folder paths. Run list_measures (or get_properties on a member) to see the folders in use.");
            });
            return new FolderRenameResult { Revision = rev, Members = members, From = from, To = to };
        }

        private static object ConvertPropertyValue(Type pt, string value, string propName)
        {
            if (pt == typeof(string)) return value ?? string.Empty;
            if (pt == typeof(bool)) return bool.Parse(value);
            if (pt.IsEnum) return Enum.Parse(pt, value, true);
            if (pt == typeof(int)) return int.Parse(value);
            if (pt == typeof(long)) return long.Parse(value);
            if (pt == typeof(double)) return double.Parse(value);
            if (pt == typeof(decimal)) return decimal.Parse(value);
            throw new InvalidOperationException($"Property '{propName}' has a type the grid can't edit ({pt.Name}).");
        }

        public Task<TreeNode[]> ListFunctionsAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => m.Functions
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TreeNode { Ref = ObjectRefs.For(f), Name = f.Name, Kind = "function", HasChildren = false })
                .ToArray());
        }

        public async Task<RenameResult> RenameObjectAsync(string objRef, string newName, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            string newRef = null;
            var rev = await s.MutateAsync(origin, "rename", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef) as TabularNamedObject ?? throw new InvalidOperationException($"{objRef} not found or not renameable — rename_object takes a named object (table, column, measure, hierarchy, role); run list_objects or search_model to find its exact ref.");
                changed = s.Rename(obj, newName); // the FormulaFixup contract seam: rewrites all DAX / RLS references
                newRef = ObjectRefs.For(obj);
            });
            return new RenameResult { Revision = rev, Changed = changed, NewRef = newRef };
        }

        // ---- Find & Replace (Phase 2, one-by-one, free) ------------------------------------------------------
        // The safe replace CHOKEPOINT both doors funnel through. MatchClass decides the path, and the safety-critical
        // decisions live HERE (never in the UI): an ObjectName replace becomes a reference-aware RENAME (FormulaFixup
        // rewrites every DAX/RLS reference); a match on a DAX IDENTIFIER (or other code) is REFUSED with the
        // "rename the object instead" hint; only plain text / string-literals / comments / M are literal-substituted.
        // A single edit → free (no bulk Pro gate). Undoable on the shared timeline like any other mutation.
        private sealed class ReplaceCtx
        {
            public string Field, MatchClass, Raw, New, Note, NewRef, Culture, DescProp;
            public bool Replaceable = true, IsDax, IsRename, IsSyn, IsM, IsNamedExpr;
            public int Count;
            public int? RefCount;  // renames: how many objects reference this one (fixup rewrites them all)
            public List<string> Warnings = new();
            public string Block;   // non-null → refuse with this message
        }

        public async Task<ReplaceResult> ReplaceInObjectAsync(ReplaceRequest req, string origin)
        {
            if (req == null || string.IsNullOrEmpty(req.Ref)) throw new ArgumentException("replace: a ref is required.");
            if (string.IsNullOrEmpty(req.Field)) throw new ArgumentException("replace: a field is required (e.g. 'name', 'description', 'expression').");
            if (string.IsNullOrEmpty(req.Find)) throw new ArgumentException("replace: the 'find' text is required.");
            var matcher = SearchMatcher.Create(req.Find, req.CaseSensitive, req.WholeWord, req.Regex);
            if (matcher.Error != null) throw new InvalidOperationException(matcher.Error);

            var s = _sessions.Require();
            // Pass 1 (read): resolve, read the raw field value, classify the targeted match(es), compute the new value,
            // and gather rename-safety context (collisions, M-breakage). No mutation.
            var ctx = await s.ReadAsync(m => BuildReplaceContext(m, req, matcher));

            // Preview (rehearsal): return everything the apply would do — before/after, warnings, blast radius —
            // without mutating, and report a safety refusal in .Blocked instead of throwing (the UI's
            // preview-before-apply panel needs the WHY, not an exception).
            if (req.Preview)
            {
                var pv = new ReplaceResult
                {
                    Ref = req.Ref, Field = req.Field, MatchClass = ctx.MatchClass,
                    Before = ctx.Raw, After = ctx.New, Replacements = ctx.Count,
                    Warnings = ctx.Warnings.ToArray(), Note = ctx.Note, References = ctx.RefCount,
                    Preview = true, Changed = false, Revision = s.Revision,
                    Blocked = ctx.Block ?? (!ctx.Replaceable ? "This match isn't safely replaceable — rename the referenced object instead." : null),
                };
                if (pv.Blocked == null && ctx.IsDax && ctx.Count > 0)
                {
                    var pvv = await ValidateDaxAsync(ctx.New);   // the apply would refuse a non-parsing rewrite — say so now
                    if (pvv == null || !pvv.Valid)
                        pv.Blocked = "The edited DAX would no longer parse — this replacement would be refused.";
                }
                return pv;
            }

            if (ctx.Block != null) throw new InvalidOperationException(ctx.Block);   // engine-side hard block (references/code/RLS)
            if (!ctx.Replaceable) throw new InvalidOperationException("This match isn't safely replaceable — rename the referenced object instead.");

            var result = new ReplaceResult
            {
                Ref = req.Ref, Field = req.Field, MatchClass = ctx.MatchClass,
                Before = ctx.Raw, After = ctx.New, Replacements = ctx.Count,
                Warnings = ctx.Warnings.ToArray(), Note = ctx.Note, References = ctx.RefCount,
            };
            if (ctx.Count == 0 || string.Equals(ctx.Raw, ctx.New, StringComparison.Ordinal))
            {
                result.Changed = false; result.Note = ctx.Note ?? "No matching text to replace (already up to date).";
                result.Revision = s.Revision;
                return result;
            }

            // A DAX literal/comment edit must still PARSE — validity only (it deliberately changes results, so it's
            // NOT equivalence-gated). Reuse the Verified-Mode validator; refuse a change that breaks the expression.
            if (ctx.IsDax)
            {
                var v = await ValidateDaxAsync(ctx.New);
                if (v == null || !v.Valid)
                {
                    var msg = v?.Diagnostics?.FirstOrDefault(d => d.Severity == "error")?.Message;
                    throw new InvalidOperationException($"The edited DAX would no longer parse{(msg != null ? " (" + msg + ")" : "")} — the replacement was refused so the model stays valid.");
                }
            }

            // Pass 2 (mutate): re-resolve and apply. One undoable step; broadcasts model/didChange (dual-drive).
            var rev = await s.MutateAsync(origin, $"replace in {req.Field}", m => ApplyReplace(m, req, ctx, s));
            result.Revision = rev;
            result.Changed = true;
            result.Ref = ctx.NewRef ?? req.Ref;
            return result;
        }

        // Reflection is the one general seam for the string properties (Description/DisplayFolder/FormatString) —
        // same approach set_property uses; avoids a per-type switch and covers every object that exposes the prop.
        private static string ReadStringProp(object obj, string prop) =>
            obj?.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as string;

        private static bool WriteStringProp(object obj, string prop, string value)
        {
            var p = obj?.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) throw new InvalidOperationException($"{ObjectRefs.KindOf(obj as ITabularObject)} has no editable '{prop}'.");
            if (Equals(p.GetValue(obj) as string, value)) return false;
            p.SetValue(obj, value); return true;   // wrapper setter tracks the change
        }

        private ReplaceCtx BuildReplaceContext(Model m, ReplaceRequest req, SearchMatcher matcher)
        {
            var ctx = new ReplaceCtx { Field = req.Field };
            var field = req.Field;

            // Shared (named) M expression — the one ref ObjectRefs doesn't carry; edited by name.
            if (req.Ref.StartsWith("namedexpr:", StringComparison.Ordinal))
            {
                if (field != ModelSearch.FM) throw new InvalidOperationException("A shared expression supports only the 'mExpression' field.");
                var name = req.Ref.Substring("namedexpr:".Length);
                var e = m.Expressions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No shared expression named '{name}'.");
                ctx.IsNamedExpr = true; ctx.IsM = true; ctx.MatchClass = "MExpression";
                ctx.Raw = e.Expression ?? ""; ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                ctx.Note = "M is not covered by reference fixup — this is a literal edit; verify the query still works.";
                return ctx;
            }

            var obj = ObjectRefs.Resolve(m, req.Ref)
                ?? throw new InvalidOperationException($"{req.Ref} not found — run search_model / list_objects to find its exact ref.");

            switch (field)
            {
                case ModelSearch.FName:
                    ctx.IsRename = true; ctx.MatchClass = "ObjectName";
                    ctx.Raw = obj.Name; ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                    if (obj is IDaxObject dxo) ctx.RefCount = dxo.ReferencedBy.Count;   // blast radius for the preview line
                    if (ctx.Count > 0)
                    {
                        if (string.IsNullOrWhiteSpace(ctx.New)) { ctx.Replaceable = false; ctx.Block = "The new name would be empty — an object must have a name."; break; }
                        if (SiblingHasName(obj, ctx.New)) { ctx.Block = $"A sibling named '{ctx.New}' already exists — pick a different replacement (names must be unique)."; break; }
                        AddMReferenceWarnings(m, obj.Name, ctx.Warnings);   // FormulaFixup won't rewrite M — warn loudly
                    }
                    break;

                case ModelSearch.FDesc: ctx.DescProp = "Description"; goto case "__plain";
                case ModelSearch.FFolder: ctx.DescProp = "DisplayFolder"; goto case "__plain";
                case ModelSearch.FFormat: ctx.DescProp = "FormatString"; goto case "__plain";
                case "__plain":
                    ctx.MatchClass = "PlainText";
                    ctx.Raw = ReadStringProp(obj, ctx.DescProp) ?? "";
                    ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                    break;

                case ModelSearch.FExpr:
                {
                    ctx.IsDax = true;
                    ctx.Raw = ReadDax(obj) ?? throw new InvalidOperationException($"{req.Ref} has no DAX expression to search/replace.");
                    var tokens = DaxMatchClassifier.Tokenize(ctx.Raw);
                    var all = matcher.Find(ctx.Raw);
                    var targeted = req.Span != null ? all.Where(sp => sp.Start == req.Span.Start && sp.Len == req.Span.Len).ToList() : all;
                    ctx.Count = targeted.Count;
                    if (targeted.Count == 0) { ctx.New = ctx.Raw; ctx.MatchClass = "DaxLiteral"; break; }
                    // The HARD BLOCK: any targeted match on an identifier/code token is refused — text-poking a
                    // reference half-rewrites the model. Route identifier changes through rename.
                    var classes = targeted.Select(sp => DaxMatchClassifier.Classify(tokens, sp.Start, sp.Len)).Distinct().ToList();
                    var bad = classes.FirstOrDefault(c => !DaxMatchClassifier.IsReplaceable(c));
                    if (bad != null)
                    {
                        ctx.Replaceable = false; ctx.MatchClass = bad;
                        ctx.Block = bad == DaxMatchClassifier.Reference
                            ? "That match is a reference to another object inside the DAX — a text replace would break the formula. Rename the referenced object instead (its references update automatically). If you meant a string literal or comment, target that exact occurrence."
                            : "That match is part of the DAX code (a function, operator, or number), not editable text. Edit the expression directly to change it.";
                        break;
                    }
                    ctx.MatchClass = classes.Count == 1 ? classes[0] : DaxMatchClassifier.Literal;
                    ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                    ctx.Note = "This edits text inside a DAX string literal/comment — it changes what the expression returns (validated for syntax, not equivalence).";
                    break;
                }

                case ModelSearch.FM:
                {
                    if (!(obj is Partition p)) throw new InvalidOperationException($"{req.Ref} is not an M partition.");
                    if (p.SourceType != PartitionSourceType.M) throw new InvalidOperationException($"{req.Ref} is a {p.SourceType} partition — only M partitions have an editable expression.");
                    ctx.IsM = true; ctx.MatchClass = "MExpression";
                    ctx.Raw = p.Expression ?? ""; ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                    ctx.Note = "M is not covered by reference fixup — this is a literal edit; verify the query still works.";
                    break;
                }

                case ModelSearch.FSyn:
                {
                    if (!(obj is Table || obj is Measure || obj is Column || obj is Hierarchy || obj is Level))
                        throw new InvalidOperationException($"{req.Ref} does not carry synonyms.");
                    ctx.IsSyn = true; ctx.MatchClass = "PlainText";
                    var cult = PickSynonymCulture(m, obj, req.Culture, out var terms);
                    if (cult == null) throw new InvalidOperationException("No linguistic culture with synonyms for this object was found.");
                    ctx.Culture = cult.Name; ctx.Raw = terms ?? "";
                    ctx.New = matcher.Apply(ctx.Raw, req.Replace, req.Span, out ctx.Count);
                    break;
                }

                case ModelSearch.FRls:
                    ctx.Replaceable = false; ctx.MatchClass = DaxMatchClassifier.Reference;
                    ctx.Block = "Security filters are not directly replaceable in this version. Rename the referenced table or column — its references, including security filters, update automatically.";
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported replace field '{field}' — use one of: name, description, expression, displayFolder, formatString, mExpression, synonyms.");
            }
            return ctx;
        }

        // The mutate-pass apply. Re-resolves inside the dispatcher transaction and writes the pre-computed new value.
        private void ApplyReplace(Model m, ReplaceRequest req, ReplaceCtx ctx, Session s)
        {
            if (ctx.IsNamedExpr)
            {
                var name = req.Ref.Substring("namedexpr:".Length);
                var e = m.Expressions.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                e.Expression = ctx.New;
                return;
            }
            var obj = ObjectRefs.Resolve(m, req.Ref) ?? throw new InvalidOperationException($"{req.Ref} vanished before the edit could apply.");

            if (ctx.IsRename) { s.Rename((TabularNamedObject)obj, ctx.New); ctx.NewRef = ObjectRefs.For(obj); return; }
            if (ctx.IsDax) { SetDaxOn(obj, ctx.New); return; }
            if (ctx.IsM) { ((Partition)obj).Expression = ctx.New; return; }
            if (ctx.IsSyn)
            {
                var cult = m.Cultures.Contains(ctx.Culture) ? m.Cultures[ctx.Culture] : m.Cultures.FirstOrDefault();
                TabularEditor.TOMWrapper.Linguistics.SynonymHelper.SetSynonyms((TabularNamedObject)obj, cult, ctx.New);
                return;
            }
            WriteStringProp(obj, ctx.DescProp, ctx.New);   // PlainText: Description / DisplayFolder / FormatString
        }

        private static string ReadDax(ITabularObject obj) => obj switch
        {
            Measure me => me.Expression,
            CalculatedColumn cc => cc.Expression,
            CalculatedTable ct => ct.Expression,
            CalculationItem ci => ci.Expression,
            Function f => f.Expression,
            _ => null,
        };

        private static void SetDaxOn(ITabularObject obj, string expr)
        {
            switch (obj)
            {
                case Measure me: me.Expression = expr; break;
                case CalculatedColumn cc: cc.Expression = expr; break;
                case CalculatedTable ct: ct.Expression = expr; break;
                case CalculationItem ci: ci.Expression = expr; break;
                case Function f: f.Expression = expr; break;
                default: throw new InvalidOperationException("replace: object has no DAX expression.");
            }
        }

        // Uniqueness pre-check for a rename (a friendlier error than the TOM setter's throw). Covers the common
        // sibling collections; anything else falls through to the setter's own duplicate guard.
        private static bool SiblingHasName(ITabularNamedObject obj, string name) => obj switch
        {
            Measure me => me.Table.Measures.Any(x => x != me && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
            Column c => c.Table.Columns.Any(x => x != c && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
            Table t => t.Model.Tables.Any(x => x != t && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
            Hierarchy h => h.Table.Hierarchies.Any(x => x != h && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
            CalculationItem ci => ci.CalculationGroupTable.CalculationItems.Any(x => x != ci && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

        // Heuristic (design G2): FormulaFixup is DAX-only, so a rename never rewrites M. If any M partition / shared
        // expression mentions the OLD name as a whole word, warn — the rename will NOT fix those references.
        private static void AddMReferenceWarnings(Model m, string oldName, List<string> warnings)
        {
            if (string.IsNullOrEmpty(oldName)) return;
            bool Mentions(string mexpr) => !string.IsNullOrEmpty(mexpr) && WholeWordIndex(mexpr, oldName) >= 0;
            foreach (var t in m.Tables)
                foreach (var p in t.Partitions.Where(p => p.SourceType == PartitionSourceType.M))
                    if (Mentions(p.Expression))
                        warnings.Add($"M partition '{t.Name}/{p.Name}' mentions '{oldName}' — the rename will NOT update M; edit it manually if it references this object.");
            foreach (var e in m.Expressions)
                if (Mentions(e.Expression))
                    warnings.Add($"Shared M expression '{e.Name}' mentions '{oldName}' — the rename will NOT update M; edit it manually if it references this object.");
        }

        private static int WholeWordIndex(string text, string word)
        {
            int from = 0;
            while (from <= text.Length - word.Length)
            {
                int i = text.IndexOf(word, from, StringComparison.Ordinal);
                if (i < 0) return -1;
                bool b = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int end = i + word.Length;
                bool a = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
                if (b && a) return i;
                from = i + word.Length;
            }
            return -1;
        }

        private static Culture PickSynonymCulture(Model m, ITabularObject obj, string preferred, out string terms)
        {
            terms = null;
            IEnumerable<Culture> order = m.Cultures;
            if (!string.IsNullOrEmpty(preferred) && m.Cultures.Contains(preferred))
                order = new[] { m.Cultures[preferred] }.Concat(m.Cultures.Where(c => c.Name != preferred));
            foreach (var c in order)
            {
                string t; try { t = TabularEditor.TOMWrapper.Linguistics.SynonymHelper.GetSynonyms((TabularNamedObject)obj, c); } catch { continue; }
                if (!string.IsNullOrEmpty(t)) { terms = t; return c; }
            }
            return null;
        }

        // ---- AI-readiness -------------------------------------------------------------------

        public async Task<Scorecard> AiReadinessScanAsync()
        {
            var s = _sessions.Require();
            var card = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
            _lastReadinessGrade = card.Grade;   // cache for the `model.readinessGrade` activation fact (D2 — never force-scanned on a menu read)
            return card;
        }

        public async Task<Scorecard> AiReadinessScanLiveAsync()
        {
            // Live cardinality-aware scan: the offline rules PLUS the Dmv-kind rules (Q&A index 5M-unique-value
            // ceiling), fed by COLUMNSTATISTICS from the attached connection. Read-only.
            var s = _sessions.Require();
            var live = _live;
            if (live == null)
                throw new InvalidOperationException("Not connected to a live model. Connect first (connect_xmla / connect_local) — the live scan reads per-column cardinality (COLUMNSTATISTICS) for the Q&A-index rules.");
            var rs = await live.ExecuteAsync("EVALUATE COLUMNSTATISTICS()", 5_000_000, 180);
            if (!string.IsNullOrEmpty(rs.Error))
                throw new InvalidOperationException("COLUMNSTATISTICS failed on the live model: " + rs.Error);
            var stats = BuildLiveStats(rs);
            var scanned = await s.ReadAsync(m =>
            {
                var card = new ReadinessAnalyzer().Analyze(m, stats);
                // On Direct Lake, COLUMNSTATISTICS cardinality is resident-only, so the cardinality-gated rules
                // (Q&A 5M-unique ceiling / CopilotLimits) under-count — a clean result here is not a guarantee.
                if (DirectLakeInfo.IsModelDirectLake(m))
                    card.Caveat = "Direct Lake: per-column cardinality (COLUMNSTATISTICS) reflects only resident columns, so the scale / Q&A-index rules (e.g. CopilotLimits) may under-count — a clean result here is not a guarantee until the model is fully reframed.";
                return card;
            });
            _lastReadinessGrade = scanned.Grade;   // cache for the `model.readinessGrade` activation fact (D2)
            return scanned;
        }

        // internal (not private) so the smoke harness can fixture-test the COLUMNSTATISTICS parse — its substring
        // matching is the one place a real result can be mis-mapped, so it must be covered.
        internal static ReadinessLiveStats BuildLiveStats(ResultSet rs)
        {
            var stats = new ReadinessLiveStats();
            if (rs?.Columns == null || rs.Rows == null) return stats;
            // Match the FULL column phrases ("Table Name"/"Column Name"/"Cardinality"), NOT bare "Table"/"Column":
            // COLUMNSTATISTICS may label columns like "[COLUMNSTATISTICS].[Column Name]", and the substring "Column"
            // also occurs in the function name "COLUMNSTATISTICS" — a bare match could swap the table/column identity.
            int Idx(string phrase) => Array.FindIndex(rs.Columns, c => c.Name != null && c.Name.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0);
            int ti = Idx("Table Name"), ci = Idx("Column Name"), cardi = Idx("Cardinality");
            if (ti < 0 || ci < 0 || cardi < 0) return stats;   // unexpected shape — no Dmv rules fire (graceful)
            var maxIdx = Math.Max(ti, Math.Max(ci, cardi));
            foreach (var row in rs.Rows)
            {
                if (row == null || row.Length <= maxIdx) continue;
                var table = row[ti]?.ToString();
                var col = row[ci]?.ToString();
                if (string.IsNullOrEmpty(col) || col.StartsWith("RowNumber", StringComparison.OrdinalIgnoreCase)) continue;
                if (row[cardi] == null) continue;
                try { stats.Set(table, col, Convert.ToInt64(row[cardi])); } catch { /* non-numeric — skip */ }
            }
            return stats;
        }

        // ---- Best Practice Analyzer (general-purpose) --------------------------------------------

        /// <summary>Default rules + any rules embedded in the model (BestPracticeAnalyzer annotation),
        /// merged by id (model rules win). Internal: the health-delta probe scans with the SAME effective set.</summary>
        internal static System.Collections.Generic.List<BpaRule> GetBpaRules(Model m)
        {
            var byId = new System.Collections.Generic.Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
            // Base = the bundled Power BI standard ruleset; fall back to the curated set only if the embedded
            // resource failed to load, so a scan is never left with zero rules.
            var standard = BpaRuleSet.Standard();
            foreach (var r in (standard.Count > 0 ? standard : BpaDefaultRules.Rules)) byId[r.ID] = r;
            try
            {
                // Model-embedded rules (the BestPracticeAnalyzer annotation — also where load_bpa_rules persists
                // user/org rules) override or add by id. Tagged with provenance so scan results can say which
                // violations came from a custom rule vs the bundled standard set.
                var json = m.GetAnnotation("BestPracticeAnalyzer");
                if (!string.IsNullOrWhiteSpace(json))
                    foreach (var r in BpaRuleSet.Parse(json)) { r.FromModelAnnotation = true; byId[r.ID] = r; }
            }
            catch { /* malformed embedded rules — ignore, keep the base set */ }
            return byId.Values.ToList();
        }

        /// <summary>Load BPA rules from a file path, an http(s) URL, or inline JSON (standard BPARules.json
        /// schema) and persist them on the model's BestPracticeAnalyzer annotation — the same place GetBpaRules
        /// reads model-embedded rules, so they layer on top of the bundled standard set and travel with the model.
        /// replace=false merges (by id) with any rules already on the model; replace=true sets the model's rules
        /// to exactly the loaded set. Undoable; reset_bpa_rules clears them.</summary>
        public async Task<BpaRulesInfo> LoadBpaRulesAsync(string source, bool replace, string origin)
        {
            // Resolve + parse OFF the dispatcher (file / HTTP IO) so a slow URL never blocks edits. Parse throws
            // on malformed JSON — surfaced to the caller, not silently ignored.
            var json = await ResolveRuleSourceAsync(source);
            var loaded = Semanticus.Analysis.BpaRuleSet.Parse(json);
            if (loaded.Count == 0)
                throw new InvalidOperationException("No valid rules found in the source (a rule set is a JSON array; each rule needs an \"ID\").");

            var s = _sessions.Require();
            int modelRules = 0, active = 0;
            string warning = null;
            var rev = await s.MutateAsync(origin, "load BPA rules", m =>
            {
                var byId = new System.Collections.Generic.Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
                if (!replace)
                {
                    var existing = m.GetAnnotation("BestPracticeAnalyzer");
                    if (!string.IsNullOrWhiteSpace(existing))
                        try { foreach (var r in Semanticus.Analysis.BpaRuleSet.Parse(existing)) byId[r.ID] = r; }
                        catch { warning = "The model's prior custom rules were unparseable and were NOT preserved; only the newly-loaded rules remain (use replace=true to silence)."; }
                }
                foreach (var r in loaded) byId[r.ID] = r;
                var merged = byId.Values.ToList();
                m.SetAnnotation("BestPracticeAnalyzer", System.Text.Json.JsonSerializer.Serialize(merged));
                modelRules = merged.Count;
                active = GetBpaRules(m).Count;
            });
            return new BpaRulesInfo
            {
                Revision = rev, Source = SourceKind(source), Loaded = loaded.Count, ModelRules = modelRules,
                StandardRules = Semanticus.Analysis.BpaRuleSet.Standard().Count, ActiveRules = active, Warning = warning,
                Note = $"{loaded.Count} rule(s) loaded ({(replace ? "replaced" : "merged")}); {active} rules active for scans.",
            };
        }

        /// <summary>Clear all model-embedded BPA rules (the BestPracticeAnalyzer annotation), reverting scans to
        /// the bundled standard ruleset only. Undoable.</summary>
        public async Task<BpaRulesInfo> ResetBpaRulesAsync(string origin)
        {
            var s = _sessions.Require();
            int active = 0;
            var rev = await s.MutateAsync(origin, "reset BPA rules", m =>
            {
                if (!string.IsNullOrEmpty(m.GetAnnotation("BestPracticeAnalyzer")))
                    m.RemoveAnnotation("BestPracticeAnalyzer");
                active = GetBpaRules(m).Count;
            });
            return new BpaRulesInfo
            {
                Revision = rev, Source = "reset", Loaded = 0, ModelRules = 0,
                StandardRules = Semanticus.Analysis.BpaRuleSet.Standard().Count, ActiveRules = active,
                Note = $"Custom rules cleared; {active} standard rules active.",
            };
        }

        private static string SourceKind(string source)
        {
            var s = (source ?? "").Trim();
            if (s.StartsWith("[") || s.StartsWith("{")) return "inline";
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "url";
            return "file";
        }

        private static async Task<string> ResolveRuleSourceAsync(string source, string what = "BPA rules")
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException($"A {what} source is required: a file path, an http(s) URL, or inline JSON.");
            var s = source.Trim();
            if (s.StartsWith("[") || s.StartsWith("{")) return s;   // inline JSON (array or object)
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8_000_000 };
                return await http.GetStringAsync(s);
            }
            if (File.Exists(s)) return File.ReadAllText(s);
            throw new FileNotFoundException($"{what} source not found — not inline JSON, an http(s) URL, or an existing file: {s}");
        }

        // ---- Custom AI-readiness rules (the readiness mirror of load_bpa_rules) -------------------
        // Same source semantics (file / URL / inline JSON), same persistence pattern (a model annotation —
        // Semanticus_ReadinessRules — merged by id, undoable, travels with the model), same dry_run denial.
        // Difference: readiness rules are VALIDATED STRICTLY against the open model — compile AND a test-run
        // evaluation — before anything is written (they move a scored category, so a rule that would error on
        // scans never lands), and a built-in rule id can never be overridden. load_bpa_rules stays deliberately
        // lenient (TE-compat): community BPARules.json collections legitimately carry rules that don't evaluate
        // on every model (CompatibilityLevel-gated, scopes we don't map, properties absent at lower CLs), BPA has
        // no score a broken rule could inflate (a scan surfaces it per-rule via BpaScorecard.RuleErrors and it
        // contributes zero violations), and refusing the whole file would make loading an org rule set impossible.

        public async Task<ReadinessRulesInfo> LoadReadinessRulesAsync(string source, bool replace, string origin)
        {
            var json = await ResolveRuleSourceAsync(source, "readiness rules");
            List<Semanticus.Analysis.CustomReadinessRuleDef> all;
            try { all = Semanticus.Analysis.CustomReadinessRuleSet.ParseAll(json); }
            catch (Exception ex)
            { throw new InvalidOperationException($"The rules JSON does not parse ({ex.Message}). A rule set is a JSON array of rule objects (or one object); validate_rule checks a rule without saving."); }
            if (all.Count == 0)
                throw new InvalidOperationException("No rules found in the source (a rule set is a JSON array; each rule needs an \"ID\"). Preview a rule with validate_rule before loading.");

            var s = _sessions.Require();
            // Strict pre-write validation against the open model — the SAME compile + test-run evaluation
            // validate_rule performs (parsing alone is not enough: an expression can compile yet throw on real
            // objects, e.g. a method call on a null property). A rule every scan would choke on never lands;
            // the refusal carries each rule's problems with validate_rule's exact teaching detail.
            var problems = await s.ReadAsync(m =>
            {
                var errs = new List<string>();
                foreach (var def in all)
                {
                    var check = Semanticus.Analysis.RuleAuthoring.CheckReadiness(m, def);
                    if (!check.Valid) errs.Add($"{(string.IsNullOrWhiteSpace(def?.ID) ? "(no id)" : def.ID.Trim())}: {string.Join(" | ", check.Errors)}");
                }
                return errs;
            });
            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"Custom readiness rules were NOT loaded — {problems.Count} rule(s) failed validation:\n - " +
                    string.Join("\n - ", problems) +
                    "\nFix the rules and load again. validate_rule previews compile + a test run against the open model without saving.");

            var loaded = Semanticus.Analysis.CustomReadinessRuleSet.Parse(json);   // de-dup by id, last wins (merge semantics)
            int modelRules = 0;
            string warning = null;
            var rev = await s.MutateAsync(origin, "load readiness rules", m =>
            {
                var byId = new Dictionary<string, Semanticus.Analysis.CustomReadinessRuleDef>(StringComparer.OrdinalIgnoreCase);
                if (!replace)
                {
                    var existing = m.GetAnnotation(Semanticus.Analysis.CustomReadinessRuleSet.AnnotationName);
                    if (!string.IsNullOrWhiteSpace(existing))
                        try { foreach (var r in Semanticus.Analysis.CustomReadinessRuleSet.Parse(existing)) byId[r.ID] = r; }
                        catch { warning = "The model's prior custom rules were unparseable and were NOT preserved; only the newly-loaded rules remain (use replace=true to silence)."; }
                }
                foreach (var r in loaded) byId[r.ID] = r;
                var merged = byId.Values.ToList();
                m.SetAnnotation(Semanticus.Analysis.CustomReadinessRuleSet.AnnotationName,
                    Semanticus.Analysis.CustomReadinessRuleSet.Serialize(merged));
                modelRules = merged.Count;
            });
            var builtin = Semanticus.Analysis.ReadinessRuleSet.Default().Count;
            return new ReadinessRulesInfo
            {
                Revision = rev, Source = SourceKind(source), Loaded = loaded.Count, ModelRules = modelRules,
                BuiltinRules = builtin, ActiveRules = builtin + modelRules, Warning = warning,
                Note = $"{loaded.Count} rule(s) loaded ({(replace ? "replaced" : "merged")}); {modelRules} custom rules now ride every ai_readiness_scan. Undoable; reset_readiness_rules clears them.",
            };
        }

        /// <summary>Clear all custom readiness rules (the Semanticus_ReadinessRules annotation), reverting scans
        /// to the built-in rule set only. Undoable.</summary>
        public async Task<ReadinessRulesInfo> ResetReadinessRulesAsync(string origin)
        {
            var s = _sessions.Require();
            var rev = await s.MutateAsync(origin, "reset readiness rules", m =>
            {
                if (!string.IsNullOrEmpty(m.GetAnnotation(Semanticus.Analysis.CustomReadinessRuleSet.AnnotationName)))
                    m.RemoveAnnotation(Semanticus.Analysis.CustomReadinessRuleSet.AnnotationName);
            });
            var builtin = Semanticus.Analysis.ReadinessRuleSet.Default().Count;
            return new ReadinessRulesInfo
            {
                Revision = rev, Source = "reset", Loaded = 0, ModelRules = 0,
                BuiltinRules = builtin, ActiveRules = builtin,
                Note = $"Custom readiness rules cleared; {builtin} built-in rules active.",
            };
        }

        /// <summary>Compile + honest test-run for rule authoring (BOTH kinds), read-only: parse errors from the
        /// REAL Dynamic-LINQ parser, and — when clean — the would-be Applicable/violation counts + the first few
        /// flagged objects on the open model. Nothing is saved; load_bpa_rules / load_readiness_rules save.</summary>
        public Task<RuleValidationResult> ValidateRuleAsync(string kind, string rules)
        {
            kind = (kind ?? "").Trim().ToLowerInvariant();
            if (kind == "air") kind = "readiness";   // accept the waiver-family alias
            if (kind != "bpa" && kind != "readiness")
                throw new ArgumentException("kind must be 'bpa' (Best Practice Analyzer) or 'readiness' (AI-readiness). Then pass the rule JSON (one object or an array) in rules.");
            if (string.IsNullOrWhiteSpace(rules))
                throw new ArgumentException("rules is required: inline JSON — one rule object or an array (the same schema load_bpa_rules / load_readiness_rules accept).");

            var s = _sessions.Require();
            var isBpa = kind == "bpa";
            return s.ReadAsync(m =>
            {
                var checks = new List<Semanticus.Analysis.RuleCheck>();
                try
                {
                    if (isBpa)
                    {
                        var trimmed = rules.Trim();
                        var parsed = trimmed.StartsWith("{")
                            ? new List<BpaRule> { System.Text.Json.JsonSerializer.Deserialize<BpaRule>(trimmed, BpaValidationJson) }
                            : System.Text.Json.JsonSerializer.Deserialize<List<BpaRule>>(trimmed, BpaValidationJson) ?? new List<BpaRule>();
                        foreach (var r in parsed) checks.Add(Semanticus.Analysis.RuleAuthoring.CheckBpa(m, r));
                    }
                    else
                    {
                        foreach (var d in Semanticus.Analysis.CustomReadinessRuleSet.ParseAll(rules))
                            checks.Add(Semanticus.Analysis.RuleAuthoring.CheckReadiness(m, d));
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new InvalidOperationException($"The rules JSON does not parse ({ex.Message}). Pass one rule object or an array of rules — the same schema load_bpa_rules / load_readiness_rules accept.");
                }
                var allValid = checks.Count > 0 && checks.All(c => c.Valid);
                return new RuleValidationResult
                {
                    Kind = kind, RuleCount = checks.Count, AllValid = allValid, Rules = checks.ToArray(),
                    Note = checks.Count == 0
                        ? "No rules found in the JSON — a rule set is an array of rule objects (each with an \"ID\")."
                        : allValid
                            ? $"All {checks.Count} rule(s) compile. This was a preview only — save with {(isBpa ? "load_bpa_rules" : "load_readiness_rules")} (merge by default)."
                            : "Fix the rules with errors, then validate again. Nothing was saved.",
                };
            });
        }

        // Mirrors BpaRuleSet.Parse's leniency (comments / trailing commas / string-encoded numbers) WITHOUT its
        // drop-no-ID behavior — a validation must report a missing ID, not silently swallow the rule.
        private static readonly System.Text.Json.JsonSerializerOptions BpaValidationJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        };

        /// <summary>The model's custom (user-authored) rules of both kinds, plus the valid category/scope
        /// vocabularies (the single source of truth the authoring UI's dropdowns render). Read-only.</summary>
        public Task<CustomRulesInfo> GetCustomRulesAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var bpa = new List<BpaRule>();
                try
                {
                    var j = m.GetAnnotation("BestPracticeAnalyzer");
                    if (!string.IsNullOrWhiteSpace(j)) bpa = BpaRuleSet.Parse(j);
                }
                catch { /* unparseable model rules — surfaced by scans; the manage list just shows none */ }
                var readiness = new List<Semanticus.Analysis.CustomReadinessRuleDef>();
                try
                {
                    var j = m.GetAnnotation(Semanticus.Analysis.CustomReadinessRuleSet.AnnotationName);
                    if (!string.IsNullOrWhiteSpace(j)) readiness = Semanticus.Analysis.CustomReadinessRuleSet.Parse(j);
                }
                catch { /* same */ }
                return new CustomRulesInfo
                {
                    Bpa = bpa.ToArray(),
                    Readiness = readiness.ToArray(),
                    ReadinessCategories = Enum.GetNames(typeof(Semanticus.Analysis.ReadinessCategory)),
                    Scopes = BpaAnalyzer.SupportedScopeNames,
                    Note = "Custom rules persisted on the model (annotations); they merge on top of the built-in sets. Author with validate_rule, save with load_bpa_rules / load_readiness_rules, clear with reset_bpa_rules / reset_readiness_rules.",
                };
            });
        }

        public Task<BpaScorecard> BpaScanAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => BpaAnalyzer.Analyze(m, GetBpaRules(m)));
        }

        public async Task<SetResult> BpaFixAsync(string ruleId, string objRef, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"BPA fix {ruleId}", m =>
            {
                var rule = GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, ruleId, StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException($"Unknown BPA rule '{ruleId}' — run bpa_scan to see the rule ids in force, or load_bpa_rules to add a rule set.");
                var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
                changed = BpaAnalyzer.ApplyFix(m, rule, obj);
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<BpaFixAllResult> BpaFixAllAsync(string origin)
        {
            var s = _sessions.Require();
            // Pro gate: "fix every BPA violation in one click" is a bulk primitive; free fixes one at a time (bpa_fix).
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Fixing all BPA violations at once",
                "Fix violations one at a time with bpa_fix.");
            var applied = 0;
            var rev = await s.MutateAsync(origin, "BPA: apply all auto-fixes", m =>
            {
                var rules = GetBpaRules(m);
                var card = BpaAnalyzer.Analyze(m, rules);
                foreach (var v in card.Violations.Where(v => v.CanAutoFix && !v.Waived))   // never auto-fix a finding you've accepted
                {
                    var rule = rules.FirstOrDefault(r => string.Equals(r.ID, v.RuleId, StringComparison.OrdinalIgnoreCase));
                    var obj = ObjectRefs.Resolve(m, v.ObjectRef);
                    if (rule != null && obj != null)
                    {
                        try { if (BpaAnalyzer.ApplyFix(m, rule, obj)) applied++; } catch { /* skip a bad fix, continue */ }
                    }
                }
            });
            var after = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, GetBpaRules(m)));
            return new BpaFixAllResult { Revision = rev, Applied = applied, Scorecard = after };
        }

        // ---- Finding waivers (accepted findings) — both BPA + AI-readiness ----
        // A waiver is a documented decision to accept a finding: it drops out of the SCORE (counts as a pass) but is
        // always surfaced (tagged + reasoned) so the grade can't be silently inflated, and hard gates still evaluate on
        // the raw count. Persisted on the model (Semanticus_Waivers annotation, undoable, travels with the model);
        // per-instance BPA waivers also mirror to TE's BestPracticeAnalyzer_IgnoreRules. A reason is required.
        // Rule-level (objRef null/'*' = every instance, model-wide) is the bulk lever → Pro; per-instance is free.

        public async Task<SetResult> WaiveFindingAsync(string system, string ruleId, string objRef, string reason, string origin)
        {
            system = (system ?? "").Trim().ToLowerInvariant();
            if (system != "bpa" && system != "air") throw new ArgumentException("system must be 'bpa' or 'air'.");
            if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("A rule id is required.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to waive a finding (it's an audited, accepted decision — not a silent suppression).");
            bool ruleLevel = WaiverStore.IsRuleLevel(objRef);
            if (ruleLevel)   // waiving an entire rule (every instance) at once is the bulk primitive — the Pro value
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "Waiving an entire rule (all instances) at once",
                    "Waive findings one at a time (pass a specific object ref).");

            var s = _sessions.Require();
            var changed = false;
            string warning = null;
            var rev = await s.MutateAsync(origin, $"Waive {system} {ruleId}" + (ruleLevel ? " (rule-level)" : ""), m =>
            {
                // DATA-PRESERVATION: if the existing waiver annotation is unreadable, copy it aside BEFORE Save
                // overwrites it — otherwise this add would silently destroy the accepted-findings record. If the
                // aside write fails, PreserveCorrupt throws and this whole mutation rolls back (refuse over destroy).
                var recs = WaiverStore.LoadForWrite(m, out var corruptRaw);
                if (corruptRaw != null)
                {
                    var backup = WaiverStore.PreserveCorrupt((IAnnotationObject)m, WaiverStore.Annotation, corruptRaw);
                    warning = $"The existing waiver data was corrupt and could not be fully read; it was preserved to the '{backup}' annotation before this waiver was written. Recover any lost waivers from there.";
                }
                WaiverStore.Add(recs, new WaiverRecord
                {
                    System = system,
                    RuleId = ruleId.Trim(),
                    ObjectRef = ruleLevel ? null : objRef.Trim(),
                    Reason = reason.Trim(),
                    By = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                    When = DateTimeOffset.UtcNow.ToString("o"),
                });
                WaiverStore.Save(m, recs);
                // Mirror a per-instance BPA waiver into TE's annotation so TE3 honours it too (interop).
                if (system == "bpa" && !ruleLevel)
                {
                    var o = ObjectRefs.Resolve(m, objRef);
                    if (o != null) WaiverStore.WriteTeIgnore(o, ruleId.Trim(), true);
                }
                changed = true;
            });
            return new SetResult { Revision = rev, Changed = changed, Warning = warning };
        }

        public async Task<SetResult> UnwaiveFindingAsync(string system, string ruleId, string objRef, string origin)
        {
            system = (system ?? "").Trim().ToLowerInvariant();
            if (system != "bpa" && system != "air") throw new ArgumentException("system must be 'bpa' or 'air'.");
            if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("A rule id is required.");
            bool ruleLevel = WaiverStore.IsRuleLevel(objRef);   // un-waiving makes the model MORE compliant — never gated

            var s = _sessions.Require();
            var removed = false;
            string warning = null;
            var rev = await s.MutateAsync(origin, $"Unwaive {system} {ruleId}", m =>
            {
                // Same data-preservation contract as WaiveFindingAsync: never let a Remove+Save destroy an unreadable
                // store — preserve it aside first, or refuse (PreserveCorrupt throws ⇒ the mutation rolls back).
                var recs = WaiverStore.LoadForWrite(m, out var corruptRaw);
                if (corruptRaw != null)
                {
                    var backup = WaiverStore.PreserveCorrupt((IAnnotationObject)m, WaiverStore.Annotation, corruptRaw);
                    warning = $"The existing waiver data was corrupt and could not be fully read; it was preserved to the '{backup}' annotation before this change. Recover any lost waivers from there.";
                }
                removed = WaiverStore.Remove(recs, system, ruleId.Trim(), ruleLevel ? null : objRef.Trim());
                WaiverStore.Save(m, recs);
                if (system == "bpa" && !ruleLevel)
                {
                    var o = ObjectRefs.Resolve(m, objRef);
                    if (o != null) WaiverStore.WriteTeIgnore(o, ruleId.Trim(), false);
                }
            });
            return new SetResult { Revision = rev, Changed = removed, Warning = warning };
        }

        public Task<WaiverRecord[]> ListWaiversAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => WaiverStore.Load(m).ToArray());
        }

        public Task<FixPrompt> BpaGetFixPromptAsync(string ruleId, string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var rule = GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, ruleId, StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException($"Unknown BPA rule '{ruleId}' — run bpa_scan to see the rule ids in force, or load_bpa_rules to add a rule set.");
                var g = BuildGrounding(m, objRef) ?? throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
                var loc = string.IsNullOrEmpty(g.Table) ? g.Name : $"{g.Kind} [{g.Table}].[{g.Name}]";
                var prompt =
                    $"Using the Semanticus MCP, fix this Best Practice Analyzer violation on {loc}.\n" +
                    $"Rule: {rule.Name} ({rule.Category}).\n" +
                    (string.IsNullOrWhiteSpace(rule.Description) ? "" : $"Why: {rule.Description.Replace("%object%", g.Name)}\n") +
                    (string.IsNullOrWhiteSpace(g.Expression) ? "" : $"Current DAX: {g.Expression}\n") +
                    $"Apply the appropriate tool (e.g. set_description / set_measure_format / rename_object / update_measure) on objRef \"{g.ObjectRef}\", then re-run bpa_scan.";
                return new FixPrompt { ObjectRef = g.ObjectRef, RuleId = ruleId, Tool = "(varies)", Prompt = prompt };
            });
        }

        public async Task<SafeFixResult> ApplySafeFixesAsync(string origin)
        {
            var s = _sessions.Require();
            // Pro gate: "apply every safe AI-readiness fix in one click" is a bulk primitive; free fixes one at a
            // time (apply_fix). Thrown before the mutate batch, so a refusal leaves the model intact.
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Applying all safe fixes at once",
                "Apply fixes one at a time with apply_fix.");
            List<string> applied = null;
            var rev = await s.MutateAsync(origin, "AI-readiness: safe fixes", m => { applied = SafeFixes.Apply(m); });
            var card = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
            return new SafeFixResult { Revision = rev, Applied = applied?.ToArray() ?? Array.Empty<string>(), Scorecard = card };
        }

        /// <summary>Apply the deterministic safe fix for ONE finding (click-to-fix in the UI). Only
        /// SafeFix-kind rules are auto-applicable; AiContent/Proposal rules return Changed=false.</summary>
        public async Task<SetResult> ApplyFixAsync(string ruleId, string objRef, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"AI-readiness fix {ruleId}", m =>
            {
                switch (ruleId)
                {
                    case "VIS-FK":
                        if (ObjectRefs.Resolve(m, objRef) is Column fk && !fk.IsHidden) { fk.IsHidden = true; changed = true; }
                        break;
                    case "FMT-SUMMARIZE":
                    case "SUMMARIZE-DIMENSION":   // same deterministic fix: a non-additive column should not auto-aggregate
                        if (ObjectRefs.Resolve(m, objRef) is Column sc && sc.SummarizeBy != AggregateFunction.None)
                        { sc.SummarizeBy = AggregateFunction.None; changed = true; }
                        break;
                    case "CAT-GEO":
                        if (ObjectRefs.Resolve(m, objRef) is Column gc)
                        {
                            var cat = ReadinessRuleSet.GeoCategory(gc.Name);
                            if (cat != null && gc.DataCategory != cat) { gc.DataCategory = cat; changed = true; }
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Rule '{ruleId}' has no deterministic safe fix; use Claude (get_fix_prompt) or a Proposal review.");
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        /// <summary>Build a ready-to-paste instruction for the user's Claude Code to remediate an
        /// AI-content finding (description / name / synonyms / format) with grounding baked in.</summary>
        public Task<FixPrompt> GetFixPromptAsync(string ruleId, string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var g = BuildGrounding(m, objRef) ?? throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
                return BuildFixPrompt(ruleId, g);
            });
        }

        private static FixPrompt BuildFixPrompt(string ruleId, GroundingBundle g)
        {
            string tool, prompt;
            var loc = string.IsNullOrEmpty(g.Table) ? g.Name : $"{g.Kind} [{g.Table}].[{g.Name}]";
            var siblings = g.SiblingNames != null && g.SiblingNames.Length > 0
                ? " Sibling fields for context: " + string.Join(", ", g.SiblingNames.Take(20)) + "."
                : "";
            switch (ruleId)
            {
                case "DESC-MEASURE":
                case "DESC-TABLE":
                case "DESC-COLUMN":
                case "DESC-ECHO":
                case "DESC-LONG":
                    tool = "set_description";
                    var dax = string.IsNullOrWhiteSpace(g.Expression) ? "" : $" Its DAX is: {g.Expression}.";
                    prompt = $"Using the Semanticus MCP, write a concise (<=200 chars, front-loaded) business description for {loc}.{dax} " +
                             $"Explain what it represents in business terms, not the formula.{siblings} " +
                             $"Then call set_description with objRef \"{g.ObjectRef}\" and your text.";
                    break;
                case "NAME-MEASURE":
                case "NAME-COLUMN":
                    tool = "rename_object";
                    prompt = $"Using the Semanticus MCP, propose a clear, human-readable name for {loc} (currently \"{g.Name}\"). " +
                             $"Use Title Case with spaces, expand acronyms, avoid codes/prefixes.{siblings} " +
                             $"If you're confident it's unused in external reports, call rename_object with objRef \"{g.ObjectRef}\"; otherwise just suggest it for review (rename auto-fixes DAX references but not report bindings).";
                    break;
                case "FMT-MEASURE":
                    tool = "set_measure_format";
                    prompt = $"Using the Semanticus MCP, choose an appropriate format string for {loc} " +
                             $"(e.g. \"#,0\" for counts, \"$#,0.00\" for currency, \"0.0%\" for ratios)" +
                             (string.IsNullOrWhiteSpace(g.Expression) ? "" : $", given its DAX: {g.Expression}") +
                             $". Then call set_measure_format with objRef \"{g.ObjectRef}\".";
                    break;
                case "SYN-FIELD":
                    tool = "set_synonyms";
                    prompt = $"Using the Semanticus MCP, list 2-5 natural-language synonyms a business user might say for {loc}. " +
                             $"Then call set_synonyms with objRef \"{g.ObjectRef}\" and the terms.";
                    break;
                case "SYN-SCHEMA":
                    tool = "enable_qna";
                    prompt = "Using the Semanticus MCP, call enable_qna to seed a Q&A / Copilot linguistic schema, then add synonyms to key fields with set_synonyms.";
                    break;
                // The AiContent DAX best-practice rules: the fix is a REWRITE that is not always behavior-preserving
                // (DIVIDE's blank-vs-zero, ISBLANK vs =BLANK() 0-coercion), so the prompt demands the engine's own
                // equivalence proof before anything is applied — the fix routing contract in
                // docs/dax-best-practice-rules.md §3 (AiContent gated behind verify_dax_equivalence, never blind).
                case "BP-DAX-IFERROR":
                case "BP-DAX-IFERROR-DIV":
                case "BP-DAX-IFERROR-SEARCH":
                case "BP-DAX-ISBLANK-EQ":
                case "BP-DAX-DIV-GUARD":
                    tool = "verify_dax_equivalence";
                    prompt = $"Using the Semanticus MCP, rewrite the DAX of {loc} to fix rule {ruleId} ({RuleGuidance(ruleId)})." +
                             (string.IsNullOrWhiteSpace(g.Expression) ? "" : $" Current DAX: {g.Expression}.") +
                             $" Then PROVE the rewrite with verify_dax_equivalence (old body vs new body) — if it is NOT proven equivalent, report the difference instead of applying (some of these rewrites legitimately change blank/zero semantics; the human decides). Only after a proven-equivalent verdict, apply with update_measure/set_dax on objRef \"{g.ObjectRef}\".";
                    break;
                default:
                    tool = "get_grounding";
                    prompt = $"Using the Semanticus MCP, review {loc} (rule {ruleId}) and remediate it with the appropriate tool. Start with get_grounding on objRef \"{g.ObjectRef}\".";
                    break;
            }
            return new FixPrompt { ObjectRef = g.ObjectRef, RuleId = ruleId, Tool = tool, Prompt = prompt };
        }

        private static string RuleGuidance(string ruleId)
        {
            switch (ruleId)
            {
                case "BP-DAX-IFERROR": return "replace the IFERROR/ISERROR trap by avoiding the error at source — DIVIDE for division, SEARCH/FIND's 4th argument for not-found, COALESCE or input validation otherwise";
                case "BP-DAX-IFERROR-DIV": return "replace the IFERROR-guarded division with DIVIDE(numerator, denominator, alternate)";
                case "BP-DAX-IFERROR-SEARCH": return "pass SEARCH/FIND's 4th NotFoundValue argument, or use CONTAINSSTRING for a boolean test";
                case "BP-DAX-ISBLANK-EQ": return "use NOT ISBLANK(x) instead of x <> BLANK() — note = BLANK() also matches 0 via coercion, so equivalence must be checked";
                case "BP-DAX-DIV-GUARD": return "replace the hand-rolled zero/blank guard with DIVIDE(numerator, denominator, alternate)";
                default: return "see the finding message";
            }
        }

        public Task<GroundingBundle> GetGroundingAsync(string objRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => BuildGrounding(m, objRef) ?? throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name)."));
        }

        // validate_dax: offline syntactic + reference check against the open model (read-only). No live engine needed.
        public Task<DaxValidation> ValidateDaxAsync(string expression)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => DaxValidator.Validate(m, expression));
        }

        // Deterministic DAX best-practice lint (token path). With a session open, lint WITH table-name context so the
        // identity-dependent rules (calculate-bare-table-filter) activate; sessionless behavior is unchanged. ReadAsync
        // is the established safe read route (see ValidateDaxAsync above).
        public Task<DaxLintResult> LintDaxAsync(string expression)
        {
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(DaxLint.Analyze(expression));
            return s.ReadAsync(m =>
            {
                var ctx = new DaxLintContext();
                foreach (var t in m.Tables) ctx.TableNames.Add(t.Name);
                return DaxLint.Analyze(expression, ctx);
            });
        }

        private static GroundingBundle BuildGrounding(Model m, string objRef)
        {
            var obj = ObjectRefs.Resolve(m, objRef);
            if (obj == null)
            {
                // A genuine MODEL-level ref (e.g. DAC-AI-INSTRUCTIONS' "obj:Model/<name>", which ObjectRefs.Resolve
                // can't resolve) grounds with the model itself, so a model-level work item still carries grounding and
                // the AI-queue's non-null-grounding invariant holds. Any OTHER unresolvable ref is a typo'd/stale/
                // deleted object — return null so callers' "?? throw Object not found" fires instead of fabricating a
                // misleading whole-model bundle (which would silently mis-ground get_grounding on a bad ref).
                if (objRef != null && objRef.StartsWith("obj:Model", StringComparison.Ordinal))
                    return new GroundingBundle { ObjectRef = objRef, Name = m.Name, Kind = "model",
                        SiblingNames = m.Tables.Select(t => t.Name).Take(50).ToArray() };
                return null;
            }
            var b = new GroundingBundle { ObjectRef = ObjectRefs.For(obj), Name = obj.Name, Kind = ObjectRefs.KindOf(obj) };
            switch (obj)
            {
                case Measure me:
                    b.Table = me.Table?.Name; b.Expression = me.Expression; b.FormatString = me.FormatString; b.ExistingDescription = me.Description;
                    b.SiblingNames = me.Table?.Measures.Select(x => x.Name).Where(n => n != me.Name).Take(50).ToArray() ?? Array.Empty<string>();
                    break;
                case Column c:
                    b.Table = c.Table?.Name; b.DataType = c.DataType.ToString(); b.ExistingDescription = c.Description;
                    b.SiblingNames = c.Table?.Columns.Select(x => x.Name).Where(n => n != c.Name).Take(50).ToArray() ?? Array.Empty<string>();
                    break;
                case Table t:
                    b.ExistingDescription = t.Description;
                    b.SiblingNames = t.Columns.Select(x => x.Name).Take(50).ToArray();
                    break;
                case CalculationItem ci:
                    b.Table = ci.CalculationGroupTable?.Name; b.Expression = ci.Expression; b.ExistingDescription = ci.Description;
                    b.SiblingNames = ci.CalculationGroupTable?.CalculationItems.Select(x => x.Name).Where(n => n != ci.Name).Take(50).ToArray() ?? Array.Empty<string>();
                    break;
            }
            // Surface the model's calendars (CL 1701+) so an agent authoring DAX for this object can prefer calendar-
            // aware forms (TOTALYTD(expr,'Fiscal')) over classic time intelligence. Model-wide + cheap (pure TOM read).
            b.Calendars = CalendarGroundingLines(m);
            return b;
        }

        public async Task<SetResult> SetColumnMetadataAsync(string objRef, bool? isHidden, string summarizeBy, string dataCategory, string sortByColumn, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set column metadata", m =>
            {
                if (!(ObjectRefs.Resolve(m, objRef) is Column c)) throw new InvalidOperationException($"{objRef} is not a column — pass a 'column:Table/Name' ref; run list_columns on the table to see its columns.");
                if (isHidden.HasValue && c.IsHidden != isHidden.Value) { c.IsHidden = isHidden.Value; changed = true; }
                if (!string.IsNullOrEmpty(summarizeBy) && Enum.TryParse<AggregateFunction>(summarizeBy, true, out var agg) && c.SummarizeBy != agg) { c.SummarizeBy = agg; changed = true; }
                if (dataCategory != null && c.DataCategory != dataCategory) { c.DataCategory = dataCategory; changed = true; }
                if (sortByColumn != null)
                {
                    if (string.IsNullOrWhiteSpace(sortByColumn))
                        throw new ArgumentException("The sort-by column name cannot be empty.");
                    if (c.Table == null || !c.Table.Columns.Contains(sortByColumn))
                        throw new InvalidOperationException($"Sort-by column '{sortByColumn}' was not found on the same table as {objRef}. Choose an existing column from that table.");
                    var sort = c.Table.Columns[sortByColumn];
                    if (ReferenceEquals(c, sort))
                        throw new InvalidOperationException($"{objRef} cannot sort by itself. Choose a different column from the same table.");
                    if (c.SortByColumn != sort) { c.SortByColumn = sort; changed = true; }
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> SetMeasureFormatAsync(string objRef, string formatString, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set measure format", m =>
            {
                if (!(ObjectRefs.Resolve(m, objRef) is Measure me)) throw new InvalidOperationException($"{objRef} is not a measure — pass a 'measure:Table/Name' ref; run list_measures to see the model's measures.");
                if (me.FormatString != formatString) { me.FormatString = formatString; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> MarkDateTableAsync(string tableRef, string dateColumn, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "mark date table", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t)) throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                if (!string.IsNullOrEmpty(dateColumn) && !t.Columns.Contains(dateColumn))
                    throw new InvalidOperationException($"Date column '{dateColumn}' not found on table '{t.Name}' — run list_columns on '{t.Name}' to see its columns, then pass an existing column name.");
                if (!string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) { t.DataCategory = "Time"; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> SetRelationshipAsync(string relationshipName, string crossFilteringBehavior, bool? isActive, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set relationship", m =>
            {
                var rel = m.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r.Name == relationshipName)
                          ?? throw new InvalidOperationException($"Relationship '{relationshipName}' not found — run get_model_summary (or list_objects) to see relationship names; create one with create_relationship.");
                if (crossFilteringBehavior != null)
                {
                    if (!Enum.TryParse<CrossFilteringBehavior>(crossFilteringBehavior, true, out var cf)
                        || !Enum.IsDefined(typeof(CrossFilteringBehavior), cf)
                        || (cf != CrossFilteringBehavior.OneDirection && cf != CrossFilteringBehavior.BothDirections))
                        throw new InvalidOperationException("Cross-filter direction must be single direction or both directions.");
                    if (rel.CrossFilteringBehavior != cf) { rel.CrossFilteringBehavior = cf; changed = true; }
                }
                if (isActive.HasValue && rel.IsActive != isActive.Value) { rel.IsActive = isActive.Value; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // Set a single-column relationship's end cardinalities (Many|One). The diagram's properties panel uses this to
        // change a relationship's shape in place instead of delete-and-recreate. A null/empty end is left unchanged.
        public async Task<SetResult> SetRelationshipCardinalityAsync(string relationshipName, string fromCardinality, string toCardinality, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set relationship cardinality", m =>
            {
                var rel = m.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r.Name == relationshipName)
                          ?? throw new InvalidOperationException($"Relationship '{relationshipName}' not found — run get_model_summary (or list_objects) to see relationship names; create one with create_relationship.");
                RelationshipEndCardinality Parse(string v, RelationshipEndCardinality current)
                {
                    if (string.IsNullOrWhiteSpace(v)) return current;
                    if (!Enum.TryParse<RelationshipEndCardinality>(v, true, out var c) || (c != RelationshipEndCardinality.One && c != RelationshipEndCardinality.Many))
                        throw new InvalidOperationException($"Cardinality must be 'One' or 'Many' (got '{v}').");
                    return c;
                }
                var from = Parse(fromCardinality, rel.FromCardinality);
                var to = Parse(toCardinality, rel.ToCardinality);
                if (rel.FromCardinality != from) { rel.FromCardinality = from; changed = true; }
                if (rel.ToCardinality != to) { rel.ToCardinality = to; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // ---- Incremental refresh policy (TOM BasicRefreshPolicy) — metadata only, never executes a refresh ----

        public Task<RefreshPolicyInfo> GetIncrementalRefreshPolicyAsync(string tableRef)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                var on = t.EnableRefreshPolicy;
                return new RefreshPolicyInfo
                {
                    Table = t.Name,
                    Enabled = on,
                    PolicyType = on ? t.PolicyType.ToString() : null,
                    Mode = on ? t.Mode.ToString() : null,
                    RollingWindowGranularity = t.RollingWindowGranularity.ToString(),
                    RollingWindowPeriods = t.RollingWindowPeriods,
                    IncrementalGranularity = t.IncrementalGranularity.ToString(),
                    IncrementalPeriods = t.IncrementalPeriods,
                    IncrementalPeriodsOffset = t.IncrementalPeriodsOffset,
                    SourceExpression = t.SourceExpression,
                    PollingExpression = t.PollingExpression,
                };
            });
        }

        public async Task<SetResult> SetIncrementalRefreshPolicyAsync(
            string tableRef, string dateColumn,
            int? rollingWindowPeriods, string rollingWindowGranularity,
            int? incrementalPeriods, string incrementalGranularity,
            int? incrementalPeriodsOffset, string mode, string pollingExpression, bool autoWire, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set incremental refresh policy", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");

                var cl = m.Database?.CompatibilityLevel ?? 0;
                if (cl < 1450)
                    throw new InvalidOperationException($"Incremental refresh requires compatibility level 1450 or higher (this model is {cl}; raise it with set_compatibility_level).");

                // Omitted args are PRESERVED on an existing policy; the hard defaults (store 5 years, refresh last 10
                // days, Import) apply only when CREATING one — matching the null=leave-unchanged convention of the
                // other partial setters (SetColumnMetadataAsync / SetRelationshipAsync), so updating one knob never
                // silently wipes the rest. (Read the current values before creating/mutating the policy.)
                var exists = t.EnableRefreshPolicy;
                var rwG = ParseGranularity(rollingWindowGranularity, exists ? t.RollingWindowGranularity : RefreshGranularityType.Year);
                var inG = ParseGranularity(incrementalGranularity, exists ? t.IncrementalGranularity : RefreshGranularityType.Day);
                var rwP = rollingWindowPeriods ?? (exists ? t.RollingWindowPeriods : 5);
                var inP = incrementalPeriods ?? (exists ? t.IncrementalPeriods : 10);
                var inOff = incrementalPeriodsOffset ?? (exists ? t.IncrementalPeriodsOffset : 0);
                if (rwP < 1 || inP < 1) throw new InvalidOperationException("Rolling-window and incremental periods must be >= 1.");
                var refMode = exists ? t.Mode : RefreshPolicyMode.Import;
                if (!string.IsNullOrEmpty(mode))
                {
                    if (!Enum.TryParse(mode, true, out refMode) || !Enum.IsDefined(typeof(RefreshPolicyMode), refMode))
                        throw new InvalidOperationException($"mode must be 'Import' or 'Hybrid' (got '{mode}').");
                    if (refMode == RefreshPolicyMode.Hybrid && cl < 1565)
                        throw new InvalidOperationException($"Hybrid (real-time DirectQuery) mode requires compatibility level 1565 or higher (this model is {cl}).");
                }

                if (autoWire)
                {
                    if (string.IsNullOrWhiteSpace(dateColumn))
                        throw new InvalidOperationException("Choose a date column when autoWire is enabled so the partition filter can be authored safely.");
                    if (!t.Columns.Contains(dateColumn))
                        throw new InvalidOperationException($"Date column '{dateColumn}' not found on table '{t.Name}'. Run list_columns on '{t.Name}' to see its columns, then pass an existing column name.");

                    var rangePartition = t.Partitions.FirstOrDefault(p => p.SourceType == PartitionSourceType.M && MFiltersOnRange(p.Expression));
                    var targetPartition = rangePartition ?? t.Partitions.FirstOrDefault(p => p.SourceType == PartitionSourceType.M)
                        ?? throw new InvalidOperationException($"Table '{t.Name}' has no M partition to receive the RangeStart/RangeEnd filter.");
                    // Build the source before changing TOM so an unsupported M shape cannot leave half-wired parameters.
                    var wiredM = rangePartition == null ? IncrementalRefreshWiring.AppendRangeFilter(targetPartition.Expression, dateColumn) : null;
                    changed |= UpsertRangeParameter(m, "RangeStart", "#datetime(2020, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]");
                    changed |= UpsertRangeParameter(m, "RangeEnd", "#datetime(2021, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]");
                    if (wiredM != null && targetPartition.Expression != wiredM) { targetPartition.Expression = wiredM; changed = true; }
                }

                // Both manual and automatic paths validate the final state before the policy is created.
                ValidateIncrementalRefreshPrereqs(m, t, dateColumn);

                // Create the policy object FIRST — every scalar setter no-ops while RefreshPolicy is null.
                if (!exists) { t.EnableRefreshPolicy = true; changed = true; }

                // Configure (compare-before-assign so an idempotent re-apply reports Changed=false).
                if (t.RollingWindowGranularity != rwG)  { t.RollingWindowGranularity = rwG;  changed = true; }
                if (t.RollingWindowPeriods    != rwP)   { t.RollingWindowPeriods    = rwP;   changed = true; }
                if (t.IncrementalGranularity  != inG)   { t.IncrementalGranularity  = inG;   changed = true; }
                if (t.IncrementalPeriods      != inP)   { t.IncrementalPeriods      = inP;   changed = true; }
                if (t.IncrementalPeriodsOffset!= inOff) { t.IncrementalPeriodsOffset= inOff; changed = true; }
                if (cl >= 1565 && t.Mode != refMode)    { t.Mode = refMode; changed = true; }

                // Detect-Data-Changes polling expression (the M scalar checked to skip re-importing an unchanged
                // partition). null PRESERVES, "" CLEARS, non-empty SETS — matching the partial-setter convention.
                if (pollingExpression != null)
                {
                    var pe = pollingExpression.Length == 0 ? null : pollingExpression;
                    if (t.PollingExpression != pe) { t.PollingExpression = pe; changed = true; }
                }

                // Capture the table's range-filtering M as the policy's partition template (only if unset).
                if (string.IsNullOrEmpty(t.SourceExpression))
                {
                    var tmpl = FirstRangeFilteringM(t);
                    if (tmpl != null) { t.SourceExpression = tmpl; changed = true; }
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> RemoveIncrementalRefreshPolicyAsync(string tableRef, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "remove incremental refresh policy", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                // EnableRefreshPolicy=false nulls RefreshPolicy (undoable via the same SetValue path). Don't clear scalars.
                if (t.EnableRefreshPolicy) { t.EnableRefreshPolicy = false; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        private static RefreshGranularityType ParseGranularity(string s, RefreshGranularityType dflt)
        {
            if (string.IsNullOrEmpty(s)) return dflt;
            // Enum.TryParse accepts any numeric string as an *undefined* enum value (e.g. "99"), so guard with
            // Enum.IsDefined too — only the named Day/Month/Quarter/Year (not Invalid) are valid granularities.
            if (!Enum.TryParse<RefreshGranularityType>(s, true, out var g) || !Enum.IsDefined(typeof(RefreshGranularityType), g) || g == RefreshGranularityType.Invalid)
                throw new InvalidOperationException($"Granularity must be Day, Month, Quarter, or Year (got '{s}').");
            return g;
        }

        private static bool UpsertRangeParameter(Model m, string name, string expression)
        {
            var exact = m.Expressions.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (exact == null)
            {
                var wrongCase = m.Expressions.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (wrongCase != null)
                    throw new InvalidOperationException($"Incremental refresh reserves the case-sensitive name '{name}', but this model contains '{wrongCase.Name}'. Rename it, then retry autoWire.");
                exact = m.AddExpression(name);
                exact.Expression = expression;
                return true;
            }
            if (IsParameterExpr(exact)) return false;   // preserve the analyst's existing parameter values
            exact.Expression = expression;   // upgrade a same-name ordinary M expression into the required parameter
            return true;
        }

        // The hard prerequisites for a *valid* incremental refresh policy. Power BI re-validates at publish, but we
        // refuse the obviously-broken cases up front rather than write a dead policy. (RangeStart/RangeEnd are the
        // reserved, case-sensitive parameter names the service binds — confirmed by TOM's own SourceExpression doc.)
        private static void ValidateIncrementalRefreshPrereqs(Model m, Table t, string dateColumn)
        {
            if (!string.IsNullOrEmpty(dateColumn) && !t.Columns.Contains(dateColumn))
                throw new InvalidOperationException($"Date column '{dateColumn}' not found on table '{t.Name}' — run list_columns on '{t.Name}' to see its columns, then pass an existing column name.");

            var rs = m.Expressions.FirstOrDefault(e => e.Name == "RangeStart");
            var re = m.Expressions.FirstOrDefault(e => e.Name == "RangeEnd");
            if (rs == null || re == null)
                throw new InvalidOperationException("Incremental refresh requires two M parameters named exactly RangeStart and RangeEnd. Create them as DateTime parameters first.");
            if (!IsParameterExpr(rs) || !IsParameterExpr(re))
                throw new InvalidOperationException("RangeStart/RangeEnd exist but are not parameter queries (missing IsParameterQuery=true in their M).");

            if (FirstRangeFilteringM(t) == null)
                throw new InvalidOperationException($"No M partition on table '{t.Name}' filters on RangeStart and RangeEnd. Its partition query needs a date filter like: each [{(string.IsNullOrEmpty(dateColumn) ? "Date" : dateColumn)}] >= RangeStart and [{(string.IsNullOrEmpty(dateColumn) ? "Date" : dateColumn)}] < RangeEnd.");
        }

        // Strip ALL whitespace (not just spaces) before matching, so tab/newline-formatted M parameter meta
        // (IsParameterQuery\t=\ttrue) still reads as a parameter.
        private static bool IsParameterExpr(NamedExpression e) =>
            System.Text.RegularExpressions.Regex.Replace(e.Expression ?? "", @"\s+", "").IndexOf("IsParameterQuery=true", StringComparison.OrdinalIgnoreCase) >= 0;

        private static readonly System.Text.RegularExpressions.Regex ReMComment =
            new System.Text.RegularExpressions.Regex(@"//[^\n]*|/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Does this M actually USE RangeStart/RangeEnd in a date filter — not merely mention them in a comment or
        // inside a larger identifier (e.g. [MyRangeStartCol])? Strip comments, then require each reserved name to
        // appear as a whole word adjacent to a relational operator (the half-open `>= RangeStart` / `< RangeEnd`
        // form, plus the `>`/`<=` and param-on-the-left variants). Best-effort heuristic (string literals aside).
        private static bool MFiltersOnRange(string m)
        {
            if (string.IsNullOrEmpty(m)) return false;
            var s = ReMComment.Replace(m, " ");
            return RelationalUse(s, "RangeStart") && RelationalUse(s, "RangeEnd");
        }

        private static bool RelationalUse(string s, string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(s, @"(<=|<|>=|>)\s*" + name + @"\b|\b" + name + @"\s*(<=|<|>=|>)");

        private static string FirstRangeFilteringM(Table t) =>
            t.Partitions.FirstOrDefault(p => p.SourceType == PartitionSourceType.M && MFiltersOnRange(p.Expression))?.Expression;

        // ---- Row-Level Security (RLS) roles --------------------------------------------------------

        public Task<RoleInfo[]> ListRolesAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => m.Roles.Select(BuildRoleInfo).ToArray());
        }

        public async Task<RoleInfo> CreateRoleAsync(string name, string modelPermission, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A role name is required.");
            var s = _sessions.Require();
            RoleInfo info = null;
            await s.MutateAsync(origin, "create role", m =>
            {
                if (FindRole(m, name) != null) throw new InvalidOperationException($"A role named '{name}' already exists.");
                var role = m.AddRole(name);
                if (!string.IsNullOrWhiteSpace(modelPermission)) role.ModelPermission = ParsePermission(modelPermission);
                info = BuildRoleInfo(role);
            });
            return info;
        }

        public async Task<SetResult> DeleteRoleAsync(string name, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "delete role", m =>
            {
                var role = FindRole(m, name);
                if (role != null) { role.Delete(); changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> SetRolePermissionAsync(string name, string modelPermission, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set role permission", m =>
            {
                var role = FindRole(m, name) ?? throw new InvalidOperationException($"Role '{name}' not found — run list_roles to see roles; create one with create_role.");
                var p = ParsePermission(modelPermission);
                if (role.ModelPermission != p) { role.ModelPermission = p; changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        /// <summary>Set (or clear) a table's RLS row-filter DAX for a role. An empty/null filter removes the filter
        /// (and its table permission). Setting a filter auto-promotes the role's ModelPermission from None to Read —
        /// the result echoes the resulting permission and flags that promotion so the elevation isn't silent.</summary>
        public async Task<SetTablePermissionResult> SetTablePermissionAsync(string roleName, string tableRef, string filterDax, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var promoted = false;
            string finalPerm = null;
            var rev = await s.MutateAsync(origin, "set RLS filter", m =>
            {
                var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found — run list_roles to see roles; create one with create_role.");
                var table = ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"Table not found: {tableRef} — run list_objects to see the model's tables.");
                // A calc group isn't a row-bearing table; the vendored RoleRLSIndexer deliberately excludes calc groups
                // (NonCalculationGroupTables) — its setter has no Keys check, so guard here against an orphaned permission.
                if (table is CalculationGroupTable)
                    throw new InvalidOperationException($"RLS row-filters cannot be applied to a calculation group table ('{table.Name}').");
                var before = role.ModelPermission;
                var current = role.RowLevelSecurity[table] ?? "";
                var next = string.IsNullOrWhiteSpace(filterDax) ? "" : filterDax;
                if (!string.Equals(current, next, StringComparison.Ordinal))
                {
                    role.RowLevelSecurity[table] = next.Length == 0 ? null : next;   // null removes the filter/permission
                    changed = true;
                }
                finalPerm = role.ModelPermission.ToString();
                promoted = before == ModelPermission.None && role.ModelPermission != ModelPermission.None;
            });
            return new SetTablePermissionResult { Revision = rev, Changed = changed, ModelPermission = finalPerm, Promoted = promoted };
        }

        /// <summary>Add or remove an (Azure AD / external) member of a role.</summary>
        public async Task<SetResult> SetRoleMemberAsync(string roleName, string memberName, bool add, string origin)
        {
            if (string.IsNullOrWhiteSpace(memberName)) throw new ArgumentException("A member name is required.");
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, add ? "add role member" : "remove role member", m =>
            {
                var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found — run list_roles to see roles; create one with create_role.");
                var existing = role.Members.FirstOrDefault(mem => string.Equals(mem.MemberName, memberName, StringComparison.OrdinalIgnoreCase));
                if (add && existing == null)
                {
                    // External members are gated by PowerBI governance (V3Restricted) unlike the role/filter itself —
                    // re-wrap the raw "Cannot create..." into actionable guidance instead of leaking TOM internals.
                    try { role.AddExternalMember(memberName); }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(
                            "Cannot add role members to a governed Power BI model — edit RLS members in the Power BI service, " +
                            "or load the model from an unrestricted source (.bim / database). Underlying: " + ex.Message, ex);
                    }
                    changed = true;
                }
                else if (!add && existing != null) { existing.Delete(); changed = true; }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // ---- Object-Level Security (OLS) — per-table / per-column metadata permissions (CL ≥ 1400) --------

        /// <summary>Set a table's object-level (metadata) security for a role: <c>None</c> hides the table's metadata
        /// from the role, <c>Read</c> grants it, <c>Default</c> removes the override. Requires CL ≥ 1400.</summary>
        public async Task<SetResult> SetTableObjectPermissionAsync(string roleName, string tableRef, string permission, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set table OLS", m =>
            {
                var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found — run list_roles to see roles; create one with create_role.");
                if (role.MetadataPermission == null) throw new InvalidOperationException("Object-level security requires compatibility level 1400 or higher.");
                var table = ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"Table not found: {tableRef} — run list_objects to see the model's tables.");
                if (table is CalculationGroupTable)
                    throw new InvalidOperationException($"Object-level security cannot be applied to a calculation group table ('{table.Name}').");
                var p = ParseMetadataPermission(permission);
                if (role.MetadataPermission[table] != p)
                {
                    role.MetadataPermission[table] = p;
                    changed = true;
                    // Default leaves an otherwise-empty TablePermission behind; remove it (NoEffect also guards a still-
                    // present RLS filter / column OLS) so Default is a clean removal — mirrors clearing an RLS filter.
                    if (p == MetadataPermission.Default)
                    {
                        var tp = role.TablePermissions.FindByName(table.Name);
                        if (tp != null && tp.NoEffect) tp.Delete();
                    }
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        /// <summary>Set a column's object-level (metadata) security for a role: <c>None</c> hides the column from the
        /// role, <c>Read</c> grants it, <c>Default</c> removes the override. Requires CL ≥ 1400.</summary>
        public async Task<SetResult> SetColumnObjectPermissionAsync(string roleName, string columnRef, string permission, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set column OLS", m =>
            {
                var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found — run list_roles to see roles; create one with create_role.");
                if (role.MetadataPermission == null) throw new InvalidOperationException("Object-level security requires compatibility level 1400 or higher.");
                if (!(ObjectRefs.Resolve(m, columnRef) is Column col)) throw new InvalidOperationException($"{columnRef} is not a column — pass a 'column:Table/Name' ref; run list_columns on the table to see its columns.");
                // Mirror the table-OLS calc-group guard: a calc group isn't part of the OLS surface (the vendor's
                // RoleOLSIndexer excludes them), so setting column OLS on one would orphan a TablePermission.
                if (col.Table is CalculationGroupTable)
                    throw new InvalidOperationException($"Object-level security cannot be applied to a calculation group column ('{col.Table.Name}'[{col.Name}]).");
                var p = ParseMetadataPermission(permission);
                if (col.ObjectLevelSecurity[role] != p)
                {
                    col.ObjectLevelSecurity[role] = p;
                    changed = true;
                    if (p == MetadataPermission.Default)
                    {
                        var tp = role.TablePermissions.FindByName(col.Table.Name);
                        if (tp != null && tp.NoEffect) tp.Delete();
                    }
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        private static MetadataPermission ParseMetadataPermission(string s) =>
            Enum.TryParse<MetadataPermission>((s ?? "").Trim(), true, out var p)
                ? p
                : throw new ArgumentException($"Unknown object-level permission '{s}'. Use Default, None, or Read.");

        private static RoleInfo BuildRoleInfo(ModelRole r) => new RoleInfo
        {
            Name = r.Name,
            Description = r.Description,
            ModelPermission = r.ModelPermission.ToString(),
            TableFilters = r.TablePermissions
                .Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression))
                .Select(tp => new TablePermissionInfo { Table = tp.Table.Name, FilterExpression = tp.FilterExpression })
                .ToArray(),
            Members = r.Members.Select(mem => mem.MemberName).ToArray(),
            // OLS only exists at CL ≥ 1400 (MetadataPermission/ColumnPermissions indexers are null below that — reading
            // them would NPE), so stay empty otherwise. Surface only non-Default (i.e. actually-restricted) objects.
            ObjectPermissions = r.MetadataPermission == null
                ? System.Array.Empty<ObjectPermissionInfo>()
                : r.TablePermissions
                    .Select(tp => new ObjectPermissionInfo
                    {
                        Table = tp.Table.Name,
                        MetadataPermission = tp.MetadataPermission == MetadataPermission.Default ? null : tp.MetadataPermission.ToString(),
                        Columns = tp.Table.Columns
                            .Where(c => tp.ColumnPermissions[c] != MetadataPermission.Default)
                            .Select(c => new ColumnPermissionInfo { Column = c.Name, MetadataPermission = tp.ColumnPermissions[c].ToString() })
                            .ToArray(),
                    })
                    .Where(op => op.MetadataPermission != null || op.Columns.Length > 0)
                    .ToArray(),
        };

        private static ModelRole FindRole(Model m, string name) =>
            m.Roles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        private static ModelPermission ParsePermission(string s) =>
            Enum.TryParse<ModelPermission>((s ?? "").Trim(), true, out var p)
                ? p
                : throw new ArgumentException($"Unknown model permission '{s}'. Use None, Read, ReadRefresh, Refresh, or Administrator.");

        private static Table ResolveTable(Model m, string tableRef)
        {
            var name = tableRef != null && tableRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase)
                ? tableRef.Substring("table:".Length) : tableRef;
            return string.IsNullOrWhiteSpace(name) ? null : m.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ReadinessPlan> MakeAiReadyAsync(string origin, int maxQueue)
        {
            var s = _sessions.Require();
            // Pro gate: "make the whole model AI-ready in one click" runs the SAME bulk SafeFixes.Apply as
            // apply_safe_fixes — gate it identically so the tool name can't bypass the gate (free scans with
            // ai_readiness_scan and applies fixes one at a time with apply_fix). Thrown before the mutate, so a
            // refusal leaves the model intact.
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Making the whole model AI-ready in one click",
                "Scan with ai_readiness_scan, then apply fixes one at a time with apply_fix.");
            List<string> applied = null;
            var rev = await s.MutateAsync(origin, "AI-readiness: safe fixes", m => { applied = SafeFixes.Apply(m); });
            return await s.ReadAsync(m =>
            {
                var card = new ReadinessAnalyzer().Analyze(m);
                var queue = card.Findings
                    .Where(f => f.Fix == nameof(FixKind.AiContent) && !f.Waived)   // don't re-queue a finding the user accepted
                    .Take(maxQueue <= 0 ? 40 : maxQueue)
                    .Select(f => new ReadinessWorkItem { Finding = f, Grounding = BuildGrounding(m, f.ObjectRef) })
                    .ToArray();
                return new ReadinessPlan { Revision = rev, Applied = applied?.ToArray() ?? Array.Empty<string>(), Scorecard = card, AiQueue = queue };
            });
        }

        public async Task<SetResult> SetDescriptionAsync(string objRef, string text, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set description", m =>
            {
                var obj = ObjectRefs.Resolve(m, objRef);
                switch (obj)
                {
                    case Measure me: if (me.Description != text) { me.Description = text; changed = true; } break;
                    case Table t: if (t.Description != text) { t.Description = text; changed = true; } break;
                    case Column c: if (c.Description != text) { c.Description = text; changed = true; } break;
                    case Perspective p: if (p.Description != text) { p.Description = text; changed = true; } break;
                    default: throw new InvalidOperationException($"setDescription: unsupported object {objRef}");
                }
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> EnableQnaAsync(string culture, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "enable Q&A linguistic schema", m =>
            {
                EnsureLinguisticCulture(m, culture, out var seeded);
                changed = seeded;
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<SetResult> SetSynonymsAsync(string objRef, string[] terms, string culture, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set synonyms", m =>
            {
                if (!(ObjectRefs.Resolve(m, objRef) is TabularNamedObject obj)) throw new InvalidOperationException($"{objRef} not found — set_synonyms takes a table/column/measure/hierarchy ref; run list_objects or search_model to find it.");
                var cult = EnsureLinguisticCulture(m, culture, out _);
                TabularEditor.TOMWrapper.Linguistics.SynonymHelper.SetSynonyms(obj, cult, string.Join(",", terms ?? Array.Empty<string>()));
                changed = true;
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // Prep-for-AI "AI instructions": a single top-level "CustomInstructions" string in the LSDL (Culture.Content),
        // capped at 10k chars (the service ignores anything beyond). Culture.Content is governance-whitelisted, so no
        // PowerBIGovernance suspension is needed (unlike most PBI edits) — it writes the same property set_synonyms does.
        internal const int AiInstructionsLimit = 10000;
        private const string LsdlRefreshNote =
            "AI instructions are set on the model and live for any in-session reader. For a DEPLOYED model, an LSDL edit syncs to the Power BI service only after a model refresh (Direct Lake / DirectQuery may refresh once a day) — it is not instant. Persist to disk with save_model.";

        public async Task<SetInstructionsResult> SetAiInstructionsAsync(string instructions, string culture, string origin)
        {
            var s = _sessions.Require();
            var text = instructions ?? string.Empty;
            if (text.Length > AiInstructionsLimit)
                throw new InvalidOperationException($"AI instructions are {text.Length} chars; the Prep-for-AI limit is {AiInstructionsLimit} (content beyond it is ignored). Condense them.");
            var changed = false;
            string chosenCulture = null; var ambiguous = false;
            var rev = await s.MutateAsync(origin, "set AI instructions", m =>
            {
                changed = ApplyAiInstructions(m, culture, text);
                // Resolve AFTER the apply (a seed may have just created the culture), through the SAME selector the
                // reader uses. Ambiguity is only meaningful when we auto-selected (no explicit culture was named).
                chosenCulture = FindLinguisticCulture(m, culture)?.Name;
                if (string.IsNullOrEmpty(culture)) PrepForAiReader.SelectLinguisticCulture(m, out ambiguous);
            });
            return new SetInstructionsResult
            {
                Revision = rev, Changed = changed, Length = text.Length, Culture = chosenCulture,
                // On a multi-culture model, name the culture we wrote to so the caller can confirm it's the intended one.
                Note = ambiguous && chosenCulture != null
                    ? LsdlRefreshNote + $" (This model has several linguistic cultures; the instructions were written to '{chosenCulture}'. Pass a culture to target a different one.)"
                    : LsdlRefreshNote,
            };
        }

        /// <summary>Read the AI instructions back (the reader the 4 Prep-for-AI WRITERS never had — recovering the
        /// current text used to take an INFO.LINGUISTICMETADATA() DMV query and hand-parsing the LSDL). Read-only.</summary>
        public Task<AiInstructionsInfo> GetAiInstructionsAsync(string culture)
        {
            var s = _sessions.Require();
            return s.ReadAsync(m =>
            {
                var cult = FindLinguisticCulture(m, culture);
                var text = ReadCustomInstructions(cult?.Content);
                return new AiInstructionsInfo
                {
                    Present = !string.IsNullOrEmpty(text),
                    Instructions = text,
                    Length = text?.Length ?? 0,
                    Limit = AiInstructionsLimit,
                    Culture = cult?.Name,
                    Note = cult == null
                        ? "No linguistic schema on the model yet — set_ai_instructions seeds one when it writes."
                        : string.IsNullOrEmpty(text)
                            ? "Linguistic schema present but no AI instructions set — set_ai_instructions writes them."
                            : null,
                };
            });
        }

        public async Task<SetResult> SetAiDataSchemaAsync(string objRef, bool included, string culture, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, "set AI data schema", m =>
            {
                var (table, prop) = ResolveSchemaTarget(m, objRef);
                changed = ApplyAiDataSchema(m, culture, table, prop, included);
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        // ---- shared apply core (used by the direct writers AND the change-plan apply path, so both enforce the
        // SAME no-op-before-seed + read-back guarantees). Each runs inside an existing MutateAsync. -------------

        /// <summary>Set/clear AI instructions. A no-op never seeds a linguistic schema (which would enable Q&A). Returns true if changed.</summary>
        private static bool ApplyAiInstructions(Model m, string culture, string text)
        {
            text ??= "";
            if (text.Length > AiInstructionsLimit)
                throw new InvalidOperationException($"AI instructions are {text.Length} chars; the Prep-for-AI limit is {AiInstructionsLimit}.");
            var existing = FindLinguisticCulture(m, culture);
            if (SetCustomInstructions(existing?.Content, text) == null) return false; // unchanged → no seed, no mutation
            var cult = existing ?? EnsureLinguisticCulture(m, culture, out _);
            var updated = SetCustomInstructions(cult.Content, text);
            if (updated == null) return false;
            cult.Content = updated;
            // Read-back guards the JSON edit (not TMDL serialization — the save+reopen smoke test proves that).
            var expected = text.Length == 0 ? null : text;
            if (!string.Equals(ReadCustomInstructions(cult.Content), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("set_ai_instructions: read-back check failed — the value in the LSDL did not match what was written.");
            PrepForAiReader.Invalidate(m);
            return true;
        }

        /// <summary>Include/exclude a field in the AI data schema. A no-op never seeds a schema. Returns true if changed.</summary>
        private static bool ApplyAiDataSchema(Model m, string culture, string table, string prop, bool included)
        {
            var existing = FindLinguisticCulture(m, culture);
            if (SetEntityVisibility(existing?.Content, table, prop, included) == null) return false; // unchanged → no seed
            var cult = existing ?? EnsureLinguisticCulture(m, culture, out _);
            var updated = SetEntityVisibility(cult.Content, table, prop, included);
            if (updated == null) return false;
            cult.Content = updated;
            if (ReadEntityExcluded(cult.Content, table, prop) != !included)
                throw new InvalidOperationException("set_ai_data_schema: read-back check failed — the AI-data-schema flag did not match the request.");
            PrepForAiReader.Invalidate(m);
            return true;
        }

        /// <summary>Resolve a field ref to its LSDL binding (table[, prop]); a table entity has no property. Throws on unsupported objects.</summary>
        private static (string table, string prop) ResolveSchemaTarget(Model m, string objRef)
        {
            switch (ObjectRefs.Resolve(m, objRef))
            {
                case Table t: return (t.Name, null);
                case Column c when c.Type != ColumnType.RowNumber: return (c.Table?.Name ?? throw new InvalidOperationException($"{objRef} has no table."), c.Name);
                case Measure me: return (me.Table?.Name ?? throw new InvalidOperationException($"{objRef} has no table."), me.Name);
                default: throw new InvalidOperationException($"set_ai_data_schema: {objRef} is not a table/column/measure that can be in the AI data schema.");
            }
        }

        /// <summary>Plan-item "after" → membership: "Excluded" (or false/exclude/hidden/no/0) ⇒ excluded; anything else ⇒ included.</summary>
        private static bool ParseIncluded(string after)
        {
            var a = (after ?? "").Trim().ToLowerInvariant();
            return !(a == "excluded" || a == "exclude" || a == "false" || a == "hidden" || a == "no" || a == "0");
        }

        // ---- AI data schema (LSDL per-entity Visibility) -------------------------------------------
        // Membership in the AI data schema is presence/absence of a per-entity Visibility:{Value:"Hidden"} block in
        // the LSDL: excluded ⇔ present, included ⇔ the whole block is omitted (there is NO positive "Visible" token).
        // The entity is keyed by a slug but identified by its Definition.Binding (ConceptualEntity[/ConceptualProperty]).

        /// <summary>Set/clear a field's AI-data-schema exclusion in an LSDL JSON blob. Returns the new JSON, or null if unchanged.</summary>
        private static string SetEntityVisibility(string content, string table, string prop, bool included)
        {
            var root = string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content).AsObject();
            if (!(root["Entities"] is JsonObject entities)) { entities = new JsonObject(); root["Entities"] = entities; }
            var entity = FindEntity(entities, table, prop);

            if (included)
            {
                // INCLUDE = remove the Visibility block (absence == included). Never delete the whole entity — it also
                // carries the field's synonyms/Terms. No entity, or no flag, means it's already included → no-op.
                if (entity == null || !entity.ContainsKey("Visibility")) return null;
                entity.Remove("Visibility");
            }
            else
            {
                // EXCLUDE = ensure an entity exists carrying Visibility:{Value:"Hidden"}.
                if (entity != null && entity["Visibility"] is JsonObject ev && AsStr(ev["Value"]) == "Hidden")
                    return null; // already excluded
                if (entity == null)
                {
                    entity = NewEntity(table, prop);
                    entities[UniqueEntityKey(entities, table, prop)] = entity;
                }
                entity["Visibility"] = new JsonObject { ["Value"] = "Hidden" };
            }
            return root.ToJsonString(LsdlJsonOptions);
        }

        /// <summary>Read a JSON node as a string, or null if it is absent or not a string (never throws on a malformed value).</summary>
        private static string AsStr(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        /// <summary>Find the LSDL entity bound to (table[, prop]) — matches Definition.Binding (or a legacy bare Binding).</summary>
        private static JsonObject FindEntity(JsonObject entities, string table, string prop)
        {
            foreach (var kv in entities)
            {
                if (!(kv.Value is JsonObject e)) continue;
                var binding = (e["Definition"] as JsonObject)?["Binding"] as JsonObject ?? e["Binding"] as JsonObject;
                if (binding == null || AsStr(binding["ConceptualEntity"]) != table) continue;
                var cp = AsStr(binding["ConceptualProperty"]);
                if (prop == null ? cp == null : cp == prop) return e;
            }
            return null;
        }

        /// <summary>Minimal LSDL entity for a field that has none yet. Mirrors Power BI's generated shape — Definition.Binding,
        /// State:Generated, and the field's own name as a Noun term (every real Power BI entity carries ≥1 term; an empty
        /// Terms array is a shape Power BI never emits and may drop on the next linguistic-schema regeneration).</summary>
        private static JsonObject NewEntity(string table, string prop)
        {
            var binding = new JsonObject { ["ConceptualEntity"] = table };
            if (prop != null) binding["ConceptualProperty"] = prop;
            var noun = prop ?? table;
            return new JsonObject
            {
                ["Definition"] = new JsonObject { ["Binding"] = binding },
                ["State"] = "Generated",
                ["Terms"] = new JsonArray(
                    new JsonObject { [noun] = new JsonObject { ["Type"] = "Noun", ["State"] = "Generated", ["Weight"] = 0.99 } }),
            };
        }

        /// <summary>A unique slug key for a new entity (the key is only a label; the Binding is authoritative). Lower-cased to mirror Power BI.</summary>
        private static string UniqueEntityKey(JsonObject entities, string table, string prop)
        {
            string Clean(string n) => n.Replace("\"", "").Replace("\\", "").Replace(",", "").Replace(".", "_").Replace(" ", "_").ToLowerInvariant();
            var baseKey = prop == null ? Clean(table) : $"{Clean(table)}.{Clean(prop)}";
            var key = baseKey;
            for (var i = 1; entities.ContainsKey(key); i++) key = $"{baseKey}_{i}";
            return key;
        }

        /// <summary>True if the field is excluded from the AI data schema (its entity carries Visibility:Hidden).</summary>
        private static bool ReadEntityExcluded(string content, string table, string prop)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            try
            {
                if (!(JsonNode.Parse(content)?.AsObject()?["Entities"] is JsonObject entities)) return false;
                return FindEntity(entities, table, prop)?["Visibility"] is JsonObject v && AsStr(v["Value"]) == "Hidden";
            }
            catch { return false; }
        }

        // Re-serialise the whole LSDL blob to set/clear one top-level key. A JSON round-trip (not string surgery) is the
        // only safe edit — escaped quotes/newlines in neighbouring values break naive regex edits. RelaxedJsonEscaping
        // keeps unicode/&/< unescaped to match Power BI's own LSDL style and minimise churn to untouched content.
        private static readonly System.Text.Json.JsonSerializerOptions LsdlJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Set or clear the top-level "CustomInstructions" in an LSDL JSON blob. Returns the new JSON, or null if unchanged.</summary>
        private static string SetCustomInstructions(string content, string value)
        {
            var obj = string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content).AsObject();
            var before = obj.TryGetPropertyValue("CustomInstructions", out var cur) ? cur?.GetValue<string>() : null;
            if (string.IsNullOrEmpty(value))
            {
                if (!obj.ContainsKey("CustomInstructions")) return null; // already absent
                obj.Remove("CustomInstructions");
            }
            else
            {
                if (string.Equals(before, value, StringComparison.Ordinal)) return null; // unchanged
                obj["CustomInstructions"] = value;
            }
            return obj.ToJsonString(LsdlJsonOptions);
        }

        /// <summary>Read the top-level "CustomInstructions" from an LSDL JSON blob (null if absent/unparseable).</summary>
        private static string ReadCustomInstructions(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                return JsonNode.Parse(content)?.AsObject() is JsonObject o
                       && o.TryGetPropertyValue("CustomInstructions", out var v) ? v?.GetValue<string>() : null;
            }
            catch { return null; }
        }

        // ---- authoring generators (calendar / time-intelligence) ----------------------------

        public async Task<GenerateResult> GenerateDateTableAsync(string tableName, string startExpr, string endExpr, bool markAsDate, string origin)
        {
            var s = _sessions.Require();
            tableName = string.IsNullOrWhiteSpace(tableName) ? "Date" : tableName.Trim();
            var start = string.IsNullOrWhiteSpace(startExpr) ? "DATE(YEAR(TODAY())-5,1,1)" : startExpr.Trim();
            var end = string.IsNullOrWhiteSpace(endExpr) ? "DATE(YEAR(TODAY()),12,31)" : endExpr.Trim();
            GeneratedObject created = null;
            var rev = await s.MutateAsync(origin, $"generate date table {tableName}", m =>
            {
                if (m.Tables.Contains(tableName)) throw new InvalidOperationException($"A table named '{tableName}' already exists.");
                var expr =
$@"ADDCOLUMNS(
    CALENDAR({start}, {end}),
    ""Year"", YEAR([Date]),
    ""Quarter"", ""Q"" & ROUNDUP(MONTH([Date]) / 3, 0),
    ""Quarter Number"", ROUNDUP(MONTH([Date]) / 3, 0),
    ""Year Quarter"", YEAR([Date]) & ""-Q"" & ROUNDUP(MONTH([Date]) / 3, 0),
    ""Half Year"", ""H"" & IF(MONTH([Date]) <= 6, 1, 2),
    ""Month Number"", MONTH([Date]),
    ""Month"", FORMAT([Date], ""mmmm""),
    ""Year Month"", FORMAT([Date], ""yyyy-MM""),
    ""Year Month Sort"", YEAR([Date]) * 100 + MONTH([Date]),
    ""Day"", DAY([Date]),
    ""Day of Week Number"", WEEKDAY([Date], 2),
    ""Day of Week"", FORMAT([Date], ""dddd""),
    ""Month Offset"", (YEAR([Date]) - YEAR(TODAY())) * 12 + (MONTH([Date]) - MONTH(TODAY())),
    ""Day Offset"", DATEDIFF(TODAY(), [Date], DAY),
    ""Is Current Month"", YEAR([Date]) = YEAR(TODAY()) && MONTH([Date]) = MONTH(TODAY()),
    ""Is Current Year"", YEAR([Date]) = YEAR(TODAY()),
    ""Is Today"", [Date] = TODAY(),
    ""Is Past"", [Date] < TODAY()
)";
                var ct = m.AddCalculatedTable(tableName, expr);
                if (markAsDate) ct.DataCategory = "Time";
                created = new GeneratedObject { Ref = ObjectRefs.For(ct), Name = ct.Name, Expression = expr };
            });
            return new GenerateResult
            {
                Revision = rev,
                Created = created == null ? Array.Empty<GeneratedObject>() : new[] { created },
                Note = "Calendar columns (Year/Quarter/Month + integer sort keys + relative columns like Is Current Month / Day Offset, which re-evaluate at each refresh) materialize when the model is deployed and processed. Marked as a date table" + (markAsDate ? "." : " skipped."),
            };
        }

        public async Task<GenerateResult> GenerateTimeIntelligenceAsync(string baseMeasureRef, string dateColumn, string[] variants, string displayFolder, string origin)
        {
            var s = _sessions.Require();
            var created = new List<GeneratedObject>();
            var skipped = new List<string>();
            var rev = await s.MutateAsync(origin, "generate time-intelligence measures", m =>
            {
                if (!(ObjectRefs.Resolve(m, baseMeasureRef) is Measure baseMeasure))
                    throw new InvalidOperationException($"{baseMeasureRef} is not a measure — generate_time_intelligence wraps a base measure; run list_measures to find one.");
                var table = baseMeasure.Table ?? throw new InvalidOperationException("Base measure has no table.");
                var baseName = baseMeasure.Name;
                var baseRef = $"[{baseName}]";
                var dateCol = ResolveDateColumnDax(m, dateColumn);
                var fmt = baseMeasure.FormatString;
                var folder = string.IsNullOrWhiteSpace(displayFolder) ? "Time Intelligence" : displayFolder;

                var wanted = (variants == null || variants.Length == 0)
                    ? new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" }
                    : variants;

                foreach (var v in wanted)
                {
                    var (suffix, expr, vfmt) = BuildTiVariant(v, baseRef, dateCol, fmt);
                    if (suffix == null) { skipped.Add($"{v} (unknown variant)"); continue; }
                    var name = $"{baseName} {suffix}";
                    if (table.Measures.Contains(name)) { skipped.Add(name + " (exists)"); continue; }
                    var me = table.AddMeasure(name, expr, folder);
                    if (!string.IsNullOrEmpty(vfmt)) me.FormatString = vfmt;
                    created.Add(new GeneratedObject { Ref = ObjectRefs.For(me), Name = me.Name, Expression = expr });
                }
            });
            return new GenerateResult { Revision = rev, Created = created.ToArray(), Skipped = skipped.ToArray() };
        }

        /// <summary>Accept a column ref ("column:Date/Date") and turn it into DAX 'Date'[Date]; pass raw DAX through.</summary>
        private static string ResolveDateColumnDax(Model m, string dateColumn)
        {
            if (string.IsNullOrWhiteSpace(dateColumn)) throw new InvalidOperationException("A date column is required (e.g. 'Date'[Date] or column:Date/Date).");
            if (dateColumn.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
            {
                if (!(ObjectRefs.Resolve(m, dateColumn) is Column c)) throw new InvalidOperationException($"{dateColumn} is not a column — pass a 'column:Table/Name' ref for the date column; run list_columns on the date table to see its columns.");
                return $"'{c.Table?.Name}'[{c.Name}]";
            }
            return dateColumn.Trim();
        }

        private static (string suffix, string expr, string fmt) BuildTiVariant(string variant, string baseRef, string dateCol, string baseFmt)
        {
            var py = $"CALCULATE({baseRef}, DATEADD({dateCol}, -1, YEAR))";
            var pm = $"CALCULATE({baseRef}, DATEADD({dateCol}, -1, MONTH))";   // prior month
            switch ((variant ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "YTD": return ("YTD", $"TOTALYTD({baseRef}, {dateCol})", baseFmt);
                case "QTD": return ("QTD", $"TOTALQTD({baseRef}, {dateCol})", baseFmt);
                case "MTD": return ("MTD", $"TOTALMTD({baseRef}, {dateCol})", baseFmt);
                case "PY": return ("PY", py, baseFmt);
                case "YOY": return ("YoY", $"{baseRef} - {py}", baseFmt);
                case "YOYPCT": return ("YoY %", $"DIVIDE({baseRef} - {py}, {py})", "0.0%");
                // Rolling windows: the trailing N months ending at the last date in filter context.
                case "ROLL12": return ("Rolling 12M", $"CALCULATE({baseRef}, DATESINPERIOD({dateCol}, MAX({dateCol}), -12, MONTH))", baseFmt);
                case "R3M": return ("Rolling 3M", $"CALCULATE({baseRef}, DATESINPERIOD({dateCol}, MAX({dateCol}), -3, MONTH))", baseFmt);
                case "R6M": return ("Rolling 6M", $"CALCULATE({baseRef}, DATESINPERIOD({dateCol}, MAX({dateCol}), -6, MONTH))", baseFmt);
                // Same period last year (the SAMEPERIODLASTYEAR idiom, vs PY's DATEADD); prior-year YTD.
                case "SPLY": return ("SPLY", $"CALCULATE({baseRef}, SAMEPERIODLASTYEAR({dateCol}))", baseFmt);
                case "PYTD": return ("PY YTD", $"CALCULATE(TOTALYTD({baseRef}, {dateCol}), DATEADD({dateCol}, -1, YEAR))", baseFmt);
                // Prior month + month-over-month delta and %.
                case "PM": case "PP": return ("PM", pm, baseFmt);
                case "MOM": return ("MoM", $"{baseRef} - {pm}", baseFmt);
                case "MOMPCT": case "MOM%": return ("MoM %", $"DIVIDE({baseRef} - {pm}, {pm})", "0.0%");
                default: return (null, null, null);
            }
        }

        /// <summary>The model's existing JSON linguistic schema (LSDL) culture, or null — never seeds. Use this for a
        /// no-op check before deciding to mutate, so a no-op never creates a schema (which would enable Q&A) as a side effect.</summary>
        private static Culture FindLinguisticCulture(Model m, string culture)
        {
            // Auto-select (no explicit culture) goes through the SAME selector the Prep-for-AI reader uses, so a scan
            // and a write never disagree on WHICH culture they touch on a multi-culture model. An explicit culture is
            // an unambiguous request — honour it exactly.
            var c = string.IsNullOrEmpty(culture)
                ? PrepForAiReader.SelectLinguisticCulture(m, out _)
                : (m.Cultures.Contains(culture) ? m.Cultures[culture] : null);
            return (c != null && c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content)) ? c : null;
        }

        /// <summary>Find or create a culture with a JSON linguistic schema (seeding a minimal LSDL if needed).</summary>
        private static Culture EnsureLinguisticCulture(Model m, string culture, out bool seeded)
        {
            seeded = false;
            if ((m.Database?.CompatibilityLevel ?? 0) < 1465)
                throw new InvalidOperationException($"Synonyms / Q&A linguistic schema require model compatibility level 1465 or higher (this model is {m.Database?.CompatibilityLevel ?? 0}).");
            var existing = FindLinguisticCulture(m, culture);
            if (existing != null) return existing;

            // Seed only happens for a model that never had Q&A. CAVEAT: this seed is Version 1.0.0 (the donor's shape, proven
            // for synonyms); real Prep-for-AI LSDL is 4.2.0. Whether the service honors AI-instructions/data-schema written
            // into a freshly-seeded 1.0.0 schema is unverified — needs a Power BI Desktop round-trip to confirm.
            var name = string.IsNullOrEmpty(culture) ? (m.Cultures.FirstOrDefault()?.Name ?? "en-US") : culture;
            var cult = m.Cultures.Contains(name) ? m.Cultures[name] : m.AddTranslation(name);
            if (cult.ContentType != ContentType.Json || string.IsNullOrEmpty(cult.Content))
            {
                cult.Content = "{\"Version\":\"1.0.0\",\"Language\":\"" + name + "\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{},\"SemanticSlots\":{},\"Relationships\":{}}";
                seeded = true;
            }
            return cult;
        }

        // ============================================================================================
        // Change-Plan engine — "analyse the whole model → assemble a plan → review as a diff → apply all
        // in one verified, undoable transaction." Also supports incremental edits (add_plan_item). The
        // plan is session-held (PlanStore); nothing mutates the model until apply_plan.
        // ============================================================================================

        /// <summary>Analyse the model and seed a change plan: deterministic safe fixes + BPA auto-fixes
        /// (fully specified) plus an AI-content work queue (Claude fills each via set_plan_item). Nothing
        /// is mutated. <paramref name="scope"/> null/empty = whole model; "table:Name" = that table only.</summary>
        public async Task<ChangePlanView> ProposePlanAsync(string scope, bool includeAi, int maxAiItems, string origin)
        {
            var s = _sessions.Require();
            scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
            var seeds = await s.ReadAsync(m => BuildSeeds(m, scope, includeAi, maxAiItems <= 0 ? 40 : maxAiItems));
            ChangePlanView view;
            await _planGate.WaitAsync();
            try
            {
                var plan = _plans.StartNew(scope);
                foreach (var seed in seeds) plan.Add(seed);
                view = PublishPlan(plan, s.Revision);
            }
            finally { _planGate.Release(); }
            await SafeRebroadcastWorkflowLibraryAsync();   // session.planLoaded → true: re-curate the menu (§10.6). Outside the plan gate to avoid nesting locks.
            return view;
        }

        /// <summary>Bulk find &amp; replace as a Change Plan (Phase 3). Runs the detailed search, then turns every
        /// replaceable match into the RIGHT KIND of plan item — <c>rename</c> for object names (FormulaFixup rewrites
        /// every reference at apply), <c>set_description</c>/<c>set_display_folder</c>/<c>set_measure_format</c>/
        /// <c>set_format_string</c>/<c>set_synonyms</c> for plain text, <c>set_dax</c> for edits INSIDE DAX string
        /// literals/comments (spliced span-wise so reference spans are never touched), <c>set_m</c> for M bodies.
        /// DAX-reference / DAX-code / RLS matches produce NO items (the plan Note reports them honestly — rename the
        /// referenced object instead). NOTHING mutates here; review with get_plan, apply via apply_plan (the existing
        /// Pro gate on &gt;1 item — a single item stays free).</summary>
        public async Task<ChangePlanView> ProposeReplaceAsync(SearchOptions find, string replace, int maxItems, string origin)
        {
            if (find == null || string.IsNullOrWhiteSpace(find.Query))
                throw new ArgumentException("propose_replace: the 'find' text is required (query).");
            var matcher = SearchMatcher.Create(find.Query.Trim(), find.CaseSensitive, find.WholeWord, find.Regex);
            if (matcher.Error != null) throw new InvalidOperationException(matcher.Error);

            var s = _sessions.Require();
            var opts = new SearchOptions
            {
                Query = find.Query.Trim(), CaseSensitive = find.CaseSensitive, WholeWord = find.WholeWord,
                Regex = find.Regex, Kinds = find.Kinds, Fields = find.Fields, Scope = find.Scope,
                Max = int.MaxValue, Offset = 0,   // the plan needs every match, not a page
            };
            var seeds = await s.ReadAsync(m => BuildReplaceSeeds(m, opts, matcher, replace ?? string.Empty));

            var cap = maxItems <= 0 ? 500 : maxItems;
            var items = seeds.Items;
            if (items.Count > cap)
            {
                seeds.Excluded.Add($"{items.Count - cap} further matches over the {cap}-item cap (narrow the search with fields/kinds/scope, or raise maxItems)");
                items = items.Take(cap).ToList();
            }

            ChangePlanView view;
            await _planGate.WaitAsync();
            try
            {
                var plan = _plans.StartNew(string.IsNullOrWhiteSpace(find.Scope) ? null : find.Scope.Trim());
                plan.Note = seeds.Excluded.Count == 0 ? null : "Not included: " + string.Join(" · ", seeds.Excluded) + ".";
                foreach (var it in items) plan.Add(it);
                view = PublishPlan(plan, s.Revision);
            }
            finally { _planGate.Release(); }
            await SafeRebroadcastWorkflowLibraryAsync();   // session.planLoaded → true (mirrors propose_plan)
            return view;
        }

        private sealed class ReplaceSeedResult
        {
            public List<ChangeItem> Items = new();
            public List<string> Excluded = new();   // honesty lines for the plan-level Note
        }

        // Turn detailed-search hits into plan items (read-only; runs on the dispatcher thread). The SAFETY layer is
        // the same one replace_in_object uses: names/plain-text/synonyms/M go through BuildReplaceContext (collision,
        // empty-name and M-breakage checks included), and DAX bodies are spliced SPAN-WISE so only the literal/comment
        // spans change and reference/code spans are never touched (they are counted into Excluded instead).
        private ReplaceSeedResult BuildReplaceSeeds(Model m, SearchOptions opts, SearchMatcher matcher, string replace)
        {
            var seeds = new ReplaceSeedResult();
            var res = ModelSearch.Run(m, opts);
            int refSpans = 0, codeSpans = 0, rlsSpans = 0;

            // Expression hits arrive one-per-MatchClass; regroup per object so ONE set_dax item carries every safe span.
            var exprOrder = new List<string>();
            var exprGroups = new Dictionary<string, (SearchHit hit, List<SearchSpan> safe, int blocked)>(StringComparer.Ordinal);

            ReplaceCtx Ctx(SearchHit h) => BuildReplaceContext(m, new ReplaceRequest
            {
                Ref = h.Ref, Field = h.Field, Find = opts.Query, Replace = replace,
                CaseSensitive = opts.CaseSensitive, WholeWord = opts.WholeWord, Regex = opts.Regex,
                Culture = h.Field == ModelSearch.FSyn ? h.Context : null,
            }, matcher);

            ChangeItem Item(SearchHit h, string kind, string before, string after, string title, string rationale, string status, string note) => new ChangeItem
            {
                ObjectRef = h.Ref, ObjectName = h.Name, Kind = kind, Source = "deterministic",
                Category = CategoryForKind(kind), Severity = "Medium", Risk = RiskForKind(kind),
                Target = TargetForKind(kind), Title = title, Rationale = rationale,
                Before = before, After = after, Status = status, Note = note, Generated = false,
            };

            // The shared apply pipeline treats an empty After as "no content authored" and skips it, so a replacement
            // that would EMPTY a field is surfaced as a skipped item (the single Replace in Search can clear a field).
            const string EmptyNote = "the replacement empties this field. Use the single Replace in Search to clear it.";

            foreach (var h in res.Hits)
            {
                switch (h.Field)
                {
                    case ModelSearch.FExpr:
                    {
                        if (!exprGroups.TryGetValue(h.Ref, out var g)) { g = (h, new List<SearchSpan>(), 0); exprOrder.Add(h.Ref); }
                        if (h.MatchClass == DaxMatchClassifier.Literal || h.MatchClass == DaxMatchClassifier.Comment)
                            g.safe.AddRange(h.Spans);
                        else
                        {
                            g.blocked += h.Spans.Length;
                            if (h.MatchClass == DaxMatchClassifier.Reference) refSpans += h.Spans.Length; else codeSpans += h.Spans.Length;
                        }
                        exprGroups[h.Ref] = g;
                        break;
                    }

                    case ModelSearch.FRls:
                        rlsSpans += h.Spans.Length;   // rename-only in this version (same rule as replace_in_object)
                        break;

                    case ModelSearch.FName:
                    {
                        var ctx = Ctx(h);
                        if (ctx.Count == 0) break;
                        var note = ctx.Warnings.Count > 0 ? string.Join(" ", ctx.Warnings) : null;
                        if (ctx.Block != null)
                            seeds.Items.Add(Item(h, "rename", ctx.Raw, ctx.New, $"Rename to '{ctx.New}'",
                                $"'{ctx.Raw}' cannot take this replacement.", "skipped", ctx.Block));
                        else
                            seeds.Items.Add(Item(h, "rename", ctx.Raw, ctx.New, $"Rename to '{ctx.New}'",
                                $"'{ctx.Raw}' becomes '{ctx.New}'. {(ctx.RefCount ?? 0)} reference(s) update automatically.", "proposed", note));
                        break;
                    }

                    case ModelSearch.FDesc:
                    case ModelSearch.FFolder:
                    case ModelSearch.FFormat:
                    {
                        var kind = h.Field == ModelSearch.FDesc ? "set_description"
                            : h.Field == ModelSearch.FFolder ? "set_display_folder"
                            : (h.Kind == "measure" ? "set_measure_format" : "set_format_string");
                        var label = h.Field == ModelSearch.FDesc ? "description" : h.Field == ModelSearch.FFolder ? "display folder" : "format string";
                        var ctx = Ctx(h);
                        if (ctx.Count == 0 || string.Equals(ctx.Raw, ctx.New, StringComparison.Ordinal)) break;
                        var empty = string.IsNullOrEmpty(ctx.New);
                        seeds.Items.Add(Item(h, kind, ctx.Raw, ctx.New, $"Replace text in {label}",
                            $"{ctx.Count} occurrence(s) of '{opts.Query}' replaced.", empty ? "skipped" : "approved", empty ? EmptyNote : null));
                        break;
                    }

                    case ModelSearch.FSyn:
                    {
                        // The plan apply writes the model's default linguistic culture; a hit from another culture
                        // can't ride this item honestly, so it is surfaced as skipped (use the single Replace instead).
                        var defCulture = FindLinguisticCulture(m, null)?.Name;
                        var ctx = Ctx(h);
                        if (ctx.Count == 0 || string.Equals(ctx.Raw, ctx.New, StringComparison.Ordinal)) break;
                        var wrongCulture = defCulture == null || !string.Equals(ctx.Culture, defCulture, StringComparison.OrdinalIgnoreCase);
                        var empty = string.IsNullOrEmpty(ctx.New);
                        seeds.Items.Add(Item(h, "set_synonyms", ctx.Raw, ctx.New, "Replace text in synonyms",
                            $"{ctx.Count} occurrence(s) of '{opts.Query}' replaced.",
                            (wrongCulture || empty) ? "skipped" : "approved",
                            wrongCulture ? $"these synonyms live in culture '{ctx.Culture}'. Bulk apply writes '{defCulture ?? "(none)"}' only; use the single Replace in Search for this one." : (empty ? EmptyNote : null)));
                        break;
                    }

                    case ModelSearch.FM:
                    {
                        var ctx = Ctx(h);
                        if (ctx.Count == 0 || string.Equals(ctx.Raw, ctx.New, StringComparison.Ordinal)) break;
                        seeds.Items.Add(Item(h, "set_m", ctx.Raw, ctx.New, "Replace text in M query",
                            $"{ctx.Count} occurrence(s) of '{opts.Query}' replaced.", "proposed",
                            "M is a literal edit (references are not auto-fixed). Check the query still works after applying."));
                        break;
                    }
                }
            }

            // One set_dax item per DAX body that has safe (literal/comment) spans; reference/code spans stay untouched.
            foreach (var key in exprOrder)
            {
                var g = exprGroups[key];
                if (g.safe.Count == 0) continue;   // reference/code only: counted into Excluded above
                var raw = ReadDax(ObjectRefs.Resolve(m, key));
                if (string.IsNullOrEmpty(raw)) continue;
                var after = SearchMatcher.ApplyAt(raw, replace, g.safe, out var n);
                if (n == 0 || string.Equals(raw, after, StringComparison.Ordinal)) continue;
                var untouched = g.blocked > 0 ? $" {g.blocked} reference/code match(es) in this formula are left unchanged; rename the referenced object to change those." : "";
                var v = DaxValidator.Validate(m, after);
                var parses = v != null && v.Valid;
                seeds.Items.Add(Item(g.hit, "set_dax", raw, after, "Replace text inside DAX literals/comments",
                    $"{n} occurrence(s) of '{opts.Query}' replaced inside string literals/comments.{untouched}",
                    parses ? "proposed" : "skipped",
                    parses
                        ? "Changes what the formula returns (validated for syntax, not equivalence)."
                        : "the edited DAX would no longer parse, so this replacement is refused."));
            }

            if (refSpans > 0) seeds.Excluded.Add($"{refSpans} formula-reference match(es) (rename the referenced object; references update automatically)");
            if (codeSpans > 0) seeds.Excluded.Add($"{codeSpans} formula-code match(es) (edit the expression directly)");
            if (rlsSpans > 0) seeds.Excluded.Add($"{rlsSpans} security-filter match(es) (rename-only in this version)");
            return seeds;
        }

        public async Task<ChangePlanView> GetPlanAsync()
        {
            await _planGate.WaitAsync();
            try { return BuildView(_plans.Current, _sessions.Current?.Revision ?? 0); }
            finally { _planGate.Release(); }
        }

        /// <summary>Add a custom change to the plan (incremental edit, or a Claude-proposed DAX rewrite).
        /// For a set_dax item, pass verifyGroupBy to gate it through the equivalence check at apply time.
        /// A set_dax rewrite WITHOUT a verify matrix is left "proposed" (opt-in, like a rename) — it is never
        /// auto-applied unverified; a human must explicitly approve it to accept applying it without a proof.</summary>
        public async Task<ChangePlanView> AddPlanItemAsync(string objRef, string kind, string after, string title, string[] verifyGroupBy, string[] verifyFilters, string origin)
        {
            var s = _sessions.Require();
            kind = (kind ?? "").Trim();
            var ctx = await s.ReadAsync(m => ReadItemContext(m, objRef, kind));
            await _planGate.WaitAsync();
            try
            {
                var plan = _plans.GetOrStart();
                string status = (after == null && RequiresAfter(kind)) ? "needs_content"
                    : OptInKind(kind, verifyGroupBy) ? "proposed"   // risky / unproven → opt-in (explicit approve required)
                    : "approved";
                var it = new ChangeItem
                {
                    ObjectRef = objRef,
                    ObjectName = ctx.name,
                    Kind = kind,
                    Source = "ai",
                    Category = CategoryForKind(kind),
                    Severity = "Medium",
                    Risk = RiskForKind(kind),
                    RuleId = null,
                    Target = TargetForKind(kind),
                    Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(kind) : title,
                    Rationale = "Added to the plan.",
                    Before = ctx.before,
                    After = after,
                    VerifyGroupBy = verifyGroupBy,   // null = no gate (opt-in apply); [] = scalar equivalence; [...] = matrix equivalence
                    VerifyFilters = verifyFilters,
                    Status = status,
                    Generated = after != null,
                };
                plan.Add(it);
                return PublishPlan(plan, s.Revision);
            }
            finally { _planGate.Release(); }
        }

        /// <summary>Fill an AI item with Claude's authored value and/or approve/reject it. Authoring a value
        /// moves the item to approved — except the OPT-IN kinds (<see cref="OptInKind"/>: rename / unproven
        /// set_dax / set_m), which always land on "proposed" after a value revision, even one made after an
        /// approval (the consent covered the old value). Approving is refused if the kind needs content but
        /// none was authored.</summary>
        public async Task<ChangePlanView> SetPlanItemAsync(string itemId, string after, bool? approved, string origin)
        {
            await _planGate.WaitAsync();
            try
            {
                var plan = _plans.Require();
                var it = plan.Find(itemId) ?? throw new InvalidOperationException($"Plan item '{itemId}' not found — run get_plan to see the current plan's item ids.");
                if (after != null)
                {
                    it.After = after;
                    it.Generated = true;
                    it.VerifyState = null; it.Note = null;   // a re-authored value invalidates any prior verify/apply note
                    // Class-driven gate integrity: an OPT-IN kind stays opt-in through ANY value revision —
                    // including one made after approval (the human consented to the OLD value; a new value needs
                    // fresh consent). Authoring only ever auto-approves the safe kinds.
                    if (OptInKind(it.Kind, it.VerifyGroupBy)) { if (it.Status != "rejected") it.Status = "proposed"; }
                    else if (it.Status == "needs_content" || it.Status == "proposed") it.Status = "approved";
                }
                if (approved == true)
                    // "approved" must mean apply-ready — refuse to approve an item that still needs content.
                    it.Status = (RequiresAfter(it.Kind) && string.IsNullOrEmpty(it.After)) ? "needs_content" : "approved";
                else if (approved == false) it.Status = "rejected";
                return PublishPlan(plan, _sessions.Current?.Revision ?? 0);
            }
            finally { _planGate.Release(); }
        }

        public async Task<ChangePlanView> ClearPlanAsync(string origin)
        {
            ChangePlanView view;
            await _planGate.WaitAsync();
            try
            {
                _plans.Clear();
                view = new ChangePlanView { Items = Array.Empty<ChangeItem>(), Summary = new PlanSummary(), Revision = _sessions.Current?.Revision ?? 0 };
                _sessions.Bus.PublishPlan(view);
            }
            finally { _planGate.Release(); }
            await SafeRebroadcastWorkflowLibraryAsync();   // session.planLoaded → false: re-curate the menu (§10.6). Outside the plan gate to avoid nesting locks.
            return view;
        }

        /// <summary>Execute the approved subset of the plan as ONE undoable batch. Only APPROVED items are
        /// applied (explicit ids merely narrow the approved set — rejected / proposed / needs_content items
        /// are never applied). DAX rewrites that carry a verify matrix are proven equivalent first and skipped
        /// if results change or can't be proven; renames run after edits and removals run last so they don't invalidate
        /// refs needed by earlier items. Returns a report with per-item outcomes + before→after deltas.</summary>
        public async Task<ApplyPlanReport> ApplyPlanAsync(string[] approvedIds, string origin, string[] overrideIds = null, string overrideReason = null)
        {
            var s = _sessions.Require();
            // Accountable override (Verified Edits): items named in overrideIds ship even when their equivalence
            // verdict is failed/unverifiable — but only WITH a reason, and each shipped override is appended to the
            // model's audit chain below. The verdict itself stays honest (a failed proof is still "failed"); the
            // override changes what ships, never what the referee measured. Never a hard wall, never silent.
            var overrides = new HashSet<string>(overrideIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (overrides.Count > 0 && string.IsNullOrWhiteSpace(overrideReason))
                throw new ArgumentException("apply_plan: overrideIds need an overrideReason — an unexplained override is a silent suppression (it is recorded in the audit trail).");
            await _planGate.WaitAsync();
            try
            {
            var plan = _plans.Require();
            var all = plan.Snapshot();

            // Target set: only APPROVED items; explicit ids narrow that set (they never widen it to
            // rejected/proposed/needs_content — that would bypass the human's review on the explicit-id path).
            HashSet<string> idSet = approvedIds != null && approvedIds.Length > 0
                ? new HashSet<string>(approvedIds, StringComparer.OrdinalIgnoreCase)
                : null;
            var targets = all.Where(i => i.Status == "approved" && (idSet == null || idSet.Contains(i.Id))).ToList();

            // Reset transient per-item state so the report reflects THIS run, not a prior apply/skip.
            foreach (var it in targets) { it.Note = null; it.VerifyState = null; }

            // Items still missing required content can't be applied.
            var toApply = new List<ChangeItem>();
            foreach (var it in targets)
            {
                if (RequiresAfter(it.Kind) && string.IsNullOrEmpty(it.After))
                {
                    it.Status = "skipped"; it.Note = "no content authored yet";
                    continue;
                }
                toApply.Add(it);
            }

            // Pro gate: applying MULTIPLE changes in one shot is the one-click bulk primitive (the Pro value); the
            // free tier applies items one at a time. Thrown BEFORE any mutation, so a refusal leaves the model intact.
            if (toApply.Count > 1)
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "Applying multiple changes at once",
                    "Apply changes one at a time (approve a single item), or use the individual edit tools.");

            // Re-read the CURRENT body of each gated set_dax target at apply time, so the equivalence proof
            // is against the body actually in the model NOW — not the (possibly stale) baseline captured at
            // add time (the other door, or an earlier item in this batch, may have changed it since).
            var gated = toApply.Where(i => string.Equals(i.Kind, "set_dax", StringComparison.OrdinalIgnoreCase) && i.VerifyGroupBy != null).ToList();
            var baseline = gated.Count == 0 ? null : await s.ReadAsync(m =>
                gated.ToDictionary(i => i.Id, i => CurrentDax(ObjectRefs.Resolve(m, i.ObjectRef)) ?? i.Before ?? ""));

            // Verify-gate set_dax rewrites that carry a matrix (async, BEFORE the mutate batch). Snapshot
            // the live connection once so a concurrent disconnect from the other door can't null it mid-loop.
            var verifyConn = _live;
            // A RED/unprovable verdict normally skips the item; an item named in overrideIds ships anyway with
            // its honest VerifyState kept and the override noted (and audit-recorded after the apply).
            var overridden = new List<ChangeItem>();
            void FailVerify(ChangeItem item, string state, string why)
            {
                item.VerifyState = state;
                if (overrides.Contains(item.Id)) { item.Note = "override accepted — " + why; overridden.Add(item); }
                else { item.Status = "skipped"; item.Note = "DAX rewrite not applied: " + why; toApply.Remove(item); }
            }
            foreach (var it in toApply.ToList())
            {
                if (!string.Equals(it.Kind, "set_dax", StringComparison.OrdinalIgnoreCase) || it.VerifyGroupBy == null) continue;
                if (verifyConn == null)
                {
                    FailVerify(it, "unverified", "no live connection to prove equivalence");
                    continue;
                }
                var v = await DaxBench.VerifyEquivalenceAsync(verifyConn, baseline[it.Id], it.After, it.VerifyGroupBy, it.VerifyFilters, 100000);
                if (!string.IsNullOrEmpty(v.Error))
                    FailVerify(it, "unverified", "equivalence check failed to run — " + v.Error);
                else if (!v.AllMatch)
                    FailVerify(it, "failed", $"changes results in {v.MismatchCount} context(s)");
                else if (v.RowsCompared <= 0)
                    // Zero rows compared (empty group-by dimension / all-excluding filters) proves nothing —
                    // AllMatch is vacuously true. Treat as unprovable, not proven.
                    FailVerify(it, "unverified", "equivalence check compared 0 rows (nothing to prove)");
                else if (v.Truncated)
                    // The comparison hit the row cap — divergent contexts past it are invisible, so a match
                    // over the read subset is not a proof. Don't apply it as "verified".
                    FailVerify(it, "unverified", $"equivalence matrix exceeded the row cap ({v.RowsCompared}+ rows) — coverage incomplete");
                else if (it.VerifyGroupBy.Length == 0)
                {
                    // Empty matrix proves only the GRAND TOTAL agrees, not per-context equivalence. The item
                    // is opt-in (proposed→approved by the human), so apply it, but label it honestly: not proven.
                    it.VerifyState = "unverified";
                    it.Note = "applied: grand-total match only — not a per-context equivalence proof";
                }
                else it.VerifyState = "verified";
            }

            // Apply order: edits, CHILD renames, TABLE renames, then removals. A rename changes an object's Name,
            // after which a cached ObjectRef can resolve stale; a removal can invalidate every later ref. (Stable.)
            toApply = toApply.OrderBy(ApplyOrder).ToList();

            // Before snapshot (for the report deltas).
            var before = await s.ReadAsync(m => (bpa: BpaAnalyzer.Analyze(m, GetBpaRules(m)).ViolationCount, card: new ReadinessAnalyzer().Analyze(m)));

            var applied = 0; var failed = 0;
            var rev = await s.MutateAsync(origin, "apply change plan", m =>
            {
                foreach (var it in toApply)
                {
                    try
                    {
                        ApplyOneItem(m, it);
                        // An approved set_dax with no verify matrix went in WITHOUT an equivalence proof
                        // (the human opted in) — flag it so the report/summary surfaces the unproven rewrite.
                        if (string.Equals(it.Kind, "set_dax", StringComparison.OrdinalIgnoreCase) && it.VerifyGroupBy == null)
                            it.VerifyState = "unverified";
                        it.Status = "applied"; it.Note = it.Note ?? "applied"; applied++;
                    }
                    catch (Exception ex) { it.Status = "failed"; it.Note = ex.Message; failed++; }
                }
            });

            var after = await s.ReadAsync(m => (bpa: BpaAnalyzer.Analyze(m, GetBpaRules(m)).ViolationCount, card: new ReadinessAnalyzer().Analyze(m)));

            var skipped = targets.Count(i => i.Status == "skipped");
            var report = new ApplyPlanReport
            {
                Revision = rev,
                AppliedCount = applied,
                FailedCount = failed,
                SkippedCount = skipped,
                Items = targets.ToArray(),
                BpaViolationsBefore = before.bpa,
                BpaViolationsAfter = after.bpa,
                GradeBefore = before.card.Grade,
                GradeAfter = after.card.Grade,
                OverallBefore = before.card.Overall,
                OverallAfter = after.card.Overall,
                Note = $"{applied} applied · {skipped} skipped · {failed} failed · BPA {before.bpa}→{after.bpa} · grade {before.card.Grade}→{after.card.Grade}",
            };
            // Learning Loop L3 distillable hint: a clean multi-item apply that didn't regress the grade
            // is a repeatable recipe worth /distill-workflow. Grade delta >= 0 = OverallAfter >= OverallBefore.
            report.Distillable = report.FailedCount == 0 && report.AppliedCount >= 2 && report.OverallAfter >= report.OverallBefore;
            report.DistillableWhy = report.Distillable
                ? $"{report.AppliedCount} items applied cleanly with no failures and the grade held ({before.card.Grade}→{after.card.Grade}) — a repeatable recipe; run /distill-workflow to capture it."
                : null;

            // Audit trail: one record per SHIPPED override (the accountable act, per-object queryable), then the
            // batch summary — the Change-Plan certificate seed: the verified/unverified/overridden denominator is
            // persisted, not just returned. Only a batch that actually changed the model is recorded.
            if (applied > 0)
            {
                foreach (var it in overridden.Where(i => i.Status == "applied"))
                    await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                    {
                        SessionId = s.Id, Revision = rev, Origin = origin, Op = "apply_plan-item", ObjectRef = it.ObjectRef,
                        Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                        Summary = $"{it.Kind} shipped past a '{it.VerifyState}' equivalence verdict — {it.Note}",
                        Evidence = System.Text.Json.JsonSerializer.Serialize(new { it.Id, it.Kind, it.VerifyState }),
                        BodyHash = VerifiedEditsStore.BodyHash(it.After ?? ""),
                    });
                var appliedItems = targets.Where(i => i.Status == "applied").ToList();
                // vitalsChangedRefs: the batch record's ObjectRef is null (model-level), so hand the ambient
                // vital-signs capture the ACTUAL applied refs — FABLE-a cone-intersection needs real roots.
                await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                {
                    SessionId = s.Id, Revision = rev, Origin = origin, Op = "apply_plan",
                    Verdict = "batch", Summary = report.Note,
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        applied, skipped, failed,
                        verified = appliedItems.Count(i => i.VerifyState == "verified"),
                        unverified = appliedItems.Count(i => i.VerifyState == "unverified"),
                        overridden = overridden.Count(i => i.Status == "applied"),
                        bpaBefore = before.bpa, bpaAfter = after.bpa,
                        gradeBefore = before.card.Grade, gradeAfter = after.card.Grade,
                        // Cap the per-item digest, NOT the whole payload: a 200-item AI-readiness batch must never
                        // trip the store's evidence cap and lose the counts above — the certificate's denominator.
                        items = appliedItems.Take(100).Select(i => new { i.Id, i.Kind, i.ObjectRef, i.VerifyState }).ToArray(),
                        itemsTruncated = appliedItems.Count > 100,
                    }),
                    BodyHash = VerifiedEditsStore.BodyHash(appliedItems.Select(i => i.After ?? "").ToArray()),
                }, appliedItems.Select(i => i.ObjectRef).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).ToArray());
            }
            // Learning Loop L0: the report (grade delta + per-item verify states) used to be built then
            // DISCARDED after return — publish it as activity so the host's experience tee persists a
            // complete "what worked" record. Best-effort: capture never breaks the apply.
            try
            {
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "apply_plan", Origin = origin, Label = report.Note, Ok = failed == 0,
                    Result = new
                    {
                        applied, skipped, failed,
                        bpaBefore = before.bpa, bpaAfter = after.bpa,
                        gradeBefore = before.card.Grade, gradeAfter = after.card.Grade,
                        overallBefore = before.card.Overall, overallAfter = after.card.Overall,
                        items = targets.Take(100).Select(i => new { i.Id, i.Kind, i.ObjectRef, i.RuleId, i.VerifyState, i.Status }).ToArray(),
                        itemsTruncated = targets.Count > 100,
                    },
                });
            }
            catch { }
            PublishPlan(plan, rev);
            return report;
            }
            finally { _planGate.Release(); }
        }

        // ---- plan seeding (read-only; runs on the dispatcher thread) -------------------------------

        private static List<ChangeItem> BuildSeeds(Model m, string scope, bool includeAi, int maxAi)
        {
            var list = new List<ChangeItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // dedup by objRef::target

            var card = new ReadinessAnalyzer().Analyze(m);

            // 1) deterministic safe fixes (AI-readiness)
            foreach (var f in card.Findings.Where(f => f.Fix == nameof(FixKind.SafeFix)))
            {
                if (!InScope(f.ObjectRef, scope)) continue;
                var it = MapSafeFix(m, f);
                if (it != null && seen.Add(it.ObjectRef + "::" + it.Target)) list.Add(it);
            }

            // 1b) deterministic fixes that the readiness ruleset files under AiContent but need no content
            //     (enable_qna / SYN-SCHEMA). These are auto-approved safe fixes, so they must be seeded
            //     regardless of includeAi and must NOT consume an AI-queue slot.
            foreach (var f in card.Findings.Where(f => f.RuleId == "SYN-SCHEMA"))
            {
                if (!InScope(f.ObjectRef, scope)) continue;
                var it = MapAiContent(m, f);
                if (it != null && seen.Add(it.ObjectRef + "::" + it.Target)) list.Add(it);
            }

            // 2) BPA deterministic auto-fixes
            var rules = GetBpaRules(m);
            var bpa = BpaAnalyzer.Analyze(m, rules);
            foreach (var v in bpa.Violations.Where(v => v.CanAutoFix))
            {
                if (!InScope(v.ObjectRef, scope)) continue;
                var it = MapBpaFix(m, rules, v);
                if (it != null && seen.Add(it.ObjectRef + "::" + it.Target)) list.Add(it);
            }

            // 3) AI-content work queue (Claude fills After via set_plan_item). SYN-SCHEMA handled in 1b.
            if (includeAi)
            {
                // The single, high-value, MODEL-level AI-instructions item must not compete in (and lose) the
                // per-object maxAi budget. Seed it FIRST, unbudgeted — mirroring the SYN-SCHEMA precedent (1b), where a
                // model-level item is deliberately seeded outside the AI-queue throttle so a flood of per-object
                // findings can't starve it. DAC-AI-INSTRUCTIONS (absent) is exclusive with -LEN/-GLOSSARY-GAP (present),
                // but -LEN and -GLOSSARY-GAP CAN co-fire — all three target set_ai_instructions, so the shared dedup key
                // keeps ONE "edit the AI instructions" item; merge the co-firing findings' rationale so none is lost.
                ChangeItem instrItem = null;
                foreach (var f in card.Findings.Where(f => f.RuleId == "DAC-AI-INSTRUCTIONS" || f.RuleId == "DAC-AI-INSTRUCTIONS-LEN" || f.RuleId == "DAC-GLOSSARY-GAP"))
                {
                    if (!InScope(f.ObjectRef, scope)) continue;
                    var it = MapAiContent(m, f);
                    if (it == null) continue;
                    if (seen.Add(it.ObjectRef + "::" + it.Target)) { list.Add(it); instrItem = it; }
                    else if (instrItem != null && !string.IsNullOrEmpty(f.Message)
                             && (instrItem.Rationale == null || !instrItem.Rationale.Contains(f.Message)))
                        instrItem.Rationale += "\n" + f.Message; // a co-firing instruction finding — keep its guidance
                }

                // Per-object AI-content queue. SYN-SCHEMA is the deterministic enable_qna seed (1b); the model-level
                // AI-instructions findings are seeded above, unbudgeted — exclude all of them from this throttle.
                var added = 0;
                foreach (var f in card.Findings.Where(f => f.Fix == nameof(FixKind.AiContent)
                             && f.RuleId != "SYN-SCHEMA" && f.RuleId != "DAC-AI-INSTRUCTIONS" && f.RuleId != "DAC-AI-INSTRUCTIONS-LEN" && f.RuleId != "DAC-GLOSSARY-GAP"))
                {
                    if (added >= maxAi) break;
                    if (!InScope(f.ObjectRef, scope)) continue;
                    var it = MapAiContent(m, f);
                    if (it != null && seen.Add(it.ObjectRef + "::" + it.Target)) { list.Add(it); added++; }
                }
            }
            return list;
        }

        private static ChangeItem MapSafeFix(Model m, ReadinessFinding f)
        {
            var obj = ObjectRefs.Resolve(m, f.ObjectRef);
            switch (f.RuleId)
            {
                case "VIS-FK":
                    return new ChangeItem
                    {
                        ObjectRef = f.ObjectRef, ObjectName = f.ObjectName, Kind = "set_column_hidden", Source = "deterministic",
                        Category = "Visibility", Severity = f.Severity, Risk = "safe", RuleId = f.RuleId, Target = "isHidden",
                        Title = "Hide foreign-key column", Rationale = f.Message, Before = "Visible", After = "Hidden", Status = "approved",
                    };
                case "FMT-SUMMARIZE":
                case "SUMMARIZE-DIMENSION":   // identical deterministic fix (SummarizeBy=None) for a non-additive identifier column
                    return new ChangeItem
                    {
                        ObjectRef = f.ObjectRef, ObjectName = f.ObjectName, Kind = "set_summarize_by", Source = "deterministic",
                        Category = f.Category, Severity = f.Severity, Risk = "safe", RuleId = f.RuleId, Target = "summarizeBy",
                        Title = "Set SummarizeBy = None", Rationale = f.Message,
                        Before = (obj as Column)?.SummarizeBy.ToString() ?? "?", After = "None", Status = "approved",
                    };
                case "CAT-GEO":
                    var geo = obj is Column gc ? ReadinessRuleSet.GeoCategory(gc.Name) : null;
                    if (geo == null) return null;
                    return new ChangeItem
                    {
                        ObjectRef = f.ObjectRef, ObjectName = f.ObjectName, Kind = "set_data_category", Source = "deterministic",
                        Category = "Formatting", Severity = f.Severity, Risk = "safe", RuleId = f.RuleId, Target = "dataCategory",
                        Title = "Set geographic data category", Rationale = f.Message, Before = "(none)", After = geo, Status = "approved",
                    };
                default:
                    return null;
            }
        }

        private static ChangeItem MapBpaFix(Model m, System.Collections.Generic.List<BpaRule> rules, BpaViolation v)
        {
            var rule = rules.FirstOrDefault(r => string.Equals(r.ID, v.RuleId, StringComparison.OrdinalIgnoreCase));
            var obj = ObjectRefs.Resolve(m, v.ObjectRef);
            if (rule == null || obj == null) return null;
            var (prop, beforeVal, afterVal) = BpaAnalyzer.PreviewFix(rule, obj);
            if (prop == null) return null;
            return new ChangeItem
            {
                ObjectRef = v.ObjectRef, ObjectName = v.ObjectName, Kind = "bpa_fix", Source = "bpa",
                Category = v.Category, Severity = SeverityName(v.Severity), Risk = "safe", RuleId = v.RuleId,
                Target = prop.ToLowerInvariant(), Title = rule.Name, Rationale = v.Message,
                Before = beforeVal, After = afterVal, Status = "approved",
            };
        }

        private static ChangeItem MapAiContent(Model m, ReadinessFinding f)
        {
            var g = BuildGrounding(m, f.ObjectRef);
            switch (f.RuleId)
            {
                case "DESC-MEASURE":
                case "DESC-TABLE":
                case "DESC-COLUMN":
                case "DESC-ECHO":
                case "DESC-LONG":
                    return AiItem(f, g, "set_description", "description", "ai", "Write a business description", g?.ExistingDescription);
                case "DAC-CALC-GROUP":
                    // Fix is a description on the calculation-group table so its items are documented for AI.
                    return AiItem(f, g, "set_description", "description", "ai", "Describe the calculation group (its items are invisible to AI unless documented)", g?.ExistingDescription);
                case "DESC-CALCGROUP-ITEMS":
                    // The group is documented but the description omits some item names — extend it to list every item.
                    return AiItem(f, g, "set_description", "description", "ai", "List every calculation item in the group's description", g?.ExistingDescription);
                case "DAC-AI-INSTRUCTIONS":
                    // Model-level, no instructions yet: Claude authors them; applied via set_ai_instructions. Before is "(none)".
                    return AiItem(f, g, "set_ai_instructions", "aiInstructions", "ai", "Author Prep-for-AI instructions for Copilot/data agents", null);
                case "DAC-AI-INSTRUCTIONS-LEN":
                {
                    // Model-level, instructions EXIST but exceed the 10k cap — the honest action is "condense", not "author",
                    // and the diff must NOT show Before="(none)" (apply OVERWRITES real instructions). Mirror ReadItemContext's
                    // truncated preview so the seeded item and the add_plan_item path agree on Before.
                    var cur = ReadCustomInstructions(FindLinguisticCulture(m, null)?.Content) ?? "";
                    var beforeLen = cur.Length > 80 ? cur.Substring(0, 80) + "…" : cur;
                    return AiItem(f, g, "set_ai_instructions", "aiInstructions", "ai", "Condense Prep-for-AI instructions to ≤10,000 chars", beforeLen);
                }
                case "DAC-GLOSSARY-GAP":
                {
                    // Model-level, instructions EXIST but omit some field-name codes — EXTEND the glossary. Same
                    // set_ai_instructions target as above (so the plan carries one "edit the instructions" item, not three).
                    var curG = ReadCustomInstructions(FindLinguisticCulture(m, null)?.Content) ?? "";
                    var beforeG = curG.Length > 80 ? curG.Substring(0, 80) + "…" : curG;
                    return AiItem(f, g, "set_ai_instructions", "aiInstructions", "ai", "Add the undefined field-name codes to the AI-instructions glossary", beforeG);
                }
                case "FMT-MEASURE":
                    return AiItem(f, g, "set_measure_format", "formatString", "ai", "Choose a format string", g?.FormatString);
                case "NAME-MEASURE":
                case "NAME-COLUMN":
                case "NAME-TABLE":
                    return AiItem(f, g, "rename", "name", "rename", "Rename to a clear, human name", f.ObjectName);
                case "SYN-FIELD":
                    return AiItem(f, g, "set_synonyms", "synonyms", "ai", "Add natural-language synonyms", "(none)");
                case "SYN-SCHEMA":
                    // Enabling the linguistic schema is deterministic (no content needed) — auto-approve.
                    return new ChangeItem
                    {
                        ObjectRef = f.ObjectRef, ObjectName = f.ObjectName, Kind = "enable_qna", Source = "deterministic",
                        Category = "Synonyms", Severity = f.Severity, Risk = "safe", RuleId = f.RuleId, Target = "linguisticSchema",
                        Title = "Enable Q&A linguistic schema", Rationale = f.Message, Before = "(none)", After = "Seed linguistic schema", Status = "approved",
                    };
                default:
                    return null;
            }
        }

        private static ChangeItem AiItem(ReadinessFinding f, GroundingBundle g, string kind, string target, string risk, string title, string before)
        {
            return new ChangeItem
            {
                ObjectRef = f.ObjectRef, ObjectName = f.ObjectName, Kind = kind, Source = "ai",
                Category = f.Category, Severity = f.Severity, Risk = risk, RuleId = f.RuleId, Target = target,
                Title = title, Rationale = f.Message, Before = string.IsNullOrEmpty(before) ? "(none)" : before,
                After = null, Status = "needs_content", Grounding = g,
            };
        }

        // ---- apply one item (runs inside the MutateAsync batch on the dispatcher thread) -----------

        private void ApplyOneItem(Model m, ChangeItem it)
        {
            var obj = ObjectRefs.Resolve(m, it.ObjectRef);
            switch ((it.Kind ?? "").Trim())
            {
                case "set_description":
                    switch (obj)
                    {
                        case Measure me: me.Description = it.After; break;
                        case Table t: t.Description = it.After; break;
                        case Column c: c.Description = it.After; break;
                        default:
                            // Other describable objects (hierarchy, function, calc item, …) — the same reflection
                            // seam replace_in_object writes through, so a find/replace plan item applies anywhere
                            // search indexed a description.
                            if (obj != null && obj.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.Instance)?.CanWrite == true)
                            { WriteStringProp(obj, "Description", it.After); break; }
                            throw new InvalidOperationException($"set_description: {it.ObjectRef} is not a describable object — fix the ref with set_plan_item or drop the item, then apply_plan again.");
                    }
                    break;
                case "set_display_folder":
                    if (obj == null) throw new InvalidOperationException($"Object not found: {it.ObjectRef} — fix the ref with set_plan_item (list_objects / search_model finds it) or drop the item, then apply_plan again.");
                    WriteStringProp(obj, "DisplayFolder", it.After ?? "");
                    break;
                case "set_format_string":
                    if (obj == null) throw new InvalidOperationException($"Object not found: {it.ObjectRef} — fix the ref with set_plan_item (list_objects / search_model finds it) or drop the item, then apply_plan again.");
                    WriteStringProp(obj, "FormatString", it.After ?? "");
                    break;
                case "set_m":
                    // Partition M or a shared (named) M expression — the ref carries which ("namedexpr:Name" for shared).
                    if ((it.ObjectRef ?? "").StartsWith("namedexpr:", StringComparison.Ordinal))
                    {
                        var exName = it.ObjectRef.Substring("namedexpr:".Length);
                        var ex = m.Expressions.FirstOrDefault(x => string.Equals(x.Name, exName, StringComparison.OrdinalIgnoreCase))
                                 ?? throw new InvalidOperationException($"No shared expression named '{exName}' — fix the ref with set_plan_item or drop the item, then apply_plan again.");
                        ex.Expression = it.After;
                        break;
                    }
                    if (!(obj is Partition mp) || mp.SourceType != PartitionSourceType.M)
                        throw new InvalidOperationException($"set_m: {it.ObjectRef} is not an M partition or shared expression — fix the ref with set_plan_item or drop the item, then apply_plan again.");
                    mp.Expression = it.After;
                    break;
                case "set_measure_format":
                    if (!(obj is Measure mf)) throw new InvalidOperationException($"{it.ObjectRef} is not a measure, but plan item 'set_measure_format' targets one — fix the ref with set_plan_item (list_measures shows measures) or drop the item, then apply_plan again.");
                    mf.FormatString = it.After;
                    break;
                case "set_summarize_by":
                    if (!(obj is Column sc)) throw new InvalidOperationException($"{it.ObjectRef} is not a column, but this plan item ('{it.Kind}') targets one — fix the ref with set_plan_item (list_columns shows columns) or drop the item, then apply_plan again.");
                    sc.SummarizeBy = Enum.Parse<AggregateFunction>(it.After, true);
                    break;
                case "set_column_hidden":
                    if (!(obj is Column hc)) throw new InvalidOperationException($"{it.ObjectRef} is not a column, but this plan item ('{it.Kind}') targets one — fix the ref with set_plan_item (list_columns shows columns) or drop the item, then apply_plan again.");
                    hc.IsHidden = ParseHidden(it.After);
                    break;
                case "set_data_category":
                    if (!(obj is Column dc)) throw new InvalidOperationException($"{it.ObjectRef} is not a column, but this plan item ('{it.Kind}') targets one — fix the ref with set_plan_item (list_columns shows columns) or drop the item, then apply_plan again.");
                    dc.DataCategory = it.After;
                    break;
                case "set_relationship_crossfilter":
                    var rel = m.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r.Name == RelName(it.ObjectRef))
                              ?? throw new InvalidOperationException($"Relationship not found: {it.ObjectRef} — run get_model_summary to see relationship names; fix the ref with set_plan_item or drop the item, then apply_plan again.");
                    rel.CrossFilteringBehavior = Enum.Parse<CrossFilteringBehavior>(it.After, true);
                    break;
                case "mark_date_table":
                    if (!(obj is Table dt)) throw new InvalidOperationException($"{it.ObjectRef} is not a table, but plan item 'mark_date_table' targets one — fix the ref with set_plan_item (list_objects shows tables) or drop the item, then apply_plan again.");
                    dt.DataCategory = "Time";
                    break;
                case "rename":
                    if (!(obj is TabularNamedObject rn)) throw new InvalidOperationException($"{it.ObjectRef} is not renameable (rename targets a named object) — fix the ref with set_plan_item or drop the item, then apply_plan again.");
                    rn.Name = it.After;   // AutoFixup rewrites DAX references
                    break;
                case "set_dax":
                    switch (obj)
                    {
                        case Measure me2: me2.Expression = it.After; break;
                        case CalculatedColumn cc: cc.Expression = it.After; break;
                        case CalculatedTable ct: ct.Expression = it.After; break;
                        case CalculationItem ci2: ci2.Expression = it.After; break;   // calc-item DAX is searched/replaced too
                        case Function fn: fn.Expression = it.After; break;
                        default: throw new InvalidOperationException($"set_dax: {it.ObjectRef} has no DAX expression (needs a measure, calculated column/table, calculation item or function) — fix the ref with set_plan_item or drop the item, then apply_plan again.");
                    }
                    break;
                case "set_synonyms":
                    if (!(obj is TabularNamedObject so)) throw new InvalidOperationException($"{it.ObjectRef} not found — fix the ref with set_plan_item (list_objects / search_model finds it) or drop the item, then apply_plan again.");
                    var cult = EnsureLinguisticCulture(m, null, out _);
                    TabularEditor.TOMWrapper.Linguistics.SynonymHelper.SetSynonyms(so, cult, it.After ?? "");
                    break;
                case "enable_qna":
                    EnsureLinguisticCulture(m, null, out _);
                    break;
                case "set_ai_instructions":
                    // Model-level; ignores obj. Shares the direct writer's no-op-before-seed + read-back guarantees.
                    ApplyAiInstructions(m, null, it.After ?? "");
                    break;
                case "set_ai_data_schema":
                    var (dsTable, dsProp) = ResolveSchemaTarget(m, it.ObjectRef);
                    ApplyAiDataSchema(m, null, dsTable, dsProp, ParseIncluded(it.After));
                    break;
                case "bpa_fix":
                    var rule = GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, it.RuleId, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidOperationException($"Unknown BPA rule '{it.RuleId}' — run bpa_scan to see the rule ids in force (load_bpa_rules to add a rule set); fix the item with set_plan_item or drop it, then apply_plan again.");
                    if (!(obj is ITabularNamedObject no)) throw new InvalidOperationException($"Object not found: {it.ObjectRef} — fix the ref with set_plan_item (list_objects / search_model finds it) or drop the item, then apply_plan again.");
                    BpaAnalyzer.ApplyFix(m, rule, no);
                    break;
                case "delete":
                    DeleteResolvedObject(m, obj, it.ObjectRef);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown plan item kind '{it.Kind}' — see get_op_catalog for valid item kinds; fix it with set_plan_item or drop the item, then apply_plan again.");
            }
        }

        // ---- plan helpers --------------------------------------------------------------------------

        /// <summary>The DAX body of a measure / calculated column / calculated table / function, or null.</summary>
        private static string CurrentDax(ITabularObject obj) => obj switch
        {
            Measure me => me.Expression,
            CalculatedColumn cc => cc.Expression,
            CalculatedTable ct => ct.Expression,
            Function fn => fn.Expression,
            _ => null,
        };

        private static (string name, string before) ReadItemContext(Model m, string objRef, string kind)
        {
            kind = (kind ?? "").Trim();
            // set_ai_instructions is MODEL-level (no object ref to resolve) — handle it before object resolution.
            if (kind == "set_ai_instructions")
            {
                var cur = ReadCustomInstructions(FindLinguisticCulture(m, null)?.Content);
                return (m.Name, string.IsNullOrEmpty(cur) ? "(none)" : (cur.Length > 80 ? cur.Substring(0, 80) + "…" : cur));
            }
            var obj = ObjectRefs.Resolve(m, objRef);
            if (obj == null) throw new InvalidOperationException($"Object not found: {objRef} — run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
            string before;
            switch (kind)
            {
                case "set_dax": before = CurrentDax(obj); break;
                case "set_description": before = (obj as Measure)?.Description ?? (obj as Table)?.Description ?? (obj as Column)?.Description; break;
                case "set_measure_format": before = (obj as Measure)?.FormatString; break;
                case "set_summarize_by": before = (obj as Column)?.SummarizeBy.ToString(); break;
                case "set_data_category": before = (obj as Column)?.DataCategory; break;
                case "set_column_hidden": before = (obj as Column)?.IsHidden == true ? "Hidden" : "Visible"; break;
                case "set_ai_data_schema":
                    var (t, p) = ResolveSchemaTarget(m, objRef);
                    before = ReadEntityExcluded(FindLinguisticCulture(m, null)?.Content, t, p) ? "Excluded" : "Included";
                    break;
                case "rename": before = obj.Name; break;
                case "delete": before = $"{obj.GetType().Name}: {obj.Name}"; break;
                default: before = null; break;
            }
            return (obj.Name, string.IsNullOrEmpty(before) ? "(none)" : before);
        }

        private ChangePlanView PublishPlan(ChangePlanState plan, long revision)
        {
            var view = BuildView(plan, revision);
            _sessions.Bus.PublishPlan(view);
            return view;
        }

        private static ChangePlanView BuildView(ChangePlanState plan, long revision)
        {
            if (plan == null) return new ChangePlanView { Items = Array.Empty<ChangeItem>(), Summary = new PlanSummary(), Revision = revision };
            // Clone every item so the returned/broadcast view is an immutable point-in-time snapshot —
            // a later in-place field mutation (from the other door) can't tear a serialized view.
            var items = plan.Snapshot()
                .Select(i => i.Clone())
                .OrderBy(i => SourceRank(i.Source))
                .ThenBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var summary = new PlanSummary
            {
                Total = items.Length,
                Deterministic = items.Count(i => i.Source == "deterministic"),
                Bpa = items.Count(i => i.Source == "bpa"),
                Ai = items.Count(i => i.Source == "ai"),
                Renames = items.Count(i => string.Equals(i.Kind, "rename", StringComparison.OrdinalIgnoreCase)),
                NeedsContent = items.Count(i => i.Status == "needs_content"),
                Approved = items.Count(i => i.Status == "approved"),
                Rejected = items.Count(i => i.Status == "rejected"),
                Applied = items.Count(i => i.Status == "applied"),
                Unverified = items.Count(i => i.VerifyState == "unverified" || i.VerifyState == "failed"),
            };
            return new ChangePlanView { PlanId = plan.PlanId, Scope = plan.Scope, Revision = revision, Items = items, Summary = summary, Note = plan.Note };
        }

        private static int SourceRank(string source) => source == "deterministic" ? 0 : source == "bpa" ? 1 : 2;

        /// <summary>Apply order within a batch: edits, child renames, table renames, then removals. A removal cannot
        /// invalidate a ref needed by an earlier item, and a table rename never invalidates a queued child rename.</summary>
        private static int ApplyOrder(ChangeItem i)
        {
            if (string.Equals(i.Kind, "delete", StringComparison.OrdinalIgnoreCase)) return 3;
            if (!string.Equals(i.Kind, "rename", StringComparison.OrdinalIgnoreCase)) return 0;
            return (i.ObjectRef ?? "").StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        private static bool InScope(string objRef, string scope)
        {
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrEmpty(objRef)) return true;
            var tn = scope.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? scope.Substring("table:".Length) : scope;
            if (objRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase))
                return string.Equals(objRef.Substring("table:".Length), tn, StringComparison.OrdinalIgnoreCase);
            if (objRef.StartsWith("measure:", StringComparison.OrdinalIgnoreCase) || objRef.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = objRef.Substring(objRef.IndexOf(':') + 1);
                var slash = rest.IndexOf('/');
                var t = slash >= 0 ? rest.Substring(0, slash) : rest;
                return string.Equals(t, tn, StringComparison.OrdinalIgnoreCase);
            }
            return false;   // model-level / relationship items are excluded under a table scope
        }

        /// <summary>The plan kinds a human must EXPLICITLY approve, however the value was authored — ONE
        /// class-driven rule shared by add_plan_item's initial status and set_plan_item's edit path (never an
        /// enumerated special case in one and not the other): a rename (rewrites references), a set_dax without a
        /// NON-EMPTY verify matrix (an empty matrix [] proves only the grand total, not per-context equivalence),
        /// a set_m (a literal M edit that reference fixup cannot reach), and a delete.</summary>
        private static bool OptInKind(string kind, string[] verifyGroupBy) =>
            string.Equals(kind, "rename", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "delete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "set_m", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(kind, "set_dax", StringComparison.OrdinalIgnoreCase)
                && (verifyGroupBy == null || verifyGroupBy.Length == 0));

        private static bool RequiresAfter(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "enable_qna":
                case "mark_date_table":
                case "bpa_fix":
                case "delete":
                    return false;
                default:
                    return true;
            }
        }

        private static bool ParseHidden(string after)
        {
            if (string.IsNullOrEmpty(after)) return true;
            var a = after.Trim().ToLowerInvariant();
            return a == "true" || a == "hidden" || a == "yes" || a == "1";
        }

        private static string RelName(string objRef) =>
            objRef != null && objRef.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase)
                ? objRef.Substring("relationship:".Length) : objRef;

        private static string SeverityName(int sev) => sev >= 3 ? "High" : sev == 2 ? "Medium" : "Info";

        private static string CategoryForKind(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "set_description": return "Descriptions";
                case "rename": return "Naming";
                case "delete": return "Structure";
                case "set_measure_format":
                case "set_format_string":
                case "set_display_folder":
                case "set_summarize_by":
                case "set_data_category": return "Formatting";
                case "set_m": return "M Code";
                case "set_column_hidden": return "Visibility";
                case "set_synonyms":
                case "enable_qna": return "Synonyms";
                case "set_ai_instructions":
                case "set_ai_data_schema": return "DataAgentConfig";
                case "set_relationship_crossfilter": return "Relationships";
                case "set_dax": return "DAX";
                default: return "Other";
            }
        }

        private static string RiskForKind(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "rename": return "rename";
                case "delete": return "structural";
                case "set_m": return "m";   // literal M edit: not reference-fixed, so it wears its own (amber) badge
                case "set_relationship_crossfilter":
                case "mark_date_table": return "structural";
                case "set_dax":
                case "set_description":
                case "set_ai_instructions":
                case "set_synonyms": return "ai";
                default: return "safe";
            }
        }

        private static string TargetForKind(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "set_description": return "description";
                case "set_measure_format": return "formatString";
                case "set_format_string": return "formatString";
                case "set_display_folder": return "displayFolder";
                case "set_m": return "mExpression";
                case "set_summarize_by": return "summarizeBy";
                case "set_column_hidden": return "isHidden";
                case "set_data_category": return "dataCategory";
                case "rename": return "name";
                case "delete": return "object";
                case "set_synonyms": return "synonyms";
                case "set_relationship_crossfilter": return "crossFilter";
                case "set_dax": return "expression";
                case "mark_date_table": return "dateTable";
                case "enable_qna": return "linguisticSchema";
                case "set_ai_instructions": return "aiInstructions";
                case "set_ai_data_schema": return "aiDataSchema";
                default: return kind;
            }
        }

        private static string DefaultTitle(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "set_description": return "Set description";
                case "set_measure_format": return "Set format string";
                case "set_format_string": return "Set format string";
                case "set_display_folder": return "Set display folder";
                case "set_m": return "Update M expression";
                case "set_summarize_by": return "Set default aggregation";
                case "set_column_hidden": return "Hide column";
                case "set_data_category": return "Set data category";
                case "rename": return "Rename";
                case "delete": return "Remove object";
                case "set_synonyms": return "Set synonyms";
                case "set_relationship_crossfilter": return "Set cross-filter direction";
                case "set_dax": return "Update DAX";
                case "mark_date_table": return "Mark as date table";
                case "enable_qna": return "Enable Q&A schema";
                case "set_ai_instructions": return "Author AI instructions";
                case "set_ai_data_schema": return "Toggle AI data-schema membership";
                default: return kind;
            }
        }

        private static SaveFormat ParseFormat(string format)
        {
            if (string.IsNullOrEmpty(format)) return SaveFormat.TMDL;
            switch (format.Trim().ToUpperInvariant())
            {
                case "TMDL": return SaveFormat.TMDL;
                case "BIM":
                case "MODELSCHEMAONLY": return SaveFormat.ModelSchemaOnly;
                case "FOLDER":
                case "TABULAREDITORFOLDER": return SaveFormat.TabularEditorFolder;
                default: return SaveFormat.TMDL;
            }
        }
    }
}
