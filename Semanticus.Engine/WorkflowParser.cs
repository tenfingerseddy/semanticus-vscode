using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    // ============================================================================================
    // md+YAML workflow parser (docs/pro-mode-spec.md §2). Deliberately a HAND parser: the gate
    // grammar is a tiny fixed subset (flat frontmatter scalars + one inline list; gate = up to two
    // lists of flat maps) and the engine carries no YAML package — a full YAML dependency for this
    // would be dead weight. Unknown KEYS are preserved-and-ignored (forward compat); unknown enum
    // VALUES are errors (a gate that can't run must be surfaced to the author, not silently skipped).
    // No other markdown semantics — the step body is never "interpreted".
    // ============================================================================================

    public static class WorkflowParser
    {
        private static readonly string[] Strictnesses = { "hard", "warn", "off" };
        private static readonly string[] InputTypes = { "verification", "text", "enum", "number", "objectRef" };
        private static readonly string[] Requireds = { "answer-or-decline", "required", "optional" };
        private static readonly string[] VerifyKinds = { "dax_probe", "dax_equivalence", "readiness_rescan", "bpa_clean", "benchmark_delta", "workflow_admissible", "interview_replay", "baseline_captured", "impact_assessment", "baseline_exists", "baseline_unchanged", "tests_replay", "plan_item_staged", "plan_item_applied" };
        private static readonly string[] Kinds = { "workflow", "template" };        // §10.3: absent ⇒ workflow
        private static readonly string[] SlotRequireds = { "required", "optional" }; // slots only ever require|optional (never answer-or-decline — that is a gate-input concept)

        private static readonly Regex StepHeading = new Regex(@"^##\s*Step\s+(\d+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex GateFenceOpen = new Regex(@"^```\s*yaml\s+gate\s*$", RegexOptions.Compiled);
        private static readonly Regex WhenExpr = new Regex(@"^inputs\.([A-Za-z0-9_-]+)\.answered$", RegexOptions.Compiled);
        // A slot NAME (and every {{ref}}) is a plain identifier so the reference is unambiguous. camelCase is the
        // house convention (§10.3); underscores are tolerated so a hand-authored snake_case slot still resolves.
        internal static readonly Regex SlotRef = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);
        private static readonly Regex SlotName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

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
                int i = ParseFrontmatter(lines, def);
                ParseSteps(lines, i, def);
                if (string.IsNullOrWhiteSpace(def.Name)) throw new FormatException("frontmatter is missing 'name'.");
                if (def.Steps.Length == 0) throw new FormatException("no '## Step N:' headings found.");
            }
            catch (Exception ex) { def.Error = ex.Message; }
            return def;
        }

        // ---- frontmatter --------------------------------------------------------------------

        private static int ParseFrontmatter(string[] lines, WorkflowDef def)
        {
            if (lines.Length == 0 || lines[0].TrimEnd() != "---")
                throw new FormatException("file must start with a '---' YAML frontmatter block.");
            int i = 1;
            for (; i < lines.Length && lines[i].TrimEnd() != "---"; i++)
            {
                var (key, value) = SplitKeyValue(lines[i]);
                if (key == null) continue;

                // `slots:` (no inline value) introduces the ONE nested frontmatter structure — a §10.3 template's
                // fill-in list. Reuse the gate's flat-map list parser on the indented block, then resume the flat
                // scan at the first dedented line (or the closing '---'). Everything else stays a flat scalar.
                if (key == "slots" && value == null)
                {
                    var block = new List<string>();
                    int j = i + 1;
                    for (; j < lines.Length && lines[j].TrimEnd() != "---"; j++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[j])) { block.Add(lines[j]); continue; }
                        if (lines[j].Length - lines[j].TrimStart().Length == 0) break;   // dedent → the slots block ended
                        block.Add(lines[j]);
                    }
                    def.Slots = ParseFlatMapList(block, m => $"slots: {m}").Select(ToSlot).ToArray();
                    i = j - 1;   // the for's i++ lands on the dedented key (re-scanned) or the '---' (loop ends)
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
                    // unknown keys: preserved (forward compat + provenance). A distilled workflow's
                    // `derived_from:` etc. land here for check_workflow to surface (value kept verbatim).
                    default: if (value != null) def.Provenance[key] = value; break;
                }
            }
            if (i >= lines.Length) throw new FormatException("frontmatter '---' block is never closed.");
            return i + 1;
        }

        // ---- steps + gate fences --------------------------------------------------------------

        private static void ParseSteps(string[] lines, int start, WorkflowDef def)
        {
            var steps = new List<WorkflowStep>();
            List<string> body = null; WorkflowStep step = null; int lastNum = 0;

            void Flush()
            {
                if (step == null) return;
                var (instructions, gate, ops) = ExtractGate(body, step.Number);
                step.Instructions = instructions; step.Gate = gate; step.Ops = ops;
                steps.Add(step);
            }

            for (int i = start; i < lines.Length; i++)
            {
                var m = StepHeading.Match(lines[i]);
                if (m.Success)
                {
                    Flush();
                    var num = int.Parse(m.Groups[1].Value);
                    if (num != lastNum + 1) throw new FormatException($"step numbering broken at '## Step {num}:' (expected {lastNum + 1}).");
                    lastNum = num;
                    step = new WorkflowStep { Id = "step-" + num, Number = num, Title = m.Groups[2].Value };
                    body = new List<string>();
                }
                else body?.Add(lines[i]);   // preamble before Step 1 is not part of any step
            }
            Flush();
            def.Steps = steps.ToArray();
        }

        private static (string instructions, GateSpec gate, string[] ops) ExtractGate(List<string> body, int stepNum)
        {
            var text = new List<string>(); GateSpec gate = null; string[] ops = null; var sawFence = false;
            for (int i = 0; i < body.Count; i++)
            {
                if (GateFenceOpen.IsMatch(body[i]))
                {
                    if (sawFence) throw new FormatException($"Step {stepNum} has more than one 'yaml gate' block (max one per step).");
                    sawFence = true;
                    var yaml = new List<string>(); int j = i + 1;
                    for (; j < body.Count && body[j].TrimEnd() != "```"; j++) yaml.Add(body[j]);
                    if (j >= body.Count) throw new FormatException($"Step {stepNum}: 'yaml gate' fence is never closed.");
                    (gate, ops) = ParseGate(yaml, stepNum);
                    // an ops-only fence declares the action chain without gating anything — no GateSpec,
                    // so the step stays outside HasEnforcedGate and submit skips gate evaluation entirely
                    if (gate.Strictness == null && gate.Inputs.Length == 0 && gate.Verify.Length == 0) gate = null;
                    i = j;                  // the fence is excluded from the instruction text
                }
                else text.Add(body[i]);
            }
            return (string.Join("\n", text).Trim(), gate, ops ?? Array.Empty<string>());
        }

        // ---- the gate YAML subset ---------------------------------------------------------------
        // Grammar: top-level `strictness: x` scalar + `inputs:`/`verify:` lists; a list item is
        // `- key: value` with deeper-indented `key: value` continuation lines. That is ALL §2 uses.

        private static (GateSpec gate, string[] ops) ParseGate(List<string> yaml, int stepNum)
        {
            var gate = new GateSpec(); string[] ops = null;
            var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> current = null;                 // the raw indented lines of the section being collected (null = under a scalar/unknown key)

            foreach (var raw in yaml)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
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
                        case "verify": current = sections[key] = new List<string>(); break;
                        default: break;                  // unknown keys: preserved-and-ignored
                    }
                    continue;
                }
                current?.Add(raw);                       // content under an unknown/scalar key is ignored (current == null)
            }

            gate.Inputs = (sections.TryGetValue("inputs", out var ins) ? ParseFlatMapList(ins, m => $"Step {stepNum} gate: {m}") : new List<Dictionary<string, string>>())
                .Select(it => ToInput(it, stepNum)).ToArray();
            gate.Verify = (sections.TryGetValue("verify", out var vs) ? ParseFlatMapList(vs, m => $"Step {stepNum} gate: {m}") : new List<Dictionary<string, string>>())
                .Select(it => ToVerify(it, stepNum)).ToArray();
            return (gate, ops);
        }

        /// <summary>Parse a YAML list of flat maps — `- key: value` items with deeper-indented `key: value`
        /// continuation lines. The ONE shared list shape in the grammar: the gate's inputs:/verify: sections AND a
        /// §10.3 template's frontmatter slots: block both go through here, so the two can never drift.
        /// <paramref name="err"/> wraps a message with the caller's context (which step, or the slots block).</summary>
        private static List<Dictionary<string, string>> ParseFlatMapList(IEnumerable<string> lines, Func<string, string> err)
        {
            var items = new List<Dictionary<string, string>>();
            Dictionary<string, string> item = null;
            void Flush() { if (item != null) items.Add(item); item = null; }

            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var line = raw.Trim();
                if (line.StartsWith("- "))
                {
                    Flush();
                    item = new Dictionary<string, string>(StringComparer.Ordinal);
                    var (k, v) = SplitKeyValue(line.Substring(2));
                    if (k == null) throw new FormatException(err($"cannot parse list item '{line}'."));
                    item[k] = v;
                }
                else
                {
                    if (item == null) throw new FormatException(err($"'{line}' is not inside a '- ' list item."));
                    var (k, v) = SplitKeyValue(line);
                    if (k == null) throw new FormatException(err($"cannot parse line '{line}'."));
                    item[k] = v;
                }
            }
            Flush();
            return items;
        }

        private static SlotDef ToSlot(Dictionary<string, string> it)
        {
            var s = new SlotDef();
            foreach (var kv in it)
                switch (kv.Key)
                {
                    case "name": s.Name = kv.Value; break;
                    case "question": s.Question = kv.Value; break;
                    case "type": s.Type = RequireEnum(kv.Value, InputTypes, "slot type"); break;
                    case "required": s.Required = RequireEnum(kv.Value, SlotRequireds, "slot required"); break;
                    case "default": s.Default = kv.Value; break;
                    case "example": s.Example = kv.Value; break;
                    case "hint": s.Hint = kv.Value; break;
                    case "values": s.Values = ParseInlineList(kv.Value); break;
                    default: break;                      // unknown slot keys: ignored (forward compat)
                }
            if (string.IsNullOrWhiteSpace(s.Name)) throw new FormatException("slots: a slot is missing 'name'.");
            if (!SlotName.IsMatch(s.Name)) throw new FormatException($"slots: slot name '{s.Name}' must be a plain identifier (letters, digits, camelCase) so its {{{{...}}}} reference resolves.");
            return s;
        }

        private static GateInput ToInput(Dictionary<string, string> it, int stepNum)
        {
            var g = new GateInput();
            foreach (var kv in it)
                switch (kv.Key)
                {
                    case "name": g.Name = kv.Value; break;
                    case "question": g.Question = kv.Value; break;
                    case "type": g.Type = RequireEnum(kv.Value, InputTypes, "input type"); break;
                    case "required": g.Required = RequireEnum(kv.Value, Requireds, "required"); break;
                    default: break;
                }
            if (string.IsNullOrWhiteSpace(g.Name)) throw new FormatException($"Step {stepNum} gate: an input is missing 'name'.");
            return g;
        }

        private static VerifySpec ToVerify(Dictionary<string, string> it, int stepNum)
        {
            var v = new VerifySpec();
            foreach (var kv in it)
                switch (kv.Key)
                {
                    case "kind": v.Kind = RequireEnum(kv.Value, VerifyKinds, "verify kind"); break;
                    case "when": v.When = kv.Value; break;
                    case "probe": v.Probe = kv.Value; break;
                    case "scope": v.Scope = RequireEnum(kv.Value, new[] { "object", "model" }, "scope"); break;
                    case "intent": v.Intent = RequireEnum(kv.Value, new[] { "change", "rename", "remove", "restructure" }, "intent"); break;
                    default: break;
                }
            if (string.IsNullOrWhiteSpace(v.Kind)) throw new FormatException($"Step {stepNum} gate: a verify entry is missing 'kind'.");
            if (v.When != null && !WhenExpr.IsMatch(v.When))
                throw new FormatException($"Step {stepNum} gate: when '{v.When}' — only the form 'inputs.<name>.answered' is supported.");
            return v;
        }

        // ---- scalars -----------------------------------------------------------------------------

        /// <summary>Split "key: value", strip quotes, cut inline " #" comments outside quotes.</summary>
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
                if (end > 0) return (key, value.Substring(1, end - 1));
            }
            var hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) value = value.Substring(0, hash).TrimEnd();
            return (key, value.Length == 0 ? null : value);
        }

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
    }
}
