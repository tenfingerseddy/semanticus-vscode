using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Sol on m11/integration 568435d3, finding 2: Primer undo and redo put back the files they captured without
    /// looking at what is on disk now, so an edit made to the Primer in another editor after a Semanticus save was
    /// overwritten by Undo. Every file a Primer step touched (the Primer, and the suggestion list an accept writes) is
    /// now checked against the version that step expects, and Undo or Redo refuses with nothing changed when one moved.</summary>
    public sealed class PrimerUndoGuardTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string Placeholder = "_Add what people and the AI Assistant should know._";
        private static string Fill(string md, string section, string text) =>
            md.Replace("## " + section + "\n\n" + Placeholder, "## " + section + "\n\n" + text);

        private static async Task<T> WithModelAsync<T>(Func<LocalEngine, SessionManager, Task<T>> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-primer-guard", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                await engine.OpenAsync(model);
                return await body(engine, sessions);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public Task Undo_refuses_when_the_Primer_was_edited_on_disk_after_the_save() => WithModelAsync<bool>(async (engine, _) =>
        {
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            var first = Fill(starter, "Gotchas", "Fiscal year starts in July.");
            var saved = await McpTools.SetModelPrimer(engine, first);
            var second = Fill(first, "Patterns", "Use the Date table for time.");
            await McpTools.SetModelPrimer(engine, second);

            var external = second + "\nA line typed in another editor.\n";
            await File.WriteAllTextAsync(saved.FilePath, external);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.UndoChange(engine));
            Assert.Contains("Nothing was undone", ex.Message);
            Assert.Contains("Primer", ex.Message);
            Assert.DoesNotContain(saved.FilePath, ex.Message);   // a plain message, no path
            Assert.Equal(external, await File.ReadAllTextAsync(saved.FilePath));

            // Nothing was popped: once the file is back to what Semanticus wrote, the same Undo works.
            await File.WriteAllTextAsync(saved.FilePath, second);
            await McpTools.UndoChange(engine);
            Assert.Equal(first, await File.ReadAllTextAsync(saved.FilePath));
            return true;
        });

        [Fact]
        public Task Redo_refuses_when_the_Primer_was_edited_on_disk_after_the_undo() => WithModelAsync<bool>(async (engine, _) =>
        {
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            var first = Fill(starter, "Gotchas", "Fiscal year starts in July.");
            var saved = await McpTools.SetModelPrimer(engine, first);
            var second = Fill(first, "Patterns", "Use the Date table for time.");
            await McpTools.SetModelPrimer(engine, second);
            await McpTools.UndoChange(engine);
            Assert.Equal(first, await File.ReadAllTextAsync(saved.FilePath));

            var external = first + "\nTyped after the undo.\n";
            await File.WriteAllTextAsync(saved.FilePath, external);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.RedoChange(engine));
            Assert.Contains("Nothing was redone", ex.Message);
            Assert.Equal(external, await File.ReadAllTextAsync(saved.FilePath));
            return true;
        });

        [Fact]
        public Task Undo_refuses_when_a_Primer_the_undo_would_delete_was_written_by_hand() => WithModelAsync<bool>(async (engine, _) =>
        {
            // The first save made the file, so its undo removes it. A person's own edit to that file since is not removed.
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            var saved = await McpTools.SetModelPrimer(engine, Fill(starter, "Gotchas", "Fiscal year starts in July."));
            await File.WriteAllTextAsync(saved.FilePath, "# Mine now\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.UndoChange(engine));
            Assert.Equal("# Mine now\n", await File.ReadAllTextAsync(saved.FilePath));
            return true;
        });

        [Fact]
        public Task Undo_to_here_refuses_as_a_whole_and_leaves_the_model_edits_between() => WithModelAsync<bool>(async (engine, sessions) =>
        {
            var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
            var first = Fill(starter, "Gotchas", "Fiscal year starts in July.");
            var saved = await McpTools.SetModelPrimer(engine, first);
            var table = (await engine.GetModelObjectsAsync()).Tables.First().Name;
            await engine.CreateMeasureAsync("table:" + table, "Guard Probe", "1", "human");
            var second = Fill(first, "Patterns", "Use the Date table for time.");
            await McpTools.SetModelPrimer(engine, second);

            var rows = (await McpTools.GetPlanHistory(engine, objectRef: "primer")).Rows;
            var oldest = Assert.Single(rows, r => r.Deltas.Any(d => d.Props != null && d.Props.Contains("Gotchas")));

            // A clean walk over two Primer steps and a model edit works: each step expects what the one after it put back.
            await engine.UndoToEntryAsync(oldest.Id, "human");
            Assert.False(File.Exists(saved.FilePath));
            await engine.RedoToEntryAsync(rows.First().Id, "human");
            Assert.Equal(second, await File.ReadAllTextAsync(saved.FilePath));

            // With the file edited by hand, the whole walk is refused: the measure made between stays too.
            await File.WriteAllTextAsync(saved.FilePath, second + "\nBy hand.\n");
            var revision = sessions.Current.Revision;
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UndoToEntryAsync(oldest.Id, "human"));
            Assert.Equal(revision, sessions.Current.Revision);
            Assert.Contains((await engine.ListMeasuresAsync()), m => m.Name == "Guard Probe");
            Assert.Equal(second + "\nBy hand.\n", await File.ReadAllTextAsync(saved.FilePath));
            return true;
        });

        [Fact]
        public Task Undoing_an_accepted_suggestion_refuses_when_the_suggestion_list_changed_on_disk() => WithModelAsync<bool>(async (engine, _) =>
        {
            await engine.AddInsightAsync("Month-end stock can lag the sales snapshot by one refresh cycle.",
                new[] { "refresh", "primer:Gotchas" }, "post-mortem", "project", true, "agent");
            var suggestion = Assert.Single((await engine.ListPrimerSuggestionsAsync()).Suggestions);
            var accepted = await engine.AcceptPrimerSuggestionAsync(suggestion.Id, "human");
            var primerFile = accepted.Primer.FilePath;
            var sideFile = Path.ChangeExtension(primerFile, ".suggestions.json");
            Assert.True(File.Exists(sideFile));
            var primerText = await File.ReadAllTextAsync(primerFile);

            // The accept owns its one entry in the list, so an edit to THAT entry (here the decision turned round by hand)
            // is the newer edit Undo would overwrite. An edit elsewhere in the list is not (verifier on m11/sol-fixes-1, D1).
            var external = (await File.ReadAllTextAsync(sideFile)).Replace("\"accepted|", "\"rejected|");
            Assert.NotEqual(await File.ReadAllTextAsync(sideFile), external);
            await File.WriteAllTextAsync(sideFile, external);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.UndoChange(engine));
            Assert.Contains("suggestion", ex.Message);
            // Checked before anything is put back: the Primer is untouched too.
            Assert.Equal(primerText, await File.ReadAllTextAsync(primerFile));
            Assert.Equal(external, await File.ReadAllTextAsync(sideFile));
            return true;
        });

        [Fact]
        public Task Undoing_an_accepted_suggestion_keeps_an_edit_made_elsewhere_in_the_suggestion_list() => WithModelAsync<bool>(async (engine, _) =>
        {
            // Verifier on m11/sol-fixes-1, D1: the accept's undo step used to own the WHOLE list, so any other write to it
            // (a reject, off the timeline) locked Undo for good. It owns one entry: the rest of the file is someone else's.
            await engine.AddInsightAsync("Month-end stock can lag the sales snapshot by one refresh cycle.",
                new[] { "refresh", "primer:Gotchas" }, "post-mortem", "project", true, "agent");
            var suggestion = Assert.Single((await engine.ListPrimerSuggestionsAsync()).Suggestions);
            var accepted = await engine.AcceptPrimerSuggestionAsync(suggestion.Id, "human");
            var sideFile = Path.ChangeExtension(accepted.Primer.FilePath, ".suggestions.json");
            var text = await File.ReadAllTextAsync(sideFile);
            var edited = text.Replace("\"Decisions\": {", "\"Decisions\": {\n    \"typed-by-hand\": \"rejected|2026-09-28T00:00:00Z\",");
            Assert.NotEqual(text, edited);
            await File.WriteAllTextAsync(sideFile, edited);

            await McpTools.UndoChange(engine);
            Assert.Contains((await engine.ListPrimerSuggestionsAsync()).Suggestions, x => x.Id == suggestion.Id);   // waiting again
            Assert.Contains("typed-by-hand", await File.ReadAllTextAsync(sideFile));                               // theirs kept
            await McpTools.RedoChange(engine);
            Assert.DoesNotContain((await engine.ListPrimerSuggestionsAsync()).Suggestions, x => x.Id == suggestion.Id);
            Assert.Contains("typed-by-hand", await File.ReadAllTextAsync(sideFile));
            return true;
        });
    }
}
