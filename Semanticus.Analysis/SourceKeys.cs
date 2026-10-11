using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// The key a spec declares for a table, kept on EVERY column of it. TOM holds IsKey on at most one column per table
    /// (setting it clears it on the others, <c>Column.OnPropertyChanged</c> in the vendored wrapper), so a two-part key
    /// such as (CODE_TYPE, CODE) that the SQL draft hides ends with its first part hidden, unreferenced and not IsKey,
    /// and reference counting cannot tell it from a column nothing needs. The spec build writes this annotation on each
    /// column the spec marks as a key, and every rule that asks "is this column a key?" reads it beside IsKey:
    /// BPA's UNNECESSARY_COLUMNS in its expression (<c>HasAnnotation("Semanticus_SourceKey")</c>, so keep the name in step
    /// with BPARules-PowerBI.json) and VIS-TECH-COLUMN's removal advice. It travels with the model like any annotation.
    /// A key TOM cannot see on a model built elsewhere is still invisible: nothing in the model records it.
    /// </summary>
    public static class SourceKeys
    {
        public const string Annotation = "Semanticus_SourceKey";

        public static void Mark(Column c) => c.SetAnnotation(Annotation, "true");

        public static bool IsDeclaredKey(Column c) => c != null && (c.IsKey || c.HasAnnotation(Annotation));
    }
}
