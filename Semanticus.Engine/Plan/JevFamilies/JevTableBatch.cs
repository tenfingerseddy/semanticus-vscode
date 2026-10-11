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
// THE CHUNK. A table with a great many objects goes in chunks (each family's own size), never in one request that could pass
// the snapshot limit. The chunks are cut over ALL the objects a family prepared, in the family's order, before anything is
// looked up, so the same model always cuts the same chunks and each chunk's digest is stable while its evidence is.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;

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

        /// <summary>The objects grouped by table (a table's first appearance fixes its place, its objects keep their order) and
        /// cut into chunks of at most <paramref name="size"/>.</summary>
        public static IReadOnlyList<IReadOnlyList<TAsk>> Chunks<TAsk>(IEnumerable<TAsk> asks, Func<TAsk, string> tableOf, int size)
        {
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            var order = new List<string>();
            var byTable = new Dictionary<string, List<TAsk>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ask in asks ?? Enumerable.Empty<TAsk>())
            {
                var table = tableOf(ask) ?? "";
                if (!byTable.TryGetValue(table, out var list)) { byTable[table] = list = new List<TAsk>(); order.Add(table); }
                list.Add(ask);
            }
            var chunks = new List<IReadOnlyList<TAsk>>();
            foreach (var table in order)
            {
                var list = byTable[table];
                for (var i = 0; i < list.Count; i += size) chunks.Add(list.Skip(i).Take(size).ToList());
            }
            return chunks;
        }

        /// <summary>The largest batched snapshot, a little under the 64 KB the client and the relay both refuse past
        /// (JevDecisionOracle.MaxSnapshotBytes, relay LIMITS.maxSnapshotBytes). An oversized snapshot is refused, never truncated, so a
        /// chunk that would pass it is split in two instead, and again while it is still too big.</summary>
        public const int MaxSnapshotBytes = 60 * 1024;

        /// <summary>One chunk as one batch, or two half chunks (and so on) when the snapshot it would send is over
        /// <see cref="MaxSnapshotBytes"/>. A single object that is still over the limit is returned as it is and refused by the client like
        /// any oversized snapshot: it is left to the person, never cut short.</summary>
        public static IEnumerable<TableBatch<TAsk>> Fit<TAsk>(IReadOnlyList<TAsk> chunk, Func<IReadOnlyList<TAsk>, TableBatch<TAsk>> build) where TAsk : class
        {
            var batch = build(chunk);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(DecisionDigest.CanonicalSnapshotJson(batch.Snapshot));
            if (chunk.Count > 1 && bytes > MaxSnapshotBytes)
            {
                var half = chunk.Count / 2;
                foreach (var b in Fit(chunk.Take(half).ToList(), build)) yield return b;
                foreach (var b in Fit(chunk.Skip(half).ToList(), build)) yield return b;
            }
            else yield return batch;
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
