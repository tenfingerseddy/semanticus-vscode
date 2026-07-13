using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Evidence;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class EvidenceLibraryTests
    {
        private sealed class Tier : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Tier(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string A, string B) Make(bool pro = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-evidence-library-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var a = Path.Combine(root, "ModelA.bim");
            var b = Path.Combine(root, "ModelB.bim");
            File.Copy(TestModels.FindBim(), a);
            File.Copy(TestModels.FindBim(), b);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Tier(pro), root), sessions, root, a, b);
        }

        [Fact]
        public async Task Saving_is_explicit_atomic_and_model_scoped_even_in_one_folder()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.A);
                    await x.Engine.RunTestSuiteAsync(false, "human");
                    var exported = await x.Engine.ExportTestReportAsync();
                    Assert.NotNull(exported.Json);
                    Assert.Empty((await x.Engine.ListEvidenceAsync()).Items); // export alone writes nothing

                    ActivityEvent activity = null;
                    void Handler(ActivityEvent e) { if (e.Kind == "save_evidence") activity = e; }
                    x.Sessions.Bus.Activity += Handler;
                    EvidenceSaveResult saved;
                    try { saved = await x.Engine.SaveEvidenceAsync("tests", null, "human"); }
                    finally { x.Sessions.Bus.Activity -= Handler; }

                    Assert.True(saved.Saved, saved.Error ?? saved.Note);
                    Assert.Equal("test-suite", saved.Item.Kind);
                    Assert.Contains(Path.Combine(".semanticus", "evidence", "file-modela-bim"), saved.Item.JsonPath);
                    Assert.True(File.Exists(saved.Item.JsonPath));
                    Assert.True(File.Exists(saved.Item.HtmlPath));
                    Assert.True(EvidenceStore.Verify(saved.Item.JsonPath, out var reason), reason);
                    Assert.NotNull(activity); // the other door receives a live refresh nudge
                    Assert.Equal(saved.Item.Id, activity.Target);

                    // Same parent directory, different model path: identity subfolders keep the libraries apart.
                    await x.Engine.OpenAsync(x.B);
                    Assert.Empty((await x.Engine.ListEvidenceAsync()).Items);
                    await x.Engine.OpenAsync(x.A);
                    Assert.Single((await x.Engine.ListEvidenceAsync()).Items);

                    // A clone at another absolute path keeps the same repository-relative storage key.
                    var clone = Path.Combine(x.Root, "clone");
                    Directory.CreateDirectory(clone);
                    var cloneModel = Path.Combine(clone, Path.GetFileName(x.A));
                    File.Copy(x.A, cloneModel);
                    var cloneEvidence = Path.Combine(clone, ".semanticus", "evidence", "file-modela-bim");
                    Directory.CreateDirectory(cloneEvidence);
                    File.Copy(saved.Item.JsonPath, Path.Combine(cloneEvidence, Path.GetFileName(saved.Item.JsonPath)));
                    File.Copy(saved.Item.HtmlPath, Path.Combine(cloneEvidence, Path.GetFileName(saved.Item.HtmlPath)));
                    await x.Engine.OpenAsync(cloneModel);
                    Assert.Single((await x.Engine.ListEvidenceAsync()).Items);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Terminal_workflow_evidence_saves_free_and_round_trips_through_the_unified_library()
        {
            var x = Make(pro: false);
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.A);
                    const string workflow = @"---
name: share-proof
title: Share proof
---
## Step 1: Record the result
Record what was checked.
";
                    await x.Engine.SaveWorkflowAsync("share-proof", workflow, "human");
                    var run = await x.Engine.StartWorkflowAsync("share-proof", "human");
                    var done = await x.Engine.SubmitWorkflowStepAsync(run.RunId, run.CurrentStep.StepId, "{}", "human");
                    Assert.Equal("completed", done.Status);

                    var saved = await x.Engine.SaveEvidenceAsync("workflow", done.RunId, "human");
                    Assert.True(saved.Saved, saved.Error ?? saved.Note);
                    Assert.Equal("workflow-run", saved.Item.Kind);
                    var list = await x.Engine.ListEvidenceAsync();
                    Assert.Single(list.Items);
                    Assert.Equal(done.RunId, list.Items[0].Id);
                    var opened = await x.Engine.GetEvidenceAsync(done.RunId);
                    Assert.Contains("\"kind\":\"workflow-run\"", opened.Json);
                    Assert.Contains(opened.ContentHash, opened.Html);

                    // Test evidence keeps the already-ratified soft Pro boundary; sharing adds no new gate.
                    await x.Engine.RunTestSuiteAsync(false, "human");
                    var testSave = await x.Engine.SaveEvidenceAsync("tests", null, "human");
                    Assert.False(testSave.Saved);
                    Assert.Contains("Pro", testSave.Note);
                    Assert.Single((await x.Engine.ListEvidenceAsync()).Items);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Tampered_records_remain_listed_invalid_and_cannot_be_opened_as_trusted()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.A);
                    await x.Engine.RunTestSuiteAsync(false, "human");
                    var saved = await x.Engine.SaveEvidenceAsync("tests", null, "human");
                    Assert.True(saved.Saved);
                    File.WriteAllText(saved.Item.JsonPath, File.ReadAllText(saved.Item.JsonPath).Replace("test evidence", "changed evidence"));

                    var list = await x.Engine.ListEvidenceAsync();
                    var item = Assert.Single(list.Items);
                    Assert.False(item.Valid);
                    Assert.Equal(1, list.InvalidCount);
                    Assert.Contains("signature", item.Note);
                    var opened = await x.Engine.GetEvidenceAsync(item.Id);
                    Assert.Contains("not trusted", opened.Error);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Re_saving_the_same_source_is_idempotent_and_invalid_source_writes_nothing()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.A);
                    await x.Engine.RunTestSuiteAsync(false, "human");
                    var first = await x.Engine.SaveEvidenceAsync("tests", null, "human");
                    var second = await x.Engine.SaveEvidenceAsync("tests", null, "human");
                    Assert.True(first.Saved && second.Saved);
                    Assert.Equal(first.Item.Id, second.Item.Id);
                    Assert.Single((await x.Engine.ListEvidenceAsync()).Items);

                    var refused = await x.Engine.SaveEvidenceAsync("guess", null, "human");
                    Assert.False(refused.Saved);
                    Assert.Contains("tests", refused.Error);
                    Assert.Single((await x.Engine.ListEvidenceAsync()).Items);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
