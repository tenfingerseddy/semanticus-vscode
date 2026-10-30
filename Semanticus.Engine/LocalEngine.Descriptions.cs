using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Jev;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    public const string DescriptionProvenanceAnnotation = "Semanticus.Description.Provenance";
    // Kane enabled alignment v2 with the measured 70/50 bands on 27 October 2026.
    internal bool DescriptionMatchEnabled { get; set; } = true;
    internal bool DescriptionMatchActive => DescriptionMatchEnabled && DecisionPointEnabled(DescriptionMatchQuestions.Point);
    internal IDescriptionMatchScorer DescriptionMatchScorer { get; set; }
    internal Func<DateTimeOffset> DescriptionNow { get; set; } = () => DateTimeOffset.UtcNow;
    private readonly object _descriptionGate = new();
    private readonly CancellationTokenSource _descriptionStop = new();
    private readonly Dictionary<string, PendingDescription> _descriptionProposals = new();
    private readonly Dictionary<(string Session, string Ref), (DescriptionMatchInput Input, Task<DescriptionMatch> Match)> _descriptionMatches = new();
    // No reason: not scored for a reason the state decides, so the door that hands it out asks UnscoredReasonsAsync (fx29c).
    private static DescriptionMatch Unscored(string reason = null) => new(null, "not scored yet", reason, "not scored yet");
    internal static string DescriptionLine(string text) => string.Join(" ", (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    internal static string DescriptionSource(Measure measure) => measure.GetAnnotation(DescriptionProvenanceAnnotation) switch {
        "person" => "person", "ai" => "ai", _ => "not recorded"
    };

    private sealed class PendingDescription
    {
        internal Session Session;
        internal Measure Measure;
        internal string Ref;
        internal DescriptionMatchInput Before;
        internal DescriptionProposal View;
        internal readonly SemaphoreSlim Refresh = new(1, 1);
    }
    private static Measure DescriptionMeasure(Model model, string reference) => ObjectRefs.Resolve(model, reference) as Measure
        ?? throw new InvalidOperationException("Choose a measure to read or draft its Description.");
    private static DescriptionMatchInput DescriptionInput(Measure measure) => new(measure.Description ?? "", measure.Expression ?? "", measure.Name);

    private void ResetDescriptions()
    {
        lock (_descriptionGate) { _descriptionProposals.Clear(); _descriptionMatches.Clear(); }
    }

    public async Task<MeasureDescription> GetMeasureDescriptionAsync(string measureRef)
    {
        var session = _sessions.Require();
        var snapshot = await session.ReadAsync(model => {
            var measure = DescriptionMeasure(model, measureRef);
            return (Input: DescriptionInput(measure), Source: DescriptionSource(measure));
        }).ConfigureAwait(false);
        PendingDescription pending;
        lock (_descriptionGate) pending = _descriptionProposals.Values.LastOrDefault(p => p.Session == session && p.Ref == measureRef);
        if (pending != null) await RefreshDescriptionAsync(pending).ConfigureAwait(false);
        var match = DescriptionMatchNow(session, measureRef, snapshot.Input);
        if (match.Score == null && match.Reason == null)
            match = match with { Reason = (await UnscoredReasonsAsync().ConfigureAwait(false)).Description(snapshot.Input.Description) };
        DescriptionProposal proposal;
        lock (_descriptionGate) proposal = pending?.View;
        return new(measureRef, snapshot.Input.Name, snapshot.Input.Description, DescriptionLine(snapshot.Input.Description), snapshot.Source, match, proposal);
    }

    // The Description score as the card shows it now. Text is model state; a remote score must never delay a kept
    // Description or its Undo/Redo, so a score still on its way reads not scored and a push announces it.
    private DescriptionMatch DescriptionMatchNow(Session session, string reference, DescriptionMatchInput input)
    {
        var scoring = ReadDescriptionMatchAsync(session, reference, input);
        return scoring.IsCompletedSuccessfully ? scoring.Result : Unscored();
    }

    /// <summary>The Alignment mark is the Description score (d1 s2), read from the card's own source so the assistant door
    /// and the card say the same (fx29b B). It is never one of the seven: it does not set the measure's verdict.
    /// Matches reads passed, Could be improved is a score only (info), Does not match is a reason to look (suspect).</summary>
    /// <paramref name="unscored"/>: why it is not scored now, for a score not asked yet or still on its way (fx29c).
    internal static MeasureMark AlignmentMark(DescriptionMatch match, string unscored) => MeasureCheckVerdict.Finish(match?.Score is int score
        ? new MeasureMark { Name = "Alignment", ReasonCode = "description_score",
            State = match.Level switch { "matches" => "passed", "does not match" => "suspect", _ => "info" },
            Sentence = $"Description score {score}, {match.Label}. {match.Reason}".TrimEnd() }
        : ScoringCopy.NotScored("Alignment", match?.Reason ?? unscored), null, false);

    private static MeasureMark[] WithAlignment(MeasureMark[] marks, DescriptionMatch match, string unscored) =>
        marks.Select(m => m.Name == "Alignment" ? AlignmentMark(match, unscored) : m).ToArray();

    private Task<DescriptionMatch> ReadDescriptionMatchAsync(Session session, string reference, DescriptionMatchInput input)
    {
        if (!DescriptionMatchActive || string.IsNullOrWhiteSpace(input.Description)) return Task.FromResult(Unscored());
        lock (_descriptionGate)
        {
            var key = (session.Id, reference);
            if (_descriptionMatches.TryGetValue(key, out var cached) && cached.Input == input) return cached.Match;
            var task = ScoreDescriptionAsync(session, input);
            _descriptionMatches[key] = (input, task);
            _ = AnnounceDescriptionScoreAsync(session, reference, input, task);
            return task;
        }
    }

    private async Task AnnounceDescriptionScoreAsync(Session session, string reference, DescriptionMatchInput input, Task<DescriptionMatch> scoring)
    {
        try
        {
            await scoring.ConfigureAwait(false);
            if (_sessions.Current != session || _descriptionStop.IsCancellationRequested) return;
            var current = await session.ReadAsync(m => ObjectRefs.Resolve(m, reference) is Measure measure && DescriptionInput(measure) == input).ConfigureAwait(false);
            lock (_descriptionGate)
                if (!current || !_descriptionMatches.TryGetValue((session.Id, reference), out var cached) || cached.Match != scoring) return;
            _sessions.Bus.Publish(new ChangeNotification { SessionId = session.Id, Revision = session.Revision,
                Origin = "system", Label = "Description score updated.", Deltas = Array.Empty<ChangeDelta>() });
        }
        catch { /* A score finishing after a model closes cannot delay or change the text. */ }
    }

    private DescriptionMatch CachedDescriptionMatch(Session session, string reference, DescriptionMatchInput input)
    {
        if (!DescriptionMatchActive) return Unscored();
        lock (_descriptionGate)
            return _descriptionMatches.TryGetValue((session.Id, reference), out var cached)
                && cached.Input == input && cached.Match.IsCompletedSuccessfully ? cached.Match.Result : Unscored();
    }

    private async Task<DescriptionMatch> ScoreDescriptionAsync(Session session, DescriptionMatchInput input)
    {
        try
        {
            var scorer = DescriptionMatchScorer ?? new JevDescriptionMatchScorer(async (snapshot, ct) => {
                var fence = new DecisionSessionFence(session.Id, session.Revision, () => {
                    var current = _sessions.Current;
                    return (current?.Id, current?.Revision ?? -1);
                });
                return await Decisions.AskAsync(DescriptionMatchQuestions.Point, DescriptionMatchQuestions.Snapshot(snapshot),
                    DescriptionMatchQuestions.Questions, Entitlement.FeatureGrants.IsProTier(_entitlement),
                    DescriptionMatchActive, fence, ct).ConfigureAwait(false);
            });
            return await scorer.ScoreAsync(input, _descriptionStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Unscored("The Description was not scored."); }
        catch { return Unscored(ScoringCopy.Unavailable); }
    }

    private void DescriptionChanged(ChangeNotification change)
    {
        if (!DescriptionMatchActive) return;
        var session = _sessions.Current;
        if (session?.Id != change.SessionId) return;
        foreach (var reference in (change.Deltas ?? Array.Empty<ChangeDelta>())
            .Where(d => d.Ref?.StartsWith("measure:", StringComparison.Ordinal) == true
                && (d.Props == null || d.Props.Any(p => p is "Description" or "Expression" or "Name")))
            .Select(d => d.Ref).Distinct())
            _ = RescoreDescriptionAsync(session, reference);
    }

    private async Task RescoreDescriptionAsync(Session session, string reference)
    {
        try
        {
            var input = await session.ReadAsync(m => DescriptionInput(DescriptionMeasure(m, reference))).ConfigureAwait(false);
            await ReadDescriptionMatchAsync(session, reference, input).ConfigureAwait(false);
        }
        catch { /* A removed or replaced measure has no current score. */ }
    }

    public Task<DescriptionProposal> DraftMeasureDescriptionAsync(string measureRef, int timeoutSeconds = 60) =>
        RequestDescriptionAsync(measureRef, false, timeoutSeconds);
    public Task<DescriptionProposal> ProposeMeasureDescriptionAsync(string measureRef, int timeoutSeconds = 60) =>
        RequestDescriptionAsync(measureRef, true, timeoutSeconds);

    private async Task<DescriptionProposal> RequestDescriptionAsync(string reference, bool improve, int timeoutSeconds)
    {
        // An absent assistant is answered without a dispatcher or queue wait.
        if (!AssistantPresence.IsAssistantAttached)
            return new(null, "unavailable", "No assistant is connected to this session. Connect your assistant, then request a Description.", null, null, null);
        if (timeoutSeconds < 1 || timeoutSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Choose a Description wait between 1 and 3600 seconds.");
        var session = _sessions.Require();
        var coverage = CheckedCoverageFor(session);
        var snapshot = await session.ReadAsync(model => {
            var measure = DescriptionMeasure(model, reference);
            var input = DescriptionInput(measure);
            if (!improve && !string.IsNullOrWhiteSpace(input.Description))
                throw new InvalidOperationException("This measure has a Description. Use Propose a better Description.");
            object Related(Measure m) => new { Ref = ObjectRefs.For(m), m.Name, Formula = m.Expression, m.Description, m.DisplayFolder };
            var visuals = Lineage.LineageGraph.AnalyzeReports(model, coverage.Parts).Reports
                .SelectMany(report => report.Visuals.Where(v => v.UsedRefs.Contains(reference))
                    .Select(v => new { Report = report.Name, v.Page, v.Visual, v.VisualType })).ToArray();
            return (Measure: measure, Input: input, Context: (object)new {
                Name = input.Name, Formula = input.Formula, measure.DisplayFolder,
                Uses = measure.DependsOn.Keys.OfType<Measure>().Select(Related).ToArray(),
                UsedBy = measure.ReferencedBy.OfType<Measure>().Select(Related).ToArray(),
                Visuals = visuals,
                VisualContext = coverage.Complete || coverage.NoReports ? "The selected reports were checked." : "Only known visuals are included. Report use may be incomplete."
            });
        }).ConfigureAwait(false);
        var match = improve ? await ReadDescriptionMatchAsync(session, reference, snapshot.Input).ConfigureAwait(false) : Unscored();
        var id = "description-" + Guid.NewGuid().ToString("N");
        var deadline = DescriptionNow().AddSeconds(timeoutSeconds);
        var context = new {
            Instruction = "Draft a detailed Description from the formula AND the name, folder, related measures and visual context. Return only the proposed Description text with complete_request. Do not write the Description. The person keeps the proposal.",
            Context = snapshot.Context,
            CurrentDescription = improve ? snapshot.Input.Description : null,
            MatchReason = improve ? match.Reason ?? match.Label : null
        };
        var brief = JsonSerializer.Serialize(context);
        if (brief.Length > PlanTasks.MaxBriefChars)
            brief = "Draft the Description from the complete context in get_measure_description for " + reference
                + ". Read proposal.context before drafting. It includes the formula AND the name, folder, related measures, visuals, current Description and match reason. Return only the proposal text with complete_request. Do not write the Description.";
        var posted = await AssistantRequests.PostAsync(new AssistantRequestPost {
            JobId = id, SessionId = session.Id, Need = (improve ? "Propose a better Description for " : "Draft a Description for ") + snapshot.Input.Name,
            Brief = brief, Objects = new[] { reference }, AllowedContext = new[] { "formula", "name", "displayFolder", "uses", "usedBy", "visuals", "currentDescription", "matchReason" }
        }).ConfigureAwait(false);
        if (!posted.Ok) return new(id, "unavailable", posted.Message, null, null, null);
        var pending = new PendingDescription {
            Session = session, Measure = snapshot.Measure, Ref = reference, Before = snapshot.Input,
            View = new(id, "waiting", $"Waiting for your assistant to draft a Description. The wait ends after {timeoutSeconds} seconds.", null, deadline, posted.Request.RequestId) { Context = context }
        };
        lock (_descriptionGate) _descriptionProposals[id] = pending;
        _ = MonitorDescriptionAsync(pending);
        return pending.View;
    }

    private async Task MonitorDescriptionAsync(PendingDescription pending)
    {
        try
        {
            while (!_descriptionStop.IsCancellationRequested && _sessions.Current == pending.Session)
            {
                await Task.Delay(250, _descriptionStop.Token).ConfigureAwait(false);
                await RefreshDescriptionAsync(pending).ConfigureAwait(false);
                lock (_descriptionGate) if (pending.View.State != "waiting") return;
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            lock (_descriptionGate) if (pending.View.State == "waiting")
                pending.View = pending.View with { State = "unavailable", Message = "The Description request could not finish. Request it again." };
        }
    }

    private async Task RefreshDescriptionAsync(PendingDescription pending)
    {
        await pending.Refresh.WaitAsync(_descriptionStop.Token).ConfigureAwait(false);
        try
        {
            lock (_descriptionGate) if (pending.View.State != "waiting") return;
            if (_sessions.Current != pending.Session) return;
            var reply = EveryAssistantRequest().FirstOrDefault(r => r.RequestId == pending.View.RequestId);
            string state = null, message = null, text = null;
            var tooLate = DescriptionNow() >= pending.View.Deadline;
            var answeredInTime = reply?.State == AssistantRequestState.Completed
                && DateTimeOffset.TryParse(reply.ClosedUtc, out var closed) && closed <= pending.View.Deadline;
            if (answeredInTime && !string.IsNullOrWhiteSpace(reply.Answer))
            { state = "ready"; message = "Review the proposed Description, then press Keep Description. Undo can restore the previous Description."; text = reply.Answer.Trim(); }
            else if (tooLate)
            { state = "timedOut"; message = "Your assistant did not answer in time. No Description was changed. Request it again when your assistant is ready."; }
            else if (!AssistantPresence.IsAssistantAttached)
            { state = "unavailable"; message = "Your assistant disconnected. No Description was changed. Connect your assistant, then request it again."; }
            else if (reply == null || reply.State is AssistantRequestState.Cancelled or AssistantRequestState.Declined
                || reply.State == AssistantRequestState.Completed)
            { state = "declined"; message = string.IsNullOrWhiteSpace(reply?.DeclineReason) ? "Your assistant returned no Description. Request it again." : reply.DeclineReason; }
            if (state == null) return;
            lock (_descriptionGate) pending.View = pending.View with { State = state, Message = message, Text = text };
            if (state != "ready") await AssistantRequests.CancelForJobAsync(pending.View.Id, message).ConfigureAwait(false);
        }
        finally { pending.Refresh.Release(); }
    }

    public async Task<SetResult> KeepMeasureDescriptionAsync(string proposalId, string origin)
    {
        var rehearsal = DryRunScope.Current != null;
        PendingDescription pending;
        lock (_descriptionGate) _descriptionProposals.TryGetValue(proposalId ?? "", out pending);
        if (pending == null || _sessions.Current != pending.Session)
            throw new InvalidOperationException("This Description proposal belongs to a model that is no longer open. Request a new proposal.");
        await RefreshDescriptionAsync(pending).ConfigureAwait(false);
        string text;
        lock (_descriptionGate)
        {
            if (pending.View.State != "ready") throw new InvalidOperationException("There is no ready Description proposal to keep. Request a new proposal.");
            text = pending.View.Text;
            pending.View = pending.View with { State = "keeping" };
        }
        try
        {
            var revision = await pending.Session.MutateAsync(origin, "keep proposed Description", model => {
                if (_sessions.Current != pending.Session || ObjectRefs.Resolve(model, pending.Ref) is not Measure measure
                    || !ReferenceEquals(measure, pending.Measure) || DescriptionInput(measure) != pending.Before)
                    throw new InvalidOperationException("The measure changed since this Description was requested. Request a new proposal.");
                measure.Description = text;
                measure.SetAnnotation(DescriptionProvenanceAnnotation, "ai");
            }).ConfigureAwait(false);
            lock (_descriptionGate) pending.View = pending.View with {
                State = rehearsal ? "ready" : "kept",
                Message = rehearsal ? "The proposal is ready to keep. No Description was changed." : "Description kept. Undo restores the previous Description."
            };
            return new SetResult { Revision = revision, Changed = true };
        }
        catch
        {
            lock (_descriptionGate) pending.View = pending.View with { State = "ready" };
            throw;
        }
    }
}
