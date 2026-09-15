using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // §10.6 [D2]: the last readiness grade computed THIS session (set by AiReadinessScan*), so the
        // `model.readinessGrade` activation fact is lazy+cached and NEVER force-scanned on a menu/policy read.
        // null = never scanned ⇒ the term is "unknown" ⇒ grade comparisons are false (the rule stays dormant).
        private string CurrentReadinessGrade() => CurrentReadinessGrade(_sessions.CurrentContext);

        private static string CurrentReadinessGrade(SessionContext context)
        {
            lock (context.ReadinessGate) return context.ReadinessGrade;
        }

        private void SetReadinessGrade(SessionContext context, string grade, string operation)
        {
            lock (context.ReadinessGate)
            {
                EnsureContextCurrent(context, operation);
                context.ReadinessGrade = grade;
            }
        }

        internal int ActiveWorkflowRunsForTest => _sessions.CurrentContext.WorkflowRuns.ActiveRuns().Count();

        // ---- library ---------------------------------------------------------------------------

        /// <summary>User workflows live in the project's `.semanticus/workflows` (beside the model;
        /// workspace fallback for live/unsaved sessions — the experience-log placement rule); the
        /// stock library ships read-only beside the engine binary. A user file SHADOWS a stock one
        /// of the same name (copy-to-customise).</summary>
        /// <summary>The project's `.semanticus` sidecar dir (beside the model; PBIP hop included), or the raw
        /// workspace fallback for a live/unsaved session — the shared root for both the workflows and the
        /// workflow-templates dirs. null when there is nowhere to persist (no model, no workspace).</summary>
        private string SidecarDir() => SidecarDir(_sessions.CurrentContext);

        private string SidecarDir(SessionContext context)
        {
            var anchor = context.Session?.SourcePath;
            // LayoutStore.DirFor already RETURNS the `.semanticus` sidecar dir (PBIP hop included) — only the
            // raw-workspace fallback needs the segment appended (the McpSmoke test-agent caught the doubling).
            return !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                 : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
        }

        private (string userDir, string stockDir) WorkflowDirs() => WorkflowDirs(_sessions.CurrentContext);

        private (string userDir, string stockDir) WorkflowDirs(SessionContext context)
        {
            var sidecar = SidecarDir(context);
            return (sidecar == null ? null : Path.Combine(sidecar, "workflows"),
                    Path.Combine(AppContext.BaseDirectory, "workflows"));
        }

        private const string RequiredWorkflowRevisionsKey = "requiredWorkflowRevisions";

        private enum WorkflowRevisionFallback { Baseline, Current }

        private sealed class ResolvedWorkflowFile
        {
            public string Path;
            public string Source;
            public string Revision;
        }

        private sealed class ResolvedWorkflowLibrary
        {
            public Dictionary<string, ResolvedWorkflowFile> Files = new Dictionary<string, ResolvedWorkflowFile>(StringComparer.Ordinal);
            public Dictionary<string, string> RequiredStockRevisions = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static string WorkflowSettingsFileFor(string userDir) =>
            userDir == null ? null : Path.Combine(Path.GetDirectoryName(userDir), "workflow-settings.json");

        private static Dictionary<string, string> WorkflowFiles(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            return Directory.EnumerateFiles(dir, "*.md")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(Path.GetFileNameWithoutExtension, Path.GetFullPath, StringComparer.Ordinal);
        }

        private static JsonObject ReadWorkflowResolutionSettings(string file)
        {
            if (file == null || !File.Exists(file)) return null;
            try { return JsonNode.Parse(ReadSettingsStrict(file)) as JsonObject; }
            catch { return null; } // Existing corrupt-settings handling remains authoritative and fail-closed.
        }

        private static HashSet<string> RequiredWorkflowNames(JsonObject root)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root?["bindings"] is not JsonObject bindings) return required;
            void Add(JsonObject binding)
            {
                if (binding?["require"] is not JsonArray names) return;
                foreach (var node in names)
                    if (node is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                        required.Add(name.Trim());
            }
            foreach (var binding in bindings)
            {
                if (binding.Value is JsonObject flat) Add(flat);
                else if (binding.Value is JsonArray conditional)
                    foreach (var rule in conditional.OfType<JsonObject>()) Add(rule);
            }
            return required;
        }

        private static bool ReadRequiredWorkflowRevisions(JsonObject root, out Dictionary<string, string> revisions)
        {
            revisions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root == null || !root.ContainsKey(RequiredWorkflowRevisionsKey)) return false;
            if (root[RequiredWorkflowRevisionsKey] is not JsonObject map)
                throw new InvalidOperationException($"{RequiredWorkflowRevisionsKey} must be an object of workflow names to stock revision hashes. Select the binding or profile again to repair it.");
            foreach (var item in map)
            {
                if (item.Value is not JsonValue value || !value.TryGetValue<string>(out var revision) || string.IsNullOrWhiteSpace(revision))
                    throw new InvalidOperationException($"The saved stock revision for required workflow '{item.Key}' is missing or invalid. Select the binding or profile again to repair it.");
                revisions[item.Key] = revision;
            }
            return true;
        }

        /// <summary>Every superseded stock definition still shipped, grouped by workflow name. One reader, so the
        /// run resolver, the pin writer and the document reader cannot disagree about what is available.</summary>
        private static Dictionary<string, string[]> CompatibleWorkflowFiles()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "workflows-compat");
            if (!Directory.Exists(root)) return new Dictionary<string, string[]>(StringComparer.Ordinal);
            return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(path => path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        }

        private static string WorkflowRevision(string path) => WorkflowByteHash(File.ReadAllBytes(path));

        private Dictionary<string, ResolvedWorkflowFile> ResolveRequiredStockFiles(
            HashSet<string> roots,
            Dictionary<string, string> pinned,
            WorkflowRevisionFallback fallback,
            Dictionary<string, string> users,
            Dictionary<string, string> current,
            Dictionary<string, string[]> compatible)
        {
            var selected = new Dictionary<string, ResolvedWorkflowFile>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            string ResolvePinned(string name, string revision)
            {
                if (current.TryGetValue(name, out var currentPath) && string.Equals(WorkflowRevision(currentPath), revision, StringComparison.Ordinal))
                    return currentPath;
                if (compatible.TryGetValue(name, out var paths))
                    foreach (var path in paths)
                        if (string.Equals(WorkflowRevision(path), revision, StringComparison.Ordinal)) return path;
                throw new InvalidOperationException($"Required workflow '{name}' is pinned to stock revision '{revision}', but that revision is unavailable. Restore the shipped workflow compatibility files or select the binding/profile again.");
            }

            void Visit(string name)
            {
                if (visited.Contains(name) || !visiting.Add(name)) return;   // seen, or a hand-off cycle
                try
                {
                    string path;
                    string source;
                    if (users.TryGetValue(name, out path)) source = "user";  // a project copy still wins outright
                    else
                    {
                        source = "stock";
                        if (pinned.TryGetValue(name, out var revision)) path = ResolvePinned(name, revision);
                        else if (fallback == WorkflowRevisionFallback.Baseline)
                        {
                            path = compatible.TryGetValue(name, out var paths)
                                ? paths.FirstOrDefault(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "v1.1.3", StringComparison.Ordinal))
                                : null;
                            // Shipped today but missing from the compatibility set means the install was stripped,
                            // and silently handing back the NEW definition is the exact swap this exists to prevent.
                            if (path == null && current.ContainsKey(name))
                                throw new InvalidOperationException($"Required workflow '{name}' comes from a saved pre-redesign binding, but its v1.1.3 compatibility definition is unavailable. Restore the shipped compatibility files before running it.");
                        }
                        // Current. A settings file that already carries a revision map got one written for every
                        // required STOCK name, so a name missing from it was user-shadowed at that moment and never
                        // chose a pre-redesign definition. Today's file is the honest answer.
                        else current.TryGetValue(name, out path);

                        // No file anywhere: a dangling binding entry (a deleted project workflow, or a settings
                        // file edited by hand). It resolved to nothing before pinning existed and still does --
                        // binding ENFORCEMENT is where that gets reported, so a library read must not throw.
                        if (path == null) return;
                        selected[name] = new ResolvedWorkflowFile { Path = path, Source = source, Revision = WorkflowRevision(path) };
                    }

                    var def = WorkflowParser.ParseFile(path, source);
                    foreach (var child in (def.Steps ?? Array.Empty<WorkflowStep>())
                        .Select(step => step.Call?.Workflow).Where(child => !string.IsNullOrWhiteSpace(child)))
                        Visit(child);
                    visited.Add(name);
                }
                finally { visiting.Remove(name); }
            }

            foreach (var root in roots) Visit(root);
            return selected;
        }

        private ResolvedWorkflowLibrary ResolveWorkflowLibrary(string userDir, string stockDir)
        {
            var users = WorkflowFiles(userDir);
            var current = WorkflowFiles(stockDir);
            var compatible = CompatibleWorkflowFiles();
            var settings = ReadWorkflowResolutionSettings(WorkflowSettingsFileFor(userDir));
            var roots = RequiredWorkflowNames(settings);
            var hasPins = ReadRequiredWorkflowRevisions(settings, out var pins);
            var required = ResolveRequiredStockFiles(roots, pins,
                hasPins ? WorkflowRevisionFallback.Current : WorkflowRevisionFallback.Baseline,
                users, current, compatible);
            var result = new ResolvedWorkflowLibrary();
            foreach (var item in current)
                result.Files[item.Key] = new ResolvedWorkflowFile { Path = item.Value, Source = "stock", Revision = WorkflowRevision(item.Value) };
            foreach (var item in required)
            {
                result.Files[item.Key] = item.Value;
                result.RequiredStockRevisions[item.Key] = item.Value.Revision;
            }
            foreach (var item in users)
                result.Files[item.Key] = new ResolvedWorkflowFile { Path = item.Value, Source = "user" };
            return result;
        }

        private Dictionary<string, string> CurrentRequiredWorkflowRevisions(JsonObject root, string userDir, string stockDir)
        {
            var users = WorkflowFiles(userDir);
            var current = WorkflowFiles(stockDir);
            var compatible = CompatibleWorkflowFiles();
            var hasPins = ReadRequiredWorkflowRevisions(root, out var pins);
            return ResolveRequiredStockFiles(RequiredWorkflowNames(root), pins,
                    hasPins ? WorkflowRevisionFallback.Current : WorkflowRevisionFallback.Baseline,
                    users, current, compatible)
                .ToDictionary(item => item.Key, item => item.Value.Revision, StringComparer.Ordinal);
        }

        private void SelectRequiredWorkflowRevisions(JsonObject root, string userDir, string stockDir,
            Dictionary<string, string> preserved)
        {
            var users = WorkflowFiles(userDir);
            var current = WorkflowFiles(stockDir);
            var compatible = CompatibleWorkflowFiles();
            var revisions = ResolveRequiredStockFiles(RequiredWorkflowNames(root), preserved ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    WorkflowRevisionFallback.Current, users, current, compatible)
                .ToDictionary(item => item.Key, item => item.Value.Revision, StringComparer.Ordinal);
            if (revisions.Count == 0) { root.Remove(RequiredWorkflowRevisionsKey); return; }
            var map = new JsonObject();
            foreach (var item in revisions.OrderBy(item => item.Key, StringComparer.Ordinal)) map[item.Key] = item.Value;
            root[RequiredWorkflowRevisionsKey] = map;
        }

        private string WorkflowSettingsFile()
        {
            var (userDir, _) = WorkflowDirs();
            return WorkflowSettingsFileFor(userDir);
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
                                $"workflow-settings.json is unreadable and its contents could not be preserved to a '.corrupt-*' sibling ({ex.Message}): refusing to overwrite it. Repair or move the file, then retry.");
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
                // A STEP-scope root in a binding condition. It parses perfectly (`inputs.approval.answered`
                // is a readable fact), it just has no value outside a run, so the rule never matches and a
                // `hard` binding quietly enforces nothing. The same root is refused at the activation WRITE
                // path; a binding condition has no write path at all, it is hand-edited, so the only place to
                // catch it is here on the way in, and policy lint already reports a rule carrying WhenError.
                // Treated exactly like a misspelled fact, which is what it effectively is in this scope.
                if (r.WhenError == null && r.When != null)
                {
                    var stepScope = WorkflowPredicate.StepScopeFacts(r.When);
                    if (stepScope.Count > 0)
                        r.WhenError = $"'{string.Join("', '", stepScope)}' only means something while a workflow is running, and a binding rule is judged before any run starts, so this rule can never match and the requirement it carries would never apply.";
                }
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
        // hard-refuses. Reading and writing activation are both Pro: Workflows is one whole feature (Kane 2026-09-15).

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
                    if (e.ValueKind != JsonValueKind.Object) { rule.InvalidReason = "an activation entry is not an object. Skipped."; list.Add(rule); continue; }
                    rule.Workflow = e.TryGetProperty("workflow", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                    rule.Tag = e.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    rule.RawWhen = e.TryGetProperty("when", out var wh) && wh.ValueKind == JsonValueKind.String ? wh.GetString() : null;
                    rule.Reason = e.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null;
                    var set = e.TryGetProperty("set", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()?.Trim().ToLowerInvariant() : null;
                    rule.When = WorkflowPredicate.Parse(rule.RawWhen, out var perr);
                    rule.WhenError = perr;
                    // The SAME hole F-045 closed in the binding reader, left open in this one. A step-scope
                    // root parses perfectly and has no value outside a run, so the rule never matches and the
                    // workflow it was meant to switch off silently stays on. set_workflow_activation refuses
                    // it on the way in, which is exactly why this reader matters: the settings file is
                    // hand-edited, so the write path is not the only door and never was. Recorded as a
                    // WhenError, which the activation lint below already surfaces and which already makes the
                    // rule inert rather than half-working.
                    if (rule.WhenError == null && rule.When != null)
                    {
                        var stepScope = WorkflowPredicate.StepScopeFacts(rule.When);
                        if (stepScope.Count > 0)
                            rule.WhenError = $"'{string.Join("', '", stepScope)}' only means something while a workflow is running, and an activation rule is judged before any run starts, so this rule can never match and the workflow it names would stay as it is.";
                    }

                    var hasWf = !string.IsNullOrWhiteSpace(rule.Workflow);
                    var hasTag = !string.IsNullOrWhiteSpace(rule.Tag);
                    if (hasWf == hasTag) rule.InvalidReason = "an activation rule needs exactly one of workflow: or tag: (skipped).";
                    else if (set != "on" && set != "off") rule.InvalidReason = $"an activation rule's set must be 'on' or 'off' (got '{set ?? "missing"}'). Skipped.";
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
            var context = _sessions.CurrentContext;
            var f = new PredicateFacts();
            if (roots.Count == 0) return f;   // nothing referenced → gather nothing

            if (roots.Contains("model") && context.Session != null)
            {
                var s = context.Session;
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
                f.Model.ReadinessGrade = CurrentReadinessGrade(context);   // cached (D2) — may be null (unknown), never force-scanned
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
            {
                await context.PlanGate.WaitAsync();
                bool planLoaded;
                try
                {
                    EnsureContextCurrent(context, "Workflow facts");
                    planLoaded = context.Plans.Current != null;
                }
                finally { context.PlanGate.Release(); }
                f.Session = new SessionFacts
                {
                    Tier = _entitlement?.Info?.Tier ?? "free",
                    VerifiedMode = _verifiedMode ? "on" : "off",
                    PlanLoaded = planLoaded,
                };
            }
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

            // A rule reads "set X when C", so it is a GATE, not a one-way switch. An on-rule whose condition does
            // NOT hold therefore has to hide the workflow: skipping it outright fell through to default-on, so the
            // workflow stayed offered while the rule that was meant to gate it looked satisfied (D-236). An
            // off-rule whose condition does not hold is the same gate read the other way, so it leaves the
            // workflow's normal default-on state alone. Later rules still get their say: the gate only closes if
            // no rule matches at all.
            var gatedShut = false;
            foreach (var r in rules)
            {
                if (!r.Valid || r.WhenError != null) continue;   // inert/broken rules never fire (E5/E6)
                if (!RuleSelects(r, def)) continue;
                if (r.When != null && !WorkflowPredicate.Evaluate(r.When, facts))
                {
                    if (r.On) gatedShut = true;   // "shown when C" with C false ⇒ hidden
                    continue;
                }
                return (r.On, string.IsNullOrWhiteSpace(r.Reason) ? DefaultActivationReason(r.On) : r.Reason);
            }
            if (gatedShut) return (false, DefaultActivationReason(false));
            return (true, null);   // default-on, invisible
        }

        // A plain fallback reason when a rule supplied no `reason:` (never echoes the predicate, §10.12).
        private static string DefaultActivationReason(bool on) =>
            on ? "shown by a project rule for this situation" : "hidden by a project rule for this situation";

        /// <summary>Resolve activation for a SINGLE def (used by start_workflow): gathers the pass's facts once
        /// and returns whether it's active, the plain reason, and whether a binding force-activates it.
        /// [T220 §5.2 row 4 / F-121] <paramref name="alsoForce"/> names the run's OTHER reachable owners, whose
        /// own force-active state the caller also needs. They are answered from the SAME enforced-binding
        /// snapshot, for two reasons: a callee's kill-switch answer must come from the identical question the
        /// root's comes from (stop condition 4 forbids the two rules differing), and re-entering this method per
        /// callee would gather the fact snapshot once per reachable name instead of the one pass §5.2 budgets.</summary>
        private async Task<(bool active, string reason, bool forceActive, HashSet<string> forcedNames)> ResolveActivationForAsync(
            WorkflowDef def, IEnumerable<WorkflowDef> alsoForce = null)
        {
            var rules = AllActivationRules();
            var arrayBindings = AllArrayBindings();
            var facts = await GatherFactsForAsync(rules.Where(r => r.Valid).Select(r => r.When)
                .Concat(arrayBindings.SelectMany(ab => ab.Rules.Select(r => r.When))));
            var enforced = ComputeEnforcedBindings(facts, arrayBindings);
            bool Forced(WorkflowDef d) => enforced.Any(b => b.Binding.Require.Contains(d.Name, StringComparer.Ordinal));
            var forceActive = Forced(def);
            var forcedNames = (alsoForce ?? Array.Empty<WorkflowDef>()).Where(Forced)
                .Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var (active, reason) = ResolveActivation(def, facts, rules, SettingsEnabledFor(def.Name), enforced);
            return (active, reason, forceActive, forcedNames);
        }

        /// <summary>Build the resolved library infos (availability + activation) — the shared body behind
        /// list_workflows AND the library rebroadcast, so the two can never drift. Gathers the lazy fact snapshot
        /// once for the whole pass (§2.5).</summary>
        private async Task<WorkflowInfo[]> BuildLibraryInfosAsync()
        {
            var defs = LoadWorkflowDefs();
            var templates = LoadTemplateDefs();
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
                return WorkflowRunner.BuildInfo(d, SettingsStrictnessFor(d.Name), global, enabled, active, reason,
                    ClosureIsGated(d, defs, templates, global));
            }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>Library and policy Gated flags follow the same call graph start uses, so a gateless parent
        /// that hands off to a gated child is badged before the analyst clicks Start.</summary>
        private bool ClosureIsGated(WorkflowDef d, IReadOnlyList<WorkflowDef> defs, IReadOnlyList<WorkflowDef> templates, string global)
        {
            if (d?.Error != null) return false;
            var closure = WorkflowParser.ReachableClosure(d, defs, templates, out _);
            return closure.Any(x => x.Error == null && x.HasEnforcedGate(SettingsStrictnessFor(x.Name), global));
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
            var context = _sessions.CurrentContext;
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
                    $"{PlainOpPhrase(op)} may be gated by a workflow policy, but .semanticus/workflow-settings.json is present and can't be read (malformed JSON): enforcement is failing closed. Repair or delete the file, or run set_workflow_enforcement (e.g. mode 'default'). Any settings write preserves the unreadable file aside as workflow-settings.json.corrupt-* and writes a fresh valid one. get_workflow_policy shows the rules once it parses.");

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
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow policy");
                foreach (var run in context.WorkflowRuns.ActiveRuns())
                {
                    // An EXPANSION-ONLY current row is not a performing step. `run.CurrentStep` hands back the
                    // AUTHORED loop body for that row, ops and all, but the row itself only fixes the list: it runs
                    // no gate, shows no ops, and no iteration exists yet. Reading its ops here would hand the
                    // step-scoped exemption to a bound op BEFORE any iteration is current: the same
                    // start-and-freestyle hole (A) closes, reopened one row earlier by public loop start. The
                    // classification is WorkflowRunner's own (the three setup shapes it already routes), never a
                    // second interpretation here and never a raw index or iteration test.
                    if (WorkflowRunner.IsExpansionOnlySubmission(run)) continue;
                    var row = run.SubmittedRow;
                    if (row == null) continue;
                    // A callee's authoring step satisfies that callee's mandate. Merely reaching a required
                    // workflow later, or starting a required caller, cannot authorize another owner's step.
                    var owner = run.RequireCoherentRow(row);
                    if (!binding.Require.Contains(owner.Name, StringComparer.Ordinal)) continue;
                    var step = row.Step;
                    if (step?.Ops != null && step.Ops.Contains(op, StringComparer.Ordinal)) { atPerformingStep = true; break; }
                }
            }
            finally { context.WorkflowGate.Release(); }
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
                Label = $"{op} landed outside its required workflow (one of: {required}). Allowed (warn).",
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
            RequireProFeature();
            // A corrupt settings file fails CLOSED: the "off" kill-switch lives in that same unreadable file, so it
            // can't be honored — enforcement stays on and we say so (mode reads null ⇒ per-def default: hard).
            if (WorkflowSettingsCorrupt())
                return Task.FromResult(new WorkflowEnforcement
                {
                    Mode = null,
                    Enforced = true,
                    Note = "workflow-settings.json is present but unreadable (malformed JSON): enforcement is failing CLOSED (any 'off' override in it is ignored). Repair or delete the file, or run set_workflow_enforcement (e.g. mode 'default'). Any settings write preserves the unreadable file aside as workflow-settings.json.corrupt-* and writes a fresh valid one.",
                });
            var mode = GlobalStrictness();
            return Task.FromResult(new WorkflowEnforcement
            {
                Mode = mode,
                Enforced = mode != "off",
                Note = mode == null
                    ? "No global override. Each playbook's own checks apply. Actions that must go through a playbook still do."
                    : mode == "off"
                        ? "Playbook checks are skipped model-wide and runs record no verified evidence. Actions that must go through a playbook still do."
                        : mode == "warn"
                            ? "Playbook checks now warn instead of blocking. Actions that must go through a playbook still do."
                            : "Playbook checks now block, even if a playbook asked for a warning. Actions that must go through a playbook still do.",
            });
        }

        /// <summary>Set (or clear) the model-wide enforcement mode. Writes the settings file non-destructively
        /// (the per-workflow overrides survive) and re-broadcasts the library — Gated flags change with the mode,
        /// so both doors see cards flip live. mode: "hard" | "warn" | "off" | "default"/null (clear).</summary>
        public async Task<WorkflowEnforcement> SetWorkflowEnforcementAsync(string mode, string origin)
        {
            RequireProFeature();
            mode = string.IsNullOrWhiteSpace(mode) || mode.Trim().ToLowerInvariant() == "default" ? null : mode.Trim().ToLowerInvariant();
            if (mode != null && mode != "hard" && mode != "warn" && mode != "off")
                throw new ArgumentException("mode must be 'hard', 'warn', 'off', or 'default' (clear the override and let each workflow's own strictness apply).");
            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings. Open a model (or start the engine with a workspace) first.");

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
            var resolved = ResolveWorkflowLibrary(userDir, stockDir);
            var defs = resolved.Files.Values
                .Select(file => WorkflowParser.ParseFile(file.Path, file.Source))
                .ToList();
            // §10.3 placement guard: a `kind: template` file in the workflows dir is a misplaced RECIPE, not a
            // runnable workflow — surface it as an error (never silently skip), so list_workflows/start_workflow
            // teach the fix instead of trying to run blanks.
            foreach (var d in defs)
                if (d.Error == null && string.Equals(d.Kind, "template", StringComparison.Ordinal))
                    d.Error = "this file declares 'kind: template' but lives in the workflows dir: a template is a recipe with blanks, not runnable. Move it to .semanticus/workflow-templates/, or remove 'kind: template' to make it a workflow.";
            return defs;
        }

        public async Task<WorkflowInfo[]> ListWorkflowsAsync()
        {
            RequireProFeature();
            return await BuildLibraryInfosAsync();
        }

        public Task<WorkflowDef> GetWorkflowAsync(string name)
        {
            RequireProFeature();
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
            RequireProFeature();
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
        private async Task<WorkflowCheckReport> CheckWorkflowDefAsync(WorkflowDef def, List<WorkflowDef> candidateLibrary = null)
        {
            var report = new WorkflowCheckReport { Name = def.Name };
            if (def.Error != null) { report.ParseError = def.Error; report.Ok = false; return report; }

            var findings = new List<CheckFinding>();

            // A distilled workflow names where it came from — info, never docks Ok.
            if (def.Provenance != null && def.Provenance.Count > 0)
                findings.Add(new CheckFinding { Severity = "info",
                    Message = "Distilled workflow: provenance " + string.Join("; ", def.Provenance.Select(kv => $"{kv.Key}: {kv.Value}")) + "." });

            // Format v2 (docs/workflow-canvas-spec.md §3.4): the checks that need the whole LIBRARY rather
            // than the file — does a hand-off resolve, is the chain acyclic and inside the depth limit, is a
            // `when:` term one this version can read. Kept in WorkflowParser so the format's rules live in
            // one place; a parse-level refusal never reaches here, having returned above as ParseError.
            var library = candidateLibrary ?? LoadWorkflowDefs();
            findings.AddRange(WorkflowParser.V2Findings(def, library, LoadTemplateDefs()));

            var catalog = (await GetOpCatalogAsync()).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var t in def.Triggers ?? Array.Empty<string>())
                if (!catalog.Contains(t))
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"trigger '{t}' is not a known op (get_op_catalog lists the real surface)." });

            // Run-wide gate inputs: a later step's verify can reference an input a *previous* step collected.
            var allInputs = def.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>()).ToList();
            // Plus every name a `call:` brings back from a callee that could actually deliver it: same rule,
            // same helper, one implementation (WorkflowParser.CallerInputNames). Names only, not GateInput
            // rows: the declaration, and therefore the type, lives in the callee, so the objectRef and
            // daxPurity views below deliberately still read this file's own declarations.
            var inputNames = WorkflowParser.CallerInputNames(def, library);
            var hasObjectRef = allInputs.Any(i => string.Equals(i.Type, "objectRef", StringComparison.Ordinal));

            // SF7 — a running SAME-OR-PRIOR-STEP view of objectRef availability: expected_values resolves its target
            // from answers accumulated up to the gated step, so an objectRef declared only on a LATER step could
            // never be answered in time — flag that honestly instead of blessing it off the run-wide set.
            var hasObjectRefSoFar = false;

            foreach (var step in def.Steps)
            {
                if (step.Call != null && (step.Call.Returns?.Length ?? 0) > 0)
                {
                    var callee = library.FirstOrDefault(d => string.Equals(d.Name, step.Call.Workflow, StringComparison.OrdinalIgnoreCase));
                    if (callee != null && string.Equals(callee.Strictness, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        var title = string.IsNullOrWhiteSpace(callee.Title) ? callee.Name : callee.Title;
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"This hand-off expects values back, but {title} has its gate off, so nothing will come back." });
                    }
                }
                hasObjectRefSoFar |= (step.Gate?.Inputs ?? Array.Empty<GateInput>())
                    .Any(i => string.Equals(i.Type, "objectRef", StringComparison.Ordinal));

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
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' has no probe: input naming the value to compare against. It would fail at run time." });

                    if (!string.IsNullOrEmpty(v.Probe) && !inputNames.Contains(v.Probe))
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' probe references input '{v.Probe}', which no gate collects." });

                    var needsTarget = v.Kind == "dax_probe" || v.Kind == "dax_equivalence" || v.Kind == "expected_values"
                        || v.Kind == "benchmark_delta"
                        || v.Kind == "impact_assessment" || v.Kind == "baseline_exists"
                        || v.Kind == "plan_item_staged" || v.Kind == "plan_item_applied"
                        || (v.Kind == "bpa_clean" && string.Equals(v.Scope, "object", StringComparison.Ordinal));
                    // expected_values judges availability SAME-OR-PRIOR-STEP (its target must be answerable by the
                    // time the gate runs — a later step's objectRef can't be); the other kinds keep the run-wide view.
                    var targetAvailable = v.Kind == "expected_values" ? hasObjectRefSoFar : hasObjectRef;
                    if (needsTarget && !targetAvailable)
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"{step.Id} verify '{v.Kind}' needs a target, but no input of type objectRef names the object to act on"
                            + (v.Kind == "expected_values" && hasObjectRef ? " on this or any prior step (a later step's objectRef cannot be answered in time)" : "")
                            + ": the check can't run and would fail a hard gate." });

                    // equivalenceGrid is an optional convention: absent = grand-total-only (thin), not an error.
                    if (v.Kind == "dax_equivalence" && !inputNames.Contains("equivalenceGrid"))
                        findings.Add(new CheckFinding { Severity = "info", Message = $"{step.Id} verify 'dax_equivalence' has no 'equivalenceGrid' input: equivalence would be grand-total only (thin). Add one for a per-context proof." });

                    // benchmarkTolerance is optional: absent = the 1.10 (10%-over-baseline) default applies.
                    if (v.Kind == "benchmark_delta" && !inputNames.Contains("benchmarkTolerance"))
                        findings.Add(new CheckFinding { Severity = "info", Message = $"{step.Id} verify 'benchmark_delta' has no 'benchmarkTolerance' input: the default 1.10 (allow 10% over baseline) applies. Add one to tighten or loosen the regression band." });

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
        public async Task<WorkflowInfo[]> SaveWorkflowAsync(string name, string markdown, string origin, bool createOnly = false, string sessionId = null)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, _) = WorkflowDirs(context);
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                GuardWorkflowDocumentContext(context, sessionId);
                lock (WorkflowDocumentLock)
                {
                    var def = ParseWorkflowDocument(markdown);
                    if (def.Error != null)
                        throw new InvalidOperationException($"The workflow does not parse. Nothing was written. {def.Error} Fix the reported error, then re-run save_workflow (or check_workflow to re-validate a library entry).");
                    if (!string.Equals(def.Name, name, StringComparison.Ordinal))
                        throw new InvalidOperationException($"frontmatter name '{def.Name}' must equal the workflow name '{name}' (it is the file identity).");
                    if (userDir == null)
                        throw new InvalidOperationException("No place to store user workflows. Run open_model (or save_model after create_model) so the .semanticus sidecar has a home, or run the engine with a workspace.");
                    var bytes = WorkflowDocumentUtf8.GetBytes(markdown);
                    var file = Path.Combine(userDir, name + ".md");
                    if (createOnly && File.Exists(file))
                        throw new InvalidOperationException($"A project copy of '{name}' already exists. Nothing was written. Read get_workflow_document to edit that copy.");
                    Directory.CreateDirectory(userDir);
                    var temp = WriteWorkflowDocumentTemp(file, bytes);
                    try { File.Move(temp, file, !createOnly); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            finally { context.WorkflowGate.Release(); }
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>Delete a USER workflow. Stock files are read-only by construction — deleting a stock
        /// name without a user shadow is refused instructively (customised shadows revert to stock).</summary>
        public async Task<WorkflowInfo[]> DeleteWorkflowAsync(string name, string origin)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, stockDir) = WorkflowDirs(context);
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                GuardWorkflowDocumentContext(context, null);
                lock (WorkflowDocumentLock)
                {
                    var file = userDir == null ? null : Path.Combine(userDir, name + ".md");
                    if (file == null || !File.Exists(file))
                        throw new InvalidOperationException(
                            File.Exists(Path.Combine(stockDir, name + ".md"))
                                ? $"'{name}' is a stock workflow (read-only, shipped with the engine) and has no user copy to delete. Customised copies live in .semanticus/workflows."
                                : $"User workflow '{name}' not found: list_workflows shows the library, and only your own copies (under .semanticus/workflows) are deletable.");
                    File.Delete(file);
                }
            }
            finally { context.WorkflowGate.Release(); }
            return await PublishWorkflowLibraryAsync();
        }

        /// <summary>§9a: enable/disable a workflow (the availability toggle). Non-destructive merge into
        /// `workflows.&lt;name&gt;.enabled` — per-workflow strictness and every other key survive. Re-broadcasts
        /// the library so both doors re-list live; a disabled workflow stays VISIBLE (marked enabled:false) so
        /// the designer can re-enable it, but start_workflow refuses it. FREE: curation is content, and a
        /// disable only narrows the caller's own menu (mirrors set_workflow_enforcement's file discipline).</summary>
        public async Task<WorkflowInfo[]> SetWorkflowEnabledAsync(string name, bool enabled, string origin)
        {
            RequireProFeature();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
            var def = LoadWorkflowDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Workflow '{name}' not found: list_workflows shows the library (stock + your .semanticus/workflows).");
            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings. Open a model (or start the engine with a workspace) first.");

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
            RequireProFeature();
            if (string.IsNullOrWhiteSpace(op)) throw new ArgumentException("op is required: the op to route, e.g. 'create_measure'.");
            op = op.Trim();
            mode = string.IsNullOrWhiteSpace(mode) ? "off" : mode.Trim().ToLowerInvariant();
            if (mode != "hard" && mode != "warn" && mode != "off")
                throw new ArgumentException("mode must be 'hard', 'warn', or 'off' (off clears the binding).");
            var require = (requireNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            var clearing = mode == "off" || require.Length == 0;

            var (userDir, stockDir) = WorkflowDirs();
            var file = WorkflowSettingsFileFor(userDir)
                ?? throw new InvalidOperationException("No workspace to hold workflow settings. Open a model (or start the engine with a workspace) first.");

            // §9.10C: a committed mandate (userDisablable:false) can't be changed OR cleared from the agent door —
            // instructive refusal, checked after the feature gate so a free caller is told the price first. Human/file edits
            // remain possible (origin != "agent"); we deliberately do not try to police the file itself.
            var existing = SettingsBindingFor(op);
            if (existing != null && !existing.UserDisablable && string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The requirement on {op} is locked by committed team policy; change it in workflow-settings.json via review.");

            if (!clearing)
            {
                // Every required name must exist — a binding to a phantom workflow would be an unstartable trap.
                var lib = LoadWorkflowDefs().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                var unknown = require.Where(n => !lib.Contains(n)).ToArray();
                if (unknown.Length > 0)
                    throw new InvalidOperationException($"Unknown workflow(s): {string.Join(", ", unknown)}. list_workflows shows the library. A binding can only require workflows that exist.");
            }

            MutateWorkflowSettings(file, root =>
            {
                // Capture the definition set the surviving requirements currently resolve to before changing
                // the binding. Old files have no revision map, so this is where their v1.1.3 choice becomes
                // explicit without a read-side migration. Newly required names use today's stock files below.
                var preserved = CurrentRequiredWorkflowRevisions(root, userDir, stockDir);
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
                SelectRequiredWorkflowRevisions(root, userDir, stockDir, preserved);
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
            RequireProFeature();
            if (string.IsNullOrWhiteSpace(workflow)) throw new ArgumentException("workflow is required: the workflow to show or hide with a rule.");
            workflow = workflow.Trim();
            when = string.IsNullOrWhiteSpace(when) ? null : when.Trim();
            set = string.IsNullOrWhiteSpace(set) ? null : set.Trim().ToLowerInvariant();
            var clearing = when == null && set == null;   // neither given ⇒ remove the rule (show it normally again)

            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace to hold workflow settings. Open a model (or start the engine with a workspace) first.");

            // A rule for a phantom workflow is a trap that only clutters the policy lints — refuse it up front.
            var lib = LoadWorkflowDefs().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            if (!lib.Contains(workflow))
                throw new InvalidOperationException($"Workflow '{workflow}' not found: list_workflows shows the library. An activation rule can only target a workflow that exists.");

            if (!clearing)
            {
                if (set != "on" && set != "off")
                    throw new ArgumentException("set must be 'on' (show it when the condition holds) or 'off' (hide it), or clear the rule by passing neither a condition nor a set.");
                // Parse-validate the condition BEFORE the write (teaching tone) — never persist a rule that can't run.
                var parsedWhen = WorkflowPredicate.Parse(when, out var perr);
                if (perr != null)
                    throw new InvalidOperationException($"That condition can't be used: {perr} Conditions read like date.monthEndOffset >= -3, connection.workspace ~ '*prod*', or model.readinessGrade < 'B'.");
                // Format v2 added inputs.* and loop.* so a STEP can have a condition. They read a run's
                // answers and the current loop pass, and an activation rule is judged before any run exists,
                // so a rule using one would silently never match. Refused here rather than in
                // WorkflowPredicate, which stays the one evaluator for every caller.
                var stepScope = WorkflowPredicate.StepScopeFacts(parsedWhen);
                if (stepScope.Count > 0)
                    throw new InvalidOperationException($"That condition can't be used here: '{string.Join("', '", stepScope)}' only means something while a workflow is running, and this rule decides whether the workflow is offered at all. Use a condition about the model, the connection, git, the session or the date. To make a STEP conditional, put 'when:' in that step's 'yaml step' block instead.");
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
            RequireProFeature();
            var global = GlobalStrictness();
            var defs = LoadWorkflowDefs();
            var templates = LoadTemplateDefs();
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
                        Gated = ClosureIsGated(d, defs, templates, global),
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
                Warn("workflow-settings.json is present but can't be parsed (malformed JSON). Workflow enforcement is failing CLOSED: bindable authoring ops are refused and any 'off' override is ignored until you repair or delete the file.");

            // Per-rule structural lints.
            foreach (var r in rules)
            {
                if (!r.Valid) { Warn(r.InvalidReason ?? "an activation rule is malformed and was skipped."); continue; }
                if (r.WhenError != null)                                   // (2) unreadable when:
                    Warn($"the condition on the activation rule for {SelectorLabel(r)} can't be used: {r.WhenError}");
                if (r.Workflow != null && !names.Contains(r.Workflow))     // (1) unknown target workflow
                    Warn($"an activation rule targets workflow '{r.Workflow}', which doesn't exist: list_workflows shows the library; the rule does nothing.");
                if (r.Tag != null && !allTags.Contains(r.Tag))            // (1) unknown target tag
                    Warn($"an activation rule targets tag '{r.Tag}', which no workflow carries: the rule does nothing.");
            }

            // Per selected-workflow contradictions.
            foreach (var d in defs)
            {
                var enabled = SettingsEnabledFor(d.Name);
                var requiredBy = enforced.Where(b => b.Binding.Require.Contains(d.Name, StringComparer.Ordinal)).Select(b => b.Op).ToArray();
                var selecting = rules.Where(r => r.Valid && r.WhenError == null && RuleSelects(r, d)).ToArray();

                if (!enabled && requiredBy.Length > 0)                     // (5) the deadlock (loudest)
                    Warn($"'{d.Name}' is turned off but required when {PlainOpPhrase(requiredBy[0])}; the requirement wins. Turn '{d.Name}' back on or drop the requirement.");
                if (!enabled && selecting.Any(r => r.On))                  // (3) dead rule (manual disable wins)
                    Warn($"a rule shows '{d.Name}' in some situations, but it's turned off for this project: the rule has no effect (turn it back on to use the rule).");
                if (requiredBy.Length > 0 && selecting.Any(r => !r.On))    // (4) binding↔activation contradiction
                    Warn($"'{d.Name}' is required when {PlainOpPhrase(requiredBy[0])} but a rule hides it: the requirement wins; the rule is overridden.");
                if (selecting.Select(r => r.On).Distinct().Count() > 1)    // (6) conflicting rules (deterministic, but a smell)
                    Info($"more than one activation rule selects '{d.Name}' with different on/off outcomes: the first matching rule wins (a likely mistake).");
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
                    // [T215] Stamp the ratified taxonomy home here, once, so every consumer of the
                    // catalog (Studio picker over RPC, agents over MCP) sees the same tree. An op
                    // OpTaxonomy does not know ships with null question/shelf — visible, not hidden,
                    // and OpTaxonomyTests fails naming it until its placement is ratified.
                    OpTaxonomy.TryGet(name, out var question, out var shelf);
                    infos.Add(new OpInfo { Name = name, Description = FirstSentence(desc?.Description), Question = question, Shelf = shelf });
                }
            // [T215] The catalog's ORDER is the ratified tree's order (question page order, shelf page
            // order, then name), so a consumer that groups in received order — the Studio picker —
            // renders the page without re-deriving it. An unfiled op sorts last, visibly.
            return new OpSurfaceData
            {
                Infos = infos
                    .OrderBy(o => OpTaxonomy.OrderOf(o.Name).QuestionIndex)
                    .ThenBy(o => OpTaxonomy.OrderOf(o.Name).ShelfIndex)
                    .ThenBy(o => o.Name, StringComparer.Ordinal)
                    .ToArray(),
                Methods = methods
            };
        });

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var cut = s.IndexOf(". ", StringComparison.Ordinal);
            var head = cut > 0 ? s.Substring(0, cut + 1) : s;
            return head.Length > 200 ? head.Substring(0, 200) + "…" : head;
        }

        // ---- run lifecycle ----------------------------------------------------------------------

        internal Func<Task> WorkflowStartReadyForTest;

        /// <summary>[T220] Phase B entitlement over the WHOLE frozen closure. Not a new rule: `HasEnforcedGate`
        /// is already a per-definition question, so this is the same question folded over one run's definitions.
        /// A free root that reaches a hard-gated callee still enforces a paid gate, because the run is one plan
        /// and one certificate; entitling only the root would sell that enforcement for nothing.
        /// The same closure is used for planning and execution.</summary>
        internal static bool ClosureRequiresPro(WorkflowOwnerClosure closure, string globalStrictness) =>
            closure.Definitions.Any(d => d.HasEnforcedGate(closure.SettingsStrictness(d), globalStrictness));

        /// <summary>[T220] Every verify kind declared anywhere in the frozen closure, so a callee-only
        /// `bpa_clean` or `readiness_rescan` still takes its baseline at start.</summary>
        internal static HashSet<string> ClosureVerifyKinds(WorkflowOwnerClosure closure) =>
            closure.Definitions.SelectMany(d => d.Steps)
                .Where(s => s.Gate != null).SelectMany(s => s.Gate.Verify)
                .Select(v => v.Kind).ToHashSet(StringComparer.Ordinal);

        /// <summary>[T220] The step-level conditions of every reachable owner, parsed once. Facts are still
        /// gathered ONCE from their union.</summary>
        internal static IEnumerable<PredicateExpr> ClosureConditionRoots(WorkflowOwnerClosure closure) =>
            closure.Definitions.SelectMany(d => d.Steps)
                .Where(s => !string.IsNullOrWhiteSpace(s.When))
                .Select(s => WorkflowPredicate.Parse(s.When, out _))
                .ToArray();

        /// <summary>[T220] The start-of-run baselines, driven by the closure rather than the root. The scans are
        /// INJECTED so this seam can be proved offline with invented results: a null scanner means no live
        /// session, which is exactly today's "session != null" test and never a claimed pass. Readiness captures
        /// two values through its own scan, which is why it stays separate from BPA rather than folding in.</summary>
        internal static async Task<WorkflowRunAux> CaptureStartSnapshotsAsync(
            WorkflowOwnerClosure closure, Func<Task<Semanticus.Analysis.BpaScorecard>> bpaScan, Func<Task<Semanticus.Analysis.Scorecard>> readinessScan)
        {
            var aux = new WorkflowRunAux();
            var kinds = ClosureVerifyKinds(closure);
            if (kinds.Contains("bpa_clean") && bpaScan != null)
                aux.BpaKeys = (await bpaScan()).Violations.Where(v => !v.Waived)
                    .Select(v => v.RuleId + "|" + v.ObjectRef).ToHashSet(StringComparer.Ordinal);
            if (kinds.Contains("readiness_rescan") && readinessScan != null)
            {
                var sc = await readinessScan();
                aux.ReadinessKeys = sc.Findings.Where(f => !f.Waived)
                    .Select(f => f.RuleId + "|" + f.ObjectRef).ToHashSet(StringComparer.Ordinal);
                aux.ReadinessOverall = sc.Overall;
            }
            return aux;
        }

        public async Task<WorkflowRunView> StartWorkflowAsync(string name, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            // §10.2/§10.12: a template is a recipe with blanks, not runnable — teach the instantiate path before
            // the generic not-found, but only when no workflow of this name exists (a real workflow always wins).
            if (!LoadWorkflowDefs().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                && LoadTemplateDefs().Any(t => t.Error == null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"'{name}' is a template (a recipe with blanks). Fill it in first: instantiate_workflow_template creates a runnable workflow from it.");
            var def = await GetWorkflowAsync(name);

            // ---- PHASE A: resolve, validate, freeze and entitle before anything expensive happens. [T220]
            // Validate the entire call graph before snapshots or registration: a missing or disabled callee
            // cannot leave behind a partly started run.
            var closureDefs = WorkflowParser.ReachableClosure(def, LoadWorkflowDefs(), LoadTemplateDefs(), out var handoffProblems);
            if (handoffProblems.Count > 0)
            {
                var more = handoffProblems.Count > 1 ? $" ({handoffProblems.Count} in total: {string.Join(" ", handoffProblems.Skip(1).Select(f => f.Message))})" : "";
                throw new InvalidOperationException(
                    $"Workflow '{def.Name}' can't be started because its hand-offs do not resolve: {handoffProblems[0].Message}{more} Open the workflow to see the full report, then save the fix.");
            }

            // §10.6 activation [D1][D4]: resolve the menu state once. A binding force-active workflow MUST be
            // startable even if manually disabled (the deadlock breaker, E1) — so the disabled-refusal is skipped
            // for it. A manual `enabled:false` still hard-refuses otherwise (the kill switch). A RULE-deactivated
            // workflow (off the menu, not manually off) is startable ON DEMAND with a teaching note (D4).
            var (active, activeReason, forceActive, forcedCallees) = await ResolveActivationForAsync(def, closureDefs.Skip(1));
            if (!SettingsEnabledFor(def.Name) && !forceActive)
                throw new InvalidOperationException($"Workflow '{def.Name}' is turned off for this project, so it can't be started. Turn it back on in the Workflows tab.");
            if (def.Error != null)
                throw new InvalidOperationException($"Workflow '{name}' has a parse error and cannot run: {def.Error} Open the workflow to see the full report, then save the fix.");

            // A CALLEE's own kill switch. Activation only curates the menu, so a callee a rule currently hides
            // is still reachable on demand exactly as a root is. `enabled:false` is different: it is the manual
            // off switch, and letting a caller route around it would make it false advertising.
            // [F-121] Force-active is the one thing that DOES beat the manual disable, at every position in the
            // graph. The root rule above already skips its refusal for a workflow an enforced binding requires
            // (E1, the deadlock breaker); this rule ignored that and so refused to reach a REQUIRED workflow
            // through a hand-off. Owner plan §5.2 row 4 gives each owner its own force-active state and stop
            // condition 4 forbids the root and callee rules differing, so the two now ask one question.
            // Force-active means an enforced binding REQUIRES that name, not that a rule put it on the menu,
            // and never a write back to its own `enabled` setting.
            foreach (var reachable in closureDefs.Skip(1))
                if (!SettingsEnabledFor(reachable.Name) && !forcedCallees.Contains(reachable.Name))
                    throw new InvalidOperationException(
                        $"Workflow '{def.Name}' hands off to '{reachable.Name}', which is turned off for this project, so the run can't start. Turn it back on in the Workflows tab, or call set_workflow_enabled(\"{reachable.Name}\", true).");

            // One settings read per DISTINCT reachable name, plus one global read, and then the whole closure
            // is frozen over exactly those values. Nothing loads or freezes a callee later in the run.
            var global = GlobalStrictness();
            var closure = new WorkflowOwnerClosure(closureDefs
                .Select(d => (Def: WorkflowFreeze.Freeze(d), Settings: SettingsStrictnessFor(d.Name)))
                .ToArray());
            var settings = closure.SettingsStrictness(closure.Root);

            // Keep admission tied to the parser's supported control fields. Calls are planned below from
            // this same frozen closure; their input values remain a submission-time question.
            var unexecutable = WorkflowParser.UnexecutableControlFields(def);
            if (unexecutable.Count > 0)
            {
                var first = unexecutable[0];
                var more = unexecutable.Count > 1 ? $" ({unexecutable.Count} in total: {string.Join(", ", unexecutable.Select(u => $"'{u.Field}' on {u.StepId}"))})" : "";
                throw new InvalidOperationException(
                    $"Workflow '{def.Name}' can't be started by this version: Step {first.StepNumber} ({first.StepId}) uses '{first.Field}:', and this engine reads that field but does not run it yet{more}. Starting it anyway would run the step as if the '{first.Field}:' were not there, which is not what the file says. You can still open the workflow and review it.");
            }

            // ---- PHASE B: the normal start preparation, over the whole frozen closure.
            // [D4] Started though a rule currently hides it (activation curates the menu, it isn't a lock): record a
            // plain advisory so the run is honest. Skip when force-active (that's "required", not "off the menu").
            if (!active && !forceActive)
                _sessions.Bus.PublishActivity(new ActivityEvent
                {
                    Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Kind = "start_workflow_off_menu", Ok = true, Target = def.Name,
                    Label = $"Started '{def.Name}' though it's off the current menu ({activeReason ?? "hidden by a rule"}): you can still run it; activation curates the menu, it doesn't lock a workflow.",
                });

            // Start-of-run snapshots for diff-based verifies — taken BEFORE any step mutates the model.
            var session = context.Session;
            var aux = await CaptureStartSnapshotsAsync(closure, session == null ? null : (Func<Task<Semanticus.Analysis.BpaScorecard>>)BpaScanAsync,
                session == null ? null : (Func<Task<Semanticus.Analysis.Scorecard>>)AiReadinessScanAsync);

            var modelName = session == null ? null : await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            // Conditions in EVERY reachable owner contribute roots, but facts are still gathered ONCE and stay
            // one point-in-time snapshot: no condition causes a second model read or git process.
            var conditionFacts = await GatherFactsForAsync(ClosureConditionRoots(closure));
            var hook = System.Threading.Interlocked.Exchange(ref WorkflowStartReadyForTest, null);
            if (hook != null) await hook();
            WorkflowRunState run;
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow start");
                var live = context.Live;   // bind the identity current at registration; a benign reconnect is allowed
                run = context.WorkflowRuns.Start(closure, global, initialize: candidate =>
                {
                    candidate.Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
                    candidate.ModelIdentity = PaneIdentity(session, live);
                    candidate.ModelName = modelName;
                    candidate.ModelFingerprint = session == null ? null : VitalsFingerprintFor(session);
                    candidate.SessionId = session?.Id;
                    candidate.FrameFacts = conditionFacts;
                    WorkflowRunner.InitializeCalls(candidate);
                    WorkflowRunner.AdvancePastInapplicableSteps(candidate);
                });
                context.WorkflowAux[run.RunId] = aux;
                return PublishWorkflowRun(context, run);
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<WorkflowRunView> GetWorkflowRunAsync(string runId)
        {
            var context = _sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow run read");
                return WorkflowRunner.BuildView(context.WorkflowRuns.Require(runId));
            }
            finally { context.WorkflowGate.Release(); }
        }

        /// <summary>Submit the run's current step. <paramref name="answersJson"/> is a JSON object:
        /// a value per gate-input name, or the explicit decline sentinel
        /// {"declined": true, "reason": "..."}. The gate evaluator's rejection text steers the agent.</summary>
        public async Task<WorkflowRunView> SubmitWorkflowStepAsync(string runId, string stepId, string answersJson, string origin, string callGate = null)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var answers = ParseAnswers(answersJson);
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow step");
                var run = context.WorkflowRuns.Require(runId);
                if (!string.IsNullOrWhiteSpace(callGate) && run.CurrentStep?.Call != null && !string.IsNullOrWhiteSpace(run.CurrentStep.Call.Workflow))
                {
                    var on = string.Equals(callGate.Trim(), "on", StringComparison.OrdinalIgnoreCase);
                    run.CallStrictnessOverride[run.CurrentStep.Call.Workflow] = on ? "hard" : "off";
                }
                // Address, coherence, frozen-source and R1 (unusable deferred list on the DECLARING submission)
                // refusals happen before the receipt scope. The runner validates again inside the transition,
                // but a bad call must never rewrite witness evidence. A current unexpanded deferred loop's own
                // expansion refusals are NOT in that list: they ride the expansion-only route below, which never
                // enters the scope at all.
                var submittingStep = WorkflowRunner.ValidateSubmission(run, stepId, answers);
                // An expansion-only current row (self-sourced preparation, inline expansion, or a deferred loop
                // reached unexpanded) only splices the plan: no verify runs, so there is no evidence to frame
                // and no witness to lock.
                // Classified after parsing and the address/coherence refusals above, and deliberately OUTSIDE the
                // scope below — installing receipt bookkeeping for a transition that records none would let a
                // setup call stamp a lock the first real iteration submission is supposed to own.
                if (WorkflowRunner.IsExpansionOnlySubmission(run))
                {
                    await WorkflowRunner.SubmitStepAsync(run, stepId, answers, null);
                    EnsureContextCurrent(context, "Workflow step");
                    return PublishWorkflowRun(context, run, origin);
                }
                using (run.CaptureSubmissionFrame())
                {
                    // The step being submitted (for a witness-revision receipt's stepId) is captured before the
                    // runner can advance. The proof frame is captured at the same boundary and remains selected
                    // through receipt bookkeeping, even after StepIndex changes or the runner throws.
                    run.SubmissionOrigin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
                    var needsDaxInventory = run.CurrentStep?.Gate?.Inputs
                        .Any(i => i.DaxPurity == "no-bare-measures") == true;
                    (string[] Measures, (string Table, string Name, bool IsCalculated)[] Columns, string[] Functions,
                        (string Name, bool IsCalculated)[] Tables)? modelDaxInventory =
                        !needsDaxInventory || context.Session == null ? null
                        : await context.Session.ReadAsync(m => (
                            m.Tables.SelectMany(t => t.Measures).Select(mm => mm.Name).ToArray(),
                            m.Tables.SelectMany(t => t.Columns.Select(c =>
                                (t.Name, c.Name, c is CalculatedColumn || c is CalculatedTableColumn || t is CalculatedTable))).ToArray(),
                            m.Functions.Select(f => f.Name).ToArray(),
                            m.Tables.Select(t => (t.Name, t is CalculatedTable)).ToArray()));
                    try
                    {
                        await WorkflowRunner.SubmitStepAsync(run, stepId, answers, ExecuteWorkflowVerifyAsync,
                            modelDaxInventory?.Measures, modelDaxInventory?.Columns, modelDaxInventory?.Functions,
                            modelDaxInventory?.Tables);
                    }
                    finally
                    {
                        // E3(a) — lock the witness on FIRST submission of any input that feeds a dax_equivalence probe,
                        // and record a revision receipt on a later change. The captured proof frame is still active here.
                        RecordWitnessLocks(run, submittingStep);
                        run.SubmissionOrigin = null;
                    }
                }
                EnsureContextCurrent(context, "Workflow step");
                return PublishWorkflowRun(context, run, origin);
            }
            catch (InvalidOperationException ex) when (!string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) && GateCopy.TryRead(ex, out _, out _))
            {
                GateCopy.TryRead(ex, out _, out var display);
                throw new InvalidOperationException(display);
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<WorkflowRunView> SkipWorkflowStepAsync(string runId, string stepId, string reason, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow step");
                var run = context.WorkflowRuns.Require(runId);
                WorkflowRunner.SkipStep(run, stepId, reason);
                var view = PublishWorkflowRun(context, run, origin);
                var skipped = view.Steps[view.StepIndex - 1];
                var certificateConsequence = skipped.Note != null
                    && skipped.Note.Contains(WorkflowRunner.OverriddenCertificateConsequence, StringComparison.Ordinal)
                    ? WorkflowRunner.OverriddenCertificateConsequence : null;
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "skip_workflow_step",
                    Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                    Label = $"Workflow '{view.Workflow}': step skipped ({reason.Trim()})"
                        + (certificateConsequence == null ? "" : " " + certificateConsequence),
                    Target = view.Workflow,
                    Ok = true,
                    Result = new { view.RunId, StepId = skipped.StepId, Reason = reason.Trim(), CertificateConsequence = certificateConsequence },
                });
                return view;
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<WorkflowRunView> AbortWorkflowAsync(string runId, string reason, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow abort");
                var run = context.WorkflowRuns.Require(runId);
                WorkflowRunner.Abort(run, reason);
                return PublishWorkflowRun(context, run, origin);
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidenceAsync(string runId)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var session = context.Session;
            if (session == null)
                return new Semanticus.Engine.Evidence.EvidenceArtifact { Error = "No open model. Open a model before exporting workflow evidence." };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var modelIdentity = PaneIdentity(session, context.Live);

            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow evidence export");
                WorkflowRunState run;
                try { run = context.WorkflowRuns.Require(runId); }
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
            finally { context.WorkflowGate.Release(); }
        }

        /// <summary>Broadcast the transition; a TERMINAL transition additionally publishes the full run
        /// record as an ActivityEvent so the ExperienceTee persists it (learning-loop §3.1 — the
        /// answers/declines/evidence outlive the session). Called under the captured context's workflow gate.</summary>
        private WorkflowRunView PublishWorkflowRun(SessionContext context, WorkflowRunState run, string origin = null)
        {
            var view = WorkflowRunner.BuildView(run);
            if (ReferenceEquals(_sessions.CurrentContext, context)) _sessions.Bus.PublishWorkflow(view);
            if (run.Status != "active")
            {
                context.WorkflowAux.Remove(run.RunId);
                _ = PublishActivityAsync(new ActivityEvent
                {
                    Kind = "workflow_run",
                    Origin = origin ?? "human",
                    Label = $"Workflow '{run.Def.Name}' {run.Status} ({run.Results.Count(r => r.Status == "passed")}/{run.Results.Count} steps passed)",
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

        // ---- E3 receipts: witness lock + candidate-drift tracking ---------------------------------

        /// <summary>SHA-256 of a DAX expression NORMALIZED (CRLF→LF, trimmed, runs of whitespace collapsed) so a
        /// pure reformat is not read as a change. Used for the witness lock (E3a) and candidate-drift (E3b).</summary>
        internal static string NormalizeDaxHash(string expr)
        {
            var norm = System.Text.RegularExpressions.Regex.Replace((expr ?? "").Replace("\r\n", "\n").Trim(), @"\s+", " ");
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(norm))).ToLowerInvariant();
        }

        internal static string ExactDaxHash(string expr)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(expr ?? ""))).ToLowerInvariant();
        }

        /// <summary>[T220 / F-120] The dax_equivalence probes ONE submission may lock: the declarations carried by
        /// the planned rows the captured submission frame can see. This used to scan every frozen definition the
        /// run can reach, which asked a definition-shaped question and then answered it with frame-shaped data.
        /// <c>AllAnswers</c> resolves inside the captured frame, so a plain caller input that merely SHARED a name
        /// with a probe only a CALLEE declares minted a caller-frame witness receipt for a probe the caller never
        /// declared. Row visibility here is the same rule <c>WorkflowRunner.OverlayAnswers</c> applies to the very
        /// answers this scan reads: a same-frame row always, a cross-frame row only when neither side is a `call`
        /// projection. Legitimate callee locking is untouched, because on the callee's own submission the callee
        /// row IS the same-frame row.
        ///
        /// The answer-side CUTOFF is deliberately NOT applied to declarations. E3(a) locks a witness on FIRST
        /// sight, and both shipped equivalence workflows collect the witness input several rows BEFORE the row
        /// that declares the probe (`optimize-dax` step 1 vs step 4, `verified-measure` step 4 vs step 6). Bounding
        /// declarations by the submitted row would move those locks later and silently drop every revision receipt
        /// in between, a changed no-loop view byte and a changed permanent-record byte, which owner-plan stop
        /// condition 7 forbids. Frame ownership is what the finding turns on, and frame ownership alone closes it.</summary>
        private static IEnumerable<string> VisibleWitnessProbes(WorkflowRunState run)
        {
            var frame = run.AnswerResolutionContext().Frame;
            var probes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < run.Plan.Count; i++)
            {
                var sameFrame = string.Equals(run.Plan[i].FrameId, frame.FrameId, StringComparison.Ordinal);
                if (!sameFrame && (string.Equals(frame.ProjectionKind, "call", StringComparison.Ordinal)
                    || string.Equals(run.FrameAtPlanIndex(i).ProjectionKind, "call", StringComparison.Ordinal)))
                    continue;
                foreach (var v in run.Plan[i].Step.Gate?.Verify ?? Array.Empty<VerifySpec>())
                    if (v.Kind == "dax_equivalence" && !string.IsNullOrWhiteSpace(v.Probe) && seen.Add(v.Probe))
                        probes.Add(v.Probe);
            }
            return probes;
        }

        /// <summary>E3(a) — for every input that feeds a dax_equivalence probe, lock its normalized-expression hash on
        /// first sight and append a {beforeHash, afterHash, stepId, timestamp} receipt on any later change (never a
        /// silent replace). Called under _workflowGate right after a submit; defensive (a receipt must never break a
        /// submit).</summary>
        internal static void RecordWitnessLocks(WorkflowRunState run, string submittingStep)
        {
            try
            {
                var all = WorkflowRunner.AllAnswers(run);
                foreach (var probe in VisibleWitnessProbes(run))
                {
                    if (!all.TryGetValue(probe, out var a) || a == null || !a.Answered) continue;
                    var h = NormalizeDaxHash(a.Value);
                    if (!run.WitnessLocks.TryGetValue(probe, out var old)) { run.WitnessLocks[probe] = h; continue; }
                    if (old == h) continue;
                    run.WitnessRevisions.Add(new WitnessRevision
                    {
                        Probe = probe, BeforeHash = old, AfterHash = h,
                        StepId = submittingStep, TimestampUtc = DateTime.UtcNow.ToString("o"),
                    });
                    run.WitnessLocks[probe] = h;
                }
            }
            catch { /* receipt bookkeeping must never fail a submit */ }
        }

        /// <summary>E3(b) — after a measure write, record its new normalized-expression hash as the WORKFLOW-AUTHORED
        /// hash for every active run whose CURRENT step declares this op and is an actual performing row (the same
        /// step-scoped rule EnforceBindingAsync uses, expansion-only setup rows excluded alike). A later change
        /// while NO declaring step is current leaves the recorded hash stale ⇒ the equivalence
        /// verify sees drift. Fully defensive: never throws into the measure-write path.</summary>
        private async Task RecordWorkflowAuthoredMeasureAsync(string op, string measureRef, string expression)
        {
            try
            {
                var context = _sessions.CurrentContext;
                if (context?.Session == null || string.IsNullOrWhiteSpace(measureRef)) return;
                var canon = await context.Session.ReadAsync(m => ObjectRefs.Resolve(m, measureRef) is Measure mm ? ObjectRefs.For(mm) : null);
                if (canon == null) return;   // not a measure — drift only tracks measures (the equivalence target)
                var h = NormalizeDaxHash(expression);
                await context.WorkflowGate.WaitAsync();
                try
                {
                    foreach (var run in context.WorkflowRuns.ActiveRuns())
                    {
                        // Same rule, same classification: an expansion-only current row is not the moment a write
                        // becomes workflow-authored. Recording provenance there would label a premature write as
                        // attested and, worse, overwrite the real baseline, so the equivalence verify would then find
                        // no drift and prove a witness against an unattested candidate.
                        if (WorkflowRunner.IsExpansionOnlySubmission(run)) continue;
                        var step = run.CurrentStep;
                        if (step?.Ops == null || !step.Ops.Contains(op, StringComparer.Ordinal)) continue;
                        if (!context.WorkflowAux.TryGetValue(run.RunId, out var aux)) continue;
                        (aux.AuthoredMeasureHashes ??= new Dictionary<string, string>(StringComparer.Ordinal))[canon] = h;
                    }
                }
                finally { context.WorkflowGate.Release(); }
            }
            catch { /* drift bookkeeping must never break a measure write */ }
        }

        // ---- verify executors (the engine-evaluated half of a gate — never self-graded) -----------

        /// <summary>Target-object convention: the run's LATEST answered input of type `objectRef`
        /// names the object the verifies act on (seed workflows collect it at the create/edit step).
        /// A verify that needs a target and can't find one FAILS with instructive text — an
        /// unrunnable check must never quietly pass a hard gate.</summary>
        internal async Task<VerifyResult> ExecuteWorkflowVerifyAsync(
            VerifySpec spec, WorkflowStep step, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            switch (spec.Kind)
            {
                case "dax_probe": return await WorkflowDaxProbeAsync(spec, run, answers);
                case "dax_equivalence": return await WorkflowDaxEquivalenceAsync(spec, step, run, answers);
                case "expected_values": return await WorkflowExpectedValuesAsync(spec, step, run, answers);
                case "anchor_coverage": return WorkflowAnchorCoverage(spec, step, run, answers);
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

        /// <summary>The latest visible objectRef answer in plan order, bounded by the submitted row.
        /// Its type belongs to the row that supplied the answer: a same-named text answer must never borrow
        /// an older objectRef declaration, including one across a call boundary.</summary>
        internal static string LatestObjectRefAnswer(WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            foreach (var source in WorkflowRunner.VisibleAnswerSources(run)
                .OrderBy(x => x.Value.PlanIndex).ThenBy(x => x.Value.DeclarationOrder).Reverse())
            {
                if (source.Value.Declaration?.Type == "objectRef"
                    && answers.TryGetValue(source.Key, out var answer) && answer?.Answered == true)
                    return ResolveWorkflowObjectRefAlias(run, answer.Value);
            }
            return null;
        }

        private static string ResolveWorkflowObjectRefAlias(WorkflowRunState run, string objectRef)
        {
            if (run == null || string.IsNullOrWhiteSpace(objectRef)) return objectRef;
            var current = objectRef;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(current) && run.ObjectRefAliases.TryGetValue(current, out var next)
                && !string.IsNullOrWhiteSpace(next))
                current = next;
            return current;
        }

        private static Measure ResolveWorkflowMeasure(Model model, WorkflowRunState run, string objectRef)
        {
            if (ObjectRefs.Resolve(model, objectRef) is Measure direct) return direct;
            var receipt = run?.LastPassedEquivalenceCandidate;
            if (run?.CoverageSurface == null || receipt == null
                || string.IsNullOrWhiteSpace(receipt.TargetLineageTag)
                || string.IsNullOrWhiteSpace(receipt.TargetRefAtProof)
                || !string.Equals(ResolveWorkflowObjectRefAlias(run, receipt.TargetRefAtProof), objectRef,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            var renamed = model.Tables.SelectMany(t => t.Measures)
                .FirstOrDefault(m => string.Equals(m.LineageTag, receipt.TargetLineageTag, StringComparison.Ordinal));
            if (renamed != null) run.ObjectRefAliases[objectRef] = ObjectRefs.For(renamed);
            return renamed;
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
            var context = _sessions.CurrentContext;
            BaselineState baseline;
            await context.BaselineGate.WaitAsync();
            try { baseline = context.Baselines.Get(id.Value.Trim()); }
            finally { context.BaselineGate.Release(); }
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
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "decided integrity or reconciliation checks", Detail = "the safety-net suite could not decide any data-integrity or reconciliation check in this session (offline or all probes not verifiable). Connect a live model to replay it. " + detail };
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
            var context = _sessions.CurrentContext;
            ChangeItem item;
            await context.PlanGate.WaitAsync();
            try { item = context.Plans.Current?.Find(id.Value.Trim())?.Clone(); }
            finally { context.PlanGate.Release(); }
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
                return new VerifyResult { Kind = kind, Status = "passed", Detail = $"'{name}' passes the admission dry-run: it parses, every op is real, and every gate binding resolves. It is safe to enable." };

            var warns = (report.Findings ?? Array.Empty<CheckFinding>())
                .Where(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal))
                .Select(f => f.Message).ToArray();
            var reason = report.ParseError != null
                ? "parse error: " + report.ParseError
                : (warns.Length > 0 ? string.Join(" | ", warns) : "the admission dry-run reported it is not Ok.");
            return new VerifyResult { Kind = kind, Status = "failed", Detail = $"'{name}' is NOT admissible: {reason}. Fix it with save_workflow and re-submit (get_workflow shows the current text; check_workflow shows the full report)." };
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
            await Task.CompletedTask.ConfigureAwait(false);   // keep the shared async verifier signature without scheduling work
            const string kind = "baseline_captured";
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var labelAns) || !labelAns.Answered || string.IsNullOrWhiteSpace(labelAns.Value))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' must carry the LABEL the certified figures were captured under (capture_baseline with that label first, then record it here)." };
            var label = labelAns.Value.Trim();

            var file = CertifiedFilePath();
            if (file == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "there is nowhere to store certified figures yet. Open or save the model so its .semanticus sidecar has a home, then capture the close's totals with capture_baseline(label:…)." };

            var cf = CertifiedStore.Load(file, out var corrupt);
            if (corrupt)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "the certified-figures store (.semanticus/certified-baselines.json) is present but unreadable. Repair or move it, then re-capture the close's figures." };

            var bl = CertifiedStore.Find(cf, label);
            if (bl == null || bl.Entries.Count == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"no certified figures were captured under '{label}'. Capture every control total and the headline at its stated context with capture_baseline(label:\"{label}\") BEFORE signing off: that recording is what lets a later refresh or edit that moves a signed-off number be caught (detection, not prevention). This gate cannot pass until they are captured." };
            // P1-3: never bless a tampered record.
            if (!CertifiedStore.HashMatches(bl))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the certified baseline '{label}' has been modified since capture (content hash mismatch): refusing to pass the close on a tampered record. Re-capture the figures under a new label." };

            // The DECLARED control totals, strictly parsed. An unparseable line is a REFUSAL (P1-A) — never dropped
            // silently into an "attested" bucket where a typo would slip the gate.
            var (declared, bad) = ParseDeclaredControlTotals(run);
            if (bad.Count > 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the close's control-totals declaration has {bad.Count} line(s) the gate cannot parse as `measure:Table/Name ~ <dax context or (grand total)> ~ <exact value>`: {string.Join(" | ", bad.Take(5))}. Every certified figure must be machine-checkable. Fix or remove each unparseable line; the gate will not silently treat it as attested." };
            if (declared.Count == 0)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "the close declared no control totals to certify. Declare each certified figure as `measure:Table/Name ~ <dax context or (grand total)> ~ <exact value>` (one per line) so the gate can bind ref + context + value: it cannot pass on an empty declaration." };

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
                if (!entry.StabilityProvable) { problems.Add($"{d.Ref} at [{where}] is certified but its context references a measure, so it can never be re-checked identically. Restate the context with columns only (no measure reference), then re-capture"); continue; }
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

        /// <summary>[T220] Control totals are DEFINITION provenance, not answer provenance, so a callee's
        /// `baseline_captured` must read the callee's own instantiated slot_values even when the caller carries
        /// a different control_totals value. Read through the submitted row's frozen owner, never inferred from
        /// the root.</summary>
        internal static string ControlTotalsSlotText(WorkflowRunState run)
        {
            var owner = run?.SubmittedRow == null ? run?.Def : run.RowOwner(run.SubmittedRow);
            if (owner?.Provenance == null || !owner.Provenance.TryGetValue("slot_values", out var sv) || string.IsNullOrWhiteSpace(sv)) return null;
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
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a live connection", Detail = "offline: no live connection, so the probe could not be proven. Connect to a live model and submit this step again." };
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var expected) || !expected.Answered)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' has no answered value to compare against." };
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef-typed input names the measure to probe: the workflow must collect one (e.g. an input `target` of type objectRef) before this gate." };

            var s = _sessions.Require();
            var name = await s.ReadAsync(m => ResolveWorkflowMeasure(m, run, target)?.Name);
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

        /// <summary>Prove the target measure's CURRENT expression equivalent to the recorded witness/original
        /// (the `probe:` input, captured at an earlier step). The matrix comes from an answered `equivalenceGrid`
        /// input (comma-separated columns); an EMPTY effective grid is a grand-total-only comparison, which is not
        /// a proof — the gate blocks it as `unavailable` (E2 fail-closed). The pinned/open shape partition comes
        /// from the static pinnedShapes/openShapes keys unioned with the run-decided `openShapesFrom` input.</summary>
        internal static bool ProbeRequiresCurrentDaxPurity(WorkflowRunState run, string probe)
        {
            // [T220] The guard now covers what this method actually reads. It used to open on run.Def.Steps,
            // which has nothing to do with the exact earlier planned row it then inspects.
            if (run == null || run.Plan == null || run.Results == null
                || run.Plan.Count != run.Results.Count || string.IsNullOrWhiteSpace(probe)) return false;
            for (var i = Math.Min(run.StepIndex, run.Results.Count - 1); i >= 0; i--)
            {
                if (!run.Results[i].Answers.ContainsKey(probe)) continue;
                return run.Plan[i].Step.Gate?.Inputs.Any(input =>
                    string.Equals(input.Name, probe, StringComparison.Ordinal)
                    && string.Equals(input.DaxPurity, "no-bare-measures", StringComparison.Ordinal)) == true;
            }
            return false;
        }

        private static VerifyResult CurrentWitnessPurityDrift(string witness,
            IReadOnlyCollection<string> modelMeasureNames,
            IReadOnlyCollection<(string Table, string Name, bool IsCalculated)> modelColumns,
            IReadOnlyCollection<string> modelFunctionNames,
            IReadOnlyCollection<(string Name, bool IsCalculated)> modelTables)
        {
            const string kind = "dax_equivalence";
            var functions = DaxBench.ModelFunctionReferences(witness, modelFunctionNames);
            if (functions.Length != 0)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = "a witness that still resolves only to base columns",
                    Detail = $"witness purity drift: the call {functions[0]}() now resolves to a model-defined function; a model function was renamed or added since the witness was locked. Inline the function's logic and re-submit the witness before equivalence.",
                };

            var calculatedTables = DaxBench.CalculatedTableReferences(witness, modelTables);
            if (calculatedTables.Length != 0)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = "a witness that still resolves only to base tables and columns",
                    Detail = $"witness purity drift: the reference {calculatedTables[0]} now resolves to a calculated table; a calculated table was created or renamed since the witness was locked. Rebuild the calculated table's row logic inline from base tables and re-submit the witness before equivalence.",
                };

            var calculated = DaxBench.CalculatedColumnReferences(witness, modelMeasureNames, modelColumns, out _);
            if (calculated.Length != 0)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = "a witness that still resolves only to base columns",
                    Detail = $"witness purity drift: the reference {calculated[0]} now resolves to a calculated column; a model object was changed or added since the witness was locked. Inline the column's logic and re-submit the witness before equivalence.",
                };

            var measures = DaxBench.MeasureReferences(witness, modelMeasureNames, modelColumns);
            if (measures.Length == 0) return null;
            var bareNames = new HashSet<string>(DaxBench.BareReferences(witness), StringComparer.OrdinalIgnoreCase);
            var bareMeasure = measures.FirstOrDefault(bareNames.Contains);
            var reference = DaxBench.BracketName(bareMeasure ?? measures[0]);
            return new VerifyResult
            {
                Kind = kind,
                Status = "unavailable",
                Missing = "a witness that still resolves only to base columns",
                Detail = bareMeasure != null
                    ? $"witness purity drift: the bare reference {reference} now resolves to a measure; a model object was renamed or added since the witness was locked. Qualify the intended base-column reference, or rebuild and re-submit the witness before equivalence."
                    : $"witness purity drift: the reference {reference} now resolves to a measure; a model object was renamed or added since the witness was locked. Rebuild the witness from qualified base columns and re-submit it before equivalence.",
            };
        }

        private async Task<VerifyResult> WorkflowDaxEquivalenceAsync(VerifySpec spec, WorkflowStep step, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "dax_equivalence";
            // E2 — the mandatory-evidence verify: every applicable-but-no-evidence path is UNAVAILABLE (fail-closed,
            // blocks a hard step), naming what was missing. Only a real divergence on a PINNED shape is `failed`.
            if (string.IsNullOrWhiteSpace(spec.Probe) || !answers.TryGetValue(spec.Probe, out var original) || !original.Answered)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = $"the witness input '{spec.Probe}'", Detail = $"the probe input '{spec.Probe}' must carry the witness/original expression to compare against: it was not answered." };
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "the target measure ref", Detail = "no answered objectRef-typed input names the measure under proof." };

            var s = _sessions.Require();
            var purityRequired = ProbeRequiresCurrentDaxPurity(run, spec.Probe);
            var modelState = await s.ReadAsync(m =>
            {
                var mm = ResolveWorkflowMeasure(m, run, target);
                return (
                    Current: mm?.Expression,
                    CanonRef: mm == null ? null : ObjectRefs.For(mm),
                    TargetLineageTag: mm?.LineageTag,
                    Measures: purityRequired
                        ? m.Tables.SelectMany(t => t.Measures).Select(measure => measure.Name).ToArray()
                        : null,
                    Columns: purityRequired
                        ? m.Tables.SelectMany(t => t.Columns.Select(column =>
                            (t.Name, column.Name, column is CalculatedColumn || column is CalculatedTableColumn || t is CalculatedTable))).ToArray()
                        : null,
                    Functions: purityRequired ? m.Functions.Select(function => function.Name).ToArray() : null,
                    Tables: purityRequired ? m.Tables.Select(t => (t.Name, t is CalculatedTable)).ToArray() : null);
            });
            var current = modelState.Current;
            var canonRef = modelState.CanonRef;
            var targetLineageTag = modelState.TargetLineageTag;
            if (current == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "the target measure", Detail = $"'{target}' does not resolve to a measure on the current model." };

            // A locked witness is still DAX and therefore resolves against the CURRENT model. Re-run the same
            // definition-gated purity boundary immediately before every comparison consumption, including the
            // receipt-based not-applicable shortcut below. A rename or addition must not turn old text into a
            // newly trusted measure, calculated column, or model function call.
            if (purityRequired)
            {
                var drift = CurrentWitnessPurityDrift(original.Value, modelState.Measures, modelState.Columns,
                    modelState.Functions, modelState.Tables);
                if (drift != null) return drift;
            }

            var coverageBacked = run.CoverageSurface != null;
            var currentHash = ExactDaxHash(current);
            var priorCandidate = run.LastPassedEquivalenceCandidate;
            if (coverageBacked
                && !WorkflowRunner.WhenHolds(spec.When, answers)
                && !string.IsNullOrWhiteSpace(targetLineageTag)
                && string.Equals(priorCandidate?.TargetLineageTag, targetLineageTag, StringComparison.Ordinal)
                && string.Equals(priorCandidate?.ExpressionHash, currentHash, StringComparison.Ordinal))
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "not_applicable",
                    Detail = $"when '{spec.When}' did not hold and the candidate is byte-identical to the expression previously proven by dax_equivalence; not applicable.",
                };

            // E3(b) — CANDIDATE DRIFT: the target's current expression no longer matches the last workflow-authored
            // write ⇒ someone changed it OUTSIDE the workflow, so the witness would be proven against an unattested
            // candidate. Refuse as unavailable (a model read, checkable even offline — before the live check).
            if (canonRef != null
                && _sessions.CurrentContext.WorkflowAux.TryGetValue(run.RunId, out var aux)
                && aux?.AuthoredMeasureHashes != null
                && aux.AuthoredMeasureHashes.TryGetValue(canonRef, out var authored)
                && authored != NormalizeDaxHash(current))
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable", Missing = "an unchanged candidate",
                    Detail = $"candidate drift: '{canonRef}' was changed since the workflow authored it (its current expression hash no longer matches the workflow-authored one). Re-author it inside the workflow before proving equivalence, so the witness is checked against an attested candidate.",
                };

            if (_live == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a live connection", Detail = "offline: no live connection, so equivalence could not be proven (open_live/open_local and re-submit for real evidence)." };

            // Once anchor_coverage has locked the v7 surface, it is the authoritative partition. The accumulated
            // answer remains the legacy source only for definitions that never established that lock.
            var countersignActive = string.Equals(spec.OpenMismatch, "countersign", StringComparison.Ordinal);
            if (countersignActive && !coverageBacked)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = "the locked anchor_coverage surface",
                    Detail = "openMismatch countersign cannot classify open disputes because this run has no locked coverage surface. Declare and pass anchor_coverage before this equivalence step.",
                };
            var grid = coverageBacked
                ? run.CoverageSurface.CurrentGrid.ToArray()
                : answers.TryGetValue("equivalenceGrid", out var g) && g.Answered
                    ? g.Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray()
                    : Array.Empty<string>();

            // E1 ADDENDUM — resolve the per-run OPEN partition before spending a comparison. `openShapesFrom` binds
            // a step input whose ANSWER lists the run-decided open shapes (the v5 seed cannot know the partition at
            // authoring time — it comes from the requirement's context ledger). Answer validation is observable
            // offline (benchmark_delta's pattern), so it sits before the live check; a bad id refuses fail-closed.
            var effectiveGrid = DaxBench.NormalizeGroupBy(grid);   // THE effective-grid point — raw .Length reopens the whitespace bypass
            var evaluatedIds = DaxBench.BuildComparisonShapes(grid).Select(sh => DaxBench.ShapeId(sh, effectiveGrid)).ToList();
            // Physical query dedup still runs one non-total query for a one-column grid. Coverage nevertheless owns
            // separate axis and cross grains, so retain the latter as a logical id for partitioning and ledger state.
            if (coverageBacked && effectiveGrid.Length == 1 && !evaluatedIds.Contains("cross", StringComparer.Ordinal))
                evaluatedIds.Add("cross");
            string partitionError = null;
            var openShapes = coverageBacked
                ? run.CoverageSurface.CurrentOpenGrains.ToArray()
                : EquivalenceGate.ResolveOpenShapes(spec, answers, evaluatedIds.ToArray(), out partitionError);
            if (partitionError != null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a valid open-shape partition", Detail = partitionError };

            // Blocker 3 — the PARTITION LOCK, taken at the first actually-run comparison (an offline submission
            // returned above and locked nothing). A re-submission with a DIFFERENT effective open set gets a receipt
            // + a blocking `unavailable`; the next submission evaluates fresh under the new locked partition. The
            // key carries the verify's ORDINAL within the step (deterministic parse order), so two same-step
            // same-probe verifies with different partitions can never deadlock on one shared lock.
            var verifyIndex = step?.Gate?.Verify != null ? Array.IndexOf(step.Gate.Verify, spec) : -1;
            if (verifyIndex < 0)
                // Defensive: the spec must be one of the submitting step's own verify entries — a future refactor
                // that copies/reallocates the spec array would silently collapse every verify onto one -1 lock key,
                // resurrecting the shared-lock deadlock. Refuse loudly instead.
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable", Missing = "a stable verify identity",
                    Detail = "internal error: this dax_equivalence spec is not among the submitting step's verify entries, so its partition lock cannot be keyed. Report this; the spec array must not be copied between parse and execution.",
                };
            var partitionBlock = EquivalenceGate.RegisterPartition(run, step?.Id ?? run.CurrentStep?.Id, verifyIndex, spec.Probe, openShapes);
            if (partitionBlock != null)
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable",
                    Missing = "shape partition changed after evidence was seen. Re-run the verify with the new partition on a fresh evaluation",
                    Detail = partitionBlock,
                };

            // The answered objectRef names the measure under proof — spec it so the proof runs measure-faithfully
            // against the target's REAL home table (see DaxQuerySpec; a Trusted=false spec degrades honestly and
            // the evidence ladder turns that into a blocking 'unavailable' below, never a false 'passed').
            var eqSpec = await BuildQuerySpecAsync(_live, target);
            var eq = await DaxBench.VerifyEquivalenceAsync(_live, original.Value, current, grid, Array.Empty<string>(), 100000, eqSpec);
            if (coverageBacked) eq = EquivalenceGate.PreserveLogicalCross(eq, effectiveGrid.Length);

            // E1/E2 — ONE evidence ladder (DaxBench.ClassifyEquivalenceEvidence) grades the comparison; the gate
            // applies the pinned/open shape ledger on top: pinned mismatches FAIL, open mismatches are RECORDED,
            // and every no-authoritative-evidence rung (error / degraded / degraded_mismatch / zero rows /
            // truncated / thin) is a blocking `unavailable` with the fidelity caveat preserved.
            var outcome = EquivalenceGate.Evaluate(eq, coverageBacked ? Array.Empty<string>() : spec.PinnedShapes,
                openShapes, effectiveGrid.Length);
            if (coverageBacked)
            {
                // Ledger updates happen for every v7 comparison whether or not this verify opts into countersign
                // enforcement. That keeps the audit complete while preserving report-only behavior by default.
                EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);
                if (countersignActive)
                {
                    answers.TryGetValue("countersign", out var countersign);
                    outcome = EquivalenceGate.ApplyOpenMismatchCountersign(run, step?.Id ?? run.CurrentStep?.Id,
                        outcome, countersign);
                }
                if (string.Equals(outcome.Status, "passed", StringComparison.Ordinal))
                    run.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt
                    {
                        TargetLineageTag = targetLineageTag,
                        ExpressionHash = currentHash,
                        TargetRefAtProof = canonRef,
                    };
            }
            return new VerifyResult
            {
                Kind = kind,
                Status = outcome.Status,
                Missing = outcome.Missing,
                Detail = outcome.Detail,
                Shapes = outcome.Shapes,
                MismatchCells = outcome.MismatchCells.Length == 0 ? null : outcome.MismatchCells,
            };
        }

        // The verify-side query evaluation ceiling (seconds). A hard measure's anchor point query is SARGable and
        // should be milliseconds; anything past this ceiling is a runaway (a bad predicate, a degenerate model) that
        // must surface as `unavailable`, never hang or crash the run — see RunVerifyQueryAsync. The extra grace only
        // guards a driver that ignores BOTH Cancel and CommandTimeout. One TOTAL budget bounds a whole expected_values
        // verify across all its anchors. Internal overrides let tests exercise the ceiling in seconds, not minutes.
        private const int VerifyQueryCeilingSecondsDefault = 30;
        private const int VerifyPostCancelGraceSecondsDefault = 15;
        private const int VerifyTotalBudgetSecondsDefault = 60;
        internal int VerifyCeilingOverrideForTest;      // seconds; 0 = default
        internal int VerifyGraceOverrideForTest;        // seconds; 0 = default
        internal int VerifyBudgetOverrideForTest;       // seconds; 0 = default
        private int VerifyQueryCeilingSeconds => VerifyCeilingOverrideForTest > 0 ? VerifyCeilingOverrideForTest : VerifyQueryCeilingSecondsDefault;
        private int VerifyPostCancelGraceSeconds => VerifyGraceOverrideForTest > 0 ? VerifyGraceOverrideForTest : VerifyPostCancelGraceSecondsDefault;
        private int VerifyTotalBudgetSeconds => VerifyBudgetOverrideForTest > 0 ? VerifyBudgetOverrideForTest : VerifyTotalBudgetSecondsDefault;

        /// <summary>Why a verify query timed out — the consumer's message must not conflate the two: a LANE-WAIT
        /// budget exhaustion (another query held the connection; nothing of ours ran) must never advise narrowing
        /// the anchor, and a CEILING breach (our own command ran too long) must never blame the lane.</summary>
        internal enum VerifyTimeoutCause { None, LaneWait, Ceiling }

        /// <summary>Test seam for the start-vs-abandon race: invoked in the lane-wait timeout branch immediately
        /// BEFORE the atomic abandon attempt, so a test can hold the branch until the queued command has started
        /// and prove the CAS handoff lets exactly one side win. Null in production.</summary>
        internal Action VerifyAbandonRaceHookForTest;

        /// <summary>Test seam for the phase-2 continuation delay: invoked immediately after the monitor awaits the
        /// command-start signal, so a test can hold the continuation past a clean post-deadline completion and prove
        /// the verdict keys off the ABSOLUTE command-start deadline (armed in the lane lambda), never off a timer the
        /// delayed continuation would arm. Null in production.</summary>
        internal Action VerifyPhase2DelayHookForTest;

        /// <summary>Test seam for the LANE-continuation delay — the mirror image: invoked in the lane lambda right
        /// after the stamped execution returns, so a test can park that continuation ACROSS the deadline after an
        /// ON-TIME completion and prove the verdict uses the synchronously-captured completion stamp (accepted), not
        /// the continuation's resume time (which would falsely refuse it). Null in production.</summary>
        internal Action VerifyLaneContinuationHookForTest;

        internal static (TimeSpan Token, int CommandTimeoutSeconds) ComputeVerifyQueryTiming(
            TimeSpan remaining, int ceilingSeconds)
        {
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var ceiling = TimeSpan.FromSeconds(ceilingSeconds);
            var token = remaining < ceiling ? remaining : ceiling;
            return (token, Math.Max(1, (int)Math.Ceiling(token.TotalSeconds)));
        }

        /// <summary>Run a verify-side query with a HARD ceiling — the scoped hardening from the live incident: verify
        /// execution must never crash, hang, or wedge the run. TWO PHASES over the serialized query lane:
        /// PHASE 1 — LANE WAIT, charged to the caller's TOTAL BUDGET (<paramref name="deadlineUtc"/>) only, never the
        ///     per-query ceiling: a verify QUEUED behind a healthy long query may exhaust the budget here, but it
        ///     never owned a command, so it may NOT retire the connection (the lane owner is someone else's healthy
        ///     work). An abandoned queued op bails out before spending the lane on a pointless query.
        /// PHASE 2 — the command has STARTED (we own the lane): NOW the per-query clock runs, with the token =
        ///     min(per-query ceiling, remaining budget) as the EXACT remaining TimeSpan (a 0.6s budget cancels at
        ///     0.6s; a 1.9s budget is never falsely truncated to 1s) — only the ADOMD CommandTimeout, an integer
        ///     property, rounds, and it rounds UP. Three layers bound it:
        ///     (1) the ADOMD CommandTimeout bounds the server;
        ///     (2) at the token a REAL cancellation fires (the registration invokes AdomdCommand.Cancel), the ADOMD
        ///         call completes with a cancellation error, and the lane + lifetime lease are RELEASED — awaited to
        ///         terminal completion, never abandoned. The verdict keys off ceiling EXPIRY alone: even a SUCCESS
        ///         result landing during the post-cancel grace stays timed out (it was produced past the ceiling);
        ///     (3) if the driver ignores BOTH (pathological), the grace expires and the connection is RETIRED —
        ///         legal ONLY here, where our OWN command was started and ignored Cancel.
        /// The queued→started/abandoned handoff is ONE atomic state machine (Interlocked.CompareExchange over
        /// 0=queued | 1=started | 2=abandoned): the lambda may only execute by winning 0→1, the timeout branch may
        /// only abandon by winning 0→2, and a lost abandon FALLS THROUGH to the normal started-path wait — exactly
        /// one side wins, a command can never start after abandonment, and a started command is never left without
        /// cancellation coverage.
        /// Returns <c>TimedOut</c> (the caller surfaces one 'unavailable'), <c>Retired</c> (the caller tells the
        /// user to reconnect), and the timeout <c>Cause</c> (LaneWait vs Ceiling — the caller's message must not
        /// blame the anchor for a busy lane). A caught exception is folded, never rethrown into the run.
        /// Deliberately confined to the verify executors — the interactive query doors keep their own timeouts.</summary>
        internal async Task<(ResultSet Rs, bool TimedOut, bool Retired, VerifyTimeoutCause Cause)> RunVerifyQueryAsync(
            string query, int maxRows, LiveConnection live, DateTime deadlineUtc)
        {
            if (live == null) return (ResultSet.FromError("Not connected."), false, false, VerifyTimeoutCause.None);
            try
            {
                using var cts = new System.Threading.CancellationTokenSource();
                var started = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);   // carries the ABSOLUTE command-start deadline
                var state = 0;   // 0=queued | 1=started | 2=abandoned — the ONE atomic handoff (see summary)

                var exec = live.RunExclusiveAsync(async () =>
                {
                    // The lane is ours — but the command may only start by winning the queued→started transition.
                    if (System.Threading.Interlocked.CompareExchange(ref state, 1, 0) != 0)
                        return (Rs: ResultSet.FromError("verify abandoned while queued (budget exhausted before the lane freed)."), CompletedUtc: DateTime.MinValue);
                    // The per-query token: the EXACT remaining budget, capped by the per-query ceiling — computed
                    // at COMMAND start, so lane-wait time was charged to the budget, never to this token. ONE
                    // captured startUtc anchors BOTH the token and the deadline (a second UtcNow read would
                    // slightly extend a budget-limited deadline past the true budget).
                    var startUtc = DateTime.UtcNow;
                    var remaining = deadlineUtc - startUtc;
                    var (token, commandTimeout) = ComputeVerifyQueryTiming(
                        remaining, VerifyQueryCeilingSeconds);
                    // ARM CANCELLATION HERE — inside the lane lambda, BEFORE signaling started and BEFORE launching
                    // execution — anchored to the ABSOLUTE command-start deadline. Arming in the awaiting continuation
                    // was the hole: a scheduler delay between the start signal and the continuation would arm a fresh
                    // full-duration timer, silently shifting the deadline. A zero token cancels synchronously.
                    var commandDeadlineUtc = startUtc + token;
                    if (token <= TimeSpan.Zero) cts.Cancel(); else cts.CancelAfter(token);
                    started.TrySetResult(commandDeadlineUtc);
                    // Only the ADOMD CommandTimeout (an integer property) rounds — UP, floor 1s, so the server
                    // bound is never tighter than the honest token (the token's cancellation stays authoritative).
                    // The completion timestamp is captured SYNCHRONOUSLY on the execution-producing thread (inside
                    // the stamped call's work body, immediately after the execute returns) — a timestamp taken after
                    // THIS await would record the continuation's RESUME time (the dispatcher queues continuations
                    // asynchronously), falsely refusing an on-time completion whose continuation got parked.
                    var stamped = await live.ExecuteWithinExclusiveStampedAsync(query, maxRows, commandTimeout, cts.Token);
                    VerifyLaneContinuationHookForTest?.Invoke();   // test seam: park THIS continuation past the deadline
                    return stamped;
                });

                // PHASE 1 — wait for lane ownership, bounded by the remaining TOTAL budget only.
                var budgetLeft = deadlineUtc - DateTime.UtcNow;
                if (budgetLeft <= TimeSpan.Zero) budgetLeft = TimeSpan.FromMilliseconds(1);
                await Task.WhenAny(started.Task, exec, Task.Delay(budgetLeft));
                if (!started.Task.IsCompleted)
                {
                    if (exec.IsCompleted)
                        return ((await exec).Rs, false, false, VerifyTimeoutCause.None);   // completed without starting (lease refused)
                    VerifyAbandonRaceHookForTest?.Invoke();
                    if (System.Threading.Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    {
                        _ = exec.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        // NO retirement: we never owned a command — the lane's current owner is healthy work.
                        return (new ResultSet { Error = "the total verify budget was exhausted waiting for the query lane" }, true, false, VerifyTimeoutCause.LaneWait);
                    }
                    // Lost the abandon race: the command started concurrently — fall through to the normal
                    // started-path wait (the token below keeps it under cancellation coverage).
                }

                // PHASE 2 — command started: cancellation is ALREADY ARMED (inside the lane lambda, anchored to the
                // absolute command-start deadline). This continuation never arms a timer of its own — it only derives
                // its GRACE WAIT from that absolute deadline, so a scheduler delay here can neither extend the
                // deadline nor grant a late result a fresh window.
                var commandDeadline = await started.Task;
                VerifyPhase2DelayHookForTest?.Invoke();
                var graceLeft = commandDeadline.AddSeconds(VerifyPostCancelGraceSeconds) - DateTime.UtcNow;
                if (graceLeft < TimeSpan.Zero) graceLeft = TimeSpan.Zero;
                var done = await Task.WhenAny(exec, Task.Delay(graceLeft));
                if (done != exec && !exec.IsCompleted)
                {
                    // Cancel + CommandTimeout both ignored on OUR OWN command — retirement is legal; observe the
                    // abandoned task's eventual fault so it never surfaces as unobserved.
                    RetireWedgedVerifyConnection(live);
                    _ = exec.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return (new ResultSet { Error = "query exceeded the evaluation ceiling" }, true, true, VerifyTimeoutCause.Ceiling);
                }
                var (rs, completedUtc) = await exec;   // terminal completion — the lane is released; the stamp is the
                                                       // TRUE finish time, taken on the producing thread (see lambda)
                // The verdict keys off the ABSOLUTE deadline alone: a result (even a clean one) whose command
                // FINISHED at/after the command-start deadline was produced past the ceiling and stays timed out —
                // however delayed any continuation ran. A command that finished BEFORE the deadline is accepted
                // even if the cancel timer fired or a continuation was parked past the deadline afterwards. The
                // command terminated either way, so no retirement.
                if (completedUtc >= commandDeadline)
                    return (new ResultSet { Error = "query exceeded the evaluation ceiling", ElapsedMs = rs.ElapsedMs }, true, false, VerifyTimeoutCause.Ceiling);
                // An ADOMD command-timeout/cancellation error is a ceiling breach too, not a DAX-authoring bug.
                if (!string.IsNullOrEmpty(rs.Error) && LooksLikeQueryTimeout(rs.Error))
                    return (new ResultSet { Error = "query exceeded the evaluation ceiling", ElapsedMs = rs.ElapsedMs }, true, false, VerifyTimeoutCause.Ceiling);
                return (rs, false, false, VerifyTimeoutCause.None);
            }
            catch (Exception ex)
            {
                // A verify query must NEVER crash the run — fold any stray throw into the unavailable path.
                return (new ResultSet { Error = "query exceeded the evaluation ceiling (" + ex.Message + ")" }, true, false, VerifyTimeoutCause.Ceiling);
            }
        }

        /// <summary>Retire a live connection whose query ignored both Cancel and CommandTimeout: swap it out (only
        /// if it is STILL the attached one — never drop a newer connection) and dispose it. LiveConnection.Dispose
        /// is retire-and-defer (the lifetime lease), so the actual teardown waits for the wedged op to unwind; the
        /// point is that _live no longer routes to the wedged lane. Best-effort — never throws into the run.</summary>
        private void RetireWedgedVerifyConnection(LiveConnection wedged)
        {
            try
            {
                var (_, displaced) = SwapLive(null, NewLiveIntent(), () => ReferenceEquals(_sessions.CurrentContext.Live, wedged));
                SafeDispose(displaced);
            }
            catch { /* retiring is best-effort; the ceiling result already decided the verify outcome */ }
        }

        private static bool LooksLikeQueryTimeout(string error) =>
            error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Offline v7 grain-lattice gate. The fixed equivalenceGrid/openGrains answer names are deliberate:
        /// a seed opts into this behavior only by declaring anchor_coverage, then binds its anchor input normally.</summary>
        internal static VerifyResult WorkflowAnchorCoverage(VerifySpec spec, WorkflowStep step, WorkflowRunState run,
            IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "anchor_coverage";
            VerifyResult Refuse(string missing, string detail) => new VerifyResult
            {
                Kind = kind,
                Status = "failed",
                Missing = missing,
                Detail = detail,
            };

            if (string.IsNullOrWhiteSpace(spec?.Anchors) || answers == null
                || !answers.TryGetValue(spec.Anchors, out var anchorAnswer) || !anchorAnswer.Answered)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = $"the anchors input '{spec?.Anchors}'",
                    Detail = $"anchor_coverage needs an answered '{spec?.Anchors}' input carrying the anchor JSON.",
                };

            var omittedCurrentAnchorInput = run.RunAnchorLocks.TryGetValue(spec.Anchors, out var inheritedAnchorLock)
                && (step?.Gate?.Inputs ?? Array.Empty<GateInput>()).Any(i => i.Name == spec.Anchors && i.Required == "optional")
                && !run.Results[run.StepIndex].Answers.ContainsKey(spec.Anchors);
            string anchorError = null;
            var anchors = omittedCurrentAnchorInput
                ? inheritedAnchorLock.CurrentAnchors
                : AnchorGate.Parse(anchorAnswer.Value, out anchorError);
            if (!omittedCurrentAnchorInput && anchorError != null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a well-formed anchor set", Detail = anchorError };

            if (!answers.TryGetValue("equivalenceGrid", out var gridAnswer) || !gridAnswer.Answered)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "unavailable",
                    Missing = "the equivalenceGrid declaration",
                    Detail = "anchor_coverage needs an answered equivalenceGrid input before any candidate is authored.",
                };

            var rawGrid = (gridAnswer.Value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (rawGrid.Length == 0)
                return Refuse("at least one equivalenceGrid column", "equivalenceGrid is empty. List 1 to 6 qualified display columns and re-submit.");
            if (rawGrid.Length > AnchorGate.MaxAxisColumns)
                return Refuse($"an equivalenceGrid no wider than {AnchorGate.MaxAxisColumns} columns",
                    $"equivalenceGrid exceeds the cap of {AnchorGate.MaxAxisColumns} columns. Keep at most {AnchorGate.MaxAxisColumns} display columns.");

            var gridRefs = new List<string>();
            var gridSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var authored in rawGrid)
            {
                if (!AnchorGate.TryParseColumnRef(authored, out var table, out var column))
                    return Refuse("qualified equivalenceGrid column references",
                        $"equivalenceGrid entry '{authored}' is not a qualified column reference. Use exactly 'Table'[Column].");
                var parsed = new AnchorGate.AnchorColumn { Table = table, Column = column };
                var canonical = AnchorGate.CanonicalRef(parsed);
                if (!gridSeen.Add(canonical))
                    return Refuse("unique equivalenceGrid columns",
                        $"equivalenceGrid declares column '{canonical}' more than once. Remove duplicate columns.");
                gridRefs.Add(canonical);
            }

            var validGrains = new List<string> { "grand_total" };
            validGrains.AddRange(gridRefs.Select(x => "axis:" + x));
            validGrains.Add("cross");
            var validGrainSet = new HashSet<string>(validGrains, StringComparer.Ordinal);
            var openGrains = new List<string>();
            if (answers.TryGetValue("openGrains", out var openAnswer) && openAnswer.Answered)
            {
                var rawOpen = (openAnswer.Value ?? "").Trim();
                if (rawOpen.StartsWith("[", StringComparison.Ordinal) && rawOpen.EndsWith("]", StringComparison.Ordinal))
                    rawOpen = rawOpen.Substring(1, rawOpen.Length - 2);
                foreach (var raw in rawOpen.Split(','))
                {
                    var id = raw.Trim().Trim('"', '\'');
                    if (id.Length == 0) continue;
                    if (!validGrainSet.Contains(id))
                    {
                        var provenance = "";
                        if (id.StartsWith("axis:", StringComparison.Ordinal)
                            && AnchorGate.TryParseColumnRef(id.Substring("axis:".Length), out var openTable, out var openColumn))
                        {
                            var openRef = AnchorGate.CanonicalRef(new AnchorGate.AnchorColumn { Table = openTable, Column = openColumn });
                            if (!gridSeen.Contains(openRef))
                                provenance = " The grid must list every display column the anchors and openGrains exercise.";
                        }
                        return Refuse("valid openGrains ids",
                            $"openGrains id '{id}' is not valid for the declared grid. Valid ids: [{string.Join(", ", validGrains)}].{provenance}");
                    }
                    if (!openGrains.Contains(id, StringComparer.Ordinal)) openGrains.Add(id);
                }
            }

            if (run.CoverageSurface != null)
            {
                var proposedGrid = new HashSet<string>(gridRefs, StringComparer.OrdinalIgnoreCase);
                var proposedOpen = new HashSet<string>(openGrains, StringComparer.Ordinal);
                var removedGrid = run.CoverageSurface.CurrentGrid.Where(x => !proposedGrid.Contains(x)).ToArray();
                var removedOpen = run.CoverageSurface.CurrentOpenGrains.Where(x => !proposedOpen.Contains(x)).ToArray();
                if (removedGrid.Length > 0 || removedOpen.Length > 0)
                {
                    var changes = new List<string>();
                    if (removedGrid.Length > 0) changes.Add("removed or renamed grid columns [" + string.Join(", ", removedGrid) + "]");
                    if (removedOpen.Length > 0) changes.Add("removed or renamed open grains [" + string.Join(", ", removedOpen) + "]");
                    return Refuse("a superset-only coverage revision",
                        "the locked coverage surface was narrowed: " + string.Join("; ", changes)
                        + ". A later submission may only add grid columns or openGrains entries. Start a new run to change the enforced surface.");
                }
            }

            var anchorRefs = anchors.SelectMany(a => a.Context)
                .Select(AnchorGate.CanonicalRef)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var outsideGrid = anchorRefs.Where(x => !gridSeen.Contains(x)).ToArray();
            if (outsideGrid.Length > 0)
                return Refuse("grid provenance for every anchor column",
                    $"the anchor set exercises column(s) [{string.Join(", ", outsideGrid)}] that are absent from equivalenceGrid. The grid must list every display column the anchors exercise.");

            bool ContextEquals(AnchorGate.Anchor anchor, IEnumerable<string> refs)
            {
                var wanted = new HashSet<string>(refs, StringComparer.OrdinalIgnoreCase);
                var actual = new HashSet<string>(anchor.Context.Select(AnchorGate.CanonicalRef), StringComparer.OrdinalIgnoreCase);
                return actual.SetEquals(wanted);
            }
            bool Covers(AnchorGate.Anchor anchor, string grain)
            {
                if (grain == "grand_total") return !anchor.IsShaped && anchor.Context.Length == 0;
                if (grain == "cross") return ContextEquals(anchor, gridRefs);
                var axisRef = grain.Substring("axis:".Length);
                if (!ContextEquals(anchor, new[] { axisRef })) return false;
                return !anchor.IsShaped
                    || (anchor.AxisColumns.Length == 1 && anchor.Slicers.Length == 0
                        && string.Equals(AnchorGate.CanonicalRef(anchor.AxisColumns[0]), axisRef, StringComparison.OrdinalIgnoreCase));
            }

            var anchored = validGrains.ToDictionary(g => g, g => anchors.Any(a => Covers(a, g)), StringComparer.Ordinal);
            var openSet = new HashSet<string>(openGrains, StringComparer.Ordinal);
            var contradictions = validGrains.Where(g => anchored[g] && openSet.Contains(g)).ToArray();
            if (contradictions.Length > 0)
                return Refuse("an exclusive anchored/open partition",
                    $"anchor coverage contradicts openGrains for [{string.Join(", ", contradictions)}]. A grain cannot be both anchored and declared open. Remove its matching anchor or remove it from openGrains.");

            var missing = validGrains.Where(g => !anchored[g] && !openSet.Contains(g)).ToArray();
            if (missing.Length > 0)
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                string JsonValue(AnchorGate.AnchorFilter filter)
                {
                    if (filter == null) return JsonSerializer.Serialize("<placeholder>", jsonOptions);
                    if (string.Equals(filter.Literal, "TRUE()", StringComparison.Ordinal)) return "true";
                    if (string.Equals(filter.Literal, "FALSE()", StringComparison.Ordinal)) return "false";
                    if (filter.Literal?.Length >= 2 && filter.Literal[0] == '"' && filter.Literal[filter.Literal.Length - 1] == '"')
                        return JsonSerializer.Serialize(filter.Literal.Substring(1, filter.Literal.Length - 2).Replace("\"\"", "\""), jsonOptions);
                    return filter.Literal;
                }
                string Skeleton(string grain)
                {
                    var refs = grain == "grand_total" ? Array.Empty<string>()
                        : grain == "cross" ? gridRefs.ToArray()
                        : new[] { grain.Substring("axis:".Length) };
                    var entries = refs.Select(reference =>
                    {
                        var known = anchors.SelectMany(a => a.Context).FirstOrDefault(f =>
                            string.Equals(AnchorGate.CanonicalRef(f), reference, StringComparison.OrdinalIgnoreCase));
                        return JsonSerializer.Serialize(reference, jsonOptions) + ":" + JsonValue(known);
                    });
                    return "{\"context\":{" + string.Join(",", entries) + "},\"expect\":\"<placeholder>\"}";
                }
                var repairs = string.Join("; ", missing.Select(g => g + ": `" + Skeleton(g) + "`"));
                return Refuse("one anchor or openGrains declaration for every grid grain",
                    $"anchor coverage is incomplete. Missing grains and copy-paste flat anchor skeletons: {repairs}. Add an anchor for each grain or declare that grain in openGrains, then re-submit.");
            }

            if (run.CoverageSurface == null)
            {
                run.CoverageSurface = new CoverageSurfaceLock
                {
                    InitialGrid = gridRefs.ToArray(),
                    CurrentGrid = gridRefs.ToArray(),
                    InitialOpenGrains = openGrains.ToArray(),
                    CurrentOpenGrains = openGrains.ToArray(),
                    StepId = step?.Id ?? run.CurrentStep?.Id,
                };
            }
            else
            {
                var addedGrid = gridRefs.Where(x => !run.CoverageSurface.CurrentGrid.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
                var addedOpen = openGrains.Where(x => !run.CoverageSurface.CurrentOpenGrains.Contains(x, StringComparer.Ordinal)).ToArray();
                if (addedGrid.Length > 0 || addedOpen.Length > 0)
                {
                    run.CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision
                    {
                        BeforeGrid = run.CoverageSurface.CurrentGrid.ToArray(),
                        AfterGrid = gridRefs.ToArray(),
                        AddedGridColumns = addedGrid,
                        BeforeOpenGrains = run.CoverageSurface.CurrentOpenGrains.ToArray(),
                        AfterOpenGrains = openGrains.ToArray(),
                        AddedOpenGrains = addedOpen,
                        StepId = step?.Id ?? run.CurrentStep?.Id,
                        TimestampUtc = DateTime.UtcNow.ToString("o"),
                    });
                    run.CoverageSurface.CurrentGrid = gridRefs.ToArray();
                    run.CoverageSurface.CurrentOpenGrains = openGrains.ToArray();
                }
            }

            var pinned = validGrains.Where(g => anchored[g]).ToArray();
            return new VerifyResult
            {
                Kind = kind,
                Status = "passed",
                Detail = $"anchor coverage partitions all {validGrains.Count} grid grain(s). Anchored: [{string.Join(", ", pinned)}]. Open: [{string.Join(", ", openGrains)}]. Grid: [{string.Join(", ", gridRefs)}].",
            };
        }

        private sealed class PendingAnchorRevision
        {
            public AnchorGate.Anchor Accepted { get; set; }
            public AnchorGate.Anchor Proposed { get; set; }
        }

        private static (AnswerValue Answer, string StepId) FirstAnsweredAnchor(WorkflowRunState run, string input)
        {
            for (var i = 0; i < run.Results.Count; i++)
                if (run.Results[i].Answers.TryGetValue(input, out var answer) && answer.Answered)
                    return (answer, run.Results[i].StepId);
            return (null, null);
        }

        private static VerifyResult PrepareAnchorRevision(string kind, WorkflowRunState run, string anchorsInput,
            AnchorGate.Anchor[] proposed, out AnchorRunLock runLock, out List<PendingAnchorRevision> changes)
        {
            changes = new List<PendingAnchorRevision>();
            if (!run.RunAnchorLocks.TryGetValue(anchorsInput, out runLock))
            {
                // Capture the FIRST answered declaration, not the accumulated latest-wins value. In the stock flow
                // this is Step 2, so a Step 3 revision can never establish itself as the initial lock.
                var first = FirstAnsweredAnchor(run, anchorsInput);
                if (first.Answer == null)
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "the initial locked anchor set", Detail = $"no answered declaration of '{anchorsInput}' was available to establish the run-level anchor lock." };
                var initial = AnchorGate.Parse(first.Answer.Value, out var initialError);
                if (initialError != null)
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a well-formed initial anchor set", Detail = "the run's first anchor declaration is invalid: " + initialError };
                if (initial.Length == 0)
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "at least one initial anchor", Detail = "the run's first anchor declaration is empty; it cannot establish an enforceable lock." };
                var initialHash = AnchorGate.CanonicalHash(initial);
                runLock = new AnchorRunLock
                {
                    AnchorsInput = anchorsInput,
                    InitialHash = initialHash,
                    CurrentHash = initialHash,
                    StepId = first.StepId,
                    CurrentAnchors = initial,
                };
                run.RunAnchorLocks[anchorsInput] = runLock;
            }

            var proposedHash = AnchorGate.CanonicalHash(proposed);
            if (string.Equals(runLock.CurrentHash, proposedHash, StringComparison.Ordinal))
                return null; // unchanged or inherited, including an inherited prior receipt

            string RevisionContextKey(AnchorGate.Anchor anchor)
            {
                var context = (anchor?.Context ?? Array.Empty<AnchorGate.AnchorFilter>())
                    .Select(f => new { r = AnchorGate.CanonicalRef(f).ToUpperInvariant(), v = f.Literal })
                    .OrderBy(x => x.r, StringComparer.Ordinal)
                    .ToArray();
                return JsonSerializer.Serialize(context);
            }

            string AxisSetKey(AnchorGate.Anchor anchor) => string.Join("\n",
                (anchor?.AxisColumns ?? Array.Empty<AnchorGate.AnchorColumn>())
                    .Select(AnchorGate.CanonicalRef)
                    .Select(x => x.ToUpperInvariant())
                    .OrderBy(x => x, StringComparer.Ordinal));

            bool TryIndex(AnchorGate.Anchor[] anchors, string which, out Dictionary<string, AnchorGate.Anchor> index, out VerifyResult error)
            {
                index = new Dictionary<string, AnchorGate.Anchor>(StringComparer.Ordinal);
                error = null;
                foreach (var anchor in anchors)
                {
                    // Revision matching deliberately ignores form but retains every parsed context value. This is
                    // what permits a receipted flat-to-shaped repair without permitting coordinate laundering.
                    var key = RevisionContextKey(anchor);
                    if (!index.TryAdd(key, anchor))
                    {
                        error = new VerifyResult
                        {
                            Kind = kind, Status = "unavailable", Missing = "one anchor per context",
                            Detail = $"the {which} anchor set repeats context [{anchor.ContextLabel}]; a revision must map each accepted context to exactly one corrected expectation.",
                        };
                        return false;
                    }
                }
                return true;
            }

            if (!TryIndex(runLock.CurrentAnchors, "accepted", out var acceptedByContext, out var indexError)) return indexError;
            if (!TryIndex(proposed, "proposed", out var proposedByContext, out indexError)) return indexError;
            if (acceptedByContext.Count != proposedByContext.Count
                || acceptedByContext.Keys.Any(k => !proposedByContext.ContainsKey(k)))
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable", Missing = "the locked anchor context coordinates",
                    Detail = "an anchor revision may correct expected values or add visual form to the same coordinate; it may not add, remove, or replace locked contexts. Start a new workflow run to change context coverage.",
                };

            foreach (var pair in proposedByContext)
            {
                var accepted = acceptedByContext[pair.Key];
                var candidate = pair.Value;
                if (accepted.IsShaped
                    && (!candidate.IsShaped
                        || !string.Equals(AxisSetKey(accepted), AxisSetKey(candidate), StringComparison.Ordinal)))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = "a monotonic visual anchor form",
                        Detail = "anchor form may only move toward visual semantics; start a new run to weaken it",
                    };

                var formUpgrade = !accepted.IsShaped && candidate.IsShaped;
                var expectationChanged = !string.Equals(AnchorGate.ExpectationKey(accepted),
                    AnchorGate.ExpectationKey(candidate), StringComparison.Ordinal);
                if (!formUpgrade && !expectationChanged)
                    continue;
                if (candidate.OriginalExpect == null || candidate.CorrectedExpect == null || string.IsNullOrWhiteSpace(candidate.ExtractQuery))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a complete live receipt for changed anchor [{candidate.ContextLabel}]",
                        Detail = formUpgrade
                            ? $"anchor [{candidate.ContextLabel}] moved from flat to shaped, but its JSON does not carry all required receipt fields: originalExpect, correctedExpect, extractQuery. A form repair needs a full live receipt even when its expectation is unchanged."
                            : $"anchor [{candidate.ContextLabel}] changed from {accepted.ExpectLabel} to {candidate.ExpectLabel}, but its JSON does not carry all required receipt fields: originalExpect, correctedExpect, extractQuery. Bare corrected JSON is refused.",
                    };
                if (!string.Equals(AnchorGate.ExpectationKey(candidate.OriginalExpect), AnchorGate.ExpectationKey(accepted), StringComparison.Ordinal))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"an honest originalExpect for changed anchor [{candidate.ContextLabel}]",
                        Detail = $"anchor [{candidate.ContextLabel}] declares originalExpect {candidate.OriginalExpect.Label}, but the currently accepted value is {accepted.ExpectLabel}.",
                    };
                if (!string.Equals(AnchorGate.ExpectationKey(candidate.CorrectedExpect), AnchorGate.ExpectationKey(candidate), StringComparison.Ordinal))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a correctedExpect matching expect for changed anchor [{candidate.ContextLabel}]",
                        Detail = $"anchor [{candidate.ContextLabel}] declares correctedExpect {candidate.CorrectedExpect.Label}, but expect is {candidate.ExpectLabel}.",
                    };
                if (!DaxQueryClassifier.IsRowReturning(candidate.ExtractQuery))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a row-returning extractQuery for changed anchor [{candidate.ContextLabel}]",
                        Detail = $"anchor [{candidate.ContextLabel}] uses a scalar or constant extractQuery. The receipt must be a row-returning grouped or raw-row DAX extract, not EVALUATE ROW(...) or a table constructor.",
                    };
                changes.Add(new PendingAnchorRevision { Accepted = accepted, Proposed = candidate });
            }

            if (changes.Count == 0)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a deterministic anchor revision delta", Detail = "the anchor fingerprint changed but no changed expectation could be identified; start a new workflow run." };
            return null;
        }

        private static string AnchorReceiptResultHash(ResultSet result)
        {
            string Cell(object value)
            {
                if (value == null) return "blank:";
                var formatted = value is IFormattable f
                    ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToString();
                return value.GetType().FullName + ":" + formatted;
            }
            var canonical = JsonSerializer.Serialize(new
            {
                columns = (result.Columns ?? Array.Empty<ColumnDef>()).Select(c => new { c.Name, c.Type }).ToArray(),
                rows = (result.Rows ?? Array.Empty<object[]>()).Select(r => (r ?? Array.Empty<object>()).Select(Cell).ToArray()).ToArray(),
                result.RowCount,
                result.Truncated,
            });
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private async Task<VerifyResult> ExecuteAndAcceptAnchorRevisionAsync(string kind, WorkflowRunState run,
            WorkflowStep step, AnchorRunLock runLock, AnchorGate.Anchor[] proposed, List<PendingAnchorRevision> changes,
            LiveConnection live)
        {
            if (changes == null || changes.Count == 0) return null;
            if (live == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a live connection for anchor-revision receipts", Detail = "the anchor set changed, but its extractQuery receipts cannot be executed while offline. Connect live and re-submit." };

            var deadlineUtc = DateTime.UtcNow.AddSeconds(VerifyTotalBudgetSeconds);
            var evidence = new List<AnchorRevisionChange>();
            foreach (var change in changes)
            {
                if (!ReferenceEquals(_sessions.CurrentContext.Live, live))
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a stable connection for anchor-revision receipts", Detail = "the live connection changed before all anchor-revision extractQuery receipts completed; no revision was accepted." };
                // extractQuery is deliberately row-returning, so it must cross the exact QueryData policy boundary
                // used by run_dax. A workflow answer cannot become a side door around agent read permissions.
                var policy = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database,
                    string.IsNullOrWhiteSpace(run.SubmissionOrigin) ? run.Origin : run.SubmissionOrigin,
                    isCommit: true,
                    summary: $"run an anchor-revision extract query against {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (policy != null)
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"permission to run extractQuery for changed anchor [{change.Proposed.ContextLabel}]",
                        Detail = $"anchor [{change.Proposed.ContextLabel}] revision was refused before extractQuery ran: {policy}"
                            + (string.IsNullOrWhiteSpace(approvalId) ? "" : " Approval: " + approvalId),
                    };
                var (rs, timedOut, retired, cause) = await RunVerifyQueryAsync(change.Proposed.ExtractQuery, 2000, live, deadlineUtc);
                if (timedOut)
                {
                    var why = cause == VerifyTimeoutCause.LaneWait
                        ? "the total receipt budget expired while waiting for the live query lane"
                        : "extractQuery exceeded the receipt evaluation ceiling";
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a completed extractQuery receipt for changed anchor [{change.Proposed.ContextLabel}]",
                        Detail = $"anchor [{change.Proposed.ContextLabel}] revision was refused because {why}."
                            + (retired ? " The connection was retired; reconnect before re-submitting." : ""),
                    };
                }
                if (!ReferenceEquals(_sessions.CurrentContext.Live, live))
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a stable connection for anchor-revision receipts", Detail = "the live connection changed while an anchor-revision extractQuery ran; its result was not trusted and no revision was accepted." };
                if (!string.IsNullOrEmpty(rs.Error))
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a successful extractQuery receipt for changed anchor [{change.Proposed.ContextLabel}]",
                        Detail = $"anchor [{change.Proposed.ContextLabel}] revision was refused because extractQuery failed: {rs.Error}",
                    };
                var returnedRows = rs.Rows?.Length ?? 0;
                if (rs.RowCount <= 0 || returnedRows <= 0)
                    return new VerifyResult
                    {
                        Kind = kind, Status = "unavailable", Missing = $"a row-returning extractQuery receipt for changed anchor [{change.Proposed.ContextLabel}]",
                        Detail = $"anchor [{change.Proposed.ContextLabel}] revision was refused because extractQuery returned no rows.",
                    };
                evidence.Add(new AnchorRevisionChange
                {
                    Context = change.Proposed.ContextLabel,
                    OriginalExpect = change.Accepted.ExpectLabel,
                    CorrectedExpect = change.Proposed.ExpectLabel,
                    ExtractQuery = change.Proposed.ExtractQuery,
                    ExtractRowCount = rs.RowCount,
                    ExtractTruncated = rs.Truncated,
                    ExtractResultHash = AnchorReceiptResultHash(rs),
                });
            }

            var before = runLock.CurrentHash;
            var after = AnchorGate.CanonicalHash(proposed);
            run.AnchorRevisions.Add(new AnchorRevision
            {
                Key = runLock.AnchorsInput,
                AnchorsInput = runLock.AnchorsInput,
                BeforeHash = before,
                AfterHash = after,
                StepId = step?.Id ?? run.CurrentStep?.Id,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Changes = evidence.ToArray(),
            });
            run.AnchorFormRepairCount += changes.Count(x => !x.Accepted.IsShaped && x.Proposed.IsShaped);
            runLock.CurrentHash = after;
            runLock.CurrentAnchors = proposed;
            return null;
        }

        /// <summary>Prove the target measure reproduces a set of LOCKED ANCHORS — {context, expect} value assertions
        /// the workflow author committed to (the enforcement half of the anchor-based verification design). Anchors
        /// ride a text input (the `anchors:` binding); each is evaluated as a SARGable point query against the target
        /// measure in the measure-faithful DEFINE shape, its actual compared to the expected value at the gold-style
        /// tolerance. Every applicable-but-no-evidence path is `unavailable` (fail-closed, blocks a hard step) naming
        /// what was missing; only a real value divergence is `failed`. The anchor set locks at first evaluation with a
        /// revision receipt on change (the anti-laundering mirror of the equivalence partition lock).</summary>
        private async Task<VerifyResult> WorkflowExpectedValuesAsync(VerifySpec spec, WorkflowStep step, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "expected_values";
            // 1. The anchors input must be answered (its answer carries the locked anchor JSON).
            if (string.IsNullOrWhiteSpace(spec.Anchors) || !answers.TryGetValue(spec.Anchors, out var anchorAns) || !anchorAns.Answered)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = $"the anchors input '{spec.Anchors}'", Detail = $"the anchors input '{spec.Anchors}' must carry the locked anchor set (a fenced JSON array of {{context, expect}}): it was not answered." };

            // 2. Parse the anchors DEFENSIVELY AND STRICTLY — malformed JSON, an unknown/duplicate property, a
            // non-column-ref context key (the injection boundary), a lost value type, or a breached cap (MaxAnchors /
            // MaxContextPairs) is `unavailable`, naming the defect (never guessed).
            // If this step declares the anchors input as optional and omits it on a RETRY, inherit the run lock's
            // current accepted set. WorkflowRunner replaces a failed step's answer map on re-submit, so accumulated
            // answers alone would otherwise fall back to Step 2 and falsely look like a revision back to the baseline.
            var omittedCurrentRevisionInput = run.RunAnchorLocks.TryGetValue(spec.Anchors, out var inheritedRunLock)
                && (step?.Gate?.Inputs ?? Array.Empty<GateInput>()).Any(i => i.Name == spec.Anchors && i.Required == "optional")
                && !run.Results[run.StepIndex].Answers.ContainsKey(spec.Anchors);
            string parseError = null;
            var anchors = omittedCurrentRevisionInput
                ? inheritedRunLock.CurrentAnchors
                : AnchorGate.Parse(anchorAns.Value, out parseError);
            if (!omittedCurrentRevisionInput && parseError != null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a well-formed anchor set", Detail = parseError };
            if (anchors.Length == 0)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "at least one anchor", Detail = "the anchor set is empty. Author at least one {context, expect} anchor the measure must reproduce, then re-submit." };

            // 2b. RUN-LEVEL LOCK + REVISION PLAN. The first answered declaration is captured before latest-wins can
            // shadow it. An unchanged/inherited set needs no receipt; a changed set must preserve contexts and carry
            // mechanically consistent receipt fields for each changed expectation.
            var revisionError = PrepareAnchorRevision(kind, run, spec.Anchors, anchors, out var runAnchorLock, out var pendingRevision);
            if (revisionError != null) return revisionError;

            // 3. The target measure (the run's tracked objectRef).
            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "the target measure ref", Detail = "no answered objectRef-typed input names the measure to check against the anchors." };

            var s = _sessions.Require();
            var (current, canonRef) = await s.ReadAsync(m => ResolveWorkflowMeasure(m, run, target) is Measure mm ? (mm.Expression, ObjectRefs.For(mm)) : (null, null));
            if (current == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "the target measure", Detail = $"'{target}' does not resolve to a measure on the current model." };

            // 4. CANDIDATE DRIFT — reuse the same guard dax_equivalence uses: the target's current expression must
            // still match the last workflow-authored write (else it was changed OUTSIDE the workflow and the anchors
            // would be proven against an unattested candidate). Checkable offline, before the live check.
            if (canonRef != null
                && _sessions.CurrentContext.WorkflowAux.TryGetValue(run.RunId, out var aux)
                && aux?.AuthoredMeasureHashes != null
                && aux.AuthoredMeasureHashes.TryGetValue(canonRef, out var authored)
                && authored != NormalizeDaxHash(current))
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable", Missing = "an unchanged candidate",
                    Detail = $"candidate drift: '{canonRef}' was changed since the workflow authored it (its current expression hash no longer matches the workflow-authored one). Re-author it inside the workflow before proving the anchors, so they are checked against an attested candidate.",
                };

            // 4b. RESOLVE every context column against the model (B1): the parsed table+column must exist; unknown
            // refs refuse as `unavailable` naming the ref. The filter is then REBOUND to the model's canonical names
            // (never the authored text — the query is rebuilt from resolved parts) and date-typed columns are noted
            // (B2: a date filter takes the quoted string form, which DAX coerces — an honest caveat, not an error).
            // A model read, checkable offline — before the live check.
            string unresolvedRef = null;
            var dateRefs = new List<string>();
            await s.ReadAsync(m =>
            {
                foreach (var a in anchors)
                    foreach (var f in a.Context)
                    {
                        var tbl = m.Tables.FirstOrDefault(t => string.Equals(t.Name, f.Table, StringComparison.OrdinalIgnoreCase));
                        if (tbl == null)
                        {
                            unresolvedRef ??= $"anchor context column {AnchorGate.CanonicalRef(f)} names table '{f.Table}', which does not exist on the model";
                            return false;
                        }
                        var col = tbl.Columns.FirstOrDefault(c => string.Equals(c.Name, f.Column, StringComparison.OrdinalIgnoreCase));
                        if (col == null)
                        {
                            unresolvedRef ??= $"anchor context column {AnchorGate.CanonicalRef(f)} does not exist on table '{tbl.Name}'";
                            return false;
                        }
                        f.Table = tbl.Name; f.Column = col.Name;   // canonical model casing — the emitted ref is never the input text
                        // The coercion caveat applies ONLY to STRING literals on date columns (a numeric/boolean
                        // literal on a date column is a plain type comparison, not string-form coercion).
                        if (col.DataType == DataType.DateTime && f.Literal.StartsWith("\"", StringComparison.Ordinal))
                        {
                            var r = AnchorGate.CanonicalRef(f);
                            if (!dateRefs.Contains(r)) dateRefs.Add(r);
                        }
                    }
                return true;
            });
            if (unresolvedRef != null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a resolvable anchor context", Detail = unresolvedRef + ". Fix the anchor's column reference, then re-submit." };
            var dateNote = dateRefs.Count == 0 ? ""
                : $" Note: date-typed context column(s) [{string.Join(", ", dateRefs)}] are filtered via their quoted string form (DAX coerces the literal). Confirm the string matches the column's date representation.";

            // 5. Pin the CONNECTION IDENTITY for the whole proof: every anchor query and the query spec must use
            // this one instance — if the session's connection changes mid-proof, the verify refuses rather than
            // mixing evidence across connections (EvaluateAnchorsAsync re-checks per anchor).
            var live = _live;
            if (live == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a live connection", Detail = "offline: no live connection, so the anchors could not be evaluated (open_live/open_local and re-submit for real evidence)." };

            // A changed set is not accepted from prose. Execute every changed anchor's row-returning extractQuery on
            // this pinned connection, require real rows, and append the result hash + delta before the new values can
            // become the run's current lock. Failure leaves the prior lock untouched.
            revisionError = await ExecuteAndAcceptAnchorRevisionAsync(kind, run, step, runAnchorLock, anchors, pendingRevision, live);
            if (revisionError != null) return revisionError;

            // 5b. FIDELITY before evidence (mirrors dax_equivalence): the DaxQuerySpec/measure-faithful path may only
            // reproduce the target under a reduced-fidelity surrogate — then the values are not authoritative, so the
            // proof degrades to `unavailable` (never a false pass) exactly as the equivalence ladder does. The note is
            // deterministic per (expr, spec), so one probe of the builder settles it before spending any point query.
            var eqSpec = await BuildQuerySpecAsync(live, target);
            DaxBench.BuildMeasureContextQuery(current, Array.Empty<string>(), eqSpec, out var fidelity);
            if (fidelity != null)
                return new VerifyResult
                {
                    Kind = kind, Status = "unavailable", Missing = "a full-fidelity evaluation: " + fidelity,
                    Detail = "the anchors would evaluate under a REDUCED-fidelity surrogate (the target's identity could not be reproduced), so the values are not authoritative: " + fidelity + " Open the model live so the target's identity is known, then re-submit, or skip_workflow_step with a reason (recorded).",
                };

            // 6/7. Evaluate + verdict (extracted so the connection-identity, budget, and ceiling semantics are
            // offline-testable against a hand-built spec + a test connection).
            var equivalenceGrid = run.CoverageSurface != null
                && answers.TryGetValue("equivalenceGrid", out var gridAnswer) && gridAnswer.Answered
                ? gridAnswer.Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray()
                : Array.Empty<string>();
            return await EvaluateAnchorsAsync(kind, anchors, current, eqSpec, live, dateNote, equivalenceGrid);
        }

        /// <summary>The anchor evaluation loop + verdict: each anchor runs as a SARGable point query against the
        /// PINNED connection, compared at the gold-style tolerance. ONE hard total budget bounds the whole set — the
        /// per-query token is min(remaining budget, per-query ceiling), enforced inside RunVerifyQueryAsync. The
        /// connection identity is re-checked per anchor: if the session's connection changed mid-proof (a swap, a
        /// retirement), the verify refuses as `unavailable` — continuing would mix evidence across connections.
        /// Internal for the offline seam tests (the executor supplies a resolved spec + the pinned connection).</summary>
        internal async Task<VerifyResult> EvaluateAnchorsAsync(string kind, AnchorGate.Anchor[] anchors, string measureExpr,
            DaxQuerySpec eqSpec, LiveConnection live, string dateNote, string[] equivalenceGrid = null)
        {
            VerifyResult MixedEvidence(string when) => new VerifyResult
            {
                Kind = kind, Status = "unavailable", Missing = "a stable connection",
                Detail = $"the live connection changed during evaluation ({when}): evidence would be mixed across connections; reconnect (open_live, connect_xmla, or connect_local) and re-submit so every anchor is proven on one connection.",
            };

            var deadlineUtc = DateTime.UtcNow.AddSeconds(VerifyTotalBudgetSeconds);
            var evaluated = new List<(AnchorGate.Anchor Anchor, object Actual, string ActualLabel, bool Matched, long ElapsedMs)>();

            int ShapedValueIndex(ResultSet result, int fallback)
            {
                var named = Array.FindIndex(result.Columns ?? Array.Empty<ColumnDef>(), c =>
                    string.Equals(c?.Name?.Trim(), "v", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c?.Name?.Trim(), "[v]", StringComparison.OrdinalIgnoreCase));
                return named >= 0 ? named : fallback;
            }

            string ContextEntryLabel(AnchorGate.AnchorFilter f) => AnchorGate.CanonicalRef(f) + "=" + f.ValueLabel;

            async Task<string> NearbyMembersAsync(AnchorGate.Anchor anchor)
            {
                if (!ReferenceEquals(_sessions.CurrentContext.Live, live) || DateTime.UtcNow > deadlineUtc) return "";
                var nearbyQuery = DaxBench.BuildShapedAnchorNearbyMembersQuery(anchor);
                var (nearby, timedOut, _, _) = await RunVerifyQueryAsync(nearbyQuery, 5, live, deadlineUtc);
                if (timedOut || !ReferenceEquals(_sessions.CurrentContext.Live, live) || !string.IsNullOrEmpty(nearby.Error)) return "";
                var values = (nearby.Rows ?? Array.Empty<object[]>())
                    .Where(row => row != null && row.Length > 0)
                    .Take(5)
                    .Select(row => DaxBench.Fmt(row[0]))
                    .ToArray();
                return values.Length == 0 ? ""
                    : $" Nearby members for first axis column {AnchorGate.CanonicalRef(anchor.AxisColumns[0])}: {string.Join(", ", values)}.";
            }

            for (var i = 0; i < anchors.Length; i++)
            {
                var a = anchors[i];
                // Identity pin, PRE-query: every anchor must be proven on the connection captured at verify start.
                if (!ReferenceEquals(_sessions.CurrentContext.Live, live))
                    return MixedEvidence($"after {evaluated.Count} of {anchors.Length} anchor(s)");
                if (DateTime.UtcNow > deadlineUtc)
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = $"completion within the total verify budget ({VerifyTotalBudgetSeconds}s)", Detail = $"the anchor set exceeded the total verify budget of {VerifyTotalBudgetSeconds}s after {evaluated.Count} of {anchors.Length} anchor(s). Trim the set or investigate the measure's cost, then re-submit." };

                var query = a.IsShaped
                    ? DaxBench.BuildShapedAnchorQuery(measureExpr, a, eqSpec, out _)
                    : DaxBench.BuildMeasureContextQuery(measureExpr, a.Context.Select(AnchorGate.CompileContextFilter).ToArray(), eqSpec, out _);
                var (rs, timedOut, retired, cause) = await RunVerifyQueryAsync(query, 10, live, deadlineUtc);
                if (timedOut)
                    return cause == VerifyTimeoutCause.LaneWait
                        // A LANE-WAIT budget exhaustion is not the anchor's fault — nothing of ours ran, so the
                        // advice is about the busy connection, never about narrowing the anchor.
                        ? new VerifyResult
                        {
                            Kind = kind, Status = "unavailable", Missing = $"completion within the total verify budget ({VerifyTotalBudgetSeconds}s)",
                            Detail = $"anchor at [{a.ContextLabel}]: the total verify budget was exhausted waiting for the query lane (another query held the connection the whole time). Re-submit when the connection is idle.",
                        }
                        : new VerifyResult
                        {
                            Kind = kind, Status = "unavailable", Missing = "a completed anchor query within the evaluation ceiling",
                            Detail = $"anchor at [{a.ContextLabel}]: query exceeded the evaluation ceiling. Narrow the anchor context or investigate the measure, then re-submit."
                                + (retired ? " The connection was retired because the query ignored cancellation. Reconnect (open_live, connect_xmla, or connect_local) before re-submitting." : ""),
                        };
                // Identity pin, POST-query: the result may only be consumed if the pinned connection is STILL the
                // session's connection — a swap during the query (even the final anchor's) makes it untrusted.
                if (!ReferenceEquals(_sessions.CurrentContext.Live, live))
                    return MixedEvidence($"while anchor {i + 1} of {anchors.Length} was evaluating; its result is untrusted");
                if (!string.IsNullOrEmpty(rs.Error))
                    return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a completed anchor query", Detail = $"anchor at [{a.ContextLabel}] query failed: {rs.Error}" };

                object actual;
                if (a.IsShaped)
                {
                    if (rs.RowCount == 0)
                    {
                        var coordinate = string.Join(", ", a.RowCoordinate.Select(ContextEntryLabel));
                        var slicers = a.Slicers.Length == 0 ? "(none)" : string.Join(", ", a.Slicers.Select(ContextEntryLabel));
                        var nearby = await NearbyMembersAsync(a);
                        return new VerifyResult
                        {
                            Kind = kind,
                            Status = "unavailable",
                            Missing = "the shaped anchor row in the visible set",
                            Detail = $"anchor at [{a.ContextLabel}]: row coordinate [{coordinate}] was absent from the visible set under these slicers [{slicers}]. Fix the coordinate or slicers and re-submit.{nearby}",
                        };
                    }
                    if (rs.RowCount > 1)
                        return new VerifyResult
                        {
                            Kind = kind,
                            Status = "unavailable",
                            Missing = "one row for the shaped anchor coordinate",
                            Detail = $"internal error: shaped anchor at [{a.ContextLabel}] returned {rs.RowCount} rows even though every axis column has a row-coordinate value. Report this result; the coordinate filter must return at most one row.",
                        };

                    var valueIndex = ShapedValueIndex(rs, a.AxisColumns.Length);
                    if (rs.Rows == null || rs.Rows.Length == 0 || rs.Rows[0] == null || valueIndex < 0 || valueIndex >= rs.Rows[0].Length)
                        return new VerifyResult
                        {
                            Kind = kind,
                            Status = "unavailable",
                            Missing = "the shaped anchor value column",
                            Detail = $"internal error: shaped anchor at [{a.ContextLabel}] returned one row without its 'v' value column. Report this result; the shaped query must return axis columns followed by 'v' and '__present'.",
                        };
                    actual = rs.Rows[0][valueIndex];
                }
                else
                {
                    // The v6 flat-anchor path intentionally retains its original zero-row-as-BLANK behavior.
                    actual = rs.RowCount > 0 && rs.Rows[0].Length > 0 ? rs.Rows[0][0] : null;
                }
                var matched = AnchorGate.Matches(a, actual, out var actualLabel);
                evaluated.Add((a, actual, actualLabel, matched, rs.ElapsedMs));
            }

            // Identity pin, PRE-verdict: a swap that landed after the last anchor's post-query check must still
            // refuse — a verdict may only certify evidence produced on one connection.
            if (!ReferenceEquals(_sessions.CurrentContext.Live, live))
                return MixedEvidence("after the final anchor, before the verdict");

            // Verdict: every anchor matched ⇒ passed (the payload lists each context/expected/actual/elapsed); any
            // mismatch ⇒ failed, naming the diverging anchors. The date-coercion caveat rides both.
            var payload = string.Join("; ", evaluated.Select(r => $"[{r.Anchor.ContextLabel}] expected {r.Anchor.ExpectLabel}, actual {r.ActualLabel} ({r.ElapsedMs} ms)"));
            var bad = evaluated.Where(r => !r.Matched).ToArray();
            if (bad.Length == 0)
                return new VerifyResult { Kind = kind, Status = "passed", Detail = $"all {evaluated.Count} anchor(s) matched: {payload}{dateNote}" };

            var hints = new Dictionary<AnchorGate.Anchor, string>();
            var grid = DaxBench.NormalizeGroupBy(equivalenceGrid);
            var hintEvaluations = 0;
            foreach (var mismatch in bad.Where(r => !r.Anchor.IsShaped))
            {
                if (hintEvaluations >= 2 || grid.Length == 0 || DateTime.UtcNow > deadlineUtc) break;
                AnchorGate.AnchorFilter matchingAxis = null;
                foreach (var gridRef in grid)
                {
                    if (!AnchorGate.TryParseColumnRef(gridRef, out var gridTable, out var gridColumn)) continue;
                    matchingAxis = mismatch.Anchor.Context.FirstOrDefault(f =>
                        string.Equals(f.Table, gridTable, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(f.Column, gridColumn, StringComparison.OrdinalIgnoreCase));
                    if (matchingAxis != null) break;
                }
                if (matchingAxis == null || !ReferenceEquals(_sessions.CurrentContext.Live, live)) continue;

                var shaped = new AnchorGate.Anchor
                {
                    Context = mismatch.Anchor.Context,
                    AxisColumns = new[] { new AnchorGate.AnchorColumn { Table = matchingAxis.Table, Column = matchingAxis.Column } },
                    Number = mismatch.Anchor.Number,
                    Blank = mismatch.Anchor.Blank,
                    Text = mismatch.Anchor.Text,
                };
                hintEvaluations++;
                var hintQuery = DaxBench.BuildShapedAnchorQuery(measureExpr, shaped, eqSpec, out _);
                var (hintResult, hintTimedOut, _, _) = await RunVerifyQueryAsync(hintQuery, 10, live, deadlineUtc);
                if (hintTimedOut || !ReferenceEquals(_sessions.CurrentContext.Live, live)
                    || !string.IsNullOrEmpty(hintResult.Error) || hintResult.RowCount != 1) continue;
                var hintValueIndex = ShapedValueIndex(hintResult, shaped.AxisColumns.Length);
                if (hintResult.Rows == null || hintResult.Rows.Length == 0 || hintResult.Rows[0] == null
                    || hintValueIndex < 0 || hintValueIndex >= hintResult.Rows[0].Length) continue;
                var visualActual = hintResult.Rows[0][hintValueIndex];
                if (DaxBench.ValuesEqual(mismatch.Actual, visualActual)) continue;

                var skeletonJsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                string JsonContextValue(AnchorGate.AnchorFilter f)
                {
                    if (string.Equals(f.Literal, "TRUE()", StringComparison.Ordinal)) return "true";
                    if (string.Equals(f.Literal, "FALSE()", StringComparison.Ordinal)) return "false";
                    if (f.Literal?.Length >= 2 && f.Literal[0] == '"' && f.Literal[f.Literal.Length - 1] == '"')
                        return JsonSerializer.Serialize(f.Literal.Substring(1, f.Literal.Length - 2).Replace("\"\"", "\""), skeletonJsonOptions);
                    return f.Literal;
                }
                var contextJson = string.Join(",", shaped.Context.Select(f =>
                    JsonSerializer.Serialize(AnchorGate.CanonicalRef(f), skeletonJsonOptions) + ":" + JsonContextValue(f)));
                var expectJson = shaped.Blank ? JsonSerializer.Serialize("BLANK", skeletonJsonOptions)
                    : shaped.Number.HasValue ? shaped.Number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : JsonSerializer.Serialize(shaped.Text ?? "", skeletonJsonOptions);
                var skeleton = "{\"context\":{" + contextJson + "},\"axis\":"
                    + JsonSerializer.Serialize(new[] { AnchorGate.CanonicalRef(matchingAxis) }, skeletonJsonOptions)
                    + ",\"expect\":" + expectJson + "}";
                hints[mismatch.Anchor] = " Flat and visual semantics disagree at this coordinate; the anchor form is load-bearing. Copy-paste shaped-anchor skeleton: `" + skeleton + "`.";
            }

            return new VerifyResult
            {
                Kind = kind, Status = "failed",
                Detail = $"{bad.Length} of {evaluated.Count} anchor(s) diverged: "
                    + string.Join("; ", bad.Select(r => $"[{r.Anchor.ContextLabel}] expected {r.Anchor.ExpectLabel} but got {r.ActualLabel}"
                        + (hints.TryGetValue(r.Anchor, out var hint) ? hint : "")))
                    + $". Adjudicate each against a raw-row extract; fix the measure (or correct a wrong anchor) and re-submit. Full set: {payload}{dateNote}",
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
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the probe input '{spec.Probe}' has no answered BASELINE (the pre-rewrite warm median in ms). Run benchmark_dax_coldwarm BEFORE the rewrite and answer the input with the warm median." };
            if (!double.TryParse(baseAns.Value, System.Globalization.NumberStyles.Any, inv, out var baselineMs))
                return new VerifyResult { Kind = kind, Status = "failed", Detail = $"the baseline '{baseAns.Value}' is not a number of milliseconds. Record it with benchmark_dax_coldwarm (use the warm median) and answer the input with just the number." };

            var target = LatestObjectRefAnswer(run, answers);
            if (target == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no answered objectRef-typed input names the measure to benchmark: the workflow must collect one (e.g. an input `target` of type objectRef) before this gate." };

            if (_live == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "a live connection", Detail = "offline: no live connection, so the benchmark delta could not be proven. Connect to a live model and submit this step again." };

            var s = _sessions.Require();
            var name = await s.ReadAsync(m => ResolveWorkflowMeasure(m, run, target)?.Name);
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
                       + (passed ? "" : ": the rewrite regressed past tolerance; revert with update_measure or re-optimize.")
                       + " Single-machine wall-clock; treat near-tolerance results as inconclusive.";
            return new VerifyResult { Kind = kind, Status = passed ? "passed" : "failed", Detail = detail };
        }

        /// <summary>Diff ACTIVE violations against the start-of-run snapshot: the step must introduce
        /// no NEW violations (scope object = on the target only; model = anywhere).</summary>
        private async Task<VerifyResult> WorkflowBpaCleanAsync(VerifySpec spec, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            const string kind = "bpa_clean";
            if (_sessions.Current == null)
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "an open model", Detail = "no open model, so the BPA scan could not be proven. Open the model and submit this step again." };
            _sessions.CurrentContext.WorkflowAux.TryGetValue(run.RunId, out var aux);
            if (aux?.BpaKeys == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no start-of-run BPA snapshot exists (no session was open at start_workflow): a before/after diff is impossible; abort and restart the workflow with the model open." };
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
                return new VerifyResult { Kind = kind, Status = "unavailable", Missing = "an open model", Detail = "no open model, so readiness could not be re-scored. Open the model and submit this step again." };
            _sessions.CurrentContext.WorkflowAux.TryGetValue(run.RunId, out var aux);
            if (aux?.ReadinessKeys == null)
                return new VerifyResult { Kind = kind, Status = "failed", Detail = "no start-of-run readiness snapshot exists (no session was open at start_workflow): a before/after diff is impossible; abort and restart the workflow with the model open." };
            var sc = await AiReadinessScanAsync();
            return EvaluateReadinessRescan(aux.ReadinessKeys, aux.ReadinessOverall, sc, run.Def?.Name);
        }

        // Main's gate: fail on new findings, a falling score, or make-ai-ready that did not improve.
        // Extracted so tests can pin the same rule without opening a session.
        internal static VerifyResult EvaluateReadinessRescan(HashSet<string> beforeKeys, double beforeOverall, Semanticus.Analysis.Scorecard after, string workflowName = null)
        {
            const string kind = "readiness_rescan";
            var remaining = (after.Findings ?? Array.Empty<Semanticus.Analysis.ReadinessFinding>())
                .Where(f => !f.Waived)
                .Select(f => f.RuleId + "|" + f.ObjectRef)
                .ToHashSet(StringComparer.Ordinal);
            var fresh = remaining.Where(key => !beforeKeys.Contains(key)).Select(key =>
            {
                var parts = key.Split('|');
                return after.Findings.FirstOrDefault(f => f.RuleId == parts[0] && f.ObjectRef == (parts.Length > 1 ? parts[1] : ""));
            }).Where(f => f != null).ToArray();
            var delta = after.Overall - beforeOverall;
            var improved = after.Overall > beforeOverall + 0.049 || remaining.Count < beforeKeys.Count;
            var fell = after.Overall + 0.049 < beforeOverall;
            var mustImprove = string.Equals(workflowName, "make-ai-ready", StringComparison.OrdinalIgnoreCase);
            if (fresh.Length > 0)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "failed",
                    Detail = $"{fresh.Length} new readiness finding(s): " + string.Join("; ", fresh.Take(10).Select(f => $"{f.RuleId} on {f.ObjectName}")) + (fresh.Length > 10 ? " …" : ""),
                };
            if (fell)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "failed",
                    Detail = $"readiness score fell to {after.Overall:0.#} ({delta:0.#} vs start of run). Fix the regression and submit this step again.",
                };
            if (mustImprove && remaining.Count > 0 && !improved)
                return new VerifyResult
                {
                    Kind = kind,
                    Status = "failed",
                    Detail = $"the model is no more ready than when this run started; score {after.Overall:0.#} ({(delta >= 0 ? "+" : "")}{delta:0.#}). Do the prep work, then submit this step again.",
                };
            return new VerifyResult
            {
                Kind = kind,
                Status = "passed",
                Detail = $"no new readiness findings; score {after.Overall:0.#} ({(delta >= 0 ? "+" : "")}{delta:0.#} vs start of run).",
            };
        }
    }
}
