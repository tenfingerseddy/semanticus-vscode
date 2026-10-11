using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, item U6 (Sol UAT-F-S10-mA2-011, S10-mA1-004): the Locked preset says "Even local writes ask
    /// first" and the matrix kept "Edit the working model" and "Write model files" at Always, so the assistant's
    /// set_description landed with no approval. Class: a preset's words that its matrix does not keep. Every preset's
    /// words are held to its matrix here, one table, and Locked is driven end to end with an assistant write.
    /// </summary>
    [Collection("restore-root")]
    public sealed class UatLockedPresetTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public UatLockedPresetTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-locked-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static readonly string[] Labels = { "local", "dev", "uat", "prod" };
        private static readonly AgentCapability[] LiveWrites =
            { AgentCapability.DeployLive, AgentCapability.DeployDelete, AgentCapability.Rollback, AgentCapability.Refresh };
        private static readonly AgentCapability[] LocalWrites = { AgentCapability.EditLocal, AgentCapability.DeployFile };

        private static string Cell(AgentPolicy p, AgentCapability cap, string label) => p.Matrix[cap.ToString()][label];

        // Each preset's words, as the card prints them, and what those words claim about the matrix.
        public static TheoryData<string, string> Cards() => new TheoryData<string, string>
        {
            { "open", "Allows the listed actions in every environment." },
            { "standard", "Asks before touching UAT or production." },
            { "cautious", "Asks for anything beyond local." },
            { "client", "Blocks production changes and reading rows from UAT or production." },
            { "locked", "Writes to published models are refused. Even local writes ask first." },
        };

        [Theory]
        [MemberData(nameof(Cards))]
        public void A_preset_card_says_only_what_its_matrix_does(string id, string words)
        {
            // The card in the app is the one this table copies: if someone rewords it, this fails until the table (and
            // so the matrix it is held to) is looked at again.
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.VSCode", "webview", "src", "permissions.tsx"));
            var onCard = Regex.Match(src, @"id:\s*'" + id + @"'[^}]*desc:\s*'([^']*)'").Groups[1].Value;
            Assert.Equal(words, onCard);

            var p = AgentPolicyPresets.Build(id);
            switch (id)
            {
                case "open":   // every environment, every listed action
                    foreach (var cap in LiveWrites.Concat(LocalWrites).Append(AgentCapability.QueryData))
                        foreach (var l in Labels) Assert.Equal("allow", Cell(p, cap, l));
                    break;
                case "standard":   // asks before UAT or production; local and dev go through
                    foreach (var cap in LiveWrites)
                    {
                        Assert.Equal("allow", Cell(p, cap, "local")); Assert.Equal("allow", Cell(p, cap, "dev"));
                        Assert.Equal("ask", Cell(p, cap, "uat")); Assert.Equal("ask", Cell(p, cap, "prod"));
                    }
                    break;
                case "cautious":   // anything beyond local asks
                    foreach (var cap in LiveWrites)
                    {
                        Assert.Equal("allow", Cell(p, cap, "local"));
                        foreach (var l in new[] { "dev", "uat", "prod" }) Assert.Equal("ask", Cell(p, cap, l));
                    }
                    break;
                case "client":   // production changes blocked; rows from UAT and production blocked
                    foreach (var cap in LiveWrites) Assert.Equal("deny", Cell(p, cap, "prod"));
                    Assert.Equal("deny", Cell(p, AgentCapability.QueryData, "uat"));
                    Assert.Equal("deny", Cell(p, AgentCapability.QueryData, "prod"));
                    break;
                case "locked":   // published models refused; EVERY local write asks, the working model and its files included
                    foreach (var cap in LiveWrites) { Assert.Equal("deny", Cell(p, cap, "uat")); Assert.Equal("deny", Cell(p, cap, "prod")); }
                    foreach (var cap in LocalWrites.Concat(LiveWrites.Take(0)))
                        foreach (var l in Labels) Assert.Equal("ask", Cell(p, cap, l));
                    foreach (var cap in LiveWrites) { Assert.Equal("ask", Cell(p, cap, "local")); Assert.Equal("ask", Cell(p, cap, "dev")); }
                    break;
                default: Assert.Fail("a preset with no row in this table: " + id);
                    break;
            }
        }

        [Fact]
        public void Every_preset_the_engine_names_has_a_card_in_the_table()
        {
            Assert.Equal(Cards().Select(c => (string)c[0]).OrderBy(x => x), AgentPolicyPresets.Names.OrderBy(x => x));
        }

        [Fact]
        public async Task Under_Locked_an_assistant_write_to_the_working_model_is_held_for_the_person_until_they_approve()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Locked", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            AgentPolicyStore.SetPreset("locked", "human");
            var revision = sessions.Current.Revision;

            // The assistant's calls go through the gate, the only engine an assistant holds.
            var assistant = AssistantEngine.ForAssistant(engine);
            var held = await Assert.ThrowsAsync<InvalidOperationException>(
                () => assistant.SetDescriptionAsync("measure:Sales/Total", "All of it.", "agent"));
            Assert.Contains("approval", held.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(revision, sessions.Current.Revision);
            Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));

            // The person is asked, in the queue the Permissions page reads.
            var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
            Assert.Equal("EditLocal", pending.Capability);
            Assert.Null(pending.GrantedUtc);

            // The person writes with no approval at all: the matrix restrains the assistant, not the person.
            await engine.SetDescriptionAsync("measure:Sales/Total", "By a person.", "human");

            // The person's own write moved the model, so the yes they gave was for a model that is gone: the retry asks again.
            await engine.ApproveAgentActionAsync(pending.Id, "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => assistant.SetDescriptionAsync("measure:Sales/Total", "All of it.", "agent"));
            Assert.Equal("By a person.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));

            // Approved at the model as it is now, the assistant's retry goes through once; the next one asks again.
            await engine.ApproveAgentActionAsync((await engine.ListPendingApprovalsAsync()).Single(p => p.GrantedUtc == null).Id, "human");
            await assistant.SetDescriptionAsync("measure:Sales/Total", "All of it.", "agent");
            Assert.Equal("All of it.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
            await Assert.ThrowsAsync<InvalidOperationException>(() => assistant.SetDescriptionAsync("measure:Sales/Total", "Again.", "agent"));
            Assert.Equal("All of it.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
        }

        [Fact]
        public async Task Under_Locked_an_assistant_undo_and_a_file_save_are_held_too_and_a_standard_policy_holds_nothing()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-lk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.CreateModelAsync("Locked", 1604);
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");

                // Standard: the assistant writes freely and saves.
                await engine.SetDescriptionAsync("measure:Sales/Total", "Plain.", "agent");
                var target = Path.Combine(dir, "m.bim");
                await engine.SaveAsync(target, "bim", false, "agent");
                Assert.True(File.Exists(target));

                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UndoAsync("agent"));
                Assert.Equal("Plain.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(target, "bim", false, "agent"));
                var other = Path.Combine(dir, "other.bim");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(other, "bim", false, "agent"));
                Assert.False(File.Exists(other));
                await engine.SaveAsync(other, "bim", false, "human");   // the person's own save
                Assert.True(File.Exists(other));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_older_Locked_policy_file_that_still_says_Always_is_read_as_the_card_says()
        {
            // A person who picked Locked before this change has a file whose two local rows say allow. The card they chose
            // says local writes ask, so the file is read that way: a stricter cell the person set later is never loosened.
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var old = AgentPolicyPresets.Build("locked");
            foreach (var cap in LocalWrites) foreach (var l in Labels) old.Matrix[cap.ToString()][l] = "allow";
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "agent-policy.json"),
                System.Text.Json.JsonSerializer.Serialize(old, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            var read = AgentPolicyStore.Get();
            Assert.Equal("locked", read.Preset);
            foreach (var cap in LocalWrites) foreach (var l in Labels) Assert.Equal("ask", Cell(read, cap, l));
            await Task.CompletedTask;
        }
    }
}
