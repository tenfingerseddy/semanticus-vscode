// The apply-time re-check of a delete_if_unused line, in ONE place (lane E5, Sol 24 Sep).
//
// A removal is promised only while its object is still unused at the moment it is removed. ApplyOneItem refuses
// delete_if_unused on purpose (LocalEngine.cs, "must be applied through apply_plan"), because only the apply_plan
// batch knows the report basis the removal was reviewed against and can sweep the model inside the write. This
// class is that check, extracted from the apply_plan loop so there is no second copy: apply_plan, approve and Run
// the plan all reach it through ApplyPlanCoreAsync, and any later applier that removes a plan line calls the same
// NotRemovedNote inside its own batch.
//
// WHAT IT CHECKS, IN ORDER, AND EVERY ANSWER IS A SKIP WITH ITS REASON, NEVER A DELETE:
//   B2  the report selection moved between the start of the call and this batch turn
//   R2  the selection moved between the review and this apply
//   R1  reports were chosen and one of them has never been read, so the protection cannot be established
//   and then the unused sweep itself, ONCE per batch, on a flushed dependency tree, so a same-batch edit that
//   just started using the object (a set_dax naming it) reads as used and the removal is skipped.
//
// ONE SWEEP SERVES THE BATCH, and that is safe because removals run LAST (ApplyOrder) and a removal can only
// shrink the referencer sets, so "safe" cannot go stale between two removals of the same batch.

using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal sealed class RemovalRecheck
        {
            private readonly ScopeBasis _entry;
            private readonly List<(string path, string error, Lineage.ReportDefinitionReader.ParseResult result)> _parts;
            private ScopeBasis _apply;
            private Dictionary<string, UnusedItem> _unusedByRef;

            /// <summary><paramref name="entry"/> is the report basis as it was when the call started, read off the
            /// dispatcher (parsing the report parts is file IO).</summary>
            public RemovalRecheck(ScopeBasis entry)
            {
                _entry = entry;
                _parts = entry?.Coverage?.Parts?.Count > 0 ? entry.Coverage.Parts : null;
            }

            /// <summary>Take the basis again, ON THE DISPATCHER, inside the batch. A scope write rides this same
            /// single-writer dispatcher, so the basis cannot move again before the removals at the end of the turn.</summary>
            public void Begin(ScopeBasis apply) => _apply = apply;

            /// <summary>Null when the line's object may be removed now; otherwise the note that says why it was
            /// not, in the words apply_plan has always used.</summary>
            public string NotRemovedNote(Model m, ChangeItem it)
            {
                var apply = _apply ?? throw new InvalidOperationException("The removal check was not started inside the batch.");
                // B2. The selection moved between the start of this call and this turn, so the parts the candidate
                // set was built from describe a basis that is gone.
                if (apply.MovedSince(_entry))
                    return "not removed: " + ReportScopeCoverage.SelectionChanged;
                // R2. The report selection moved between the review and this apply.
                if (it.ReviewedScope != null && !string.Equals(it.ReviewedScope, apply.Signature, StringComparison.Ordinal))
                    return "not removed: " + ReportScopeCoverage.SelectionChanged;
                // R1. Reports were chosen and one of them has never been read.
                if (apply.Coverage.HasChoices && !apply.Coverage.Complete)
                    return "not removed: " + ReportScopeCoverage.NothingRemoved;
                if (_unusedByRef == null)
                {
                    // Flush postponed dependencies so same-batch referencers are visible.
                    TabularEditor.TOMWrapper.Utils.DependencyMaintenance.RebuildNow();
                    _unusedByRef = (_parts == null
                            ? (_entry?.Coverage?.NoReports == true ? Lineage.LineageGraph.UnusedNoReports(m) : Lineage.LineageGraph.Unused(m))
                            : Lineage.LineageGraph.AnalyzeReports(m, _parts).Unused)
                        .Items.ToDictionary(x => x.Ref, StringComparer.OrdinalIgnoreCase);
                }
                // Never a cleanup candidate at all (a calculation group's own column): the verdict did not go
                // stale, there was never one, so the "no longer holds" sentence below would be false here.
                if (ObjectRefs.Resolve(m, it.ObjectRef) is Column cgc && cgc.Table is CalculationGroupTable)
                    return "not removed: " + NotSafeReason(m, it.ObjectRef, _unusedByRef, _parts != null);
                // "visibleAttribute" is allowed here and "safe" is not the only pass: a plan line is something a
                // person named and approved, and the verdict only says Semanticus never sweeps such a column away by
                // itself. Nothing in the model references it, so the removal is as safe as any other on this list
                // (dogfood round 2, D3). Refusing it reported "the unused verdict no longer holds", which had never
                // held and had not moved.
                if (!_unusedByRef.TryGetValue(it.ObjectRef, out var now)
                    || (now.Verdict != "safe" && now.Verdict != "visibleAttribute"))
                    return "not removed: it no longer looks safe to remove. "
                         + NotSafeReason(m, it.ObjectRef, _unusedByRef, _parts != null);
                return null;
            }
        }
    }
}
