using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>C7.3: tool answers are plain and complete. Each fact is a UAT defect (D-125, D-133, D-134,
    /// D-135, D-136, D-139, D-140, D-141).</summary>
    public sealed class McpAnswerContractTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info => new EntitlementInfo { Tier = IsPro ? "pro" : "free" };
            public Fake(bool pro) { IsPro = pro; }
        }

        private static string CopyBim(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "S.bim");
            File.Copy(TestModels.FindBim(), model);
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        // D-125: save_model with path null (and the TMDL default) must overwrite a .bim source, not refuse
        // because the file already exists. No arguments must not leak framework text.
        [Fact]
        public async Task Save_with_null_path_overwrites_a_bim_source_even_when_format_defaults_to_tmdl()
        {
            var dir = CopyBim("sem-save-overwrite-");
            var model = Path.Combine(dir, "S.bim");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                var table = (await engine.ListMeasuresAsync()).First().Table;
                await engine.CreateMeasureAsync("table:" + table, "SaveProbe", "1", "human");

                var saved = await engine.SaveAsync(null, "TMDL");
                Assert.True(File.Exists(model), "The .bim source must still be a file, not replaced by a TMDL folder.");
                Assert.False(Directory.Exists(model));
                Assert.Equal(Path.GetFullPath(model), saved.Path);
                Assert.DoesNotContain("already exists", saved.Path, StringComparison.OrdinalIgnoreCase);

                using var reopen = new LocalEngine(new SessionManager(), new Fake(true));
                await reopen.OpenAsync(model);
                Assert.Contains(await reopen.ListMeasuresAsync(), m => m.Name == "SaveProbe");
            }
            finally { DeleteTree(dir); }
        }

        [Fact]
        public async Task Save_with_no_path_and_no_format_overwrites_the_open_bim()
        {
            var dir = CopyBim("sem-save-noargs-");
            var model = Path.Combine(dir, "S.bim");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                var table = (await engine.ListMeasuresAsync()).First().Table;
                await engine.CreateMeasureAsync("table:" + table, "NoArgsProbe", "2", "human");
                var saved = await McpTools.SaveModel(engine);
                Assert.True(File.Exists(model));
                Assert.Equal(Path.GetFullPath(model), saved.Path);
            }
            finally { DeleteTree(dir); }
        }

        [Fact]
        public async Task Save_missing_argument_jargon_is_rewritten_in_plain_words()
        {
            var res = await McpErrorBoundary.InvokeAsync("save_model", () =>
                throw new ArgumentException("The arguments dictionary is missing a value for the required parameter 'path'. (Parameter 'arguments')"));
            Assert.True(res.IsError);
            var text = ((TextContentBlock)res.Content.Single()).Text;
            Assert.DoesNotContain("arguments dictionary", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(Parameter '", text, StringComparison.Ordinal);
            Assert.Contains("path", text, StringComparison.OrdinalIgnoreCase);
        }

        // D-136: git_diff on an uncommitted (untracked) model must not look empty.
        [Fact]
        public async Task Git_diff_lists_untracked_model_files_instead_of_looking_clean()
        {
            var dir = CopyBim("sem-git-untracked-");
            var model = Path.Combine(dir, "S.bim");
            try
            {
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "diff@test.local");
                Git(dir, "config", "user.name", "Diff Test");
                Git(dir, "config", "commit.gpgsign", "false");
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                var diff = await engine.GitDiffAsync(null, false);
                Assert.Null(diff.Error);
                Assert.False(diff.Empty);
                Assert.Contains("S.bim", diff.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally { DeleteTree(dir); }
        }

        // D-139: a restore that put the files back must report restored true even if the follow-up commit fails.
        [Fact]
        public async Task Restore_reports_true_when_files_went_back_even_if_the_commit_fails()
        {
            var dir = CopyBim("sem-restore-flag-");
            var model = Path.Combine(dir, "S.bim");
            try
            {
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "hist@test.local");
                Git(dir, "config", "user.name", "Hist Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "--", "S.bim");
                Git(dir, "commit", "-q", "-m", "initial");

                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                var measure = (await engine.ListMeasuresAsync()).First().Ref;
                await engine.SetDaxAsync(measure, "1", "human");
                var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                Assert.True(first.Committed);
                await engine.SetDaxAsync(measure, "2", "human");
                await engine.SaveAsync(null, null);

                engine.FailCheckpointCommitMatchingForTest = "Restored to";
                var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                Assert.True(restored.Restored);
                Assert.Contains("restored and reopened", restored.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(x => x.Ref == measure).Expression);
            }
            finally { DeleteTree(dir); }
        }

        // D-134: create_column and list_partitions accept the bare table name they advertise.
        [Fact]
        public async Task Create_column_and_list_partitions_accept_a_bare_table_name()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListMeasuresAsync()).First().Table;

            var colRef = await engine.CreateColumnAsync(table, "BareCol", "Int64", "bare_src", "agent");
            Assert.Equal("column:" + table + "/BareCol", colRef);

            var parts = await engine.ListPartitionsAsync(table);
            Assert.NotEmpty(parts);
            await engine.DeleteObjectAsync(colRef, "agent");
        }

        // D-135: get_dependencies accepts the obvious direction word "downstream".
        [Fact]
        public async Task Get_dependencies_accepts_downstream_as_dependents()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListMeasuresAsync()).First().Table;
            var colRef = await engine.CreateColumnAsync("table:" + table, "DepCol", "Int64", null, "agent");
            var tq = "'" + table.Replace("'", "''") + "'";
            await engine.CreateMeasureAsync("table:" + table, "DepMeas", "SUM(" + tq + "[DepCol])", "agent");

            var viaWord = await McpTools.GetDependencies(engine, colRef, "downstream");
            Assert.Contains(viaWord, d => d.Name == "DepMeas");
            var viaAlias = await McpTools.GetDependencies(engine, colRef, "dependents");
            Assert.Equal(viaAlias.Select(d => d.Ref), viaWord.Select(d => d.Ref));
        }

        // D-140: the downvote that retires an insight still returns the record.
        [Fact]
        public async Task Retiring_downvote_returns_the_record_with_score_zero()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-down-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            var orig = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(ws, "home");
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true), ws);
                var added = await engine.AddInsightAsync("Retire this lesson", new[] { "K" }, "insight", "project", false, "agent");
                await engine.DownvoteInsightAsync(added.Id, "agent");
                await engine.DownvoteInsightAsync(added.Id, "agent");
                var gone = await engine.DownvoteInsightAsync(added.Id, "agent");
                Assert.NotNull(gone);
                Assert.Equal(added.Id, gone.Id);
                Assert.Equal(0, gone.Score);
                Assert.Contains("retired", gone.Note, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain((await engine.ListInsightsAsync("project", null)).Insights, i => i.Id == added.Id);
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", orig);
                DeleteTree(ws);
            }
        }

        // D-141: after team-standard, a bare create_measure result names the profile promise.
        [Fact]
        public async Task Team_standard_create_measure_names_the_profile_promise_on_the_agent_door()
        {
            var dir = CopyBim("sem-profile-note-");
            var model = Path.Combine(dir, "S.bim");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);
                await engine.ActivateWorkflowProfileAsync("team-standard", "human");
                var table = (await engine.ListMeasuresAsync()).First().Table;

                var result = await McpHealthAppender.InvokeAsync(engine, async () =>
                {
                    var created = await McpTools.CreateMeasure(engine, "table:" + table, "ProfileProbe", "1");
                    return new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = created } } };
                }, "create_measure");

                var texts = result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToArray();
                Assert.Contains(texts, t => t.Contains("measure:", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.IndexOf("Verified measure", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Contains(texts, t => t.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0
                    && t.IndexOf("continue", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally { DeleteTree(dir); }
        }

        // D-133: accepted Primer text keeps a blank line before the next heading and one trailing newline.
        [Fact]
        public void Accepted_primer_block_has_a_blank_line_before_the_next_heading()
        {
            var markdown = PrimerContract.Template("Contoso");
            var suggestion = new PrimerSuggestion
            {
                Section = "Gotchas",
                Markdown = "- Month-end stock can lag.\n  _Provenance: captured learning · 2026-01-01T00:00:00Z._",
            };
            var after = PrimerContract.ApplySuggestion(markdown, suggestion);
            Assert.Contains("_Provenance: captured learning · 2026-01-01T00:00:00Z._\n\n## Patterns", after);
            Assert.False(after.Contains("._\n## Patterns", StringComparison.Ordinal));
            Assert.EndsWith("\n", after);
            Assert.False(after.EndsWith("\n\n", StringComparison.Ordinal));
        }

        // ---- D-202 / GOV-06: no tool description may claim a push to the agent's own door ----------------------
        // Golden rule 2: the UI door IS pushed a change live; the agent door is never sent anything and learns on
        // its next call. The UAT read the shipped bundle (the engine DLL inside the VSIX) and found four
        // workflow-setting descriptions claiming the change is broadcast to "both doors" — one of which is the
        // door the reader is on. The honest form states the timing instead of the broadcast.

        private static IEnumerable<(string Tool, string Description)> ToolDescriptions()
        {
            foreach (var m in typeof(McpTools).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var tool = m.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                    .Cast<McpServerToolAttribute>().FirstOrDefault();
                if (tool?.Name is not string name) continue;
                var desc = m.GetCustomAttributes(typeof(DescriptionAttribute), false)
                    .Cast<DescriptionAttribute>().FirstOrDefault()?.Description ?? "";
                yield return (name, desc);
            }
        }

        [Fact]
        public void No_tool_description_claims_a_change_is_pushed_to_the_agent_door()
        {
            var descriptions = ToolDescriptions().ToList();
            Assert.True(descriptions.Count >= 50, "the tool surface did not load, so this walk proved nothing");
            var claims = new System.Text.RegularExpressions.Regex(
                @"(?i)\b(broadcast\w*|pushed|sent|synced)\b[^.]{0,80}\bboth doors\b");
            var bad = descriptions.Where(d => claims.IsMatch(d.Description)).Select(d => d.Tool).ToList();
            Assert.True(bad.Count == 0,
                "these descriptions claim a push to the agent's own door, which never happens: " + string.Join(", ", bad));
        }

        [Fact]
        public void Workflow_setting_descriptions_say_when_the_agent_door_sees_the_change()
        {
            var byTool = ToolDescriptions().ToDictionary(d => d.Tool, d => d.Description);
            foreach (var tool in new[]
            {
                "set_workflow_enforcement", "set_workflow_enabled", "set_workflow_binding", "set_workflow_activation",
            })
            {
                Assert.True(byTool.ContainsKey(tool), tool + " is missing from the tool surface");
                Assert.Contains("on your next call", byTool[tool]);
            }
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }
    }
}
