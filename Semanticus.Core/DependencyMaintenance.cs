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
    }
}
