using System;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// The raw-TOM seam for calendar-based time intelligence (CL 1701+): <c>Table.Calendars</c>,
    /// <c>Calendar</c>, <c>TimeUnitColumnAssociation</c>, <c>TimeRelatedColumnGroup</c> — TOM 19.114 objects the
    /// vendored (pinned, pristine) TOMWrapper predates and does not wrap. Mutations therefore can't ride the
    /// wrapper's per-property change tracking; instead <see cref="Mutate"/> captures the table's BEFORE/AFTER
    /// TMDL and registers the same <see cref="TmdlApplyUndoAction"/> a TMDL apply uses — so a calendar edit is
    /// fully undoable on the one shared timeline and marks the model dirty, without modifying the submodule.
    /// Lives in Semanticus.Core to reach the wrapper's protected-internal MetadataObject + internal undo API.
    /// </summary>
    public static class CalendarOps
    {
        /// <summary>The CL floor for calendar metadata (the TMDL <c>calendar</c> block).</summary>
        public const int MinCompatibilityLevel = 1701;

        /// <summary>The raw TOM table behind a wrapper table — the read entry point for Calendars.</summary>
        public static TOM.Table Raw(Table table) => (TOM.Table)table.MetadataObject;

        /// <summary>True if ANY table in the model defines a calendar. The minimal public seam for callers outside
        /// Core (e.g. the AI-readiness analyzer) that must see calendar PRESENCE — Calendars are raw TOM the wrapper
        /// doesn't expose, and <see cref="Raw"/>'s cast needs Core's protected-internal MetadataObject access.</summary>
        public static bool AnyCalendars(Model model)
        {
            foreach (var t in model.Tables)
                if (Raw(t).Calendars.Count > 0) return true;
            return false;
        }

        /// <summary>Mutate a table's raw-TOM calendar metadata as ONE undoable step. The mutation must be
        /// synchronous, pure TOM-object-graph work (no I/O). On failure the BEFORE document is re-applied so
        /// the model never keeps a half-applied calendar (same atomicity contract as TmdlApplier.Apply).</summary>
        public static void Mutate(Model model, Table table, Action<TOM.Table> mutate)
        {
            // The vendored wrapper's constructors consult a process-global handler. Long-lived engine work can
            // create temporary models and move that singleton, so pin it back to this session before any Reinit.
            TabularModelHandler.Singleton = model.Handler;
            var path = "./tables/" + table.Name;
            var before = TmdlScripter.ScriptTmdl(table);
            var tomModel = (TOM.Model)model.MetadataObject;
            string after;
            try
            {
                mutate(Raw(table));
                after = TmdlScripter.ScriptTmdl(table);   // capture pre-Reinit: the wrapper ref is still current
            }
            catch
            {
                try { TmdlApplier.ApplyDoc(tomModel, path, before); } catch { /* best-effort revert */ }
                model.Reinit();
                throw;
            }
            model.Reinit();   // rebuild the wrapper view (it reuses existing wrappers; calendars stay raw-only)
            model.Handler.UndoManager.Add(new TmdlApplyUndoAction(model, path, before, after));
        }

        /// <summary>Create a calculated table and mutate its raw calendar metadata as ONE undoable step. This is
        /// deliberately a raw-TOM create: mixing the wrapper's add-object undo action with <see cref="Model.Reinit"/>
        /// leaves that action holding a stale wrapper, which can make an opened/upgraded model fail during rollback.</summary>
        public static void CreateCalculatedTable(Model model, string tableName, string expression, Action<TOM.Table> mutate)
        {
            var tomModel = (TOM.Model)model.MetadataObject;
            // Wrapper constructors use TabularModelHandler.Singleton rather than the parent wrapper's handler.
            // Reassert both pieces of the active-session invariant before Reinit creates the new table wrapper.
            TabularModelHandler.Singleton = model.Handler;
            model.Handler.WrapperLookup[tomModel] = model;
            if (tomModel.Tables.Find(tableName) != null)
                throw new InvalidOperationException($"A table named '{tableName}' already exists.");

            var lineageTag = Guid.NewGuid().ToString();
            var table = new TOM.Table { Name = tableName, LineageTag = lineageTag };
            table.Partitions.Add(new TOM.Partition
            {
                Name = tableName,
                Mode = TOM.ModeType.Import,
                Source = new TOM.CalculatedPartitionSource { Expression = expression },
            });

            var path = "./tables/" + tableName;
            try
            {
                tomModel.Tables.Add(table);
                mutate(table);
                model.Reinit();
                var after = TmdlApplier.CaptureDoc(model, path)
                    ?? throw new InvalidOperationException($"Calendar table '{tableName}' was created but could not be scripted for undo.");
                model.Handler.UndoManager.Add(new TmdlCreateTableUndoAction(model, tableName, lineageTag, after));
            }
            catch
            {
                var added = tomModel.Tables.Find(tableName);
                if (added != null && string.Equals(added.LineageTag, lineageTag, StringComparison.Ordinal))
                    tomModel.Tables.Remove(added);
                model.Handler.WrapperLookup[tomModel] = model;
                model.Reinit();
                throw;
            }
        }
    }

    /// <summary>Undo/redo for the raw-TOM calculated-table creation above. Identity is pinned by LineageTag so an
    /// unexpected same-name replacement is never deleted. Redo reuses the same TMDL partial-update path as calendar
    /// edits, restoring the complete table, generated columns, sort pairs and calendar metadata in one operation.</summary>
    internal sealed class TmdlCreateTableUndoAction : IUndoAction
    {
        private readonly Model _model;
        private readonly string _tableName, _lineageTag, _after;

        public TmdlCreateTableUndoAction(Model model, string tableName, string lineageTag, string after)
        { _model = model; _tableName = tableName; _lineageTag = lineageTag; _after = after; }

        public string ActionName => "Create calendar table";
        public string GetSummary() => "Create calendar table: " + _tableName;

        public void Undo()
        {
            var tom = (TOM.Model)_model.MetadataObject;
            TabularModelHandler.Singleton = _model.Handler;
            var table = tom.Tables.Find(_tableName)
                ?? throw new InvalidOperationException($"Cannot undo calendar table creation: '{_tableName}' no longer exists.");
            if (!string.Equals(table.LineageTag, _lineageTag, StringComparison.Ordinal))
                throw new InvalidOperationException($"Cannot undo calendar table creation: '{_tableName}' now has a different identity.");
            tom.Tables.Remove(table);
            _model.Handler.WrapperLookup[tom] = _model;
            _model.Reinit();
        }

        public void Redo()
        {
            var tom = (TOM.Model)_model.MetadataObject;
            TabularModelHandler.Singleton = _model.Handler;
            if (tom.Tables.Find(_tableName) != null)
                throw new InvalidOperationException($"Cannot redo calendar table creation: '{_tableName}' already exists.");
            TmdlApplier.ApplyDoc(tom, "./tables/" + _tableName, _after);
            _model.Handler.WrapperLookup[tom] = _model;
            _model.Reinit();
        }
    }
}
