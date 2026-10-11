using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Give a new relationship a NAME OF THE CALLER'S CHOOSING, as a step on the model's undo timeline. TOM names a new
    /// relationship with a fresh GUID and the wrapper's CreateNew ignores the name it is offered (its collection's
    /// GetNewName returns a GUID), so the name has to be set on the raw TOM object after the add. TE2's add record
    /// snapshots the relationship's JSON when the add is recorded, so on its own a redo brings the link back under the
    /// random GUID. This registers the rename as its own undo action right after the add: undo puts the old name back
    /// before the add is undone, redo restores the fixed name after the add is redone, so the same plan gives the same
    /// identity after undo then redo (spec-first start, S3). The wrapper finds its objects by reference, so a raw rename
    /// moves nothing else. The wrapper's Handler and MetadataObject are protected internal, hence this seam in
    /// Semanticus.Core (like <see cref="AuditAnnotations"/>).
    /// </summary>
    public static class NamedRelationship
    {
        public static void Rename(SingleColumnRelationship rel, string name)
        {
            var old = ((TOM.Relationship)rel.MetadataObject).Name;
            if (string.IsNullOrWhiteSpace(name) || name == old) return;
            ((TOM.Relationship)rel.MetadataObject).Name = name;
            rel.Handler.UndoManager.Add(new RenameAction(rel, old, name));
        }

        private sealed class RenameAction : IUndoAction
        {
            private readonly SingleColumnRelationship _rel;
            private readonly string _old, _new;
            public RenameAction(SingleColumnRelationship rel, string old, string @new) { _rel = rel; _old = old; _new = @new; }
            public string ActionName => "Name relationship";
            public string GetSummary() => "Name relationship: " + _new;
            public void Undo() => ((TOM.Relationship)_rel.MetadataObject).Name = _old;
            public void Redo() => ((TOM.Relationship)_rel.MetadataObject).Name = _new;
        }
    }
}
