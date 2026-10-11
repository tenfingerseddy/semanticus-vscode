// The production route for typed decisions: the hosted relay, on a licence, with no vendor key anywhere.
//
// F-194 is the failure this suite exists to stop coming back. The oracle was built from SEMANTICUS_JEV_KEY
// and nothing else, so an INSTALLED Pro session had no route to a decision service at all: no extension
// path sets that variable, so the engine built the null oracle and every decision point in the product was
// skipped in silence. Nothing failed, nothing was logged as wrong, and the plan simply stayed in rule order
// forever.
//
// Four things are proved here and each one is a different way that could come back:
//   the route is chosen from what an installed copy actually has, which is a licence;
//   the vendor is never contacted on that route, asserted by a handler that fails on the vendor host
//   rather than by reading the code;
//   what the engine sends is what the relay will admit, checked against tools/relay/templates.mjs read
//   off the disk, so a change on either side fails here instead of at a customer;
//   a refusal is a plain sentence in the outcome, never an exception, and never a second charge.
//
// The template check is the C# half of tools/relay/test/drift.test.mjs. That test reads the engine's
// registry and compares it to the relay's templates; this one builds a real request through the real
// client and compares the BYTES ON THE WIRE to the same templates. They overlap on purpose: the JS side
// catches a declaration that drifted, and this side catches a client that declares one thing and sends
// another.
//
// No test in this file may reach a network. Every handler is scripted, and one of them throws on the
// vendor host so a regression cannot pass quietly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionRelayTests
    {
        private const string RelayBase = "https://relay.test.invalid";
        private const string Licence = "not-a-real-licence-token";

        // ---- which route a session gets ------------------------------------------------------------

        [Fact]
        public void A_pro_licence_and_no_vendor_key_gives_the_relay()
        {
            using (new EnvVar(JevDecisionOracle.KeyVariable, null))
            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, RelayBase))
            using (var oracle = JevDecisionOracle.FromEnvironment(new DecisionBudget(), Licence, new RefusingHandler()))
            {
                Assert.NotNull(oracle);
                Assert.True(oracle.IsRelay);
                Assert.Equal(JevDecisionOracle.RouteRelay, oracle.Route);
            }
        }

        [Fact]
        public void A_vendor_key_wins_and_is_the_development_route()
        {
            // A developer who set the key meant it. Falling through to the relay would quietly spend a
            // customer's monthly allowance on a debugging session.
            using (new EnvVar(JevDecisionOracle.KeyVariable, "not-a-real-vendor-key"))
            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, RelayBase))
            using (var oracle = JevDecisionOracle.FromEnvironment(new DecisionBudget(), Licence, new RefusingHandler()))
            {
                Assert.NotNull(oracle);
                Assert.False(oracle.IsRelay);
                Assert.Equal(JevDecisionOracle.RouteVendor, oracle.Route);
            }
        }

        [Fact]
        public void With_neither_a_key_nor_a_licence_there_is_no_service_at_all()
        {
            // The free session, and the default. A null oracle is the caller's signal to use the null one.
            using (new EnvVar(JevDecisionOracle.KeyVariable, null))
            {
                Assert.Null(JevDecisionOracle.FromEnvironment(new DecisionBudget(), null, new RefusingHandler()));
                Assert.Null(JevDecisionOracle.FromEnvironment(new DecisionBudget(), "   ", new RefusingHandler()));
            }
        }

        [Fact]
        public void A_relay_address_that_is_not_an_address_is_ignored_rather_than_obeyed()
        {
            // A typo in an environment variable must not become a request to somewhere nobody chose.
            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, "relay.example.com"))
                Assert.Equal(JevDecisionOracle.DefaultRelayBaseUrl, JevDecisionOracle.RelayBaseFromEnvironment());

            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, "file:///etc/passwd"))
                Assert.Equal(JevDecisionOracle.DefaultRelayBaseUrl, JevDecisionOracle.RelayBaseFromEnvironment());

            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, null))
                Assert.Equal(JevDecisionOracle.DefaultRelayBaseUrl, JevDecisionOracle.RelayBaseFromEnvironment());

            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, RelayBase + "/"))
                Assert.Equal(RelayBase, JevDecisionOracle.RelayBaseFromEnvironment());
        }

        // ---- the vendor is not on the production route ----------------------------------------------

        [Fact]
        public async Task On_a_licence_the_ask_goes_to_the_relay_and_the_vendor_is_never_contacted()
        {
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler))
            {
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.True(answers.Decided, answers.NotDecidedReason);
                var sent = Assert.Single(handler.Sent);
                Assert.Equal(RelayBase + "/v1/decide", sent.Url);
                Assert.Equal("Bearer", sent.AuthScheme);
                Assert.Equal(Licence, sent.AuthParameter);

                // Nothing in the request names the vendor, and no vendor key exists to be sent. The host
                // string is rebuilt from parts so this assertion does not itself put a second copy of it in
                // an engine-adjacent file.
                var vendorHost = string.Join(".", new[] { "api", "typesafe", "ai" });
                Assert.DoesNotContain(vendorHost, sent.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(vendorHost, sent.Url, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task On_a_development_key_the_ask_goes_straight_to_the_vendor()
        {
            using (var handler = new CapturingHandler())
            using (var oracle = new JevDecisionOracle("not-a-real-vendor-key", new DecisionBudget(), handler))
            {
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.True(answers.Decided, answers.NotDecidedReason);
                var sent = Assert.Single(handler.Sent);
                Assert.Equal(JevDecisionOracle.Endpoint, sent.Url);
                Assert.Equal("not-a-real-vendor-key", sent.AuthParameter);
                // The vendor body still carries the model alias and still puts the reference in a header.
                Assert.Contains("\"model\":", sent.Body, StringComparison.Ordinal);
                Assert.False(string.IsNullOrEmpty(sent.IdempotencyHeader));
            }
        }

        [Fact]
        public async Task The_relay_body_is_the_relays_contract_and_not_the_vendors()
        {
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler))
            {
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply));
                await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                var sent = Assert.Single(handler.Sent);
                using (var doc = JsonDocument.Parse(sent.Body))
                {
                    var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
                    Assert.Equal(new[] { "point", "schemaVersion", "snapshot", "questions", "idempotencyKey" }, keys);

                    // The caller does NOT choose the served model. That is the relay's job and the whole
                    // reason the body is a different shape from the vendor's.
                    Assert.False(doc.RootElement.TryGetProperty("model", out _));
                    Assert.False(doc.RootElement.TryGetProperty("state", out _));

                    Assert.Equal(DecisionRegistry.MeasureFolderAndFormat, doc.RootElement.GetProperty("point").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("idempotencyKey").GetString()));
                }

                // The reference travels in the body, so a second copy in a header would be an unread one.
                Assert.Null(sent.IdempotencyHeader);
            }
        }

        // ---- what we send is what the relay admits ---------------------------------------------------

        [Fact]
        public async Task Every_registered_point_sends_only_what_the_relay_admits()
        {
            var templates = RelayTemplates.Read();

            Assert.True(templates.Points.Count >= 3,
                "the relay templates parsed to " + templates.Points.Count + " points; the parser has lost its grip on the file");
            Assert.Equal(
                DecisionRegistry.Points.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                templates.Points.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray());

            foreach (var request in RealRequests())
            {
                using (var handler = new CapturingHandler { FailOnVendorHost = true })
                using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(capInputTokens: 5_000_000), handler))
                {
                    // The relay's own refusal path is not what is under test here; the BODY is. A paused
                    // switch is the cheapest reply that leaves the request fully built.
                    handler.Replies.Enqueue((HttpStatusCode.ServiceUnavailable,
                        "{\"error\":\"paused\",\"reason\":\"Cloud advice is paused right now. Nothing else has changed.\"}"));

                    await oracle.AskAsync(request, CancellationToken.None);

                    var sent = Assert.Single(handler.Sent);
                    var template = templates.Points[request.PointName];

                    using (var doc = JsonDocument.Parse(sent.Body))
                    {
                        Assert.Equal(request.PointName, doc.RootElement.GetProperty("point").GetString());

                        // The relay refuses a schemaVersion that is not the one it pinned, so the number the
                        // client sends has to be the number the template names.
                        Assert.Equal(template.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
                        Assert.Equal(template.SchemaVersion, request.SchemaVersion);

                        var snapshotKeys = doc.RootElement.GetProperty("snapshot")
                            .EnumerateObject().Select(p => p.Name).ToArray();
                        foreach (var key in snapshotKeys)
                            Assert.True(template.Fields.Contains(key, StringComparer.Ordinal),
                                request.PointName + " sent the snapshot field '" + key + "', which the relay template does not declare");

                        var questionNames = doc.RootElement.GetProperty("questions")
                            .EnumerateObject().Select(p => p.Name).ToArray();
                        Assert.NotEmpty(questionNames);
                        Assert.True(questionNames.Length <= template.MaxQuestions,
                            request.PointName + " sent " + questionNames.Length + " questions and the relay admits "
                            + template.MaxQuestions);

                        foreach (var name in questionNames)
                        {
                            var admitted = template.QuestionPrefix != null
                                ? name.StartsWith(template.QuestionPrefix, StringComparison.Ordinal)
                                    && name.Length > template.QuestionPrefix.Length
                                : template.Questions.Contains(name, StringComparer.Ordinal);
                            Assert.True(admitted,
                                request.PointName + " asked '" + name + "', which the relay template does not admit");
                        }

                        // The relay caps the snapshot at the same 64 KB the client does, so a request that
                        // clears the client's cap has to clear the server's.
                        var snapshotBytes = Encoding.UTF8.GetByteCount(doc.RootElement.GetProperty("snapshot").GetRawText());
                        Assert.True(snapshotBytes <= templates.MaxSnapshotBytes,
                            request.PointName + " built a " + snapshotBytes + " byte snapshot and the relay admits "
                            + templates.MaxSnapshotBytes);
                    }

                    Assert.True(Encoding.UTF8.GetByteCount(sent.Body) <= templates.MaxRequestBytes,
                        request.PointName + " built a body the relay would refuse as too large");
                }
            }
        }

        [Fact]
        public void A_whole_plan_of_scored_items_is_inside_the_bound_the_relay_pins()
        {
            // Astra's finding 13 in the other direction: the engine sends one question per plan item, up to
            // forty, and the general bound is eight. If the relay's plan.rank bound ever drops back to the
            // general one, every real plan 413s. The bound is read off the file rather than written here.
            var templates = RelayTemplates.Read();
            Assert.Equal(PlanRankPoint.MaxScoredItems, templates.Points[DecisionRegistry.PlanRank].MaxQuestions);
            Assert.True(templates.Points[DecisionRegistry.PlanRank].MaxQuestions > templates.MaxQuestions,
                "plan.rank needs its own bound; the general one would refuse every real plan");
        }

        // ---- a refusal is a sentence, not an exception -----------------------------------------------

        [Theory]
        [InlineData(401, "no_licence", "This needs a Semanticus Pro licence. Sign in and try again.")]
        [InlineData(402, "licence_budget_spent", "This licence has used its cloud advice for the month. It resets on 2026-10-01.")]
        [InlineData(429, "too_many_in_flight", "Too many advice requests are already running for this licence. Try again in a moment.")]
        [InlineData(503, "paused", "Cloud advice is paused right now. Nothing else has changed.")]
        public async Task A_relay_refusal_comes_back_as_the_relays_own_sentence(int status, string code, string reason)
        {
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler))
            {
                handler.Replies.Enqueue(((HttpStatusCode)status,
                    "{\"error\":\"" + code + "\",\"reason\":" + JsonSerializer.Serialize(reason) + "}"));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.False(answers.Decided);
                // The relay already writes in the product's words, so its sentence is carried through rather
                // than replaced by a status number nobody can act on.
                Assert.Equal(reason, answers.NotDecidedReason);

                // ONE attempt. A retry through the relay is a second hold and a second possible charge, and
                // it cannot change a refusal the relay has already decided.
                Assert.Single(handler.Sent);
            }
        }

        [Fact]
        public async Task A_refusal_the_relay_did_not_explain_still_comes_back_as_a_sentence()
        {
            foreach (var body in new[] { "", "not json at all", "[]", "{\"error\":\"paused\"}" })
            {
                using (var handler = new CapturingHandler { FailOnVendorHost = true })
                using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler))
                {
                    handler.Replies.Enqueue((HttpStatusCode.BadGateway, body));

                    var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                    Assert.False(answers.Decided);
                    Assert.False(string.IsNullOrWhiteSpace(answers.NotDecidedReason), body);
                    // Plain words, never the status number (m11/plan-harm: the Plan header once read "because the
                    // advice service answered 422"). The number still goes to the log line.
                    Assert.DoesNotContain("502", answers.NotDecidedReason, StringComparison.Ordinal);
                    Assert.Equal(JevDecisionOracle.NotAvailableReason, answers.NotDecidedReason);
                }
            }
        }

        /// <summary>The relay refuses for free before it reserves or forwards anything (relay.md, "The order of the
        /// gates", and its refusal table), so this session was charged nothing for such a refusal and must not count
        /// one. It used to count every refusal at its estimate, and after the licence's month ran out a few ordinary
        /// edits' worth of refused asks used this session's budget up: every row then blamed this session instead of
        /// the month (ai.schema verify round 5, D1). One case per kind of free refusal the engine meets.</summary>
        [Theory]
        [InlineData(402, "licence_budget_spent", "This licence has used its cloud advice for the month. It resets on 2026-10-01.")]
        [InlineData(503, "paused", "Cloud advice is paused right now. Nothing else has changed.")]
        // The served model's own switch. relay.md folds it into "paused"; the worker names it (tools/relay/worker.mjs).
        [InlineData(503, "model_paused", "Cloud advice is paused right now. Nothing else has changed.")]
        [InlineData(503, "house_budget_spent", "Cloud advice is at its limit right now. Try again later.")]
        [InlineData(429, "too_many_in_flight", "Too many advice requests are already running for this licence. Try again in a moment.")]
        [InlineData(403, "expired", "This licence has run out. Renew it to use cloud advice again.")]
        public async Task A_refusal_the_relay_makes_before_it_forwards_costs_this_session_nothing(int status, string code, string reason)
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, budget, handler))
            {
                handler.Replies.Enqueue(((HttpStatusCode)status,
                    "{\"error\":\"" + code + "\",\"reason\":" + JsonSerializer.Serialize(reason) + "}"));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.Equal(reason, answers.NotDecidedReason);   // precondition: the relay's own refusal came back
                Assert.Equal(0, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
                Assert.Equal(1, budget.Requests);   // it still reached a service, and is counted as an attempt
            }
        }

        /// <summary>A hold is not spend until it settles, and a free refusal gives it back. So an ask whose hold does not
        /// fit ONLY because another ask is holding capacity right now is not stopped by this session's budget: it waits
        /// for that hold to settle and asks then. It used to be refused as the budget at once, and a batch then told a
        /// person "this session has used its suggestion budget" although the other ask's hold was given back a moment
        /// later and nothing had been spent (Sol on the round 5 fix). Two sizes of allowance: one where the other hold
        /// makes the budget read as used up, one where it only leaves too little room for this ask.</summary>
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public async Task An_ask_that_does_not_fit_only_because_another_is_holding_capacity_waits_for_that_hold(double room)
        {
            const string Month = "This licence has used its cloud advice for the month. It resets on 2026-10-01.";
            var request = DecisionFixture.SampleRequest();
            var estimate = JevDecisionOracle.EstimateInputTokens(JevDecisionOracle.BuildRelayBody(
                request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot), new string('0', 64)));
            var budget = new DecisionBudget(capInputTokens: (long)(estimate * room));
            using (var handler = new GatedRelay("{\"error\":\"licence_budget_spent\",\"reason\":" + JsonSerializer.Serialize(Month) + "}"))
            using (var oracle = new JevDecisionOracle(Licence, budget, handler, TimeSpan.FromSeconds(20), maxConcurrent: 2, RelayBase))
            {
                var holding = Task.Run(() => oracle.AskAsync(request, CancellationToken.None));
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first ask is out and holding its estimate");
                var waiting = Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None));
                var early = await Task.WhenAny(waiting, Task.Delay(500)) == waiting;
                handler.Gate.TrySetResult(true);   // the first ask is refused for the month, for free

                var first = await holding;
                var second = await waiting;

                Assert.Equal(Month, first.NotDecidedReason);   // precondition
                Assert.False(second.OverBudget,
                    "the second ask was stopped as this session's budget " + (early ? "at once" : "") + " while the only thing "
                    + "in the way was the first ask's hold, which was given back: " + second.NotDecidedReason);
                Assert.Equal(Month, second.NotDecidedReason);
                Assert.Equal(2, handler.Sends);
                Assert.Equal(0, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>An ask that waited for another ask's hold and was stopped (its caller moved on, or its deadline
        /// passed) in the moment between waking with its own hold and sending must not go out, and costs nothing. A
        /// continuation woken by a settle runs later than the settle, so that moment is real; the check before the send
        /// is what closes it, and the seam puts the cancel exactly there (Sol on the round 5 fix, second pass).</summary>
        [Fact]
        public async Task An_ask_stopped_between_waking_with_its_hold_and_sending_sends_nothing_and_costs_nothing()
        {
            const string Month = "This licence has used its cloud advice for the month. It resets on 2026-10-01.";
            var request = DecisionFixture.SampleRequest();
            var estimate = JevDecisionOracle.EstimateInputTokens(JevDecisionOracle.BuildRelayBody(
                request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot), new string('0', 64)));
            var budget = new DecisionBudget(capInputTokens: estimate);
            using (var handler = new GatedRelay("{\"error\":\"licence_budget_spent\",\"reason\":" + JsonSerializer.Serialize(Month) + "}",
                                                502, "{\"error\":\"upstream_failed\",\"reason\":\"The advice service could not answer.\"}"))
            using (var oracle = new JevDecisionOracle(Licence, budget, handler, TimeSpan.FromSeconds(20), maxConcurrent: 2, RelayBase))
            using (var caller = new CancellationTokenSource())
            {
                oracle.ReservedAfterWaitForTest = () => caller.Cancel();
                var holding = Task.Run(() => oracle.AskAsync(request, CancellationToken.None));
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first ask is out and holding the whole allowance");
                var waiting = Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest(), caller.Token));
                Assert.False(await Task.WhenAny(waiting, Task.Delay(500)) == waiting, "precondition: the second ask is waiting for the hold");
                handler.Gate.TrySetResult(true);

                await holding;
                var second = await waiting;

                Assert.True(caller.IsCancellationRequested, "precondition: the second ask woke with its hold and was stopped there");
                Assert.Equal(1, handler.Sends);
                Assert.Equal(JevDecisionOracle.CancelledReason, second.NotDecidedReason);
                Assert.Equal(0, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
                Assert.Equal(1, budget.Requests);   // only the first ask reached a service
            }
        }

        [Theory]
        [InlineData(502, "upstream_failed", "The advice service could not answer.")]
        [InlineData(504, "upstream_timeout", "The advice service did not answer in time.")]
        // A free refusal's code on a status the relay never gives it is not the relay's contract, so it is not free.
        [InlineData(502, "paused", "Cloud advice is paused right now. Nothing else has changed.")]
        public async Task A_refused_ask_still_counts_as_spend_that_may_have_been_charged(int status, string code, string reason)
        {
            // The relay reserves before it forwards, so a 402 really did cost nothing. A 502 or a 504 did not: the
            // relay had already forwarded and is holding the charge, so the estimate stands as spend, which is the
            // side to be wrong on.
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, budget, handler))
            {
                handler.Replies.Enqueue(((HttpStatusCode)status,
                    "{\"error\":\"" + code + "\",\"reason\":" + JsonSerializer.Serialize(reason) + "}"));

                await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.True(budget.UsedInputTokens > 0);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        // ---- the monthly allowance -------------------------------------------------------------------

        [Fact]
        public async Task The_monthly_allowance_is_read_from_the_relay_through_the_same_bearer()
        {
            using (var f = new DecisionFixture())
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            {
                var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler);
                f.Engine.UseDecisionsForTest(new DecisionService(oracle, oracle.Budget));
                handler.Replies.Enqueue((HttpStatusCode.OK,
                    "{\"used\":1250000,\"cap\":30000000,\"resetsAt\":\"2026-10-01T00:00:00.000Z\"}"));

                var info = await f.Engine.GetDecisionBudgetAsync();

                var sent = Assert.Single(handler.Sent);
                Assert.Equal(RelayBase + "/v1/budget", sent.Url);
                Assert.Equal("GET", sent.Method);
                Assert.Equal(Licence, sent.AuthParameter);

                Assert.Equal(JevDecisionOracle.RouteRelay, info.Route);
                Assert.Equal(1250000, info.MonthlyUsedInputTokens);
                Assert.Equal(30000000, info.MonthlyCapInputTokens);
                Assert.Equal("2026-10-01T00:00:00.000Z", info.MonthlyResetsAtUtc);
                Assert.Null(info.MonthlyReason);

                // The session's own counter is a different number and is not overwritten by the month's.
                Assert.Equal(oracle.Budget.CapInputTokens, info.CapInputTokens);
                Assert.Equal(0, info.UsedInputTokens);
            }
        }

        [Fact]
        public async Task A_relay_that_cannot_be_read_says_so_rather_than_reporting_a_zero()
        {
            using (var f = new DecisionFixture())
            using (var handler = new CapturingHandler { FailOnVendorHost = true })
            {
                var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(), handler);
                f.Engine.UseDecisionsForTest(new DecisionService(oracle, oracle.Budget));
                handler.Replies.Enqueue((HttpStatusCode.Unauthorized,
                    "{\"error\":\"bad_signature\",\"reason\":\"That licence did not come from Semanticus.\"}"));

                var info = await f.Engine.GetDecisionBudgetAsync();

                Assert.Equal(JevDecisionOracle.RouteRelay, info.Route);
                Assert.Null(info.MonthlyUsedInputTokens);
                Assert.Null(info.MonthlyCapInputTokens);
                Assert.Equal("That licence did not come from Semanticus.", info.MonthlyReason);
            }
        }

        [Fact]
        public async Task With_no_service_the_budget_says_so_on_both_doors_and_asks_nothing()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                f.UseNull();

                var viaMcp = await McpTools.GetDecisionBudget(f.Engine);
                var viaRpc = await new EngineRpcTarget(f.Engine).getDecisionBudget();

                foreach (var info in new[] { viaMcp, viaRpc })
                {
                    Assert.Equal(JevDecisionOracle.RouteNone, info.Route);
                    Assert.Null(info.MonthlyUsedInputTokens);
                    Assert.False(string.IsNullOrWhiteSpace(info.MonthlyReason));
                }
                Assert.Equal(viaMcp.MonthlyReason, viaRpc.MonthlyReason);
            }
        }

        [Fact]
        public async Task The_development_route_reports_no_monthly_allowance_and_asks_for_none()
        {
            using (var f = new DecisionFixture())
            using (var handler = new RefusingHandler())
            {
                var oracle = new JevDecisionOracle("not-a-real-vendor-key", new DecisionBudget(), handler);
                f.Engine.UseDecisionsForTest(new DecisionService(oracle, oracle.Budget));

                // RefusingHandler throws on any send, so this passing at all is the proof that no budget
                // request was made. There is no relay to ask.
                var info = await f.Engine.GetDecisionBudgetAsync();

                Assert.Equal(JevDecisionOracle.RouteVendor, info.Route);
                Assert.Null(info.MonthlyCapInputTokens);
                Assert.Contains("development", info.MonthlyReason, StringComparison.Ordinal);
            }
        }

        // ---- the wiring F-194 was actually missing ----------------------------------------------------

        [Fact]
        public async Task An_installed_pro_session_builds_the_relay_oracle_and_not_the_null_one()
        {
            // THE regression. The engine used to read SEMANTICUS_JEV_KEY and nothing else, so an installed Pro
            // copy, which has a licence and no such variable, built the null oracle: every point skipped, no
            // error, no log line, and a plan stuck in rule order forever. The licence is minted here against a
            // throwaway keypair so the test proves the real evaluator's real output reaches the real oracle.
            var keys = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(keys.privateKey, new LicenseClaims
            {
                Sub = "relay-test@example.invalid",
                Tier = "pro",
                Iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
            });
            var entitlement = LicenseEntitlement.Evaluate(token, keys.publicKey, devPro: false, DateTimeOffset.UtcNow);
            Assert.True(entitlement.IsPro);

            using (new EnvVar(JevDecisionOracle.KeyVariable, null))
            using (new EnvVar(JevDecisionOracle.RelayUrlVariable, RelayBase))
            using (var f = new DecisionFixture(entitlement))
            {
                await f.BuildModelAsync();

                var oracle = Assert.IsType<JevDecisionOracle>(f.Engine.Decisions.Oracle);
                Assert.True(oracle.IsRelay, "an installed Pro session must reach the relay, not nothing");
                Assert.Equal(JevDecisionOracle.RouteRelay, oracle.Route);
                Assert.True(oracle.Available);

                // get_decision_budget is deliberately NOT called here. This oracle holds a real HttpClient
                // aimed at a real address, and asking it for the month would be a network attempt. The
                // mapping from a relay oracle to the budget response is proved against a scripted handler
                // in The_monthly_allowance_is_read_from_the_relay_through_the_same_bearer.
            }
        }

        [Fact]
        public async Task The_dev_escape_unlocks_pro_locally_and_still_buys_nothing()
        {
            // SEMANTICUS_DEV_PRO is a local feature gate and it is allowed to be generous. Spending money is
            // not: the relay refuses the flag server side, so the engine hands it no credential and there is
            // no service at all. A free session reads exactly the same.
            using (new EnvVar(JevDecisionOracle.KeyVariable, null))
            using (var f = new DecisionFixture(LicenseEntitlement.DevPro()))
            {
                await f.BuildModelAsync();

                Assert.Same(NullDecisionOracle.Instance, f.Engine.Decisions.Oracle);
                var info = await f.Engine.GetDecisionBudgetAsync();
                Assert.Equal(JevDecisionOracle.RouteNone, info.Route);
                Assert.False(info.ServiceAvailable);
            }
        }

        [Fact]
        public async Task A_free_session_still_has_no_service_at_all()
        {
            using (new EnvVar(JevDecisionOracle.KeyVariable, null))
            using (var f = new DecisionFixture(TestEntitlements.Free))
            {
                await f.BuildModelAsync();
                Assert.Same(NullDecisionOracle.Instance, f.Engine.Decisions.Oracle);
            }
        }

        // ---- join fixes (M11 join part 1, ai.schema final verify low items) -----------------------------

        private const string MonthSentence = "This licence has used its cloud advice for the month. It resets on 2026-10-01.";
        private const string PausedSentence = "Cloud advice is paused right now. Nothing else has changed.";

        private static string RefusalBody(string code, string reason) =>
            "{\"error\":" + JsonSerializer.Serialize(code) + ",\"reason\":" + JsonSerializer.Serialize(reason) + "}";

        private static int RelayEstimate(DecisionRequest request) =>
            JevDecisionOracle.EstimateInputTokens(JevDecisionOracle.BuildRelayBody(
                request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot), new string('0', 64)));

        /// <summary>An ask stopped by its caller while it waited for another ask's hold never went out, so the operator log
        /// must not count an attempt for it. It used to say attempts=1, because the count was set before the hold was taken
        /// (ai.schema final verify, low item 2; the verifier's attack T1).</summary>
        [Fact]
        public async Task A_callers_cancel_while_waiting_for_another_asks_hold_is_logged_with_no_attempt_and_costs_nothing()
        {
            var request = DecisionFixture.SampleRequest();
            var budget = new DecisionBudget(capInputTokens: RelayEstimate(request));
            var log = new List<string>();
            using (var handler = new GatedRelay(RefusalBody("licence_budget_spent", MonthSentence)))
            using (var oracle = new JevDecisionOracle(Licence, budget, handler, TimeSpan.FromSeconds(20), maxConcurrent: 2, RelayBase))
            using (var caller = new CancellationTokenSource())
            {
                oracle.LogLineForTest = l => { lock (log) log.Add(l); };
                var holding = Task.Run(() => oracle.AskAsync(request, CancellationToken.None));
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first ask is out and holds the whole allowance");
                var waiting = Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest(), caller.Token));
                Assert.False(await Task.WhenAny(waiting, Task.Delay(500)) == waiting, "precondition: the second ask is waiting for the hold");
                caller.Cancel();
                Assert.True(await Task.WhenAny(waiting, Task.Delay(5000)) == waiting, "the second ask did not come back after its caller cancelled");
                var second = await waiting;

                Assert.Equal(1, handler.Sends);
                Assert.False(second.OverBudget, "a cancelled wait was reported as the budget: " + second.NotDecidedReason);
                Assert.Equal(JevDecisionOracle.CancelledReason, second.NotDecidedReason);
                string cancelledLine;
                lock (log) cancelledLine = Assert.Single(log, l => l.Contains("outcome=cancelled", StringComparison.Ordinal));
                Assert.Contains("attempts=0 ", cancelledLine, StringComparison.Ordinal);

                handler.Gate.TrySetResult(true);
                var first = await holding;
                Assert.Equal(MonthSentence, first.NotDecidedReason);
                Assert.Equal(0, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
                Assert.Equal(1, budget.Requests);
            }
        }

        /// <summary>The same for an ask whose deadline passed while it waited for another ask's hold: it is dropped unsent
        /// with the queue's reason, costs nothing, and the log counts no attempt (low item 2; the verifier's attack T2).</summary>
        [Fact]
        public async Task An_ask_whose_deadline_passes_while_it_waits_for_a_hold_is_logged_with_no_attempt_and_costs_nothing()
        {
            var request = DecisionFixture.SampleRequest();
            var budget = new DecisionBudget(capInputTokens: RelayEstimate(request));
            var log = new List<string>();
            using (var handler = new GatedRelay(RefusalBody("licence_budget_spent", MonthSentence)) { FirstIsDeaf = true })
            using (var oracle = new JevDecisionOracle(Licence, budget, handler, TimeSpan.FromMilliseconds(1500), maxConcurrent: 2, RelayBase))
            {
                oracle.LogLineForTest = l => { lock (log) log.Add(l); };
                var holding = Task.Run(() => oracle.AskAsync(request, CancellationToken.None));
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "precondition: the first ask is out and holds the whole allowance");
                var waiting = Task.Run(() => oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None));
                Assert.True(await Task.WhenAny(waiting, Task.Delay(8000)) == waiting, "the second ask did not come back after its deadline passed");
                var second = await waiting;

                Assert.Equal(1, handler.Sends);
                Assert.False(second.OverBudget, "a wait that ran out of time was reported as the budget: " + second.NotDecidedReason);
                Assert.Equal(JevDecisionOracle.QueueDroppedReason, second.NotDecidedReason);
                Assert.Equal(0, budget.UsedInputTokens);
                string droppedLine;
                lock (log) droppedLine = Assert.Single(log, l => l.Contains("outcome=queue-deadline", StringComparison.Ordinal));
                Assert.Contains("attempts=0 ", droppedLine, StringComparison.Ordinal);

                handler.Gate.TrySetResult(true);
                await holding;   // it outlived its own deadline on purpose; how it settles is not what this test is about
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>The relay serves the same question under the same reference from its store and charges nothing for it
        /// (relay.md: "A retry with the same payload returns the stored answer and forwards nothing"; worker.mjs sends
        /// x-relay-tokens-used: 0). This session must not charge it again at the usage the stored body still reports. It
        /// used to, because the client never read the relay's own figure (low item 4; the verifier's attack T11).</summary>
        [Fact]
        public async Task An_answer_the_relay_serves_from_its_store_at_no_cost_is_not_charged_again()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new HeaderRelay())
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, budget, handler))
            {
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply, new Dictionary<string, string> { ["x-relay-tokens-used"] = "100" }));
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply, new Dictionary<string, string> { ["x-relay-tokens-used"] = "0", ["x-relay-cached"] = "1" }));

                var first = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                Assert.True(first.Decided, first.NotDecidedReason);
                var afterFirst = budget.UsedInputTokens;
                Assert.Equal(100, afterFirst);
                var again = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                Assert.True(again.Decided, again.NotDecidedReason);

                Assert.Equal(afterFirst, budget.UsedInputTokens);
                Assert.Equal(0, budget.ReservedInputTokens);
            }
        }

        /// <summary>The relay's own figure is what the licence was charged, so it is what this session counts, not the usage
        /// the body reports. A reply with no readable figure keeps the body's.</summary>
        [Theory]
        [InlineData("250", 250)]
        [InlineData(null, 100)]
        [InlineData("not a number", 100)]
        public async Task The_relays_own_charge_is_what_this_session_counts(string header, long charged)
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using (var handler = new HeaderRelay())
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, budget, handler))
            {
                var headers = new Dictionary<string, string>();
                if (header != null) headers["x-relay-tokens-used"] = header;
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply, headers));

                var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.True(answers.Decided, answers.NotDecidedReason);
                Assert.Equal(charged, budget.UsedInputTokens);
            }
        }

        /// <summary>NO BRAKE AFTER A SPENT MONTH OR A PAUSE (low item 5). Such a refusal is free, so without a brake every model
        /// change asks everything again and the relay refuses all of it until the month resets or the pause lifts. After the
        /// relay refuses for the licence's month (402) or a pause or the house's limit (503), this session sends nothing for
        /// the hold-off, on any point, and each ask comes back with the relay's own sentence; after the hold-off the next ask
        /// goes out.</summary>
        [Theory]
        [InlineData(402, "licence_budget_spent", MonthSentence)]
        [InlineData(503, "paused", PausedSentence)]
        [InlineData(503, "model_paused", PausedSentence)]
        [InlineData(503, "house_budget_spent", "Cloud advice is at its limit right now. Try again later.")]
        public async Task After_the_relay_refuses_for_the_month_or_a_pause_nothing_is_sent_until_the_hold_off_has_passed(int status, string code, string reason)
        {
            var now = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
            var budget = new DecisionBudget(capInputTokens: 100000);
            var log = new List<string>();
            using (var handler = new HeaderRelay())
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, budget, handler))
            {
                oracle.UtcNowForTest = () => now;
                oracle.LogLineForTest = l => { lock (log) log.Add(l); };
                handler.Replies.Enqueue(((HttpStatusCode)status, RefusalBody(code, reason), null));

                var refused = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                Assert.Equal(reason, refused.NotDecidedReason);   // precondition: the relay's refusal came back
                Assert.Equal(1, handler.Sends);

                // A model change a minute later asks again, on this point and on another: nothing goes out.
                now = now.AddMinutes(1);
                var held = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                var heldOther = await oracle.AskAsync(RealRequests().Last(), CancellationToken.None);
                Assert.Equal(1, handler.Sends);
                Assert.False(held.Decided);
                Assert.False(held.OverBudget, "a held-off ask was told this session's budget: " + held.NotDecidedReason);
                Assert.Equal(reason, held.NotDecidedReason);
                Assert.Equal(reason, heldOther.NotDecidedReason);
                Assert.Equal(1, budget.Requests);
                Assert.Equal(0, budget.UsedInputTokens);
                lock (log) Assert.Contains(log, l => l.Contains("attempts=0 ", StringComparison.Ordinal) && l.Contains("outcome=held-off", StringComparison.Ordinal));

                // Once the hold-off has passed, the next ask goes out again.
                now = now.Add(JevDecisionOracle.RefusedHoldOff);
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply, null));
                var after = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                Assert.Equal(2, handler.Sends);
                Assert.True(after.Decided, after.NotDecidedReason);
            }
        }

        /// <summary>A pause on ONE point (503 point_paused) holds off that point only; every other point still asks.</summary>
        [Fact]
        public async Task A_pause_on_one_point_holds_off_that_point_only()
        {
            const string PointPaused = "This kind of advice is paused right now.";
            var now = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
            using (var handler = new HeaderRelay())
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(capInputTokens: 100000), handler))
            {
                oracle.UtcNowForTest = () => now;
                handler.Replies.Enqueue((HttpStatusCode.ServiceUnavailable, RefusalBody("point_paused", PointPaused), null));
                await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                now = now.AddMinutes(1);
                var held = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                Assert.Equal(1, handler.Sends);
                Assert.Equal(PointPaused, held.NotDecidedReason);

                handler.Replies.Enqueue((HttpStatusCode.ServiceUnavailable, RefusalBody("paused", PausedSentence), null));
                var other = await oracle.AskAsync(RealRequests().Last(), CancellationToken.None);
                Assert.Equal(2, handler.Sends);
                Assert.Equal(PausedSentence, other.NotDecidedReason);
            }
        }

        /// <summary>A refusal that is not the month, a pause or the house's limit holds nothing off: a crowded moment (429), a
        /// failed forward (502), a code the relay's contract does not name, and a pause's code on a status the relay never
        /// gives it each leave the next ask free to go out.</summary>
        [Theory]
        [InlineData(429, "too_many_in_flight", "Too many advice requests are already running for this licence. Try again in a moment.")]
        [InlineData(502, "upstream_failed", "The advice service could not answer.")]
        [InlineData(503, "not_a_relay_code", "Something else went wrong.")]
        [InlineData(502, "paused", PausedSentence)]
        public async Task A_refusal_that_is_not_the_month_or_a_pause_holds_nothing_off(int status, string code, string reason)
        {
            var now = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
            using (var handler = new HeaderRelay())
            using (var oracle = JevDecisionOracle.ForRelay(Licence, RelayBase, new DecisionBudget(capInputTokens: 100000), handler))
            {
                oracle.UtcNowForTest = () => now;
                handler.Replies.Enqueue(((HttpStatusCode)status, RefusalBody(code, reason), null));
                handler.Replies.Enqueue((HttpStatusCode.OK, DecidedReply, null));

                await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);
                now = now.AddSeconds(5);
                var next = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

                Assert.Equal(2, handler.Sends);
                Assert.True(next.Decided, next.NotDecidedReason);
            }
        }

        /// <summary>Answers from a queue, each reply with the headers the test gives it; counts sends; never a network.</summary>
        private sealed class HeaderRelay : HttpMessageHandler
        {
            public readonly Queue<(HttpStatusCode Status, string Body, Dictionary<string, string> Headers)> Replies =
                new Queue<(HttpStatusCode, string, Dictionary<string, string>)>();
            private int _sends;
            public int Sends => Volatile.Read(ref _sends);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref _sends);
                (HttpStatusCode Status, string Body, Dictionary<string, string> Headers) reply;
                lock (Replies)
                {
                    if (Replies.Count == 0) throw new InvalidOperationException("the test queued no reply for this send");
                    reply = Replies.Dequeue();
                }
                var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
                foreach (var kv in reply.Headers ?? new Dictionary<string, string>()) response.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                return Task.FromResult(response);
            }
        }

        // ---- the requests the product really builds --------------------------------------------------

        /// <summary>One real request per registered point, built by the points' OWN builders rather than by
        /// hand, so this suite compares what the product sends and not what a test author remembered.</summary>
        private static IEnumerable<DecisionRequest> RealRequests()
        {
            yield return DecisionFixture.SampleRequest();

            var items = Enumerable.Range(1, PlanRankPoint.MaxScoredItems)
                .Select(i => new ModelPlanItem
                {
                    Id = "safefix/rule:RULE_" + i.ToString(CultureInfo.InvariantCulture),
                    Section = "Make it clear",
                    Title = "Hide the key column on table " + i.ToString(CultureInfo.InvariantCulture),
                    Sub = "1 column",
                    Who = "claude",
                    Touches = "1 column in Sales",
                    Why = new[] { "a key column is visible and nobody filters on it" },
                    Diff = new[] { "~ Sales[Key].IsHidden = true" },
                })
                .ToList();

            var planPoint = DecisionRegistry.Require(DecisionRegistry.PlanRank);
            yield return new DecisionRequest(planPoint.Name, planPoint.SchemaVersion,
                PlanRankPoint.Snapshot("DecisionProof", 4, 30, 12, connected: false, candidates: items),
                PlanRankPoint.Questions(items));

            var askPoint = DecisionRegistry.Require(DecisionRegistry.AskRoute);
            yield return new DecisionRequest(askPoint.Name, askPoint.SchemaVersion,
                AskRoutePoint.Snapshot("where do I fix the number formats"),
                AskRoutePoint.Questions());
        }

        private static readonly string DecidedReply =
            "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10},\"answers\":{" +
            "\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + MeasureDefaultsPoint.FolderOptionId("Volume") +
            "\",\"confidence\":0.93}," +
            "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}";

        // ---- the relay's pinned templates, read off the disk -----------------------------------------

        /// <summary>tools/relay/templates.mjs, parsed.
        ///
        /// It is PARSED rather than imported or mirrored for the same reason tools/relay/test/drift.test.mjs
        /// parses the C#: a mirror is a third copy somebody has to remember to update, and the thing being
        /// guarded is exactly the class of mistake where somebody did not. Every match the parser needs is
        /// asserted, so a refactor that moves the declarations out of reach fails loudly here rather than
        /// quietly passing over nothing.</summary>
        private sealed class RelayTemplates
        {
            public Dictionary<string, RelayTemplate> Points { get; } =
                new Dictionary<string, RelayTemplate>(StringComparer.Ordinal);

            public int MaxQuestions { get; private set; }
            public int MaxSnapshotBytes { get; private set; }
            public int MaxRequestBytes { get; private set; }

            public static RelayTemplates Read()
            {
                var path = Path.Combine(DecisionRegistryTests.RepoRoot(), "tools", "relay", "templates.mjs");
                Assert.True(File.Exists(path), "the relay templates were not found at " + path);
                var source = File.ReadAllText(path);

                var parsed = new RelayTemplates
                {
                    MaxQuestions = Number(Region(source, "LIMITS"), "maxQuestions"),
                    MaxSnapshotBytes = Number(Region(source, "LIMITS"), "maxSnapshotBytes"),
                    MaxRequestBytes = Number(Region(source, "LIMITS"), "maxRequestBytes"),
                };

                var templates = Region(source, "TEMPLATES");
                foreach (Match entry in Regex.Matches(templates, @"'([^']+)':\s*\{(.*?)\n    \}", RegexOptions.Singleline))
                {
                    var name = entry.Groups[1].Value;
                    var block = entry.Groups[2].Value;
                    var prefix = Regex.Match(block, @"questionPrefix:\s*'([^']*)'");
                    var max = Regex.Match(block, @"maxQuestions:\s*([\d_]+)");

                    parsed.Points[name] = new RelayTemplate
                    {
                        SchemaVersion = Number(block, "schemaVersion"),
                        Fields = Strings(block, "fields"),
                        Questions = Strings(block, "questions"),
                        QuestionPrefix = prefix.Success ? prefix.Groups[1].Value : null,
                        MaxQuestions = max.Success ? int.Parse(max.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture)
                                                   : parsed.MaxQuestions,
                    };
                }

                foreach (var point in parsed.Points)
                    Assert.True(point.Value.Fields.Count > 0, point.Key + " parsed to an empty field list");

                return parsed;
            }

            /// <summary>The body of one top-level <c>export const NAME = { ... };</c>.</summary>
            private static string Region(string source, string name)
            {
                var match = Regex.Match(source, @"export const " + name + @" = \{(.*?)\n\};", RegexOptions.Singleline);
                Assert.True(match.Success, "the relay templates no longer declare " + name + " where the parser can see it");
                return match.Groups[1].Value;
            }

            private static int Number(string block, string key)
            {
                var match = Regex.Match(block, key + @":\s*([\d_]+)");
                Assert.True(match.Success, "the relay templates no longer declare " + key);
                return int.Parse(match.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture);
            }

            private static List<string> Strings(string block, string key)
            {
                var match = Regex.Match(block, key + @":\s*\[([^\]]*)\]", RegexOptions.Singleline);
                Assert.True(match.Success, "the relay templates no longer declare " + key + " as a list");
                return Regex.Matches(match.Groups[1].Value, @"'([^']+)'").Select(m => m.Groups[1].Value).ToList();
            }
        }

        private sealed class RelayTemplate
        {
            public int SchemaVersion;
            public List<string> Fields = new List<string>();
            public List<string> Questions = new List<string>();
            public string QuestionPrefix;
            public int MaxQuestions;
        }

        // ---- scaffolding ------------------------------------------------------------------------------

        /// <summary>One environment variable, set for a scope and put back exactly as it was.</summary>
        private sealed class EnvVar : IDisposable
        {
            private readonly string _name;
            private readonly string _previous;

            public EnvVar(string name, string value)
            {
                _name = name;
                _previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
        }

        private sealed class SentRequest
        {
            public string Method;
            public string Url;
            public string AuthScheme;
            public string AuthParameter;
            public string IdempotencyHeader;
            public string Body;
        }

        /// <summary>Records what would have gone out and answers from a queue. It never touches a network, and
        /// with <see cref="FailOnVendorHost"/> set it THROWS on the vendor host, so "the vendor is never
        /// contacted" is proved by the attempt failing rather than by reading the code.</summary>
        private sealed class CapturingHandler : HttpMessageHandler
        {
            public readonly Queue<(HttpStatusCode Status, string Body)> Replies = new Queue<(HttpStatusCode, string)>();
            public readonly List<SentRequest> Sent = new List<SentRequest>();
            public bool FailOnVendorHost;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (FailOnVendorHost && string.Equals(request.RequestUri.Host,
                        string.Join(".", new[] { "api", "typesafe", "ai" }), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "A relay-route request reached the decision vendor. The licence route must never contact it.");

                var sent = new SentRequest
                {
                    Method = request.Method.Method,
                    Url = request.RequestUri.GetLeftPart(UriPartial.Path),
                    AuthScheme = request.Headers.Authorization?.Scheme,
                    AuthParameter = request.Headers.Authorization?.Parameter,
                };

                IEnumerable<string> idempotency;
                if (request.Headers.TryGetValues("idempotency-key", out idempotency))
                    sent.IdempotencyHeader = idempotency.FirstOrDefault();

                if (request.Content != null)
                    sent.Body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                lock (Sent) Sent.Add(sent);

                (HttpStatusCode Status, string Body) reply;
                lock (Replies)
                {
                    if (Replies.Count == 0) throw new InvalidOperationException("the test queued no reply for this send");
                    reply = Replies.Dequeue();
                }

                return new HttpResponseMessage(reply.Status)
                {
                    Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
                };
            }
        }

        /// <summary>A relay whose first send waits at a gate and whose every send is refused with the same body.</summary>
        private sealed class GatedRelay : HttpMessageHandler
        {
            private readonly string _refusal;
            private readonly int _laterStatus;
            private readonly string _laterBody;
            private int _sends;
            /// <summary>The first send waits at the gate and is refused 402 with <paramref name="refusal"/>; every later
            /// send gets <paramref name="laterStatus"/> and <paramref name="laterBody"/>, the same refusal by default.</summary>
            public GatedRelay(string refusal, int laterStatus = 402, string laterBody = null)
            {
                _refusal = refusal;
                _laterStatus = laterStatus;
                _laterBody = laterBody ?? refusal;
            }
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public readonly TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Sends => Volatile.Read(ref _sends);
            /// <summary>The first send ignores its token and waits for the gate, so it outlives its own deadline.</summary>
            public bool FirstIsDeaf;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _sends) == 1)
                {
                    Entered.Set();
                    if (FirstIsDeaf) await Gate.Task.ConfigureAwait(false);
                    else await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
                    return new HttpResponseMessage((HttpStatusCode)402) { Content = new StringContent(_refusal, Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage((HttpStatusCode)_laterStatus) { Content = new StringContent(_laterBody, Encoding.UTF8, "application/json") };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Entered.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>Fails loudly if anything tries to send at all.</summary>
        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("A test tried to reach the network. No decision test may do that.");
        }
    }
}
