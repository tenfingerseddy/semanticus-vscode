using System.Collections.Generic;
using System.Linq;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Forces the TOMWrapper DAX dependency tree (<c>DependsOn</c> / <c>ReferencedBy</c>) current NOW.
    ///
    /// Inside an open <c>BeginUpdate</c> batch the wrapper POSTPONES every dependency-tree rebuild to
    /// <c>EndUpdate</c> (<c>EoB_PostponeOperations</c>), so an expression set earlier in a batch is not yet
    /// reflected in <c>ReferencedBy</c> while the batch is still open. The single-writer engine occasionally
    /// needs the graph current mid-batch — e.g. <c>apply_plan</c> re-verifying a <c>delete_if_unused</c> "unused"
    /// verdict AFTER an earlier same-batch <c>set_dax</c> added a referencer to the target: without a flush the
    /// column reads as still-unused and would be wrongly deleted. This mirrors the handler's own
    /// <c>EndUpdateAll</c> flush (clear postpone → rebuild → restore) and leaves the batch otherwise untouched.
    ///
    /// Lives in Semanticus.Core because <see cref="FormulaFixup"/> and the handler's postpone flags are internal
    /// to this assembly (the vendored TOMWrapper is compiled in-place here — the submodule is never modified).
    /// </summary>
    public static class DependencyMaintenance
    {
        /// <summary>Everything a rename needs to be true before it runs: the tree is current, and the KPIs are in it. A rename's fixup rewrites only the formulas the tree lists.
        /// Inside a write batch the tree lags the formulas written in that batch (a measure made earlier in the same batch is not in it yet), and the wrapper's full rebuild
        /// never lists a KPI at all (it walks the tables' children, and a KPI is a measure's child), so a KPI's target, status and trend formulas stayed on the old names after
        /// any rename, on any model. Plan statement check 4: a Free start build renames in the batch that makes its measures.
        ///
        /// ALWAYS REBUILD. A rename changes which names resolve, not only which formulas exist: a reference to a name that did not exist yet starts to resolve when another object
        /// takes that name, and no comparison of objects and formula texts sees that, so a second rename in the same batch missed it. TE2 rebuilds before a rename too (Column.cs,
        /// Measure.cs and Table.cs, outside a batch). The cost is about 40 ms per rename on 500 formulas (measured 4 Oct). Revisit only for a flow that makes more than 100 renames in one batch on more than 1,000 formulas.</summary>
        public static void PrepareForRename()
        {
            var h = TabularModelHandler.Singleton;
            if (h == null) return;
            RebuildNow();
            foreach (var kpi in h.Model.Tables.SelectMany(t => t.Measures).Select(x => x.KPI).Where(k => k != null).ToList())
                FormulaFixup.BuildDependencyTree(kpi, true);
        }

        /// <summary>Rebuilds every formula's links now, whatever the batch is postponing.</summary>
        public static void RebuildNow()
        {
            var h = TabularModelHandler.Singleton;
            if (h == null) return;
            var wasPostponing = h.EoB_PostponeOperations;
            h.EoB_PostponeOperations = false;              // let the rebuild run instead of postponing it again
            FormulaFixup.BuildDependencyTree();
            h.EoB_RequireRebuildDependencyTree = false;    // we just satisfied the pending request
            h.EoB_PostponeOperations = wasPostponing;      // restore the batch's postpone state for the remaining items
        }

        /// <summary>Rebuilds the wrapper's objects from the raw model (<c>Model.Reinit</c>), and sees to it that the formula links are rebuilt. Every path that reloads objects goes through
        /// here (apply_tmdl, its undo and redo, the calendar changes): a reload can make objects the links have never seen, and formulas that were written to the raw model, so a rename
        /// after it would leave them on the old names. Inside a write batch the rebuild is asked for at the end of the batch and before the next rename; outside one it is made now.</summary>
        public static void Reload(Model model)
        {
            model.Reinit();
            MarkLinksStale(model.Handler);
        }

        /// <summary>The links are not to be trusted until they are rebuilt. Never throws: a rebuild that fails here is made again before the next rename, which always rebuilds.</summary>
        public static void MarkLinksStale(TabularModelHandler h)
        {
            if (h == null) return;
            h.EoB_RequireRebuildDependencyTree = true;     // and the end of the batch rebuilds, as for any formula written in it
            if (h.EoB_PostponeOperations) return;
            var pinned = TabularModelHandler.Singleton;
            try { TabularModelHandler.Singleton = h; RebuildNow(); }
            catch (System.Exception) { /* the next rename rebuilds */ }
            finally { TabularModelHandler.Singleton = pinned; }
        }
    }
}
