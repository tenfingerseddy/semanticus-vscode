// The agent door's read of the plan, sized for an assistant (verify L9, round 3's L9 and round 4's L11, 29 Sep).
//
// get_model_plan on a 12-table model was 211,951 characters, about 53,000 tokens, where Claude Code reads at most 25,000
// tokens from one tool result, and lets through uncounted only a result of at most 50,000 characters. A default read here
// is at most 40,000 characters of the JSON as it goes on the wire (escapes counted: the door writes ' and + as six
// characters), on ANY model and after Run the plan has been pressed many times. Four things make a plan long, and each is
// paged and none is ever cut without saying where the rest is:
//
//   the per-row detail    a row's lines, its per-table lines, its objects, its footprint and the rows it waits for. A short
//                         read leaves them out (the view's Detail says so per row); item = a row's id pages them.
//   the rows themselves   a model with hundreds of tables has hundreds of rows. A short row names what it waits for by id
//                         only, and past a few gives a count and the first few. If the rows still do not fit, the reason
//                         each row carries is left out, then the rows at the end of the list are, and item = "rows" pages them.
//   the history           every press keeps every line it wrote. A short read carries the newest few entries with their
//                         first lines; item = "history" pages the entries and item = "history/N" pages one entry's lines.
//   the map               the table list is cut to what fits.
//
// A read never empties the page it was asked for: a detail read keeps its row, a history read keeps its entries, a rows
// read keeps its rows, and rows are the first thing cut to make room. It is a projection of the same plan the Studio reads
// (IEngine.GetModelPlanAsync is unchanged and the pipe still sends the whole plan), so both doors keep the same content
// and only the agent door is asked to page. A plan that already fits is returned whole, so a small model reads exactly
// what it always did.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine
{
    internal static class PlanReadShape
    {
        /// <summary>A plan at or under this many characters is returned whole. It is also the most a default read of the
        /// plan is: Claude Code lets a tool result of up to 50,000 characters through uncounted and cuts a longer one, and
        /// what the door appends after the JSON (the health line) needs room beside it.</summary>
        internal const int WholePlanChars = 40_000;

        /// <summary>Lines of one row's detail on a page when the caller names none, and the most it may name.</summary>
        internal const int DetailPageDefault = 40;
        internal const int DetailPageMax = 200;

        /// <summary>The most characters any one read is, rows and page together: a page holds fewer lines than asked when
        /// they are long or when the row touches many tables, until the whole read fits.</summary>
        internal const int ReadChars = WholePlanChars;

        /// <summary>History entries a short read carries (the newest), each with its first few lines. The plan's history
        /// keeps every line of every press, about 18,500 characters a press on a 12-table model, so it is a count and
        /// the last few here and item = "history" pages the rest.</summary>
        internal const int HistoryShown = 3;
        internal const int HistoryLinesShown = 3;

        /// <summary>The most lines of each list of one entry on a page of the history. A press that ran thousands of lines
        /// is cut there with a line that says how many were left out and which read returns them (item = history/N).</summary>
        internal const int HistoryEntryLines = 100;
        internal const string HistoryItem = "history";
        internal const string HistoryEntryPrefix = HistoryItem + "/";

        /// <summary>item = "rows" pages the rows themselves, short, in plan order.</summary>
        internal const string RowsItem = "rows";

        /// <summary>Rows a page of rows names at most when the caller names a count.</summary>
        internal const int RowsPageMax = 200;

        /// <summary>The rows a short row names by id that it waits for. Past this many it gives a count and these.</summary>
        internal const int WaitsShown = 3;

        /// <summary>Room kept back in a read that had to be cut for the notes that say what was cut and where the rest is.</summary>
        private const int NoteReserve = 1_500;

        // What the MCP door sends is the tool's return value serialized with the SDK's own options, so that is what is counted.
        private static readonly JsonSerializerOptions Json = ModelContextProtocol.McpJsonUtilities.DefaultOptions;

        internal static int Chars(object o) => JsonSerializer.Serialize(o, Json).Length;

        private enum Ask { Plan, Rows, Row, History, Entry }

        /// <summary>The plan as the agent door reads it. <paramref name="itemId"/> null: the whole plan if it fits, else the
        /// rows short (as many as fit). A row's id: the rows short and that row with its detail from <paramref name="offset"/>,
        /// at most <paramref name="max"/> lines of each list. "rows": a page of the rows, short, from row <paramref name="offset"/>.
        /// "history": the rows short and a page of the plan's history. "history/N": one entry of the history with its
        /// lines from <paramref name="offset"/>. Whatever is asked, the read is cut shorter until it is at most
        /// <see cref="ReadChars"/> characters, keeping at least one line (or one history entry, or one row).</summary>
        public static ModelPlanView For(ModelPlanView full, string itemId, int offset, int max)
        {
            if (full == null) return null;
            if (string.IsNullOrEmpty(itemId) && Chars(full) <= WholePlanChars) return full;
            var ask = AskOf(itemId, out var number);
            var from = Math.Max(0, offset);
            var page = ask == Ask.Rows ? (max <= 0 ? int.MaxValue : Math.Min(max, RowsPageMax))
                : max <= 0 ? DetailPageDefault : Math.Min(max, DetailPageMax);
            var lines = HistoryEntryLines;
            ModelPlanView view;
            for (; ; page = Math.Max(1, page / 2), lines = Math.Max(3, lines / 2))
            {
                view = Fit(Build(full, ask, itemId, number, from, page, lines), ask, itemId, full.History?.Length ?? 0);
                var pageable = ask == Ask.Row || ask == Ask.History || ask == Ask.Entry;
                if (!pageable || page <= 1 && lines <= 3 || Chars(view) <= ReadChars) break;
            }
            if (ask == Ask.Rows)
            {
                var total = full.Items.Length;
                var end = from + view.Items.Length;
                view.DetailNote = (view.DetailNote + " " + (view.Items.Length == 0 ? "The plan has " + total + " rows, and none starts at " + from + "."
                    : "Rows " + (from + 1) + " to " + end + " of " + total + " are here" + (end < total ? "; call again with item = rows and offset = " + end + " for the next." : "."))).Trim();
            }
            return Chars(view) <= ReadChars ? view : CutLongStrings(view);
        }

        /// <summary>The catch-all, for a read that is still too long after every page is as short as it goes: one line (a change
        /// that carries a whole formula, a title, a reason) longer than the page. Every string in the read is cut to the same
        /// length, the length halved until the read fits, and each cut says how long the whole was. Works on a copy: the plan
        /// the Studio reads is never touched.</summary>
        private static ModelPlanView CutLongStrings(ModelPlanView view) =>
            CutLongStrings(view, "detailNote", (cut, note) => cut.DetailNote = ((cut.DetailNote ?? "") + " " + note).Trim());

        /// <summary>The same catch-all for any answer the agent door sends about the plan (the reads above, and the short
        /// answers to a change, PlanResultShape: verify R5-1). Every string but <paramref name="noteKey"/> is cut to the same
        /// length, halved until the answer fits, and <paramref name="addNote"/> says so. Works on a copy.</summary>
        internal static T CutLongStrings<T>(T value, string noteKey, Action<T, string> addNote) where T : class
        {
            var text = JsonSerializer.Serialize(value, Json);
            T cut = value;
            for (var keep = 8_000; keep >= 100; keep /= 2)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                CutStrings(node, keep, noteKey);
                cut = node.Deserialize<T>(Json);
                addNote(cut, "Text longer than " + keep + " characters is cut to fit; each cut says how long the whole was.");
                if (Chars(cut) <= ReadChars) return cut;
            }
            return cut;
        }

        private static void CutStrings(System.Text.Json.Nodes.JsonNode node, int keep, string noteKey)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToArray())
                    {
                        if (key == noteKey) continue;
                        var child = o[key];
                        if (child is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str) && str.Length > keep)
                            o[key] = str.Substring(0, keep) + " ... [cut here, " + str.Length + " characters in all]";
                        else CutStrings(child, keep, noteKey);
                    }
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    for (var n = 0; n < a.Count; n++)
                    {
                        if (a[n] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str) && str.Length > keep)
                            a[n] = str.Substring(0, keep) + " ... [cut here, " + str.Length + " characters in all]";
                        else CutStrings(a[n], keep, noteKey);
                    }
                    break;
            }
        }

        private const string EveryRowHere = "Every row is here with its state and its counts.";

        private static string ShownHistory(int total, int shown) =>
            "History: " + total + (total == 1 ? " entry" : " entries") + " in all; the newest " + shown
            + " are here, each with its first " + HistoryLinesShown + " lines. item = history reads them all, a page at a time.";

        private static IEnumerable<string> Lists(ModelPlanHistoryEntry h) =>
            (h.Detail ?? Array.Empty<string>()).Concat(h.Appeared ?? Array.Empty<string>()).Concat(h.Gone ?? Array.Empty<string>()).Concat(h.Completed ?? Array.Empty<string>());

        private static Ask AskOf(string itemId, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(itemId)) return Ask.Plan;
            if (string.Equals(itemId, RowsItem, StringComparison.Ordinal)) return Ask.Rows;
            if (string.Equals(itemId, HistoryItem, StringComparison.Ordinal)) return Ask.History;
            if (itemId.StartsWith(HistoryEntryPrefix, StringComparison.Ordinal))
            {
                number = int.TryParse(itemId.Substring(HistoryEntryPrefix.Length), out var n) ? n : 0;
                return Ask.Entry;
            }
            return Ask.Row;
        }

        private static ModelPlanView Build(ModelPlanView full, Ask ask, string itemId, int number, int from, int page, int entryLines)
        {
            var view = full.ShallowCopy();
            var found = false;
            var details = new List<PlanRowDetail>();
            var source = ask == Ask.Rows ? full.Items.Skip(from).Take(page) : full.Items;
            view.Items = source.Select(i =>
            {
                var lines = DetailTotal(i);
                if (ask == Ask.Row && string.Equals(i.Id, itemId, StringComparison.Ordinal))
                {
                    found = true;
                    var shown = Window(i, from, page, lines, out var next);
                    details.Add(new PlanRowDetail { ItemId = i.Id, Shown = true, Lines = lines, Next = next });
                    return shown;
                }
                details.Add(new PlanRowDetail { ItemId = i.Id, Shown = false, Lines = lines });
                return Short(i);
            }).ToArray();
            view.Detail = details.ToArray();
            var history = full.History ?? Array.Empty<ModelPlanHistoryEntry>();
            var note = new List<string>();
            if (ask == Ask.History)
            {
                var shown = Math.Min(page, 100);
                view.History = history.Select((e, n) => (e, n)).Skip(from).Take(shown).Select(x => TrimEntry(x.e, entryLines, x.n + 1)).ToArray();
                found = true;
                note.Add("The plan's history, oldest first: entries " + Math.Min(from + 1, history.Length) + " to " + Math.Min(from + view.History.Length, history.Length)
                    + " of " + history.Length + (from + view.History.Length < history.Length ? "; call again with item = history and offset = " + (from + view.History.Length) + "." : ".")
                    + (view.History.Any(h => Lists(h).Any(l => l.StartsWith(MoreLinesMark, StringComparison.Ordinal)))
                        ? " An entry ends with '... and N more lines' when it ran more than " + entryLines + " lines in a list: item = history/N (its number is on that line) reads the rest, a page at a time."
                        : ""));
            }
            else if (ask == Ask.Entry)
            {
                found = true;
                if (number < 1 || number > history.Length)
                {
                    view.History = Array.Empty<ModelPlanHistoryEntry>();
                    note.Add("The plan's history has " + history.Length + (history.Length == 1 ? " entry" : " entries") + " and none is number " + itemId.Substring(HistoryEntryPrefix.Length) + ". item = history reads them.");
                }
                else
                {
                    var e = history[number - 1];
                    var total = new[] { e.Detail?.Length ?? 0, e.Appeared?.Length ?? 0, e.Gone?.Length ?? 0, e.Completed?.Length ?? 0 }.Max();
                    view.History = new[] { WindowOf(e, from, page) };
                    var to = Math.Min(from + page, total);
                    note.Add("History entry " + number + " of " + history.Length + ", lines " + Math.Min(from + 1, total) + " to " + to + " of " + total + " in its longest list"
                        + (to < total ? "; call again with item = " + itemId + " and offset = " + to + "." : "."));
                }
            }
            else
            {
                view.History = history.Select((e, n) => (e, n)).Skip(Math.Max(0, history.Length - HistoryShown)).Select(x => TrimEntry(x.e, HistoryLinesShown, x.n + 1)).ToArray();
                if (history.Length > 0) note.Add(ShownHistory(history.Length, view.History.Length));
            }
            switch (ask)
            {
                case Ask.Plan:
                    note.Add(EveryRowHere + " A row's detail (its lines, its tables, its objects, every row it waits for) is left out "
                        + "so the read fits: call get_model_plan with item = the row's id, then offset = that row's Detail.Next until it is null.");
                    break;
                case Ask.Rows:
                    note.Add("The plan's rows, in plan order, short. A row's detail is read with item = its id.");
                    break;
                case Ask.History:
                case Ask.Entry:
                    note.Add("The rows are short.");
                    break;
                default:
                    note.Add(found ? "Row " + itemId + " is read in detail from offset " + from + " (Detail.Next says where the next page starts). The other rows are short."
                        // Not "every row is here": on a large plan Fit may leave rows out below (verify R5 L-4).
                        : "No row has the id " + itemId + ". The rows here carry their state and their counts; pass one of their ids as item.");
                    break;
            }
            view.DetailNote = string.Join(" ", note);
            return view;
        }

        /// <summary>The last resort, for a plan whose short rows alone pass the limit (hundreds of rows or tables). In this
        /// order, each only while the read is still too long: the reason each row carries is dropped, the map's table list is
        /// cut, the newest history entries a read carries go, and last the rows at the end of the list are left out (a rows
        /// page or a history page may end up with none but the one it was asked for). What is left out is said, with the read
        /// that returns it. A read never empties the page it was asked for.</summary>
        private static ModelPlanView Fit(ModelPlanView view, Ask ask, string itemId, int historyTotal)
        {
            var room = ReadChars - NoteReserve;         // what the notes appended below add is counted before, not after
            if (Chars(view) <= ReadChars) return view;
            var cut = new List<string>();

            // 1. The reason each row carries. The row's detail read has it.
            view.Items = view.Items.Select((i, n) => ask == Ask.Row && string.Equals(i.Id, itemId, StringComparison.Ordinal) ? i : Terse(i)).ToArray();
            cut.Add("The reasons (why) are left out of the rows to fit; a row's own read has them.");
            if (Chars(view) <= room) return Noted(view, cut);

            // 2. The map.
            var tables = view.Tables ?? Array.Empty<ModelPlanTable>();
            var keep = tables.Length;
            while (Chars(view) > room && keep > 10)
            {
                keep = Math.Max(10, keep / 2);
                view.Tables = tables.Take(keep).ToArray();
            }
            if (keep < tables.Length)
                cut.Add("The map lists the first " + keep + " of " + tables.Length + " tables to fit.");
            if (Chars(view) <= room) return Noted(view, cut);

            // 3. The newest history entries a read carries (a history read is asked for its entries and keeps them).
            if (ask != Ask.History && ask != Ask.Entry && view.History != null && view.History.Length > 0)
            {
                view.DetailNote = (view.DetailNote ?? "").Replace(ShownHistory(historyTotal, view.History.Length),
                    "History: " + historyTotal + (historyTotal == 1 ? " entry" : " entries") + " in all, left out of this read to fit; item = history reads them, a page at a time.");
                view.History = Array.Empty<ModelPlanHistoryEntry>();
                if (Chars(view) <= room) return Noted(view, cut);
            }

            // 4. Rows, from the end. The row a detail read is about stays, and a rows read keeps at least one row.
            var rows = view.Items;
            var pinned = ask == Ask.Row ? Array.FindIndex(rows, i => string.Equals(i.Id, itemId, StringComparison.Ordinal)) : -1;
            var others = Enumerable.Range(0, rows.Length).Where(n => n != pinned).ToArray();
            var floor = ask == Ask.Rows && others.Length > 0 ? 1 : 0;
            var allDetail = view.Detail ?? Array.Empty<PlanRowDetail>();
            void Show(int k)
            {
                var keepAt = new HashSet<int>(others.Take(k));
                if (pinned >= 0) keepAt.Add(pinned);
                var order = Enumerable.Range(0, rows.Length).Where(keepAt.Contains).ToArray();
                view.Items = order.Select(n => rows[n]).ToArray();
                view.Detail = order.Where(n => n < allDetail.Length).Select(n => allDetail[n]).ToArray();
            }
            var lo = floor;
            var hi = others.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                Show(mid);
                if (Chars(view) <= room) lo = mid; else hi = mid - 1;
            }
            Show(lo);
            if (lo < others.Length && ask != Ask.Rows)      // a rows read says where it stopped when it is done (For)
            {
                if (ask == Ask.Plan)
                    view.DetailNote = (view.DetailNote ?? "").Replace(EveryRowHere, "Each row listed here has its state and its counts.");
                if (ask == Ask.Plan)
                    cut.Add("Only the first " + lo + " of " + rows.Length + " rows fit here, each with its state and its counts; item = rows reads the rest, a page at a time (item = rows, offset = " + lo + " for the next).");
                else
                    cut.Add(pinned < 0
                        ? "Only the first " + lo + " of " + rows.Length + " rows fit here; item = rows reads the rows, a page at a time."
                        : lo == 0 ? "Only the row asked for fits here; item = rows reads the rows, a page at a time."
                        : "Only the first " + lo + " of the other rows fit here; item = rows reads the rows, a page at a time.");
            }
            return Noted(view, cut);
        }

        private static ModelPlanView Noted(ModelPlanView view, List<string> cut)
        {
            if (cut.Count > 0) view.DetailNote = (view.DetailNote + " " + string.Join(" ", cut)).Trim();
            return view;
        }

        internal const string MoreLinesMark = "... and ";

        /// <summary>An entry with at most <paramref name="lines"/> lines of each list; a longer list ends with a line saying
        /// how many were left out and which read returns them.</summary>
        internal static ModelPlanHistoryEntry TrimEntry(ModelPlanHistoryEntry e, int lines, int number) => new ModelPlanHistoryEntry
        {
            When = e.When, Text = e.Text, Kind = e.Kind, Actor = e.Actor, ItemId = e.ItemId, Title = e.Title,
            Revision = e.Revision, Sentence = e.Sentence,
            Detail = Cut(e.Detail, lines, number), Appeared = Cut(e.Appeared, lines, number), Gone = Cut(e.Gone, lines, number), Completed = Cut(e.Completed, lines, number),
        };

        private static string[] Cut(string[] all, int lines, int number) =>
            all == null || all.Length <= lines ? all
            : all.Take(lines).Append(MoreLinesMark + (all.Length - lines) + " more lines; item = " + HistoryEntryPrefix + number + " reads them").ToArray();

        /// <summary>One entry with one page of each of its lists, from the same offset.</summary>
        private static ModelPlanHistoryEntry WindowOf(ModelPlanHistoryEntry e, int from, int page) => new ModelPlanHistoryEntry
        {
            When = e.When, Text = e.Text, Kind = e.Kind, Actor = e.Actor, ItemId = e.ItemId, Title = e.Title,
            Revision = e.Revision, Sentence = e.Sentence,
            Detail = Slice(e.Detail, from, page), Appeared = Slice(e.Appeared, from, page), Gone = Slice(e.Gone, from, page), Completed = Slice(e.Completed, from, page),
        };

        private static int DetailTotal(ModelPlanItem i) =>
            new[]
            {
                i.Diff?.Length ?? 0, i.Objects?.Length ?? 0, i.Reads?.Length ?? 0, i.Writes?.Length ?? 0, i.WaitsFor?.Length ?? 0,
                i.ChangeSet?.Lines?.Length ?? 0, i.ChangeSet?.Refused?.Length ?? 0,
                i.Tables == null || i.Tables.Length == 0 ? 0 : i.Tables.Max(t => t.Diff?.Length ?? 0),
            }.Max();

        /// <summary>The row with its counts and state and none of its detail lists. What it waits for is the ids of the first
        /// few and a count; the row's own read has every one, with its title.</summary>
        private static ModelPlanItem Short(ModelPlanItem i)
        {
            var c = i.ShallowCopy();
            c.Diff = Array.Empty<string>();
            c.Objects = Array.Empty<string>();
            c.Reads = Array.Empty<string>();
            c.Writes = Array.Empty<string>();
            c.Tables = Array.Empty<ModelPlanItemTable>();       // per-table lines are detail; the plan's own Tables stay
            if (i.WaitsFor != null && i.WaitsFor.Length > 0)
            {
                c.WaitsFor = i.WaitsFor.Take(WaitsShown).Select(w => new PlanWaitRef { Id = w.Id }).ToArray();
                c.WaitsForCount = i.WaitsFor.Length > WaitsShown ? i.WaitsFor.Length : (int?)null;
            }
            if (i.ChangeSet != null)
            {
                c.ChangeSet = i.ChangeSet.ShallowCopy();
                c.ChangeSet.Lines = Array.Empty<ModelPlanChangeLine>();
                c.ChangeSet.Refused = Array.Empty<ModelPlanChangeRefusal>();
            }
            return c;
        }

        /// <summary>A short row without its reasons or its ledger links: what a plan of hundreds of rows keeps of each.</summary>
        private static ModelPlanItem Terse(ModelPlanItem i)
        {
            var c = i.ShallowCopy();
            c.Why = Array.Empty<string>();
            c.Events = Array.Empty<string>();
            return c;
        }

        /// <summary>The row with one page of every detail list, from the same offset: at most <paramref name="page"/> lines
        /// of each. The caller cuts the page shorter until the whole read fits.</summary>
        private static ModelPlanItem Window(ModelPlanItem i, int from, int page, int total, out int? next)
        {
            var c = i.ShallowCopy();
            c.Diff = Slice(i.Diff, from, page);
            c.Objects = Slice(i.Objects, from, page);
            c.Reads = Slice(i.Reads, from, page);
            c.Writes = Slice(i.Writes, from, page);
            c.WaitsFor = Slice(i.WaitsFor, from, page);
            c.Tables = (i.Tables ?? Array.Empty<ModelPlanItemTable>()).Select(t => new ModelPlanItemTable
                { Name = t.Name, Count = t.Count, What = t.What, Diff = Slice(t.Diff, from, page) }).ToArray();
            if (i.ChangeSet != null)
            {
                c.ChangeSet = i.ChangeSet.ShallowCopy();
                c.ChangeSet.Lines = Slice(i.ChangeSet.Lines, from, page);
                c.ChangeSet.Refused = Slice(i.ChangeSet.Refused, from, page);
            }
            next = from + page < total ? from + page : (int?)null;
            return c;
        }

        private static T[] Slice<T>(T[] all, int from, int page) =>
            all == null || from >= all.Length ? Array.Empty<T>() : all.Skip(from).Take(page).ToArray();
    }
}
