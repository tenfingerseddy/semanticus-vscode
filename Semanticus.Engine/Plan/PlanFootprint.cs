// Footprints, "Waits for" and Ready (Plan-heart contract 5.1 to 5.4, lane E2).
//
// WHAT A FOOTPRINT IS. Every row on the plan reads some things and writes some things. A key is an object ref as
// the engine spells it ("column:Sales/CustomerKey", "measure:Sales/Total Sales", "table:Date") or a FACET, a
// model-wide fact that is not one object ("model:date-table", "model:relationships:Sales"). Dependencies between
// rows are never typed by anybody: they fall out of the footprints by one rule (contract 5.2), so "Waits for" on
// the page, the assistant's offer and Run the plan can never disagree about them.
//
// THE CAREFUL RULE (Kane 24 Sep). The object-level rule called two pairs independent that were not (spike P0): a
// create adds members to another row's population, and on a model with reports chosen any commit makes the report
// reading stale. Two facets close both, and both are EVENT facets:
//   model:objects   written by every create, delete and rename; read by every population row (a sweep over
//                   "every undescribed ...", "every unused ...").
//   model:revision  written by every row that commits; read by every report-aware row.
// An event facet conflicts only with a READER. Two rows that both write it do not conflict on it: two commits
// bumping one counter give the same model in either order, and only a reader cares. Read literally without this,
// every committing row conflicts with every other and no pair could ever be independent (P0b 24 Sep).
//
// THE PRINCIPLE EVERY FAMILY BELOW FOLLOWS: a wrong "waits for" costs time, a wrong "independent" is forbidden. A
// family whose reads cannot be stated exactly reads the widest key that covers them, and a row whose footprint the
// engine cannot state at all is UNKNOWN: it gets no derived waits and is never called independent. A row whose
// footprint the engine can state only in part (a Plan task: its attached objects are a floor, its words can reach
// anything) is unknown too, so it is never called independent either; it waits on its floor, because a conflict on
// keys it is sure to touch is a real one (Sol 6, slice 7, finding 3).
//
// THE GATE THAT HOLDS THIS FILE HONEST is PlanCommutationTests: over eight fixture models it does every pair of
// rows in both orders and proves that every pair this file calls independent gives the same model and the same
// change either way. A footprint edited here that lets a real dependency through fails that test.
//
// WHAT THIS FILE DOES NOT DO. It never reads the model on its own: the compose takes one snapshot on the
// dispatcher (FootprintSnapshot.Take) and everything here is arithmetic over it and over lines already read. It
// does not compute the state word (Plan item model, lane E1): it hands that lane WaitsFor, and a row whose
// WaitsFor is not empty is Waiting (contract 1.4).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>A footprint: what one row reads and writes, and whether the engine could tell at all.</summary>
    internal sealed class Footprint
    {
        public readonly HashSet<string> Reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Writes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Known = true;

        /// <summary>The objects a KEPT line of the row's worked-out change set writes: what the row will change if
        /// approved, as opposed to what its family declares it may touch. Only this makes a person's row a pending
        /// writer that a task waits for (Coordinator 25 Sep, F-281); empty for a row whose lines are not worked
        /// out, and for every task. Waits reads it for direction only.</summary>
        public readonly HashSet<string> Chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The row offers CANDIDATES nobody has chosen: a person's or an assistant's row whose lines are not
        /// worked out, or any row whose worked-out lines are all dropped. Set by Stamp; read by Waits for direction
        /// only (Coordinator 26 Sep).</summary>
        public bool Candidate;

        /// <summary>The row has worked-out lines of its own, kept or not (a proposal, not a sweep). Set by Stamp; read
        /// by Waits for the words row's direction only.</summary>
        public bool Worked;
        /// <summary>The row is optional (ModelPlanItem.Optional): nothing waits for it. Set by Stamp; read by Waits for
        /// direction only.</summary>
        public bool Optional;

        public static Footprint Unknown() => new Footprint { Known = false };

        /// <summary>True when the footprint names anything. An unknown footprint can still name a FLOOR: keys it is
        /// sure to touch (a task's attached objects), with more it may touch that nobody can name.</summary>
        public bool HasKeys => Reads.Count > 0 || Writes.Count > 0;

        public Footprint Read(string key) { if (!string.IsNullOrWhiteSpace(key)) Reads.Add(key); return this; }
        public Footprint Write(string key) { if (!string.IsNullOrWhiteSpace(key)) Writes.Add(key); return this; }
        public Footprint ReadAll(IEnumerable<string> keys) { foreach (var k in keys ?? Enumerable.Empty<string>()) Read(k); return this; }
        public Footprint WriteAll(IEnumerable<string> keys) { foreach (var k in keys ?? Enumerable.Empty<string>()) Write(k); return this; }
    }

    /// <summary>What a footprint needs to know about the model, read ONCE per compose on the dispatcher, so every
    /// function below is pure. Names are kept exactly as the model spells them.</summary>
    internal sealed class FootprintSnapshot
    {
        public string[] MeasureRefs = Array.Empty<string>();
        public string[] CalculatedRefs = Array.Empty<string>();
        /// <summary>Table name to the refs of its columns.</summary>
        public Dictionary<string, string[]> ColumnsByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Table name to the refs of its measures.</summary>
        public Dictionary<string, string[]> MeasuresByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Measure name to ref. A name two tables share maps to both.</summary>
        public ILookup<string, string> MeasureByName = Array.Empty<string>().ToLookup(x => x, StringComparer.OrdinalIgnoreCase);
        /// <summary>Every DAX-carrying object's ref and expression, for "who names this" (a rename's fixup set).</summary>
        public (string Ref, string Expression)[] Expressions = Array.Empty<(string, string)>();
        /// <summary>Every calculation item's ref and expression. Kept apart from <see cref="Expressions"/> because a
        /// rename's fixup set has never counted them; the date mark reads them (a calculation group is where a model often
        /// keeps all its time intelligence).</summary>
        public (string Ref, string Expression)[] CalculationItems = Array.Empty<(string, string)>();
        /// <summary>Relationship name to the two tables it joins.</summary>
        public Dictionary<string, string[]> RelationshipTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The date columns the rule's date mark may choose from (<see cref="PlanDateMark.Pool"/>): every DateTime
        /// column on a table that is not the many side of a relationship.</summary>
        public HashSet<string> CandidateDateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every DAX object (measure, calculated column or table, calculation item) that reads one of
        /// <see cref="CandidateDateColumns"/>: what the rule's date mark chooses its table from.</summary>
        public string[] DateReaders = Array.Empty<string>();
        /// <summary>The columns that are a table's key now. A date mark and the best-practice row's key fix
        /// (MARK_PRIMARY_KEYS) write a key, and setting one clears the key on every other column of that table
        /// (TOMWrapper Column.cs, OnPropertyChanged), so both write these.</summary>
        public HashSet<string> KeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The columns whose key a readiness population can see change (ReadinessRuleSet.KeyStatusCanMatter): all
        /// but a date column that is a relationship end. A mark that moves one of these writes <see cref="PlanFootprint.Keys"/>.</summary>
        public HashSet<string> KeyStatusRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Relationship name to the refs of its two columns. A relationship's name is spelled with its columns'
        /// tables and names (TOMWrapper SingleColumnRelationship.UpdateName), so a rename of either moves its ref.</summary>
        public Dictionary<string, string[]> RelationshipColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static FootprintSnapshot Take(Model m)
        {
            var snap = new FootprintSnapshot();
            if (m == null) return snap;
            snap.MeasureRefs = m.AllMeasures.Select(x => ObjectRefs.For(x)).ToArray();
            snap.CalculatedRefs = m.AllColumns.OfType<CalculatedColumn>().Select(x => ObjectRefs.For(x))
                .Concat(m.Tables.OfType<CalculatedTable>().Select(x => ObjectRefs.For(x))).ToArray();
            foreach (var t in m.Tables)
            {
                snap.ColumnsByTable[t.Name] = t.Columns.Select(c => ObjectRefs.For(c)).ToArray();
                snap.MeasuresByTable[t.Name] = t.Measures.Select(x => ObjectRefs.For(x)).ToArray();
            }
            snap.MeasureByName = m.AllMeasures.ToLookup(x => x.Name, x => ObjectRefs.For(x), StringComparer.OrdinalIgnoreCase);
            snap.Expressions = m.AllMeasures.Select(x => (ObjectRefs.For(x), x.Expression ?? ""))
                .Concat(m.AllColumns.OfType<CalculatedColumn>().Select(x => (ObjectRefs.For(x), x.Expression ?? "")))
                .Concat(m.Tables.OfType<CalculatedTable>().Select(x => (ObjectRefs.For(x), x.Expression ?? "")))
                .ToArray();
            snap.CalculationItems = m.CalculationGroups.SelectMany(g => g.CalculationItems)
                .Select(ci => (ObjectRefs.For(ci), ci.Expression ?? "")).ToArray();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
                if (!string.IsNullOrEmpty(r.Name))
                {
                    snap.RelationshipTables[r.Name] = new[] { r.FromTable?.Name, r.ToTable?.Name }.Where(n => n != null).ToArray();
                    snap.RelationshipColumns[r.Name] = new Column[] { r.FromColumn, r.ToColumn }.Where(c => c != null).Select(c => ObjectRefs.For(c)).ToArray();
                }
            var (pool, readers) = PlanDateMark.Pool(m);
            snap.CandidateDateColumns = new HashSet<string>(pool.Select(c => ObjectRefs.For(c)), StringComparer.OrdinalIgnoreCase);
            snap.DateReaders = readers.Select(r => ObjectRefs.For((ITabularObject)r.Object)).ToArray();
            snap.KeyColumns = new HashSet<string>(m.AllColumns.Where(c => c.IsKey).Select(c => ObjectRefs.For(c)), StringComparer.OrdinalIgnoreCase);
            snap.KeyStatusRead = new HashSet<string>(m.AllColumns.Where(ReadinessRuleSet.KeyStatusCanMatter).Select(c => ObjectRefs.For(c)), StringComparer.OrdinalIgnoreCase);
            return snap;
        }
    }

    /// <summary>Everything one compose knows that a footprint may need: the model snapshot and the facts the
    /// plan's rows were built from.</summary>
    internal sealed class FootprintInputs
    {
        public FootprintSnapshot Model = new FootprintSnapshot();
        public IReadOnlyList<ReadinessFinding> Findings = Array.Empty<ReadinessFinding>();
        /// <summary>The refs of the objects the cleanup sweep calls safe to remove.</summary>
        public string[] UnusedSafe = Array.Empty<string>();
        /// <summary>The objects the best-practice row's lines name, before its lines are worked out.</summary>
        public string[] BpaFixTargets = Array.Empty<string>();
        /// <summary>The objects whose format the words row asks the assistant for beyond the readiness words: a measure or a column
        /// a best-practice rule wants a format on (LocalEngine.BpaFormatItems).</summary>
        public string[] WordsFormatTargets = Array.Empty<string>();
        /// <summary>The columns the best-practice row's lines make a key (MARK_PRIMARY_KEYS).</summary>
        public string[] BpaFixKeyTargets = Array.Empty<string>();
        /// <summary>True when reports are chosen for this model, so the cleanup verdict rests on a
        /// revision-stamped report reading (LocalEngine.ReportScope.cs).</summary>
        public bool ReportsChosen;
        /// <summary>The starter rule's measures, fact table by fact table.</summary>
        public IReadOnlyList<(string Table, List<(string Column, string Measure)> Measures)> StarterRule =
            Array.Empty<(string, List<(string, string)>)>();
        /// <summary>A proposal finding's family, as the plan groups them (LocalEngine.ProposalFamily).</summary>
        public Func<ReadinessFinding, string> ProposalFamily = _ => null;
        /// <summary>The table and key column the rule's date row will mark when its line is worked out, or null when the
        /// model does not name one (PlanDateMark, the same answer that titled the row).</summary>
        public PlanDateMarkCandidate DateMark;
    }

    internal static class PlanFootprint
    {
        // ---- the facets ---------------------------------------------------------------------------------------

        public const string Objects = "model:objects";
        public const string Revision = "model:revision";
        public const string DateTable = "model:date-table";
        public const string Culture = "model:culture";
        public const string AiInstructions = "model:ai-instructions";
        public const string AiDataSchema = "model:ai-data-schema";
        public const string Calendar = "model:calendar";
        public const string Reports = "model:reports";
        /// <summary>The model's roles. A table permission names its role only in its line's arguments, so a role and a
        /// permission meet here.</summary>
        public const string Roles = "model:roles";
        public static string RelationshipsAt(string table) => "model:relationships:" + table;
        /// <summary>Which columns are keys, as the readiness populations that read it see it
        /// (ReadinessRuleSet.KeyStatusRules). Only a date mark moves a key, and it writes this only when the key it sets
        /// or clears is one a population can see (<see cref="Marks"/>); the rows those rules feed read it.</summary>
        public const string Keys = "model:keys";
        /// <summary>That something answers to this ref: the name a line reaches an object by, as opposed to the object's
        /// settings. Written by what makes, renames or removes it; read by every line that reaches the object by that
        /// name, which includes a line on anything inside a table (its ref is spelled with the table's name), a line on a
        /// relationship (spelled with its two columns), and a line that names the object only in its arguments. A
        /// setting written on the object (a table's description) is not its name, so it does not meet these.</summary>
        public static string NameOf(string objRef) => string.IsNullOrWhiteSpace(objRef) ? null : "model:name:" + objRef;

        /// <summary>What a line that makes, renames or removes <paramref name="objRef"/> takes or frees: its name
        /// (<see cref="NameOf"/>), and the names it shares with objects of another kind (verify PW1 and PW2, lane
        /// m11/plan-waits). A column and a measure may not share a name on one table, and a measure's name is unique
        /// across the whole model (StarterMeasureTemplates.NameTaken), so a new measure Customer[Segment] and a new
        /// column Customer[Segment] meet on the table's member name, and a rename to Customer Count on Sales and a new
        /// measure Customer[Customer Count] meet on the measure name. Written only, never read: two lines that take one
        /// name conflict on it (write-write), because whichever runs second is refused or renamed.</summary>
        public static IEnumerable<string> Claims(string objRef)
        {
            if (string.IsNullOrWhiteSpace(objRef)) yield break;
            yield return NameOf(objRef);
            var colon = objRef.IndexOf(':');
            var kind = colon > 0 ? objRef.Substring(0, colon) : "";
            var isMeasure = string.Equals(kind, "measure", StringComparison.OrdinalIgnoreCase);
            if (!isMeasure && !string.Equals(kind, "column", StringComparison.OrdinalIgnoreCase)) yield break;
            var slash = objRef.IndexOf('/', colon);
            if (slash < 0 || slash == objRef.Length - 1) yield break;
            var table = objRef.Substring(colon + 1, slash - colon - 1);
            var name = objRef.Substring(slash + 1);
            yield return "model:member-name:" + table + "/" + name;
            if (isMeasure) yield return "model:measure-name:" + name;
        }

        /// <summary>The careful rule's two EVENT facets: write-write on them is never a conflict (P0b 24 Sep).</summary>
        public static readonly IReadOnlyCollection<string> EventFacets =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Objects, Revision };

        // ---- the rule (contract 5.2) --------------------------------------------------------------------------

        /// <summary>j produces something i needs.</summary>
        public static bool Feeds(Footprint j, Footprint i) => j.Writes.Overlaps(i.Reads);

        /// <summary>Either feeds the other, or both write the same key that is not an event facet.</summary>
        public static bool Conflict(Footprint i, Footprint j) =>
            Feeds(j, i) || Feeds(i, j) || i.Writes.Any(w => !EventFacets.Contains(w) && j.Writes.Contains(w));

        /// <summary>Contract 5.4: independent only when both footprints are known and conflict holds in neither
        /// direction.</summary>
        public static bool Independent(Footprint i, Footprint j) => i.Known && j.Known && !Conflict(i, j);

        private static bool WordsOverCleanup(string words, string cleanup) =>
            LocalEngine.IsWordsItem(words) && string.Equals(cleanup, "cleanup/model", StringComparison.Ordinal);

        /// <summary>A row with worked-out lines of its own meets the words row, which has none.</summary>
        private static bool WorkedOverWords((string Id, Footprint Fp) worked, (string Id, Footprint Fp) words) =>
            LocalEngine.IsWordsItem(words.Id) && !words.Fp.Worked && worked.Fp.Worked;

        private static bool UnchosenCleanup((string Id, Footprint Fp) row) =>
            string.Equals(row.Id, "cleanup/model", StringComparison.Ordinal) && row.Fp.Candidate;

        /// <summary>The note a row carries when a loop among rows was broken by plan order.</summary>
        public const string OrderedByPlanNote = "Semanticus ordered these by the plan's order.";

        /// <summary>WaitsFor for rows given in PLAN ORDER (rank = position). An unknown footprint that names nothing takes
        /// no part; one that names a floor waits, and is waited for, on that floor only.
        /// Returns, for each row id that waits, the ids it waits for in plan order, and the set of rows whose
        /// waits were decided by plan order inside a loop.
        ///
        /// first(j, i) holds when they conflict and either j feeds i one way, or the relation is symmetric (both
        /// feed each other, or they only share a write) and j comes first in plan order. The cycle pass then finds
        /// every strongly connected group and keeps, inside it, only the edges that point from earlier to later,
        /// so the result is acyclic and some row is always Ready.</summary>
        public static (Dictionary<string, List<string>> Waits, HashSet<string> Reordered) Waits(
            IReadOnlyList<(string Id, Footprint Fp)> rows, IReadOnlyDictionary<string, string> doers = null)
        {
            var n = rows.Count;
            var edges = new List<int>[n];       // edges[j] = the rows that wait for j
            for (var k = 0; k < n; k++) edges[k] = new List<int>();
            var mutual = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                var a = rows[i].Fp; var b = rows[j].Fp;
                if (a == null || b == null || !(a.Known || a.HasKeys) || !(b.Known || b.HasKeys)) continue;
                if (!Conflict(a, b)) continue;
                var ji = Feeds(b, a); var ij = Feeds(a, b);
                var first = (ji && !ij) || (ji == ij && j < i);
                // THE ONE EXCEPTION TO PRODUCER-FIRST (slice 7, amending 5.2). When a person's row feeds a
                // Semanticus row through nothing but the population facet, the Semanticus row goes first. Left
                // producer-first, the person's open cleanup and proposal rows (which may delete, rename or create)
                // held every Semanticus population row, so on a fresh model nothing was Ready and Run the plan did
                // nothing. The order is safe because nothing else links them: an object both touch is its own key
                // and makes the pair conflict on it, which this does not cover, and a member a later create or
                // rename adds is a new gap the next compose derives. Conflict and Independent are untouched.
                //
                // A PERSON'S ROW NEVER HOLDS UP AN AUTOMATED ROW (Kane, 8 Oct 2026: "Waits for: Fix 7 names that Copilot reads as the same
                // word" sat under all six of the assistant's rows, and it was a row only the person could do). The exception now covers
                // the assistant's rows as well as Semanticus's, and a person's row with nothing chosen yet (no change worked out and
                // ticked) yields to every automated row it conflicts with, whatever the facets, because it holds no pending write: it
                // is worked out, and approved by digest, against the model as the automated rows leave it.
                //
                // SEMANTICUS NEVER WAITS FOR WORDS IT DOES NOT NEED (Kane, 8 Oct 2026, D4 automation first). After Jev ranks, Jev's rows read every
                // measure, so an assistant's task ("Describe Legacy Discount %") or an assistant row nobody has chosen a change for fed every one of
                // them by that one read, and nothing was Ready: no Run button. Such a row holds no pending write; its words are worked out, and
                // approved by digest, against the model as the Semanticus rows leave it. A row with changes somebody chose (kept lines) is a real
                // pending write and keeps the ordinary rule. Direction only: Conflict and Independent are untouched.
                if (AutomationLeads(rows[j], rows[i], doers)) first = true;
                else if (AutomationLeads(rows[i], rows[j], doers)) first = false;
                else if (PersonYieldsToAutomation(rows[j], rows[i], doers)) first = false;
                else if (PersonYieldsToAutomation(rows[i], rows[j], doers)) first = true;
                // UNCHOSEN CANDIDATES HOLD NOTHING (Coordinator 26 Sep, F-281 option (a) widened from tasks to every
                // row). A row whose lines nobody has chosen (the cleanup's unused list before any line is kept, the
                // describe sweep before the assistant writes) is a list of possibilities, not a pending write, so a
                // row with real work to do never waits behind it; the candidate row waits instead. Once a person
                // keeps a line the row is no longer a candidate and the ordinary rule orders it, so a kept delete of
                // an object another row writes still goes first. Safe because nothing is written in between that the
                // candidate row relied on: its lines are worked out, and approved by digest, against the model as it
                // is then. Direction only: Conflict and Independent are untouched.
                else if (rows[j].Fp.Candidate && !rows[i].Fp.Candidate) first = false;
                else if (rows[i].Fp.Candidate && !rows[j].Fp.Candidate) first = true;
                // A FORMAT ROW WAITS FOR A SEMANTICUS ROW THAT CHANGES THE SAME OBJECT (THE FORMAT FILL RULE, addendum 4 item 27). What a format
                // fits is decided by what the object is, and a Semanticus row (a data category, a summarize-by, a hide) can change that in a
                // later pass, so the format is worked out after it and never before. Direction only: Conflict and Independent are untouched.
                else if (FormatYieldsTo(rows[i], rows[j], doers)) first = true;
                else if (FormatYieldsTo(rows[j], rows[i], doers)) first = false;
                // THE WORDS NEVER WAIT FOR THE CLEANUP (Plan statement check, 28 Sep). Both are candidate lists, so plan
                // order used to decide, and on a new model the unused sweep (which lists nearly everything) came first
                // and held the words behind a delete nobody had chosen. Words on an object the person later removes are
                // lost with it, which harms nothing; a model with no words cannot reach grade A. Direction only.
                if (WordsOverCleanup(rows[i].Id, rows[j].Id)) first = false;
                else if (WordsOverCleanup(rows[j].Id, rows[i].Id)) first = true;
                // A ROW WITH ITS OWN CHANGES GOES BEFORE THE WORDS (review-fix round 2). The words queue now carries the
                // formats Semanticus cannot prove, and a format, like a description, is chosen for the formula as it
                // stands: an assistant's DAX rewrite of the same measure changes what kind of number it is. So when the
                // words row, a sweep with no lines, meets a row that has worked-out lines of its own, that row goes first
                // and the queue is read from the model it leaves. Both are candidates, so plan order used to decide.
                // Direction only.
                var wordsLed = false;
                if (!WordsOverCleanup(rows[i].Id, rows[j].Id) && !WordsOverCleanup(rows[j].Id, rows[i].Id))
                {
                    if (WorkedOverWords(rows[j], rows[i])) first = wordsLed = true;
                    else if (WorkedOverWords(rows[i], rows[j])) { first = false; wordsLed = true; }
                }
                // A TASK THE PERSON GAVE GOES FIRST (Coordinator 25 Sep, amending 5.2). Against a population or
                // proposal row that Semanticus does not do itself (one that reads the objects facet, or one the
                // person does), the task leads and that row waits for it, however the pair conflicts. Left to plan order, describe/model and
                // cleanup/model, which are almost always open, held every task given from a finding, so Send to
                // your assistant never reached the assistant (lane U3). Direction only: Conflict and Independent
                // are untouched, tasks still wait for real writers of their objects and for each other. A person's
                // row with a kept change to the task's named object IS such a writer (Sol 6 slice 9 review): the task
                // waits for it in either plan order, so an assistant never works an object ahead of the person's own
                // pending change to it. Task-first stays for the population conflict and for a candidate nobody has
                // chosen yet (Coordinator 25 Sep).
                var taskLed = false;
                if (TaskYields(rows[i], rows[j], doers)) first = taskLed = true;
                else if (TaskYields(rows[j], rows[i], doers)) first = false;
                else if (TaskLeads(rows[i].Id, rows[j], doers)) first = false;
                else if (TaskLeads(rows[j].Id, rows[i], doers)) first = taskLed = true;
                // AN UNCHOSEN CLEANUP HOLDS NOTHING, and this outranks every rule above (Kane, 28 Sep: no row may wait
                // for the cleanup; verify m11/plan-rows M1). The candidate rule above leaves a pair of candidates to
                // plan order, so the words row waited for the person's relationship and name rows, and they waited for
                // the unused sweep nobody had chosen. The cleanup a person has kept no line of waits for whatever it
                // conflicts with, candidate or not, so no row reaches it through another. It is the same direction
                // m11/plan-harm gives its always-optional cleanup (Footprint.Optional, the next rule), which now covers a
                // kept delete too. Direction only: Conflict and Independent are untouched.
                var cleanupLed = UnchosenCleanup(rows[j]) != UnchosenCleanup(rows[i]);
                if (cleanupLed) first = UnchosenCleanup(rows[i]);
                // AN OPTIONAL ROW HOLDS NOTHING, and this outranks every rule above (Kane, 28 Sep: no other row may wait
                // for the cleanup, whether or not a report has been read). The plan must be able to END without it, so
                // it waits for whatever it conflicts with and nothing waits for it. Direction only: Conflict and
                // Independent are untouched, and its lines are worked out and approved by digest against the model as
                // it is then, so a row that goes ahead of it changes only what the optional row will offer.
                var optionalLed = rows[j].Fp.Optional != rows[i].Fp.Optional;
                if (optionalLed) first = rows[i].Fp.Optional;
                // THE BEST-PRACTICE ROW IS THE LAST PASS (lane m11/plan-rows). Its fills are worked out on the model as
                // the other rows leave it, so nothing ever waits for it, and it waits only for Semanticus's own rows,
                // which Run the plan lands first. A person's or an assistant's row it shares an object with holds it
                // not at all: that work may never come, and its lines are worked out again, digest-fenced, at the press.
                if (LocalEngine.IsBpaFixItem(rows[j].Id)) continue;
                if (LocalEngine.IsBpaFixItem(rows[i].Id))
                {
                    if (doers == null || !doers.TryGetValue(rows[j].Id, out var jd) || !string.Equals(jd, PlanDoers.Semanticus, StringComparison.Ordinal))
                        continue;
                    first = true;
                }
                if (!first) continue;
                edges[j].Add(i);
                // Each feeds the other: plan order decided it, and the later row says so. Not when the task rule
                // decided it, because then plan order did not.
                if (ji && ij && !taskLed && !cleanupLed && !wordsLed && !optionalLed) mutual.Add(rows[i].Id);
            }

            // Tarjan's strongly connected components, iteratively deep enough for any plan (a plan is tens of rows).
            var index = new int[n]; var low = new int[n]; var onStack = new bool[n]; var comp = new int[n];
            for (var k = 0; k < n; k++) { index[k] = -1; comp[k] = -1; }
            var stack = new Stack<int>(); var counter = 0; var comps = 0;
            void Visit(int v)
            {
                index[v] = low[v] = counter++; stack.Push(v); onStack[v] = true;
                foreach (var w in edges[v])
                {
                    if (index[w] < 0) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                }
                if (low[v] == index[v])
                {
                    int w;
                    do { w = stack.Pop(); onStack[w] = false; comp[w] = comps; } while (w != v);
                    comps++;
                }
            }
            for (var k = 0; k < n; k++) if (index[k] < 0) Visit(k);

            var waits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var reordered = new HashSet<string>(mutual, StringComparer.Ordinal);
            for (var j = 0; j < n; j++)
            foreach (var i in edges[j])
            {
                if (comp[i] == comp[j])
                {
                    // Inside a loop: only earlier-to-later survives, and the later row says why.
                    if (j > i) continue;
                    reordered.Add(rows[i].Id);
                }
                if (!waits.TryGetValue(rows[i].Id, out var list)) waits[rows[i].Id] = list = new List<string>();
                list.Add(rows[j].Id);
            }
            // Plan order inside each list, so the first blocker named is the one that comes first.
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var k = 0; k < n; k++) rank[rows[k].Id] = k;
            foreach (var list in waits.Values) list.Sort((x, y) => rank[x].CompareTo(rank[y]));
            return (waits, reordered);
        }

        /// <summary>True when <paramref name="format"/> is a Jev format row (measures or column format) and <paramref name="other"/> is a row
        /// Semanticus does itself that writes one of the same objects: the format row waits for it.</summary>
        private static bool FormatYieldsTo((string Id, Footprint Fp) format, (string Id, Footprint Fp) other, IReadOnlyDictionary<string, string> doers)
        {
            if (!IsJevFormatRow(format.Id) || JevFamily.IsJevItem(other.Id) || PlanTasks.IsTaskId(other.Id)) return false;
            string doer = null;
            doers?.TryGetValue(other.Id, out doer);
            if (!string.Equals(doer, PlanDoers.Semanticus, StringComparison.Ordinal)) return false;
            return ObjectsIn(format.Fp.Writes).Any(o => other.Fp.Writes.Contains(o));
        }

        private static bool IsJevFormatRow(string id) =>
            id != null && (id.StartsWith(JevFamily.ItemId(MeasuresFamily.Name, ""), StringComparison.Ordinal)
                           || id.StartsWith(JevFamily.ItemId(ColumnFormatFamily.Name, ""), StringComparison.Ordinal));

        /// <summary>True when <paramref name="taskId"/> is a Plan task and <paramref name="other"/> is a derived row
        /// that yields to it: a population row (it reads the objects facet) or a row the person does (Needs you).
        /// Semanticus's own rows never yield: they are the safe automatic work every other row already waits for
        /// (the exception above), Run the plan clears them, and a task on their object waits for them as for any
        /// real writer. Another task, and a derived row that is neither, keep the rule above.</summary>
        private static bool TaskLeads(string taskId, (string Id, Footprint Fp) other, IReadOnlyDictionary<string, string> doers)
        {
            if (!PlanTasks.IsTaskId(taskId) || PlanTasks.IsTaskId(other.Id)) return false;
            string doer = null;
            doers?.TryGetValue(other.Id, out doer);
            if (string.Equals(doer, PlanDoers.Semanticus, StringComparison.Ordinal)) return false;
            return other.Fp.Reads.Contains(Objects) || string.Equals(doer, PlanDoers.You, StringComparison.Ordinal);
        }

        /// <summary>True when <paramref name="task"/> is a Plan task and <paramref name="other"/> is a derived row the
        /// person does that holds a KEPT change to one of the objects the task names: the person's pending change to
        /// that object comes first, whatever the plan order (Sol 6 slice 9 review). A candidate the row only declares
        /// (cleanup's unused objects before any line is worked out and kept) is not a pending write, so the task still
        /// leads (Coordinator 25 Sep); the approve fence refuses anything the task changed under a later kept line.</summary>
        private static bool TaskYields((string Id, Footprint Fp) task, (string Id, Footprint Fp) other, IReadOnlyDictionary<string, string> doers)
        {
            if (!PlanTasks.IsTaskId(task.Id) || PlanTasks.IsTaskId(other.Id)) return false;
            string doer = null;
            doers?.TryGetValue(other.Id, out doer);
            if (!string.Equals(doer, PlanDoers.You, StringComparison.Ordinal)) return false;
            return ObjectsIn(task.Fp.Writes).Any(o => other.Fp.Chosen.Contains(o));
        }

        /// <summary>True when <paramref name="automated"/> is a row Semanticus does and <paramref name="other"/> is an assistant's or a person's row (or a task)
        /// with no change chosen yet: the Semanticus row goes first, whatever way the two read each other.</summary>
        private static bool AutomationLeads(
            (string Id, Footprint Fp) automated, (string Id, Footprint Fp) other, IReadOnlyDictionary<string, string> doers)
        {
            // The date mark is NOT in this: what it reads is exactly what a task about a time-intelligence measure can change (DateMarkReaders),
            // so it truly depends on that task and keeps the ordinary rule. This is for the rows that read every measure only to look for a name or a gap.
            if (doers == null || PlanTasks.IsTaskId(automated.Id)
                || automated.Id.StartsWith("datetable/", StringComparison.Ordinal)
                || automated.Id.StartsWith(JevFamily.ItemPrefix + DateTableFamily.Name + ".", StringComparison.Ordinal)
                || !doers.TryGetValue(automated.Id, out var ad) || !string.Equals(ad, PlanDoers.Semanticus, StringComparison.Ordinal))
                return false;
            if (!doers.TryGetValue(other.Id, out var od)
                || !(string.Equals(od, PlanDoers.Assistant, StringComparison.Ordinal) || string.Equals(od, PlanDoers.You, StringComparison.Ordinal)))
                return false;
            // A task's lines are not worked out (Asked), so it never has a chosen write; a derived row has one once a line is kept.
            return other.Fp.Chosen.Count == 0;
        }

        /// <summary>True when <paramref name="person"/> is a row the person does and <paramref name="automated"/> is a row Semanticus or the
        /// assistant does, and the person's row must wait for it: it feeds the automated row through the population facet only, or it
        /// has nothing chosen yet.</summary>
        private static bool PersonYieldsToAutomation(
            (string Id, Footprint Fp) person, (string Id, Footprint Fp) automated, IReadOnlyDictionary<string, string> doers)
        {
            if (doers == null
                || !doers.TryGetValue(person.Id, out var pd) || !string.Equals(pd, PlanDoers.You, StringComparison.Ordinal)
                || !doers.TryGetValue(automated.Id, out var ad)
                || !(string.Equals(ad, PlanDoers.Semanticus, StringComparison.Ordinal) || string.Equals(ad, PlanDoers.Assistant, StringComparison.Ordinal)))
                return false;
            // A task the person claimed has its own rules (TaskLeads, TaskYields); only the derived rows are covered here.
            if (PlanTasks.IsTaskId(person.Id)) return false;
            if (person.Fp.Candidate && person.Fp.Chosen.Count == 0) return true;
            return PersonFeedsAutomationOnlyByPopulation(person.Fp, automated.Fp);
        }

        /// <summary>The person's row feeds the automated row through the objects facet and nothing else, the automated row does not
        /// feed the person's row back, and they share no write that is not an event facet.</summary>
        private static bool PersonFeedsAutomationOnlyByPopulation(Footprint p, Footprint q)
        {
            if (Feeds(q, p)) return false;
            if (p.Writes.Any(w => !EventFacets.Contains(w) && q.Writes.Contains(w))) return false;
            var through = p.Writes.Where(w => q.Reads.Contains(w)).ToArray();
            return through.Length == 1 && string.Equals(through[0], Objects, StringComparison.OrdinalIgnoreCase);
        }

        // ---- stamping the composed plan -----------------------------------------------------------------------

        /// <summary>Stamp every row's footprint, then WaitsFor on the open rows. <paramref name="ordered"/> is in
        /// plan order. <paramref name="isOpen"/> says which rows still count: done, set aside and applied-checking
        /// rows neither wait nor block (contract 5.2). A held row still counts, so what depends on it stays
        /// Waiting and names it.</summary>
        public static void Stamp(IReadOnlyList<ModelPlanItem> ordered, FootprintInputs x, Func<ModelPlanItem, bool> isOpen)
        {
            var open = new List<(string Id, Footprint Fp)>();
            var byId = new Dictionary<string, ModelPlanItem>(StringComparer.Ordinal);
            foreach (var item in ordered)
            {
                if (item?.Id == null) continue;
                var fp = PlanTasks.IsTaskId(item.Id) ? Asked(item) : Of(item.Id, PendingLines(item), x);
                if (!PlanTasks.IsTaskId(item.Id))
                {
                    var pending = PendingLines(item);
                    fp.Chosen.UnionWith(ObjectsIn(FromLines(pending.Where(l => l.Kept).ToArray(), x).Writes));
                    // With lines: a candidate while none is kept, because approving it would write nothing. Without
                    // lines: only a POPULATION row (it reads the objects facet: cleanup, describe, the proposal sweeps)
                    // that somebody other than Semanticus does. Semanticus's own rows are what Run the plan will do, and
                    // a single choice (which table is the date table) is a real pending write its readers wait for.
                    var semanticus = string.Equals(item.Doer, PlanDoers.Semanticus, StringComparison.Ordinal);
                    fp.Candidate = pending.Count > 0
                        ? !pending.Any(l => l.Kept)
                        : !semanticus && fp.Known && fp.Reads.Contains(Objects);
                    fp.Worked = pending.Count > 0;
                }
                fp.Optional = item.Optional;
                item.Reads = fp.Reads.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                item.Writes = fp.Writes.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                item.FootprintKnown = fp.Known;
                item.WaitsFor = Array.Empty<PlanWaitRef>();
                if (isOpen(item) && !string.Equals(item.State, ModelPlanItemStates.Applied, StringComparison.Ordinal)
                    && byId.TryAdd(item.Id, item))
                    open.Add((item.Id, fp));
            }

            var (waits, reordered) = Waits(open, byId.ToDictionary(kv => kv.Key, kv => kv.Value.Doer, StringComparer.Ordinal));
            foreach (var kv in waits)
            {
                var item = byId[kv.Key];
                // A PAUSED blocker is named first (contract 5.2: a row that waits for a paused row stays Waiting and its
                // sub line names the paused row). The sub line and the page name only the first blocker, and in plan
                // order a paused row further down held the row without being named. The rest keep plan order.
                item.WaitsFor = kv.Value.OrderBy(id => byId[id].Held ? 0 : 1)
                    .Select(id => new PlanWaitRef { Id = id, Title = byId[id].Title }).ToArray();
                // An optional row keeps its word on the collapsed row: it nearly always waits (it conflicts with every
                // row it could go ahead of), and this line replaces its sub line (verify, 28 Sep, L1).
                item.Sub = (item.Optional ? "Optional. " : "") + "Waits for: " + item.WaitsFor[0].Title
                         + (item.WaitsFor.Length > 1 ? " and " + (item.WaitsFor.Length - 1) + " more" : "");
                // The engine-written qualifier, not a reason: a row's reasons are its family's three sentences.
                if (reordered.Contains(kv.Key) && string.IsNullOrEmpty(item.StateNote))
                    item.StateNote = OrderedByPlanNote;
            }
        }

        /// <summary>The lines a row would still write: a set that is waiting to be approved. An applied set is a
        /// receipt and writes nothing more.</summary>
        private static IReadOnlyList<ModelPlanChangeLine> PendingLines(ModelPlanItem item)
        {
            var set = item.ChangeSet;
            if (set == null || !string.Equals(set.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal))
                return Array.Empty<ModelPlanChangeLine>();
            return set.Lines ?? Array.Empty<ModelPlanChangeLine>();
        }

        // ---- the families (contract 5.1) ----------------------------------------------------------------------

        /// <summary>Every family id prefix the plan composes, and the function that states its footprint. A
        /// prefix the plan composes that is missing here fails PlanFootprintTests' completeness test.</summary>
        internal static readonly IReadOnlyDictionary<string, Func<string, IReadOnlyList<ModelPlanChangeLine>, FootprintInputs, Footprint>> Families =
            new Dictionary<string, Func<string, IReadOnlyList<ModelPlanChangeLine>, FootprintInputs, Footprint>>(StringComparer.Ordinal)
            {
                ["safefix/"] = SafeFix,
                ["describe/"] = Describe,
                ["bpafix/"] = BpaFix,
                ["words/"] = Words,
                ["cleanup/"] = Cleanup,
                ["check/"] = Check,
                ["recheck/"] = Recheck,
                ["ship/"] = Ship,
                ["proposal/"] = Proposal,
                ["datetable/"] = DateTableRow,
                ["calendar/"] = CalendarRow,
                ["starter/"] = Starter,
                ["target/"] = (_, _, _) => Footprint.Unknown(),   // an aim: words only, nothing the engine can name
                [LocalEngine.AssistantItemPrefix] = Assistant,
                [JevFamily.ItemPrefix] = Jev,
            };

        /// <summary>The footprint of one row. With lines, the writes are the lines' own and the family adds its
        /// reads; with none yet, the family's declared footprint over the objects its findings name.</summary>
        public static Footprint Of(string itemId, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            if (string.IsNullOrEmpty(itemId)) return Footprint.Unknown();
            x ??= new FootprintInputs();
            lines ??= Array.Empty<ModelPlanChangeLine>();
            foreach (var kv in Families)
                if (itemId.StartsWith(kv.Key, StringComparison.Ordinal))
                    return kv.Value(itemId, lines, x);
            return Footprint.Unknown();
        }

        /// <summary>What a set of lines writes, and what each line needs to be true, read off the op. Every line
        /// commits, so every line writes <see cref="Revision"/>. Returns an unknown footprint when a line's
        /// effect cannot be stated (a BPA fix can do anything its rule says).</summary>
        internal static Footprint FromLines(IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var fp = new Footprint();
            foreach (var l in lines ?? Array.Empty<ModelPlanChangeLine>())
            {
                var op = (l.Op ?? "").ToLowerInvariant();
                var target = l.Target;
                fp.Write(Revision);
                // WHAT THE LINE REACHES ITS OBJECT BY (lane m11/plan-waits, verify D3). A line on a column, a measure or
                // anything else inside a table reaches it through the table's name, and a line on a relationship through
                // its two columns' names, so a rename or a delete of those is ordered against it. The kinds below add
                // what they name only in their arguments.
                fp.ReadAll(NamesUnder(x.Model, target));
                switch (op)
                {
                    case "bpa_fix":
                        // One property of one object: BpaAnalyzer.PreviewFix is an assignment, never a script. A name
                        // or a formula is not something the Plan's best-practice row writes, and stays unknown.
                        if (string.Equals(l.Property, "name", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(l.Property, "expression", StringComparison.OrdinalIgnoreCase)) return Footprint.Unknown();
                        // A property of the object, not the object: a key of its own, so the row waits for every
                        // other writer of the object and holds none of them (BpaFix below).
                        fp.Write(BpaKey(target, l.Property)).Read(target);
                        // MARK_PRIMARY_KEYS sets IsKey, and a key set clears the key on every other column of the
                        // table, as the date mark's does (verify m11/plan-rows L7): it writes what a mark writes, so
                        // the date mark and the key status rules are ordered against it.
                        if (string.Equals(l.Property, "iskey", StringComparison.OrdinalIgnoreCase)
                            && target != null && target.StartsWith("column:", StringComparison.Ordinal) && target.IndexOf('/') > 0)
                            Marks(fp, x.Model, TableOf(target), target.Substring(target.IndexOf('/') + 1));
                        break;
                    case "create_measure":
                        fp.Write(target).WriteAll(Claims(target)).Write(Objects).ReadAll(RefsIn(x.Model, l.After, TableOf(target)))
                          .ReadAll(NamesIn(x.Model, l.After, TableOf(target)));
                        if (ReadsTheDateTable(x.Model, l.After, TableOf(target))) fp.Read(DateTable);
                        break;
                    case "delete":
                    case "delete_if_unused":
                        fp.Write(target).Read(target).Write(Objects).WriteAll(Claims(target));
                        // A DELETE OF A TABLE DELETES EVERYTHING ON IT, and every member's name goes with it. A
                        // measure's name is claimed across the WHOLE model (Claims), so a delete of the table that
                        // holds one frees that name for a new measure on any table. Writing the table's own claim and
                        // nothing else had the two meet on nothing: the engine called a delete of Scratch and a new
                        // Customer[Scratch Count] independent, while one order is refused outright ("A measure named
                        // Scratch Count already exists in this model"). Round 3, the sibling of PW2.
                        foreach (var member in MembersRemovedBy(x.Model, target))
                            fp.Write(member).WriteAll(Claims(member));
                        if (IsRole(target)) fp.Write(Roles);
                        // Only one relationship between two tables may be active (GuardNoOtherActiveOnTablePair), so a
                        // delete that removes a relationship frees that pair for another (verify PW3). Removing a table or
                        // a column removes every relationship it ends too, so each writes the relationships at those tables.
                        foreach (var t in RelationshipTablesRemovedBy(x.Model, target)) fp.Write(RelationshipsAt(t));
                        break;
                    case "rename":
                        // The old ref, the new ref and every object whose DAX names it (the fixup set). Both names move.
                        fp.Write(target).Read(target).Write(Objects).Write(Renamed(target, l.After))
                          .WriteAll(Claims(target)).WriteAll(Claims(Renamed(target, l.After)));
                        fp.WriteAll(Users(x.Model, target));
                        if (IsRole(target)) fp.Write(Roles);
                        break;
                    case "set_dax":
                        fp.Write(target).Read(target).ReadAll(RefsIn(x.Model, l.After, TableOf(target)))
                          .ReadAll(NamesIn(x.Model, l.After, TableOf(target)));
                        if (ReadsTheDateTable(x.Model, l.After, TableOf(target))) fp.Read(DateTable);
                        break;
                    case "set_data_type":
                    case "set_column_data_type":
                        // A column that becomes, or stops being, a date changes what the date mark can choose from.
                        fp.Write(target).Read(target).Read(DateTable);
                        break;
                    case "mark_date_table":
                    {
                        fp.Write(target).Write(DateTable).Read(DateTable);
                        var table = TableOf(target);
                        if (table == null) break;
                        if (!string.IsNullOrWhiteSpace(l.After)) Marks(fp, x.Model, table, l.After);
                        else if (x.Model.ColumnsByTable.TryGetValue(table, out var cols))
                        {
                            // No key named: the mark chooses among the table's date columns, and any of them may become it.
                            fp.ReadAll(cols).WriteAll(cols);
                            Marks(fp, x.Model, table, null);
                        }
                        break;
                    }
                    case "generate_date_table":
                        // Adds a calculated table (and its columns) and, unless told not to, marks it. A create writes the
                        // objects facet, a mark writes the date table facet, and only one table can be marked, so it reads
                        // which one is. The name ("Date", else the first free "Date N") and the range (the facts' own date
                        // columns) were read off the tables there are, so it reads the objects facet too.
                        fp.Write(target).Read(target).WriteAll(Claims(target)).Write(Objects).Read(Objects).Write(DateTable).Read(DateTable);
                        if (Arg(l, "tableName") is string gt)
                            // It declares CALENDAR's Date column and, when it marks, keys it (GenerateDateTableCore).
                            fp.WriteAll(Claims("table:" + gt)).Write("column:" + gt + "/Date").WriteAll(Claims("column:" + gt + "/Date"));
                        foreach (var e in new[] { Arg(l, "startExpr"), Arg(l, "endExpr") })
                            fp.ReadAll(RefsIn(x.Model, e)).ReadAll(NamesIn(x.Model, e, null));
                        break;
                    case "set_synonyms":
                    case "create_linguistic_schema":
                        fp.Write(target).Read(target).Write(Culture);
                        break;
                    case "set_ai_instructions":
                        fp.Write(AiInstructions);
                        break;
                    case "set_ai_data_schema":
                        // The flag is one entry in the model's schema, and the schema's own rule (AISCHEMA-DEP-MISSING)
                        // ties entries together: a measure put back needs what it uses in, and a field left out needs
                        // every measure that uses it out. So a line reads the entries it leans on, and the row that
                        // provides them goes first: put a field back before the measure that uses it, and leave the
                        // measure out before the field (the verifier's D4, 26 Sep). It also reads its own object (is
                        // it still there and visible) and its table (a table left out carries its fields).
                        fp.Write(target).Read(target).Write(AiDataSchema).ReadAll(AiSchemaLeansOn(x.Model, target, l.After));
                        break;
                    case "create_relationship":
                    {
                        // The from column is the target. A Jev line names the key it joins at the head of its after
                        // ("Product[ProductKey], many to one, ..."), resolved against the model's own column refs
                        // (RelationshipKeysOf). An assistant's line names both ends in its arguments (fromColumnRef,
                        // toColumnRef) and read by their names. Either way the ends it names are all it touches.
                        fp.Write(target).Read(target).Write(Objects);
                        if (TableOf(target) is string from) fp.Write(RelationshipsAt(from));
                        var keys = RelationshipKeysOf(l.After, x.Model).ToList();
                        foreach (var (keyTable, keyRef) in keys)
                            fp.Read(keyRef).Write(RelationshipsAt(keyTable));
                        var ends = new[] { Arg(l, "fromColumnRef"), Arg(l, "toColumnRef") }.Where(e => e != null).ToList();
                        foreach (var end in ends)
                        {
                            fp.Read(end).Read(NameOf(end)).ReadAll(NamesUnder(x.Model, end));
                            if (TableOf(end) is string et) fp.Write(RelationshipsAt(et));
                        }
                        // A line that names its other end nowhere (no key in its after, no arguments) can join any table,
                        // so it writes the relationships at every one: wide is the safe side.
                        if (keys.Count == 0 && ends.Count == 0)
                            foreach (var t in x.Model.ColumnsByTable.Keys) fp.Write(RelationshipsAt(t));
                        break;
                    }
                    case "set_relationship_crossfilter":
                    case "set_relationship_active":
                    case "set_relationship_cardinality":
                        fp.Write(target).Read(target);
                        foreach (var t in RelationshipTablesOf(x.Model, target)) fp.Write(RelationshipsAt(t));
                        break;
                    // THE CREATES ApplyOneItem runs (PlanExecutorKinds.cs, the composer's kinds). Each adds a member to
                    // some population, so each writes the objects facet (the careful rule). They used to fall through to
                    // the default and write only their target. Each also writes the NAME of what it makes, which a line
                    // that names the new object in its arguments reads (a column added to a table another row adds).
                    case "create_column":
                        // The table is in the arguments (tableRef), and the new column is spelled with its name.
                        fp.Write(target).Read(target).WriteAll(Claims(target)).Write(Objects);
                        if (Arg(l, "tableRef") is string ct)
                        {
                            fp.Read(NameOf(ct));
                            if (TableOf(ct) is string ctn && Arg(l, "name") is string cn)
                                fp.Write("column:" + ctn + "/" + cn).WriteAll(Claims("column:" + ctn + "/" + cn));
                        }
                        break;
                    case "create_import_table":
                    case "create_calculation_group":
                        fp.Write(target).Read(target).WriteAll(Claims(target)).Write(Objects);
                        if (Arg(l, "name") is string tn) fp.Write("table:" + tn).WriteAll(Claims("table:" + tn));
                        break;
                    case "create_calculation_item":
                    {
                        // The line's target is the group; the item's own name, and the group if it says so, are only in
                        // the line's arguments. Its DAX reaches every table it names by that table's name.
                        var group = Arg(l, "calcGroupRef") ?? target;
                        fp.Write(target).Read(target).Read(group).Read(NameOf(group)).Write(Objects)
                          .ReadAll(RefsIn(x.Model, l.After)).ReadAll(NamesIn(x.Model, l.After, null));
                        if (TableOf(group) is string gn && Arg(l, "name") is string iname)
                            fp.Write("calcitem:" + gn + "/" + iname).WriteAll(Claims("calcitem:" + gn + "/" + iname));
                        if (ReadsTheDateTable(x.Model, l.After, null)) fp.Read(DateTable);
                        break;
                    }
                    case "create_role":
                        fp.Write(target).Read(target).WriteAll(Claims(target)).Write(Objects).Write(Roles);
                        if (Arg(l, "name") is string rn) fp.Write("role:" + rn).WriteAll(Claims("role:" + rn));
                        break;
                    case "set_table_permission":
                    {
                        // Writes a permission on a role the line names only in its arguments, and needs that role: a rename
                        // or a delete of a role writes the roles facet, and of this one its name. The table can be named
                        // in the arguments too, and the filter's DAX reaches every table it names by that table's name.
                        var table = Arg(l, "tableRef") ?? target;
                        fp.Write(Roles).Read(Roles).Read(target).Read(table).Read(NameOf(table))
                          .ReadAll(RefsIn(x.Model, l.After, TableOf(table))).ReadAll(NamesIn(x.Model, l.After, TableOf(table)));
                        if (Arg(l, "roleName") is string role) fp.Read("role:" + role).Read(NameOf("role:" + role));
                        break;
                    }
                    case "generate_time_intelligence":
                    {
                        // Adds measures beside its base measure; their names are read off the measures there are, and
                        // time intelligence rests on the date table. The date column is in the arguments.
                        fp.Read(target).Write(Objects).Read(Objects).Read(DateTable);
                        if (Arg(l, "baseMeasureRef") is string bm) fp.Read(bm).Read(NameOf(bm)).ReadAll(NamesUnder(x.Model, bm));
                        var dc = Arg(l, "dateColumn");
                        if (dc != null && dc.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
                            fp.Read(dc).Read(NameOf(dc)).ReadAll(NamesUnder(x.Model, dc));
                        else fp.ReadAll(RefsIn(x.Model, dc)).ReadAll(NamesIn(x.Model, dc, null));
                        break;
                    }
                    case "set_sort_by_column":
                        fp.Write(target).Read(target);
                        if (TableOf(target) is string st && !string.IsNullOrWhiteSpace(l.After)) fp.Read("column:" + st + "/" + l.After);
                        break;
                    default:
                        // A property of one object: the object is written, and a change reads what it changes. A census
                        // setting on a relationship (set_property, CrossFilteringBehavior) is a relationship change like
                        // set_relationship_crossfilter, so it writes the relationships at its tables too.
                        fp.Write(target).Read(target);
                        foreach (var t in RelationshipTablesOf(x.Model, target)) fp.Write(RelationshipsAt(t));
                        break;
                }
            }
            return fp;
        }

        private static string Arg(ModelPlanChangeLine l, string key) =>
            l.Args != null && l.Args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        private static bool IsRole(string objRef) => objRef != null && objRef.StartsWith("role:", StringComparison.OrdinalIgnoreCase);

        /// <summary>The names a ref is reached by, beyond its own: the table of anything inside a table, and for a
        /// relationship the two columns and their tables (its name is spelled with them).</summary>
        private static IEnumerable<string> NamesUnder(FootprintSnapshot m, string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) yield break;
            if (objRef.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase))
            {
                if (m.RelationshipColumns.TryGetValue(objRef.Substring("relationship:".Length), out var ends))
                    foreach (var c in ends)
                    {
                        yield return NameOf(c);
                        if (TableOf(c) is string ct) yield return NameOf("table:" + ct);
                    }
                yield break;
            }
            var colon = objRef.IndexOf(':');
            if (colon > 0 && objRef.IndexOf('/', colon) > colon && TableOf(objRef) is string t) yield return NameOf("table:" + t);
        }

        /// <summary>The names a DAX text reaches the model by: the table of every column or measure it names, and every
        /// table it names on its own (COUNTROWS ( Customer )). A rename or a delete of that table makes the text refer to
        /// nothing, and the rename's fixup cannot rewrite a text that is not in the model yet.</summary>
        private static IEnumerable<string> NamesIn(FootprintSnapshot m, string dax, string homeTable)
        {
            if (string.IsNullOrEmpty(dax) || m == null) yield break;
            foreach (var r in RefsIn(m, dax, homeTable)) if (TableOf(r) is string t) yield return NameOf("table:" + t);
            foreach (var t in m.ColumnsByTable.Keys)
                if (dax.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 && NamesTable(dax, t)) yield return NameOf("table:" + t);
        }

        private static Footprint LinesOrDeclared(IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x, Func<Footprint> declared) =>
            lines.Count > 0 ? FromLines(lines, x) : declared();

        private static IEnumerable<ReadinessFinding> Live(FootprintInputs x) =>
            (x.Findings ?? Array.Empty<ReadinessFinding>()).Where(f => f != null && !f.Waived);

        /// <summary>A safe fix reads the object it fixes and the relationships at its table (the key and
        /// foreign-key rules read them), and is a population row.</summary>
        private static Footprint SafeFix(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var category = id.Substring("safefix/".Length);
            var fp = LinesOrDeclared(lines, x, () => new Footprint()
                .WriteAll(Live(x).Where(f => f.Fix == nameof(FixKind.SafeFix) && string.Equals(f.Category ?? "BestPractice", category, StringComparison.Ordinal))
                                 .Select(f => f.ObjectRef))
                .Write(Revision));
            if (!fp.Known) return fp;
            foreach (var w in ObjectsIn(fp.Writes)) { fp.Read(w); if (TableOf(w) is string t) fp.Read(RelationshipsAt(t)); }
            if (SafeFixReadsKeys(category)) fp.Read(Keys);
            return fp.Read(Objects);
        }

        /// <summary>A description reads its object; "every undescribed thing" is a population.</summary>
        private static Footprint Describe(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var fp = LinesOrDeclared(lines, x, () => new Footprint()
                .WriteAll(Live(x).Where(f => f.Fix == nameof(FixKind.AiContent)
                                         && ((f.Category ?? "").IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0
                                             || (f.RuleId ?? "").StartsWith("DESC-", StringComparison.OrdinalIgnoreCase)))
                                 .Select(f => f.ObjectRef))
                .Write(Revision));
            if (!fp.Known) return fp;
            return fp.ReadAll(ObjectsIn(fp.Writes).ToArray()).Read(Objects);
        }

        /// <summary>The best-practice row writes the objects its lines name, one property each, and reads them.</summary>
        /// <summary>THE LAST PASS. It writes one property per line, under a key of its own ("model:bpa/..."), and reads the
        /// objects, so every row or task that writes one of those objects feeds it and goes first, and it holds none of
        /// them: a best-practice fill (hidden, MDX, summarize, an empty format) never changes what another row reads. The
        /// fixes it makes are worked out on the model as that row left it, when Run the plan comes back to it.</summary>
        private static Footprint BpaFix(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            if (lines.Count > 0)
            {
                var fromLines = FromLines(lines, x);
                return fromLines.Known ? fromLines.Read(Objects) : fromLines;
            }
            var fp = new Footprint().Write(Revision).Read(Objects);
            foreach (var t in x.BpaFixTargets ?? Array.Empty<string>()) fp.Write(BpaKey(t, null)).Read(t);
            foreach (var t in x.BpaFixKeyTargets ?? Array.Empty<string>())
                if (t.StartsWith("column:", StringComparison.Ordinal) && t.IndexOf('/') > 0)
                    Marks(fp, x.Model, TableOf(t), t.Substring(t.IndexOf('/') + 1));
            return fp;
        }

        private static string BpaKey(string target, string property) =>
            "model:bpa/" + (target ?? "") + (string.IsNullOrEmpty(property) ? "" : "/" + property.ToLowerInvariant());

        /// <summary>The words row writes words on the objects its findings name (a name among them, so it reads the
        /// objects facet like the describe sweep), and is a candidate until the assistant writes.</summary>
        private static Footprint Words(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var fp = LinesOrDeclared(lines, x, () => new Footprint()
                .WriteAll(Live(x).Where(LocalEngine.IsWordsFinding).Select(f => f.ObjectRef).Concat(x.WordsFormatTargets).Distinct(StringComparer.Ordinal))
                .Write(Revision));
            if (!fp.Known) return fp;
            return fp.ReadAll(ObjectsIn(fp.Writes).ToArray()).Read(Objects);
        }

        /// <summary>Unused is a claim about every referrer: each expression, the relationships at its table, and
        /// the report reading. It deletes, so it writes the objects facet; with reports chosen the verdict rests on
        /// a revision-stamped reading.</summary>
        private static Footprint Cleanup(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var fp = LinesOrDeclared(lines, x, () => new Footprint().WriteAll(x.UnusedSafe).Write(Objects).Write(Revision));
            if (!fp.Known) return fp;
            foreach (var w in ObjectsIn(fp.Writes).ToArray()) { fp.Read(w); if (TableOf(w) is string t) fp.Read(RelationshipsAt(t)); }
            fp.ReadAll(x.Model.MeasureRefs).ReadAll(x.Model.CalculatedRefs).Read(Reports).Read(Objects);
            if (x.ReportsChosen) fp.Read(Revision);
            return fp;
        }

        /// <summary>A check is saved beside the model, not in it, and rests on the report reading.</summary>
        private static Footprint Check(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var fp = new Footprint().Read(Revision).Read(Reports);
            var subject = id.Substring("check/".Length);
            if (subject.Contains(':')) fp.Read(subject);
            return fp;
        }

        private static Footprint Recheck(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x) =>
            new Footprint().Read(Revision).Read(Reports).Write(Reports);

        /// <summary>Saving writes nothing in the model. Saving moves the source version, which makes a waiting set
        /// out of date; that is the source guard (LocalEngine.PlanChanges.cs), not a dependency.</summary>
        private static Footprint Ship(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x) => new Footprint();

        /// <summary>A proposal family: with lines, the lines; with none yet, the objects its findings name, plus
        /// what the family's fix can reach. A family whose fix may rename, create or delete writes the objects
        /// facet, because which of those the person picks is not known until they pick.</summary>
        private static Footprint Proposal(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var family = id.Substring("proposal/".Length);
            var findings = Live(x).Where(f => LocalEngine.IsProposalFinding(f) && x.ProposalFamily(f) == family).ToArray();
            var fp = LinesOrDeclared(lines, x, () =>
            {
                var d = new Footprint().WriteAll(findings.Select(f => f.ObjectRef)).Write(Revision);
                switch (family)
                {
                    case "collisions":
                    case "names":
                    case "duplicate-measures":
                    case "implicit-measures":
                        d.Write(Objects);
                        break;
                    case "relationships":
                        foreach (var f in findings) if (TableOf(f.ObjectRef) is string t) d.Write(RelationshipsAt(t));
                        foreach (var f in findings) foreach (var t in RelationshipTablesOf(x.Model, f.ObjectRef)) d.Write(RelationshipsAt(t));
                        break;
                    case "dates":
                        d.Write(DateTable).Write(Calendar);
                        break;
                    case "synonyms":
                        d.Write(Culture);
                        break;
                    case "ai-settings":
                        d.Write(AiInstructions).Write(AiDataSchema);
                        break;
                }
                return d;
            });
            if (!fp.Known) return fp;
            fp.ReadAll(ObjectsIn(fp.Writes).ToArray()).Read(Objects);
            if (family is "collisions" or "synonyms" or "names") fp.Read(Culture);
            if (ProposalReadsKeys(x, family)) fp.Read(Keys);
            if (family == "implicit-measures")
                foreach (var w in ObjectsIn(fp.Writes).ToArray())
                    if (TableOf(w) is string t && x.Model.MeasuresByTable.TryGetValue(t, out var ms)) fp.ReadAll(ms);
            return fp;
        }

        /// <summary>Marks the table and its key column, and reads which table is marked and what the mark can change
        /// (<see cref="DateMarkReaders"/>).
        ///
        /// BEFORE ITS LINE IS WORKED OUT (Run the plan does that) the rule's row still knows what it will mark when the
        /// model names one table and date column (<see cref="FootprintInputs.DateMark"/>, the answer that titled it
        /// "Mark Date as the date table"): the work-out asks PlanDateMark again and marks what it answers. So the row
        /// writes that table and key, and reads what the mark can change plus everything that answer rests on: the DAX
        /// that reads a candidate date column (a rewrite can take the last read away), the relationships (a table that
        /// becomes the many side drops out) and the date table facet. A line that ADDS a read of a candidate date
        /// column, or changes a column's data type, reads the facet itself (FromLines), so that conflict is kept from
        /// the other side. With no answer, which table is marked is the person's choice, and the row reads wide.</summary>
        private static Footprint DateTableRow(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var mark = Live(x).FirstOrDefault(f => string.Equals(f.RuleId, "DATE-MARK", StringComparison.OrdinalIgnoreCase));
            var named = lines.Count == 0 && mark != null ? x.DateMark : null;
            var fp = LinesOrDeclared(lines, x, () => named != null
                ? new Footprint().Write(named.TableRef).Write("column:" + named.Table + "/" + named.Column).Write(DateTable).Write(Revision)
                : new Footprint().Write(mark?.ObjectRef).Write(DateTable).Write(Revision));
            if (!fp.Known) return fp;
            fp.Write(DateTable).Read(DateTable).Read(Objects);
            if (named != null)
            {
                fp.ReadAll(DateMarkReaders(x.Model, new[] { named.Table })).ReadAll(x.Model.DateReaders);
                foreach (var t in x.Model.ColumnsByTable.Keys) fp.Read(RelationshipsAt(t));
            }
            else fp.ReadAll(DateMarkReaders(x.Model, lines));
            if (named != null) Marks(fp.Read(named.TableRef), x.Model, named.Table, named.Column);
            else if (lines.Count == 0 && TableOf(mark?.ObjectRef) is string table)
            {
                fp.Read("table:" + table);
                // Which column becomes the key is not known until it is chosen, so all of them may be written.
                if (x.Model.ColumnsByTable.TryGetValue(table, out var cols)) fp.ReadAll(cols).WriteAll(cols);
                Marks(fp, x.Model, table, null);
            }
            return fp;
        }

        /// <summary>Defining a calendar creates a model-level object and reads which table is marked.</summary>
        private static Footprint CalendarRow(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x) =>
            LinesOrDeclared(lines, x, () => new Footprint().Write(Calendar).Write(Objects).Write(Revision))
                .Read(DateTable).Read(Calendar).ReadAll(x.Model.MeasureRefs).Read(Objects);

        /// <summary>The starter rule reads every column of its fact table (type, summary, format, key role), the
        /// relationships at it, and every measure (a column a measure already sums gets none; a name taken is
        /// refused). It creates, so it writes the objects facet.</summary>
        private static Footprint Starter(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            var table = id.StartsWith(StarterMeasuresFamily.RuleItemPrefix + "table:", StringComparison.Ordinal)
                ? id.Substring((StarterMeasuresFamily.RuleItemPrefix + "table:").Length)
                : null;
            var fp = LinesOrDeclared(lines, x, () =>
            {
                var d = new Footprint().Write(Objects).Write(Revision);
                var rule = x.StarterRule?.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
                foreach (var (_, measure) in rule?.Measures ?? new List<(string, string)>()) d.Write("measure:" + table + "/" + measure);
                return d;
            });
            if (!fp.Known) return fp;
            if (table != null)
            {
                fp.Read("table:" + table).Read(RelationshipsAt(table));
                if (x.Model.ColumnsByTable.TryGetValue(table, out var cols)) fp.ReadAll(cols);
            }
            // The objects facet is NOT read (the M11 join, item 8): it is the facet every creating row writes, so a starter row that read it
            // waited for every starter row before it (each writes it), one press per table, 21 presses on 42 tables. What two starter rows
            // could really share is a measure name (a measure's name is unique across the model), and that is claimed by name from the
            // lines (Claims), so two starters that would make the same name still meet there. The rows that read the objects facet (the
            // describe row, the words row) still wait for the starters, because a starter still WRITES it.
            return fp.ReadAll(x.Model.MeasureRefs);
        }

        /// <summary>Fixed lines the assistant wrote: each reads and writes its own object, and nothing else. An
        /// assistant row exists only while it has lines, so an empty one is unknown rather than empty.</summary>
        private static Footprint Assistant(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x) =>
            lines.Count > 0 ? FromLines(lines, x) : Footprint.Unknown();

        /// <summary>An asked row, a Plan task (contract 5.1, lane E3). ALWAYS UNKNOWN (Sol 6, slice 7, finding 3): a
        /// task is words and a brief, and words can ask the assistant to create, rename or remove something no
        /// attached object names, so two tasks on different objects can still land on the same new one. Nothing on
        /// a task bounds its effects the way a typed operation's lines do, so it is never called independent.
        ///
        /// The objects the person or the Send attached are its FLOOR: written, and read, because words about an
        /// object read that object, plus the revision, because it commits. The floor is what the task waits on and
        /// is waited on for; it is never taken as the whole footprint. A task with no objects (Your words, an aim)
        /// has no floor, so it takes no part in waits at all.</summary>
        internal static Footprint Asked(ModelPlanItem item)
        {
            var fp = Footprint.Unknown();
            // A composer draft's part (lane L4) is words written into the DRAFT, not the model: the draft's change
            // writes the model later, as its own row. The objects it names are there for the claim's grounding, not
            // as a floor, so a part takes no part in waits, like a task with no objects, and is still never independent.
            if (item?.OriginLink?.Kind == PlanOriginKinds.Draft) return fp;
            var objects = (item?.Objects ?? Array.Empty<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();
            if (objects.Length == 0) return fp;
            return fp.WriteAll(objects).ReadAll(objects).Write(Revision);
        }

        /// <summary>A Jev family row. Its choices are made over a population (every measure with no folder or no
        /// format; every fact column with no measure), so it reads the objects facet and every measure. Before Jev
        /// answers there are no lines and the row's footprint is unknown.</summary>
        private static Footprint Jev(string id, IReadOnlyList<ModelPlanChangeLine> lines, FootprintInputs x)
        {
            if (lines.Count == 0) return Footprint.Unknown();
            var fp = FromLines(lines, x);
            if (!fp.Known) return fp;
            // The date table point is one choice, not a sweep over measures: it reads what the mark can change.
            if (id.StartsWith(JevFamily.ItemPrefix + DateTableFamily.Name + ".", StringComparison.Ordinal))
                return fp.ReadAll(DateMarkReaders(x.Model, lines)).Read(Objects);
            // The column format family asks about FMT-COLUMN's population, which leaves keys out.
            if (id.StartsWith(JevFamily.ItemPrefix + ColumnFormatFamily.Name + ".", StringComparison.Ordinal)) fp.Read(Keys);
            // The starter family chooses its fact tables and columns the way the starter rule does (StarterMeasureTemplates:
            // the many side of a relationship by its cardinality, a column's key and relationship role), so it reads what
            // the rule's row reads.
            const string starter = JevFamily.ItemPrefix + StarterMeasuresFamily.Name + ".table:";
            if (id.StartsWith(starter, StringComparison.Ordinal))
            {
                var table = id.Substring(starter.Length);
                fp.Read("table:" + table).Read(RelationshipsAt(table));
                if (x.Model.ColumnsByTable.TryGetValue(table, out var cols)) fp.ReadAll(cols);
            }
            // What Copilot sees: every line already reads exactly the schema entries its check leans on (FromLines,
            // set_ai_data_schema), found from the measures' formulas. Reading EVERY measure as well would make each
            // row that writes a measure's entry feed every other such row, and the order the schema's rule needs
            // (a field back before the measure that uses it) would fall to plan order. It is still a population row.
            if (id.StartsWith(JevFamily.ItemPrefix + AiSchemaFamily.Name + ".", StringComparison.Ordinal)) return fp.Read(Objects);
            return fp.ReadAll(x.Model.MeasureRefs).Read(Objects);
        }

        /// <summary>The schema entries a set_ai_data_schema line leans on: its table's; for a measure put back, what its
        /// formula uses and their tables; for anything left out, every formula that names it (a text match, wide on
        /// purpose: the safe side costs a wait, never a wrong "independent").</summary>
        internal static IEnumerable<string> AiSchemaLeansOn(FootprintSnapshot m, string target, string after)
        {
            if (string.IsNullOrEmpty(target) || m == null) yield break;
            var table = TableOf(target);
            var isTable = target.StartsWith("table:", StringComparison.OrdinalIgnoreCase);
            if (table != null && !isTable) yield return "table:" + table;
            var included = (after ?? "").Trim().StartsWith("incl", StringComparison.OrdinalIgnoreCase)
                        || string.Equals((after ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);
            if (included)
            {
                if (!target.StartsWith("measure:", StringComparison.OrdinalIgnoreCase)) yield break;
                var dax = m.Expressions.FirstOrDefault(e => string.Equals(e.Ref, target, StringComparison.OrdinalIgnoreCase)).Expression;
                foreach (var r in RefsIn(m, dax, table))
                {
                    yield return r;
                    if (TableOf(r) is string rt) yield return "table:" + rt;
                }
                yield break;
            }
            foreach (var u in Users(m, target)) if (!string.Equals(u, target, StringComparison.OrdinalIgnoreCase)) yield return u;
        }

        /// <summary>WHAT MARKING A DATE TABLE READS (UAT 26 Sep: "Mark Date as the date table" waited for two tasks about
        /// Margin %, a ratio with no time intelligence, because the row read every measure). The mark writes the
        /// table's date category and its key column; nothing in a measure is written. What it needs to be true, and what
        /// it can change, is:
        ///   - every DAX object that uses time intelligence (measure, calculated column or table, calculation item): its
        ///     result rests on the mark, and it is why the mark is needed at all (DATE-MARK), so a row that rewrites one
        ///     can make the mark unneeded and must be ordered against it;
        ///   - every DAX object that names the marked table: a filter on a marked date table's key column behaves as if
        ///     the whole table's filters were removed, so even a measure with no time intelligence can change answer.
        /// A row that ADDS time intelligence to a measure the mark does not read reads the date table facet itself
        /// (FromLines, set_dax and create_measure), so that conflict is kept from the other side.
        ///
        /// Until a line names the table, which one is marked is not known, so every measure and calculation item is
        /// read, as before: wide is the safe side.</summary>
        private static IEnumerable<string> DateMarkReaders(FootprintSnapshot m, IReadOnlyList<ModelPlanChangeLine> lines)
        {
            var tables = (lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Op is "mark_date_table" or "generate_date_table")
                .Select(l => TableOf(l.Target)).Where(t => t != null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (tables.Length == 0)
                return m.MeasureRefs.Concat(m.CalculationItems.Select(c => c.Ref));
            return DateMarkReaders(m, tables);
        }

        private static IEnumerable<string> DateMarkReaders(FootprintSnapshot m, IReadOnlyCollection<string> tables) =>
            m.Expressions.Concat(m.CalculationItems)
             .Where(e => IsTimeIntelligence(e.Expression) || tables.Any(t => NamesTable(e.Expression, t)))
             .Select(e => e.Ref);

        /// <summary>A DAX text the date mark rests on: it uses time intelligence (contract 5.1), or it reads a date column
        /// the rule's mark could choose (<see cref="FootprintSnapshot.CandidateDateColumns"/>), so writing it can change
        /// which table the mark picks.</summary>
        private static bool ReadsTheDateTable(FootprintSnapshot m, string dax, string homeTable) =>
            IsTimeIntelligence(dax) || RefsIn(m, dax, homeTable).Any(m.CandidateDateColumns.Contains);

        /// <summary>What marking <paramref name="table"/> with a named <paramref name="key"/> touches in the table
        /// (MarkDateTableCore): it checks the key column (it exists and is a date) and sets its key, and setting a key
        /// clears it on every other column of the table (TOMWrapper Column.cs), so the columns keyed now are written too.
        /// No other column is read or written. A mark and the best-practice row's key fix are the only writers of a key;
        /// marks are ordered against each other by the date table facet, and the key fix writes what a mark writes, so
        /// no other row can move the key under it.
        ///
        /// WHICH COLUMNS ARE KEYS IS READ BY POPULATIONS (verify D1 and D2, lane m11/plan-waits). FMT-SUMMARIZE sweeps the
        /// keys, FMT-COLUMN and three more sweep the columns that are not keys (ReadinessRuleSet.KeyStatusRules), so a
        /// key set or cleared can add a line to a row that names neither column. The mark writes <see cref="Keys"/> when
        /// a key it sets (one not a key yet) or clears is one those rules would find something different about
        /// (ReadinessRuleSet.KeyStatusCanMatter): a key that sums, an old number key with no format. Northstar's Date[Date]
        /// (a date with a format and no summary) is not, so its mark writes nothing more. A key column the snapshot does
        /// not know (another row adds it) is counted as seen: wide is the safe side. With no key named, any column of the
        /// table may become it. What such a finding rests on (the column's summary, format, visibility, type) is a
        /// setting of the key column or of a column whose key is cleared, and the mark writes both, so a row that
        /// changes it is ordered against the mark on that column.</summary>
        private static void Marks(Footprint fp, FootprintSnapshot m, string table, string key)
        {
            var keyRef = string.IsNullOrWhiteSpace(key) ? null : "column:" + table + "/" + key.Trim();
            if (keyRef != null) fp.Write(keyRef).Read(keyRef);
            var keyed = m.KeyColumns.Where(k => string.Equals(TableOf(k), table, StringComparison.OrdinalIgnoreCase)).ToArray();
            fp.WriteAll(keyed);
            m.ColumnsByTable.TryGetValue(table, out var cols);
            cols ??= Array.Empty<string>();
            bool Seen(string c) => m.KeyStatusRead.Contains(c) || !cols.Contains(c, StringComparer.OrdinalIgnoreCase);
            var moved = keyRef == null
                ? cols.Concat(keyed)
                : keyed.Where(k => !string.Equals(k, keyRef, StringComparison.OrdinalIgnoreCase))
                       .Concat(m.KeyColumns.Contains(keyRef) ? Array.Empty<string>() : new[] { keyRef });
            if (moved.Any(Seen)) fp.Write(Keys);
        }

        /// <summary>The key status rules (ReadinessRuleSet.KeyStatusRules) as the plan files their findings: the safe
        /// fix categories and the proposal families that hold one. A row of one of these reads <see cref="Keys"/>.</summary>
        private static readonly Lazy<(string Category, string Fix, string RuleId)[]> KeyStatusRuleRows = new Lazy<(string, string, string)[]>(() =>
            ReadinessRuleSet.Default().Where(r => ReadinessRuleSet.KeyStatusRules.Contains(r.Id))
                .Select(r => (r.Category.ToString(), r.Fix.ToString(), r.Id)).ToArray());

        private static bool SafeFixReadsKeys(string category) =>
            KeyStatusRuleRows.Value.Any(r => r.Fix == nameof(FixKind.SafeFix) && string.Equals(r.Category, category, StringComparison.Ordinal));

        private static bool ProposalReadsKeys(FootprintInputs x, string family) =>
            KeyStatusRuleRows.Value.Any(r => r.Fix == nameof(FixKind.Proposal)
                && string.Equals(x.ProposalFamily(new ReadinessFinding { RuleId = r.RuleId, Category = r.Category }), family, StringComparison.Ordinal));

        // ---- small readers ------------------------------------------------------------------------------------

        /// <summary>The object refs among a set of keys (facets left out).</summary>
        private static IEnumerable<string> ObjectsIn(IEnumerable<string> keys) =>
            keys.Where(k => k != null && !k.StartsWith("model:", StringComparison.OrdinalIgnoreCase) && k.IndexOf(':') > 0);

        /// <summary>The fixed tail every relationship line's after carries (RelationshipFamily.Line): nothing in it is a
        /// name, so the key is everything before it.</summary>
        private static readonly Regex RelationshipAfterTail = new Regex(
            @", (?:many to one|one to one), filters (?:one way|both ways), (?:active|inactive)$",
            RegexOptions.CultureInvariant);

        /// <summary>The key column a relationship line joins, as (table, column ref), from the head of its after
        /// ("Product[ProductKey], many to one, ..."). Empty when the after names no column that way.
        ///
        /// The head is display text, and a bracket is legal in a table's name and in a column's, so "Dim [Old][OldKey]"
        /// could be split at either bracket. The first split used to be taken, which reads a column that does not
        /// exist (the verifier's D5, 26 September). Every split the text allows is tried against the model's own
        /// structured column refs (<see cref="FootprintSnapshot.ColumnsByTable"/>, built from ObjectRefs.For), and
        /// the ones that name a real column are the answer. When none does (no model in hand, or a key the model does
        /// not hold yet), every split is returned: reading a column that is not there costs a line only a wait,
        /// never a run beside a row that touches the real key.</summary>
        internal static IReadOnlyList<(string Table, string Ref)> RelationshipKeysOf(string after, FootprintSnapshot model)
        {
            if (string.IsNullOrWhiteSpace(after)) return Array.Empty<(string, string)>();
            var tail = RelationshipAfterTail.Match(after);
            var head = tail.Success ? after.Substring(0, tail.Index) : after.Trim();
            if (!head.EndsWith("]", StringComparison.Ordinal)) return Array.Empty<(string, string)>();

            var splits = new List<(string Table, string Ref)>();
            for (var open = head.IndexOf('['); open > 0; open = head.IndexOf('[', open + 1))
            {
                var table = head.Substring(0, open);
                splits.Add((table, "column:" + table + "/" + head.Substring(open + 1, head.Length - open - 2)));
            }
            var known = splits.Where(s => model != null && model.ColumnsByTable.TryGetValue(s.Table, out var cols)
                                          && cols.Contains(s.Ref, StringComparer.OrdinalIgnoreCase)).ToList();
            return known.Count > 0 ? known : splits;
        }

        /// <summary>The table an object ref sits in: "column:Sales/Amount" and "table:Sales" both give "Sales".</summary>
        internal static string TableOf(string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) return null;
            var colon = objRef.IndexOf(':');
            if (colon < 0) return null;
            var kind = objRef.Substring(0, colon);
            if (kind is "model" or "relationship" or "namedexpr" or "role" or "perspective" or "culture") return null;
            var rest = objRef.Substring(colon + 1);
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        private static string Renamed(string objRef, string newName)
        {
            if (string.IsNullOrEmpty(objRef) || string.IsNullOrWhiteSpace(newName)) return null;
            var slash = objRef.LastIndexOf('/');
            var colon = objRef.IndexOf(':');
            return slash > colon ? objRef.Substring(0, slash + 1) + newName : objRef.Substring(0, colon + 1) + newName;
        }

        /// <summary>Every DAX object whose expression mentions the name the ref ends in: the set a rename's fixup
        /// may rewrite. A text match, so it errs wide, which is the safe side.</summary>
        private static IEnumerable<string> Users(FootprintSnapshot m, string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) yield break;
            var slash = objRef.LastIndexOf('/');
            var colon = objRef.IndexOf(':');
            var name = slash > colon ? objRef.Substring(slash + 1) : objRef.Substring(colon + 1);
            if (string.IsNullOrWhiteSpace(name)) yield break;
            foreach (var (r, e) in m.Expressions)
                if (e.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) yield return r;
        }

        private static IEnumerable<string> RelationshipTablesOf(FootprintSnapshot m, string objRef)
        {
            if (string.IsNullOrEmpty(objRef) || !objRef.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Empty<string>();
            return m.RelationshipTables.TryGetValue(objRef.Substring("relationship:".Length), out var t) ? t : Enumerable.Empty<string>();
        }

        /// <summary>Everything a delete of <paramref name="objRef"/> takes with it, which is everything ON a table:
        /// its columns, its measures and its calculation items are gone when the table is. Their names go too, and a
        /// measure's name is claimed model-wide, so this is how a table delete frees a name a new measure elsewhere
        /// would take. Empty for anything that is not a table.</summary>
        private static IEnumerable<string> MembersRemovedBy(FootprintSnapshot m, string objRef)
        {
            if (m == null || objRef == null || !objRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase)) yield break;
            var table = TableOf(objRef);
            if (string.IsNullOrWhiteSpace(table)) yield break;
            if (m.ColumnsByTable.TryGetValue(table, out var cols))
                foreach (var c in cols) yield return c;
            if (m.MeasuresByTable.TryGetValue(table, out var measures))
                foreach (var mref in measures) yield return mref;
            // A calculation group is a table, and its items are kept apart from the other expressions.
            foreach (var item in m.CalculationItems)
                if (string.Equals(TableOf(item.Ref), table, StringComparison.OrdinalIgnoreCase)) yield return item.Ref;
        }

        /// <summary>The tables at both ends of every relationship a delete of <paramref name="objRef"/> removes: the
        /// relationship itself, every relationship a column ends, and every relationship at a table.</summary>
        private static IEnumerable<string> RelationshipTablesRemovedBy(FootprintSnapshot m, string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) return Enumerable.Empty<string>();
            if (objRef.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase)) return RelationshipTablesOf(m, objRef);
            IEnumerable<string> names;
            if (objRef.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
                names = m.RelationshipColumns.Where(kv => kv.Value.Contains(objRef, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key);
            else if (objRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase) && TableOf(objRef) is string table)
                names = m.RelationshipTables.Where(kv => kv.Value.Contains(table, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key);
            else return Enumerable.Empty<string>();
            return names.SelectMany(n => m.RelationshipTables.TryGetValue(n, out var t) ? t : Array.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // 'Table'[Name] (quotes doubled inside) or Table[Name], and [Name] with nothing in front that would make it
        // qualified. A bracketed name doubles a closing bracket: [Odd]]Name] is the name Odd]Name.
        private static readonly Regex QualifiedRef = new Regex(
            @"(?:'((?:[^']|'')+)'|([A-Za-z0-9_]+))\s*\[((?:[^\]]|\]\])+)\]", RegexOptions.Compiled);
        private static readonly Regex BareRef = new Regex(@"(?<![A-Za-z0-9_'\]])\[((?:[^\]]|\]\])+)\]", RegexOptions.Compiled);

        /// <summary>The column and measure refs a DAX text names, matched against the model. A coarse token match;
        /// a name it cannot match is simply not a read, which is why every family that carries DAX also reads a wider
        /// key beside it.
        ///
        /// A QUALIFIED reference is read as a column AND as a measure (Sol 6, slice 7, finding 2). DAX writes a measure
        /// as Sales[Base] as readily as [Base], and reading Sales[Base] only as a column missed the measure, so a row
        /// rewriting Base could be called independent of one that reads it. Every measure of that name is a read, not
        /// only the one on that table: wide is the safe side, it costs a wait, never a wrong "independent". A BARE
        /// reference is every measure of that name and, when <paramref name="homeTable"/> is given, the column of that
        /// name on the object's own table (a calculated column names its own table's columns bare).</summary>
        internal static IEnumerable<string> RefsIn(FootprintSnapshot m, string dax, string homeTable = null)
        {
            if (string.IsNullOrEmpty(dax) || m == null) yield break;
            foreach (Match x in QualifiedRef.Matches(dax))
            {
                var table = x.Groups[1].Success ? x.Groups[1].Value.Replace("''", "'") : x.Groups[2].Value;
                var name = x.Groups[3].Value.Replace("]]", "]");
                foreach (var c in ColumnNamed(m, table.Trim(), name)) yield return c;
                foreach (var r in m.MeasureByName[name]) yield return r;
            }
            foreach (Match x in BareRef.Matches(dax))
            {
                var name = x.Groups[1].Value.Replace("]]", "]");
                foreach (var r in m.MeasureByName[name]) yield return r;
                if (homeTable != null) foreach (var c in ColumnNamed(m, homeTable, name)) yield return c;
            }
        }

        private static IEnumerable<string> ColumnNamed(FootprintSnapshot m, string table, string name)
        {
            if (!m.ColumnsByTable.TryGetValue(table, out var cols)) yield break;
            var col = "column:" + table + "/" + name;
            foreach (var c in cols) if (string.Equals(c, col, StringComparison.OrdinalIgnoreCase)) yield return c;
        }

        private static readonly Regex TimeIntelligence = new Regex(
            @"\b(TOTAL[YQM]TD|DATES[YQM]TD|SAMEPERIODLASTYEAR|DATEADD|PARALLELPERIOD|PREVIOUS(DAY|MONTH|QUARTER|YEAR)"
            + @"|NEXT(DAY|MONTH|QUARTER|YEAR)|DATESINPERIOD|DATESBETWEEN|(OPENING|CLOSING)BALANCE(MONTH|QUARTER|YEAR)"
            + @"|(START|END)OF(MONTH|QUARTER|YEAR)|FIRSTDATE|LASTDATE)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"A time intelligence measure reads model:date-table" (contract 5.1).</summary>
        internal static bool IsTimeIntelligence(string dax) => !string.IsNullOrEmpty(dax) && TimeIntelligence.IsMatch(dax);

        private static readonly Regex Bracketed = new Regex(@"\[(?:[^\]]|\]\])*\]", RegexOptions.Compiled);
        private static readonly Regex StringLiteral = new Regex("\"(?:[^\"]|\"\")*\"", RegexOptions.Compiled);

        /// <summary>Whether a DAX text names a table: one of its columns ('Date'[Year], Date[Year]) or the table itself
        /// (ALL ( 'Date' ), VALUES ( Date )). Text inside brackets and strings is not a table name, and a name followed
        /// by "(" is a function (DATE ( 2024, 1, 1 )). A text match, so it errs wide, which is the safe side.</summary>
        internal static bool NamesTable(string dax, string table)
        {
            if (string.IsNullOrEmpty(dax) || string.IsNullOrEmpty(table)) return false;
            foreach (Match x in QualifiedRef.Matches(dax))
            {
                var t = x.Groups[1].Success ? x.Groups[1].Value.Replace("''", "'") : x.Groups[2].Value;
                if (string.Equals(t.Trim(), table, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var rest = Bracketed.Replace(StringLiteral.Replace(dax, " "), " ");
            if (rest.IndexOf("'" + table.Replace("'", "''") + "'", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Regex.IsMatch(rest, @"(?<![A-Za-z0-9_'])" + Regex.Escape(table) + @"(?![A-Za-z0-9_'])(?!\s*\()", RegexOptions.IgnoreCase);
        }
    }
}
