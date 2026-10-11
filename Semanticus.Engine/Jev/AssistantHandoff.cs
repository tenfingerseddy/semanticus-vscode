using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Jev
{
    /// <summary>
    /// M0a contract 3: THE ASSISTANT HANDOFF (docs/redesign/contracts/assistant-handoff.md).
    ///
    /// A connected MCP client is NOT an agent waiting for work, and nothing in this engine can push it a task.
    /// <c>RpcServer.cs</c> broadcasts model/didChange to the PIPE clients only; <c>RemoteEngine.cs</c> registers
    /// no notification handler and the MCP door has never been sent a notification of any kind. So the handoff
    /// is a PULL contract: the engine owns a request queue, a job that needs words posts to it and SUSPENDS, and
    /// the assistant learns of the request the same way it learns of a change, folded into its next tool result
    /// by the health appender.
    ///
    /// The honest cost, stated rather than designed away: an assistant that makes no tool call never learns the
    /// request exists. The queue therefore never promises delivery, only availability, and every request carries
    /// a posted time so a person can see how long it has waited. A genuine push becomes legal for the first time
    /// under the spec's subscriptions/listen, which is [T175] and not this contract.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AssistantRequestState
    {
        Queued,
        Claimed,
        Completed,
        Declined,
        /// <summary>Cancelled because the job was cancelled, or because consent was withdrawn.</summary>
        Cancelled,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HandoffRefusal
    {
        None,
        NotFound,
        AlreadyClaimed,
        NotClaimed,
        /// <summary>The claim token does not match the current claim. A late reply from a stale claim.</summary>
        WrongClaimToken,
        AlreadyTerminal,
        /// <summary>Consent was withdrawn, so no metadata may leave and no late reply is accepted.</summary>
        ConsentWithdrawn,
        NoConsent,
        /// <summary>Consent is on, but there is no assistant attached to this session to ask. Distinct from
        /// <see cref="NoConsent"/> on purpose: one is a permission, the other is a presence, and a job that
        /// waits forever because nobody is listening is the failure this separates out.</summary>
        NoAssistantAttached,
        /// <summary>The person paused tasks for their assistant (Plan at the heart, contract 3.2 and 4). A claim
        /// already held may finish; nothing new is handed out.</summary>
        QueuePaused,
        /// <summary>The task is not ready to be taken: it waits for another row, the person paused it, or it is the
        /// person's to do. The message names which.</summary>
        NotReady,
        /// <summary>A draft's part did not pass its written checks (composer contract 5.4). The claim is kept and a try
        /// is counted; the message names each failed check.</summary>
        ChecksFailed,
    }

    /// <summary>One request for words, owned by the engine and readable from both doors.</summary>
    public sealed class AssistantRequest
    {
        public string RequestId { get; set; }
        public string JobId { get; set; }
        public string SessionId { get; set; }
        /// <summary>The named need, in plain words, e.g. "a description for measure Sales[Margin]".</summary>
        public string Need { get; set; }
        /// <summary>The field allow-list this request may read. Nothing outside it is offered, ever.</summary>
        public string[] AllowedContext { get; set; } = Array.Empty<string>();
        public string PostedUtc { get; set; }
        public AssistantRequestState State { get; set; } = AssistantRequestState.Queued;
        public string ClaimedBy { get; set; }
        public string ClaimedUtc { get; set; }
        /// <summary>Returned to the claimant only. complete and decline need it, so a second claimant with the
        /// same client id still cannot answer a claim it did not make.</summary>
        public string ClaimToken { get; set; }
        public string Answer { get; set; }
        public string DeclineReason { get; set; }
        public string ClosedUtc { get; set; }

        // ---- the packet a Plan task carries (contract 3.4). Empty on a request that is not a Plan task. ----

        /// <summary>Place in the person's order for you, 1 first. Null when the request is not in the offer.</summary>
        public int? Order { get; set; }
        /// <summary>True when this task changes nothing another offered task touches, so it can be taken beside
        /// them (contract 5.4). False when it overlaps one, or when Semanticus cannot tell what it touches.</summary>
        public bool Independent { get; set; }
        /// <summary>One plain line about the task's place in the offer, e.g. "Semanticus cannot tell what this one
        /// touches."</summary>
        public string OfferNote { get; set; }
        /// <summary>The person's note on the task.</summary>
        public string Note { get; set; }
        /// <summary>The engine-built brief a Send from a page carried.</summary>
        public string Brief { get; set; }
        /// <summary>The person's whole text, when it was longer than the task's title. Sent on a claim only.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string FullText { get; set; }
        /// <summary>The objects the task is about, as refs.</summary>
        public string[] Objects { get; set; } = Array.Empty<string>();
        /// <summary>How done is known, as the engine says it.</summary>
        public string DoneText { get; set; }
        /// <summary>For a draft's part: the tries left before it goes to the person (composer contract 5.4).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? AttemptsLeft { get; set; }
        /// <summary>The grounding for the task's objects, filled on a claim only.</summary>
        public Semanticus.Analysis.GroundingBundle[] Grounding { get; set; }

        public AssistantRequest Copy() => (AssistantRequest)MemberwiseClone();

        /// <summary>The copy that goes OUT to a lister who has not claimed it. The claim token never leaves the
        /// engine except in the claim result itself.</summary>
        public AssistantRequest Public()
        {
            var c = Copy();
            c.ClaimToken = null;
            return c;
        }
    }

    public sealed class AssistantRequestPost
    {
        public string JobId { get; set; }
        public string SessionId { get; set; }
        public string Need { get; set; }
        public string[] AllowedContext { get; set; } = Array.Empty<string>();
        /// <summary>The packet a draft's part carries (composer contract 5.3): notes, brief, what to write, checks.</summary>
        public string Brief { get; set; }
        /// <summary>The objects the part is about that exist in the model now, for the claim's grounding.</summary>
        public string[] Objects { get; set; }
    }

    public sealed class AssistantAnswer
    {
        /// <summary>The words. Plain text; the queue does not interpret it.</summary>
        public string Text { get; set; }
    }

    public sealed class HandoffResult
    {
        public AssistantRequest Request { get; set; }
        public HandoffRefusal Refusal { get; set; } = HandoffRefusal.None;
        public string Message { get; set; }
        public bool Ok => Refusal == HandoffRefusal.None;
    }

    /// <summary>What the health appender folds into every MCP result while requests are pending.</summary>
    public sealed class AssistantPendingSummary
    {
        public int Pending { get; set; }
        public PendingRequestLine[] Requests { get; set; } = Array.Empty<PendingRequestLine>();
    }

    public sealed class PendingRequestLine
    {
        public string Id { get; set; }
        public string Job { get; set; }
        public string Need { get; set; }
        public string WaitingSince { get; set; }
    }

    /// <summary>Consent, as the queue sees it. Withdrawing cancels every queued and claimed request and
    /// suppresses every late reply.</summary>
    public interface IHandoffConsent
    {
        bool HasConsent { get; }
    }

    public sealed class AlwaysConsented : IHandoffConsent
    {
        public bool HasConsent => true;
    }

    /// <summary>
    /// Whether an assistant is actually ATTACHED to this session right now. Astra B8: the queue checked consent
    /// and nothing else, so with consent on and no assistant connected a job queued a request and waited for a
    /// reply that could never come. Consent is a permission; this is a presence. Both have to hold.
    ///
    /// THE REAL SIGNAL, for the lane that wires this up. The engine already knows, in two shapes:
    ///   - the MCP process OWNS the model (Program.cs Mcp, the LocalEngine branch): the MCP client is this
    ///     process's own stdio peer, so an assistant is attached by construction;
    ///   - the MCP process ATTACHES to a running engine over the named pipe: RpcServer.cs:102-119 classifies
    ///     every connection as Human or agent and keeps them in <c>_clients</c>, so an attached assistant is an
    ///     agent-role client in that list. RpcServer must expose that count; it is one property, and it is
    ///     outside this contract's files.
    /// Until that property exists the engine supplies <see cref="DelegatedAssistantPresence"/> over whatever it
    /// can answer, and a build with no answer at all uses <see cref="NoAssistantAttached"/> so a job stops at a
    /// named stub instead of waiting on nobody.
    /// </summary>
    public interface IAssistantPresence
    {
        bool IsAssistantAttached { get; }
        /// <summary>Plain words for a false <see cref="IsAssistantAttached"/>.</summary>
        string AbsenceReason { get; }
    }

    /// <summary>The reference queue's default. It has no transport to ask, so it answers yes and says so
    /// rather than pretending to know. A real door passes <see cref="DelegatedAssistantPresence"/>.</summary>
    public sealed class AssistantAssumedPresent : IAssistantPresence
    {
        public bool IsAssistantAttached => true;
        public string AbsenceReason => "";
    }

    /// <summary>The honest stub for a build that cannot answer the question, and the fixture for the test that
    /// proves a job stops rather than waits.</summary>
    public sealed class NoAssistantAttached : IAssistantPresence
    {
        public bool IsAssistantAttached => false;
        public string AbsenceReason => "No assistant is connected to this session.";
    }

    /// <summary>Presence read from whatever the engine can answer, e.g. the agent-role client count on
    /// RpcServer. See <see cref="IAssistantPresence"/> for the two real signals.</summary>
    public sealed class DelegatedAssistantPresence : IAssistantPresence
    {
        private readonly Func<bool> _attached;
        private readonly string _reason;

        public DelegatedAssistantPresence(Func<bool> attached, string absenceReason = null)
        {
            _attached = attached ?? throw new ArgumentNullException(nameof(attached));
            _reason = string.IsNullOrWhiteSpace(absenceReason)
                ? "No assistant is connected to this session."
                : absenceReason;
        }

        public bool IsAssistantAttached { get { try { return _attached(); } catch { return false; } } }
        public string AbsenceReason => _reason;
    }

    /// <summary>
    /// The engine-owned request queue. Operation names, identical on both doors:
    /// list_assistant_requests, claim_request, complete_request, decline_request. Posting is not a door
    /// operation: only a job posts, so a client cannot invent work for another client.
    /// </summary>
    public interface IAssistantRequestQueue
    {
        Task<HandoffResult> PostAsync(AssistantRequestPost post, CancellationToken ct = default);
        Task<IReadOnlyList<AssistantRequest>> ListAsync(string sessionId, bool includeClosed = false, CancellationToken ct = default);
        Task<HandoffResult> ClaimAsync(string requestId, string claimantId, CancellationToken ct = default);
        Task<HandoffResult> CompleteAsync(string requestId, string claimToken, AssistantAnswer answer, CancellationToken ct = default);
        Task<HandoffResult> DeclineAsync(string requestId, string claimToken, string reason, CancellationToken ct = default);
        /// <summary>Cancel every open request belonging to a job. Returns how many were cancelled.</summary>
        Task<int> CancelForJobAsync(string jobId, string reason, CancellationToken ct = default);
        /// <summary>Consent withdrawal: cancel every open request and refuse every later reply.</summary>
        Task<int> WithdrawConsentAsync(CancellationToken ct = default);
        /// <summary>The block the health appender folds into a tool result. Null when nothing is pending, so the
        /// common case costs zero tokens, exactly like the health delta.</summary>
        AssistantPendingSummary PendingFor(string sessionId);
    }

    /// <summary>
    /// The in-memory reference queue. The later lane replaces the store, not the rules.
    /// </summary>
    public sealed class InMemoryAssistantRequestQueue : IAssistantRequestQueue
    {
        private readonly object _lock = new object();
        private readonly List<AssistantRequest> _requests = new List<AssistantRequest>();
        private readonly IHandoffConsent _consent;
        private readonly IAssistantPresence _presence;
        private bool _withdrawn;
        private int _seq;

        /// <summary>Test seam: a fixed clock keeps the posted times deterministic.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public InMemoryAssistantRequestQueue(IHandoffConsent consent = null, IAssistantPresence presence = null)
        {
            _consent = consent ?? new AlwaysConsented();
            _presence = presence ?? new AssistantAssumedPresent();
        }

        private bool Consented => !_withdrawn && _consent.HasConsent;

        public Task<HandoffResult> PostAsync(AssistantRequestPost post, CancellationToken ct = default)
        {
            if (post == null) throw new ArgumentNullException(nameof(post));
            lock (_lock)
            {
                if (!Consented)
                    return Task.FromResult(new HandoffResult
                    {
                        Refusal = HandoffRefusal.NoConsent,
                        Message = "Nothing is sent to your assistant while consent is off. Turn it on in Settings, or the job stops at a stub.",
                    });

                // Permission is not presence. Posting to a queue nobody is reading is how a job waits forever,
                // so the promised stop needs this second answer as well.
                if (!_presence.IsAssistantAttached)
                    return Task.FromResult(new HandoffResult
                    {
                        Refusal = HandoffRefusal.NoAssistantAttached,
                        Message = (string.IsNullOrWhiteSpace(_presence.AbsenceReason)
                                      ? "No assistant is connected to this session."
                                      : _presence.AbsenceReason.Trim())
                                  + " Nothing was queued, and the job stops at a stub rather than waiting.",
                    });

                var r = new AssistantRequest
                {
                    RequestId = "ar-" + (++_seq),
                    JobId = post.JobId,
                    SessionId = post.SessionId,
                    Need = post.Need,
                    AllowedContext = post.AllowedContext ?? Array.Empty<string>(),
                    PostedUtc = UtcNow().ToString("o"),
                    State = AssistantRequestState.Queued,
                };
                _requests.Add(r);
                return Task.FromResult(new HandoffResult { Request = r.Public() });
            }
        }

        public Task<IReadOnlyList<AssistantRequest>> ListAsync(string sessionId, bool includeClosed = false, CancellationToken ct = default)
        {
            lock (_lock)
            {
                IReadOnlyList<AssistantRequest> list = _requests
                    .Where(r => sessionId == null || string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                    .Where(r => includeClosed || r.State == AssistantRequestState.Queued || r.State == AssistantRequestState.Claimed)
                    .Select(r => r.Public())
                    .ToArray();
                return Task.FromResult(list);
            }
        }

        public Task<HandoffResult> ClaimAsync(string requestId, string claimantId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var r = Find(requestId);
                if (r == null) return NotFound(requestId);
                if (!Consented) return Withdrawn();
                if (r.State == AssistantRequestState.Claimed)
                    return Task.FromResult(new HandoffResult
                    {
                        Request = r.Public(),
                        Refusal = HandoffRefusal.AlreadyClaimed,
                        Message = $"Request {r.RequestId} is already being answered by {r.ClaimedBy}.",
                    });
                if (r.State != AssistantRequestState.Queued)
                    return Terminal(r);

                r.State = AssistantRequestState.Claimed;
                r.ClaimedBy = claimantId;
                r.ClaimedUtc = UtcNow().ToString("o");
                r.ClaimToken = "ct-" + Guid.NewGuid().ToString("N");
                // The claim result is the ONE place the token leaves the engine.
                return Task.FromResult(new HandoffResult { Request = r.Copy() });
            }
        }

        public Task<HandoffResult> CompleteAsync(string requestId, string claimToken, AssistantAnswer answer, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var r = Find(requestId);
                if (r == null) return NotFound(requestId);
                // Consent is checked BEFORE the state, so a late reply after a withdrawal is suppressed and the
                // answer is discarded rather than stored and then ignored.
                if (!Consented) return Withdrawn();
                if (r.State == AssistantRequestState.Cancelled || r.State == AssistantRequestState.Completed || r.State == AssistantRequestState.Declined)
                    return Terminal(r);
                if (r.State != AssistantRequestState.Claimed) return NotClaimed(r);
                if (!string.Equals(r.ClaimToken, claimToken, StringComparison.Ordinal)) return WrongToken(r);

                r.State = AssistantRequestState.Completed;
                r.Answer = answer?.Text ?? "";
                r.ClosedUtc = UtcNow().ToString("o");
                return Task.FromResult(new HandoffResult { Request = r.Public() });
            }
        }

        public Task<HandoffResult> DeclineAsync(string requestId, string claimToken, string reason, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var r = Find(requestId);
                if (r == null) return NotFound(requestId);
                if (!Consented) return Withdrawn();
                if (r.State == AssistantRequestState.Cancelled || r.State == AssistantRequestState.Completed || r.State == AssistantRequestState.Declined)
                    return Terminal(r);
                if (r.State != AssistantRequestState.Claimed) return NotClaimed(r);
                if (!string.Equals(r.ClaimToken, claimToken, StringComparison.Ordinal)) return WrongToken(r);

                r.State = AssistantRequestState.Declined;
                r.DeclineReason = reason ?? "";
                r.ClosedUtc = UtcNow().ToString("o");
                return Task.FromResult(new HandoffResult { Request = r.Public() });
            }
        }

        public Task<int> CancelForJobAsync(string jobId, string reason, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var r in _requests.Where(x => string.Equals(x.JobId, jobId, StringComparison.Ordinal)
                    && (x.State == AssistantRequestState.Queued || x.State == AssistantRequestState.Claimed)))
                {
                    r.State = AssistantRequestState.Cancelled;
                    r.DeclineReason = reason;
                    r.ClosedUtc = UtcNow().ToString("o");
                    r.ClaimToken = null;
                    n++;
                }
                return Task.FromResult(n);
            }
        }

        public Task<int> WithdrawConsentAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                _withdrawn = true;
                var n = 0;
                foreach (var r in _requests.Where(x => x.State == AssistantRequestState.Queued || x.State == AssistantRequestState.Claimed))
                {
                    r.State = AssistantRequestState.Cancelled;
                    r.DeclineReason = "Consent was withdrawn, so this request was cancelled and nothing was sent.";
                    r.ClosedUtc = UtcNow().ToString("o");
                    r.ClaimToken = null;
                    n++;
                }
                return Task.FromResult(n);
            }
        }

        public AssistantPendingSummary PendingFor(string sessionId)
        {
            lock (_lock)
            {
                var open = _requests
                    .Where(r => sessionId == null || string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                    .Where(r => r.State == AssistantRequestState.Queued)
                    .ToArray();
                if (open.Length == 0) return null;          // zero tokens in the common case
                return new AssistantPendingSummary
                {
                    Pending = open.Length,
                    Requests = open.Select(r => new PendingRequestLine
                    {
                        Id = r.RequestId, Job = r.JobId, Need = r.Need, WaitingSince = r.PostedUtc,
                    }).ToArray(),
                };
            }
        }

        /// <summary>Read the live record, for a runner that owns this queue. Never handed to a door.</summary>
        internal AssistantRequest Peek(string requestId)
        {
            lock (_lock) { return Find(requestId)?.Copy(); }
        }

        private AssistantRequest Find(string id) =>
            _requests.FirstOrDefault(r => string.Equals(r.RequestId, id, StringComparison.Ordinal));

        private static Task<HandoffResult> NotFound(string id) => Task.FromResult(new HandoffResult
        {
            Refusal = HandoffRefusal.NotFound,
            Message = $"No assistant request named '{id}'. Run list_assistant_requests to see what is waiting.",
        });

        private static Task<HandoffResult> Terminal(AssistantRequest r) => Task.FromResult(new HandoffResult
        {
            Request = r.Public(),
            Refusal = HandoffRefusal.AlreadyTerminal,
            Message = $"Request {r.RequestId} is already {Describe(r.State)}. Nothing was changed.",
        });

        private static Task<HandoffResult> NotClaimed(AssistantRequest r) => Task.FromResult(new HandoffResult
        {
            Request = r.Public(),
            Refusal = HandoffRefusal.NotClaimed,
            Message = $"Claim request {r.RequestId} first, then answer it.",
        });

        private static Task<HandoffResult> WrongToken(AssistantRequest r) => Task.FromResult(new HandoffResult
        {
            Request = r.Public(),
            Refusal = HandoffRefusal.WrongClaimToken,
            Message = $"That claim on {r.RequestId} is not the current one. Claim it again before answering.",
        });

        private static Task<HandoffResult> Withdrawn() => Task.FromResult(new HandoffResult
        {
            Refusal = HandoffRefusal.ConsentWithdrawn,
            Message = "Consent is off, so the request was cancelled and the reply was not used.",
        });

        private static string Describe(AssistantRequestState s) => s switch
        {
            AssistantRequestState.Completed => "answered",
            AssistantRequestState.Declined => "declined",
            AssistantRequestState.Cancelled => "cancelled",
            AssistantRequestState.Claimed => "claimed",
            _ => "waiting",
        };
    }

    /// <summary>
    /// The text the MCP health appender folds into a tool result, in the same shape as the health delta it
    /// already appends. Kept here so the appender has one source and the wording cannot drift.
    /// </summary>
    public static class AssistantPendingBlock
    {
        public static string Text(AssistantPendingSummary summary)
        {
            if (summary == null || summary.Pending <= 0) return null;
            var lines = summary.Requests.Select(r => $"{r.Id} ({r.Job}): {r.Need}");
            return $"assistant: {summary.Pending} request(s) waiting for you. "
                 + string.Join("; ", lines)
                 + ". Use claim_request then complete_request, or decline_request.";
        }
    }
}
