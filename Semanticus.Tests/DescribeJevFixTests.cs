// The fixes for the verifier's FAIL on m11/describe-jev (26 Sep): the tests that are not the verifier's own. The
// verifier's are in VerifyDescribeJevDefectTests.cs. These cover what that file does not reach:
//
//   What an ask that did not decide costs (the coordinator's decision, 27 Sep, round 3). The budget is a cost cap and
//   must never under-count spend, so the direct route follows the relay contract (relay.md, worker.mjs): a vendor 4xx,
//   422 and 429 included, reached the vendor and counts as spent, because the vendor does not document a refused
//   request as unbilled. Only an ask that provably never reached anyone is free: the connection refused, no name, no
//   secure channel, cancelled before it was sent, and on the relay route the relay's own refusal, which it gives
//   before it forwards anything. One test per class.
//   The per-clause rule's merge, as one pure step: an extra leaning step is never added twice, and a sure step takes
//   the place of an extra leaning one of its kind (the other half of D7, which the verifier's test covers); a clause
//   only Jev leaned to keeps its own step beside one of its kind (R2-D7).
//   The audit surface names every kind of name compose.draft can send, read off the engine's own lists, so the text
//   and the code cannot drift apart again (D3a).
//   Workflows says so when the words name more than twelve steps (D6a on the other door).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DescribeJevFixTests
    {
        // ---- a refused ask uses none of the budget ---------------------------------------------------------------

        /// <summary>Hands back the queued replies, or throws the queued failure, and never touches a network.</summary>
        private sealed class Replies : HttpMessageHandler
        {
            public readonly Queue<Func<HttpResponseMessage>> Queue = new Queue<Func<HttpResponseMessage>>();
            public int Sends;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Sends);
                Func<HttpResponseMessage> next;
                lock (Queue)
                {
                    if (Queue.Count == 0) throw new InvalidOperationException("the test queued no reply for this send");
                    next = Queue.Dequeue();
                }
                return Task.FromResult(next());
            }

            public void Status(HttpStatusCode status, string body = "{\"error\":{\"type\":\"permission_error\"}}") =>
                Queue.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static int EstimateOf(DecisionRequest request) =>
            JevDecisionOracle.EstimateInputTokens(JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot)));

        /// <summary>A vendor refusal on the direct route reached the vendor, and the vendor does not document a refused
        /// request as unbilled, so it counts as spent at the estimate, as the relay counts it (relay.md: "a 4xx, settled
        /// at the estimate"; worker.mjs). This includes 422, which plan.rank answers in every lane run.</summary>
        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.PaymentRequired)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        public async Task A_vendor_refusal_counts_as_spent_because_it_reached_the_vendor(HttpStatusCode status)
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = new JevDecisionOracle("not-a-real-key", budget, handler);
            handler.Status(status);
            var request = DecisionFixture.SampleRequest();

            var answers = await oracle.AskAsync(request, CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.Equal(1, handler.Sends);
            Assert.True(budget.UsedInputTokens == EstimateOf(request),
                "a " + (int)status + " refusal from the vendor was counted as " + budget.UsedInputTokens + " input tokens, not its estimate " + EstimateOf(request));
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        /// <summary>A rate-limit refusal is retried on the direct route, and both attempts count as spent: the relay keeps
        /// a vendor 429 as possibly charged (worker.mjs), and so does the direct route.</summary>
        [Fact]
        public async Task A_rate_limited_ask_and_its_retry_both_count_as_spent()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = new JevDecisionOracle("not-a-real-key", budget, handler);
            handler.Status(HttpStatusCode.TooManyRequests, "{}");
            handler.Status(HttpStatusCode.TooManyRequests, "{}");
            var request = DecisionFixture.SampleRequest();

            var answers = await oracle.AskAsync(request, CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.Equal(2, handler.Sends);
            Assert.Equal(2, budget.Requests);
            Assert.Equal(2L * EstimateOf(request), budget.UsedInputTokens);
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        /// <summary>The relay's own refusal is free: the relay refuses before it forwards (its hold is released, relay.md),
        /// and a vendor refusal comes back from it as a 502, which is charged (DecisionRelayTests). Every status the relay
        /// itself refuses with (worker.mjs).</summary>
        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.PaymentRequired)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.Conflict)]
        [InlineData(HttpStatusCode.RequestEntityTooLarge)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        public async Task The_relays_own_refusal_uses_none_of_the_budget(HttpStatusCode status)
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = JevDecisionOracle.ForRelay("not-a-real-licence-token", "https://relay.test.invalid", budget, handler);
            handler.Status(status, "{\"error\":\"refused\",\"reason\":\"The relay said no.\"}");

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.Equal(1, handler.Sends);
            Assert.Equal(0, budget.UsedInputTokens);
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        /// <summary>An ask cancelled before it was sent is never sent and costs nothing.</summary>
        [Fact]
        public async Task An_ask_cancelled_before_it_was_sent_uses_none_of_the_budget()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = new JevDecisionOracle("not-a-real-key", budget, handler);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), cancelled.Token);

            Assert.False(answers.Decided);
            Assert.Equal(0, handler.Sends);
            Assert.Equal(0, budget.UsedInputTokens);
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        /// <summary>An ask that never reached the service (the connection was refused) cost nothing either.</summary>
        [Fact]
        public async Task An_ask_that_never_reached_the_service_uses_none_of_the_budget()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = new JevDecisionOracle("not-a-real-key", budget, handler);
            for (var i = 0; i < 2; i++)
                handler.Queue.Enqueue(() => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.Equal(0, budget.UsedInputTokens);
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        /// <summary>The other side of the line, kept: a server error may come after the work was done (a relay 502 has
        /// already forwarded), so it is still charged the estimate, as a timeout is.</summary>
        [Fact]
        public async Task A_server_error_is_still_charged_because_it_may_have_been_served()
        {
            var budget = new DecisionBudget(capInputTokens: 100000);
            using var handler = new Replies();
            using var oracle = new JevDecisionOracle("not-a-real-key", budget, handler);
            handler.Status(HttpStatusCode.BadGateway, "{}");
            handler.Status(HttpStatusCode.BadGateway, "{}");

            var answers = await oracle.AskAsync(DecisionFixture.SampleRequest(), CancellationToken.None);

            Assert.False(answers.Decided);
            Assert.True(budget.UsedInputTokens > 0);
            Assert.Equal(0, budget.ReservedInputTokens);
        }

        // ---- the per-clause rule's merge -------------------------------------------------------------------------

        private static DescribedStep Clause(string words) => DescribedSteps.From(words).Steps.Single();

        private static JevDraftBuilder.Result Answer(params (string Template, bool Sure)[] steps) => new JevDraftBuilder.Result
        {
            Steps = steps.Select(s => new JevDraftBuilder.Drafted { Template = DraftMenu.Find(s.Template) ?? DescribedSteps.AfterTheChange.Single(t => t.Id == s.Template), Sure = s.Sure }).ToList(),
        };

        [Fact]
        public void Two_clauses_that_each_ask_surely_for_one_kind_keep_a_step_each()
        {
            var merged = DescribedSteps.MergeClauses(
                new[] { Clause("format Total Sales as currency"), Clause("format Order Count as a whole number") },
                new[] { Answer(("format", true)), Answer(("format", true)) });
            Assert.Equal(new[] { "format", "format" }, merged.Steps.Select(s => s.Template.Id));
            Assert.All(merged.Steps, s => Assert.True(s.Sure));
        }

        [Fact]
        public void A_leaning_step_of_a_kind_already_there_is_not_added_twice()
        {
            var merged = DescribedSteps.MergeClauses(
                new[] { Clause("add a Margin measure"), Clause("describe it") },
                new[] { Answer(("add-measure", true), ("relate", false)), Answer(("describe", true), ("relate", false)) });
            Assert.Equal(new[] { "relate", "add-measure", "describe" }, merged.Steps.Select(s => s.Template.Id));
            Assert.Single(merged.Steps, s => !s.Sure);

            // A leaning step whose kind the draft already holds adds nothing either (Add more words).
            var more = DescribedSteps.MergeClauses(new[] { Clause("describe it") }, new[] { Answer(("describe", true), ("relate", false)) },
                have: new[] { "create_relationship" });
            Assert.Equal(new[] { "describe" }, more.Steps.Select(s => s.Template.Id));
        }

        /// <summary>R2-D7 as one pure step: a leaning step that is its clause's only step is that clause's own. It is kept
        /// beside another clause's step of its kind, in either order, and beside a kind the draft already holds, and no
        /// sure step takes its place.</summary>
        [Fact]
        public void A_clause_only_Jev_leaned_to_keeps_its_own_step_beside_one_of_its_kind()
        {
            var sureFirst = DescribedSteps.MergeClauses(
                new[] { Clause("hide Amount"), Clause("also Qty please") },
                new[] { Answer(("hide", true)), Answer(("hide", false)) });
            Assert.Equal(new[] { (Id: "hide", Sure: true), (Id: "hide", Sure: false) }, sureFirst.Steps.Select(s => (Id: s.Template.Id, Sure: s.Sure)));

            var leaningFirst = DescribedSteps.MergeClauses(
                new[] { Clause("also Qty please"), Clause("hide Amount") },
                new[] { Answer(("hide", false)), Answer(("hide", true)) });
            Assert.Equal(new[] { (Id: "hide", Sure: false), (Id: "hide", Sure: true) }, leaningFirst.Steps.Select(s => (Id: s.Template.Id, Sure: s.Sure)));

            var more = DescribedSteps.MergeClauses(new[] { Clause("also Qty please") }, new[] { Answer(("hide", false)) },
                have: new[] { "set_column_hidden" });
            Assert.Equal(new[] { "hide" }, more.Steps.Select(s => s.Template.Id));
        }

        [Fact]
        public void A_sure_step_takes_the_place_of_a_leaning_one_of_its_kind()
        {
            var merged = DescribedSteps.MergeClauses(
                new[] { Clause("add a Margin measure"), Clause("relate Sales to Returns") },
                new[] { Answer(("add-measure", true), ("relate", false)), Answer(("relate", true)) });
            Assert.Equal(new[] { "relate", "add-measure" }, merged.Steps.Select(s => s.Template.Id));
            Assert.All(merged.Steps, s => Assert.True(s.Sure));
        }

        [Fact]
        public void A_clause_Jev_is_not_sure_of_takes_its_word_match_even_when_another_clause_has_that_kind()
        {
            var merged = DescribedSteps.MergeClauses(
                new[] { Clause("refresh Sales"), Clause("refresh Returns") },
                new JevDraftBuilder.Result[] { Answer(("refresh", true)), Answer() });
            Assert.Equal(new[] { "refresh", "refresh" }, merged.Steps.Select(s => s.Template.Id));
            Assert.False(merged.Steps[0].FromWords);
            Assert.True(merged.Steps[1].FromWords);
        }

        // ---- the audit surface names every kind of name compose.draft can send (D3a) --------------------------------

        /// <summary>The kinds of model name the engine lists as a pick's options (LocalEngine.OptionsOf), and the words the
        /// audit text must use for each. An enumerator OptionsOf does not list sends nothing.</summary>
        private static readonly IReadOnlyDictionary<string, string[]> NamesSent = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["tables"] = new[] { "table" },
            ["columns"] = new[] { "column" },
            ["number_columns"] = new[] { "column" },
            ["measures"] = new[] { "measure" },
            ["objects"] = new[] { "table", "column", "measure" },
            ["partitions"] = new[] { "partition" },
            ["roles"] = new[] { "role" },
            ["relationships"] = new[] { "relationship" },
        };

        [Fact]
        public void The_audit_surface_names_every_kind_of_name_a_draft_can_send()
        {
            var purpose = DecisionRegistry.Require(DecisionRegistry.ComposeDraft).Purpose;
            var sources = JevDraftBuilder.Sources(JevDraftBuilder.Scope.Compose).Concat(JevDraftBuilder.Sources(JevDraftBuilder.Scope.Describe))
                .Distinct(StringComparer.Ordinal).ToArray();
            var missing = sources.Where(NamesSent.ContainsKey).SelectMany(s => NamesSent[s].Select(w => (Source: s, Word: w)))
                .Where(x => purpose.IndexOf(x.Word + " names", StringComparison.Ordinal) < 0)
                .ToArray();
            Assert.True(missing.Length == 0, "compose.draft can send names the audit text does not name: "
                + string.Join(", ", missing.Select(x => x.Word + " (" + x.Source + ")")) + ". Sources: " + string.Join(", ", sources));
            // And the limit it states is the engine's own.
            Assert.Contains(JevDraftBuilder.MaxPickOptions.ToString(System.Globalization.CultureInfo.InvariantCulture), purpose);
            Assert.DoesNotContain("no columns", purpose);
        }

        // ---- Workflows says when the words name more than twelve steps (D6a) ---------------------------------------

        [Fact]
        public void A_description_past_twelve_steps_says_what_was_left_out()
        {
            var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "refresh Table" + i));
            var d = DescribedSteps.From(words);
            // Sol on m11/integration 568435d3, finding 4: the two past the limit are kept as steps to fill in, and named.
            Assert.Equal(14, d.Steps.Length);
            Assert.Equal(2, d.LeftOut);
            Assert.Contains(DraftCopy.StepsNotRead(new[] { "refresh Table13", "refresh Table14" }, DescribedSteps.MaxSteps), d.Sentence);
        }
    }
}
