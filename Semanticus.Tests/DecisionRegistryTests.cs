// The registry is the audit surface, so these tests hold it to being complete and hard to widen by accident.
//
// Two of them are containment guards rather than behaviour tests:
//   the vendor host appears in exactly one engine file
//   nothing can ask a question under a name the registry does not carry

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionRegistryTests
    {
        /// <summary>The declared field list, frozen. Adding, removing or renaming a field on ANY point changes
        /// this, and the change is meant to be uncomfortable: it means the person has to be asked for permission
        /// again, so DecisionRegistry.ConsentSchemaVersion is bumped in the same commit as this value.</summary>
        // Version 2 (Jev M2): plan.rank and ask.route joined the registry, so the list of details the product
        // could send grew and every permission granted against version 1 stops counting. That is the rule
        // working, not a nuisance: the person agreed to one list and this is a different one.
        // Version 3 (F-229, F-231): measure.folder_and_format:measure_expression became measure_formula_facts,
        // and the card now names typed questions, so every permission granted against version 2 stops counting.
        // references_measures joined version 3 before release, so it changed this value and not the version.
        // measure.starter (T268) joined version 3 before release too: no release carries a decision registry.
        // Version 4 (composer lane L5, 25 Sep): compose.draft joined and the card gained a line for the words it sends.
        // column.format (T268, columns first) joined the same version 4 set.
        // relationship.join (T268, M4 point 2) changed this value and not the version: the integrator bumps the
        // consent version once for every M4 point landing in this release.
        // ai.schema (T268, what Copilot sees) joined the same version 4 set too.
        // query.summarize (Jev question-to-DAX, 26 Sep) changed this value and NOT the version, on the brief's word: the
        // integrator bumps the version once for the release. The card names its words ("the words of your data
        // questions"), so a permission given on this card covers them. A permission given before the point joined does
        // not: get_consent finds its three fields missing, reads the permission as not current, and the card is shown
        // again (The_card_covers_the_words_of_a_data_question_and_an_older_permission_asks_again). On the first combined
        // build this comment said so while the card never named data questions (verify-askdata defect 7).
        // query.summarize:stated_reading (29 Sep, the ruling "Jev data questions v2") joined it the same way: the reading the
        // check judges, told in the question's own words. Same version 4 set; the integrator's one release bump covers it,
        // the card's "What goes" line for data questions is worded to include it, and the relay's template gained the field
        // in the same commit (tools/relay/templates.mjs; the relay is not deployed, tools/relay/README.md).
        // column.category (T268, the fair replay, 26 Sep) joined version 4 too, on Kane's instruction not to bump it;
        // a permission given before lists none of its fields, so the point stays off until the card is agreed again.
        // column.summarize (Kane's decision, 26 Sep) joined version 4 on the same terms.
        // date.table (T268, M4 point 3, 26 Sep) joined the same version 4 set; Covers still asks again for its two fields.
        // query.summarize (Jev question-to-DAX, 26 Sep, ask_data) changed this value and NOT the version, on the brief's word: the
        // integrator bumps the version once for the release; the card names its words, so an older permission asks again.
        // queries.chart_kind (D6, 28 Sep, OFF until its replay clears its bar) changed this value and NOT the version, the
        // same way: question_words and result_shape (split names, date or not, distinct counts, how many numbers and rows).
        // cleanup.suggest (m11/plan-harm round 5, 30 Sep) joined the same version 4 set: still no release carries the registry.
        private const string FrozenFieldFingerprint = "1e91f6812065acd5c24c9c4c0c7899cff1adf37f2de68613ffef5a1ba0d899f9";

        [Fact]
        public void The_declared_fields_are_frozen_against_the_consent_version()
        {
            Assert.True(FrozenFieldFingerprint == DecisionRegistry.DeclaredFieldFingerprint,
                "expected " + FrozenFieldFingerprint + " but the declared fields hash to " + DecisionRegistry.DeclaredFieldFingerprint);
            Assert.Equal(5, DecisionRegistry.ConsentSchemaVersion);   // the M11 join's one release bump (30 Sep)
        }

        /// <summary>F-229. The measure point used to send the formula text, and a formula can carry a value (a
        /// customer's name in a filter), which the consent card promises never goes. It now declares derived
        /// facts instead, so the text is not merely left out by the caller: the validator refuses it.</summary>
        /// <summary>verify-askdata defect 7, the other way round once Jev reads data questions (the 160 held-out replay
        /// cleared the bar on m11/jev-query-v2). The words of a data question are in force, so the card's OK grants them
        /// (its "What goes" names them: test/consent-card.test.mjs). A permission given before the point joined, without
        /// those fields, is not current, so the person is asked again rather than Jev reading under a permission that
        /// never named data questions.</summary>
        [Fact]
        public async Task The_card_covers_the_words_of_a_data_question_and_an_older_permission_asks_again()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                var consent = await f.Engine.GetConsentAsync();
                var summarize = DecisionRegistry.Require(DecisionRegistry.QuerySummarize).SnapshotFields
                    .Select(x => DecisionRegistry.QuerySummarize + ":" + x).ToArray();
                Assert.Contains(DecisionRegistry.QuerySummarize + ":question_words", summarize);
                foreach (var field in summarize) Assert.Contains(field, consent.FieldsInForce);

                var older = await f.Engine.SetConsentAsync(consent.FieldsInForce.Except(summarize).ToArray(), consent.SchemaVersionInForce, "human");
                Assert.False(older.Current, "a permission that never covered the words of data questions still counts");
                Assert.Contains("question_words", older.Reason, StringComparison.Ordinal);

                var card = await f.Engine.SetConsentAsync(consent.FieldsInForce, consent.SchemaVersionInForce, "human");
                Assert.True(card.Current);
                foreach (var field in summarize) Assert.Contains(field, card.Fields);
            }
        }

        [Fact]
        public void The_measure_point_refuses_the_formula_text_it_used_to_send()
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            Assert.DoesNotContain("measure_expression", point.SnapshotFields);
            Assert.Contains("measure_formula_facts", point.SnapshotFields);
            Assert.Equal(2, point.SchemaVersion);

            var e = Assert.Throws<InvalidOperationException>(() => DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_expression", "SUM ( Sales[Amount] )"),
            }));
            Assert.Contains("has not declared", e.Message, StringComparison.Ordinal);

            // The settings list shows this sentence as what leaves the machine, so it must not still say the
            // formula goes, and it must say plainly that it does not.
            Assert.DoesNotContain("name and expression", point.Purpose, StringComparison.Ordinal);
            Assert.Contains("never sends the formula", point.Purpose, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_point_declares_everything_the_settings_list_has_to_show()
        {
            Assert.NotEmpty(DecisionRegistry.Points);
            foreach (var point in DecisionRegistry.Points)
            {
                Assert.False(string.IsNullOrWhiteSpace(point.Name));
                Assert.False(string.IsNullOrWhiteSpace(point.Caller));
                Assert.False(string.IsNullOrWhiteSpace(point.Purpose));
                Assert.False(string.IsNullOrWhiteSpace(point.LowConfidenceBehaviour));
                Assert.NotEmpty(point.SnapshotFields);
                Assert.NotEmpty(point.Questions);
                Assert.InRange(point.ConfidenceBar, 0.5, 1.0);
                Assert.True(point.SchemaVersion >= 1);

                // No em dashes anywhere a person reads. docs/product-copy-style.md.
                Assert.DoesNotContain("—", point.Purpose, StringComparison.Ordinal);
                Assert.DoesNotContain("—", point.LowConfidenceBehaviour, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_proof_point_is_Pro_only_and_sits_at_the_agreed_bar()
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            Assert.True(point.ProOnly);
            Assert.Equal(0.85, point.ConfidenceBar, 3);
            Assert.Equal("propose_measure_defaults", point.Caller);
            Assert.Equal(new[] { "currency", "percent", "whole_number", "decimal", "date_or_time", "text_or_none" },
                DecisionRegistry.FormatFamilies.ToArray());
        }

        [Fact]
        public async Task An_unregistered_point_cannot_be_asked_by_anything()
        {
            // The service refuses before any gate, so a typo cannot become an upload under a name nobody declared.
            using (var service = new DecisionService(RecordedDecisionOracle.Empty()))
            {
                var e = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(
                    "measure.not_registered",
                    new Dictionary<string, object>(),
                    new[] { new DecisionQuestion("q", DecisionQuestionKind.Choice, "Which?",
                        new[] { new DecisionOption("a", "the world is a"), new DecisionOption("b", "the world is b") }) },
                    isPro: true, enabled: true, fence: null, ct: CancellationToken.None));
                Assert.Contains("is not a registered decision point", e.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task The_client_ALSO_refuses_an_unregistered_point_on_its_own()
        {
            // Belt and braces on purpose: the client is the thing holding the key, so it does not rely on a
            // caller having checked. This request never reaches a socket, which is why no handler is needed.
            using (var oracle = new JevDecisionOracle("not-a-real-key", new DecisionBudget(), new RefusingHandler()))
            {
                var request = new DecisionRequest("measure.not_registered", 1,
                    new Dictionary<string, object> { { "a", 1 } },
                    new[] { new DecisionQuestion("q", DecisionQuestionKind.Choice, "Which?",
                        new[] { new DecisionOption("a", "the world is a"), new DecisionOption("b", "the world is b") }) });

                var answers = await oracle.AskAsync(request, CancellationToken.None);
                Assert.False(answers.Decided);
                Assert.Contains("is not a registered decision point", answers.NotDecidedReason, StringComparison.Ordinal);
                Assert.Equal(0, oracle.Budget.Requests);
            }
        }

        [Fact]
        public void The_request_body_matches_the_shape_the_pilots_measured()
        {
            // This is the ONE definition of what leaves the machine, so it is pinned rather than described.
            // The shape is the one docs/redesign/jev-pilot/run.ts posted and the one the recorded replies in
            // pilot2-raw-a-run1.json answered: a model alias, a state object, and questions keyed by name,
            // each with a type, instructions, and criteria keyed by option id.
            var request = DecisionFixture.SampleRequest();
            var body = JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot));

            using (var doc = System.Text.Json.JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                Assert.Equal(new[] { "model", "state", "questions" }, root.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Equal("jev-latest", root.GetProperty("model").GetString());

                var state = root.GetProperty("state");
                Assert.Equal(System.Text.Json.JsonValueKind.Object, state.ValueKind);
                Assert.Equal("Total Sales", state.GetProperty("measure_name").GetString());

                var questions = root.GetProperty("questions");
                var format = questions.GetProperty("format_family");
                Assert.Equal("choice", format.GetProperty("type").GetString());
                Assert.False(string.IsNullOrWhiteSpace(format.GetProperty("instructions").GetString()));

                var criteria = format.GetProperty("criteria");
                Assert.Equal(DecisionRegistry.FormatFamilies.ToArray(),
                    criteria.EnumerateObject().Select(p => p.Name).ToArray());
                foreach (var c in criteria.EnumerateObject())
                    Assert.False(string.IsNullOrWhiteSpace(c.Value.GetString()));

                // Nothing else rides along: no key, no session id, no licence subject, no idempotency value.
                Assert.DoesNotContain("authorization", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("idempotency", body, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>plan.rank got HTTP 422 from the vendor in every live lane: a score question's criteria must be a
        /// LIST, in ladder order, lowest rung first (the vendor said "Input should be a valid list" at
        /// questions.value:...score.criteria, measured 26 Sep). A choice question's criteria stay keyed by option id.</summary>
        [Fact]
        public void A_score_question_sends_its_criteria_as_a_list_lowest_rung_first()
        {
            var item = new ModelPlanItem { Id = "proposal/relationships", Title = "Sort out 2 relationships", Section = "Structure" };
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);
            var request = new DecisionRequest(point.Name, point.SchemaVersion,
                PlanRankPoint.Snapshot("Contoso", 3, 9, 2, false, new[] { item }), PlanRankPoint.Questions(new[] { item }));

            foreach (var body in new[]
            {
                JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot)),
                JevDecisionOracle.BuildRelayBody(request, DecisionDigest.CanonicalSnapshotJson(request.Snapshot), "k"),
            })
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var q = doc.RootElement.GetProperty("questions").GetProperty(PlanRankPoint.QuestionFor(item.Id));
                Assert.Equal("score", q.GetProperty("type").GetString());
                var criteria = q.GetProperty("criteria");
                Assert.Equal(System.Text.Json.JsonValueKind.Array, criteria.ValueKind);
                Assert.Equal(request.Questions[0].Options.Select(o => o.Criterion).ToArray(),
                             criteria.EnumerateArray().Select(c => c.GetString()).ToArray());
            }
        }

        [Fact]
        public void The_vendor_host_appears_in_exactly_one_engine_file()
        {
            var root = RepoRoot();
            var host = string.Join(".", new[] { "api", "typesafe", "ai" });

            var offenders = new List<string>();
            foreach (var dir in new[] { "Semanticus.Engine", "Semanticus.Core", "Semanticus.Analysis" })
            {
                var full = Path.Combine(root, dir);
                if (!Directory.Exists(full)) continue;
                foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    var text = File.ReadAllText(file);
                    if (!text.Contains(host, StringComparison.Ordinal)) continue;
                    if (Path.GetFileName(file) == "JevDecisionOracle.cs") continue;
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }

            Assert.True(offenders.Count == 0,
                "Only JevDecisionOracle.cs may name the decision vendor's host. Also here: " + string.Join(", ", offenders));
        }

        [Fact]
        public void Only_the_client_builds_an_HttpClient_in_the_decision_layer()
        {
            var root = RepoRoot();
            var offenders = Directory
                .EnumerateFiles(Path.Combine(root, "Semanticus.Engine", "Decisions"), "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "JevDecisionOracle.cs")
                .Where(f => File.ReadAllText(f).Contains("new HttpClient", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();

            Assert.True(offenders.Count == 0,
                "The decision layer has ONE client, in JevDecisionOracle.cs. Also here: " + string.Join(", ", offenders));
        }

        /// <summary>Find the checkout this assembly was built from.
        ///
        /// Honest limit, and the reason the anchor below is two files rather than one: a source-scanning guard
        /// cannot run without sources, so it walks up from the test binary. It refuses to scan a directory that
        /// is not recognisably THIS product, and it FAILS rather than skips when it finds nothing, because a
        /// containment guard that quietly does not run is worse than no guard at all.</summary>
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))
                    && File.Exists(Path.Combine(dir.FullName, "Semanticus.Engine", "Decisions", "JevDecisionOracle.cs")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "This containment guard reads the engine's own sources and could not find the checkout above "
              + AppContext.BaseDirectory + ". Run the tests from the repository.");
        }

        /// <summary>A handler that fails loudly if anything actually tries to send. No test in this suite may
        /// reach a network, so an attempted send is a test bug, not a network flake.</summary>
        private sealed class RefusingHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("A test tried to reach the network. No decision test may do that.");
        }
    }
}
