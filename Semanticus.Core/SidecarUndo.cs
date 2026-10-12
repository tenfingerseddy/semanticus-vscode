using System;
using System.Collections.Generic;
using System.IO;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Undo;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>What a sidecar step owns in one file: the whole file, or one PART of it (one entry of a list other writers
    /// also write), with what that held before the step and after it (null = none), and what to call it in a refusal
    /// ("The Primer file"). The step's undo and redo put back only what it owns, and are refused only when that moved.</summary>
    public sealed class SidecarFile
    {
        private readonly Func<string, string> _read;
        private readonly Action<string, string> _write;

        /// <summary>The whole file: <paramref name="read"/> gives its text (null = no file) and <paramref name="write"/>
        /// puts a text back exactly (null = delete it). Both take the file's path, which a Save As can move.</summary>
        public SidecarFile(string what, string path, string before, string after, Func<string, string> read, Action<string, string> write)
            : this(what, path, null, before, after, read, write) { }

        /// <summary>One part of a file, named by <paramref name="part"/>: <paramref name="read"/> gives that part's value
        /// (null = absent) and <paramref name="write"/> sets or removes it and leaves the rest of the file as it is now.</summary>
        public SidecarFile(string what, string path, string part, string before, string after, Func<string, string> read, Action<string, string> write)
        {
            What = what; Path = path; Part = part; Before = before; After = after;
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }
        public string What { get; }
        /// <summary>Where the file is now. A Save As that carries it to the new model's name moves it; null once the step
        /// has let it go, because the file is no longer this session's model's (<see cref="SidecarUndo.Rebase"/>).</summary>
        public string Path { get; internal set; }
        public string Part { get; }
        public string Before { get; }
        public string After { get; }
        internal string Now() => _read(Path);
        internal void Put(string value) { if (Path != null) _write(Path, value); }
    }

    /// <summary>Puts a change that lives BESIDE the model (a sidecar file such as the Primer) on the model's one undo
    /// timeline, so History can show it and Undo to here can take it back with the model edits around it (F-353).
    /// The caller has already made the change; <paramref name="undo"/> and <paramref name="redo"/> restore the before
    /// and after states. Call it inside the tracked batch, like any other undoable change.</summary>
    public static class SidecarUndo
    {
        public static void Register(Model model, string name, Action undo, Action redo)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            model.Handler.UndoManager.Add(new SidecarUndoAction(name, null, undo, redo));
        }

        /// <summary>As above, for a step that owns <paramref name="files"/> (or parts of them): undo puts back each one's
        /// before, redo its after, and either is refused before it starts when one of them was changed on disk since
        /// (Sol on m11/integration 568435d3, finding 2: Undo used to overwrite a Primer edited in another editor). A step
        /// owns only what it wrote, so another writer's change to the rest of a shared file (a rejected suggestion beside
        /// an accepted one) is neither a conflict nor overwritten. See <see cref="Refusal"/>.</summary>
        public static void Register(Model model, string name, IReadOnlyList<SidecarFile> files)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (files == null) throw new ArgumentNullException(nameof(files));
            model.Handler.UndoManager.Add(new SidecarUndoAction(name, files,
                undo: () => { foreach (var f in files) f.Put(f.Before); },
                redo: () => { foreach (var f in files) f.Put(f.After); }));
        }

        private static readonly System.Reflection.FieldInfo UndoStackField =
            typeof(UndoManager).GetField("_UndoStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo RedoStackField =
            typeof(UndoManager).GetField("_RedoStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>Why the next <paramref name="steps"/> undo (or redo) steps must not run, or null when they may. What
        /// each sidecar step owns must hold what that step left there (after, for an undo; before, for a redo), and a walk
        /// over several steps checks each against what the steps before it will have put back, so two Primer saves undone
        /// together still agree. It reads the stack exactly as TE2's XDo walks it, a batch as one step, and changes
        /// nothing. A refusal must come BEFORE the walk: an action that throws inside XDo leaves TE2's stack half popped.</summary>
        public static string Refusal(UndoManager undo, bool redo, int steps)
        {
            if (undo == null || steps <= 0) return null;
            if ((redo ? RedoStackField : UndoStackField)?.GetValue(undo) is not Stack<IUndoAction> stack) return null;
            var planned = new Dictionary<(string Path, string Part), string>();   // what the walk will have put back so far
            using var walk = stack.GetEnumerator();   // a Stack enumerates in pop order
            string Check(IUndoAction item, bool forward)
            {
                if (item is IInverseUndoAction inverse)
                {
                    foreach (var inner in inverse.ActionsFor(forward))
                    {
                        var why = Check(inner, !forward);
                        if (why != null) return why;
                    }
                    return null;
                }
                if (item is not SidecarUndoAction s || s.Files == null) return null;
                foreach (var f in s.Files)
                {
                    if (f.Path == null) continue;   // let go at a Save As: nothing of this session's to check or overwrite
                    var now = planned.TryGetValue((f.Path, f.Part), out var p) ? p : f.Now();
                    if (!string.Equals(now, forward ? f.Before : f.After, StringComparison.Ordinal))
                        return (redo ? "Nothing was redone. " : "Nothing was undone. ") + f.What + " changed on disk after "
                               + (redo ? "it was undone" : "this change") + ", and " + (redo ? "redoing" : "undoing")
                               + " it would overwrite that newer edit.";
                }
                foreach (var f in s.Files) if (f.Path != null) planned[(f.Path, f.Part)] = forward ? f.After : f.Before;
                return null;
            }
            for (var i = 0; i < steps; i++)
            {
                if (!walk.MoveNext()) return null;
                var item = walk.Current;
                var why = Check(item, redo);
                if (why != null) return why;
                if (item is not UndoBatchAction batch) continue;
                IUndoAction next;
                do
                {
                    if (!walk.MoveNext()) return null;
                    next = walk.Current;
                    why = Check(next, redo);
                    if (why != null) return why;
                }
                while (next is not UndoBatchAction inner || inner.Depth > batch.Depth);
            }
            return null;
        }

        /// <summary>The session's model now lives somewhere else (Save As): every file a step on the timeline owns is handed
        /// to <paramref name="follow"/>, which says where that file is now for THIS model, or null when it is no longer this
        /// model's file (it stayed with the old model, or the new place holds something else). A step follows its file, so
        /// the move is not mistaken for an edit made in another editor, which locked Undo (verifier on m11/sol-fixes-1,
        /// D4); a step that lost its file lets it go, so Undo never writes another model's file and a save of that file
        /// never refuses this session's Undo (VR1, VR1b). Run it on the model's dispatcher, as every other touch of the
        /// timeline is.</summary>
        public static void Rebase(UndoManager undo, Func<string, string> follow)
        {
            if (undo == null || follow == null) return;
            var visited = new HashSet<IUndoAction>();
            void Visit(IUndoAction item)
            {
                if (!visited.Add(item)) return;
                if (item is IInverseUndoAction inverse)
                    foreach (var inner in inverse.ActionsFor(true)) Visit(inner);
                if (item is not SidecarUndoAction s || s.Files == null) return;
                foreach (var f in s.Files)
                    if (f.Path != null) f.Path = follow(f.Path);
            }
            foreach (var field in new[] { UndoStackField, RedoStackField })
            {
                if (field?.GetValue(undo) is not Stack<IUndoAction> stack) continue;
                foreach (var item in stack) Visit(item);
            }
        }
    }

    internal sealed class SidecarUndoAction : IUndoAction
    {
        private readonly string _name;
        private readonly Action _undo, _redo;
        public SidecarUndoAction(string name, IReadOnlyList<SidecarFile> files, Action undo, Action redo) { _name = name; Files = files; _undo = undo; _redo = redo; }
        public IReadOnlyList<SidecarFile> Files { get; }
        public string ActionName => _name;
        public string GetSummary() => _name;
        public void Undo() => _undo();
        public void Redo() => _redo();
    }
}
