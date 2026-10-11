using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Takes a deleted column, or a deleted table, back WHOLE when its columns grouped other columns together (a field
    /// parameter does: its display column groups by its Fields column). Without this, undoing the delete threw "An object
    /// that has been removed from the Model tree cannot be modified or reattached" and left the model unreadable.
    ///
    /// Why the vendored wrapper cannot do it. Deleting a column clears its group-by list, and the clear is recorded as a
    /// "clear collection" undo step. That step undoes by re-creating every column in the list from saved JSON, which
    /// is right for a list that OWNS its items and wrong for this one, which only points at columns that live in the
    /// table: it re-creates a second copy of a column that is still there, and the list itself still holds the TOM
    /// object of the column as it was before the table came back, so the step reaches into a removed object.
    ///
    /// What this does instead. As a delete starts, it writes down which of the doomed columns group which, takes the
    /// groups off those columns itself, so the wrapper's own clear finds nothing to record, and adds ONE undo step of its
    /// own (before the delete's steps, so it is taken back after the table or column is back) that puts the groups on
    /// the columns that are in the model by then, found by name. Redo takes them off again. Each column's cached
    /// group list is dropped when the column comes back, so it never points at the object it replaced.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public static class GroupByUndo
    {
        private sealed class Grouping
        {
            public string Table, Column;
            public string[] GroupedBy;   // the group-by list this column had before the delete
            public string[] After;       // and the list it has once the delete is done (a column that survives keeps what it still groups by)
        }

        /// <summary>Called by the model session for every delete the wrapper is about to make, AFTER every other listener has
        /// had its say and none called the delete off (a rehearsal made to ask the person calls every delete off, and must
        /// leave the model as it was). Records the groups of every column the delete takes, whichever way round the pair
        /// is: a column that groups others (its list goes with it) and a column that others group by (it is taken off
        /// their lists, which would otherwise point at a removed object and stop the model reading).</summary>
        public static void OnDeleting(TabularModelHandler handler, ObjectDeletingEventArgs e)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (e == null || e.Cancel) return;
            // An undo or redo deletes objects too (it takes an add back). Its own step already carries the groups.
            if (handler.UndoManager.UndoInProgress) return;
            TabularModelHandler.Singleton = handler;   // the wrapper's own clear below makes its collection from this
            IEnumerable<Column> doomed = e.TabularObject switch
            {
                Column c => new[] { c },
                Table t => t.Columns.ToList(),
                _ => Enumerable.Empty<Column>(),
            };
            var doomedList = doomed.ToList();
            if (doomedList.Count == 0) return;
            var found = new List<Grouping>();
            foreach (var column in doomedList)
            {
                var details = column.MetadataObject?.RelatedColumnDetails;
                if (details == null || details.GroupByColumns.Count == 0) continue;
                found.Add(new Grouping
                {
                    Table = column.Table?.Name, Column = column.Name,
                    GroupedBy = details.GroupByColumns.Select(g => g.GroupingColumn?.Name).Where(n => n != null).ToArray(),
                    After = Array.Empty<string>(),
                });
            }
            // The other way round: a column that survives this delete and groups by one that does not.
            var doomedValues = new HashSet<TOM.Column>(doomedList.Select(c => c.MetadataObject).Where(v => v != null), ReferenceEqualityComparer.Instance);
            var stripped = new List<(TOM.Column Owner, TOM.GroupByColumn[] Doomed)>();
            foreach (var table in handler.Model.Tables)
                foreach (var owner in table.Columns)
                {
                    var value = owner.MetadataObject;
                    if (value?.RelatedColumnDetails == null || doomedValues.Contains(value)) continue;
                    var hit = value.RelatedColumnDetails.GroupByColumns.Where(g => g.GroupingColumn != null && doomedValues.Contains(g.GroupingColumn)).ToArray();
                    if (hit.Length == 0) continue;
                    var all = value.RelatedColumnDetails.GroupByColumns.Select(g => g.GroupingColumn?.Name).Where(n => n != null).ToArray();
                    found.Add(new Grouping
                    {
                        Table = table.Name, Column = owner.Name, GroupedBy = all,
                        After = value.RelatedColumnDetails.GroupByColumns.Where(g => !hit.Contains(g)).Select(g => g.GroupingColumn?.Name).Where(n => n != null).ToArray(),
                    });
                    stripped.Add((value, hit));
                }
            if (found.Count == 0) return;
            foreach (var column in doomedList)
                if (column.MetadataObject?.RelatedColumnDetails != null) column.MetadataObject.RelatedColumnDetails = null;
            foreach (var (owner, hit) in stripped)
            {
                foreach (var g in hit) owner.RelatedColumnDetails.GroupByColumns.Remove(g);
                if (owner.RelatedColumnDetails.GroupByColumns.Count == 0) owner.RelatedColumnDetails = null;
            }
            handler.UndoManager.Add(new SidecarUndoAction("restore group-by columns", null,
                undo: () => Put(handler, found, before: true),
                redo: () => Put(handler, found, before: false)));
        }

        private static TOM.Column Find(TabularModelHandler handler, string table, string column)
        {
            var t = handler.Model.Tables.FindByName(table);
            return t?.Columns.FindByName(column)?.MetadataObject;
        }

        // Sets each column's group-by list to what it was before the delete (undo) or what it is after it (redo), found by
        // name among the columns that are in the model at that step.
        private static void Put(TabularModelHandler handler, List<Grouping> groups, bool before)
        {
            foreach (var g in groups)
            {
                var value = Find(handler, g.Table, g.Column);
                if (value == null) continue;
                var names = before ? g.GroupedBy : g.After;
                if (names.Length == 0) { if (value.RelatedColumnDetails != null) value.RelatedColumnDetails = null; continue; }
                var details = new TOM.RelatedColumnDetails();
                foreach (var name in names)
                    if (Find(handler, g.Table, name) is { } grouping) details.GroupByColumns.Add(new TOM.GroupByColumn { GroupingColumn = grouping });
                value.RelatedColumnDetails = details;
            }
        }
    }

}

namespace TabularEditor.TOMWrapper
{
    public partial class Column
    {
        // The cached group-by list holds the TOM column it was made for. A column that comes back from an undo has a new
        // one, so the list is made again here (see GroupByUndo). The wrapper's collections take the process-wide Singleton
        // handler when they are made, so it is pointed at this column's own handler first, as the other Core helpers do.
        internal override void ReapplyReferences()
        {
            if (_groupByColumns != null)
            {
                TabularModelHandler.Singleton = Handler;
                _groupByColumns = null;
                _ = GroupByColumns;
            }
            base.ReapplyReferences();
        }
    }
}
