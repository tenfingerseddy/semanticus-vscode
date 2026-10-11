// The assistant's queue as a view over the Plan (Plan at the heart, lane E3; contract sections 3.2, 5.4 and 6).
//
// This file holds three things and no state of its own:
//   PlanOffer       the offer read off one composed plan: every Ready task for the assistant in the person's order,
//                   the ones not offered yet and why, and which offered tasks are independent of each other.
//   the wording     the fold line, the paused line and the list sentence, in one place so they cannot drift.
//   PlanTaskQueue   IAssistantRequestQueue over the engine's tasks, so a job and the composer post, list, claim,
//                   answer and decline through the same owner records both doors read.
//
// INDEPENDENCE IS PAIRWISE AND CAREFUL (contract 5.4). Two offered tasks are independent only when both footprints
// are known and neither conflicts with the other under PlanFootprint's rule. A task whose footprint is unknown is
// never called independent: a wrong "independent" is forbidden, a wrong "one at a time" only costs time.
//
// THE OFFER IS IN THE PERSON'S ORDER (UAT-S09-1-001). Up is the person saying which task comes next, and the contract
// reads the queue in that order (4.1, Up). A task Semanticus cannot read keeps its place and carries the "cannot
// tell" note there. It used to be moved behind the rest, so Up on it changed the Plan and nothing the assistant read.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Jev
{
    /// <summary>What list_assistant_requests returns.</summary>
    public sealed class AssistantRequestList
    {
        /// <summary>"on" or "paused": the person's switch for tasks for you.</summary>
        public string Switch { get; set; }
        /// <summary>The offer, in the order to read it: the person's order. Empty while paused.</summary>
        public AssistantRequest[] Requests { get; set; } = Array.Empty<AssistantRequest>();
        /// <summary>The tasks you hold now. You may finish them even while tasks are paused.</summary>
        public AssistantRequest[] Held { get; set; } = Array.Empty<AssistantRequest>();
        /// <summary>Tasks for you that are not offered yet, each with the row it waits for.</summary>
        public PlanNotOffered[] NotOffered { get; set; } = Array.Empty<PlanNotOffered>();
        public string Sentence { get; set; }
    }

    public sealed class PlanNotOffered
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string WaitsFor { get; set; }
    }

    /// <summary>get_model_summary's ActiveWork.Tasks (contract 6.2). Omitted when there is nothing to say.</summary>
    public sealed class ActiveTasksBrief
    {
        public string Switch { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ActiveTaskRef[] Ready { get; set; }
        public int Waiting { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ActiveTaskRef[] Claimed { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }
        /// <summary>The open composer drafts (Plan-heart contract 14: ActiveWork.Drafts becomes part of Tasks). A draft's
        /// parts waiting for you are also in Ready, each naming its draft.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ActiveDraftRef[] Drafts { get; set; }
    }

    public sealed class ActiveTaskRef
    {
        public string Id { get; set; }
        public string Title { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Independent { get; set; }
        /// <summary>The draft this task is a part of, when it is one.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Draft { get; set; }
    }

    /// <summary>One open draft as the summary carries it.</summary>
    public sealed class ActiveDraftRef
    {
        public string DraftId { get; set; }
        public string Title { get; set; }
        /// <summary>The Plan's word for the draft's row.</summary>
        public string State { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }
        public int PartsWaiting { get; set; }
    }

    /// <summary>The offer, read off one composed plan.</summary>
    internal sealed class PlanOffer
    {
        public string Switch = PlanAssistantQueue.On;
        /// <summary>Ready tasks for the assistant, in the person's order. Empty while paused.</summary>
        public List<ModelPlanItem> Ready = new List<ModelPlanItem>();
        /// <summary>Open tasks for the assistant that wait for another row.</summary>
        public List<ModelPlanItem> Waiting = new List<ModelPlanItem>();
        /// <summary>Tasks an assistant holds now.</summary>
        public List<ModelPlanItem> Claimed = new List<ModelPlanItem>();
        /// <summary>Pairs of offered tasks that must be taken one at a time, with the thing they share.</summary>
        public List<(string A, string B, string Key)> Overlaps = new List<(string, string, string)>();
        /// <summary>The title of each open draft on the plan, by draft id, so its parts can sit under its line.</summary>
        public Dictionary<string, string> DraftTitles = new Dictionary<string, string>(StringComparer.Ordinal);

        public static PlanOffer From(ModelPlanView plan)
        {
            var o = new PlanOffer { Switch = plan?.AssistantQueue == PlanAssistantQueue.Paused ? PlanAssistantQueue.Paused : PlanAssistantQueue.On };
            foreach (var d in (plan?.Items ?? Array.Empty<ModelPlanItem>()).Where(i => Composer.DraftRows.IsDraftRow(i.Id)))
                o.DraftTitles[d.Id.Substring(Composer.DraftRows.Prefix.Length)] = d.Title;
            var mine = (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => PlanTasks.IsTaskId(i.Id) && i.Doer == PlanDoers.Assistant)
                .ToList();
            o.Claimed = (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => PlanTasks.IsTaskId(i.Id) && i.Claim != null && i.Claim.ClaimedBy != PlanDoers.You && i.StateWord == PlanStateWords.OnIt)
                .ToList();
            o.Waiting = mine.Where(i => i.StateWord == PlanStateWords.Waiting).ToList();
            if (o.Switch == PlanAssistantQueue.Paused) return o;

            o.Ready = mine.Where(i => i.StateWord == PlanStateWords.Ready)
                          .OrderBy(i => i.Order ?? int.MaxValue).ToList();
            for (var a = 0; a < o.Ready.Count; a++)
            for (var b = a + 1; b < o.Ready.Count; b++)
            {
                var fa = FootprintOf(o.Ready[a]); var fb = FootprintOf(o.Ready[b]);
                if (!fa.Known || !fb.Known || !PlanFootprint.Conflict(fa, fb)) continue;
                o.Overlaps.Add((o.Ready[a].Id, o.Ready[b].Id, SharedKey(fa, fb)));
            }
            return o;
        }

        /// <summary>The offer in the order it is read: the person's order, the order the Plan shows and Up moves. A
        /// task Semanticus has nothing to go on for keeps its place and carries the note (UAT-S09-1-001).</summary>
        public List<ModelPlanItem> Listed() => Ready.ToList();

        /// <summary>The draft a ready task is a part of, or null for a task of its own.</summary>
        internal static string DraftOf(ModelPlanItem r) =>
            r?.OriginLink?.Kind == PlanOriginKinds.Draft && !string.IsNullOrWhiteSpace(r.OriginLink.Ref) ? r.OriginLink.Ref : null;

        /// <summary>The numbered lines of the offer (contract 6.1): a task of its own is one line, and a draft's parts
        /// share one line under their draft, at the place of its first part.</summary>
        public List<(string Draft, List<ModelPlanItem> Rows)> Entries()
        {
            var entries = new List<(string Draft, List<ModelPlanItem> Rows)>();
            var byDraft = new Dictionary<string, List<ModelPlanItem>>(StringComparer.Ordinal);
            foreach (var r in Listed())
            {
                var draft = DraftOf(r);
                if (draft == null) { entries.Add((null, new List<ModelPlanItem> { r })); continue; }
                if (byDraft.TryGetValue(draft, out var parts)) { parts.Add(r); continue; }
                byDraft[draft] = parts = new List<ModelPlanItem> { r };
                entries.Add((draft, parts));
            }
            return entries;
        }

        /// <summary>The number the fold gives each offered task; a draft's parts share their draft's number.</summary>
        public Dictionary<string, int> Numbers()
        {
            var number = new Dictionary<string, int>(StringComparer.Ordinal);
            var entries = Entries();
            for (var k = 0; k < entries.Count; k++)
                foreach (var r in entries[k].Rows) number[r.Id] = k + 1;
            return number;
        }

        /// <summary>True when Semanticus can name something the row touches: a known footprint, or the floor of an
        /// unknown one (a task's attached objects). Only a row with neither carries the "cannot tell" note. A floor
        /// is never independence: that needs <see cref="ModelPlanItem.FootprintKnown"/>.</summary>
        internal static bool HasFloor(ModelPlanItem r) => r.FootprintKnown || (r.Writes?.Length ?? 0) > 0;

        public bool IsIndependent(string id)
        {
            // Pairwise over the offer (contract 5.4): independent of EVERY other offered task, so one task Semanticus
            // cannot read leaves no pair it is part of independent.
            var row = Ready.FirstOrDefault(r => r.Id == id);
            return row != null && Ready.All(r => r.FootprintKnown) && !Overlaps.Any(p => p.A == id || p.B == id);
        }

        internal static Footprint FootprintOf(ModelPlanItem i)
        {
            if (!i.FootprintKnown) return Footprint.Unknown();
            return new Footprint().ReadAll(i.Reads).WriteAll(i.Writes);
        }

        /// <summary>The first thing two conflicting footprints share, an object before a model-wide fact.</summary>
        private static string SharedKey(Footprint a, Footprint b)
        {
            var shared = a.Writes.Where(w => !PlanFootprint.EventFacets.Contains(w) && b.Writes.Contains(w))
                .Concat(a.Writes.Where(b.Reads.Contains))
                .Concat(b.Writes.Where(a.Reads.Contains))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return shared.OrderBy(k => k.StartsWith("model:", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        }
    }

    /// <summary>The wording the assistant reads, and the IAssistantRequestQueue over the engine's tasks.</summary>
    public sealed class PlanTaskQueue : IAssistantRequestQueue
    {
        internal const string LinePrefix = "Plan:";
        internal const int MaxListed = 5;
        internal const int MaxChars = 600;
        private const int MaxTitle = 90;

        internal const string AllIndependentSentence = "They do not depend on each other, so take more than one if you can.";
        internal const string OneAtATimeSentence = "Take them one at a time.";
        internal const string UnknownFootprintNote = "Semanticus cannot tell what this one touches.";
        internal const string PausedSentence = "Plan: tasks are paused by the person. Carry on with what they ask you directly.";
        internal const string Footer = "Call list_assistant_requests, then claim_request.";

        /// <summary>"Plan: 2 tasks are ready for you." plus the independence sentence when every pair is independent.</summary>
        internal static string Header(PlanOffer o, int count)
        {
            var head = LinePrefix + " " + count.ToString(CultureInfo.InvariantCulture)
                     + (count == 1 ? " task is ready for you." : " tasks are ready for you.");
            if (count > 1 && o.Ready.All(r => r.FootprintKnown) && o.Overlaps.Count == 0)
                head += " " + AllIndependentSentence;
            return head;
        }

        /// <summary>The fold line (contract 6.1): at most five tasks then "and N more", at most 600 characters,
        /// titles as the person or the engine wrote them. Null when nothing is offered.</summary>
        internal static string OfferLine(PlanOffer o)
        {
            if (o == null || o.Switch == PlanAssistantQueue.Paused || o.Ready.Count == 0) return null;
            var listed = o.Entries();
            var shown = Math.Min(MaxListed, listed.Count);
            var waiting = o.Waiting.Count;
            var number = o.Numbers();

            while (true)
            {
                var lines = new List<string> { Header(o, listed.Count) };
                for (var k = 0; k < shown; k++)
                {
                    var (draft, rows) = listed[k];
                    var n = (k + 1).ToString(CultureInfo.InvariantCulture) + ". ";
                    if (draft != null)
                    {
                        // Draft blanks appear under their draft's line (contract 6.1).
                        var title = o.DraftTitles.TryGetValue(draft, out var t) && !string.IsNullOrWhiteSpace(t) ? t : "A draft";
                        lines.Add(n + Short(title) + " (draft " + draft + "): " + rows.Count.ToString(CultureInfo.InvariantCulture)
                                  + (rows.Count == 1 ? " part to write." : " parts to write."));
                        continue;
                    }
                    var r = rows[0];
                    lines.Add(n + Short(r.Title) + " (" + r.Id + ")" + (PlanOffer.HasFloor(r) ? "" : " " + UnknownFootprintNote));
                }
                if (listed.Count > shown) lines.Add("and " + (listed.Count - shown).ToString(CultureInfo.InvariantCulture) + " more");
                // Two parts of one draft share its line, so a pair inside one draft is not named.
                var pairs = o.Overlaps.Where(p => number[p.A] <= shown && number[p.B] <= shown && number[p.A] != number[p.B])
                    .GroupBy(p => (Math.Min(number[p.A], number[p.B]), Math.Max(number[p.A], number[p.B])))
                    .Select(g => g.First()).Take(3).ToList();
                if (pairs.Count > 0)
                    lines.Add(string.Join(" ", pairs.Select(p =>
                        number[p.A].ToString(CultureInfo.InvariantCulture) + " and " + number[p.B].ToString(CultureInfo.InvariantCulture)
                        + " both change " + Plain(p.Key) + ".")) + " " + OneAtATimeSentence);
                foreach (var w in o.Waiting.Take(waiting))
                    lines.Add("Not offered yet: " + Short(w.Title) + ". It waits for " + Short(w.WaitsFor?.FirstOrDefault()?.Title ?? "another row") + ".");
                lines.Add(Footer);

                var text = string.Join("\n", lines);
                if (text.Length <= MaxChars) return text;
                // Too long: drop the not-offered lines first, then list fewer tasks.
                if (waiting > 0) { waiting--; continue; }
                if (shown > 1) { shown--; continue; }
                return text.Substring(0, MaxChars);
            }
        }

        /// <summary>The paused line (contract 4.1), with the tasks this assistant already holds and may finish.</summary>
        internal static string PausedLine(IEnumerable<string> heldTitles)
        {
            var lines = new List<string> { PausedSentence };
            lines.AddRange((heldTitles ?? Enumerable.Empty<string>()).Take(MaxListed).Select(t => Short(t) + ": you may finish it."));
            var text = string.Join("\n", lines);
            return text.Length <= MaxChars ? text : text.Substring(0, MaxChars);
        }

        private static string Short(string title)
        {
            var t = (title ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            return t.Length <= MaxTitle ? t : t.Substring(0, MaxTitle - 3).TrimEnd() + "...";
        }

        /// <summary>A footprint key in plain words: an object by its name, a model-wide fact by what it is.</summary>
        internal static string Plain(string key)
        {
            if (string.IsNullOrEmpty(key)) return "the same thing";
            if (key.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(key, PlanFootprint.Objects, StringComparison.OrdinalIgnoreCase)) return "which objects the model has";
                if (string.Equals(key, PlanFootprint.Revision, StringComparison.OrdinalIgnoreCase)) return "the model";
                if (string.Equals(key, PlanFootprint.DateTable, StringComparison.OrdinalIgnoreCase)) return "the date table";
                if (key.StartsWith("model:relationships:", StringComparison.OrdinalIgnoreCase))
                    return "the relationships at " + key.Substring("model:relationships:".Length);
                return "the model's " + key.Substring("model:".Length).Replace('-', ' ');
            }
            return PlanTasks.NameOfRef(key);
        }

        // ---- IAssistantRequestQueue over the engine's tasks ---------------------------------------------------

        private readonly LocalEngine _engine;
        private readonly IHandoffConsent _consent;
        private bool _withdrawn;

        internal PlanTaskQueue(LocalEngine engine, IHandoffConsent consent = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _consent = consent ?? new AlwaysConsented();
        }

        private bool Consented => !_withdrawn && _consent.HasConsent;

        private static HandoffResult Withdrawn() => new HandoffResult
        {
            Refusal = HandoffRefusal.ConsentWithdrawn,
            Message = "Consent is off, so the request was cancelled and the reply was not used.",
        };

        /// <summary>Posting no longer refuses for absence (contract 3.2 and the AMENDS): the task is the owner record
        /// and waits with or without an assistant attached. It still refuses while consent is off.</summary>
        public async Task<HandoffResult> PostAsync(AssistantRequestPost post, CancellationToken ct = default)
        {
            if (post == null) throw new ArgumentNullException(nameof(post));
            if (!Consented)
                return new HandoffResult
                {
                    Refusal = HandoffRefusal.NoConsent,
                    Message = "Nothing is sent to your assistant while consent is off. Turn it on in Settings, or the job stops at a stub.",
                };
            return await _engine.PostJobRequestAsync(post).ConfigureAwait(false);
        }

        /// <summary>What is offered and held. With <paramref name="includeClosed"/>, every other task too, in its true
        /// state: a job reads its request back this way to resume (InMemoryJobRunner.ResumeAsync), so an answered one
        /// must come back Completed with its reply and a handed-back one Declined with its reason, never as gone (Codex
        /// on pull request 393).</summary>
        public async Task<IReadOnlyList<AssistantRequest>> ListAsync(string sessionId, bool includeClosed = false, CancellationToken ct = default)
        {
            var list = await _engine.ListAssistantRequestsAsync().ConfigureAwait(false);
            var all = list.Requests.Concat(list.Held).ToList();
            if (includeClosed)
            {
                var listed = new HashSet<string>(all.Select(r => r.RequestId), StringComparer.Ordinal);
                all.AddRange(_engine.EveryAssistantRequest().Where(r => listed.Add(r.RequestId)));
            }
            return all.ToArray();
        }

        public Task<HandoffResult> ClaimAsync(string requestId, string claimantId, CancellationToken ct = default) =>
            Consented ? _engine.ClaimAssistantRequestAsync(requestId, claimantId) : Task.FromResult(Withdrawn());

        public Task<HandoffResult> CompleteAsync(string requestId, string claimToken, AssistantAnswer answer, CancellationToken ct = default) =>
            Consented ? _engine.CompleteAssistantRequestAsync(requestId, claimToken, answer?.Text) : Task.FromResult(Withdrawn());

        public Task<HandoffResult> DeclineAsync(string requestId, string claimToken, string reason, CancellationToken ct = default) =>
            Consented ? _engine.DeclineAssistantRequestAsync(requestId, claimToken, reason) : Task.FromResult(Withdrawn());

        public Task<int> CancelForJobAsync(string jobId, string reason, CancellationToken ct = default) =>
            _engine.SetAsideJobRequestsAsync(t => string.Equals(t.OriginLink?.Ref, jobId, StringComparison.Ordinal),
                string.IsNullOrWhiteSpace(reason) ? "The job was cancelled." : reason);

        /// <summary>Consent withdrawn: every task a job posted is set aside and every later reply refused. The
        /// person's own tasks stay: they are the person's asks, not requests a job made.</summary>
        public Task<int> WithdrawConsentAsync(CancellationToken ct = default)
        {
            _withdrawn = true;
            return _engine.SetAsideJobRequestsAsync(_ => true,
                "Consent was withdrawn, so this request was cancelled and nothing was sent.");
        }

        public AssistantPendingSummary PendingFor(string sessionId) => _engine.PendingPlanRequests();
    }
}
