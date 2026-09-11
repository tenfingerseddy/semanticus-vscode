using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The six measured v6 failures replayed through the shipped v7 definition. Live DAX is the only fake:
    /// query text is built by the production compiler and routed to scripted ResultSets, while parsing, runner
    /// strictness, coverage locks, shaped-anchor mechanics, countersigns, revisions, and certificates stay real.</summary>
    public sealed class VerifiedMeasureV7CounterfactualTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private sealed class ScriptedAnchorHarness : IDisposable
        {
            private readonly string _workspace;
            private readonly SessionManager _sessions;
            public LocalEngine Engine { get; }
            public LiveConnection Live { get; }
            public List<string> Queries { get; } = new List<string>();

            public ScriptedAnchorHarness(Func<string, ResultSet> execute)
            {
                _workspace = Path.Combine(Path.GetTempPath(), "smx-v7-cf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_workspace);
                _sessions = new SessionManager();
                Engine = new LocalEngine(_sessions, new Pro(), _workspace);
                Live = LiveConnection.ForTest("xmla", "endpoint-v7-counterfactual", execute: query =>
                {
                    Queries.Add(query);
                    return execute(query);
                });
                Engine.SetLiveConnectionForTest(Live);
            }

            public WorkflowVerifyExecutor Executor(WorkflowVerifyExecutor? equivalence = null) => async (spec, step, run, all) =>
            {
                if (spec.Kind == "anchor_coverage")
                    return LocalEngine.WorkflowAnchorCoverage(spec, step, run, all);
                if (spec.Kind == "expected_values")
                {
                    var anchors = AnchorGate.Parse(all[spec.Anchors].Value, out var error);
                    if (anchors == null)
                        return new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "valid anchors", Detail = error };
                    var candidate = all.TryGetValue("candidate", out var answer) && answer.Answered ? answer.Value : "1";
                    var querySpec = new DaxQuerySpec
                    {
                        HomeTable = "Fact",
                        TargetMeasureName = "Candidate",
                        ModelMeasureNames = new[] { "Candidate" },
                    };
                    return await Engine.EvaluateAnchorsAsync(spec.Kind, anchors, candidate, querySpec, Live, "",
                        run.CoverageSurface?.CurrentGrid ?? Array.Empty<string>());
                }
                if (spec.Kind == "dax_equivalence" && equivalence != null)
                    return await equivalence(spec, step, run, all);
                return new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "scripted pass" };
            };

            public void Dispose()
            {
                Engine.Dispose();
                _sessions.Dispose();
                if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
            }
        }

        private static WorkflowDef V7Definition()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "workflows", "verified-measure.md");
            var def = WorkflowParser.Parse(File.ReadAllText(path));
            Assert.Null(def.Error);
            Assert.Equal(7, def.Version);
            Assert.Equal("hard", def.Strictness);
            return def;
        }

        private static AnswerValue Answer(string value) => new AnswerValue { Value = value };
        private static AnswerValue Decline(string reason) => new AnswerValue { Declined = true, DeclineReason = reason };

        private static ResultSet Scalar(object? value) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "v", Type = "Double" } },
            Rows = new[] { new object[] { value! } },
            RowCount = 1,
        };

        private static ResultSet Shaped(string column, object member, object? value) => new ResultSet
        {
            Columns = new[]
            {
                new ColumnDef { Name = column }, new ColumnDef { Name = "v" }, new ColumnDef { Name = "__present" },
            },
            Rows = new[] { new object[] { member, value!, 1 } },
            RowCount = 1,
        };

        private static ResultSet Comparison(string[] columns, params object[][] rows) => new ResultSet
        {
            Columns = columns.Select(x => new ColumnDef { Name = x })
                .Concat(new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } }).ToArray(),
            Rows = rows,
            RowCount = rows.Length,
        };

        private static async Task Step1(WorkflowRunState run)
        {
            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["requirement"] = Answer("The requirement pins every listed grain and names any silence."),
                ["modelFacts"] = Answer("Fact and calendar extents are recorded separately."),
                ["contextLedger"] = Answer("The lattice is classified before authoring."),
                ["clarification"] = Decline("No clarification was needed."),
            }, null);
        }

        private static async Task Step2(WorkflowRunState run, string anchors, string grid, string openGrains,
            WorkflowVerifyExecutor executor)
        {
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>
            {
                ["expectedValues"] = Answer(anchors),
                ["equivalenceGrid"] = Answer(grid),
                ["openGrains"] = openGrains == null ? Decline("Every grain is anchored.") : Answer(openGrains),
                ["externalAnchors"] = Decline("No stronger external anchor is available."),
                ["naiveForm"] = Answer("The naive form diverges at the named bare-axis grain."),
            }, executor);
        }

        private static async Task Step3(WorkflowRunState run, string candidate, WorkflowVerifyExecutor executor)
        {
            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>
            {
                ["candidate"] = Answer(candidate),
                ["target"] = Answer("measure:Fact/Candidate"),
            }, executor);
        }

        private static async Task Steps4And5(WorkflowRunState run)
        {
            const string witness = "SUMX ( 'Fact', 'Fact'[Value] )";
            await WorkflowRunner.SubmitStepAsync(run, "step-4", new Dictionary<string, AnswerValue>
            {
                ["witnessDax"] = Answer(witness),
                ["witnessTiming"] = Answer("Bare-axis witness query completed in 0.1 seconds."),
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-5", new Dictionary<string, AnswerValue>
            {
                ["battery"] = Answer("Candidate and witness were evaluated across the complete lattice."),
                ["witnessDax"] = Answer(witness),
                ["adjudications"] = Decline("No pinned disagreement required adjudication."),
            }, null);
        }

        private static WorkflowVerifyExecutor EquivalenceExecutor(Func<string, ResultSet> execute) => async (spec, step, run, all) =>
        {
            var grid = run.CoverageSurface.CurrentGrid;
            var witness = all[spec.Probe].Value;
            var candidate = all["candidate"].Value;
            var eq = await DaxBench.VerifyEquivalenceCoreAsync((query, _) => Task.FromResult(execute(query)),
                witness, candidate, grid, Array.Empty<string>(), 100000,
                new DaxQuerySpec { HomeTable = "Fact", ModelMeasureNames = Array.Empty<string>() });
            EquivalenceGate.PreserveLogicalCross(eq, DaxBench.NormalizeGroupBy(grid).Length);
            var open = run.CoverageSurface.CurrentOpenGrains;
            var ids = eq.Shapes.Select(x => x.ShapeId).Distinct(StringComparer.Ordinal).ToArray();
            var pinned = ids.Where(x => !open.Contains(x, StringComparer.Ordinal)).ToArray();
            var outcome = EquivalenceGate.Evaluate(eq, pinned, open, DaxBench.NormalizeGroupBy(grid).Length);
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);
            if (spec.OpenMismatch == "countersign")
            {
                all.TryGetValue("countersign", out var countersign);
                outcome = EquivalenceGate.ApplyOpenMismatchCountersign(run, step.Id, outcome, countersign);
            }
            return new VerifyResult
            {
                Kind = spec.Kind,
                Status = outcome.Status,
                Missing = outcome.Missing,
                Detail = outcome.Detail,
            };
        };

        [Fact]
        public async Task X12_ALLEXCEPT_is_caught_when_anchored_and_countersigned_when_predeclared_open()
        {
            const string grid = "'Date'[Year],'Product'[Subcategory]";
            const string pinnedAnchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Date'[Year]\":2025},\"expect\":1},"
                + "{\"context\":{\"'Product'[Subcategory]\":\"Laptops\"},\"expect\":0.218},"
                + "{\"context\":{\"'Date'[Year]\":2025,\"'Product'[Subcategory]\":\"Laptops\"},\"expect\":1}]";

            using (var anchored = new ScriptedAnchorHarness(query =>
            {
                if (query.Contains("FILTER (", StringComparison.Ordinal))
                    return Shaped("Product[Subcategory]", "Laptops", 0.218);
                if (query.Contains("Laptops", StringComparison.Ordinal) && !query.Contains("2025", StringComparison.Ordinal))
                    return Scalar(0.25);
                return Scalar(1.0);
            }))
            {
                var run = new WorkflowRunStore().Start(V7Definition(), null);
                await Step1(run);
                await Step2(run, pinnedAnchors, grid, null, anchored.Executor());
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Step3(run, "CALCULATE ( SUM ( 'Fact'[Value] ), ALLEXCEPT ( 'Product', 'Product'[Category] ) )", anchored.Executor()));

                Assert.Contains("expected 0.218, actual 0.25", blocked.Message);
                Assert.Contains("anchor form is load-bearing", blocked.Message);
                Assert.Equal("failed", run.Results[2].VerifyResults.Single().Status);
            }

            const string openAnchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Date'[Year]\":2025},\"expect\":1},"
                + "{\"context\":{\"'Date'[Year]\":2025,\"'Product'[Subcategory]\":\"Laptops\"},\"expect\":1}]";
            using (var open = new ScriptedAnchorHarness(_ => Scalar(1.0)))
            {
                var run = new WorkflowRunStore().Start(V7Definition(), null);
                await Step1(run);
                await Step2(run, openAnchors, grid, "axis:'Product'[Subcategory]", open.Executor());
                await Step3(run, "SUM ( 'Fact'[Value] )", open.Executor());
                await Steps4And5(run);

                ResultSet Equivalence(string query)
                {
                    var year = query.Contains("'Date'[Year]", StringComparison.Ordinal);
                    var subcategory = query.Contains("'Product'[Subcategory]", StringComparison.Ordinal);
                    if (year && subcategory)
                        return Comparison(new[] { "Date[Year]", "Product[Subcategory]" }, new object[] { 2025, "Laptops", 1.0, 1.0 });
                    if (year) return Comparison(new[] { "Date[Year]" }, new object[] { 2025, 1.0, 1.0 });
                    if (subcategory) return Comparison(new[] { "Product[Subcategory]" }, new object[] { "Laptops", 0.218, 0.25 });
                    return Comparison(Array.Empty<string>(), new object[] { 1.0, 1.0 });
                }

                var eqExecutor = open.Executor(EquivalenceExecutor(Equivalence));
                var first = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                    run, "step-6", new Dictionary<string, AnswerValue>
                    {
                        ["certificate"] = Answer("FULL"),
                    }, eqExecutor));
                Assert.Contains("OPEN-shape disagreement", first.Message);
                Assert.Contains("candidate=0.25", first.Message);
                Assert.Contains("witness=0.218", first.Message);
                var coordinate = Assert.Single(run.ShapeMismatchLedger["axis:'Product'[Subcategory]"].Cells).Coordinate;

                var countersign = JsonSerializer.Serialize(new
                {
                    cells = new[] { coordinate },
                    stated = "The requirement deliberately leaves the bare subcategory grain open.",
                });
                await WorkflowRunner.SubmitStepAsync(run, "step-6", new Dictionary<string, AnswerValue>
                {
                    ["certificate"] = Answer("FULL"),
                    ["countersign"] = Answer(countersign),
                }, eqExecutor);
                await WorkflowRunner.SubmitStepAsync(run, "step-7", new Dictionary<string, AnswerValue>
                {
                    ["perfPass"] = Decline("The candidate matched the model floor."),
                    ["finalized"] = Answer("Production metadata applied."),
                }, open.Executor());

                Assert.Equal("completed", run.Status);
                Assert.Equal("PARTIAL", run.Certificate.Level);
                var receipt = Assert.Single(run.Certificate.CountersignedCells);
                Assert.Equal("0.25", receipt.CandidateValue);
                Assert.Equal("0.218", receipt.WitnessValue);
                Assert.Equal(coordinate, receipt.Coordinate);
            }
        }

        [Fact]
        public async Task X08_ALLSELECTED_rank_uses_shaped_semantics_and_flat_form_gets_the_load_bearing_hint()
        {
            const string grid = "'Product'[Name]";
            const string shaped = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Product'[Name]\":\"B\"},\"axis\":[\"'Product'[Name]\"],\"expect\":2}]";
            using (var visual = new ScriptedAnchorHarness(query =>
                query.Contains("FILTER (", StringComparison.Ordinal)
                    ? Shaped("Product[Name]", "B", 2.0)
                    : Scalar(1.0)))
            {
                var run = new WorkflowRunStore().Start(V7Definition(), null);
                await Step1(run);
                await Step2(run, shaped, grid, null, visual.Executor());
                await Step3(run, "RANKX ( ALLSELECTED ( 'Product'[Name] ), SUM ( 'Fact'[Value] ) )", visual.Executor());

                Assert.Equal("passed", run.Results[2].Status);
                var shapedQuery = Assert.Single(visual.Queries, q => q.Contains("FILTER (", StringComparison.Ordinal));
                Assert.Contains("SUMMARIZECOLUMNS", shapedQuery);
                Assert.Contains("'Product'[Name] = \"B\"", shapedQuery);
            }

            const string flat = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Product'[Name]\":\"B\"},\"expect\":2}]";
            using (var collapsed = new ScriptedAnchorHarness(query =>
                query.Contains("FILTER (", StringComparison.Ordinal)
                    ? Shaped("Product[Name]", "B", 2.0)
                    : Scalar(1.0)))
            {
                var run = new WorkflowRunStore().Start(V7Definition(), null);
                await Step1(run);
                await Step2(run, flat, grid, null, collapsed.Executor());
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Step3(run, "RANKX ( ALLSELECTED ( 'Product'[Name] ), SUM ( 'Fact'[Value] ) )", collapsed.Executor()));

                Assert.Contains("Flat and visual semantics disagree at this coordinate", blocked.Message);
                Assert.Contains("Copy-paste shaped-anchor skeleton", blocked.Message);
                Assert.Contains("\"axis\":[\"'Product'[Name]\"]", blocked.Message);
            }
        }

        [Fact]
        public async Task X25_shaped_anchor_cannot_be_revised_down_but_expectation_receipt_succeeds()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-x25-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            var sessions = new SessionManager();
            try
            {
                var engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("X25", 1604);
                await engine.CreateTableAsync("Product", "human");
                await engine.CreateColumnAsync("table:Product", "Name", "String", "Name", "human");
                await engine.CreateColumnAsync("table:Product", "Value", "Double", "Value", "human");
                var target = await engine.CreateMeasureAsync("table:Product", "Candidate", "SUM ( 'Product'[Value] )", "human");
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-x25", "X25", null);
                var shapedCalls = 0;
                var receiptCalls = 0;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-x25", "X25", query =>
                {
                    if (query == "EVALUATE 'Product'")
                    {
                        receiptCalls++;
                        return new ResultSet
                        {
                            Columns = new[] { new ColumnDef { Name = "Product[Value]" } },
                            Rows = new[] { new object[] { 2.0 } },
                            RowCount = 1,
                        };
                    }
                    if (query.Contains("\"__A\"", StringComparison.Ordinal))
                    {
                        if (query.Contains("'Product'[Name]", StringComparison.Ordinal))
                            return Comparison(new[] { "Product[Name]" }, new object[] { "A", 1.0, 1.0 });
                        return Comparison(Array.Empty<string>(), new object[] { 1.0, 1.0 });
                    }
                    if (query.Contains("FILTER (", StringComparison.Ordinal))
                    {
                        shapedCalls++;
                        return Shaped("Product[Name]", "A", shapedCalls == 1 ? 1.0 : 2.0);
                    }
                    return Scalar(1.0);
                }));

                var run = await engine.StartWorkflowAsync("verified-measure", "human");
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["requirement"] = "Pinned rank requirement",
                    ["modelFacts"] = "Recorded facts",
                    ["contextLedger"] = "Every grain pinned",
                    ["clarification"] = new { declined = true, reason = "none" },
                }), "human");
                const string initial = "[{\"context\":{},\"expect\":1},"
                    + "{\"context\":{\"'Product'[Name]\":\"A\"},\"axis\":[\"'Product'[Name]\"],\"expect\":1}]";
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-2", JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["expectedValues"] = initial,
                    ["equivalenceGrid"] = "'Product'[Name]",
                    ["openGrains"] = new { declined = true, reason = "all anchored" },
                    ["externalAnchors"] = new { declined = true, reason = "none" },
                    ["naiveForm"] = "Flat rank collapses the visible set.",
                }), "human");
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-3", JsonSerializer.Serialize(new
                {
                    candidate = "SUM ( 'Product'[Value] )",
                    target,
                }), "human");
                const string witness = "SUMX ( 'Product', 'Product'[Value] )";
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-4", JsonSerializer.Serialize(new
                {
                    witnessDax = witness,
                    witnessTiming = "0.1 seconds",
                }), "human");
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-5", JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["battery"] = "All pinned cells agree.",
                    ["witnessDax"] = witness,
                    ["adjudications"] = new { declined = true, reason = "none" },
                }), "human");
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-6", JsonSerializer.Serialize(new
                {
                    witnessDax = witness,
                    certificate = "FULL",
                }), "human");

                const string flatRevision = "[{\"context\":{},\"expect\":1},"
                    + "{\"context\":{\"'Product'[Name]\":\"A\"},\"expect\":1,\"originalExpect\":1,"
                    + "\"correctedExpect\":1,\"extractQuery\":\"EVALUATE 'Product'\"}]";
                var weakened = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    run.RunId, "step-7", JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["expectedValues"] = flatRevision,
                        ["perfPass"] = new { declined = true, reason = "fine" },
                        ["finalized"] = "metadata applied",
                    }), "human"));
                Assert.Contains("anchor form may only move toward visual semantics; start a new run to weaken it", weakened.Message);
                Assert.Equal(0, receiptCalls);

                const string shapedRevision = "[{\"context\":{},\"expect\":1},"
                    + "{\"context\":{\"'Product'[Name]\":\"A\"},\"axis\":[\"'Product'[Name]\"],\"expect\":2,"
                    + "\"originalExpect\":1,\"correctedExpect\":2,\"extractQuery\":\"EVALUATE 'Product'\"}]";
                var completed = await engine.SubmitWorkflowStepAsync(run.RunId, "step-7", JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["expectedValues"] = shapedRevision,
                    ["perfPass"] = new { declined = true, reason = "fine" },
                    ["finalized"] = "metadata applied",
                }), "human");

                Assert.Equal("completed", completed.Status);
                Assert.Equal(1, receiptCalls);
                Assert.Single(completed.AnchorRevisions);
                Assert.Equal("2", Assert.Single(completed.AnchorRevisions[0].Changes).CorrectedExpect);
            }
            finally
            {
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task X05_missing_bare_Year_grain_is_refused_with_its_anchor_skeleton()
        {
            const string anchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Date'[Quarter]\":\"Q2\"},\"expect\":1},"
                + "{\"context\":{\"'Date'[Year]\":2025,\"'Date'[Quarter]\":\"Q2\"},\"expect\":1}]";
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => Step2(run, anchors,
                "'Date'[Year],'Date'[Quarter]", null,
                (spec, step, state, all) => Task.FromResult(LocalEngine.WorkflowAnchorCoverage(spec, step, state, all))));

            Assert.Contains("axis:'Date'[Year]", blocked.Message);
            var coverage = run.Results[1].VerifyResults.Single();
            Assert.Equal("failed", coverage.Status);
            Assert.Contains("copy-paste flat anchor skeletons", coverage.Detail);
            Assert.Contains("{\"context\":{\"'Date'[Year]\":", coverage.Detail);
            Assert.Contains("\"expect\":\"<placeholder>\"", coverage.Detail);
        }

        [Fact]
        public async Task X07_date_axis_without_a_bare_period_anchor_or_open_declaration_is_refused_with_a_skeleton()
        {
            const string anchors = "[{\"context\":{},\"expect\":1}]";
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => Step2(run, anchors,
                "'Date'[Month]", null,
                (spec, step, state, all) => Task.FromResult(LocalEngine.WorkflowAnchorCoverage(spec, step, state, all))));

            Assert.Contains("axis:'Date'[Month]", blocked.Message);
            var coverage = run.Results[1].VerifyResults.Single();
            Assert.Equal("failed", coverage.Status);
            Assert.Contains("copy-paste flat anchor skeletons", coverage.Detail);
            Assert.Contains("{\"context\":{\"'Date'[Month]\":\"<placeholder>\"},\"expect\":\"<placeholder>\"}", coverage.Detail);
        }

        [Fact]
        public async Task Boundary_crossing_bare_month_anchor_rejects_the_reversed_blank_guard()
        {
            const string anchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Date'[Month]\":\"2025-01\"},\"expect\":7}]";
            using var harness = new ScriptedAnchorHarness(query =>
            {
                if (query.Contains("2025-01", StringComparison.Ordinal))
                {
                    if (query.Contains("FILTER (", StringComparison.Ordinal))
                        return Shaped("Date[Month]", "2025-01", null);
                    return Scalar(null);
                }
                return Scalar(1.0);
            });
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);
            await Step2(run, anchors, "'Date'[Month]", null, harness.Executor());

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => Step3(run,
                "IF ( MAX ( 'Date'[Date] ) >= MAX ( 'Fact'[Date] ), BLANK (), SUM ( 'Fact'[Value] ) )",
                harness.Executor()));

            Assert.Contains("expected_values:failed", blocked.Message);
            var mismatch = run.Results[2].VerifyResults.Single();
            Assert.Equal("failed", mismatch.Status);
            Assert.Contains("expected 7", mismatch.Detail);
            Assert.Contains("got (blank)", mismatch.Detail);
        }

        [Fact]
        public async Task P04_bare_currency_anchor_catches_a_closing_value_anchored_to_the_fact_date()
        {
            // The 120 expectation represents the closing balance derived from raw rows at the marked Date edge.
            const string anchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Currency'[Code]\":\"AUD\"},\"expect\":120}]";
            using var harness = new ScriptedAnchorHarness(query =>
            {
                if (!query.Contains("AUD", StringComparison.Ordinal)) return Scalar(1.0);
                return query.Contains("FILTER (", StringComparison.Ordinal)
                    ? Shaped("Currency[Code]", "AUD", 100.0)
                    : Scalar(100.0);
            });
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);
            await Step2(run, anchors, "'Currency'[Code]", null, harness.Executor());

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => Step3(run,
                "CALCULATE ( SUM ( 'Fact'[Balance] ), LASTDATE ( 'Fact'[TransactionDate] ) )",
                harness.Executor()));

            Assert.Contains("expected_values:failed", blocked.Message);
            var mismatch = run.Results[2].VerifyResults.Single();
            Assert.Equal("failed", mismatch.Status);
            Assert.Contains("'Currency'[Code]", mismatch.Detail);
            Assert.Contains("AUD", mismatch.Detail);
            Assert.Contains("expected 120", mismatch.Detail);
            Assert.Contains("got 100", mismatch.Detail);
        }

        [Fact]
        public async Task X28_bare_month_anchor_catches_a_cumulative_distinct_guard_that_blanks_months()
        {
            const string anchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Date'[Month]\":\"2025-03\"},\"expect\":37}]";
            using var harness = new ScriptedAnchorHarness(query =>
            {
                if (!query.Contains("2025-03", StringComparison.Ordinal)) return Scalar(1.0);
                return query.Contains("\"__present\"", StringComparison.Ordinal)
                    ? Shaped("Date[Month]", "2025-03", null)
                    : Scalar(null);
            });
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);
            await Step2(run, anchors, "'Date'[Month]", null, harness.Executor());

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => Step3(run,
                "IF ( NOT ISINSCOPE ( 'Fact'[EventDate] ), BLANK (), CALCULATE ( DISTINCTCOUNT ( 'Fact'[EntityId] ), FILTER ( ALL ( 'Date'[Date] ), 'Date'[Date] <= MAX ( 'Date'[Date] ) ) ) )",
                harness.Executor()));

            Assert.Contains("expected_values:failed", blocked.Message);
            var mismatch = run.Results[2].VerifyResults.Single();
            Assert.Equal("failed", mismatch.Status);
            Assert.Contains("'Date'[Month]", mismatch.Detail);
            Assert.Contains("2025-03", mismatch.Detail);
            Assert.Contains("expected 37", mismatch.Detail);
            Assert.Contains("got (blank)", mismatch.Detail);
        }

        [Fact]
        public async Task Step7_candidate_or_target_change_forces_equivalence_while_identical_receipt_is_not_applicable()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-m1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("M1", 1604);
                await engine.CreateTableAsync("Fact", "human");
                await engine.CreateTableAsync("Other", "human");
                await engine.CreateColumnAsync("table:Fact", "Category", "String", "Category", "human");
                await engine.CreateColumnAsync("table:Fact", "Value", "Double", "Value", "human");
                var target = await engine.CreateMeasureAsync("table:Fact", "Candidate", "SUM ( 'Fact'[Value] )", "human");
                var sameTextTarget = await engine.CreateMeasureAsync("table:Other", "CandidateB", "SUM ( 'Fact'[Value] )", "human");
                var targetLineage = await sessions.Current.ReadAsync(m =>
                    ((TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(m, target)).LineageTag);
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-m1", "M1", null);
                var mismatch = false;
                var queryCount = 0;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-m1", "M1", query =>
                {
                    queryCount++;
                    var candidate = mismatch ? 2.0 : 1.0;
                    return query.Contains("'Fact'[Category]", StringComparison.Ordinal)
                        ? Comparison(new[] { "Fact[Category]" }, new object[] { "A", 1.0, candidate })
                        : Comparison(Array.Empty<string>(), new object[] { 1.0, candidate });
                }));

                var def = new WorkflowDef
                {
                    Name = "m1-step7-candidate-swap",
                    Strictness = "hard",
                    Steps = new[]
                    {
                        new WorkflowStep
                        {
                            Id = "step-6", Number = 6, Title = "Prove",
                            Gate = new GateSpec
                            {
                                Inputs = new[]
                                {
                                    new GateInput { Name = "target", Type = "objectRef", Required = "required" },
                                    new GateInput { Name = "witnessDax", Type = "text", Required = "required" },
                                },
                                Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = "witnessDax" } },
                            },
                        },
                        new WorkflowStep
                        {
                            Id = "step-7", Number = 7, Title = "Finalize",
                            Gate = new GateSpec
                            {
                                Inputs = new[]
                                {
                                    new GateInput { Name = "perfPass", Type = "text", Required = "answer-or-decline" },
                                    new GateInput { Name = "finalTarget", Type = "objectRef", Required = "optional" },
                                },
                                Verify = new[]
                                {
                                    new VerifySpec
                                    {
                                        Kind = "dax_equivalence", Probe = "witnessDax",
                                        When = "inputs.perfPass.answered",
                                    },
                                },
                            },
                        },
                    },
                };

                WorkflowRunState NewRun()
                {
                    var state = new WorkflowRunStore().Start(def, null);
                    state.CoverageSurface = new CoverageSurfaceLock
                    {
                        CurrentGrid = new[] { "'Fact'[Category]" },
                        CurrentOpenGrains = Array.Empty<string>(),
                    };
                    return state;
                }

                const string witness = "SUMX ( 'Fact', 'Fact'[Value] )";
                async Task Prove(WorkflowRunState run) => await WorkflowRunner.SubmitStepAsync(run, "step-6",
                    new Dictionary<string, AnswerValue>
                    {
                        ["target"] = Answer(target),
                        ["witnessDax"] = Answer(witness),
                    }, engine.ExecuteWorkflowVerifyAsync);

                var identical = NewRun();
                await Prove(identical);
                var beforeIdenticalFinalize = queryCount;
                await WorkflowRunner.SubmitStepAsync(identical, "step-7", new Dictionary<string, AnswerValue>
                {
                    ["perfPass"] = Decline("No performance rewrite was needed."),
                }, engine.ExecuteWorkflowVerifyAsync);
                Assert.Equal("not_applicable", Assert.Single(identical.Results[1].VerifyResults).Status);
                Assert.Equal(beforeIdenticalFinalize, queryCount);

                var retargeted = NewRun();
                mismatch = false;
                await Prove(retargeted);
                Assert.Equal(targetLineage, retargeted.LastPassedEquivalenceCandidate.TargetLineageTag);
                var beforeRetargetedFinalize = queryCount;
                mismatch = true;
                var retargetedBlock = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                    retargeted, "step-7", new Dictionary<string, AnswerValue>
                    {
                        ["perfPass"] = Decline("No performance rewrite was needed."),
                        ["finalTarget"] = Answer(sameTextTarget),
                    }, engine.ExecuteWorkflowVerifyAsync));
                Assert.True(queryCount > beforeRetargetedFinalize);
                Assert.Contains("candidate and witness DIVERGE", retargetedBlock.Message);
                Assert.Equal("failed", Assert.Single(retargeted.Results[1].VerifyResults).Status);

                var changed = NewRun();
                mismatch = false;
                await Prove(changed);
                var beforeChangedFinalize = queryCount;
                await engine.SetDaxAsync(target, "SUM ( 'Fact'[Value] ) + 1", "human");
                mismatch = true;

                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                    changed, "step-7", new Dictionary<string, AnswerValue>
                    {
                        ["perfPass"] = Decline("No performance rewrite was needed."),
                    }, engine.ExecuteWorkflowVerifyAsync));

                Assert.True(queryCount > beforeChangedFinalize);
                Assert.Contains("candidate and witness DIVERGE", blocked.Message);
                Assert.Equal("failed", Assert.Single(changed.Results[1].VerifyResults).Status);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Step7_revalidates_locked_witness_purity_against_model_drift_before_full_or_receipt_paths()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-r6-purity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("R6Purity", 1604);
                var fact = await engine.CreateTableAsync("Fact", "human");
                var other = await engine.CreateTableAsync("Other", "human");
                await engine.CreateColumnAsync(fact, "Category", "String", "Category", "human");
                await engine.CreateColumnAsync(fact, "Qty", "Double", "Qty", "human");
                var target = await engine.CreateMeasureAsync(fact, "Candidate", "SUM ( 'Fact'[Qty] )", "human");
                var shadow = await engine.CreateMeasureAsync(other, "Shadow", "1", "human");
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-r6", "R6Purity", null);
                var equivalenceQueries = 0;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-r6", "R6Purity", query =>
                {
                    equivalenceQueries++;
                    return query.Contains("'Fact'[Category]", StringComparison.Ordinal)
                        ? Comparison(new[] { "Fact[Category]" }, new object[] { "A", 1.0, 1.0 })
                        : Comparison(Array.Empty<string>(), new object[] { 1.0, 1.0 });
                }));

                var def = new WorkflowDef
                {
                    Name = "r6-current-witness-purity",
                    Strictness = "hard",
                    Steps = new[]
                    {
                        new WorkflowStep
                        {
                            Id = "step-6", Number = 6, Title = "Prove",
                            Gate = new GateSpec
                            {
                                Inputs = new[]
                                {
                                    new GateInput { Name = "target", Type = "objectRef", Required = "required" },
                                    new GateInput
                                    {
                                        Name = "witnessDax", Type = "text", Required = "required",
                                        DaxPurity = "no-bare-measures",
                                    },
                                },
                                Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = "witnessDax" } },
                            },
                        },
                        new WorkflowStep
                        {
                            Id = "step-7", Number = 7, Title = "Finalize",
                            Gate = new GateSpec
                            {
                                Inputs = new[]
                                {
                                    new GateInput { Name = "perfPass", Type = "text", Required = "answer-or-decline" },
                                },
                                Verify = new[]
                                {
                                    new VerifySpec
                                    {
                                        Kind = "dax_equivalence", Probe = "witnessDax",
                                        When = "inputs.perfPass.answered",
                                    },
                                },
                            },
                        },
                    },
                };

                WorkflowRunState NewRun()
                {
                    var state = new WorkflowRunStore().Start(def, null);
                    state.CoverageSurface = new CoverageSurfaceLock
                    {
                        CurrentGrid = new[] { "'Fact'[Category]" },
                        CurrentOpenGrains = Array.Empty<string>(),
                    };
                    return state;
                }

                async Task<(string[] Measures, (string Table, string Name, bool IsCalculated)[] Columns,
                    string[] Functions, (string Name, bool IsCalculated)[] Tables)> Inventory() =>
                    await sessions.Current.ReadAsync(m => (
                        m.Tables.SelectMany(t => t.Measures).Select(measure => measure.Name).ToArray(),
                        m.Tables.SelectMany(t => t.Columns.Select(column =>
                            (Table: t.Name, Name: column.Name, IsCalculated: false))).ToArray(),
                        m.Functions.Select(function => function.Name).ToArray(),
                        m.Tables.Select(table => (table.Name, table is TabularEditor.TOMWrapper.CalculatedTable)).ToArray()));

                const string witness = "SUMX ( 'Fact', [Qty] )";
                async Task Prove(WorkflowRunState run, string witnessDax = witness)
                {
                    var inventory = await Inventory();
                    await WorkflowRunner.SubmitStepAsync(run, "step-6", new Dictionary<string, AnswerValue>
                    {
                        ["target"] = Answer(target),
                        ["witnessDax"] = Answer(witnessDax),
                    }, engine.ExecuteWorkflowVerifyAsync, inventory.Measures, inventory.Columns, inventory.Functions,
                        inventory.Tables);
                }

                async Task<InvalidOperationException> DriftAndFinalize(WorkflowRunState run, AnswerValue perfPass)
                {
                    var renamed = await engine.RenameObjectAsync(shadow, "Qty", "human");
                    shadow = renamed.NewRef;
                    var inventory = await Inventory();
                    var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                        run, "step-7", new Dictionary<string, AnswerValue> { ["perfPass"] = perfPass },
                        engine.ExecuteWorkflowVerifyAsync, inventory.Measures, inventory.Columns, inventory.Functions,
                        inventory.Tables));
                    var restored = await engine.RenameObjectAsync(shadow, "Shadow", "human");
                    shadow = restored.NewRef;
                    return blocked;
                }

                var unchanged = NewRun();
                await Prove(unchanged);
                var beforeUnchanged = equivalenceQueries;
                var unchangedInventory = await Inventory();
                await WorkflowRunner.SubmitStepAsync(unchanged, "step-7", new Dictionary<string, AnswerValue>
                {
                    ["perfPass"] = Decline("The proved form already meets the model floor."),
                }, engine.ExecuteWorkflowVerifyAsync, unchangedInventory.Measures, unchangedInventory.Columns,
                    unchangedInventory.Functions, unchangedInventory.Tables);
                Assert.Equal("not_applicable", Assert.Single(unchanged.Results[1].VerifyResults).Status);
                Assert.Equal(beforeUnchanged, equivalenceQueries);

                var receiptPath = NewRun();
                await Prove(receiptPath);
                var beforeReceiptPath = equivalenceQueries;
                var receiptBlock = await DriftAndFinalize(receiptPath,
                    Decline("The proved form already meets the model floor."));
                Assert.Equal(beforeReceiptPath, equivalenceQueries);
                Assert.Contains("the bare reference [Qty] now resolves to a measure", receiptBlock.Message);
                Assert.Contains("renamed or added since the witness was locked", receiptBlock.Message);
                Assert.Equal("unavailable", Assert.Single(receiptPath.Results[1].VerifyResults).Status);

                var fullPath = NewRun();
                await Prove(fullPath);
                var beforeFullPath = equivalenceQueries;
                var fullBlock = await DriftAndFinalize(fullPath, Answer("A performance rewrite must be proved."));
                Assert.Equal(beforeFullPath, equivalenceQueries);
                Assert.Contains("the bare reference [Qty] now resolves to a measure", fullBlock.Message);
                Assert.Contains("renamed or added since the witness was locked", fullBlock.Message);
                Assert.Equal("unavailable", Assert.Single(fullPath.Results[1].VerifyResults).Status);

                var tableDriftPath = NewRun();
                await Prove(tableDriftPath, "COUNTROWS ( 'FutureRows' )");
                var beforeTableDrift = equivalenceQueries;
                await engine.CreateCalculatedTableAsync("FutureRows", "ROW ( \"Qty\", 1 )", "human");
                var tableInventory = await Inventory();
                var tableBlock = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                    tableDriftPath, "step-7", new Dictionary<string, AnswerValue>
                    {
                        ["perfPass"] = Decline("The proved form already meets the model floor."),
                    }, engine.ExecuteWorkflowVerifyAsync, tableInventory.Measures, tableInventory.Columns,
                    tableInventory.Functions, tableInventory.Tables));
                Assert.Equal(beforeTableDrift, equivalenceQueries);
                Assert.Contains("the reference 'FutureRows' now resolves to a calculated table", tableBlock.Message);
                Assert.Contains("created or renamed since the witness was locked", tableBlock.Message);
                Assert.Equal("unavailable", Assert.Single(tableDriftPath.Results[1].VerifyResults).Status);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Step7_measure_rename_follows_the_run_alias_and_lineage_receipt_but_a_renamed_expression_change_reproves()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-r3-rename-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "rename-stable-proof.md"), @"---
name: rename-stable-proof
title: Rename-stable proof
version: 7
strictness: hard
---
## Step 1: Intent
```yaml gate
inputs:
  - name: intent
    question: ""Intent?""
    type: text
    required: required
```
## Step 2: Coverage
```yaml gate
inputs:
  - name: expectedValues
    question: ""Anchors?""
    type: text
    required: required
  - name: equivalenceGrid
    question: ""Grid?""
    type: text
    required: required
  - name: openGrains
    question: ""Open grains?""
    type: text
    required: answer-or-decline
verify:
  - kind: anchor_coverage
    anchors: expectedValues
```
## Step 3: Candidate
```yaml gate
inputs:
  - name: target
    question: ""Target?""
    type: objectRef
    required: required
verify:
  - kind: expected_values
    anchors: expectedValues
```
## Step 4: Witness
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    type: text
    required: required
    daxPurity: no-bare-measures
```
## Step 5: Battery
```yaml gate
inputs:
  - name: battery
    question: ""Battery?""
    type: text
    required: required
```
## Step 6: Prove
```yaml gate
inputs:
  - name: proof
    question: ""Proof?""
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
```
## Step 7: Finalize
```yaml gate
ops: [update_measure, rename_object, set_property]
inputs:
  - name: perfPass
    question: ""Performance rewrite?""
    type: text
    required: answer-or-decline
  - name: finalized
    question: ""Finalized?""
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: expectedValues
  - kind: dax_equivalence
    when: inputs.perfPass.answered
    probe: witnessDax
```
");
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("RenameStable", 1604);
                var fact = await engine.CreateTableAsync("Fact", "human");
                await engine.CreateColumnAsync(fact, "Category", "String", "Category", "human");
                await engine.CreateColumnAsync(fact, "Value", "Double", "Value", "human");
                var unchangedTarget = await engine.CreateMeasureAsync(fact, "Working", "SUM ( 'Fact'[Value] )", "human");
                var changedTarget = await engine.CreateMeasureAsync(fact, "Working Changed", "SUM ( 'Fact'[Value] )", "human");
                sessions.Current.LiveOrigin = new LiveOrigin("endpoint-r3", "RenameStable", null);
                var mismatch = false;
                var equivalenceQueries = 0;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-r3", "RenameStable", query =>
                {
                    if (query.Contains("ROW (\n    \"v\"", StringComparison.Ordinal)) return Scalar(1.0);
                    equivalenceQueries++;
                    var candidate = mismatch ? 2.0 : 1.0;
                    return query.Contains("'Fact'[Category]", StringComparison.Ordinal)
                        ? Comparison(new[] { "Fact[Category]" }, new object[] { "A", 1.0, candidate })
                        : Comparison(Array.Empty<string>(), new object[] { 1.0, candidate });
                }));

                const string anchors = "[{\"context\":{},\"expect\":1},{\"context\":{\"'Fact'[Category]\":\"A\"},\"expect\":1}]";
                async Task<WorkflowRunView> ProveThroughStep6(string target)
                {
                    var run = await engine.StartWorkflowAsync("rename-stable-proof", "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"intent\":\"prove\"}", "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-2", JsonSerializer.Serialize(new
                    {
                        expectedValues = anchors,
                        equivalenceGrid = "'Fact'[Category]",
                        openGrains = new { declined = true, reason = "Every grain is pinned." },
                    }), "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-3",
                        JsonSerializer.Serialize(new { target }), "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-4",
                        JsonSerializer.Serialize(new { witnessDax = "SUMX ( 'Fact', 'Fact'[Value] )" }), "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-5", "{\"battery\":\"complete\"}", "human");
                    return await engine.SubmitWorkflowStepAsync(run.RunId, "step-6", "{\"proof\":\"complete\"}", "human");
                }

                mismatch = false;
                var unchanged = await ProveThroughStep6(unchangedTarget);
                var unchangedEquivalenceCount = equivalenceQueries;
                var renamed = await engine.RenameObjectAsync(unchangedTarget, "Production", "human");
                var completed = await engine.SubmitWorkflowStepAsync(unchanged.RunId, "step-7", JsonSerializer.Serialize(new
                {
                    perfPass = new { declined = true, reason = "The proved form already meets the model floor." },
                    finalized = renamed.NewRef,
                }), "human");
                Assert.Equal("completed", completed.Status);
                Assert.Equal("passed", completed.Steps[6].VerifyResults[0].Status);
                Assert.Equal("not_applicable", completed.Steps[6].VerifyResults[1].Status);
                Assert.Equal(unchangedEquivalenceCount, equivalenceQueries);
                Assert.Equal(unchangedTarget, completed.Steps[2].Answers["target"].Value);

                mismatch = false;
                var changed = await ProveThroughStep6(changedTarget);
                var changedEquivalenceCount = equivalenceQueries;
                await engine.SetObjectPropertyAsync(changedTarget, "Name", "Production Changed", "human");
                const string renamedChangedRef = "measure:Fact/Production Changed";
                await engine.SetDaxAsync(renamedChangedRef, "SUM ( 'Fact'[Value] ) + 1", "human");
                mismatch = true;
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    changed.RunId, "step-7", JsonSerializer.Serialize(new
                    {
                        perfPass = new { declined = true, reason = "The original form met the model floor." },
                        finalized = renamedChangedRef,
                    }), "human"));
                Assert.True(equivalenceQueries > changedEquivalenceCount);
                Assert.Contains("candidate and witness DIVERGE", blocked.Message);
                Assert.DoesNotContain("does not resolve", blocked.Message);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Witness_purity_resolves_a_qualified_column_before_a_same_named_measure()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-purity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "purity-collision.md"), @"---
name: purity-collision
title: Purity collision
version: 7
strictness: hard
---
## Step 1: Witness
Author the raw-column witness.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    type: text
    required: required
    daxPurity: no-bare-measures
```
");
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("PurityCollision", 1604);
                var fact = await engine.CreateTableAsync("Fact", "human");
                var measureHome = await engine.CreateTableAsync("Measure Home", "human");
                var derived = await engine.CreateTableAsync("Derived", "human");
                await engine.CreateColumnAsync(fact, "Amount", "Double", "Amount", "human");
                await engine.CreateMeasureAsync(measureHome, "Amount", "SUM ( 'Fact'[Amount] )", "human");
                await engine.CreateCalculatedColumnAsync(derived, "Amount", "1", "human");

                var accepted = await engine.StartWorkflowAsync("purity-collision", "human");
                var acceptedView = await engine.SubmitWorkflowStepAsync(accepted.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "SUM ( 'Fact'[Amount] )" }), "human");
                Assert.Equal("completed", acceptedView.Status);

                var bareRun = await engine.StartWorkflowAsync("purity-collision", "human");
                var bare = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    bareRun.RunId, "step-1", JsonSerializer.Serialize(new { witnessDax = "[Amount]" }), "human"));
                Assert.Contains("must contain zero measure references", bare.Message);
                Assert.Contains("[Amount]", bare.Message);

                var qualifiedRun = await engine.StartWorkflowAsync("purity-collision", "human");
                var qualified = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    qualifiedRun.RunId, "step-1", JsonSerializer.Serialize(new { witnessDax = "'Measures'[Amount]" }), "human"));
                Assert.Contains("must contain zero measure references", qualified.Message);
                Assert.Contains("[Amount]", qualified.Message);

                var calculatedRun = await engine.StartWorkflowAsync("purity-collision", "human");
                var calculated = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    calculatedRun.RunId, "step-1", JsonSerializer.Serialize(new { witnessDax = "SUM ( 'Derived'[Amount] )" }), "human"));
                Assert.Contains("witness must use base columns", calculated.Message);
                Assert.Contains("Inline this column's logic instead", calculated.Message);
                Assert.Contains("'Derived'[Amount]", calculated.Message);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Witness_purity_refuses_a_bare_calculated_column_and_teaches_qualification_for_a_base_collision()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-purity-bare-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "purity-bare.md"), @"---
name: purity-bare
title: Bare column purity
version: 7
strictness: hard
---
## Step 1: Witness
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    type: text
    required: required
    daxPurity: no-bare-measures
```
");
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("BarePurity", 1604);
                var fact = await engine.CreateTableAsync("Fact", "human");
                var derived = await engine.CreateTableAsync("Derived", "human");
                await engine.CreateColumnAsync(fact, "Amount", "Double", "Amount", "human");
                await engine.CreateColumnAsync(fact, "Qty", "Int64", "Qty", "human");
                await engine.CreateCalculatedColumnAsync(derived, "Amount", "1", "human");

                var calculatedRun = await engine.StartWorkflowAsync("purity-bare", "human");
                var calculated = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    calculatedRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "SUMX ( 'Derived', [Amount] )" }), "human"));
                Assert.Contains("witness must use base columns", calculated.Message);
                Assert.Contains("'Derived'[Amount]", calculated.Message);
                Assert.Contains("Qualify the intended base-column reference", calculated.Message);

                var baseOnlyRun = await engine.StartWorkflowAsync("purity-bare", "human");
                var baseOnly = await engine.SubmitWorkflowStepAsync(baseOnlyRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "SUMX ( 'Fact', [Qty] )" }), "human");
                Assert.Equal("completed", baseOnly.Status);

                var qualifiedBaseRun = await engine.StartWorkflowAsync("purity-bare", "human");
                var qualifiedBase = await engine.SubmitWorkflowStepAsync(qualifiedBaseRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "SUM ( 'Fact'[Amount] )" }), "human");
                Assert.Equal("completed", qualifiedBase.Status);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Witness_purity_refuses_calculated_table_references_and_accepts_base_tables()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-purity-table-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "purity-table.md"), @"---
name: purity-table
title: Calculated table purity
version: 7
strictness: hard
---
## Step 1: Witness
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    type: text
    required: required
    daxPurity: no-bare-measures
```
");
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("TablePurity", 1604);
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateCalculatedTableAsync("BadRows", "ROW ( \"Value\", 1 )", "human");

                var baseRun = await engine.StartWorkflowAsync("purity-table", "human");
                var accepted = await engine.SubmitWorkflowStepAsync(baseRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "COUNTROWS ( 'Sales' )" }), "human");
                Assert.Equal("completed", accepted.Status);

                var quotedRun = await engine.StartWorkflowAsync("purity-table", "human");
                var quoted = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    quotedRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "COUNTROWS ( 'BadRows' )" }), "human"));
                Assert.Contains("witness must use base tables", quoted.Message);
                Assert.Contains("Rebuild this calculated table's row logic inline from base tables: 'BadRows'", quoted.Message);

                var caseInsensitiveRun = await engine.StartWorkflowAsync("purity-table", "human");
                var caseInsensitive = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    caseInsensitiveRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "COUNTROWS ( 'badrows' )" }), "human"));
                Assert.Contains("'BadRows'", caseInsensitive.Message);

                var bareRun = await engine.StartWorkflowAsync("purity-table", "human");
                var bare = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    bareRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "COUNTROWS ( BadRows )" }), "human"));
                Assert.Contains("'BadRows'", bare.Message);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Witness_purity_refuses_model_UDF_calls_while_built_in_DAX_functions_remain_allowed()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-v7-purity-udf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "purity-udf.md"), @"---
name: purity-udf
title: UDF purity
version: 7
strictness: hard
---
## Step 1: Witness
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    type: text
    required: required
    daxPurity: no-bare-measures
```
");
            var sessions = new SessionManager();
            LocalEngine? engine = null;
            try
            {
                engine = new LocalEngine(sessions, new Pro(), workspace);
                await engine.CreateModelAsync("UdfPurity", 1702);
                var fact = await engine.CreateTableAsync("Fact", "human");
                await engine.CreateColumnAsync(fact, "Date", "DateTime", "Date", "human");
                await engine.CreateColumnAsync(fact, "Value", "Double", "Value", "human");
                await engine.CreateFunctionAsync("Bad.Net", "() => 1", "human");

                var builtInsRun = await engine.StartWorkflowAsync("purity-udf", "human");
                var builtIns = await engine.SubmitWorkflowStepAsync(builtInsRun.RunId, "step-1",
                    JsonSerializer.Serialize(new
                    {
                        witnessDax = "SUMX ( 'Fact', CALCULATE ( SUM ( 'Fact'[Value] ), DATESINPERIOD ( 'Fact'[Date], DATE ( 2025, 1, 1 ), -1, MONTH ) ) )",
                    }), "human");
                Assert.Equal("completed", builtIns.Status);

                var udfRun = await engine.StartWorkflowAsync("purity-udf", "human");
                var udf = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    udfRun.RunId, "step-1", JsonSerializer.Serialize(new { witnessDax = "Bad.Net ()" }), "human"));
                Assert.Contains("contain no model-defined function calls", udf.Message);
                Assert.Contains("Inline this function's logic instead: Bad.Net", udf.Message);

                await engine.CreateFunctionAsync("SUMX", "() => 1", "human");
                var builtInNamedRun = await engine.StartWorkflowAsync("purity-udf", "human");
                var builtInNamed = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                    builtInNamedRun.RunId, "step-1",
                    JsonSerializer.Serialize(new { witnessDax = "SUMX ( 'Fact', 'Fact'[Value] )" }), "human"));
                Assert.Contains("model-defined function calls", builtInNamed.Message);
                Assert.Contains("SUMX", builtInNamed.Message);
            }
            finally
            {
                engine?.Dispose();
                sessions.Dispose();
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task X27_v7_refuses_bare_measure_witness_while_undeclared_policy_keeps_legacy_acceptance()
        {
            const string anchors = "[{\"context\":{},\"expect\":1},"
                + "{\"context\":{\"'Product'[Name]\":\"A\"},\"expect\":1}]";
            var run = new WorkflowRunStore().Start(V7Definition(), null);
            await Step1(run);
            await Step2(run, anchors, "'Product'[Name]", null,
                (spec, step, state, all) => Task.FromResult(LocalEngine.WorkflowAnchorCoverage(spec, step, state, all)));
            await Step3(run, "SUM ( 'Fact'[Value] )",
                (spec, step, state, all) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "scripted anchor match" }));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "step-4", new Dictionary<string, AnswerValue>
                {
                    ["witnessDax"] = Answer("SUM ( 'Fact'[Value] ) + [Sales PY] + 0 * LEN ( \"[Ignored]\" ) // [Comment]"),
                    ["witnessTiming"] = Answer("0.1 seconds"),
                }, null));
            Assert.Contains("must contain zero measure references", refused.Message);
            Assert.Contains("[Sales PY]", refused.Message);
            Assert.DoesNotContain("[Ignored]", refused.Message);
            Assert.DoesNotContain("[Comment]", refused.Message);

            var legacyDef = new WorkflowDef
            {
                Name = "legacy-witness",
                Strictness = "hard",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "step-1", Number = 1, Title = "Witness",
                        Gate = new GateSpec
                        {
                            Inputs = new[]
                            {
                                new GateInput { Name = "witnessDax", Type = "text", Required = "required" },
                            },
                        },
                    },
                },
            };
            var legacy = new WorkflowRunStore().Start(legacyDef, null);
            await WorkflowRunner.SubmitStepAsync(legacy, "step-1", new Dictionary<string, AnswerValue>
            {
                ["witnessDax"] = Answer("[Sales PY]"),
            }, null);
            Assert.Equal("completed", legacy.Status);
        }
    }
}
