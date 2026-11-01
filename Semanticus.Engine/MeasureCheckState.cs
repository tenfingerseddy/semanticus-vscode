using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine;

/// <summary>The person's Keep of an edit: the closure inputs of the Equivalence mark they kept, and when.</summary>
public sealed record MeasureKeep
{
    public string Inputs { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>The push: the check states that changed, each with the version it was written at.</summary>
public sealed class CheckStateChange
{
    public string SessionId { get; set; }
    public long Version { get; set; }
    public MeasureCheckCache[] States { get; set; } = Array.Empty<MeasureCheckCache>();
    /// <summary>Whether a run is going on as this push leaves, with its count: every run plan shows a run another screen started
    /// (rc34 tester Next 5). Never null, so the wire always carries it.</summary>
    public MeasureRunActivity Run { get; set; } = MeasureRunActivity.Idle;
}

public sealed record MeasureRunActivity(bool Active, int Done, int Total)
{
    public static readonly MeasureRunActivity Idle = new(false, 0, 0);
}

/// <summary>The one place a mark gets its verdict. Every copy that leaves the engine passes through Finish, so the
/// tree, the Checks rows, the card and the assistant read the same answer (d1 spec sections 2 and 3).</summary>
internal static class MeasureCheckVerdict
{
    internal static readonly string[] Seven = { "Totals", "Filters", "SQL", "Trusted number", "Equivalence", "Contexts", "Performance" };
    private static readonly string[] Order = { "broken", "review", "unknown", "overridden", "verified" };

    // A mark with one of these codes does not apply to the measure: it is not shown and never counts.
    private static readonly HashSet<string> NotApplying = new(StringComparer.Ordinal)
    { "no_sql", "no_trusted", "no_edit", "kept", "not_timed", "totals_off", "not_scored", "filters_no_rows", "not_connected" };
    // A Totals Jev typed but did not judge (totals_distinct, totals_not_judged, totals_type_unclear) applies: it shows the type Jev
    // chose and has no verdict, so it never counts (Kane, 1 Nov, f173; this overturns the head decision of 29 Oct, wr1 F7).
    // A mark a run is producing. It has no result of its own yet.
    private static readonly HashSet<string> Working = new(StringComparer.Ordinal) { "waiting", "timing" };

    internal static bool Applies(string reasonCode) => reasonCode == null || !NotApplying.Contains(reasonCode);
    internal static bool InProgress(MeasureMark mark) => mark?.ReasonCode != null && Working.Contains(mark.ReasonCode);

    /// <summary>Sets Applies and Verdict on a copy. <paramref name="keep"/> hides an Equivalence mark compared on the kept
    /// inputs; <paramref name="expected"/> is the Totals waiver annotation.</summary>
    internal static MeasureMark Finish(MeasureMark mark, MeasureKeep keep, bool expected)
    {
        if (mark.Name == "Equivalence" && keep?.Inputs is { Length: > 0 } inputs && mark.Inputs == inputs && mark.State != "not run")
            mark.ReasonCode = "kept";
        if (mark.Name == "Totals" && expected && mark.State == "suspect" && mark.Total != null)
        {
            // The check can predate the waiver. Every surface stops warning at once.
            mark.State = "info"; mark.Action = null;
            mark.Total = mark.Total with { Expected = true };
            mark.Sentence = JevTotalsScorer.ExpectedSentence(mark.Total.Rule);
        }
        if (mark.State == "not run" && mark.ReasonCode == null) mark.ReasonCode = "could_not_run";
        mark.Applies = Applies(mark.ReasonCode);
        mark.Verdict = !mark.Applies ? null : VerdictOf(mark);
        return mark;
    }

    private static string VerdictOf(MeasureMark mark)
    {
        // L2's judgement codes. A total between the two cuts is not clearly right, so it is not a pass (R8, ra1 item 1).
        if (mark.ReasonCode is "filters_on_purpose" or "totals_expected") return "overridden";
        if (mark.ReasonCode is "totals_unclear") return "unknown";
        if (mark.Name == "Totals" && mark.Total?.Expected == true && mark.State is "passed" or "info") return "overridden";
        return mark.State switch
        {
            "passed" => "verified",
            "suspect" => mark.Name is "SQL" or "Trusted number" ? "broken" : "review",
            "info" => mark.Name == "Filters" ? "review" : null,
            "unknown" => "unknown",
            "not run" => InProgress(mark) ? null : "unknown",
            _ => null
        };
    }

    /// <summary>The worst verdict of the seven marks, or null when none has one.</summary>
    internal static string Worst(IEnumerable<MeasureMark> marks) => marks
        .Where(m => Seven.Contains(m.Name) && m.Applies && m.Verdict != null)
        .Select(m => m.Verdict).OrderBy(v => Array.IndexOf(Order, v)).FirstOrDefault();
}
