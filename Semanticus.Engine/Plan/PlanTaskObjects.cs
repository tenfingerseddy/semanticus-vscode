// A task's objects against the model it is about (UAT S12-5-006 and S12-6-002, Sol's chaos round).
//
// GROUNDED WHEN IT IS ADDED. A task keeps the objects it names that were in the model at that moment, each with its
// lineage tag. A Send passes its objects; a task typed in the Plan's field names them in its words, as "Sales[Total
// Sales]" or as a measure's own name, and those are found here. The model is only read: nothing is guessed from a
// table's name or a column's bare name, because those are ordinary words ("Sales", "Amount") far more often than they
// are a reference, and a wrong object on a task is worse than none.
//
// GONE MEANS GONE, NOT RENAMED. On every compose each grounded object is looked up by its ref, then by its tag. Found
// either way, it is there (a rename shows the new name). Found neither way, it was deleted, and the row reads Needs you
// with one plain sentence; claim_request and complete_request refuse it, because nothing can be done to an object that
// is not there, and decline_request still hands it back. An object the task named that was NOT in the model when it
// was added (one it is to create) was never grounded, so it is never called gone.
//
// A TASK NO GROUNDING EVER SAW (UAT S12-5-006, still there on the final build). A task saved before grounding existed,
// or written to the plan file by something else, names objects and holds no grounds, so the check above had nothing to
// look up and a deleted object read as one still to be made: the assistant's "no description was applied" closed the
// task as Done. Such a ref is called gone when it is not in the model AND the History shows it (or the table it sits
// on) deleted after the task was added. A ref the History never saw deleted is left alone, because "not there yet" and
// "never there" stay indistinguishable and a wrong "gone" is worse than none.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>What one grounded object is now.</summary>
    public readonly struct PlanGroundNow
    {
        public PlanGroundNow(string current) { Current = current; }

        /// <summary>The object's ref now (its new name after a rename), or null when it is gone.</summary>
        public string Current { get; }
        public bool Gone => Current == null;
    }

    internal static class PlanTaskObjects
    {
        /// <summary>At most this many objects are grounded from a task's words.</summary>
        internal const int MaxFromWords = 12;

        /// <summary>"Sales[Total Sales] is no longer in the model." for one; the first two and a count for more.</summary>
        internal static string GoneSentence(IReadOnlyList<string> refs)
        {
            var names = refs.Select(PlanTasks.NameOfRef).ToList();
            var said = names.Count == 1 ? names[0]
                     : names.Count == 2 ? names[0] + " and " + names[1]
                     : names[0] + ", " + names[1] + " and " + (names.Count - 2) + " more";
            return said + (names.Count == 1 ? " is" : " are") + " no longer in the model.";
        }

        internal static string TagOf(ITabularNamedObject o)
        {
            switch (o)
            {
                case Measure m: return NullIfEmpty(m.LineageTag);
                case Column c: return NullIfEmpty(c.LineageTag);
                case Table t: return NullIfEmpty(t.LineageTag);
                case Hierarchy h: return NullIfEmpty(h.LineageTag);
                case Function f: return NullIfEmpty(f.LineageTag);
                default: return null;
            }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>The grounds for refs that resolve now. Runs on the dispatcher.</summary>
        internal static PlanObjectGround[] GroundsOf(Model m, IEnumerable<string> refs)
        {
            var list = new List<PlanObjectGround>();
            foreach (var r in refs ?? Enumerable.Empty<string>())
            {
                ITabularNamedObject o;
                try { o = ObjectRefs.Resolve(m, r); } catch { o = null; }
                if (o == null || o is Model) continue;
                list.Add(new PlanObjectGround { Ref = ObjectRefs.For(o), Tag = TagOf(o) });
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        /// <summary>True when <paramref name="history"/> holds a deletion of <paramref name="objRef"/>, or of the table it
        /// sits on, made at or after <paramref name="since"/> and not taken back. A delete that was undone is not one:
        /// the object is back, and a ref that is back is never asked about here. Nor is the deletion of an object that was
        /// itself made after <paramref name="since"/>: a task naming it is to make it.</summary>
        internal static bool DeletedSince(IReadOnlyList<EditLedgerEntry> history, string objRef, DateTimeOffset since)
        {
            if (history == null || string.IsNullOrEmpty(objRef)) return false;
            var names = new List<string> { objRef };
            var colon = objRef.IndexOf(':');
            var slash = colon < 0 ? -1 : objRef.IndexOf('/', colon + 1);
            if (colon > 0 && slash > colon && !objRef.StartsWith("table:", StringComparison.Ordinal))
                names.Add("table:" + objRef.Substring(colon + 1, slash - colon - 1));
            bool Touches(EditLedgerEntry e, string kind) => e != null && e.Kind == EditLedgerKinds.Edit && !e.Undone && e.When >= since
                && (e.Deltas ?? Array.Empty<ChangeDelta>()).Any(d => d != null && d.Kind == kind && names.Contains(d.Ref));
            // An object made after the task was added was never in the model when the task was: the task is to MAKE it, so a
            // first try that was made and deleted again does not leave the task asking for something that is gone.
            if (history.Any(e => Touches(e, "add"))) return false;
            return history.Any(e => Touches(e, "remove"));
        }

        /// <summary>A grounded object as it is now. Runs on the dispatcher.</summary>
        internal static PlanGroundNow Now(Model m, PlanObjectGround g)
        {
            ITabularNamedObject o;
            try { o = ObjectRefs.Resolve(m, g.Ref); } catch { o = null; }
            if (o != null) return new PlanGroundNow(g.Ref);
            if (g.Tag == null) return new PlanGroundNow(null);
            var renamed = Tagged(m).FirstOrDefault(x => string.Equals(TagOf(x), g.Tag, StringComparison.Ordinal));
            return new PlanGroundNow(renamed == null ? null : ObjectRefs.For(renamed));
        }

        private static IEnumerable<ITabularNamedObject> Tagged(Model m) =>
            m.Tables.Cast<ITabularNamedObject>()
             .Concat(m.AllMeasures).Concat(m.AllColumns).Concat(m.AllHierarchies);

        // "Sales[Total Sales]", "'Sales Order'[Total Sales]" or "[Total Sales]".
        private static readonly Regex Bracketed = new Regex(@"\[([^\[\]]+)\]", RegexOptions.CultureInvariant);

        /// <summary>The measures and columns a task's words name. Runs on the dispatcher.
        ///
        /// Two forms, and only these. A bracketed name, with or without its table in front ("Sales[Total Sales]",
        /// "'Sales Order'[Margin]", "[Total Sales]"): the table is the longest table name the text before the bracket
        /// ends with; with none, a measure whose name is that and is the only one so named. And a measure's own name
        /// as whole words ("Review Total Sales every week"), when exactly one measure has it and no table does.</summary>
        internal static string[] NamedIn(Model m, string words)
        {
            if (m == null || string.IsNullOrWhiteSpace(words)) return Array.Empty<string>();
            var found = new List<string>();
            void Add(ITabularNamedObject o) { if (o != null) { var r = ObjectRefs.For(o); if (!found.Contains(r)) found.Add(r); } }

            var tables = m.Tables.OrderByDescending(t => t.Name.Length).ToList();
            var measures = m.AllMeasures.ToList();
            foreach (Match b in Bracketed.Matches(words))
            {
                var name = b.Groups[1].Value.Trim();
                var before = words.Substring(0, b.Index).TrimEnd();
                if (before.EndsWith("'", StringComparison.Ordinal))
                {
                    var open = before.LastIndexOf('\'', before.Length - 2);
                    if (open >= 0) before = before.Substring(open + 1, before.Length - open - 2);
                }
                var table = tables.FirstOrDefault(t => before.EndsWith(t.Name, StringComparison.OrdinalIgnoreCase)
                    && (before.Length == t.Name.Length || !char.IsLetterOrDigit(before[before.Length - t.Name.Length - 1])));
                if (table != null)
                {
                    Add(table.Measures.Contains(name) ? table.Measures[name] : table.Columns.Contains(name) ? table.Columns[name] : null);
                    continue;
                }
                var only = measures.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (only.Count == 1) Add(only[0]);
            }

            foreach (var g in measures.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (g.Count() != 1 || g.Key.Length < 3 || m.Tables.Contains(g.Key)) continue;
                if (Regex.IsMatch(words, @"(?<![\p{L}\p{N}_\[])" + Regex.Escape(g.Key) + @"(?![\p{L}\p{N}_\]])",
                                  RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    Add(g.First());
            }
            return found.Take(MaxFromWords).ToArray();
        }
    }

    public sealed partial class LocalEngine
    {
        /// <summary>The objects a new task is about and the ones of those that are in the model now. Objects passed in
        /// are kept as given; with none, the words are read for them. No model open, nothing is grounded.</summary>
        internal async Task<(string[] Objects, PlanObjectGround[] Grounds)> GroundNewTaskAsync(string words, string[] objects)
        {
            var s = _sessions.Current;
            if (s == null) return (objects, null);
            try
            {
                return await s.ReadAsync(m =>
                {
                    var named = objects is { Length: > 0 } ? objects : PlanTaskObjects.NamedIn(m, words);
                    return (named, PlanTaskObjects.GroundsOf(m, named));
                }).ConfigureAwait(false);
            }
            catch { return (objects, null); }
        }

        /// <summary>Every grounded object of these tasks and repeats as it is now, by its ground ref. Best effort: a
        /// model that cannot be read calls nothing gone.</summary>
        internal async Task<Dictionary<string, PlanGroundNow>> GroundsNowAsync(Session s, IEnumerable<PlanObjectGround> grounds)
        {
            var wanted = (grounds ?? Enumerable.Empty<PlanObjectGround>()).Where(g => g?.Ref != null)
                .GroupBy(g => g.Ref, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var none = new Dictionary<string, PlanGroundNow>(StringComparer.Ordinal);
            if (s == null || wanted.Count == 0) return none;
            try
            {
                return await s.ReadAsync(m => wanted.ToDictionary(g => g.Ref, g => PlanTaskObjects.Now(m, g), StringComparer.Ordinal)).ConfigureAwait(false);
            }
            catch { return none; }
        }

        /// <summary>The refs of a task's or repeat's grounded objects that are gone now, in its own order.</summary>
        internal static string[] GoneOf(PlanObjectGround[] grounds, IReadOnlyDictionary<string, PlanGroundNow> now) =>
            (grounds ?? Array.Empty<PlanObjectGround>())
                .Where(g => g?.Ref != null && now.TryGetValue(g.Ref, out var n) && n.Gone)
                .Select(g => g.Ref).ToArray();

        /// <summary>Every ref to look up for these tasks: the grounded ones, and the refs a task names that no ground holds
        /// (looked up by ref alone, there being no lineage tag to follow a rename by).</summary>
        internal static PlanObjectGround[] AllGroundsOf(IEnumerable<PlanTask> tasks)
        {
            var all = new List<PlanObjectGround>();
            foreach (var t in tasks ?? Enumerable.Empty<PlanTask>())
            {
                if (t == null) continue;
                all.AddRange((t.Grounds ?? Array.Empty<PlanObjectGround>()).Where(g => g?.Ref != null));
                all.AddRange(UngroundedRefs(t).Select(r => new PlanObjectGround { Ref = r }));
            }
            return all.ToArray();
        }

        /// <summary>The refs a task names that none of its grounds holds.</summary>
        internal static string[] UngroundedRefs(PlanTask t) =>
            (t?.Objects ?? Array.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r) && !(t.Grounds ?? Array.Empty<PlanObjectGround>()).Any(g => string.Equals(g?.Ref, r, StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>The refs of a task's objects that are gone now: a grounded object found neither by ref nor by tag, and
        /// an ungrounded one that is not in the model and that <paramref name="history"/> shows deleted since the task
        /// was added. In the task's own order.</summary>
        internal static string[] GoneOfTask(PlanTask task, IReadOnlyDictionary<string, PlanGroundNow> now, IReadOnlyList<EditLedgerEntry> history)
        {
            if (task == null) return Array.Empty<string>();
            var gone = GoneOf(task.Grounds, now).ToList();
            foreach (var r in UngroundedRefs(task))
                if (now.TryGetValue(r, out var n) && n.Gone && PlanTaskObjects.DeletedSince(history, r, task.CreatedUtc))
                    gone.Add(r);
            return gone.ToArray();
        }

        /// <summary>The History as a task's check reads it. Best effort: no history is an empty one, and calls nothing gone.</summary>
        internal IReadOnlyList<EditLedgerEntry> HistoryForTasks(Session s)
        {
            try { return s == null ? Array.Empty<EditLedgerEntry>() : _sessions.Ledger.ForModel(s.Id); }
            catch { return Array.Empty<EditLedgerEntry>(); }
        }

        /// <summary>Carry a rename a read saw into the tasks' and repeats' grounds and objects, so the object is named by
        /// its name now, and a later deletion is said under that name. True when anything changed.</summary>
        internal static bool FollowRenamesIn(PlanMemoryState state, IReadOnlyDictionary<string, PlanGroundNow> now)
        {
            if (state == null || now == null || now.Count == 0) return false;
            var changed = false;
            string[] Move(string[] refs, PlanObjectGround[] grounds)
            {
                if (grounds == null) return refs;
                foreach (var g in grounds)
                {
                    if (g?.Ref == null || !now.TryGetValue(g.Ref, out var n) || n.Gone || n.Current == g.Ref) continue;
                    var old = g.Ref;
                    g.Ref = n.Current;
                    if (refs != null) refs = refs.Select(r => string.Equals(r, old, StringComparison.Ordinal) ? n.Current : r).ToArray();
                    changed = true;
                }
                return refs;
            }
            foreach (var t in state.Tasks) t.Objects = Move(t.Objects, t.Grounds) ?? Array.Empty<string>();
            foreach (var r in state.Repeats) r.Objects = Move(r.Objects, r.Grounds);
            return changed;
        }

        /// <summary>The sentence a claim or an answer is refused with when the task's object is gone, or null.</summary>
        internal async Task<string> GoneRefusalAsync(PlanTask task)
        {
            var looked = AllGroundsOf(new[] { task });
            if (looked.Length == 0) return null;
            var gone = GoneOfTask(task, await GroundsNowAsync(_sessions.Current, looked).ConfigureAwait(false), HistoryForTasks(_sessions.Current));
            return gone.Length == 0 ? null
                : PlanTaskObjects.GoneSentence(gone).TrimEnd('.') + ", so this task cannot be done. Hand it back with "
                  + "decline_request and a reason, and the person decides what to do with it.";
        }
    }
}
