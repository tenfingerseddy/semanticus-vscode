// The AI data schema family (T268, M4 point 5: what Copilot sees; Kane, 26 September: in this release).
//
// WHAT IT DECIDES. For every visible table, column and measure, whether Copilot and data agents should see it when
// they answer questions about the model (include) or not (exclude). Microsoft's guidance for the schema is to keep
// the fields people ask about and remove what could confuse Copilot; Copilot still follows relationships whatever
// the schema says, so a join key can be left out without breaking an answer. This is Copilot and data agents
// framing only: nothing here is about the retired Q&A feature.
//
// WHAT JEV SEES. Names, types and facts read from the model's STRUCTURE: the object's kind and name, its table's
// name, a column's data type and whether it is calculated, a key, an end of a relationship, the order of another
// column, and how many measures use it; the names of what a measure's formula uses (never the formula); a table's
// kind and how many visible fields and relationships it has; and the names and types of the other fields on its
// table. Never a value, a row count or a sample.
//
// WHAT THE ENGINE CHECKS. Every pick comes from the two options. Then the schema's own rule, the one
// AISCHEMA-DEP-MISSING reads: nothing a measure Copilot still sees uses may be left out, and a measure is not put
// back while something it uses stays out. A table left out as a whole carries its fields with it, so a field line
// under it is not needed, and a field cannot be put back while its table stays out. A pick that fails a check is
// never a line; it goes on the refused list with a plain reason. A model that keeps its Copilot setup in the newer
// Copilot tooling files is not asked about at all, because the schema this writes is not the one Copilot reads there.
//
// JEV WHEN SURE, ELSE THE RULE. A pick Jev is sure of (at or above the point's bar) is Jev's line. Anything less sure
// is replaced by today's rules for that object (ReadinessRuleSet.AiSchemaRuleExcludes) and the line says it is the
// rule's. Both go through the same checks.
//
// WHAT A PERSON ALREADY LEFT OUT IS THEIR INTENT (coordinator, 26 September, D1). No readiness rule ever says to put
// a field back, so the rule never proposes it: a pick handed to the rule for something already out is no line. Jev
// may still be sure people ask about it, and then the line arrives DROPPED and says so: it is a suggestion that waits
// for the person to keep it. New exclusions, and anything that is in today, follow the Pro path as above.
//
// PUT-BACKS THAT LEAN ON EACH OTHER. A measure put back needs what it uses put back too. When that is another put-back
// in the same answer, the measure's line is offered (dropped, like every put-back) and its check names the line it
// needs. The two may sit in different tables' rows; the Plan orders them through the footprint (a measure's put-back
// reads what it uses, PlanFootprint.FromLines), so the row that puts a field back comes before the row that puts
// back the measure using it, and the reverse for leaving things out.
//
// WHO DECIDES. The point goes live only once its replay clears the committed bar (tools/jev-replay/ai-schema,
// docs/redesign/jev-ai-schema-replay.md); JevDecides says whether it has. Below the bar nothing is asked and the
// readiness rules keep saying what they say today.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Resolve is pure.
// The asking is LocalEngine.JevFamilies.cs, through DecisionService, where every gate lives (Pro, consent, budget,
// the fence).

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class AiSchemaFamily
    {
        internal const string Name = "ai-schema";

        internal const string Question = "ai_schema";

        /// <summary>The rule id a line carries when today's rules decided it because Jev was not sure.</summary>
        internal const string RuleId = "AI-SCHEMA-RULE";

        internal const string Include = "include";
        internal const string Exclude = "exclude";

        /// <summary>WHETHER JEV DECIDES. The contract's rule (jev-groundwork-contract.md section 2): a family's point is
        /// live only when its replay clears its bar (docs/redesign/jev-ai-schema-replay.md). Run 2 (key v2, Pro path "Jev
        /// when sure, else the rule") read MORE DATA under bar v2's 5-point margin. After run 2, on 26 September, Kane set
        /// a new switch-on bar for every Jev decision: beat the rule, McNemar p below 0.05, and no more harmful mistakes
        /// than the rule (here, wrong exclusions). Run 2 meets it: 0.901 against 0.878, p 0.008, no wrong exclusion.
        /// The "sure" cut stays as run 2 scored it; "likely" answers still go to the rule.</summary>
        internal const bool JevDecides = true;

        /// <summary>How many NEW objects one run asks about. A batch, not a sample: every object is prepared, the answers
        /// already known (the session's cache, or kept on this computer, DecisionAnswerStore) are read first and cost
        /// nothing, and then at most this many objects nobody has asked about yet are asked, tables first, then each
        /// table's columns and measures in name order. It is no longer a small number (80 until 15 Oct 2026): a whole model goes in one run
        /// (JevTableBatch.MaxObjectsPerRun), and the rest of a very large one follows in the next run, which the next read of the
        /// Plan starts, and until then every row says how far Jev has got (ProgressLine).</summary>
        internal const int MaxObjectsPerRun = JevTableBatch.MaxObjectsPerRun;

        /// <summary>WHETHER THE ASKS ARE BATCHED (m11/jev-batch, JevTableBatch.cs; across tables since m16/jev-batch): the objects of the whole model
        /// (the tables, their columns, their measures) in as few requests as Jev's context allows, each table's fields once, one question per
        /// object pointing at its entry in `objects`.
        /// True, as shipped: ON (Kane, 1 Oct: "the batching is the best mitigation not caps"). The comparison run of 30 September missed its fixed bar (docs/redesign/jev-batch-comparison-result.md: the unsure middle moves, sure answers stay), and Kane accepted that an unsure answer can move and arrive ticked or unticked in the reviewed Plan. JevPerObjectModeTests still proves both modes give the same plan lines. False: one request per object (the test seam JevBatchedForTest sets it).</summary>
        internal const bool Batched = true;

        /// <summary>The most objects one batched request asks about (JevTableBatch.AiSchemaObjectsPerRequest, measured live: a bigger request loses
        /// Jev's sure answers to unrelated tables). Jev's context (JevTableBatch.MaxBodyBytes) can cut a request earlier. Names, kinds and structure facts only.</summary>
        internal const int ChunkSize = JevTableBatch.AiSchemaObjectsPerRequest;

        /// <summary>The id of the one row that exists only to say Jev has not finished (LocalEngine.JevProgressRow).
        /// It carries the family's own prefix so the progress line lands on it like any other row, and it can never
        /// collide with a table's row, whose reference always begins "table:".</summary>
        internal const string NotFinishedItemId = JevFamily.ItemPrefix + Name + ".not-finished";

        /// <summary>The line a row carries while some objects are still to be asked: how far Jev has got, then, when the
        /// batches stopped, what stopped them. <paramref name="stopReason"/> null with restFollow false is THIS session's own
        /// suggestion budget, which has its own sentence because it is the only stop that names the next session. A stop the
        /// person can act on (the licence's month, an ended licence, a pause) passes the service's own sentence in: a row must
        /// never read as the whole answer, and must never blame the wrong thing for stopping.
        ///
        /// A stop nobody can act on (<see cref="JevDecisionOracle.IsNotAvailable"/>: unreachable, timed out, a server fault, a
        /// bare refusal) says only how far Jev got, or NOTHING when it looked at none: the Plan's order line says "Jev is not
        /// available right now" once (Kane, 8 Oct). No stop promises that Jev tries again: it does (a later change or the next
        /// session asks the rest), but the person cannot hurry it, and a promise the product does not keep to the minute
        /// reads as an overclaim.</summary>
        internal static string ProgressLine(int looked, int total, bool restFollow = true, string stopReason = null)
        {
            var count = "Jev has looked at " + looked.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of "
                        + JevFamily.Count(total, "thing", "things") + " in this model so far.";
            if (restFollow && string.Equals(stopReason, LookingAgain, StringComparison.Ordinal)) return count + " " + LookingAgain;
            if (restFollow) return JevDecisionOracle.IsCrowded(stopReason) ? count + " " + JevDecisionOracle.CrowdedReason : count + " The rest follow.";
            if (string.IsNullOrWhiteSpace(stopReason))
                return count + " This session has used its suggestion budget, so the rest wait for the next session.";
            if (JevDecisionOracle.IsNotAvailable(stopReason)) return looked == 0 ? null : count;
            return count + " " + AsSentence(stopReason);
        }

        /// <summary>The line a row carries from the moment a stopped job starts again until it lands. It replaces the old refusal text; the row stays, so the page does not flicker.</summary>
        internal const string LookingAgain = "Jev is looking again.";

        /// <summary>The calm line a row carries while the relay is crowded and Jev's asks wait their turn: "Jev is waiting its turn, 0:12."</summary>
        internal static string WaitingLine(TimeSpan waited)
        {
            if (waited < TimeSpan.Zero) waited = TimeSpan.Zero;
            return "Jev is waiting its turn, " + ((int)waited.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + ":" + waited.Seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".";
        }

        /// <summary>A reason in the service's own words as one sentence: a capital at the front and a full stop at
        /// the end. The reasons arrive both ways (the relay writes whole sentences, the engine writes clauses).</summary>
        private static string AsSentence(string reason)
        {
            var text = (reason ?? string.Empty).Trim();
            if (text.Length == 0) return text;
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            return text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith("!", StringComparison.Ordinal)
                   || text.EndsWith("?", StringComparison.Ordinal) ? text : text + ".";
        }

        private const int MaxTableFields = 40;
        private const int MaxReferences = 12;

        internal const string KindTable = "table";
        internal const string KindColumn = "column";
        internal const string KindMeasure = "measure";

        internal sealed class Ask
        {
            public string Ref;
            public string Kind;
            public string ObjectName;
            public string TableRef;
            public string TableName;

            /// <summary>Whether the object's OWN entry leaves it out now (a table's, or a field's own flag).</summary>
            public bool OwnExcluded;

            /// <summary>What today's rules say (ReadinessRuleSet.AiSchemaRuleExcludes): the answer when Jev is not sure.</summary>
            public bool RuleExcludes;

            // Facts the reasons are built from. Structure only.
            public string JoinsTo;          // the other table, when a column is the many end of a relationship
            public bool JoinedFrom;         // a column other tables join to (the one end)
            public bool IsKey;
            public string Sorts;            // the column whose order this one sets
            public bool IsBinary;
            public bool IsFieldParameter;
            public bool IsCalculationGroup;

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>This object's entry in a batched request's `objects`: its kind, its name and its structure facts.</summary>
            public Dictionary<string, object> Entry;
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;

            /// <summary>Each table's fields (its visible columns and measures), by table ref: the batched request's one copy.</summary>
            public Dictionary<string, List<object>> Fields = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Every eligible measure's ref, with the refs of the objects its formula uses, and its table.
            /// All of them, not only the asked ones: a measure past the cap still has to keep what it uses.</summary>
            public Dictionary<string, (string TableRef, HashSet<string> Uses)> Measures =
                new Dictionary<string, (string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The own exclusion flag of every eligible object, and each field's table.</summary>
            public Dictionary<string, bool> OwnExcluded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> TableOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> NameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // ---- read the model once --------------------------------------------------------------------------

        internal static Prepared Prepare(Model m, int cap = int.MaxValue)
        {
            var prepared = new Prepared();
            // The newer Copilot tooling files hold the schema Copilot reads there; the linguistic schema this family
            // writes is not it, so it asks nothing rather than change a schema nobody reads.
            if (PrepForAiReader.Read(m).CopilotToolingFormatPresent) return prepared;

            var point = DecisionRegistry.Require(DecisionRegistry.AiSchema);
            var questions = Questions();
            var excluded = ReadinessRuleSet.AiSchemaExcluded(m);

            var tables = m.Tables.Where(t => ReadinessRuleSet.AiSchemaEligible(t))
                                 .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var measures = tables.SelectMany(t => t.Measures).Where(x => ReadinessRuleSet.AiSchemaEligible(x)).ToList();

            // Who uses what, over every eligible measure: the dependency check and the "used by" fact both read it.
            var usedBy = new Dictionary<TabularNamedObject, int>();
            foreach (var me in measures)
            {
                var uses = ReadinessRuleSet.AiSchemaDependencies(m, me);
                prepared.Measures[ObjectRefs.For(me)] = (ObjectRefs.For(me.Table),
                    new HashSet<string>(uses.Select(ObjectRefs.For), StringComparer.OrdinalIgnoreCase));
                foreach (var d in uses) usedBy[d] = usedBy.TryGetValue(d, out var n) ? n + 1 : 1;
            }

            var ordered = new List<TabularNamedObject>();
            ordered.AddRange(tables);
            foreach (var t in tables)
            {
                ordered.AddRange(t.Columns.Where(c => ReadinessRuleSet.AiSchemaEligible(c)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
                ordered.AddRange(t.Measures.Where(x => ReadinessRuleSet.AiSchemaEligible(x)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var obj in ordered)
            {
                var r = ObjectRefs.For(obj);
                var table = obj as Table ?? (obj as Column)?.Table ?? (obj as Measure)?.Table;
                prepared.OwnExcluded[r] = excluded.Contains(obj);
                prepared.TableOf[r] = ObjectRefs.For(table);
                prepared.NameOf[r] = obj.Name;
            }

            var asked = 0;
            foreach (var obj in ordered)
            {
                if (asked >= cap) break;
                asked++;
                var table = obj as Table ?? (obj as Column)?.Table ?? (obj as Measure)?.Table;
                var ask = new Ask
                {
                    Ref = ObjectRefs.For(obj),
                    Kind = obj is Table ? KindTable : obj is Column ? KindColumn : KindMeasure,
                    ObjectName = obj.Name,
                    TableRef = ObjectRefs.For(table),
                    TableName = table.Name,
                    OwnExcluded = excluded.Contains(obj),
                    RuleExcludes = ReadinessRuleSet.AiSchemaRuleExcludes(m, obj),
                    Questions = questions,
                };
                if (obj is Column c)
                {
                    var rels = m.Relationships.OfType<SingleColumnRelationship>().ToList();
                    ask.JoinsTo = rels.Where(x => ReferenceEquals(x.FromColumn, c)).Select(x => x.ToTable?.Name).FirstOrDefault(n => n != null);
                    ask.JoinedFrom = rels.Any(x => ReferenceEquals(x.ToColumn, c));
                    ask.IsKey = c.IsKey;
                    ask.Sorts = c.Table.Columns.Where(x => ReferenceEquals(x.SortByColumn, c)).Select(x => x.Name)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    ask.IsBinary = c.DataType == DataType.Binary;
                }
                else if (obj is Table t)
                {
                    ask.IsFieldParameter = ReadinessRuleSet.IsFieldParameterTable(t);
                    ask.IsCalculationGroup = t is CalculationGroupTable;
                }
                ask.Snapshot = Snapshot(m, obj, table, point, usedBy);
                var (entryKind, entryFacts) = FactsOf(m, obj, usedBy);
                ask.Entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "kind", entryKind },
                    { "name", obj.Name },
                    { "facts", entryFacts },
                };
                if (!prepared.Fields.ContainsKey(ask.TableRef)) prepared.Fields[ask.TableRef] = FieldsOf(table, null);
                prepared.Asks.Add(ask);
            }
            return prepared;
        }

        /// <summary>The declared snapshot for one object, through <see cref="DecisionSnapshot"/> so an undeclared field
        /// cannot be sent. Names, types and structure facts only.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(Model m, TabularNamedObject obj, Table table, DecisionPoint point,
                                                                    IReadOnlyDictionary<TabularNamedObject, int> usedBy)
        {
            var (kind, facts) = FactsOf(m, obj, usedBy);
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_name", table.Name),
                new KeyValuePair<string, object>("object_kind", kind),
                new KeyValuePair<string, object>("object_name", obj.Name),
                new KeyValuePair<string, object>("object_facts", facts),
                new KeyValuePair<string, object>("table_fields", FieldsOf(table, obj)),
            });
        }

        /// <summary>The kind of an object and the structure facts about it. Names, types, counts and yes or no only.</summary>
        internal static (string Kind, Dictionary<string, object> Facts) FactsOf(Model m, TabularNamedObject obj,
                                                                                IReadOnlyDictionary<TabularNamedObject, int> usedBy)
        {
            int Used(TabularNamedObject o) => usedBy != null && usedBy.TryGetValue(o, out var n) ? n : 0;
            var facts = new Dictionary<string, object>(StringComparer.Ordinal);
            string kind;
            switch (obj)
            {
                case Table t:
                    kind = KindTable;
                    facts["table_kind"] = t is CalculationGroupTable ? "calculation group"
                        : ReadinessRuleSet.IsFieldParameterTable(t) ? "field parameter"
                        : t is CalculatedTable ? "calculated table" : "table";
                    facts["visible_columns"] = t.Columns.Count(x => ReadinessRuleSet.AiSchemaEligible(x));
                    facts["visible_measures"] = t.Measures.Count(x => ReadinessRuleSet.AiSchemaEligible(x));
                    facts["relationships"] = m.Relationships.OfType<SingleColumnRelationship>()
                        .Count(x => ReferenceEquals(x.FromTable, t) || ReferenceEquals(x.ToTable, t));
                    break;
                case Column c:
                    kind = KindColumn;
                    var rels = m.Relationships.OfType<SingleColumnRelationship>().ToList();
                    facts["data_type"] = c.DataType.ToString();
                    facts["is_calculated"] = c is CalculatedColumn || c is CalculatedTableColumn;
                    facts["is_key"] = c.IsKey;
                    facts["relationship_end"] = rels.Any(x => ReferenceEquals(x.FromColumn, c)) ? "many"
                        : rels.Any(x => ReferenceEquals(x.ToColumn, c)) ? "one" : "none";
                    facts["sets_order_of_another_column"] = c.Table.Columns.Any(x => ReferenceEquals(x.SortByColumn, c));
                    facts["used_by_measures"] = Used(c);
                    break;
                default:
                    var me = (Measure)obj;
                    kind = KindMeasure;
                    facts["uses"] = ReadinessRuleSet.AiSchemaDependencies(m, me)
                        .Select(d => d is Measure dm ? "[" + dm.Name + "]" : (d as Column)?.Table?.Name + "[" + d.Name + "]")
                        .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).Take(MaxReferences)
                        .Cast<object>().ToList();
                    facts["used_by_measures"] = Used(me);
                    break;
            }
            return (kind, facts);
        }

        /// <summary>A table's other visible fields (columns, then measures), by name, the first forty. <paramref name="except"/> is
        /// the object the per-object request asks about, left out of its own list; the batched request passes null and lists them all.</summary>
        internal static List<object> FieldsOf(Table table, TabularNamedObject except) =>
            table.Columns.Where(x => ReadinessRuleSet.AiSchemaEligible(x) && !ReferenceEquals(x, except))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "name", x.Name }, { "kind", KindColumn }, { "data_type", x.DataType.ToString() },
                    })
                .Concat(table.Measures.Where(x => ReadinessRuleSet.AiSchemaEligible(x) && !ReferenceEquals(x, except))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "name", x.Name }, { "kind", KindMeasure },
                    }))
                .Take(MaxTableFields)
                .ToList();

        /// <summary>The batched requests for the asks: the objects of every table together, each table's fields once in a map keyed by the
        /// table's name, and a question for each object that starts "For the object `objects[i]`:" and then says what the per-object question
        /// says. Each entry in `objects` names its table.</summary>
        internal static List<TableBatch<Ask>> Batches(Prepared prepared, int size = ChunkSize, bool perTable = false)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.AiSchema);
            var options = DecisionRegistry.AiSchemaChoices.Select(o => new DecisionOption(o, Criteria[o])).ToList();
            TableBatch<Ask> Build(IReadOnlyList<Ask> chunk)
            {
                var batch = new TableBatch<Ask> { TableRef = chunk[0].TableRef, Asks = chunk.ToList() };
                var questions = new List<DecisionQuestion>();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var name = JevTableBatch.QuestionName(Question, chunk[i].Ref);
                    questions.Add(new DecisionQuestion(name, DecisionQuestionKind.Choice,
                        JevTableBatch.Lead("object", "objects", i, (string)chunk[i].Entry["name"], chunk[i].TableName) + Instruction, options));
                    batch.Names[chunk[i]] = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(name, Question) };
                }
                batch.Questions = questions;
                batch.Snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("table_fields", JevTableBatch.ByTable(chunk, a => a.TableName, a => prepared.Fields[a.TableRef])),
                    new KeyValuePair<string, object>("objects", chunk.Select(a => (object)JevTableBatch.WithTable(a.Entry, a.TableName)).ToList()),
                });
                return batch;
            }
            return JevTableBatch.Pack(prepared.Asks, a => a.TableRef, Build, point, perTable ? int.MaxValue : size, perTable, perTable ? JevTableBatch.LegacyQuestions : JevTableBatch.MaxQuestions);
        }

        private static readonly Dictionary<string, string> Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { Include, "people asking Copilot or a data agent about this business would ask about it, group by it or filter by it" },
            { Exclude, "it is plumbing that could confuse Copilot: a join key, a row identifier, a column that only sets another "
                     + "column's order, a technical or audit field, a picture, a helper step of other measures, or a report control" },
        };

        internal const string Instruction = "Should Copilot and data agents see this when they answer questions about the model?";

        internal static IReadOnlyList<DecisionQuestion> Questions() => new[]
        {
            new DecisionQuestion(Question, DecisionQuestionKind.Choice, Instruction,
                DecisionRegistry.AiSchemaChoices.Select(o => new DecisionOption(o, Criteria[o])).ToList()),
        };

        // ---- Jev's answers into lines -------------------------------------------------------------------------

        internal readonly struct Pick
        {
            public Pick(Ask ask, string choice, double confidence, bool fromRule = false)
            { Ask = ask; Choice = choice; Confidence = confidence; FromRule = fromRule; }
            public Ask Ask { get; }
            public string Choice { get; }
            public double Confidence { get; }

            /// <summary>Today's rules decided this one, because Jev was not sure.</summary>
            public bool FromRule { get; }
        }

        /// <summary>THE PRO PATH: JEV WHEN SURE, ELSE THE RULE. A pick at or above the point's bar is Jev's; anything
        /// less sure (likely, unsure, or an option that was never offered) is replaced by what today's rules say for
        /// that object. The replay scored exactly this (docs/redesign/jev-ai-schema-replay.md).</summary>
        internal static Pick ProPick(Ask ask, string choice, double confidence, double bar) =>
            confidence >= bar && DecisionRegistry.AiSchemaChoices.Contains(choice)
                ? new Pick(ask, choice, confidence)
                : new Pick(ask, ask.RuleExcludes ? Exclude : Include, 1.0, fromRule: true);

        private sealed class Proposal
        {
            public Ask Ask;
            public bool Exclude;
            public string Word;
            public double Confidence;
            public bool FromRule;

            /// <summary>It puts back something already left out. Always Jev's (the rule never proposes one), and it
            /// arrives dropped: the person's exclusion stands until they keep this line.</summary>
            public bool PutBack;

            /// <summary>The names of what this measure's put-back needs put back too, by other lines in this answer.</summary>
            public List<string> Needs = new List<string>();

            public bool Kept => !PutBack && (FromRule || !string.Equals(Word, JevFamily.Unsure, StringComparison.Ordinal));
        }

        /// <summary>Every pick into a line or a refusal, after the schema's own checks. Pure: no model, no ask.</summary>
        internal static void Resolve(Prepared prepared, IEnumerable<Pick> picks, double bar,
                                     List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (prepared == null) return;
            var chosen = new Dictionary<string, (Ask Ask, bool Exclude, double Confidence)>(StringComparer.OrdinalIgnoreCase);
            var byRule = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in picks ?? Enumerable.Empty<Pick>())
            {
                if (p.Ask == null || !DecisionRegistry.AiSchemaChoices.Contains(p.Choice)) continue;   // never offered
                chosen[p.Ask.Ref] = (p.Ask, string.Equals(p.Choice, Exclude, StringComparison.Ordinal), p.Confidence);
                if (p.FromRule) byRule.Add(p.Ask.Ref); else byRule.Remove(p.Ask.Ref);
            }
            // A put-back of what is already out: never the rule's, and Jev's arrives dropped (see the file header).
            Proposal New(Ask ask, bool exclude, double confidence)
            {
                var putBack = !exclude && ask.OwnExcluded;
                if (byRule.Contains(ask.Ref))
                    return putBack ? null : new Proposal { Ask = ask, Exclude = exclude, Confidence = confidence, FromRule = true };
                return new Proposal { Ask = ask, Exclude = exclude, Confidence = confidence, Word = JevFamily.ConfidenceWord(confidence, bar), PutBack = putBack };
            }

            // Tables first: a table's own line decides whether its fields need lines at all.
            var proposals = new Dictionary<string, Proposal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ask, exclude, confidence) in chosen.Values.Where(c => c.Ask.Kind == KindTable))
                if (exclude != ask.OwnExcluded && New(ask, exclude, confidence) is Proposal tp)
                    proposals[ask.Ref] = tp;

            bool TableOutAfter(string tableRef, bool keptOnly) =>
                proposals.TryGetValue(tableRef, out var tp) && (!keptOnly || tp.Kept)
                    ? tp.Exclude
                    : prepared.OwnExcluded.TryGetValue(tableRef, out var own) && own;

            foreach (var (ask, exclude, confidence) in chosen.Values.Where(c => c.Ask.Kind != KindTable))
            {
                var tableOut = TableOutAfter(ask.TableRef, keptOnly: false);
                if (exclude)
                {
                    if (tableOut || ask.OwnExcluded) continue;   // already out, or out with its table
                }
                else
                {
                    if (tableOut)
                    {
                        // Only Jev ever asks for a put-back; the rule's include of something already out is no line.
                        if (!byRule.Contains(ask.Ref) && (ask.OwnExcluded || prepared.OwnExcluded.TryGetValue(ask.TableRef, out var t) && t))
                            refused.Add(new ModelPlanChangeRefusal
                            {
                                Target = ask.Ref,
                                Reason = ask.ObjectName + " is in " + ask.TableName + ", which Copilot does not see, so it cannot be put back on its own.",
                            });
                        continue;
                    }
                    if (!ask.OwnExcluded) continue;               // already in
                }
                if (New(ask, exclude, confidence) is Proposal fp) proposals[ask.Ref] = fp;
            }

            // THE SCHEMA'S OWN RULE, to a fixed point. "Out after" counts only lines that arrive kept: a dropped line
            // is not applied unless a person keeps it, so nothing else may lean on it.
            bool OutAfter(string objRef)
            {
                var tableRef = prepared.TableOf.TryGetValue(objRef, out var tr) ? tr : null;
                if (tableRef != null && !string.Equals(tableRef, objRef, StringComparison.OrdinalIgnoreCase) && TableOutAfter(tableRef, keptOnly: true)) return true;
                if (proposals.TryGetValue(objRef, out var p) && p.Kept) return p.Exclude;
                return prepared.OwnExcluded.TryGetValue(objRef, out var own) && own;
            }

            string Blocker(Proposal p)
            {
                if (p.Exclude)
                {
                    foreach (var kv in prepared.Measures.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var (measureTable, uses) = kv.Value;
                        if (string.Equals(kv.Key, p.Ask.Ref, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.Ask.Kind == KindTable && string.Equals(measureTable, p.Ask.Ref, StringComparison.OrdinalIgnoreCase)) continue;
                        if (OutAfter(kv.Key)) continue;
                        var hit = p.Ask.Kind == KindTable
                            ? uses.FirstOrDefault(u => prepared.TableOf.TryGetValue(u, out var ut) && string.Equals(ut, p.Ask.Ref, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(u, p.Ask.Ref, StringComparison.OrdinalIgnoreCase))
                            : uses.FirstOrDefault(u => string.Equals(u, p.Ask.Ref, StringComparison.OrdinalIgnoreCase));
                        if (hit == null) continue;
                        var user = prepared.NameOf.TryGetValue(kv.Key, out var un) ? un : kv.Key;
                        return p.Ask.Kind == KindTable
                            ? user + " uses " + (prepared.NameOf.TryGetValue(hit, out var hn) ? hn : hit) + " from " + p.Ask.ObjectName
                              + ", and Copilot still sees " + user + ", so " + p.Ask.ObjectName + " stays in."
                            : user + " uses " + p.Ask.ObjectName + ", and Copilot still sees " + user + ", so " + p.Ask.ObjectName
                              + " stays in. Leave " + user + " out too to leave " + p.Ask.ObjectName + " out.";
                    }
                    return null;
                }
                if (p.Ask.Kind == KindMeasure && prepared.Measures.TryGetValue(p.Ask.Ref, out var mine))
                {
                    p.Needs.Clear();
                    foreach (var u in mine.Uses.Where(u => prepared.OwnExcluded.ContainsKey(u) && OutAfter(u))
                                               .OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
                    {
                        // Out after every KEPT line. A put-back in this same answer can bring it back, but only if the
                        // person keeps it, so this line may only be offered as a put-back that waits for them too.
                        var backBy = PutBackOf(u);
                        if (p.PutBack && backBy != null) { p.Needs.Add(prepared.NameOf.TryGetValue(u, out var nn) ? nn : u); continue; }
                        return p.Ask.ObjectName + " uses " + (prepared.NameOf.TryGetValue(u, out var mn) ? mn : u)
                             + ", which Copilot does not see, so " + p.Ask.ObjectName + " stays out until that is put back.";
                    }
                }
                return null;
            }

            // The put-back line that would bring this object back: its own, or its table's.
            Proposal PutBackOf(string objRef)
            {
                if (proposals.TryGetValue(objRef, out var own) && own.PutBack) return own;
                var tableRef = prepared.TableOf.TryGetValue(objRef, out var tr) ? tr : null;
                return tableRef != null && proposals.TryGetValue(tableRef, out var table) && table.PutBack ? table : null;
            }

            var refusedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var p in proposals.Values.OrderBy(x => x.Ask.Ref, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var why = Blocker(p);
                    if (why == null) continue;
                    proposals.Remove(p.Ask.Ref);
                    refused.Add(new ModelPlanChangeRefusal { Target = p.Ask.Ref, Reason = why });
                    changed = true;
                }
            }

            foreach (var p in proposals.Values.OrderBy(x => x.Ask.Ref, StringComparer.OrdinalIgnoreCase))
                lines.Add(Line(p, prepared));
        }

        private static ChangeItem Line(Proposal p, Prepared prepared)
        {
            var a = p.Ask;
            // Every include is a put-back (a field under a table that is out comes back with its table's own line),
            // and a put-back is Jev's alone, so its reason says whose idea it is and whose choice it stays.
            var reason = p.Exclude ? ExcludeReason(a, prepared)
                : "Jev thinks people ask about " + a.ObjectName + "; you left it out, so this waits for you.";
            var check = p.Exclude
                ? (a.Kind == KindTable
                    ? "Semanticus checked that no measure Copilot still sees uses anything in " + a.ObjectName + "."
                    : "Semanticus checked that no measure Copilot still sees uses " + a.ObjectName + ".")
                : p.Needs.Count > 0
                    ? a.ObjectName + " uses " + JoinNames(p.Needs) + ", which Copilot does not see yet. Tick the change that puts "
                      + (p.Needs.Count == 1 ? "it" : "them") + " back too, or " + a.ObjectName + " is seen without "
                      + (p.Needs.Count == 1 ? "it" : "them") + "."
                : (a.Kind == KindMeasure
                    ? "Semanticus checked that everything " + a.ObjectName + " uses is in what Copilot sees."
                    : "Semanticus checked that " + a.ObjectName + " is visible in the model.");
            return new ChangeItem
            {
                ObjectRef = a.Ref,
                ObjectName = a.ObjectName,
                Kind = "set_ai_data_schema",
                Source = JevFamily.Source,
                Category = "DataAgentConfig",
                Severity = "Info",
                Risk = "safe",
                Target = "aiDataSchema",
                Title = p.Exclude ? "Leave out of what Copilot sees" : "Let Copilot see it",
                Rationale = reason,
                Before = p.Exclude ? "Included" : "Excluded",
                After = p.Exclude ? "Excluded" : "Included",
                // A rule's line arrives kept and names its rule; it carries no confidence, so its provenance reads
                // "rule" and never as a Jev decision. It keeps the point, so switching the point off takes it away too.
                Status = p.FromRule ? "approved" : p.PutBack ? "rejected" : JevFamily.StatusFor(p.Word),
                JevItem = JevFamily.ItemId(Name, a.TableRef),
                RuleId = p.FromRule ? RuleId : null,
                DecisionPoint = DecisionRegistry.AiSchema,
                DecisionConfidence = p.FromRule ? (double?)null : Math.Round(p.Confidence, 4),
                DecisionWord = p.FromRule ? null : p.Word,
                Reason = reason,
                CheckText = check,
            };
        }

        private static string JoinNames(IReadOnlyList<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

        /// <summary>The reason for leaving something out, from the facts the engine read, most telling first.</summary>
        private static string ExcludeReason(Ask a, Prepared prepared)
        {
            if (a.IsFieldParameter) return a.ObjectName + " is a field parameter that switches fields in a report, so it means nothing to Copilot on its own.";
            if (a.IsCalculationGroup) return a.ObjectName + " is a calculation group, a report control that changes other measures rather than something people ask about.";
            if (a.JoinsTo != null) return a.ObjectName + " joins " + a.TableName + " to " + a.JoinsTo + ". Copilot still follows that join without seeing the key.";
            if (a.JoinedFrom) return a.ObjectName + " is the key other tables join to. Copilot still follows the join without seeing it.";
            if (a.IsKey) return a.ObjectName + " is the key of " + a.TableName + ", so it tells rows apart rather than answering questions.";
            if (a.Sorts != null) return a.ObjectName + " sets the order of " + a.Sorts + ", so people ask about " + a.Sorts + " instead.";
            if (a.IsBinary) return a.ObjectName + " holds pictures or files, which Copilot cannot read.";
            var usedBy = prepared.Measures.Count(kv => kv.Value.Uses.Contains(a.Ref));
            if (a.Kind == KindMeasure && usedBy > 0)
                return a.ObjectName + " is a step inside " + JevFamily.Count(usedBy, "other measure", "other measures") + " that are left out too, so Copilot does not need it.";
            return a.ObjectName + " looks like a technical " + (a.Kind == KindMeasure ? "measure" : a.Kind == KindTable ? "table" : "field")
                 + " rather than something people ask about, and nothing Copilot sees uses it.";
        }

        /// <summary>The first "why" line of an AI data schema item.</summary>
        internal static string Missing(int n, string table) =>
            JevFamily.Count(n, "thing", "things") + " in " + table + " should be shown to Copilot or hidden from it. "
            + "Then Copilot answers from what people ask about, not from parts that could confuse it.";
    }
}
