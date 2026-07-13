using System;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;
using TabularEditor.TOMWrapper.Undo;

namespace Semanticus.Engine
{
    /// <summary>
    /// The control-plane seam around ONE open model. The engine (<see cref="Session"/>) depends on this
    /// interface, never on the concrete TE2 <c>TabularModelHandler</c> — so the TOM-wrapper backing, and in
    /// particular the process-wide <c>TabularModelHandler.Singleton</c> that backing sets, can be de-static-ed
    /// or swapped by replacing the adapter (<see cref="Te2ModelSession"/>) without touching engine logic. This
    /// is the spine doc 02 §5 calls for and where the Singleton dies later.
    ///
    /// SCOPE — honest boundary: this isolates the handler's *lifecycle / control plane* (build, save, the
    /// begin/end update framing, the undo timeline, the rename→FormulaFixup contract, dirty state, the change
    /// firehose). The TOM object graph itself (<see cref="Model"/>) still flows through for reading and writing
    /// model *content* — the engine is built on TOM wrapper objects, and hiding those entirely is a far larger,
    /// later effort. The seam draws the line exactly where the architectural liability (the static, the fixup
    /// engine, the serializer) lives, not around all of TOM.
    /// </summary>
    public interface IModelSession
    {
        /// <summary>The TOM wrapper object graph for this model.</summary>
        Model Model { get; }

        /// <summary>The underlying RAW TOM database (Microsoft.AnalysisServices.Tabular), for read-only interop that
        /// needs the unwrapped metadata — e.g. extracting a Dax.Metadata model for a .vpax export. The wrapper's
        /// MetadataObject is protected-internal (engine can't reach it), so the seam surfaces the handler's Database.</summary>
        Microsoft.AnalysisServices.Tabular.Database TomDatabase { get; }

        /// <summary>True when there are edits not yet serialized to the model's source.</summary>
        bool HasUnsavedChanges { get; }

        /// <summary>The single undo/redo timeline both doors share (one per open model).</summary>
        IUndoLog Undo { get; }

        /// <summary>The rename→reference-rewrite contract (FormulaFixup). See <see cref="IRenameService"/>.</summary>
        IRenameService Renamer { get; }

        /// <summary>Open one undoable edit batch labelled <paramref name="label"/>. Pair with <see cref="EndUpdate"/>.</summary>
        void BeginUpdate(string label);

        /// <summary>Close the current edit batch. <paramref name="rollback"/> reverts it (used on the failure path).</summary>
        void EndUpdate(bool undoable = true, bool rollback = false);

        /// <summary>Serialize the model to <paramref name="target"/>. Not a tracked model mutation; the engine
        /// runs it on the dispatcher thread. <paramref name="resetCheckpoint"/> marks the result as clean.</summary>
        void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint);

        /// <summary>The real TOM change firehose, one event per property change on a tracked object. The engine
        /// coalesces these into per-operation deltas on the <see cref="ChangeBus"/>.</summary>
        event ObjectChangedEventHandler ObjectChanged;
    }

    /// <summary>The shared undo/redo timeline. Single, regardless of which door (human UI / agent MCP) made the
    /// edit — the single-writer dispatcher gives a total order, so undo is "last edit first" across both.</summary>
    public interface IUndoLog
    {
        bool CanUndo { get; }
        bool CanRedo { get; }
        bool AtCheckpoint { get; }
        void Undo();
        void Redo();
        /// <summary>Mark the current state as the clean checkpoint (e.g. just-opened, just-saved).</summary>
        void SetCheckpoint();
    }

    /// <summary>
    /// The single contract point for "rename an object and rewrite every DAX / RLS-filter reference to it"
    /// (FormulaFixup). It is deliberately one named seam because rename is the riskiest invariant in the engine:
    /// a rename that forgets a reference dangles DAX exactly where it is most dangerous (a measure, or a
    /// governance-critical row-level-security filter). D4 (docs/strategy/04) keeps the *implementation* on TE2's
    /// ANTLR-backed fixup rather than re-deriving it natively; this interface is where a future backend (or a
    /// TOM-bump-gated swap) attaches without changing the rename verb's callers. The
    /// <c>FormulaFixup_rewrites_a_renamed_column…</c> test pins the behavior.
    /// </summary>
    public interface IRenameService
    {
        /// <summary>Rename <paramref name="obj"/> to <paramref name="newName"/>, letting fixup rewrite all
        /// references. Returns true if the name actually changed (a no-op rename returns false).</summary>
        bool Rename(TabularNamedObject obj, string newName);
    }

    /// <summary>
    /// The TE2-backed <see cref="IModelSession"/>: a thin, 1:1 adapter over <c>TabularModelHandler</c>. ALL
    /// construction of the handler — and therefore the only place the process-wide Singleton it sets is born —
    /// lives in the <see cref="Open"/>/<see cref="Create"/> factories here, so <see cref="SessionManager"/> no
    /// longer names the concrete TE2 type. The handler is intentionally NOT disposed (it never was pre-seam: the
    /// dispatcher lifecycle governs model replacement, and the next handler's ctor resets the Singleton); the
    /// adapter holds no other unmanaged state, so it owns no <c>IDisposable</c>.
    /// </summary>
    internal sealed class Te2ModelSession : IModelSession
    {
        private readonly TabularModelHandler _h;
        private readonly Te2UndoLog _undo;
        private readonly Te2RenameService _renamer;

        private Te2ModelSession(TabularModelHandler handler)
        {
            _h = handler;
            _undo = new Te2UndoLog(handler.UndoManager);
            _renamer = new Te2RenameService();
        }

        /// <summary>Open a model from a file/folder path. MUST be invoked on the dispatcher thread: the handler
        /// ctor builds the wrapper graph and sets the process-wide Singleton there. Mirrors the prior inline
        /// build in <see cref="SessionManager.OpenAsync"/> exactly.</summary>
        public static Te2ModelSession Open(string path)
        {
            var h = new TabularModelHandler(path);
            h.Settings.AutoFixup = true; // rename -> auto-rewrite all DAX references (the real FormulaFixup/ANTLR layer)
            return new Te2ModelSession(h);
        }

        /// <summary>Create a brand-new, empty Power-BI-mode model. MUST be invoked on the dispatcher thread.
        /// Mirrors the prior inline build in <see cref="SessionManager.CreateAsync"/> exactly.</summary>
        public static Te2ModelSession Create(string name, int compatibilityLevel)
        {
            // pbiDatasetModel: true => Power BI compatibility mode + V3 data sources (the Fabric/Power BI target).
            var h = new TabularModelHandler(compatibilityLevel, null, pbiDatasetModel: true);
            h.Settings.AutoFixup = true;                          // rename -> auto DAX fixup, like Open
            h.Settings.UsePowerQueryPartitionsByDefault = true;   // Fabric/M-first; no auto legacy provider source
            if (!string.IsNullOrWhiteSpace(name)) h.Database.Name = name;
            return new Te2ModelSession(h);
        }

        public Model Model => _h.Model;
        public Microsoft.AnalysisServices.Tabular.Database TomDatabase => _h.Database;
        public bool HasUnsavedChanges => _h.HasUnsavedChanges;
        public IUndoLog Undo => _undo;
        public IRenameService Renamer => _renamer;

        public void BeginUpdate(string label) => _h.BeginUpdate(label);
        public void EndUpdate(bool undoable = true, bool rollback = false) => _h.EndUpdate(undoable, rollback);

        public void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint) =>
            _h.Save(target, format, options, resetCheckpoint: resetCheckpoint);

        public event ObjectChangedEventHandler ObjectChanged
        {
            add => _h.ObjectChanged += value;
            remove => _h.ObjectChanged -= value;
        }

        /// <summary>1:1 over TE2's <c>UndoManager</c> — the one timeline both doors share.</summary>
        private sealed class Te2UndoLog : IUndoLog
        {
            private readonly UndoManager _u;
            public Te2UndoLog(UndoManager u) => _u = u;
            public bool CanUndo => _u.CanUndo;
            public bool CanRedo => _u.CanRedo;
            public bool AtCheckpoint => _u.AtCheckpoint;
            public void Undo() => _u.Undo();
            public void Redo() => _u.Redo();
            public void SetCheckpoint() => _u.SetCheckpoint();
        }

        /// <summary>The TE2 rename: setting the wrapper's <c>Name</c> triggers AutoFixup (enabled at build) which
        /// rewrites every DAX / RLS reference. No handler state is needed here — the contract IS the wrapper
        /// setter — but keeping it behind the seam is the point (where a future fixup backend attaches).</summary>
        private sealed class Te2RenameService : IRenameService
        {
            public bool Rename(TabularNamedObject obj, string newName)
            {
                if (obj.Name == newName) return false;
                obj.Name = newName; // AutoFixup rewrites all references (measures, calc cols, RLS filters, …)
                return true;
            }
        }
    }
}
