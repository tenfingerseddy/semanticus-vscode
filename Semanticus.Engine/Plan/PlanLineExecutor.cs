// The Plan's synchronous line loop, in ONE place (lane m11/sf-exec, design-spec-first-start-v2 S5 and S12).
//
// It was the middle of LocalEngine.ApplyPlanCoreAsync: the Needs closure, the apply order, the late-format follow and the ref
// refresh, the waived-seed check, the write-time best-practice refusal, the apply-time value check, the format fill rule's proof,
// the removal re-check, and the approval's all-or-none rule. The start plan's single build step has to apply its kept lines
// through exactly these checks, so a second copy was never an option: the Plan and the start plan share this class.
//
// NOTHING IN HERE IS NEW BEHAVIOUR. The bodies moved as they were. The one addition is that the model facts the two data checks
// read (sort by one to one, a narrowing data type) can be handed in. Null means "what the engine reads today" (JevFacts()), which is
// every existing caller, so the Plan's results do not move.
//
// WHAT STAYS IN ApplyPlanCoreAsync: choosing the targets, the async verify gates (a DAX proof cannot run inside a write), the refusal
// that restores the lines' notes, the before and after grades, the report, the audit records and the activity event. Those are the
// Plan's own, not the loop's.

using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>What one batch of lines is allowed to do, beyond writing them.</summary>
    internal sealed class PlanLineOptions
    {
        /// <summary>Present only when a Model Plan approval drives the batch: one guard per approved item, each re-checked on the
        /// dispatcher against its own lines, and the batch is then ALL OF THE KEPT LINES OR NONE. Null for apply_plan, which applies
        /// what it can and reports the rest.</summary>
        public IReadOnlyList<PlanApprovalGuard> Guards;

        /// <summary>Run the plan: a delete_if_unused line whose apply-time re-check says no is skipped with its reason and the rest
        /// applies. False for approve, which promises all of the kept lines or none.</summary>
        public bool KeepGoingPastInUseRemovals;

        /// <summary>A row's own approve and Run the plan: an automatic format line the batch's own other edits made unprovable is left
        /// out with its note and the rest applies.</summary>
        public bool KeepGoingPastFormat;

        /// <summary>When set, the Needs closure runs again INSIDE the batch against these lines (every line of the plan, applied or not),
        /// so a line whose needed line is not being written is skipped with its reason in the same transaction (S12). The Plan leaves it
        /// null: it closes the set before its async gates, and the closure there is the one its approvals count.</summary>
        public IReadOnlyList<ChangeItem> CloseNeedsAgainst;
    }

    internal sealed class PlanLineExecutor
    {
        private readonly LocalEngine _engine;
        private readonly Session _s;
        private readonly PlanLineOptions _options;
        private readonly Semanticus.Engine.IJevModelFacts _facts;

        // The removal re-check is taken against the report basis as it stood when the call started, and again inside the batch.
        private readonly LocalEngine.RemovalRecheck _removals;
        // The removals a Run the plan batch skipped because the re-check said no; never a reason to roll back.
        private readonly HashSet<ChangeItem> _removalSkips = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);
        // The automatic format lines the rule refused at the end of the batch. Run the plan leaves them out with their note and goes on
        // with the rest: a line nobody ticked must never stop every row (addendum 3 item 16).
        private readonly HashSet<ChangeItem> _formatSkips = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);
        // The object each automatic format line is about, taken BEFORE any other edit of the batch. A rename earlier in the batch
        // moves the name, never the object, so the line follows the object it was worked out for and never lands on another
        // measure that took the name (addendum 3 item 12).
        private readonly Dictionary<ChangeItem, ITabularNamedObject> _formatObjects = new Dictionary<ChangeItem, ITabularNamedObject>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChangeItem, (string Full, string Untyped)> _formatDigestsAtStart = new Dictionary<ChangeItem, (string Full, string Untyped)>(ReferenceEqualityComparer.Instance);
        // The best-practice fixes the row's own check refused AT THE WRITE (see BpaFixRefusedAtWrite).
        private readonly HashSet<ChangeItem> _refusedSkips = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);

        /// <summary>This batch's own cascade channel (never another caller's): pass it to MutateAsync.</summary>
        public List<CascadeWarning> Cascade { get; } = new List<CascadeWarning>();

        /// <summary>The exact renamed object per rename line, keyed by reference identity: a cascade warning carries the object it
        /// concerns, so it attributes to the RIGHT line even when two lines rename same-named objects on different tables.</summary>
        public Dictionary<object, ChangeItem> RenamedByObject { get; } = new Dictionary<object, ChangeItem>(ReferenceEqualityComparer.Instance);

        public int Applied { get; private set; }
        public int Failed { get; private set; }

        /// <summary>Build the executor BEFORE the write: the removal re-check reads the report basis as it was when the call started.
        /// <paramref name="facts"/> null means the facts the engine reads today (<c>JevFacts()</c>).</summary>
        public PlanLineExecutor(LocalEngine engine, Session s, PlanLineOptions options = null, Semanticus.Engine.IJevModelFacts facts = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _s = s ?? throw new ArgumentNullException(nameof(s));
            _options = options ?? new PlanLineOptions();
            if (_options.Guards != null && _options.Guards.Count == 0) _options.Guards = null;
            _facts = facts;
            // B2: that reading is the basis as it was when this call started, and it is not the one the deletion is finally judged
            // against: the batch re-takes it (ApplyInBatch).
            _removals = new LocalEngine.RemovalRecheck(engine.CaptureScopeBasis(s));
        }

        /// <summary>The start plan's door: one executor, one batch, inside the caller's own write. Returns the executor for its counts.</summary>
        public static PlanLineExecutor ApplyInBatch(LocalEngine engine, Model m, Session s, List<ChangeItem> lines, PlanLineOptions options = null,
                                                    Semanticus.Engine.IJevModelFacts facts = null)
        {
            var executor = new PlanLineExecutor(engine, s, options, facts);
            executor.ApplyInBatch(m, lines);
            return executor;
        }

        // ---- the pieces that need no model ------------------------------------------------------------------------------------

        /// <summary>A Jev line that needs another line (ChangeItem.Needs: a base measure's hide needs the measure, its Excel line needs
        /// the hide) is applied only with it. Naming just the hide, or leaving the measure unapproved, skips the hide with its reason
        /// rather than hide a column no measure adds up; and a line this pass skips holds back what needs it in turn. Only a needed
        /// line already applied, or gone from the plan, holds nothing back. Under an approval the skip is a lost line, so the whole
        /// approval is refused, as it is for every other line a check drops. Lines leave <paramref name="toApply"/> in place.</summary>
        public static void CloseNeeds(IReadOnlyList<ChangeItem> all, List<ChangeItem> toApply)
        {
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var it in toApply.ToList())
                {
                    if (it.Needs == null || !JevFamily.IsJevLine(it)) continue;
                    var needed = all.FirstOrDefault(o => !ReferenceEquals(o, it) && JevFamily.IsJevLine(o)
                                                         && !string.Equals(o.Status, "applied", StringComparison.OrdinalIgnoreCase)
                                                         && string.Equals(o.JevItem, it.JevItem, StringComparison.Ordinal)
                                                         && string.Equals(JevFamily.NeedsKey(o.Kind, o.ObjectRef), it.Needs, StringComparison.OrdinalIgnoreCase));
                    if (needed == null || toApply.Contains(needed)) continue;
                    it.Status = "skipped";
                    it.Note = "not applied: it needs \"" + (needed.Title ?? needed.Kind) + "\" on " + (needed.ObjectName ?? needed.ObjectRef)
                              + ", which is not being applied with it";
                    toApply.Remove(it);
                    moved = true;
                }
            }
        }

        /// <summary>The order a batch writes in: edits, CHILD renames, TABLE renames, then removals, with the automatic formats last of
        /// all. A rename changes an object's Name, after which a cached ObjectRef can resolve stale; a removal can invalidate every
        /// later ref. Stable, so a line keeps its place among its equals.</summary>
        public static List<ChangeItem> Order(IEnumerable<ChangeItem> lines) => lines.OrderBy(LocalEngine.ApplyOrder).ToList();

        /// <summary>A keep or a drop of <paramref name="line"/> moves the lines that cannot stand without it (or that it cannot stand
        /// without): drop a base measure and its format, description, hide and Excel lines go too; keep the hide and its measure comes
        /// back. ONE path for both doors of the Plan and for the start plan. The line itself is not touched.</summary>
        public static void FollowKeep(ChangeItem line, IEnumerable<ChangeItem> waiting, bool keep)
        {
            foreach (var other in JevFamily.Follow(line, waiting, keep))
                other.Status = keep ? "approved" : "rejected";
        }

        // ---- the loop ---------------------------------------------------------------------------------------------------------

        /// <summary>Write the lines, in order, INSIDE the caller's write (the single-writer dispatcher). Every line ends with a status:
        /// applied, skipped (with its reason), or failed. Under an approval a line that does not apply throws
        /// <see cref="PlanApprovalRefusedException"/>, which unwinds everything this batch wrote and nothing else.</summary>
        public void ApplyInBatch(Model m, List<ChangeItem> lines)
        {
            var guards = _options.Guards;
            var s = _s;
            if (_options.CloseNeedsAgainst != null) CloseNeeds(_options.CloseNeedsAgainst, lines);
            // The order the Plan already puts its lines in: ordering an ordered list changes nothing.
            var toApply = Order(lines);

            // delete_if_unused: the unused verdict recomputed INSIDE the single-writer delegate — one sweep
            // serves the batch. Deletes run LAST (ApplyOrder) and a delete can only SHRINK referencer sets,
            // so a "safe" computed here cannot go stale mid-batch (the same one-sweep argument
            // RemoveSafeObjectsAsync documents); a not-safe that BECAME safe merely skips — conservative.
            // But an earlier item in the SAME batch can GROW referencer sets (a set_dax/rename that adds a DAX
            // reference to a delete target). The wrapper POSTPONES its dependency-tree rebuild to EndUpdate, so
            // ReferencedBy is stale mid-batch — the sweep is fed a flushed tree (DependencyMaintenance.RebuildNow)
            // so a target a same-batch edit just referenced reads as used and SKIPS, never a stale delete.
            //
            // B2: the report basis is re-taken HERE, once, and every removal in this batch is judged against
            // that one snapshot. A scope write rides this same single-writer dispatcher now, so a basis read
            // at the top of the turn cannot move again before the deletes run at the end of it.
            // THE APPROVAL CHECK THAT COVERS THE WRITE. This is the single-writer dispatcher, so
            // nothing else can commit between this line and the last property set below. A check made
            // anywhere earlier is a check on a model that could still move.
            // One check per approved item, each against exactly that item's lines, so a refusal knows
            // which item it is about. Every line in toApply belongs to exactly one guard: the target set
            // was narrowed to the union of the guards' lines, and apply_plan_items refuses a line two
            // items both claim before it gets here.
            if (guards != null)
                foreach (var g in guards)
                {
                    var moved = g.Refusal(s, toApply.Where(i => g.Lines.ContainsKey(i.Id ?? "")).ToList());
                    if (moved != null) throw new PlanApprovalRefusedException(moved) { Guard = g };
                }

            _removals.Begin(_engine.CaptureScopeBasis(s));
            var waiversNow = new LocalEngine.SeedWaivers(m);   // a finding accepted after the plan was made (Plan/LocalEngine.PlanWaivers.cs)
            if (toApply.Any(i => LocalEngine.IsSeeded(i, out _))) waiversNow.Prime();   // read before this batch's own writes change what the scans see
            foreach (var f in toApply.Where(LocalEngine.IsAutomaticFormatWrite))
            {
                try { _formatObjects[f] = ObjectRefs.Resolve(m, f.ObjectRef); } catch { }
                // What the object computed when this step began, with and without data types, so a person's tick this same
                // step sets aside only by a type change is told so in those words (v-rules-v1i, decision 4).
                if (_formatObjects.TryGetValue(f, out var began) && began != null)
                    _formatDigestsAtStart[f] = (Semanticus.Analysis.FormatFill.ComputeDigest(began), Semanticus.Analysis.FormatFill.ComputeDigestWithoutTypes(began));
            }
            foreach (var it in toApply)
            {
                try
                {
                    if (LocalEngine.LeaveOutWaivedSeed(waiversNow, it)) continue;
                    var isFormat = LocalEngine.IsAutomaticFormatWrite(it);
                    if (isFormat && _formatObjects.TryGetValue(it, out var followed) && followed != null)
                    {
                        // Follow the object through the batch's renames; an object the batch deleted or replaced is left out.
                        string fresh;
                        try { fresh = ObjectRefs.For(followed); } catch { fresh = null; }
                        ITabularNamedObject there = null;
                        try { there = fresh == null ? null : ObjectRefs.Resolve(m, fresh); } catch { }
                        if (!ReferenceEquals(there, followed))
                        {
                            it.Status = "skipped";
                            it.Note = "The measure or column this format was worked out for was removed or replaced by another change in this run, so it was not written.";
                            _formatSkips.Add(it);
                            continue;
                        }
                        it.ObjectRef = fresh;
                    }
                    // A Model Plan batch never writes a best-practice fix the row itself would refuse, whoever worked
                    // the line out (verify V9: the change plan's propose_plan seeds every fix the scan can make).
                    if (guards != null && LocalEngine.BpaFixRefusedAtWrite(m, it) is { } refusal)
                    {
                        it.Status = "skipped";
                        it.Note = refusal;
                        // A format the rule refuses at the write is the format rule's skip (a row's own approve goes on past it);
                        // any other fix the row refuses is the removal-recheck kind.
                        if (isFormat) _formatSkips.Add(it); else _refusedSkips.Add(it);
                        continue;
                    }
                    if (!LocalEngine.IsRemovalKind(it.Kind))
                    {
                        var live = LocalEngine.LiveValueForPlanItem(m, it);
                        if (live != null && !LocalEngine.PlanTextEquals(live, it.Before) && !LocalEngine.PlanTextEquals(live, it.After))
                        {
                            it.Status = "skipped";
                            it.Note = "This value changed after the plan was built. It was not applied. Review it again, then apply.";
                            if (isFormat) _formatSkips.Add(it);
                            continue;
                        }
                    }
                    // A bpa_fix item was seeded from a scan taken when the plan was built, and the guard above
                    // reads only the property the fix writes. So ASK THE RULE AGAIN at the moment of writing, the
                    // way apply_fix does: a measure that had no format then and was given a format string
                    // expression since still reads as blank to that guard, and the fix would land beside it.
                    if (string.Equals(it.Kind, "bpa_fix", StringComparison.OrdinalIgnoreCase)
                        && _engine.BpaFixNoLongerApplies(m, it) is string gone)
                    {
                        it.Status = "skipped";
                        it.Note = gone;
                        if (isFormat) _formatSkips.Add(it);
                        continue;
                    }
                    // The same hole on the plan's own format items (FMT-MEASURE's set_measure_format, and
                    // set_format_string): the guard above reads the static format only, so a measure given a format
                    // string expression since the plan was built still reads as blank, and the static format would
                    // be saved beside the dynamic one, which Analysis Services does not allow.
                    if ((string.Equals(it.Kind, "set_measure_format", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(it.Kind, "set_format_string", StringComparison.OrdinalIgnoreCase))
                        && ObjectRefs.Resolve(m, it.ObjectRef) is Measure dyn && !string.IsNullOrWhiteSpace(dyn.FormatStringExpression))
                    {
                        it.Status = "skipped";
                        it.Note = "This measure's format is now set by a format string expression, and a static format would sit beside it. It was not applied.";
                        if (isFormat) _formatSkips.Add(it);
                        continue;
                    }
                    // PROOF AT WRITE TIME (the format fill rule, addendum 2 item 8). A Jev format line was proved, or
                    // refused, when it was worked out; the model may have moved since, and a line the engine approved for
                    // itself carries no consent. Ask the rule again, against the model as it is now.
                    if (LocalEngine.JevFormatWriteRefusal(m, it, _formatDigestsAtStart.TryGetValue(it, out var atStart) ? atStart : null) is string refusedFormat)
                    {
                        it.Status = "skipped";
                        it.Note = refusedFormat;
                        if (isFormat) _formatSkips.Add(it);
                        continue;
                    }
                    // Capture the object BEFORE the rename (its ref still resolves by the old name); the same
                    // instance survives the rename, so the warning's object matches it exactly at drain time.
                    if (string.Equals(it.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                        && ObjectRefs.Resolve(m, it.ObjectRef) is TabularNamedObject renameTarget)
                        RenamedByObject[renameTarget] = it;
                    // The Storage tab's plan-routed removal: re-verify "unused" AT APPLY TIME — the promise
                    // remove_safe_objects keeps, kept on the plan door too. An object that gained a referencer
                    // since the caller's scan SKIPS with the honest per-item reason, never a stale delete.
                    if (string.Equals(it.Kind, "delete_if_unused", StringComparison.OrdinalIgnoreCase))
                    {
                        // The one re-check, shared (Plan/LocalEngine.RemovalRecheck.cs): the report basis, then
                        // the unused sweep on a flushed dependency tree, inside this same batch.
                        var notRemoved = _removals.NotRemovedNote(m, it);
                        if (notRemoved != null)
                        {
                            it.Status = "skipped";
                            it.Note = notRemoved;
                            _removalSkips.Add(it);
                            continue;
                        }
                        LocalEngine.DeleteResolvedObject(m, ObjectRefs.Resolve(m, it.ObjectRef), it.ObjectRef);
                        it.Status = "applied"; it.Note = "applied (unused verdict re-verified at apply time)"; Applied++;
                        continue;
                    }
                    _engine.ApplyOneItem(m, it, s, _facts);
                    // An approved set_dax with no verify matrix went in WITHOUT an equivalence proof
                    // (the human opted in) — flag it so the report/summary surfaces the unproven rewrite.
                    if (string.Equals(it.Kind, "set_dax", StringComparison.OrdinalIgnoreCase) && it.VerifyGroupBy == null)
                        it.VerifyState = "unverified";
                    it.Status = "applied"; it.Note = it.Note ?? "applied"; Applied++;
                }
                catch (PlanApprovalRefusedException) { throw; }
                catch (Exception ex) { it.Status = "failed"; it.Note = ex.Message; Failed++; }
            }

            // ALL OF THEM OR NONE, and the rollback is the batch's own. Throwing here unwinds everything
            // this delegate wrote and nothing else, which is the one thing a separate undo call could
            // never promise: an undo runs after the batch has closed, and by then the newest entry may
            // belong to somebody else, or this batch may have written nothing at all and left no entry
            // to undo.
            if (guards != null)
            {
                // Run the plan names what a person's acceptance left out at apply (accepted after the row was worked out) and goes on with
                // the rest, as it does for a removal that is in use now; approving one row still puts the whole set back.
                var keepGoingPastInUseRemovals = _options.KeepGoingPastInUseRemovals;
                var keepGoingPastFormat = _options.KeepGoingPastFormat;
                var short_ = toApply.FirstOrDefault(i => !string.Equals(i.Status, "applied", StringComparison.OrdinalIgnoreCase)
                                                         && !(keepGoingPastInUseRemovals && (_removalSkips.Contains(i) || _refusedSkips.Contains(i) || LocalEngine.IsLeftOut(i)))
                                                         && !((keepGoingPastInUseRemovals || keepGoingPastFormat) && _formatSkips.Contains(i)));
                if (short_ != null)
                    throw new PlanApprovalRefusedException(
                        "One of these changes could not be made, so the whole set was put back and nothing "
                        + "was changed. "
                        + (string.IsNullOrWhiteSpace(short_.ObjectName) ? short_.ObjectRef : short_.ObjectName)
                        + ": " + (short_.Note ?? "it could not be made."))
                    {
                        Guard = LocalEngine.GuardOf(guards, short_),
                        Line = short_.Clone(),   // the note that says why survives the lines going back
                    };
            }
        }
    }
}
