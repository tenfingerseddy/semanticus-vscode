// The decision client. THE ONLY FILE IN THE ENGINE THAT NAMES THE VENDOR HOST, and the only file in the
// decision layer that builds an HttpClient.
//
// DecisionRegistryTests asserts both of those, by reading every .cs file in the engine and failing if the
// host string or a second HttpClient appears anywhere else. If you are about to add either, that test is
// the conversation, not an obstacle. It is also why relay mode lives in this file rather than in a second
// oracle class beside it: two destinations, one client, one place in the layer that can reach a network.
//
// What this file does and does not hold:
//   It holds one HttpClient, the deadline, the retry, the concurrency cap and the budget counter.
//   It does NOT hold consent, entitlement, the registry, the cache or the enabled switch. Those are gates,
//   they are checked before an ask ever reaches here, and they live in DecisionService. Splitting them that
//   way is what lets the boundary tests prove "zero requests" by pointing at this object's own counter.
//
// TWO ROUTES, chosen once at construction and never per request:
//
//   RELAY, the production route and the only one an installed copy can take. The engine posts to
//   SEMANTICUS_RELAY_URL, defaulting to the host in DefaultRelayBaseUrl below, carrying the LICENCE TOKEN as
//   the bearer. No vendor key exists anywhere in the process. The relay holds the house key, re-verifies
//   the licence server side against its own copy of the public key, holds the money before it is spent and
//   forwards the call. Its wire contract is docs/redesign/contracts/relay.md and it is NOT the vendor's:
//   the body is { point, schemaVersion, snapshot, questions, idempotencyKey }, and the relay builds the
//   vendor body from it, so the served model is pinned by the server and a caller cannot choose it.
//
//   DIRECT VENDOR, development only. SEMANTICUS_JEV_KEY set wins over the relay and posts straight to the
//   vendor with that key. It is how the pilot and the replay runs are funded, and it is logged once,
//   plainly, as development only. No extension path supplies that variable: there is no bring-your-own
//   key, no settings entry and no UI for it (Kane, 2026-09-21).
//
// With no key AND no licence token there is no oracle at all: the engine uses the null one and the product
// behaves exactly as it does today. That is the free session, and it is the default.
//
// F-194 is why there are two routes rather than one. The oracle used to be built from SEMANTICUS_JEV_KEY
// alone, so an INSTALLED Pro session had no route to a decision service of any kind and every ranking call
// fell back to rule order without anyone being told.
//
// Spend honesty: the idempotency key is sent, but its deduplication on the vendor side is UNCONFIRMED, so a
// retry is counted as a request that may have been charged. The budget counter is deliberately pessimistic
// for that reason, and the pessimism is structural rather than remembered:
//   capacity is RESERVED before each attempt and the attempt is refused if the reservation would go past the
//   session cap, so the cap is a ceiling rather than a line the last ask may cross
//   every attempt settles in a finally, so a timeout, a dropped socket, a refusal and a retry all count as
//   spend that may have been charged; only a reply that reported its own cost is charged that cost instead,
//   and only a relay refusal its contract makes before anything is forwarded is given back (FreeRelayRefusals)
//   (an attempt that never reached anyone is given back too, and a vendor 4xx, 422 and 429 included, counts as spent)
//   the budget is read again AFTER the queue. A request can sit behind five others for seconds, and the
//   allowance it was admitted against can be gone by the time it reaches the front.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;

namespace Semanticus.Engine.Decisions
{
    public sealed class JevDecisionOracle : IDecisionOracle, IDisposable
    {
        /// <summary>The vendor endpoint. One string, one file.</summary>
        internal const string Endpoint = "https://api.typesafe.ai/v1/systemone";

        /// <summary>The moving alias we ask for. The reply names the version that actually served it, and that
        /// version, not this alias, is what the cache keys on.</summary>
        internal const string ModelAlias = "jev-latest";

        /// <summary>The only place a VENDOR key is read from, and it is the development route.</summary>
        public const string KeyVariable = "SEMANTICUS_JEV_KEY";

        /// <summary>Where the hosted relay lives, overridable so a shadow deployment or a staging worker can be
        /// pointed at without a rebuild. The default is the production host.</summary>
        public const string RelayUrlVariable = "SEMANTICUS_RELAY_URL";

        /// <summary>The relay's production origin. Kane sets this up the way the billing domain is set up; until
        /// he does, a Pro session simply cannot reach it and every ask comes back not decided with the plain
        /// reason, which is the same shape as any other refusal.</summary>
        public const string DefaultRelayBaseUrl = "https://relay.semanticus.com.au";

        internal const string DecideRoute = "/v1/decide";
        internal const string BudgetRoute = "/v1/budget";

        /// <summary>The wire value <c>get_decision_budget</c> reports for each route.</summary>
        public const string RouteRelay = "relay";
        public const string RouteVendor = "vendor";
        public const string RouteNone = "none";

        /// <summary>One overall deadline covering the first attempt, the wait, and the retry. The pilot's p90
        /// was 2.1 seconds over 133 calls, so four seconds leaves room for one slow call without leaving a
        /// person waiting. See docs/redesign/jev-pilot-verdict-2.md.</summary>
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(4);

        internal const int MaxConcurrent = 6;
        private const int MaxSnapshotBytes = 64 * 1024;

        /// <summary>A request that asks a table at a time (m11/jev-batch: up to about thirty questions) gets a little more time than a
        /// request with a handful: a quarter of a second for each question past eight, and at most eight seconds more. Nobody waits on
        /// these (they are the Plan's background asks), the relay's own deadline on the vendor is fifteen seconds, and the four
        /// seconds of a request with eight questions or fewer are unchanged.</summary>
        internal static TimeSpan ExtraFor(DecisionRequest request) =>
            TimeSpan.FromMilliseconds(Math.Min(8000, Math.Max(0, (request?.Questions.Count ?? 0) - 8) * 250));

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _bearer;
        private readonly TimeSpan _deadline;
        private readonly SemaphoreSlim _cap;
        private readonly string _sessionSalt = Guid.NewGuid().ToString("N");

        /// <summary>The relay's origin, with no trailing slash, or null in direct vendor mode. Null is what every
        /// per-request branch in this file tests, so there is one switch rather than a flag beside a URL that
        /// could disagree with it.</summary>
        private readonly string _relayBase;

        private static int _vendorModeLogged;

        /// <summary>Test seam. Runs after the fast budget check and before a request joins the queue, so a test
        /// can spend the session's allowance while a request is genuinely waiting behind another one and prove
        /// the queued request is refused rather than sent. Null on every production path.</summary>
        internal Action EnteringQueueForTest;

        /// <summary>Test seam. Sees each log line as it is written, so a suite can check what an operator would read
        /// without swapping the process's error stream under tests running beside it. Null on every production path.</summary>
        internal Action<string> LogLineForTest;

        /// <summary>Test seam. Runs when an ask that waited for other asks' holds has just taken its own, before anything
        /// is sent, which is the moment a deadline or a cancel can land between the wake-up and the send. Null on every
        /// production path.</summary>
        internal Action ReservedAfterWaitForTest;

        /// <summary>Test seam: the clock the refusal hold-off reads (<see cref="RefusedHoldOff"/>), so a suite can move past it
        /// without waiting for it. Null on every production path, which reads the real clock.</summary>
        internal Func<DateTime> UtcNowForTest;

        private DateTime UtcNow => UtcNowForTest?.Invoke() ?? DateTime.UtcNow;

        /// <summary>How long a relay refusal that says the service is not answering for now holds off this session's next
        /// asks (see HeldOffReason).</summary>
        internal static readonly TimeSpan RefusedHoldOff = TimeSpan.FromMinutes(5);

        /// <summary>Build the oracle this session gets, or null when it gets none.
        ///
        /// The order is the whole policy. A vendor key wins, because a developer who set one meant it and a
        /// silent fall-through to the relay would spend a customer's allowance on a debugging session. Then the
        /// licence token, which is the production route. Then nothing, which is the free session and the default.
        ///
        /// A null return is the signal for the caller to use <see cref="NullDecisionOracle"/>; it is not a
        /// failure and is never logged as one.</summary>
        /// <param name="licenceToken">The verified Pro licence token, or null. It is the relay's credential; see
        /// LicenseEntitlement.CloudCredential for which entitlements carry one and which deliberately do not.</param>
        public static JevDecisionOracle FromEnvironment(DecisionBudget budget, string licenceToken = null,
                                                        HttpMessageHandler handlerForTests = null)
        {
            var key = Environment.GetEnvironmentVariable(KeyVariable);
            if (!string.IsNullOrWhiteSpace(key))
            {
                // Once per process, not once per ask. A developer needs to know their session is spending the
                // house key rather than a licence; they do not need it on every measure they create.
                if (Interlocked.Exchange(ref _vendorModeLogged, 1) == 0)
                {
                    try
                    {
                        Console.Error.WriteLine("[decision] direct vendor mode, development only: " + KeyVariable
                            + " is set, so asks go straight to the vendor and no licence is used.");
                    }
                    catch { /* a log line must never break a session */ }
                }
                return new JevDecisionOracle(key, budget, handlerForTests);
            }

            if (string.IsNullOrWhiteSpace(licenceToken)) return null;
            return ForRelay(licenceToken, RelayBaseFromEnvironment(), budget, handlerForTests);
        }

        /// <summary>The relay origin this process will use, normalised with no trailing slash. An override that
        /// is not an absolute http(s) URL is IGNORED rather than obeyed: a typo in an environment variable must
        /// not become a request to somewhere nobody chose.</summary>
        internal static string RelayBaseFromEnvironment()
        {
            var configured = Environment.GetEnvironmentVariable(RelayUrlVariable);
            if (string.IsNullOrWhiteSpace(configured)) return DefaultRelayBaseUrl;

            Uri parsed;
            if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                try
                {
                    Console.Error.WriteLine("[decision] " + RelayUrlVariable
                        + " is not an absolute http or https address, so the default relay is used instead.");
                }
                catch { /* a log line must never break a session */ }
                return DefaultRelayBaseUrl;
            }

            return configured.Trim().TrimEnd('/');
        }

        /// <summary>Relay mode: the bearer is the licence token and the destination is the relay.</summary>
        public static JevDecisionOracle ForRelay(string licenceToken, string relayBaseUrl, DecisionBudget budget,
                                                 HttpMessageHandler handlerForTests = null) =>
            new JevDecisionOracle(licenceToken, budget, handlerForTests, Deadline, MaxConcurrent,
                                  string.IsNullOrWhiteSpace(relayBaseUrl) ? DefaultRelayBaseUrl : relayBaseUrl.TrimEnd('/'));

        public JevDecisionOracle(string key, DecisionBudget budget, HttpMessageHandler handlerForTests = null)
            : this(key, budget, handlerForTests, Deadline, MaxConcurrent, null)
        {
        }

        /// <summary>Test seam for the deadline and the queue width, so a suite can prove the timeout and the
        /// queued-at-exhaustion paths without waiting four seconds or opening six sockets. Nothing in
        /// production reaches this: the public constructor above passes the shipped values.</summary>
        internal JevDecisionOracle(string key, DecisionBudget budget, HttpMessageHandler handlerForTests,
                                   TimeSpan deadline, int maxConcurrent, string relayBaseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A decision credential is required.", nameof(key));
            if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
            if (maxConcurrent < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
            _bearer = key;
            _relayBase = string.IsNullOrWhiteSpace(relayBaseUrl) ? null : relayBaseUrl.TrimEnd('/');
            _deadline = deadline;
            _cap = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            Budget = budget ?? new DecisionBudget();
            _ownsHttp = true;
            _http = handlerForTests == null ? new HttpClient() : new HttpClient(handlerForTests, disposeHandler: false);
            // The per-request deadline is enforced with a linked token, not here, because one deadline must
            // cover the attempt AND the retry. This is a backstop for a socket that never returns.
            _http.Timeout = _deadline + TimeSpan.FromSeconds(8) + TimeSpan.FromSeconds(2);
        }

        public DecisionBudget Budget { get; }

        public bool Available { get { return true; } }

        /// <summary>True when asks go through the hosted relay on a licence, false when they go straight to the
        /// vendor on a development key.</summary>
        public bool IsRelay { get { return _relayBase != null; } }

        /// <summary>The wire word for which route this session takes: <c>relay</c> or <c>vendor</c>.</summary>
        public string Route { get { return _relayBase == null ? RouteVendor : RouteRelay; } }

        private string DecideUrl { get { return _relayBase == null ? Endpoint : _relayBase + DecideRoute; } }

        /// <summary>The version that served the most recent decided answer, or null before the first one. The
        /// cache asks for this so a vendor version roll invalidates every entry rather than silently mixing two
        /// models' answers.</summary>
        public string LastServedModelVersion { get; private set; }

        public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // A point that is not registered never reaches the wire, whatever the caller thought it was doing.
            DecisionPoint point;
            if (!DecisionRegistry.TryGet(request.PointName, out point))
                return DecisionAnswers.NotDecided(request, $"'{request.PointName}' is not a registered decision point");

            if (Budget.Exhausted)
                return DecisionAnswers.NotDecidedOverBudget(request, BudgetRefusedReason);

            // The relay said a moment ago that it is not answering for now (the licence's month, a pause, the house's
            // limit). Nothing is sent until the hold-off passes, and the ask comes back with the relay's own sentence, so
            // every row still names what stopped Jev.
            var heldOff = HeldOffReason(request.PointName);
            if (heldOff != null)
            {
                Log(request.PointName, 0, TimeSpan.Zero, "held-off");
                return DecisionAnswers.NotDecided(request, heldOff);
            }

            string snapshotJson;
            try
            {
                DecisionSnapshot.RequireWithin(request.Snapshot, MaxSnapshotBytes);
                snapshotJson = DecisionDigest.CanonicalSnapshotJson(request.Snapshot);
            }
            catch (InvalidOperationException e)
            {
                return DecisionAnswers.NotDecided(request, e.Message);
            }

            var idempotencyKey = DecisionDigest.Sha256Hex(_sessionSalt + "|" + request.Digest);
            var body = _relayBase == null
                ? BuildBody(request, snapshotJson)
                : BuildRelayBody(request, snapshotJson, idempotencyKey);

            // The estimate is taken from the body we are about to send. On the relay route that is NOT byte for
            // byte the body the relay forwards, so this figure and the relay's own reservation differ by the
            // wrapper: our point name and reference against its model id. They are not meant to agree. This one
            // holds a ceiling on THIS session; the relay's holds the licence's month, settled against what the
            // vendor really reported. Trying to make them match would mean guessing at the relay's wrapper,
            // which is a second copy of someone else's contract.
            var estimate = EstimateInputTokens(body);

            // ONE attempt through the relay, two direct to the vendor, and the difference is about money rather
            // than taste. The relay's own deadline on the vendor is fifteen seconds and ours is four, so a retry
            // after a relay timeout cannot beat the deadline it already missed; it can only take a second hold
            // and be charged a second time. A relay refusal is worse to retry: the relay has already decided,
            // and hammering a paused switch or a spent budget adds latency to a refusal that will not change.
            var maxAttempts = _relayBase == null ? 2 : 1;

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_deadline + ExtraFor(request));

                var started = DateTime.UtcNow;
                // How many attempts WENT OUT, for the log. Counted where an attempt is sent, not where it starts, so an
                // ask stopped while it waited for a hold, or refused its hold, logs no attempt (ai.schema final verify,
                // low item 2).
                var attempts = 0;
                string failure = null;
                // Set when the budget, not the service, is what stopped this ask: a refused hold.
                var overBudget = false;
                // Set immediately before an attempt goes out, so it is true even for one whose connection never
                // opened. That is the same pessimism the spend counter uses: an attempt that may have left is
                // treated as one that did. The direct vendor route makes two attempts, so the budget can refuse the
                // RETRY of an ask already tried once, and such an ask is never reported as one Jev was not asked
                // (verify round 4, item 4).
                var sent = false;

                // The service's own refusal code, for the log line only. A code is jargon and never reaches a
                // person; the SENTENCE in `failure` is what they read. Keeping both is what lets an operator
                // grep for licence_budget_spent without the product saying it out loud.
                string failureCode = null;

                var entering = EnteringQueueForTest;
                if (entering != null) entering();

                try
                {
                    await _cap.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The caller's token and the deadline are one linked token, so which of them fired is read from
                    // the caller's own. A caller that moved on is not a queue that was too slow. Nothing was sent
                    // and nothing was held either way.
                    if (ct.IsCancellationRequested)
                    {
                        Log(request.PointName, 0, DateTime.UtcNow - started, "cancelled");
                        return DecisionAnswers.NotDecided(request, CancelledReason);
                    }
                    Log(request.PointName, 0, DateTime.UtcNow - started, "queue-deadline");
                    return DecisionAnswers.NotDecided(request, QueueDroppedReason);
                }

                try
                {
                    // Read the budget AGAIN, now that the queue is behind us. The check before the queue was
                    // true when it was made and can be false by the time a request reaches the front, and a
                    // request that waited its turn is exactly the one most likely to be spending money that
                    // another ask has already committed.
                    //
                    // Honest note: no test can tell this check apart from the reservation below, because an
                    // exhausted budget always refuses a reservation too, and refuses it as final: Exhausted is
                    // charged >= cap (holds are not counted, DecisionBudget.Exhausted), a reservation needs
                    // charged+held+estimate <= cap, and an estimate is at least one token, so no hold can settle
                    // its way into room. The reservation is what ENFORCES the rule; this line states it where a
                    // reader looking for it will find it, and gets the refusal out before a body and a digest are
                    // carried any further.
                    if (Budget.Exhausted)
                    {
                        Log(request.PointName, 0, DateTime.UtcNow - started, "budget");
                        return DecisionAnswers.NotDecidedOverBudget(request, BudgetRefusedReason);
                    }

                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (deadline.IsCancellationRequested) break;

                        // Hold the capacity before anything leaves. A refused hold is the cap doing its job:
                        // the attempt is not sent at all, rather than sent and accounted for afterwards. But a
                        // refusal that only other asks' HOLDS are behind is not the budget yet: a hold is not spend
                        // until it settles, and a free relay refusal gives it back. So the ask waits, inside its own
                        // deadline, for a hold to settle and tries again; only a refusal that what is already charged
                        // makes final is this session's budget (Sol on ai.schema round 5).
                        DecisionSpend spend;
                        Task holdsSettle;
                        var waitedOut = false;
                        var waited = false;
                        while (!Budget.TryReserve(estimate, out spend, out holdsSettle) && holdsSettle != null)
                        {
                            waited = true;
                            if (deadline.IsCancellationRequested) { waitedOut = true; break; }
                            try { await holdsSettle.WaitAsync(deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { waitedOut = true; break; }
                        }
                        if (spend != null && waited) ReservedAfterWaitForTest?.Invoke();
                        // A continuation woken by a settle runs after it, so the deadline or the caller can land between
                        // the wake-up and here. Nothing has left yet, so the hold is given back and nothing is sent (Sol
                        // on the round 5 fix, second pass). A caller's cancel is named after the loop.
                        if (spend != null && deadline.IsCancellationRequested)
                        {
                            spend.ReleaseUnsent();
                            if (waited)
                            {
                                failure = QueueDroppedReason;
                                failureCode = "queue-deadline";
                            }
                            break;
                        }
                        if (spend == null)
                        {
                            if (waitedOut)
                            {
                                // Nothing was sent. A caller's cancel is named after the loop; this is the deadline.
                                failure = QueueDroppedReason;
                                failureCode = "queue-deadline";
                                break;
                            }
                            failure = BudgetRefusedReason;
                            overBudget = true;
                            break;
                        }

                        HttpResponseMessage response = null;
                        try
                        {
                            using (var message = new HttpRequestMessage(HttpMethod.Post, DecideUrl))
                            {
                                // The bearer is the vendor key on the direct route and the LICENCE TOKEN on the
                                // relay route. Two credentials, one header, and only ever one of them exists in
                                // a given process.
                                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
                                // The relay reads the reference out of the body and puts its own header on the
                                // forward, so sending one here as well would be a second, unread copy.
                                if (_relayBase == null)
                                    message.Headers.TryAddWithoutValidation("idempotency-key", idempotencyKey);
                                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                                sent = true;
                                attempts = attempt;
                                response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                            }

                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            if (response.IsSuccessStatusCode)
                            {
                                // The relay returns the vendor's answer byte for byte, so there is one reader
                                // for both routes and the relay never becomes a second place where an answer's
                                // meaning is decided.
                                var answers = Parse(request, text);
                                if (answers.Decided)
                                {
                                    LastServedModelVersion = answers.ServedModelVersion;
                                    // On the relay route the relay says what it charged the licence (x-relay-tokens-used),
                                    // and that is what this session counts. An answer it serves from its store under the
                                    // same reference cost nothing ("0"), although the stored body still reports the usage
                                    // of the first time; charging that again over-counted (ai.schema final verify, low
                                    // item 4). A reply with no readable figure keeps the body's.
                                    var relayCharged = _relayBase == null ? null : RelayTokensUsed(response);
                                    if (relayCharged == 0) spend.Release();
                                    else spend.Settle(relayCharged ?? answers.InputTokens);
                                }
                                // A malformed or rejected reply reported no cost and still may have been
                                // charged, so the hold stands as spend. The finally below does that.
                                Log(request.PointName, attempts, DateTime.UtcNow - started, answers.Decided ? "decided" : "rejected");
                                return answers;
                            }

                            // Only the relay's own refusal is free: the relay refuses before it forwards anything and
                            // releases its hold (relay.md), and a vendor refusal reaches us from it as a 502. On the
                            // direct route every refusal reached the vendor, which does not document a refused request
                            // as unbilled, so it keeps the estimate in the finally below, 422 and 429 included, as the
                            // relay counts a vendor 4xx (worker.mjs). The budget is a cost cap: it never under-counts.
                            if (RefusedBeforeAnyWork(response.StatusCode, _relayBase != null)) spend.Refund();
                            failure = RefusalReason(text, response.StatusCode, out failureCode);
                            // A refusal the relay's contract makes BEFORE it reserves or forwards cost nothing, so
                            // the hold is given back rather than counted. Counting it let a licence's spent month
                            // use this session's allowance up in refusals, and every row then blamed this session
                            // (ai.schema verify round 5, D1). Anything else stays spend that may have been charged.
                            if (_relayBase != null && IsFreeRelayRefusal(response.StatusCode, failureCode))
                            {
                                spend.Release();
                                HoldOffAfter(request.PointName, response.StatusCode, failureCode, failure);
                            }
                            if (attempt == maxAttempts || !Retryable(response.StatusCode)) break;

                            if (!await WaitBeforeRetry(response, deadline.Token).ConfigureAwait(false)) break;
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                        {
                            // The attempt went out, so whichever token fired it may have been charged and the hold
                            // stands (relay.md: "A client timeout never refunds a possibly charged call"). When it was
                            // the CALLER's token, the check after the loop says so; this is the deadline's wording.
                            failure = TimedOutReason;
                            failureCode = "timeout";
                            break;
                        }
                        catch (HttpRequestException e)
                        {
                            // Never reached the service: nothing was sent, so nothing is spent. A socket dropped after the request
                            // went out may still have been served, and keeps the estimate.
                            if (NeverReached(e)) spend.Refund();
                            failure = UnreachableReason;
                            // The log names how the ASK ended, so this attempt's end replaces the one before it: a
                            // refusal and then a lost connection is logged as the lost connection a person is told
                            // about (ai.schema verify round 5, L4).
                            failureCode = "unreachable";
                            if (attempt == maxAttempts) break;
                            try { await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                            GC.KeepAlive(e);
                        }
                        finally
                        {
                            // Whatever happened to this attempt, it is accounted for before the next one or the
                            // return. Settling as unknown after a real settle does nothing, so a reply that
                            // reported its cost keeps that cost, a free relay refusal stays released, and
                            // everything else keeps the estimate. This is
                            // the line that stops a timeout walking off without being counted.
                            spend.SettleUnknown();
                            if (response != null) response.Dispose();
                        }
                    }
                }
                finally
                {
                    _cap.Release();
                }

                // A caller that stopped the ask, wherever it was (in flight, in the wait before a retry, between
                // attempts), is told and logged as a cancel, not as a deadline or the last attempt's refusal: the
                // caller's token and the deadline are one linked token, so only the caller's own says which fired.
                // The budget's own stop is not overridden: it says what it says below.
                if (ct.IsCancellationRequested && !overBudget)
                {
                    failure = CancelledReason;
                    failureCode = "cancelled";
                }
                Log(request.PointName, attempts, DateTime.UtcNow - started, failureCode ?? "failed");
                // ASKED IS ASKED. The budget refusing a RETRY is not "Jev was not asked": the first attempt went out
                // and may have been charged. OverBudget is what every caller reads as "nothing was sent" and turns
                // into that sentence (DraftCopy.JevNotAsked), so an ask that was sent never carries it, whatever
                // ended it. Only the direct vendor route retries, so only it can reach this (verify round 4, item 4).
                if (overBudget && sent)
                    return DecisionAnswers.NotDecided(request, AskedThenStoppedReason);
                // A refused hold is the usual way the session budget ends, well before it reads as exhausted,
                // because the next ask's estimate stops fitting first. It says so structurally (OverBudget), so
                // the service treats it exactly as its own spent-budget gate (DecisionService, gate 6).
                return overBudget
                    ? DecisionAnswers.NotDecidedOverBudget(request, failure)
                    : DecisionAnswers.NotDecided(request, failure ?? "the advice service did not answer");
            }
        }

        /// <summary>What an ask the session's budget stopped says, whichever check stopped it. The same words as
        /// DecisionService.BudgetSpentReason, which is what a person reads for the same stop everywhere else.</summary>
        public const string BudgetRefusedReason = "this session has used its suggestion budget";

        /// <summary>THE ONE SENTENCE A PERSON READS WHEN JEV CANNOT BE REACHED OR REFUSES AND NOBODY AT THE KEYBOARD CAN DO ANYTHING
        /// ABOUT IT (Kane, 8 Oct): the connection failed, the deadline passed, the service had a problem of its own, or it refused
        /// the sign-in without a reason of its own to give. It names no sign-in, no status number and no promise to try again.
        /// What went wrong exactly goes to the log line (the code out-parameter of <see cref="RefusalReason"/>), never here. A
        /// refusal the PERSON can act on (an ended licence, a plan without Jev, a spent month) keeps the relay's own sentence.</summary>
        public const string NotAvailableReason = "Jev is not available right now.";

        /// <summary>What an Ask says for the same stop (Kane, D17, 8 Oct: no word-match fallback; say plainly what happened and what to do).</summary>
        public const string NotAvailableAskNote = "Couldn't reach Jev. Check your internet connection and ask again.";

        /// <summary>The reply is lost without a word, from the engine's own side (the plan's batches name it so).</summary>
        internal const string NoAnswerReason = "the advice service did not answer";

        /// <summary>True for a reason in the "nobody can act" class: the sentence <see cref="NotAvailableReason"/> (which the
        /// connection, deadline, server-fault and bare-sign-in refusals all carry) or one of the engine's own words for silence.
        /// Every surface that would blame a cause, count what Jev did not look at or promise a retry asks this first.</summary>
        public static bool IsNotAvailable(string reason)
        {
            var r = (reason ?? string.Empty).Trim();
            return string.Equals(r, NotAvailableReason, StringComparison.Ordinal)
                   || string.Equals(r, QueueDroppedReason, StringComparison.Ordinal)
                   || string.Equals(r, NoAnswerReason, StringComparison.Ordinal);
        }

        /// <summary>No reply came back before the deadline. The log line still says "timeout" (the failure code).</summary>
        public const string TimedOutReason = NotAvailableReason;

        /// <summary>The ask waited its turn behind others past the deadline, so it was never sent.</summary>
        public const string QueueDroppedReason = "the advice request waited too long behind others and was dropped";

        /// <summary>The connection failed: no reply came back at all.</summary>
        public const string UnreachableReason = NotAvailableReason;

        /// <summary>What an ask its caller stopped says (the caller moved on, so nobody is waiting for the answer).</summary>
        public const string CancelledReason = "the advice request was stopped before an answer came back";

        /// <summary>What an ask that WAS sent, did not answer, and could not be tried again says. It never reads as
        /// an ask nobody made, because one was made.</summary>
        public const string AskedThenStoppedReason =
            "Jev was asked but did not answer, and this session has used its suggestion budget, so it was not asked again";

        /// <summary>The relay's refusals that its contract makes before anything is reserved or forwarded
        /// (docs/redesign/contracts/relay.md, "The order of the gates" and "Every refusal"), each with the one status
        /// the relay gives it. Such a refusal cost nothing, so its hold is given back. The code AND the status must
        /// match: a free code on another status is not the relay's contract, and 502 upstream_failed and 504
        /// upstream_timeout are missing on purpose, because those were forwarded and may have been charged.</summary>
        private static readonly Dictionary<string, int> FreeRelayRefusals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["no_licence"] = 401, ["bad_signature"] = 401,
            ["not_pro"] = 403, ["expired"] = 403, ["subscription_inactive"] = 403,
            ["bad_request"] = 400, ["undeclared_field"] = 400, ["undeclared_question"] = 400,
            ["unknown_point"] = 404, ["not_found"] = 404, ["method_not_allowed"] = 405,
            ["schema_mismatch"] = 409, ["idempotency_reused"] = 409, ["already_in_flight"] = 409,
            ["too_large"] = 413, ["too_many_questions"] = 413,
            ["too_many_in_flight"] = 429,
            ["licence_budget_spent"] = 402,
            ["house_budget_spent"] = 503, ["paused"] = 503, ["point_paused"] = 503, ["not_configured"] = 503,
            // The served model's own switch: relay.md folds it into "paused", the worker names it (worker.mjs).
            ["model_paused"] = 503,
        };

        internal static bool IsFreeRelayRefusal(HttpStatusCode status, string code) =>
            code != null && FreeRelayRefusals.TryGetValue(code, out var expected) && expected == (int)status;

        // ---- the brake after a spent month or a pause (ai.schema final verify, low item 5) --------------------------
        //
        // A free refusal costs this session nothing, which is right, but it also left nothing to stop the next ask: every
        // model change asked every object Jev had not looked at again (up to AiSchemaFamily.MaxObjectsPerRun, and the other
        // families beside it), and the relay refused all of it until the month reset or the pause lifted. So a relay
        // refusal that says the service is not answering FOR NOW (402, the licence's month; 503, a pause, the served
        // model's pause or the house's limit) holds off this session's next asks for RefusedHoldOff. A held-off ask is not
        // sent, holds nothing, counts as no attempt, and comes back with the relay's own sentence, so the rows keep naming
        // what stopped Jev. Five minutes because none of those lifts faster than a person notices: a month resets once a
        // month and a pause is a person's switch, while a person working on a model changes it every few seconds, so this
        // turns a stream of refused asks into at most one batch every five minutes and costs a lifted pause at most five
        // minutes. A pause on ONE point (point_paused) holds off that point only. Everything else (a crowded moment, a
        // refusal about one request, a failed forward) holds nothing off.

        private readonly object _holdOffGate = new object();
        private readonly Dictionary<string, (DateTime Until, string Reason)> _heldOff =
            new Dictionary<string, (DateTime, string)>(StringComparer.Ordinal);
        private const string EveryPoint = "";

        private void HoldOffAfter(string pointName, HttpStatusCode status, string code, string reason)
        {
            if ((int)status != 402 && (int)status != 503) return;
            var key = string.Equals(code, "point_paused", StringComparison.Ordinal) ? pointName ?? EveryPoint : EveryPoint;
            lock (_holdOffGate) _heldOff[key] = (UtcNow + RefusedHoldOff, reason);
        }

        /// <summary>The relay's sentence while an ask for <paramref name="pointName"/> is held off, or null when it may go out.</summary>
        private string HeldOffReason(string pointName)
        {
            var now = UtcNow;
            lock (_holdOffGate)
            {
                foreach (var key in new[] { EveryPoint, pointName ?? EveryPoint })
                    if (_heldOff.TryGetValue(key, out var held))
                    {
                        if (now < held.Until) return held.Reason;
                        _heldOff.Remove(key);
                    }
            }
            return null;
        }

        /// <summary>What the relay says it charged the licence for this reply, or null when it did not say in a form this can
        /// read.</summary>
        private static int? RelayTokensUsed(HttpResponseMessage response)
        {
            if (response == null || !response.Headers.TryGetValues("x-relay-tokens-used", out var values)) return null;
            var text = values.FirstOrDefault();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var used) ? used : (int?)null;
        }

        /// <summary>A pessimistic input-token estimate for one attempt, used to hold capacity before the real
        /// cost is known. The request body IS the input, and about four characters to a token is the usual rule
        /// of thumb, so dividing by three deliberately over-counts: a budget that guesses low is a budget that
        /// lets a session overspend.</summary>
        internal static int EstimateInputTokens(string body) =>
            Math.Max(1, (body == null ? 0 : body.Length) / 3);

        /// <summary>A refusal given before anything was forwarded to the vendor: a 4xx from the relay, which answers every
        /// vendor failure with a 502. Never a refusal from the vendor itself, which reached it.</summary>
        internal static bool RefusedBeforeAnyWork(HttpStatusCode status, bool fromRelay) =>
            fromRelay && (int)status >= 400 && (int)status < 500;

        /// <summary>A failure before the request reached the service: no connection, no name, no secure channel.</summary>
        internal static bool NeverReached(HttpRequestException e) =>
            e.HttpRequestError == HttpRequestError.ConnectionError || e.HttpRequestError == HttpRequestError.NameResolutionError
            || e.HttpRequestError == HttpRequestError.SecureConnectionError;

        private static bool Retryable(HttpStatusCode status) =>
            status == HttpStatusCode.TooManyRequests || (int)status >= 500;

        private async Task<bool> WaitBeforeRetry(HttpResponseMessage response, CancellationToken ct)
        {
            var wait = TimeSpan.FromMilliseconds(250);
            if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
            {
                var asked = response.Headers.RetryAfter.Delta.Value;
                // Never honour a wait longer than the deadline itself; the linked token would cancel anyway and
                // a person is not going to wait for a minute to be told about a display folder.
                wait = asked > _deadline ? _deadline : asked;
            }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { return false; }
        }

        /// <summary>The DIRECT VENDOR body: a model alias, the state, and the questions. The caller chooses the
        /// model here, which is exactly the thing the relay does not allow.</summary>
        internal static string BuildBody(DecisionRequest request, string snapshotJson)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":");
            AppendJsonString(sb, ModelAlias);
            sb.Append(",\"state\":").Append(snapshotJson);
            sb.Append(",\"questions\":");
            AppendQuestions(sb, request);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>The RELAY body, exactly as docs/redesign/contracts/relay.md sets it out:
        /// <c>{ point, schemaVersion, snapshot, questions, idempotencyKey }</c>.
        ///
        /// Three differences from the vendor body, and each one is the relay doing a job the client must not:
        ///   no model. The relay pins the served model and the caller never chooses it.
        ///   the snapshot travels under its own name, so the relay can check every key against the pinned
        ///   template for this point and refuse a field the point never declared.
        ///   the schema version is SENT. The relay treats it as optional and assumes the pinned version when it
        ///   is absent, which means a client that has moved ahead is only caught if it says so. Saying so is
        ///   this line, and it turns a silent mismatch into a plain "update Semanticus".
        /// The questions object is byte for byte what the vendor route sends, because the relay forwards it
        /// unchanged and both ends have to read the same thing.</summary>
        internal static string BuildRelayBody(DecisionRequest request, string snapshotJson, string idempotencyKey)
        {
            var sb = new StringBuilder();
            sb.Append("{\"point\":");
            AppendJsonString(sb, request.PointName);
            sb.Append(",\"schemaVersion\":").Append(request.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"snapshot\":").Append(snapshotJson);
            sb.Append(",\"questions\":");
            AppendQuestions(sb, request);
            sb.Append(",\"idempotencyKey\":");
            AppendJsonString(sb, idempotencyKey);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendQuestions(StringBuilder sb, DecisionRequest request)
        {
            sb.Append('{');
            for (var i = 0; i < request.Questions.Count; i++)
            {
                var q = request.Questions[i];
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, q.Name);
                sb.Append(":{\"type\":");
                AppendJsonString(sb, q.WireType);
                sb.Append(",\"instructions\":");
                AppendJsonString(sb, q.Instructions);
                // A SCORE's criteria are an ordered LIST, lowest rung first: the service refuses an object there with
                // 422 "Input should be a valid list" (measured 28 Sep; it is why plan.rank failed on every call). It
                // answers by position, and DecisionAnswerReader maps each position back to the option sent there.
                // A choice and a noul keep the keyed object, which is what they have always been answered from.
                if (q.Kind == DecisionQuestionKind.Score)
                {
                    sb.Append(",\"criteria\":[");
                    for (var j = 0; j < q.Options.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        AppendJsonString(sb, q.Options[j].Criterion);
                    }
                    sb.Append("]}");
                    continue;
                }
                sb.Append(",\"criteria\":{");
                for (var j = 0; j < q.Options.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    AppendJsonString(sb, q.Options[j].Id);
                    sb.Append(':');
                    AppendJsonString(sb, q.Options[j].Criterion);
                }
                sb.Append("}}");
            }
            sb.Append('}');
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append(JsonSerializer.Serialize(value ?? string.Empty));
        }

        /// <summary>Turn a refusal into the sentence a person reads, and hand back the service's own code for
        /// the log line.
        ///
        /// The relay refuses in the product's words already (docs/product-copy-style.md governs its reason
        /// strings), so the honest thing is to carry its sentence through rather than replace it with one of
        /// ours: "This licence has used its cloud advice for the month. It resets on 2026-10-01." says more
        /// than "the advice service answered 402" ever could. The CODE goes to the log and nowhere near a
        /// person, because a code is jargon.
        ///
        /// A body we cannot read falls back to the status number, which is what the direct vendor route has
        /// always said and is still the right answer for a vendor that owes us no error shape.</summary>
        internal static string RefusalReason(string text, HttpStatusCode status, out string code)
        {
            code = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            JsonElement error, reason;
                            if (doc.RootElement.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.String)
                                code = error.GetString();
                            if (doc.RootElement.TryGetProperty("reason", out reason) && reason.ValueKind == JsonValueKind.String)
                            {
                                var sentence = reason.GetString();
                                // A server fault the relay explains (502 upstream_failed, 504 upstream_timeout) is still one nobody at the
                                // keyboard can act on, so it reads as the calm sentence; the relay's own free refusals (a pause on a 503)
                                // keep their words (see IsFreeRelayRefusal).
                                if (!string.IsNullOrWhiteSpace(sentence))
                                    return (int)status >= 500 && !IsFreeRelayRefusal(status, code) ? NotAvailableReason : sentence.Trim();
                            }
                        }
                    }
                }
                catch (Exception e) when (e is JsonException || e is FormatException || e is OverflowException)
                {
                    // An unreadable refusal is still a refusal. Fall through to the status number.
                }
            }

            // The status NUMBER is jargon and goes to the log line only (the code out-parameter), never into a sentence
            // a person reads: the Plan header once said "because the advice service answered 422" (28 Sep).
            code ??= ((int)status).ToString(CultureInfo.InvariantCulture);
            var n = (int)status;
            // A refusal with no reason of its own: the person cannot sign in again to fix it (the sign-in is the licence, and the
            // relay's reasons for a bad licence arrive with a sentence), so it is not blamed on "this session's sign-in".
            if (n == 401 || n == 403) return NotAvailableReason;
            if (n == 402) return "the advice allowance is used up for now";
            // No promise of when it is asked again: a landed plan run is reused for the same revision, and this sentence
            // also explains other asks (the allowance reading), so "later" was not true of every caller (verify, 28 Sep, L9).
            if (n == 429) return "the advice service is busy right now";
            if (n >= 500) return NotAvailableReason;
            return "the advice service could not read the question it was sent";
        }

        private static DecisionAnswers Parse(DecisionRequest request, string text)
        {
            try
            {
                using (var doc = JsonDocument.Parse(text))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        return DecisionAnswers.NotDecided(request, "the advice service sent something that was not an answer");

                    JsonElement answers;
                    if (!root.TryGetProperty("answers", out answers))
                        return DecisionAnswers.NotDecided(request, "the advice service sent no answers");

                    JsonElement served;
                    var version = root.TryGetProperty("model", out served) && served.ValueKind == JsonValueKind.String
                        ? served.GetString() : null;

                    // A usage count that no int can hold makes the whole reply unreadable, deliberately: a
                    // decided answer settles the budget hold at the reported cost, so reading a broken count
                    // as zero would settle a call that may really have been charged at nothing. Unreadable
                    // leaves the hold standing as spend, which is the side to be wrong on.
                    int inputTokens = 0, outputTokens = 0;
                    JsonElement usage, tokens;
                    if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("input_tokens", out tokens) && tokens.ValueKind == JsonValueKind.Number)
                            inputTokens = tokens.GetInt32();
                        if (usage.TryGetProperty("output_tokens", out tokens) && tokens.ValueKind == JsonValueKind.Number)
                            outputTokens = tokens.GetInt32();
                    }

                    return DecisionAnswerReader.Read(request, answers, version, inputTokens, outputTokens);
                }
            }
            // JsonException is not the whole boundary. A number that is syntactically valid JSON but that no
            // double or int can hold, such as 1e400, throws FormatException out of a Get, and an untrusted
            // reply must never turn into an exception escaping a model operation. (Codex on PR 384.)
            catch (Exception e) when (e is JsonException || e is FormatException || e is OverflowException)
            {
                return DecisionAnswers.NotDecided(request, "the advice service sent a reply that could not be read");
            }
        }

        /// <summary>One structured line per ask. Counts, latency and the POINT NAME only. Never a snapshot,
        /// never a question, never a key, never a reply. If you are adding a field here, that is the rule to
        /// check it against.</summary>
        private void Log(string point, int attempts, TimeSpan elapsed, string outcome)
        {
            try
            {
                var line = string.Format(CultureInfo.InvariantCulture,
                    "[decision] point={0} attempts={1} ms={2} outcome={3}",
                    point, attempts, (int)elapsed.TotalMilliseconds, outcome);
                Console.Error.WriteLine(line);
                LogLineForTest?.Invoke(line);
            }
            catch { /* a log line must never break an operation */ }
        }

        /// <summary>Read the licence's monthly allowance from the relay, through the SAME bearer the asks use.
        ///
        /// Two allowances exist and they measure different things, which is why this one is fetched rather than
        /// computed. <see cref="DecisionBudget"/> is a per-SESSION ceiling on this process, held locally so a
        /// runaway loop cannot spend all month in one sitting. The relay's is the per-LICENCE monthly figure,
        /// settled server side across every session on every machine this licence is used from, and nothing
        /// here can know it. Both are reported by <c>get_decision_budget</c> and neither is the other.
        ///
        /// Never throws, and never turns a status read into a failed operation. A refusal, a timeout, an
        /// unreachable host or an unreadable reply all come back as a reading whose numbers are absent with a
        /// plain reason, exactly the way an ask that could not be answered does.
        ///
        /// Returns null in direct vendor mode: there is no relay to ask, and inventing a figure would be worse
        /// than saying nothing.</summary>
        public async Task<RelayBudgetReading> ReadBudgetAsync(CancellationToken ct)
        {
            if (_relayBase == null) return null;

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_deadline);
                HttpResponseMessage response = null;
                try
                {
                    using (var message = new HttpRequestMessage(HttpMethod.Get, _relayBase + BudgetRoute))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
                        response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                    }

                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        string code;
                        return RelayBudgetReading.Unread(RefusalReason(text, response.StatusCode, out code));
                    }

                    using (var doc = JsonDocument.Parse(text))
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Object)
                            return RelayBudgetReading.Unread("the advice service sent something that was not an allowance");

                        JsonElement used, cap, resets;
                        var haveUsed = doc.RootElement.TryGetProperty("used", out used) && used.ValueKind == JsonValueKind.Number;
                        var haveCap = doc.RootElement.TryGetProperty("cap", out cap) && cap.ValueKind == JsonValueKind.Number;
                        if (!haveUsed || !haveCap)
                            return RelayBudgetReading.Unread("the advice service did not say how much of the month is left");

                        return new RelayBudgetReading
                        {
                            Read = true,
                            UsedInputTokens = used.GetInt64(),
                            CapInputTokens = cap.GetInt64(),
                            ResetsAtUtc = doc.RootElement.TryGetProperty("resetsAt", out resets) && resets.ValueKind == JsonValueKind.String
                                ? resets.GetString() : null,
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    return RelayBudgetReading.Unread("the advice service did not answer in time");
                }
                catch (HttpRequestException)
                {
                    return RelayBudgetReading.Unread("the advice service could not be reached");
                }
                catch (Exception e) when (e is JsonException || e is FormatException || e is OverflowException)
                {
                    return RelayBudgetReading.Unread("the advice service sent a reply that could not be read");
                }
                finally
                {
                    if (response != null) response.Dispose();
                }
            }
        }

        public void Dispose()
        {
            _cap.Dispose();
            if (_ownsHttp) _http.Dispose();
        }
    }

    /// <summary>What the relay said about this licence's month, or the plain reason it did not say. Counts and
    /// a date only: no identity, no subscription id, nothing about who the licence belongs to.</summary>
    public sealed class RelayBudgetReading
    {
        /// <summary>True when the numbers below are the relay's. False means <see cref="Reason"/> says why
        /// there are none, and a caller must not render a zero in their place.</summary>
        public bool Read { get; set; }

        public long UsedInputTokens { get; set; }
        public long CapInputTokens { get; set; }

        /// <summary>When the month rolls over, as the relay's own ISO instant, or null when it did not say.</summary>
        public string ResetsAtUtc { get; set; }

        /// <summary>Why the allowance could not be read, in plain words. Null when it was.</summary>
        public string Reason { get; set; }

        internal static RelayBudgetReading Unread(string reason) => new RelayBudgetReading { Read = false, Reason = reason };
    }
}
