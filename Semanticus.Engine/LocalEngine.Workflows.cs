using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Pro-mode workflow ops (docs/pro-mode-spec.md §4/§5) — dual-drive like everything else.
    /// Reading the library is FREE (the funnel); STARTING a workflow with any enforced gate is the
    /// one Pro chokepoint, so both doors inherit the gate. Definitions are re-read from disk on
    /// every call (hot-editable); a run is session-held state (never persisted to the model), and
    /// its terminal record rides the Activity bus into the experience log so the audit of a run
    /// does not die with the session.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private readonly System.Threading.SemaphoreSlim _workflowGate = new System.Threading.SemaphoreSlim(1, 1); // serialize run mutations across both doors
        private readonly WorkflowRunStore _workflowRuns = new WorkflowRunStore();
        // §10.6 [D2]: the last readiness grade computed THIS session (set by AiReadinessScan*), so the
        // `model.readinessGrade` activation fact is lazy+cached and NEVER force-scanned on a menu/policy read.
        // null = never scanned ⇒ the term is "unknown" ⇒ grade comparisons are false (the rule stays dormant).
        private volatile string _lastReadinessGrade;
        // Start-of-run scan snapshots for the *_rescan/_clean verifies (keyed by runId; under _workflowGate).
        private readonly Dictionary<string, WorkflowRunAux> _workflowAux = new Dictionary<string, WorkflowRunAux>(StringComparer.Ordinal);

        private sealed class WorkflowRunAux
        {
            public HashSet<string> BpaKeys;            // RuleId|ObjectRef of ACTIVE violations at start (null = no bpa_clean gate)
            public HashSet<string> ReadinessKeys;      // ditto for readiness findings
            public double ReadinessOverall;
        }

        // ---- library ---------------------------------------------------------------------------

        /// <summary>User workflows live in the project's `.semanticus/workflows` (beside the model;
        /// workspace fallback for live/unsaved sessions — the experience-log placement rule); the
        /// stock library ships read-only beside the engine binary. A user file SHADOWS a stock one
        /// of the same name (copy-to-customise).</summary>
        /// <summary>The project's `.semanticus` sidecar dir (beside the model; PBIP hop included), or the raw
        /// workspace fallback for a live/unsaved session — the shared root for both the workflows and the
        /// workflow-templates dirs. null when there is nowhere to persist (no model, no workspace).</summary>
        private string SidecarDir()
        {
            var anchor = _sessions.Current?.SourcePath;
            // LayoutStore.DirFor already RETURNS the `.semanticus` sidecar dir (PBIP hop included) — only the
            // raw-workspace fallback needs the segment appended (the McpSmoke test-agent caught the doubling).
            return !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                 : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
        }

        private (string userDir, string stockDir) WorkflowDirs()
        {
            var sidecar = SidecarDir();
            return (sidecar == null ? null : Path.Combine(sidecar, "workflows"),
                    Path.Combine(AppContext.BaseDirectory, "workflows"));
        }

        private string WorkflowSettingsFile()
        {
            var (userDir, _) = WorkflowDirs();
            return userDir == null ? null : Path.Combine(Path.GetDirectoryName(userDir), "workflow-settings.json");
        }

        private readonly object _settingsFileLock = new object();   // serialize workflow-settings read-modify-write across both doors

        /// <summary>Read workflow-settings.json for the READ side with the SAME strict decode the write side uses.
        /// File.ReadAllText decodes with REPLACEMENT fallback, so a malformed file carrying a valid-looking
        /// "strictness":"off" beside an invalid byte sequence would read as HEALTHY — and the corruption would then
        /// CONTROL the very enforcement the fail-closed posture exists to protect. A DecoderFallbackException here
        /// means CORRUPT: it surfaces through each reader's existing catch (display readers degrade to defaults;
        /// WorkflowSettingsCorrupt reports true, so bindable ops refuse and enforcement stays on).</summary>
        private static string ReadSettingsStrict(string file) => DecodeSettingsStrict(File.ReadAllBytes(file));

        /// <summary>True when workflow-settings.json is PRESENT but cannot be strictly decoded + parsed as a JSON
        /// object. ENFORCEMENT reads fail CLOSED on this (a corrupt file must never silently drop a mandated binding —
        /// corrupting the JSON would otherwise BYPASS the mandate); DISPLAY reads still degrade to defaults. A MISSING
        /// file is not corrupt — that's the normal no-settings default. Mirrors the shipped AgentPolicyStore
        /// fail-closed posture. Judged with the strict decode (ReadSettingsStrict), so what the WRITE side would
        /// preserve aside as corrupt is never simultaneously trusted by the read side.</summary>
        private bool WorkflowSettingsCorrupt()
        {
            var file = WorkflowSettingsFile();
            if (file == null || !File.Exists(file)) return false;
            try { using var doc = JsonDocument.Parse(ReadSettingsStrict(file)); return doc.RootElement.ValueKind != JsonValueKind.Object; }
            catch { return true; }
        }

        /// <summary>Read-modify-write workflow-settings.json under a process lock with an ATOMIC temp+move write — so
        /// two concurrent writers (both doors) can't lose each other's update and a reader never sees a half-written
        /// file. A PRESENT-but-corrupt file has its raw bytes preserved to a `.corrupt-&lt;hex&gt;` sibling BEFORE the
        /// fresh write (never silently clobbered — the old per-method catch reset the file to empty, losing every
        /// binding/override); if that preservation fails the mutation is ABORTED (preserve-over-progress). Project-
        /// scoped, so an in-process lock + atomic write is the right instrument; unifying with the cross-process
        /// Policy/HomeFile helper is a follow-up once both branches merge.</summary>
        private void MutateWorkflowSettings(string file, Action<System.Text.Json.Nodes.JsonObject> mutate)
        {
            lock (_settingsFileLock)
            {
                System.Text.Json.Nodes.JsonObject root;
                if (File.Exists(file))
                {
                    // BYTES first: the aside must preserve the EXACT original — re-encoding through a string
                    // (ReadAllText→WriteAllText) transcodes a BOM/UTF-16/mangled file, defeating the preservation.
                    // Parsing uses a separately DECODED copy via the SAME strict decoder the read side uses
                    // (ReadSettingsStrict), so a valid UTF-16/BOM'd settings file still parses instead of being
                    // mis-flagged corrupt, and read and write can never disagree about what corruption is.
                    var bytes = File.ReadAllBytes(file);
                    try
                    {
                        // STRICT decode: the default replacement fallback turns an invalid byte INSIDE a JSON string
                        // into U+FFFD, so a file that is syntactically valid JSON except for one bad byte would parse
                        // CLEAN and the rewrite would destroy the original bytes with no aside. DecodeSettingsStrict
                        // sniffs the BOM ITSELF and decodes the remainder with a THROWING instance of the matching
                        // encoding — a bad byte surfaces as a DecoderFallbackException, handled below exactly like a
                        // parse failure (preserve aside, fresh root). A valid UTF-16/BOM'd settings file (PowerShell
                        // 5's Out-File default) still decodes via its own encoding.
                        // WONTFIX (§9.10B): that valid UTF-16/BOM file intentionally normalizes to UTF-8 on this next
                        // settings WRITE — its content is preserved (we re-serialize the parsed JSON), the file is OURS
                        // (the designer writes it via these ops), and encoding-preservation machinery is deliberately
                        // not built.
                        var raw = DecodeSettingsStrict(bytes);
                        root = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject ?? throw new FormatException("not a JSON object");
                    }
                    // ONLY the three "this file is unreadable" shapes route to the corrupt-aside path: a strict-decode
                    // failure (bad byte), malformed JSON, and the deliberate non-object FormatException above. Anything
                    // else (OOM, a transient runtime/resource failure) PROPAGATES and the mutation aborts unchanged —
                    // a transient failure must never classify a VALID settings file as corrupt and replace it.
                    catch (Exception e) when (e is System.Text.DecoderFallbackException or System.Text.Json.JsonException or FormatException)
                    {
                        try { WriteCorruptAside(file, bytes); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"workflow-settings.json is unreadable and its contents could not be preserved to a '.corrupt-*' sibling ({ex.Message}) — refusing to overwrite it. Repair or move the file, then retry.");
                        }
                        root = new System.Text.Json.Nodes.JsonObject();   // fresh truth; the old bytes are safe in the aside sibling
                    }
                }
                else root = new System.Text.Json.Nodes.JsonObject();

                mutate(root);

                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                try { File.Move(tmp, file, overwrite: true); }
                catch { try { File.Delete(tmp); } catch { } throw; }
            }
        }

        /// <summary>Strict BOM-aware decode for the settings bytes. We sniff the BOM OURSELVES: handing a throwing
        /// encoding to StreamReader with detectEncodingFromByteOrderMarks:true is a trap — when the reader sees a
        /// UTF-16/UTF-32 BOM it SWAPS IN framework encoding instances with REPLACEMENT fallback, silently defeating
        /// the strict decode for exactly the file family the BOM path exists for. UTF-32LE is checked BEFORE UTF-16LE
        /// (its FF FE 00 00 preamble starts with UTF-16LE's FF FE). The preamble is skipped and the remainder decoded
        /// with a THROWING instance of the matching encoding, so any invalid sequence surfaces as a
        /// DecoderFallbackException the caller treats as corruption. No BOM ⇒ strict UTF-8.</summary>
        private static string DecodeSettingsStrict(byte[] bytes)
        {
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(bytes, 4, bytes.Length - 4);
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return new System.Text.UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true).GetString(bytes, 4, bytes.Length - 4);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }

        /// <summary>Write the corrupt file's exact bytes to a fresh `.corrupt-&lt;8hex&gt;` sibling. CreateNew + a new
        /// name per retry: an existing sibling must never be overwritten (a name collision would clobber a PREVIOUS
        /// backup). Non-collision failures (disk full, access denied) propagate so the caller can abort the mutation.</summary>
        private static void WriteCorruptAside(string file, byte[] bytes)
        {
            for (var attempt = 0; ; attempt++)
            {
                var aside = file + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                // Separate the OPEN from the WRITE. Only a CreateNew that fails because the NAME is already taken (the
                // file existed before our attempt) is a benign collision to retry. The old combined catch keyed on
                // File.Exists(aside): if the open SUCCEEDED and the WRITE then failed (disk full), File.Exists is true
                // for the file WE just created, so it misread that as a collision and retried — leaving partial
                // backups. Now a write failure deletes the partial aside (best-effort) and propagates.
                FileStream fs;
                try { fs = new FileStream(aside, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (attempt < 4 && File.Exists(aside)) { continue; }   // name collision — roll a new name
                try
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Dispose();   // flush inside the try: a failed flush is a failed backup, same as a failed write
                    return;
                }
                catch (Exception writeEx)
                {
                    // Dispose inside its OWN guard: on disk exhaustion the flush in Dispose can throw too, and an
                    // exception thrown while unwinding a using-block REPLACES the original — losing the root cause.
                    // Rethrow the ORIGINAL with its stack intact so the caller aborts on the real failure.
                    try { fs.Dispose(); } catch { }
                    try { File.Delete(aside); } catch { }   // best-effort: never leave a partial/empty backup behind
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeEx).Throw();
                    throw;   // unreachable (Throw never returns) — keeps the compiler's flow analysis satisfied
                }
            }
        }

        /// <summary>Per-workflow strictness override from `.semanticus/workflow-settings.json`
        /// (hot-read like the definitions; the designer writes this file). Shape:
        /// { "strictness": "off", "workflows": { "new-measure": { "strictness": "warn" } } } —
        /// the top-level "strictness" is the MODEL-WIDE enforcement override (set_workflow_enforcement).</summary>
        private string SettingsStrictnessFor(string name)
        {
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("workflows", out var wfs)
                    && wfs.TryGetProperty(name, out var wf)
                    && wf.TryGetProperty("strictness", out var s)
                    && s.ValueKind == JsonValueKind.String)
                {
                    var v = s.GetString();
                    return v == "hard" || v == "warn" || v == "off" ? v : null;
                }
            }
            catch { /* a malformed settings file must not brick the library; the def's own default applies */ }
            return null;
        }

        /// <summary>§9a availability toggle: per-workflow `enabled` in `.semanticus/workflow-settings.json`
        /// (`workflows.&lt;name&gt;.enabled`). Absent or malformed ⇒ AVAILABLE (the safe default, §9.3). A disabled
        /// workflow stays IN the library listing (marked `enabled:false` so the designer can re-enable it) but
        /// `start_workflow` refuses it. Independent of strictness/binding — three orthogonal axes.</summary>
        private bool SettingsEnabledFor(string name)
        {
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return true;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("workflows", out var wfs)
                    && wfs.TryGetProperty(name, out var wf)
                    && wf.TryGetProperty("enabled", out var e)
                    && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                    return e.GetBoolean();
            }
            catch { /* a malformed settings file must not hide the library — default AVAILABLE */ }
            return true;
        }

        /// <summary>The model-wide enforcement override (the toggle): top-level "strictness" in the same
        /// settings file. When set it tops the whole resolution — see WorkflowRunner.EffectiveStrictness.</summary>
        private string GlobalStrictness()
        {
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("strictness", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    var v = s.GetString();
                    return v == "hard" || v == "warn" || v == "off" ? v : null;
                }
            }
            catch { /* malformed file → no override; the definitions decide */ }
            return null;
        }

        // ---- §9c op→workflow binding (mandatory routing) -----------------------------------------
        // The third orthogonal axis (enabled · binding · strictness, §9.1): a binding routes a bare authoring
        // op through a required workflow. Read from the same git-tracked settings file, hot off disk like the
        // others. v1 evaluates the FLAT require-set (§9.11's `when:` conditional bindings — where bindings.<op>
        // becomes an ARRAY of when/require rules — are a later slice; this engine treats that array as "no
        // binding I can evaluate" and fails safe rather than guessing). Independent of strictness (§9.10B).

        /// <summary>A parsed op→workflow binding. UserDisablable defaults true; false (§9.10C) marks a committed
        /// team mandate a contributor cannot quietly turn off from the agent door.</summary>
        private sealed class WorkflowBinding
        {
            public string[] Require = Array.Empty<string>();
            public string Mode;                 // "hard" | "warn" | "off" | null — only hard/warn enforce
            public bool UserDisablable = true;
        }

        /// <summary>Parse ONE bindings.&lt;op&gt; entry. Only the v1 OBJECT form {require:[…], mode, userDisablable?}
        /// is understood; anything else (a §9.11 conditional ARRAY, a scalar, junk) returns null so the caller
        /// fails safe — an unparsed binding never silently enforces.</summary>
        private static WorkflowBinding ParseBinding(JsonElement b)
        {
            if (b.ValueKind != JsonValueKind.Object) return null;   // §9.11 array form is a later slice — not evaluated here
            var require = b.TryGetProperty("require", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                   .Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                : Array.Empty<string>();
            var mode = b.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()?.Trim().ToLowerInvariant() : null;
            // userDisablable:false ONLY when the key is literally the JSON false; absent/malformed ⇒ true (the safe default).
            var userDisablable = !(b.TryGetProperty("userDisablable", out var u) && u.ValueKind == JsonValueKind.False);
            return new WorkflowBinding { Require = require, Mode = mode, UserDisablable = userDisablable };
        }

        /// <summary>The binding for one op (null when none / malformed / unevaluatable — fail-safe, same
        /// try/catch-and-continue discipline as SettingsEnabledFor: a broken settings file must never brick an
        /// authoring op).</summary>
        private WorkflowBinding SettingsBindingFor(string op)
        {
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("bindings", out var bindings)
                    && bindings.ValueKind == JsonValueKind.Object
                    && bindings.TryGetProperty(op, out var b))
                    return ParseBinding(b);
            }
            catch { /* a malformed settings file must not brick an authoring op — no binding */ }
            return null;
        }

        /// <summary>Every ENFORCED (hard/warn) binding in the file, for the policy view (§9.12). Fail-safe: a
        /// malformed file yields an empty set, never an exception.</summary>
        private List<(string Op, WorkflowBinding Binding)> AllSettingsBindings()
        {
            var list = new List<(string, WorkflowBinding)>();
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return list;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
                    foreach (var p in bindings.EnumerateObject())
                    {
                        var b = ParseBinding(p.Value);
                        if (b != null && (b.Mode == "hard" || b.Mode == "warn") && b.Require.Length > 0)
                            list.Add((p.Name, b));
                    }
            }
            catch { /* malformed → no bindings surfaced */ }
            return list;
        }

        // ---- §9.11 conditional (array-form) bindings [D6] ---------------------------------------
        // §10.6:632 mandates ONE predicate grammar shared by bindings AND activation. ParseBinding above still
        // returns null for the array form (so the flat path + all WorkflowBindingTests are untouched); the array
        // form is handled HERE via the shared WorkflowPredicate. v1 supplies CONTEXT facts only (model/connection/
        // git/session/date) — target.*/workflow.active are deferred to 10-T4, so a rule referencing them never
        // matches (evaluates false) + a policy lint surfaces it (never a silent enforce).

        private sealed class BindingRule
        {
            public PredicateExpr When;   // null = the unconditional fallback rule (§9.11:389)
            public string WhenError;     // parse/typing problem — the rule never matches + lint
            public string RawWhen;
            public string[] Require = Array.Empty<string>();
            public string Mode;          // hard | warn | off | null
            public bool UserDisablable = true;
        }

        /// <summary>Parse the §9.11 ARRAY form of one bindings.&lt;op&gt; entry into ordered when/require rules.
        /// Mirrors ParseBinding's field handling; each rule's `when:` goes through the shared evaluator.</summary>
        private static List<BindingRule> ParseBindingRules(JsonElement arr)
        {
            var rules = new List<BindingRule>();
            if (arr.ValueKind != JsonValueKind.Array) return rules;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var r = new BindingRule();
                r.RawWhen = e.TryGetProperty("when", out var wh) && wh.ValueKind == JsonValueKind.String ? wh.GetString() : null;
                r.When = WorkflowPredicate.Parse(r.RawWhen, out var perr);
                r.WhenError = perr;
                r.Require = e.TryGetProperty("require", out var rq) && rq.ValueKind == JsonValueKind.Array
                    ? rq.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())
                       .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                    : Array.Empty<string>();
                r.Mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()?.Trim().ToLowerInvariant() : null;
                r.UserDisablable = !(e.TryGetProperty("userDisablable", out var u) && u.ValueKind == JsonValueKind.False);
                rules.Add(r);
            }
            return rules;
        }

        /// <summary>Every op whose binding is the §9.11 ARRAY form, with its parsed rules. Fail-safe (a malformed
        /// file yields an empty set). The object (flat) form is handled by ParseBinding/AllSettingsBindings.</summary>
        private List<(string Op, List<BindingRule> Rules)> AllArrayBindings()
        {
            var list = new List<(string, List<BindingRule>)>();
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return list;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (doc.RootElement.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
                    foreach (var p in bindings.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Array)
                            list.Add((p.Name, ParseBindingRules(p.Value)));
            }
            catch { /* malformed → no array bindings */ }
            return list;
        }

        /// <summary>The §9c binding for one op, D6-aware. OBJECT form ⇒ the flat rule (unconditional, as shipped —
        /// no facts gathered). ARRAY form ⇒ the FIRST rule whose `when:` holds against context facts (a rule
        /// referencing a deferred/unknown fact never matches). Returns the same WorkflowBinding the enforcement
        /// path already consumes, so EnforceBindingAsync is unchanged beyond receiving a possibly-conditional one.</summary>
        private async Task<WorkflowBinding> ResolveEnforcedBindingAsync(string op)
        {
            List<BindingRule> rules = null;
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (!doc.RootElement.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Object
                    || !bindings.TryGetProperty(op, out var b)) return null;
                if (b.ValueKind == JsonValueKind.Object) return ParseBinding(b);   // flat form — no facts needed
                if (b.ValueKind != JsonValueKind.Array) return null;
                rules = ParseBindingRules(b);
            }
            catch { return null; }   // malformed → no binding (fail-safe, same discipline as SettingsBindingFor)
            if (rules == null || rules.Count == 0) return null;

            var facts = await GatherFactsForAsync(rules.Select(r => r.When));
            foreach (var r in rules)
            {
                if (r.WhenError != null) continue;   // a rule referencing an unavailable/misspelled fact never matches
                if (r.When == null || WorkflowPredicate.Evaluate(r.When, facts))
                    return new WorkflowBinding { Require = r.Require, Mode = r.Mode, UserDisablable = r.UserDisablable };
            }
            return null;   // no rule matched → no binding for this op right now
        }

        // ---- §10.6 dynamic activation -----------------------------------------------------------
        // Activation CURATES the menu (which workflows are on offer given today's date / connection / branch /
        // readiness / …) via a top-level `activation:` array in workflow-settings.json — read hot off disk like
        // strictness/enabled/bindings, fail-safe (malformed ⇒ no rules ⇒ every workflow active). It is NOT a lock:
        // a rule-deactivated workflow is still startable on demand (D4); only the manual `enabled:false` kill-switch
        // hard-refuses. Reading activation is FREE; only WRITING a rule (set_workflow_activation) is Pro (§10.7).

        private sealed class ActivationRule
        {
            public string Workflow;      // selector: a workflow name  (exactly one of Workflow/Tag)
            public string Tag;           // selector: a workflow tag
            public PredicateExpr When;   // null = unconditional
            public string WhenError;     // parse/typing problem (lint) — an erroring rule never fires
            public string RawWhen;
            public bool On;              // set: on/off (only when Valid)
            public string Reason;        // optional author-supplied PLAIN reason (never a predicate echo)
            public bool Valid;           // false = missing/ambiguous selector or bad set (inert + lint)
            public string InvalidReason;
        }

        /// <summary>Every `activation:` rule from the settings file, in file order. Fail-safe (E9): a malformed
        /// block yields NO rules so every workflow stays active — a broken settings file never bricks the menu.</summary>
        private List<ActivationRule> AllActivationRules()
        {
            var list = new List<ActivationRule>();
            try
            {
                var file = WorkflowSettingsFile();
                if (file == null || !File.Exists(file)) return list;
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));   // strict decode — see ReadSettingsStrict
                if (!doc.RootElement.TryGetProperty("activation", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in arr.EnumerateArray())
                {
                    var rule = new ActivationRule();
                    if (e.ValueKind != JsonValueKind.Object) { rule.InvalidReason = "an activation entry is not an object — skipped."; list.Add(rule); continue; }
                    rule.Workflow = e.TryGetProperty("workflow", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                    rule.Tag = e.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    rule.RawWhen = e.TryGetProperty("when", out var wh) && wh.ValueKind == JsonValueKind.String ? wh.GetString() : null;
                    rule.Reason = e.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null;
                    var set = e.TryGetProperty("set", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()?.Trim().ToLowerInvariant() : null;
                    rule.When = WorkflowPredicate.Parse(rule.RawWhen, out var perr);
                    rule.WhenError = perr;

                    var hasWf = !string.IsNullOrWhiteSpace(rule.Workflow);
                    var hasTag = !string.IsNullOrWhiteSpace(rule.Tag);
                    if (hasWf == hasTag) rule.InvalidReason = "an activation rule needs exactly one of workflow: or tag: — skipped.";
                    else if (set != "on" && set != "off") rule.InvalidReason = $"an activation rule's set must be 'on' or 'off' (got '{set ?? "missing"}') — skipped.";
                    else { rule.Valid = true; rule.On = set == "on"; }
                    list.Add(rule);
                }
            }
            catch { /* malformed activation block → fail-safe: no rules, every workflow active (E9) */ return new List<ActivationRule>(); }
            return list;
        }

        private static bool RuleSelects(ActivationRule r, WorkflowDef def) =>
            r.Workflow != null ? string.Equals(r.Workflow, def.Name, StringComparison.Ordinal)
            : r.Tag != null && def.Tags.Contains(r.Tag, StringComparer.OrdinalIgnoreCase);

        /// <summary>The workspace derived from the live endpoint (powerbi://…/myorg/&lt;Workspace&gt; ⇒ its last
        /// segment). null for local/offline (there is no workspace) — a `connection.workspace ~ …` rule then
        /// never fires. The endpoint is the live session's DataSource (ConnectionStatus.DataSource).</summary>
        private static string DeriveWorkspace(ConnectionStatus cs)
        {
            if (cs == null || !cs.Connected || cs.Kind != "xmla" || string.IsNullOrWhiteSpace(cs.DataSource)) return null;
            var ds = cs.DataSource.TrimEnd('/');
            var slash = ds.LastIndexOf('/');
            return slash >= 0 && slash < ds.Length - 1 ? ds.Substring(slash + 1) : null;
        }

        /// <summary>Gather ONLY the fact roots the given predicates reference (§2.5, the perf contract): zero
        /// predicates ⇒ empty root set ⇒ NO model read, NO git spawn, NO scan — the common path stays exactly as
        /// cheap as before. One snapshot is shared across all workflows in a pass.</summary>
        private async Task<PredicateFacts> GatherFactsForAsync(IEnumerable<PredicateExpr> exprs)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in exprs)
                if (e != null)
                    foreach (var r in WorkflowPredicate.ReferencedRoots(e)) roots.Add(r);
            return await GatherFactsAsync(roots);
        }

        private async Task<PredicateFacts> GatherFactsAsync(IReadOnlyCollection<string> roots)
        {
            var f = new PredicateFacts();
            if (roots.Count == 0) return f;   // nothing referenced → gather nothing

            if (roots.Contains("model") && _sessions.Current != null)
            {
                var s = _sessions.Current;
                f.Model = await s.ReadAsync(m => new ModelFacts
                {
                    TableCount = m.Tables.Count,
                    MeasureCount = m.AllMeasures.Count(),
                    HasRls = m.Roles.Any(r => r.TablePermissions.Any(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression))),
                    HasCalcGroups = m.Tables.OfType<CalculationGroupTable>().Any(),
                    CompatLevel = m.Database?.CompatibilityLevel ?? 0,
                    StorageMode = DirectLakeInfo.IsModelDirectLake(m) ? "directlake" : m.DefaultMode.ToString().ToLowerInvariant(),
                    Fingerprint = KnowledgeStore.ComputeFingerprint(m).FingerprintKey,
                });
                f.Model.ReadinessGrade = _lastReadinessGrade;   // cached (D2) — may be null (unknown), never force-scanned
            }
            if (roots.Contains("connection"))
            {
                var cs = await ConnectionStatusAsync();
                f.Connection = new ConnectionFacts
                {
                    Kind = cs.Connected ? cs.Kind : "offline",   // ConnectionStatus never emits "offline" — synthesise it (review A3)
                    Database = cs.DataSource,
                    Workspace = DeriveWorkspace(cs),
                };
            }
            if (roots.Contains("git"))
            {
                // git.* SHELLS OUT — gathered only because a rule referenced it. Never a throw (git absent ⇒ unknown).
                try { var g = await GitStatusAsync(); f.Git = new GitFacts { Branch = g.Branch, Dirty = g.ModelDirty }; }
                catch { f.Git = null; }
            }
            if (roots.Contains("session"))
                f.Session = new SessionFacts
                {
                    Tier = _entitlement?.Info?.Tier ?? "free",
                    VerifiedMode = _verifiedMode ? "on" : "off",
                    PlanLoaded = _plans.Current != null,
                };
            if (roots.Contains("date"))
            {
                var now = DateTime.UtcNow;
                f.Date = new DateFacts
                {
                    Iso = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    DayOfMonth = now.Day,
                    MonthEndOffset = now.Day - DateTime.DaysInMonth(now.Year, now.Month),   // 0 on the last day, -1 the day before
                };
            }
            return f;
        }

        /// <summary>Every ENFORCED (hard/warn, non-empty require) binding given the current facts: flat bindings
        /// always apply; each array binding contributes its first matching rule. Drives the activation resolver's
        /// force-active check + the policy inversion.</summary>
        private List<(string Op, WorkflowBinding Binding)> ComputeEnforcedBindings(PredicateFacts facts, List<(string Op, List<BindingRule> Rules)> arrayBindings)
        {
            var enforced = AllSettingsBindings();   // flat, already filtered to hard/warn + non-empty require
            foreach (var (op, ruleList) in arrayBindings)
                foreach (var r in ruleList)
                {
                    if (r.WhenError != null) continue;
                    if (r.When == null || WorkflowPredicate.Evaluate(r.When, facts))
                    {
                        if ((r.Mode == "hard" || r.Mode == "warn") && r.Require.Length > 0)
                            enforced.Add((op, new WorkflowBinding { Require = r.Require, Mode = r.Mode, UserDisablable = r.UserDisablable }));
                        break;   // first matching rule wins
                    }
                }
            return enforced;
        }

        /// <summary>Resolve ONE workflow's activation, TOTAL precedence (§2.4 / D1): (1) a matching enforced binding
        /// force-activates it (beats a manual disable — the deadlock breaker, E1/E3); (2) a manual `enabled:false`
        /// wins over any rule (E2); (3) the first file-order activation rule that selects it and whose `when:` holds;
        /// (4) default-on with no reason (zero-config invisibility). The reason is ALWAYS plain language, never a
        /// predicate echo (§10.12).</summary>
        private (bool active, string reason) ResolveActivation(WorkflowDef def, PredicateFacts facts,
            IReadOnlyList<ActivationRule> rules, bool manualEnabled, IReadOnlyList<(string Op, WorkflowBinding Binding)> enforced)
        {
            var forcingOp = enforced.FirstOrDefault(b => b.Binding.Require.Contains(def.Name, StringComparer.Ordinal)).Op;
            if (forcingOp != null) return (true, $"required when {PlainOpPhrase(forcingOp)}");

            if (!manualEnabled) return (false, "turned off for this project");

            foreach (var r in rules)
            {
                if (!r.Valid || r.WhenError != null) continue;   // inert/broken rules never fire (E5/E6)
                if (!RuleSelects(r, def)) continue;
                if (r.When != null && !WorkflowPredicate.Evaluate(r.When, facts)) continue;
                return (r.On, string.IsNullOrWhiteSpace(r.Reason) ? DefaultActivationReason(r.On) : r.Reason);
            }
            return (true, null);   // default-on, invisible
        }

        // A plain fallback reason when a rule supplied no `reason:` (never echoes the predicate, §10.12).
        private static string DefaultActivationReason(bool on) =>
            on ? "shown by a project rule for this situation" : "hidden by a project rule for this situation";

        /// <summary>Resolve activation for a SINGLE def (used by start_workflow): gathers the pass's facts once
        /// and returns whether it's active, the plain reason, and whether a binding force-activates it.</summary>
        private async Task<(bool active, string reason, bool forceActive)> ResolveActivationForAsync(WorkflowDef def)
        {
            var rules = AllActivationRules();
            var arrayBindings = AllArrayBindings();
            var facts = await GatherFactsForAsync(rules.Where(r => r.Valid).Select(r => r.When)
                .Concat(arrayBindings.SelectMany(ab => ab.Rules.Select(r => r.When))));
            var enforced = ComputeEnforcedBindings(facts, arrayBindings);
            var forceActive = enforced.Any(b => b.Binding.Require.Contains(def.Name, StringComparer.Ordinal));
            var (active, reason) = ResolveActivation(def, facts, rules, SettingsEnabledFor(def.Name), enforced);
            return (active, reason, forceActive);
        }

        /// <summary>Build the resolved library infos (availability + activation) — the shared body behind
        /// list_workflows AND the library rebroadcast, so the two can never drift. Gathers the lazy fact snapshot
        /// once for the whole pass (§2.5).</summary>
        private async Task<WorkflowInfo[]> BuildLibraryInfosAsync()
        {
            var defs = LoadWorkflowDefs();
            var global = GlobalStrictness();
            var rules = AllActivationRules();
            var arrayBindings = AllArrayBindings();
            var facts = await GatherFactsForAsync(rules.Where(r => r.Valid).Select(r => r.When)
                .Concat(arrayBindings.SelectMany(ab => ab.Rules.Select(r => r.When))));
            var enforced = ComputeEnforcedBindings(facts, arrayBindings);

            return defs.Select(d =>
            {
                var enabled = SettingsEnabledFor(d.Name);
                var (active, reason) = ResolveActivation(d, facts, rules, enabled, enforced);
                return WorkflowRunner.BuildInfo(d, SettingsStrictnessFor(d.Name), global, enabled, active, reason);
            }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>§9c binding enforcement, called at the TOP of the bindable authoring ops (BEFORE any mutation,
        /// so a refusal can never leave a half-applied model). The bindable-op set is the six dual-drive authoring
        /// chokepoints wired below — deliberately small in v1: an unwired op cannot be bound, which is honest
        /// (§9.6) rather than a binding that silently does nothing. Decisions honored: (A) STEP-scoped exemption —
        /// a bound op is allowed only when an ACTIVE run of a require: workflow is AT a step whose declared ops:
        /// contains this op (merely having a run active is the closed "start-and-freestyle" hole); (B) read ONLY
        /// bindings.&lt;op&gt;.mode — DELIBERATELY no GlobalStrictness() check here: docs/pro-mode-spec.md §9.10(B)
        /// (ratified 2026-07-05, shipped 2026-07-06) rules that strictness governs how gates bite INSIDE a run
        /// while binding is a different axis (whether you must ENTER a run at all), and "the strictness switch
        /// never reaches into the binding decision" — otherwise committing `strictness: off` would evaporate every
        /// project mandate (pinned by WorkflowBindingTests.Strictness_off_does_not_void_a_hard_binding); and the
        /// DryRunScope exemption — a rehearsal is not a landing (replay_check_workflow dry-runs bound ops from
        /// exemplars; a hard binding must not break admission replays).</summary>
        private async Task EnforceBindingAsync(string op, string origin)
        {
            // DryRunScope is AsyncLocal, set on the caller's thread before the op is invoked; read it synchronously
            // here (before this method's first await) so the rehearsal exemption never depends on async-flow to a
            // dispatcher thread — same contract as Session.MutateAsync's dry-run check.
            if (DryRunScope.Current != null) return;

            // FAIL CLOSED: a PRESENT-but-corrupt settings file must NOT silently drop a mandated binding. Parsing it
            // yields "no binding", which would let a bound op through — so corrupting the JSON would bypass the very
            // mandate this gate exists to enforce. We can't tell WHICH workflow the op should route through, so the
            // strictest honest posture is to refuse the bindable op until the file is repaired (a MISSING file stays
            // the normal no-bindings default). An honest, READABLE `strictness: off` wouldn't void a binding either
            // (§9.10B above), so there is no kill-switch carve-out to honor here — this only governs what a
            // PRESENT-but-corrupt file MEANS; the workflow-enforcement-toggle precedence for gates is unchanged.
            if (WorkflowSettingsCorrupt())
                throw new InvalidOperationException(
                    $"{PlainOpPhrase(op)} may be gated by a workflow policy, but .semanticus/workflow-settings.json is present and can't be read (malformed JSON) — enforcement is failing closed. Repair or delete the file, or run set_workflow_enforcement (e.g. mode 'default') — any settings write preserves the unreadable file aside as workflow-settings.json.corrupt-* and writes a fresh valid one. get_workflow_policy shows the rules once it parses.");

            // [D6] D6: object (flat) OR §9.11 array (conditional) form, resolved through the SHARED evaluator. The
            // flat path is byte-for-byte what SettingsBindingFor returned (no regression); an array binding
            // contributes the first rule whose `when:` holds against context facts (target.*/workflow.active deferred).
            var binding = await ResolveEnforcedBindingAsync(op);
            if (binding == null) return;                                   // no binding (the safe default)
            var mode = binding.Mode;
            if (mode != "hard" && mode != "warn") return;                  // absent/"off"/malformed ⇒ not enforced
            if (binding.Require.Length == 0) return;                       // a require-less binding routes nowhere — treat as none

            // (A) STEP-scoped exemption: some active run of a required workflow must be AT a step performing THIS op.
            bool atPerformingStep = false;
            await _workflowGate.WaitAsync();
            try
            {
                foreach (var run in _workflowRuns.ActiveRuns())
                {
                    if (!binding.Require.Contains(run.Def.Name, StringComparer.Ordinal)) continue;
                    var step = run.CurrentStep;
                    if (step?.Ops != null && step.Ops.Contains(op, StringComparer.Ordinal)) { atPerformingStep = true; break; }
                }
            }
            finally { _workflowGate.Release(); }
            if (atPerformingStep) return;

            var required = string.Join(", ", binding.Require.Select(n => $"'{n}'"));
            if (mode == "hard")
                // §9.17/§10.12: the error teaches in plain language and names the actual op + required set — never "binding violation".
                throw new InvalidOperationException(
                    $"In this project, {PlainOpPhrase(op)} go through one of: {required}. Start one with start_workflow and do this {op} at its authoring step. " +
                    "(get_workflow_policy shows this project's rules; a team lead can change them with set_workflow_binding.)");

            // warn (§9.6 v1 audit posture): allow the edit, but the advisory record IS the enforcement artifact.
            // Rides the same Activity bus SetWorkflowEnabledAsync uses, so the ExperienceTee persists it (the
            // compliance trail outlives the session) and both doors see it live.
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Kind = "landed_outside_required_workflow", Ok = true, Target = op,
                Label = $"{op} landed outside its required workflow (one of: {required}) — allowed (warn).",
            });
        }

        /// <summary>Plain-language noun phrase for a bound op, for the teaching error (§10.12 "the UI never speaks
        /// engine"); falls back to the op name for anything unmapped.</summary>
        private static string PlainOpPhrase(string op) => op switch
        {
            "create_measure" => "new measures",
            "update_measure" => "measure edits",
            "create_calculated_column" => "new calculated columns",
            "create_calculation_item" => "new calculation items",
            "create_table" => "new tables",
            "create_relationship" => "new relationships",
            _ => op,
        };

        public Task<WorkflowEnforcement> GetWorkflowEnforcementAsync()
        {
            // A corrupt settings file fails CLOSED: the "off" kill-switch lives in that same unreadable file, so it
            // can't be honored — enforcement stays on and we say so (mode reads null ⇒ per-def default: hard).
            if (WorkflowSettingsCorrupt())
                return Task.FromResult(new WorkflowEnforcement
                {
                    Mode = null,
                    Enforced = true,
                    Note = "workflow-settings.json is present but unreadable (malformed JSON) — enforcement is failing CLOSED (any 'off' override in it is ignored). Repair or delete the file, or run set_workflow_enforcement (e.g. mode 'default') — any settings write preserves the unreadable file aside as workflow-settings.json.corrupt-* and writes a fresh valid one.",
                });
            var mode = GlobalStrictness();
            return Task.FromResult(new WorkflowEnforcement
            {
                Mode = mode,
                Enforced = mode != "off",
                Note = mode == null
                    ? "No global override — each workflow's own strictness applies (engine default: hard)."
                    : mode == "off"
                        ? "Enforcement is OFF model-wide: gates are skipped, runs record no verified evidence, and gated runs start without Pro. set_workflow_enforcement(mode:\"default\") restores enforcement."
                        : $"Every gate runs at '{mode}' regardless of what the workflow declares.",
            });
        }

        /// <summary>Set (or clear) the model-wide enforcement mode. Writes the settings file non-destructively
        /// (the per-workflow overrides survive) and re-broadcasts the library — Gated flags change with the mode,
        /// so both doors see cards flip live. mode: "hard" | "warn" | "off" | "default"/null (clear).</summary>
        public async Task<WorkflowEnforcement> SetWorkflowEnforcementAsync(string mode, string origin)
        {
            mode = string.IsNullOrWhiteSpace(mode) || mode.Trim().ToLowerInvariant() == "default" ? null : mode.Trim().ToLowerInvariant();
            if (mode != null && mode != "hard" && mode != "warn" && mode != "off")
                throw new ArgumentException("mode must be 'hard', 'warn', 'off', or 'default' (clear the override and let each workflow's own strictness apply).");
            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings — open a model (or start the engine with a workspace) first.");

            // Merge, never clobber: the file also carries per-workflow overrides the designer writes. The shared
            // helper does the read-modify-write under a lock with an atomic write, preserving a corrupt file aside.
            MutateWorkflowSettings(file, root =>
            {
                root.Remove("profile");
                if (mode == null) root.Remove("strictness"); else root["strictness"] = mode;
            });

            await PublishWorkflowLibraryAsync();   // Gated flags just changed — both doors re-list live
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "set_workflow_enforcement", Ok = true,
                Label = mode == null ? "Workflow enforcement: per-definition (override cleared)" : $"Workflow enforcement: {mode} (model-wide)",
            });
            return await GetWorkflowEnforcementAsync();
        }

        private List<WorkflowDef> LoadWorkflowDefs()
        {
            var (userDir, stockDir) = WorkflowDirs();
            var defs = WorkflowParser.LoadDirectory(stockDir, "stock");
            foreach (var user in WorkflowParser.LoadDirectory(userDir, "user"))
            {
                defs.RemoveAll(d => string.Equals(d.Name, user.Name, StringComparison.Ordinal)); // user shadows stock
                defs.Add(user);
            }
            // §10.3 placement guard: a `kind: template` file in the workflows dir is a misplaced RECIPE, not a
            // runnable workflow — surface it as an error (never silently skip), so list_workflows/start_workflow
            // teach the fix instead of trying to run blanks.
            foreach (var d in defs)
                if (d.Error == null && string.Equals(d.Kind, "template", StringComparison.Ordinal))
                    d.Error = "this file declares 'kind: template' but lives in the workflows dir — a template is a recipe with blanks, not runnable. Move it to .semanticus/workflow-templates/, or remove 'kind: template' to make it a workflow.";
            return defs;
        }

        public async Task<WorkflowInfo[]> ListWorkflowsAsync() => await BuildLibraryInfosAsync();

        public Task<WorkflowDef> GetWorkflowAsync(string name)
        {
            var def = LoadWorkflowDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Workflow '{name}' not found (list_workflows shows the library).");
            return Task.FromResult(def);
        }

        // ---- admission dry-run (Learning Loop L4 — docs/learning-loop-plan.md §3.3) --------------

        private static readonly System.Text.RegularExpressions.Regex WhenInputExpr =
            new System.Text.RegularExpressions.Regex(@"^inputs\.([A-Za-z0-9_-]+)\.answered$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The admission DRY-RUN for a learned/authored workflow — the cheap half of the L4 pipeline
        /// (parse-valid → dry-run). Loads the def (parse errors surface as today), then statically resolves it
        /// against the LIVE op surface and its own gate inputs: every `triggers:`/`ops:` entry is a real op;
        /// every verify `when`/`probe` names an input some gate collects; probe/equivalence (and object-scoped
        /// bpa) verifies have a target objectRef input to act on. A DISTILLED workflow (derived_from provenance)
        /// gets an info finding naming its origin. REPLAY of the deterministic steps against the originating
        /// snapshot is a LATER layer — not run here. Free, read-only.</summary>
        public async Task<WorkflowCheckReport> CheckWorkflowAsync(string name)
        {
            var wf = LoadWorkflowDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (wf != null) return await CheckWorkflowDefAsync(wf);

            // §10.5: check_workflow also accepts a TEMPLATE name — validate decls↔refs BOTH directions and run a
            // trial instantiation with every slot's example through the full admission (CheckTemplateAsync).
            var tmpl = LoadTemplateDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tmpl != null) return await CheckTemplateAsync(tmpl);

            throw new InvalidOperationException($"'{name}' not found (list_workflows shows the workflow library; list_workflow_templates shows the template shelf).");
        }

        /// <summary>The admission dry-run over an already-loaded def (extracted so the §10 template
        /// trial-instantiation runs the SAME resolution rules a workflow gets — never a drifting copy).</summary>
        private async Task<WorkflowCheckReport> CheckWorkflowDefAsync(WorkflowDef def)
        {
            var report = new WorkflowCheckReport { Name = def.Name };
            if (def.Error != null) { report.ParseError = def.Error; report.Ok = false; return report; }

            var findings = new List<CheckFinding>();

            // A distilled workflow names where it came from — info, never docks Ok.
            if (def.Provenance != null && def.Provenance.Count > 0)
                findings.Add(new CheckFinding { Severity = "info",
                    Message = "Distilled workflow — provenance " + string.Join("; ", def.Provenance.Select(kv => $"{kv.Key}: {kv.Value}")) + "." });

            var catalog = (await GetOpCatalogAsync()).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var t in def.Triggers ?? Array.Empty<string>())
                if (!catalog.Contains(t))
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"trigger '{t}' is not a known op (get_op_catalog lists the real surface)." });

            // Run-wide gate inputs: a later step's verify can reference an input a *previous* step collected.
            var allInputs = def.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>()).ToList();
            var inputNames = allInputs.Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.Ordinal);
            var hasObjectRef = allInputs.Any(i => string.Equals(i.Type, "objectRef", StringComparison.Ordinal));

            foreach (var step in def.Steps)
            {
                foreach (var op in step.Ops ?? Array.Empty<string>())
                    if (!catalog.Contains(op))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} op '{op}' is not a known op." });

                foreach (var v in step.Gate?.Verify ?? Array.Empty<VerifySpec>())
                {
                    var whenInput = v.When == null ? null : (WhenInputExpr.Match(v.When) is var m && m.Success ? m.Groups[1].Value : null);
                    if (whenInput != null && !inputNames.Contains(whenInput))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' when references input '{whenInput}', which no gate collects." });

                    // A verify whose kind REQUIRES a probe (the value/expression to compare against) but declares
                    // none slips past the non-empty-probe check below yet fails at run time — flag it at admission.
                    var needsProbe = v.Kind == "dax_probe" || v.Kind == "dax_equivalence"
                        || v.Kind == "benchmark_delta" || v.Kind == "workflow_admissible" || v.Kind == "baseline_captured"
                        || v.Kind == "baseline_exists" || v.Kind == "baseline_unchanged"
                        || v.Kind == "plan_item_staged" || v.Kind == "plan_item_applied";
                    if (needsProbe && string.IsNullOrEmpty(v.Probe))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' has no probe: input naming the value to compare against — it would fail at run time." });

                    if (!string.IsNullOrEmpty(v.Probe) && !inputNames.Contains(v.Probe))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' probe references input '{v.Probe}', which no gate collects." });

                    var needsTarget = v.Kind == "dax_probe" || v.Kind == "dax_equivalence" || v.Kind == "benchmark_delta"
                        || v.Kind == "impact_assessment" || v.Kind == "baseline_exists"
                        || v.Kind == "plan_item_staged" || v.Kind == "plan_item_applied"
                        || (v.Kind == "bpa_clean" && string.Equals(v.Scope, "object", StringComparison.Ordinal));
                    if (needsTarget && !hasObjectRef)
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' needs a target, but no input of type objectRef names the object to act on — the check can't run and would fail a hard gate." });

                    // equivalenceGrid is an optional convention: absent = grand-total-only (thin), not an error.
                    if (v.Kind == "dax_equivalence" && !inputNames.Contains("equivalenceGrid"))
                        findings.Add(new CheckFinding { Severity = "info", Message = $"{step.Id} verify 'dax_equivalence' has no 'equivalenceGrid' input — equivalence would be grand-total only (thin). Add one for a per-context proof." });

                    // benchmarkTolerance is optional: absent = the 1.10 (10%-over-baseline) default applies.
                    if (v.Kind == "benchmark_delta" && !inputNames.Contains("benchmarkTolerance"))
                        findings.Add(new CheckFinding { Severity = "info", Message = $"{step.Id} verify 'benchmark_delta' has no 'benchmarkTolerance' input — the default 1.10 (allow 10% over baseline) applies. Add one to tighten or loosen the regression band." });

                    if (v.Kind == "impact_assessment" && !inputNames.Contains("reportPaths"))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify 'impact_assessment' needs a 'reportPaths' answer-or-decline input so report scope is explicit." });

                    if ((v.Kind == "plan_item_staged" || v.Kind == "plan_item_applied") && !inputNames.Contains("newName"))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' needs a 'newName' input to bind the plan item to the declared rename." });
                }
            }

            report.Findings = findings.ToArray();
            report.Ok = report.ParseError == null && !findings.Any(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal));
            return report;
        }

        // ---- authoring (the designer's write path — FREE: authoring is content, enforcement is paid) ----

        private static readonly System.Text.RegularExpressions.Regex KebabName =
            new System.Text.RegularExpressions.Regex("^[a-z0-9]+(-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Write a user workflow file — PARSE-VALIDATE FIRST: a file the parser refuses is never
        /// written (the designer must not be able to author a broken library entry; the parse error comes
        /// back verbatim). Saving a stock name creates the user shadow (copy-to-customise).</summary>
        public async Task<WorkflowInfo[]> SaveWorkflowAsync(string name, string markdown, string origin)
        {
            name = (name ?? "").Trim();
            if (!KebabName.IsMatch(name))
                throw new InvalidOperationException($"'{name}' is not a valid workflow name — kebab-case (e.g. 'my-workflow'); it becomes the filename.");
            var def = WorkflowParser.Parse(markdown);
            if (def.Error != null)
                throw new InvalidOperationException($"The workflow does not parse — nothing was written. {def.Error} Fix the reported error, then re-run save_workflow (or check_workflow to re-validate a library entry).");
            if (!string.Equals(def.Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"frontmatter name '{def.Name}' must equal the workflow name '{name}' (it is the file identity).");

            var (userDir, _) = WorkflowDirs();
            if (userDir == null)
                throw new InvalidOperationException("No place to store user workflows — run open_model (or save_model after create_model) so the .semanticus sidecar has a home, or run the engine with a workspace.");
            Directory.CreateDirectory(userDir);
            await Task.Run(() => File.WriteAllText(Path.Combine(userDir, name + ".md"), markdown));
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>Delete a USER workflow. Stock files are read-only by construction — deleting a stock
        /// name without a user shadow is refused instructively (customised shadows revert to stock).</summary>
        public async Task<WorkflowInfo[]> DeleteWorkflowAsync(string name, string origin)
        {
            var (userDir, stockDir) = WorkflowDirs();
            var file = userDir == null ? null : Path.Combine(userDir, (name ?? "").Trim() + ".md");
            if (file == null || !File.Exists(file))
                throw new InvalidOperationException(
                    File.Exists(Path.Combine(stockDir, (name ?? "").Trim() + ".md"))
                        ? $"'{name}' is a stock workflow (read-only, shipped with the engine) and has no user copy to delete. Customised copies live in .semanticus/workflows."
                        : $"User workflow '{name}' not found — list_workflows shows the library, and only your own copies (under .semanticus/workflows) are deletable.");
            await Task.Run(() => File.Delete(file));
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>§9a: enable/disable a workflow (the availability toggle). Non-destructive merge into
        /// `workflows.&lt;name&gt;.enabled` — per-workflow strictness and every other key survive. Re-broadcasts
        /// the library so both doors re-list live; a disabled workflow stays VISIBLE (marked enabled:false) so
        /// the designer can re-enable it, but start_workflow refuses it. FREE: curation is content, and a
        /// disable only narrows the caller's own menu (mirrors set_workflow_enforcement's file discipline).</summary>
        public async Task<WorkflowInfo[]> SetWorkflowEnabledAsync(string name, bool enabled, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
            var def = LoadWorkflowDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Workflow '{name}' not found — list_workflows shows the library (stock + your .semanticus/workflows).");
            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings — open a model (or start the engine with a workspace) first.");

            MutateWorkflowSettings(file, root =>
            {
                root.Remove("profile");
                if (root["workflows"] is not System.Text.Json.Nodes.JsonObject wfs) { wfs = new System.Text.Json.Nodes.JsonObject(); root["workflows"] = wfs; }
                if (wfs[def.Name] is not System.Text.Json.Nodes.JsonObject wf) { wf = new System.Text.Json.Nodes.JsonObject(); wfs[def.Name] = wf; }
                if (enabled) wf.Remove("enabled");            // absent = available (the default) — keep the file minimal
                else wf["enabled"] = false;
                // Prune emptied containers so re-enabling a workflow that carried ONLY this key leaves no orphan {}
                // behind: drop the per-workflow object once it holds nothing, then drop "workflows" itself if that
                // emptied it. A surviving sibling key (e.g. a per-workflow strictness) keeps the subtree intact.
                if (wf.Count == 0) wfs.Remove(def.Name);
                if (wfs.Count == 0) root.Remove("workflows");
            });

            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "set_workflow_enabled", Ok = true,
                Label = enabled ? $"Workflow '{def.Name}' enabled (on the menu)" : $"Workflow '{def.Name}' disabled (off the menu)",
            });
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>§9c: bind (or clear) an op → required workflow(s). Non-destructive merge into
        /// `bindings.&lt;op&gt;` — sibling bindings and every other key survive; re-broadcasts the library so both
        /// doors re-list live. mode:"off" or an empty require CLEARS the binding (and prunes the emptied
        /// containers, mirroring set_workflow_enabled). PRO for mode hard|warn (§9.8 — mandatory routing IS the
        /// enforcement the moat sells); clearing is free UNLESS the existing binding is `userDisablable:false`
        /// (§9.10C), which locks it against the AGENT door (a human/reviewed file edit still governs it — we do
        /// not, and cannot, police the file itself).</summary>
        public async Task<WorkflowInfo[]> SetWorkflowBindingAsync(string op, string[] requireNames, string mode, string origin)
        {
            if (string.IsNullOrWhiteSpace(op)) throw new ArgumentException("op is required — the op to route, e.g. 'create_measure'.");
            op = op.Trim();
            mode = string.IsNullOrWhiteSpace(mode) ? "off" : mode.Trim().ToLowerInvariant();
            if (mode != "hard" && mode != "warn" && mode != "off")
                throw new ArgumentException("mode must be 'hard', 'warn', or 'off' (off clears the binding).");
            var require = (requireNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            var clearing = mode == "off" || require.Length == 0;

            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings — open a model (or start the engine with a workspace) first.");

            // §9.10C: a committed mandate (userDisablable:false) can't be changed OR cleared from the agent door —
            // instructive refusal, checked before the Pro gate so the more-specific reason wins. Human/file edits
            // remain possible (origin != "agent"); we deliberately do not try to police the file itself.
            var existing = SettingsBindingFor(op);
            if (existing != null && !existing.UserDisablable && string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The requirement on {op} is locked by committed team policy; change it in workflow-settings.json via review.");

            if (!clearing)
            {
                // §9.8 Pro gate — writing a mandate (hard|warn) is the paid enforcement; reading/curating stays free.
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "set_workflow_binding (requiring an op to route through a workflow)",
                    "Free alternative: curate the menu with set_workflow_enabled and follow a workflow manually (get_workflow).");
                // Every required name must exist — a binding to a phantom workflow would be an unstartable trap.
                var lib = LoadWorkflowDefs().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                var unknown = require.Where(n => !lib.Contains(n)).ToArray();
                if (unknown.Length > 0)
                    throw new InvalidOperationException($"Unknown workflow(s): {string.Join(", ", unknown)}. list_workflows shows the library — a binding can only require workflows that exist.");
            }

            MutateWorkflowSettings(file, root =>
            {
                root.Remove("profile");
                if (clearing)
                {
                    if (root["bindings"] is System.Text.Json.Nodes.JsonObject bs) { bs.Remove(op); if (bs.Count == 0) root.Remove("bindings"); }
                }
                else
                {
                    if (root["bindings"] is not System.Text.Json.Nodes.JsonObject bindings) { bindings = new System.Text.Json.Nodes.JsonObject(); root["bindings"] = bindings; }
                    if (bindings[op] is not System.Text.Json.Nodes.JsonObject b) { b = new System.Text.Json.Nodes.JsonObject(); bindings[op] = b; }
                    b["require"] = new System.Text.Json.Nodes.JsonArray(require.Select(n => (System.Text.Json.Nodes.JsonNode)n).ToArray());
                    b["mode"] = mode;
                    // userDisablable is committed team policy, only ever hand-edited (the op takes no such arg) — leave
                    // any existing key untouched so an update can't silently unlock a locked mandate.
                }
            });

            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "set_workflow_binding", Ok = true, Target = op,
                Label = clearing ? $"Binding cleared: {op} is free to call again"
                                  : $"Binding set: {op} → requires one of {string.Join(", ", require)} ({mode})",
            });
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>§10.6 [D5]: show a workflow only when a condition holds — upsert (or clear) a single
        /// `workflow:`-selector activation rule. Passing neither a condition nor a set CLEARS the rule for this
        /// workflow (prunes the emptied array, mirroring set_workflow_binding). The workflow must exist and the
        /// condition must parse (a bad predicate is refused with the plain reason). PRO (§10.7 — activation rules
        /// are Pro; reading/curating is free); re-broadcasts the library so both doors re-list live. Tag-form rules
        /// are hand-edited in v1 (this op takes a name).</summary>
        public async Task<WorkflowInfo[]> SetWorkflowActivationAsync(string workflow, string when, string set, string origin)
        {
            if (string.IsNullOrWhiteSpace(workflow)) throw new ArgumentException("workflow is required — the workflow to show or hide with a rule.");
            workflow = workflow.Trim();
            when = string.IsNullOrWhiteSpace(when) ? null : when.Trim();
            set = string.IsNullOrWhiteSpace(set) ? null : set.Trim().ToLowerInvariant();
            var clearing = when == null && set == null;   // neither given ⇒ remove the rule (show it normally again)

            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings — open a model (or start the engine with a workspace) first.");

            // A rule for a phantom workflow is a trap that only clutters the policy lints — refuse it up front.
            var lib = LoadWorkflowDefs().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            if (!lib.Contains(workflow))
                throw new InvalidOperationException($"Workflow '{workflow}' not found — list_workflows shows the library. An activation rule can only target a workflow that exists.");

            if (!clearing)
            {
                if (set != "on" && set != "off")
                    throw new ArgumentException("set must be 'on' (show it when the condition holds) or 'off' (hide it), or clear the rule by passing neither a condition nor a set.");
                // Parse-validate the condition BEFORE the write (teaching tone) — never persist a rule that can't run.
                WorkflowPredicate.Parse(when, out var perr);
                if (perr != null)
                    throw new InvalidOperationException($"That condition can't be used — {perr} Conditions read like date.monthEndOffset >= -3, connection.workspace ~ '*prod*', or model.readinessGrade < 'B'.");
                // §10.7 Pro gate — writing an activation rule is the paid curation; reading it stays free.
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "set_workflow_activation (showing a workflow only when a condition holds)",
                    "Free alternative: turn workflows on/off with set_workflow_enabled, or edit the activation list in .semanticus/workflow-settings.json.");
            }

            MutateWorkflowSettings(file, root =>
            {
                root.Remove("profile");
                // Upsert-replace: drop any existing workflow:-selector rules for this name, then append the new one
                // (tag:-selector rules are hand-edited only in v1, so they survive untouched).
                var old = root["activation"] as System.Text.Json.Nodes.JsonArray;
                var kept = new System.Text.Json.Nodes.JsonArray();
                if (old != null)
                    foreach (var n in old)
                    {
                        if (n is System.Text.Json.Nodes.JsonObject o && o["workflow"] is System.Text.Json.Nodes.JsonValue wv
                            && wv.TryGetValue<string>(out var wn) && string.Equals(wn, workflow, StringComparison.Ordinal))
                            continue;   // replaced (or, when clearing, removed)
                        kept.Add(n?.DeepClone());
                    }
                if (!clearing)
                {
                    var entry = new System.Text.Json.Nodes.JsonObject { ["workflow"] = workflow, ["set"] = set };
                    if (when != null) entry["when"] = when;
                    kept.Add(entry);
                }
                if (kept.Count == 0) root.Remove("activation"); else root["activation"] = kept;
            });

            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "set_workflow_activation", Ok = true, Target = workflow,
                Label = clearing ? $"Activation rule cleared: '{workflow}' shows normally again"
                       : set == "on" ? $"Activation: '{workflow}' shown {(when == null ? "always" : "when the condition holds")}"
                                     : $"Activation: '{workflow}' hidden {(when == null ? "always" : "when the condition holds")}",
            });
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>§9.12: the whole workflow POLICY for this project in one compact read — global enforcement
        /// mode, one row per workflow (availability + gated + whenToUse + the ops that REQUIRE it, inverted from
        /// the bindings), and the raw bindings. Token-lean by design (no step bodies, no descriptions beyond
        /// whenToUse): it rides the orientation primer so Claude self-routes rather than discovering mandates by
        /// rejection. Free, read-only.</summary>
        public async Task<WorkflowPolicy> GetWorkflowPolicyAsync()
        {
            var global = GlobalStrictness();
            var defs = LoadWorkflowDefs();
            var rules = AllActivationRules();
            var arrayBindings = AllArrayBindings();
            var facts = await GatherFactsForAsync(rules.Where(r => r.Valid).Select(r => r.When)
                .Concat(arrayBindings.SelectMany(ab => ab.Rules.Select(r => r.When))));
            var enforced = ComputeEnforcedBindings(facts, arrayBindings);
            var flatBindings = AllSettingsBindings();   // the raw Bindings view keeps its shipped (flat) shape

            var entries = defs
                .Select(d =>
                {
                    var enabled = SettingsEnabledFor(d.Name);
                    var (active, reason) = ResolveActivation(d, facts, rules, enabled, enforced);
                    return new WorkflowPolicyEntry
                    {
                        Name = d.Name,
                        Enabled = enabled,
                        Active = active,
                        ActiveReason = reason,
                        Gated = d.HasEnforcedGate(SettingsStrictnessFor(d.Name), global),
                        WhenToUse = d.WhenToUse,
                        // invert the (effective) bindings: which ops name THIS workflow in their require set
                        RequiredForOps = enforced.Where(b => b.Binding.Require.Contains(d.Name, StringComparer.Ordinal))
                                                 .Select(b => b.Op).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToArray(),
                    };
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();

            var bindingViews = flatBindings
                .Select(b => new WorkflowBindingView { Op = b.Op, Require = b.Binding.Require, Mode = b.Binding.Mode, UserDisablable = b.Binding.UserDisablable })
                .OrderBy(v => v.Op, StringComparer.Ordinal).ToArray();

            var lints = ComputePolicyLints(defs, rules, arrayBindings, enforced);
            return new WorkflowPolicy { ActiveProfile = ActiveWorkflowProfile(), Enforcement = global, Workflows = entries, Bindings = bindingViews, Lints = lints };
        }

        /// <summary>§10.6:653 — the policy lints, surfaced loudly on the free/read-only policy view. NEVER blocks;
        /// reports. Each message is analyst-facing plain language (§10.12), never a raw predicate.</summary>
        private WorkflowPolicyLint[] ComputePolicyLints(
            List<WorkflowDef> defs, List<ActivationRule> rules,
            List<(string Op, List<BindingRule> Rules)> arrayBindings,
            List<(string Op, WorkflowBinding Binding)> enforced)
        {
            var lints = new List<WorkflowPolicyLint>();
            var names = defs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var allTags = defs.SelectMany(d => d.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Warn(string m) => lints.Add(new WorkflowPolicyLint { Severity = "warn", Message = m });
            void Info(string m) => lints.Add(new WorkflowPolicyLint { Severity = "info", Message = m });

            // Loudest of all: a PRESENT-but-corrupt settings file means enforcement is failing CLOSED (bindable ops
            // refused, any 'off' override ignored). Surface it so the fix — repair or delete the file — is obvious.
            if (WorkflowSettingsCorrupt())
                Warn("workflow-settings.json is present but can't be parsed (malformed JSON) — workflow enforcement is failing CLOSED: bindable authoring ops are refused and any 'off' override is ignored until you repair or delete the file.");

            // Per-rule structural lints.
            foreach (var r in rules)
            {
                if (!r.Valid) { Warn(r.InvalidReason ?? "an activation rule is malformed and was skipped."); continue; }
                if (r.WhenError != null)                                   // (2) unreadable when:
                    Warn($"the condition on the activation rule for {SelectorLabel(r)} can't be used: {r.WhenError}");
                if (r.Workflow != null && !names.Contains(r.Workflow))     // (1) unknown target workflow
                    Warn($"an activation rule targets workflow '{r.Workflow}', which doesn't exist — list_workflows shows the library; the rule does nothing.");
                if (r.Tag != null && !allTags.Contains(r.Tag))            // (1) unknown target tag
                    Warn($"an activation rule targets tag '{r.Tag}', which no workflow carries — the rule does nothing.");
            }

            // Per selected-workflow contradictions.
            foreach (var d in defs)
            {
                var enabled = SettingsEnabledFor(d.Name);
                var requiredBy = enforced.Where(b => b.Binding.Require.Contains(d.Name, StringComparer.Ordinal)).Select(b => b.Op).ToArray();
                var selecting = rules.Where(r => r.Valid && r.WhenError == null && RuleSelects(r, d)).ToArray();

                if (!enabled && requiredBy.Length > 0)                     // (5) the deadlock (loudest)
                    Warn($"'{d.Name}' is turned off but required when {PlainOpPhrase(requiredBy[0])}; the requirement wins — turn '{d.Name}' back on or drop the requirement.");
                if (!enabled && selecting.Any(r => r.On))                  // (3) dead rule (manual disable wins)
                    Warn($"a rule shows '{d.Name}' in some situations, but it's turned off for this project — the rule has no effect (turn it back on to use the rule).");
                if (requiredBy.Length > 0 && selecting.Any(r => !r.On))    // (4) binding↔activation contradiction
                    Warn($"'{d.Name}' is required when {PlainOpPhrase(requiredBy[0])} but a rule hides it — the requirement wins; the rule is overridden.");
                if (selecting.Select(r => r.On).Distinct().Count() > 1)    // (6) conflicting rules (deterministic, but a smell)
                    Info($"more than one activation rule selects '{d.Name}' with different on/off outcomes — the first matching rule wins (a likely mistake).");
            }

            // Array-binding rules referencing a not-yet-available fact (D6/T4 deferral) — surfaced so a
            // never-matching binding is loud, not silent.
            foreach (var (op, ruleList) in arrayBindings)
                foreach (var r in ruleList)
                    if (r.WhenError != null)
                        Warn($"a rule that would require {PlainOpPhrase(op)} uses a condition that isn't available yet: {r.WhenError}");

            return lints.ToArray();
        }

        private static string SelectorLabel(ActivationRule r) => r.Workflow != null ? $"workflow '{r.Workflow}'" : $"tag '{r.Tag}'";

        /// <summary>Rebuild the resolved library (availability + activation) and broadcast it so BOTH doors re-list
        /// live (workflow/libraryDidChange). Async because activation may gather a fact snapshot (§2.5 — lazy;
        /// nothing when no rules reference a root).</summary>
        private async Task<WorkflowInfo[]> PublishWorkflowLibraryAsync()
        {
            var list = await BuildLibraryInfosAsync();
            _sessions.Bus.PublishWorkflowLibrary(list);
            return list;
        }

        /// <summary>Rebroadcast the library on a session/connection/plan transition (§10.6:655-657) — GUARDED so a
        /// broadcast failure never fails the transition itself (log-and-continue). Date-driven flips re-evaluate on
        /// the next read/transition (no background timer, by design).</summary>
        private async Task SafeRebroadcastWorkflowLibraryAsync()
        {
            try { await PublishWorkflowLibraryAsync(); }
            catch { /* the transition already succeeded — a menu rebroadcast must never undo it */ }
        }

        /// <summary>The engine's MCP tool catalog (name + first sentence) — the designer's action picker.
        /// Reflected once from the McpTools attributes (via <see cref="OpSurface"/>) so it can never drift from
        /// the real tool surface.</summary>
        public Task<OpInfo[]> GetOpCatalogAsync() => Task.FromResult(OpSurface.Infos);

        /// <summary>ONE reflection pass over the static McpTools surface, producing BOTH the name+first-sentence
        /// catalog (the designer's picker) AND the op-name→method map (<see cref="DryRunOpAsync"/>'s resolver) — built
        /// together so the two can never drift from each other or from the real tool surface. Internal so the
        /// dry-run wrapper can resolve + invoke a tool method by its attribute Name.</summary>
        internal sealed class OpSurfaceData
        {
            public OpInfo[] Infos { get; init; }
            public Dictionary<string, System.Reflection.MethodInfo> Methods { get; init; }
        }

        internal static OpSurfaceData OpSurface => _opSurface.Value;

        private static readonly Lazy<OpSurfaceData> _opSurface = new Lazy<OpSurfaceData>(() =>
        {
            var infos = new List<OpInfo>();
            var methods = new Dictionary<string, System.Reflection.MethodInfo>(StringComparer.Ordinal);
            // MCP tools are intentionally split across several [McpServerToolType] classes. Reflecting only the
            // original McpTools class made newer real ops invisible to the workflow designer and admission check.
            foreach (var type in typeof(McpTools).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed))
                foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    var tool = m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                    if (tool == null) continue;
                    var name = (string)tool.GetType().GetProperty("Name")?.GetValue(tool);
                    if (string.IsNullOrEmpty(name)) continue;
                    var desc = m.GetCustomAttributes(true).OfType<System.ComponentModel.DescriptionAttribute>().FirstOrDefault();
                    methods[name] = m;   // tool Names are unique across the assembled MCP surface
                    infos.Add(new OpInfo { Name = name, Description = FirstSentence(desc?.Description) });
                }
            return new OpSurfaceData { Infos = infos.OrderBy(o => o.Name, StringComparer.Ordinal).ToArray(), Methods = methods };
        });

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var cut = s.IndexOf(". ", StringComparison.Ordinal);
            var head = cut > 0 ? s.Substring(0, cut + 1) : s;
            return head.Length > 200 ? head.Substring(0, 200) + "…" : head;
        }

        // ---- run lifecycle ----------------------------------------------------------------------

        public async Task<WorkflowRunView> StartWorkflowAsync(string name, string origin)
        {
            // §10.2/§10.12: a template is a recipe with blanks, not runnable — teach the instantiate path before
            // the generic not-found, but only when no workflow of this name exists (a real workflow always wins).
            if (!LoadWorkflowDefs().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                && LoadTemplateDefs().Any(t => t.Error == null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"'{name}' is a template (a recipe with blanks). Fill it in first: instantiate_workflow_template creates a runnable workflow from it.");
            var def = await GetWorkflowAsync(name);

            // §10.6 activation [D1][D4]: resolve the menu state once. A binding force-active workflow MUST be
            // startable even if manually disabled (the deadlock breaker, E1) — so the disabled-refusal is skipped
            // for it. A manual `enabled:false` still hard-refuses otherwise (the kill switch). A RULE-deactivated
            // workflow (off the menu, not manually off) is startable ON DEMAND with a teaching note (D4).
            var (active, activeReason, forceActive) = await ResolveActivationForAsync(def);
            if (!SettingsEnabledFor(def.Name) && !forceActive)
                throw new InvalidOperationException($"Workflow '{def.Name}' is turned off for this project, so it can't be started. Turn it back on in the Workflows tab, or call set_workflow_enabled(\"{def.Name}\", true) (it's still listed so you can re-enable it).");
            if (def.Error != null)
                throw new InvalidOperationException($"Workflow '{name}' has a parse error and cannot run: {def.Error} Run check_workflow to see the full admission report, then fix it with save_workflow (or read it with get_workflow).");
            var settings = SettingsStrictnessFor(def.Name);
            var global = GlobalStrictness();

            // THE entitlement chokepoint (both doors inherit): what's paid is enforcement, not the
            // playbook — a workflow whose every gate resolves to off runs free (incl. via the
            // model-wide enforcement toggle: enforcement off ⇒ nothing enforced ⇒ nothing gated).
            if (def.HasEnforcedGate(settings, global))
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "start_workflow (an enforced, evidence-verified workflow run)",
                    "Free alternative: read the workflow with get_workflow and follow its steps manually.");

            // [D4] Started though a rule currently hides it (activation curates the menu, it isn't a lock): record a
            // plain advisory so the run is honest. Skip when force-active (that's "required", not "off the menu").
            if (!active && !forceActive)
                _sessions.Bus.PublishActivity(new ActivityEvent
                {
                    Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "start_workflow_off_menu", Ok = true, Target = def.Name,
                    Label = $"Started '{def.Name}' though it's off the current menu ({activeReason ?? "hidden by a rule"}) — you can still run it; activation curates the menu, it doesn't lock a workflow.",
                });

            // Start-of-run snapshots for diff-based verifies — taken BEFORE any step mutates the model.
            var aux = new WorkflowRunAux();
            var kinds = def.Steps.Where(s => s.Gate != null).SelectMany(s => s.Gate.Verify).Select(v => v.Kind).ToHashSet(StringComparer.Ordinal);
            if (kinds.Contains("bpa_clean") && _sessions.Current != null)
                aux.BpaKeys = (await BpaScanAsync()).Violations.Where(v => !v.Waived).Select(v => v.RuleId + "|" + v.ObjectRef).ToHashSet(StringComparer.Ordinal);
            if (kinds.Contains("readiness_rescan") && _sessions.Current != null)
            {
                var sc = await AiReadinessScanAsync();
                aux.ReadinessKeys = sc.Findings.Where(f => !f.Waived).Select(f => f.RuleId + "|" + f.ObjectRef).ToHashSet(StringComparer.Ordinal);
                aux.ReadinessOverall = sc.Overall;
            }

            var session = _sessions.Current;
            var modelName = session == null ? null : await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            WorkflowRunState run;
            await _workflowGate.WaitAsync();
            try
            {
                run = _workflowRuns.Start(def, settings, global);
                run.Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
                run.ModelIdentity = PaneIdentity(session, _live);
                run.ModelName = modelName;
                run.ModelFingerprint = session == null ? null : VitalsFingerprintFor(session);
                run.SessionId = session?.Id;
                _workflowAux[run.RunId] = aux;
                return PublishWorkflowRun(run);
            }
            finally { _workflowGate.Release(); }
        }

        public async Task<WorkflowRunView> GetWorkflowRunAsync(string runId)
        {
            await _workflowGate.WaitAsync();
            try { return WorkflowRunner.BuildView(_workflowRuns.Require(runId)); }
            finally { _workflowGate.Release(); }
        }

        /// <summary>Submit the run's current step. <paramref name="answersJson"/> is a JSON object:
        /// a value per gate-input name, or the explicit decline sentinel
        /// {"declined": true, "reason": "..."}. The gate evaluator's rejection text steers the agent.</summary>
        public async Task<WorkflowRunView> SubmitWorkflowStepAsync(string runId, string stepId, string answersJson, string origin)
        {
            var answers = ParseAnswers(answersJson);
            await _workflowGate.WaitAsync();
            try
            {
                var run = _workflowRuns.Require(runId);
                await WorkflowRunner.SubmitStepAsync(run, stepId, answers, ExecuteWorkflowVerifyAsync);
                return PublishWorkflowRun(run, origin);
            }
            finally { _workflowGate.Release(); }
        }

        public async Task<WorkflowRunView> SkipWorkflowStepAsync(string runId, string stepId, string reason, string origin)
        {
            await _workflowGate.WaitAsync();
            try
            {
                var run = _workflowRuns.Require(runId);
                WorkflowRunner.SkipStep(run, stepId, reason);
                var view = PublishWorkflowRun(run, origin);
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "skip_workflow_step",
                    Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                    Label = $"Workflow '{view.Workflow}': step skipped ({reason.Trim()})",
                    Target = view.Workflow,
                    Ok = true,
                    Result = new { view.RunId, StepId = view.Steps[view.StepIndex - 1].StepId, Reason = reason.Trim() },
                });
                return view;
            }
            finally { _workflowGate.Release(); }
        }

        public async Task<WorkflowRunView> AbortWorkflowAsync(string runId, string reason, string origin)
        {
            await _workflowGate.WaitAsync();
            try
            {
                var run = _workflowRuns.Require(runId);
                WorkflowRunner.Abort(run, reason);
                return PublishWorkflowRun(run, origin);
            }
            finally { _workflowGate.Release(); }
        }

        public async Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidenceAsync(string runId)
        {
            var session = _sessions.Current;
            if (session == null)
                return new Semanticus.Engine.Evidence.EvidenceArtifact { Error = "No open model. Open a model before exporting workflow evidence." };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var modelIdentity = PaneIdentity(session, _live);

            await _workflowGate.WaitAsync();
            try
            {
                WorkflowRunState run;
                try { run = _workflowRuns.Require(runId); }
                catch (Exception ex) { return new Semanticus.Engine.Evidence.EvidenceArtifact { Note = ex.Message }; }
                if (run.Status == "active")
                    return new Semanticus.Engine.Evidence.EvidenceArtifact { Note = "This workflow is still active. Finish or abort it before exporting its evidence." };
                var sameOwner = !string.IsNullOrWhiteSpace(run.ModelIdentity)
                    ? string.Equals(run.ModelIdentity, modelIdentity, StringComparison.Ordinal)
                    : string.Equals(run.SessionId, session.Id, StringComparison.Ordinal);
                if (!sameOwner || (!string.IsNullOrWhiteSpace(run.ModelName) && !string.Equals(run.ModelName, modelName, StringComparison.Ordinal)))
                    return new Semanticus.Engine.Evidence.EvidenceArtifact { Note = "The workflow run belongs to a different model. Open its owning model before exporting the evidence." };
                return Semanticus.Engine.Evidence.EvidenceArtifact.Seal(Semanticus.Engine.Evidence.WorkflowEvidenceRenderer.Build(run));
            }
            finally { _workflowGate.Release(); }
        }

        /// <summary>Broadcast the transition; a TERMINAL transition additionally publishes the full run
        /// record as an ActivityEvent so the ExperienceTee persists it (learning-loop §3.1 — the
        /// answers/declines/evidence outlive the session). Called under _workflowGate.</summary>
        private WorkflowRunView PublishWorkflowRun(WorkflowRunState run, string origin = null)
        {
            var view = WorkflowRunner.BuildView(run);
            _sessions.Bus.PublishWorkflow(view);
            if (run.Status != "active")
            {
                _workflowAux.Remove(run.RunId);
                _ = PublishActivityAsync(new ActivityEvent
                {
                    Kind = "workflow_run",
                    Origin = origin ?? "human",
                    Label = $"Workflow '{run.Def.Name}' {run.Status} ({run.Results.Count(r => r.Status == "passed")}/{run.Results.Length} steps passed)",
                    Target = run.Def.Name,
                    Ok = run.Status == "completed",
                    Result = WorkflowRunner.BuildRunRecord(run),
                });
            }
            return view;
        }

        private static Dictionary<string, AnswerValue> ParseAnswers(string answersJson)
        {
            var answers = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(answersJson)) return answers;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(answersJson); }
            catch (Exception ex) { throw new InvalidOperationException("answers must be a JSON object of {inputName: value | {\"declined\":true,\"reason\":\"...\"}}: " + ex.Message); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("answers must be a JSON OBJECT keyed by gate-input name.");
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("declined", out var d) && d.ValueKind == JsonValueKind.True)
                        answers[p.Name] = new AnswerValue { Declined = true, DeclineReason = p.Value.TryGetProperty("reason", out var r) ? r.GetString() : null };
                    else
                        answers[p.Name] = new AnswerValue
                        {
                            Value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText(),
                        };
                }
            }
            return answers;
        }

        // ---- verify executors (the engine-evaluated half of a gate — never self-graded) -----------

        /// <summary>Target-object convention: the run's LATEST answered input of type `objectRef`
        /// names the object the verifies act on (seed workflows collect it at the create/edit step).
        /// A verify that needs a target and can't find one FAILS with instructive text — an
        /// unrunnable check must never quietly pass a hard gate.</summary>
        private async Task<VerifyResult> ExecuteWorkflowVerifyAsync(
            VerifySpec spec, WorkflowStep step, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            switch (spec.Kind)
            {
                case "dax_probe": return await WorkflowDaxProbeAsync(spec, run, answers);
                case "dax_equivalence": return await WorkflowDaxEquivalenceAsync(spec, run, answers);
                case "bpa_clean": return await WorkflowBpaCleanAsync(spec, run, answers);
                case "readiness_rescan": return await WorkflowReadinessRescanAsync(spec, run);
                case "benchmark_delta": return await WorkflowBenchmarkDeltaAsync(spec, run, answers);
                case "workflow_admissible": return await WorkflowAdmissibleAsync(spec, answers);
                case "baseline_captured": return await WorkflowBaselineCapturedAsync(spec, run, answers);   // certified totals: the labeled baseline must exist AND contain the declared figures
                case "interview_replay": return await WorkflowInterviewReplayAsync(spec);   // LocalEngine.Interview.cs — replays the saved pack; no target/probe/snapshot needed
                case "impact_assessment": return await WorkflowImpactAssessmentAsync(spec, run, answers);
                case "baseline_exists": return await WorkflowBaselineExistsAsync(spec, run, answers);
                case "baseline_unchanged": return await WorkflowBaselineUnchangedAsync(spec, answers);
                case "tests_replay": return await WorkflowTestsReplayAsync();
                case "plan_item_staged": return await WorkflowPlanItemAsync(spec, run, answers, applied: false);
                case "plan_item_applied": return await WorkflowPlanItemAsync(spec, run, answers, applied: true);
                default:
                    return new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = $"unknown verify kind '{spec.Kind}'." };
            }
        }

        private string LatestObjectRefAnswer(WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            string found = null;
            foreach (var st in run.Def.Steps)
                foreach (var input in st.Gate?.Inputs ?? Array.Empty<GateInput>())
                    if (input.Type == "objectRef" && answers.TryGetValue(input.Name, out var a) && a.Answered)
                        found = a.Value;
            return found;
        }

        /// <summary>The workflow form of T87's referee. An answered `reportPaths` input supplies local PBIR
        /// definitions; an explicit decline narrows the certificate to model-only scope. Known impact is expected
        /// and passes as NeedsReview evidence, while Broken holds a hard gate. For intent=='rename' an Unknown
        /// assessment reports "skipped" (Unknown/Overridden, NOT a hard-gate block) — the accountable rename gap is
        /// carried into the Step-4 mandatory decision rather than blocking here.</summary>
        private async Task<VerifyResult> WorkflowImpactAssessmentAsync(VerifySpec spec, WorkflowRunState run,
            IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "impact_assessment";
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef input names the change target." };

            answers.TryGetValue("reportPaths", out var reportAnswer);
            var modelOnly = reportAnswer?.Declined == true;
            var paths = modelOnly ? Array.Empty<string>() : ParseWorkflowReportPaths(reportAnswer?.Value);
            Lineage.ImpactAssessmentResult result;
            try
            {
                result = await ImpactAssessmentAsync(new Lineage.ImpactAssessmentRequest
                {
                    ObjectRef = target, Intent = spec.Intent ?? "change", Scope = modelOnly ? "model" : "modelAndReports", ReportPaths = paths,
                });
            }
            catch (Exception ex)
            {
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "assessment could not run: " + ex.Message };
            }

            var coverage = string.Join(", ", result.Coverage.Select(c => $"{c.Area}={c.Status}"));
            var checks = result.ReplayChecks.Length == 0 ? "none" : string.Join("; ", result.ReplayChecks.Take(8).Select(c => c.Title));
            var gaps = result.Unknowns.Length == 0 ? "none" : string.Join(" | ", result.Unknowns.Take(5));
            var detail = $"{result.Verdict}: {result.Summary} Scope: {result.Scope}; coverage: {coverage}; "
                       + $"reports/visuals impacted: {result.ReportsImpacted}/{result.VisualsImpacted}; replay checks: {checks}; gaps: {gaps}.";
            var pass = result.Verdict == "Verified" || result.Verdict == "NeedsReview";
            var accountableRenameGap = string.Equals(spec.Intent, "rename", StringComparison.Ordinal) && result.Verdict == "Unknown";
            return new VerifyResult { Kind = kind, Status = pass ? "passed" : accountableRenameGap ? "skipped" : "failed", Detail = detail };
        }

        private static string[] ParseWorkflowReportPaths(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            var text = value.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        return doc.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                }
                catch { /* the assessment reports the unresolvable literal below */ }
            }
            return text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        /// <summary>Proves a recorded capture id is an engine-held, representative, complete baseline for the
        /// declared target. A caller can decline the baseline input when there is no live evidence to capture;
        /// an answered id never degrades to a self-attestation.</summary>
        private async Task<VerifyResult> WorkflowBaselineExistsAsync(VerifySpec spec, WorkflowRunState run,
            IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "baseline_exists";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var id) || !id.Answered || string.IsNullOrWhiteSpace(id.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the captureId returned by capture_baseline." };
            BaselineState baseline;
            await _baselineGate.WaitAsync();
            try { baseline = _baselines.Get(id.Value.Trim()); }
            finally { _baselineGate.Release(); }
            if (baseline == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{id.Value}' is not held in this model session. Run capture_baseline, then record its captureId." };
            var target = LatestObjectRefAnswer(run, answers);
            if (!string.Equals(baseline.Root, target, StringComparison.OrdinalIgnoreCase))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{baseline.Id}' belongs to {baseline.Root}, not the declared target {target}." };
            if (baseline.Entries.Count == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{baseline.Id}' contains no measurable downstream measures." };
            var errors = baseline.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Error)).ToArray();
            if (errors.Length > 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{baseline.Id}' has {errors.Length} measure error(s): " + string.Join("; ", errors.Take(3).Select(e => e.Name + ": " + e.Error)) };
            if (baseline.GroupBy.Length == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{baseline.Id}' is grand-total only. Capture again with representative groupBy columns, or explicitly decline the baseline with the reason thin evidence is acceptable." };
            if (baseline.Entries.Any(e => e.Truncated))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"capture '{baseline.Id}' hit the row cap, so coverage is incomplete. Narrow the representative grid and capture again." };
            return new VerifyResult
            {
                Kind = kind, Status = "passed",
                Detail = $"capture '{baseline.Id}' holds {baseline.Entries.Count} affected measure(s) across [{string.Join(", ", baseline.GroupBy)}] at revision {baseline.Revision}; no errors or truncation.",
            };
        }

        /// <summary>Replay the complete Tests suite as a safe superset of T87's affected saved checks. Failures and
        /// missing bindings hold the gate; offline or otherwise unverified coverage stays visible in the certificate.</summary>
        private async Task<VerifyResult> WorkflowTestsReplayAsync()
        {
            const string kind = "tests_replay";
            var run = await RunTestSuiteAsync(false, "workflow");
            if (!string.IsNullOrWhiteSpace(run.Error))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = run.Error };
            var h = run.Health;
            if (h == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "the Tests suite returned no health result." };
            var detail = $"grade {h.Grade} at {h.CoveragePct:0.#}% coverage: {h.Passed} pass, {h.Failed} fail, {h.Suspect} suspect, {h.NotVerifiable} not verifiable, {h.Missing} missing; {run.DefinitionCount} saved test(s) replayed.";
            if (h.Failed > 0 || h.Missing > 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = detail };
            // NotVerifiable is NEVER a pass (Tests I1). The safety net exists to catch the data-integrity and
            // reconciliation breaks a rename/blast-radius causes — so gate on THOSE categories deciding, not on an
            // aggregate that a passing static-Security check would inflate. When the safety-net population decided
            // nothing (offline, every integrity/correctness probe NotVerifiable), report "skipped" (-> Unknown, no
            // green) rather than a green "passed" over zero decided integrity checks.
            var safetyNet = (h.Categories ?? Array.Empty<TestCategoryHealth>())
                .Where(c => c.Category == "Integrity" || c.Category == "Correctness");
            var decided = safetyNet.Sum(c => c.Passed + c.Failed);
            if (decided == 0)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "the safety-net suite could not decide any data-integrity or reconciliation check in this session (offline or all probes not verifiable) — connect a live model to replay it. " + detail };
            return new VerifyResult { Kind = kind, Status = "passed", Detail = detail };
        }

        private async Task<VerifyResult> WorkflowBaselineUnchangedAsync(VerifySpec spec,
            IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "baseline_unchanged";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var id) || !id.Answered || string.IsNullOrWhiteSpace(id.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the captureId returned before the rename." };
            var result = await CompareBaselineAsync(id.Value.Trim(), null, "workflow");
            if (!string.Equals(result.Status, "ok", StringComparison.Ordinal))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = result.Message ?? $"baseline compare returned {result.Status}." };
            return new VerifyResult
            {
                Kind = kind, Status = result.Safe ? "passed" : "failed",
                Detail = result.Message + $" Compared {result.Diffs.Length} affected measure(s); {result.MovedCount} moved, {result.MissingCount} missing.",
            };
        }

        /// <summary>Bind the preview and apply steps to the actual shared Change Plan item. The workflow never accepts
        /// a pasted description of a rename in place of the proposed/applied plan state.</summary>
        private async Task<VerifyResult> WorkflowPlanItemAsync(VerifySpec spec, WorkflowRunState run,
            IReadOnlyDictionary<string, AnswerValue> answers, bool applied)
        {
            var kind = applied ? "plan_item_applied" : "plan_item_staged";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var id) || !id.Answered || string.IsNullOrWhiteSpace(id.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the rename Change Plan item id." };
            ChangeItem item;
            await _planGate.WaitAsync();
            try { item = _plans.Current?.Find(id.Value.Trim())?.Clone(); }
            finally { _planGate.Release(); }
            if (item == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"plan item '{id.Value}' was not found. Stage the rename with add_plan_item, then record its id." };
            var target = LatestObjectRefAnswer(run, answers);
            answers.TryGetValue("newName", out var newName);
            if (!string.Equals(item.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.ObjectRef, target, StringComparison.OrdinalIgnoreCase)
                || newName?.Answered != true || !string.Equals(item.After, newName.Value, StringComparison.Ordinal))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"plan item '{item.Id}' is not the declared rename {target} -> '{newName?.Value}'." };
            var expectedStatus = applied ? "applied" : "proposed";
            if (!string.Equals(item.Status, expectedStatus, StringComparison.Ordinal))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"plan item '{item.Id}' is {item.Status}, not {expectedStatus}. " + (applied ? "Approve and apply that one item, then retry." : "Keep the preview proposed until the review steps are complete.") };
            if (applied)
            {
                var expectedRef = RenamedObjectRef(target, newName.Value);
                var exists = await _sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, expectedRef) != null);
                if (!exists)
                    return new VerifyResult { Kind = kind, Status = "failed", Detail = $"plan item '{item.Id}' says applied, but the renamed object '{expectedRef}' does not resolve." };
                return new VerifyResult { Kind = kind, Status = "passed", Detail = $"plan item '{item.Id}' applied {target} -> {expectedRef} through the reference-aware rename path." };
            }
            return new VerifyResult { Kind = kind, Status = "passed", Detail = $"plan item '{item.Id}' is a proposed reference-aware rename of {target} to '{newName.Value}'; nothing has been applied yet." };
        }

        private static string RenamedObjectRef(string oldRef, string newName)
        {
            var slash = (oldRef ?? "").LastIndexOf('/');
            if (slash >= 0) return oldRef.Substring(0, slash + 1) + newName;
            var colon = (oldRef ?? "").IndexOf(':');
            return colon >= 0 ? oldRef.Substring(0, colon + 1) + newName : newName;
        }

        /// <summary>§9 "Claude authors workflows": the meta-gate. The `probe:` input holds the NAME of a
        /// workflow the agent just wrote with save_workflow; this runs the SAME admission dry-run check_workflow
        /// runs (parse-valid + every trigger/op real + every verify probe/when resolved + verifies have their
        /// target) and passes ONLY if that report is Ok — no warns. save_workflow already refuses an unparseable
        /// file, so this catches the deeper defects a parse can't: a phantom op, a probe naming no input, a dax
        /// verify with no objectRef target. Offline-safe (check is a static resolve, no live model needed) — the
        /// one verify kind that gates the authoring of workflows on the workflows being real.</summary>
        private async Task<VerifyResult> WorkflowAdmissibleAsync(VerifySpec spec, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "workflow_admissible";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var nameAns) || !nameAns.Answered || string.IsNullOrWhiteSpace(nameAns.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the NAME of the workflow you saved (save_workflow first, then record its name in that input)." };

            var name = nameAns.Value.Trim();
            WorkflowCheckReport report;
            try { report = await CheckWorkflowAsync(name); }
            catch (Exception ex)
            {
                // Most often: the named workflow was never saved (or the name is misspelled) — instructive, not a crash.
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"could not check '{name}': {ex.Message}" };
            }

            if (report.Ok)
                return new VerifyResult { Kind = kind, Status = "passed", Detail = $"'{name}' passes the admission dry-run — it parses, every op is real, and every gate binding resolves. It is safe to enable." };

            var warns = (report.Findings ?? Array.Empty<CheckFinding>())
                .Where(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal))
                .Select(f => f.Message).ToArray();
            var reason = report.ParseError != null
                ? "parse error: " + report.ParseError
                : (warns.Length > 0 ? string.Join(" | ", warns) : "the admission dry-run reported it is not Ok.");
            return new VerifyResult { Kind = kind, Status = "failed", Detail = $"'{name}' is NOT admissible — {reason}. Fix it with save_workflow and re-submit (get_workflow shows the current text; check_workflow shows the full report)." };
        }

        /// <summary>CERTIFIED TOTALS (month-end close): the enforcement gate — bound to what the close DECLARED,
        /// FULLY (ref + context + value), not a bare ref-string presence. Passes ONLY if the labeled certified baseline
        /// (a) exists, (b) is not tampered (content hash), and (c) contains, for EVERY declared control total, a
        /// certified figure at THAT ref AND THAT context whose certified value equals the declared signed-off figure
        /// within the certified tolerance. The control_totals declaration is STRICTLY structured (`measure:Table/Name ~
        /// &lt;dax context or (grand total)&gt; ~ &lt;exact value&gt;`, one per line); a line the gate cannot parse is a
        /// REFUSAL, never a silent downgrade to attestation. It proves the figures were RECORDED at their declared
        /// contexts + values (offline-safe file check); it does NOT claim they still hold (compare_baseline(label:…)).</summary>
        private async Task<VerifyResult> WorkflowBaselineCapturedAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "baseline_captured";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var labelAns) || !labelAns.Answered || string.IsNullOrWhiteSpace(labelAns.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the LABEL the certified figures were captured under (capture_baseline with that label first, then record it here)." };
            var label = labelAns.Value.Trim();

            var file = CertifiedFilePath();
            if (file == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "there is nowhere to store certified figures yet — open or save the model so its .semanticus sidecar has a home, then capture the close's totals with capture_baseline(label:…)." };

            var cf = CertifiedStore.Load(file, out var corrupt);
            if (corrupt)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "the certified-figures store (.semanticus/certified-baselines.json) is present but unreadable — repair or move it, then re-capture the close's figures." };

            var bl = CertifiedStore.Find(cf, label);
            if (bl == null || bl.Entries.Count == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"no certified figures were captured under '{label}'. Capture every control total and the headline at its stated context with capture_baseline(label:\"{label}\") BEFORE signing off — that recording is what lets a later refresh or edit that moves a signed-off number be caught (detection, not prevention). This gate cannot pass until they are captured." };
            // P1-3: never bless a tampered record.
            if (!CertifiedStore.HashMatches(bl))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the certified baseline '{label}' has been modified since capture (content hash mismatch) — refusing to pass the close on a tampered record. Re-capture the figures under a new label." };

            // The DECLARED control totals, strictly parsed. An unparseable line is a REFUSAL (P1-A) — never dropped
            // silently into an "attested" bucket where a typo would slip the gate.
            var (declared, bad) = ParseDeclaredControlTotals(run);
            if (bad.Count > 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the close's control-totals declaration has {bad.Count} line(s) the gate cannot parse as `measure:Table/Name ~ <dax context or (grand total)> ~ <exact value>`: {string.Join(" | ", bad.Take(5))}. Every certified figure must be machine-checkable — fix or remove each unparseable line; the gate will not silently treat it as attested." };
            if (declared.Count == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "the close declared no control totals to certify. Declare each certified figure as `measure:Table/Name ~ <dax context or (grand total)> ~ <exact value>` (one per line) so the gate can bind ref + context + value — it cannot pass on an empty declaration." };

            var tol = bl.Tolerance > 0 ? bl.Tolerance : CertifiedStore.Tolerance;
            var problems = new List<string>();
            foreach (var d in declared)
            {
                var declCtx = CertifiedStore.NormalizeContext(new[] { d.Context });
                var entry = bl.Entries.FirstOrDefault(e => RefEq(e.Ref, d.Ref)
                    && string.Equals(CertifiedStore.NormalizeContext(e.Filters), declCtx, StringComparison.Ordinal));
                var where = string.IsNullOrWhiteSpace(d.Context) ? "grand total" : d.Context;
                if (entry == null) { problems.Add($"{d.Ref} at [{where}] is NOT certified at that context"); continue; }
                // P2-3: a figure whose context references a measure can NEVER be re-checked identically — a hard close
                // must not sign off on a number nobody can ever verify. Fail it, naming why.
                if (!entry.StabilityProvable) { problems.Add($"{d.Ref} at [{where}] is certified but its context references a measure, so it can never be re-checked identically — restate the context with columns only (no measure reference), then re-capture"); continue; }
                var cell = entry.Cells?.Length == 1 ? entry.Cells[0] : null;
                if (cell == null || !cell.Number.HasValue) { problems.Add($"{d.Ref} at [{where}] is certified but not a single numeric value to check against {DaxBench.Fmt(d.Value)}"); continue; }
                if (!DaxBench.ValuesEqual(cell.Number.Value, d.Value))
                    problems.Add($"{d.Ref} at [{where}] is certified as {DaxBench.Fmt(cell.Number.Value)} but the close declared {DaxBench.Fmt(d.Value)}");
            }
            if (problems.Count > 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the close declared {declared.Count} figure(s), but {problems.Count} do NOT match what was certified under '{label}': {string.Join("; ", problems)}. Capture each at its DECLARED context with capture_baseline(label:\"{label}\", filters:[…the declared context…]) so its certified value equals the signed-off figure. This gate cannot pass until every declared figure is certified at its declared context and value." };

            return new VerifyResult
            {
                Kind = kind,
                Status = "passed",
                Detail = $"{declared.Count} declared figure(s) certified under '{label}' at their declared contexts and matching their signed-off values (within the certified tolerance {tol.ToString("0.0e0", System.Globalization.CultureInfo.InvariantCulture)}). This proves the figures were RECORDED; run compare_baseline(label:\"{label}\") after any refresh or edit to check whether they still hold.",
            };
        }

        /// <summary>Match a certified entry's ref to a declared ref, tolerating a present/absent 'measure:' prefix.</summary>
        private static bool RefEq(string entryRef, string declaredRef)
        {
            string Bare(string r) => (r ?? "").Trim().Replace("measure:", "", StringComparison.OrdinalIgnoreCase).Trim();
            return string.Equals((entryRef ?? "").Trim(), (declaredRef ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Bare(entryRef), Bare(declaredRef), StringComparison.OrdinalIgnoreCase);
        }

        // "exact number": an optional leading sign + digits + optional single decimal point. NO thousands separators
        // (so a European "1,5" can't misread as 15), NO currency, NO parentheses-negatives (P3-2).
        private const System.Globalization.NumberStyles ExactNumber =
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint;

        /// <summary>Parse the close's control_totals declaration (from instance provenance) into structured triples.
        /// Each non-blank line MUST be `measure:Table/Name ~ &lt;dax context, or the literal (grand total)&gt; ~ &lt;exact
        /// value&gt;`; a line that does not parse goes to `bad` (the gate REFUSES on any), never silently dropped. A BLANK
        /// context is REFUSED (P3-1) — grand total must be written literally, so an empty middle can't silently bind to
        /// the grand-total entry. The value must be an exact number: no thousands separators, currency, or 'M'/'K' (P3-2).</summary>
        private static (List<(string Ref, string Context, double Value)> declared, List<string> bad) ParseDeclaredControlTotals(WorkflowRunState run)
        {
            var declared = new List<(string, string, double)>();
            var bad = new List<string>();
            var text = ControlTotalsSlotText(run);
            if (string.IsNullOrWhiteSpace(text)) return (declared, bad);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split('~');
                if (parts.Length != 3) { bad.Add(line); continue; }
                var reff = parts[0].Trim();
                var ctx = parts[1].Trim();
                var valStr = parts[2].Trim();
                if (!reff.StartsWith("measure:", StringComparison.OrdinalIgnoreCase) || reff.Length <= "measure:".Length) { bad.Add(line); continue; }
                if (string.Equals(ctx, "(grand total)", StringComparison.OrdinalIgnoreCase)) ctx = "";
                else if (ctx.Length == 0) { bad.Add(line); continue; }   // P3-1: a blank context must NOT silently mean grand total
                if (!double.TryParse(valStr, ExactNumber, System.Globalization.CultureInfo.InvariantCulture, out var val)) { bad.Add(line); continue; }
                declared.Add((reff, ctx, val));
            }
            return (declared, bad);
        }

        private static string ControlTotalsSlotText(WorkflowRunState run)
        {
            if (run?.Def?.Provenance == null || !run.Def.Provenance.TryGetValue("slot_values", out var sv) || string.IsNullOrWhiteSpace(sv)) return null;
            try
            {
                using var doc = JsonDocument.Parse(sv);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("control_totals", out var ct) && ct.ValueKind == JsonValueKind.String
                    ? ct.GetString() : null;
            }
            catch { return null; }
        }

        /// <summary>Evaluate the target measure on the live model and compare it to the user's known-good number.
        /// A control total can carry the DAX context where it is true; a bare number keeps the legacy grand-total
        /// path. v1 proves one scalar at one context; a per-group matrix is a later increment.</summary>
        private async Task<VerifyResult> WorkflowDaxProbeAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "dax_probe";
            if (_live == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "offline — no live connection, the probe was NOT verified (open_live/open_local and re-submit for real evidence)." };
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var expected) || !expected.Answered)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' has no answered value to compare against." };
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef-typed input names the measure to probe — the workflow must collect one (e.g. an input `target` of type objectRef) before this gate." };

            var s = _sessions.Require();
            var name = await s.ReadAsync(m => ObjectRefs.Resolve(m, target) is Measure mm ? mm.Name : null);
            if (name == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"'{target}' does not resolve to a measure on the current model." };

            // The opt-in context shape is deliberately narrow. Hard measures need proof at a slice, while every
            // existing bare control total must keep its byte-for-byte grand-total query.
            var (context, valStr) = DaxBench.SplitControlValue(expected.Value);
            var probeExpr = Baseline.MeasureRefExpr(name);
            var query = string.IsNullOrWhiteSpace(context)
                ? DaxBench.BuildProbeQuery(probeExpr, Array.Empty<string>(), Array.Empty<string>())
                : DaxBench.BuildScalarContextQuery(probeExpr, context);
            var rs = await _live.ExecuteAsync(query, 10, 120);
            if (!string.IsNullOrEmpty(rs.Error))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "probe query failed: " + rs.Error };
            var rows = Baseline.KeyRows(rs, 0);
            var actual = rows.Values.FirstOrDefault();
            var matches = double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var want)
                ? DaxBench.ValuesEqual(actual, want)
                : DaxBench.ValuesEqual(actual, valStr);
            var at = string.IsNullOrWhiteSpace(context) ? "at the model's grand total" : $"at [{context}]";
            return new VerifyResult
            {
                Kind = kind,
                Status = matches ? "passed" : "failed",
                Detail = matches
                    ? $"[{name}] evaluated {at} to {DaxBench.Fmt(actual)} - matches the user's known-good value {valStr}."
                    : $"[{name}] evaluated {at} to {DaxBench.Fmt(actual)} but the user's known-good value is {valStr} - the measure does not produce the number the user can check.",
            };
        }

        /// <summary>Prove the target measure's CURRENT expression equivalent to the recorded original
        /// (the `probe:` input holds the pre-rewrite DAX, captured at an earlier step). The matrix
        /// comes from an answered `equivalenceGrid` input (comma-separated columns); absent = grand
        /// total only, honestly disclosed as thin.</summary>
        private async Task<VerifyResult> WorkflowDaxEquivalenceAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "dax_equivalence";
            if (_live == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "offline — no live connection, equivalence was NOT verified." };
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var original) || !original.Answered)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the ORIGINAL expression (recorded before the rewrite) to compare against." };
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef-typed input names the rewritten measure." };

            var s = _sessions.Require();
            var current = await s.ReadAsync(m => ObjectRefs.Resolve(m, target) is Measure mm ? mm.Expression : null);
            if (current == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"'{target}' does not resolve to a measure on the current model." };

            var grid = answers.TryGetValue("equivalenceGrid", out var g) && g.Answered
                ? g.Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray()
                : Array.Empty<string>();
            var eq = await DaxBench.VerifyEquivalenceAsync(_live, original.Value, current, grid, Array.Empty<string>(), 100000);
            if (!string.IsNullOrEmpty(eq.Error))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "equivalence query failed: " + eq.Error };
            var thin = grid.Length == 0 ? " (grand-total only — answer `equivalenceGrid` with group-by columns for a per-context proof)" : "";
            return new VerifyResult
            {
                Kind = kind,
                Status = eq.AllMatch && !eq.Truncated ? "passed" : "failed",
                Detail = eq.AllMatch && !eq.Truncated
                    ? $"original and current expressions match across {eq.RowsCompared} context(s){thin}."
                    : eq.Truncated
                        ? $"comparison hit the row cap ({eq.RowsCompared} rows) — coverage incomplete, a match is not a proof."
                        : $"{eq.MismatchCount}/{eq.RowsCompared} context(s) DIFFER — e.g. " + string.Join("; ", eq.Mismatches.Take(3).Select(m => $"{m.Context}: {m.ValueA} vs {m.ValueB}")),
            };
        }

        /// <summary>Prove the rewrite did not REGRESS on speed: time the target measure's grand-total query
        /// NOW and compare its warm median against a recorded BASELINE. The `probe:` input carries the
        /// pre-rewrite warm median in ms (run benchmark_dax_coldwarm BEFORE the rewrite, answer with the warm
        /// median). PASSED iff nowMedian &lt;= baseline * tolerance (an OPTIONAL answered `benchmarkTolerance`
        /// multiplier; default 1.10). Recorded-evidence pattern, exactly like dax_equivalence's original —
        /// offline can't measure, so it SKIPS honestly (a skip must never block a hard gate on a fabricated pass).</summary>
        private async Task<VerifyResult> WorkflowBenchmarkDeltaAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "benchmark_delta";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Validate the RECORDED answers before the live-connection check: a missing/malformed baseline or a
            // missing target is a workflow-answer error worth teaching whether or not a connection exists (and it
            // keeps these branches observable offline — the live timing itself can't be), so fail-fast on them,
            // then skip honestly when there's simply no connection to time against.
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var baseAns) || !baseAns.Answered)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' has no answered BASELINE (the pre-rewrite warm median in ms) — run benchmark_dax_coldwarm BEFORE the rewrite and answer the input with the warm median." };
            if (!double.TryParse(baseAns.Value, System.Globalization.NumberStyles.Any, inv, out var baselineMs))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the baseline '{baseAns.Value}' is not a number of milliseconds — record it with benchmark_dax_coldwarm (use the warm median) and answer the input with just the number." };

            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef-typed input names the measure to benchmark — the workflow must collect one (e.g. an input `target` of type objectRef) before this gate." };

            if (_live == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "offline — no live connection, the benchmark delta was NOT verified (open_live/open_local and re-submit for real timing evidence)." };

            var s = _sessions.Require();
            var name = await s.ReadAsync(m => ObjectRefs.Resolve(m, target) is Measure mm ? mm.Name : null);
            if (name == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"'{target}' does not resolve to a measure on the current model." };

            // REUSE the benchmark op's warm-run machinery: DaxBench.BenchmarkAsync(…, 6) runs the query 6×,
            // DISCARDS the first (cache warm-up) and reports WarmMedianMs = the MEDIAN of the remaining 5 — exactly
            // the semantics this gate needs. It's a plain static helper (not op-shaped), so reuse beats a bespoke
            // Stopwatch loop. Timed over the SAME grand-total probe query dax_probe uses, so both verifies measure
            // the identical thing.
            var bench = await DaxBench.BenchmarkAsync(_live, DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(name), Array.Empty<string>(), Array.Empty<string>()), 6);
            if (!string.IsNullOrEmpty(bench.Error))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "benchmark query failed: " + bench.Error };

            var tolerance = answers.TryGetValue("benchmarkTolerance", out var tv) && tv.Answered
                            && double.TryParse(tv.Value, System.Globalization.NumberStyles.Any, inv, out var parsed) && parsed > 0
                ? parsed : 1.10;   // default: allow up to 10% over baseline before calling it a regression
            double medianMs = bench.WarmMedianMs;
            var ratio = baselineMs > 0 ? medianMs / baselineMs : double.PositiveInfinity;
            var passed = medianMs <= baselineMs * tolerance;
            // Detail ALWAYS carries the numbers (pass or fail) + the honest noise disclosure — a near-tolerance
            // wall-clock verdict is inconclusive, not a guarantee.
            var detail = $"[{name}] warm median {medianMs.ToString("0.#", inv)} ms vs baseline {baselineMs.ToString("0.#", inv)} ms "
                       + $"(ratio {ratio.ToString("0.##", inv)}, tolerance {tolerance.ToString("0.##", inv)}, 5 warm runs)"
                       + (passed ? "" : " — the rewrite regressed past tolerance; revert with update_measure or re-optimize.")
                       + " Single-machine wall-clock; treat near-tolerance results as inconclusive.";
            return new VerifyResult { Kind = kind, Status = passed ? "passed" : "failed", Detail = detail };
        }

        /// <summary>Diff ACTIVE violations against the start-of-run snapshot: the step must introduce
        /// no NEW violations (scope object = on the target only; model = anywhere).</summary>
        private async Task<VerifyResult> WorkflowBpaCleanAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "bpa_clean";
            if (_sessions.Current == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "no open session." };
            _workflowAux.TryGetValue(run.RunId, out var aux);
            if (aux?.BpaKeys == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no start-of-run BPA snapshot exists (no session was open at start_workflow) — a before/after diff is impossible; abort and restart the workflow with the model open." };
            var before = aux.BpaKeys;
            var scan = await BpaScanAsync();
            var fresh = scan.Violations.Where(v => !v.Waived).Where(v => !before.Contains(v.RuleId + "|" + v.ObjectRef)).ToArray();
            if (string.Equals(spec.Scope, "object", StringComparison.OrdinalIgnoreCase))
            {
                var target = LatestObjectRefAnswer(run, answers);
                if (target == null)
                    return new VerifyResult { Kind = kind, Status = "failed", Detail = "scope: object but no answered objectRef-typed input names the object to check." };
                fresh = fresh.Where(v => string.Equals(v.ObjectRef, target, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            return new VerifyResult
            {
                Kind = kind,
                Status = fresh.Length == 0 ? "passed" : "failed",
                Detail = fresh.Length == 0
                    ? "no new BPA violations vs the start of the run."
                    : $"{fresh.Length} NEW BPA violation(s): " + string.Join("; ", fresh.Take(10).Select(v => $"{v.RuleId} on {v.ObjectName}")) + (fresh.Length > 10 ? " …" : ""),
            };
        }

        private async Task<VerifyResult> WorkflowReadinessRescanAsync(VerifySpec spec, WorkflowRunState run)
        {
            const string kind = "readiness_rescan";
            if (_sessions.Current == null)
                return new VerifyResult { Kind = kind, Status = "skipped", Detail = "no open session." };
            _workflowAux.TryGetValue(run.RunId, out var aux);
            if (aux?.ReadinessKeys == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no start-of-run readiness snapshot exists (no session was open at start_workflow) — a before/after diff is impossible; abort and restart the workflow with the model open." };
            var before = aux.ReadinessKeys;
            var sc = await AiReadinessScanAsync();
            var fresh = sc.Findings.Where(f => !f.Waived).Where(f => !before.Contains(f.RuleId + "|" + f.ObjectRef)).ToArray();
            var delta = aux == null ? 0 : sc.Overall - aux.ReadinessOverall;
            return new VerifyResult
            {
                Kind = kind,
                Status = fresh.Length == 0 ? "passed" : "failed",
                Detail = fresh.Length == 0
                    ? $"no new readiness findings; score {sc.Overall:0.#} ({(delta >= 0 ? "+" : "")}{delta:0.#} vs start of run)."
                    : $"{fresh.Length} NEW readiness finding(s): " + string.Join("; ", fresh.Take(10).Select(f => $"{f.RuleId} on {f.ObjectName}")) + (fresh.Length > 10 ? " …" : ""),
            };
        }
    }
}
