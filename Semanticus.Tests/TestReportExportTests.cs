using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Evidence;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TestReportExportTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task No_open_model_is_the_only_export_error()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(true));
            var result = await engine.ExportTestReportAsync();
            Assert.NotNull(result.Error);
            Assert.Null(result.Note);
            Assert.Null(result.Markdown);
        }

        [Fact]
        public async Task No_run_and_free_tier_are_soft_refusals_with_next_actions()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(false));
            await engine.OpenAsync(TestModels.FindBim());

            var before = await engine.ExportTestReportAsync();
            Assert.Contains("run the suite again", before.Note);
            Assert.Null(before.Error);

            var run = await engine.RunTestSuiteAsync(false, "human");
            Assert.Null(run.Error);
            var after = await engine.ExportTestReportAsync();
            Assert.Contains("signable test report is Pro", after.Note);
            Assert.Null(after.Error);
            Assert.Null(after.Markdown);
        }

        [Fact]
        public async Task Pro_export_is_bound_to_the_current_model_fingerprint()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(true));
            var opened = await engine.OpenAsync(TestModels.FindBim());
            var run = await engine.RunTestSuiteAsync(false, "human");

            var report = await engine.ExportTestReportAsync();
            Assert.Null(report.Error);
            Assert.Contains("# Semanticus test report", report.Markdown);
            Assert.Contains("- Model: " + opened.ModelName, report.Markdown);
            Assert.Contains("## Behavioral contracts (Model Interview)", report.Markdown);
            Assert.Contains("behavioral evidence only; they do not change the test grade or coverage", report.Markdown);
            Assert.Contains("<!doctype html>", report.Html);
            Assert.Contains(opened.ModelName, report.Html);
            Assert.Contains("\"kind\":\"test-suite\"", report.Json);
            Assert.Equal(report.ContentHash, EvidenceHash.HashOfJsonText(report.Json));
            Assert.Contains(report.ContentHash, report.Html);

            await engine.CreateModelAsync("Different model", 1604);
            var stale = await engine.ExportTestReportAsync();
            Assert.Contains("different model", stale.Note);
            Assert.Null(stale.Markdown);
            Assert.Null(stale.Html);
            Assert.Null(stale.Json);
            Assert.Null(stale.ContentHash);
        }
    }
}
