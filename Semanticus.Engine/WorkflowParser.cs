using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Semanticus.Engine
{
    // ============================================================================================
    // md+YAML workflow parser (docs/pro-mode-spec.md §2, docs/workflow-canvas-spec.md §1).
    //
    // TWO RULESETS, chosen by the file's own `schemaVersion` (§1.2), and since [T217] they have two
    // different READERS, not just two flag settings:
    //   v1 (absent, or `schemaVersion: 1`) — unchanged FOREVER, and read by the ORIGINAL HAND-ROLLED
    //     line reader, never by a YAML library. Unknown KEYS are preserved-and-ignored (forward
    //     compat); unknown enum VALUES are errors; every quirk (naive comma splits, quotes stripped
    //     after splitting, comments as ordinary keys) is frozen and pinned byte-identical against the
    //     pre-v2 parser (WorkflowV1CompatibilityTests, capture.ps1). Every file shipped before v2
    //     parses under this ruleset and must keep producing identical results.
    //   v2 (`schemaVersion: 2`) — STRICT, and its YAML interiors (the frontmatter and the fence
    //     interiors) are read by YamlDotNet. The library owns what standard YAML owns: quoting,
    //     escapes, comments, flow and block collections, indentation, and refusing malformed input by
    //     line. OUR format rules are then enforced on the document model it returns: closed key sets,
    //     named reservations, version-first, value shapes, the empty-item refusal, loop scoping, the
    //     depth limit. Eight review rounds of hand-rolled YAML refusals were the price of not doing
    //     this; the exit-test pack (WorkflowV2YamlReaderExitTests) is the deal that decided it.
    //     An unrecognised key in any CLOSED scope is an error naming the key, the scope and the line.
    //     That strictness is the whole content of [T169]: silent absorption means an old engine handed
    //     a new file runs it anyway, missing the parts it cannot read, and looks like it worked.
    // A version above what this engine reads is refused outright, never partially run.
    //
    // No other markdown semantics: the parser stores the step body exactly as authored. At run-view time,
    // WorkflowRunner replaces only the ratified [[loop.*]] tokens for an already-expanded iteration.
    // ============================================================================================

    public static class WorkflowParser
    {
        /// <summary>The highest <c>schemaVersion</c> this engine reads. Raising it is a format change:
        /// see docs/workflow-canvas-spec.md §1.2 before touching it.</summary>
        public const int MaxSchemaVersion = 2;

        private static readonly string[] Strictnesses = { "hard", "warn", "off" };
        private static readonly string[] InputTypes = { "verification", "text", "enum", "number", "objectRef", "planItem" };
        private static readonly string[] Requireds = { "answer-or-decline", "required", "optional" };
        private static readonly string[] VerifyKinds = { "dax_probe", "dax_equivalence", "expected_values", "anchor_coverage", "readiness_rescan", "bpa_clean", "benchmark_delta", "workflow_admissible", "interview_replay", "baseline_captured", "impact_assessment", "baseline_exists", "baseline_unchanged", "tests_replay", "plan_item_staged", "plan_item_applied" };
        private static readonly string[] Kinds = { "workflow", "template" };        // §10.3: absent ⇒ workflow
        private static readonly string[] SlotRequireds = { "required", "optional" }; // slots only ever require|optional (never answer-or-decline — that is a gate-input concept)
        private static readonly string[] InputScopes = { "iteration", "run" };       // §2.2a

        // ---- the closed v2 scopes (§1.3). Each set is enumerated here so a builder never has to
        // infer one, and so a refusal can teach the allowed set rather than just say no.
        //
        // F-075: this said there were SEVEN closed scopes and there are EIGHT arrays below it, the same
        // miscount the spec carried until it was corrected for omitting the `slots:` map. The number is not
        // restated here on purpose: a count in a comment beside the list it counts is a fact with two
        // sources, and this one has now been wrong twice. Count the arrays.
        //
        // These are the CLOSED scopes. The format's two OPEN maps are `with:` (open in spelling only; its
        // keys are the callee's declared inputs) and `provenance:` (a true bag, arbitrary keys, no
        // validation). See the spec, section 4.
        private static readonly string[] FrontmatterKeys = { "schemaVersion", "name", "kind", "title", "description", "whenToUse", "version", "strictness", "triggers", "tags", "slots", "provenance" };
        private static readonly string[] GateKeys = { "strictness", "ops", "inputs", "verify" };
        private static readonly string[] GateInputKeys = { "name", "question", "type", "required", "daxPurity", "scope" };
        private static readonly string[] GateVerifyKeys = { "kind", "when", "probe", "scope", "intent", "pinnedShapes", "openShapes", "openShapesFrom", "openMismatch", "anchors" };
        private static readonly string[] StepKeys = { "id", "when", "forEach", "call" };
        private static readonly string[] ForEachKeys = { "in", "as", "maxIterations" };
        private static readonly string[] CallKeys = { "workflow", "with", "returns" };
        // Which keys inside a LIST ITEM carry a LIST value, per scope. Everything else in an item is a
        // single scalar, and a sequence under a scalar key is refused rather than flattened (SeqOfMaps).
        // Enumerated here beside the closed key sets, because "which keys take a list" is the same kind of
        // fact as "which keys exist".
        private static readonly string[] InputListKeys = Array.Empty<string>();
        private static readonly string[] VerifyListKeys = { "pinnedShapes", "openShapes" };
        private static readonly string[] SlotListKeys = { "values" };
        // §10.3 template slots. The spec's §1.3 table names seven scopes and omits this one, which is how it
        // stayed open while the same section claimed `with:` was the only open map. Enumerated here and
        // added to the spec table, so the claim is true rather than nearly true.
        private static readonly string[] SlotKeys = { "name", "question", "type", "required", "default", "example", "hint", "values" };
        // `with:` is the ONE open map in the format: its keys are the callee's declared input names, so it is
        // validated against the callee (check_workflow) rather than against a fixed list. Stated because an
        // unstated exception is how a strictness rule quietly becomes a suggestion.

        /// <summary>§1.8 — the NINE destination spellings a later version will define, refused now with a
        /// named reason each. This is the single shared constant the acceptance test enumerates, so a
        /// reservation added without a test is impossible. The SCOPE is load-bearing: a key is reserved
        /// where it will LIVE, because moving a key later is exactly the format break schemaVersion exists
        /// to prevent. Reserved is not implemented, and nothing here defines semantics.</summary>
        public static readonly IReadOnlyList<ReservedKeySpec> ReservedDestinationKeys = new[]
        {
            new ReservedKeySpec { Key = "instructions", Scope = "step", Reason = "'instructions:' is reserved for a later version of this format. Custom instructions on a step are not built yet. Write the guidance in the step body instead." },
            new ReservedKeySpec { Key = "maxRunTime", Scope = "step", Reason = "'maxRunTime:' is reserved for a later version of this format. Nothing times a step out today." },
            new ReservedKeySpec { Key = "approvals", Scope = "step", Reason = "'approvals:' is reserved for a later version of this format. Requiring someone to approve a step before it runs is not built yet." },
            new ReservedKeySpec { Key = "agent", Scope = "step", Reason = "'agent:' is reserved for a later version of this format. Choosing which assistant runs a step is not built yet." },
            new ReservedKeySpec { Key = "script", Scope = "step", Reason = "'script:' is reserved for a later version of this format. Running a script from a step is not built yet." },
            new ReservedKeySpec { Key = "lookup", Scope = "step", Reason = "'lookup:' is reserved for a later version of this format. Reading a value on a step to fill a variable is not built yet." },
            new ReservedKeySpec { Key = "successCriteria", Scope = "gate", Reason = "'successCriteria:' is reserved for a later version of this format. Your own success criteria are not built yet. Use 'verify:' for the checks this version can run." },
            new ReservedKeySpec { Key = "variables", Scope = "frontmatter", Reason = "'variables:' is reserved for a later version of this format. Run-level variables are not built yet. Use gate inputs to carry a value between steps." },
            new ReservedKeySpec { Key = "agents", Scope = "frontmatter", Reason = "'agents:' is reserved for a later version of this format. Naming a roster of assistants is not built yet." },
        };

        /// <summary>§1.3 — `after:` is reserved separately from the nine, because it is an ORDERING key held
        /// back by stage 1's decision that there is no parallel execution and the format may not express it
        /// (§2.4), not a destination key held back by design work. Naming it also stops a third party
        /// defining `after:` to mean something else before stage 2 does.</summary>
        public static readonly ReservedKeySpec ReservedAfterKey = new ReservedKeySpec
        {
            Key = "after", Scope = "step",
            Reason = "'after:' is reserved for a later version of this format. This version runs steps in file order. Remove it, or use 'when:' to control whether a step runs.",
        };

        // The consumed-or-refused LINE LEDGER that used to live here is GONE, and deliberately so
        // ([T217] exit item 5). It existed because a hand reader could read a line and then drop it; a
        // real YAML reader cannot: every content line inside the frontmatter and every recognised fence
        // is part of exactly one node of the document YamlDotNet returns, or is a YAML error naming its
        // line. What the ledger guarded is now guaranteed structurally — an unknown key is refused by
        // the closed key sets, a wrong-shaped value by the shape rules, and a malformed line by the
        // library. A guard that can no longer fire is a comment wearing a uniform, so it was deleted
        // rather than kept. What the ledger could never see (a fence outside every step, an indented
        // fence) keeps its own refusals below, exactly as before.

        /// <summary>What the v1 reader skips: whitespace and NOTHING else, exactly as the pinned pre-v2
        /// parser did. v1 HAS NO COMMENT SYNTAX: `# owner: finance` is an ordinary key there — it lands in
        /// `Provenance` in the frontmatter, and at column zero inside a gate it ENDS the section above it.
        /// Both are observable in parse output and pinned by fixtures (F-065, F-066). v2 comments belong to
        /// YamlDotNet and never reach this reader.</summary>
        private static bool IsSkippable(string raw) => string.IsNullOrWhiteSpace(raw);

        private static ReservedKeySpec Reservation(string scope, string key) =>
            ReservedDestinationKeys.Concat(new[] { ReservedAfterKey })
                .FirstOrDefault(r => r.Scope == scope && string.Equals(r.Key, key, StringComparison.Ordinal));

        private static readonly Regex StepHeading = new Regex(@"^##\s*Step\s+(\d+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex GateFenceOpen = new Regex(@"^```\s*yaml\s+gate\s*$", RegexOptions.Compiled);
        private static readonly Regex StepFenceOpen = new Regex(@"^```\s*yaml\s+step\s*$", RegexOptions.Compiled);
        // The same two fences with leading whitespace, which the anchored patterns above deliberately do not
        // match. Used only to REFUSE an indented fence in v2, never to accept one: a fence that starts in the
        // wrong column is an authoring mistake, and reading it as prose is how a gate vanishes.
        private static readonly Regex IndentedFence = new Regex(@"^\s+```\s*yaml\s+(gate|step)\s*$", RegexOptions.Compiled);
        // The step HEADING with leading whitespace, for exactly the same reason and used exactly the same
        // way: only to REFUSE. ' ## Step 1: Review' is not a heading to the anchored pattern above, so the
        // step, its body, its fences and its gate were all read as ordinary prose and disappeared — and the
        // step-numbering check could not notice, because as far as the parser was concerned that step was
        // never written at all.
        private static readonly Regex IndentedStepHeading = new Regex(@"^\s+##\s*Step\s+(\d+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex WhenExpr = new Regex(@"^inputs\.([A-Za-z0-9_-]+)\.answered$", RegexOptions.Compiled);
        // A slot NAME (and every {{ref}}) is a plain identifier so the reference is unambiguous. camelCase is the
        // house convention (§10.3); underscores are tolerated so a hand-authored snake_case slot still resolves.
        internal static readonly Regex SlotRef = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);
        private static readonly Regex SlotName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        // §1.4 — a step id is lower kebab: groups of letters and digits, single hyphens between them. An
        // earlier revision of the spec wrote ^[a-z][a-z0-9-]*$, which also accepted 'prove-' and
        // 'prove--rewrite'; it was tightened before any file could rely on it, so there is nothing to
        // migrate. The tighter pattern is what a canvas can round-trip as a label without surprises.
        private static readonly Regex StepId = new Regex(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);
        private static readonly Regex PositionalId = new Regex(@"^step-(\d+)$", RegexOptions.Compiled);
        private static readonly Regex LoopVarName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex ForEachInput = new Regex(@"^inputs\.([A-Za-z0-9_-]+)$", RegexOptions.Compiled);
        // §1.6 — {{...}} is the template SLOT delimiter, substituted ONCE at instantiation. A loop value is
        // substituted fresh every pass, so it uses [[...]]. Two lifetimes need two delimiters, and a
        // run-time substitution wearing the slot delimiters would be indistinguishable in the file.
        private static readonly Regex SlotShapedLoopRef = new Regex(@"\{\{\s*loop\.([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

        /// <summary>Load every *.md in a directory. Per-file error capture: a malformed file comes
        /// back with <c>Error</c> set (surfaced in list_workflows), never dropped from the list.</summary>
        public static List<WorkflowDef> LoadDirectory(string dir, string source)
        {
            var defs = new List<WorkflowDef>();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return defs;
            foreach (var path in Directory.EnumerateFiles(dir, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                defs.Add(ParseFile(path, source));
            return defs;
        }

        public static WorkflowDef ParseFile(string path, string source)
        {
            try
            {
                var def = Parse(File.ReadAllText(path));
                def.Source = source; def.FilePath = path;
                var stem = Path.GetFileNameWithoutExtension(path);
                if (def.Error == null && !string.Equals(def.Name, stem, StringComparison.Ordinal))
                    def.Error = $"frontmatter name '{def.Name}' must match the filename '{stem}'.";
                if (string.IsNullOrWhiteSpace(def.Name)) def.Name = stem;   // a broken file must still say WHICH file
                return def;
            }
            catch (Exception ex)
            {
                return new WorkflowDef { Name = Path.GetFileNameWithoutExtension(path), Source = source, FilePath = path, Error = ex.Message };
            }
        }

        public static WorkflowDef Parse(string text)
        {
            var def = new WorkflowDef();
            try
            {
                var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
                // §1.2 — the version is read FIRST, by a one-line lookahead, because the parser must know
                // which ruleset applies before it interprets a single other key. A two-pass alternative
                // would have to be lenient in pass one about exactly the keys pass two wants to refuse.
                // The lookahead stays HAND-ROLLED under both rulesets (ruled 2026-07-31, §1.2.1): it is
                // version-independent by construction, and a version-aware read of the frontmatter has no
                // fixed point on some inputs.
                def.SchemaVersion = ReadSchemaVersion(lines);
                int i = def.SchemaVersion >= 2 ? ParseFrontmatterV2(lines, def) : ParseFrontmatter(lines, def);
                ParseSteps(lines, i, def);
                if (string.IsNullOrWhiteSpace(def.Name)) throw new FormatException("frontmatter is missing 'name'.");
                if (def.Steps.Length == 0) throw new FormatException("no '## Step N:' headings found.");
                ValidateInputBindings(def);
                if (def.SchemaVersion >= 2) ValidateV2(def);
            }
            catch (Exception ex) { def.Error = ex.Message; }
            return def;
        }

        // ---- version lookahead (§1.2) ----------------------------------------------------------

        /// <summary>The index of the frontmatter's closing '---', or <c>lines.Length</c> when it never
        /// closes (which <see cref="ParseFrontmatter"/> reports).</summary>
        private static int FrontmatterEnd(string[] lines)
        {
            int i = 1;
            while (i < lines.Length && lines[i].TrimEnd() != "---") i++;
            return i;
        }

        /// <summary>The v1 frontmatter walk, frozen. `slots:` (no inline value) introduces the ONE nested
        /// frontmatter structure a v1 file has — a §10.3 template's fill-in list. Everything else is a flat
        /// scalar, and `provenance:` is an ordinary key whose indented lines are themselves top-level keys,
        /// exactly as the pinned pre-v2 parser read them. v2 never comes through here.</summary>
        private static IEnumerable<(int Index, string Key, string Value, List<(int No, string Text)> Block)>
            FrontmatterEntries(string[] lines, int end)
        {
            for (int i = 1; i < end; i++)
            {
                if (IsSkippable(lines[i])) continue;   // v1 has no comments at all
                var (key, value) = SplitKeyValue(lines[i]);
                if (key == null) continue;

                if (key == "slots" && value == null)
                {
                    var block = new List<(int, string)>();
                    int j = i + 1;
                    for (; j < end; j++)
                    {
                        if (IsSkippable(lines[j])) { block.Add((j + 1, lines[j])); continue; }   // in v1 a '#' line is a key and dedent rules apply to it
                        if (lines[j].Length - lines[j].TrimStart().Length == 0) break;   // dedent → the block ended
                        block.Add((j + 1, lines[j]));
                    }
                    yield return (i, key, value, block);
                    i = j - 1;   // the for's i++ lands on the dedented key (re-scanned) or the '---' (loop ends)
                    continue;
                }
                yield return (i, key, value, null);
            }
        }

        /// <summary>§1.2 — which ruleset this file is read by, decided before any other key is interpreted.
        ///
        /// VERSION-INDEPENDENT BY CONSTRUCTION, and that is the whole design (ruled 2026-07-31). What this
        /// scan looks at cannot depend on the answer it is computing: it reads the lines at column zero up to
        /// the FIRST nested line, splits them one fixed way, and takes the first `schemaVersion` it finds
        /// there. No strictness, no second pass, no block rules.
        ///
        /// **Chosen over a two-pass scan because that scan was unsound, not merely buggy.** Letting
        /// `provenance:` open a block only in v2 made "is this line a top-level key?" depend on the version
        /// being read, and there is an input with NO fixed point: assume v1 and the scan finds
        /// `schemaVersion: 2`; assume v2 and the same scan finds nothing, so the answer is v1. No amount of
        /// iterating converges, so that design would have had to invent a "version undecidable" refusal for a
        /// contradiction of its own making. Three review rounds found a defect in that one spot.
        ///
        /// The scan therefore refuses ONLY what stops it picking a ruleset: a version that is not a whole
        /// number, is below 1, or is above what this engine reads. Every other judgement about a
        /// `schemaVersion` key (is it the first key, is it a duplicate, is one sitting somewhere this scan
        /// never looked) needs to know both the version AND the block structure, so it lives in
        /// <see cref="ParseFrontmatter"/>, which knows both. Nothing is silently ignored as a result: v2
        /// refuses any further `schemaVersion` entry, and v1 keeps it and check_workflow says so out loud.</summary>

        // ClassifyVersionLine, its Broken taxonomy and the broken-quoting refusals are GONE, by
        // ruling (read eight). Three review rounds each bled from that one method, because it was
        // reimplementing YAML quoting semantics on raw lines - the exact hand-rolled class this slice
        // was ratified to delete. The structure now mirrors how slice 1's version-scan saga ended:
        //   - the SCAN below recognises exactly ONE spelling, the literal text `schemaVersion:` plain
        //     at column zero. No quote handling, no escape handling, no taxonomy.
        //   - v2's MODEL WALK enforces the rest. YamlDotNet decodes keys, so any spelling that decodes
        //     to `schemaVersion` - quoted, escaped, or one nobody has thought of yet - meets the
        //     version rules (first-plain-key, one-declaration, closed sets) by construction.
        //   - v1 stays byte-frozen and gains only the additive warn (the section 1.2.1 pattern): a
        //     line that LOOKS like a version key in a non-plain spelling loads exactly as the pinned
        //     pre-v2 parser loaded it, and check_workflow says it is not being read as the version.
        //     The warn detector is deliberately OVER-approximate (it strips quotes, backslashes and a
        //     brace from a raw key and compares) because a warn cannot refuse anything; its false
        //     positives are hedged wording, its misses are backstopped by the inert-fence warn any v2
        //     construct in a v1-read file already draws.

        /// <summary>The ONE spelling the version scan reads: the literal text `schemaVersion:` at
        /// column zero, inside the window (before the first indented line). Everything else is not a
        /// version to the scan, whatever it decodes to - the model walk and the warn own the rest.</summary>
        private static bool IsPlainVersionLine(string line) =>
            line.StartsWith("schemaVersion:", StringComparison.Ordinal);

        private static int ReadSchemaVersion(string[] lines)
        {
            var i = VersionLineIndex(lines);
            if (i < 0) return 1;   // absent ⇒ version 1, which parses under v1 rules forever
            var (_, value) = SplitKeyValue(lines[i]);
            if (!int.TryParse(value, out var v))
                throw new FormatException($"schemaVersion '{value}' is not a whole number (line {i + 1}). Write 'schemaVersion: 2', or leave the key out for version 1.");
            if (v < 1)
                throw new FormatException($"schemaVersion '{value}' is not a version this format has (line {i + 1}).");
            if (v > MaxSchemaVersion)
                throw new FormatException($"this workflow file says schemaVersion: {v}. This Semanticus reads up to {MaxSchemaVersion}. Update Semanticus, or open the file with the version that wrote it. Nothing was run.");
            return v;
        }

        private static int VersionLineIndex(string[] lines)
        {
            if (lines.Length == 0 || lines[0].TrimEnd() != "---") return -1;
            var end = FrontmatterEnd(lines);
            for (var i = 1; i < end; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                if (lines[i].Length - lines[i].TrimStart().Length > 0) break;
                if (IsPlainVersionLine(lines[i])) return i;
            }
            return -1;
        }

        // The upgrader uses the parser's own boundaries, including headings inside ordinary Markdown fences.
        internal static (int VersionLine, int[] StepLines) UpgradeSourceAnchors(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var start = Math.Min(FrontmatterEnd(lines) + 1, lines.Length);
            return (VersionLineIndex(lines), Enumerable.Range(start, lines.Length - start)
                .Where(i => StepHeading.IsMatch(lines[i])).ToArray());
        }

        /// <summary>True when <paramref name="index"/> is the line the version scan would have read as the
        /// file's version: at column zero, and before the first nested line. <see cref="ParseFrontmatter"/>
        /// uses it to tell the ONE declaration that counts from every other `schemaVersion` in the file, so
        /// the two readers cannot drift on that question: this method IS the scan's window, stated once.</summary>
        /// <summary>The 1-based line number of the first `schemaVersion` key in the frontmatter that the
        /// version scan did NOT read, or 0 when there is none. Indentation and block membership are
        /// irrelevant here on purpose: a version line hidden under `slots:` is exactly as unread as one
        /// sitting plainly below the window, and the author needs telling in both cases. A list item
        /// (`- schemaVersion: 2`) is not one of these, because its key is `- schemaVersion` to every reader
        /// in this file.</summary>
        /// <summary>The 1-based line of the first line that LOOKS like a version key and is not the one
        /// the scan read, or 0. Feeds the additive warn only — it refuses nothing, by the read-eight
        /// ruling. Deliberately OVER-approximate: the raw key is stripped of quote characters,
        /// backslashes and a leading brace before comparing, so the quoted, escaped and flow-map
        /// spellings all draw the warn, and a scalar line that merely looks key-shaped may too; the
        /// warn's wording hedges for that, and a warn's false positive costs a sentence where a refusal's
        /// would cost a loadable file.</summary>
        private static int UnreadVersionLine(string[] lines, int end)
        {
            for (int i = 1; i < end; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var (key, _) = SplitKeyValue(lines[i].Trim());
                if (key == null || !string.Equals(key.Trim('"', '\'', '\\', '{'), "schemaVersion", StringComparison.Ordinal)) continue;
                if (!IsTheVersionLine(lines, i)) return i + 1;
            }
            return 0;
        }

        private static bool IsTheVersionLine(string[] lines, int index)
            => VersionLineIndex(lines) == index;

        // ---- frontmatter --------------------------------------------------------------------

        /// <summary>The v1 frontmatter reader, frozen forever. Unknown keys land in Provenance; duplicates
        /// win last; nothing is refused that the pinned pre-v2 parser accepted.</summary>
        private static int ParseFrontmatter(string[] lines, WorkflowDef def)
        {
            if (lines.Length == 0 || lines[0].TrimEnd() != "---")
                throw new FormatException("file must start with a '---' YAML frontmatter block.");
            var end = FrontmatterEnd(lines);
            // §1.2.1: a `schemaVersion` the scan did not read is not a version. v1 keeps it and
            // check_workflow says so out loud.
            // Section 1.2.1: a schemaVersion the scan did not read is not a version. v1 keeps every
            // byte exactly as the pinned pre-v2 parser did — no refusals on this path, by the
            // read-eight ruling — and check_workflow warns that the line is not being read as the version.
            def.HasUnreadSchemaVersion = UnreadVersionLine(lines, end) > 0;

            foreach (var (i, key, value, block) in FrontmatterEntries(lines, end))
            {
                if (block != null)
                {
                    def.Slots = ParseFlatMapList(block, m => $"slots: {m}").Select(m => ToSlot(m, strict: false)).ToArray();
                    continue;
                }

                if (string.Equals(key, "schemaVersion", StringComparison.Ordinal))
                {
                    // v1 keeps it, exactly as it did before v2 existed, when `schemaVersion` was not a key at
                    // all and landed in Provenance like any other unknown key.
                    if (value != null) def.Provenance[key] = value;
                    continue;
                }

                switch (key)
                {
                    case "name": def.Name = value; break;
                    case "kind": def.Kind = RequireEnum(value, Kinds, "kind"); break;   // §10.3 (absent ⇒ workflow — never reached when the key is missing)
                    case "title": def.Title = value; break;
                    case "description": def.Description = value; break;
                    case "version":
                        if (!int.TryParse(value, out var v)) throw new FormatException($"version '{value}' is not an integer.");
                        def.Version = v; break;
                    case "strictness": def.Strictness = RequireEnum(value, Strictnesses, "strictness"); break;
                    case "triggers": def.Triggers = ParseInlineList(value); break;
                    case "tags": def.Tags = ParseInlineList(value); break;   // §10.6: labels an activation rule can select by (`tag:`) — parsed like triggers
                    case "whenToUse": def.WhenToUse = value; break;   // §9.5 routing hint (optional; quotes stripped by SplitKeyValue)
                    default:
                        // v1: unknown keys are preserved (forward compat + provenance). A distilled workflow's
                        // `derived_from:` etc. land here for check_workflow to surface (value kept verbatim).
                        if (value != null) def.Provenance[key] = value;
                        break;
                }
            }
            if (end >= lines.Length) throw new FormatException("frontmatter '---' block is never closed.");
            return end + 1;
        }

        /// <summary>The v2 frontmatter reader: YamlDotNet reads the interior, our rules are enforced on the
        /// model it returns (§1.2, §1.3). Spec section 1.1's promise — nothing an author writes is silently
        /// absorbed — is structural here: every line is part of a node or a YAML error, every key is known,
        /// reserved or refused, and every value's shape is checked.</summary>
        private static int ParseFrontmatterV2(string[] lines, WorkflowDef def)
        {
            if (lines.Length == 0 || lines[0].TrimEnd() != "---")
                throw new FormatException("file must start with a '---' YAML frontmatter block.");
            var end = FrontmatterEnd(lines);
            if (end >= lines.Length) throw new FormatException("frontmatter '---' block is never closed.");

            var interior = new List<(int No, string Text)>();
            for (int i = 1; i < end; i++) interior.Add((i + 1, lines[i]));
            var root = ReadYamlBlock(interior, "frontmatter");
            var map = RequireMap(root, "frontmatter");

            // §1.2.1 — one version declaration per file. The root map's own duplicates are refused by the
            // model builder; this catches a `schemaVersion` HIDDEN one level down (inside `provenance:`),
            // which the version scan never read and must not silently keep. A slot property named
            // `schemaVersion` falls to the slots closed-key refusal instead, exactly as before.
            foreach (var e in map.Entries)
                if (e.Value is YMap nested)
                    foreach (var ne in nested.Entries)
                        if (string.Equals(ne.Key, "schemaVersion", StringComparison.Ordinal))
                            throw new FormatException($"this file declares schemaVersion more than once (line {ne.Line}). It can only be one version, so keep the first and delete the rest.");

            // §1.2 — version-first. The version scan already read it; here the frontmatter reader, which
            // knows the block structure, enforces the position.
            var first = map.Entries.Count > 0 ? map.Entries[0] : null;
            var versionEntry = map.Entries.FirstOrDefault(e => string.Equals(e.Key, "schemaVersion", StringComparison.Ordinal));
            if (versionEntry != null && !ReferenceEquals(first, versionEntry))
                throw new FormatException($"schemaVersion must be the first key of the frontmatter, but '{first.Key}' comes before it (line {versionEntry.Line}). The version has to be known before any other key is read.");

            foreach (var e in map.Entries)
            {
                switch (e.Key)
                {
                    case "schemaVersion":
                        // The scan SELECTED this parser from the literal text `schemaVersion: 2` and that is
                        // all the scan does (read eight's ruling: the scan stays dumb). The value the YAML
                        // reader really built is judged here, or a spelling the scan cannot see - an
                        // indented continuation line makes `2` into the scalar `2 extra` - would leave the
                        // author's own text read by nobody and refused by nothing. Position is enforced
                        // above; this is the value.
                        var sv = ScalarOf(e, "frontmatter");
                        if (!int.TryParse(sv, out _))
                            throw new FormatException($"schemaVersion '{sv}' is not a whole number (line {e.Line}). Write 'schemaVersion: 2', or leave the key out for version 1.");
                        break;
                    case "name": def.Name = ScalarOf(e, "frontmatter"); break;
                    case "kind": def.Kind = RequireEnum(ScalarOf(e, "frontmatter"), Kinds, "kind"); break;
                    case "title": def.Title = ScalarOf(e, "frontmatter"); break;
                    case "description": def.Description = ScalarOf(e, "frontmatter"); break;
                    case "version":
                        var vv = ScalarOf(e, "frontmatter");
                        if (!int.TryParse(vv, out var v)) throw new FormatException($"version '{vv}' is not an integer.");
                        def.Version = v; break;
                    case "strictness": def.Strictness = RequireEnum(ScalarOf(e, "frontmatter"), Strictnesses, "strictness"); break;
                    case "triggers": def.Triggers = ListOf(e, "frontmatter triggers"); break;
                    case "tags": def.Tags = ListOf(e, "frontmatter tags"); break;
                    case "whenToUse": def.WhenToUse = ScalarOf(e, "frontmatter"); break;
                    case "slots":
                        def.Slots = SeqOfMaps(e, "slots", SlotListKeys).Select(m => ToSlot(m, strict: true)).ToArray();
                        break;
                    case "provenance":
                        // The v2 home for free-form authorship keys: a true bag, open keys. A value is a
                        // scalar or a list — the spec's own example is `derived_from: [wfr-3, wfr-7]`, and
                        // the spec is the contract, so the documented shape is accepted and kept in its
                        // written form. A deeper structure has no meaning this format reads.
                        // The bare-section ruling (read three), applied to MAPS too (read four): a bare
                        // optional map key means an empty map, exactly as the pre-swap v2 reader read it.
                        var bag = BlockOf(e, "frontmatter",
                            "Write each authorship key as its own indented 'key: value' line beneath it.",
                            bareIsEmpty: true);
                        foreach (var pe in bag.Entries)
                            def.Provenance[pe.Key] = pe.Value is YSeq pseq
                                ? ProvenanceListForm(pseq, pe.Key)
                                : ScalarOf(pe, "provenance");
                        break;
                    default:
                        var reservedHere = Reservation("frontmatter", e.Key);
                        if (reservedHere != null)
                            throw new FormatException($"frontmatter: {reservedHere.Reason} Nothing was saved.");
                        throw Unknown("frontmatter", "frontmatter", e.Key, e.Line, FrontmatterKeys);
                }
            }
            return end + 1;
        }

        // ---- steps + fences --------------------------------------------------------------------

        private static void ParseSteps(string[] lines, int start, WorkflowDef def)
        {
            var strict = def.SchemaVersion >= 2;
            var steps = new List<WorkflowStep>();
            List<(int No, string Text)> body = null; WorkflowStep step = null; int lastNum = 0;

            void Flush()
            {
                if (step == null) return;
                var (instructions, gate, ops) = ExtractFences(body, step, strict);
                step.Instructions = instructions; step.Gate = gate; step.Ops = ops;
                steps.Add(step);
            }

            for (int i = start; i < lines.Length; i++)
            {
                var m = StepHeading.Match(lines[i]);
                // Checked before anything else decides what this line is, so it covers the preamble and a
                // step body alike: an indented heading is the same authoring mistake in both places, and the
                // damage is worse in the body, where the dropped step's own gate is silently folded into the
                // step above it as prose.
                if (!m.Success && strict)
                {
                    var indentedHeading = IndentedStepHeading.Match(lines[i]);
                    if (indentedHeading.Success)
                        throw new FormatException($"the heading '## Step {indentedHeading.Groups[1].Value}: {indentedHeading.Groups[2].Value}' on line {i + 1} is indented. A step heading starts at the left margin; indented, it is not a heading at all, so that step and everything under it would be read as ordinary text and none of it would run.");
                }
                if (m.Success)
                {
                    Flush();
                    var num = int.Parse(m.Groups[1].Value);
                    if (num != lastNum + 1) throw new FormatException($"step numbering broken at '## Step {num}:' (expected {lastNum + 1}).");
                    lastNum = num;
                    step = new WorkflowStep { Id = "step-" + num, Number = num, Title = m.Groups[2].Value };
                    body = new List<(int, string)>();
                }
                else if (body != null) body.Add((i + 1, lines[i]));
                // Preamble, before any step exists to own it. Prose here is ordinary and stays ordinary, but
                // a RECOGNISED fence is not prose: it belongs to a step, there is no step yet, and it was
                // dropped whole. The family's eighth member, and the one the accounted-for ledger cannot see
                // because a fence outside every step is outside every region the ledger asserts.
                else if (strict && (GateFenceOpen.IsMatch(lines[i]) || StepFenceOpen.IsMatch(lines[i])))
                    throw new FormatException($"a '{(GateFenceOpen.IsMatch(lines[i]) ? "yaml gate" : "yaml step")}' block appears before the first '## Step 1:' heading (line {i + 1}). A block belongs to the step above it, and there is no step above this one, so nothing in it would take effect. Move it under a step heading.");
                // The same loss with one leading space in front of it. The refusal above reads only what the
                // ANCHORED patterns call a fence, so an indented preamble fence matched neither it nor the
                // indented-fence refusal in ExtractFences, which never sees the preamble at all. Two guards
                // that each cover one axis leave the corner where both are wrong, which is where this sat.
                else if (strict && IndentedFence.IsMatch(lines[i]))
                    throw new FormatException($"a 'yaml {IndentedFence.Match(lines[i]).Groups[1].Value}' block appears before the first '## Step 1:' heading (line {i + 1}), and it is indented as well. A fence starts at the left margin and belongs to the step above it; there is no step above this one, so nothing in it would take effect. Move it under a step heading, at the left margin.");
            }
            Flush();
            def.Steps = steps.ToArray();
        }

        /// <summary>Pull the `yaml gate` fence, and (v2 only) the `yaml step` fence, out of a step body.
        /// In v1 a `yaml step` fence is ORDINARY TEXT, exactly as it is today: the v1-forever guarantee means
        /// a v1 file cannot start meaning something new. check_workflow warns that the block is inert, so the
        /// silence [T169] is about is broken by lint rather than by changing what a v1 file does.</summary>
        private static (string instructions, GateSpec gate, string[] ops) ExtractFences(
            List<(int No, string Text)> body, WorkflowStep step, bool strict)
        {
            var text = new List<string>(); GateSpec gate = null; string[] ops = null;
            var sawGate = false; var sawStepFence = false;
            for (int i = 0; i < body.Count; i++)
            {
                var isGate = GateFenceOpen.IsMatch(body[i].Text);
                var isStepFence = strict && StepFenceOpen.IsMatch(body[i].Text);
                // An INDENTED fence is not a fence to the regexes above, so the whole block became ordinary
                // instructions: one leading space and a hard verification disappears with nothing said. v2
                // refuses it; v1 keeps reading it as text, which is what a v1 file has always done.
                if (!isGate && !isStepFence && strict)
                {
                    var indented = IndentedFence.Match(body[i].Text);
                    if (indented.Success)
                        throw new FormatException($"Step {step.Number}: the '{indented.Groups[1].Value}' fence on line {body[i].No} is indented. A fence starts at the left margin; indented, this whole block is read as ordinary text and everything in it would be ignored.");
                }
                if (!isGate && !isStepFence) { text.Add(body[i].Text); continue; }

                var what = isGate ? "yaml gate" : "yaml step";
                if (isGate && sawGate) throw new FormatException($"Step {step.Number} has more than one 'yaml gate' block (max one per step).");
                if (isStepFence && sawStepFence) throw new FormatException($"Step {step.Number} has more than one 'yaml step' block (max one per step).");
                if (isGate) sawGate = true; else sawStepFence = true;

                var yaml = new List<(int, string)>(); int j = i + 1;
                for (; j < body.Count && body[j].Text.TrimEnd() != "```"; j++) yaml.Add(body[j]);
                if (j >= body.Count) throw new FormatException($"Step {step.Number}: '{what}' fence is never closed.");

                if (isGate)
                {
                    (gate, ops) = strict ? ParseGateV2(yaml, step.Number) : ParseGate(yaml, step.Number);
                    // an ops-only fence declares the action chain without gating anything — no GateSpec,
                    // so the step stays outside HasEnforcedGate and submit skips gate evaluation entirely
                    if (gate.Strictness == null && gate.Inputs.Length == 0 && gate.Verify.Length == 0) gate = null;
                }
                else ParseStepFence(yaml, step);

                i = j;                  // the fence is excluded from the instruction text
            }
            return (string.Join("\n", text).Trim(), gate, ops ?? Array.Empty<string>());
        }

        // ---- the v2 `yaml step` fence (§1.4-§1.7) ------------------------------------------------

        private static void ParseStepFence(List<(int No, string Text)> yaml, WorkflowStep step)
        {
            var ctx = $"Step {step.Number} step block";
            var map = RequireMap(ReadYamlBlock(yaml, ctx), ctx);
            foreach (var e in map.Entries)
            {
                switch (e.Key)
                {
                    case "id":
                        var id = ScalarOf(e, ctx);
                        if (string.IsNullOrWhiteSpace(id))
                            throw new FormatException($"Step {step.Number}: 'id:' has no value (line {e.Line}).");
                        if (!StepId.IsMatch(id))
                            throw new FormatException($"Step {step.Number}: id '{id}' must be lower-case letters, digits and hyphens, starting with a letter (line {e.Line}). For example: prove-rewrite.");
                        var pos = PositionalId.Match(id);
                        if (pos.Success && int.Parse(pos.Groups[1].Value) != step.Number)
                            throw new FormatException($"Step {step.Number}: id '{id}' is another step's positional address (line {e.Line}). A step may only take 'step-<N>' when N is its own number, so Step {step.Number} may use 'step-{step.Number}'. Pick a name instead.");
                        step.Id = id; step.HasExplicitId = true;
                        break;

                    case "when":
                        var when = ScalarOf(e, ctx);
                        if (string.IsNullOrWhiteSpace(when))
                            throw new FormatException($"Step {step.Number}: 'when:' has no condition (line {e.Line}).");
                        // §1.5 — parsed by the ONE evaluator so activation, verify-level `when:` and
                        // step-level `when:` can never grow three dialects. A SEMANTIC problem (an
                        // unreadable fact) is left for check_workflow to warn about, because the condition
                        // is merely inert; a STRUCTURAL one cannot be run at all, so it is refused here.
                        var expr = WorkflowPredicate.Parse(when, out var perr);
                        if (expr == null)
                            throw new FormatException($"Step {step.Number}: when '{when}' (line {e.Line}): {perr}");
                        step.When = when;
                        break;

                    case "forEach": step.ForEach = ToForEach(e, step); break;
                    case "call": step.Call = ToCall(e, step); break;

                    default:
                        var reserved = Reservation("step", e.Key);
                        if (reserved != null)
                            throw new FormatException($"Step {step.Number}: {reserved.Reason} Nothing was saved.");
                        throw Unknown(ctx, "step block", e.Key, e.Line, StepKeys);
                }
            }
        }

        private static ForEachSpec ToForEach(YEntry entry, WorkflowStep step)
        {
            var map = BlockOf(entry, $"Step {step.Number}",
                "It needs 'in:' (the list) and 'as:' (the name for one item), each on its own indented line.",
                bareIsEmpty: false);
            var ctx = $"Step {step.Number} forEach";
            var fe = new ForEachSpec();
            foreach (var e in map.Entries)
            {
                switch (e.Key)
                {
                    case "in":
                        // §1.6: a list is an inline literal or a gate input, and NOTHING else. No op
                        // results, no globs, no model queries — a list that comes from the model comes
                        // through an answer somebody gave, so it is on the record. The literal may be
                        // written in either YAML list spelling; both arrive here as a sequence.
                        if (e.Value is YSeq)
                        {
                            fe.InLiteral = ListOf(e, $"Step {step.Number}: forEach in");
                            break;
                        }
                        var inv = ScalarOf(e, ctx);
                        if (string.IsNullOrWhiteSpace(inv))
                            throw new FormatException($"Step {step.Number}: forEach 'in:' has no value (line {e.Line}).");
                        var m = ForEachInput.Match(inv);
                        if (!m.Success)
                            throw new FormatException($"Step {step.Number}: forEach in '{inv}' is not a list this format reads (line {e.Line}). Use an inline list like [Sales, Product], or an input like inputs.tableList.");
                        fe.InInput = m.Groups[1].Value;
                        break;
                    case "as":
                        var asv = ScalarOf(e, ctx);
                        if (string.IsNullOrWhiteSpace(asv) || !LoopVarName.IsMatch(asv))
                            throw new FormatException($"Step {step.Number}: forEach as '{asv}' must be a plain name (line {e.Line}), for example 'table'.");
                        fe.As = asv;
                        break;
                    case "maxIterations":
                        var mv = ScalarOf(e, ctx);
                        if (!int.TryParse(mv, out var max))
                            throw new FormatException($"Step {step.Number}: forEach maxIterations '{mv}' is not a whole number (line {e.Line}).");
                        if (max < 1)
                            throw new FormatException($"Step {step.Number}: forEach maxIterations {max} must be at least 1 (line {e.Line}).");
                        if (max > ForEachSpec.MaxMaxIterations)
                            throw new FormatException($"Step {step.Number}: forEach maxIterations {max} is above the ceiling of {ForEachSpec.MaxMaxIterations} (line {e.Line}). A longer list is refused rather than cut short, because a loop that stopped early would claim it covered everything.");
                        fe.MaxIterations = max;
                        break;
                    default:
                        throw Unknown(ctx, "forEach", e.Key, e.Line, ForEachKeys);
                }
            }
            if (fe.InLiteral == null && fe.InInput == null)
                throw new FormatException($"Step {step.Number}: 'forEach:' needs an 'in:' naming the list to repeat over (line {entry.Line}).");
            if (fe.As == null)
                throw new FormatException($"Step {step.Number}: 'forEach:' needs an 'as:' naming one item of the list (line {entry.Line}).");
            // §1.6: `loop.index` is the loop's own counter, so an item called `index` makes that name mean
            // two things and no condition could say which. Same rule as the existing refusal of an `as:` that
            // collides with a gate input name, one scope over.
            if (string.Equals(fe.As, "index", StringComparison.Ordinal))
                throw new FormatException($"Step {step.Number}: forEach as 'index' is the name of the loop's own counter (loop.index), so it cannot also name the item. Pick another name for the item.");
            return fe;
        }

        private static CallSpec ToCall(YEntry entry, WorkflowStep step)
        {
            var ctx = $"Step {step.Number} call";
            var map = BlockOf(entry, $"Step {step.Number}",
                "It takes 'workflow:', and optionally 'with:' and 'returns:', each on its own indented line.",
                bareIsEmpty: false);
            var call = new CallSpec();
            foreach (var e in map.Entries)
            {
                switch (e.Key)
                {
                    case "workflow": call.Workflow = ScalarOf(e, ctx); break;
                    case "returns": call.Returns = ListOf(e, $"Step {step.Number}: call returns"); break;
                    case "with":
                        // Same bare-section ruling: a bare `with:` is an empty hand-over, as pre-swap v2
                        // accepted it, so the shared body is told the bare spelling is legal here.
                        var with = BlockOf(e, ctx,
                            "It maps the callee's input names to values, so each pair goes on its own indented line.",
                            bareIsEmpty: true);
                        foreach (var we in with.Entries)
                        {
                            // An UNQUOTED loop reference reads as nested flow sequences in YAML, so the one
                            // mistake every author of a looped call will make first gets its own answer.
                            if (we.Value is YSeq ws && ws.Items.Count == 1 && ws.Items[0] is YSeq)
                                throw new FormatException($"Step {step.Number} call with: '{we.Key}' has an unquoted [[...]] value (line {we.Line}). Double square brackets are YAML lists here, so write the loop reference in quotes: \"[[loop.item]]\".");
                            call.With[we.Key] = ScalarOf(we, $"Step {step.Number} call with");   // the one OPEN map: keys are the CALLEE's input names
                        }
                        break;
                    default:
                        throw Unknown(ctx, "call", e.Key, e.Line, CallKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(call.Workflow))
                throw new FormatException($"Step {step.Number}: 'call:' needs a 'workflow:' naming the workflow to hand off to (line {entry.Line}).");
            return call;
        }

        // ---- the gate YAML subset ---------------------------------------------------------------
        // Grammar: top-level `strictness: x` scalar + `inputs:`/`verify:` lists; a list item is
        // `- key: value` with deeper-indented `key: value` continuation lines. That is ALL §2 uses.

        private static (GateSpec gate, string[] ops) ParseGate(List<(int No, string Text)> yaml, int stepNum)
        {
            var gate = new GateSpec(); string[] ops = null;
            var sections = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);
            List<(int, string)> current = null;          // the raw indented lines of the section being collected (null = under a scalar/unknown key)

            foreach (var (no, raw) in yaml)
            {
                if (IsSkippable(raw)) continue;   // in v1 a column-zero '#' line ENDS the section above it
                var indent = raw.Length - raw.TrimStart().Length;

                if (indent == 0)
                {
                    var (key, value) = SplitKeyValue(raw.Trim());
                    if (key == null) throw new FormatException($"Step {stepNum} gate: cannot parse line '{raw.Trim()}'.");
                    current = null;
                    switch (key)
                    {
                        case "strictness": gate.Strictness = RequireEnum(value, Strictnesses, "strictness"); break;
                        case "ops": ops = ParseInlineList(value); break;   // the declared MCP action chain (advisory)
                        case "inputs":
                        case "verify":
                            // A repeated section REPLACES the earlier one, and an inline value on a list key
                            // parses to zero entries. Both are v1's frozen behaviour; check_workflow warns.
                            current = sections[key] = new List<(int, string)>(); break;
                        default:
                            break;               // v1: unknown keys preserved-and-ignored
                    }
                    continue;
                }
                // Indented content with NO list section open: v1 drops it silently and always has.
                if (current == null) continue;
                current.Add((no, raw));
            }

            gate.Inputs = (sections.TryGetValue("inputs", out var ins) ? ParseFlatMapList(ins, m => $"Step {stepNum} gate: {m}") : new List<FlatMap>())
                .Select(it => ToInput(it, stepNum, strict: false)).ToArray();
            gate.Verify = (sections.TryGetValue("verify", out var vs) ? ParseFlatMapList(vs, m => $"Step {stepNum} gate: {m}") : new List<FlatMap>())
                .Select(it => ToVerify(it, stepNum, strict: false)).ToArray();
            return (gate, ops);
        }

        /// <summary>The v2 gate reader: YamlDotNet reads the fence interior, our rules run on the model.
        /// The closed key set, the named reservations, the list-key shape rule and the enum checks are all
        /// OURS; what a line means as YAML (quoting, comments, indentation, duplicates) is the library's.</summary>
        private static (GateSpec gate, string[] ops) ParseGateV2(List<(int No, string Text)> yaml, int stepNum)
        {
            var ctx = $"Step {stepNum} gate";
            var gate = new GateSpec(); string[] ops = null;
            var map = RequireMap(ReadYamlBlock(yaml, ctx), ctx);

            foreach (var e in map.Entries)
            {
                switch (e.Key)
                {
                    case "strictness": gate.Strictness = RequireEnum(ScalarOf(e, ctx), Strictnesses, "strictness"); break;
                    case "ops": ops = ListOf(e, $"{ctx}: ops"); break;   // the declared MCP action chain (advisory)
                    case "inputs":
                        gate.Inputs = SeqOfMaps(e, ctx, InputListKeys).Select(it => ToInput(it, stepNum, strict: true)).ToArray();
                        break;
                    case "verify":
                        gate.Verify = SeqOfMaps(e, ctx, VerifyListKeys).Select(it => ToVerify(it, stepNum, strict: true)).ToArray();
                        break;
                    default:
                        var reserved = Reservation("gate", e.Key);
                        if (reserved != null)
                            throw new FormatException($"{ctx}: {reserved.Reason} Nothing was saved.");
                        throw Unknown(ctx, "Gate", e.Key, e.Line, GateKeys);
                }
            }
            return (gate, ops);
        }

        // openShapesFrom (dax_equivalence) and anchors (expected_values / anchor_coverage) both bind a TEXT input on the SAME step or
        // ANY PRIOR step. Resolution is run-wide (AccumulatedAnswers, later answers win) exactly like `probe:`, and a
        // completed step can never be re-answered (WorkflowRunner.RequireCurrentStep) — so a prior-step binding is
        // INHERITANCE-ONLY. A dangling name would silently mean "no partition"/"no anchors", so refuse; and the bound
        // input must be TEXT — the answer is a shape-id list / anchor JSON, so an objectRef/number/enum binding is an
        // authoring mistake that could only ever fail at run time. Cross-step by design, so it runs AFTER every step
        // is parsed (never inside a single gate's scope).
        private static void ValidateInputBindings(WorkflowDef def)
        {
            for (int s = 0; s < def.Steps.Length; s++)
                foreach (var v in def.Steps[s].Gate?.Verify ?? Array.Empty<VerifySpec>())
                {
                    ValidateTextBinding(def, s, v.OpenShapesFrom, "openShapesFrom", "the answer is a comma-separated shape-id list");
                    ValidateTextBinding(def, s, v.Anchors, "anchors", "the answer carries the locked anchor JSON");
                    if (v.OpenMismatch == "countersign")
                    {
                        ValidateTextBinding(def, s, "countersign", "openMismatch countersign", "the answer is the exact disputed-cell set and stated sentence");
                        var countersign = def.Steps.Take(s + 1)
                            .SelectMany(st => st.Gate?.Inputs ?? Array.Empty<GateInput>())
                            .Last(i => string.Equals(i.Name, "countersign", StringComparison.Ordinal));
                        if (countersign.Required != "optional")
                            throw new FormatException($"Step {def.Steps[s].Number} gate: openMismatch countersign input 'countersign' must use 'required: optional'. It is a machine exit that must be answered when taken; a decline is not a countersign.");
                    }
                    if (v.Kind == "anchor_coverage")
                    {
                        ValidateTextBinding(def, s, "equivalenceGrid", "anchor_coverage equivalenceGrid", "the answer is a comma-separated qualified-column list");
                        ValidateTextBinding(def, s, "openGrains", "anchor_coverage openGrains", "the answer is a comma-separated canonical shape-id list");
                    }
                }
        }

        private static void ValidateTextBinding(WorkflowDef def, int stepIndex, string name, string key, string why)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            // Latest declaration wins on a same-name clash across steps — mirroring the run-wide answer map.
            var bound = def.Steps.Take(stepIndex + 1)
                .SelectMany(st => st.Gate?.Inputs ?? Array.Empty<GateInput>())
                .LastOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (bound == null)
                throw new FormatException($"Step {def.Steps[stepIndex].Number} gate: {key} '{name}' does not name an input on this or any prior step.");
            if (!string.Equals(bound.Type, "text", StringComparison.Ordinal))
                throw new FormatException($"Step {def.Steps[stepIndex].Number} gate: {key} '{name}' must bind a 'text' input (got '{bound.Type}'): {why}.");
        }

        // ---- v2 cross-step validation (§1.4, §1.6) ------------------------------------------------
        // Everything here needs the WHOLE file, so it cannot live inside a single step's parse. What needs
        // the whole LIBRARY (does the callee exist, is the chain too deep) is check_workflow's job instead,
        // because the parser has no library to look in.

        private static void ValidateV2(WorkflowDef def)
        {
            // ids: unique, and no explicit id may take a positional address that is not its own. The
            // positional-collision case is already refused per step; this catches name-on-name collisions.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var st in def.Steps)
            {
                if (seen.TryGetValue(st.Id, out var first))
                    throw new FormatException($"Step {first} and Step {st.Number} both use the id '{st.Id}'. A step id has to be unique within the file, because it is how a step is addressed.");
                seen[st.Id] = st.Number;
            }

            var inputNames = def.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>())
                .Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.Ordinal);

            for (int s = 0; s < def.Steps.Length; s++)
            {
                var st = def.Steps[s];

                // §1.6 — the slot delimiters and the loop delimiters have two different lifetimes, and a
                // run-time substitution wearing the slot delimiters would be indistinguishable in the file.
                // EVERY string the step carries, not just its body. Scanning `Instructions` alone covered the
                // one place the finding was first written and left `with:` values, `returns:`, `when:` and
                // the loop's own list open, which is the shape of defect this slice keeps paying for. The
                // spec (section 1.6) says every use is refused, so the code now reads every use.
                foreach (var authored in AuthoredStrings(st))
                {
                    var slotShaped = SlotShapedLoopRef.Match(authored ?? "");
                    if (slotShaped.Success)
                        throw new FormatException($"Step {st.Number}: a loop value is written [[loop.{slotShaped.Groups[1].Value}]], not {{{{loop.{slotShaped.Groups[1].Value}}}}}. Double braces mark a template fill-in, which is filled in once when the template is set up; a loop value is different on every pass.");
                }

                var fe = st.ForEach;
                if (fe == null) continue;

                if (inputNames.Contains(fe.As))
                    throw new FormatException($"Step {st.Number}: forEach as '{fe.As}' is also the name of a gate input in this file. The loop item needs a name of its own, so a condition reading loop.{fe.As} cannot mean two things.");

                RefuseRunLevelAnswersInsideALoop(def, st);

                if (fe.InInput == null) continue;

                // §3.4 check 5 — the source must be collected by a STRICTLY PRECEDING step, or by this
                // step's own gate. A loop reads its list when its own step is reached (§1.6.1), so a source
                // collected LATER can never have a value in time. Accepting "some step" would pass lint and
                // then fail at run time, which is the authoring-time-to-run-time downgrade this format
                // refuses everywhere else.
                var declaredOnOrBefore = def.Steps.Take(s + 1)
                    .Any(p => (p.Gate?.Inputs ?? Array.Empty<GateInput>()).Any(i => string.Equals(i.Name, fe.InInput, StringComparison.Ordinal)));
                if (declaredOnOrBefore) continue;

                var later = def.Steps.Skip(s + 1)
                    .FirstOrDefault(p => (p.Gate?.Inputs ?? Array.Empty<GateInput>()).Any(i => string.Equals(i.Name, fe.InInput, StringComparison.Ordinal)));
                if (later != null)
                    throw new FormatException($"Step {st.Number} ({st.Id}): forEach in 'inputs.{fe.InInput}' names an answer that Step {later.Number} collects, which is after this step. The list is read when Step {st.Number} is reached, so Step {later.Number} has to move before it.");
                throw new FormatException($"Step {st.Number} ({st.Id}): forEach in 'inputs.{fe.InInput}' does not name an answer this step or any earlier step collects.");
            }
        }

        /// <summary>Every string on a step that an AUTHOR wrote, so a rule about "anything the author typed"
        /// is applied to all of it rather than to the one field the finding named. Derived from the step
        /// model rather than sampled: `WorkflowStep` plus the four objects it owns (`ForEachSpec`, `CallSpec`,
        /// `GateSpec`'s inputs and verifies). The enum-validated fields (`kind`, `type`, `required`, `scope`,
        /// `intent`, `openMismatch`, `strictness`) are deliberately left out — <c>RequireEnum</c> has already
        /// refused anything that is not one of a fixed set, so they cannot carry author text of any shape.</summary>
        private static IEnumerable<string> AuthoredStrings(WorkflowStep st)
        {
            yield return st.Id; yield return st.Title; yield return st.Instructions; yield return st.When;
            foreach (var op in st.Ops ?? Array.Empty<string>()) yield return op;
            if (st.ForEach != null)
            {
                yield return st.ForEach.As; yield return st.ForEach.InInput;
                foreach (var v in st.ForEach.InLiteral ?? Array.Empty<string>()) yield return v;
            }
            if (st.Call != null)
            {
                yield return st.Call.Workflow;
                foreach (var r in st.Call.Returns ?? Array.Empty<string>()) yield return r;
                foreach (var kv in st.Call.With) { yield return kv.Key; yield return kv.Value; }
            }
            foreach (var gi in st.Gate?.Inputs ?? Array.Empty<GateInput>())
            {
                yield return gi.Name; yield return gi.Question; yield return gi.DaxPurity;
            }
            foreach (var v in st.Gate?.Verify ?? Array.Empty<VerifySpec>())
            {
                yield return v.When; yield return v.Probe; yield return v.OpenShapesFrom; yield return v.Anchors;
                foreach (var s in v.PinnedShapes ?? Array.Empty<string>()) yield return s;
                foreach (var s in v.OpenShapes ?? Array.Empty<string>()) yield return s;
            }
        }

        /// <summary>§2.2a, acceptance check 16b — the answers a repeating step may NOT collect.
        ///
        /// `certificate` is the front-door case: <c>ComputeCertificate</c> reads the claim through
        /// <c>AccumulatedAnswers</c>, last-wins, so a `certificate` answered inside a loop would let pass 5's
        /// FULL erase pass 2's OVERRIDDEN. That is precisely the laundering the weakest-wins rule exists to
        /// stop, arriving through the escape hatch instead. The refusal holds WITH OR WITHOUT
        /// <c>scope: run</c>, because scope is the hatch and a rule that only caught the default would leave
        /// it open.
        ///
        /// The same holds for any answer a verify reads as run-level LOCK state — `openShapesFrom`,
        /// `anchors`, and a `dax_equivalence` `probe`. Those resolve against singletons, so a fresh answer
        /// per pass re-points a lock that an earlier pass's evidence has already been judged against. That
        /// is what shrinks `scope: run` to what it should be: notes, labels, a ticket reference, and nothing
        /// anything verifies against.
        ///
        /// Refused at PARSE time, which gives both halves the spec asks for at once: save_workflow
        /// parse-validates before writing, and check_workflow surfaces it as ParseError.</summary>
        private static void RefuseRunLevelAnswersInsideALoop(WorkflowDef def, WorkflowStep loopStep)
        {
            foreach (var input in loopStep.Gate?.Inputs ?? Array.Empty<GateInput>())
            {
                if (string.Equals(input.Name, "certificate", StringComparison.Ordinal))
                    throw new FormatException($"Step {loopStep.Number} ({loopStep.Id}): the answer 'certificate' cannot be collected on a step that repeats, with or without 'scope: run'. It is one claim about the whole run, so it is collected after the repeating is finished, not once per pass.");

                foreach (var other in def.Steps)
                    foreach (var v in other.Gate?.Verify ?? Array.Empty<VerifySpec>())
                    {
                        string what = null;
                        if (string.Equals(v.OpenShapesFrom, input.Name, StringComparison.Ordinal)) what = "openShapesFrom";
                        else if (string.Equals(v.Anchors, input.Name, StringComparison.Ordinal)) what = "anchors";
                        else if (v.Kind == "dax_equivalence" && string.Equals(v.Probe, input.Name, StringComparison.Ordinal)) what = "dax_equivalence probe";
                        if (what == null) continue;
                        throw new FormatException($"Step {loopStep.Number} ({loopStep.Id}): the answer '{input.Name}' cannot be collected on a step that repeats, because Step {other.Number}'s {what} reads it as a value locked for the whole run. A fresh answer on each pass would move a lock that an earlier pass has already been judged against.");
                    }
            }
        }

        // ---- the v2 lint (§3.4) -------------------------------------------------------------------
        // These are check_workflow's checks, not the parser's, because each one needs the whole LIBRARY:
        // does the callee exist, is it a workflow rather than a template, does the chain loop or run too
        // deep, does a `with:` key name an input the callee actually declares. All static, all free, all
        // decidable from the files without running anything — which is the point: an authoring-time refusal
        // beats a run-time surprise, and this format refuses that downgrade everywhere else.

        private static readonly Regex StepFenceInText = new Regex(@"^\s*```\s*yaml\s+step\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex StepInputFact = new Regex(@"^inputs\.([A-Za-z0-9_-]+)\.(?:answered|declined|value)$", RegexOptions.Compiled);
        /// <summary>§1.7 — a `with:` VALUE may be a literal, a `[[loop.*]]` reference, or an
        /// `inputs.<name>` reference resolved in the CALLER.</summary>
        private static readonly Regex CallerInputRef = new Regex(@"^inputs\.([A-Za-z0-9_-]+)$", RegexOptions.Compiled);

        /// <summary>§1.7 — the `[[loop.<fact>]]` form of a `with:` VALUE. Anchored, because the whole value
        /// is the reference: a literal that merely CONTAINS the text is a literal, and matching loosely here
        /// would repeat the substring search that reported `connection.database ~ '*loop.*'` as a loop read.
        /// Note the deliberately different bracket from <see cref="SlotShapedLoopRef"/>, which exists to
        /// catch the `{{loop.x}}` spelling and say why it is wrong.</summary>
        // One spelling for run-time loop references. Call bindings anchor the whole value; instruction
        // rendering finds the same token inside markdown. Sharing the token grammar prevents the two
        // consumers from disagreeing about harmless inner whitespace or which names must fail closed.
        internal const string LoopReferencePattern = @"\[\[\s*loop\.([A-Za-z0-9_]+)\s*\]\]";
        private static readonly Regex LoopRefValue = new Regex("^" + LoopReferencePattern + "$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>What one workflow's `inputs.` namespace holds: its own gate declarations, plus every name
        /// a `call:` brings back FROM A CALLEE THAT COULD ACTUALLY DELIVER IT. §1.7 (spec §1.7, the `returns:`
        /// bullet) puts a returned answer in the CALLER's namespace under the same name, so leaving those out
        /// reported legal references as unresolvable — but taking the caller's raw `returns:` list on trust is
        /// the same mistake pointing the other way: a name a missing, unreadable, template or non-declaring
        /// callee can never produce would silently license every reference to it.
        ///
        /// A callee with a retained <c>Error</c> is the case that looks sound and is not: a file refused after
        /// its steps were parsed still carries its input rows, so the name resolves against a workflow that
        /// cannot run. Both consumers of this set (V2Findings here, and check_workflow's own run-wide input
        /// names) call it, because two copies of this rule would drift and the second copy is where the hole
        /// would reopen.
        ///
        /// Deliberately NOT order-sensitive: the callee is unrolled into the caller's plan at run start
        /// (§1.7) rather than resolved in document order, so a name returned anywhere in the file is a name
        /// this file collects.</summary>
        public static HashSet<string> CallerInputNames(WorkflowDef def, IReadOnlyList<WorkflowDef> workflows) =>
            CallerInputNames(def, ByName(workflows), new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 1);

        private static Dictionary<string, WorkflowDef> ByName(IReadOnlyList<WorkflowDef> workflows) =>
            (workflows ?? Array.Empty<WorkflowDef>())
                .Where(w => !string.IsNullOrEmpty(w.Name))
                .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        /// <summary>RECURSIVE, because a returned name is a name in the callee's namespace, and that
        /// namespace can itself hold a name the callee got back from a hand-off of its own. A chain of three
        /// (C declares it, B brings it back, A brings back what B has) is legal and inside the depth limit,
        /// and reading only B's own gates reported it broken. <paramref name="onPath"/> stops a cycle:
        /// the loop itself is reported by the (d) walk, so here it only has to terminate.
        ///
        /// BOUNDED BY <see cref="CallSpec.MaxDepth"/>, counting the workflow being resolved as depth 1: the
        /// same ratified limit the run engine refuses at run start. Unbounded, it admitted a name from four
        /// workflows down, so the checker told the author a reference resolves against something the engine
        /// will never reach. The separate depth WARNING does not undo a false namespace, and a checker more
        /// permissive than the engine is the authoring-time-to-run-time downgrade pointing backwards.</summary>
        private static HashSet<string> CallerInputNames(WorkflowDef def, Dictionary<string, WorkflowDef> byName, HashSet<string> onPath, int depth)
        {
            var names = (def?.Steps ?? Array.Empty<WorkflowStep>())
                .SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>())
                .Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.Ordinal);
            if (def == null || depth >= CallSpec.MaxDepth || !onPath.Add(def.Name ?? "")) return names;

            foreach (var st in def.Steps)
            {
                var call = st.Call;
                if (call?.Workflow == null || call.Returns.Length == 0) continue;
                if (!byName.TryGetValue(call.Workflow, out var callee)) continue;          // missing: reported by (c)
                if (callee.Error != null) continue;                                        // unreadable: reported by (c)
                if (string.Equals(callee.Kind, "template", StringComparison.Ordinal)) continue;   // not runnable
                var available = CallerInputNames(callee, byName, onPath, depth + 1);
                foreach (var ret in call.Returns)
                    if (available.Contains(ret)) names.Add(ret);
            }
            onPath.Remove(def.Name ?? "");
            return names;
        }

        /// <summary>All parsed stage-1 control fields execute on the shared runner. Retained as the public
        /// admission query; later reserved fields are still refused by the file parser.</summary>
        public static IReadOnlyList<(string StepId, int StepNumber, string Field)> UnexecutableControlFields(WorkflowDef def) =>
            Array.Empty<(string StepId, int StepNumber, string Field)>();

        /// <summary>The format-v2 findings for one workflow, given the library it sits in. Parse-level
        /// refusals (unknown keys, bad ids, a loop reading a list collected later) are NOT repeated here:
        /// they already came back as <c>ParseError</c> and a file carrying one never reaches this.</summary>
        public static List<CheckFinding> V2Findings(WorkflowDef def, IReadOnlyList<WorkflowDef> workflows, IReadOnlyList<WorkflowDef> templates)
        {
            var findings = new List<CheckFinding>();
            workflows ??= Array.Empty<WorkflowDef>();
            templates ??= Array.Empty<WorkflowDef>();

            // (a) A v1 file carrying a `yaml step` block. The block is inert TEXT in v1 and must stay that
            // way, because a v1 file cannot start meaning something new. But silence about a block the
            // author clearly meant to have an effect IS the [T169] defect, so it is said out loud here.
            if (def.SchemaVersion < 2)
                foreach (var st in def.Steps)
                    if (StepFenceInText.IsMatch(st.Instructions ?? ""))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} has a 'yaml step' block, but this file has no version number, so it is read as version 1 and the block is treated as ordinary text. Add 'schemaVersion: 2' as the first line of the frontmatter to make it take effect." });

            // (a2) A `schemaVersion` the version scan did not read: not the first key, or below a nested line,
            // or hidden under a key that opens a block. v1 cannot refuse it (frozen) and v2 already has, so
            // this warn is the only thing standing between the author and a file that quietly runs under the
            // wrong ruleset. That silence is exactly the [T169] defect this slice exists to end.
            if (def.SchemaVersion < 2 && def.HasUnreadSchemaVersion)
                findings.Add(new CheckFinding { Severity = "warn",
                    Message = "this file has a 'schemaVersion' line, but not as the first key of the frontmatter, so it is not read as the file's version and the file runs as version 1. Move it to the first line of the frontmatter to make it take effect." });

            // (b) Every `when:` term is one this version can read. An unreadable term is a WARN, not an
            // error, matching how a deferred fact is already treated: the condition is inert. The warn is
            // the load-bearing half, because at run time an unknown term makes every comparison false, so a
            // misspelling SILENTLY skips the step and nothing else would ever tell the author.
            var collected = CallerInputNames(def, workflows);

            // F-072: availability is also a question of ORDER, and asking only "does any gate collect this?"
            // could not see a step reading an answer collected AFTER it. `when:` is evaluated BEFORE the step
            // runs, so a step cannot see its OWN gate's answers either: at run time the fact is unknown, every
            // comparison is false and the step silently does not run — the identical end state as a
            // misspelling, which the warn beside this one has covered for a long time.
            //
            // Built as a running prefix rather than by index arithmetic, so a step contributes only what it
            // collects: its own gate inputs, plus any name a `call:` on it brings back. Deliberately NOT
            // recursive into the callee's own hand-offs: `collected` above already owns the "does this name
            // exist at all" question, and this check only ever narrows it, so the worst it can do is stay
            // silent. Reported only when the name IS collected somewhere, so the two warns never double up.
            var availableBefore = new List<HashSet<string>>();
            var running = new HashSet<string>(StringComparer.Ordinal);
            foreach (var st in def.Steps)
            {
                availableBefore.Add(new HashSet<string>(running, StringComparer.Ordinal));
                foreach (var gi in st.Gate?.Inputs ?? Array.Empty<GateInput>())
                    if (!string.IsNullOrEmpty(gi.Name)) running.Add(gi.Name);
                foreach (var r in st.Call?.Returns ?? Array.Empty<string>()) running.Add(r);
            }

            for (int si = 0; si < def.Steps.Length; si++)
            {
                var st = def.Steps[si];
                if (string.IsNullOrWhiteSpace(st.When)) continue;
                var parsed = WorkflowPredicate.Parse(st.When, out var perr);
                if (perr != null)
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"{st.Id} when: {perr}" });

                // `inputs.<name>.*` is matched by SHAPE, so a misspelled input name is a perfectly readable
                // fact and the unreadable-term warn above never fires. At run time the fact is unknown,
                // every comparison against it is false, and the step SILENTLY does not run. The verify-level
                // equivalent has been warned about for a long time (LocalEngine.Workflows.cs:910); the
                // step-level one was not, which left the more dangerous of the two unguarded.
                foreach (var term in WorkflowPredicate.FactTerms(parsed))
                {
                    var m = StepInputFact.Match(term);
                    if (!m.Success) continue;
                    var iname = m.Groups[1].Value;
                    if (!collected.Contains(iname))
                    {
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} when '{st.When}' reads '{term}', but no gate collects an answer named '{iname}'. The condition would never hold, so this step would never run." });
                        continue;
                    }
                    if (!availableBefore[si].Contains(iname))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} when '{st.When}' reads '{term}', but '{iname}' is not collected until this step or a later one. A step's condition is checked before it runs, so that answer does not exist yet and this step would never run. Move the question to an earlier step." });
                }
                // Read from the PARSED terms, never the raw text. A substring search matched 'loop.' inside a
                // string literal, so a valid condition like `connection.database ~ '*loop.*'` was reported as
                // reading a loop value outside a loop, and that warn makes check_workflow return not-ok. An
                // over-refusal that blocks a workflow which would run is the worse direction of error, and a
                // lint that reads source text instead of the thing the engine evaluates will always find one.
                var loopTerms = WorkflowPredicate.FactTerms(parsed)
                    .Where(t => t.Split('.')[0] == "loop").Distinct(StringComparer.Ordinal).ToList();
                if (st.ForEach == null)
                {
                    if (loopTerms.Count > 0)
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} when '{st.When}' reads a loop value, but this step does not repeat. Outside a loop that value is never set, so the condition never holds and the step never runs." });
                }
                // Having a loop is not the same as having THAT value. §1.6 binds exactly two loop facts:
                // `loop.<as>` and the fixed `loop.index`. Checking only for the PRESENCE of a forEach let
                // `when: loop.measure` pass lint on a loop bound `as: table`, where the fact is unknown at
                // run time, every comparison against it is false, and the step silently does not run — the
                // identical hazard the misspelled-input warn above covers, one fact root over, and the same
                // shape as checking that a fence exists rather than that it is the right fence.
                else foreach (var term in loopTerms)
                {
                    var parts = term.Split('.');
                    if (parts.Length < 2) continue;   // a bare `loop`: the unreadable-term warn above owns it
                    if (parts[1] == "index" || string.Equals(parts[1], st.ForEach.As, StringComparison.Ordinal)) continue;
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} when '{st.When}' reads '{term}', but this step's loop names its item '{st.ForEach.As}'. Only loop.{st.ForEach.As} and loop.index are set on a pass, so the condition never holds and the step never runs." });
                }
            }

            // (b2) A `[[loop.*]]` binding in `with:` is inside a loop that actually binds it. §1.6's fact
            // table (spec §1.6, the `loop.index` and `loop.<as>` rows) sets those two facts ONLY on a pass of
            // a `forEach:`, so outside one the reference resolves to nothing and the callee is handed an
            // unset value. That is the same hazard the `when:` scoping above covers, one field over.
            //
            // F-068: this was deliberately deferred, on the reasoning that a loop reference "is checked
            // against the enclosing loop when the runner slice lands the frames it would resolve against".
            // The reasoning does not hold, and the deferral was the more dangerous half: whether a step has
            // a `forEach:` and what it binds are both known HERE, from the file alone, with no frame and no
            // runner. Nothing was waiting on the runner except the check.
            //
            // Separate from the `with:` validation in (c) on purpose: that one needs the callee to resolve,
            // and an impossible binding is impossible whether or not the callee can be found. Folding it in
            // there would have made a real defect invisible behind a missing file.
            foreach (var st in def.Steps)
            {
                if (st.Call == null) continue;
                foreach (var pair in st.Call.With)
                {
                    var lm = LoopRefValue.Match(pair.Value ?? "");
                    if (!lm.Success) continue;
                    var fact = lm.Groups[1].Value;
                    if (st.ForEach == null)
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} call with '{pair.Key}: {pair.Value}' reads a loop value, but this step does not repeat. Outside a loop that value is never set, so '{pair.Key}' would be handed over empty." });
                    else if (fact != "index" && !string.Equals(fact, st.ForEach.As, StringComparison.Ordinal))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} call with '{pair.Key}: {pair.Value}' reads 'loop.{fact}', but this step's loop names its item '{st.ForEach.As}'. Only loop.{st.ForEach.As} and loop.index are set on a pass, so '{pair.Key}' would be handed over empty." });
                }
            }

            // (c) Hand-offs resolve, against the library rather than against hope. Same index the namespace
            // helper builds, from the same builder, so "which file is this name?" has one answer here too.
            var byName = ByName(workflows);
            var templateNames = templates.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            ValidateCallEdges(def, byName, templateNames, collected, findings);

            // (d) The call graph is acyclic and inside the depth limit. Walked over the whole reachable
            // graph, not just this file's own hand-offs, because a chain that runs too deep three levels
            // down still refuses at run start and the author needs to know now.
            WalkReachable(def, byName, findings);

            return findings;
        }

        /// <summary>[T220] Every `call:` edge of ONE definition, under the format's callee-aware rules. Lifted
        /// out of <see cref="V2Findings"/>'s (c) block unchanged so authoring-time checking and run-start
        /// closure resolution share ONE rule set. Two copies would drift, and the copy that drifts looser is
        /// where the authoring-time-to-run-time downgrade reopens.</summary>
        private static void ValidateCallEdges(WorkflowDef def, Dictionary<string, WorkflowDef> byName,
            HashSet<string> templateNames, HashSet<string> collected, List<CheckFinding> findings)
        {
            foreach (var st in def.Steps)
            {
                var call = st.Call;
                if (call?.Workflow == null) continue;

                if (string.Equals(call.Workflow, def.Name, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} call workflow '{call.Workflow}' is this workflow itself. A workflow cannot hand off to itself." });
                    continue;
                }
                if (templateNames.Contains(call.Workflow) && !byName.ContainsKey(call.Workflow))
                {
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} call workflow '{call.Workflow}' is a template, not a workflow. A template is a recipe with blanks to fill in and cannot be run; set one up first, then hand off to what that produces." });
                    continue;
                }
                if (!byName.TryGetValue(call.Workflow, out var callee))
                {
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} call workflow '{call.Workflow}' is not a workflow this library holds. The Workflows tab shows what is available." });
                    continue;
                }
                if (string.Equals(callee.Kind, "template", StringComparison.Ordinal))
                {
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} call workflow '{call.Workflow}' is a template, not a workflow. A template is a recipe with blanks to fill in and cannot be run." });
                    continue;
                }
                // A callee that does not parse. It still carries whatever was read before it was refused, so
                // its input rows would answer "does the callee declare this?" perfectly well and the hand-off
                // would look sound while the run it promises cannot happen. Checked BEFORE those rows are
                // used, and stops here rather than validating `with:`/`returns:` against a partial file.
                if (callee.Error != null)
                {
                    findings.Add(new CheckFinding { Severity = "warn",
                        Message = $"{st.Id} call workflow '{call.Workflow}' cannot be read, so this hand-off cannot run: {callee.Error} Fix that workflow first; nothing it is meant to return can be relied on here." });
                    continue;
                }

                var declared = callee.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>())
                    .Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.Ordinal);
                foreach (var pair in call.With)
                {
                    if (!declared.Contains(pair.Key))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} call with '{pair.Key}' is not a question that '{callee.Name}' asks. The boundary is closed, so only its own questions can be answered from here." });

                    // The VALUE side, which went unchecked: only the callee-side key was validated, so a
                    // mapping could name a real question of the callee and feed it from an answer the
                    // caller never collects. A loop reference is left alone here; it is checked against the
                    // enclosing loop when the runner slice lands the frames it would resolve against.
                    var cm = CallerInputRef.Match(pair.Value ?? "");
                    if (cm.Success && !collected.Contains(cm.Groups[1].Value))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} call with '{pair.Key}: {pair.Value}' reads an answer named '{cm.Groups[1].Value}', but no gate in this workflow collects one. There would be nothing to hand over." });
                }
                // `returns:` asks a WIDER question than `with:` does, and conflating the two reported a legal
                // relay as broken. `with:` answers a question the callee itself asks, so it is checked
                // against the callee's own gate declarations. `returns:` brings back a name from the
                // callee's NAMESPACE, which also holds whatever the callee gets back from a hand-off of its
                // own: C declares it, B returns it, A returns what B has. Three levels is inside the limit.
                // depth 2: the callee sits one below the workflow being checked, so what IT can bring back is
                // bounded from here exactly as it is when the caller's own namespace is built.
                var available = CallerInputNames(callee, byName, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 2);
                foreach (var ret in call.Returns)
                    if (!available.Contains(ret))
                        findings.Add(new CheckFinding { Severity = "warn",
                            Message = $"{st.Id} call returns '{ret}' is not a question that '{callee.Name}' asks or brings back itself, so there is nothing by that name to hand over." });
            }
        }

        /// <summary>[T220] The ONE case-insensitive reachable-graph walk, lifted out of <see cref="V2Findings"/>'s
        /// (d) block. It reports cycles and over-depth chains through <paramref name="findings"/> in exactly the
        /// order and wording the checker already printed, and returns the reachable definitions in discovery
        /// order with the root first, which is what the run start freezes.
        /// <para>Traversal is deliberately NOT deduped by name: a definition reached down two different paths is
        /// walked twice, because the second path can carry a chain the first did not and dropping it would make
        /// the checker quieter than it is today. Only the returned SET is deduped.</para></summary>
        private static List<WorkflowDef> WalkReachable(
            WorkflowDef root, Dictionary<string, WorkflowDef> byName, List<CheckFinding> findings)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<WorkflowDef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return order;
            seen.Add(root.Name ?? "");
            order.Add(root);

            void Walk(WorkflowDef cur, List<string> path)
            {
                foreach (var st in cur.Steps)
                {
                    var callee = st.Call?.Workflow;
                    if (callee == null) continue;
                    var chain = string.Join(" -> ", path.Concat(new[] { callee }));

                    if (path.Contains(callee, StringComparer.OrdinalIgnoreCase))
                    {
                        if (reported.Add("cycle:" + chain))
                            findings.Add(new CheckFinding { Severity = "warn",
                                Message = $"the hand-offs loop back on themselves: {chain}. Remove one of them." });
                        continue;
                    }
                    if (!byName.TryGetValue(callee, out var next)) continue;   // already reported as missing
                    if (path.Count + 1 > CallSpec.MaxDepth)
                    {
                        if (reported.Add("depth:" + chain))
                            findings.Add(new CheckFinding { Severity = "warn",
                                Message = $"the hand-offs run {path.Count + 1} deep: {chain}. The limit is {CallSpec.MaxDepth}, counting this run as 1. Remove a hand-off, or fold one of them into the workflow above it." });
                        continue;
                    }
                    if (seen.Add(next.Name ?? "")) order.Add(next);
                    Walk(next, path.Concat(new[] { callee }).ToList());
                }
            }
            Walk(root, new List<string> { root.Name ?? "" });
            return order;
        }

        /// <summary>[T220] The complete reachable closure a run start freezes, plus every problem that makes it
        /// unrunnable. Same walk and same edge rules the checker uses, so the engine and the checker cannot
        /// disagree about reachability: the checker maps these to authoring warnings, the run start maps them to
        /// refusals. Proving the callee FILE exists is not enough before a run is registered, so every reachable
        /// definition's own `with:` and `returns:` bindings are validated too, not just the root's.</summary>
        internal static IReadOnlyList<WorkflowDef> ReachableClosure(
            WorkflowDef root, IReadOnlyList<WorkflowDef> workflows, IReadOnlyList<WorkflowDef> templates,
            out IReadOnlyList<CheckFinding> problems)
        {
            var byName = ByName(workflows);
            var templateNames = (templates ?? Array.Empty<WorkflowDef>()).Select(t => t.Name)
                .Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var findings = new List<CheckFinding>();
            var order = WalkReachable(root, byName, findings);
            foreach (var def in order.ToArray())
                ValidateCallEdges(def, byName, templateNames,
                    CallerInputNames(def, byName, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 1),
                    findings);
            problems = findings;
            return order;
        }

        // ---- the v2 YAML document model ([T217]) ---------------------------------------------------
        // YamlDotNet's event parser reads a v2 interior (the frontmatter, a fence) and the loop below
        // builds this tiny node model from it, keeping the FILE line of every node so a refusal can name
        // it. The library owns what YAML owns; every rule about what a node MAY BE is ours and lives in
        // the walkers above. v1 never comes through here — the frozen line reader does v1.

        private abstract class YNode { public int Line; }
        /// <summary>A scalar. <see cref="Value"/> is null for an empty plain scalar (a key with nothing
        /// after it). <see cref="Quoted"/> is true for any non-plain style (quoted, literal, folded), which
        /// is what makes "one item, verbatim" distinguishable from "a plain scalar a list key may split".</summary>
        private sealed class YScalar : YNode { public string Value; public bool Quoted; }
        private sealed class YSeq : YNode { public readonly List<YNode> Items = new List<YNode>(); }
        private sealed class YEntry { public string Key; public int Line; public YNode Value; }
        private sealed class YMap : YNode
        {
            public readonly List<YEntry> Entries = new List<YEntry>();
            // The duplicate-key check used to scan all prior entries per key: quadratic, and measured
            // (read three, one probe process per size, Release): 10k keys 0.6s, 30k 2.3s, 100k keys in
            // one hostile ~1.3 MB fence stalled the parse for 27 SECONDS. A set beside the ordered list
            // makes the same refusal O(1) per key; re-measured after the change: 100k keys 307 ms,
            // 300k keys 813 ms — linear, and the stall is gone.
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Read one contiguous block of file lines as ONE YAML document. Returns null for an
        /// empty block. Every YamlException is rethrown as a FormatException carrying the FILE line.</summary>
        private static YNode ReadYamlBlock(List<(int No, string Text)> lines, string ctx)
        {
            if (lines.Count == 0) return null;
            var offset = lines[0].No - 1;   // yaml line 1 == file line lines[0].No
            // The block's LAST line ends in a newline too, and the reconstruction has to say so. On disk it
            // always does: a frontmatter is closed by '---' and a fence by '```', so there is always a line
            // after this one. Joining without the terminator made a block scalar that sits last read its
            // chomping indicator wrong: clip (`|`) and keep (`|+`) both degraded to strip (`|-`), because
            // YAML decides chomping on the line breaks that FOLLOW the scalar's content and there were
            // none. The author wrote one indicator and the reader read another, which is the silent-drop
            // family one byte wide. Everything else here is indifferent to a trailing newline
            // (read nine, F-090).
            var text = string.Join("\n", lines.Select(l => l.Text)) + "\n";
            try
            {
                var parser = new YamlDotNet.Core.Parser(new StringReader(text));
                parser.Consume<StreamStart>();
                if (parser.TryConsume<StreamEnd>(out _)) return null;
                var doc = parser.Consume<DocumentStart>();
                // A '%YAML'/'%TAG' directive, or an explicit leading '---', arrives as a non-implicit
                // DocumentStart and used to be absorbed whole — a version pin the author wrote, silently
                // ignored. Author text this format does not read is refused, never dropped.
                if (!doc.IsImplicit)
                    throw new FormatException($"{ctx}: this block starts with a '%' directive or a '---' document marker (line {(int)doc.Start.Line + offset}). A block here is one plain YAML document; remove it.");
                var node = ReadNode(parser, ctx, offset, depth: 0);
                var docEnd = parser.Consume<DocumentEnd>();
                // The ninth absorption (read two): an explicit '...' end-of-document marker arrives as a
                // non-implicit DocumentEnd and was consumed without a look — the same one-property
                // blindness as the DocumentStart case, one event over. With this, every author-visible
                // property in the event stream is either read into the model or refused by name:
                // DocumentStart.IsImplicit (directives, leading '---'), DocumentEnd.IsImplicit ('...'),
                // Anchor and Tag on all three node kinds, AnchorAlias, and a trailing DocumentStart.
                // Scalar styles are all read (plain, quoted, literal, folded); flow versus block on
                // collections is a spelling, not a meaning; comments are never emitted by this parser.
                if (!docEnd.IsImplicit)
                    throw new FormatException($"{ctx}: there is a '...' end-of-document marker in this block (line {(int)docEnd.Start.Line + offset}). A block here is one plain YAML document with no markers; remove it.");
                // The document ENDED does not mean the block did: '---' starts a SECOND document, and
                // returning here would hand back document one while everything after the marker vanished.
                // The silent-drop family through YAML's own front door.
                if (parser.Current is DocumentStart second)
                    throw new FormatException($"{ctx}: there is a second YAML document in this block (line {(int)second.Start.Line + offset}). One block is one document, so nothing after the '---' would take effect. Remove the '---', or fold those lines into the block above it.");
                parser.Consume<StreamEnd>();
                return node;
            }
            catch (YamlException ex)
            {
                // The library's marks count from the block's first line; the author needs the file's.
                var line = (int)ex.Start.Line + offset;
                var msg = Regex.Replace(ex.Message, @"\(Lin?e?: [^)]*\)( - )?", "").Trim();
                if (ex.InnerException is YamlException inner)
                    msg = Regex.Replace(inner.Message, @"\(Lin?e?: [^)]*\)( - )?", "").Trim();
                throw new FormatException($"{ctx}: the YAML here cannot be read (line {line}): {msg}");
            }
            catch (InvalidOperationException)
            {
                // A YamlDotNet 18.1.0 wart, measured rather than assumed: some malformed inputs (an
                // unclosed '[' whose next line looks like a key) surface as a bare state error with no
                // mark, so there is no line to name. Named honestly instead of invented.
                throw new FormatException($"{ctx}: the YAML here cannot be read (the block starting at line {lines[0].No}). Check it for an unclosed '[' or '{{' and for a quote that never closes.");
            }
        }

        /// <summary>How deep a v2 YAML value may nest. The format itself nests three levels; 32 is more
        /// than 10x that and roughly 45x under the measured cliff: unbounded, this recursion kills the
        /// PROCESS with a stack overflow between 1450 and 1500 levels of `[[[[...]]]]` (measured on this
        /// machine, Release, default stack, one probe process per depth — 1450 parses, 1500 dies). A
        /// crash is not a parse error, so the bound is explicit and the refusal names the line.</summary>
        private const int MaxYamlNesting = 32;

        private static YNode ReadNode(IParser p, string ctx, int off, int depth)
        {
            // Anchor DECLARATIONS and tags ride on every node kind (scalar, sequence, mapping) and the
            // library resolves them away, so `title: &label T` used to parse as plain 'T' with the
            // author's syntax vanishing. Aliases were refused from day one; the declaration side and
            // tags are the same family and get the same answer, on every node kind at once.
            // No alias exclusion here, and none is needed: in YamlDotNet 18.1.0 NodeEvent and AnchorAlias
            // are SIBLING subclasses of ParsingEvent, so an alias never reaches this branch. The exclusion
            // that used to sit here could not fail, which read like a guard and guarded nothing. Aliases
            // are refused by the AnchorAlias branch at the bottom of this method; the type relationship and
            // that refusal are both pinned by test (read nine, F-092).
            if (p.Current is NodeEvent ne)
            {
                var nline = (int)ne.Start.Line + off;
                if (!ne.Anchor.IsEmpty)
                    throw new FormatException($"{ctx}: '&{ne.Anchor}' is a YAML anchor (line {nline}). Anchors and aliases are not part of this format; write the value out where it is used.");
                if (!ne.Tag.IsEmpty)
                    throw new FormatException($"{ctx}: '{ne.Tag}' is a YAML tag (line {nline}). Tags are not part of this format; remove it and write the plain value.");
            }
            if (p.TryConsume<Scalar>(out var s))
                return new YScalar
                {
                    Line = (int)s.Start.Line + off,
                    Quoted = s.Style != ScalarStyle.Plain && s.Style != ScalarStyle.Any,
                    Value = s.Style == ScalarStyle.Plain && s.Value.Length == 0 ? null : s.Value,
                };
            if (p.TryConsume<SequenceStart>(out var q))
            {
                if (depth >= MaxYamlNesting)
                    throw new FormatException($"{ctx}: this value nests more than {MaxYamlNesting} levels deep (line {(int)q.Start.Line + off}). Nothing in this format nests more than a few levels; flatten it.");
                var seq = new YSeq { Line = (int)q.Start.Line + off };
                while (!p.TryConsume<SequenceEnd>(out _)) seq.Items.Add(ReadNode(p, ctx, off, depth + 1));
                return seq;
            }
            if (p.TryConsume<MappingStart>(out var ms))
            {
                if (depth >= MaxYamlNesting)
                    throw new FormatException($"{ctx}: this value nests more than {MaxYamlNesting} levels deep (line {(int)ms.Start.Line + off}). Nothing in this format nests more than a few levels; flatten it.");
                var map = new YMap { Line = (int)ms.Start.Line + off };
                while (!p.TryConsume<MappingEnd>(out _))
                {
                    var keyNode = ReadNode(p, ctx, off, depth + 1);
                    if (keyNode is not YScalar ks || ks.Value == null)
                        throw new FormatException($"{ctx}: a key here has to be a plain name (line {keyNode.Line}).");
                    var valueNode = ReadNode(p, ctx, off, depth + 1);
                    // One key, one value (§1.3). YAML agrees — a mapping's keys are unique — and the
                    // refusal is ours so it can teach instead of only objecting.
                    if (!map.Keys.Add(ks.Value))
                        throw new FormatException($"{ctx}: '{ks.Value}' is set twice (line {ks.Line}). Only the last one would be read, so the earlier one would be dropped. Keep one.");
                    map.Entries.Add(new YEntry { Key = ks.Value, Line = ks.Line, Value = valueNode });
                }
                return map;
            }
            if (p.Current is AnchorAlias alias)
                throw new FormatException($"{ctx}: '*{alias.Value}' is a YAML alias (line {(int)alias.Start.Line + off}). Aliases are not part of this format; write the value out.");
            throw new FormatException($"{ctx}: cannot read the YAML here (line {(int)(p.Current?.Start.Line ?? 0) + off}).");
        }

        /// <summary>A provenance list, kept FAITHFULLY and INJECTIVELY in written form (read two finding
        /// 4, tightened twice under review). Chosen the string form over storing structure because
        /// `Provenance` is a public string map every consumer already reads, and chosen faithful over
        /// pretty because `["a, b"]` and `[a, b]` used to collapse to the same stored string. Items are
        /// NOT trimmed and empties are NOT refused (the one unvalidated bag, by design). The encoding
        /// law and its round-trip property live on <see cref="EncodeProvenanceList"/>.</summary>
        private static string ProvenanceListForm(YSeq seq, string key)
        {
            var items = new List<string>();
            foreach (var item in seq.Items)
            {
                if (item is not YScalar s)
                    throw new FormatException($"provenance: '{key}' holds a structure inside its list (line {item.Line}). A provenance value is a single value or a flat list.");
                items.Add(s.Value ?? "");
            }
            return EncodeProvenanceList(items);
        }

        /// <summary>The injective encoding, and THE PROPERTY IT MUST SATISFY, which the round-trip test
        /// generates hostile items against rather than hand-listing (read four — the two prior versions
        /// of this rule each missed a case a hand-list did not contain): reading the stored form back as
        /// a YAML flow sequence yields exactly the authored items, every one a scalar. So an item stays
        /// BARE only when its bare form cannot reread as structure: it must START with a letter, digit,
        /// '.' or '_' (a leading '-', '?' or ':' is a YAML indicator — `[- a]` measurably fails to parse
        /// at all, `[? a]` and `[: a]` reread as maps), contain only letters, digits, '._- ' and no edge
        /// whitespace. Everything else is double-quoted with '\' and '"' escaped and every control
        /// character written as an escape (n, r, t, or u00XX), because a literal newline inside a
        /// double-quoted scalar FOLDS to a space on reread and the authored item is lost. The one
        /// remaining collapse — a YAML null item versus an empty string, both stored "" — is deliberate
        /// and pinned by C18c: this is the format's one unvalidated bag, its store is a string map, and
        /// encoding a distinction no consumer reads would validate the unvalidated.
        /// Internal for the property test, via InternalsVisibleTo.</summary>
        internal static string EncodeProvenanceList(IEnumerable<string> items)
        {
            var parts = items.Select(v =>
            {
                var safe = v.Length > 0
                    && (char.IsLetterOrDigit(v[0]) || v[0] == '.' || v[0] == '_')
                    && !char.IsWhiteSpace(v[v.Length - 1])
                    && v.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' || ch == ' ');
                if (safe) return v;
                var sb = new System.Text.StringBuilder("\"");
                foreach (var ch in v)
                {
                    if (ch == '\\') sb.Append("\\\\");
                    else if (ch == '"') sb.Append("\\\"");
                    else if (ch == '\n') sb.Append("\\n");
                    else if (ch == '\r') sb.Append("\\r");
                    else if (ch == '\t') sb.Append("\\t");
                    // The OTHER Unicode line terminators YAML knows. Written raw, LS and PS make the
                    // stored form fail to reparse at all (measured: SyntaxErrorException, "wrong
                    // indentation"); NEL is C1 so IsControl already catches it. YAML's own \L and \P
                    // escapes supply the values directly, and YamlDotNet reads them (measured).
                    else if (ch == '\u2028') sb.Append("\\L");
                    else if (ch == '\u2029') sb.Append("\\P");
                    else if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                }
                return sb.Append('"').ToString();
            });
            return "[" + string.Join(", ", parts) + "]";
        }

        private static YMap RequireMap(YNode node, string ctx)
        {
            if (node == null) return new YMap();
            if (node is YMap map) return map;
            throw new FormatException($"{ctx}: this block is 'key: value' lines, and line {node.Line} is not one.");
        }

        /// <summary>A single scalar value, or a refusal naming the key and the line. The empty-plain scalar
        /// comes back null, exactly as the v1 reader returns null for a key with nothing after it.</summary>
        private static string ScalarOf(YEntry e, string ctx)
        {
            // A bare key arrives as a scalar with a null Value (never a null node), so the first branch
            // already returns null for it.
            if (e.Value is YScalar s) return s.Value;
            var shape = e.Value is YSeq ? "a list" : "a block of keys";
            throw new FormatException($"{ctx}: '{e.Key}' takes a single value, not {shape} (line {e.Line}).");
        }

        /// <summary>A list value (§1.3): a real YAML sequence in either spelling, a quoted scalar (ONE item,
        /// verbatim), or a plain scalar split on commas. An empty item is refused, never deleted — the
        /// format has no use for one anywhere it reads a list, so it is an authoring mistake in every
        /// position (F-060).</summary>
        private static string[] ListOf(YEntry e, string what)
        {
            if (e.Value is YSeq seq)
            {
                var items = new string[seq.Items.Count];
                for (int i = 0; i < seq.Items.Count; i++)
                {
                    // A nested collection is refused as what it IS: reporting `[[finance]]` as "an
                    // empty item" told the author about a mistake they did not make (read two).
                    if (seq.Items[i] is not YScalar s)
                        throw new FormatException($"{what} holds {(seq.Items[i] is YSeq ? "a list inside the list" : "a block of keys inside the list")} (line {seq.Items[i].Line}). A list here is a flat list of values.");
                    if ((!s.Quoted && s.Value == null) || (s.Value ?? "").Trim().Length == 0)
                        throw new FormatException($"{what} has an empty item (line {s.Line}). Every entry in a list has to be something; remove the extra comma, or fill the entry in.");
                    // A QUOTED item keeps its content exactly — trimming it was the accepted-and-altered
                    // fault in its last hiding place (read five): `[" finance "]` silently became
                    // `finance` in every list the format reads. A PLAIN item may be trimmed because YAML
                    // itself sheds a plain scalar's edge whitespace. A quoted all-whitespace item is
                    // still refused above, not altered: a tag of spaces is an authoring mistake in every
                    // list this format has, and refusing says so where trimming would lie.
                    items[i] = s.Quoted ? s.Value : s.Value.Trim();
                }
                return items;
            }
            // Not a sequence and not a scalar: a map where a list belongs. A blind cast here surfaced an
            // InvalidCastException wearing internal type names and naming no line, which teaches nothing.
            if (e.Value is not YScalar scalar)
                throw new FormatException($"{what}: '{e.Key}' takes a list, not a block of keys (line {e.Line}).");
            if (scalar.Value == null) return Array.Empty<string>();   // a key with nothing after it
            // The empty-item refusal has to survive onto EVERY path: `tags: ""` used to return one empty
            // tag because the quoted branch sat above this check.
            if (scalar.Value.Trim().Length == 0)
                throw new FormatException($"{what} has an empty item (line {e.Line}). Every entry in a list has to be something; fill it in, or remove the key.");
            if (scalar.Quoted) return new[] { scalar.Value };         // quoted = one item, verbatim
            var parts = scalar.Value.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Any(x => x.Length == 0))
                throw new FormatException($"{what} '{scalar.Value}' has an empty item (line {e.Line}). Every entry in a list has to be something; remove the extra comma, or fill the entry in.");
            return parts;
        }

        /// <summary>A key whose value is a BLOCK of indented child keys: `forEach:`, `call:`, `call with:`
        /// and `provenance:`. One body, because the family was diagnosed one member at a time and drifted:
        /// `forEach: author-value` was reported as EMPTY though it carried a value, and the advice told the
        /// author to add child keys without removing the scalar, which is not valid YAML. The three shapes
        /// an author can write are answered separately here, and the SCALAR one names the value it saw, the
        /// way <see cref="SeqOfMaps"/> already does for a value on a list key. It names that value WITHOUT
        /// rebuilding the author's line from it: a block scalar (`forEach: |-` with the text beneath) is a
        /// scalar too, and quoting it back as `forEach: author-value` invented text nobody wrote and then
        /// taught that only indented child lines are legal, which the accepted flow map
        /// `forEach: { in: [Sales], as: table }` contradicts (read eleven). The diagnosis is the true one:
        /// a scalar arrived where a map belongs, and a map has two spellings.
        /// <paramref name="takes"/> is the per-key sentence saying what belongs beneath it;
        /// <paramref name="bareIsEmpty"/> carries the bare-section ruling (read three) for the keys where a
        /// bare spelling is a legal empty block.</summary>
        private static YMap BlockOf(YEntry e, string ctx, string takes, bool bareIsEmpty)
        {
            if (e.Value is YScalar { Value: null })
            {
                if (bareIsEmpty) return new YMap();
                throw new FormatException($"{ctx}: '{e.Key}:' is empty (line {e.Line}). {takes}");
            }
            if (e.Value is YScalar s)
                throw new FormatException($"{ctx}: '{e.Key}:' takes a map, and it was given the value '{s.Value}' (line {e.Line}). {takes} A map may also be written on one line in flow form, like {{ key: value }}.");
            if (e.Value is not YMap map)
                throw new FormatException($"{ctx}: '{e.Key}:' takes indented 'key: value' lines beneath it, not a list (line {e.Line}). {takes}");
            return map;
        }

        /// <summary>A sequence of flat maps — the gate's inputs:/verify: lists and the frontmatter slots:
        /// list. Produces the same <see cref="FlatMap"/> shape the v1 reader produces, so ToSlot, ToInput
        /// and ToVerify stay one body per concept. A scalar on the list key is refused, not dropped: that
        /// was round 1's `verify: bpa_clean` finding, and the rule is ours to keep.</summary>
        private static List<FlatMap> SeqOfMaps(YEntry e, string ctx, string[] listKeys)
        {
            // A bare section key (`verify:` with nothing under it) arrives as a scalar with a null
            // value, never as a null NODE — an `e.Value == null` branch here was a guard in uniform,
            // unreachable, and the once-accepted empty section was refused with a message about a value
            // the author never wrote (read three). A bare section key IS an empty section, exactly as
            // the pre-swap v2 reader and v1 both read it.
            if (e.Value is YScalar { Value: null }) return new List<FlatMap>();
            if (e.Value is YScalar s)
                throw new FormatException($"{ctx}: '{e.Key}: {s.Value}' puts a value on a list key (line {e.Line}). '{e.Key}:' takes '- ' entries on the lines beneath it, so this value would not be read at all.");
            if (e.Value is not YSeq seq)
                throw new FormatException($"{ctx}: '{e.Key}:' takes '- ' entries on the lines beneath it (line {e.Line}).");
            var items = new List<FlatMap>();
            foreach (var itemNode in seq.Items)
            {
                if (itemNode is not YMap m)
                    throw new FormatException($"{ctx}: an entry under '{e.Key}:' is 'key: value' lines (line {itemNode.Line}), and this one is not.");
                var item = new FlatMap();
                foreach (var me in m.Entries)
                {
                    if (listKeys.Contains(me.Key))
                    {
                        item.Set(me.Key, null, me.Line);
                        item.ListValues[me.Key] = ListOf(me, $"{ctx}: {me.Key}");
                    }
                    else if (me.Value is YScalar sc) item.Set(me.Key, sc.Value, me.Line);
                    else
                    {
                        // The value is a list or a block where a single value belongs. HELD, not thrown:
                        // ToSlot/ToInput/ToVerify judge the KEY first, so an unknown key is named as
                        // unknown whatever shape its value has, and only a key this format really has is
                        // refused for its shape. Same message either way, written here where the shape and
                        // the line are in hand.
                        item.Set(me.Key, null, me.Line);
                        item.ShapeErrors[me.Key] =
                            $"{ctx}: '{me.Key}' takes a single value, not {(me.Value is YSeq ? "a list" : "a block of keys")} (line {me.Line}).";
                    }
                }
                items.Add(item);
            }
            return items;
        }

        // ---- shared list/map readers ---------------------------------------------------------------

        /// <summary>One flat map from a list item, with the LINE each key sat on so a refusal can name it.
        /// The line map is why this does not just return a dictionary. Both readers produce these: the v1
        /// line reader through <see cref="ParseFlatMapList"/>, the v2 model walk through
        /// <see cref="SeqOfMaps"/> — so ToSlot/ToInput/ToVerify stay ONE body per concept rather than two.
        /// A list-valued key (slot `values:`, verify `pinnedShapes:`/`openShapes:`) lands in
        /// <see cref="ListValues"/> when the v2 reader saw a real YAML sequence; the v1 reader always
        /// stores the raw inline string in <see cref="Values"/> and the consumer splits it, frozen.</summary>
        private sealed class FlatMap
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string[]> ListValues = new Dictionary<string, string[]>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Lines = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>A key whose value was the wrong SHAPE (a list or a block where a single value
            /// belongs), with the refusal already written. It is held rather than thrown because the KEY
            /// has to be judged first: `verrify: [a]` used to be refused for its shape, which told the
            /// author to give the typo a single value instead of naming it as a key this format does not
            /// have (read nine, F-091). The v1 reader never fills this, so v1 cannot reach it.</summary>
            public readonly Dictionary<string, string> ShapeErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            public void Set(string k, string v, int line) { Values[k] = v; Lines[k] = line; }
            public int LineOf(string k) => Lines.TryGetValue(k, out var l) ? l : 0;
            /// <summary>Raise the held shape refusal for a key the caller has already accepted as known.</summary>
            public void RequireShape(string k)
            {
                if (ShapeErrors.TryGetValue(k, out var msg)) throw new FormatException(msg);
            }
            /// <summary>The list under <paramref name="k"/>: the v2 pre-parsed sequence when there is one,
            /// else the v1 inline split of the raw string.</summary>
            public string[] List(string k) => ListValues.TryGetValue(k, out var l) ? l : ParseInlineList(Values[k]);
        }

        /// <summary>Parse a v1 YAML list of flat maps — `- key: value` items with deeper-indented
        /// `key: value` continuation lines — exactly as the pinned pre-v2 parser did. The gate's
        /// inputs:/verify: sections AND a §10.3 template's frontmatter slots: block both come through here.
        /// <paramref name="err"/> wraps a message with the caller's context (which step, or the slots block).</summary>
        private static List<FlatMap> ParseFlatMapList(IEnumerable<(int No, string Text)> lines, Func<string, string> err)
        {
            var items = new List<FlatMap>();
            FlatMap item = null;
            void Flush() { if (item != null) items.Add(item); item = null; }

            foreach (var (no, raw) in lines)
            {
                if (IsSkippable(raw)) continue;   // v1 reads a '#' line as an ordinary (inert) property
                var line = raw.Trim();
                if (line.StartsWith("- "))
                {
                    Flush();
                    item = new FlatMap();
                    var (k, v) = SplitKeyValue(line.Substring(2));
                    if (k == null) throw new FormatException(err($"cannot parse list item '{line}'."));
                    item.Set(k, v, no);
                }
                else
                {
                    if (item == null) throw new FormatException(err($"'{line}' is not inside a '- ' list item."));
                    var (k, v) = SplitKeyValue(line);
                    if (k == null) throw new FormatException(err($"cannot parse line '{line}'."));
                    item.Set(k, v, no);   // v1 keeps last-wins, frozen
                }
            }
            Flush();
            return items;
        }

        private static SlotDef ToSlot(FlatMap it, bool strict)
        {
            var s = new SlotDef();
            foreach (var kv in it.Values)
            {
                // The key is judged before its value's shape is. A held shape refusal (F-091) belongs only
                // to a key this format has; an unknown key falls through to the closed-set refusal below.
                if (SlotKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                switch (kv.Key)
                {
                    case "name": s.Name = kv.Value; break;
                    case "question": s.Question = kv.Value; break;
                    case "type": s.Type = RequireEnum(kv.Value, InputTypes, "slot type"); break;
                    case "required": s.Required = RequireEnum(kv.Value, SlotRequireds, "slot required"); break;
                    case "default": s.Default = kv.Value; break;
                    case "example": s.Example = kv.Value; break;
                    case "hint": s.Hint = kv.Value; break;
                    case "values": s.Values = it.List("values"); break;
                    default:
                        // v1: unknown slot keys ignored (forward compat). v2: refused. Section 1.3 states
                        // `with:` is THE one open map, and that claim is false while a slot map swallows
                        // anything; a misspelled slot key means the fill-in silently never appears and the
                        // template renders with a blank the author thought they had filled.
                        if (!strict) break;
                        throw Unknown("slots", "Slot", kv.Key, it.LineOf(kv.Key), SlotKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(s.Name)) throw new FormatException("slots: a slot is missing 'name'.");
            if (!SlotName.IsMatch(s.Name)) throw new FormatException($"slots: slot name '{s.Name}' must be a plain identifier (letters, digits, camelCase) so its {{{{...}}}} reference resolves.");
            return s;
        }

        private static GateInput ToInput(FlatMap it, int stepNum, bool strict)
        {
            var g = new GateInput();
            string rawScope = null;
            foreach (var kv in it.Values)
            {
                if (GateInputKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                switch (kv.Key)
                {
                    case "name": g.Name = kv.Value; break;
                    case "question": g.Question = kv.Value; break;
                    case "type": g.Type = RequireEnum(kv.Value, InputTypes, "input type"); break;
                    case "required": g.Required = RequireEnum(kv.Value, Requireds, "required"); break;
                    case "daxPurity": g.DaxPurity = RequireEnum(kv.Value, new[] { "no-bare-measures" }, "daxPurity"); break;
                    // §2.2a. v2 ONLY: `scope:` did not exist in v1, so in a v1 file it is an unknown key and
                    // unknown keys are preserved-and-ignored. Validating it there would make a v1 file that
                    // parses today start failing, which is the one thing v1 must never do. Held raw and
                    // checked below, so the refusal can name the INPUT and not just the value.
                    case "scope":
                        if (!strict) break;
                        rawScope = kv.Value; break;
                    default:
                        if (!strict) break;
                        throw Unknown($"Step {stepNum} gate input", "Gate input", kv.Key, it.LineOf(kv.Key), GateInputKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(g.Name)) throw new FormatException($"Step {stepNum} gate: an input is missing 'name'.");
            // Nothing honours `scope:` until the runner slice lands the per-iteration answer frames. The VALUE
            // is still checked now: an accepted-but-ignored key is the planned seam, an accepted-but-WRONG key
            // is the [T169] defect wearing the seam's clothes.
            if (rawScope != null && !InputScopes.Contains(rawScope))
                throw new FormatException($"Step {stepNum} gate: input '{g.Name}' scope '{rawScope}' is not one of: {string.Join(" | ", InputScopes)}.");
            g.Scope = rawScope;
            if (g.DaxPurity != null && g.Type != "text")
                throw new FormatException($"Step {stepNum} gate: daxPurity applies only to a text input (got '{g.Type}' for '{g.Name}').");
            return g;
        }

        private static VerifySpec ToVerify(FlatMap it, int stepNum, bool strict)
        {
            var v = new VerifySpec();
            foreach (var kv in it.Values)
            {
                if (GateVerifyKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                switch (kv.Key)
                {
                    case "kind": v.Kind = RequireEnum(kv.Value, VerifyKinds, "verify kind"); break;
                    case "when": v.When = kv.Value; break;
                    case "probe": v.Probe = kv.Value; break;
                    case "scope": v.Scope = RequireEnum(kv.Value, new[] { "object", "model" }, "scope"); break;
                    case "intent": v.Intent = RequireEnum(kv.Value, new[] { "change", "rename", "remove", "restructure" }, "intent"); break;
                    // E1 — the shape ledger: comma/bracket lists of canonical shape ids, validated so a typo surfaces
                    // to the author (a mis-named pinned shape would otherwise silently un-arm the gate).
                    case "pinnedShapes": v.PinnedShapes = RequireShapeIds(it.List("pinnedShapes"), stepNum); break;
                    case "openShapes": v.OpenShapes = RequireShapeIds(it.List("openShapes"), stepNum); break;
                    // E1 ADDENDUM — the input whose RUN answer lists the open shapes (per-run partition; the seed
                    // cannot know it at authoring time). Validated below against this step's or any prior step's inputs.
                    case "openShapesFrom": v.OpenShapesFrom = kv.Value; break;
                    case "openMismatch": v.OpenMismatch = RequireEnum(kv.Value, new[] { "countersign" }, "openMismatch"); break;
                    // expected_values / anchor_coverage: the text input carrying the locked anchor JSON (same binding rules as
                    // openShapesFrom; validated against this or any prior step's inputs below).
                    case "anchors": v.Anchors = kv.Value; break;
                    default:
                        if (!strict) break;
                        throw Unknown($"Step {stepNum} gate verify", "Gate verify", kv.Key, it.LineOf(kv.Key), GateVerifyKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(v.Kind)) throw new FormatException($"Step {stepNum} gate: a verify entry is missing 'kind'.");
            if (v.When != null && !WhenExpr.IsMatch(v.When))
                throw new FormatException($"Step {stepNum} gate: when '{v.When}': only the form 'inputs.<name>.answered' is supported.");
            if ((v.PinnedShapes.Length > 0 || v.OpenShapes.Length > 0 || !string.IsNullOrWhiteSpace(v.OpenShapesFrom)) && v.Kind != "dax_equivalence")
                throw new FormatException($"Step {stepNum} gate: pinnedShapes/openShapes/openShapesFrom apply only to a dax_equivalence verify (got '{v.Kind}').");
            if (!string.IsNullOrWhiteSpace(v.OpenMismatch) && v.Kind != "dax_equivalence")
                throw new FormatException($"Step {stepNum} gate: openMismatch applies only to a dax_equivalence verify (got '{v.Kind}').");
            if (!string.IsNullOrWhiteSpace(v.Anchors) && v.Kind != "expected_values" && v.Kind != "anchor_coverage")
                throw new FormatException($"Step {stepNum} gate: anchors applies only to an expected_values verify or an anchor_coverage verify (got '{v.Kind}').");
            if ((v.Kind == "expected_values" || v.Kind == "anchor_coverage") && string.IsNullOrWhiteSpace(v.Anchors))
                throw new FormatException($"Step {stepNum} gate: an {v.Kind} verify needs an 'anchors: <inputName>' binding to the text input carrying the locked anchor set.");
            return v;
        }

        // ---- scalars -----------------------------------------------------------------------------

        /// <summary>The v2 unknown-key refusal (§1.3): names the key, the scope and the line, and teaches the
        /// closed set rather than only saying no. Every scope's refusal comes through here so none of them
        /// can drift into a bare "unexpected key".</summary>
        private static FormatException Unknown(string ctx, string scopeWord, string key, int line, string[] allowed) =>
            new FormatException($"{ctx}: '{key}' is not a key this format has (line {line}). {scopeWord} keys are: {string.Join(", ", allowed)}. Nothing was saved.");

        /// <summary>The v1 "key: value" split: strip quotes, cut inline " #" comments wherever they fall,
        /// drop anything after a closing quote. Every quirk here is the pinned pre-v2 parser's behaviour,
        /// frozen (F-065, F-066): making v1 smarter is still changing v1. v2 lines never come through here —
        /// YamlDotNet reads them.</summary>
        private static (string key, string value) SplitKeyValue(string line)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) return (null, null);
            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
            {
                var q = value[0];
                var end = value.IndexOf(q, 1);
                if (end > 0) return (key, value.Substring(1, end - 1));   // v1 discards any tail, frozen
            }
            var cut = value.IndexOf(" #", StringComparison.Ordinal);
            if (cut >= 0) value = value.Substring(0, cut).TrimEnd();
            return (key, value.Length == 0 ? null : value);
        }

        /// <summary>The v1 inline list, the pre-v2 body verbatim: split on EVERY comma, quoted or not, strip
        /// quote characters afterwards, delete empty items, keep an unmatched bracket as literal content.
        /// Defects, and v1's defects, frozen (spec §1.2) — do not "improve" this. This was the strict/lenient
        /// two-body helper until [T217]; the v2 body is GONE because a v2 list is a real YAML sequence read
        /// by the library (or a plain scalar comma-split by <see cref="ListOf"/> with its own refusals).</summary>
        private static string[] ParseInlineList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            value = value.Trim();
            if (value.StartsWith("[") && value.EndsWith("]")) value = value.Substring(1, value.Length - 2);
            return value.Split(',').Select(s => s.Trim().Trim('"', '\'')).Where(s => s.Length > 0).ToArray();
        }

        private static string RequireEnum(string value, string[] allowed, string what)
        {
            if (allowed.Contains(value)) return value;
            throw new FormatException($"{what} '{value}' is not one of: {string.Join(" | ", allowed)}.");
        }

        // E1 — a shape id is one of the canonical names the comparator produces: 'grand_total', 'cross', or
        // 'axis:<column>'. Anything else is a typo that would silently mis-classify a shape, so it is refused.
        // Shared with the run-time openShapesFrom answer validation (EquivalenceGate.ResolveOpenShapes) — one grammar.
        internal static bool IsShapeId(string id) =>
            id == "grand_total" || id == "cross"
            || (id != null && id.StartsWith("axis:", StringComparison.Ordinal) && id.Length > "axis:".Length);

        private static string[] RequireShapeIds(string[] ids, int stepNum)
        {
            foreach (var id in ids)
                if (!IsShapeId(id))
                    throw new FormatException($"Step {stepNum} gate: shape id '{id}' is not one of 'grand_total' | 'cross' | 'axis:<column>'.");
            return ids;
        }
    }
}
