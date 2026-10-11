// The result types of validate_dax. They live beside the validator in Semanticus.Analysis (the readiness and best-practice checks for a formula that
// names an object the model does not have run the same validator), and keep the Semanticus.Engine namespace because that is the wire contract.
namespace Semanticus.Engine
{
    /// <summary>Result of validate_dax: a syntactic/reference check of a DAX expression against the open model.
    /// Valid=false only when there is a structural ERROR (unbalanced brackets); unknown refs are warnings.</summary>
    public sealed class DaxValidation
    {
        public bool Valid { get; set; }
        public DaxDiagnostic[] Diagnostics { get; set; } = System.Array.Empty<DaxDiagnostic>();
    }

    public sealed class DaxDiagnostic
    {
        public string Severity { get; set; }   // "error" | "warning"
        public string Message { get; set; }
        public int Line { get; set; }           // 1-based
        public int Column { get; set; }         // 1-based
    }
}
