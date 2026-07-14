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
    }
}
