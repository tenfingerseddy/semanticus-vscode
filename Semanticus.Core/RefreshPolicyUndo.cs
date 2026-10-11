using System;
using System.Runtime.CompilerServices;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Takes a switched-off incremental refresh policy back WHOLE (Sol UAT F-S05b-3, OP-remove_incremental_refresh_policy:
    /// undo gave "enabled" with no windows, an invalid granularity and no source expression).
    ///
    /// The vendored wrapper models "policy on or off" as one boolean, <see cref="Table.EnableRefreshPolicy"/>. Switching
    /// it off sets <c>RefreshPolicy = null</c> and records only the boolean's old value, so undo switches it on again and
    /// the setter makes a BLANK policy. The windows, mode, polling expression and source expression were never recorded.
    /// This hooks the handler's change events so that EVERY path that switches a policy off (the remove tool, the
    /// properties grid, a plan item, a workflow step) records the whole policy beside the boolean, and undo puts that
    /// policy back whole. It records nothing for any other change.
    ///
    /// The snapshot is taken as the change starts (the policy is still there) and the undo step is added once the change
    /// has applied, so a change that is cancelled leaves no step. On undo this step runs first and restores the policy;
    /// the wrapper's own step then finds the boolean already true and does nothing. Redo mirrors it.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public static class RefreshPolicyUndo
    {
        private static readonly ConditionalWeakTable<Table, TOM.RefreshPolicy> Pending = new ConditionalWeakTable<Table, TOM.RefreshPolicy>();

        /// <summary>Record the whole policy whenever this handler's tables switch one off.</summary>
        public static void Track(TabularModelHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            handler.ObjectChanging += OnChanging;
            handler.ObjectChanged += OnChanged;
        }

        private static void OnChanging(object sender, ObjectChangingEventArgs e)
        {
            if (!(e.TabularObject is Table t) || e.PropertyName != nameof(Table.EnableRefreshPolicy) || !(e.NewValue is bool on) || on) return;
            Pending.Remove(t);
            if (t.MetadataObject?.RefreshPolicy is TOM.RefreshPolicy policy) Pending.Add(t, (TOM.RefreshPolicy)policy.Clone());
        }

        private static void OnChanged(object sender, ObjectChangedEventArgs e)
        {
            if (!(e.TabularObject is Table t) || e.PropertyName != nameof(Table.EnableRefreshPolicy)) return;
            if (!Pending.TryGetValue(t, out var saved)) return;
            Pending.Remove(t);
            if (t.MetadataObject?.RefreshPolicy != null || !(sender is TabularModelHandler h)) return;   // the change did not switch it off
            h.UndoManager.Add(new SidecarUndoAction("restore refresh policy", null,
                undo: () => t.MetadataObject.RefreshPolicy = (TOM.RefreshPolicy)saved.Clone(),
                redo: () => t.MetadataObject.RefreshPolicy = null));
        }
    }
}
