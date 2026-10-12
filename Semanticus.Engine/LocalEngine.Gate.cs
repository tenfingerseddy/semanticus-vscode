using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Evidence;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using RawTom = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    // The engine's half of the assistant's gate (docs/redesign: m11/uat-undo). AssistantEngine is the door; this file is what the
    // door asks of the engine (the witness an approval is bound to, the words that need the model) and the SINKS: the few places a
    // write actually lands, each of which refuses an assistant's write that no ticket covers while the policy asks or denies.
    public sealed partial class LocalEngine
    {
        /// <summary>The key a ticket keeps the open model's own files under.</summary>
        internal const string ModelFilesKey = "<model files>";

        private static bool IsAssistantOrigin(string origin) => string.Equals(origin, "agent", StringComparison.Ordinal);

        // ---- what an approval is asked at ---------------------------------------------------------------------------

        internal async Task<GateWitnessData> GateWitnessAsync(GateEntry entry, GateCall call)
        {
            var s = _sessions.Current;
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var (key, state) = PathWitness.Of(path);
                paths[key] = state;
            }
            foreach (var name in entry.PathArgs) Add(call.Arg<string>(name));
            if (entry.PathsOf != null) foreach (var p in entry.PathsOf(call) ?? Array.Empty<string>()) Add(p);
            if (entry.Witness.HasFlag(GateWitness.ModelFiles) && !string.IsNullOrWhiteSpace(s?.SourcePath))
                paths[ModelFilesKey] = PathWitness.Of(s.SourcePath).State;
            return new GateWitnessData
            {
                SessionId = s?.Id ?? "",
                ModelIdentity = s?.SourcePath ?? "",
                Revision = s != null && entry.Witness.HasFlag(GateWitness.Revision) ? s.Revision : -1,
                Paths = paths,
                Basis = entry.Basis == null ? "" : await entry.Basis(call).ConfigureAwait(false) ?? "",
            };
        }

        /// <summary>The open model's name as the person knows it, for the questions an assistant's held write asks; null when none is open. Read off the
        /// model's own name, which only a rename changes, so the read needs no turn on the dispatcher.</summary>
        internal string GateModelName()
        {
            try
            {
                var m = _sessions.Current?.Model;
                var name = string.IsNullOrWhiteSpace(m?.Database?.Name) ? m?.Name : m.Database.Name;
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch { return null; }
        }

        internal Task<bool> GateHasUnsavedAsync() => Task.FromResult(_sessions.Current?.HasUnsavedEdits == true);

        // ---- the sinks -----------------------------------------------------------------------------------------------

        /// <summary>False when an assistant's write of this kind needs no answer from the person (the policy allows it, or the caller is
        /// not an assistant), so a sink can skip the work of reading the state of what it would write.</summary>
        private bool AssistantMayBeAsked(AgentCapability cap, string origin) =>
            IsAssistantOrigin(origin) && AgentPolicyGuard.Decide(cap, "local", "agent", isCommit: true, AgentPolicyStore.Get()).Outcome != GateOutcome.Allow;

        /// <summary>A sink that knows its whole request asks for itself when no ticket from the assistant's door covers it, in
        /// words it can say and bound to the model, its revision and what the write touches. Null when the write may go on. The
        /// door is the way through for an assistant; this is what makes a road around the door fail closed instead of open.
        /// <paramref name="ambient"/> is for a sink with no origin of its own: it asks when an assistant's call is behind it.</summary>
        private string HoldAtSink(AgentCapability cap, string origin, string sink, string what, string basis,
            Func<AssistantTicket, string> verifyTicket = null, bool ambient = false)
        {
            if (!IsAssistantOrigin(origin) && !(ambient && GateContext.InAssistantCall)) return null;
            if (GateSeams.Off(sink)) return null;
            var decision = AgentPolicyGuard.Decide(cap, "local", "agent", isCommit: true, AgentPolicyStore.Get());
            if (decision.Outcome == GateOutcome.Allow) return null;
            if (decision.Outcome == GateOutcome.Deny) return decision.Reason;
            var s = _sessions.Current;
            var ticket = GateContext.Ticket;
            if (ticket != null && ticket.Covers(cap, s?.Id)) return verifyTicket?.Invoke(ticket);
            var intent = ApprovalLedger.IntentHash("sink", cap.ToString(), sink, s?.Id ?? "", s?.SourcePath ?? "",
                s == null ? "" : s.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), basis ?? "");
            try { if (ApprovalLedger.TryConsume(cap, "local", intent)) return null; }
            catch { /* fail closed: refuse below */ }
            var model = GateModelName();
            try { ApprovalLedger.Request(cap, "local", intent, GateWords.WithModel(what, model), model ?? "the open model"); }
            catch { /* queueing is best-effort; the refusal still stands */ }
            return decision.Reason;
        }

        /// <summary>THE chokepoint for a write that lands outside the open model: a note, a workflow or its settings, a test, a connection or
        /// engine setting, a working copy, a repository. Each such writer calls it first, by the name of the method it is, so the row the
        /// registry holds for that method decides what it needs. It refuses an assistant's write (an <c>agent</c> origin, or a call made
        /// inside the assistant's door) that no approval covers while the person's policy asks or denies, and it refuses it outright inside
        /// a rehearsal, where nothing outside the model is rolled back. The door gets an approval first; this is what makes a road around
        /// the door (any caller that reaches the engine directly) fail closed instead of open. A person's write is never asked.</summary>
        internal void GuardOutsideWrite(string origin, [CallerMemberName] string method = null)
        {
            if (!IsAssistantOrigin(origin) && !GateContext.InAssistantCall) return;
            if (DryRunScope.Current?.FromTool == true) throw new InvalidOperationException(GateRegistry.CannotPreviewSentence);
            if (GateSeams.Off("outside")) return;
            var entry = GateRegistry.Find(method);
            // A method with no row is a gap: it is treated as the most ordinary file write, so it still asks under Locked.
            // A row decided by the call (Dyn) is a stub here: the capability the person approved is the one on the ticket this very call carries.
            var carried = GateContext.Ticket;
            var cap = carried != null && string.Equals(carried.Method, method, StringComparison.Ordinal) && carried.Capabilities.Length > 0
                ? carried.Capabilities[0] : entry?.Capability ?? AgentCapability.DeployFile;
            var words = "Your assistant wants to make this change outside the model: " + GateWords.Humanize(method) + ".";
            var held = HoldAtSink(cap, "agent", "outside:" + method, words, method, ambient: true);
            if (held != null) throw new InvalidOperationException(held);
        }

        /// <summary>The commit sink's refusal (<see cref="Session"/>): an assistant's edit no ticket covers. The sink holds only the
        /// label, so the request it registers is for the person to see and is never one an approval can be spent on; the assistant's
        /// door is the way through, and it asks in words made from the request.</summary>
        private string RefuseCommitWithoutTicket(string origin, string label)
        {
            var decision = AgentPolicyGuard.Decide(AgentCapability.EditLocal, "local", origin, isCommit: true, AgentPolicyStore.Get());
            if (decision.Outcome == GateOutcome.Deny) return decision.Reason;
            var what = label == "undo" ? "undo the newest change to the open model"
                     : label == "redo" ? "redo the change that was taken back in the open model"
                     : "make this change to the open model: " + label;
            var s = _sessions.Current;
            // No approval is ever spent on this intent: it is unique to the call, so the retry through the door asks its own question.
            var intent = ApprovalLedger.IntentHash("commit-refused", s?.Id ?? "", label ?? "", s == null ? "" : s.Revision.ToString(), Guid.NewGuid().ToString("N"));
            var model = GateModelName();
            try { ApprovalLedger.Request(AgentCapability.EditLocal, "local", intent, GateWords.Question(what, model), model ?? "the open model"); }
            catch { /* queueing is best-effort; the refusal still stands */ }
            return decision.Reason;
        }

        /// <summary>The model's own files are about to be written or replaced on behalf of <paramref name="origin"/>: an assistant's
        /// write is held under Locked (the "Write model files" row), a person's and the engine's never are. The one door every path
        /// that touches the model's files takes (save, git commit and checkpoint when they save first, restore, checkout, pull), so
        /// no inner caller can hand an assistant's write on as the person's.</summary>
        private string HoldFileWrite(string origin, string what, string intentBasis = null)
        {
            if (!AssistantMayBeAsked(AgentCapability.DeployFile, origin)) return null;
            var source = _sessions.Current?.SourcePath;
            var state = string.IsNullOrWhiteSpace(source) ? "" : PathWitness.Of(source).State;
            return HoldAtSink(AgentCapability.DeployFile, origin, "model-files", what, (intentBasis ?? "") + "|" + state, t =>
            {
                // Approved at the door: still the files the person was asked about? One write stands on the approval; the checks
                // after it in the same call (the call's own writes changed the files) do not bind again.
                if (t.ModelFilesState == null) return null;
                if (!string.Equals(t.ModelFilesState, state, StringComparison.Ordinal))
                    return "The model's files changed after you approved this, so nothing was written. Ask again.";
                t.ModelFilesState = null;
                return null;
            });
        }

        /// <summary>An assistant is about to write a file it named. EVERY such write asks under Locked, a new path as much as an
        /// existing one: the Locked card says "even local writes ask first". Whether the path is the open model's own file, or sits in
        /// the folder the model is read from (a link anywhere in the path followed), only shapes the words. The approval is for the
        /// path as it really is (<see cref="PathWitness"/>): a link put in a parent folder, a file that appeared, or a file that changed
        /// since the question is a new question. A person's and the engine's own writes are never asked.</summary>
        internal string HoldPathWrite(string origin, string path, string what) => HoldPathWrite(origin, path, what, out _, out _);

        internal string HoldPathWrite(string origin, string path, string what, out bool createNew) => HoldPathWrite(origin, path, what, out createNew, out _);

        /// <summary><paramref name="createNew"/> is true when an assistant's write is to a file the person approved as NOT being there:
        /// the writer then makes it with CreateNew, so a file that appears in the instant between this check and the write is never
        /// replaced. <paramref name="resolved"/> is the fully resolved destination the person was asked about (every link followed, a parent's and a link
        /// target's parent's as much as the last part's), or null when the caller is not asked. The writer writes THERE, not to the text it
        /// was given, so a link moved between this check and the write cannot send the write to another file.</summary>
        internal string HoldPathWrite(string origin, string path, string what, out bool createNew, out string resolved)
        {
            createNew = false;
            resolved = null;
            if (string.IsNullOrWhiteSpace(path) || !AssistantMayBeAsked(AgentCapability.DeployFile, origin)) return null;
            var (key, state) = PathWitness.Of(path);
            // A path whose links cannot be followed to the end (one that never ends, one that cannot be read, one that points at nothing) is
            // not a file the person could be shown, so it is refused rather than asked about. It must never read as "a new file".
            if (PathWitness.IsUnresolved(state))
                return "That path goes through a link that cannot be followed to a real file, so nothing was written. Use a path without it.";
            resolved = key;
            createNew = state == PathWitness.Absent;
            var words = IsModelFilePath(key)
                ? "Your assistant wants to write over a file your open model is read from: " + key + " (" + what + ")."
                : state == PathWitness.Absent
                    ? "Your assistant wants to create a new file: " + key + " (" + what + ")."
                    : "Your assistant wants to replace a file that already exists: " + key + " (" + what + ").";
            return HoldAtSink(AgentCapability.DeployFile, origin, "path", words, key + "@" + state, t =>
                t.Paths.TryGetValue(key, out var granted) && string.Equals(granted, state, StringComparison.Ordinal) ? null
                : "That file is not the one you approved, or it changed since you did, so nothing was written. Ask again.");
        }

        /// <summary>True when <paramref name="realPath"/> (links already followed) is the open model's own file or sits inside the
        /// folder it is read from (a TMDL root, a PBIP's definition folder and the semantic model folder around it).</summary>
        private bool IsModelFilePath(string realPath)
        {
            var source = _sessions.Current?.SourcePath;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(realPath)) return false;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string src;
            try { src = PathWitness.Real(Path.GetFullPath(source)); } catch { return false; }
            if (src == null) return false;   // a link that cannot be followed: nothing here can be said to be the model's own
            if (string.Equals(realPath, src, cmp)) return true;
            if (!Directory.Exists(src)) return false;   // a .bim file is the model; its neighbours are not
            var roots = new List<string> { src };
            if (string.Equals(Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar)), "definition", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(src.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent)) roots.Add(parent);
            }
            return roots.Any(r => realPath.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmp));
        }

        /// <summary>WHAT AN OPEN WOULD LOSE, defined once (Sol 6.1, join 6 area B round 3). An open or a new model replaces the whole session, so
        /// everything the swap discards that is not already on disk is work the person loses: the model's unsaved edits (only when the call
        /// says to discard them; without that the open refuses on its own), a spec held in the session, and the plan lines the person decided
        /// on or wrote. Measured, not assumed: the change plan lives in the session's memory and nothing writes it to disk (PlanMemory keeps
        /// only what a person set aside on the model plan, a different list), and nothing records that a spec was saved, a saved copy is not read
        /// back by reopening the model, so a held spec counts. The open rows, <see cref="RefuseUnheldDiscard"/>, <see cref="HoldDiscardUnsaved"/> and
        /// <see cref="RefuseStaleDiscard"/> all read it from here.</summary>
        internal readonly struct OpenLoss
        {
            internal bool UnsavedEdits { get; init; }
            internal bool Spec { get; init; }
            internal int PlanLines { get; init; }
            /// <summary>The held spec as a few words (its name, its tables and measures), and the first plan lines as the person reads them: what the swap
            /// would throw away, said so the person can tell whether it is worth it (UAT-J7-S3-007). Empty when there is none.</summary>
            internal string SpecWords { get; init; }
            internal string PlanLinesText { get; init; }
            /// <summary>A hash of the spec and the plan lines the swap would discard, "" when it discards neither. The model's own edits are bound by the
            /// revision, so they are not in it.</summary>
            internal string MemoryBasis { get; init; }
            internal bool InMemory => Spec || PlanLines > 0;
            internal bool Any => UnsavedEdits || InMemory;

            /// <summary>"your unsaved changes to the open model, your unsaved spec and 3 lines on your plan". It does not say the person approved the lines: the lines an
            /// assistant proposed start approved, so a count of approvals would claim a yes the person never gave.</summary>
            internal string Words()
            {
                var parts = new List<string>();
                if (UnsavedEdits) parts.Add("your unsaved changes to the open model");
                if (Spec) parts.Add("your unsaved spec" + (string.IsNullOrEmpty(SpecWords) ? "" : " (" + SpecWords + ")"));
                if (PlanLines > 0)
                    parts.Add(PlanLines + (PlanLines == 1 ? " line on your plan" : " lines on your plan") + (string.IsNullOrEmpty(PlanLinesText) ? "" : " (" + PlanLinesText + ")"));
                return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
            }

            /// <summary>The sentence the person is asked with, after the words that say what is being opened.</summary>
            internal string Question() => "That throws away " + Words() + ". They will be lost";
        }

        /// <summary>The plan lines a swap would lose: approved, rejected, kept or authored. A proposal seeds most lines as approved (RestoredStatus), so these are
        /// lines on the person's plan, not lines the person approved; only a plan whose lines are all still proposed or waiting for content loses nothing.</summary>
        private static ChangeItem[] PersonPlanLines(ChangePlanState plan) =>
            (plan?.Snapshot() ?? Array.Empty<ChangeItem>())
                .Where(l => l.KeptByPerson || l.AuthoredAfter != null || string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase) || string.Equals(l.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        /// <summary>What replacing <paramref name="context"/> would lose. The caller holds the spec and plan gates when it needs the answer to be exact.</summary>
        internal OpenLoss OpenLossOf(SessionContext context, bool discardUnsaved)
        {
            var spec = context?.Spec.Current;
            var lines = PersonPlanLines(context?.Plans.Current);
            return new OpenLoss
            {
                UnsavedEdits = discardUnsaved && context?.Session?.HasUnsavedEdits == true,
                Spec = spec != null,
                PlanLines = lines.Length,
                SpecWords = spec == null ? "" : SpecSummary(spec),
                PlanLinesText = PlanLinesLost(lines),
                MemoryBasis = spec == null && lines.Length == 0 ? "" : GateHash(SpecBasisOf(spec), lines.Length == 0 ? "no plan" : PlanLinesHash(lines)),
            };
        }

        /// <summary>The same, read under the spec's own gate, for the open rows.</summary>
        internal async Task<OpenLoss> GateOpenLossAsync(bool discardUnsaved)
        {
            var context = _sessions.CurrentContext;
            if (context == null) return default;
            await context.SpecGate.WaitAsync().ConfigureAwait(false);
            try { return OpenLossOf(context, discardUnsaved); }
            finally { context.SpecGate.Release(); }
        }

        /// <summary>What an approval of an open is bound to besides the model's revision: the spec and the plan lines it would throw away.</summary>
        internal async Task<string> GateBasisOpenLossAsync(GateCall call) =>
            (await GateOpenLossAsync(call.Arg<bool>("discardUnsaved")).ConfigureAwait(false)).MemoryBasis;

        /// <summary>An assistant is about to open or create a model with discardUnsaved: the person's unsaved edits to the open model
        /// would be thrown away. Held like any other assistant write to the open model (the "Edit the open model" row), the sentence
        /// says plainly that the changes will be lost, and the approval is bound to the model's revision, so work the person did after
        /// the question is not thrown away by a yes given before it. Null when nothing is lost or the person acts.</summary>
        internal string HoldDiscardUnsaved(string origin, string action)
        {
            var loss = OpenLossOf(_sessions.CurrentContext, discardUnsaved: true);
            return HoldAtSink(AgentCapability.EditLocal, origin, "discard",
                "Your assistant wants to " + action + ". " + (loss.Any ? loss.Question() : "That throws away your unsaved changes to the open model. They will be lost") + ".",
                "discard|" + action + "|" + (loss.UnsavedEdits ? "unsaved" : "clean") + "|" + loss.MemoryBasis);
        }

        /// <summary>The open that replaces the model, for a caller with no origin of its own: when an assistant's call is behind it and
        /// would throw away work (<see cref="OpenLoss"/>), no ticket and a policy that asks means no open.</summary>
        private void RefuseUnheldDiscard(bool discardUnsaved, string action)
        {
            if (!GateContext.InAssistantCall) return;
            var loss = OpenLossOf(_sessions.CurrentContext, discardUnsaved);
            GateContext.NoteOpenEntry(loss.MemoryBasis);   // what the open saw at stake when it started: the swap compares it (RefuseMovedMemory), even when nothing was asked
            if (!loss.Any) return;
            var held = HoldAtSink(AgentCapability.EditLocal, "human", "discard",
                "Your assistant wants to " + action + ". " + loss.Question() + ".",
                "discard|" + action + "|" + (loss.UnsavedEdits ? "unsaved" : "clean") + "|" + loss.MemoryBasis, ambient: true,
                verifyTicket: t => StaleOpenApproval(t, loss));
            if (held != null) throw new InvalidOperationException(held);
        }

        /// <summary>The words the person was asked about no longer describe what the open would throw away (a spec or a plan line came or
        /// changed after the question), or null when they do. Only an approval for an open is held to it.</summary>
        private static string StaleOpenApproval(AssistantTicket approved, OpenLoss now)
        {
            if (approved?.ContentBasis == null || Array.IndexOf(SessionReplacingMethods, approved.Method) < 0 || GateSeams.Off("discard-swap")) return null;
            return string.Equals(approved.ContentBasis, now.MemoryBasis, StringComparison.Ordinal) ? null
                : "Your spec or plan lines changed after you approved throwing them away, so nothing was opened and they are as you left them. Ask again.";
        }

        // ---- state the model's revision does not move with: the spec, the plan, a source file, a note ---------------------------------
        //
        // THE CLASS (Sol 6.1 on the M11 delta, items 3 to 7). An approval binds the model's revision and the call's arguments. Some writes
        // also read something that is neither: the spec in the session's memory, a file a copy is taken from, the note an edit replaces.
        // Each such row declares that input (GateEntry.OutsideInputs) and a Basis, a hash of exactly what the write will use. The door binds
        // the grant to it, and the engine reads it again under the store's own lock, right before the write, and refuses on a mismatch.

        /// <summary>A short, stable hash of the parts, for a witness: it names what a write will use without carrying it.</summary>
        internal static string GateHash(params string[] parts)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\u001f", parts.Select(x => x ?? "\u2205"))));
            return Convert.ToHexString(bytes).Substring(0, 32).ToLowerInvariant();
        }

        /// <summary>The check under the store's lock. When an assistant's door approved THIS call bound to the state of something outside the
        /// model, that state must still be what the person was asked about. Does nothing for a call that carries no such approval (a
        /// person's, one below Locked, or a road that asked at the sink, which bound it into its own question).</summary>
        private static void RequireApprovedBasis(string method, string current, string what) =>
            RequireApprovedBasis(GateContext.Ticket, method, current, what);

        private static void RequireApprovedBasis(AssistantTicket ticket, string method, string current, string what)
        {
            if (ticket?.ContentBasis == null || !string.Equals(ticket.Method, method, StringComparison.Ordinal)) return;
            if (!string.Equals(ticket.ContentBasis, current, StringComparison.Ordinal))
                throw new InvalidOperationException("The " + what + " changed after you approved this, so nothing was changed. Ask again.");
        }

        /// <summary>The sink for a write that clears or replaces something the person may be holding in the session or beside the model (the
        /// spec, the plan): a road around the door (an <c>agent</c> origin with no ticket) asks here, bound to what would be lost. The door's
        /// own ticket covers it, and the content is checked again under the store's lock by the op itself.</summary>
        private void GuardContentWrite(string origin, string method, Func<string> basis)
        {
            if (!IsAssistantOrigin(origin) && !GateContext.InAssistantCall) return;
            if (DryRunScope.Current?.FromTool == true) throw new InvalidOperationException(GateRegistry.CannotPreviewSentence);
            if (GateSeams.Off("content")) return;
            var cap = GateRegistry.Find(method)?.Capability ?? AgentCapability.EditLocal;
            if (!AssistantMayBeAsked(cap, "agent")) return;
            var held = HoldAtSink(cap, "agent", "content:" + method, "Your assistant wants to " + GateWords.Humanize(method) + ".", GateHash(method, basis()), ambient: true);
            if (held != null) throw new InvalidOperationException(held);
        }

        // ---- the spec

        private static string SpecSummary(ModelSpec spec)
        {
            if (spec == null) return "no spec";
            var tables = (spec.Tables ?? Array.Empty<SpecTable>()).Select(t => t?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var shown = string.Join(", ", tables.Take(GateWords.MostListed)) + (tables.Count > GateWords.MostListed ? ", and " + (tables.Count - GateWords.MostListed) + " more" : "");
            var measures = spec.Measures?.Length ?? 0;
            return (string.IsNullOrWhiteSpace(spec.Name) ? "" : "\"" + spec.Name.Trim() + "\": ") + tables.Count + (tables.Count == 1 ? " table" : " tables") + (tables.Count == 0 ? "" : " (" + shown + ")") + ", " + measures + (measures == 1 ? " measure" : " measures");
        }

        private string SpecBasisOf(ModelSpec spec) => spec == null ? "no spec" : GateHash(System.Text.Json.JsonSerializer.Serialize(spec, SpecJson));

        /// <summary>The spec as it stands, read under the spec's own gate: what a build, a save, a clear or a replace is bound to.</summary>
        internal async Task<string> GateBasisSpecAsync(GateCall call)
        {
            var context = _sessions.CurrentContext;
            if (context == null) return "no model";
            await context.SpecGate.WaitAsync().ConfigureAwait(false);
            try { return SpecBasisOf(context.Spec.Current); }
            finally { context.SpecGate.Release(); }
        }

        internal async Task<string> GateSpecSummaryAsync()
        {
            var context = _sessions.CurrentContext;
            if (context == null) return "no spec";
            await context.SpecGate.WaitAsync().ConfigureAwait(false);
            try { return SpecSummary(context.Spec.Current); }
            finally { context.SpecGate.Release(); }
        }

        /// <summary>What a spec the assistant wants to put in the place of the held one holds, in a few words (or why it cannot be read).</summary>
        internal string GateSpecJsonSummary(string specJson)
        {
            try { return SpecSummary(ParseSpec(specJson, "spec JSON")); }
            catch { return "one that cannot be read as a spec"; }
        }

        /// <summary>For a load: the held spec AND the words of the file the new one is read from, as the load will read them.</summary>
        internal async Task<string> GateBasisLoadSpecAsync(GateCall call)
        {
            var held = await GateBasisSpecAsync(call).ConfigureAwait(false);
            string text = null;
            try { text = File.ReadAllText(Path.GetFullPath(call.Arg<string>("path") ?? "")); } catch { /* an unreadable file reads as none, and the load fails on it anyway */ }
            return GateHash(held, GateHash(text));
        }

        /// <summary>True when the session holds a spec: replacing or clearing it then throws something away.</summary>
        internal async Task<bool> GateSpecHasContentAsync()
        {
            var context = _sessions.CurrentContext;
            if (context == null) return false;
            await context.SpecGate.WaitAsync().ConfigureAwait(false);
            try { return context.Spec.Current != null; }
            finally { context.SpecGate.Release(); }
        }

        // ---- the plan

        /// <summary>Every line of the change plan, with the words and the person's decision on it: what a clear of the plan would throw away.</summary>
        internal static string PlanAllBasis(ChangePlanState plan) => plan == null ? "no plan" : PlanLinesHash(plan.Snapshot());

        private static string PlanLinesHash(IEnumerable<ChangeItem> lines)
        {
            var sb = new StringBuilder();
            foreach (var it in lines.OrderBy(i => i.Id, StringComparer.Ordinal))
                sb.Append(it.Id).Append('|').Append(it.Status).Append('|').Append(it.Kind).Append('|').Append(it.ObjectRef).Append('|').Append(it.Target).Append('|')
                  .Append(it.After).Append('|').Append(it.KeptByPerson ? "kept" : "").Append('|').Append(it.Note).Append('\u001f');
            return GateHash(sb.ToString());
        }

        internal Task<string> GateBasisPlanAll(GateCall call)
        {
            try { return Task.FromResult(PlanAllBasis(_sessions.CurrentContext?.Plans.Current)); }
            catch { return Task.FromResult(""); }
        }

        /// <summary>True when the plan holds a line: clearing it throws that line away.</summary>
        internal bool GatePlanHasLines() => (_sessions.CurrentContext?.Plans.Current?.Snapshot().Length ?? 0) > 0;

        /// <summary>True when the plan holds a line somebody decided on or wrote over (approved, rejected, kept, or authored): a new proposal
        /// would drop it, so it is held. A plan of nothing but the assistant's own untouched proposals loses nothing to a fresh one.</summary>
        internal bool GatePlanHasPersonWork() => PersonPlanLines(_sessions.CurrentContext?.Plans.Current).Length > 0;

        internal string GatePlanSummary()
        {
            var lines = _sessions.CurrentContext?.Plans.Current?.Snapshot() ?? Array.Empty<ChangeItem>();
            var approved = lines.Count(l => string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase));
            var listed = PlanLinesLost(lines);
            return lines.Length + (lines.Length == 1 ? " line" : " lines") + (approved == 0 ? "" : ", " + approved + " approved") + (listed.Length == 0 ? "" : ": " + listed);
        }

        // ---- the start plan's words (the question names the model the plan builds and what the person kept)

        /// <summary>The model the start plan builds: its name, not the model that happens to be open. Null when there is no plan.</summary>
        internal string GateStartPlanModelName() { lock (_startPlanLock) return string.IsNullOrWhiteSpace(_startPlan?.Destination?.Name) ? null : _startPlan.Destination.Name.Trim(); }

        /// <summary>What the person kept on the start plan, in a few words: "3 tables (Sales, DimDate, and 1 more), 7 measures and 2 links".</summary>
        internal string GateStartPlanKept()
        {
            StartPlanDoc doc;
            lock (_startPlanLock) doc = _startPlan == null ? null : StartPlanStore.Copy(_startPlan);
            if (doc == null) return "there is no start plan";
            // The lines the person reviews, from the same view the review is made from (the manifest's tables and links, the time measures), not the stored lines alone.
            var kept = StartPlanViewBuilder.Build(doc).Sections.SelectMany(sec => sec.Groups).SelectMany(g => g.Lines).Where(l => l?.Item != null && l.Kept).ToList();
            var tables = kept.Where(l => l.Item.Kind == "include_table").Select(l => l.Item.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var measures = kept.Count(l => l.Item.Kind == "create_measure");
            var links = kept.Count(l => l.Item.Kind == "create_relationship");
            var other = kept.Count - tables.Count - measures - links;
            var parts = new List<string>();
            if (tables.Count > 0)
                parts.Add(tables.Count + (tables.Count == 1 ? " table" : " tables") + " (" + string.Join(", ", tables.Take(GateWords.MostListed))
                    + (tables.Count > GateWords.MostListed ? ", and " + (tables.Count - GateWords.MostListed) + " more" : "") + ")");
            if (measures > 0) parts.Add(measures + (measures == 1 ? " measure" : " measures"));
            if (links > 0) parts.Add(links + (links == 1 ? " link" : " links"));
            if (other > 0) parts.Add(other + (other == 1 ? " other line" : " other lines"));
            return parts.Count == 0 ? "no lines kept" : parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        }

        internal bool GateStartPlanApproved() { lock (_startPlanLock) return _startPlan?.Approval != null; }

        // ---- notes (the insight store beside the model)

        /// <summary>The note a call names as it reads now, or null: what an edit would replace or a delete would erase, so the question can show it.</summary>
        internal async Task<InsightRecord> GateReadInsightAsync(string id)
        {
            await _knowledgeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var scope in new[] { "project", "global" })
                {
                    var file = ScopeFile(scope);
                    if (file == null) continue;
                    var rec = MaterializeOne(file, scope, id);
                    if (rec != null) return rec;
                }
                return null;
            }
            catch { return null; }
            finally { _knowledgeGate.Release(); }
        }

        /// <summary>A note write's words: the note as it reads now, and for an edit what it would read after. The id alone says nothing to a person.</summary>
        internal async Task<string> GateDescribeNoteAsync(GateCall call, string verb, bool edit)
        {
            var id = call.Arg<string>("id");
            var rec = await GateReadInsightAsync(id).ConfigureAwait(false);
            var now = rec == null ? "the note " + id + " (no such note is kept)" : "the note \"" + GateWords.Cut(rec.Text ?? "") + "\" (" + id + ")";
            return edit ? verb + " " + now + " to: \"" + GateWords.Cut(call.Arg<string>("text") ?? "") + "\"" : verb + " " + now;
        }

        private static string InsightBasis(InsightRecord rec) => rec == null ? "no such note"
            : GateHash(rec.Id, rec.Kind, rec.Status, rec.Text, string.Join("\u001f", rec.Keys ?? Array.Empty<string>()));

        /// <summary>The note a call names, as it reads now (its words, its keys, its status; never its score or its retrieval count, which
        /// move on their own). Read under the knowledge gate.</summary>
        internal async Task<string> GateBasisInsightAsync(GateCall call)
        {
            var id = call.Arg<string>("id");
            await _knowledgeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var scope in new[] { "project", "global" })
                {
                    var file = ScopeFile(scope);
                    if (file == null) continue;
                    var rec = MaterializeOne(file, scope, id);
                    if (rec != null) return InsightBasis(rec);
                }
                return InsightBasis(null);
            }
            catch { return ""; }
            finally { _knowledgeGate.Release(); }
        }

        /// <summary>Every live note in the scope a purge would erase, as they read now.</summary>
        internal async Task<string> GateBasisPurgeAsync(GateCall call)
        {
            await _knowledgeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var scope = NormalizeScope(call.Arg<string>("scope"));
                var file = ScopeFile(scope);
                if (file == null) return "no store";
                var (live, _) = KnowledgeStore.Materialize(file, scope);
                return GateHash(scope, string.Join("\u001e", live.OrderBy(r => r.Id, StringComparer.Ordinal).Select(InsightBasis)));
            }
            catch { return ""; }
            finally { _knowledgeGate.Release(); }
        }

        // ---- a copy from another model (cherry_pick)

        /// <summary>What a cherry_pick will copy, as the source reads now: for a file, where it really is (links followed) and, for each ref, the whole
        /// definition of the object in it. Read from the same load the copy will take its definitions from, so the copy and the witness cannot
        /// disagree. An object the source does not have reads as missing.</summary>
        private static string CherryPickBasisOf(ModelRef source, RawTom.Model src, string[] refs)
        {
            if (string.Equals((source?.Kind ?? "session").Trim(), "session", StringComparison.OrdinalIgnoreCase)) return "session";
            var parts = new List<string> { (source?.Kind ?? "session").Trim().ToLowerInvariant() };
            if (string.Equals(source?.Kind, "file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(source.Path))
                parts.Add(PathWitness.Of(source.Path).Key);
            foreach (var raw in refs ?? Array.Empty<string>())
            {
                var (kind, table, name, _) = ParseChildRef(raw);
                RawTom.MetadataObject obj = null;
                try
                {
                    var t = string.IsNullOrEmpty(table) ? null : src.Tables.Find(table);
                    obj = kind switch
                    {
                        "measure" => (RawTom.MetadataObject)t?.Measures.Find(name),
                        "column" => (RawTom.MetadataObject)t?.Columns.Find(name),
                        "calcitem" => (RawTom.MetadataObject)t?.CalculationGroup?.CalculationItems.Find(name),
                        _ => null,
                    };
                }
                catch { /* an unreadable object reads as missing */ }
                parts.Add(raw + "=" + (obj == null ? "missing" : RawTom.JsonSerializer.SerializeObject(obj)));
            }
            return GateHash(parts.ToArray());
        }

        internal async Task<string> GateBasisCherryPickAsync(GateCall call)
        {
            var source = call.Arg<ModelRef>("source") ?? new ModelRef { Kind = "session" };
            if (string.Equals((source.Kind ?? "session").Trim(), "session", StringComparison.OrdinalIgnoreCase)) return "session";   // the open model: its revision already binds it
            try
            {
                var (sdb, _, clean) = await ResolveModelRefAsync(source, "agent").ConfigureAwait(false);
                try { return CherryPickBasisOf(source, sdb.Model, call.Arg<string[]>("refs")); }
                finally { clean(); }
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- the last rows: stores and sources the revision knows nothing about (join 6, area B, the coordinator's order to close every gap) ----------
        //
        // Same class and same rule as above. Each hook below only READS (it writes and migrates nothing), and the op reads the same thing again under its
        // own lock and compares it with what the door bound.

        internal static string GateFileState(string file)
        {
            try { return file != null && File.Exists(file) ? GateHash("file", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))) : "absent"; }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        /// <summary>Runs a basis check and turns its refusal into the type that a store with a catch-all lets through.</summary>
        private static void Stale(Action check)
        {
            try { check(); }
            catch (StaleApprovalException) { throw; }
            catch (InvalidOperationException ex) { throw new StaleApprovalException(ex.Message); }
        }

        /// <summary>The check for a store file: what it holds now must be what the person was asked about. Handed to a store, which calls it under its own lock.</summary>
        private static Action<string> FileBasisCheck(string method, string what)
        {
            var ticket = GateContext.Ticket;   // taken now: the store may call this on another thread (the dispatcher), where the ambient ticket is not carried
            return file => Stale(() => RequireApprovedBasis(ticket, method, GateFileState(file), what));
        }

        /// <summary>Arms the same check for the first write a machine registry makes inside the scope (see <see cref="RegistryWriteGuard"/>).</summary>
        private static IDisposable GuardRegistry(string method, string what, string file) => RegistryWriteGuard.Around(FileBasisCheck(method, what), file);

        /// <summary>The door's side of every row guarded by a store file: the state of the file the write will replace.</summary>
        internal Task<string> GateBasisStoreFileAsync(string store)
        {
            try
            {
                var s = _sessions.Current;
                var dir = s == null ? null : TestsDirFor(s);
                var file = store switch
                {
                    "connections" => ConnectionRegistry.StatePath(),
                    "sql-sources" => SqlSourceRegistry.StatePath(),
                    "model-sql-endpoints" => ModelSqlEndpointRegistry.StatePath(),
                    "table-sources" => dir == null ? null : TableSourceMappingStore.PathFor(dir),
                    "report-scope" => dir == null ? null : Lineage.ReportScopeStore.PathFor(dir),
                    "layout" => s?.SourcePath == null ? null : Path.Combine(LayoutStore.DirFor(s.SourcePath) ?? "", LayoutStore.FileName),
                    _ => throw new ArgumentException("unknown store " + store),
                };
                return Task.FromResult(file == null ? "no home" : GateFileState(file));
            }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        /// <summary>The workflow canvas file the save would replace.</summary>
        internal Task<string> GateBasisWorkflowLayoutAsync(GateCall call)
        {
            try
            {
                var (def, _, file) = WorkflowLayoutTarget(call.Arg<string>("name"), SidecarDir());
                return Task.FromResult(file == null ? "no home" : GateFileState(file));
            }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        /// <summary>The async twin of <see cref="RequireApprovedBasis"/>, for a state that takes a read to find (git): the read happens only when an approval is there to check.</summary>
        private static async Task RequireApprovedBasisAsync(string method, Func<Task<string>> current, string what)
        {
            var ticket = GateContext.Ticket;
            if (ticket?.ContentBasis == null || !string.Equals(ticket.Method, method, StringComparison.Ordinal)) return;
            RequireApprovedBasis(method, await current().ConfigureAwait(false), what);
        }

        // ---- a draft (run_draft)
        private static string DraftBasis(Draft d) => d == null ? "no draft" : GateHash(ApprovedContentOf(d));

        internal async Task<string> GateBasisDraftAsync(GateCall call)
        {
            try
            {
                var p = BindDrafts();
                await _draftGate.WaitAsync().ConfigureAwait(false);
                try { return DraftBasis(ReadDraft(p, call.Arg<string>("draftId"))); }
                finally { _draftGate.Release(); }
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- rules from a file, a URL or inline text (load_bpa_rules, load_readiness_rules)
        internal async Task<string> GateBasisRuleSourceAsync(GateCall call)
        {
            try { return GateHash(await ResolveRuleSourceAsync(call.Arg<string>("source"), call.Method.Name == nameof(LoadReadinessRulesAsync) ? "readiness rules" : "BPA rules").ConfigureAwait(false)); }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- a package from the registry (dax_lib_install)
        private static string DaxLibPlanBasis(List<InstallPkg> packages) => GateHash((packages ?? new List<InstallPkg>()).Select(p =>
            GateHash(p.PackageId, p.Version, p.Authors, string.Join("\u001e", (p.Functions ?? Array.Empty<DaxLibFunction>()).Select(f => GateHash(f.Name, f.Expression, f.Description))))).ToArray());

        internal async Task<string> GateBasisDaxLibAsync(GateCall call)
        {
            try
            {
                var id = call.Arg<string>("id");
                var ver = await ResolveVersionAsync(id, call.Arg<string>("version"), DaxLibCt).ConfigureAwait(false);
                return DaxLibPlanBasis((await BuildInstallPlanAsync(id, ver, DaxLibCt).ConfigureAwait(false)).Packages);
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- the commits a push would send (git_push)
        private async Task<string> GitPushBasisAsync(string dir, string remote, string branch)
        {
            if (string.IsNullOrEmpty(dir)) return "no repository";
            async Task<string> Sha(params string[] a) { var r = await GitCli.RunAsync(dir, a); return r.Ok ? (r.Stdout ?? "").Trim() : "none"; }
            var pushed = await Sha("rev-parse", string.IsNullOrWhiteSpace(branch) ? "HEAD" : branch.Trim());
            var upstream = await Sha("rev-parse", "@{u}");
            var current = await Sha("rev-parse", "--abbrev-ref", "HEAD");
            return GateHash(remote?.Trim() ?? "", branch?.Trim() ?? "", pushed, upstream, current);
        }

        internal async Task<string> GateBasisGitPushAsync(GateCall call)
        {
            try { return await GitPushBasisAsync(GitWorkingDirOrNull(), call.Arg<string>("remote"), call.Arg<string>("branch")).ConfigureAwait(false); }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- a workflow or a template file that a save overwrites, or a template an instantiate reads
        internal Task<string> GateBasisWorkflowFileAsync(GateCall call)
        {
            try
            {
                var (userDir, _) = WorkflowDirs();
                return Task.FromResult(userDir == null ? "no home" : GateFileState(Path.Combine(userDir, ValidateWorkflowDocumentName(call.Arg<string>("name")) + ".md")));
            }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        internal Task<string> GateBasisTemplateFileAsync(GateCall call)
        {
            try
            {
                var (userDir, _) = TemplateDirs();
                return Task.FromResult(userDir == null ? "no home" : GateFileState(Path.Combine(userDir, (call.Arg<string>("name") ?? "").Trim() + ".md")));
            }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        private static string TemplateBasis(string path, string markdown) => GateHash(path, GateHash(markdown));

        internal Task<string> GateBasisInstantiateAsync(GateCall call)
        {
            try
            {
                var name = call.Arg<string>("templateName");
                var tmpl = LoadTemplateDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(tmpl == null ? "stock:" + name : TemplateBasis(tmpl.FilePath, File.ReadAllText(tmpl.FilePath)));
            }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        // ---- the stored tests (save_test_definition, run_test_suite)
        private string TestDefinitionBasis(string dir, string id)
        {
            var existing = string.IsNullOrEmpty(id) ? null : TestSuiteStore.LoadSuite(dir).Item1.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            return TestDefinitionBasisOf(existing);
        }

        private static string TestDefinitionBasisOf(TestDefinition existing) => existing == null ? "new" : GateHash(TestSuiteStore.Serialize(existing));

        /// <summary>The door's side of delete_test_definition: the test named by <c>id</c>, as the save twin reads the one named by its definition.</summary>
        internal Task<string> GateBasisTestDefinitionByIdAsync(GateCall call)
        {
            try { var s = _sessions.Current; return Task.FromResult(s == null ? "no model" : TestDefinitionBasis(TestsDirFor(s), call.Arg<string>("id"))); }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        private string SuiteBasis(string dir) =>
            GateHash(string.Join("\u001e", TestSuiteStore.LoadSuite(dir).Item1.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => GateHash(TestSuiteStore.Serialize(d)))));

        internal Task<string> GateBasisTestDefinitionAsync(GateCall call)
        {
            try { var s = _sessions.Current; return Task.FromResult(s == null ? "no model" : TestDefinitionBasis(TestsDirFor(s), call.Arg<TestDefinition>("def")?.Id)); }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        internal Task<string> GateBasisSuiteAsync(GateCall call)
        {
            try { var s = _sessions.Current; return Task.FromResult(s == null ? "no model" : SuiteBasis(TestsDirFor(s))); }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        // ---- the evidence artifact a save writes (save_evidence)
        private static string EvidenceBasis(string source, EvidenceArtifact artifact) =>
            GateHash(source, string.IsNullOrWhiteSpace(artifact.Json) ? "none:" + artifact.Error + artifact.Note : (artifact.ContentHash ?? GateHash(artifact.Json)));

        internal async Task<string> GateBasisEvidenceAsync(GateCall call)
        {
            try
            {
                var source = (call.Arg<string>("source") ?? "").Trim().ToLowerInvariant();
                var artifact = await BuildEvidenceArtifactAsync(source, call.Arg<string>("sourceId")).ConfigureAwait(false);
                return artifact == null ? "unknown source" : EvidenceBasis(source, artifact);
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- the stored interview question (run_interview)
        private static string InterviewQuestionBasis(InterviewQuestion q) => q == null ? "no such question" : GateHash(q.Id, q.Question, q.Tier, q.Query, q.ScalarExpr, q.ParaphraseExpr,
            string.Join("\u001f", q.GroupBy ?? Array.Empty<string>()), string.Join("\u001f", q.Filters ?? Array.Empty<string>()), q.ExpectedValue,
            q.ExpectedMatrix == null ? "" : JsonSerializer.Serialize(q.ExpectedMatrix), q.ExpectRefusal ? "refusal" : "");

        internal async Task<string> GateBasisInterviewAsync(GateCall call)
        {
            var id = call.Arg<string>("questionId");
            if (string.IsNullOrWhiteSpace(id)) return "inline";   // an inline question is the call's own argument
            await _interviewGate.WaitAsync().ConfigureAwait(false);
            try { return InterviewQuestionBasis(LocateInterviewQuestion(id.Trim()).q); }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
            finally { _interviewGate.Release(); }
        }

        /// <summary>delete_interview_question: the question named by <c>id</c>, wherever it lives (the op locates it the same way).</summary>
        internal async Task<string> GateBasisInterviewDeleteAsync(GateCall call)
        {
            var id = call.Arg<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return "no such question";
            await _interviewGate.WaitAsync().ConfigureAwait(false);
            try { return InterviewQuestionBasis(LocateInterviewQuestion(id.Trim()).q); }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
            finally { _interviewGate.Release(); }
        }

        /// <summary>add_interview_question: only a call with an <c>id</c> replaces something, and what it replaces is the question of that id in the scope's file.
        /// A call without an id adds a new question and replaces nothing, so it has no earlier state to be stale against.</summary>
        internal async Task<string> GateBasisInterviewEditAsync(GateCall call)
        {
            var id = call.Arg<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return "new question";
            await _interviewGate.WaitAsync().ConfigureAwait(false);
            try { return InterviewQuestionBasis(StoredInterviewQuestion(NormalizeInterviewScope(call.Arg<string>("scope")), id.Trim())); }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
            finally { _interviewGate.Release(); }
        }

        // ---- the Primer and a suggestion for it (set_primer, accept_primer_suggestion, reject_primer_suggestion)
        private static string SuggestionBasis(PrimerSuggestion s) => s == null ? "no such suggestion" : GateHash(s.Id, s.Section, s.Markdown);

        internal async Task<string> GateBasisPrimerAsync(GateCall call)
        {
            try
            {
                var context = _sessions.CurrentContext;
                return GateHash((await GetPrimerCoreAsync(context, PrimerLocusFor(context)).ConfigureAwait(false)).Markdown);
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        internal async Task<string> GateBasisPrimerSuggestionAsync(GateCall call)
        {
            try
            {
                var context = _sessions.CurrentContext;
                var locus = PrimerLocusFor(context);
                var id = call.Arg<string>("id");
                var suggestion = (await ListPrimerSuggestionsCoreAsync(context, locus).ConfigureAwait(false)).Suggestions?.FirstOrDefault(x => x.Id == id);
                var accept = call.Method.Name == nameof(AcceptPrimerSuggestionAsync);
                return accept ? GateHash(SuggestionBasis(suggestion), GateHash((await GetPrimerCoreAsync(context, locus).ConfigureAwait(false)).Markdown)) : SuggestionBasis(suggestion);
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
        }

        // ---- the ratchet ---------------------------------------------------------------------------------------------

        private static int ModeRank(string mode) => mode switch { "hard" => 2, "warn" => 1, _ => 0 };

        /// <summary>True when the binding this call would write is looser than the one the person has on the op: removed, a weaker
        /// mode, or a list of workflows that offers another way to satisfy it. A binding written with rules (a condition per rule) is
        /// the person's own shape, so any change to it by an assistant counts as loosening. A new binding only adds a requirement.</summary>
        internal bool GateLoosensBinding(string op, string[] requireNames, string mode)
        {
            op = op?.Trim();
            if (string.IsNullOrEmpty(op)) return false;
            var m = string.IsNullOrWhiteSpace(mode) ? "off" : mode.Trim().ToLowerInvariant();
            var require = (requireNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            var clearing = m == "off" || require.Length == 0;
            string existingMode = null;
            string[] existingNames = null;
            var present = false;
            var flat = true;
            try
            {
                var file = WorkflowSettingsFile();
                if (file != null && File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(ReadSettingsStrict(file));
                    if (doc.RootElement.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object
                        && bindings.TryGetProperty(op, out var b))
                    {
                        present = true;
                        if (b.ValueKind != JsonValueKind.Object) flat = false;
                        else
                        {
                            existingMode = b.TryGetProperty("mode", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString()?.Trim().ToLowerInvariant() : null;
                            existingNames = b.TryGetProperty("require", out var er) && er.ValueKind == JsonValueKind.Array
                                ? er.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray()
                                : Array.Empty<string>();
                        }
                    }
                }
            }
            catch { present = true; flat = false; }   // a settings file that cannot be read cannot be shown to be loosened by nothing
            if (!present) return false;
            if (!flat) return true;
            if (ModeRank(existingMode) == 0 || (existingNames?.Length ?? 0) == 0) return false;   // not enforced: nothing to take off
            if (clearing) return true;
            if (ModeRank(m) < ModeRank(existingMode)) return true;
            return !require.All(n => existingNames.Contains(n, StringComparer.Ordinal));
        }

        // ---- words that need the model (read-only; they change nothing and leave no file) -----------------------------

        internal async Task<string> GateDescribeStepAsync(bool redo)
        {
            var plain = redo ? "redo the change that was taken back in the open model" : "undo the newest change to the open model";
            var s = _sessions.Current;
            if (s == null) return plain;
            try
            {
                var ledger = _sessions.Ledger;
                var rows = await s.ReadAsync(_ => ledger.RowsWalked(s.Id, s.UndoStateNow().Steps, 1, redo)).ConfigureAwait(false);
                if (rows.Count == 0) return plain;
                return WriteIntent.WalkWords(redo, 1, rows.Select(r => (r.EntryId, r.Label, r.Deltas ?? Array.Empty<ChangeDelta>())).ToList());
            }
            catch { return plain; }
        }

        internal async Task<string> GateDescribeWalkAsync(GateCall call, bool redo)
        {
            var entryId = call.Arg<string>("entryId");
            var (_, steps, rows, _) = await ReadWalkAsync(entryId, redo).ConfigureAwait(false);
            var through = call.Arg<long?>("throughRevision");
            if (!redo && through.HasValue)
            {
                rows = rows.Where(r => r.Revision <= through.Value).ToArray();
                steps = rows.Sum(r => r.StepIndex - r.StepFirst + 1);
            }
            return WriteIntent.WalkWords(redo, steps, rows.Select(r => (r.EntryId, r.Label, r.Deltas ?? Array.Empty<ChangeDelta>())).ToList());
        }

        private const int MostScriptBlocks = 8;
        private const int MostScriptWords = 400;

        /// <summary>The question for a find and replace on one object, in plain words: what is replaced, with what, in which part of which object, and how it matches.
        /// A request object is never printed whole (UAT of join 8, U8).</summary>
        internal static Task<string> GateDescribeReplace(ReplaceRequest r)
        {
            if (r == null) return Task.FromResult("replace text in the model");
            var part = (r.Field ?? "").ToLowerInvariant() switch
            {
                "expression" => "formula", "displayfolder" => "display folder", "formatstring" => "number format", "rlsfilter" => "row filter",
                "mexpression" => "Power Query code", "" => "text", var other => other,
            };
            var how = new List<string>();
            if (r.Regex) how.Add("matched as a pattern");
            if (r.WholeWord) how.Add("whole word only");
            if (r.CaseSensitive) how.Add("capital letters must match");
            if (r.Span != null) how.Add("only the one place chosen");
            var text = (r.Regex ? "replace the pattern " : "replace ") + GateWords.Value("find", r.Find) + " with " + GateWords.Value("replace", r.Replace ?? "")
                + " in the " + part + (string.IsNullOrEmpty(r.Ref) ? " of the model" : " of " + GateWords.NameOf(r.Ref));
            return Task.FromResult(how.Count == 0 ? text : text + " (" + string.Join(", ", how) + ")");
        }

        internal Task<string> GateDescribeDaxScript(string script)
        {
            var blocks = ParseDaxScript(script);
            if (blocks.Count == 0) return Task.FromResult("apply a DAX script (no object blocks were found in it)");
            var lines = blocks.Take(MostScriptBlocks).Select(b => "set the formula of " + GateWords.NameOf(b.objRef) + " to: \"" + ShortenScript(b.expr) + "\"").ToList();
            var text = "apply a DAX script of " + blocks.Count + " block" + (blocks.Count == 1 ? "" : "s") + ": " + string.Join("; ", lines);
            if (blocks.Count > lines.Count) text += "; and " + (blocks.Count - lines.Count) + " more";
            return Task.FromResult(text);
        }

        internal Task<string> GateDescribeTmdlScript(string script)
        {
            List<TmdlScriptDocument> docs;
            try { docs = ParseTmdlDocuments(script); } catch { docs = new List<TmdlScriptDocument>(); }
            if (docs.Count == 0) return Task.FromResult("apply a TMDL script (no objects were found in it)");
            var lines = docs.Take(MostScriptBlocks).Select(d =>
                (d.ObjectRef != null ? "rewrite " + GateWords.NameOf(d.ObjectRef) : "replace the whole definition of " + d.LogicalPath)
                + " with: \"" + ShortenScript(d.Content) + "\"").ToList();
            var text = "apply a TMDL script of " + docs.Count + " block" + (docs.Count == 1 ? "" : "s") + ": " + string.Join("; ", lines);
            if (docs.Count > lines.Count) text += "; and " + (docs.Count - lines.Count) + " more";
            return Task.FromResult(text);
        }

        private static string ShortenScript(string text)
        {
            var one = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            while (one.Contains("  ")) one = one.Replace("  ", " ");
            one = one.Trim();
            return one.Length <= MostScriptWords ? one : one.Substring(0, MostScriptWords) + "... (" + one.Length + " characters in all)";
        }

        internal async Task<string> GateDescribeSafeFixes()
        {
            const string plain = "apply every fix the AI-readiness check calls safe";
            var s = _sessions.Current;
            if (s == null) return plain;
            try
            {
                var card = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m)).ConfigureAwait(false);
                var groups = card.Findings.Where(f => f.Fix == nameof(FixKind.SafeFix) && !f.Waived)
                    .GroupBy(f => f.RuleTitle ?? f.RuleId).Select(g => g.Key + " (" + g.Count() + ")").ToList();
                return groups.Count == 0 ? plain + ": the check finds none to fix now"
                    : plain + ": " + string.Join("; ", groups.Take(GateWords.MostListed)) + (groups.Count > GateWords.MostListed ? "; and " + (groups.Count - GateWords.MostListed) + " more kinds" : "");
            }
            catch { return plain; }
        }

        internal async Task<string> GateDescribeBpaFixAll()
        {
            const string plain = "apply every Best Practice fix that can be made safely";
            var s = _sessions.Current;
            if (s == null) return plain;
            try
            {
                var card = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, GetBpaRules(m))).ConfigureAwait(false);
                var groups = card.Violations.Where(v => v.CanAutoFix && !v.Waived)
                    .GroupBy(v => v.RuleName ?? v.RuleId).Select(g => g.Key + " (" + g.Count() + ")").ToList();
                return groups.Count == 0 ? plain + ": the scan finds none to fix now"
                    : plain + ": " + string.Join("; ", groups.Take(GateWords.MostListed)) + (groups.Count > GateWords.MostListed ? "; and " + (groups.Count - GateWords.MostListed) + " more kinds" : "");
            }
            catch { return plain; }
        }

        internal async Task<string> GateDescribeCicdAsync(GateCall call)
        {
            var ado = string.Equals(call.Arg<string>("target"), "ado", StringComparison.OrdinalIgnoreCase)
                || string.Equals(call.Arg<string>("target"), "azuredevops", StringComparison.OrdinalIgnoreCase);
            string root = null;
            try { root = await ResolveRepoRootForWriteAsync().ConfigureAwait(false); } catch { /* the words then name no folder */ }
            var files = new[] { "parameter.yml", ".deploy/deploy.py", ado ? "azure-pipelines.yml" : ".github/workflows/fabric-cicd.yml" };
            return "write a CI scaffold into your repository" + (root == null ? "" : " (" + root + ")") + ": " + string.Join(", ", files) + " (a file that is already there is left alone)";
        }

        internal async Task<string> GateDescribeDraftAsync(GateCall call)
        {
            var id = call.Arg<string>("draftId");
            try
            {
                var view = await GetDraftAsync(id).ConfigureAwait(false);
                var title = string.IsNullOrWhiteSpace(view?.Title) ? id : view.Title;
                var summary = ShortenScript(view?.Summary);
                return "run the draft \"" + title + "\"" + (string.IsNullOrWhiteSpace(summary) ? "" : ": " + summary);
            }
            catch { return "run the draft " + id; }
        }

        // ---- the plan: words that name every object and the new value (Q1: the read-only describe hook) -----------------------------

        /// <summary>The most lines one question lists; the rest are counted. The approval is bound to ALL of them (a plan line's content is
        /// part of what the grant is for), so a long list is cut for reading only.</summary>
        private const int MostPlanLines = 25;

        private static string PlanWords(string lead, List<string> lines)
        {
            if (lines.Count == 0) return lead + " (nothing on it would be written now)";
            var shown = lines.Take(MostPlanLines).ToList();
            var text = lead + ": " + string.Join("; ", shown);
            if (lines.Count > shown.Count) text += "; and " + (lines.Count - shown.Count) + " more, which you can read in the plan";
            return text;
        }

        /// <summary>One line of the assistant's change plan as the person reads it: the object and the value it will be given.</summary>
        internal static string PlanLineWords(ChangeItem it)
        {
            var what = PlanLineCore(it);
            return string.Equals(it.Status, "approved", StringComparison.OrdinalIgnoreCase) ? what : what + " (not approved yet, so it will be skipped)";
        }

        /// <summary>The first lines a clear or a swap would throw away, as the person reads them, the rest counted. Empty for none.</summary>
        private static string PlanLinesLost(IEnumerable<ChangeItem> items)
        {
            var all = (items ?? Array.Empty<ChangeItem>()).Where(i => i != null).ToList();
            if (all.Count == 0) return "";
            var shown = all.Take(GateWords.MostListed).Select(PlanLineCore).ToList();
            return string.Join("; ", shown) + (all.Count > shown.Count ? "; and " + (all.Count - shown.Count) + " more" : "");
        }

        private static string PlanLineCore(ChangeItem it)
        {
            var obj = !string.IsNullOrWhiteSpace(it.ObjectRef) ? GateWords.NameOf(it.ObjectRef) : (it.ObjectName ?? "the model");
            var after = GateWords.Cut(it.After ?? "");
            var kind = (it.Kind ?? "").Trim().ToLowerInvariant();
            var what = kind switch
            {
                "rename" => "rename " + obj + " to \"" + after + "\"",
                "delete" => "delete " + obj,
                "delete_if_unused" => "delete " + obj + " if nothing uses it",
                "set_description" => "set the description of " + obj + " to \"" + after + "\"",
                "set_dax" => "set the formula of " + obj + " to \"" + after + "\"",
                _ => string.IsNullOrEmpty(after) ? kind.Replace('_', ' ') + " on " + obj : kind.Replace('_', ' ') + " on " + obj + ": \"" + after + "\"",
            };
            return what;
        }

        /// <summary>The lines apply_plan will look at: the ones named, or every approved one when none are named.</summary>
        private static List<ChangeItem> PlanLinesFor(ChangePlanState plan, string[] approvedIds)
        {
            var all = plan?.Snapshot() ?? Array.Empty<ChangeItem>();
            var named = approvedIds != null && approvedIds.Length > 0
                ? new HashSet<string>(approvedIds, StringComparer.OrdinalIgnoreCase) : null;
            return all.Where(i => named == null ? string.Equals(i.Status, "approved", StringComparison.OrdinalIgnoreCase) : named.Contains(i.Id ?? "")).ToList();
        }

        /// <summary>What an approval of apply_plan is bound to besides the model: the content of each line it will look at. A line edited,
        /// approved or dropped after the question is a different question. Re-computed inside the apply, under the plan's own gate.</summary>
        internal static string PlanLinesBasis(ChangePlanState plan, string[] approvedIds)
        {
            var sb = new StringBuilder();
            foreach (var it in PlanLinesFor(plan, approvedIds).OrderBy(i => i.Id, StringComparer.Ordinal))
                sb.Append(it.Id).Append('|').Append(it.Status).Append('|').Append(it.Kind).Append('|').Append(it.ObjectRef).Append('|')
                  .Append(it.Target).Append('|').Append(it.After).Append('|').Append(it.Suggested ? "suggested" : "").Append('\u001f');
            return sb.ToString();
        }

        internal Task<string> GateDescribePlanItems(string[] approvedIds)
        {
            var plain = "apply the approved plan items " + GateWords.Value("approvedIds", approvedIds ?? Array.Empty<string>(), raw: true);
            try
            {
                var plan = _sessions.CurrentContext?.Plans.Current;
                if (plan == null) return Task.FromResult(plain);
                return Task.FromResult(PlanWords("apply the approved plan items", PlanLinesFor(plan, approvedIds).Select(PlanLineWords).ToList()));
            }
            catch { return Task.FromResult(plain); }
        }

        internal Task<string> GateBasisPlanItems(GateCall call)
        {
            try { return Task.FromResult(PlanLinesBasis(_sessions.CurrentContext?.Plans.Current, call.Arg<string[]>("approvedIds"))); }
            catch { return Task.FromResult(""); }
        }

        /// <summary>The approved lines on the assistant's change plan that a Model Plan row carries, read off the change plan itself (in memory:
        /// nothing is composed, so nothing is written and no background work starts). A row's lines are filed by what each line IS
        /// (<see cref="ItemIdForLine"/>), and a line no family claims sits on the assistant's own row for its kind, as the plan files it.</summary>
        private async Task<List<ChangeItem>> GateModelPlanRowLinesAsync(string rowId)
        {
            var lines = await PlanLinesSnapshotAsync().ConfigureAwait(false);
            return lines.Where(l => string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(ItemIdForLine(l), rowId, StringComparison.Ordinal)
                    || string.Equals(AssistantItemPrefix + (l.Kind ?? ""), rowId, StringComparison.Ordinal))).ToList();
        }

        internal async Task<string> GateDescribePlanItemDigests(GateCall call)
        {
            if (!string.IsNullOrWhiteSpace(call.Arg<string>("readyDigest")))
            {
                // The ready rows are worked out when the plan is composed, which writes. The words name every approved change the plan
                // holds, which is what the run can take; the grant is bound to the digest of the ready set the person is shown.
                var held = (await PlanLinesSnapshotAsync().ConfigureAwait(false)).Where(l => string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase)).ToList();
                return PlanWords("run every plan row that is ready for Semanticus (the whole ready set the plan showed). The approved changes on the plan now are",
                    held.Select(PlanLineWords).ToList());
            }
            var ids = (call.Arg<PlanItemDigest[]>("items") ?? Array.Empty<PlanItemDigest>()).Where(i => i != null).Select(i => i.Id).ToArray();
            var lines = new List<string>();
            foreach (var id in ids)
            {
                var rowLines = await GateModelPlanRowLinesAsync(id).ConfigureAwait(false);
                if (rowLines.Count == 0) lines.Add("the plan row " + id + " (the changes it carries are worked out when it runs)");
                else lines.AddRange(rowLines.Select(PlanLineWords));
            }
            return PlanWords("run the plan rows " + GateWords.Value("items", ids, raw: true), lines);
        }

        internal async Task<string> GateDescribeModelPlanActionAsync(GateCall call)
        {
            var action = string.IsNullOrWhiteSpace(call.Arg<string>("action")) ? "start" : call.Arg<string>("action").Trim().ToLowerInvariant();
            var id = call.Arg<string>("id");
            var lead = action == "approve" ? "apply the changes kept on the plan row " + id : "run the plan row " + id + " straight away";
            var rowLines = await GateModelPlanRowLinesAsync(id ?? "").ConfigureAwait(false);
            return rowLines.Count == 0 ? lead + " (the changes it carries are worked out when it runs)" : PlanWords(lead, rowLines.Select(PlanLineWords).ToList());
        }
    }
}
