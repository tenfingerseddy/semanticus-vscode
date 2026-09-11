using System;

namespace Semanticus.Engine
{
    public sealed class WorkflowDocumentResult
    {
        public string Name { get; set; }
        public string Library { get; set; }
        public string Path { get; set; }
        public string ExactText { get; set; }
        public string ByteHash { get; set; }
        public WorkflowDocumentMetadata Metadata { get; set; }
    }

    public sealed class WorkflowDocumentMetadata
    {
        public int SchemaVersion { get; set; }
        public string Title { get; set; }
        public int Version { get; set; }
        public string[] StepIds { get; set; } = Array.Empty<string>();
        public bool ExplicitIds { get; set; }
        public bool Parses { get; set; }
        public string ParseError { get; set; }
    }

    public sealed class WorkflowDocumentEditResult
    {
        public string Name { get; set; }
        public bool Changed { get; set; }
        public string Reason { get; set; }
        public string ByteHash { get; set; }
        public WorkflowDocumentResult Document { get; set; }
    }
}
