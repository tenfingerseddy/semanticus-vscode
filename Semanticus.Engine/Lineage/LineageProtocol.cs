using System;

namespace Semanticus.Engine
{
    // ---- Lineage & Impact (the "Measure Killer" tab) — wire DTOs --------------------------------------------------
    // Phase 1 is OFFLINE (TOM-only): model-internal lineage (source→table→column/measure + the DAX dependency
    // graph), forward Impact ("what breaks if I change this?"), and the reverse Safe-to-remove sweep ("what can I
    // delete with zero downstream impact?"). Published-report field usage is Phases 2-3; until then the unused
    // verdict is MODEL-ONLY and says so (see Caveat). Dual-drive: identical shape over the RPC door (the Studio
    // Lineage tab) and the MCP door (the user's Claude). Spec: docs/lineage-impact-plan.md.

    /// <summary>One node in the lineage graph — a model object or an upstream data source.</summary>
    public sealed class LineageNode
    {
        public string Ref { get; set; }       // an ObjectRefs ref (table:/measure:/column:/…) or a synthetic source ref (source:…)
        public string Name { get; set; }
        public string Kind { get; set; }       // source | unresolved | table | calcTable | calcGroup | column | calcColumn | measure
        public string Table { get; set; }      // owning table for column/measure; null otherwise
        public bool IsHidden { get; set; }
        public string Detail { get; set; }      // optional: source connector / "shared expression" / "UNRESOLVED"
    }

    /// <summary>One directed edge. Kind tells you the relationship; From/To are node refs.</summary>
    public sealed class LineageEdge
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Kind { get; set; }        // source (source→table) | contains (table→child) | dependsOn (dependant→dependency) | relationship (fromCol→toCol)
    }

    /// <summary>The whole model's lineage graph (Phase 1: offline, model-internal + a pragmatic source→table edge).</summary>
    public sealed class LineageResult
    {
        public LineageNode[] Nodes { get; set; } = Array.Empty<LineageNode>();
        public LineageEdge[] Edges { get; set; } = Array.Empty<LineageEdge>();
        public string Caveat { get; set; }      // "model-only — published-report field usage not yet included…"
    }

    /// <summary>One object affected by a change to the impact root (with its BFS depth from the root).</summary>
    public sealed class ImpactNode
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public int Depth { get; set; }          // 1 = directly references the root; deeper = transitive
        public string Via { get; set; }         // dax | relationship | hierarchy | sortBy
    }

    /// <summary>Forward impact — everything that breaks if the root object changes or is removed.</summary>
    public sealed class ImpactResult
    {
        public string Root { get; set; }
        public string RootName { get; set; }
        public string RootKind { get; set; }
        public ImpactNode[] Impacted { get; set; } = Array.Empty<ImpactNode>();
        public int Measures { get; set; }
        public int Columns { get; set; }
        public int Tables { get; set; }
        public int Relationships { get; set; }
        public int Other { get; set; }
        public string Caveat { get; set; }
    }

    /// <summary>One safe-to-remove candidate with the tri-state verdict + why.</summary>
    public sealed class UnusedItem
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }        // measure | column | calcColumn
        public string Table { get; set; }
        public bool IsHidden { get; set; }
        public string Verdict { get; set; }     // safe | usedByUnusedOnly | caution
        public int RefCount { get; set; }        // direct model-side referencers
        public string[] BlockedBy { get; set; } = Array.Empty<string>();   // the (dead) referencers, for usedByUnusedOnly
        public string Reason { get; set; }
    }

    /// <summary>The reverse safe-to-remove sweep over the model (Phase 1: MODEL-ONLY — see Caveat).</summary>
    public sealed class UnusedResult
    {
        public UnusedItem[] Items { get; set; } = Array.Empty<UnusedItem>();
        public int SafeCount { get; set; }
        public int UsedByUnusedOnlyCount { get; set; }
        public int CautionCount { get; set; }
        public string Caveat { get; set; }
    }

    // ---- The sweep's ACT half (remove_safe_objects) ----------------------------------------------------------------

    /// <summary>One object the sweep deleted (identity echoed from the verified candidate, for the report/UI).</summary>
    public sealed class RemovedObject
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }        // measure | column | calcColumn
        public string Table { get; set; }
    }

    /// <summary>One candidate the sweep did NOT delete, with the plain-English reason. A "safe" verdict that went
    /// stale between the caller's scan and the apply downgrades here — never a stale delete.</summary>
    public sealed class SkippedObject
    {
        public string Ref { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>remove_safe_objects result: what the ONE undoable transaction removed / skipped, and which
    /// verification basis (model-only vs report-aware) vouched for "safe" at apply time.</summary>
    public sealed class RemoveSafeReport
    {
        public long Revision { get; set; }
        public RemovedObject[] Removed { get; set; } = Array.Empty<RemovedObject>();
        public SkippedObject[] Skipped { get; set; } = Array.Empty<SkippedObject>();
        public int Count { get; set; }              // == Removed.Length (the headline number)
        public string Verification { get; set; }    // "model-only" | "report-aware (N report(s))"
        public string Caveat { get; set; }          // the sweep's honesty caveat (model-only / report coverage)
        public string Note { get; set; }            // one plain-English line: outcome + the undo hint
    }

    // ---- Report-aware lineage (Phase 3 — local PBIR; cloud getDefinition reuses the same parser) -----------------

    /// <summary>Which model fields ONE page/visual of a report uses — the page+visual drill-down. <see cref="Visual"/>
    /// is null for a page-level filter. Ids come from the PBIR part path (display names are a later enrichment).</summary>
    public sealed class ReportVisualUsage
    {
        public string Page { get; set; }
        public string Visual { get; set; }       // null = a page-level filter (page, no specific visual)
        public string VisualType { get; set; }   // e.g. "barChart" (best-effort from the visual.json)
        public string[] UsedRefs { get; set; } = Array.Empty<string>();   // resolved model object refs this visual uses
    }

    /// <summary>What one published/local report uses from the open model (parsed from its PBIR definition).</summary>
    public sealed class ReportUsage
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public bool Read { get; set; }                                  // false = not a PBIR definition / unreadable
        public int FieldCount { get; set; }                            // distinct model field references resolved
        public int Unresolved { get; set; }                            // references that couldn't be attributed (⇒ caution)
        public string[] UsedRefs { get; set; } = Array.Empty<string>(); // resolved model object refs this report uses
        public string[] ExtensionMeasures { get; set; } = Array.Empty<string>();  // report-level measures (not model objects)
        public ReportVisualUsage[] Visuals { get; set; } = Array.Empty<ReportVisualUsage>();   // page+visual-level drill-down
        public string Error { get; set; }                              // scrubbed reason this report couldn't be read (RDL/paginated, getDefinition blocked by a sensitivity label, fetch failure) — fail-loud, not silent
    }

    // ---- Cloud report discovery (Phase 3 — Power BI REST per-workspace listing, the non-admin default) -----------

    /// <summary>One published report discovered in a Fabric/Power BI workspace via the non-admin per-workspace list
    /// (<c>GET /groups/{id}/reports</c>). Carries the <see cref="DatasetId"/> the report binds to (Power BI REST
    /// exposes it; the Fabric item list does not) so a caller can pick the reports that use the open model, and the
    /// <see cref="ReportType"/> so paginated/RDL reports (which PBIR getDefinition can't parse) are skipped early.</summary>
    public sealed class CloudReport
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string DatasetId { get; set; }   // the semantic model this report binds to (match to the open model)
        public string ReportType { get; set; }  // PowerBIReport (PBIR-capable) | PaginatedReport (RDL — not parsed)
        public string WebUrl { get; set; }
    }

    /// <summary>Report-aware analysis: which model fields the given reports use, plus a safe-to-remove sweep that
    /// EXCLUDES anything a report uses (closing the model-only blind spot for descriptive columns).</summary>
    public sealed class ReportAnalysisResult
    {
        public ReportUsage[] Reports { get; set; } = Array.Empty<ReportUsage>();
        public int ReportsRead { get; set; }
        public int ReportsUnreadable { get; set; }
        public string[] ModelFieldsUsed { get; set; } = Array.Empty<string>();   // union of resolved refs across all reports
        public UnusedResult Unused { get; set; }                                 // report-aware safe-to-remove
        public string Caveat { get; set; }
    }
}
