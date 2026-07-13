namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// NON-UNDOABLE model-annotation writes — the persistence seam for the append-only Verified Edits audit
    /// trail. The public <c>IAnnotationObject.SetAnnotation</c> registers an <c>UndoAnnotationAction</c>, so a
    /// store written through it (waivers, DaxLib provenance) is one <c>undo_change</c> away from silently
    /// vanishing — acceptable for a preference, fatal for an audit record. The TOMWrapper's internal 3-arg
    /// overload with <c>undoable:false</c> skips BOTH the undo stack (the record survives undo/redo from either
    /// door) and the property-changed broadcast (no phantom delta in the didChange stream). It is internal to
    /// this assembly, so the seam lives here in Semanticus.Core (like <see cref="TmdlApplier"/>).
    /// AUDIT-ONLY: every ordinary edit must keep using the public undoable route — the undo invariant
    /// (docs/op-routing-map.md) holds for everything a user would ever want to take back.
    /// </summary>
    public static class AuditAnnotations
    {
        public static string Get(Model m, string name) => (m as IAnnotationObject)?.GetAnnotation(name);

        /// <summary>Write (or with <paramref name="value"/> null, remove) a model annotation WITHOUT an undo
        /// step. Callers must treat the target annotation as append-only state they own outright.</summary>
        public static void Set(Model m, string name, string value)
        {
            if (m is IAnnotationObject ao) ao.SetAnnotation(name, value, undoable: false);
        }
    }
}
