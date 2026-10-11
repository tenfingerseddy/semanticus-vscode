// What the doors need from the engine for the spec-first start (lane sf-doors): the pointer the Plan carries while a
// start plan exists, and the one call every start-plan writer makes to tell both doors it moved. The verbs themselves
// (LocalEngine.StartPlan.cs and the lanes behind it) own behaviour; nothing here decides anything.

using System;
using System.Threading.Tasks;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Tell both doors the start plan moved. <paramref name="origin"/> is "human", "agent", or null for the
        /// engine's own work (the decide loop, a restart): the Studio is pushed the whole view as
        /// <c>startPlan/didChange</c> and every OTHER assistant reads it as one line on its next call. Every writer of the
        /// plan calls this after its change lands. Never throws into the change that caused it.</summary>
        /// <summary>An undo or a redo can take a build back or bring it again, and the plan's page shows "built" from what the model holds NOW. Neither moves the plan's own document, so no
        /// start-plan event followed and a page open on the plan kept saying it was built (UAT-J7-S1, after Undo). Read the plan as it stands and say it moved. Never throws.</summary>
        internal async Task RefreshStartPlanAfterHistoryAsync(string origin)
        {
            try
            {
                var view = await GetStartPlanAsync().ConfigureAwait(false);
                if (view != null && view.Exists) NotifyStartPlanChanged(view, origin);
            }
            catch { /* the undo happened; a page that could not be told reads the plan again on its next call */ }
        }

        internal void NotifyStartPlanChanged(StartPlanView view, string origin = null)
        {
            try { _sessions.Bus.PublishStartPlan(view, origin); }
            catch { /* a broken listener must never fail a plan edit */ }
        }

        /// <summary>The pointer a Plan read carries: where the start plan is up to, or null when none exists. Read fresh
        /// every time and never remembered: whether a plan is parked depends on the model open NOW, and a swap raises no
        /// start-plan event, so a remembered pointer would go stale the moment the person opened another model. With no
        /// plan the read is a directory check, so a model without one pays nothing.</summary>
        internal async Task<StartPlanPointer> StartPlanPointerAsync()
        {
            try { return StartPlanDoors.PointerOf(await GetStartPlanAsync().ConfigureAwait(false)); }
            catch (Exception) { return null; }   // a build without the verbs, or a read that failed: the Plan reads as before
        }

        /// <summary>The name of the model open now, for the notice a start gives when it closes one; null when none is open.</summary>
        private async Task<string> OpenModelNameForNoticeAsync()
        {
            if (_sessions.Current == null) return null;
            try { return (await SessionInfoAsync().ConfigureAwait(false))?.ModelName; }
            catch { return null; }
        }
    }
}
