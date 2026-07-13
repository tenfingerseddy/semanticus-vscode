using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Learning Loop L1 (knowledge store) + L2 (recall), offline (docs/learning-loop-plan.md §3.3–3.5). Pins the
    /// KERNEL invariants — delta replay (add→edit→vote→approve→delete ordering; score&lt;=0 vanishes; purge erases
    /// prior; a corrupt line is skipped + counted), the auto-approve write-gate, both-scope recall, deterministic
    /// fingerprinting, the recall ranking (key overlap beats recency, fingerprint bonus, retrieval-counter bump),
    /// purge dry-run purity, and provenance required-fields. All offline (no live connection, no inference).
    /// Both 'global' scope (USERPROFILE-redirected) and project scope (a TEMP COPY of the model, never the vendored
    /// submodule) write only into disposable temp dirs.
    /// </summary>
    public sealed class KnowledgeStoreTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Redirect USERPROFILE so 'global' scope lands in a temp home, never the real one; caller restores it.
        private static string RedirectHome(string ws)
        {
            var orig = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(ws, "home");
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            return orig;
        }

        // A workspace-anchored engine (no model open) exercises project scope via the workspace fallback.
        private static (LocalEngine engine, string ws, string origHome) MakeWs(bool autoApprove = true)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-know-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            if (!autoApprove)
                File.WriteAllText(Path.Combine(ws, ".semanticus", "knowledge-settings.json"), "{ \"autoApprove\": false }");
            var origHome = RedirectHome(ws);
            return (new LocalEngine(new SessionManager(), new Fake(true), ws), ws, origHome);
        }

        // A model-open engine whose sidecar (project scope) is a TEMP COPY of the .bim — opening the vendored
        // model directly would write knowledge files into the submodule working tree.
        private static async Task<(LocalEngine engine, string dir, string origHome)> MakeModelAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-know-m-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var origHome = RedirectHome(dir);
            var copy = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), copy);
            var e = new LocalEngine(new SessionManager(), new Fake(true), dir);
            await e.OpenAsync(copy);
            return (e, dir, origHome);
        }

        private static void Cleanup(string dir, string origHome)
        {
            Environment.SetEnvironmentVariable("USERPROFILE", origHome);
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- delta replay invariants (the store kernel) ----

        [Fact]
        public async Task Add_edit_vote_approve_delete_replay_in_order()
        {
            var (e, ws, home) = MakeWs(autoApprove: false);   // pending path so approve is observable
            try
            {
                var added = await e.AddInsightAsync("Use DIVIDE not '/'", new[] { "DAX-DIVIDE" }, "insight", "project", false, "agent");
                Assert.StartsWith("ki-", added.Id);
                Assert.Equal("pending", added.Status);          // auto-approve off → write-gated
                Assert.Equal(3, added.Score);                    // ExpeL init

                var edited = await e.EditInsightAsync(added.Id, "Use DIVIDE, never bare '/'", new[] { "DAX-DIVIDE", "divide-by-zero" }, "agent");
                Assert.Equal("Use DIVIDE, never bare '/'", edited.Text);
                Assert.Contains("divide-by-zero", edited.Keys);

                var up = await e.UpvoteInsightAsync(added.Id, "agent");
                Assert.Equal(4, up.Score);

                var approved = await e.ApproveInsightAsync(added.Id, "agent");
                Assert.Equal("approved", approved.Status);

                var del = await e.DeleteInsightAsync(added.Id, "agent");
                Assert.True(del.Changed);
                var listed = await e.ListInsightsAsync("project", null);
                Assert.DoesNotContain(listed.Insights, i => i.Id == added.Id);   // tombstoned
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Downvote_to_zero_materializes_the_insight_out()
        {
            var (e, ws, home) = MakeWs();
            try
            {
                var a = await e.AddInsightAsync("Score-3 lesson", new[] { "K" }, "insight", "project", false, "agent");
                await e.DownvoteInsightAsync(a.Id, "agent");   // 2
                await e.DownvoteInsightAsync(a.Id, "agent");   // 1
                var gone = await e.DownvoteInsightAsync(a.Id, "agent");   // 0 → out
                Assert.Null(gone);                              // materialized out (delta trail kept, live set drops it)
                var listed = await e.ListInsightsAsync("project", null);
                Assert.DoesNotContain(listed.Insights, i => i.Id == a.Id);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Purge_dry_run_is_pure_then_confirm_erases_prior()
        {
            var (e, ws, home) = MakeWs();
            try
            {
                await e.AddInsightAsync("one", new[] { "a" }, "insight", "project", false, "agent");
                await e.AddInsightAsync("two", new[] { "b" }, "insight", "project", false, "agent");

                var dry = await e.PurgeKnowledgeAsync("project", confirm: false, "agent");
                Assert.False(dry.Purged);
                Assert.Equal(2, dry.LiveCount);
                Assert.Equal(2, (await e.ListInsightsAsync("project", null)).Insights.Length);   // dry-run changed nothing

                var done = await e.PurgeKnowledgeAsync("project", confirm: true, "agent");
                Assert.True(done.Purged);
                Assert.Empty((await e.ListInsightsAsync("project", null)).Insights);              // prior erased

                // deltas AFTER the purge marker survive.
                var after = await e.AddInsightAsync("post-purge", new[] { "c" }, "insight", "project", false, "agent");
                var live = (await e.ListInsightsAsync("project", null)).Insights;
                Assert.Single(live);
                Assert.Equal(after.Id, live[0].Id);
            }
            finally { Cleanup(ws, home); }
        }

        [Fact]
        public async Task Corrupt_line_is_skipped_and_counted_never_bricks_the_store()
        {
            var (e, ws, home) = MakeWs();
            try
            {
                await e.AddInsightAsync("valid", new[] { "k" }, "insight", "project", false, "agent");
                // inject a garbage line mid-file, then a valid op via the API.
                var file = Path.Combine(ws, ".semanticus", "knowledge", "insights.jsonl");
                File.AppendAllText(file, "this is not json\n");
                await e.AddInsightAsync("second", new[] { "k2" }, "insight", "project", false, "agent");

                var listed = await e.ListInsightsAsync("project", null);
                Assert.Equal(2, listed.Insights.Length);            // both valid records replayed
                Assert.Equal(1, listed.SkippedCorruptLines);        // the garbage line surfaced, not hidden
                Assert.NotNull(listed.Note);
            }
            finally { Cleanup(ws, home); }
        }

        // ---- write-gate (auto-approve setting) ----

        [Fact]
        public async Task Auto_approve_default_true_approves_immediately_off_forces_pending()
        {
            var (on, wsOn, homeOn) = MakeWs(autoApprove: true);
            try { Assert.Equal("approved", (await on.AddInsightAsync("x", null, "insight", "project", false, "agent")).Status); }
            finally { Cleanup(wsOn, homeOn); }

            var (off, wsOff, homeOff) = MakeWs(autoApprove: false);
            try { Assert.Equal("pending", (await off.AddInsightAsync("x", null, "insight", "project", false, "agent")).Status); }
            finally { Cleanup(wsOff, homeOff); }
        }

        // ---- fingerprint determinism + domain tokens ----

        [Fact]
        public async Task Fingerprint_is_deterministic_and_extracts_domain_tokens()
        {
            var e1 = new LocalEngine(new SessionManager(), new Fake(true));
            await e1.OpenAsync(TestModels.FindBim());
            var fp1 = await e1.GetModelFingerprintAsync();

            var e2 = new LocalEngine(new SessionManager(), new Fake(true));
            await e2.OpenAsync(TestModels.FindBim());
            var fp2 = await e2.GetModelFingerprintAsync();

            Assert.Equal(fp1.FingerprintKey, fp2.FingerprintKey);      // same model → same key
            Assert.Equal(fp1.NamingHash, fp2.NamingHash);
            Assert.True(fp1.Tables > 0 && fp1.Columns > 0);
            Assert.NotEmpty(fp1.DomainTokens);                          // tokens extracted from table+measure names
            Assert.All(fp1.DomainTokens, t => Assert.True(t.Length > 3 && t == t.ToLowerInvariant()));
        }

        // ---- recall (L2) ----

        [Fact]
        public async Task Recall_needs_a_model_and_reports_no_prior_experience_when_empty()
        {
            // no session → recall requires a model
            var (bare, ws, home) = MakeWs();
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => bare.RecallExperienceAsync(null, 12)); }
            finally { Cleanup(ws, home); }

            var (e, dir, home2) = await MakeModelAsync();
            try
            {
                var empty = await e.RecallExperienceAsync("anything", 12);
                Assert.Empty(empty.Candidates);
                Assert.NotNull(empty.Fingerprint);
                Assert.Contains("No prior experience", empty.Note);
            }
            finally { Cleanup(dir, home2); }
        }

        [Fact]
        public async Task Recall_key_overlap_beats_recency_and_bumps_retrieval_counters()
        {
            var (e, dir, home) = await MakeModelAsync();
            try
            {
                // the RELEVANT insight matches the query keys; the DISTRACTOR is added LAST (more recent) but off-topic.
                var relevant = await e.AddInsightAsync("Prefer SUMX for row-context aggregation", new[] { "SUMX", "row-context" }, "insight", "project", false, "agent");
                await e.AddInsightAsync("Unrelated note", new[] { "colour", "theme" }, "insight", "project", false, "agent");

                var r = await e.RecallExperienceAsync("how do I use sumx for aggregation", 12);
                Assert.NotEmpty(r.Candidates);
                Assert.Equal(relevant.Id, r.Candidates[0].Insight.Id);         // key overlap wins over recency
                Assert.Contains("SUMX", r.Candidates[0].MatchedKeys);
                Assert.Contains("Deterministic", r.RankingNote);

                // each returned insight got a retrieve delta → counter incremented on the live record.
                var listed = await e.ListInsightsAsync("project", null);
                Assert.True(listed.Insights.Single(i => i.Id == relevant.Id).Retrievals >= 1);
            }
            finally { Cleanup(dir, home); }
        }

        [Fact]
        public async Task Recall_applies_the_fingerprint_bonus_and_merges_both_scopes()
        {
            var (e, dir, home) = await MakeModelAsync();
            try
            {
                // a GLOBAL insight (travels) + a PROJECT fingerprint-scoped insight (this model's shape), same keys.
                var global = await e.AddInsightAsync("global lesson", new[] { "zzz" }, "insight", "global", false, "agent");
                var scoped = await e.AddInsightAsync("shape lesson", new[] { "zzz" }, "insight", "project", fingerprintScoped: true, "agent");

                var r = await e.RecallExperienceAsync(null, 12);
                var ids = r.Candidates.Select(c => c.Insight.Id).ToArray();
                Assert.Contains(global.Id, ids);                                 // both scopes merged
                Assert.Contains(scoped.Id, ids);
                // the fingerprint-scoped one carries the strong bonus → it outranks the equal-keyed global one.
                Assert.True(Array.IndexOf(ids, scoped.Id) < Array.IndexOf(ids, global.Id));
                Assert.True(r.Candidates.Single(c => c.Insight.Id == scoped.Id).FingerprintMatch);
            }
            finally { Cleanup(dir, home); }
        }

        // ---- ephemeral (live-XMLA) + no-workspace fallback: project insights borrow the global knowledge DIR but must
        //      stay a DISTINCT, per-model file so a project purge can't erase global memory (the P1) and scope stays honest.

        // The live-XMLA-no-folder case: an ephemeral (semanticus-*) model open, NO workspace, USERPROFILE redirected so the
        // global knowledge dir is a disposable temp home. Caller restores + deletes via CleanupEph.
        private static async Task<(LocalEngine engine, string snapDir, string home, string origHome)> MakeEphemeralNoWorkspaceAsync()
        {
            var snapDir = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapDir);
            var bim = Path.Combine(snapDir, "Model.bim");
            File.Copy(TestModels.FindBim(), bim);
            Assert.True(ExperienceStore.IsEphemeralAnchor(bim));
            var home = Path.Combine(Path.GetTempPath(), "smx-know-eph-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(home);
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            var e = new LocalEngine(new SessionManager(), new Fake(true));   // NO workspaceDir → ProjectFallsBackToGlobal
            await e.OpenAsync(bim);
            return (e, snapDir, home, origHome);
        }

        private static void CleanupEph(string snapDir, string home, string origHome)
        {
            Environment.SetEnvironmentVariable("USERPROFILE", origHome);
            try { Directory.Delete(home, true); } catch { }
            try { Directory.Delete(snapDir, true); } catch { }
        }

        [Fact]
        public async Task Ephemeral_no_workspace_project_purge_does_not_erase_global_insights()
        {
            // P1 regression. The project store borrows the global knowledge DIR here. If it SHARED the global insights.jsonl,
            // a `purge` marker (which erases the whole file on replay) would wipe the user's unrelated GLOBAL memory. Distinct
            // per-scope files must keep a project purge from touching global insights.
            var (e, snapDir, home, origHome) = await MakeEphemeralNoWorkspaceAsync();
            try
            {
                var global = await e.AddInsightAsync("Global pattern that travels.", new[] { "GLOBAL" }, "insight", "global", false, "agent");
                var project = await e.AddInsightAsync("Project note for this model.", new[] { "PROJECT" }, "insight", "project", false, "agent");

                var knowledgeDir = Path.Combine(home, ".semanticus", "knowledge");
                var globalFile = Path.Combine(knowledgeDir, "insights.jsonl");
                Assert.True(File.Exists(globalFile), "global insight lands in the shared global insights.jsonl");
                var projectFiles = Directory.GetFiles(knowledgeDir, "insights.project.*.jsonl");
                Assert.Single(projectFiles);                                    // project store is a DISTINCT, scope-private file
                Assert.NotEqual(globalFile, projectFiles[0]);

                var purge = await e.PurgeKnowledgeAsync("project", confirm: true, "agent");
                Assert.True(purge.Purged);

                var listed = await e.ListInsightsAsync(null, null);
                Assert.Contains(listed.Insights, i => i.Id == global.Id);       // GLOBAL memory INTACT — the P1 guarantee
                Assert.DoesNotContain(listed.Insights, i => i.Id == project.Id);// project store purged as asked
            }
            finally { CleanupEph(snapDir, home, origHome); }
        }

        [Fact]
        public async Task Ephemeral_no_workspace_keeps_scope_labels_honest_across_the_two_files()
        {
            // Scope is not stored per record — the FILE is the scope. Distinct project/global files must keep list + scope
            // filters honest (a global insight never reads back as project, and vice-versa).
            var (e, snapDir, home, origHome) = await MakeEphemeralNoWorkspaceAsync();
            try
            {
                var g = await e.AddInsightAsync("global one", new[] { "G" }, "insight", "global", false, "agent");
                var p = await e.AddInsightAsync("project one", new[] { "P" }, "insight", "project", false, "agent");

                var listed = await e.ListInsightsAsync(null, null);
                Assert.Equal("global", listed.Insights.Single(i => i.Id == g.Id).Scope);
                Assert.Equal("project", listed.Insights.Single(i => i.Id == p.Id).Scope);

                var projOnly = await e.ListInsightsAsync("project", null);
                Assert.Contains(projOnly.Insights, i => i.Id == p.Id);
                Assert.DoesNotContain(projOnly.Insights, i => i.Id == g.Id);

                var globalOnly = await e.ListInsightsAsync("global", null);
                Assert.Contains(globalOnly.Insights, i => i.Id == g.Id);
                Assert.DoesNotContain(globalOnly.Insights, i => i.Id == p.Id);
            }
            finally { CleanupEph(snapDir, home, origHome); }
        }

        [Fact]
        public async Task Ephemeral_no_workspace_project_insight_is_isolated_to_its_own_model()
        {
            // A fingerprint-scoped project insight added for one model is recallable for THAT model, but a different model's
            // session (a different per-model project file) never sees it — isolation comes from the per-model file, so it
            // holds even though both fixtures share the same semantic fingerprint (a shared file would have leaked it).
            var home = Path.Combine(Path.GetTempPath(), "smx-know-fp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(home);
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            var snapA = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            var snapB = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(snapA); Directory.CreateDirectory(snapB);
                var bimA = Path.Combine(snapA, "Model.bim"); File.Copy(TestModels.FindBim(), bimA);
                var bimB = Path.Combine(snapB, "Model.bim"); File.Copy(TestModels.FindBim(), bimB);

                var eA = new LocalEngine(new SessionManager(), new Fake(true));
                await eA.OpenAsync(bimA);
                var added = await eA.AddInsightAsync("Model A project lesson about divide.", new[] { "DIVIDE" }, "insight", "project", fingerprintScoped: true, "agent");
                var recallA = await eA.RecallExperienceAsync("divide", 12);
                Assert.Contains(recallA.Candidates, c => c.Insight.Id == added.Id);       // recallable for its OWN model

                var eB = new LocalEngine(new SessionManager(), new Fake(true));
                await eB.OpenAsync(bimB);
                var recallB = await eB.RecallExperienceAsync("divide", 12);
                Assert.DoesNotContain(recallB.Candidates, c => c.Insight.Id == added.Id); // another model's session never sees it
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(home, true); } catch { }
                try { Directory.Delete(snapA, true); } catch { }
                try { Directory.Delete(snapB, true); } catch { }
            }
        }

        // ---- provenance required-fields ----

        [Fact]
        public async Task Every_insight_carries_a_provenance_envelope()
        {
            var (e, ws, home) = MakeWs();
            try
            {
                var a = await e.AddInsightAsync("lesson", new[] { "k" }, "insight", "project", false, "human");
                Assert.NotNull(a.Provenance);
                Assert.False(string.IsNullOrEmpty(a.Provenance.When));
                Assert.Equal("human", a.Provenance.Origin);
                Assert.Equal(1, a.Provenance.SchemaVersion);
                Assert.NotNull(a.Provenance.SourceRunIds);
            }
            finally { Cleanup(ws, home); }
        }
    }
}
