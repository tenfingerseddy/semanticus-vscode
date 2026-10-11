// The real scratch host of the decide loop (lane sf-decide): a private clone of the person's empty model (sf-core's StartPlanScratch), the plan's
// manifest built into it (sf-manifest's ToSpec through the build's own BuildSpecInto), and every kept line applied by the SHARED executor
// (sf-exec's PlanLineExecutor), in ONE write on the scratch. A line the executor refuses comes back with its reason, so the loop leaves it out and
// the plan shows only what the build will make.
//
// The scratch is never the open model: it has its own bus, nothing it does reaches the person's undo, history or live window.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.StartPlan
{
    internal sealed class EngineStartScratchHost : IStartScratchHost
    {
        private readonly LocalEngine _engine;
        private readonly Func<Task<StartPlanScratch>> _open;

        /// <param name="open">Where the clone comes from. Null is the decide loop's own slot (the engine keeps ONE and replaces it each round); the words
        /// preview passes its own, so reading the words never takes the loop's scratch out from under it.</param>
        public EngineStartScratchHost(LocalEngine engine, Func<Task<StartPlanScratch>> open = null) { _engine = engine; _open = open; }

        public async Task<StartScratch> BuildAsync(StartPlanDoc doc, CancellationToken ct)
        {
            var scratch = _open != null ? await _open().ConfigureAwait(false) : await _engine.OpenStartScratchAsync().ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var spec = doc.Manifest.ToSpec();
                var kept = doc.Lines.Where(l => l.Kept).Select(l => (Line: l, Item: StartPlanDecide.ToChangeItem(l))).ToList();
                var applied = new List<(StartLine Line, ChangeItem Item)>();
                await scratch.Session.MutateAsync("human", "start plan scratch", m =>
                {
                    LocalEngine.BuildSpecIntoScratch(m, spec);
                    // The assistant's words on an object this scratch does not have (its table is left out) are HELD: kept on the plan, not applied, not refused.
                    var present = StartPlanWords.PresentRefs(doc, m);
                    var live = kept.Where(k => !StartPlanWords.IsHeld(k.Line, present)).ToList();
                    // A kept name is an answer, not a change: it is carried out as an acceptance after the lines (StartPlanBuild.AcceptKeptNames), as the build does it.
                    var keeps = live.Where(k => StartPlanBuild.IsKeptName(k.Item)).ToList();
                    applied = live.Where(k => !StartPlanBuild.IsKeptName(k.Item)).ToList();
                    var items = applied.Select(k => k.Item).ToList();
                    if (items.Count > 0)
                        PlanLineExecutor.ApplyInBatch(_engine, m, scratch.Session,
                            items, new PlanLineOptions { KeepGoingPastInUseRemovals = true, KeepGoingPastFormat = true, CloseNeedsAgainst = items });
                    StartPlanBuild.AcceptKeptNames(doc, m, keeps);
                }).ConfigureAwait(false);
                var refused = applied.Where(k => !string.Equals(k.Item.Status, "applied", StringComparison.OrdinalIgnoreCase))
                    .Select(k => (k.Line, k.Item.Note)).ToList();
                var result = new StartScratch { Session = scratch.Session, Refused = refused, OnDispose = () => { try { scratch.Dispose(); } catch { } } };
                // THE SAME FACTS BEFORE AND AFTER THE BUILD. When a sample of the source was read for this plan, the scratch carries the facts sf-sample works out
                // from it, laid over the scratch's own tables by SOURCE name, exactly as the model the build makes will serve them. Jev then sees on every round what
                // the Plan's own run will see on the built model, so that run asks nothing new. No sample: no facts, as before.
                // The sample goes in only while the person's stored permission for this plan covers it (the one gate, LocalEngine.SampleDisclosedAsync): the rounds ask
                // Jev from these facts, so a sample the person said Not now to, or never allowed, never reaches a request.
                // The sample is cut to the tables the grant names before any fact from it is laid over the scratch (LocalEngine.SampleForJevAsync).
                var sample = await _engine.SampleForJevAsync(doc.Id, _engine.StartSampleFor(doc)).ConfigureAwait(false);
                if (sample != null)
                {
                    var facts = await _engine.BuildSampleFactsAsync(scratch.Session, sample).ConfigureAwait(false);
                    result.Facts = () => facts;
                    result.ExecutorFacts = () => new Facts.JevModelFactsAdapter(facts);
                }
                return result;
            }
            catch
            {
                try { await scratch.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }
}
