using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using TabularEditor.TOMWrapper.Undo;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Makes every edit take back EXACTLY, whatever the vendored wrapper does behind its own undo.
    ///
    /// Why. The wrapper writes some model values straight into TOM and records no undo step for them: making a column the
    /// key clears every sibling's key (Column.cs IsKey), putting a calculation item first numbers its -1 siblings
    /// (CompactLevelOrdinals), and 72 more direct writes sit outside the generated files. Taking such an edit back left
    /// the siblings changed, and a rehearsal (dry run, the Plan's safe-fix rehearsal, a draft rehearsal) is exactly a
    /// rolled-back edit, so each could leave a trace. A list of families does not converge; this is the class fix.
    ///
    /// What this does. It brackets an edit's batch. At Capture it reads the settable scalar and reference values of every
    /// model object and pushes a HEAD step at the bottom of the batch. At Seal it reads them again and keeps what the work
    /// changed but the wrapper's own steps do not account for. Undo walks the batch newest first, so HEAD runs last and
    /// ends the walk at the values the edit found. Redo walks oldest first, so the TAIL step this pushes runs last and
    /// ends at the values the edit left. A rollback is a batch undo, so a rehearsal is exact by the same step. Each step
    /// only sets a value that is not already as it should be, so a value the wrapper restored itself is never touched.
    ///
    /// What it does NOT cover: objects the wrapper makes or drops outside its undo (PerspectiveTableUndo, GroupByUndo and
    /// RefreshPolicyUndo stay per family). <see cref="Unrestorable"/> is the generic detector for those: it names every
    /// object a batch left added or removed that no undo step of the batch accounts for, and tests fail on a non-empty list.
    ///
    /// Lives in Semanticus.Core because the vendored wrapper is compiled in place here and never modified.
    /// </summary>
    public static class ModelSeal
    {
        /// <summary>The switches and findings of ONE model, so a test that turns the seal off (the red proof) or the detector on
        /// cannot reach another test's model running beside it.</summary>
        public sealed class Settings
        {
            /// <summary>Off, Capture and Seal do nothing: the red proof that the sweep names what the seal closes.</summary>
            public bool Enabled { get; set; } = true;
            /// <summary>On, Seal records each unrestorable structure change and each value a step could not put back. Off in
            /// production, where both lists stay empty.</summary>
            public bool Detect { get; set; }
            private readonly object _gate = new();
            private readonly List<string> _unrestorable = new();
            private readonly List<string> _failures = new();
            /// <summary>Objects a batch left added or removed that no undo step of that batch accounts for.</summary>
            public IReadOnlyList<string> Unrestorable { get { lock (_gate) return _unrestorable.ToList(); } }
            /// <summary>Values a seal step tried to put back and could not.</summary>
            public IReadOnlyList<string> Failures { get { lock (_gate) return _failures.ToList(); } }
            private readonly List<string> _sealed = new();
            /// <summary>Each value a seal had to keep (what TE2 wrote outside its undo), as "Kind.Property". For tests and the cost report.</summary>
            public IReadOnlyList<string> Sealed { get { lock (_gate) return _sealed.ToList(); } }
            internal void AddSealed(string s) { lock (_gate) _sealed.Add(s); }
            private int _captures;
            /// <summary>How many whole-model reads <see cref="Capture"/> has taken on this model: the cost a bulk call must pay once, not once per item.</summary>
            public int Captures => System.Threading.Volatile.Read(ref _captures);
            internal void CountCapture() => System.Threading.Interlocked.Increment(ref _captures);
            internal void AddUnrestorable(string s) { lock (_gate) _unrestorable.Add(s); }
            internal void AddFailure(string s) { lock (_gate) _failures.Add(s); }
            public void Clear() { lock (_gate) { _unrestorable.Clear(); _failures.Clear(); _sealed.Clear(); } }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabularModelHandler, Settings> Switches = new();

        public static Settings For(TabularModelHandler h) => Switches.GetOrCreateValue(h);
        public static Settings For(Model model) => For(model.Handler);
        /// <summary>The TOM types the seal reads in a model, with how many objects of each and how many values each carries: the coverage numbers.</summary>
        public static IReadOnlyDictionary<string, (int Objects, int Values)> Coverage(Model model)
        {
            var h = model.Handler;
            var d = new Dictionary<string, (int, int)>();
            Walk(h, Root(h), new HashSet<object>(ReferenceEqualityComparer.Instance), (o, shape, _) =>
            {
                d.TryGetValue(o.GetType().Name, out var c);
                d[o.GetType().Name] = (c.Item1 + 1, shape.Props.Length);
            });
            return d.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2));
        }

        /// <summary>The undo stack as text, for a test that needs to say what a batch left on it.</summary>
        public static string History(Model model) => model.Handler.UndoManager.GetHistory();

        /// <summary>What Capture read: the values, and the HEAD step now on the stack.</summary>
        public sealed class Bracket
        {
            internal Dictionary<MetadataKey, object[]> Values;
            internal HashSet<MetadataKey> Objects;
            internal Dictionary<MetadataKey, MetadataKey> Parents;   // detector mode only
            internal Head HeadStep;
            internal int StackMark;
        }

        // ---- shapes: what to read of each TOM type, worked out once -------------------------------------------------------

        internal sealed class Prop
        {
            public string Name;
            public Func<object, object> Get;
            public Action<object, object> Set;
            public bool IsRef;
        }

        internal sealed class Shape
        {
            public Prop[] Props;
            public Func<object, IEnumerable>[] Collections;
            public (string Name, Func<object, object> Get, bool Owned)[] Singles;
        }

        private static readonly Dictionary<Type, Shape> Shapes = new();

        // Values the server stamps, never the model's own. They would differ on every edit and are not the edit's to restore.
        private static readonly HashSet<string> Stamps = new(StringComparer.Ordinal)
        {
            "ModifiedTime", "StructureModifiedTime", "RefreshedTime", "LastProcessed", "LastUpdate", "LastSchemaUpdate",
            "State", "ErrorMessage", "ObjectType", "Parent",
        };

        // Objects whose Name is the name of what they point at (a perspective's table, a permission's column): it follows the
        // target's own rename, which TE2 undoes on the target. Putting it back here would only fight that.
        private static readonly HashSet<string> NameFollowsTarget = new(StringComparer.Ordinal)
        {
            "PerspectiveTable", "PerspectiveColumn", "PerspectiveMeasure", "PerspectiveHierarchy", "TablePermission", "ColumnPermission",
        };

        private static bool IsScalar(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
                || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid);
        }

        private static Func<object, object> Getter(PropertyInfo p)
        {
            var o = Expression.Parameter(typeof(object));
            var call = Expression.Convert(Expression.Property(Expression.Convert(o, p.DeclaringType), p), typeof(object));
            return Expression.Lambda<Func<object, object>>(call, o).Compile();
        }

        private static Action<object, object> Setter(PropertyInfo p)
        {
            var o = Expression.Parameter(typeof(object));
            var v = Expression.Parameter(typeof(object));
            var assign = Expression.Assign(Expression.Property(Expression.Convert(o, p.DeclaringType), p), Expression.Convert(v, p.PropertyType));
            return Expression.Lambda<Action<object, object>>(assign, o, v).Compile();
        }

        private static Shape ShapeOf(Type t)
        {
            lock (Shapes)
            {
                if (Shapes.TryGetValue(t, out var s)) return s;
                var props = new List<Prop>();
                var collections = new List<Func<object, IEnumerable>>();
                var singles = new List<(string, Func<object, object>, bool)>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0 || !p.CanRead || Stamps.Contains(p.Name)) continue;
                    if (p.Name == "Name" && NameFollowsTarget.Contains(t.Name)) continue;
                    var pt = p.PropertyType;
                    var settable = p.CanWrite && p.SetMethod.IsPublic;
                    if (IsScalar(pt))
                    {
                        if (settable) props.Add(new Prop { Name = p.Name, Get = Getter(p), Set = Setter(p) });
                    }
                    else if (IsValueObject(pt) && settable)
                    {
                        // A value object TOM keeps under its owner without being a MetadataObject (a partition's Source): owned.
                        singles.Add((p.Name, Getter(p), true));
                    }
                    else if (typeof(TOM.MetadataObject).IsAssignableFrom(pt))
                    {
                        // Walked as an owned child when the value's Parent is this object (Partition.Source, Table.RefreshPolicy).
                        var get = Getter(p);
                        // A value object (PartitionSource, KPI) is always owned: its Parent is not trusted to be this object.
                        singles.Add((p.Name, get, !typeof(TOM.NamedMetadataObject).IsAssignableFrom(pt)));
                        // Kept as a VALUE only when it points at a named object that lives elsewhere (Level.Column, a role's table).
                        // An owned value object (FormatStringDefinition, KPI) comes and goes with its owner's own steps and is
                        // structure, not a value to put back: setting it from the old one would reattach a dead object.
                        if (settable && typeof(TOM.NamedMetadataObject).IsAssignableFrom(pt)) props.Add(new Prop { Name = p.Name, Get = get, Set = Setter(p), IsRef = true });
                    }
                    else if (pt != typeof(string) && typeof(IEnumerable).IsAssignableFrom(pt) && ElementIsMetadata(pt))
                    {
                        // Annotations stay out: some are written on purpose WITHOUT an undo step (the audit trail, AuditAnnotations), and
                        // undoing an edit must not take them back. The ones TE2 does track are replayed by AnnotationSteps below.
                        if (p.Name == "Annotations" || p.Name == "ExtendedProperties") continue;
                        // A collection property is only walked on the type that DECLARES it, so an owned list is read once.
                        var get = Getter(p);
                        collections.Add(o => (IEnumerable)get(o));
                    }
                }
                return Shapes[t] = new Shape { Props = props.ToArray(), Collections = collections.ToArray(), Singles = singles.ToArray() };
            }
        }

        private static bool IsValueObject(Type t) =>
            t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t) && !typeof(TOM.MetadataObject).IsAssignableFrom(t)
            && t.Namespace == "Microsoft.AnalysisServices.Tabular" && !typeof(Delegate).IsAssignableFrom(t);

        private static bool ElementIsMetadata(Type t) =>
            t.GetInterfaces().Concat(new[] { t }).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && typeof(TOM.MetadataObject).IsAssignableFrom(i.GetGenericArguments()[0]));

        private static readonly PropertyInfo ParentOf = typeof(TOM.MetadataObject).GetProperty("Parent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static TOM.MetadataObject ParentObject(object o) => o is TOM.MetadataObject m ? ParentOf?.GetValue(m) as TOM.MetadataObject : null;

        /// <summary>Every wrapper object the seal reads, for the tests that sweep a model (how many kinds and values it covers).</summary>
        public static IReadOnlyList<TabularObject> WrapperObjects(Model model)
        {
            var h = model.Handler;
            var list = new List<TabularObject>();
            Walk(h, Root(h), new HashSet<object>(ReferenceEqualityComparer.Instance), (o, _, __) => { if (o is TOM.MetadataObject mo && h.WrapperLookup.TryGetValue(mo, out var w)) list.Add(w); });
            return list;
        }

        // ---- identity: the wrapper where there is one, because it survives a delete and its undo -------------------------

        /// <summary>The thing a recorded value belongs to. TE2 brings a deleted object back as a NEW TOM object under the SAME
        /// wrapper, so a value recorded against the TOM object would point at the dead one after a delete is undone. The
        /// wrapper is what survives, so it is the key wherever the object has one; a TOM object with none (an annotation) is
        /// its own key.</summary>
        internal readonly struct MetadataKey : IEquatable<MetadataKey>
        {
            public readonly object Id;
            public MetadataKey(object id) { Id = id; }
            public bool Equals(MetadataKey other) => Id is OwnedValue a && other.Id is OwnedValue b ? a.Owner.Equals(b.Owner) && a.Member == b.Member : ReferenceEquals(Id, other.Id);
            public override bool Equals(object obj) => obj is MetadataKey k && Equals(k);
            public override int GetHashCode() => Id is OwnedValue a ? HashCode.Combine(a.Owner.GetHashCode(), a.Member) : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Id);
        }

        /// <summary>A value object TOM keeps under its owner and replaces whole (a partition's Source): named by its owner and the
        /// member that holds it, because the object itself is a different instance after the owner's own undo.</summary>
        internal sealed class OwnedValue
        {
            public readonly MetadataKey Owner; public readonly string Member; public readonly Func<object, object> Get;
            public OwnedValue(MetadataKey owner, string member, Func<object, object> get) { Owner = owner; Member = member; Get = get; }
        }

        private static MetadataKey KeyOf(TabularModelHandler h, object o) =>
            new(o is TOM.MetadataObject m && h.WrapperLookup.TryGetValue(m, out var w) ? w : o);

        private static object Resolve(MetadataKey k)
        {
            if (k.Id is OwnedValue ov) { var owner = Resolve(ov.Owner); return owner == null ? null : ov.Get(owner); }
            return k.Id is TabularObject w ? w.MetadataObject : k.Id;
        }

        // ---- reading ---------------------------------------------------------------------------------------------------

        private static void Walk(TabularModelHandler h, object root, HashSet<object> seen, Action<object, Shape, MetadataKey> visit)
        {
            var stack = new Stack<(object, MetadataKey)>();
            stack.Push((root, KeyOf(h, root)));
            while (stack.Count > 0)
            {
                var (o, key) = stack.Pop();
                if (o == null || !seen.Add(o)) continue;
                var shape = ShapeOf(o.GetType());
                visit(o, shape, key);
                foreach (var c in shape.Collections)
                {
                    IEnumerable items;
                    try { items = c(o); } catch { continue; }
                    if (items == null) continue;
                    foreach (var child in items) if (child is TOM.MetadataObject m && ReferenceEquals(ParentObject(m), o)) stack.Push((m, KeyOf(h, m)));
                }
                foreach (var (name, sg, owned) in shape.Singles)
                {
                    object child;
                    try { child = sg(o); } catch { continue; }
                    if (child == null) continue;
                    if (child is TOM.MetadataObject cm) { if (owned || ReferenceEquals(ParentObject(cm), o)) stack.Push((cm, KeyOf(h, cm))); }
                    else stack.Push((child, new MetadataKey(new OwnedValue(key, name, sg))));
                }
            }
        }

        private static object Read(TabularModelHandler h, object o, Prop p)
        {
            object v;
            try { v = p.Get(o); } catch { return Unreadable; }
            return p.IsRef && v is TOM.MetadataObject m ? KeyOf(h, m) : v;
        }

        private static readonly object Unreadable = new();

        private static object Root(TabularModelHandler h) => h.Database.Model;

        // ---- the steps ---------------------------------------------------------------------------------------------------

        internal sealed class Change
        {
            public MetadataKey Key; public Prop Prop; public object Before; public object After;
            // For a value object kept under an owner (a partition's Source): every value it held before and after, so one TOM will
            // not set back (MPartitionSource.Attributes ignores a null or empty write) can be put back by building the object again.
            public Shape Whole; public object[] WholeBefore, WholeAfter;
        }

        private static void Put(TabularModelHandler h, Change c, bool before)
        {
            var key = c.Key; var p = c.Prop; var value = before ? c.Before : c.After;
            try
            {
                var target = Resolve(key);
                if (target == null) return;
                var now = Read(h, target, p);
                if (Same(now, value)) return;   // the wrapper's own step already put it back
                if (For(h).Detect) For(h).AddSealed("put " + target.GetType().Name + "." + p.Name);
                if (value is MetadataKey vk) p.Set(target, Resolve(vk)); else p.Set(target, value);
                if (Same(Read(h, target, p), value) || c.Whole == null || key.Id is not OwnedValue ov) return;
                // TOM took the write and kept the old value: build the object again from what it held.
                var owner = Resolve(ov.Owner);
                var member = owner?.GetType().GetProperty(ov.Member);
                if (member == null || !member.CanWrite) return;
                var fresh = Activator.CreateInstance(target.GetType());
                var vals = before ? c.WholeBefore : c.WholeAfter;
                for (var i = 0; i < c.Whole.Props.Length; i++)
                {
                    var v = vals[i];
                    if (ReferenceEquals(v, Unreadable)) continue;
                    try { if (v is MetadataKey rk) c.Whole.Props[i].Set(fresh, Resolve(rk)); else if (!Same(Read(h, fresh, c.Whole.Props[i]), v)) c.Whole.Props[i].Set(fresh, v); } catch { }
                }
                member.SetValue(owner, fresh);
                if (For(h).Detect) For(h).AddSealed("rebuilt " + target.GetType().Name + " under " + owner.GetType().Name + "." + ov.Member);
            }
            catch (Exception e)
            {
                if (For(h).Detect) For(h).AddFailure(p.Name + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Replay(TabularModelHandler h, IAnnotationObject obj, string name, string value)
        {
            try { if (!string.Equals(obj.GetAnnotation(name), value, StringComparison.Ordinal)) obj.SetAnnotation(name, value); }
            catch (Exception e) { if (For(h).Detect) For(h).AddFailure("annotation " + name + ": " + e.GetType().Name + ": " + e.Message); }
        }

        private static readonly Type AnnotationStep = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoAnnotationAction");
        private static readonly FieldInfo[] AnnotationFields = new[] { "Obj", "name", "oldValue", "newValue" }
            .Select(n => AnnotationStep?.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)).ToArray();

        /// <summary>The annotation steps TE2 recorded since HEAD went on, newest first. A property's own hook can undo one of them
        /// again while the batch is taken back, so they are replayed last (see <see cref="Head"/>).</summary>
        private static List<(IAnnotationObject, string, string, string)> AnnotationSteps(Stack<IUndoAction> stack, IUndoAction head)
        {
            var list = new List<(IAnnotationObject, string, string, string)>();
            if (AnnotationFields.Any(f => f == null)) return list;
            foreach (var a in stack)
            {
                if (ReferenceEquals(a, head)) break;
                if (a.GetType() != AnnotationStep) continue;
                if (AnnotationFields[0].GetValue(a) is IAnnotationObject obj)
                    list.Add((obj, (string)AnnotationFields[1].GetValue(a), (string)AnnotationFields[2].GetValue(a), (string)AnnotationFields[3].GetValue(a)));
            }
            return list;
        }

        private static bool Same(object a, object b) => a is MetadataKey ka && b is MetadataKey kb ? ka.Equals(kb) : Equals(a, b);

        internal sealed class Head : IUndoAction
        {
            private readonly TabularModelHandler _h;
            public List<Change> Changes = new();
            public List<(IAnnotationObject Obj, string Name, string Old, string New)> Annotations = new();   // newest first
            public Head(TabularModelHandler h) { _h = h; }
            public string ActionName => "restore values";
            public string GetSummary() => "restore values the wrapper writes outside its undo";
            public void Redo() { }
            public void Undo()
            {
                foreach (var c in Changes) Put(_h, c, before: true);
                // Last of all, so a hook the wrapper re-runs on undo (FormatString drops the "Format" annotation again) cannot win.
                foreach (var a in Annotations) Replay(_h, a.Obj, a.Name, a.Old);
            }
        }

        internal sealed class Tail : IUndoAction
        {
            private readonly TabularModelHandler _h;
            private readonly List<Change> _changes;
            private readonly List<(IAnnotationObject Obj, string Name, string Old, string New)> _annotations;
            public Tail(TabularModelHandler h, List<Change> changes, List<(IAnnotationObject, string, string, string)> annotations) { _h = h; _changes = changes; _annotations = annotations; }
            public string ActionName => "restore values";
            public string GetSummary() => "restore values the wrapper writes outside its undo";
            public void Undo() { }
            public void Redo()
            {
                foreach (var c in _changes) Put(_h, c, before: false);
                foreach (var a in Enumerable.Reverse(_annotations)) Replay(_h, a.Obj, a.Name, a.New);
            }
        }

        private static readonly FieldInfo UndoStackField = typeof(UndoManager).GetField("_UndoStack", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Stack<IUndoAction> StackOf(UndoManager u) => UndoStackField?.GetValue(u) as Stack<IUndoAction>;

        // ---- the bracket -------------------------------------------------------------------------------------------------

        /// <summary>Read every object's values and put the HEAD step at the bottom of the batch the caller has just opened.
        /// Returns null when sealing is off or the undo manager is not recording.</summary>
        public static Bracket Capture(TabularModelHandler h)
        {
            if (h?.Database == null || !For(h).Enabled) return null;
            var um = h.UndoManager;
            var stack = StackOf(um);
            if (stack == null || !um.Enabled || um.UndoInProgress) return null;
            For(h).CountCapture();
            var b = new Bracket { Values = new(), Objects = new(), HeadStep = new Head(h), Parents = For(h).Detect ? new() : null };
            Walk(h, Root(h), new HashSet<object>(ReferenceEqualityComparer.Instance), (o, shape, key) =>
            {
                b.Objects.Add(key);
                if (b.Parents != null && ParentObject(o) is { } po) b.Parents[key] = KeyOf(h, po);
                if (shape.Props.Length == 0) return;
                var vals = new object[shape.Props.Length];
                for (var i = 0; i < vals.Length; i++) vals[i] = Read(h, o, shape.Props[i]);
                b.Values[key] = vals;
            });
            // Raw push: UndoManager.Add would clear the redo stack for a batch that may change nothing.
            stack.Push(b.HeadStep);
            b.StackMark = stack.Count;
            return b;
        }

        /// <summary>Read the values again, keep what the work changed and the wrapper's own steps do not account for, and
        /// close the bracket: the HEAD step is filled and a TAIL step pushed, or HEAD is taken off when there is nothing to
        /// seal. Call inside the batch, after the work and before the batch closes, and ALSO when the work threw, so that
        /// the rollback which follows is exact.</summary>
        public static void Seal(TabularModelHandler h, Bracket b)
        {
            if (b == null) return;
            var um = h.UndoManager;
            var stack = StackOf(um);
            if (stack == null) return;
            var tracked = TrackedSince(stack, b.HeadStep);
            var touched = TouchedSince(stack, b.HeadStep);
            var changes = new List<Change>();
            var after = new HashSet<MetadataKey>();
            var afterParents = b.Parents != null ? new Dictionary<MetadataKey, MetadataKey>() : null;
            Walk(h, Root(h), new HashSet<object>(ReferenceEqualityComparer.Instance), (o, shape, key) =>
            {
                after.Add(key);
                if (afterParents != null && ParentObject(o) is { } po) afterParents[key] = KeyOf(h, po);
                if (shape.Props.Length == 0 || !b.Values.TryGetValue(key, out var was)) return;
                object[] nowAll = null;
                for (var i = 0; i < was.Length; i++)
                {
                    var now = Read(h, o, shape.Props[i]);
                    if (ReferenceEquals(was[i], Unreadable) || ReferenceEquals(now, Unreadable)) continue;
                    // A value the batch's own steps rewrite when taken back or redone is kept even when it ends where it began: TE2's
                    // undo of such a step does not restore what the batch found (see TouchedSince), so only this step can.
                    var isTouched = touched.Touches(o, key, shape.Props[i].Name, was[i], now);
                    if (Same(was[i], now) && !isTouched) continue;
                    if (!isTouched && tracked.Contains((key, shape.Props[i].Name))) continue;   // TE2 has a step for it
                    var change = new Change { Key = key, Prop = shape.Props[i], Before = was[i], After = now };
                    if (key.Id is OwnedValue && shape.Collections.Length == 0 && shape.Singles.All(x => !x.Owned))
                    {
                        nowAll ??= shape.Props.Select(pr => Read(h, o, pr)).ToArray();
                        change.Whole = shape; change.WholeBefore = was; change.WholeAfter = nowAll;
                    }
                    changes.Add(change);
                }
            });
            if (For(h).Detect) DetectStructure(h, b, after, afterParents, tracked, stack);
            var annotations = AnnotationSteps(stack, b.HeadStep);
            if (changes.Count == 0 && annotations.Count == 0) { Remove(stack, b.HeadStep); return; }
            if (For(h).Detect)
            {
                foreach (var c in changes) For(h).AddSealed(Resolve(c.Key)?.GetType().Name + "." + c.Prop.Name);
                foreach (var a in annotations) For(h).AddSealed("annotation " + a.Item2);
            }
            b.HeadStep.Changes = changes;
            b.HeadStep.Annotations = annotations;
            // The wrapper's own step for the work may already be on the stack above HEAD; TAIL goes on top of all of it.
            um.Add(new Tail(h, changes, annotations));
        }

        /// <summary>Close a bracket without reading the model again: its HEAD step is taken off and nothing is kept. Only for a
        /// bracket nested inside another one that is sealed later, which then keeps everything the work did (a bulk call's items
        /// inside the call's own commit). A bracket that must take its work back is sealed instead.</summary>
        public static void Release(TabularModelHandler h, Bracket b)
        {
            if (b == null) return;
            var stack = StackOf(h.UndoManager);
            if (stack != null) Remove(stack, b.HeadStep);
        }

        private static void Remove(Stack<IUndoAction> stack, IUndoAction step)
        {
            var above = new List<IUndoAction>();
            while (stack.Count > 0)
            {
                var a = stack.Pop();
                if (ReferenceEquals(a, step)) break;
                above.Add(a);
            }
            for (var i = above.Count - 1; i >= 0; i--) stack.Push(above[i]);
        }

        private static readonly FieldInfo PropChangedObject = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoPropertyChangedAction")?.GetField("tabularObject", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PropChangedProp = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoPropertyChangedAction")?.GetField("prop", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PropChangedIndex = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoPropertyChangedAction")?.GetField("index", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The (object, property) pairs TE2 recorded a property step for since HEAD went on. Whatever a step here names
        /// is put back by TE2 itself, so sealing it too would only add a step to every ordinary edit.</summary>
        private static HashSet<(MetadataKey, string)> TrackedSince(Stack<IUndoAction> stack, IUndoAction head)
        {
            var set = new HashSet<(MetadataKey, string)>();
            if (PropChangedObject == null || PropChangedProp == null || PropChangedIndex == null) return set;
            foreach (var a in stack)
            {
                if (ReferenceEquals(a, head)) break;
                if (a.GetType().Name != "UndoPropertyChangedAction") continue;
                if (PropChangedIndex.GetValue(a) != null) continue;
                if (PropChangedObject.GetValue(a) is not TabularObject w || PropChangedProp.GetValue(a) is not PropertyInfo p) continue;
                set.Add((new MetadataKey(w), p.Name));
                if (TomTwin(w, p.Name) is { } twin) set.Add(twin);
            }
            return set;
        }

        /// <summary>The TOM value a wrapper property step writes when that value sits on another TOM object than the wrapper's own. A
        /// culture's Content lives on its LinguisticMetadata (Culture.cs): read under that object's key, the text looked untracked, so
        /// every synonym write kept a second full copy of the schema in its seal step. Matched here, TE2's step (packed, SchemaUndo)
        /// puts the text back, and a write outside undo still shows as touched (its chain disagrees) and is kept.</summary>
        private static (MetadataKey, string)? TomTwin(TabularObject w, string prop) =>
            prop == nameof(Culture.Content) && w is Culture && !w.IsRemoved && (w.MetadataObject as TOM.Culture)?.LinguisticMetadata is { } lm
                ? (new MetadataKey(lm), nameof(TOM.LinguisticMetadata.Content)) : null;

        // ---- values the batch's steps rewrite ------------------------------------------------------------------------------------

        /// <summary>Which values a batch's own undo and redo steps WRITE, so the seal keeps them even when the batch ends where it began.
        /// The seal used to keep only a value whose end differed from its start and that TE2 has no step for. That assumed TE2's undo
        /// puts back the value the batch found. It does not always: setting B as the key clears A's key straight in TOM, so when the
        /// same batch then makes A the key, TE2 records A as "false, then true", and taking the batch back leaves neither the key. A
        /// calculation item's order step takes the batch back by numbering every item 0, 1, 2 even when they were all -1. Neither
        /// shows as a net change, so neither was kept. Here "touched" is what a step rewrites, not what ended up different:
        /// <list type="bullet">
        /// <item>a property step whose recorded old value is not what the batch found, or whose new value is not where it ended;</item>
        /// <item>the siblings of a property that clears its siblings when set (<see cref="SiblingWriters"/>), whatever TE2 recorded;</item>
        /// <item>every item or level of a group or hierarchy that has an order step, because that step renumbers all of them.</item>
        /// </list>
        /// A step TE2 undoes correctly still adds nothing, so an ordinary edit keeps its stack shape.</summary>
        internal sealed class Touched
        {
            internal readonly Dictionary<(MetadataKey, string), (object FirstOld, object LastNew)> Chains = new();
            internal readonly Dictionary<object, HashSet<string>> Siblings = new(ReferenceEqualityComparer.Instance);
            internal void Sibling(object parent, string prop)
            {
                if (parent == null) return;
                if (!Siblings.TryGetValue(parent, out var set)) Siblings[parent] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(prop);
            }

            internal bool Touches(object o, MetadataKey key, string prop, object was, object now)
            {
                if (Siblings.Count > 0 && ParentObject(o) is { } parent && Siblings.TryGetValue(parent, out var props) && props.Contains(prop)) return true;
                if (!Chains.TryGetValue((key, prop), out var c)) return false;
                return !Agrees(c.FirstOld, was) || !Agrees(c.LastNew, now);
            }

            // Only a plain value can be compared with what the seal read: a reference TE2 recorded as a wrapper is another shape.
            private static bool Agrees(object recorded, object read) => recorded is TabularObject || read is MetadataKey || Equals(recorded, read);
        }

        /// <summary>Properties that clear the same property on every sibling when set, straight in TOM and with no step (Column.cs, IsKey).</summary>
        private static readonly HashSet<string> SiblingWriters = new(StringComparer.Ordinal) { "IsKey" };

        private static readonly Type PropStepType = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoPropertyChangedAction");
        private static readonly FieldInfo PropChangedOld = PropStepType?.GetField("oldValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PropChangedNew = PropStepType?.GetField("newValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ItemsOrderGroup = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoCalculationItemsOrderAction")?.GetField("calculationGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LevelsOrderHierarchy = typeof(UndoManager).Assembly.GetType("TabularEditor.TOMWrapper.Undo.UndoHierarchyLevelOrderAction")?.GetField("hierarchy", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Touched TouchedSince(Stack<IUndoAction> stack, IUndoAction head)
        {
            var t = new Touched();
            // The stack is newest first, so the last write seen per pair is the OLDEST step: its old value is where TE2's undo ends.
            foreach (var a in stack)
            {
                if (ReferenceEquals(a, head)) break;
                var n = a.GetType().Name;
                try
                {
                    if (n == "UndoPropertyChangedAction")
                    {
                        if (PropChangedIndex?.GetValue(a) != null || PropChangedObject?.GetValue(a) is not TabularObject w || PropChangedProp?.GetValue(a) is not PropertyInfo p) continue;
                        var k = (new MetadataKey(w), p.Name);
                        var newest = t.Chains.TryGetValue(k, out var c) ? c.LastNew : PropChangedNew?.GetValue(a);
                        t.Chains[k] = (PropChangedOld?.GetValue(a), newest);
                        if (TomTwin(w, p.Name) is { } twin) t.Chains[twin] = t.Chains[k];
                        if (SiblingWriters.Contains(p.Name) && !w.IsRemoved) t.Sibling(ParentObject(w.MetadataObject), p.Name);
                    }
                    else if (n == "UndoCalculationItemsOrderAction" && ItemsOrderGroup?.GetValue(a) is CalculationGroupTable g)
                        t.Sibling((g.MetadataObject as TOM.Table)?.CalculationGroup, "Ordinal");
                    else if (n == "UndoHierarchyLevelOrderAction" && LevelsOrderHierarchy?.GetValue(a) is Hierarchy hi)
                        t.Sibling(hi.MetadataObject, "Ordinal");
                }
                catch { /* a step that cannot be read adds nothing: the seal then behaves as it did before */ }
            }
            return t;
        }

        // ---- the detector --------------------------------------------------------------------------------------------------

        /// <summary>An object the batch added or removed that nothing in the batch accounts for cannot be taken back. It is
        /// accounted for by a TE2 add or remove step naming it, by a step on a property of the object that owns it (setting
        /// FormatStringExpression makes and drops the FormatStringDefinition under it), by a per-family step of this codebase
        /// (a perspective's table), or by being under an object that is itself in one of those. The rest are recorded for
        /// tests to fail on.</summary>
        private static void DetectStructure(TabularModelHandler h, Bracket b, HashSet<MetadataKey> after, Dictionary<MetadataKey, MetadataKey> afterParents,
            HashSet<(MetadataKey, string)> tracked, Stack<IUndoAction> stack)
        {
            var explained = new HashSet<MetadataKey>();
            var perFamily = false;
            foreach (var a in stack)
            {
                if (ReferenceEquals(a, b.HeadStep)) break;
                if (a is SidecarUndoAction) perFamily = true;
                var n = a.GetType().Name;
                if ((n == "UndoAddRemoveAction" || n == "UndoAddRemoveReferenceAction") && a.GetType().GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(a) is { } o)
                    explained.Add(new MetadataKey(o));
            }
            var owners = new HashSet<MetadataKey>(tracked.Select(t => t.Item1));
            var gone = b.Objects.Where(k => !after.Contains(k)).ToList();
            var made = after.Where(k => !b.Objects.Contains(k)).ToList();
            var moved = new HashSet<MetadataKey>(gone.Concat(made));
            var explainedTom = new HashSet<object>(explained.Select(Resolve).Where(x => x != null), ReferenceEqualityComparer.Instance);
            var movedTom = new HashSet<object>(moved.Select(Resolve).Where(x => x != null), ReferenceEqualityComparer.Instance);
            bool Accounted(MetadataKey k, Dictionary<MetadataKey, MetadataKey> parents, bool self)
            {
                if (explained.Contains(k)) return true;
                if (owners.Contains(k) && !self) return true;
                if (perFamily && Resolve(k)?.GetType().Name is "PerspectiveTable") return true;
                if (!self && moved.Contains(k)) return true;   // reported on its own
                if (k.Id is OwnedValue ov) return Accounted(ov.Owner, parents, false);   // a value object goes with its owner
                // A translation, or a perspective's entry for a measure, column or hierarchy, is made and dropped with the object it
                // points at (TE2 gives a new measure a translation in each culture, and takes it out of every perspective on delete).
                if (self && PointsAt(Resolve(k)) is { } pointed && (explainedTom.Contains(pointed) || movedTom.Contains(pointed))) return true;
                return parents != null && parents.TryGetValue(k, out var parent) && Accounted(parent, parents, false);
            }
            var settings = For(h);
            foreach (var k in gone) if (!Accounted(k, b.Parents, true)) settings.AddUnrestorable("removed with no undo step: " + Describe(k));
            foreach (var k in made) if (!Accounted(k, afterParents, true)) settings.AddUnrestorable("added with no undo step: " + Describe(k));
        }

        private static object PointsAt(object o)
        {
            try
            {
                if (o is TOM.ObjectTranslation tr) return tr.Object;
                var n = o?.GetType().Name;
                if (n is "PerspectiveColumn" or "PerspectiveMeasure" or "PerspectiveHierarchy")
                    return o.GetType().GetProperty(n.Substring("Perspective".Length))?.GetValue(o);
            }
            catch { }
            return null;
        }

        private static string Describe(MetadataKey k)
        {
            var o = Resolve(k);
            var name = (o as TOM.NamedMetadataObject)?.Name;
            var parent = o is TOM.MetadataObject m ? ParentObject(m) : null;
            return o.GetType().Name + (name != null ? " " + name : "") + (parent is TOM.NamedMetadataObject pn ? " in " + pn.Name : "");
        }
    }
}
