using System;
using System.Linq;
using Semanticus.Engine;
using EvidenceArtifact = Semanticus.Engine.Evidence.EvidenceArtifact;
using EvidenceHash = Semanticus.Engine.Evidence.EvidenceHash;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TestReportRendererTests
    {
        [Fact]
        public void Grade_and_coverage_are_on_one_physical_line()
        {
            var markdown = TestReportRenderer.Render(Run());
            var line = markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Single(x => x.Contains("**Grade "));
            Assert.Contains("Grade B", line);
            Assert.Contains("Coverage 82.6%", line);
        }

        [Fact]
        public void Root_causes_lead_every_detail_section()
        {
            var markdown = TestReportRenderer.Render(Run());
            var root = markdown.IndexOf("Measure Revenue ties to GL: mismatch at total.", StringComparison.Ordinal);
            Assert.True(root >= 0);
            Assert.True(root < markdown.IndexOf("## Measures", StringComparison.Ordinal));
            Assert.True(root < markdown.IndexOf("## Relationships", StringComparison.Ordinal));
            Assert.True(root < markdown.IndexOf("## Security", StringComparison.Ordinal));
        }

        [Fact]
        public void Compare_rows_are_emitted_only_for_failing_measures()
        {
            var run = Run();
            run.Reconciles = new[]
            {
                Outcome("Failing measure", Verdict.Fail, "Fail row context"),
                Outcome("Passing measure", Verdict.Pass, "Pass row context"),
            };
            var markdown = TestReportRenderer.Render(run);
            Assert.Contains("Fail row context", markdown);
            Assert.DoesNotContain("Pass row context", markdown);
        }

        [Fact]
        public void Output_contains_no_em_or_en_dashes()
        {
            var run = Run();
            run.ModelName = "Sales — governed";
            run.Reconciles[0].Message = "mismatch – source moved";
            var markdown = TestReportRenderer.Render(run);
            Assert.DoesNotContain('—', markdown);
            Assert.DoesNotContain('–', markdown);
        }

        [Fact]
        public void Offline_run_states_what_could_not_be_verified()
        {
            var run = Run();
            run.Live = false;
            run.Environment = null;
            var markdown = TestReportRenderer.Render(run);
            Assert.Contains("Run mode: Offline. Live-only probes and reconciliations could not be verified.", markdown);
        }

        [Fact]
        public void Report_includes_measured_relationship_and_security_evidence_but_no_history()
        {
            var markdown = TestReportRenderer.Render(Run());
            Assert.Contains("2 orphan rows (2% of 100)", markdown);
            Assert.Contains("Table row counts", markdown);
            Assert.Contains("Model COUNTROWS 100 at 2026-07-11T12:00:01Z; source COUNT_BIG 100 at 2026-07-11T12:00:03Z", markdown);
            Assert.Contains("Preview: `[Region] = 1`", markdown);
            Assert.Contains("8 visible tables, 2 hidden tables, 3 hidden columns", markdown);
            Assert.DoesNotContain("## History", markdown);
        }

        [Fact]
        public void Html_is_print_ready_and_carries_the_same_fixed_sections()
        {
            var html = TestReportRenderer.RenderHtml(Run());
            Assert.StartsWith("<!doctype html>", html);
            Assert.Contains("@media print", html);
            Assert.Contains("<strong>Grade B | Coverage 82.6%</strong>", html);
            Assert.True(html.IndexOf("Root causes", StringComparison.Ordinal) < html.IndexOf("Measures", StringComparison.Ordinal));
            Assert.Contains("Table row counts", html);
            Assert.Contains("Object visibility Regional", html);
        }

        [Fact]
        public void Html_escapes_model_content_before_applying_report_markup()
        {
            var run = Run();
            run.ModelName = "<script>alert('x')</script>";
            var html = TestReportRenderer.RenderHtml(run);
            Assert.DoesNotContain("<script>alert", html);
            Assert.Contains("&lt;script&gt;alert", html);
        }

        [Fact]
        public void Interview_is_timestamped_evidence_without_changing_health()
        {
            var run = Run();
            var grade = run.Health.Grade;
            var coverage = run.Health.CoveragePct;
            run.Interview = new[]
            {
                new InterviewEvidence
                {
                    Question = "What were total sales?", Outcome = "Correct",
                    When = "2026-07-12T09:30:00Z", Detail = "the answer matched the trusted value",
                    ReplayStatus = "replayed", PreviousOutcome = "Correct",
                },
                new InterviewEvidence { Question = "Can this model answer churn?", ReplayStatus = "chat-only" },
            };

            var markdown = TestReportRenderer.Render(run);

            Assert.Contains("## Behavioral contracts (Model Interview)", markdown);
            Assert.Contains("behavioral evidence only; they do not change the test grade or coverage", markdown);
            Assert.Contains("Right. Observed 2026-07-12T09:30:00Z", markdown);
            Assert.Contains("Replayed in this Tests run", markdown);
            Assert.Contains("Chat-only contract", markdown);
            Assert.Equal(grade, run.Health.Grade);
            Assert.Equal(coverage, run.Health.CoveragePct);
        }

        [Fact]
        public void Shared_evidence_maps_tests_honestly_and_seals_one_json_html_pair()
        {
            var run = Run();
            run.RunId = "test-run-1";
            run.Health.Checked = 4;
            run.Health.Passed = 1;
            run.Health.Failed = 1;
            run.Health.Suspect = 1;
            run.Health.NotVerifiable = 1;

            var artifact = EvidenceArtifact.Seal(TestReportRenderer.BuildEvidence(run));
            var doc = EvidenceHash.Deserialize(artifact.Json);

            Assert.Equal("test-suite", doc.Kind);
            Assert.Equal(Semanticus.Engine.Evidence.Verdict.Broken, doc.Verdict);
            Assert.Equal(1, doc.Coverage.Verified);
            Assert.Equal(4, doc.Coverage.Total);
            Assert.Equal(1, doc.Coverage.Unknowns);
            Assert.Equal(artifact.ContentHash, EvidenceHash.HashOfJsonText(artifact.Json));
            Assert.Contains(artifact.ContentHash, artifact.Html);
            Assert.Contains("Relationships and integrity", artifact.Html);
            Assert.Contains("Model Interview", artifact.Html);
        }

        private static TestSuiteRunResult Run()
        {
            var relationships = RelationshipIntegrity.Evaluate(new[]
            {
                new RelationshipCheckInput
                {
                    Name = "Sales to Customer", ManyTable = "Sales", ManyColumn = "CustomerKey",
                    OneTable = "Customer", OneColumn = "CustomerKey", Cardinality = "manyToOne",
                    ManyColumnType = "Int64", OneColumnType = "Int64",
                    Probe = new RelationshipProbeResult
                    {
                        OrphanRows = 2, DuplicateKeys = 0, ManyRowCount = 100, OneRowCount = 20,
                        BlankForeignKeys = 1, BlankKeys = 0,
                    },
                },
            });
            relationships.TableRowCounts = new[]
            {
                TableRowCountReconciliation.Evaluate(new TableRowCountInput
                {
                    ModelTable = "Sales", Schema = "dbo", Entity = "fact_sales", ModelCount = 100, SourceCount = 100,
                    ModelObservedUtc = "2026-07-11T12:00:01Z", SourceObservedUtc = "2026-07-11T12:00:03Z",
                }),
            };
            var security = SecurityStaticChecks.Evaluate(new[]
            {
                new RoleFilterInput { Role = "Regional", Table = "Sales", FilterExpression = "[Region] = 1" },
            });
            security.Ols = new[]
            {
                new RoleOls { Role = "Regional", TablesTotal = 10, TablesHidden = 2, ColumnsHidden = 3 },
            };
            return new TestSuiteRunResult
            {
                ModelName = "Contoso",
                ModelFingerprint = "fp",
                When = "2026-07-11T12:00:00.0000000Z",
                Live = true,
                Environment = "Contoso on localhost:1234",
                DurationMs = 1234,
                Health = new TestHealth
                {
                    Grade = "B", CoveragePct = 82.6, GatedBy = new[] { "one relationship check failed" },
                },
                Relationships = relationships,
                Security = security,
                Reconciles = new[] { Outcome("Revenue ties to GL", Verdict.Fail, "Grand total") },
            };
        }

        private static ReconcileOutcome Outcome(string title, Verdict verdict, string context) => new ReconcileOutcome
        {
            DefId = title,
            Title = title,
            Verdict = verdict,
            Message = verdict == Verdict.Fail ? "mismatch at total" : "reconciled",
            Rows = new[]
            {
                new CompareRow
                {
                    Context = context, Sql = 100m, Dax = verdict == Verdict.Fail ? 99m : 100m,
                    Delta = verdict == Verdict.Fail ? -1m : 0m, Verdict = verdict,
                    Explanation = verdict == Verdict.Fail ? "outside tolerance" : "within tolerance",
                },
            },
            Variants = new[]
            {
                new TiVariantVerdict { Variant = title + " YTD", Kind = TiVariantKind.Ytd, Verdict = verdict, Explanation = "identity checked" },
            },
        };
    }
}
