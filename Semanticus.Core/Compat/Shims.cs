// Semanticus.Core compatibility shims.
//
// Adapted from TE2's BPALib.Shims.cs. These supply empty/minimal stand-ins for the WinForms
// PropertyGrid converter/editor zoo (referenced by attributes baked into the generated TOM
// wrappers) plus a few app-side types the engine references, so the headless engine compiles
// without System.Windows.Forms.
//
// DELTAS vs BPALib.Shims.cs:
//   * REMOVED the no-op `partial TabularModelHandler { DoObject* }` block — Semanticus.Core
//     includes the REAL TabularModelHandler.Events.cs (the change-event firehose), so providing
//     the no-ops here would be a duplicate definition.
//   * REMOVED the `Program.testRun` field (the NUnit `TestRun` type is excluded in M0).
//   * FormulaFixup is stubbed separately in Compat\FormulaFixupStub.cs.

using System.Collections.Generic;
using System.Linq;
using System.Globalization;

#if NETSTANDARD || NETFRAMEWORK
using System.ComponentModel;
namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif

namespace TabularEditor
{
    internal static class Program
    {
        // NOTE: BPALib's shim had `internal static TestRun testRun;` here; dropped for M0
        // because the NUnit test path (which defines TestRun) is excluded.
    }

    internal class ColumnSelectConverter { }
    internal class NoEditor { }

    interface IDropDownProperties
    {
        string[] GetDropDownItems(string propertyName);
    }
}

namespace TabularEditor.PropertyGridUI
{
    namespace CollectionEditors { }

    internal class AnnotationCollectionEditor { }
    internal class CalculationItemCollectionEditor { }
    internal class CultureCollectionEditor { }
    internal class DataCoverageDefinitionEditor { }
    internal class AlternateOfEditor { }
    internal class VariationCollectionEditor { }
    internal class ExtendedPropertyCollectionEditor { }
    internal class CustomDialogEditor { }
    internal class ColumnSetCollectionEditor { }
    internal class ConnectionAddressPropertyCollectionEditor { }
    internal class CredentialPropertyCollectionEditor { }
    internal class DataSourceOptionsPropertyCollectionEditor { }
    internal class KpiEditor { }
    internal class PartitionCollectionEditor { }
    // Newer collection editors (Calendar feature) that derive from WinForms CollectionEditor in TE2;
    // stubbed here so the generated wrapper attributes that typeof() them compile headless.
    internal class BindingInfoCollectionEditor { }
    internal class CalendarCollectionEditor { }
    internal class CalendarColumnGroupCollectionEditor { }
    internal class RoleMemberCollectionEditor { }
    internal class SetCollectionEditor { }
    internal class ClonableObjectCollectionEditor<T> { }

    internal class AllColumnConverter { }
    internal class AllOtherTablesColumnConverter { }
    internal class AllHierarchyConverter { }
    internal class AllRelationshipConverter { }
    internal class ColumnConverter { }
    internal class ColumnDataCategoryConverter { }
    internal class ConnectionStringConverter { }

    internal class CultureConverter
    {
        public static Dictionary<string, CultureInfo> Cultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures).ToDictionary(c => c.Name, c => c);
    }
    internal class DataTypeEnumConverter { }
    internal class DataSourceConverter { }
    internal class FormatStringConverter { }
    internal class IndexerConverter { }
    internal class HierarchyColumnConverter { }
    internal class KPIStatusGraphicConverter { }
    internal class KPITrendGraphicConverter { }
    internal class NamedExpressionConverter { }
    internal class OtherTablesConverter { }
    internal class TableColumnConverter { }
    internal class TableDataCategoryConverter { }

    /// <summary>
    /// This interface must be implemented by dictionary-type properties on a class, such as
    /// annotations, translations, etc.
    /// </summary>
    internal interface IExpandableIndexer
    {
        string Summary { get; }
        IEnumerable<string> Keys { get; }
        object this[string index] { get; set; }
        string GetDisplayName(string key);
        bool EnableMultiLine { get; }
    }
}

namespace TabularEditor.PropertyGridUI.Converters
{
}

namespace TabularEditor.TOMWrapper
{
    public class TabularCommonActions
    {
        internal TabularCommonActions(object _)
        {
        }

        // Real impl lives in the excluded TabularCommonActions.cs (object add/insert/paste, references
        // FormulaFixup). Only hit on TMDL *import*/paste, not full load/save. Throw loudly if reached in M0.
        public System.Collections.Generic.List<TabularObject> InsertObjects(
            TabularEditor.TOMWrapper.Serialization.ObjectJsonContainer objectContainer,
            ITabularNamedObject destination = null,
            bool replaceObjects = false)
            => throw new System.NotSupportedException("Semanticus M0: TabularCommonActions.InsertObjects (TMDL import/paste) is not ported yet.");
    }
}

namespace TabularEditor.TOMWrapper.Serialization
{
}

namespace TabularEditor.TOMWrapper.Undo
{
}

namespace TabularEditor.TOMWrapper.Utils
{
    public class TabularDeployer
    {
        public static DeploymentResult GetLastDeploymentResults(object database)
        {
            return new DeploymentResult();
        }
    }

    public class DeploymentResult { }

    internal class CTCBackup
    {
        internal static CTCBackup BackupColumn(object _) => default;
    }
}

namespace TabularEditor.Utils
{
}

namespace System.Drawing.Design
{
#if !NETFRAMEWORK
    internal class UITypeEditor { }
#endif
}

namespace System.ComponentModel.Design
{
    internal class MultilineStringEditor { }
}
