using System;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine;

// Scoring failures can carry remote question ids and service details. Product sentences use local copy only.
internal static class ScoringCopy
{
    internal const string Missing = "This score turns on when Jev scoring is released.";
    internal const string MissingCheck = Missing;
    internal const string Unavailable = Missing;
    internal const string Unreachable = Missing;
    internal static string Sentence(string reason) => "Not scored yet. " + (reason ?? Missing);
    internal static bool Calm(string sentence) => sentence == Sentence(Missing);
    internal const string NeedsPro = "Scoring needs a Pro plan.";
    internal const string SwitchedOff = "Scoring is off for this model.";
    internal const string BudgetSpent = "The scoring allowance is used up for now.";
    // Pro advice (permission to send this model's details) is off for the open model: Turn on fixes it (fx29 gap 1).
    internal const string AdviceOff = "Pro advice is off for this model.";
    // A model never saved or opened live has nowhere to keep permission: saving it is what the person can do (fx29b E).
    internal const string NotSaved = "Save the model or open it live to score it.";
    // Permission was taken back and given again while a score was on its way: advice is on, the score was dropped (fx29b G).
    internal const string AdviceChanged = "Pro advice changed while this was being scored. Check again to score it.";
    // Permission was given after this line was written: the line is dropped and the measure is checked again (fx29b F).
    internal const string AdviceOnNow = "Pro advice is on now. Check again to score it.";
    // Nothing stops this score: it has not been asked yet (fx29c d). The seeds say this until a door reads the real state.
    internal const string NotChecked = "It is scored when this measure is checked.";
    // A Description score needs a Description (fx29c e, spec d1 4.4 "No Description yet.").
    internal const string NoDescription = "There is no Description to score.";
    // A reason the person can act on is kept; any other unscored Totals line (old service wording) reads the calm line.
    // Only an unscored Totals is rewritten. A mark with its own line keeps it: a check still waiting to run (fx29c: every copy
    // made it the calm line), a result from before an edit (fx35, rc34 tester gap 1: the stale Totals read "turns on when Jev
    // scoring is released" for the whole rerun after each edit) and a held one.
    internal static string MarkSentence(MeasureMark mark) => mark.Name == "Totals" && mark.ReasonCode is null or "totals_off" or "could_not_run"
        && (mark.State == "not run" || mark.State == "info" && mark.Sentence?.StartsWith("Not scored yet", StringComparison.OrdinalIgnoreCase) == true)
        && !Actionable(mark.Sentence)
        ? Sentence(Missing) : mark.Sentence;
    private static bool Actionable(string sentence) => sentence != null
        && (sentence.EndsWith(NeedsPro, StringComparison.Ordinal) || sentence.EndsWith(SwitchedOff, StringComparison.Ordinal)
            || sentence.EndsWith(BudgetSpent, StringComparison.Ordinal) || sentence.EndsWith(NotChecked, StringComparison.Ordinal)
            || ConsentLine(sentence));
    /// <summary>Something stops scoring that the person can act on: not a score still to be asked (NotChecked) and not a
    /// service fault (the calm line). The Checks page names it at open (wr1 F1).</summary>
    internal static bool StopsScoring(string reason) => reason != null && reason != NotChecked && reason != Missing;
    /// <summary>A line the state decided when it was written (the licence, the switch, the allowance, permission, a saved
    /// model). Once nothing stops scoring, it is no longer true (fx29c).</summary>
    internal static bool StateLine(string sentence) => sentence != null
        && (sentence.EndsWith(NeedsPro, StringComparison.Ordinal) || sentence.EndsWith(SwitchedOff, StringComparison.Ordinal)
            || sentence.EndsWith(BudgetSpent, StringComparison.Ordinal) || sentence.EndsWith(AdviceOff, StringComparison.Ordinal)
            || sentence.EndsWith(NotSaved, StringComparison.Ordinal));
    /// <summary>A line that permission for this model decides. When permission changes, it is no longer true (fx29b F).</summary>
    internal static bool ConsentLine(string sentence) => sentence != null
        && (sentence.EndsWith(AdviceOff, StringComparison.Ordinal) || sentence.EndsWith(NotSaved, StringComparison.Ordinal)
            || sentence.EndsWith(AdviceChanged, StringComparison.Ordinal) || sentence.EndsWith(AdviceOnNow, StringComparison.Ordinal));
    // Not scored is "does not apply" (d1 s3): the relay is not serving this judgement. The actionable line comes from the sentence.
    // Every caller names its reason: the calm line is written only where a service call failed (fx29c guard).
    internal static MeasureMark NotScored(string name, string reason)
    {
        var mark = MeasureMarkJudge.NotRun(name, Sentence(reason));
        mark.ReasonCode = name == "Totals" ? "totals_off" : "not_scored";
        return mark;
    }
    internal static string Reason(DecisionOutcome outcome)
    {
        var reason = outcome?.SkippedReason ?? outcome?.Answers?.NotDecidedReason ?? "";
        if (reason == DecisionService.BudgetSpentReason) return BudgetSpent;
        if (reason.Contains("switched off", StringComparison.OrdinalIgnoreCase)) return SwitchedOff;
        if (reason.Contains("part of Semanticus Pro", StringComparison.OrdinalIgnoreCase)) return NeedsPro;
        // The relay client is built from a licence and nothing else, so no client means no licence to present: the rc29
        // capture (tier pro, no licence) read the calm line here after every run (fx29c). Not a service fault.
        if (reason == DecisionService.NoServiceReason) return NeedsPro;
        // A model with nowhere to keep permission (never saved) is not one Turn on can help: saving it is (fx29b E).
        if (reason.Contains("no permission that belongs", StringComparison.OrdinalIgnoreCase)) return NotSaved;
        // Taken back and given again mid-score: advice is on now, so "off" would be false (fx29b G).
        if (reason.Contains("taken back and given again", StringComparison.OrdinalIgnoreCase)) return AdviceChanged;
        // Every way permission can be missing for this model (DecisionConsent.Covers): never given, withdrawn, out of date, or
        // short of a detail this point sends. A licensed person with advice off was told the feature is not out (rc28 gap 1).
        if (reason.Contains("permission", StringComparison.OrdinalIgnoreCase)) return AdviceOff;
        return Missing;   // the service, the template or the reply: a real service fault, so the calm line
    }
}
