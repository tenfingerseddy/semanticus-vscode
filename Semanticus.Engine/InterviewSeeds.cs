using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    // Interview SEED candidates — the two deterministic sources that pre-fill the Model Interview without any
    // inference (golden rule 1): (A) the model's own VERIFIED ANSWERS (files beside the TMDL, parsed read-only
    // and fail-soft — the format is observed, not documented) and (B) the built-in HARD-QUESTION PACK
    // (docs/interview-hard-pack.json — model-agnostic templates distilled from the benchmark's trap families,
    // bound only to objects that actually exist in the open model). Neither source ever fabricates an oracle:
    // a candidate carries the QUESTION (and, for the pack, a canonical reference query); the trusted answer is
    // confirmed by a human before add_interview_question persists it. The op lives in LocalEngine.Interview.cs.

    // ---- result shapes -----------------------------------------------------------------------------------

    public sealed class InterviewSeedCandidate
    {
        public string Source { get; set; }                                // "verified-answer" | "hard-pack"
        public string Id { get; set; }                                    // VA: the definition folder name; pack: template id
        public string Family { get; set; }                                // pack: the trap family (null for VA)
        public string Question { get; set; }                              // the primary natural-language question
        public string[] AltPhrasings { get; set; } = Array.Empty<string>(); // VA: further trigger phrasings (paraphrase material)
        public string[] Targets { get; set; } = Array.Empty<string>();    // object refs ("measure:T/N", "column:T/N", "table:T")
        public string Query { get; set; }                                 // pack: the bound canonical EVALUATE (no oracle attached!)
        public string SuggestedTier { get; set; }                         // "value"
        public string Note { get; set; }                                  // per-candidate context (the trap, or the VA label)
    }

    public sealed class InterviewSeedSkip
    {
        public string Source { get; set; }
        public string Id { get; set; }
        public string Family { get; set; }
        public string Reason { get; set; }                                // honest: exactly what could not bind / parse
    }

    public sealed class InterviewSeedResult
    {
        public InterviewSeedCandidate[] Candidates { get; set; } = Array.Empty<InterviewSeedCandidate>();
        public InterviewSeedSkip[] Skipped { get; set; } = Array.Empty<InterviewSeedSkip>();
        public int VerifiedAnswersFound { get; set; }                     // definition.json files seen (usable or not)
        public int HardPackTemplates { get; set; }                        // templates in the built-in pack
        public string Note { get; set; }
    }

    // ---- deploy-gate advisory (item: the interview leg on deploy_gate — informs, NEVER blocks) ------------

    /// <summary>The ADVISORY interview replay attached to a deploy gate. It never contributes to
    /// <c>DeployGate.Pass</c>/<c>Blockers</c> — its whole job is the per-question outcome deltas vs the last
    /// recorded outcomes, so a deploy sees "these user questions changed since you last checked".</summary>
    public sealed class InterviewGateAdvisory
    {
        public int Questions { get; set; }                                // saved project-pack questions found
        public int Replayed { get; set; }                                 // value/paraphrase questions actually replayed
        public int Right { get; set; }
        public int Wrong { get; set; }                                    // SilentlyWrong now — the number that matters
        public int Unverified { get; set; }
        public int NotReplayable { get; set; }                            // refusal-tier: graded in chat, not here
        public int NeverAsked { get; set; }                               // first-ever gradings — NOT deltas (a change needs a recorded before)
        public InterviewOutcomeDelta[] Changes { get; set; } = Array.Empty<InterviewOutcomeDelta>();
        public string Note { get; set; }
    }

    public sealed class InterviewOutcomeDelta
    {
        public string QuestionId { get; set; }
        public string Question { get; set; }
        public string Before { get; set; }                                // the last RECORDED outcome (never null — never-asked questions land in NeverAsked, not here)
        public string After { get; set; }
        public string Detail { get; set; }                                // the fresh run's evidence line
    }

    // ---- (A) verified answers → seeds ---------------------------------------------------------------------

    /// <summary>
    /// Parses <c>VerifiedAnswers/definitions/&lt;guid&gt;/definition.json</c> beside the model into seed
    /// candidates. The on-disk schema is OBSERVED, not documented (the same reason PrepForAiReader only counted
    /// them until now), so the parse is deliberately structural: trigger/question phrasings are any strings under
    /// properties whose name contains trigger|question|prompt|phrase|utterance, and target refs are the PBIR
    /// field-ref shape ({ "Measure"/"Column": { "Expression": { "SourceRef": { "Entity": T } }, "Property": P } },
    /// case-insensitive) plus bare "queryRef" strings. Anything that doesn't yield a question is SKIPPED with the
    /// reason — counted honestly, never guessed at.
    /// </summary>
    internal static class VerifiedAnswerSeeds
    {
        private const int MaxFileBytes = 1024 * 1024;                     // a definition is small; a megabyte says "not what we think it is"
        private const int MaxDepth = 48;

        public static (List<InterviewSeedCandidate> usable, List<InterviewSeedSkip> skipped, int found) Parse(string modelFolder)
        {
            var usable = new List<InterviewSeedCandidate>();
            var skipped = new List<InterviewSeedSkip>();
            int found = 0;
            var defs = modelFolder == null ? null : Path.Combine(modelFolder, "VerifiedAnswers", "definitions");
            if (defs == null || !Directory.Exists(defs)) return (usable, skipped, 0);

            foreach (var dir in Directory.EnumerateDirectories(defs).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, "definition.json");
                if (!File.Exists(file)) continue;
                found++;
                var id = Path.GetFileName(dir);
                void Skip(string reason) => skipped.Add(new InterviewSeedSkip { Source = "verified-answer", Id = id, Reason = reason });

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxFileBytes) { Skip($"definition.json is {info.Length:N0} bytes — far larger than a verified-answer definition; not parsed."); continue; }

                    using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { Skip("definition.json is not a JSON object."); continue; }

                    var questions = new List<string>();
                    var targets = new List<string>();
                    Walk(root, null, 0, questions, targets);
                    questions = Dedup(questions);

                    if (questions.Count == 0)
                    {
                        Skip("no question text found — the definition carries no trigger/question phrasing this parser recognizes (the format is observed, not documented; the raw file is beside the model).");
                        continue;
                    }

                    var name = FirstString(root, "name", "displayName", "title");
                    usable.Add(new InterviewSeedCandidate
                    {
                        Source = "verified-answer",
                        Id = id,
                        Question = questions[0],
                        AltPhrasings = questions.Skip(1).Take(14).ToArray(),   // the platform caps triggers at 15
                        Targets = Dedup(targets).ToArray(),
                        SuggestedTier = "value",
                        Note = name != null ? $"from the verified answer “{name}”" : "from a verified answer",
                    });
                }
                catch (Exception ex)
                {
                    // Fail-soft by contract: one malformed definition never hides the others.
                    Skip("definition.json could not be parsed: " + ex.Message);
                }
            }
            return (usable, skipped, found);
        }

        private static List<string> Dedup(List<string> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return items.Where(s => seen.Add(s)).ToList();
        }

        private static string FirstString(JsonElement obj, params string[] names)
        {
            foreach (var p in obj.EnumerateObject())
                foreach (var n in names)
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
            return null;
        }

        private static bool IsQuestionKey(string key) =>
            key != null && (key.Contains("trigger", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("question", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("phrase", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("utterance", StringComparison.OrdinalIgnoreCase));

        private static void Walk(JsonElement el, string key, int depth, List<string> questions, List<string> targets)
        {
            if (depth > MaxDepth) return;
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TryFieldRef(el, key, out var fieldRef)) { targets.Add(fieldRef); return; }
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            var s = p.Value.GetString()?.Trim();
                            if (string.IsNullOrEmpty(s)) continue;
                            // A string under a trigger-ish key — or the "text"-ish member of an object that sits
                            // under a trigger-ish key (e.g. triggers: [{ text: "..." }]).
                            if (IsQuestionKey(p.Name) || (IsQuestionKey(key) && IsTextKey(p.Name))) questions.Add(s);
                            else if (string.Equals(p.Name, "queryRef", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(p.Name, "nativeQueryRef", StringComparison.OrdinalIgnoreCase))
                                targets.Add("field:" + s);
                        }
                        else Walk(p.Value, p.Name, depth + 1, questions, targets);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && IsQuestionKey(key))
                        {
                            var s = item.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(s)) questions.Add(s);
                        }
                        else Walk(item, key, depth + 1, questions, targets);
                    }
                    break;
            }
        }

        private static bool IsTextKey(string key) =>
            string.Equals(key, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "value", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "question", StringComparison.OrdinalIgnoreCase);

        // The PBIR field-ref shape (ReportDefinitionReader's contract, matched case-insensitively because the
        // observed VA snapshots mix casings): parentKey ∈ {Measure, Column} and the object carries Property +
        // an Expression whose (possibly nested) SourceRef names an Entity.
        private static bool TryFieldRef(JsonElement obj, string parentKey, out string @ref)
        {
            @ref = null;
            var kind = string.Equals(parentKey, "Measure", StringComparison.OrdinalIgnoreCase) ? "measure"
                     : string.Equals(parentKey, "Column", StringComparison.OrdinalIgnoreCase) ? "column" : null;
            if (kind == null) return false;
            string property = null; JsonElement expr = default; bool hasExpr = false;
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, "Property", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) property = p.Value.GetString();
                else if (string.Equals(p.Name, "Expression", StringComparison.OrdinalIgnoreCase)) { expr = p.Value; hasExpr = true; }
            }
            if (property == null || !hasExpr) return false;
            if (!TryFindEntity(expr, 0, out var entity)) return false;
            @ref = $"{kind}:{entity}/{property}";
            return true;
        }

        private static bool TryFindEntity(JsonElement expr, int depth, out string entity)
        {
            entity = null;
            if (depth > 8 || expr.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in expr.EnumerateObject())
            {
                if (string.Equals(p.Name, "SourceRef", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var q in p.Value.EnumerateObject())
                        if (string.Equals(q.Name, "Entity", StringComparison.OrdinalIgnoreCase) && q.Value.ValueKind == JsonValueKind.String)
                        { entity = q.Value.GetString(); return !string.IsNullOrEmpty(entity); }
                }
                else if (p.Value.ValueKind == JsonValueKind.Object && TryFindEntity(p.Value, depth + 1, out entity)) return true;
            }
            return false;
        }
    }

    // ---- (B) the built-in hard-question pack --------------------------------------------------------------

    /// <summary>A metadata-only snapshot of the open model's SHAPES, so the binder is a pure function the tests
    /// exercise offline (the same kernel/ops split the interview store uses). Built inside Session.ReadAsync.</summary>
    public sealed class PackShape
    {
        public sealed class Meas { public string Table; public string Name; public bool Hidden; }
        public sealed class Col { public string Table; public string Name; public string Kind; public bool Hidden; public bool Key; }   // Kind: "date" | "text" | "number" | "other"
        public sealed class Rel { public string FromTable; public string FromColumn; public string ToTable; public string ToColumn; public bool Active; }
        public List<Meas> Measures { get; } = new List<Meas>();
        public List<Col> Columns { get; } = new List<Col>();
        public List<Rel> Relationships { get; } = new List<Rel>();
        public HashSet<string> DateTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // marked DataCategory=Time
    }

    /// <summary>
    /// The built-in hard-question pack: ~a dozen MODEL-AGNOSTIC templates (docs/interview-hard-pack.json,
    /// embedded like the fix map so binary and docs can't drift) distilled from the benchmark's trap families —
    /// the question patterns an AI answers *confidently wrong* most often. Binding is deterministic and honest:
    /// a template instantiates ONLY when every shape it needs exists in the model (each choice disclosed), and
    /// anything unbindable is reported as skipped with the exact missing shape. NO gold values ship with the
    /// pack and none are fabricated at binding — the canonical query's number is confirmed by a human before it
    /// becomes an oracle (the ProBench lesson: self-verification isn't verification).
    /// </summary>
    internal static class HardQuestionPack
    {
        public sealed class Template
        {
            public string Id { get; set; }
            public string Family { get; set; }
            public string[] Needs { get; set; } = Array.Empty<string>();
            public string Question { get; set; }
            public string Trap { get; set; }
            public string Query { get; set; }
        }
        private sealed class PackFile { public Template[] Templates { get; set; } }

        private static Template[] _templates;
        private static readonly object Gate = new object();

        public static Template[] Templates()
        {
            if (_templates != null) return _templates;
            lock (Gate)
            {
                if (_templates != null) return _templates;
                var asm = typeof(HardQuestionPack).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("interview-hard-pack.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("interview-hard-pack.json is not embedded in Semanticus.Engine — the built-in hard-question pack is missing from the build.");
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<PackFile>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _templates = doc?.Templates ?? Array.Empty<Template>();
                return _templates;
            }
        }

        // DAX ref builders (escape the name grammar, not just hope).
        private static string T(string table) => "'" + (table ?? "").Replace("'", "''") + "'";
        private static string CRef(string table, string col) => T(table) + "[" + (col ?? "").Replace("]", "]]") + "]";
        private static string MRef(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";
        private static string Lit(string s) => (s ?? "").Replace("\"", "\"\"");   // names inside "..." string literals

        /// <summary>Bind every template against the shape. Deterministic choices, each disclosed in the
        /// candidate's Targets; every miss lands in skips with the exact missing shape named.</summary>
        public static (List<InterviewSeedCandidate> candidates, List<InterviewSeedSkip> skips) Bind(PackShape shape, string measureArg)
        {
            var candidates = new List<InterviewSeedCandidate>();
            var skips = new List<InterviewSeedSkip>();
            var templates = Templates();

            // ---- resolve the shared bindings ONCE (stable order ⇒ stable candidates run-to-run) ----
            PackShape.Meas measure = null;
            string measureMiss = null;
            if (!string.IsNullOrWhiteSpace(measureArg))
            {
                var hits = shape.Measures.Where(m => string.Equals(m.Name, measureArg.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0)
                    throw new InvalidOperationException($"Measure '{measureArg.Trim()}' was not found in the model — list_measures shows what exists, or omit `measure` to bind the first visible one.");
                measure = hits[0];
            }
            else
            {
                measure = shape.Measures.Where(m => !m.Hidden).OrderBy(m => m.Table, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (measure == null) measureMiss = "the model has no visible measure to target (create one, or pass measure= to bind a hidden one)";
            }
            var fact = measure?.Table;

            // Marked date table + its date column (key date column first — the column mark_date_table anchors).
            var dateTable = shape.DateTables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(t => shape.Columns.Any(c => c.Table.Equals(t, StringComparison.OrdinalIgnoreCase) && c.Kind == "date"));
            var dateCol = dateTable == null ? null : shape.Columns
                .Where(c => c.Table.Equals(dateTable, StringComparison.OrdinalIgnoreCase) && c.Kind == "date")
                .OrderByDescending(c => c.Key).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var dateMiss = dateTable == null ? "no table is marked as a date table (mark_date_table fixes that)" : null;
            // Time-intelligence templates additionally need the fact table to actually reach those dates.
            var factToDate = fact != null && dateTable != null && shape.Relationships.Any(r =>
                r.Active && r.FromTable.Equals(fact, StringComparison.OrdinalIgnoreCase) && r.ToTable.Equals(dateTable, StringComparison.OrdinalIgnoreCase));
            var factToDateMiss = dateMiss ?? (fact == null ? measureMiss
                : factToDate ? null : $"no active relationship from '{fact}' (the measure's table) to the marked date table '{dateTable}'");

            // A textual attribute ON the date table (the YTD-under-a-text-slice trap needs one).
            var dateAttr = dateTable == null ? null : shape.Columns
                .Where(c => c.Table.Equals(dateTable, StringComparison.OrdinalIgnoreCase) && c.Kind == "text" && !c.Hidden && !c.Key)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            // A slicer dimension: an active relationship from the fact table to a non-date table, labelled by
            // that table's first visible text column (prefer a label over the key it joins on).
            PackShape.Col dim = null;
            if (fact != null)
                foreach (var rel in shape.Relationships
                    .Where(r => r.Active && r.FromTable.Equals(fact, StringComparison.OrdinalIgnoreCase)
                             && (dateTable == null || !r.ToTable.Equals(dateTable, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(r => r.ToTable, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ToColumn, StringComparer.OrdinalIgnoreCase))
                {
                    dim = shape.Columns
                        .Where(c => c.Table.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase) && c.Kind == "text" && !c.Hidden && !c.Name.Equals(rel.ToColumn, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                        ?? shape.Columns.FirstOrDefault(c => c.Table.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(rel.ToColumn, StringComparison.OrdinalIgnoreCase) && c.Kind == "text");
                    if (dim != null) break;
                }

            // An entity key: the fact-side column of an active relationship to a non-date table — DISTINCTCOUNT
            // of it approximates "distinct <entity>". The entity label is the one-side table's name.
            var entityRel = fact == null ? null : shape.Relationships
                .Where(r => r.Active && r.FromTable.Equals(fact, StringComparison.OrdinalIgnoreCase)
                         && (dateTable == null || !r.ToTable.Equals(dateTable, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.ToTable, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.FromColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            // Two plain numeric columns on the fact table (weighted average) — keys and relationship columns are
            // ids, so they are excluded: averaging an id would be a nonsense question, not a hard one.
            var relCols = new HashSet<string>(shape.Relationships.SelectMany(r => new[] { r.FromTable + "|" + r.FromColumn, r.ToTable + "|" + r.ToColumn }), StringComparer.OrdinalIgnoreCase);
            var numerics = fact == null ? new List<PackShape.Col>() : shape.Columns
                .Where(c => c.Table.Equals(fact, StringComparison.OrdinalIgnoreCase) && c.Kind == "number" && !c.Hidden && !c.Key && !relCols.Contains(c.Table + "|" + c.Name))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(2).ToList();

            // An inactive relationship leaving the fact table (prefer one into the marked date table — the
            // classic order-date/ship-date trap).
            var inactiveRel = fact == null ? null : shape.Relationships
                .Where(r => !r.Active && r.FromTable.Equals(fact, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => dateTable != null && r.ToTable.Equals(dateTable, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.ToTable, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.FromColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            // ---- per-need availability + the honest miss reasons ----
            string Miss(string need) => need switch
            {
                "measure" => measure == null ? measureMiss : null,
                "dimension" => measure == null ? measureMiss
                             : dim == null ? $"no related dimension with a visible text column reachable from '{fact}' (the measure's table)" : null,
                "dateTable" => factToDateMiss,
                "dateAttr" => factToDateMiss ?? (dateAttr == null ? $"the marked date table '{dateTable}' has no visible text attribute column (e.g. a day-of-week or month name)" : null),
                "entityKey" => measure == null ? measureMiss
                             : entityRel == null ? $"no active relationship from '{fact}' to an entity table (nothing to distinct-count)" : null,
                "twoNumeric" => measure == null ? measureMiss
                              : numerics.Count < 2 ? $"'{fact}' has {numerics.Count} visible plain numeric column(s) — a weighted average needs two (a value and a weight)" : null,
                "inactiveRel" => measure == null ? measureMiss
                               : inactiveRel == null ? $"no inactive relationship from '{fact}' (nothing for USERELATIONSHIP to activate)" : null,
                _ => $"template declares an unknown shape requirement '{need}' — the pack file and the binder have drifted",
            };

            // ---- token map (query context escapes names for string literals; question context stays raw) ----
            string entity = entityRel?.ToTable;
            var raw = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["{measure}"] = measure?.Name,
                ["{measureRef}"] = measure == null ? null : MRef(measure.Name),
                ["{factTable}"] = fact == null ? null : T(fact),
                ["{dim}"] = dim?.Name,
                ["{dimCol}"] = dim == null ? null : CRef(dim.Table, dim.Name),
                ["{dateTable}"] = dateTable,
                ["{dateCol}"] = dateCol == null ? null : CRef(dateCol.Table, dateCol.Name),
                ["{dateAttr}"] = dateAttr?.Name,
                ["{dateAttrCol}"] = dateAttr == null ? null : CRef(dateAttr.Table, dateAttr.Name),
                ["{entity}"] = entity,
                ["{entityKeyCol}"] = entityRel == null ? null : CRef(entityRel.FromTable, entityRel.FromColumn),
                ["{num1}"] = numerics.Count > 0 ? numerics[0].Name : null,
                ["{num1Col}"] = numerics.Count > 0 ? CRef(numerics[0].Table, numerics[0].Name) : null,
                ["{num2}"] = numerics.Count > 1 ? numerics[1].Name : null,
                ["{num2Col}"] = numerics.Count > 1 ? CRef(numerics[1].Table, numerics[1].Name) : null,
                ["{inactiveFrom}"] = inactiveRel?.FromColumn,
                ["{inactiveFromCol}"] = inactiveRel == null ? null : CRef(inactiveRel.FromTable, inactiveRel.FromColumn),
                ["{inactiveToCol}"] = inactiveRel == null ? null : CRef(inactiveRel.ToTable, inactiveRel.ToColumn),
                ["{inactiveToTable}"] = inactiveRel?.ToTable,
            };
            // Name tokens that can land inside a DAX string literal get their quotes doubled in QUERY context.
            var literalNameTokens = new[] { "{measure}", "{dim}", "{dateAttr}", "{entity}", "{num1}", "{num2}", "{inactiveFrom}", "{dateTable}", "{inactiveToTable}" };

            string Render(string template, bool queryContext)
            {
                var s = template;
                foreach (var kv in raw)
                {
                    if (kv.Value == null) continue;
                    var v = queryContext && literalNameTokens.Contains(kv.Key) ? Lit(kv.Value) : kv.Value;
                    s = s.Replace(kv.Key, v);
                }
                return s;
            }

            foreach (var t in templates)
            {
                var miss = t.Needs.Select(Miss).FirstOrDefault(m => m != null);
                if (miss != null)
                {
                    skips.Add(new InterviewSeedSkip { Source = "hard-pack", Id = t.Id, Family = t.Family, Reason = miss });
                    continue;
                }
                var question = Render(t.Question, queryContext: false);
                var query = Render(t.Query, queryContext: true);
                if (question.Contains('{') || query.Contains('{'))
                {
                    // A leftover token means the template asks for a shape its `needs` didn't declare — surfaced
                    // loudly rather than emitting a question that references nothing.
                    skips.Add(new InterviewSeedSkip { Source = "hard-pack", Id = t.Id, Family = t.Family, Reason = "template placeholders did not fully bind (pack-file defect: a token is missing from `needs`) — not emitted" });
                    continue;
                }

                var targets = new List<string> { $"measure:{measure.Table}/{measure.Name}" };
                void Target(string kind, PackShape.Col c) { if (c != null) targets.Add($"{kind}:{c.Table}/{c.Name}"); }
                if (t.Needs.Contains("dimension")) Target("column", dim);
                if (t.Needs.Contains("dateTable") || t.Needs.Contains("dateAttr")) targets.Add($"column:{dateCol.Table}/{dateCol.Name}");
                if (t.Needs.Contains("dateAttr")) Target("column", dateAttr);
                if (t.Needs.Contains("entityKey")) targets.Add($"column:{entityRel.FromTable}/{entityRel.FromColumn}");
                if (t.Needs.Contains("twoNumeric")) { Target("column", numerics[0]); Target("column", numerics[1]); }
                if (t.Needs.Contains("inactiveRel")) { targets.Add($"column:{inactiveRel.FromTable}/{inactiveRel.FromColumn}"); targets.Add($"column:{inactiveRel.ToTable}/{inactiveRel.ToColumn}"); }

                candidates.Add(new InterviewSeedCandidate
                {
                    Source = "hard-pack",
                    Id = t.Id,
                    Family = t.Family,
                    Question = question,
                    Targets = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Query = query,
                    SuggestedTier = "value",
                    Note = t.Trap,
                });
            }
            return (candidates, skips);
        }
    }
}
