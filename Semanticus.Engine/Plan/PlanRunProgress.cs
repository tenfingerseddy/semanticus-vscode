// What the window hears while Run the plan works (round 1, 8 Oct 2026; Kane: "nothing on screen said it ran").
//
// The run is one long call. It used to say nothing until it ended, so the rows stayed "Ready" for 40 to 60 seconds and then
// were gone. Each step now publishes one of these on the ChangeBus; the RpcServer sends it to the UI door as
// plan/runProgress. It is observational: it changes nothing and no door depends on it. The agent door gets no push (golden
// rule 2) and reads the same facts whole in the run's result (entryId, waves, itemIds, skipped).

using System;

namespace Semanticus.Engine
{
    /// <summary>The steps one row goes through in a run, in order: work, apply, check, then done (or skipped, instead).</summary>
    public static class PlanRunPhases
    {
        /// <summary>The row has no worked-out changes yet and they are being worked out. Writes nothing.</summary>
        public const string Work = "work";
        /// <summary>The row's changes are about to go into the one batch.</summary>
        public const string Apply = "apply";
        /// <summary>The batch is written; the plan is being read again to check it.</summary>
        public const string Check = "check";
        /// <summary>The row's changes are in.</summary>
        public const string Done = "done";
        /// <summary>The run left the row out; <see cref="PlanRunProgress.Reason"/> says why.</summary>
        public const string Skipped = "skipped";
        /// <summary>The press waits for Jev to look again at the row (at most 15 seconds, once per press); <see cref="PlanRunProgress.WaitedSeconds"/> counts it.</summary>
        public const string WaitJev = "waitjev";
    }

    /// <summary>One step of a run, for the rows named in <see cref="ItemIds"/>.</summary>
    public sealed class PlanRunProgress
    {
        /// <summary>The run these steps belong to; the same on every event of one press and on the result.</summary>
        public string RunId { get; set; }

        /// <summary>The rows this step is about: one row for work, apply, done and skipped; every row of the wave for check.</summary>
        public string[] ItemIds { get; set; } = Array.Empty<string>();

        /// <summary>One of <see cref="PlanRunPhases"/>.</summary>
        public string Phase { get; set; }

        /// <summary>1 for the first batch of the press. A row that waited for a row in wave 1 runs in wave 2, in the same press.</summary>
        public int Wave { get; set; }

        /// <summary>For apply and done: how many changes this row writes (apply) or wrote (done). Null otherwise.</summary>
        public int? Changes { get; set; }

        /// <summary>For skipped: why the row was left out, in a sentence. Null otherwise.</summary>
        public string Reason { get; set; }

        /// <summary>For waitjev: the seconds the press has waited for Jev so far. Null otherwise.</summary>
        public int? WaitedSeconds { get; set; }

        /// <summary>For waitjev: what Jev is being asked right now, in plain words with the real count ("Jev is choosing how 48 columns add up
        /// and what Copilot sees in 396 fields"), so the wait says what it is for. Null when no request is out.</summary>
        public string Choosing { get; set; }

        /// <summary>Milliseconds since the press started (one stopwatch for the whole run), when this step began. The gap between two
        /// events is how long the step before it took, which is how a later job sees where the time goes.</summary>
        public long Ms { get; set; }
    }
}
