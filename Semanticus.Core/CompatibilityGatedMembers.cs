using System;
using System.Reflection;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Keeps the wrapper's level-gated members in step with a compatibility level raised inside a live session.
    ///
    /// The vendored wrapper builds its object-level-security indexers (<see cref="ModelRole.MetadataPermission"/>,
    /// <see cref="Table.ObjectLevelSecurity"/>, <see cref="Column.ObjectLevelSecurity"/>) only in each object's
    /// <c>Init</c>, and only when the level is already 1400 or higher. Nothing re-runs that when
    /// <see cref="Database.CompatibilityLevel"/> is raised later, so every role, table and column that existed
    /// before the raise keeps a null indexer while the level-gated code paths (<c>Table.DeleteLinkedObjects</c>,
    /// <c>Column.DeleteLinkedObjects</c>, <c>TableOLSIndexer.SetValue</c>) now run and dereference it. Undoing a
    /// table add on a raised model with roles threw a NullReferenceException for exactly this reason (found by
    /// lane C1, 24 Sep).
    ///
    /// <see cref="Track"/> hooks the handler's change firehose, which the level setter raises on every path: an
    /// engine op, a batch that raises the level and then fails and rolls back, and undo or redo. The fill-in is
    /// what <c>Init</c> would have built at the new level, it touches no TOM metadata and records no undo action,
    /// and it is a no-op below 1400 (a lowered level leaves the extra indexers behind, where the level gates keep
    /// them unread, the same state a model loaded at 1400 and then lowered would be in).
    ///
    /// Lives in Semanticus.Core because the indexer constructors are internal to this assembly (the vendored
    /// TOMWrapper is compiled in place here; the submodule is never modified). Two of the three setters are
    /// private, so they are reached by reflection, and a vendored rename fails loudly rather than silently.
    /// </summary>
    public static class CompatibilityGatedMembers
    {
        private const int ObjectLevelSecurityLevel = 1400;

        private static readonly MethodInfo RoleMetadataPermissionSetter = PrivateSetter(typeof(ModelRole), nameof(ModelRole.MetadataPermission));
        private static readonly MethodInfo ColumnObjectLevelSecuritySetter = PrivateSetter(typeof(Column), nameof(Column.ObjectLevelSecurity));

        /// <summary>Fill the gated members whenever this handler's compatibility level changes.</summary>
        public static void Track(TabularModelHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            handler.ObjectChanged += OnObjectChanged;
        }

        private static void OnObjectChanged(object sender, ObjectChangedEventArgs e)
        {
            if (e.TabularObject is Database && e.PropertyName == nameof(Database.CompatibilityLevel) && sender is TabularModelHandler h)
                Ensure(h.Model);
        }

        /// <summary>Build any level-gated member the current level calls for and the object does not have yet.
        /// Idempotent: an object that already has its member keeps it.</summary>
        public static void Ensure(Model model)
        {
            if (model?.Handler == null || model.Handler.CompatibilityLevel < ObjectLevelSecurityLevel) return;

            foreach (var role in model.Roles)
                if (role.MetadataPermission == null)
                    RoleMetadataPermissionSetter.Invoke(role, new object[] { new RoleOLSIndexer(role) });

            foreach (var table in model.Tables)
            {
                if (table.ObjectLevelSecurity == null) table.ObjectLevelSecurity = new TableOLSIndexer(table);
                foreach (var column in table.Columns)
                    if (column.ObjectLevelSecurity == null)
                        ColumnObjectLevelSecuritySetter.Invoke(column, new object[] { new ColumnOLSIndexer(column) });
            }
        }

        private static MethodInfo PrivateSetter(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"The vendored {type.Name}.{property} has no setter; the level-raise fill-in needs updating.");
    }
}
