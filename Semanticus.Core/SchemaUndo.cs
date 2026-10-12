using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TabularEditor.TOMWrapper.Undo;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Keeps the undo step of a linguistic schema write small.
    ///
    /// Why. TE2 records a write to a culture's Content as one property step holding the whole old text and the whole new
    /// text. A synonym write rewrites the whole schema, so every write kept a full copy on the undo stack: 807 KB a write
    /// on SpaceParts, 97 MB for 120 writes, and about 900 MB over a long session on a real model.
    ///
    /// What this does. Once a commit is sealed, each such step of the commit is swapped for one that holds the same two
    /// texts packed (Brotli over the UTF-16 bytes, so every string comes back exactly, a lone surrogate included; a text
    /// that does not come back equal is left as TE2's step). Its Undo and Redo set the property exactly as TE2's step does,
    /// to a text the step holds itself. It never reads the live Content and never stores a difference against it, so a
    /// write made outside undo cannot make it put back the wrong text: an undo after such a write restores the old text,
    /// as TE2's own step would. Steps next to each other share the packed bytes of the text they have in common.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public static class SchemaUndo
    {
        private static readonly FieldInfo UndoStackField = typeof(UndoManager).GetField("_UndoStack", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OldField = typeof(UndoPropertyChangedAction).GetField("oldValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NewField = typeof(UndoPropertyChangedAction).GetField("newValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ObjectField = typeof(UndoPropertyChangedAction).GetField("tabularObject", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PropField = typeof(UndoPropertyChangedAction).GetField("prop", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo IndexField = typeof(UndoPropertyChangedAction).GetField("index", BindingFlags.Instance | BindingFlags.NonPublic);

        // The packed bytes of a text a step holds, so the next step's old text (the same string) is not packed twice. Weak: an
        // entry goes when its text does.
        private static readonly ConditionalWeakTable<string, byte[]> Packed = new();

        /// <summary>Swap each full schema step pushed since the undo stack held <paramref name="undoSizeBefore"/> steps for a
        /// packed one. Call once a commit is sealed and before its batch closes, at the outermost batch only. A step that
        /// cannot be read or packed stays as it is; nothing is swapped until every packed text has been read back equal.</summary>
        public static int Pack(TabularModelHandler h, int undoSizeBefore)
        {
            if (h?.UndoManager == null || UndoStackField?.GetValue(h.UndoManager) is not Stack<IUndoAction> stack) return 0;
            if (OldField == null || NewField == null || ObjectField == null || PropField == null || IndexField == null) return 0;
            var fresh = stack.Count - undoSizeBefore;
            if (fresh <= 0) return 0;
            var top = new List<IUndoAction>(fresh);
            foreach (var a in stack) { if (top.Count == fresh) break; top.Add(a); }   // newest first
            var swaps = new Dictionary<int, IUndoAction>();
            for (var i = top.Count - 1; i >= 0; i--)   // oldest first, so a step's old text finds the bytes of the one before
            {
                if (top[i] is not UndoPropertyChangedAction step) continue;
                if (IndexField.GetValue(step) != null || ObjectField.GetValue(step) is not Culture culture) continue;
                if (PropField.GetValue(step) is not PropertyInfo prop || prop.Name != nameof(Culture.Content)) continue;
                var was = OldField.GetValue(step) as string;
                var now = NewField.GetValue(step) as string;
                if (!TryPack(was, out var oldBytes) || !TryPack(now, out var newBytes)) continue;
                swaps[i] = new PackedTextStep(culture, prop, step.ActionName, oldBytes, was?.Length ?? 0, newBytes, now?.Length ?? 0);
            }
            if (swaps.Count == 0) return 0;
            for (var i = 0; i < top.Count; i++) stack.Pop();
            for (var i = top.Count - 1; i >= 0; i--) stack.Push(swaps.TryGetValue(i, out var swap) ? swap : top[i]);
            return swaps.Count;
        }

        /// <summary>The schema steps on the undo and redo stacks: how many still hold full texts, the bytes the packed ones hold, and
        /// the bytes their texts would take in full (UTF-16, each text once). For tests and the cost report.</summary>
        public static (int FullSteps, int PackedSteps, long PackedBytes, long TextBytes) Held(Model model)
        {
            var full = 0; var packed = 0; long bytes = 0, text = 0;
            var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            foreach (var name in new[] { "_UndoStack", "_RedoStack" })
            {
                if (typeof(UndoManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(model.Handler.UndoManager) is not Stack<IUndoAction> stack) continue;
                foreach (var a in stack)
                {
                    if (a is PackedTextStep p)
                    {
                        packed++;
                        if (p.Old != null && seen.Add(p.Old)) { bytes += p.Old.Length; text += 2L * p.OldLength; }
                        if (p.New != null && seen.Add(p.New)) { bytes += p.New.Length; text += 2L * p.NewLength; }
                    }
                    else if (a is UndoPropertyChangedAction && ObjectField?.GetValue(a) is Culture && (PropField?.GetValue(a) as PropertyInfo)?.Name == nameof(Culture.Content)) full++;
                }
            }
            return (full, packed, bytes, text);
        }

        /// <summary>A text as Brotli over its UTF-16 bytes; null stays null.</summary>
        public static byte[] PackText(string text)
        {
            if (text == null) return null;
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                brotli.Write(MemoryMarshal.AsBytes(text.AsSpan()));
            return output.ToArray();
        }

        public static string UnpackText(byte[] packed)
        {
            if (packed == null) return null;
            using var input = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            input.CopyTo(raw);
            return new string(MemoryMarshal.Cast<byte, char>(raw.GetBuffer().AsSpan(0, (int)raw.Length)));
        }

        private static bool TryPack(string text, out byte[] packed)
        {
            packed = null;
            if (text == null) return true;
            if (Packed.TryGetValue(text, out packed)) return true;
            try
            {
                packed = PackText(text);
                if (!string.Equals(UnpackText(packed), text, StringComparison.Ordinal)) { packed = null; return false; }
            }
            catch { packed = null; return false; }
            Packed.AddOrUpdate(text, packed);
            return true;
        }

        /// <summary>TE2's property step for one text, with both texts packed. Undo and Redo set the property as TE2's step does.</summary>
        internal sealed class PackedTextStep : IUndoAction
        {
            private readonly TabularObject _obj;
            private readonly PropertyInfo _prop;
            internal readonly int OldLength, NewLength;
            internal readonly byte[] Old, New;
            public PackedTextStep(TabularObject obj, PropertyInfo prop, string actionName, byte[] old, int oldLength, byte[] @new, int newLength)
            {
                _obj = obj; _prop = prop; ActionName = actionName; Old = old; OldLength = oldLength; New = @new; NewLength = newLength;
            }
            public string ActionName { get; }
            public string GetSummary() => $"Set Property {{{_obj.GetObjectPath()}.{_prop.Name}}}: {OldLength} characters --> {NewLength} characters";
            public void Undo() => Put(Old);
            public void Redo() => Put(New);
            private void Put(byte[] packed)
            {
                var text = UnpackText(packed);
                _prop.SetValue(_obj, text);
                // The text now in the model is this string: the next write's step finds its bytes instead of packing it again.
                if (text != null) Packed.AddOrUpdate(text, packed);
            }
        }
    }
}
