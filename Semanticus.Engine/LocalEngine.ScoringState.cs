using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    /// <summary>Why a Totals or Description score is not on a measure now, read from the real state: the licence, the switch,
    /// permission for this model, a saved model, the allowance (DecisionService.WouldNotAskAsync, its gates in its order) and,
    /// for the Description score, a Description to score. The one place every seed and fallback asks (fx29c: the seeds and
    /// fallbacks each wrote the calm "turns on when Jev scoring is released" line, so a session with no licence read it at
    /// open and after every run). Nothing stops it: <see cref="ScoringCopy.NotChecked"/>.</summary>
    internal async Task<UnscoredReasons> UnscoredReasonsAsync()
    {
        var pro = Entitlement.FeatureGrants.IsProTier(_entitlement);
        return new(await Why(TotalsQuestions.TypePoint, DecisionPointEnabled(TotalsQuestions.TypePoint)).ConfigureAwait(false),
            await Why(DescriptionMatchQuestions.Point, DescriptionMatchActive).ConfigureAwait(false));

        async Task<string> Why(string point, bool enabled)
        {
            try
            {
                var why = await Decisions.WouldNotAskAsync(point, pro, enabled).ConfigureAwait(false);
                return why == null ? ScoringCopy.NotChecked : ScoringCopy.Reason(DecisionOutcome.Skipped(why));
            }
            catch { return ScoringCopy.Unavailable; }
        }
    }

    // One settle at a time, so the last one writes the state as it reads last.
    private readonly SemaphoreSlim settleLines = new(1, 1);

    /// <summary>Permission or the licence changed: every stored unscored line is written again as the state reads now, and
    /// pushed, so a door that holds a record reads the true line without reading again (fx30e K1, K5). Settle touches unscored
    /// lines only, so a scored total is never thrown away (K1 addendum). The reads settle each line anyway.</summary>
    internal async Task SettleStoredLinesAsync(Session session)
    {
        await settleLines.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session == null || _sessions.Current != session) return;
            var unscored = await UnscoredReasonsAsync().ConfigureAwait(false);
            var descriptions = await session.ReadAsync(model => model.AllMeasures
                .ToDictionary(m => ObjectRefs.For(m), m => m.Description, StringComparer.Ordinal)).ConfigureAwait(false);
            string[] references;
            lock (measureRunLock)
            {
                if (_sessions.Current != session) return;
                LoadMeasureCache(session);
                references = measureCheckCache.Keys.ToArray();
            }
            var wrote = false;
            foreach (var reference in references)
                wrote |= WriteCheckState(session, reference, state =>
                {
                    var description = descriptions.GetValueOrDefault(reference);
                    var marks = unscored.Settle(state.Marks, description);
                    var cheap = state.Cheap == null ? null : unscored.Settle(state.Cheap, description);
                    return marks.SequenceEqual(state.Marks) && (cheap == null || cheap.SequenceEqual(state.Cheap)) ? null
                        : state with { Marks = marks, Cheap = cheap };
                }) != null;
            if (wrote) lock (measureRunLock) if (_sessions.Current == session) SaveMeasureCache(session);
        }
        catch (Exception) { /* observational: a read settles every line anyway */ }
        finally { settleLines.Release(); }
    }

    internal sealed record UnscoredReasons(string Totals, string Described)
    {
        internal string Description(string text) => string.IsNullOrWhiteSpace(text) ? ScoringCopy.NoDescription : Described;

        /// <summary>A score that failed while nothing in the state stopped it: that is a service fault.</summary>
        internal string TotalsFault => Totals == ScoringCopy.NotChecked ? ScoringCopy.Unavailable : Totals;   // service fault

        /// <summary>An unscored line as it is true now. A seed, the calm line or a check still waiting gives way to what stops
        /// scoring now. A line the state decided (the licence, the switch, the allowance, permission, a saved model) always
        /// follows the state now (fx30e K5: "advice is off" outlived the licence that went; K1: "advice is on now" outlived the
        /// permission taken back). A fault is kept, and so is a waiting check that nothing stops; a check on its way while
        /// nothing stops it is checking now, never a line that asks for a check (fx30e K3).</summary>
        internal MeasureMark[] Settle(MeasureMark[] marks, string description) => marks.Select(m =>
            // A result from before an edit is not an unscored line: it waits for its rerun (fx34).
            m.Total != null || m.State != "not run" || m.ReasonCode == "stale" ? m
            : m.Name == "Totals" ? Settled(m, Totals)
            : m.Name == "Description match" ? Settled(m, Description(description))
            : m).ToArray();

        private static MeasureMark Settled(MeasureMark mark, string reason)
        {
            var now = ScoringCopy.Sentence(reason);
            if (mark.Sentence == now) return mark;
            var free = reason == ScoringCopy.NotChecked;
            // The one permission line still true while nothing stops scoring: a score dropped mid-flight asks for a check.
            var stale = mark.Sentence == ScoringCopy.Sentence(ScoringCopy.NoDescription) || ScoringCopy.StateLine(mark.Sentence)
                || ScoringCopy.ConsentLine(mark.Sentence) && !(free && mark.Sentence.EndsWith(ScoringCopy.AdviceChanged, StringComparison.Ordinal))
                || !free && (mark.Sentence == ScoringCopy.Sentence(ScoringCopy.NotChecked) || ScoringCopy.Calm(mark.Sentence) || mark.ReasonCode == "waiting");
            if (!stale) return mark;
            if (free && mark.Pending)
                return MeasureCheckVerdict.Finish(new MeasureMark { Name = mark.Name, State = "not run", Sentence = "This check is waiting to run.",
                    ReasonCode = "waiting", Pending = true }, null, false);
            return MeasureCheckVerdict.Finish(ScoringCopy.NotScored(mark.Name, reason), null, false);
        }
    }
}
