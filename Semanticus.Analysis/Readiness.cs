using System;
using System.Collections.Generic;
using TabularEditor.TOMWrapper;

// The tests pin internals that carry cross-assembly contracts (Refs must format identically to the engine's
// public ObjectRefs — see the Refs doc-comment below).
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Semanticus.Tests")]

namespace Semanticus.Analysis
{
    public enum ReadinessCategory
    {
        Naming,
        Descriptions,
        Synonyms,
        Relationships,
        Visibility,
        Formatting,
        CopilotLimits,
        DataAgentConfig,
        BestPractice,
    }

    /// <summary>Severity doubles as the rule weight inside its category (Info=1 … Critical=5).</summary>
    public enum Severity { Info = 1, Medium = 2, High = 3, Critical = 5 }

    /// <summary>
    /// Analyst-copy helpers. A handful of readiness messages append the MCP op the user's AI assistant would call to
    /// remediate (e.g. "Enable it (enable_qna)."). That op is load-bearing for the AGENT door, so the RULE supplies it
    /// OUT OF BAND, as a typed <see cref="FixOp"/> part between literal text parts, never a substring of any string.
    /// <see cref="Compose"/> builds both renderings from the ordered part list: <see cref="ReadinessFinding.Message"/>
    /// renders each op inline as " (op_id)" (byte-identical to writing the hint inline, so the agent door is unchanged)
    /// and <see cref="ReadinessFinding.DisplayMessage"/> drops the ops. Because the op is never inside a string, nothing
    /// is ever scanned or mutated: arbitrary text (object names, even one containing a control char; custom-rule
    /// messages; literal parentheses like <c>(order_date, ship_date)</c> or <c>(SummarizeBy = None)</c>) flows through
    /// BOTH fields untouched. There is no reserved character, so no user input can collide with the op marker.
    /// </summary>
    public static class ReadinessCopy
    {
        /// <summary>An out-of-band MCP fix-op hint. Rules pass it as a part between literal text parts (e.g.
        /// <c>NewFinding(o, name, "Enable it", ReadinessCopy.Op("enable_qna"), ".")</c>); it is a typed object, never a
        /// substring of any string, so no user prose or object name can be confused for it.</summary>
        public sealed class FixOp
        {
            public string Op { get; }
            internal FixOp(string op) { Op = op; }
        }

        /// <summary>Wraps an MCP op id as an out-of-band <see cref="FixOp"/> part for <see cref="Compose"/>.</summary>
        public static FixOp Op(string opId) => new FixOp(opId);

        /// <summary>Build the agent-facing Message and analyst-facing DisplayMessage from an ordered part list of literal
        /// strings and <see cref="FixOp"/> hints. Message renders each op inline as " (op_id)"; DisplayMessage drops the
        /// ops. DisplayMessage is null when the parts carry no op (the UI then renders Message verbatim) and is never an
        /// empty/blank string. Nothing is parsed out of a string, so any literal text (including a raw control char)
        /// passes through both fields unchanged.</summary>
        public static (string Message, string Display) Compose(params object[] parts)
        {
            if (parts == null || parts.Length == 0) return (string.Empty, null);
            var msg = new System.Text.StringBuilder();
            var disp = new System.Text.StringBuilder();
            var hasOp = false;
            foreach (var p in parts)
            {
                if (p is FixOp f) { msg.Append(" (").Append(f.Op).Append(')'); hasOp = true; }
                else { var str = p as string ?? p?.ToString() ?? string.Empty; msg.Append(str); disp.Append(str); }
            }
            var message = msg.ToString();
            if (!hasOp) return (message, null);              // no op => DisplayMessage null, UI falls back to Message
            var display = disp.ToString();
            return (message, string.IsNullOrWhiteSpace(display) ? null : display);   // never an empty/blank display
        }
    }

    /// <summary>How the rule is evaluated.</summary>
    public enum RuleKind { Deterministic, LlmJudgment, LlmGenerate, Dmv }

    /// <summary>How a violation is remediated (drives the UI/agent affordance).</summary>
    public enum FixKind
    {
        None,            // advisory only
        SafeFix,         // deterministic, applied by "Apply all safe fixes"
        AiContent,       // the user's Claude generates content (description/synonyms/name) then applies
        Proposal,        // needs human review (structural / risky)
    }

    public sealed class ReadinessFinding
    {
        public string RuleId { get; set; }
        public string RuleTitle { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public string Fix { get; set; }
        public string ObjectRef { get; set; }
        public string ObjectName { get; set; }
        public string Message { get; set; }
        // The analyst-facing rendering of Message with agent-only MCP op-name hints removed (e.g. "Enable it
        // (enable_qna)." -> "Enable it."). NULL when Message carries no op hint (the UI then renders Message verbatim);
        // never an empty string (Compose guards blank). Message and DisplayMessage are BOTH built server-side by
        // ReadinessCopy.Compose from the rule's ordered parts, where the op is a typed out-of-band ReadinessCopy.Op(...)
        // part, never a substring of any string — so nothing is ever scanned or mutated and an object literally named
        // like an op (or a custom message full of parentheses, or a raw control char) is never confused for one. The UI
        // does no string surgery of its own; only the VS Code Findings list reads this field.
        public string DisplayMessage { get; set; }
        public bool Waived { get; set; }              // an accepted finding: surfaced but excluded from the score
        public string WaiverReason { get; set; }      // why it was accepted (null unless Waived)
        public bool WaiverRuleLevel { get; set; }      // true when waived by a rule-level (model-wide) waiver, not a per-instance one
        public bool Custom { get; set; }               // provenance: true = produced by a model-embedded (user-authored) rule, false = built-in
    }

    public sealed class CategoryScore
    {
        public string Category { get; set; }
        public double Score { get; set; }        // 0..100 (waived findings count as pass)
        public double Weight { get; set; }
        public int Applicable { get; set; }       // objects evaluated across the category's rules
        public int Violations { get; set; }       // ACTIVE (un-waived) violations
        public int Waived { get; set; }           // accepted findings in this category (informational)
        public bool HasRules { get; set; }
    }

    public sealed class Scorecard
    {
        public double Overall { get; set; }       // 0..100 (after gates)
        public double RawOverall { get; set; }    // before gates
        public string Grade { get; set; }         // A..F
        public string[] GatedBy { get; set; } = Array.Empty<string>();
        public CategoryScore[] Categories { get; set; } = Array.Empty<CategoryScore>();
        public Dictionary<string, double> Coverage { get; set; } = new Dictionary<string, double>();
        public ReadinessFinding[] Findings { get; set; } = Array.Empty<ReadinessFinding>();
        public int SafeFixCount { get; set; }     // ACTIVE findings remediable by "Apply all safe fixes"
        public int WaivedCount { get; set; }      // accepted findings excluded from the score (always surfaced, never hidden)
        public string Caveat { get; set; }        // set when the score may be incomplete (e.g. Direct Lake live stats are resident-only); null otherwise
        // Custom-rule problems surfaced loudly instead of a silent pass (unparseable annotation, an eval error, a
        // hand-edited id colliding with a built-in). Built-in rules never populate this. Mirrors BpaScorecard.RuleErrors.
        public string[] RuleErrors { get; set; } = Array.Empty<string>();
    }

    /// <summary>Live per-column statistics (distinct-value cardinality, from COLUMNSTATISTICS / DMVs) that the
    /// Dmv-kind readiness rules need. ABSENT on an offline scan — those rules are then simply not evaluated, so
    /// the offline scorecard is unchanged. Supplied only by the live scan path (ai_readiness_scan_live).</summary>
    public sealed class ReadinessLiveStats
    {
        // (table, column) -> distinct value count. Tuple key, compared CASE-INSENSITIVELY (TOM treats object names
        // case-insensitively, so a COLUMNSTATISTICS label must still join to the model column regardless of case).
        private sealed class CiKey : IEqualityComparer<(string Table, string Column)>
        {
            public bool Equals((string Table, string Column) a, (string Table, string Column) b) =>
                string.Equals(a.Table, b.Table, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Column, b.Column, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Table, string Column) t) =>
                (StringComparer.OrdinalIgnoreCase.GetHashCode(t.Table ?? "") * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(t.Column ?? "");
        }
        public Dictionary<(string Table, string Column), long> Cardinality { get; } = new Dictionary<(string, string), long>(new CiKey());
        public void Set(string table, string column, long card) => Cardinality[(table ?? "", column ?? "")] = card;
        public bool TryGet(string table, string column, out long card) => Cardinality.TryGetValue((table ?? "", column ?? ""), out card);

        /// <summary>The Q&A linguistic index stores at most this many unique TEXT values across indexed (string)
        /// columns; beyond it values are dropped and Copilot/Q&A value-matching degrades. (Microsoft-documented.)</summary>
        public const long QnaUniqueValueCeiling = 5_000_000;
        /// <summary>A single visible text column above this dominates the Q&A index (>20% of the ceiling) — almost
        /// always a surrogate key / id / free-text column that should be hidden or excluded from the AI data schema.</summary>
        public const long HighCardinalityColumn = 1_000_000;
    }

    /// <summary>Grounding context the engine hands to the user's Claude to author a description / synonyms / name.</summary>
    public sealed class GroundingBundle
    {
        public string ObjectRef { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Table { get; set; }
        public string Expression { get; set; }
        public string FormatString { get; set; }
        public string ExistingDescription { get; set; }
        public string DataType { get; set; }
        public string[] SiblingNames { get; set; } = Array.Empty<string>();
        // Calendar-based time intelligence (CL 1701+) available in the model, one compact line per calendar
        // ("'Fiscal' on [Date]: Date→Date, Fiscal Year→Year, …"). Model-wide (a calendar is referenced by NAME in
        // TOTALYTD(expr,'Fiscal') regardless of which table the object sits on), so the agent knows it can author
        // calendar-aware DAX instead of the classic forms. Empty when the model defines no calendars.
        public string[] Calendars { get; set; } = Array.Empty<string>();
    }

    /// <summary>Stable object-ref format shared with the engine's resolver (measure:Table/Name, etc.).
    /// Must cover EVERY case the engine's ObjectRefs.For emits and format it IDENTICALLY — the health probe
    /// intersects analyzer findings with engine change-deltas by ref, and scoped BPA pre-filters collections by
    /// ref, so a formatter mismatch silently drops those objects from both. Pinned by the parity test
    /// (ReadinessRefsParityTests) that walks a model with every object kind through both formatters.</summary>
    internal static class Refs
    {
        public static string For(ITabularObject obj)
        {
            switch (obj)
            {
                case Measure m: return "measure:" + m.Table?.Name + "/" + m.Name;
                case Column c: return "column:" + c.Table?.Name + "/" + c.Name;
                case Table t: return "table:" + t.Name;
                case Function f: return "function:" + f.Name;
                case SingleColumnRelationship r: return "relationship:" + r.Name;
                case Hierarchy h: return "hierarchy:" + h.Table?.Name + "/" + h.Name;
                case Level lv: return "level:" + lv.Table?.Name + "/" + lv.Hierarchy?.Name + "/" + lv.Name;
                // Must match the engine resolver's "calcitem:Group/Name" format — the BestPractice DAX rules emit
                // findings on calc items, and an unresolvable ref would null the make_ai_ready grounding invariant.
                case CalculationItem ci: return "calcitem:" + ci.CalculationGroupTable?.Name + "/" + ci.Name;
                case ModelRole role: return "role:" + role.Name;
                case Perspective p: return "perspective:" + p.Name;
                case Partition pt: return "partition:" + pt.Table?.Name + "/" + pt.Name;
                default: return (obj is ITabularNamedObject n) ? "obj:" + obj.ObjectType + "/" + n.Name : "obj:" + obj.ObjectType;
            }
        }
    }
}
