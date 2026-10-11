using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;
using TabularEditor.TOMWrapper.Undo;
using TabularEditor.TOMWrapper.Utils;

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

        /// <summary>Enter the process-wide wrapper gate with this model's handler pinned as the wrapper's static
        /// handler (<see cref="WrapperGate"/>), until the returned scope is disposed. Every dispatcher work item of a
        /// session runs inside one, so an object it creates binds to THIS model whatever another model's dispatcher is
        /// doing. Re-entrant on one thread.</summary>
        IDisposable Pin();

        /// <summary>How many edit batches are open now, TE2's own nested ones included. A refusal path reads it to
        /// close every batch its work opened, not just the innermost one.</summary>
        int BatchDepth { get; }

        /// <summary>Serialize the model to <paramref name="target"/>. Not a tracked model mutation; the engine
        /// runs it on the dispatcher thread. <paramref name="resetCheckpoint"/> marks the result as clean.</summary>
        void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint);

        /// <summary>The real TOM change firehose, one event per property change on a tracked object. The engine
        /// coalesces these into per-operation deltas on the <see cref="ChangeBus"/>.</summary>
        event ObjectChangedEventHandler ObjectChanged;

        /// <summary>Fired just before an object is deleted, while it can still name itself.</summary>
        event ObjectDeletingEventHandler ObjectDeleting;

        /// <summary>Fired once an object has been deleted (a delete, or an undo that takes an add back).</summary>
        event ObjectDeletedEventHandler ObjectDeleted;
    }

    /// <summary>The shared undo/redo timeline. Single, regardless of which door (human UI / agent MCP) made the
    /// edit — the single-writer dispatcher gives a total order, so undo is "last edit first" across both.</summary>
    public interface IUndoLog
    {
        bool CanUndo { get; }
        bool CanRedo { get; }
        bool AtCheckpoint { get; }
        /// <summary>Steps on the undo stack, a batch counted once (TE2 <c>UndoManager.UndoSteps</c>).</summary>
        int UndoSteps { get; }
        /// <summary>Steps on the redo stack, a batch counted once (TE2 <c>UndoManager.RedoSteps</c>).</summary>
        int RedoSteps { get; }
        /// <summary>Raw actions on the undo stack (a batch's markers included); the mark <see cref="ObjectsAddedSince"/> counts from.</summary>
        int UndoSize { get; }
        /// <summary>The objects added by the actions pushed since the stack held <paramref name="undoSize"/> actions,
        /// oldest first. TOM fires no event for an add, so this is how a commit can tell it made one (contract 8.3).</summary>
        System.Collections.Generic.IReadOnlyList<ITabularObject> ObjectsAddedSince(int undoSize);
        void Undo();
        void Redo();
        /// <summary>Mark the current state as the clean checkpoint (e.g. just-opened, just-saved).</summary>
        void SetCheckpoint();
    }

    /// <summary>Vendored TE2 clears redo as soon as a batch records its first action, even when that batch later
    /// rolls back. Capture/restore lets a refused mutation put the previous redo back (D-113).</summary>
    /// <summary>Wrapper changes that make or drop model structure outside TE2's undo (a perspective's table entry): read before
    /// an edit's work, then sealed into one undo step after it, so taking the edit back, or rolling a rehearsal back,
    /// leaves the model as it was.</summary>
    internal interface IStructureGuard
    {
        object Capture();
        void Seal(object before);
    }

    internal interface IRedoPreserve
    {
        object CaptureRedo();
        void RestoreRedo(object snapshot);
    }

    /// <summary>Why the next undo or redo steps must not run because a file beside the model that one of them restores
    /// was changed on disk since (the Primer, its suggestion list), or null. Asked BEFORE the walk, which then never
    /// starts (Sol on m11/integration 568435d3, finding 2).</summary>
    internal interface ISidecarGuard
    {
        string SidecarRefusal(bool redo, int steps);
        /// <summary>The model moved (Save As): each sidecar file a step owns follows to where <paramref name="follow"/> says,
        /// or is let go on null (TOMWrapper SidecarUndo.Rebase).</summary>
        void SidecarRebase(Func<string, string> follow);
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
    internal sealed class Te2ModelSession : IModelSession, IStructureGuard
    {
        private readonly TabularModelHandler _h;
        private readonly Te2UndoLog _undo;
        private readonly Te2RenameService _renamer;

        private Te2ModelSession(TabularModelHandler handler)
        {
            _h = handler;
            CompatibilityGatedMembers.Track(handler);   // a level raised mid-session must not leave null OLS indexers behind
            RefreshPolicyUndo.Track(handler);   // a policy switched off comes back whole on undo, not blank
            // A deleted field parameter (or any column that groups others, or is grouped by another) comes back whole on undo.
            // It runs AFTER every listener of the delete (the session's own may call it off, which a rehearsal does to every
            // delete, and a called-off delete must touch nothing), so this class forwards the event instead of subscribing beside it.
            handler.ObjectDeleting += (sender, e) => { _deleting?.Invoke(sender, e); GroupByUndo.OnDeleting(handler, e); };
            _undo = new Te2UndoLog(handler.UndoManager);
            _renamer = new Te2RenameService();
        }

        /// <summary>Open a model from a file/folder path. MUST be invoked on the dispatcher thread: the handler
        /// ctor builds the wrapper graph and sets the process-wide Singleton there. Mirrors the prior inline
        /// build in <see cref="SessionManager.OpenAsync"/> exactly.</summary>
        public static Te2ModelSession Open(string path) => WrapperGate.Construct(() =>
        {
            // Built inside the gate: the handler's ctor moves the process-wide static, and no other model's work item
            // may be part-way through while it does (WrapperGate).
            var h = new TabularModelHandler(path);
            h.Settings.AutoFixup = true; // rename -> auto-rewrite all DAX references (the real FormulaFixup/ANTLR layer)
            h.Settings.UsePowerQueryPartitionsByDefault = true;   // Fabric/M-first; same as Create. New Table must not invent a provider source (D-050).
            return new Te2ModelSession(h);
        });

        /// <summary>Create a brand-new, empty Power-BI-mode model. MUST be invoked on the dispatcher thread.
        /// Mirrors the prior inline build in <see cref="SessionManager.CreateAsync"/> exactly.</summary>
        public static Te2ModelSession Create(string name, int compatibilityLevel) => WrapperGate.Construct(() =>
        {
            // pbiDatasetModel: true => Power BI compatibility mode + V3 data sources (the Fabric/Power BI target).
            var h = new TabularModelHandler(compatibilityLevel, null, pbiDatasetModel: true);
            h.Settings.AutoFixup = true;                          // rename -> auto DAX fixup, like Open
            h.Settings.UsePowerQueryPartitionsByDefault = true;   // Fabric/M-first; no auto legacy provider source
            if (!string.IsNullOrWhiteSpace(name)) h.Database.Name = name;
            return new Te2ModelSession(h);
        });

        public IDisposable Pin() => WrapperGate.Pin(_h);
        public Model Model => _h.Model;
        public Microsoft.AnalysisServices.Tabular.Database TomDatabase => _h.Database;
        public bool HasUnsavedChanges => _h.HasUnsavedChanges;
        public IUndoLog Undo => _undo;
        public IRenameService Renamer => _renamer;

        public void BeginUpdate(string label) => _h.BeginUpdate(label);
        public void EndUpdate(bool undoable = true, bool rollback = false) => _h.EndUpdate(undoable, rollback);
        public int BatchDepth => _h.UndoManager.BatchDepth;

        public void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint)
        {
            // TE2's save writes two annotations for its own use into every TMDL and .bim save, whether the model
            // had them or not: __TEdtr ("edited in Tabular Editor") and TabularEditor_SerializeOptions (the options
            // of this one save). A model from Power BI Desktop or a generator gained both on its first save, which
            // is noise in its repo (dogfood feedback, 27 Sep). Nothing reads them back: TE2 only hides __TEdtr from
            // its annotation list, and the options annotation matters only to TE2's own folder format, which is
            // left as TE2 writes it. So a stamp the model did not carry before the save is taken back out of the
            // written files, and out of memory, where TE2's TMDL path leaves __TEdtr for the next save to write.
            var stamps = format == SaveFormat.TMDL || format == SaveFormat.ModelSchemaOnly
                ? ToolStamps.NotOn(_h.Database?.Model) : Array.Empty<string>();
            _h.Save(target, format, options, resetCheckpoint: resetCheckpoint);
            if (stamps.Length == 0) return;
            ToolStamps.RemoveFromMemory(_h.Database?.Model, stamps);
            if (format == SaveFormat.TMDL) ToolStamps.StripTmdl(System.IO.Path.Combine(target, "model.tmdl"), stamps);
            else ToolStamps.StripBim(target, stamps);
        }

        public event ObjectChangedEventHandler ObjectChanged
        {
            add => _h.ObjectChanged += value;
            remove => _h.ObjectChanged -= value;
        }

        private event ObjectDeletingEventHandler _deleting;
        public event ObjectDeletingEventHandler ObjectDeleting
        {
            add => _deleting += value;
            remove => _deleting -= value;
        }

        // What the wrapper does outside its undo: the values it writes straight into TOM (ModelSeal, the class fix), and the
        // perspective tables it makes or drops (PerspectiveTableUndo, per family: an object made or dropped is not a value).
        object IStructureGuard.Capture() => (ModelSeal.Capture(_h), PerspectiveTableUndo.Capture(_h));
        void IStructureGuard.Seal(object before)
        {
            if (before is not ValueTuple<ModelSeal.Bracket, object> (var values, var tables)) return;
            PerspectiveTableUndo.Seal(_h, tables);
            ModelSeal.Seal(_h, values);
        }

        public event ObjectDeletedEventHandler ObjectDeleted
        {
            add => _h.ObjectDeleted += value;
            remove => _h.ObjectDeleted -= value;
        }

        /// <summary>1:1 over TE2's <c>UndoManager</c> — the one timeline both doors share.</summary>
        private sealed class Te2UndoLog : IUndoLog, IRedoPreserve, ISidecarGuard
        {
            private static readonly System.Reflection.FieldInfo RedoStackField =
                typeof(UndoManager).GetField("_RedoStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            private readonly UndoManager _u;
            public Te2UndoLog(UndoManager u) => _u = u;
            public bool CanUndo => _u.CanUndo;
            public bool CanRedo => _u.CanRedo;
            public bool AtCheckpoint => _u.AtCheckpoint;
            public int UndoSteps => _u.UndoSteps;
            public int RedoSteps => _u.RedoSteps;
            public int UndoSize => _u.UndoSize;

            private static readonly System.Reflection.FieldInfo UndoStackField =
                typeof(UndoManager).GetField("_UndoStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // TE2 records an add as an internal UndoAddRemoveAction; its type, action kind and object are private, so
            // they are read by name. A TE2 bump that renames them makes this return nothing (adds read as updates),
            // never throw into a commit; ChangeValuesTests pins the add so such a bump fails a test.
            public System.Collections.Generic.IReadOnlyList<ITabularObject> ObjectsAddedSince(int undoSize)
            {
                var added = new System.Collections.Generic.List<ITabularObject>();
                try
                {
                    var fresh = _u.UndoSize - undoSize;
                    if (fresh <= 0 || UndoStackField?.GetValue(_u) is not System.Collections.IEnumerable stack) return added;
                    var top = new System.Collections.Generic.List<object>();
                    foreach (var a in stack) { if (top.Count == fresh) break; top.Add(a); }   // a Stack enumerates newest first
                    for (var i = top.Count - 1; i >= 0; i--)
                    {
                        var a = top[i];
                        var t = a.GetType();
                        if (t.Name != "UndoAddRemoveAction") continue;
                        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        if (t.GetField("_actionType", F)?.GetValue(a)?.ToString() != "Add") continue;
                        if (t.GetField("_obj", F)?.GetValue(a) is ITabularObject obj) added.Add(obj);
                    }
                }
                catch { /* reading the stack is a ride-along: an add that cannot be read stays an update */ }
                return added;
            }
            public void Undo() => _u.Undo();
            public void Redo() => _u.Redo();
            public string SidecarRefusal(bool redo, int steps) => TabularEditor.TOMWrapper.Utils.SidecarUndo.Refusal(_u, redo, steps);
            public void SidecarRebase(Func<string, string> follow) => TabularEditor.TOMWrapper.Utils.SidecarUndo.Rebase(_u, follow);
            public void SetCheckpoint() => _u.SetCheckpoint();

            public object CaptureRedo()
            {
                var stack = RedoStackField?.GetValue(_u);
                if (stack == null) return System.Array.Empty<object>();
                return stack.GetType().GetMethod("ToArray").Invoke(stack, null);
            }

            public void RestoreRedo(object snapshot)
            {
                if (snapshot == null || RedoStackField == null) return;
                var stack = RedoStackField.GetValue(_u);
                if (stack == null) return;
                stack.GetType().GetMethod("Clear").Invoke(stack, null);
                var items = (System.Array)snapshot;
                var push = stack.GetType().GetMethod("Push");
                for (var i = items.Length - 1; i >= 0; i--)
                    push.Invoke(stack, new[] { items.GetValue(i) });
            }
        }

        /// <summary>The TE2 rename: setting the wrapper's <c>Name</c> triggers AutoFixup (enabled at build) which
        /// rewrites every DAX / RLS reference. No handler state is needed here — the contract IS the wrapper
        /// setter — but keeping it behind the seam is the point (where a future fixup backend attaches).</summary>
        private sealed class Te2RenameService : IRenameService
        {
            public bool Rename(TabularNamedObject obj, string newName)
            {
                if (obj.Name == newName) return false;
                // The fixup rewrites only the formulas the dependency tree lists: bring the tree up to date first (it lags inside a write batch, and never lists a KPI).
                TabularEditor.TOMWrapper.Utils.DependencyMaintenance.PrepareForRename();
                obj.Name = newName; // AutoFixup rewrites all references (measures, calc cols, RLS filters, …)
                return true;
            }
        }
    }

    /// <summary>The two annotations TE2 stamps on a save for its own use (see <see cref="Te2ModelSession.Save"/>),
    /// and how to take back the ones a model did not already carry. Each strip is shaped to what TE2 just wrote and
    /// leaves anything it does not recognise alone: a stamp left in is noise, a mangled file is a broken model.</summary>
    internal static class ToolStamps
    {
        internal static readonly string[] Names = { "__TEdtr", "TabularEditor_SerializeOptions" };

        public static string[] NotOn(Microsoft.AnalysisServices.Tabular.Model m) =>
            m == null ? Array.Empty<string>() : Names.Where(n => !m.Annotations.Contains(n)).ToArray();

        public static void RemoveFromMemory(Microsoft.AnalysisServices.Tabular.Model m, string[] names)
        {
            foreach (var n in names)
                if (m != null && m.Annotations.Contains(n)) m.Annotations.Remove(n);
        }

        /// <summary>TE2 writes a .bim with Newtonsoft's indented form, so re-writing it the same way changes only
        /// the removed entries.</summary>
        public static void StripBim(string path, string[] names)
        {
            if (!File.Exists(path)) return;
            var root = JObject.Parse(File.ReadAllText(path));
            if (!(root["model"]?["annotations"] is JArray list)) return;
            var drop = list.OfType<JObject>().Where(a => names.Contains((string)a["name"], StringComparer.Ordinal)).ToList();
            if (drop.Count == 0) return;
            foreach (var a in drop) a.Remove();
            if (list.Count == 0) list.Parent.Remove();   // TOM writes no empty annotations array
            File.WriteAllText(path, root.ToString(Formatting.Indented));
        }

        /// <summary>In TMDL each stamp is one line, "annotation NAME = value", followed by the blank line TOM puts
        /// between siblings. Both go. A stamp whose value runs onto more lines is left in place. Only a MODEL-LEVEL
        /// declaration is a stamp: the lines of another annotation's multi-line value are its text, not declarations,
        /// so a line inside one that reads like a stamp stays (Sol 4.4), and so does a stamp on a nested object.
        /// <paramref name="takeMultiLine"/> is for a copy that is only read (the start plan's projection): there a stamp whose value runs onto more lines is
        /// taken out whole too, with the lines of its value, because a JSON value (the waivers, the readiness rules) is written that way. A real save never sets it.</summary>
        public static void StripTmdl(string path, string[] names, bool takeMultiLine = false)
        {
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var lines = Regex.Split(text, "(?<=\n)").Where(l => l.Length > 0).ToList();
            var declaration = new Regex("^([ \\t]*)annotation[ \\t]+");
            var stamp = new Regex("^([ \\t]*)annotation[ \\t]+(?:" + string.Join("|", names.Select(Regex.Escape)) + ")[ \\t]*=");
            // TE2 writes the model's own annotations at the model's own level or one level in; anything deeper belongs to an object under it.
            var header = lines.FindIndex(l => l.StartsWith("model", StringComparison.Ordinal) && (l.Length == 5 || char.IsWhiteSpace(l[5])));
            var modelLevel = 0;
            if (header >= 0)
                for (var k = header + 1; k < lines.Count; k++)
                    if (lines[k].Trim().Length > 0) { modelLevel = Indent(lines[k]); break; }
            var changed = false;
            var owner = -1;   // the indent of the annotation whose multi-line value we are inside, or -1
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length == 0) continue;   // a blank line belongs to a value block as much as to the gap after it
                var indent = Indent(lines[i]);
                if (owner >= 0) { if (indent > owner) continue; owner = -1; }
                if (!declaration.IsMatch(lines[i])) continue;
                var m = stamp.Match(lines[i]);
                var next = i + 1 < lines.Count ? lines[i + 1] : null;
                var blank = next != null && next.Trim().Length == 0;
                var multiLine = next != null && !blank && Indent(next) > indent;
                if (multiLine) owner = indent;   // its next lines are this value's text
                if (m.Success && takeMultiLine && multiLine && indent <= modelLevel)
                {
                    var last = i;   // the value's lines: deeper than the declaration, a blank line among them belongs to it
                    for (var k = i + 1; k < lines.Count && (lines[k].Trim().Length == 0 || Indent(lines[k]) > indent); k++)
                        if (lines[k].Trim().Length > 0) last = k;
                    lines.RemoveRange(i, last - i + 1 + (last + 1 < lines.Count && lines[last + 1].Trim().Length == 0 ? 1 : 0));   // and the blank line TOM puts after it
                    owner = -1; i--; changed = true;
                    continue;
                }
                if (!m.Success || indent > modelLevel || multiLine) continue;
                lines.RemoveRange(i, blank ? 2 : 1);
                i--;
                changed = true;
            }
            if (!changed) return;
            var body = Encoding.UTF8.GetBytes(string.Concat(lines));
            File.WriteAllBytes(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body);
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ', '\t').Length;
    }
}
