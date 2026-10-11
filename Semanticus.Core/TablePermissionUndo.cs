using System.Linq;

namespace TabularEditor.TOMWrapper
{
    /// <summary>
    /// Puts a table permission's references back when an undo or redo brings the permission (or the role that owns it)
    /// back from its saved JSON (Sol UAT-F-S07a-4-003/-004: undoing an emptied row filter left a permission with no
    /// table, and every read of the role then threw).
    ///
    /// The vendored undo re-creates a removed object by deserializing the JSON it saved at removal. A permission's JSON
    /// names its table and its columns, but the deserialized TOM object does not point at them, so <see cref="Table"/>
    /// is null until something resolves the name; the wrapper's own load path does that (<c>DelayedInit</c>), its undo
    /// path does not. <c>Undelete</c> ends with <c>ReapplyReferences</c> on the object and, for a role, on each child,
    /// so this is where the permission is re-attached, whole: filter, metadata permission and annotations came back
    /// with the JSON, and the table and column pointers come back here.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public sealed partial class TablePermission
    {
        internal override void ReapplyReferences()
        {
            RelinkToModel();
            base.ReapplyReferences();
        }

        private void RelinkToModel()
        {
            var tom = MetadataObject;
            if (tom == null || Handler?.Model == null) return;
            if (tom.Table == null)
            {
                var table = Handler.Model.Tables.FindByName(tom.Name);
                if (table == null) return;
                tom.Table = table.MetadataObject;
            }
            var owner = Handler.Model.Tables.FindByName(tom.Table.Name);
            if (owner == null) return;
            foreach (var cp in tom.ColumnPermissions.Where(c => c.Column == null).ToList())
            {
                var column = owner.Columns.FindByName(cp.Name);
                if (column != null) cp.Column = column.MetadataObject;
            }
        }
    }
}
