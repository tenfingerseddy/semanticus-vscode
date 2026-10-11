// The start plan's decide loop, the PURE half (spec-first start, lane sf-decide; design-spec-first-start-v2.md sections 1 to 4).
//
// WHAT THIS FILE DECIDES, AND WHAT IT NEVER TOUCHES. Everything here is a function of a StartPlanDoc and the results of one round
// (Jev's lines, the rules' lines, what the scratch model holds). It reads no model and asks nothing: the asking and the scratch are
// LocalEngine.StartPlanDecide.cs. So every rule below is tested without an engine.
//
// ONE ROUND, IN ORDER (section 2.3):
//   1. the scratch is rebuilt from the manifest with every KEPT line already applied (so a round sees the model the build will make);
//   2. the Jev families run over it (LocalEngine.JevFamilies.cs, through the JevRunTarget seam) and the rules run over it;
//   3. ARBITRATE (Merge): the rules stand aside where Jev decided, one line per change (OneLinePerChange), a person's pin, keep or
//      drop is never overwritten, and a line that already stands stays until the family answers again for it;
//   4. FOLD the base measure picks into the manifest (S4): Jev's pick IS the base measure, so the family's create_measure line is not
//      kept, and its followers (format, description, hide...) stay as lines that need the measure;
//   5. count what moved (an add, a removal or a changed value). A round that moves nothing is stable (S10).
//
// WHY A LINE STANDS UNTIL THE FAMILY ANSWERS AGAIN. The scratch has every kept line applied, so the next round's families see a model
// where those writes are already made and propose nothing for them. That is the Plan's own rule ("a decision holds until Jev answers
// again for that object", LocalEngine.JevFamilies.cs) and it is what makes a second decide send nothing: the final round asked about
// the model the build will make, which is the model the Plan's own run will ask about after it.
//
// WHERE A LINE STOPS STANDING. When the object it names is no longer in the scratch (an excluded table, a renamed column whose line
// was not carried), and when a line it needs is gone (a hide whose base measure the pick no longer makes).
//
// CONTRACT NOTES (raised in the lane report, not changed here): ChangeItem.Needs is internal, so System.Text.Json drops it when the
// document is stored. Needs is therefore ALSO kept in Args["needs"] (a public dictionary), and NeedsOf reads either.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>The plain words the decide loop writes into a document. Constants so a test and the view can recognise them.</summary>
    internal static class StartDecideCopy
    {
        /// <summary>The start of the Why on a line the loop froze because Jev kept changing its mind about it (shown in Needs you).</summary>
        public const string OscillatesPrefix = "Jev keeps changing its mind about this one";
        /// <summary>The start of the Why on a line the executor refused to apply: it is left out, so the plan shows only what the build will make.</summary>
        public const string LeftOutPrefix = "Left out, because it does not apply here: ";
        public const string RoundCapReason = "Jev kept finding more to change after six rounds, so this plan may not be settled yet.";
        public const string WaitingGrantReason = "Jev needs your permission before it looks at this plan.";
        public const string NoServiceReason = "No suggestion service is set up, so nothing was asked.";
        public const string FreeReason = "Jev is part of Semanticus Pro, so nothing was asked.";
        public const string NoGrantReason = "Jev has not been shown this plan's source and tables yet, so nothing was asked.";
        public const string DeclinedReason = "You chose to carry on without Jev, so nothing was asked. You can allow it later.";
        // A stop nobody can act on reads as the one calm sentence the Plan and the Ask use (Kane, 8 Oct, r1 join).
        public static string StoppedReason(string why) =>
            JevDecisionOracle.IsNotAvailable(why) ? JevDecisionOracle.NotAvailableReason
            : "Jev stopped before it had looked at everything" + (string.IsNullOrWhiteSpace(why) ? "." : ": " + why + ".");
    }

    /// <summary>The kinds of line in the Needs-you band that the loop produces.</summary>
    internal static class StartNeedKind
    {
        public const string UnsurePick = "unsure_pick";
        public const string UnsureLine = "unsure_line";
        public const string Oscillation = "oscillation";
        /// <summary>A keep the person made that no longer counts, because the object it was for computes something else now. Asked again, so Approve is never offered with it unseen.</summary>
        public const string VoidedKeep = "voided_keep";
    }

    /// <summary>What one round changed, counted the way S10 says: an add, a removal, or a changed value.</summary>
    internal sealed class StartRoundChange
    {
        public int Added, Removed, Changed;
        /// <summary>Base measures made, removed or re-picked this round.</summary>
        public int Measures;
        public readonly List<string> Frozen = new List<string>();
        public int Total => Added + Removed + Changed + Measures;
        public bool Any => Total > 0;
    }

    internal static class StartRefs
    {
        /// <summary>The ref of an object with its tables and columns named by their manifest identity, so a rename between two rounds is
        /// not read as one object going and another coming (the canonical identity, S3). A base measure is named by ITS identity.
        /// Anything the manifest does not know keeps its own ref.</summary>
        public static string Canon(StructureManifest manifest, string objectRef)
        {
            if (string.IsNullOrEmpty(objectRef) || manifest == null) return objectRef ?? "";
            var colon = objectRef.IndexOf(':');
            if (colon < 0) return objectRef;
            var kind = objectRef.Substring(0, colon);
            var body = objectRef.Substring(colon + 1);
            var slash = body.IndexOf('/');
            var tableName = slash < 0 ? body : body.Substring(0, slash);
            var rest = slash < 0 ? null : body.Substring(slash + 1);
            var table = FindTable(manifest, tableName);
            switch (kind)
            {
                case "table":
                    return table == null ? objectRef : "table:#" + IdOf(table.Identity, table.Name);
                case "column":
                {
                    if (table == null) return objectRef;
                    var col = table.Columns.FirstOrDefault(c => string.Equals(c.Name, rest, StringComparison.OrdinalIgnoreCase));
                    return "column:#" + IdOf(table.Identity, table.Name) + "/" + (col == null ? rest : "#" + IdOf(col.Identity, col.Name));
                }
                case "measure":
                {
                    var bm = manifest.BaseMeasures.FirstOrDefault(b => string.Equals(b.Table, tableName, StringComparison.OrdinalIgnoreCase)
                                                                       && string.Equals(b.Name, rest, StringComparison.OrdinalIgnoreCase));
                    if (bm != null && !string.IsNullOrEmpty(bm.Identity)) return "measure:#" + bm.Identity;
                    return table == null ? objectRef : "measure:#" + IdOf(table.Identity, table.Name) + "/" + rest;
                }
                default:
                    return objectRef;
            }
        }

        private static string IdOf(string identity, string name) => string.IsNullOrEmpty(identity) ? name : identity;

        public static ManifestTable FindTable(StructureManifest m, string name) =>
            m?.Tables?.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>What a line is ABOUT, without its value: the same proposal whatever text it carries. Canonical, so a rename keeps it.</summary>
        public static string ShapeKey(StructureManifest m, ChangeItem item) =>
            (item.Kind ?? "") + "\u001f" + Canon(m, item.ObjectRef) + "\u001f" + (item.Target ?? "").ToLowerInvariant();
    }

    internal static class StartPlanDecide
    {
        // ------------------------------------------------------------------ consent

        private static string Norm(string name) => (name ?? "").Trim().ToLowerInvariant();

        internal static string SourceNameOf(ManifestTable t) =>
            !string.IsNullOrWhiteSpace(t.SourceName) ? (string.IsNullOrWhiteSpace(t.SourceSchema) ? t.SourceName : t.SourceSchema + "." + t.SourceName) : t.Name ?? "";

        /// <summary>The Disclosure that names exactly what the plan discloses right now: its source, every included table, and the sample flag. What a grant
        /// records, and what a test that stands in for the grant records.</summary>
        internal static StartDisclosure DisclosureFor(StartPlanDoc doc, bool sampleFacts) => new StartDisclosure
        {
            SourceKey = (doc.Source?.Server ?? "").Trim() + "/" + (doc.Source?.Database ?? "").Trim(),
            IncludedTables = (doc.Manifest?.Tables ?? new List<ManifestTable>()).Where(t => t != null && t.Include).Select(SourceNameOf).ToArray(),
            SampleFacts = sampleFacts, SampleTables = sampleFacts ? DisclosedScope.Of(doc, true).SampleTables : null, Utc = DateTime.UtcNow,
        };

        /// <summary>WHY NO ASK MAY GO NOW, or null when one may, judged from the plan's own Disclosure. Fail shut: nothing reaches Jev while the person chose to carry on
        /// without it, while the plan has no Disclosure, or while the plan discloses more than the Disclosure named (a table put back in after the grant, another
        /// source, the sample switched on or off). A table LEFT OUT since the grant is narrower, never a reason to stop.
        ///
        /// THE RULES ARE sf-consent's, not a second copy: this is <see cref="StartPlanConsent.WhyNot"/> over the plan's own copy of what was granted. The
        /// engine's real bar (the grant kept beside the model's permission, read fresh on every ask) is <c>StartAskFenceAsync</c> and the loop uses it;
        /// this form serves a plan held outside an engine. It does NOT read the decide state: a plan that waits for its grant leaves that wait the moment the
        /// grant is there, a table is taken back out, or the model's permission returns, with no new grant needed (the wiring step, W7).</summary>
        internal static string WhyNoAsk(StartPlanDoc doc, bool sampleFacts)
        {
            if (doc == null) return StartDecideCopy.NoGrantReason;
            if (doc.JevDeclined) return StartDecideCopy.DeclinedReason;
            var granted = DisclosedScope.Of(doc.Disclosure);
            if (granted == null) return StartDecideCopy.NoGrantReason;
            return StartPlanConsent.WhyNot(granted, DisclosedScope.Of(doc, sampleFacts || doc.SampleFacts), null);
        }

        // ------------------------------------------------------------------ lines

        /// <summary>A person's word, or their assistant's (an edit through either door): the loop never overwrites it.</summary>
        internal static bool IsHandMade(string decidedBy) => decidedBy == StartDecidedBy.Person || decidedBy == StartDecidedBy.Assistant;

        /// <summary>Whose line it is, from the family's own marks: the rules' standing-in lines (the principled rule's pick, the "stop adding
        /// up" and "hide" lines that are good practice and nobody's pick) are the rule's, every other Jev-family line is Jev's.</summary>
        /// <summary>A line that carries hand-written words: the assistant's (write_start_words) or the person's own edit of them. The loop and an edit that leaves an object
        /// out hold it, never delete it.</summary>
        internal static bool IsAssistantWords(StartLine line) =>
            line?.Item != null && line.Word != null && IsHandMade(line.DecidedBy);

        /// <summary>True when the object is out of the plan by the person's choice (a table, column, measure or link left out). See <see cref="StartPlanHold.IsOutByChoice"/>.</summary>
        internal static bool IsOutByChoice(StartPlanDoc doc, string objectRef) => StartPlanHold.IsOutByChoice(doc, objectRef);

        internal static string DecidedByOf(ChangeItem line)
        {
            if (!JevFamily.IsJevLine(line)) return StartDecidedBy.Rule;
            return line.RuleId == ColumnSummarizeFamily.OffRuleId || line.RuleId == ColumnSummarizeFamily.HideRuleId
                   || line.RuleId == ColumnSummarizeFamily.PrincipledRuleId
                ? StartDecidedBy.Rule : StartDecidedBy.Jev;
        }

        internal static StartLine ToStartLine(ChangeItem item, string decidedBy, int round)
        {
            var copy = item.Clone();
            // Needs is internal and never serialised: keep it where the stored document keeps it (see the file's CONTRACT NOTES).
            if (item.Needs != null)
                copy.Args = new Dictionary<string, string>(item.Args ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["needs"] = item.Needs };
            var kept = string.Equals(item.Status, "approved", StringComparison.OrdinalIgnoreCase);
            return new StartLine
            {
                Item = copy, DecidedBy = decidedBy, Point = item.DecisionPoint, Round = round, Kept = kept,
                Why = item.Reason ?? item.Rationale,
            };
        }

        /// <summary>The line a line needs, as <see cref="JevFamily.NeedsKey"/> spells it, or null. Reads the internal field when it is there
        /// (a line made in this process) and the stored copy otherwise.</summary>
        internal static string NeedsOf(StartLine line)
        {
            if (line?.Item == null) return null;
            if (line.Item.Needs != null) return line.Item.Needs;
            return line.Item.Args != null && line.Item.Args.TryGetValue("needs", out var n) ? n : null;
        }

        /// <summary>Where each object ends up once the plan's kept rename lines have run: "measure:T/Old" to "measure:T/New", followed through a chain of renames. A ref
        /// nothing renames comes back as it is.</summary>
        internal static Func<string, string> RenameFold(StartPlanDoc doc)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in doc.Lines.Where(l => l?.Item != null && l.Kept && string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                                                   && !string.IsNullOrWhiteSpace(l.Item.After) && !string.IsNullOrEmpty(l.Item.ObjectRef)))
            {
                var slash = l.Item.ObjectRef.LastIndexOf('/');
                if (slash >= 0) map[l.Item.ObjectRef] = l.Item.ObjectRef.Substring(0, slash + 1) + l.Item.After.Trim();
            }
            return r =>
            {
                for (var hops = 0; hops < 8 && r != null && map.TryGetValue(r, out var next); hops++) r = next;
                return r;
            };
        }

        /// <summary>The ChangeItem the executor applies: the stored line with its Needs restored and its status set from Kept.</summary>
        internal static ChangeItem ToChangeItem(StartLine line)
        {
            var c = line.Item.Clone();
            c.Needs = NeedsOf(line);
            c.Status = line.Kept ? "approved" : "rejected";
            return c;
        }

        /// <summary>The key a person's pin, drop or keep is made on, and the one the view hands out: the line's kind, its object and the
        /// property it writes. Exact text (case-insensitive), so a person or an assistant can paste it back.</summary>
        internal static string LineKey(StartLine line) =>
            (line.Item.Kind ?? "") + "|" + (line.Item.ObjectRef ?? "") + "|" + (line.Item.Target ?? "");

        internal static string CanonLineKey(StructureManifest m, StartLine line) => StartRefs.ShapeKey(m, line.Item);

        /// <summary>The executor refused these kept lines while building the scratch (a format that stopped being provable once the batch's other
        /// lines were made, a fix whose rule no longer raises the object). A line the loop made is LEFT OUT with the reason, so the plan never
        /// promises what the build will not write; a person's or assistant's own line keeps its place and says why it will not land. Returns how
        /// many loop lines changed.</summary>
        internal static int LeaveOutRefused(StartPlanDoc doc, IEnumerable<(StartLine Line, string Reason)> refused)
        {
            var changed = 0;
            foreach (var (line, rawReason) in refused ?? Enumerable.Empty<(StartLine, string)>())
            {
                if (line == null || !doc.Lines.Contains(line)) continue;
                var reason = InStartPlanWords(rawReason);
                var why = StartDecideCopy.LeftOutPrefix + (string.IsNullOrWhiteSpace(reason) ? "it could not be applied." : reason);
                if (IsHandMade(line.DecidedBy))
                {
                    // A hand-made line the executor will not write is HELD with the reason (StartPlanHold.CannotLandPrefix), so the preview and the build skip it instead of
                    // refusing the whole plan for it; it is tried again next round. Its Why stays what the person or assistant did to it: the line IS kept, so it never
                    // reads "Left out" (that is for a line the loop made), and the reason is Held's.
                    line.Held = StartPlanHold.CannotLandPrefix + (string.IsNullOrWhiteSpace(reason) ? "it could not be applied." : reason);
                    continue;
                }
                if (!line.Kept) continue;
                line.Kept = false;
                line.Item.Status = "rejected";
                line.Why = why;
                changed++;
            }
            return changed;
        }

        /// <summary>The executor's reason for refusing a line is worded for the Plan, where a person TICKS a line. On the start plan the person KEEPS it, so the same
        /// reason is said in those words (join 6, J3): "nobody ticked the line ... tick it on the Plan" is untrue here, and sends the person to a page that has no such line.</summary>
        internal static string InStartPlanWords(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return reason;
            return reason
                .Replace("and nobody ticked the line, so it was not written. Check it, and tick it on the Plan if it fits.",
                         "and you have not kept this line yourself, so it was not written. Check it, and keep it on this plan if it fits.")
                .Replace("so your tick was set aside", "so your keep was set aside")
                .Replace("so your tick no longer counts", "so your keep no longer counts")
                .Replace("You ticked this line", "You kept this line")
                .Replace("Tick it again on the Plan", "Keep it again on this plan")
                .Replace("tick it on the Plan", "keep it on this plan");
        }

        internal static bool IsLeftOut(StartLine line) => line?.Why != null && line.Why.StartsWith(StartDecideCopy.LeftOutPrefix, StringComparison.Ordinal);

        // ------------------------------------------------------------------ base measures

        internal static void AddBase(StructureManifest m, ManifestBaseMeasure b) => m.BaseMeasures.Add(b);
        internal static bool RemoveBase(StructureManifest m, ManifestBaseMeasure b) => m.BaseMeasures.Remove(b);
        internal static int RemoveBases(StructureManifest m, Func<ManifestBaseMeasure, bool> match) => m.BaseMeasures.RemoveAll(x => match(x));

        /// <summary>The identity the manifest itself gives a base measure (<c>StructureManifest.Derive</c>: the seed, "measure", and the SOURCE table and
        /// column), so a measure the loop makes and one Derive makes for the same column are the same object, and a rename never moves it.</summary>
        internal static string BaseMeasureIdentity(StructureManifest m, string table, string column)
        {
            var t = StartRefs.FindTable(m, table);
            var c = t?.Columns?.FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
            var key = t == null || c == null ? table + "\u001f" + column : LocalEngine.SchemaKey(t.SourceSchema, t.SourceName) + "\u001f" + c.SourceColumn;
            return StartIdentity.Of(m.IdentitySeed, "measure", key) ?? StartIdentity.Derived("measure", key);
        }

        /// <summary>The key a base measure answers to when a line needs it: <see cref="JevFamily.NeedsKey"/> of its create line.</summary>
        internal static string MeasureNeedsKey(ManifestBaseMeasure bm) =>
            JevFamily.NeedsKey("create_measure", StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name));

        internal static ManifestBaseMeasure BaseMeasureFor(StructureManifest m, string table, string column) =>
            m.BaseMeasures.FirstOrDefault(b => string.Equals(b.Table, table, StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(b.Column, column, StringComparison.OrdinalIgnoreCase));

        internal static ManifestBaseMeasure BaseMeasureByNeedsKey(StructureManifest m, string needsKey) =>
            needsKey == null ? null : m.BaseMeasures.FirstOrDefault(b => b.Pick != StarterMeasureTemplates.None
                                                                         && string.Equals(MeasureNeedsKey(b), needsKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>The base measure a measure ref names, or null.</summary>
        internal static ManifestBaseMeasure BaseMeasureByRef(StructureManifest m, string measureRef) =>
            m.BaseMeasures.FirstOrDefault(b => string.Equals(StarterMeasuresFamily.MeasureRef(b.Table, b.Name), measureRef, StringComparison.OrdinalIgnoreCase));

        /// <summary>FOLD the family's picks into the manifest's base measures (S4). A person's pick is never overwritten; "none" removes the
        /// measure; a rule's standing-in pick is recorded as the rule's (it shows in Needs you); a column the family did not hear back on keeps
        /// whatever it had. Returns how many base measures were made, removed or re-picked.</summary>
        internal static int FoldPicks(StructureManifest manifest, IEnumerable<LocalEngine.SummarizePick> picks, IDictionary<string, int> flips = null)
        {
            var moved = 0;
            foreach (var pick in picks ?? Enumerable.Empty<LocalEngine.SummarizePick>())
            {
                var existing = BaseMeasureFor(manifest, pick.TableName, pick.ColumnName);
                if (existing != null && IsHandMade(existing.DecidedBy)) continue;
                // THE OSCILLATION GUARD FOR A PICK (the lines have their own, in Merge). A pick that has changed twice inside one decide is frozen where it is
                // and handed to the person as the rule's pick Jev was unsure of: it is never shown as Jev's sure choice, and no later round moves it.
                var flipKey = existing == null ? null : PickFlipKey(existing);
                if (flipKey != null && flips != null && flips.TryGetValue(flipKey, out var changes) && changes >= 2) continue;
                var decidedBy = pick.Sure ? StartDecidedBy.Jev : StartDecidedBy.Rule;
                if (pick.Pick == StarterMeasureTemplates.None)
                {
                    if (existing != null) { RemoveBase(manifest, existing); moved++; }
                    continue;
                }
                if (existing == null)
                {
                    if (string.IsNullOrEmpty(pick.MeasureName)) continue;   // no name could be given: nothing to make
                    AddBase(manifest, new ManifestBaseMeasure
                    {
                        Table = pick.TableName, Column = pick.ColumnName, Pick = pick.Pick, Name = pick.MeasureName,
                        DecidedBy = decidedBy, Identity = BaseMeasureIdentity(manifest, pick.TableName, pick.ColumnName),
                    });
                    moved++;
                    continue;
                }
                if (!string.Equals(existing.Pick, pick.Pick, StringComparison.Ordinal))
                {
                    if (flips != null)
                    {
                        var n = (flips.TryGetValue(flipKey, out var seen) ? seen : 0) + 1;
                        flips[flipKey] = n;
                        if (n >= 2) { existing.DecidedBy = StartDecidedBy.Rule; continue; }   // frozen: Needs you asks which one
                    }
                    // A new pick is a new measure (another name, another formula), so the one it replaces goes and its followers with it.
                    existing.Pick = pick.Pick;
                    if (!string.IsNullOrEmpty(pick.MeasureName)) existing.Name = pick.MeasureName;
                    existing.DecidedBy = decidedBy;
                    moved++;
                }
                else if (existing.DecidedBy != decidedBy)
                {
                    existing.DecidedBy = decidedBy;   // the same measure, now settled by a different hand: not a change to the model
                }
            }
            return moved;
        }

        private static string PickFlipKey(ManifestBaseMeasure b) => "pick:" + (string.IsNullOrEmpty(b.Identity) ? b.Table + "/" + b.Column : b.Identity);

        // ------------------------------------------------------------------ merge

        /// <summary>Everything one round hands to the merge.</summary>
        internal sealed class RoundInput
        {
            public int Round;
            /// <summary>The families' lines, with the base measure families' create_measure lines already taken out (folded).</summary>
            public List<ChangeItem> JevLines = new List<ChangeItem>();
            public List<ChangeItem> RuleLines = new List<ChangeItem>();
            /// <summary>The objects a live family decided this round, keyed like JevRun.Decided: the rules stand aside for these.</summary>
            public ISet<string> Decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Whether an object ref is in the scratch the round ran over. Null means every ref is.</summary>
            public Func<string, bool> Resolves;
            /// <summary>The canonical refs of the assistant's word lines the scratch has (StartPlanWords.PresentRefs: the ref as stored, or as the plan's renames leave it).
            /// Null means none are known, and the table and column flags alone decide.</summary>
            public ISet<string> Present;
        }

        /// <summary>The rule line a decided object takes away: a rule that writes what a live family decided stands aside for it.</summary>
        internal static bool RuleStandsAside(ChangeItem rule, ISet<string> decided)
        {
            if (rule?.RuleId == null) return false;
            if (!LocalEngine.PointForRule.TryGetValue(rule.RuleId, out var point)) return false;
            return decided.Contains(LocalEngine.JevRun.UnansweredKey(point, rule.ObjectRef));
        }

        /// <summary>ARBITRATE one round into the document's lines. Mutates <paramref name="doc"/>.Lines and returns what moved.
        /// <paramref name="flips"/> remembers, across the rounds of ONE decide, how often each line's value changed; a line that changes
        /// twice is frozen (dropped, with the reason, and listed for Needs you) so the loop always ends.</summary>
        internal static StartRoundChange Merge(StartPlanDoc doc, RoundInput input, IDictionary<string, int> flips)
        {
            var manifest = doc.Manifest ?? new StructureManifest();
            var change = new StartRoundChange();
            var prior = new Dictionary<string, StartLine>(StringComparer.Ordinal);
            foreach (var l in doc.Lines) prior[CanonLineKey(manifest, l)] = l;

            // 1. who proposes what this round: Jev's lines, then the rules' that stand aside for nothing, one line per change.
            var rules = input.RuleLines.Where(r => !RuleStandsAside(r, input.Decided)).ToList();
            var all = input.JevLines.Concat(rules).ToArray();
            var chosen = LocalEngine.OneLinePerChange(all, jevLive: true);

            var next = new List<StartLine>();
            var carried = new HashSet<string>(StringComparer.Ordinal);
            var madeNow = new HashSet<StartLine>(ReferenceEqualityComparer.Instance);

            foreach (var item in chosen)
            {
                var key = StartRefs.ShapeKey(manifest, item);
                if (!carried.Add(key)) continue;   // one line per change, even if two rules asked
                var decidedBy = DecidedByOf(item);
                if (prior.TryGetValue(key, out var was))
                {
                    if (IsHandMade(was.DecidedBy))
                    {
                        // A person's or their assistant's word is never overwritten. A family that proposes the same line does NOT mean the object is back (the column-summarize
                        // family still proposes lines for the measure it picked, which the person's own pick replaced), so a hold is released only by the object itself.
                        StartPlanHold.Settle(doc, was, input.Resolves, input.Present);
                        next.Add(was); continue;
                    }
                    if (IsLeftOut(was) && string.Equals(was.Item.After, item.After, StringComparison.Ordinal)) { next.Add(was); continue; }   // refused to apply: it stays out
                    if (flips.TryGetValue(key, out var n) && n >= 2 && !was.Kept) { next.Add(was); continue; }   // frozen: stays out of the loop
                    var line = ToStartLine(item, decidedBy, input.Round);
                    var sameValue = string.Equals(was.Item.After, item.After, StringComparison.Ordinal) && was.Kept == line.Kept;
                    if (sameValue) { next.Add(was); continue; }
                    flips[key] = (flips.TryGetValue(key, out var seen) ? seen : 0) + 1;
                    if (flips[key] >= 2)
                    {
                        // THE OSCILLATION GUARD. Twice changed within one decide: the line is dropped, says why, and goes to Needs you.
                        line.Kept = false;
                        line.Item.Status = "rejected";
                        line.Why = StartDecideCopy.OscillatesPrefix + ": it has been " + (was.Item.After ?? "(none)") + " and "
                                   + (item.After ?? "(none)") + ". Pick the one you want.";
                        change.Frozen.Add(key);
                    }
                    next.Add(line);
                    change.Changed++;
                    continue;
                }
                var made = ToStartLine(item, decidedBy, input.Round);
                // A pin outlives its line: when the line it was made on is proposed again, it comes back as the person's value.
                if (doc.Pins.TryGetValue(key, out var pinned))
                {
                    made.Item.After = pinned; made.DecidedBy = StartDecidedBy.Person; made.Kept = true; made.Item.Status = "approved";
                    made.Why = "Pinned: the value you chose is kept.";
                }
                madeNow.Add(made);
                next.Add(made);
                change.Added++;
            }

            // 2. what already stood and nothing proposed again this round. It stays while its object is still there (the scratch had it
            //    applied, so the family proposes nothing for it), unless the family heard back on it and chose differently by saying nothing
            //    new (a held line is replaced by a heard one above), and it goes when its object is gone.
            foreach (var was in doc.Lines)
            {
                var key = CanonLineKey(manifest, was);
                if (carried.Contains(key)) continue;
                // Whether the line stands, is HELD or goes is ONE decision, StartPlanHold.Settle (the wiring step, W6): a hand-made line whose object is out by the
                // person's choice, or that was held by an edit, is kept and shown (never deleted), and a hold is released the moment its object is back.
                var fate = StartPlanHold.Settle(doc, was, input.Resolves, input.Present);
                if (fate == StartPlanHold.Fate.Goes) { change.Removed++; continue; }
                next.Add(was);
            }

            // 3. a line that needs a line or a base measure that is not there goes with it (S12, Needs closure).
            var kept = new List<StartLine>(next);
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var l in kept.ToList())
                {
                    var needs = NeedsOf(l);
                    if (needs == null) continue;
                    var provided = BaseMeasureByNeedsKey(manifest, needs) != null
                                   || kept.Any(o => !ReferenceEquals(o, l) && string.Equals(JevFamily.NeedsKey(o.Item.Kind, o.Item.ObjectRef), needs, StringComparison.OrdinalIgnoreCase));
                    if (provided) continue;
                    // A need on a line the loop never made (a person's own line) is not ours to remove; only the loop's lines go.
                    if (IsHandMade(l.DecidedBy)) continue;
                    kept.Remove(l);
                    // A line the family proposed this very round for a measure nobody makes (a person dropped it) is not a change: it never stood.
                    if (madeNow.Contains(l)) change.Added--; else change.Removed++;
                    moved = true;
                }
            }

            // Lines go to the executor in this order, and a line a LATER round found may depend on one an earlier round made (a hidden column is
            // what makes "leave it out of Excel" apply), so the order is by round. LINQ's sort is stable: lines of one round keep their order.
            doc.Lines = kept.OrderBy(l => l.Round).ToList();
            return change;
        }

        // ------------------------------------------------------------------ the picture of a document

        /// <summary>A stable text of everything the loop decides, so two states can be compared whatever order the lines were made in.</summary>
        internal static string Snapshot(StartPlanDoc doc)
        {
            var m = doc.Manifest ?? new StructureManifest();
            var lines = doc.Lines.Select(l => CanonLineKey(m, l) + "\u001e" + (l.Item.After ?? "") + "\u001e" + (l.Kept ? "1" : "0") + "\u001e" + l.DecidedBy
                                              + (l.Item.KeptByPerson ? "\u001e" + l.Item.KeptIdentity + "\u001e" + l.Item.KeptDigest : ""))   // a keep re-stamped for a renamed object is a new state: one more round tries the lines again with it
                .OrderBy(x => x, StringComparer.Ordinal);
            var bases = m.BaseMeasures.Select(b => b.Identity + "\u001e" + b.Table + "\u001e" + b.Column + "\u001e" + b.Pick + "\u001e" + b.Name + "\u001e" + b.DecidedBy)
                .OrderBy(x => x, StringComparer.Ordinal);
            return string.Join("\n", lines.Concat(new[] { "--" }).Concat(bases));
        }

        // ------------------------------------------------------------------ Needs you

        /// <summary>THE PERSON'S KEEPS THAT NO LONGER COUNT. A format the person kept is held by the build when the object it was for computes something else now (a pick of theirs or an edit changed
        /// the formula); the line stays on the plan, kept and theirs, with the reason. The band asks about each one, whatever Jev's state, so the person never approves a plan
        /// believing an answer of theirs is in it when it is not (plan-statement-check-3, defect 2: five keeps were lost with the band empty). An answer is a new keep or a drop, and a keep
        /// is stamped for the object as it is then.</summary>
        internal static IEnumerable<StartNeed> VoidedKeeps(StartPlanDoc doc)
        {
            foreach (var l in doc.Lines.Where(l => l.Item != null && l.Kept && l.DecidedBy == StartDecidedBy.Person && StartPlanHold.IsCannotLand(l) && LocalEngine.IsJevFormatLine(l.Item)))
                yield return new StartNeed
                {
                    Id = StartNeedKind.VoidedKeep + ":" + LineKey(l), Kind = StartNeedKind.VoidedKeep,
                    Text = (l.Item.Title ?? l.Item.Kind) + " on " + (l.Item.ObjectName ?? l.Item.ObjectRef) + ": your keep no longer counts, because " + (l.Item.ObjectName ?? "it")
                           + " works out something else now, so it is not built until you say.",
                    Options = new[] { "keep", "drop" }, Recommended = "keep",
                };
        }

        /// <summary>Why a Jev line sits out of the plan, from what the line itself says. "Not sure" is said only when Jev was not sure: a line Jev was sure of (a format it suggested
        /// with full confidence that Semanticus has not proven fits) never reads "not sure" beside its own "sure" (UAT-J7-S2-011).</summary>
        internal static string LeftOutWhy(ChangeItem item)
        {
            var word = item?.DecisionWord;
            if (string.IsNullOrEmpty(word) || string.Equals(word, JevFamily.Unsure, StringComparison.Ordinal)) return "Jev was not sure";
            var how = string.Equals(word, JevFamily.Sure, StringComparison.Ordinal) ? "Jev was sure" : "Jev was fairly sure";
            var pct = item.DecisionConfidence is double c && c > 0 ? " (" + Math.Round(c * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%)" : "";
            return how + pct + ", but Semanticus has not proven this change fits";
        }

        /// <summary>The Needs-you lines the loop and the manifest's picks produce: a pick the rule made because Jev was unsure, a Jev line that
        /// arrived dropped because it was unsure, and a line frozen because Jev kept changing it. Derived from the document, never stored.
        /// (The grant, composite keys and the fiscal year start are other lanes' lines.)</summary>
        internal static List<StartNeed> NeedsYou(StartPlanDoc doc)
        {
            var needs = new List<StartNeed>();
            var m = doc.Manifest ?? new StructureManifest();
            needs.AddRange(VoidedKeeps(doc));
            // Nothing is "unsure" until Jev has looked: a Free plan, one waiting for permission and one still deciding have rule picks that nobody was
            // unsure about (they are the source's defaults), so the band stays quiet for them.
            if (doc.Decide?.State != StartDecideState.Stable && doc.Decide?.State != StartDecideState.Incomplete) return needs;
            foreach (var b in m.BuiltBases().Where(b => b.DecidedBy == StartDecidedBy.Rule))
            {
                var id = StartNeedKind.UnsurePick + ":" + b.Identity;
                if (doc.Answers.ContainsKey(id)) continue;
                needs.Add(new StartNeed
                {
                    Id = id, Kind = StartNeedKind.UnsurePick,
                    Text = "Jev was not sure how to add up " + b.Column + " in " + b.Table + ", so the plan uses " + b.Pick + ". Is that right?",
                    Options = new[] { b.Pick, "none" }.Concat(new[] { "sum", "average", "count", "distinctcount", "min", "max" }).Distinct().ToArray(),
                    Recommended = b.Pick,
                });
            }
            // ONE QUESTION PER OBJECT. A rename the manifest cannot make (a time measure: the assistant's business name for "Total Hrs Value MTD") stays a line for
            // the executor, so a line Jev was unsure about on the old name and its twin on the new name can both be on the plan, and both would be asked: two
            // questions about one measure, one of them stale. Renames are folded here, before the questions are made: lines about the same object (by the name it
            // ends with), kind and property are one question, and the line already on the new name is the one that stands for it.
            var fold = RenameFold(doc);
            var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in doc.Lines.Where(l => !l.Kept && l.DecidedBy == StartDecidedBy.Jev && !IsLeftOut(l))
                                       .OrderBy(l => string.Equals(fold(l.Item.ObjectRef), l.Item.ObjectRef, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                if (!asked.Add((l.Item.Kind ?? "") + "|" + fold(l.Item.ObjectRef) + "|" + (l.Item.Target ?? ""))) continue;
                var oscillates = l.Why != null && l.Why.StartsWith(StartDecideCopy.OscillatesPrefix, StringComparison.Ordinal);
                var key = LineKey(l);
                var id = (oscillates ? StartNeedKind.Oscillation : StartNeedKind.UnsureLine) + ":" + key;
                if (doc.Answers.ContainsKey(id)) continue;
                needs.Add(new StartNeed
                {
                    Id = id, Kind = oscillates ? StartNeedKind.Oscillation : StartNeedKind.UnsureLine,
                    Text = oscillates ? l.Why : (l.Item.Title ?? l.Item.Kind) + " on " + (l.Item.ObjectName ?? l.Item.ObjectRef) + ": " + LeftOutWhy(l.Item) + ", so it is left out until you say.",
                    Options = new[] { "keep", "drop" },
                    Recommended = oscillates ? "drop" : "drop",
                });
            }
            return needs;
        }
    }
}
