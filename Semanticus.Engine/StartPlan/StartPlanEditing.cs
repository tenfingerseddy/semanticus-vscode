// What an edit to the start plan MEANS (spec-first start, lane sf-decide; design S12 and section 5). One pure function over the
// document, so both doors (the Studio's edit_start_plan and the assistant's) get the same result and the same words.
//
// NEEDS FOLLOW (S12). A line can need another (a base measure's format, description, hide and Excel lines need the measure; the Excel
// line needs the hide). Dropping a measure drops its followers, and keeping a follower keeps its measure and what that measure's lines
// need, so no build can hide a column with no measure doing its adding. The base measure is a manifest entry, not a line (S4), so
// "dropping it" is a pick of none, and the pick it had is remembered (Pins["was-pick:" + identity]) so a keep can bring it back.
//
// KNOCK-ON ATTRIBUTION. Every line an edit moved besides the one the person named comes back in StartEditOutcome.KnockOns with the plain
// reason, and says so on the line itself (its Why), so the view can show "dropped with Total Revenue because you dropped it" before the
// person approves. A person never finds a change they did not ask for with no word on it.
//
// WHO MADE IT. An edit is the person's (human origin) or their assistant's (agent origin). Either is a hand-made decision the loop never
// overwrites (StartPlanDecide.IsHandMade). Any edit that changes anything clears the approval: the digest it was given for has moved.
//
// RENAMES. A rename rewrites the refs everywhere a ref is kept (the manifest, its links and base measures, every line and what it needs,
// the pins and the answers), so a rename is never read as one object going and another coming.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>A line an edit moved that the person did not name.</summary>
    public sealed class StartKnockOn
    {
        public string Ref { get; set; }
        public string Key { get; set; }
        /// <summary>"dropped", "kept", "removed" or "held" (the assistant's words, kept off the plan until their table is back).</summary>
        public string Action { get; set; }
        public string Because { get; set; }
    }

    internal sealed class StartEditOutcome
    {
        public bool Ok = true;
        /// <summary>The plain reason the edit was refused, when it was. The document is then untouched.</summary>
        public string Refused;
        public bool Changed;
        public readonly List<StartKnockOn> KnockOns = new List<StartKnockOn>();
        public static StartEditOutcome Refuse(string why) => new StartEditOutcome { Ok = false, Refused = why };
    }

    internal static class StartPlanEditing
    {
        private const string WasPickPrefix = "was-pick:";
        /// <summary>The name a base measure had under a pick it was replaced from ("was-name:" identity ":" pick), so a pick that goes back brings the measure back UNDER ITS OLD NAME and the words held on it return.</summary>
        private const string WasNamePrefix = "was-name:";

        internal static readonly string[] Ops =
            { "include_table", "include_column", "rename", "set_link", "set_pick", "pin", "drop_line", "keep_line", "answer", "set_fiscal_year_end", "set_table_sample" };

        internal static StartEditOutcome Apply(StartPlanDoc doc, StartPlanEdit edit, string origin)
        {
            if (doc == null) return StartEditOutcome.Refuse("There is no start plan to edit.");
            if (edit == null || string.IsNullOrWhiteSpace(edit.Op)) return StartEditOutcome.Refuse("Say what to change: " + string.Join(", ", Ops) + ".");
            doc.Manifest ??= new StructureManifest();
            var by = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? StartDecidedBy.Assistant : StartDecidedBy.Person;
            var who = by == StartDecidedBy.Assistant ? "your assistant" : "you";
            StartEditOutcome outcome;
            switch (edit.Op.Trim().ToLowerInvariant())
            {
                case "include_table": outcome = IncludeTable(doc, edit, who); break;
                case "include_column": outcome = IncludeColumn(doc, edit, who); break;
                case "rename": outcome = Rename(doc, edit, by); break;
                case "set_link": outcome = SetLink(doc, edit); break;
                case "set_pick": outcome = SetPick(doc, edit, by, who); break;
                case "pin": outcome = Pin(doc, edit, by); break;
                case "drop_line": outcome = DropLine(doc, edit, by, who); break;
                case "keep_line": outcome = KeepLine(doc, edit, by, who); break;
                case "answer": outcome = Answer(doc, edit, by, who); break;
                case "set_fiscal_year_end": outcome = SetFiscalYearEnd(doc, edit); break;
                case "set_table_sample": outcome = SetTableSample(doc, edit); break;
                default: return StartEditOutcome.Refuse("\"" + edit.Op + "\" is not something the plan can change. Use one of: " + string.Join(", ", Ops) + ".");
            }
            if (outcome.Ok && outcome.Changed)
            {
                doc.Manifest.RefreshTimeIntelligence();  // the derived list follows the bases, the links and the date table
                StartPlanHold.Refresh(doc);              // a hand-made line whose object went out is held, and one whose object is back is released
                doc.Approval = null;                     // any edit moves the digest, so the approval given for the old one is void
                doc.Parked = false;
                doc.UpdatedUtc = DateTime.UtcNow;
            }
            return outcome;
        }

        // ------------------------------------------------------------------ finding things

        private static ManifestTable FindTable(StartPlanDoc doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.StartsWith("table:", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
            return doc.Manifest.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                                           || (!string.IsNullOrEmpty(t.Identity) && string.Equals(t.Identity, name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>"Table/Column" or "column:Table/Column".</summary>
        private static (ManifestTable Table, ManifestColumn Column) FindColumn(StartPlanDoc doc, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return (null, null);
            target = target.Trim();
            if (target.StartsWith("column:", StringComparison.OrdinalIgnoreCase)) target = target.Substring(7);
            var slash = target.IndexOf('/');
            if (slash <= 0) return (null, null);
            var table = FindTable(doc, target.Substring(0, slash));
            var col = table?.Columns.FirstOrDefault(c => string.Equals(c.Name, target.Substring(slash + 1), StringComparison.OrdinalIgnoreCase));
            return (table, col);
        }

        /// <summary>A line by the id the view hands out (Item.Id: what the Studio presses), by its key (kind|ref|property), by "ref|property", or by its ref when that
        /// names one line. A table, a link or the date table is not a stored line (the view makes it from the manifest), so it comes back as <c>Shown</c>: Drop and Keep
        /// act on the manifest for it. UAT-J7-S1-003: the Studio sent the id and only the key was looked for, so Drop never found a line it was showing.</summary>
        private static (StartLine Line, ManifestBaseMeasure Measure, ManifestTimeMeasure Time, StartLine Shown, string Refused) FindLine(StartPlanDoc doc, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return (null, null, null, null, "Say which line.");
            var t = target.Trim();
            var byId = doc.Lines.Where(l => l.Item != null && !string.IsNullOrEmpty(l.Item.Id) && string.Equals(l.Item.Id, t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1) return (byId[0], null, null, null, null);
            var exact = doc.Lines.Where(l => string.Equals(StartPlanDecide.LineKey(l), t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0], null, null, null, null);
            // The view's own lines for measures and for the manifest's structure carry ids too (StartPlanViewBuilder.ShownId), so a press on one of them names it the same way.
            var shownLines = StartPlanViewBuilder.Build(doc).Sections.SelectMany(x => x.Groups).SelectMany(g => g.Lines).Where(l => l.Item != null && !doc.Lines.Contains(l)).ToList();
            var byShownId = shownLines.FirstOrDefault(l => string.Equals(l.Item.Id, t, StringComparison.OrdinalIgnoreCase));
            if (byShownId != null) t = byShownId.Item.Kind == "create_measure" ? byShownId.Item.ObjectRef : StartPlanDecide.LineKey(byShownId);
            var bm = doc.Manifest.BaseMeasures.FirstOrDefault(b => string.Equals(StartPlanDecide.LineKey(StartPlanEditing.MeasureLine(b)), t, StringComparison.OrdinalIgnoreCase)
                                                                    || string.Equals(StarterMeasuresFamily.MeasureRef(b.Table, b.Name), t, StringComparison.OrdinalIgnoreCase));
            if (bm != null) return (null, bm, null, null, null);
            // A time measure is not a stored line: the view makes it from its base measure, so it is found the same way a base measure is, by its line key or its ref.
            var tm = doc.Manifest.AllTimeMeasures().FirstOrDefault(x => string.Equals(StartPlanDecide.LineKey(TimeMeasureLine(x)), t, StringComparison.OrdinalIgnoreCase)
                                                                      || string.Equals(StarterMeasuresFamily.MeasureRef(x.Table, x.Name), t, StringComparison.OrdinalIgnoreCase));
            if (tm != null) return (null, null, tm, null, null);
            var loose = doc.Lines.Where(l => string.Equals((l.Item.ObjectRef ?? "") + "|" + (l.Item.Target ?? ""), t, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(l.Item.ObjectRef, t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count == 1) return (loose[0], null, null, null, null);
            if (loose.Count > 1) return (null, null, null, null, loose.Count + " lines are about " + t + ". Name one by its key: kind|ref|property.");
            var structural = shownLines.Where(l => l.Item.Kind is "include_table" or "create_relationship" or "mark_date_table"
                                                   && (string.Equals(StartPlanDecide.LineKey(l), t, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Item.ObjectRef, t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (structural.Count == 1) return (null, null, null, structural[0], null);
            return (null, null, null, null, "No line is called " + t + ".");
        }

        /// <summary>Drop or keep a table, a link or the date table: the manifest's own switch for it (include_table, set_link). The date table has none, so it is refused in words.</summary>
        private static StartEditOutcome ToggleShown(StartPlanDoc doc, StartLine shown, bool keep, string by, string who)
        {
            switch (shown.Item.Kind)
            {
                case "include_table":
                    return IncludeTable(doc, new StartPlanEdit { Op = "include_table", Target = shown.Item.ObjectName, Value = keep ? "true" : "false" }, who);
                case "create_relationship":
                    // A link the plan guessed from names is dropped (not built) and kept again; a declared one is switched off and on, as set_link does.
                    if (LinkGuesser.FindLink(doc, shown.Item.ObjectRef) is { Guessed: true } guessed)
                        return keep ? KeepLink(doc, guessed, by, who) : DropLink(doc, guessed, by, who);
                    return SetLink(doc, new StartPlanEdit { Op = "set_link", Target = shown.Item.ObjectName ?? "", Value = keep ? "on" : "off" });
                default:
                    return StartEditOutcome.Refuse("The date table is chosen by the plan, so there is nothing to " + (keep ? "keep" : "drop") + " on this line. Leave the table out or rename it to change it.");
            }
        }

        /// <summary>The line a time measure stands for as an edit target (the same key the view gives it).</summary>
        private static StartLine TimeMeasureLine(ManifestTimeMeasure t) => new StartLine
        {
            Item = new ChangeItem { ObjectRef = StarterMeasuresFamily.MeasureRef(t.Table, t.Name), ObjectName = t.Name, Kind = "create_measure", Target = "expression" },
        };

        /// <summary>The line a base measure stands for in a view and as an edit target (it is a manifest entry, never stored as a line).</summary>
        internal static StartLine MeasureLine(ManifestBaseMeasure b) => new StartLine
        {
            Item = new ChangeItem
            {
                ObjectRef = StarterMeasuresFamily.MeasureRef(b.Table, b.Name), ObjectName = b.Name, Kind = "create_measure", Target = "expression",
                Title = "Add the measure " + b.Name, Source = b.DecidedBy == StartDecidedBy.Jev ? JevFamily.Source : "deterministic",
                DecisionPoint = DecisionRegistry.ColumnSummarize,
            },
            DecidedBy = b.DecidedBy, Point = DecisionRegistry.ColumnSummarize, Kept = b.Pick != StarterMeasureTemplates.None,
        };

        // ------------------------------------------------------------------ needs

        /// <summary>The lines that need this one (or this base measure), and the lines those need in turn.</summary>
        private static List<StartLine> Followers(StartPlanDoc doc, string needsKey)
        {
            var found = new List<StartLine>();
            var queue = new Queue<string>(new[] { needsKey });
            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                foreach (var l in doc.Lines)
                {
                    if (found.Contains(l)) continue;
                    if (!string.Equals(StartPlanDecide.NeedsOf(l), key, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(l);
                    queue.Enqueue(JevFamily.NeedsKey(l.Item.Kind, l.Item.ObjectRef));
                }
            }
            return found;
        }

        private static StartLine Provider(StartPlanDoc doc, StartLine line) =>
            StartPlanDecide.NeedsOf(line) is { } needs
                ? doc.Lines.FirstOrDefault(o => !ReferenceEquals(o, line) && string.Equals(JevFamily.NeedsKey(o.Item.Kind, o.Item.ObjectRef), needs, StringComparison.OrdinalIgnoreCase))
                : null;

        private static void Mark(StartPlanDoc doc, StartLine l, bool kept, string by, string why, StartEditOutcome outcome, string action)
        {
            if (l.Kept == kept && l.DecidedBy == by) return;
            l.Kept = kept;
            l.DecidedBy = by;
            l.Item.Status = kept ? "approved" : "rejected";
            l.Item.DroppedBy = kept ? null : (by == StartDecidedBy.Assistant ? "assistant" : "person");
            l.Why = why;
            RecordKeep(l, StartDecidedBy.Rule);   // a knock-on is not a tick
            outcome.KnockOns.Add(new StartKnockOn { Ref = l.Item.ObjectRef, Key = StartPlanDecide.LineKey(l), Action = action, Because = why });
        }

        /// <summary>WHO TICKED IT. A format Semanticus could not prove fits is written only for a line a PERSON kept: the executor's format gate reads
        /// ChangeItem.KeptByPerson and the digest of the object the person was looking at (KeptDigest), which the Plan sets when its tick is pressed
        /// (LocalEngine.SetPlanItemAsync). The start plan's own keeps only said "Kept" on the line, so the gate read every one as "nobody ticked the line" and
        /// the format was never built. Every way a person decides ONE line (keep_line, an answer of keep, a pinned value) records it here. The digest is the one
        /// Jev worked the line out for, which is what the person saw; the gate still voids the keep when the object no longer computes that. A line that
        /// comes back as a KNOCK-ON of another keep (Mark) was not ticked, and an assistant's keep is not a person's: both clear it, so no format goes in
        /// that nobody looked at.</summary>
        private static void RecordKeep(StartLine line, string by)
        {
            var item = line.Item;
            if (by == StartDecidedBy.Person && line.Kept && LocalEngine.IsJevFormatLine(item))
            {
                item.KeptByPerson = true; item.KeptAgainst = item.Before; item.KeptDigest = item.WorkedOutDigest; item.KeptIdentity = null;   // the next round stamps what the keep is for
                return;
            }
            item.KeptByPerson = false; item.KeptAgainst = null; item.KeptDigest = null; item.KeptIdentity = null;
        }

        // ------------------------------------------------------------------ drop and keep

        private static StartEditOutcome DropLine(StartPlanDoc doc, StartPlanEdit edit, string by, string who)
        {
            var (line, measure, time, shown, refused) = FindLine(doc, edit.Target);
            if (refused != null)
                return LinkGuesser.FindLink(doc, edit.Target) is { } link ? DropLink(doc, link, by, who) : StartEditOutcome.Refuse(refused);
            if (shown != null) return ToggleShown(doc, shown, false, by, who);
            var outcome = new StartEditOutcome();
            if (measure != null) return DropMeasure(doc, measure, by, who, outcome);
            if (time != null) return DropTimeMeasure(doc, time, by, outcome);
            if (line.Kept == false && line.DecidedBy == by) return outcome;
            var name = line.Item.ObjectName ?? line.Item.ObjectRef;
            line.Kept = false; line.DecidedBy = by; line.Item.Status = "rejected"; line.Item.DroppedBy = by == StartDecidedBy.Assistant ? "assistant" : "person";
            line.Why = "Dropped by " + who + ".";
            RecordKeep(line, by);
            outcome.Changed = true;
            foreach (var f in Followers(doc, JevFamily.NeedsKey(line.Item.Kind, line.Item.ObjectRef)))
                Mark(doc, f, false, by, "Dropped with \"" + (line.Item.Title ?? line.Item.Kind) + "\" on " + name + ", because " + who + " dropped it and this line cannot stand without it.", outcome, "dropped");
            return outcome;
        }

        private static StartEditOutcome DropMeasure(StartPlanDoc doc, ManifestBaseMeasure bm, string by, string who, StartEditOutcome outcome)
        {
            if (bm.Pick == StarterMeasureTemplates.None) return outcome;
            doc.Pins[WasPickPrefix + bm.Identity] = bm.Pick;
            bm.Pick = StarterMeasureTemplates.None;
            bm.DecidedBy = by;
            outcome.Changed = true;
            foreach (var f in Followers(doc, StartPlanDecide.MeasureNeedsKey(bm)))
                Mark(doc, f, false, by, "Dropped with the measure " + bm.Name + ", because " + who + " dropped it and this line cannot stand without it.", outcome, "dropped");
            return outcome;
        }

        /// <summary>Leave one time measure out. It is the base measure's own list that remembers it (DroppedTimeKinds), so a rename of the base keeps the choice, the review shows
        /// the measure as left out, and the build leaves it out: the same list the review is made from. Nothing else needs it (a time measure has no follower lines).</summary>
        private static StartEditOutcome DropTimeMeasure(StartPlanDoc doc, ManifestTimeMeasure t, string by, StartEditOutcome outcome)
        {
            var bm = doc.Manifest.BaseMeasures.First(b => string.Equals(b.Table, t.Table, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Name, t.BaseMeasure, StringComparison.OrdinalIgnoreCase));
            bm.DroppedTimeKinds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var who = by == StartDecidedBy.Assistant ? StartDecidedBy.Assistant : StartDecidedBy.Person;
            if (bm.DroppedTimeKinds.TryGetValue(t.Kind, out var already) && already == who) return outcome;
            bm.DroppedTimeKinds[t.Kind] = who;
            outcome.Changed = true;
            return outcome;
        }

        private static StartEditOutcome KeepTimeMeasure(StartPlanDoc doc, ManifestTimeMeasure t, StartEditOutcome outcome)
        {
            var bm = doc.Manifest.BaseMeasures.First(b => string.Equals(b.Table, t.Table, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Name, t.BaseMeasure, StringComparison.OrdinalIgnoreCase));
            if (bm.DroppedTimeKinds != null && bm.DroppedTimeKinds.Remove(t.Kind)) outcome.Changed = true;
            return outcome;
        }

        private static StartEditOutcome KeepLine(StartPlanDoc doc, StartPlanEdit edit, string by, string who)
        {
            var (line, measure, time, shown, refused) = FindLine(doc, edit.Target);
            if (refused != null)
                return LinkGuesser.FindLink(doc, edit.Target) is { } link ? KeepLink(doc, link, by, who) : StartEditOutcome.Refuse(refused);
            if (shown != null) return ToggleShown(doc, shown, true, by, who);
            var outcome = new StartEditOutcome();
            if (measure != null) return KeepMeasure(doc, measure, by, who, outcome);
            if (time != null) return KeepTimeMeasure(doc, time, outcome);
            if (line.Kept && line.DecidedBy == by) { RecordKeep(line, by); return outcome; }   // already kept this way: the tick is still recorded on it
            // What this line needs comes back with it, up to the line nothing else is needed for.
            var chain = new List<StartLine>();
            ManifestBaseMeasure rootMeasure = null;
            var cursor = line;
            while (cursor != null)
            {
                var needs = StartPlanDecide.NeedsOf(cursor);
                if (needs == null) break;
                var bm = doc.Manifest.BaseMeasures.FirstOrDefault(b => string.Equals(StartPlanDecide.MeasureNeedsKey(b), needs, StringComparison.OrdinalIgnoreCase));
                if (bm != null)
                {
                    rootMeasure = bm;
                    if (bm.Pick == StarterMeasureTemplates.None)
                    {
                        if (!doc.Pins.TryGetValue(WasPickPrefix + bm.Identity, out var was))
                            return StartEditOutcome.Refuse("This line needs the measure " + bm.Name + ", which was left out, and the plan does not know how it should add up. Pick that first.");
                        bm.Pick = was;
                        bm.DecidedBy = by;
                        outcome.Changed = true;
                        outcome.KnockOns.Add(new StartKnockOn { Ref = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name), Key = StartPlanDecide.LineKey(MeasureLine(bm)), Action = "kept", Because = "Kept, because " + who + " kept a line that cannot stand without it." });
                    }
                    break;
                }
                cursor = Provider(doc, cursor);
                if (cursor != null) chain.Add(cursor);
            }
            line.Kept = true; line.DecidedBy = by; line.Item.Status = "approved"; line.Item.DroppedBy = null; line.Why = "Kept by " + who + ".";
            RecordKeep(line, by);
            outcome.Changed = true;
            foreach (var p in chain)
                Mark(doc, p, true, by, "Kept with \"" + (line.Item.Title ?? line.Item.Kind) + "\" on " + (line.Item.ObjectName ?? line.Item.ObjectRef) + ", because " + who + " kept it and it cannot stand without this line.", outcome, "kept");
            // Everything that needs the root of the chain comes back too: keeping any one line of a base measure brings back the rest of its lines.
            var rootKey = rootMeasure != null ? StartPlanDecide.MeasureNeedsKey(rootMeasure)
                : JevFamily.NeedsKey((chain.Count > 0 ? chain[chain.Count - 1] : line).Item.Kind, (chain.Count > 0 ? chain[chain.Count - 1] : line).Item.ObjectRef);
            foreach (var f in Followers(doc, rootKey).Where(f => !ReferenceEquals(f, line)))
                Mark(doc, f, true, by, "Kept with the line it needs, because " + who + " kept one of that line's pair.", outcome, "kept");
            return outcome;
        }

        private static StartEditOutcome KeepMeasure(StartPlanDoc doc, ManifestBaseMeasure bm, string by, string who, StartEditOutcome outcome)
        {
            if (bm.Pick != StarterMeasureTemplates.None) return outcome;
            if (!doc.Pins.TryGetValue(WasPickPrefix + bm.Identity, out var was))
                return StartEditOutcome.Refuse("The plan does not know how " + bm.Name + " should add up. Pick that first.");
            bm.Pick = was;
            bm.DecidedBy = by;
            outcome.Changed = true;
            foreach (var f in Followers(doc, StartPlanDecide.MeasureNeedsKey(bm)))
                Mark(doc, f, true, by, "Kept with the measure " + bm.Name + ", because " + who + " kept it.", outcome, "kept");
            return outcome;
        }

        // ------------------------------------------------------------------ the pick

        private static StartEditOutcome SetPick(StartPlanDoc doc, StartPlanEdit edit, string by, string who)
        {
            var (table, column) = FindColumn(doc, edit.Target);
            if (column == null) return StartEditOutcome.Refuse("No column is called " + edit.Target + ". Name it as Table/Column.");
            var pick = (edit.Value ?? "").Trim().ToLowerInvariant();
            if (!DecisionRegistry.SummarizeOptions.Contains(pick))
                return StartEditOutcome.Refuse("\"" + edit.Value + "\" is not a way to add up a column. Use one of: " + string.Join(", ", DecisionRegistry.SummarizeOptions) + ".");
            var outcome = new StartEditOutcome();
            var bm = StartPlanDecide.BaseMeasureFor(doc.Manifest, table.Name, column.Name);
            if (pick == StarterMeasureTemplates.None)
            {
                if (bm == null || bm.Pick == StarterMeasureTemplates.None) return outcome;
                return DropMeasure(doc, bm, by, who, outcome);
            }
            if (bm != null && bm.Pick == pick) return outcome;
            var name = bm?.Name != null && bm.Pick == StarterMeasureTemplates.None ? bm.Name : StarterMeasureTemplates.Name(pick, column.Name);
            if (bm != null && doc.Pins.TryGetValue(WasNamePrefix + bm.Identity + ":" + pick, out var rememberedName)) name = rememberedName;
            if (doc.Manifest.BaseMeasures.Any(b => !ReferenceEquals(b, bm) && string.Equals(b.Table, table.Name, StringComparison.OrdinalIgnoreCase)
                                                    && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                return StartEditOutcome.Refuse("Another measure in " + table.Name + " is already called " + name + ".");
            if (bm == null)
            {
                bm = new ManifestBaseMeasure
                {
                    Table = table.Name, Column = column.Name, Pick = pick, Name = name, DecidedBy = by,
                    Identity = StartPlanDecide.BaseMeasureIdentity(doc.Manifest, table.Name, column.Name),
                };
                StartPlanDecide.AddBase(doc.Manifest, bm);
            }
            else
            {
                // A new pick is a new measure: the lines the old one carried are about something that is gone.
                // The words and choices on the measure it replaces are HELD, never deleted: they come back if the pick goes back to the one they were made for.
                if (bm.Pick != StarterMeasureTemplates.None) HoldFollowers(doc, bm, "the measure " + bm.Name + " was replaced by " + who, outcome);
                if (bm.Pick != StarterMeasureTemplates.None && !string.IsNullOrEmpty(bm.Identity)) doc.Pins[WasNamePrefix + bm.Identity + ":" + bm.Pick] = bm.Name;
                bm.Pick = pick; bm.Name = name; bm.DecidedBy = by;
            }
            doc.Pins.Remove(WasPickPrefix + bm.Identity);
            outcome.Changed = true;
            return outcome;
        }

        /// <summary>Everything that is about a base measure and goes with it when its column is left out or its pick replaces it: the lines that need it, and the
        /// lines on the measure itself. A line the loop made is removed (the loop proposes it again); a hand-made one is HELD (StartPlanHold.RemoveOrHold).</summary>
        private static void HoldFollowers(StartPlanDoc doc, ManifestBaseMeasure bm, string because, StartEditOutcome outcome)
        {
            var measureRef = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name);
            var affected = Followers(doc, StartPlanDecide.MeasureNeedsKey(bm)).ToList();
            foreach (var l in doc.Lines.Where(l => string.Equals(l.Item.ObjectRef, measureRef, StringComparison.OrdinalIgnoreCase) && !affected.Contains(l))) affected.Add(l);
            StartPlanHold.RemoveOrHold(doc, affected, because, outcome);
        }

        // ------------------------------------------------------------------ pin and answer

        private static StartEditOutcome Pin(StartPlanDoc doc, StartPlanEdit edit, string by)
        {
            var (line, measure, time, shown, refused) = FindLine(doc, edit.Target);
            if (refused != null) return StartEditOutcome.Refuse(refused);
            if (shown != null) return StartEditOutcome.Refuse("This line is made by the plan from the source, so there is no value to keep on it. Use drop to leave it out.");
            if (time != null) return StartEditOutcome.Refuse("A time measure's formula is made by the plan and cannot be pinned. Drop the measure if you do not want it.");
            if (line == null) return StartEditOutcome.Refuse("A measure is pinned by its pick (set_pick), not by a value.");
            if (edit.Value == null) return StartEditOutcome.Refuse("Say the value to keep.");
            var outcome = new StartEditOutcome();
            doc.Pins[StartRefs.ShapeKey(doc.Manifest, line.Item)] = edit.Value;
            line.Item.After = edit.Value;
            line.Item.AuthoredAfter = null;
            line.Kept = true; line.DecidedBy = by; line.Item.Status = "approved"; line.Item.DroppedBy = null;
            RecordKeep(line, by);
            line.Why = "Pinned: " + (by == StartDecidedBy.Assistant ? "your assistant" : "you") + " chose this value and it is kept.";
            outcome.Changed = true;
            return outcome;
        }

        private static StartEditOutcome Answer(StartPlanDoc doc, StartPlanEdit edit, string by, string who)
        {
            if (string.IsNullOrWhiteSpace(edit.Target)) return StartEditOutcome.Refuse("Say which question.");
            var id = edit.Target.Trim();
            var value = (edit.Value ?? "").Trim();
            var colon = id.IndexOf(':');
            var kind = colon > 0 ? id.Substring(0, colon) : id;
            var rest = colon > 0 ? id.Substring(colon + 1) : "";
            StartEditOutcome outcome;
            if (kind == StartNeedKind.UnsurePick)
            {
                var bm = doc.Manifest.BaseMeasures.FirstOrDefault(b => string.Equals(b.Identity, rest, StringComparison.OrdinalIgnoreCase));
                if (bm == null) return StartEditOutcome.Refuse("That question is no longer open.");
                outcome = SetPick(doc, new StartPlanEdit { Op = "set_pick", Target = bm.Table + "/" + bm.Column, Value = value }, by, who);
                if (!outcome.Ok) return outcome;
                // Saying "yes, that is right" (the pick it already has) is an answer too: the measure is settled by hand now.
                if (bm.DecidedBy == StartDecidedBy.Rule && bm.Pick == value.ToLowerInvariant()) { bm.DecidedBy = by; outcome.Changed = true; }
            }
            else if (kind == StartNeedKind.UnsureLine || kind == StartNeedKind.Oscillation || kind == StartNeedKind.VoidedKeep)
            {
                var keep = string.Equals(value, "keep", StringComparison.OrdinalIgnoreCase);
                if (!keep && !string.Equals(value, "drop", StringComparison.OrdinalIgnoreCase)) return StartEditOutcome.Refuse("Answer keep or drop.");
                outcome = keep ? KeepLine(doc, new StartPlanEdit { Op = "keep_line", Target = rest }, by, who)
                               : DropLine(doc, new StartPlanEdit { Op = "drop_line", Target = rest }, by, who);
                if (!outcome.Ok) return outcome;
                outcome.Changed = true;
            }
            else if (kind == "fiscal_year")
            {
                // The fiscal year question: an answer is the year end itself (6/30) or "calendar", the same thing set_fiscal_year_end takes.
                outcome = SetFiscalYearEnd(doc, new StartPlanEdit { Op = "set_fiscal_year_end", Value = value });
                if (!outcome.Ok) return outcome;
            }
            else if (kind == "grant")
            {
                // THE SOURCE GRANT'S "NOT NOW" (Pro). The person says Jev is not to look at this source: the plan carries on without it, made the way a Free plan is
                // made (the source's own draft and the assistant's words), and nothing is asked. It is not a refusal that ends anything: a later grant (grant_start_disclosure,
                // the Allow button) clears it and Jev takes the plan up from where it is. Allowing is not done here, because a grant is bound to the digest of the plan the
                // person looked at and says which tables Jev may see; that is its own verb.
                var v = value.ToLowerInvariant();
                if (v == "not now" || v == "no" || v == "decline" || v == "not_now")
                {
                    if (doc.JevDeclined) return StartEditOutcome.Refuse("Jev is already switched off for this plan. Allow it from the plan when you want it back.");
                    doc.JevDeclined = true;
                    doc.Disclosure = null;
                    if (doc.Decide != null) { doc.Decide.State = StartDecideState.Off; doc.Decide.Reason = StartDecideCopy.DeclinedReason; doc.Decide.Round = 0; }
                    outcome = new StartEditOutcome();
                }
                else if (v == "allow" || v == "yes")
                    return StartEditOutcome.Refuse("Allowing Jev is done with grant_start_disclosure, quoting the plan's digest, so the permission is for the tables and sample you looked at.");
                else return StartEditOutcome.Refuse("Answer Not now to carry on without Jev. To allow it, use grant_start_disclosure.");
            }
            else outcome = new StartEditOutcome();   // another lane's question (the fiscal year start): recorded, nothing else moves here
            doc.Answers[id] = value;
            outcome.Changed = true;
            return outcome;
        }

        // ------------------------------------------------------------------ the manifest's own edits

        private static StartEditOutcome IncludeTable(StartPlanDoc doc, StartPlanEdit edit, string who)
        {
            var table = FindTable(doc, edit.Target);
            if (table == null) return StartEditOutcome.Refuse("No table is called " + edit.Target + ".");
            if (!TryFlag(edit.Value, out var include)) return StartEditOutcome.Refuse("Say true or false.");
            var outcome = new StartEditOutcome();
            if (table.Include == include) return outcome;
            table.Include = include;
            outcome.Changed = true;
            if (!include)
            {
                // The base measures stay in the manifest (IsBuilt says they do not build while their table is out), so the person's own pick comes back with the table.
                RemoveLinesAbout(doc, r => RefIsInTable(r, table.Name) || RefIsLinkOf(doc, r, table.Name), "the table " + table.Name + " was left out by " + who, outcome);
            }
            return outcome;
        }

        private static StartEditOutcome IncludeColumn(StartPlanDoc doc, StartPlanEdit edit, string who)
        {
            var (table, column) = FindColumn(doc, edit.Target);
            if (column == null) return StartEditOutcome.Refuse("No column is called " + edit.Target + ". Name it as Table/Column.");
            if (!TryFlag(edit.Value, out var include)) return StartEditOutcome.Refuse("Say true or false.");
            var outcome = new StartEditOutcome();
            if (column.Include == include) return outcome;
            column.Include = include;
            outcome.Changed = true;
            if (!include)
            {
                var colRef = "column:" + table.Name + "/" + column.Name;
                var bm = StartPlanDecide.BaseMeasureFor(doc.Manifest, table.Name, column.Name);
                // The base measure STAYS in the manifest (it builds nothing while its column is out), so the person's own pick and the name the assistant's words are
                // about come back with the column. What is about it is held or removed by the one function that decides (StartPlanHold.RemoveOrHold).
                if (bm != null) HoldFollowers(doc, bm, "the column " + column.Name + " was left out by " + who, outcome);
                RemoveLinesAbout(doc, r => string.Equals(r, colRef, StringComparison.OrdinalIgnoreCase) || RefIsLinkOfColumn(doc, r, table.Name, column.Name),
                    "the column " + column.Name + " was left out by " + who, outcome);
            }
            return outcome;
        }

        private static bool RefIsInTable(string r, string table)
        {
            if (string.IsNullOrEmpty(r)) return false;
            return string.Equals(r, "table:" + table, StringComparison.OrdinalIgnoreCase)
                   || r.StartsWith("column:" + table + "/", StringComparison.OrdinalIgnoreCase)
                   || r.StartsWith("measure:" + table + "/", StringComparison.OrdinalIgnoreCase)
                   || r.StartsWith("hierarchy:" + table + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static void RemoveLinesAbout(StartPlanDoc doc, Func<string, bool> about, string because, StartEditOutcome outcome) =>
            StartPlanHold.RemoveOrHold(doc, doc.Lines.Where(l => about(l.Item.ObjectRef)).ToList(), because, outcome);

        /// <summary>A line about a link that joins this table (its ref is "relationship:" and the link's identity).</summary>
        private static bool RefIsLinkOf(StartPlanDoc doc, string r, string table)
        {
            if (string.IsNullOrEmpty(r) || !r.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase)) return false;
            var id = r.Substring("relationship:".Length);
            return doc.Manifest.Links.Any(l => !string.IsNullOrEmpty(l.Identity) && string.Equals(l.Identity, id, StringComparison.OrdinalIgnoreCase)
                                               && (string.Equals(l.FromTable, table, StringComparison.OrdinalIgnoreCase) || string.Equals(l.ToTable, table, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>A line about a link that joins on this column.</summary>
        private static bool RefIsLinkOfColumn(StartPlanDoc doc, string r, string table, string column)
        {
            if (string.IsNullOrEmpty(r) || !r.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase)) return false;
            var id = r.Substring("relationship:".Length);
            return doc.Manifest.Links.Any(l => !string.IsNullOrEmpty(l.Identity) && string.Equals(l.Identity, id, StringComparison.OrdinalIgnoreCase)
                && ((string.Equals(l.FromTable, table, StringComparison.OrdinalIgnoreCase) && l.FromColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
                    || (string.Equals(l.ToTable, table, StringComparison.OrdinalIgnoreCase) && l.ToColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))));
        }

        private static bool TryFlag(string value, out bool flag)
        {
            flag = false;
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "include": flag = true; return true;
                case "false": case "no": case "off": case "exclude": flag = false; return true;
                default: return false;
            }
        }

        private static StartEditOutcome SetLink(StartPlanDoc doc, StartPlanEdit edit)
        {
            var t = (edit.Target ?? "").Trim();
            var link = doc.Manifest.Links.FirstOrDefault(l => !string.IsNullOrEmpty(l.Identity) && string.Equals(l.Identity, t, StringComparison.OrdinalIgnoreCase))
                       ?? doc.Manifest.Links.FirstOrDefault(l => string.Equals(LinkText(l), t, StringComparison.OrdinalIgnoreCase));
            if (link == null) return StartEditOutcome.Refuse("No link is called " + edit.Target + ".");
            if (!TryFlag(edit.Value, out var active)) return StartEditOutcome.Refuse("Say on or off.");
            if (link.Unresolved && active)
                return StartEditOutcome.Refuse("This link joins on a combined key (" + string.Join(" and ", link.FromColumns) + "), which Semanticus does not build. It stays out until the key is one column. " + link.Note);
            var outcome = new StartEditOutcome();
            if (link.Guessed)
            {
                // A guessed link turned on is checked the way the declared rule checks one: another active link between the same two tables, or a second path, keeps it off.
                if (active && LinkGuesser.WhyNotOn(doc.Manifest, link) is { } why) return StartEditOutcome.Refuse(why);
                if (link.Dropped && !active) return outcome;
                if (!link.Dropped && link.Active == active) return outcome;
                link.Dropped = false; link.DroppedBy = null;
                link.Active = active;
                if (active) link.Note = null;
                outcome.Changed = true;
                FollowLinks(doc, outcome, who: null);
                return outcome;
            }
            if (link.Active == active) return outcome;
            // A declared link turned on is checked the same way (join 10, J2): a second date role beside the first, or a link that would give a table a second path, keeps the plan
            // from ever being built, and it used to sit at "Still working out" for good. The person turns the other link off first.
            if (active && LinkGuesser.WhyNotOn(doc.Manifest, link) is { } clash) return StartEditOutcome.Refuse(clash);
            link.Active = active;
            outcome.Changed = true;
            return outcome;
        }

        /// <summary>Leave a link out. A guessed link is dropped: it is not built, and the plan works out its table roles, measures and date table again from the links that stand.
        /// A declared link is switched off, as <c>set_link</c> does (it is still built, off, so a measure can use it).</summary>
        private static StartEditOutcome DropLink(StartPlanDoc doc, ManifestLink link, string by, string who)
        {
            var outcome = new StartEditOutcome();
            if (link.Unresolved)
                return StartEditOutcome.Refuse("This link joins on a combined key (" + string.Join(" and ", link.FromColumns) + "), which Semanticus does not build, so there is nothing to drop. " + link.Note);
            if (!link.Guessed)
            {
                if (!link.Active) return outcome;
                link.Active = false;
                outcome.Changed = true;
                return outcome;
            }
            if (link.Dropped) return outcome;
            link.Dropped = true;
            link.DroppedBy = by == StartDecidedBy.Assistant ? "assistant" : "person";
            outcome.Changed = true;
            FollowLinks(doc, outcome, who);
            return outcome;
        }

        /// <summary>Bring a dropped guessed link back. It comes back as it was, unless another link took its place meanwhile (a twin, or a second path), and then it comes back off, in the rule's words.</summary>
        private static StartEditOutcome KeepLink(StartPlanDoc doc, ManifestLink link, string by, string who)
        {
            var outcome = new StartEditOutcome();
            if (link.Unresolved)
                return StartEditOutcome.Refuse("This link joins on a combined key (" + string.Join(" and ", link.FromColumns) + "), which Semanticus does not build. It stays out until the key is one column. " + link.Note);
            if (!link.Guessed)
            {
                if (link.Active) return outcome;
                if (LinkGuesser.WhyNotOn(doc.Manifest, link) is { } clash) return StartEditOutcome.Refuse(clash);   // the same check as set_link on (join 10, J2)
                link.Active = true;
                link.Note = null;
                outcome.Changed = true;
                return outcome;
            }
            if (!link.Dropped) return outcome;
            link.Dropped = false; link.DroppedBy = null;
            LinkGuesser.PlaceAgain(doc.Manifest, link);
            outcome.Changed = true;
            FollowLinks(doc, outcome, who);
            return outcome;
        }

        /// <summary>The plan follows its links: the roles, the starter measures the rule chose and the date table are worked out again, and each measure that came or went is a knock-on the person reads.</summary>
        private static void FollowLinks(StartPlanDoc doc, StartEditOutcome outcome, string who)
        {
            var m = doc.Manifest;
            string[] Built() => m.BuiltBases().Where(b => b.Pick != StarterMeasureTemplates.None).Select(b => b.Table + "\u001f" + b.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] Time() => m.TimeMeasures().Select(t => t.Table + "\u001f" + t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var before = Built().Concat(Time()).ToHashSet();
            m.RefreshFromLinks();
            var after = Built().Concat(Time()).ToHashSet();
            foreach (var gone in before.Except(after).OrderBy(x => x, StringComparer.Ordinal))
                outcome.KnockOns.Add(new StartKnockOn { Ref = "measure:" + gone.Replace("\u001f", "/"), Key = gone, Action = "dropped", Because = "Dropped, because the links it depended on changed: the table is no longer a fact table, or has no link to the date table." });
            foreach (var came in after.Except(before).OrderBy(x => x, StringComparer.Ordinal))
                outcome.KnockOns.Add(new StartKnockOn { Ref = "measure:" + came.Replace("\u001f", "/"), Key = came, Action = "kept", Because = "Added, because the links changed: the table is a fact table again, or reaches the date table." });
        }

        /// <summary>"Sales.CustomerKey -> Customer.CustomerKey": how a link reads when it has no identity yet.</summary>
        internal static string LinkText(ManifestLink l) =>
            l.FromTable + "." + string.Join("+", l.FromColumns) + " -> " + l.ToTable + "." + string.Join("+", l.ToColumns);

        /// <summary>Allow or refuse the sample of ONE table's rows (on or off). A refused table is still in the plan and Jev still sees its names and settings; only the counts and
        /// patterns worked out from its rows are held back. The same choice the person has in the permission dialog, on both doors (golden rule 2).</summary>
        private static StartEditOutcome SetTableSample(StartPlanDoc doc, StartPlanEdit edit)
        {
            var table = FindTable(doc, edit.Target);
            if (table == null) return StartEditOutcome.Refuse("No table is called " + edit.Target + ".");
            if (!TryFlag(edit.Value, out var allowed)) return StartEditOutcome.Refuse("Say on to allow this table's sample or off to refuse it.");
            var key = DisclosedScope.Norm(DisclosedScope.SourceNameOf(table));
            doc.SampleRefused ??= new List<string>();
            var outcome = new StartEditOutcome();
            var has = doc.SampleRefused.Any(r => string.Equals(DisclosedScope.Norm(r), key, StringComparison.Ordinal));
            if (allowed && has) { doc.SampleRefused.RemoveAll(r => string.Equals(DisclosedScope.Norm(r), key, StringComparison.Ordinal)); outcome.Changed = true; }
            else if (!allowed && !has) { doc.SampleRefused.Add(DisclosedScope.SourceNameOf(table)); outcome.Changed = true; }
            return outcome;
        }

        private static StartEditOutcome SetFiscalYearEnd(StartPlanDoc doc, StartPlanEdit edit)
        {
            if (string.IsNullOrEmpty(doc.Manifest.DateTable?.Name)) return StartEditOutcome.Refuse("This plan has no date table, so there is no fiscal year to set.");
            var value = (edit.Value ?? "").Trim();
            string normal = null;
            if (value.Length > 0 && !string.Equals(value, "calendar", StringComparison.OrdinalIgnoreCase))
            {
                // The manifest's own reading of a year end ("6/30", "06/30" and "6-30" are one), and its own rule that 12/31, DAX's default, adds nothing.
                var fy = StructureManifest.NormalizeYearEnd(value);
                if (fy == null) return StartEditOutcome.Refuse("Write the last day of the fiscal year as month/day, for example 6/30, or \"calendar\".");
                normal = fy == "12/31" ? null : fy;
            }
            var outcome = new StartEditOutcome();
            if (string.Equals(doc.Manifest.DateTable.FiscalYearEnd, normal, StringComparison.Ordinal)) return outcome;
            doc.Manifest.DateTable.FiscalYearEnd = normal;
            outcome.Changed = true;
            return outcome;
        }

        // ------------------------------------------------------------------ the assistant's business names, folded into the manifest

        /// <summary>The assistant's business names arrive as RENAME LINES (write_start_words, display_name): the executor applies them after everything else on the model
        /// the plan builds. The decide loop, though, looks at the plan by the names the MANIFEST gives, and a renamed table, column or measure the manifest does not know
        /// by its new name is, to the loop, one object gone and another new: it would make a second base measure under the new name and propose every line twice
        /// (found by the end-to-end run on J8: "dim_date/DOW_NO" and "Dim Date/DOW_NO" side by side). So before the loop looks, a rename of a table, a column or a base
        /// measure becomes a rename of the MANIFEST (the same one the person's own rename makes, which rewrites every ref the plan keeps), and the line is gone because
        /// the structure now says it. Tables go first, then columns, then measures, so a column's rename that was written against its table's old name still finds it.
        /// A rename the manifest cannot make (a time measure, a name another object has) stays a line for the executor. Returns how many were folded.</summary>
        internal static int FoldRenames(StartPlanDoc doc)
        {
            var folded = 0;
            int Order(StartLine l) => StartPlanWords.Split(l.Item.ObjectRef, out var k, out _, out _) ? (k == "table" ? 0 : k == "column" ? 1 : 2) : 3;
            foreach (var line in doc.Lines.Where(l => l.Kept && l.Item != null && string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                                                      && !string.IsNullOrWhiteSpace(l.Item.After)).OrderBy(Order).ToList())
            {
                if (!doc.Lines.Contains(line) || Order(line) > 2) continue;
                var by = line.DecidedBy == StartDecidedBy.Assistant ? StartDecidedBy.Assistant : StartDecidedBy.Person;
                var trial = StartPlanStore.Copy(doc);
                var outcome = Rename(trial, new StartPlanEdit { Op = "rename", Target = line.Item.ObjectRef, Value = line.Item.After.Trim() }, by);
                if (!outcome.Ok || !outcome.Changed) continue;   // the manifest cannot (or need not) make it: the line stays for the executor
                Rename(doc, new StartPlanEdit { Op = "rename", Target = line.Item.ObjectRef, Value = line.Item.After.Trim() }, by);
                doc.Lines.Remove(line);
                folded++;
            }
            if (folded > 0) doc.Manifest.RefreshTimeIntelligence();
            return folded;
        }

        // ------------------------------------------------------------------ rename

        private static StartEditOutcome Rename(StartPlanDoc doc, StartPlanEdit edit, string by)
        {
            var target = (edit.Target ?? "").Trim();
            var newName = (edit.Value ?? "").Trim();
            if (newName.Length == 0) return StartEditOutcome.Refuse("Say the new name.");
            var colon = target.IndexOf(':');
            var kind = colon > 0 ? target.Substring(0, colon).ToLowerInvariant() : "table";
            var body = colon > 0 ? target.Substring(colon + 1) : target;
            var m = doc.Manifest;
            var outcome = new StartEditOutcome();
            Func<string, string> map;
            switch (kind)
            {
                case "table":
                {
                    var table = FindTable(doc, body);
                    if (table == null) return StartEditOutcome.Refuse("No table is called " + body + ".");
                    if (string.Equals(table.Name, newName, StringComparison.Ordinal)) return outcome;
                    if (m.Tables.Any(t => !ReferenceEquals(t, table) && string.Equals(t.Name, newName, StringComparison.OrdinalIgnoreCase)))
                        return StartEditOutcome.Refuse("A table is already called " + newName + ".");
                    var old = table.Name;
                    map = r => RefIsInTable(r, old) ? ReplaceTable(r, old, newName) : r;
                    table.Name = newName;
                    foreach (var b in m.BaseMeasures.Where(b => string.Equals(b.Table, old, StringComparison.OrdinalIgnoreCase))) b.Table = newName;
                    foreach (var l in m.Links)
                    {
                        if (string.Equals(l.FromTable, old, StringComparison.OrdinalIgnoreCase)) l.FromTable = newName;
                        if (string.Equals(l.ToTable, old, StringComparison.OrdinalIgnoreCase)) l.ToTable = newName;
                    }
                    if (m.DateTable != null && string.Equals(m.DateTable.Name, old, StringComparison.OrdinalIgnoreCase)) m.DateTable.Name = newName;
                    break;
                }
                case "column":
                {
                    var (table, column) = FindColumn(doc, body);
                    if (column == null) return StartEditOutcome.Refuse("No column is called " + body + ". Name it as Table/Column.");
                    if (string.Equals(column.Name, newName, StringComparison.Ordinal)) return outcome;
                    if (table.Columns.Any(c => !ReferenceEquals(c, column) && string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase)))
                        return StartEditOutcome.Refuse("A column in " + table.Name + " is already called " + newName + ".");
                    var oldRef = "column:" + table.Name + "/" + column.Name;
                    var newRef = "column:" + table.Name + "/" + newName;
                    var oldName = column.Name;
                    map = r => string.Equals(r, oldRef, StringComparison.OrdinalIgnoreCase) ? newRef : r;
                    column.Name = newName;
                    foreach (var b in m.BaseMeasures.Where(b => string.Equals(b.Table, table.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Column, oldName, StringComparison.OrdinalIgnoreCase)))
                        b.Column = newName;
                    foreach (var l in m.Links)
                    {
                        if (string.Equals(l.FromTable, table.Name, StringComparison.OrdinalIgnoreCase)) l.FromColumns = l.FromColumns.Select(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase) ? newName : c).ToArray();
                        if (string.Equals(l.ToTable, table.Name, StringComparison.OrdinalIgnoreCase)) l.ToColumns = l.ToColumns.Select(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase) ? newName : c).ToArray();
                    }
                    if (m.DateTable != null && string.Equals(m.DateTable.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.DateTable.DateColumn, oldName, StringComparison.OrdinalIgnoreCase)) m.DateTable.DateColumn = newName;
                    break;
                }
                case "measure":
                {
                    var bm = m.BaseMeasures.FirstOrDefault(b => string.Equals(StarterMeasuresFamily.MeasureRef(b.Table, b.Name), "measure:" + body, StringComparison.OrdinalIgnoreCase));
                    if (bm == null) return StartEditOutcome.Refuse("No base measure is called " + body + ". A measure the plan does not make is renamed after the build.");
                    if (string.Equals(bm.Name, newName, StringComparison.Ordinal)) return outcome;
                    if (m.BaseMeasures.Any(b => !ReferenceEquals(b, bm) && string.Equals(b.Table, bm.Table, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Name, newName, StringComparison.OrdinalIgnoreCase)))
                        return StartEditOutcome.Refuse("Another measure in " + bm.Table + " is already called " + newName + ".");
                    var oldRef = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name);
                    var newRef = StarterMeasuresFamily.MeasureRef(bm.Table, newName);
                    var oldName = bm.Name; var table = bm.Table;
                    // The time measures take their names from the base ("Total Sales YTD"), so a rename moves them too, and what is held or written about them follows.
                    map = r =>
                    {
                        if (string.Equals(r, oldRef, StringComparison.OrdinalIgnoreCase)) return newRef;
                        foreach (var v in StructureManifest.TimeIntelligenceVariants)
                            if (string.Equals(r, StarterMeasuresFamily.MeasureRef(table, oldName + " " + v), StringComparison.OrdinalIgnoreCase))
                                return StarterMeasuresFamily.MeasureRef(table, newName + " " + v);
                        return r;
                    };
                    bm.Name = newName;
                    bm.DecidedBy = by;
                    break;
                }
                default: return StartEditOutcome.Refuse("A rename names a table, a column or a base measure: table:Name, column:Table/Name or measure:Table/Name.");
            }
            RewriteRefs(doc, map);
            outcome.Changed = true;
            return outcome;
        }

        private static string ReplaceTable(string r, string old, string @new)
        {
            var colon = r.IndexOf(':');
            var kind = r.Substring(0, colon);
            var rest = r.Substring(colon + 1);
            return kind + ":" + @new + rest.Substring(old.Length);
        }

        /// <summary>Rewrite a ref everywhere the plan keeps one: each line's ref and name, what it needs, the pins, the answers.</summary>
        private static void RewriteRefs(StartPlanDoc doc, Func<string, string> map)
        {
            string NeedsKey(string key)
            {
                if (key == null) return null;
                var i = key.IndexOf('\u001f');
                return i < 0 ? key : key.Substring(0, i + 1) + map(key.Substring(i + 1));
            }
            string LineKey(string key)   // kind|ref|property
            {
                var parts = key.Split('|');
                if (parts.Length < 2) return key;
                parts[1] = map(parts[1]);
                return string.Join("|", parts);
            }
            foreach (var l in doc.Lines)
            {
                var before = l.Item.ObjectRef;
                l.Item.ObjectRef = map(l.Item.ObjectRef);
                if (!string.Equals(before, l.Item.ObjectRef, StringComparison.Ordinal))
                {
                    l.Item.ObjectName = ObjectNameOf(l.Item.ObjectRef) ?? l.Item.ObjectName;
                    // A rename line states the name the object has NOW (the executor refuses a line whose Before is not what the object holds): when the ref moved
                    // because a table, column or base measure was renamed, the name it holds moved with it.
                    if (string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase) && l.Item.Before != null)
                        l.Item.Before = ObjectNameOf(l.Item.ObjectRef) ?? l.Item.Before;
                }
                if (l.Item.Needs != null) l.Item.Needs = NeedsKey(l.Item.Needs);
                if (l.Item.Args != null && l.Item.Args.TryGetValue("needs", out var n))
                    l.Item.Args = new Dictionary<string, string>(l.Item.Args, StringComparer.Ordinal) { ["needs"] = NeedsKey(n) };
            }
            doc.Pins = doc.Pins.ToDictionary(p => p.Key.StartsWith(WasPickPrefix, StringComparison.Ordinal) || p.Key.StartsWith(WasNamePrefix, StringComparison.Ordinal) ? p.Key : ShapeRewrite(p.Key, map), p => p.Value, StringComparer.Ordinal);
            doc.Answers = doc.Answers.ToDictionary(a =>
            {
                var colon = a.Key.IndexOf(':');
                if (colon < 0) return a.Key;
                var kind = a.Key.Substring(0, colon);
                return kind == StartNeedKind.UnsureLine || kind == StartNeedKind.Oscillation ? kind + ":" + LineKey(a.Key.Substring(colon + 1)) : a.Key;
            }, a => a.Value, StringComparer.Ordinal);
        }

        /// <summary>A pin's key is the line's shape (kind, canonical ref, property); a rename keeps the identity-based part, but a key written
        /// from names (the manifest has none) is rewritten the same way a ref is.</summary>
        private static string ShapeRewrite(string key, Func<string, string> map)
        {
            var parts = key.Split('\u001f');
            if (parts.Length >= 2) parts[1] = map(parts[1]);
            return string.Join("\u001f", parts);
        }

        private static string ObjectNameOf(string objectRef)
        {
            if (string.IsNullOrEmpty(objectRef)) return null;
            var i = objectRef.LastIndexOf('/');
            if (i >= 0) return objectRef.Substring(i + 1);
            var c = objectRef.IndexOf(':');
            return c >= 0 ? objectRef.Substring(c + 1) : null;
        }
    }
}
