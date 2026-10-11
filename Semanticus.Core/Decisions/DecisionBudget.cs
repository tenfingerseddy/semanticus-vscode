// The per-session spend counter.
//
// THERE IS NO SESSION CEILING IN THE APP (Kane, 1 Oct: "dont put silly allowances on Jev. it basically cost nothing ... the batching
// is the best mitigation not caps"). Jev is USD 0.042 per million input tokens and its output is free, so a whole 15-table model costs
// a fraction of a cent, and a cap that stopped the asks halfway only left the person with a plan that was quietly worse. The app builds
// this budget with no cap: it COUNTS (get_decision_budget reads the tokens, the requests and the money) and never refuses on a total.
// What protects the person is the month's money at the relay (tools/relay) and asking a table at a time (JevTableBatch). A cap can still
// be given to the constructor, for a test or a tool that wants one; the reservation rules below are what it then enforces.
//
// Counted in INPUT tokens, because that is what the pilot measured and what dominates the bill: the
// snapshot and the criteria are long, the answer is a handful of tokens.
//
// The counter used to be purely post-hoc: it added up what replies said they cost, so an ask that failed,
// was refused, was retried or never came back at all was recorded as free. None of those are free. The
// idempotency key is sent but the vendor's handling of it is unconfirmed, and a request that timed out may
// have been served and billed after we stopped listening. So the rule here is the pessimistic one:
//
//   capacity is RESERVED before an attempt goes out, and an attempt is refused when the reservation would
//   take the session past its cap. The cap is therefore a ceiling on what is HELD, not a line the last ask
//   may cross. Honest limit: a reply that reports a real cost above its estimate is charged that cost, so
//   what is charged can still end a little past the cap; the estimate is pessimistic so that is rare.
//   every attempt settles. A reply that reports its cost is charged that cost; anything else (a refusal, a
//   malformed answer, a retry, a timeout, a dropped socket) is charged the estimate, because it may have
//   been billed and we cannot tell. The one exception is a refusal whose service's own contract says it
//   happened before anything was forwarded (the relay's free refusals): that hold is given back, because
//   counting it would let refusals that cost nothing use this session's allowance up.
//   An attempt that never reached any service (the connection refused, no name, no secure channel) is given back too
//   (Refund), and a refusal from the vendor itself, 422 and 429 included, is charged: the budget never under-counts.
//   a retry is a second attempt, so it reserves and settles a second time.
//
// An exhausted budget is never an error and never touches what the person can do offline. It means the
// product stops proposing and behaves as it does with no decision service at all.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Decisions
{
    public sealed class DecisionBudget
    {
        private readonly object _gate = new object();
        private long _inputTokens;
        private long _reserved;
        private long _requests;
        private TaskCompletionSource<bool> _settled = NewSettled();

        private static TaskCompletionSource<bool> NewSettled() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>No argument: no ceiling, which is how the app builds it. A number is a ceiling for a test or a tool (never the app).</summary>
        public DecisionBudget(long? capInputTokens = null)
        {
            if (capInputTokens < 0) throw new ArgumentOutOfRangeException(nameof(capInputTokens));
            CapInputTokens = capInputTokens;
        }

        /// <summary>The session's ceiling, in input tokens. Null means there is none, which is the app's own setting.</summary>
        public long? CapInputTokens { get; }

        /// <summary>What this session has been charged, in US dollars at Jev's price (<see cref="JevPrice"/>). Output is free, so this is all of it.</summary>
        public double UsedUsd => JevPrice.UsdFor(UsedInputTokens);

        /// <summary>What has been charged: real costs where a reply reported one, and the estimate everywhere
        /// a charge was possible but unknown.</summary>
        public long UsedInputTokens { get { lock (_gate) return _inputTokens; } }

        /// <summary>What is held for attempts that are in flight right now.</summary>
        public long ReservedInputTokens { get { lock (_gate) return _reserved; } }

        /// <summary>Charged plus held. This, not the charged figure alone, is what the cap is compared to, so
        /// two asks in flight together cannot both be admitted against the same last slice of allowance.</summary>
        public long CommittedInputTokens { get { lock (_gate) return _inputTokens + _reserved; } }

        /// <summary>How many attempts reached a service this session. Counts, never content. A retried ask
        /// counts twice, because it may have been charged twice.</summary>
        public long Requests { get { lock (_gate) return _requests; } }

        /// <summary>What has been CHARGED has reached the cap. Holds still in flight are not counted: a hold is not
        /// spend until it settles, and a free refusal gives it back, so a budget that read as used up while other asks
        /// were only holding capacity told a person this session was out when it was not (Sol on ai.schema round 5).
        /// The cap is still a ceiling, because TryReserve counts every hold.</summary>
        public bool Exhausted { get { lock (_gate) return CapInputTokens != null && _inputTokens >= CapInputTokens; } }

        /// <summary>What is left under the ceiling, or null when there is no ceiling.</summary>
        public long? RemainingInputTokens
        {
            get
            {
                lock (_gate)
                {
                    if (CapInputTokens == null) return null;
                    var left = CapInputTokens.Value - (_inputTokens + _reserved);
                    return left < 0 ? 0 : left;
                }
            }
        }

        /// <summary>Hold capacity for one attempt before it goes out. False means this attempt would take the
        /// session past its cap, and it must not be sent. The caller MUST settle whatever it gets back, and
        /// settling in a finally is the only way to be sure a timeout does not walk off with the hold.</summary>
        public bool TryReserve(int estimatedInputTokens, out DecisionSpend spend) =>
            TryReserve(estimatedInputTokens, out spend, out _);

        /// <summary>The same hold, and when it is refused, whether waiting could change that. <paramref name="holdsSettle"/>
        /// is null when the refusal is final (what is already charged leaves no room for this estimate), and otherwise
        /// completes the next time any hold settles, because only other asks' holds are in the way and a hold can be
        /// given back. It is taken under the same lock as the refusal, so a settle can never slip in between.</summary>
        public bool TryReserve(int estimatedInputTokens, out DecisionSpend spend, out Task holdsSettle)
        {
            var estimate = estimatedInputTokens < 0 ? 0 : (long)estimatedInputTokens;
            lock (_gate)
            {
                if (CapInputTokens != null && _inputTokens + _reserved + estimate > CapInputTokens)
                {
                    spend = null;
                    holdsSettle = _inputTokens + estimate <= CapInputTokens && _reserved > 0 ? _settled.Task : null;
                    return false;
                }
                holdsSettle = null;
                _reserved += estimate;
                _requests++;
            }
            spend = new DecisionSpend(this, estimate);
            return true;
        }

        /// <summary>Record one attempt that reached a service, charging it directly with no reservation. The
        /// recorded oracle and the tests use this; the live client reserves instead.</summary>
        public void Record(int inputTokens, int outputTokens)
        {
            lock (_gate)
            {
                _requests++;
                if (inputTokens > 0) _inputTokens += inputTokens;
            }
        }

        internal void Settle(long estimate, long charged, bool sent = true)
        {
            TaskCompletionSource<bool> settled;
            lock (_gate)
            {
                _reserved -= estimate;
                if (_reserved < 0) _reserved = 0;
                if (charged > 0) _inputTokens += charged;
                // A hold given back before its attempt went out never reached a service, so it is not an attempt.
                if (!sent && _requests > 0) _requests--;
                settled = _settled;
                _settled = NewSettled();
            }
            // Outside the lock: whoever was waiting for room reads the budget again, and must not find the gate held.
            settled.TrySetResult(true);
        }

        public void Reset()
        {
            lock (_gate)
            {
                _inputTokens = 0;
                _reserved = 0;
                _requests = 0;
            }
        }
    }

    /// <summary>Jev's price, in one place. USD 0.042 per million input tokens; output tokens are free (Kane, 1 Oct).</summary>
    public static class JevPrice
    {
        public const double UsdPerMillionInputTokens = 0.042;

        public static double UsdFor(long inputTokens) => inputTokens <= 0 ? 0 : inputTokens * UsdPerMillionInputTokens / 1_000_000.0;

        /// <summary>The price in words, for a read a person or an assistant sees.</summary>
        public const string PriceWords = "Jev costs USD 0.042 per million input tokens, and its output is free.";
    }

    /// <summary>One attempt's hold on the budget. Settle it exactly once: with the real cost when a reply said
    /// what it was, released when the service said it refused before anything was forwarded, or as unknown for
    /// everything else. Settling twice does nothing, which is what makes it safe to settle in a finally and
    /// still report a real cost, or a release, on the way out.</summary>
    public sealed class DecisionSpend
    {
        private DecisionBudget _budget;
        private readonly long _estimate;

        internal DecisionSpend(DecisionBudget budget, long estimate)
        {
            _budget = budget;
            _estimate = estimate;
        }

        /// <summary>The reply said what it cost. Charge that and release the rest of the hold. A reply that said
        /// nothing (zero) keeps the estimate, because a call that reported no cost may still have been charged.</summary>
        public void Settle(int actualInputTokens) => Close(actualInputTokens > 0 ? actualInputTokens : _estimate);

        /// <summary>The attempt failed, was refused, was retried or never came back. It may still have been
        /// charged, so the estimate stands as spend.</summary>
        public void SettleUnknown() => Close(_estimate);

        /// <summary>The service refused this attempt before forwarding anything, and its own contract says such a
        /// refusal is free, so nothing was charged and the whole hold is given back. Only a caller that can name the
        /// contract (the relay's refusal table) may use this; anything it cannot be sure of is SettleUnknown.</summary>
        public void Release() => Close(0);

        /// <summary>The attempt never reached any service (the connection was refused, the name did not resolve, no secure
        /// channel could be made): nothing was served, so nothing was charged and the hold is given back. A socket that dropped
        /// AFTER the request went out may still have been served and is SettleUnknown. A refusal from the vendor itself, 422
        /// and 429 included, is not this either: the vendor does not document it as unbilled (27 Sep, the coordinator's
        /// decision: the budget is a cost cap and never under-counts).</summary>
        public void Refund() => Close(0);

        /// <summary>The attempt this hold was for was never sent (its deadline or its caller stopped it first), so
        /// nothing was charged and it is not counted as an attempt either.</summary>
        public void ReleaseUnsent() => Close(0, sent: false);

        private void Close(long charged, bool sent = true)
        {
            var budget = Interlocked.Exchange(ref _budget, null);
            if (budget != null) budget.Settle(_estimate, charged, sent);
        }
    }
}
