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
        public void Report_includes_measured_relationship_evidence_but_no_history()
        {
            var markdown = TestReportRenderer.Render(Run());
            Assert.Contains("2 orphan rows (2% of 100)", markdown);
            Assert.Contains("Table row counts", markdown);
            Assert.Contains("Model COUNTROWS 100 at 2026-07-11T12:00:01Z; source COUNT_BIG 100 at 2026-07-11T12:00:03Z", markdown);
            Assert.DoesNotContain("## History", markdown);
        }

        // Kane, 16 Oct: the page, the assistant's note and the report count the checks a person sees (here a failing saved check, a failing
        // Relationships check and a matching Table row counts check), never the probes the grade is worked out from.
        [Fact]
        public void The_page_the_assistants_note_and_the_report_count_checks_in_one_unit()
        {
            var run = Run();
            var tally = CheckTally.Of(run);
            Assert.Equal((1, 2, 0), (tally.Passed, tally.Differs, tally.Cannot));
            Assert.Equal("1 of 3 checks pass", tally.Words());
            Assert.Contains("1 of 3 checks pass", McpToolsTesting.RunLabel(run));
            Assert.Contains("- Checks: 1 of 3 checks pass.", TestReportRenderer.Render(run));
            var doc = TestReportRenderer.BuildEvidence(run);
            Assert.Equal((1, 3, 0), (doc.Coverage.Verified, doc.Coverage.Total, doc.Coverage.Unknowns));
        }

        // Hero 26 N3: a measure with no saved check carries a coverage outcome ("unmapped:") so the grade sees the gap. It is not a check the person has,
        // so it is never part of "2 of 3": the run said "2 of 11" for a page that said "2 of 3".
        [Fact]
        public void Measures_with_no_saved_check_are_not_counted_as_checks()
        {
            var run = Run();
            var before = CheckTally.Of(run);
            var extra = Enumerable.Range(0, 8).Select(i => new ReconcileOutcome { DefId = "unmapped:measure:Sales/M" + i, Title = "M" + i, Verdict = Verdict.NotVerifiable, Message = "No human-accepted source SQL mapping exists for this measure." });
            run.Reconciles = run.Reconciles.Concat(extra).ToArray();
            var after = CheckTally.Of(run);
            Assert.Equal((before.Passed, before.Differs, before.Cannot), (after.Passed, after.Differs, after.Cannot));
            Assert.Equal(before.Words(), after.Words());
            Assert.Equal("1 of 3 checks pass: 2 differ.", after.Line());
            Assert.Equal(after.Line(), run.Headline);
            var one = CheckTally.Of(new TestSuiteRunResult { Reconciles = new[] { Outcome("A", Verdict.Fail, "x") } });
            Assert.Equal("0 of 1 check passes: 1 differs.", one.Line());
        }

        [Fact]
        public void The_report_names_no_tool_and_no_assembly_number()
        {
            var html = TestReportRenderer.RenderHtml(Run());
            var markdown = TestReportRenderer.Render(Run());
            foreach (var text in new[] { html, markdown })
            {
                Assert.DoesNotContain("run_tests", text, StringComparison.Ordinal);
                Assert.DoesNotContain("1.0.0.0", text, StringComparison.Ordinal);
                Assert.DoesNotContain("root failure(s)", text, StringComparison.Ordinal);
            }
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

        // Security and the Model interview left Tests in 1.2.0 (Kane, 2026-09-15), so neither may reappear
        // in a report: a sealed artifact that still carried them would be evidence of checks nobody ran.
        [Fact]
        public void The_report_has_no_security_or_interview_section()
        {
            var markdown = TestReportRenderer.Render(Run());
            Assert.DoesNotContain("## Security", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("Model Interview", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("Object visibility", markdown, StringComparison.Ordinal);

            var html = EvidenceArtifact.Seal(TestReportRenderer.BuildEvidence(Run())).Html;
            Assert.DoesNotContain("Model Interview", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Object visibility", html, StringComparison.Ordinal);
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
            // Coverage is in the checks the page shows (a failing saved check, a failing Relationships check, a matching Table row counts
            // check), not the probe counts the grade is worked out from (Kane, 16 Oct).
            Assert.Equal(1, doc.Coverage.Verified);
            Assert.Equal(3, doc.Coverage.Total);
            Assert.Equal(0, doc.Coverage.Unknowns);
            Assert.Equal(artifact.ContentHash, EvidenceHash.HashOfJsonText(artifact.Json));
            Assert.Contains(artifact.ContentHash, artifact.Html);
            Assert.Contains("Relationships and integrity", artifact.Html);
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
