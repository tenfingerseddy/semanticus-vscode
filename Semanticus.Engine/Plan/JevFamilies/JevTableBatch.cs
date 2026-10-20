// One request for a table, not one for each of its objects (m11/jev-batch; Kane, 30 Sep 2026: "Yes, this release").
//
// THE SHAPE, FROM THE TYPESAFE SKILL: ask independent questions over the SAME state together. Four families used to send one
// request per object (a column, a measure, a table), each resending the facts every request of that table shares (the model's
// tables, the table's columns): up to about 260 requests a run, six at a time, and the same facts paid for again in every one.
// Now the facts a table shares go ONCE, its objects are a list, and there is one question for each object that points at its
// entry by path: "For the column `columns[3]`: ...". The shape is the one cleanup.suggest already uses (CleanupSuggest.cs).
//
// WHAT DOES NOT CHANGE. Every question's words, criteria and options are exactly what they were (each question starts with the
// one sentence that says which entry it is about, and then says what it always said). Only the facts' shape changes. Every fact
// is still a name, a type, a count, a share or a yes or no; nothing new leaves the computer.
//
// HOW AN ANSWER COMES BACK. A batched request is read question by question (DecisionRequest.PerQuestion): one malformed answer
// refuses only itself, and the object it was about reads as "Jev has not answered" and keeps whatever it had. A reply that
// came back whole is kept in the cache under the request's digest, like any other; a reply with an answer refused is used for
// the answers it has and asked again next time (DecisionService).
//
// THE REQUEST (m16/jev-batch, Kane, 15 Oct 2026: "Yes": batch, do not cap). Jev answers as many questions as fit its context in ONE call,
// in parallel, and a call costs next to nothing (jev-costs-nothing). So the requests are no longer cut by table: the objects of every
// table go together, in the family's order, and a request is cut where the point's measured bound (below), Jev's context (MaxBodyBytes) or the relay's limits say.
// What a table shares (its column names, its fields) goes once in a map keyed by the table's name, what the whole model shares (the
// list of tables) goes once for the request, and each object's entry names its table. A 30-table model that took 75 calls to build
// and 514 to run takes a few.
//
// HOW BIG A REQUEST, AND HOW A QUESTION FINDS ITS ENTRY (m16/jev-size, measured live on the 30-table model, 15 Oct 2026). Whole-model
// requests lost most of Jev's sure answers (Copilot 34 to 1, summarize 47 to 18, measure format 55 to 22). The cause was not the shared
// maps: it was the path. A question that names its entry only as `columns[37]` asks Jev to count into a long list, and Jev does not count
// (docs.typesafe.ai, jev-1.13 jaggedness: counting, indirection). Confidence fell with the entry's place in the list: sure up to about
// the tenth entry, rarely past the twentieth, in every shape and size. So each question now also NAMES its entry ("For the column
// `columns[37]`, named "Cost" in the table "Activity Event": ..."): the name and the table are already in the entry, so nothing new leaves
// the computer, and no field is added. Named, a whole-model request keeps the sure answers of one request per object (every sure answer
// matched that reference in every arm), and the shared maps stay (carrying each table's facts inside every entry, tried as well, added
// nothing and cost two and a half times the tokens). One point still loses from a big state: ai.schema, where a request of the whole
// model turns business columns such as Category from a sure include to an unsure exclude (unrelated tables are distractors, the
// jaggedness page's "large state"). So each point has its own bound on objects a request (AiSchemaObjectsPerRequest and the two after it), chosen from that sweep.
//
// A request the relay refuses as too big (a relay older than this client) is asked again a table at a time (TableBatch.Narrow), so a
// client ahead of its relay still works.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    /// <summary>One batched request: the objects it asks about (in order), the one snapshot they share, one or more questions
    /// for each, and how each object's answers are named in the reply.</summary>
    internal sealed class TableBatch<TAsk> where TAsk : class
    {
        public string TableRef;
        public List<TAsk> Asks = new List<TAsk>();
        public IReadOnlyDictionary<string, object> Snapshot;
        public IReadOnlyList<DecisionQuestion> Questions;

        /// <summary>Per object: the batched question name to the plain question name the family reads (for example
        /// "summarize_by:column:Sales/Amount" to "summarize_by").</summary>
        public Dictionary<TAsk, List<KeyValuePair<string, string>>> Names =
            new Dictionary<TAsk, List<KeyValuePair<string, string>>>(ReferenceEqualityComparer.Instance as IEqualityComparer<TAsk>);

        /// <summary>The same asks as requests of one table each and at most a given number of QUESTIONS (not objects: a measure asks two),
        /// for a relay that refuses this one as too big. Null when this request is already that small.</summary>
        public Func<int, IReadOnlyList<TableBatch<TAsk>>> Narrower;

        /// <summary>The same asks as requests of one table each and at most <paramref name="maxQuestions"/> questions each (the old relay's bound
        /// for the point by default). A request of one object comes back as itself: there is nothing smaller.</summary>
        public IReadOnlyList<TableBatch<TAsk>> Narrow(int maxQuestions = JevTableBatch.LegacyQuestions) =>
            Narrower == null || Asks.Count <= 1 ? new[] { this } : Narrower(maxQuestions);

        public DecisionRequest RequestFor(DecisionPoint point) =>
            new DecisionRequest(point.Name, point.SchemaVersion, Snapshot, Questions, perQuestion: true);
    }

    internal static class JevTableBatch
    {
        /// <summary>The name of one object's question in a batched request: the plain question's name, then the object's ref. The
        /// ref is already in the snapshot (it is made of the table's and the object's own names), so the name carries nothing
        /// the request did not.</summary>
        public static string QuestionName(string plain, string objectRef) => plain + ":" + objectRef;

        /// <summary>How an entry is pointed at in a question's words: the path into the request's own list, in backticks, as the
        /// TypeSafe docs write a reference to state ("`columns[3]`").</summary>
        public static string Path(string list, int index) => "`" + list + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]`";

        /// <summary>How a batched question opens: the entry's path AND its name and table, then a colon. Jev finds an entry by its name;
        /// it does not count into a list, so a question that gave only the path lost its sure answer past about the tenth entry (see the
        /// file comment). The name and the table are the entry's own (its "name" and "table"), so the words carry nothing the state does not.</summary>
        public static string Lead(string noun, string list, int index, string name, string table) =>
            "For the " + noun + " " + Path(list, index) + ", named \"" + name + "\" in the table \"" + table + "\": ";

        /// <summary>The most objects one request asks about, by point (m16/jev-size, live sweep of 8, 15, 20, 30, 60 and whole on the 30-table
        /// model, named questions, two repeats of the best arms). ai.schema 15: the most sure answers (87 to 96 against 36 to 38 a table at a
        /// time) and the closest to one request per object; bigger requests lose sure answers to unrelated tables. measure.folder_and_format 30:
        /// every reference sure answer kept at 30 and 60, and 30 leaves room for a measure that asks two questions. column.summarize and
        /// column.format 60: every size kept the sure answers, so the bound is only the biggest size measured. Several small tables still
        /// share one request up to the bound.</summary>
        public const int AiSchemaObjectsPerRequest = 15;
        /// <summary>See <see cref="AiSchemaObjectsPerRequest"/>.</summary>
        public const int MeasureObjectsPerRequest = 30;
        /// <summary>See <see cref="AiSchemaObjectsPerRequest"/>: column.summarize and column.format.</summary>
        public const int ColumnObjectsPerRequest = 60;

        /// <summary>The largest batched snapshot, a little under the 64 KB the client and the relay both refuse past
        /// (JevDecisionOracle.MaxSnapshotBytes, relay LIMITS.maxSnapshotBytes). An oversized snapshot is refused, never truncated, so a
        /// request that would pass it is cut earlier instead.</summary>
        public const int MaxSnapshotBytes = 60 * 1024;

        /// <summary>The most one request's body (the state and every question) may hold. Jev's own limit is 64,000 tokens a request for the
        /// state and all the questions together, with the state plus the longest question inside 32,000 (docs.typesafe.ai/models, 15 Oct
        /// 2026). The engine's estimate counts three bytes a token on purpose (JevDecisionOracle.EstimateInputTokens; real text is nearer
        /// four), so a body of this size is at most 40,000 tokens by the estimate and about 30,000 in fact: a margin of a third against the
        /// limit at the estimate and more than half against the real count. The state alone is held to <see cref="MaxSnapshotBytes"/>.</summary>
        public const int MaxBodyBytes = 40_000 * 3;

        /// <summary>The most questions one request asks (the relay's bound for the batched points, tools/relay/templates.mjs). The body budget
        /// bites first for every point today; this stops a point with very short questions from asking for thousands in one reply.</summary>
        public const int MaxQuestions = 400;

        /// <summary>The most NEW objects a family asks in one run. Not a cost guard (Jev costs next to nothing, memory jev-costs-nothing): it keeps a run
        /// of a model with tens of thousands of objects to a few dozen requests, and the rest wait for the next read like any other batch.</summary>
        public const int MaxObjectsPerRun = 2_000;

        /// <summary>The most QUESTIONS a request carried to the relay before its bound was raised (30 for every batched point). A request the relay
        /// refuses as too big (an older relay) is asked again in requests of a table and at most this many questions, and again in halves while
        /// one is still refused (<see cref="TableBatch{TAsk}.Narrow"/>). Counted in questions, not objects: a measure asks a folder and a format.</summary>
        public const int LegacyQuestions = 30;

        /// <summary>Kept for the callers that count objects: the same number.</summary>
        public const int LegacyChunk = LegacyQuestions;

        /// <summary>What the relay before this release took in one request, by point: 30 for the batched points, 40 cleanup.suggest, 24 query.summarize,
        /// 64 compose.draft. A relay rolled back to that release refuses a bigger request as too big.</summary>
        public static int LegacyQuestionsFor(string pointName) => pointName switch
        {
            "cleanup.suggest" => 40,
            "query.summarize" => 24,
            "compose.draft" => 64,
            _ => LegacyQuestions,
        };

        /// <summary>About how many bytes a question adds to a request body (its name, words and options). For a builder that has no real body
        /// yet and must stop before the context budget (JevDraftBuilder); a family measures the real body instead (<see cref="Pack"/>).</summary>
        public static int QuestionBytes(DecisionQuestion q) =>
            64 + q.Name.Length + q.Instructions.Length + q.Options.Sum(o => 40 + o.Id.Length + o.Criterion.Length);

        /// <summary>The objects grouped by table (a table's first appearance fixes its place, its objects keep their order).</summary>
        private static List<TAsk> InTableOrder<TAsk>(IEnumerable<TAsk> asks, Func<TAsk, string> tableOf)
        {
            var order = new List<string>();
            var byTable = new Dictionary<string, List<TAsk>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ask in asks ?? Enumerable.Empty<TAsk>())
            {
                var table = tableOf(ask) ?? "";
                if (!byTable.TryGetValue(table, out var list)) { byTable[table] = list = new List<TAsk>(); order.Add(table); }
                list.Add(ask);
            }
            return order.SelectMany(t => byTable[t]).ToList();
        }

        /// <summary>Whether one built request is inside every limit: Jev's context (<see cref="MaxBodyBytes"/>), the state's own bound, and the
        /// questions' bound. The body is measured exactly as it is sent (JevDecisionOracle.BuildBody), not guessed.</summary>
        private static bool Fits<TAsk>(TableBatch<TAsk> batch, DecisionPoint point, int maxQuestions) where TAsk : class
        {
            if (batch.Questions.Count > maxQuestions) return false;
            var json = DecisionDigest.CanonicalSnapshotJson(batch.Snapshot);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes) return false;
            return System.Text.Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(batch.RequestFor(point), json)) <= MaxBodyBytes;
        }

        /// <summary>The requests for the asks: objects of every table together, in the family's order, each request as full as Jev's context and
        /// the relay's limits allow (found by doubling and then halving the number of asks, building the real request each time, so it is exact
        /// and cannot pass a limit). <paramref name="build"/> must be pure. A single ask that is over a limit on its own is returned alone and
        /// refused by the client like any oversized state: it is left to the person, never cut short. With <paramref name="perTable"/> a request
        /// never spans two tables (the shape of an older relay).</summary>
        public static List<TableBatch<TAsk>> Pack<TAsk>(IEnumerable<TAsk> asks, Func<TAsk, string> tableOf, Func<IReadOnlyList<TAsk>, TableBatch<TAsk>> build,
                                                        DecisionPoint point, int maxObjects = int.MaxValue, bool perTable = false, int maxQuestions = MaxQuestions) where TAsk : class
        {
            if (maxObjects < 1) throw new ArgumentOutOfRangeException(nameof(maxObjects));
            var ordered = InTableOrder(asks, tableOf);
            var segments = new List<List<TAsk>>();
            if (perTable)
                foreach (var g in ordered.GroupBy(a => tableOf(a) ?? "", StringComparer.OrdinalIgnoreCase)) segments.Add(g.ToList());
            else if (ordered.Count > 0) segments.Add(ordered);

            var result = new List<TableBatch<TAsk>>();
            foreach (var segment in segments)
                for (var at = 0; at < segment.Count;)
                {
                    var hi = Math.Min(segment.Count - at, maxObjects);
                    bool Probe(int k) => Fits(build(segment.GetRange(at, k)), point, maxQuestions);
                    var good = 1;
                    if (Probe(1))
                    {
                        var bad = -1;
                        for (var k = 2; k / 2 < hi; k *= 2)
                        {
                            var probe = Math.Min(k, hi);
                            if (probe <= good) break;
                            if (Probe(probe)) good = probe; else { bad = probe; break; }
                            if (probe == hi) break;
                        }
                        if (bad > 0)
                            while (bad - good > 1)
                            {
                                var mid = (good + bad) / 2;
                                if (Probe(mid)) good = mid; else bad = mid;
                            }
                    }
                    var chunk = segment.GetRange(at, good);
                    var batch = build(chunk);
                    batch.Narrower = q => Pack(chunk, tableOf, build, point, int.MaxValue, perTable: true, maxQuestions: Math.Max(1, q));
                    result.Add(batch);
                    at += good;
                }
            return result;
        }

        /// <summary>The tables a chunk covers, in order of first appearance, with a value for each, keyed by the table's name: what a table shares
        /// (its columns, its fields) goes once in such a map and each entry names its table.</summary>
        public static Dictionary<string, object> ByTable<TAsk>(IEnumerable<TAsk> chunk, Func<TAsk, string> tableName, Func<TAsk, object> sharedOf)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var ask in chunk)
            {
                var name = tableName(ask) ?? "";
                if (!map.ContainsKey(name)) map[name] = sharedOf(ask);
            }
            return map;
        }

        /// <summary>An object's entry with the name of its table added, so a request that holds many tables says which one each entry is in.</summary>
        public static Dictionary<string, object> WithTable(Dictionary<string, object> entry, string tableName)
        {
            var copy = new Dictionary<string, object>(entry, StringComparer.Ordinal) { ["table"] = tableName };
            return copy;
        }

        /// <summary>The batches to ask next: the chunks nobody has an answer for, whole chunks in order, until the run's cap on
        /// NEW objects is reached (always at least one chunk, so a small cap cannot starve the family). Pure.</summary>
        public static List<TableBatch<TAsk>> TakeWithinCap<TAsk>(IEnumerable<TableBatch<TAsk>> fresh, int cap) where TAsk : class
        {
            var taken = new List<TableBatch<TAsk>>();
            var objects = 0;
            foreach (var batch in fresh)
            {
                if (taken.Count > 0 && objects + batch.Asks.Count > Math.Max(1, cap)) break;
                taken.Add(batch);
                objects += batch.Asks.Count;
            }
            return taken;
        }
    }
}
