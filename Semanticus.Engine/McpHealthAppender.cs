using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The SUCCESS-path sibling of <see cref="McpErrorBoundary"/> (feature #4, "health delta everywhere"):
    /// after a mutating tool call commits, append ONE terse block to the tool's own result —
    /// <c>health: {"grade":"B-&gt;C","new":["SYN-MISSING"],"findings":2,"impact":3}</c> — the ground-truth
    /// feedback loop (docs/harness-engineering.md §3) made ambient: the agent self-corrects in the same turn.
    ///
    /// Correlation is BY TOOL-CALL IDENTITY: this filter mints an id per call, flows it ambiently
    /// (<see cref="HealthCorrelation"/>) into every commit the call performs, and drains exactly that id from
    /// the engine's <see cref="AgentHealthMailbox"/> — so two parallel mutating calls each get their own delta
    /// and a model swap mid-call can't cross-attribute. Read-only calls stash nothing; the free tier and
    /// below-threshold edits stash nothing — so the common case costs zero tokens (P-Efficiency). Works
    /// identically when the MCP process OWNS the model (LocalEngine) or ATTACHES to a running engine
    /// (RemoteEngine pulls the owner's mailbox over the pipe; those commits ride the unscoped slot).
    ///
    /// A call that COMMITS then THROWS must not read as pure failure — the human chip already showed the delta
    /// at commit time, so the doors would disagree. The drained health rides the exception's Data (the same
    /// instance rethrows, so the tool's teaching error text is untouched) and <see cref="McpErrorBoundary"/>
    /// surfaces it on the error result; when this filter sits OUTSIDE the boundary it appends to the IsError
    /// result directly. Either order yields one honest block. The ride-along must never break a tool result,
    /// hence the blanket catches.
    /// </summary>
    internal static class McpHealthAppender
    {
        /// <summary>Exception.Data key carrying the display text of a committed-then-threw call's health block
        /// (set here, surfaced by <see cref="McpErrorBoundary"/>).</summary>
        internal const string HealthDataKey = "semanticus.healthDelta";

        /// <summary>The fixed opening of the user-edit fold line (F-246).</summary>
        internal const string FoldPrefix = "changed since your last call";

        private static readonly JsonSerializerOptions Terse = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,   // omit-when-unchanged, field by field
        };

        /// <summary>The call-tool filter registered in Program.Mcp via WithRequestFilters (after the error
        /// boundary; order-agnostic — it tolerates both a thrown exception and an IsError result from next).</summary>
        public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
            IEngine engine, McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) => InvokeAsync(engine, () => next(request, ct), request?.Params?.Name);

        /// <summary>The testable core: mint the call id, run the tool under it, then drain-and-append this
        /// call's health block (success or failure — failure carries it on the exception's Data).</summary>
        internal static ValueTask<CallToolResult> InvokeAsync(IEngine engine, Func<ValueTask<CallToolResult>> next)
            => InvokeAsync(engine, next, null);

        internal static async ValueTask<CallToolResult> InvokeAsync(IEngine engine, Func<ValueTask<CallToolResult>> next, string toolName)
        {
            var callId = Guid.NewGuid().ToString("N");
            CallToolResult result;
            using (HealthCorrelation.Begin(callId))
            using (TaskCause(engine, ChangeJournal.InProcessReader))
            {
                try { result = await next().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    var health = await DrainAsync(engine, callId).ConfigureAwait(false);
                    if (health != null)
                    {
                        // Same instance rethrows (type + teaching message intact); the boundary reads Data.
                        try { ex.Data[HealthDataKey] = FailureText(health); } catch { /* Data may be read-only */ }
                    }
                    throw;
                }
            }
            var delta = await DrainAsync(engine, callId).ConfigureAwait(false);
            if (delta != null && result != null)
                Append(result, result.IsError == true ? FailureText(delta) : SuccessText(delta));
            if (result != null)
            {
                // F-246: what somebody else changed since this assistant's previous call. A call that throws skips
                // this, so the next call still carries it.
                var fold = FoldText(await PullForeignAsync(engine).ConfigureAwait(false));
                if (fold != null) Append(result, fold);
                // Plan at the heart (contract 6.1): the tasks the person gave this assistant, only when the offer
                // changed for this reader, so an unchanged offer costs nothing. The owner path asks the engine in
                // this process; the attached path pulls the owner's answer for its own pipe connection.
                var plan = await PullPlanOfferAsync(engine).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(plan)) Append(result, plan);
            }
            if (result != null && result.IsError != true)
            {
                var note = await WorkflowProfileNoteAsync(engine, toolName).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(note)) Append(result, note);
            }
            return result;
        }

        /// <summary>The user-edit fold line (F-246), short and stable:
        /// <c>changed since your last call (in Studio): measure:Sales/Amount, table:Date; rev 4812</c>.
        /// Null when nothing changed, so the common case costs nothing.</summary>
        internal static string FoldText(ForeignChanges c)
        {
            if (c == null) return null;
            var model = ModelFoldText(c);
            // The spec-first start plan (M11) rides the same take: a sentence the engine already worded, appended as its
            // own line, so a plan change alone is still said and an older engine (no StartPlan) changes nothing here.
            var plan = string.IsNullOrWhiteSpace(c.StartPlan) ? null : c.StartPlan;
            return model == null ? plan : plan == null ? model : model + "\n" + plan;
        }

        private static string ModelFoldText(ForeignChanges c)
        {
            if (c.Items == null || c.Items.Length == 0) return null;
            var who = c.Studio && c.Assistant ? "in Studio and by another assistant"
                : c.Studio ? "in Studio" : "by another assistant";
            var list = string.Join(", ", c.Items)
                + (c.More > 0 ? $", and {c.More} more" : "")
                + (c.Truncated ? ", and earlier changes" : "");
            return $"{FoldPrefix} ({who}): {list}; rev {c.Revision}";
        }

        private static async Task<ForeignChanges> PullForeignAsync(IEngine engine)
        {
            try { return await engine.PullForeignChangesAsync().ConfigureAwait(false); }
            catch { return null; }   // an owner that predates the fold, or a dropped pipe: the line is best-effort
        }

        /// <summary>Contract 8.4, lane E3: while this door's assistant holds a claim, its edits count toward the task it
        /// claimed most recently; while this reader has a guided run on a step, toward the run instead
        /// (LocalEngine.RunCause.cs). Read once, BEFORE the call runs, so a claim made inside this call starts counting on
        /// the next one. The owner path only: an attached assistant's edits are stamped in the owner, per request, by
        /// RpcServer. An explicit cause opened deeper (a plan approve) still wins, because it is the inner scope.</summary>
        internal static IDisposable TaskCause(IEngine engine, string writer)
        {
            var local = engine as LocalEngine;
            // Contract 8.4, lane m11/plan-gaps: a reader with a guided run on a step files its edits under the run.
            var run = local?.RunCauseFor(writer);
            if (run != null) return ChangeCause.Begin(run);
            var task = local?.WorkingTaskFor(writer);
            return task == null ? null : (IDisposable)ChangeCause.Begin(ChangeCauseKinds.Task, local.WorkingTaskTitleFor(writer), null, task);
        }

        private static async Task<string> PullPlanOfferAsync(IEngine engine)
        {
            try { return await engine.PullPlanOfferAsync().ConfigureAwait(false); }
            catch { return null; }   // an owner that predates the queue, or a dropped pipe: the line is best-effort
        }

        private static string SuccessText(HealthDelta h) => "health: " + JsonSerializer.Serialize(h, Terse);

        // The honest commit-then-throw message: the call FAILED, but these changes were already committed and
        // model health moved — pure-failure retry logic would double-apply. Keep it terse and actionable.
        private static string FailureText(HealthDelta h) =>
            "health: " + JsonSerializer.Serialize(h, Terse)
            + ". The call failed but these changes were already committed (model health moved as shown). "
            + "Verify the model state before retrying; undo_change can revert the committed part.";

        /// <summary>Append one text block, defensively: Content may be null or a non-resizable IList (a fixed
        /// array wrapper would throw NotSupportedException straight into the silent catch) — copy to a fresh
        /// List when it isn't appendable.</summary>
        private static void Append(CallToolResult result, string text)
        {
            try
            {
                var block = new TextContentBlock { Text = text };
                var content = result.Content;
                if (content == null)
                {
                    result.Content = new List<ContentBlock> { block };
                    return;
                }
                try { content.Add(block); }
                catch (NotSupportedException)
                {
                    result.Content = new List<ContentBlock>(content) { block };
                }
            }
            catch { /* the ride-along must never break the tool result */ }
        }

        private static async ValueTask<HealthDelta> DrainAsync(IEngine engine, string callId)
        {
            try { return await engine.PullAgentHealthAsync(callId).ConfigureAwait(false); }
            catch { return null; }   // e.g. the owner pipe dropped mid-call — health is best-effort
        }

        /// <summary>When a warn-mode workflow profile is active, name its promise on the bound authoring ops
        /// so the agent door sees the same guidance Studio already shows.</summary>
        internal static async Task<string> WorkflowProfileNoteAsync(IEngine engine, string toolName)
        {
            try
            {
                if (engine == null || string.IsNullOrWhiteSpace(toolName)) return null;
                if (!string.Equals(toolName, "create_measure", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(toolName, "update_measure", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(toolName, "create_relationship", StringComparison.OrdinalIgnoreCase))
                    return null;
                // Workflows is a Pro feature as a whole, reads included, so on the free tier this refuses and the
                // catch below returns null: a free session simply gets no workflow advisory, which is correct.
                // The refusal is thrown at operation entry, so it costs nothing beyond the call itself.
                var policy = await engine.GetWorkflowPolicyAsync().ConfigureAwait(false);
                var binding = policy?.Bindings?.FirstOrDefault(b => string.Equals(b.Op, toolName, StringComparison.OrdinalIgnoreCase));
                if (binding == null || !string.Equals(binding.Mode, "warn", StringComparison.OrdinalIgnoreCase))
                    return null;
                // Name the workflow ids the agent can pass to start_workflow. The profile's Effects are Studio copy
                // written for people ("Every workflow stays available"), and they name no id the agent can act on.
                var require = binding.Require ?? Array.Empty<string>();
                if (require.Length == 0) return null;
                var profiles = await engine.ListWorkflowProfilesAsync().ConfigureAwait(false);
                var title = profiles?.FirstOrDefault(p => p.Selected)?.Title ?? "The active";
                return title + " workflow profile expects " + toolName + " to run inside the "
                    + string.Join(" or ", require.Select(r => "'" + r + "'"))
                    + " workflow (start it with start_workflow). This profile only warns, so work can continue without it.";
            }
            catch { return null; }
        }
    }
}
