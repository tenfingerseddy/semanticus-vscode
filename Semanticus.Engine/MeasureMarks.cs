using System;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed class MeasureMarkCell
{
    public string Context { get; set; }
    public string Before { get; set; }
    public string After { get; set; }
    // Display text of Before and After, formatted once by the engine (L3 fills them). Null: the UI formats the raw value.
    public string BeforeText { get; set; }
    public string AfterText { get; set; }
    // The gap from Before to After (always positive) in the measure's format, with enough places to see it ("$1,005.00", "$0.001").
    // Null when either side is not a number or the two are the same.
    public string GapText { get; set; }
}

public sealed class MeasureMark
{
    public string Name { get; set; }
    public string State { get; set; }
    public string Sentence { get; set; }
    public MeasureMarkCell[] Cells { get; set; } = Array.Empty<MeasureMarkCell>();
    public string Action { get; set; }
    public string LastRan { get; set; }
    public string ReasonCode { get; set; }
    public string[] ActionCodes { get; set; } = Array.Empty<string>();
    public string BeforeFormatString { get; set; }
    public string AfterFormatString { get; set; }
    // Equivalence only: the formula before and after the edit, so the mark detail can show the edit.
    public string BeforeFormula { get; set; }
    public string AfterFormula { get; set; }
    // Equivalence only: a hash of the measure and every measure it uses, as compared. Keep holds by it.
    public string Inputs { get; set; }
    public TotalReading Total { get; set; }
    public MeasureTimingDetail[] Timings { get; set; } = Array.Empty<MeasureTimingDetail>();
    public MeasureContexts.ContextCandidate[] ChosenContexts { get; set; } = Array.Empty<MeasureContexts.ContextCandidate>();
    // Set only by the engine's check state (MeasureCheckVerdict.Finish), so every door and surface reads one answer.
    // Applies false: the mark is not shown and never counts. Pending: a run has not reached it; its last result stands.
    public bool Applies { get; set; } = true;
    public bool Pending { get; set; }
    public string Verdict { get; set; }
    // While pending: what the run is doing for this mark ("Timing this measure: Waiting for 2 other checks first.").
    public string Progress { get; set; }
}

public sealed class MeasureMarksResult
{
    public string MeasureRef { get; set; }
    public long Revision { get; set; }
    public bool Running { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public MeasurePerformanceSettings PerformanceSettings { get; set; }
    public bool IncludePerformanceForMeasure { get; set; }
    public MeasureMark[] Marks { get; set; } = Array.Empty<MeasureMark>();
    // The check state's version this view was read at. A reader drops a view older than one it already has.
    public long Version { get; set; }
}
