using System.IO;
using System.Linq;
using System.Text;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;
using SER = Microsoft.AnalysisServices.Tabular.Serialization;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// In-place TMDL APPLY — the deserialize counterpart of <see cref="TmdlScripter"/>, integrated with the
    /// TOMWrapper change/undo system. Uses TOM's <c>MetadataSerializationContext.UpdateModel</c> (the same API
    /// Power BI Desktop's TMDL view uses) to apply a top-level object document (e.g. a table) onto the existing
    /// model in place, then:
    ///  • <c>Model.Reinit()</c> so the wrapper reflects the change (incl. structural adds like a new measure), and
    ///  • registers a custom <see cref="IUndoAction"/> that re-applies the BEFORE TMDL on undo / AFTER on redo —
    ///    so a TMDL apply is fully undoable and correctly marks the model dirty, exactly like every other edit.
    /// Lives in Semanticus.Core (compiled alongside the TOMWrapper sources) so it can reach the wrapper's
    /// <c>protected internal</c> MetadataObject and the <c>internal</c> undo API.
    /// </summary>
    public static class TmdlApplier
    {
        /// <summary>Apply a single top-level TMDL document (<paramref name="afterTmdl"/>) at
        /// <paramref name="logicalPath"/> (e.g. "./tables/Sales"). The object must already exist (edit existing
        /// objects — incl. adding child measures/columns); a brand-new top-level object is rejected so undo stays
        /// reversible. Throws TmdlFormatException on a malformed script.</summary>
        public static void Apply(Model model, string logicalPath, string afterTmdl)
        {
            // Wrapper constructors used during Reinit consult this process-global value. Pin the active session
            // because an unrelated temporary model may have moved it since this model was opened.
            TabularModelHandler.Singleton = model.Handler;
            var before = CaptureDoc(model, logicalPath);
            if (before == null)
                throw new System.InvalidOperationException(
                    $"apply_tmdl: '{logicalPath}' does not exist. TMDL apply edits an existing object (you can add measures/columns inside it); create a new table/role with the typed actions first.");
            var tom = (TOM.Model)model.MetadataObject;
            try
            {
                ApplyDoc(tom, logicalPath, afterTmdl);
            }
            catch (System.Exception applyEx)
            {
                // ATOMICITY: UpdateModel can throw mid-mutation (e.g. an expression that fails validation), leaving
                // the TOM model partially changed. Re-apply the captured BEFORE document to restore the prior state,
                // reinit the wrapper, then rethrow — so the caller's batch skips this doc and the next doc applies
                // onto a CONSISTENT model (no half-applied changes, no stale wrapper, nothing left undo-untracked).
                try { ApplyDoc(tom, logicalPath, before); }
                catch (System.Exception rollbackEx)
                {
                    // DOUBLE FAILURE: the revert ALSO threw, so the model is left half-modified AND we could not undo
                    // it. Fail LOUD with EVERY failure as a real inner exception (message text alone loses the stacks)
                    // — and guard the wrapper rebuild so a Reinit throw can't MASK both underlying failures.
                    System.Exception reinitEx = null;
                    try { model.Reinit(); } catch (System.Exception rx) { reinitEx = rx; }
                    var msg = $"apply_tmdl failed: {applyEx.Message}; ROLLBACK ALSO FAILED: {rollbackEx.Message} — the in-memory model may be partially modified; re-open it from disk before editing further.";
                    throw reinitEx == null
                        ? new System.AggregateException(msg, applyEx, rollbackEx)
                        : new System.AggregateException(msg + $" (the wrapper rebuild also failed: {reinitEx.Message})", applyEx, rollbackEx, reinitEx);
                }
                // Rollback succeeded — surface the ORIGINAL error, stack intact. Guard the rebuild: the TOM state is
                // restored, and a stale-wrapper failure here must not REPLACE the real error the caller needs.
                try { model.Reinit(); }
                catch (System.Exception reinitEx)
                {
                    throw new System.AggregateException(
                        $"apply_tmdl failed: {applyEx.Message}; the rollback restored the object, but the wrapper failed to rebuild: {reinitEx.Message} — re-open the model from disk before editing further.",
                        applyEx, reinitEx);
                }
                throw;
            }
            model.Reinit();   // rebuild the wrapper from TOM so structural changes (e.g. a new measure) surface
            model.Handler.UndoManager.Add(new TmdlApplyUndoAction(model, logicalPath, before, afterTmdl));
        }

        // The raw in-place apply (no undo registration) — an EMPTY context reads just this document, UpdateModel
        // applies it as a partial update to the existing model (other objects untouched).
        internal static void ApplyDoc(TOM.Model tom, string logicalPath, string tmdl)
        {
            var ctx = SER.MetadataSerializationContext.Create(SER.MetadataSerializationStyle.Tmdl);
            using var reader = new StringReader(tmdl);
            ctx.ReadFromDocument(logicalPath, reader, Encoding.UTF8);
            ctx.UpdateModel(tom, SER.MetadataDeserializationOptions.Default, null);
        }

        // Current TMDL of the object at logicalPath ("./tables/Sales" -> table Sales), or null if it doesn't exist.
        internal static string CaptureDoc(Model model, string logicalPath)
        {
            var obj = ResolveTopLevel(model, logicalPath);
            return obj == null ? null : TmdlScripter.ScriptTmdl(obj);
        }

        internal static TabularNamedObject ResolveTopLevel(Model model, string logicalPath)
        {
            var p = (logicalPath ?? "").TrimStart('.', '/');
            var slash = p.IndexOf('/');
            if (slash < 0) return null;
            var kind = p.Substring(0, slash);
            var name = p.Substring(slash + 1);
            switch (kind)
            {
                case "tables": return model.Tables.FirstOrDefault(t => t.Name == name);
                case "roles": return model.Roles.FirstOrDefault(r => r.Name == name);
                default: return null;
            }
        }
    }

    /// <summary>Undo/redo for a TMDL apply: re-apply the BEFORE document on undo, the AFTER on redo (each followed
    /// by a wrapper reinit). Re-applying TMDL is the same proven operation as the forward apply, so this restores
    /// the exact prior/next state including structural adds. Runs inside the UndoManager (Add is a no-op while
    /// undo/redo is in progress, so these re-applies never re-register themselves).</summary>
    internal sealed class TmdlApplyUndoAction : IUndoAction
    {
        private readonly Model _model;
        private readonly string _path, _before, _after;
        public TmdlApplyUndoAction(Model model, string path, string before, string after)
        { _model = model; _path = path; _before = before; _after = after; }

        public string ActionName => "Apply TMDL";
        public string GetSummary() => "Apply TMDL: " + _path;
        public void Undo()
        {
            TabularModelHandler.Singleton = _model.Handler;
            TmdlApplier.ApplyDoc((TOM.Model)_model.MetadataObject, _path, _before);
            _model.Reinit();
        }
        public void Redo()
        {
            TabularModelHandler.Singleton = _model.Handler;
            TmdlApplier.ApplyDoc((TOM.Model)_model.MetadataObject, _path, _after);
            _model.Reinit();
        }
    }
}
