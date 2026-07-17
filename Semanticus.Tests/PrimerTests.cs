using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PrimerTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info => new EntitlementInfo { Tier = IsPro ? "pro" : "free" };
            public Fake(bool pro) { IsPro = pro; }
        }

        [Fact]
        public void Template_has_the_six_fixed_sections_in_order()
        {
            var markdown = PrimerContract.Template("Contoso");
            PrimerContract.Validate(markdown);
            var at = -1;
            foreach (var section in PrimerContract.Sections)
            {
                var next = markdown.IndexOf("## " + section, StringComparison.Ordinal);
                Assert.True(next > at);
                at = next;
            }
        }

        [Fact]
        public async Task Primer_is_plain_markdown_and_never_crosses_models_in_one_folder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "A.bim");
            var b = Path.Combine(dir, "B.bim");
            File.Copy(TestModels.FindBim(), a);
            File.Copy(TestModels.FindBim(), b);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                await engine.OpenAsync(a);
                var starter = await engine.GetPrimerAsync();
                Assert.False(starter.Exists);
                var markdown = starter.Markdown.Replace("_Add what people and the AI Assistant should know._", "Contoso-specific context");
                var saved = await engine.SetPrimerAsync(markdown, "human");
                Assert.True(saved.Exists);
                Assert.True(File.Exists(saved.FilePath));
                Assert.Equal(markdown.Replace("\r\n", "\n"), await File.ReadAllTextAsync(saved.FilePath));

                await engine.OpenAsync(b);
                var other = await engine.GetPrimerAsync();
                Assert.False(other.Exists);
                Assert.NotEqual(saved.FilePath, other.FilePath);
                Assert.DoesNotContain("Contoso-specific context", other.Markdown);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Set_model_primer_public_doors_share_the_Free_sidecar_write_and_fail_atomically()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-public-doors", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false));
                var rpc = new EngineRpcTarget(engine);
                await engine.OpenAsync(model);
                var revision = sessions.Current.Revision;
                var activities = new List<ActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.Activity += activities.Add;
                sessions.Bus.Changed += _ => modelChanges++;

                var agentMarkdown = PrimerContract.Template("Public Primer")
                    .Replace("_Add what people and the AI Assistant should know._", "Agent-reviewed context")
                    .Replace("\n", "\r\n");
                var agentSaved = await McpTools.SetModelPrimer(engine, agentMarkdown);

                Assert.True(agentSaved.Exists);
                Assert.Equal(agentMarkdown.Replace("\r\n", "\n"), agentSaved.Markdown);
                Assert.Equal(agentSaved.Markdown, await File.ReadAllTextAsync(agentSaved.FilePath));
                var agentActivity = Assert.Single(activities);
                Assert.Equal("set_model_primer", agentActivity.Kind);
                Assert.Equal("agent", agentActivity.Origin);
                Assert.Equal(sessions.Current.Id, agentActivity.Target);
                Assert.True(agentActivity.Ok);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(0, modelChanges);

                activities.Clear();
                var humanMarkdown = agentSaved.Markdown.Replace("Agent-reviewed context", "Human-reviewed context");
                var humanSaved = await rpc.setPrimer(humanMarkdown);

                Assert.Equal(humanMarkdown, humanSaved.Markdown);
                var humanActivity = Assert.Single(activities);
                Assert.Equal("set_model_primer", humanActivity.Kind);
                Assert.Equal("human", humanActivity.Origin);
                Assert.Equal(sessions.Current.Id, humanActivity.Target);
                Assert.True(humanActivity.Ok);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(0, modelChanges);

                activities.Clear();
                var beforeFailure = await File.ReadAllTextAsync(humanSaved.FilePath);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.SetModelPrimer(engine, "# Broken Primer\n\n## Overview\n"));

                Assert.Contains("exactly these six sections", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(beforeFailure, await File.ReadAllTextAsync(humanSaved.FilePath));
                Assert.Empty(activities);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(0, modelChanges);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Missing_or_reordered_sections_are_refused()
        {
            var markdown = PrimerContract.Template("Contoso").Replace("## Gotchas", "## Traps");
            var ex = Assert.Throws<InvalidOperationException>(() => PrimerContract.Validate(markdown));
            Assert.Contains("Gotchas", ex.Message);
        }

        [Fact]
        public async Task Reviewed_model_scoped_learning_needs_human_acceptance_before_it_changes_the_Primer()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-suggestions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                var insight = await engine.AddInsightAsync(
                    "Month-end stock can lag the sales snapshot by one refresh cycle.",
                    new[] { "refresh", "primer:Gotchas" }, "post-mortem", "project", true, "agent");

                var before = await engine.GetPrimerAsync();
                var waiting = await engine.ListPrimerSuggestionsAsync();
                var suggestion = Assert.Single(waiting.Suggestions);
                Assert.Equal(insight.Id, suggestion.Id);
                Assert.Equal("Gotchas", suggestion.Section);
                Assert.DoesNotContain("Month-end stock", before.Markdown);
                Assert.Contains("captured learning", suggestion.Provenance);

                var accepted = await engine.AcceptPrimerSuggestionAsync(suggestion.Id, "human");
                Assert.True(accepted.Changed);
                Assert.Contains("Month-end stock", accepted.Primer.Markdown);
                Assert.Contains("Provenance:", accepted.Primer.Markdown);
                Assert.Empty((await engine.ListPrimerSuggestionsAsync()).Suggestions);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Rejected_suggestions_stay_dismissed_and_free_users_keep_manual_editing()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-reject", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using (var pro = new LocalEngine(new SessionManager(), new Fake(true)))
                {
                    await pro.OpenAsync(model);
                    var insight = await pro.AddInsightAsync("Use the approved fiscal calendar.", new[] { "primer:Patterns" }, "insight", "project", true, "agent");
                    var rejected = await pro.RejectPrimerSuggestionAsync(insight.Id, "human");
                    Assert.Equal("rejected", rejected.Decision);
                    Assert.False(rejected.Changed);
                    Assert.Empty((await pro.ListPrimerSuggestionsAsync()).Suggestions);
                    Assert.DoesNotContain("approved fiscal calendar", (await pro.GetPrimerAsync()).Markdown);
                }

                using var free = new LocalEngine(new SessionManager(), new Fake(false));
                await free.OpenAsync(model);
                var gated = await free.ListPrimerSuggestionsAsync();
                Assert.False(gated.IsPro);
                Assert.Empty(gated.Suggestions);
                var manual = await free.GetPrimerAsync();
                var saved = await free.SetPrimerAsync(manual.Markdown.Replace("_Add what people and the AI Assistant should know._", "Manual context"), "human");
                Assert.Contains("Manual context", saved.Markdown);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ============================================================================================
        // Local Power BI Desktop identity: the endpoint (localhost:port) AND database (a per-session
        // GUID) both rotate every Desktop restart, so the old endpoint|database key orphaned the primer
        // each reload. The key now rests on the Desktop file's DISPLAY NAME, captured once at open time
        // and stamped as LiveOrigin.LocalName. These tests simulate open_local's honest session shape:
        // an EPHEMERAL anchor (created-from-scratch model = the temp snapshot), the WORKSPACE sidecar,
        // and a FRESH engine + SessionManager per "Desktop session".
        // ============================================================================================

        private static string Sha8(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)), 0, 8).ToLowerInvariant();
        }

        // A VALID provenance stamp for an existing primer file (its ContentHash vouches for the exact current
        // content) — how tests construct copied-sidecar / claim-transition states the engine itself never writes.
        private static async Task StampAsync(string primerFile, string pbixPath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(primerFile)))).ToLowerInvariant();
            var json = System.Text.Json.JsonSerializer.Serialize(new { PbixPath = pbixPath, Stem = "Sales", KeySource = pbixPath == null ? "title" : "path", ContentHash = hash });
            await File.WriteAllTextAsync(Path.ChangeExtension(primerFile, ".identity.json"), json);
        }

        [Fact]
        public async Task Local_primer_survives_a_desktop_restart_that_rotates_endpoint_and_database()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-restart", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                string savedPath, savedMarkdown;
                // Desktop session 1.
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("Model", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Contoso Sales");
                    var starter = await engine.GetPrimerAsync();
                    Assert.False(starter.Exists);
                    var saved = await engine.SetPrimerAsync(
                        starter.Markdown.Replace("_Add what people and the AI Assistant should know._", "Local-desktop context"), "human");
                    Assert.True(saved.Exists);
                    savedPath = saved.FilePath;
                    savedMarkdown = saved.Markdown;
                    Assert.StartsWith("local-", Path.GetFileName(saved.FilePath));   // the name-keyed lane, not endpoint|database
                }
                // Desktop restart: fresh engine + session, BOTH coordinates rotated, same .pbix display name.
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("Model", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:52002", Guid.NewGuid().ToString(), null, localName: "Contoso Sales");
                    var reread = await engine.GetPrimerAsync();
                    Assert.True(reread.Exists);
                    Assert.Equal(savedMarkdown, reread.Markdown);
                    Assert.Equal(savedPath, reread.FilePath);          // the SAME file, not a new orphan
                    Assert.Contains("Local-desktop context", reread.Markdown);
                }
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Two_local_models_in_one_workspace_never_share_or_steal_a_primer()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-two-locals", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("Sales", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales Report");
                var sales = await engine.SetPrimerAsync(
                    PrimerContract.Template("Sales").Replace("_Add what people and the AI Assistant should know._", "Sales-only context"), "human");

                // A second Desktop model opened from the same workspace: distinct name, distinct key.
                await engine.CreateModelAsync("Finance", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51002", Guid.NewGuid().ToString(), null, localName: "Finance Report");
                var finance = await engine.GetPrimerAsync();
                Assert.False(finance.Exists);                          // never shows the other model's primer
                Assert.NotEqual(sales.FilePath, finance.FilePath);
                Assert.DoesNotContain("Sales-only context", finance.Markdown);
                Assert.True(File.Exists(sales.FilePath));              // and never steals (moves/renames) its file
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_structural_edit_between_set_and_get_does_not_move_the_primer()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-edit", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                await engine.CreateTableAsync("Sales", "human");
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Contoso Sales");
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Survives edits"), "human");

                // A structural edit changes the model's SHAPE (the reason a content fingerprint was rejected as the
                // identity) — the primer must not move.
                await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");

                var after = await engine.GetPrimerAsync();
                Assert.True(after.Exists);
                Assert.Equal(saved.FilePath, after.FilePath);
                Assert.Contains("Survives edits", after.Markdown);
            }
            finally { Directory.Delete(ws, true); }
        }

        // Regression pin: the cloud XMLA and loopback-SSAS keys are byte-identical to the pre-change scheme
        // ("live-" + sha8(lower(endpoint) + "|" + lower(database)) + ".md"). A real SSAS on localhost carries no
        // Desktop name, so it KEEPS its perfectly stable coordinate key; only stamped Desktop opens use local-*.
        [Fact]
        public async Task Cloud_and_loopback_ssas_primer_keys_are_byte_identical_to_the_coordinate_scheme()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-cloudpin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);

                await engine.CreateModelAsync("Cloud", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/WS", "SalesDW", "tenant");
                var cloud = await engine.GetPrimerAsync();
                Assert.Equal("live-" + Sha8("powerbi://api.powerbi.com/v1.0/myorg/ws" + "|" + "salesdw") + ".md",
                    Path.GetFileName(cloud.FilePath));

                await engine.CreateModelAsync("Ssas", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:2383", "AdventureWorks", null);   // loopback, NO Desktop name
                var ssas = await engine.GetPrimerAsync();
                Assert.Equal("live-" + Sha8("localhost:2383" + "|" + "adventureworks") + ".md",
                    Path.GetFileName(ssas.FilePath));
            }
            finally { Directory.Delete(ws, true); }
        }

        // Disk keys: unchanged code path — pin the shape and the cross-process stability (same file, same key).
        [Fact]
        public async Task Disk_primer_key_is_stable_across_engine_instances()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-diskpin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                string first;
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false)))
                {
                    await engine.OpenAsync(model);
                    first = (await engine.GetPrimerAsync()).FilePath;
                }
                Assert.Matches("^disk-[0-9a-f]{16}\\.md$", Path.GetFileName(first));
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false)))
                {
                    await engine.OpenAsync(model);
                    Assert.Equal(first, (await engine.GetPrimerAsync()).FilePath);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        // Pre-fix local primers (endpoint|database keyed, live-*.md) are SURFACED, never silently adopted: a legacy
        // file names no model, a shared workspace sidecar can hold another (even a cloud) model's primer, and two
        // engines racing an adopt-by-rename would be nondeterministic. The user re-saves to migrate.
        [Fact]
        public async Task Legacy_endpoint_keyed_primer_surfaces_a_note_and_is_never_claimed()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-legacy", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Contoso Sales");

                var before = await engine.GetPrimerAsync();
                Assert.False(before.Exists);
                Assert.Null(before.Note);                              // no legacy files yet: no note

                var primersDir = Path.Combine(ws, ".semanticus", "primers");
                Directory.CreateDirectory(primersDir);
                var legacy = Path.Combine(primersDir, "live-deadbeefcafe1234.md");
                await File.WriteAllTextAsync(legacy, PrimerContract.Template("Legacy").Replace("\r\n", "\n"));

                var after = await engine.GetPrimerAsync();
                Assert.False(after.Exists);                            // never auto-claimed
                Assert.Contains("earlier version", after.Note);        // surfaced honestly instead
                Assert.Contains("set_model_primer", after.Note);
                Assert.True(File.Exists(legacy));                      // and the legacy file is untouched
                Assert.NotEqual(legacy, after.FilePath);

                // A CLOUD session must not get the note: live-*.md IS its current key shape, not a legacy artifact.
                await engine.CreateModelAsync("Cloud", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/WS", "SalesDW", "tenant");
                var cloud = await engine.GetPrimerAsync();
                Assert.False(cloud.Exists);
                Assert.Null(cloud.Note);
            }
            finally { Directory.Delete(ws, true); }
        }

        // ============================================================================================
        // The PATH rung of the local identity ladder: the full .pbix path (captured from the owning
        // Desktop's command line when the file was opened by double-click/shell) is the strongest
        // identity — distinct across same-named files, stable across restarts. The stem rung's
        // same-name collision is armored by a provenance stamp beside the artifact: reads fail closed
        // on a PROVEN mismatch, and an unprovable same-stem file is flagged, never silently served.
        // Residual (disclosed): two same-stem models with NO recoverable path on either side are
        // indistinguishable — nothing ties either file to either model.
        // ============================================================================================

        [Fact]
        public async Task Path_keyed_local_primer_survives_a_desktop_restart()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-pathrestart", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                var pbix = @"C:\data\reports\Contoso Sales.pbix";
                string savedPath, savedMarkdown;
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("Model", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Contoso Sales", localPath: pbix);
                    var saved = await engine.SetPrimerAsync(
                        PrimerContract.Template("Model").Replace("_Add what people and the AI Assistant should know._", "Path-keyed context"), "human");
                    savedPath = saved.FilePath;
                    savedMarkdown = saved.Markdown;
                    Assert.StartsWith("local-", Path.GetFileName(saved.FilePath));
                }
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("Model", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:52002", Guid.NewGuid().ToString(), null, localName: "Contoso Sales", localPath: pbix);
                    var reread = await engine.GetPrimerAsync();
                    Assert.True(reread.Exists);
                    Assert.Equal(savedMarkdown, reread.Markdown);
                    Assert.Equal(savedPath, reread.FilePath);
                }
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Same_stem_models_with_known_paths_get_distinct_keys()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-samestem", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("A", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: @"C:\region-a\Sales.pbix");
                var a = await engine.SetPrimerAsync(
                    PrimerContract.Template("A").Replace("_Add what people and the AI Assistant should know._", "Region A context"), "human");

                await engine.CreateModelAsync("B", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51002", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: @"C:\region-b\Sales.pbix");
                var b = await engine.GetPrimerAsync();
                Assert.False(b.Exists);
                Assert.NotEqual(a.FilePath, b.FilePath);          // the path key separates same-named files
                Assert.DoesNotContain("Region A context", b.Markdown);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Unprovable_same_stem_primer_is_flagged_never_served()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-unprovable", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                // Session 1: stem only (no path capturable) — writes the stem-keyed file, stamped WITHOUT a path.
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("M", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                    await engine.SetPrimerAsync(
                        PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Stem-era context"), "human");
                }
                // Session 2: same stem, path now KNOWN — path-keyed. The stem file cannot be verified as this
                // model's (its stamp has no path), so it is flagged honestly and NOT served.
                using (var sessions = new SessionManager())
                using (var engine = new LocalEngine(sessions, new Fake(false), ws))
                {
                    await engine.CreateModelAsync("M", 1604);
                    sessions.Current.LiveOrigin = new LiveOrigin("localhost:52002", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: @"C:\data\Sales.pbix");
                    var doc = await engine.GetPrimerAsync();
                    Assert.False(doc.Exists);
                    Assert.DoesNotContain("Stem-era context", doc.Markdown);
                    Assert.Contains("cannot be verified", doc.Note);
                    Assert.Contains("save its content again", doc.Note);
                }
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Stem_file_stamped_for_a_different_pbix_is_foreign_and_silently_ignored()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-foreigntwin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                // The OTHER model's sidecar state: a stem-keyed primer stamped with ITS OWN .pbix path (as a
                // sidecar copied between machines would carry). Written by a first session so key + stamp shape
                // are the engine's own, then the stamp's path is what proves foreignness.
                await engine.CreateModelAsync("Other", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var other = await engine.SetPrimerAsync(
                    PrimerContract.Template("Other").Replace("_Add what people and the AI Assistant should know._", "Other model's context"), "human");
                await StampAsync(other.FilePath, @"C:\elsewhere\Sales.pbix");

                await engine.CreateModelAsync("Mine", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51002", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: @"C:\mine\Sales.pbix");
                var doc = await engine.GetPrimerAsync();
                Assert.False(doc.Exists);
                Assert.DoesNotContain("Other model's context", doc.Markdown);   // proven-foreign: never served
                Assert.Null(doc.Note);                                          // and not offered either
                Assert.True(File.Exists(other.FilePath));                       // the other model's file untouched
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Stem_file_stamped_with_this_models_path_is_served_with_a_migrate_note()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-twinserve", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                var pbix = @"C:\data\Sales.pbix";
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var stemSaved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Twin-claimed context"), "human");
                // The stamp gains the path (a stem-era file positively tied to this .pbix).
                await StampAsync(stemSaved.FilePath, pbix);

                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:52002", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: pbix);
                var doc = await engine.GetPrimerAsync();
                Assert.True(doc.Exists);                                       // provably this model's: served
                Assert.Contains("Twin-claimed context", doc.Markdown);
                Assert.Equal(stemSaved.FilePath, doc.FilePath);
                Assert.Contains("Save it again", doc.Note);                    // and the migrate nudge
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_primary_file_stamped_for_a_different_pbix_fails_closed()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-primarymismatch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                var pbix = @"C:\mine\Sales.pbix";
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: pbix);
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Original context"), "human");

                // The stamp now claims another .pbix (a copied/restored sidecar): fail closed, show the truth.
                await StampAsync(saved.FilePath, @"C:\other\Sales.pbix");
                var doc = await engine.GetPrimerAsync();
                Assert.False(doc.Exists);
                Assert.DoesNotContain("Original context", doc.Markdown);
                Assert.Contains("different model file", doc.Note);
                Assert.Contains("Saving this model's Primer will replace it", doc.Note);

                // Re-saving claims the key back for THIS model.
                var reclaimed = await engine.SetPrimerAsync(saved.Markdown, "human");
                Assert.True(reclaimed.Exists);
                Assert.True((await engine.GetPrimerAsync()).Exists);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Pathless_reader_never_serves_a_path_stamped_primer()
        {
            // The stem key is shared; a file POSITIVELY tied to some .pbix must not be served to a session that
            // cannot prove it is that file (dialog-opened Desktop: no path captured). Fail closed with the note
            // that says how to claim it — open the file directly once (the path capture then proves it) or re-save.
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-pathstamped", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Path-claimed context"), "human");
                await StampAsync(saved.FilePath, @"C:\somewhere\Sales.pbix");   // the file becomes path-claimed

                // A fresh pathless session under the same stem: same key, but the claim is unverifiable here.
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:52002", Guid.NewGuid().ToString(), null, localName: "Sales");
                var doc = await engine.GetPrimerAsync();
                Assert.False(doc.Exists);
                Assert.DoesNotContain("Path-claimed context", doc.Markdown);
                Assert.Contains("cannot prove", doc.Note);
                Assert.Contains("double-click", doc.Note);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_torn_stamp_pair_is_unprovable_until_resaved()
        {
            // The stamp vouches for the EXACT content (ContentHash): a crash between the md move and the stamp
            // move, or a mixed sidecar copy, leaves a torn pair that must never be served or authorize anything.
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-tornpair", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Original torn-pair content"), "human");

                // Tear the pair: the md moves on without the stamp (old-stamp/new-md).
                await File.WriteAllTextAsync(saved.FilePath, saved.Markdown.Replace("Original torn-pair content", "Content the stamp never vouched for"));
                var torn = await engine.GetPrimerAsync();
                Assert.False(torn.Exists);
                Assert.DoesNotContain("never vouched for", torn.Markdown);
                Assert.Contains("does not match its content", torn.Note);
                Assert.Contains("save the Primer again", torn.Note);

                // Re-saving writes a fresh md+stamp pair and repairs the state.
                var repaired = await engine.SetPrimerAsync(saved.Markdown, "human");
                Assert.True(repaired.Exists);
                Assert.True((await engine.GetPrimerAsync()).Exists);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Deleting_a_stamp_never_launders_a_stem_keyed_primer_into_service()
        {
            // An Invalid (refused) pair must not become a served one by deleting the stamp: a STEM key without a
            // claim is refused as unclaimed. Re-saving claims it back.
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-launder", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Unclaimed after deletion"), "human");

                File.Delete(Path.ChangeExtension(saved.FilePath, ".identity.json"));
                var doc = await engine.GetPrimerAsync();
                Assert.False(doc.Exists);
                Assert.DoesNotContain("Unclaimed after deletion", doc.Markdown);
                Assert.Contains("stamp was removed", doc.Note);
                Assert.Contains("claim it", doc.Note);

                var reclaimed = await engine.SetPrimerAsync(saved.Markdown, "human");
                Assert.True(reclaimed.Exists);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task An_absent_stamp_on_a_path_keyed_primer_still_serves()
        {
            // The canonical path key is strong evidence on its own: only this .pbix hashes to this key, so a
            // missing stamp does not orphan a path-keyed primer.
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-absentpath", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: @"C:\data\Sales.pbix");
                var saved = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "Path-keyed no-stamp content"), "human");

                File.Delete(Path.ChangeExtension(saved.FilePath, ".identity.json"));
                var doc = await engine.GetPrimerAsync();
                Assert.True(doc.Exists);
                Assert.Contains("Path-keyed no-stamp content", doc.Markdown);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_failed_stamp_write_never_removes_an_existing_stamp()
        {
            // The B-full-write / A-stamp-fail interleaving: engine B's just-written valid stamp is ITS claim; a
            // concurrent save whose stamp write fails must leave it in place (the hash mismatch refuses the torn
            // pair safely), and the save warning is a FIXED string that never carries exception/path details.
            if (!OperatingSystem.IsWindows()) return;   // the forced failure = an exclusive lock making the stamp MOVE fail; POSIX renames over open files, so only Windows can stage it (the no-delete behavior itself is structural)
            var ws = Path.Combine(Path.GetTempPath(), "sem-primer-stampsurvive", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(false), ws);
                await engine.CreateModelAsync("M", 1604);
                sessions.Current.LiveOrigin = new LiveOrigin("localhost:51001", Guid.NewGuid().ToString(), null, localName: "Sales");
                var b = await engine.SetPrimerAsync(
                    PrimerContract.Template("M").Replace("_Add what people and the AI Assistant should know._", "B's content"), "human");
                var stampFile = Path.ChangeExtension(b.FilePath, ".identity.json");
                var bStamp = await File.ReadAllTextAsync(stampFile);

                // Hold B's stamp so the next save's stamp move fails (the md still saves).
                PrimerDocument a;
                using (new FileStream(stampFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    a = await engine.SetPrimerAsync(b.Markdown.Replace("B's content", "A's content"), "human");
                }
                Assert.Contains("provenance stamp could not be written", a.Note);
                Assert.DoesNotContain(ws, a.Note);                      // fixed text only: no exception/path leak
                Assert.Equal(bStamp, await File.ReadAllTextAsync(stampFile));   // B's stamp SURVIVED

                // The torn pair (A md + B stamp) is refused, never served unclaimed; re-saving repairs it.
                var torn = await engine.GetPrimerAsync();
                Assert.False(torn.Exists);
                Assert.Contains("does not match its content", torn.Note);
                var repaired = await engine.SetPrimerAsync(b.Markdown, "human");
                Assert.True(repaired.Exists);
            }
            finally { Directory.Delete(ws, true); }
        }
    }

    // The LocalDesktop DECISION core, tested through the injectable process-info seam (the WMI/Process plumbing is
    // integration-only). Pins: proof-beats-fallback, the exactly-one-PROCESS fallback rule (one process, not one
    // readable title), the fail-closed title-suffix policy, and command-line path extraction.
    public sealed class LocalDesktopTests
    {
        private static LocalDesktop.ProcInfo Proc(int pid, string name, string title = null, string cmd = null, int ppid = 0)
            => new LocalDesktop.ProcInfo { Pid = pid, ParentPid = ppid, Name = name, Title = title, CommandLine = cmd };

        [Fact]
        public void Owner_proof_selects_the_workspaces_own_desktop_among_several()
        {
            var ws = @"C:\Users\u\AppData\Local\Microsoft\Power BI Desktop\AnalysisServicesWorkspaces\AnalysisServicesWorkspace_guid1";
            var procs = new[]
            {
                Proc(10, "PBIDesktop.exe", "Contoso 10M", "\"C:\\pbi\\PBIDesktop.exe\""),
                Proc(20, "PBIDesktop.exe", "Contoso 10M-2", "\"C:\\pbi\\PBIDesktop.exe\" \"C:\\data\\Contoso 10M-2.pbix\""),
                Proc(30, "msmdsrv.exe", null, "\"C:\\pbi\\msmdsrv.exe\" -s \"" + ws + "\\Data\"", ppid: 20),
            };
            var id = LocalDesktop.Resolve(ws, procs);
            Assert.Equal(@"C:\data\Contoso 10M-2.pbix", id.PbixPath);
            Assert.Equal("Contoso 10M-2", id.Stem);
        }

        [Fact]
        public void A_proven_non_desktop_owner_yields_null_even_with_one_desktop_running()
        {
            // The workspace's owner is devenv (SSDT) — guessing past a PROVEN non-Desktop owner would bind an
            // unrelated Desktop's model to it.
            var ws = @"C:\ws\AnalysisServicesWorkspace_guid1";
            var procs = new[]
            {
                Proc(10, "devenv.exe", "Solution1"),
                Proc(20, "PBIDesktop.exe", "Contoso 10M"),
                Proc(30, "msmdsrv.exe", null, "-s \"" + ws + "\\Data\"", ppid: 10),
            };
            Assert.Null(LocalDesktop.Resolve(ws, procs));
        }

        [Fact]
        public void No_proven_owner_yields_null_there_is_no_sole_desktop_fallback()
        {
            // A "sole Desktop on the machine" heuristic is not ownership proof: a stale port file can alias a
            // reused port onto an unrelated Desktop. No msmdsrv claims the workspace ⇒ null, no matter how many
            // (or few) Desktops are running or how readable they are.
            var ws = @"C:\ws\AnalysisServicesWorkspace_guid1";
            Assert.Null(LocalDesktop.Resolve(ws, new[]
            {
                Proc(10, "PBIDesktop.exe", null),
                Proc(20, "PBIDesktop.exe", "Contoso 10M"),
            }));
            Assert.Null(LocalDesktop.Resolve(ws, new[]
            {
                Proc(10, "PBIDesktop.exe", "Contoso 10M", "\"C:\\pbi\\PBIDesktop.exe\" \"C:\\d\\Contoso 10M.pbix\""),
            }));
            Assert.Null(LocalDesktop.Resolve(ws, System.Array.Empty<LocalDesktop.ProcInfo>()));   // WMI unavailable = empty snapshot
        }

        [Fact]
        public void Pbix_path_extraction_handles_quoted_unquoted_relative_and_absent()
        {
            Assert.Equal(@"C:\data files\Contoso 10M.pbix",
                LocalDesktop.ExtractPbixPath("\"C:\\pbi\\PBIDesktop.exe\" \"C:\\data files\\Contoso 10M.pbix\""));
            Assert.Equal(@"C:\d\model.pbix", LocalDesktop.ExtractPbixPath(@"C:\pbi\PBIDesktop.exe C:\d\model.pbix"));
            Assert.Null(LocalDesktop.ExtractPbixPath("\"C:\\pbi\\PBIDesktop.exe\" "));   // dialog-opened: bare exe
            Assert.Null(LocalDesktop.ExtractPbixPath(null));
            // Relative arguments are NEVER an identity: they would resolve against OUR cwd, not the model's location.
            Assert.Null(LocalDesktop.ExtractPbixPath("\"C:\\pbi\\PBIDesktop.exe\" \"model.pbix\""));
            Assert.Null(LocalDesktop.ExtractPbixPath(@"C:\pbi\PBIDesktop.exe .\reports\model.pbix"));
        }

        [Fact]
        public void Title_policy_strips_known_suffixes_and_fails_closed_on_unknown_dash_tails()
        {
            Assert.Equal("Contoso 10M", LocalDesktop.CleanTitle("Contoso 10M"));
            Assert.Equal("Sales - EU", LocalDesktop.CleanTitle("Sales - EU - Power BI Desktop"));   // dash-preserving stem
            Assert.Equal("Contoso", LocalDesktop.CleanTitle("Contoso - Power BI Designer"));
            Assert.Null(LocalDesktop.CleanTitle("Sales - EU"));                          // unknown tail: uncapturable
            Assert.Null(LocalDesktop.CleanTitle("Ventes - Power BI Desktop (Preview)")); // localized/edition variant
            Assert.Null(LocalDesktop.CleanTitle(" - Power BI Desktop"));                 // empty stem
            Assert.Null(LocalDesktop.CleanTitle("  "));
        }
    }
}
