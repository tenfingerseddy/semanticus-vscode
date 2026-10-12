using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TabularEditor.TOMWrapper.Undo;

namespace TabularEditor.TOMWrapper.Utils
{
    internal interface IInverseUndoAction
    {
        IEnumerable<IUndoAction> ActionsFor(bool redo);
    }

    /// <summary>Reverse selected complete steps as a new, reversible edit. The original stack stays intact.</summary>
    public static class AssistantUndo
    {
        private static readonly FieldInfo StackField = typeof(UndoManager).GetField("_UndoStack", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PlayingField = typeof(UndoManager).GetField("inProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo Undoing = typeof(UndoManager).GetProperty("IsUndoing");
        private static readonly PropertyInfo Redoing = typeof(UndoManager).GetProperty("IsRedoing");

        // Called before the session opens its mutation batch.
        public static Action Prepare(Model model, int first, int last, int steps)
        {
            var manager = model.Handler.UndoManager;
            var stack = StackField?.GetValue(manager) as Stack<IUndoAction>
                ?? throw new InvalidOperationException("The undo history cannot be read.");
            var actions = new List<IUndoAction>();
            var step = steps;
            var depth = 0;
            // The caller prepares before opening the new batch, so every marker belongs to an existing step.
            foreach (var action in stack)
            {
                if (step >= first && step <= last) actions.Add(action);
                if (action is UndoBatchAction batch) depth += batch.Begin ? 1 : -1;
                if (depth == 0) step--;
            }
            if (actions.Count == 0 || first < 1 || last > steps || first > last)
                throw new InvalidOperationException("That change is no longer on the undo history.");
            var inverse = new Inverse(manager, actions.ToArray());
            return () =>
            {
                inverse.Redo();
                manager.Add(inverse);
            };
        }

        private sealed class Inverse : IUndoAction, IInverseUndoAction
        {
            private readonly UndoManager _manager;
            private readonly IUndoAction[] _actions;
            public Inverse(UndoManager manager, IUndoAction[] actions) { _manager = manager; _actions = actions; }
            public string ActionName => "Undo assistant changes";
            public string GetSummary() => ActionName;
            public void Undo() => Play(reverse: false);
            public void Redo() => Play(reverse: true);
            public IEnumerable<IUndoAction> ActionsFor(bool redo) => redo ? _actions : _actions.Reverse();
            private void Play(bool reverse)
            {
                var ordered = reverse ? _actions : _actions.Reverse().ToArray();
                var done = new List<IUndoAction>();
                var playing = _manager.UndoInProgress;
                var undoing = _manager.IsUndoing;
                var redoing = _manager.IsRedoing;
                // Wrapper hooks must see the same replay flags as an ordinary Undo or Redo.
                PlayingField.SetValue(_manager, true);
                Undoing.SetValue(_manager, reverse);
                Redoing.SetValue(_manager, !reverse);
                _manager.Suspend();
                try
                {
                    foreach (var action in ordered)
                    {
                        if (reverse) action.Undo(); else action.Redo();
                        done.Add(action);
                    }
                }
                catch
                {
                    foreach (var action in done.AsEnumerable().Reverse())
                        if (reverse) action.Redo(); else action.Undo();
                    throw;
                }
                finally
                {
                    _manager.Resume();
                    PlayingField.SetValue(_manager, playing);
                    Undoing.SetValue(_manager, undoing);
                    Redoing.SetValue(_manager, redoing);
                }
            }
        }
    }
}
