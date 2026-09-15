using System;
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

        // Tests and Saved reports became a WHOLE Pro feature on 2026-09-15, so there is no degraded free export to
        // pin any more: free is refused at the entry, and the only soft refusal left is "no run belongs to this
        // model yet". Both halves are asserted here so the entitled path and the refused one stay one story.
        [Fact]
        public async Task No_run_is_a_soft_refusal_and_free_is_refused_at_the_entry()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(TestModels.FindBim());

            var before = await engine.ExportTestReportAsync();
            Assert.Contains("run the suite again", before.Note);
            Assert.Null(before.Error);
            Assert.Null(before.Markdown);

            using var freeSessions = new SessionManager();
            using var free = new LocalEngine(freeSessions, new Fake(false));
            await free.OpenAsync(TestModels.FindBim());
            var refusal = await Assert.ThrowsAsync<EntitlementException>(() => free.ExportTestReportAsync());
            Assert.Contains("is a Semanticus Pro feature.", refusal.Message);
            Assert.Contains("Tests", refusal.Message);
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
            // Security and the Model interview left Tests in 1.2.0 (Kane, 2026-09-15): an exported report may
            // not carry a section for checks this run never made.
            Assert.DoesNotContain("Model Interview", report.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("## Security", report.Markdown, StringComparison.Ordinal);
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
