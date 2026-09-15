using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
        public WorkflowEditModel EditModel { get; set; }
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

    public sealed class WorkflowEditModel
    {
        public string Format { get; set; }
        public WorkflowEditDefinition Definition { get; set; }
        public WorkflowEditRestriction[] Restrictions { get; set; } = Array.Empty<WorkflowEditRestriction>();
        public WorkflowEditChoices Choices { get; set; } = new WorkflowEditChoices();
    }

    public sealed class WorkflowEditDefinition
    {
        public string Key { get; set; }
        public int SchemaVersion { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string WhenToUse { get; set; }
        public int Version { get; set; }
        public string Strictness { get; set; }
        public string[] Triggers { get; set; } = Array.Empty<string>();
        public string[] Tags { get; set; } = Array.Empty<string>();
        public WorkflowEditMapEntry[] Provenance { get; set; } = Array.Empty<WorkflowEditMapEntry>();
        public WorkflowEditSlot[] Slots { get; set; } = Array.Empty<WorkflowEditSlot>();
        public WorkflowEditStep[] Steps { get; set; } = Array.Empty<WorkflowEditStep>();
    }

    public sealed class WorkflowEditMapEntry
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public sealed class WorkflowEditSlot
    {
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public string Name { get; set; }
        public string Question { get; set; }
        public string Type { get; set; }
        public string Required { get; set; }
        public string Default { get; set; }
        public string Example { get; set; }
        public string Hint { get; set; }
        public string[] Values { get; set; } = Array.Empty<string>();
    }

    public sealed class WorkflowEditStep
    {
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public string Id { get; set; }
        public string IdKind { get; set; }
        public int Number { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }
        public string[] Ops { get; set; } = Array.Empty<string>();
        public string When { get; set; }
        public WorkflowEditForEach ForEach { get; set; }
        public WorkflowEditCall Call { get; set; }
        public WorkflowEditGate Gate { get; set; }
    }

    public sealed class WorkflowEditForEach
    {
        public WorkflowEditLoopSource Source { get; set; }
        public string As { get; set; }
        public int MaxIterations { get; set; }
    }

    public sealed class WorkflowEditLoopSource
    {
        public string Kind { get; set; }
        public string[] Values { get; set; } = Array.Empty<string>();
        public string Name { get; set; }
    }

    public sealed class WorkflowEditCall
    {
        public string Workflow { get; set; }
        public WorkflowEditMapEntry[] With { get; set; } = Array.Empty<WorkflowEditMapEntry>();
        public string[] Returns { get; set; } = Array.Empty<string>();
    }

    public sealed class WorkflowEditGate
    {
        public string Strictness { get; set; }
        public WorkflowEditInput[] Inputs { get; set; } = Array.Empty<WorkflowEditInput>();
        public WorkflowEditVerify[] Verify { get; set; } = Array.Empty<WorkflowEditVerify>();
    }

    public sealed class WorkflowEditInput
    {
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public string Name { get; set; }
        public string Question { get; set; }
        public string Type { get; set; }
        public string Required { get; set; }
        public string DaxPurity { get; set; }
        public string Scope { get; set; }
    }

    public sealed class WorkflowEditVerify
    {
        public string Key { get; set; }
        public string Fingerprint { get; set; }
        public string Kind { get; set; }
        public string When { get; set; }
        public string Probe { get; set; }
        public string Scope { get; set; }
        public string Intent { get; set; }
        public string[] PinnedShapes { get; set; } = Array.Empty<string>();
        public string[] OpenShapes { get; set; } = Array.Empty<string>();
        public string OpenShapesFrom { get; set; }
        public string OpenMismatch { get; set; }
        public string Anchors { get; set; }
    }

    public sealed class WorkflowEditRestriction
    {
        public string Target { get; set; }
        public string Field { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public sealed class WorkflowEditChoices
    {
        public string[] Strictness { get; set; } = { "hard", "warn", "off" };
        public string[] Kinds { get; set; } = { "workflow", "template" };
        public string[] InputTypes { get; set; } = { "verification", "text", "enum", "number", "objectRef", "planItem" };
        public string[] SlotTypes { get; set; } = { "verification", "text", "enum", "number", "objectRef", "planItem" };
        public string[] InputRequired { get; set; } = { "answer-or-decline", "required", "optional" };
        public string[] InputScopes { get; set; } = { "iteration", "run" };
        public string[] DaxPurity { get; set; } = { "no-bare-measures" };
        public string[] SlotRequired { get; set; } = { "required", "optional" };
        public string[] VerifyKinds { get; set; } = { "dax_probe", "dax_equivalence", "expected_values", "anchor_coverage", "readiness_rescan", "bpa_clean", "benchmark_delta", "workflow_admissible", "interview_replay", "baseline_captured", "impact_assessment", "baseline_exists", "baseline_unchanged", "tests_replay", "plan_item_staged", "plan_item_applied" };
        public string[] VerifyScopes { get; set; } = { "object", "model" };
        public string[] VerifyIntents { get; set; } = { "change", "rename", "remove", "restructure" };
        public string[] ShapeIds { get; set; } = { "grand_total", "cross", "axis:<column>" };
        public string[] OpenMismatch { get; set; } = { "countersign" };
        public int DefaultMaxIterations { get; set; } = ForEachSpec.DefaultMaxIterations;
        public int MaxIterations { get; set; } = ForEachSpec.MaxMaxIterations;
    }

    public sealed class WorkflowEditPreviewResult
    {
        public string Name { get; set; }
        public string Outcome { get; set; }
        public bool CanApply { get; set; }
        public bool RequiresReview { get; set; }
        public string Reason { get; set; }
        public WorkflowDocumentResult Document { get; set; }
        public string ProposedText { get; set; }
        public string ProposedByteHash { get; set; }
        public string Diff { get; set; }
        public WorkflowEditModel EditModel { get; set; }
        public WorkflowEditIssue[] Issues { get; set; } = Array.Empty<WorkflowEditIssue>();
        public WorkflowEditKeyChange[] KeyChanges { get; set; } = Array.Empty<WorkflowEditKeyChange>();
        [JsonPropertyName("suggested_next_action")]
        [Newtonsoft.Json.JsonProperty("suggested_next_action")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkflowEditNextAction SuggestedNextAction { get; set; }
    }

    public sealed class WorkflowEditIssue
    {
        public string Code { get; set; }
        public string Severity { get; set; }
        public string Target { get; set; }
        public string Field { get; set; }
        public string[] RelatedTargets { get; set; } = Array.Empty<string>();
        public string Message { get; set; }
    }

    public sealed class WorkflowEditKeyChange
    {
        public string OldKey { get; set; }
        public string NewKey { get; set; }
        public string OldStepId { get; set; }
        public string NewStepId { get; set; }
    }

    public sealed class WorkflowEditNextAction
    {
        public string Op { get; set; }
        public WorkflowEditApplyArgs Args { get; set; }
        public string Why { get; set; }
    }

    public sealed class WorkflowEditApplyArgs
    {
        public string Name { get; set; }
        public string ExpectByteHash { get; set; }
        public string ExpectPath { get; set; }
        public string ExactText { get; set; }
        public string Markdown { get; set; }
        public bool? CreateOnly { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SessionId { get; set; }
    }
}
