using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Model Interview ops (docs/product-innovation-brainstorm.md §1) — dual-drive like everything else.
    /// GOLDEN RULE 1 holds hard: the engine only EXECUTES the recorded DAX, COMPARES against the recorded oracle,
    /// SCORES deterministically (Interview.cs), and STORES outcomes; the user's Claude authors the questions and
    /// the DAX attempts via the /interview-model skill. FREE/PRO (Kane, locked 2026-07-07): list + one-off
    /// run_interview are free; add_interview_question (persisting to the pack) is Pro — mirroring "verify free,
    /// enforce paid".
    /// </summary>
    public sealed partial class LocalEngine
    {
        private readonly System.Threading.SemaphoreSlim _interviewGate = new System.Threading.SemaphoreSlim(1, 1);

        private static readonly JsonSerializerOptions InterviewJson = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        // ---- scope → file (the ExperienceLog placement rule, exactly as LocalEngine.Knowledge.cs) --------

        private string ProjectInterviewDir()
        {
            var anchor = _sessions.Current?.SourcePath;
            var sidecar = !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                        : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
            return sidecar == null ? null : Path.Combine(sidecar, InterviewStore.DirName);
        }

        private static string GlobalInterviewDir()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, LayoutStore.DirName, InterviewStore.DirName);
        }

        private string InterviewScopeFile(string scope)
        {
            var dir = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ? GlobalInterviewDir() : ProjectInterviewDir();
            return dir == null ? null : Path.Combine(dir, InterviewStore.FileName);
        }

        private static string NormalizeInterviewScope(string scope)
            => string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "project";

        // ---- #157: bind a pack to the model it was authored against --------------------------------------
        // TWO independent gates, because an interview touches TWO models that can differ:
        //   • GATE 1 — PANE attribution (list buckets, replay/advisory inclusion): the EDITABLE session's identity.
        //   • GATE 2 — EXECUTION target (grading): the ATTACHED live connection (_live) the DAX actually runs against,
        //     which is INDEPENDENT of the editable session (connect_xmla/connect_local swap _live without touching
        //     the session). Checking only the session (the original #157 fix) let a pack validated as "mine" be
        //     graded against a completely different live model — closed here by re-deriving and matching _live at the
        //     moment of execution.
        //
        // Identity is DELIBERATELY the edit-STABLE provenance identity, NOT the semantic shape fingerprint
        // (get_model_fingerprint / ComputeFingerprint): the shape hash changes the instant you add/drop a measure —
        // the very edits interview_replay guards — so it would orphan the pack on the first edit.
        //
        // WHAT THE BINDING PROVES: the question was SAVED while this endpoint+database (live) / this model file (disk)
        // was open, and — for a value/paraphrase question — its trusted answer was confirmed against that exact live
        // connection. WHAT IT DOES NOT PROVE: that the model now occupying that address is still the same model. A
        // model REPLACED IN PLACE at the same endpoint|database is indistinguishable by coordinate alone; the weak
        // ModelWitness (the model NAME at authoring) catches the common case (a renamed/replaced model) and refuses,
        // but a same-name replacement remains a residual, disclosed limit. RENAME LIMIT: the live key includes the
        // database name and the disk key is the file path, so renaming the live database or moving the file changes
        // the identity — the pack then reads as "from another model", surfaced honestly, never silently adopted.

        // ---- identity primitives (normalized, so one model never yields two keys and two never yield one) --------
        private static string Sha8(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")), 0, 8).ToLowerInvariant();
        }

        // Live endpoint|database normalization (P3-6): trim, drop trailing slashes, lowercase — equivalent accepted
        // spellings of the same endpoint must collapse to one key.
        private static string NormLivePart(string s) => (s ?? "").Trim().TrimEnd('/', '\\').ToLowerInvariant();

        // Anchor (on-disk model file) canonicalization (P3-6): GetFullPath resolves . / .. / separators; ResolveLinkTarget
        // resolves symlinks/junctions; GetLongPathName expands 8.3 short names on Windows. Case is folded ONLY on
        // Windows (a case-insensitive FS) — on Linux/macOS 'a.bim' and 'A.bim' are DIFFERENT files and must not
        // collide. Best-effort: any step that throws falls back to the previous form.
        private static string CanonAnchor(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string p;
            try { p = Path.GetFullPath(path); } catch { p = path; }
            try { var final = File.ResolveLinkTarget(p, returnFinalTarget: true)?.FullName; if (!string.IsNullOrEmpty(final)) p = final; } catch { }
            try { if (OperatingSystem.IsWindows() && (File.Exists(p) || Directory.Exists(p))) p = LongPath(p); } catch { }
            p = p.TrimEnd('/', '\\');
            return OperatingSystem.IsWindows() ? p.ToUpperInvariant() : p;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetLongPathName(string shortPath, [System.Runtime.InteropServices.Out] char[] longPath, int bufferSize);

        private static string LongPath(string p)
        {
            var buf = new char[1024];
            int n = GetLongPathName(p, buf, buf.Length);
            return n > 0 && n < buf.Length ? new string(buf, 0, n) : p;
        }

        /// <summary>The live connection's execution identity (endpoint|database), or null. This is what a query
        /// ACTUALLY runs against — the grading key.</summary>
        private static string LiveIdentityFor(LiveConnection c)
            => c == null ? null : "live:" + Sha8(NormLivePart(c.DataSource) + "|" + NormLivePart(c.Database));

        private static string LiveIdentityFor(LiveOrigin o)
            => o == null ? null : "live:" + Sha8(NormLivePart(o.Endpoint) + "|" + NormLivePart(o.Database));

        private static string LiveTargetLabel(LiveConnection c)
            => c == null ? null : (string.IsNullOrWhiteSpace(c.Database) ? c.DataSource : $"{c.Database} on {c.DataSource}");

        /// <summary>The PANE identity — which model's interview this pane shows, and the DURABLE key a pack binds to.
        /// In priority: the editable session's own durable identity (open_live endpoint|database; else the FULL
        /// on-disk model PATH, not the folder, so two models in one folder differ, P1-2); else the ATTACHED live
        /// connection (a connect_xmla IS working on that live model). Returns null when there is NO durable anchor
        /// (an unsaved, unconnected model, or nothing open): such a model CANNOT own a persisted pack. A per-process
        /// session counter is NOT used as an identity — Session.Id is "s"+Interlocked-counter reset to 0 each launch,
        /// so the first scratch model of every process is sess:s1 and packs would collide across launches.</summary>
        private static string PaneIdentity(Session s, LiveConnection live)
        {
            if (s != null)
            {
                if (s.LiveOrigin != null) return LiveIdentityFor(s.LiveOrigin);
                if (!ExperienceStore.IsEphemeralAnchor(s.SourcePath)) return "disk:" + Sha8(CanonAnchor(s.SourcePath));
            }
            return live != null ? LiveIdentityFor(live) : null;   // an attached live connection is the only durable anchor left; else none
        }

        private string OpenModelIdentity() => PaneIdentity(_sessions.Current, _live);

        // Whether the identity names an ADDRESS the model merely occupies (a live endpoint|database, OR a file
        // path), as opposed to a content identity. BOTH are subject to a same-address, same-name in-place
        // replacement: overwrite C:\model.bim (or the dataset at an endpoint) with a different model and re-open the
        // same address, and the key is unchanged. Gates the witness and the honest disclosure — a file path is an
        // address just as much as an endpoint is.
        private static bool IsAddressIdentity(string id) => id != null && (id.StartsWith("live:", StringComparison.Ordinal) || id.StartsWith("disk:", StringComparison.Ordinal));

        /// <summary>The tier-independent, connection-independent WITNESS: a pack bound to an ADDRESS (a live endpoint
        /// or a file path) is refused when the model NAME now at that address no longer matches the name at
        /// authoring — an in-place replacement the open session reflects (a re-open of the same path/endpoint after
        /// the model there changed). Covers the REFUSAL tier and the disk path that Gate 2 (live execution) does not.
        /// A SAME-name replacement is not caught (disclosed in the list Note); a pack authored with no session has no
        /// witness (connect_xmla-only), also disclosed. Returns null (allow) when there is nothing to compare.</summary>
        private static string PaneReplacementRefusal(InterviewQuestion q, string currentModelName)
        {
            if (!IsAddressIdentity(q.ModelIdentity) || string.IsNullOrEmpty(q.ModelWitness) || string.IsNullOrEmpty(currentModelName))
                return null;
            return string.Equals(q.ModelWitness, currentModelName, StringComparison.Ordinal) ? null
                : $"The model open at this address is now “{currentModelName}”, but this question was authored against “{q.ModelWitness}” (the model may have been replaced). Re-confirm the trusted answer against the current model before grading.";
        }

        // The honest same-address caveat, appended to a confidently-graded run so a user who reads "Right" also sees
        // the limit — not only the list Note. True for a file path and an endpoint alike.
        private const string AddressMatchCaveat = " Note: this question is matched by the model's address (its file path or live endpoint and database); a model replaced in place at the same address with the same name is not detected.";

        /// <summary>A readable name for the open model, for the "from another model" divider (display only).</summary>
        private static string ModelPaneLabel(Session s, string modelName)
        {
            if (s == null) return null;
            if (s.LiveOrigin != null && !string.IsNullOrWhiteSpace(s.LiveOrigin.Database)) return s.LiveOrigin.Database.Trim();
            if (!ExperienceStore.IsEphemeralAnchor(s.SourcePath) && !string.IsNullOrWhiteSpace(s.SourcePath))
                return Path.GetFileNameWithoutExtension(s.SourcePath.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(modelName) ? null : modelName;
        }

        private static Task<string> ReadModelNameAsync(Session s)
            => s == null ? Task.FromResult<string>(null)
             : s.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);

        private static bool HasOracle(InterviewQuestion q)
            => !string.IsNullOrEmpty(q.ExpectedValue) || (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0);

        private enum PackAttribution { Mine, Other, Unattributed }

        /// <summary>GATE 1 (pane): which model a saved question belongs to, relative to the open model. FAIL-CLOSED —
        /// a binding that cannot be positively established as the open model's is NEVER "mine". No file-location
        /// adoption (a sidecar is folder-scoped and can be shared by two models, P1-2), so an unbound legacy question
        /// is always surfaced as unattributed, never silently adopted.</summary>
        private static PackAttribution ClassifyQuestion(InterviewQuestion q, string openIdentity)
        {
            if (string.IsNullOrEmpty(openIdentity))
                // No open model at all: nothing can be positively attributed. A bound question belongs to SOME model
                // (just not one we can confirm here) → surface as "other"; an unbound one → unattributed. Never mine.
                return string.IsNullOrEmpty(q.ModelIdentity) ? PackAttribution.Unattributed : PackAttribution.Other;
            if (!string.IsNullOrEmpty(q.ModelIdentity))
                return string.Equals(q.ModelIdentity, openIdentity, StringComparison.Ordinal) ? PackAttribution.Mine : PackAttribution.Other;
            return PackAttribution.Unattributed;   // unbound legacy — surfaced honestly, never adopted
        }

        /// <summary>Split a materialized set into the OPEN model's pack, other models' packs, and unattributed legacy
        /// questions (fail-closed). Session captured ONCE for a stable classification. Shared by list, replay and the
        /// deploy advisory so every read filters identically.</summary>
        private (List<InterviewQuestion> mine, List<InterviewQuestion> other, List<InterviewQuestion> unattributed)
            PartitionByAttribution(IEnumerable<InterviewQuestion> all)
        {
            var openId = PaneIdentity(_sessions.Current, _live);
            var mine = new List<InterviewQuestion>();
            var other = new List<InterviewQuestion>();
            var unattr = new List<InterviewQuestion>();
            foreach (var q in all)
                switch (ClassifyQuestion(q, openId))
                {
                    case PackAttribution.Other: other.Add(q); break;
                    case PackAttribution.Unattributed: unattr.Add(q); break;
                    default: mine.Add(q); break;
                }
            return (mine, other, unattr);
        }

        // ---- GATE 2: the EXECUTION target must be the model the oracle was confirmed against ---------------
        /// <summary>Returns null if a value/paraphrase question may be GRADED against <paramref name="liveConn"/>,
        /// else a teaching refusal. Offline (no live connection) is always null: nothing executes, so scoring is
        /// Unverified and no misleading grade is possible. Refusal-tier questions never touch the live connection
        /// (they grade the assistant, offline) so they are governed by Gate 1 alone.</summary>
        private string ExecutionTargetRefusal(InterviewQuestion q, LiveConnection liveConn, string currentModelName)
        {
            if (liveConn == null || q.Tier == "refusal") return null;
            var liveId = LiveIdentityFor(liveConn);
            var liveTarget = LiveTargetLabel(liveConn);
            // 2a — the pack's confirmed execution target must be the one now attached. THE core R1 fix: grading runs
            // against _live, independent of the editable session, so a pack "owned" by session A can still point at B.
            if (!string.IsNullOrEmpty(q.ExecIdentity))
            {
                if (!string.Equals(q.ExecIdentity, liveId, StringComparison.Ordinal))
                    return $"This question's trusted answer was confirmed against {Disp(q.ExecTarget)}, but the connection now attached is “{liveTarget}” (a different model). Grading it here would score the answer against another model's data. Re-attach the original connection to run it, or re-confirm the number against the connected model (add_interview_question).";
            }
            else if (HasOracle(q))
            {
                // 2b — an oracle never confirmed against ANY live connection (saved offline) cannot be tied to the
                // attached model. Fail closed rather than grade a trusted number against an unverified target.
                return $"This question's trusted answer was never confirmed against a live connection, so grading it against “{liveTarget}” could compare it to the wrong model's data. Run it while connected to the model it describes and re-save (add_interview_question) to bind it.";
            }
            // The in-place-replacement WITNESS is handled tier-independently by PaneReplacementRefusal (so it also
            // covers the refusal tier and the disk-path pack), applied by every caller alongside this check.
            return null;
        }

        private static string Disp(string s) => string.IsNullOrWhiteSpace(s) ? "a different connection" : $"“{s}”";

        /// <summary>TOCTOU guard (P1-3): the session/live connection captured at op start must still be the current
        /// one right before execution; a swap in between must abort, not grade against the replacement.</summary>
        private string StabilityRefusal(Session capturedSession, LiveConnection capturedLive)
        {
            if (!ReferenceEquals(_sessions.Current, capturedSession))
                return "The open model changed while this interview was running, so it was not graded (re-run against the model you intend).";
            if (!ReferenceEquals(_live, capturedLive))
                return "The live connection changed while this interview was running, so it was not graded (re-run against the connection you intend).";
            return null;
        }

        // Test seam (P3-E): fires right before the live execution inside RunInterviewCoreAsync, so a test can drive a
        // real mid-run session/live swap through the actual code path and prove it aborts rather than grades. This is
        // test-only surface, NOT an extension point: internal, null by default, and unreachable from the MCP/RPC
        // doors (only Semanticus.Tests, via InternalsVisibleTo, ever assigns it).
        internal Action BeforeInterviewExecuteForTest;

        private static InterviewRunResult Uncheckable(InterviewQuestion q, string detail)
            => new InterviewRunResult { QuestionId = q.Id, Question = q.Question, Tier = q.Tier, Outcome = InterviewScoring.Unverified, Detail = detail, Recorded = false, Query = null };

        // Test seam (P1-3): the TOCTOU guard the run/replay/advisory paths invoke right before executing. Exposed so a
        // deterministic test can prove a session/live swap between capture and execution aborts rather than grades.
        internal string StabilityRefusalForTest(Session capturedSession, LiveConnection capturedLive) => StabilityRefusal(capturedSession, capturedLive);
        internal string ExecutionTargetRefusalForTest(InterviewQuestion q, LiveConnection liveConn, string currentModelName) => ExecutionTargetRefusal(q, liveConn, currentModelName);

        /// <summary>Find which scope's file holds a live question id (project first, then global). Throws
        /// instructively when absent — list_interview_questions shows the live set.</summary>
        private (string file, string scope, InterviewQuestion q) LocateInterviewQuestion(string id)
        {
            foreach (var scope in new[] { "project", "global" })
            {
                var file = InterviewScopeFile(scope);
                var (live, _) = InterviewStore.Materialize(file, scope);
                var q = live.FirstOrDefault(r => r.Id == id);
                if (q != null) return (file, scope, q);
            }
            throw new InvalidOperationException($"Interview question '{id}' not found (it may have been deleted — list_interview_questions shows the saved pack).");
        }

        // ---- reads (free) --------------------------------------------------------------------------------

        public Task<InterviewListResult> ListInterviewQuestionsAsync(string scope)
        {
            var scopes = string.IsNullOrEmpty(scope) ? new[] { "project", "global" } : new[] { NormalizeInterviewScope(scope) };
            var all = new List<InterviewQuestion>();
            int skipped = 0;
            foreach (var sc in scopes)
            {
                var (live, sk) = InterviewStore.Materialize(InterviewScopeFile(sc), sc);
                all.AddRange(live);
                skipped += sk;
            }
            // #157: bind the pack to the open model. `Questions` is the OPEN model's pack only; questions stamped
            // with another model's identity, or unattributed legacy questions in a shared store, get their own
            // buckets so they are surfaced honestly, never presented as this model's interview.
            var (mine, other, unattr) = PartitionByAttribution(all);
            var buckets = new List<string>();
            if (other.Count > 0) buckets.Add($"{other.Count} belong to a different model");
            if (unattr.Count > 0) buckets.Add($"{unattr.Count} are unattributed (saved before questions were tied to a model)");
            // P2-B disclosure: state the honest limit of an ADDRESS identity (a file path OR a live endpoint|database)
            // right where the pack is read — a model REPLACED IN PLACE at the same address, keeping the same name, is
            // not detected. True for a disk pack as much as a live one.
            bool address = IsAddressIdentity(OpenModelIdentity());
            var caveat = address && mine.Count > 0
                ? " Note: this pack is matched by the model's address (its file path, or its live endpoint and database); a model replaced in place at the same address with the same name is not detected."
                : "";
            // P3-F: unattributed questions have no re-bind op yet; point at the honest workaround + the tracking issue.
            var rebind = unattr.Count > 0
                ? " To claim an unattributed question for this model, delete_interview_question and re-add it while this model is open (a dedicated re-bind op is tracked in #164)."
                : "";
            return Task.FromResult(new InterviewListResult
            {
                Questions = mine.ToArray(),
                OtherModelQuestions = other.ToArray(),
                UnattributedQuestions = unattr.ToArray(),
                SkippedCorruptLines = skipped,
                Note = skipped > 0 ? $"{skipped} corrupt line(s) were skipped (the store is not bricked; the rest replayed cleanly)."
                     : mine.Count == 0 && buckets.Count > 0 ? $"No interview questions for the open model. {string.Join("; ", buckets)}; shown separately, not run against this model.{rebind}"
                     : mine.Count == 0 ? "No saved interview questions yet. run_interview grades a one-off inline question for free; add_interview_question saves questions to the pack (Pro) so they replay on every future edit."
                     : buckets.Count > 0 ? $"Also present but not this model's: {string.Join("; ", buckets)} (shown separately).{rebind}{caveat}"
                     : (caveat.Length > 0 ? caveat.TrimStart() : null),
            });
        }

        // ---- writes --------------------------------------------------------------------------------------

        /// <summary>Persist a question to the pack (PRO — the free path is one-off run_interview, which grades the
        /// same question inline without saving). Validates the tier's required fields at add time, fail-loud, so a
        /// saved question is always runnable.</summary>
        public async Task<InterviewQuestion> AddInterviewQuestionAsync(
            string question, string tier, string query, string scalarExpr, string paraphraseExpr,
            string[] groupBy, string[] filters, string expectedValue, string expectedMatrixJson,
            bool expectRefusal, string fixRuleId, string seedSource, string scope, string origin)
        {
            Entitlement.EntitlementGuard.RequirePro(_entitlement,
                "add_interview_question (saving a question to the model's interview pack, so it replays as a regression check)",
                "One-off checks stay free: run_interview grades any inline question without saving it, and list_interview_questions reads the saved pack.");

            var q = new InterviewQuestion
            {
                Question = (question ?? "").Trim(),
                Tier = InterviewScoring.NormalizeTier(tier),
                Query = Trimmed(query),
                ScalarExpr = Trimmed(scalarExpr),
                ParaphraseExpr = Trimmed(paraphraseExpr),
                GroupBy = groupBy ?? Array.Empty<string>(),
                Filters = filters ?? Array.Empty<string>(),
                ExpectedValue = Trimmed(expectedValue),
                ExpectedMatrix = ParseExpectedMatrix(expectedMatrixJson),
                ExpectRefusal = expectRefusal || InterviewScoring.NormalizeTier(tier) == "refusal",
                FixRuleId = Trimmed(fixRuleId),
                SeedSource = Trimmed(seedSource) ?? "user",
            };
            ValidateInterviewQuestion(q);

            // #157: capture the session + live connection ONCE (a swap mid-op must not stamp a mismatched identity —
            // P1-3 save race). scope→file is resolved from the captured session's anchor, and every identity field is
            // derived from these same captured refs.
            var sess = _sessions.Current;
            var liveConn = _live;
            scope = NormalizeInterviewScope(scope);
            var file = InterviewScopeFile(scope);
            if (file == null)
                throw new InvalidOperationException("No project interview store: open or save a model, or run the engine with a workspace (or use scope='global').");

            // Stamp BOTH bindings: the PANE identity (which editable model's pack) and the EXECUTION identity (the
            // live connection this question's oracle was confirmed against, what grading must later match). Plus the
            // weak model-name witness so an in-place replacement at the same address is caught, not silently graded.
            q.ModelIdentity = PaneIdentity(sess, liveConn);
            // Fail closed: a persisted pack MUST bind to a durable model identity. An unsaved, unconnected model has
            // none (a per-process session counter would collide across launches), so refuse rather than persist a
            // pack that a different model could later inherit. Save the model or connect it first.
            if (string.IsNullOrEmpty(q.ModelIdentity))
                throw new InvalidOperationException("This model has no durable identity to bind the question to (it is unsaved and not connected to a live source), so the question was NOT saved. Save the model (save_model) or connect it (open_live / connect_xmla) first, so a saved question belongs to a specific model and is only ever replayed against that model.");
            q.ExecIdentity = LiveIdentityFor(liveConn);
            q.ExecTarget = LiveTargetLabel(liveConn);
            var modelName = sess == null ? null : await ReadModelNameAsync(sess);
            q.ModelWitness = modelName;
            q.ModelLabel = ModelPaneLabel(sess, modelName);

            var id = InterviewStore.NewId();
            await _interviewGate.WaitAsync();
            try
            {
                // TOCTOU: the label read above awaited on the dispatcher — a session swap could have landed. Refuse to
                // write a binding for a model that is no longer the one we read (never stamp B's identity into A's pack).
                if (!ReferenceEquals(_sessions.Current, sess))
                    throw new InvalidOperationException("The open model changed while saving this question, so it was NOT saved (re-run add_interview_question against the model you intend).");
                // Fail-loud, as promised: Append is best-effort by contract (the run-record paths need that),
                // so the SAVE path must check it — returning a "saved" question that never hit disk would be
                // the exact silent failure this op's doc-comment forbids.
                if (!InterviewStore.TryAppend(file, new InterviewStore.Delta
                {
                    Op = "add", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent",
                    Question = q.Question, Tier = q.Tier, Query = q.Query, ScalarExpr = q.ScalarExpr,
                    ParaphraseExpr = q.ParaphraseExpr, GroupBy = q.GroupBy, Filters = q.Filters,
                    ExpectedValue = q.ExpectedValue, ExpectedMatrix = q.ExpectedMatrix, ExpectRefusal = q.ExpectRefusal,
                    FixRuleId = q.FixRuleId, SeedSource = q.SeedSource,
                    ModelIdentity = q.ModelIdentity, ModelLabel = q.ModelLabel,
                    ExecIdentity = q.ExecIdentity, ExecTarget = q.ExecTarget, ModelWitness = q.ModelWitness,
                }, out var why))
                    throw new InvalidOperationException($"The question was NOT saved — {why}." + (why.Contains("per-line cap")
                        ? " Trim the biggest fields: prefer a scalar expectedValue over a large expectedMatrixJson (or record fewer rows / narrow the query with filters)."
                        : ""));
            }
            finally { _interviewGate.Release(); }

            await EmitInterviewActivity("add_interview_question", true, $"Saved interview question {id} ({q.Tier}, {scope})", id, origin);
            var (live, _) = InterviewStore.Materialize(file, scope);
            return live.FirstOrDefault(r => r.Id == id);
        }

        /// <summary>Tombstone a saved question (delta append; the JSONL is never rewritten). Free — it is the
        /// user's own data, and a pack you can't prune is a pack you stop trusting.</summary>
        public async Task<SetResult> DeleteInterviewQuestionAsync(string id, string origin)
        {
            await _interviewGate.WaitAsync();
            try
            {
                var (file, _, _) = LocateInterviewQuestion(id);   // throws instructively if it isn't live
                // Same fail-loud contract as the save path: a tombstone that never hit disk would leave the
                // question alive while this op claims Changed=true.
                if (!InterviewStore.TryAppend(file, new InterviewStore.Delta { Op = "delete", Id = id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent" }, out var why))
                    throw new InvalidOperationException($"The question was NOT deleted — {why}.");
            }
            finally { _interviewGate.Release(); }
            await EmitInterviewActivity("delete_interview_question", true, $"Deleted interview question {id}", id, origin);
            return new SetResult { Changed = true };
        }

        // ---- run (free — execute + score; persists only a run RECORD on a saved question) ------------------

        /// <summary>Grade one question. Saved (<paramref name="questionId"/>) or inline (<paramref name="inlineJson"/>,
        /// the InterviewQuestion shape) — one-off inline runs persist NOTHING (that is the free tier's whole path);
        /// a saved question gets a best-effort record-run delta so the Studio card shows the latest outcome.</summary>
        public async Task<InterviewRunResult> RunInterviewAsync(string questionId, string inlineJson, bool abstained, string attemptDax, string origin)
        {
            InterviewQuestion q;
            string file = null, scope = null;
            // Capture the session + live connection ONCE (P1-3): the whole op — classify, execute, grade — is judged
            // against THESE refs, and a swap in between aborts rather than grading against the replacement.
            var capturedSession = _sessions.Current;
            var capturedLive = _live;
            // Read the CURRENT session model name unconditionally (P3, R3): the in-place-replacement witness must
            // cover the disk-path + refusal-tier lane, which has no live connection, so it cannot be gated on _live.
            var currentName = capturedSession == null ? null : await ReadModelNameAsync(capturedSession);
            if (!string.IsNullOrWhiteSpace(questionId))
            {
                (file, scope, q) = LocateInterviewQuestion(questionId.Trim());
                // GATE 1 (pane): never OFFER another model's (or an unattributed) question for the open model. Its DAX
                // and oracle were authored against a DIFFERENT model. Fail closed with a teaching message; the question
                // is still visible (list surfaces it under its own bucket) and deletable.
                if (ClassifyQuestion(q, PaneIdentity(capturedSession, capturedLive)) != PackAttribution.Mine)
                    throw new InvalidOperationException(
                        $"Question '{q.Id}' was authored against a different model" + (string.IsNullOrWhiteSpace(q.ModelLabel) ? "" : $" (“{q.ModelLabel}”)") +
                        (string.IsNullOrEmpty(q.ModelIdentity) ? " and is unattributed (saved before questions were tied to a model, in a shared store)" : "") +
                        ". Running it against the open model would grade its trusted answer against another model's data (a misleading result). Re-open that model to run it, author the question for THIS model (add_interview_question saves it bound to the open model), or delete_interview_question to retire it.");
                // WITNESS (tier-independent): the model at this address (file path OR live endpoint) may have been
                // replaced in place; refuse a name mismatch before grading (covers the refusal tier + disk path).
                var witness = PaneReplacementRefusal(q, currentName);
                if (witness != null) throw new InvalidOperationException(witness);
                // GATE 2 (execution): even a question that IS this pane's may not be graded against the wrong LIVE
                // connection (connect_xmla/connect_local attach _live independently of the editable session). This is
                // the R1 core fix — check the connection the query will ACTUALLY execute against, and refuse loudly.
                var execRefusal = ExecutionTargetRefusal(q, capturedLive, currentName);
                if (execRefusal != null) throw new InvalidOperationException(execRefusal);
            }
            else if (!string.IsNullOrWhiteSpace(inlineJson))
            {
                try { q = JsonSerializer.Deserialize<InterviewQuestion>(inlineJson, InterviewJson); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("inlineJson does not parse as a question. Expected a JSON object like " +
                        "{\"question\":\"What were total sales in 2024?\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(\\\"v\\\", CALCULATE([Total Sales], 'Date'[Year]=2024))\",\"expectedValue\":\"1234567.89\"}. " + ex.Message);
                }
                if (q == null) throw new InvalidOperationException("inlineJson parsed to nothing — pass a JSON object with question/tier and the tier's fields.");
                q.Tier = InterviewScoring.NormalizeTier(q.Tier);
                if (q.Tier == "refusal") q.ExpectRefusal = true;
                ValidateInterviewQuestion(q, forRun: true, hasAttempt: !string.IsNullOrWhiteSpace(attemptDax), abstained: abstained);
            }
            else
            {
                throw new InvalidOperationException("Nothing to run — pass questionId (from list_interview_questions) or inlineJson (a one-off question; free).");
            }

            // #129: every DAX this call executes is gated under the ENTRY DOOR's origin — saved or not. The earlier
            // "saved question ⇒ human" provenance rule laundered agent origin: a saved question can itself be
            // agent-authored (add_interview_question), so an agent could save a row-returning oracle and replay it
            // ungated (a deliberately mismatching oracle discloses source values in the mismatch detail). The
            // INTERNAL replays (deploy-gate advisory, the interview_replay workflow gate) are engine-initiated and
            // keep the default human origin at their own call sites; scalar oracles stay ungated by the classifier.
            // Inline one-offs are the user's explicit choice of question AND connection right now, so Gate 2 (which
            // guards SAVED packs from silently grading against the wrong connection) does not apply to them; the
            // stability check inside core still aborts a mid-run swap. Saved questions enforce Gate 2.
            bool saved = file != null;
            var r = await RunInterviewCoreAsync(q, abstained, Trimmed(attemptDax), origin, capturedSession, capturedLive, currentName, enforceExecTarget: saved);

            // Surface the same-address caveat on the SUCCESSFUL-RUN path too, not only the list Note (R3): a user who
            // runs a saved address-bound question and reads a confident verdict must also see that a same-name in-
            // place replacement is undetectable. Only on a confident outcome (Unverified already says "couldn't check").
            if (saved && IsAddressIdentity(q.ModelIdentity)
                && (r.Outcome == InterviewScoring.Correct || r.Outcome == InterviewScoring.Refused || r.Outcome == InterviewScoring.SilentlyWrong))
                r.Detail = (r.Detail ?? "") + AddressMatchCaveat;

            // Persist the outcome on a SAVED question (best-effort — a failed record must not fail the run).
            if (saved)
            {
                await _interviewGate.WaitAsync();
                try
                {
                    r.Recorded = InterviewStore.Append(file, new InterviewStore.Delta
                    {
                        Op = "record-run", Id = q.Id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent",
                        Outcome = r.Outcome, Detail = r.Detail, ResolvedFixRuleId = r.FixRuleId, ResolvedFixHint = r.FixHint,
                    });
                }
                finally { _interviewGate.Release(); }
            }

            await EmitInterviewActivity("run_interview", true,
                $"Interviewed: “{Shorten(q.Question)}” → {r.Outcome}", q.Id ?? "(inline)", origin);
            return r;
        }

        /// <summary>The tier dispatch: execute (live ops only — offline is scored as Unverified by the kernel,
        /// never a fake pass) then map the outcome + resolve the failure→fix. Shared by run_interview and the
        /// interview_replay workflow verify executor. <paramref name="execOrigin"/> is the origin the EXECUTED DAX is
        /// gated under (#129): the ENTRY DOOR's origin, always — run_interview passes it through verbatim (MCP
        /// "agent", Studio/RPC "human"), saved question or not, because a saved question can itself be agent-authored
        /// and must not launder the door. The ENGINE-initiated replays (the deploy-gate advisory, the
        /// interview_replay workflow gate) take the default "human" at their call sites, and the engine-CONSTRUCTED
        /// paraphrase oracle probe below is hardcoded ungated — neither executes caller-chosen DAX. Scalar oracles
        /// stay ungated everywhere by the classifier.</summary>
        private async Task<InterviewRunResult> RunInterviewCoreAsync(InterviewQuestion q, bool abstained, string attemptDax, string execOrigin = "human",
            Session capturedSession = null, LiveConnection capturedLive = null, string currentModelName = null, bool enforceExecTarget = false)
        {
            // GATE 2 (execution target) for BOUND questions (P1-1/#157-R1): the connection the DAX will run against
            // must be the one this question's oracle was confirmed against. Offline / refusal-tier are always allowed
            // (they never grade against live data). A failure here is Unverified — never a graded verdict.
            if (enforceExecTarget)
            {
                var witness = PaneReplacementRefusal(q, currentModelName);   // tier-independent in-place-replacement guard (disk + refusal)
                if (witness != null) return Uncheckable(q, witness);
                var er = ExecutionTargetRefusal(q, capturedLive, currentModelName);
                if (er != null) return Uncheckable(q, er);
            }
            BeforeInterviewExecuteForTest?.Invoke();   // P3-E seam: lets a test swap the session/live mid-run before the stability guard
            string outcome, detail, executed = null;
            switch (q.Tier)
            {
                case "refusal":
                    (outcome, detail) = InterviewScoring.ScoreRefusal(q, abstained, !string.IsNullOrWhiteSpace(attemptDax));
                    executed = attemptDax;
                    break;
                case "paraphrase":
                {
                    EquivalenceResult eq = null;
                    ResultSet oracleProbe = null;
                    if (capturedLive != null)
                    {
                        // TOCTOU (P1-3): the captured connection must still be the current one right before executing.
                        var instab = StabilityRefusal(capturedSession, capturedLive);
                        if (instab != null) return Uncheckable(q, instab);
                        eq = await VerifyEquivalenceAsync(q.ScalarExpr, q.ParaphraseExpr, q.GroupBy, q.Filters, 100000);
                        // Agreement is not correctness (the H16 lesson): when the two phrasings AGREE and the
                        // question pins a trusted answer, cross-check the agreed answer against that oracle — the
                        // only probe that catches two phrasings wrong the same way. Only bother when the equivalence
                        // actually PROVED agreement (so ScoreParaphrase's own oracle gate is reachable); a mismatch /
                        // truncation / zero-coverage is already Unverified-or-worse without a probe.
                        bool hasOracle = !string.IsNullOrWhiteSpace(q.ExpectedValue) || (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0);
                        // ENGINE-constructed probe (BuildParaphraseOracleQuery over the question's own fields) — not
                        // caller-authored DAX, so it deliberately runs ungated (default human origin), not execOrigin.
                        if (hasOracle && eq != null && string.IsNullOrEmpty(eq.Error) && eq.AllMatch && !eq.Truncated && eq.RowsCompared > 0)
                            oracleProbe = await RunDaxAsync(BuildParaphraseOracleQuery(q), 10000);
                    }
                    (outcome, detail) = InterviewScoring.ScoreParaphrase(q, eq, oracleProbe);
                    executed = eq?.Query;
                    break;
                }
                default: // value
                {
                    executed = attemptDax ?? q.Query;
                    ResultSet rs = null;
                    // Gated under execOrigin = the entry door's origin (see the method doc): an agent-door run of a
                    // row-returning attempt OR saved/inline oracle takes run_dax's QueryData gate (#129).
                    if (capturedLive != null)
                    {
                        var instab = StabilityRefusal(capturedSession, capturedLive);   // TOCTOU (P1-3)
                        if (instab != null) return Uncheckable(q, instab);
                        rs = await RunDaxAsync(executed, 10000, execOrigin);
                    }
                    (outcome, detail) = InterviewScoring.ScoreValue(q, rs);
                    break;
                }
            }

            string fixRuleId = null, fixHint = null;
            if (outcome == InterviewScoring.SilentlyWrong)
            {
                var (mapRule, mapHint) = InterviewFixMap.Resolve(q.Tier, outcome);
                fixRuleId = q.FixRuleId ?? mapRule;   // the author's mapping always wins; the data table is the fallback
                fixHint = mapHint;
            }

            return new InterviewRunResult
            {
                QuestionId = q.Id, Question = q.Question, Tier = q.Tier,
                Outcome = outcome, Detail = detail, FixRuleId = fixRuleId, FixHint = fixHint,
                Recorded = false, Query = executed,
            };
        }

        // ---- seeds: verified answers + the built-in hard-question pack (read-only, free) --------------------

        /// <summary>Ready-made question candidates from the two deterministic sources: the model's VERIFIED
        /// ANSWERS (parsed read-only + fail-soft from the files beside the model) and the BUILT-IN HARD-QUESTION
        /// PACK (trap-family templates bound only to shapes that exist). Emits candidates WITHOUT oracles — the
        /// trusted answer is confirmed before add_interview_question persists one (nothing is fabricated). A
        /// candidate whose question text is already saved is reported as skipped, not duplicated.</summary>
        public async Task<InterviewSeedResult> ListInterviewSeedsAsync(string source, string measure)
        {
            var src = Trimmed(source)?.ToLowerInvariant();
            bool wantVa = src == null || src.StartsWith("verified", StringComparison.Ordinal);
            bool wantPack = src == null || src == "hard-pack" || src == "hardpack" || src == "pack";
            if (!wantVa && !wantPack)
                throw new InvalidOperationException($"Unknown seed source '{source}' — pass 'verified-answers', 'hard-pack', or omit it for both.");

            var s = _sessions.Current;
            if (s == null)
                return new InterviewSeedResult
                {
                    HardPackTemplates = HardQuestionPack.Templates().Length,
                    Note = "No model is open — open_model/open_local first. Seeds come from the model's own verified answers and from binding the built-in hard-question pack to its objects.",
                };

            var candidates = new List<InterviewSeedCandidate>();
            var skipped = new List<InterviewSeedSkip>();
            var notes = new List<string>();
            int vaFound = 0;

            if (wantVa)
            {
                // Invalidate first: the files may have been authored since the last readiness scan memoized them.
                var folder = await s.ReadAsync(m => { PrepForAiReader.Invalidate(m); return PrepForAiReader.Read(m).ModelFolder; });
                if (folder == null)
                    notes.Add("verified answers live in files beside an on-disk model — this session has no readable model folder (a live/XMLA-opened model can't be inspected), so that source was not searched");
                else
                {
                    var (usable, vaSkips, found) = VerifiedAnswerSeeds.Parse(folder);
                    vaFound = found;
                    candidates.AddRange(usable);
                    skipped.AddRange(vaSkips);
                }
            }

            if (wantPack)
            {
                var shape = await s.ReadAsync(BuildPackShape);
                var (packCands, packSkips) = HardQuestionPack.Bind(shape, Trimmed(measure));   // a bad `measure` arg throws teaching
                candidates.AddRange(packCands);
                skipped.AddRange(packSkips);
            }

            // Already-saved questions are skips, not duplicates — seeding twice would double the pack silently.
            var saved = await ListInterviewQuestionsAsync(null);
            var byText = saved.Questions.GroupBy(q => q.Question?.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key)).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates.ToArray())
                if (byText.TryGetValue(c.Question?.Trim() ?? "", out var existingId))
                {
                    candidates.Remove(c);
                    skipped.Add(new InterviewSeedSkip { Source = c.Source, Id = c.Id, Family = c.Family, Reason = $"already saved as {existingId} (delete_interview_question retires it if it went stale)" });
                }

            notes.Insert(0, $"{candidates.Count} candidate(s), {skipped.Count} skipped (each with its reason)");
            notes.Add("no candidate carries a trusted answer: run its query (run_interview grades it inline for free), have someone who knows the data confirm the number, then save it with add_interview_question (expectedValue = the confirmed number, seedSource 'verified-answer' | 'hard-pack')");
            return new InterviewSeedResult
            {
                Candidates = candidates.ToArray(),
                Skipped = skipped.ToArray(),
                VerifiedAnswersFound = vaFound,
                HardPackTemplates = HardQuestionPack.Templates().Length,
                Note = string.Join(". ", notes) + ".",
            };
        }

        /// <summary>The model's SHAPES only (names, kinds, relationships) — everything the pack binder needs and
        /// nothing more, so the binder stays a pure offline-testable function.</summary>
        private static PackShape BuildPackShape(TabularEditor.TOMWrapper.Model m)
        {
            var shape = new PackShape();
            foreach (var t in m.Tables)
            {
                if (string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) shape.DateTables.Add(t.Name);
                foreach (var me in t.Measures)
                    shape.Measures.Add(new PackShape.Meas { Table = t.Name, Name = me.Name, Hidden = me.IsHidden });
                foreach (var c in t.Columns)
                {
                    if (c.Type == TabularEditor.TOMWrapper.ColumnType.RowNumber) continue;
                    var kind = c.DataType switch
                    {
                        TabularEditor.TOMWrapper.DataType.DateTime => "date",
                        TabularEditor.TOMWrapper.DataType.String => "text",
                        TabularEditor.TOMWrapper.DataType.Int64 => "number",
                        TabularEditor.TOMWrapper.DataType.Double => "number",
                        TabularEditor.TOMWrapper.DataType.Decimal => "number",
                        _ => "other",
                    };
                    shape.Columns.Add(new PackShape.Col { Table = t.Name, Name = c.Name, Kind = kind, Hidden = c.IsHidden, Key = c.IsKey });
                }
            }
            foreach (var r in m.Relationships.OfType<TabularEditor.TOMWrapper.SingleColumnRelationship>())
            {
                if (r.FromColumn == null || r.ToColumn == null) continue;
                shape.Relationships.Add(new PackShape.Rel
                {
                    FromTable = r.FromColumn.Table.Name, FromColumn = r.FromColumn.Name,
                    ToTable = r.ToColumn.Table.Name, ToColumn = r.ToColumn.Name, Active = r.IsActive,
                });
            }
            return shape;
        }

        // ---- Tests behavioral-contract replay (evidence only; T71 forbids grading integration) ------------

        internal sealed class InterviewEvidenceReplay
        {
            public InterviewEvidence[] Evidence { get; set; } = Array.Empty<InterviewEvidence>();
            public string Note { get; set; }
        }

        /// <summary>Replay the open model's saved value/paraphrase questions inside run_tests. Outcomes are current,
        /// timestamped evidence but never enter TestHealthAnalyzer; refusal questions need an assistant attempt and
        /// therefore remain explicitly chat-only. One broken/unavailable question cannot abort the rest of the suite.</summary>
        internal async Task<InterviewEvidenceReplay> ReplayInterviewEvidenceForTestsAsync(string origin)
        {
            var file = InterviewScopeFile("project");
            var (all, skipped) = InterviewStore.Materialize(file, "project");
            var (mine, other, unattributed) = PartitionByAttribution(all);
            if (mine.Count == 0)
                return new InterviewEvidenceReplay
                {
                    Note = "No saved behavioral questions belong to the open model."
                        + (other.Count + unattributed.Count > 0 ? $" {other.Count + unattributed.Count} question(s) for another or unattributed model were not replayed." : "")
                        + (skipped > 0 ? $" {skipped} unreadable interview-store line(s) were skipped." : ""),
                };

            var capturedSession = _sessions.Current;
            var capturedLive = _live;
            var currentName = capturedSession == null ? null : await ReadModelNameAsync(capturedSession);
            var evidence = new List<InterviewEvidence>();
            var right = 0; var wrong = 0; var unverified = 0; var chatOnly = 0; var changed = 0;
            foreach (var q in mine)
            {
                var previous = q.LastRun?.Outcome;
                if (q.Tier == "refusal")
                {
                    chatOnly++;
                    evidence.Add(new InterviewEvidence
                    {
                        QuestionId = q.Id, Question = q.Question, Tier = q.Tier, ReplayStatus = "chat-only",
                        PreviousOutcome = previous, Outcome = q.LastRun?.Outcome, When = q.LastRun?.When,
                        Detail = "Safe-decline questions grade an assistant response, not model DAX, so run_tests does not fabricate an answer. Ask it in Model Interview.",
                    });
                    continue;
                }

                var when = DateTime.UtcNow.ToString("o");
                InterviewRunResult run;
                try
                {
                    run = await RunInterviewCoreAsync(q, abstained: false, attemptDax: null, execOrigin: origin ?? "human",
                        capturedSession: capturedSession, capturedLive: capturedLive, currentModelName: currentName, enforceExecTarget: true);
                }
                catch (Exception ex)
                {
                    run = new InterviewRunResult { QuestionId = q.Id, Question = q.Question, Tier = q.Tier, Outcome = InterviewScoring.Unverified, Detail = "The contract could not run: " + ex.Message };
                }
                var didChange = previous != null && !string.Equals(previous, run.Outcome, StringComparison.Ordinal);
                if (didChange) changed++;
                switch (run.Outcome)
                {
                    case InterviewScoring.Correct: right++; break;
                    case InterviewScoring.SilentlyWrong: wrong++; break;
                    default: unverified++; break;
                }
                evidence.Add(new InterviewEvidence
                {
                    QuestionId = q.Id, Question = q.Question, Tier = q.Tier, ReplayStatus = "replayed",
                    Outcome = run.Outcome, PreviousOutcome = previous, Changed = didChange, Detail = run.Detail, When = when,
                });
                // Offline evidence belongs to THIS suite run, but must not overwrite the last real live observation.
                if (capturedLive != null)
                {
                    await _interviewGate.WaitAsync();
                    try
                    {
                        InterviewStore.Append(file, new InterviewStore.Delta
                        {
                            Op = "record-run", Id = q.Id, When = when, Origin = "tests",
                            Outcome = run.Outcome, Detail = run.Detail, ResolvedFixRuleId = run.FixRuleId, ResolvedFixHint = run.FixHint,
                        });
                    }
                    finally { _interviewGate.Release(); }
                }
            }

            return new InterviewEvidenceReplay
            {
                Evidence = evidence.ToArray(),
                Note = $"Behavioral contracts are evidence only: replayed {right + wrong + unverified}, right {right}, confidently wrong {wrong}, could not check {unverified}, chat-only {chatOnly}, changed since last observation {changed}."
                    + (capturedLive == null ? " Offline, so DAX contracts could not execute and prior live outcomes were not overwritten." : "")
                    + (other.Count + unattributed.Count > 0 ? $" {other.Count + unattributed.Count} question(s) for another or unattributed model were not replayed." : "")
                    + (skipped > 0 ? $" {skipped} unreadable interview-store line(s) were skipped." : ""),
            };
        }

        // ---- deploy-gate advisory leg (informs, NEVER blocks) ----------------------------------------------

        /// <summary>Replay the saved project pack for a deploy gate — ADVISORY by contract: the result never
        /// contributes to Pass/Blockers, it reports each question's outcome DELTA vs the last recorded one so
        /// a deploy sees what changed. Offline honesty is the run_interview ladder verbatim (everything comes
        /// back Unverified — a ceiling, never a fabricated pass), and offline outcomes are NOT recorded (a
        /// connectivity gap must not stomp the last real evidence on the card). Refusal-tier questions grade
        /// the assistant, not the model — counted as not-replayable, exactly like the card's "Ask all".</summary>
        internal async Task<InterviewGateAdvisory> InterviewGateAdvisoryAsync()
        {
            var file = InterviewScopeFile("project");
            var (all, _) = InterviewStore.Materialize(file, "project");
            var (live, _o, _u) = PartitionByAttribution(all);   // #157: only the OPEN model's pack is replayed
            if (live.Count == 0) return null;   // no pack for THIS model ⇒ the gate result keeps its exact pre-existing shape

            // Capture the session + live connection ONCE for the whole replay (P1-1/P1-3): grade only against the
            // connection actually attached, and read the current model name once for the in-place-replacement witness
            // (read from the session unconditionally so the witness also covers the disk-path/offline lane).
            var capturedSession = _sessions.Current;
            var capturedLive = _live;
            var currentName = capturedSession == null ? null : await ReadModelNameAsync(capturedSession);

            var adv = new InterviewGateAdvisory { Questions = live.Count };
            var changes = new List<InterviewOutcomeDelta>();
            var wrongOnes = new List<string>();
            foreach (var q in live)
            {
                if (q.Tier == "refusal") { adv.NotReplayable++; continue; }
                var r = await RunInterviewCoreAsync(q, abstained: false, attemptDax: null,
                    capturedSession: capturedSession, capturedLive: capturedLive, currentModelName: currentName, enforceExecTarget: true);
                adv.Replayed++;
                switch (r.Outcome)
                {
                    case InterviewScoring.Correct: adv.Right++; break;
                    case InterviewScoring.SilentlyWrong: adv.Wrong++; wrongOnes.Add($"“{Shorten(q.Question)}”"); break;
                    default: adv.Unverified++; break;
                }
                var before = q.LastRun?.Outcome;
                // "Changed since last asked" means exactly that: a question with NO recorded outcome is a
                // first-ever grading — its own honest bucket, never a fabricated delta (a delta needs a before).
                if (before == null) adv.NeverAsked++;
                else if (!string.Equals(before, r.Outcome, StringComparison.Ordinal))
                    changes.Add(new InterviewOutcomeDelta
                    { QuestionId = q.Id, Question = q.Question, Before = before, After = r.Outcome, Detail = r.Detail });
                if (_live != null)
                    InterviewStore.Append(file, new InterviewStore.Delta
                    {
                        Op = "record-run", Id = q.Id, When = DateTime.UtcNow.ToString("o"), Origin = "deploy-gate",
                        Outcome = r.Outcome, Detail = r.Detail, ResolvedFixRuleId = r.FixRuleId, ResolvedFixHint = r.FixHint,
                    });
            }
            adv.Changes = changes.ToArray();
            adv.Note = "Advisory only: the interview never blocks a deploy."
                + (_live == null ? " Offline, so nothing was executed and 'Unverified' is the honest ceiling (open_live/open_local for real evidence; offline outcomes are not recorded)." : "")
                + (adv.Wrong > 0 ? $" {adv.Wrong} question(s) now come back confidently wrong, e.g. {string.Join("; ", wrongOnes.Take(3))}." : "")
                + (adv.NeverAsked > 0 ? $" {adv.NeverAsked} question(s) were graded here for the first time (no previous outcome to compare against)." : "")
                + (adv.NotReplayable > 0 ? $" {adv.NotReplayable} safe-decline question(s) are graded in chat, not replayed here." : "");
            return adv;
        }

        // ---- workflow verify executor: interview_replay ----------------------------------------------------

        /// <summary>Replay the PROJECT pack as a workflow gate ("did I just break what my users ask?"). The
        /// honesty ladder, in order: an empty pack FAILS instructively (an unrunnable check must never quietly
        /// pass a hard gate — the bpa_clean missing-snapshot precedent); offline SKIPS (never blocked, never
        /// fabricated — the dax_probe precedent verbatim); any question graded SilentlyWrong FAILS the gate;
        /// otherwise it passes with the full tally disclosed (including what could NOT be checked).</summary>
        private async Task<VerifyResult> WorkflowInterviewReplayAsync(VerifySpec spec)
        {
            const string kind = "interview_replay";
            var file = InterviewScopeFile("project");
            var (all, _) = InterviewStore.Materialize(file, "project");
            var (live, other, unattr) = PartitionByAttribution(all);   // #157: replay only the OPEN model's pack
            if (live.Count == 0)
            {
                var stray = other.Count + unattr.Count;
                var strayNote = stray > 0
                    ? $" ({stray} saved question(s) belong to a different model or are unattributed and were NOT replayed here; they were authored against another schema.)"
                    : "";
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no saved interview questions to replay for the open model. Save some with add_interview_question (the /interview-model skill authors them) before gating on interview_replay." + strayNote };
            }
            var capturedSession = _sessions.Current;
            var capturedLive = _live;
            if (capturedLive == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "offline — no live connection, the interview was NOT replayed (open_live/open_local and re-submit for real evidence)." };
            var currentName = await ReadModelNameAsync(capturedSession);

            int right = 0, wrong = 0, unverified = 0, refused = 0;
            var wrongOnes = new List<string>();
            var unverifiedOnes = new List<string>();
            foreach (var q in live)
            {
                // Refusal-tier questions need an authored attempt (an agent in the loop) — a replay has none, so
                // they come back Unverified from the kernel with the teaching detail; counted, never guessed.
                var r = await RunInterviewCoreAsync(q, abstained: false, attemptDax: null,
                    capturedSession: capturedSession, capturedLive: capturedLive, currentModelName: currentName, enforceExecTarget: true);
                InterviewStore.Append(file, new InterviewStore.Delta
                {
                    Op = "record-run", Id = q.Id, When = DateTime.UtcNow.ToString("o"), Origin = "workflow",
                    Outcome = r.Outcome, Detail = r.Detail, ResolvedFixRuleId = r.FixRuleId, ResolvedFixHint = r.FixHint,
                });
                switch (r.Outcome)
                {
                    case InterviewScoring.Correct: right++; break;
                    case InterviewScoring.Refused: refused++; break;
                    case InterviewScoring.SilentlyWrong: wrong++; wrongOnes.Add($"“{Shorten(q.Question)}”" + (r.FixRuleId != null ? $" (fix: {r.FixRuleId})" : "")); break;
                    default: unverified++; unverifiedOnes.Add($"“{Shorten(q.Question)}”"); break;
                }
            }

            if (wrong > 0)
                return new VerifyResult
                {
                    Kind = kind, Status = "failed",
                    Detail = $"{wrong}/{live.Count} interview question(s) now come back confidently WRONG — e.g. {string.Join("; ", wrongOnes.Take(3))}. Fix the model (or the mapped readiness finding) and re-submit.",
                };
            var extras = new List<string>();
            if (refused > 0) extras.Add($"{refused} safely declined");
            if (unverified > 0) extras.Add($"{unverified} could not be checked ({string.Join("; ", unverifiedOnes.Take(3))})");
            return new VerifyResult
            {
                Kind = kind, Status = "passed",
                Detail = $"replayed {live.Count} interview question(s): {right} right" + (extras.Count > 0 ? ", " + string.Join(", ", extras) : "") + ".",
            };
        }

        // ---- helpers --------------------------------------------------------------------------------------

        /// <summary>Fail-loud per-tier validation so a saved/inline question is always runnable. For a RUN of a
        /// refusal question the DAX attempt may arrive via attemptDax/abstained instead of the record itself.</summary>
        private static void ValidateInterviewQuestion(InterviewQuestion q, bool forRun = false, bool hasAttempt = false, bool abstained = false)
        {
            if (string.IsNullOrWhiteSpace(q.Question))
                throw new InvalidOperationException("A question needs its natural-language text — that IS the artifact (e.g. \"What were total sales in 2024?\").");
            switch (q.Tier)
            {
                case "value":
                    if (string.IsNullOrWhiteSpace(q.Query) && !(forRun && hasAttempt))
                        throw new InvalidOperationException("A value-tier question needs `query` — the full EVALUATE DAX that answers it (e.g. EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year]=2024))). Scalar expressions belong to the paraphrase tier (GAP C: the shapes are not interchangeable).");
                    // The oracle is required to PERSIST (a saved question must always grade as a regression
                    // check), but an inline RUN without one IS the confirm-and-record flow list_interview_seeds
                    // advertises: it executes and comes back Unverified with the computed number in the detail —
                    // never a pass, and never a refusal of the tool's own guidance.
                    if (!forRun && string.IsNullOrWhiteSpace(q.ExpectedValue) && (q.ExpectedMatrix == null || q.ExpectedMatrix.Length == 0))
                        throw new InvalidOperationException("A value-tier question needs its trusted answer — `expectedValue` (a single number/text) or `expectedMatrix` (rows) confirmed by the user or a verified answer. Without an oracle the engine can only ever say \"couldn't check\" (run_interview an inline no-oracle question first to see the computed number, then record the confirmed value here).");
                    break;
                case "paraphrase":
                    if (string.IsNullOrWhiteSpace(q.ScalarExpr) || string.IsNullOrWhiteSpace(q.ParaphraseExpr))
                        throw new InvalidOperationException("A paraphrase-tier question needs BOTH `scalarExpr` and `paraphraseExpr` — the same question answered two ways as scalar DAX expressions (full EVALUATE queries belong to the value tier).");
                    break;
                case "refusal":
                    if (!q.ExpectRefusal)
                        throw new InvalidOperationException("A refusal-tier question must set expectRefusal=true — it asserts the model CANNOT answer it.");
                    if (forRun && !hasAttempt && !abstained)
                    {
                        // Allowed: it scores Unverified with a teaching detail. Nothing to validate.
                    }
                    break;
            }
        }

        private static string[][] ParseExpectedMatrix(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var m = JsonSerializer.Deserialize<string[][]>(json, InterviewJson);
                return m != null && m.Length > 0 ? m : null;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("expectedMatrixJson must be a JSON array of rows, each an array of cell values as strings — e.g. [[\"2023\",\"1200.5\"],[\"2024\",\"1310.0\"]]. " + ex.Message);
            }
        }

        /// <summary>The trusted-answer cross-check query for a paraphrase question that pins an oracle: the AGREED
        /// answer (scalarExpr — the equivalence already proved paraphraseExpr computes the same value) evaluated for
        /// the value-tier comparer. A matrix oracle is checked per-context across the group-by grid; a single value
        /// anchors the grand total. Filters ride both shapes so the check runs in the question's own context.</summary>
        private static string BuildParaphraseOracleQuery(InterviewQuestion q)
        {
            var expr = "(\n" + (q.ScalarExpr ?? "").Trim() + "\n)";
            var groupBy = (q.GroupBy ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();
            var filters = (q.Filters ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToArray();

            if (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0 && groupBy.Length > 0)
            {
                // SUMMARIZECOLUMNS(group cols, filter args, "v", expr) → columns [group…, v], the exact shape
                // ScoreValue's matrix path compares against expectedMatrix (order-insensitive, per the house oracle).
                var args = new List<string>();
                args.AddRange(groupBy);
                args.AddRange(filters);
                args.Add("\"v\", " + expr);
                return "EVALUATE\nSUMMARIZECOLUMNS(\n    " + string.Join(",\n    ", args) + "\n)";
            }
            // Grand-total anchor for a single trusted value; filters (if any) restore the context via CALCULATE.
            var body = filters.Length > 0 ? "CALCULATE(" + expr + ", " + string.Join(", ", filters) + ")" : expr;
            return "EVALUATE ROW(\"v\", " + body + ")";
        }

        private static string Trimmed(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string Shorten(string s) => s == null ? "" : s.Length <= 80 ? s : s.Substring(0, 77) + "…";

        private Task EmitInterviewActivity(string kind, bool ok, string label, string target, string origin)
            => PublishActivityAsync(new ActivityEvent { Kind = kind, Origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin, Label = label, Target = target, Ok = ok });
    }
}
