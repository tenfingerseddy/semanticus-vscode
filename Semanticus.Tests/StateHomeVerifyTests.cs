// Verifier tests for m11/state-home (27 Sep). Each one pins a way a throwaway run can still reach, or cost, the
// person's own per-user state after the fix. They are left FAILING on purpose: each is a defect the verifier found,
// and the lane goes back with them. Evidence for each (process runs, before/after trees) is in the verifier's report,
// /tmp/semanticus-joint-review/lane-reports/verify-state-home.md.
//
//   1. --state-dir=<dir> (the equals form) is neither honoured nor refused, so the run writes ~/.semanticus.
//   2. An agent started with --state-dir that JOINS an owner already running for its folder writes that owner's home.
//   3. The sign-in store (saved accounts and sign-in records) stays under HOME whatever --state-dir says.
//   4. Temp-folder opens now have no connection id, so 25 of them push the person's own id-less history rows out
//      (account switches, failed sign-ins to a target never saved). Before the fix those rows survived.
//   5. Two tools that start the installed engine (the jev-replay gather scripts) still run it on the real home.
//   6. After a torn connection list (a power cut can leave the renamed file's bytes unwritten), the next two ordinary
//      writes rotate the only good copy out of .bak: both copies are then lost.
//
// Round 3 (716bc9e0), the verifier's report /tmp/semanticus-joint-review/lane-reports/v-state-home-r3.md:
//   7. After a torn list, a write the engine refuses (an agent forgetting a labelled target) has already set the torn
//      file aside: the list then shows nothing, and two ordinary writes rotate the good .bak out. Both copies lost.
//      The timeline has the same gap after a write that fails (a full disk).
//   8. A state-dir spelled with a typographic dash (what a word processor or chat app makes of "--") is silently
//      ignored, so the run writes the real ~/.semanticus.
//   9. The person's assistant, which chose no state folder, joins an owner an isolated run started with its own
//      folder, and every write it makes lands in that scratch folder, silently.
//  10. The locks that keep two engines on one model from overwriting each other's History, drafts and Plan claims
//      live under the state folder, so an engine given its own folder no longer shares them with the person's engine.
//  11. Recovering an unreadable list from .bak brings back an older, looser label than the person last set, and the
//      agent policy gates on it (found by Sol reviewing this round; it sits with item 7).
//
// Round 4 (20f78194), the verifier's report /tmp/semanticus-joint-review/lane-reports/v-state-home-r4.md:
//  12. A row the person labelled prod comes back from .bak unlabelled when .bak predates the label (the label was the
//      write that tore), so the agent can forget it again: only a labelled row is protected from the agent's forget
//      (found by Sol reviewing this round). It sits with item 11.
//  13. The net for test runs removes DISPLAY and WAYLAND_DISPLAY, but a test run still inherits the person's session bus
//      and runtime folder, which is how a browser opens on this machine without either variable (omarchy-launch-browser
//      goes through systemd-run --user; the portal's OpenURI and the keyring need only the bus).
//  14. Two lock folders that are one folder reached through a link inside a link target are not seen as one, so the
//      engine takes the same lock file twice and waits on itself (found by Sol reviewing this round).
//
// Targeted round (1eb02d8a), the verifier's report /tmp/semanticus-joint-review/lane-reports/v-state-home-targeted.md:
//  15. A test run still reaches the person's own Azure CLI sign-in. The az it finds is the real one, on the account's own
//      ~/.azure, so ProGateRemovalTests' push test asks it for a Power BI token for the person and connects to
//      api.powerbi.com with it wherever the Azure CLI is signed in (the Inspiron and the Yoga), and the sign-in probe
//      runs `az account show` there too; the run stays green and names nothing. The browser half of that push test is
//      fixed: it no longer reaches xdg-open.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root and SEMANTICUS_HOME
    public sealed class StateHomeVerifyTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public StateHomeVerifyTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-statehome-verify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            ThrowawayPaths.RootsOverride = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- 1 ----------------------------------------------------------------------------------------------------

        [Fact]
        public void An_equals_form_state_dir_is_honoured_or_refused_never_silently_ignored()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var target = Path.Combine(_root, "state");
            try
            {
                ArgumentException refused = null;
                try { SemanticusHome.ApplyCommandLine(new[] { "mcp", "--workspace", _root, "--state-dir=" + target }); }
                catch (ArgumentException ex) { refused = ex; }
                // Ignored, every store resolves to ~/.semanticus: the exact failure the flag exists to prevent, and the
                // one the ApplyCommandLine comment promises cannot happen ("a typo can never quietly fall back").
                Assert.True(refused != null || string.Equals(SemanticusHome.Root(), Path.GetFullPath(target), StringComparison.Ordinal),
                    "--state-dir=<dir> was neither honoured nor refused, so this engine would write the real ~/.semanticus");
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        // ---- 2 ----------------------------------------------------------------------------------------------------

        /// <summary>The person's window is an owner for the folder with its own home. An agent then starts the MCP door
        /// on the same folder with --state-dir. Refusing to join, or writing the agent's state dir, are both acceptable;
        /// silently writing the person's home is not.</summary>
        [EngineApphostFact]
        public async Task An_agent_started_with_a_state_dir_never_writes_the_home_of_an_owner_it_joins()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws");
            var decoy = Path.Combine(_root, "decoy-home");
            var personHome = Path.Combine(_root, "person-home");
            var agentState = Path.Combine(_root, "agent-state");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(decoy);
            EngineProc person = null, agent = null;
            try
            {
                person = EngineProc.StartMcp(apphost, ws, decoy, new Dictionary<string, string> { [SemanticusHome.Variable] = personHome });
                await person.InitializeAsync();
                agent = EngineProc.StartMcp(apphost, ws, decoy, null, "--state-dir", agentState);
                try
                {
                    await agent.InitializeAsync();
                    await agent.CallToolAsync("remember_xmla_connection", new JObject
                    {
                        ["endpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/AgentOnly",
                        ["database"] = "Sales",
                    });
                }
                catch (InvalidOperationException) { /* the door refused to start or to join: an acceptable answer */ }

                var personList = Path.Combine(personHome, "connections.json");
                Assert.False(File.Exists(personList) && File.ReadAllText(personList).Contains("AgentOnly"),
                    "an agent that asked for --state-dir joined the running owner and wrote into that owner's home\n" + agent.Errors);
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                agent?.Dispose();
                person?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ---- 3 ----------------------------------------------------------------------------------------------------

        /// <summary>A real HOME has ~/.local/share, where .NET puts LocalApplicationData on Unix and the engine keeps its
        /// saved accounts and sign-in records. Listing the accounts is enough to create the folder there.</summary>
        [EngineApphostFact(unixOnly: true)]
        public async Task The_sign_in_store_moves_with_the_state_dir_too()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws-auth");
            var decoy = Path.Combine(_root, "decoy-home-auth");
            var state = Path.Combine(_root, "state-auth");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(Path.Combine(decoy, ".local", "share"));
            EngineProc mcp = null;
            try
            {
                mcp = EngineProc.StartMcp(apphost, ws, decoy, null, "--state-dir", state);
                await mcp.InitializeAsync();
                var listed = await mcp.CallToolAsync("list_account_profiles");
                Assert.False(listed.IsError, listed.Text + "\n" + mcp.Errors);
                Assert.False(Directory.Exists(Path.Combine(decoy, ".local", "share", "Semanticus")),
                    "the engine was given --state-dir but created its sign-in store under HOME (~/.local/share/Semanticus/auth)");
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ---- 4 ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Temp_opens_do_not_push_the_persons_own_history_rows_out()
        {
            // The person's own rows that carry no connection id: an account switch, and a failed sign-in to a target
            // that was never saved.
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "switch", Account = "person@contoso.com", Ok = true, Detail = "made the default account" });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "signin", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/NeverSaved", Database = "Sales", Ok = false });
            var bim = Path.Combine(_root, "Throwaway.bim");
            TinyBim.Write(bim);
            using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _root))
                for (var i = 0; i < ConnectionHistory.MaxPerConnection; i++)
                    await engine.OpenAsync(bim);   // under the temp folder: not saved, so its history row has no id

            var kinds = ConnectionHistory.List().Select(e => e.Kind).ToList();
            Assert.True(kinds.Contains("switch") && kinds.Contains("signin"),
                $"{ConnectionHistory.MaxPerConnection} temp opens pushed the person's own history rows out: {string.Join(",", kinds.Distinct())}");
        }

        // ---- 5 ----------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("tools/jev-replay/columns/gather.mjs")]
        [InlineData("tools/jev-replay/starter-measures/gather.mjs")]
        public void A_tool_that_starts_the_engine_gives_it_a_scratch_state_folder(string tool)
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), tool));
            Assert.Contains("new McpClient(", src);   // it does start `Semanticus.Engine mcp`
            Assert.True(src.Contains("SEMANTICUS_HOME") || src.Contains("--state-dir"),
                tool + " starts the installed engine on the person's real ~/.semanticus: every connect_xmla it makes rewrites the saved list and adds history rows");
        }

        // ---- 6 ----------------------------------------------------------------------------------------------------

        [Fact]
        public void After_a_torn_connection_list_the_last_good_copy_survives_the_next_two_writes()
        {
            foreach (var n in new[] { "TornA", "TornB", "TornC" })
                ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/" + n, "Sales", "Sales");
            var path = Path.Combine(_root, "connections.json");
            // The next rewrite was cut by a power loss after its rename: the file it replaced survives as .bak (a hard
            // link taken before the rename), and the new file's bytes never reached the disk (NTFS does not journal file
            // data; neither does ext4 with data=writeback). Nothing in the rewrite flushes the .tmp before the rename.
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);

            // Two ordinary writes after the restart: an open, then a connect.
            ConnectionRegistry.Remember("file", Path.Combine(_root, "Next.bim"), null, "Next");
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/After", "Sales", "Sales");

            var anywhere = Directory.GetFiles(_root, "connections.json*").Select(File.ReadAllText).ToList();
            Assert.True(ConnectionRegistry.List().Any(r => r.Endpoint.EndsWith("/TornA", StringComparison.Ordinal))
                        || anywhere.Any(t => t.Contains("TornA") && t.Contains("TornC")),
                "the torn list was set aside and then the next two writes rotated the only good copy out of .bak: every saved connection is gone");
        }

        // ---- 7 ----------------------------------------------------------------------------------------------------

        /// <summary>The round-2 fix recovers a torn list from .bak inside the same write that sets the torn file aside.
        /// When that write is refused after the set-aside (here an agent asking to forget a target the person labelled,
        /// which Mutate refuses after ReadForWrite ran), nothing is saved: connections.json is gone, the good rows are
        /// only in .bak, and the display read, which falls back to .bak only for an unreadable file, not a missing one,
        /// shows an empty list. The next write starts from empty too, and the one after it rotates .bak away.</summary>
        [Fact]
        public void After_a_torn_list_a_refused_write_neither_hides_nor_loses_the_last_good_copy()
        {
            foreach (var n in new[] { "KeptA", "KeptB", "KeptC" })
                ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/" + n, "Sales", "Sales");
            var labelled = ConnectionRegistry.List().Single(r => r.Endpoint.EndsWith("/KeptA", StringComparison.Ordinal));
            ConnectionRegistry.SetLabel(labelled.Id, "prod", "human");
            var path = Path.Combine(_root, "connections.json");
            File.Copy(path, path + ".bak", overwrite: true);                   // the last good list
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);     // a torn rewrite

            // The agent's door refuses this (a label is governance data), but only after the write path set the torn
            // file aside.
            Assert.Throws<InvalidOperationException>(() => ConnectionRegistry.Forget(labelled.Id, "agent"));
            var shownAfterRefusal = ConnectionRegistry.List().Count(r => r.Endpoint.Contains("/Kept", StringComparison.Ordinal));

            // Two ordinary writes: an open, then a connect.
            ConnectionRegistry.Remember("file", Path.Combine(_root, "Next.bim"), null, "Next");
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/After", "Sales", "Sales");

            var anywhere = Directory.GetFiles(_root, "connections.json*").Select(File.ReadAllText).ToList();
            var survives = anywhere.Any(t => t.Contains("/KeptA") && t.Contains("/KeptB") && t.Contains("/KeptC"));
            // The label a policy gates on (unlabelled reads as prod, so a fix may also drop labels it cannot vouch for).
            var labelKept = ConnectionRegistry.EffectiveLabel(ConnectionRegistry.List()
                .FirstOrDefault(r => r.Endpoint.EndsWith("/KeptA", StringComparison.Ordinal))) == "prod";
            Assert.True(shownAfterRefusal == 3 && survives && labelKept,
                $"after a torn list and one refused write: the list showed {shownAfterRefusal} of the 3 saved rows; "
                + $"after two ordinary writes the 3 rows are {(survives ? "still in some copy" : "in no file at all")} "
                + $"and the 'prod' label is {(labelKept ? "kept" : "gone")}");
        }

        /// <summary>The same gap on the timeline: its only way to reach "set aside, nothing written" is a write that
        /// fails after the set-aside (a full disk; here a folder squatting on the .tmp path stands in for it, and is
        /// removed straight after). Append swallows the failure, as it should, and the good rows are then only in .bak.</summary>
        [Fact]
        public void After_a_torn_history_a_failed_write_neither_hides_nor_loses_the_last_good_copy()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "switch", Account = "person@contoso.com", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "kept-endpoint", Ok = true });
            var path = Path.Combine(_root, "connection-history.json");
            File.Copy(path, path + ".bak", overwrite: true);                   // the last good timeline
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);     // a torn rewrite

            Directory.CreateDirectory(path + ".tmp");
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "failed-endpoint", Ok = true });
            Directory.Delete(path + ".tmp");
            var shownAfterFailure = ConnectionHistory.List().Count(e => e.Kind == "switch" || e.Endpoint == "kept-endpoint");

            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "next-endpoint", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "after-endpoint", Ok = true });
            var anywhere = Directory.GetFiles(_root, "connection-history.json*").Select(File.ReadAllText).ToList();
            var survives = anywhere.Any(t => t.Contains("person@contoso.com") && t.Contains("kept-endpoint"));
            Assert.True(shownAfterFailure == 2 && survives,
                $"after a torn timeline and one failed write: the timeline showed {shownAfterFailure} of its 2 rows; "
                + $"after two ordinary writes they are {(survives ? "still in some copy" : "in no file at all")}");
        }

        /// <summary>Found by Sol on this round's review. Recovering from .bak brings back whatever labels .bak holds, and
        /// .bak is one write behind the file it backs up. A person who tightened a target (dev, then prod) and whose list
        /// then becomes unreadable in place (a hand edit, a sync conflict, a bad sector: nothing rotated it into .bak) gets
        /// the older, looser label back, and the agent policy gates on it (standard allows on dev what it asks for on
        /// prod). The next write makes it permanent. Before round 2 an unreadable list read as empty, so every target was
        /// unlabelled, which is prod: it failed closed. Recovery must never loosen a label.</summary>
        [Fact]
        public void Recovering_a_list_from_its_backup_never_loosens_a_label_the_person_tightened()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Tightened", "Sales", "Sales");
            var id = ConnectionRegistry.List().Single(r => r.Endpoint.EndsWith("/Tightened", StringComparison.Ordinal)).Id;
            ConnectionRegistry.SetLabel(id, "dev", "human");
            ConnectionRegistry.SetLabel(id, "prod", "human");               // .bak now holds the dev version
            var path = Path.Combine(_root, "connections.json");
            File.WriteAllText(path, "{ this is no longer the list");          // unreadable in place; .bak untouched

            var shown = ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(id));
            ConnectionRegistry.Remember("file", Path.Combine(_root, "Next.bim"), null, "Next");   // the next ordinary write
            var afterWrite = ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(id));
            Assert.True(shown == "prod" && afterWrite == "prod",
                $"the person set this target to prod after dev; with the list unreadable the gate reads '{shown}', and after the next "
                + $"write '{afterWrite}': a looser label than the person's last declaration");
        }

        // ---- 8 ----------------------------------------------------------------------------------------------------

        /// <summary>A command copied from a document or a chat often arrives with "--" turned into an en or em dash.
        /// ApplyCommandLine refuses look-alikes only when the first character is an ASCII hyphen, so these fall through
        /// to "not an option at all", and every store then resolves to the real home: the exact thing D1 closed.</summary>
        [Theory]
        [InlineData("\u2013state-dir", false)]          // en dash
        [InlineData("\u2014state-dir", false)]          // em dash
        [InlineData("\u2013\u2013state-dir", false)]    // two en dashes
        [InlineData("\u2013state-dir", true)]           // en dash, equals form
        public void A_state_dir_spelled_with_a_typographic_dash_is_honoured_or_refused_never_ignored(string option, bool equalsForm)
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var target = Path.Combine(_root, "state-dash");
            try
            {
                var args = equalsForm
                    ? new[] { "mcp", "--workspace", _root, option + "=" + target }
                    : new[] { "mcp", "--workspace", _root, option, target };
                ArgumentException refused = null;
                try { SemanticusHome.ApplyCommandLine(args); }
                catch (ArgumentException ex) { refused = ex; }
                Assert.True(refused != null || string.Equals(SemanticusHome.Root(), Path.GetFullPath(target), StringComparison.Ordinal),
                    $"'{string.Join(" ", args.Skip(3))}' was neither honoured nor refused, so this engine would write the real ~/.semanticus");
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        // ---- 9 ----------------------------------------------------------------------------------------------------

        /// <summary>The mirror of item 2. An isolated run (its own SEMANTICUS_HOME) owns the folder first; then the
        /// person's assistant, which chose nothing, starts the MCP door on the same folder. StateFolderMismatch returns
        /// false for any process that chose nothing, so it joins, and what it saves lands in the isolated run's scratch
        /// folder, which is deleted when that run ends. Refusing to join is an acceptable answer; writing the person's
        /// connections into a folder the person never chose, without a word, is not.</summary>
        [EngineApphostFact]
        public async Task The_persons_assistant_never_writes_into_the_state_folder_of_an_isolated_owner_it_joins()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws-reverse");
            var decoy = Path.Combine(_root, "decoy-home-reverse");      // the person's HOME
            var isolated = Path.Combine(_root, "isolated-state");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(decoy);
            EngineProc iso = null, person = null;
            try
            {
                iso = EngineProc.StartMcp(apphost, ws, decoy, new Dictionary<string, string> { [SemanticusHome.Variable] = isolated });
                await iso.InitializeAsync();
                await iso.CallToolAsync("list_connections");
                person = EngineProc.StartMcp(apphost, ws, decoy, null);
                var refused = false;
                (bool IsError, string Text) remembered = (false, null);
                try
                {
                    await person.InitializeAsync();
                    remembered = await person.CallToolAsync("remember_xmla_connection", new JObject
                    {
                        ["endpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/PersonOnly",
                        ["database"] = "Sales",
                    });
                }
                catch (InvalidOperationException) { refused = true; /* the door refused to join: an acceptable answer */ }

                var isolatedList = Path.Combine(isolated, "connections.json");
                var landedInScratch = File.Exists(isolatedList) && File.ReadAllText(isolatedList).Contains("PersonOnly");
                Assert.True(refused || (!remembered.IsError && !landedInScratch),
                    (remembered.IsError ? "the call failed for another reason: " + remembered.Text + "\n"
                        : "the person's assistant chose no state folder, joined an owner that keeps its state in an isolated run's scratch folder, "
                        + "and its saved connection was written there without a word\n") + person.Errors);
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                person?.Dispose();
                iso?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ---- 10 ---------------------------------------------------------------------------------------------------

        /// <summary>SidecarFileLock (History, drafts) and EngineRunLiveness (whether the run behind a Plan claim is alive)
        /// name their lock files under SemanticusHome.Root(). Their own comment rests on "two engines of one user on one
        /// machine" sharing that home, which a public --state-dir ends: the person's engine and an isolated run open the
        /// same model through different workspaces (the README's own example shape), take different lock files for the
        /// same file beside the model, and each can read the other's live claim as gone. One model file, one lock.</summary>
        [Fact]
        public async Task Two_engines_with_different_state_folders_share_the_locks_for_one_models_files()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            Task<IDisposable> second = null;
            try
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "person-home"));
                var personRun = EngineRunLiveness.PathFor("run-0001");
                bool excluded;
                using (SidecarFileLock.Acquire(sidecar))          // the person's engine is writing the model's History
                {
                    Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "isolated-state"));
                    second = Task.Run(() => SidecarFileLock.Acquire(sidecar));   // an isolated run writes the same file
                    excluded = !second.Wait(TimeSpan.FromSeconds(1)) || second.IsFaulted;
                }
                var isolatedRun = EngineRunLiveness.PathFor("run-0001");
                Assert.True(excluded && personRun == isolatedRun,
                    (excluded ? "" : "an engine given its own state folder took the lock for a file beside the model while the person's engine held it; ")
                    + (personRun == isolatedRun ? "" : $"and it looks for the person's live run at {isolatedRun}, not {personRun}"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, before);
                if (second != null)
                    try { (await second)?.Dispose(); } catch { /* it timed out waiting: the lock was shared */ }
            }
        }

        // ---- 12 ---------------------------------------------------------------------------------------------------

        /// <summary>The person remembers a target, then labels it prod. That labelling write rotates the unlabelled row
        /// into .bak, and it is the write that tears. Recovery raises a label .bak carries, but this row carries none, so
        /// it comes back unlabelled, and the agent may forget an unlabelled target. The person's last word was prod,
        /// which an agent cannot forget.</summary>
        [Fact]
        public void An_agent_cannot_forget_a_target_the_person_labelled_prod_after_the_list_is_recovered_from_bak()
        {
            var rec = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso", "Sales", "Sales");
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");           // .bak now holds the row unlabelled
            var path = Path.Combine(_root, "connections.json");
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);    // and the labelling write tore

            Exception refused = null;
            try { ConnectionRegistry.Forget(rec.Id, "agent"); }
            catch (InvalidOperationException ex) { refused = ex; }
            Assert.True(refused != null && ConnectionRegistry.List().Any(r => r.Id == rec.Id),
                "the agent forgot a target the person had labelled prod, because the row came back from .bak unlabelled");
        }

        // ---- 13 ---------------------------------------------------------------------------------------------------

        /// <summary>The test-run net (InteractiveSignIn.StubBrowsersForTestRun) removes DISPLAY and WAYLAND_DISPLAY and
        /// says nothing the run starts can then reach a desktop. An agent's shell on the person's machine has neither
        /// variable, and it reached the desktop on 27 Sep all the same. What it does have is the session bus and the
        /// runtime folder: BROWSER there is omarchy-launch-browser, which opens Chromium through systemd-run --user, and
        /// systemd-run, the desktop portal's OpenURI and the keyring need nothing else. A test run must not reach them.</summary>
        [Fact]
        public void A_test_run_cannot_reach_the_desktop_session_that_opens_browsers_and_keyrings()
        {
            if (OperatingSystem.IsWindows()) return;   // no session bus there: the opt-in is the net
            var reachable = new List<string>();
            var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (!string.IsNullOrWhiteSpace(bus))
            {
                var unixPath = bus.Split(';', ',').Select(p => p.Trim())
                    .FirstOrDefault(p => p.StartsWith("unix:path=", StringComparison.Ordinal))?.Substring("unix:path=".Length);
                if (unixPath == null || File.Exists(unixPath)) reachable.Add("DBUS_SESSION_BUS_ADDRESS=" + bus);
            }
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
            {
                if (File.Exists(Path.Combine(runtime, "bus"))) reachable.Add("the session bus socket in XDG_RUNTIME_DIR (" + runtime + ")");
                if (File.Exists(Path.Combine(runtime, "systemd", "private"))) reachable.Add("the systemd user manager in XDG_RUNTIME_DIR");
                reachable.AddRange(Directory.GetFileSystemEntries(runtime, "wayland-*").Select(w => "a Wayland socket " + w));
            }
            Assert.True(reachable.Count == 0,
                "this test run can still reach a desktop session, where a launcher it starts opens a real browser: " + string.Join("; ", reachable));
        }

        // ---- 14 ---------------------------------------------------------------------------------------------------

        /// <summary>SamePlace follows the link it meets, but not a link inside that link's target. Here the legacy lock
        /// folder is "alias", a link to "bridge/locks", and "bridge" is itself a link to the folder that holds the lock
        /// folder: one folder, two names. The engine takes the same lock file under both names and waits on itself.</summary>
        [Fact]
        public void A_lock_folder_reached_through_a_link_inside_a_link_is_still_taken_once()
        {
            if (OperatingSystem.IsWindows()) return;   // symbolic links need privileges there
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            var lockRoot = SemanticusHome.LockRoot();
            Directory.CreateDirectory(lockRoot);
            var bridge = Path.Combine(_root, "bridge");
            Directory.CreateSymbolicLink(bridge, Path.GetDirectoryName(Path.GetFullPath(lockRoot)));
            var alias = Path.Combine(_root, "alias");
            Directory.CreateSymbolicLink(alias, Path.Combine(bridge, Path.GetFileName(Path.GetFullPath(lockRoot))));
            SemanticusHome.LegacyLockRootForTests = () => alias;
            try
            {
                var clock = Stopwatch.StartNew();
                Exception failed = null;
                try { SidecarFileLock.Acquire(sidecar).Dispose(); }
                catch (Exception ex) { failed = ex; }
                Assert.True(failed == null && clock.Elapsed < TimeSpan.FromSeconds(5),
                    $"one lock folder under two names was taken twice: the engine waited {clock.Elapsed.TotalSeconds:F0}s on itself"
                    + (failed == null ? "" : " and failed: " + failed.Message));
            }
            finally { SemanticusHome.LegacyLockRootForTests = null; }
        }

        // ---- 15 ---------------------------------------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("libc")] private static extern uint getuid();
        [System.Runtime.InteropServices.DllImport("libc")] private static extern IntPtr getpwuid(uint uid);

        /// <summary>This account's own home folder from the password database, whatever HOME says. The field read is
        /// pw_dir, at byte 32 of struct passwd on 64-bit Linux.</summary>
        private static string AccountHome()
        {
            var pw = getpwuid(getuid());
            return pw == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(System.Runtime.InteropServices.Marshal.ReadIntPtr(pw, 32));
        }

        /// <summary>A push, compare or connect as the person with no sign-in mode named starts with the Azure CLI
        /// (`az account get-access-token --resource https://analysis.windows.net/powerbi/api`), and the sign-in probe runs
        /// `az account show`. A test run gives both the real az on the account's own Azure CLI folder (~/.azure), so
        /// ProGateRemovalTests.Free_may_push_more_than_one_object_to_a_published_model gets a real Power BI token for the
        /// person wherever the Azure CLI is signed in (the Inspiron and the Yoga), connects to api.powerbi.com with it,
        /// and passes; nothing fails and nothing names it. Proved with a stand-in az found just before the real one: it
        /// writes down which Azure CLI folder each start would read, and fails as a CLI with nobody signed in, so the real
        /// az never runs and nothing leaves the machine. The run must reach no Azure CLI on the account's own folder.</summary>
        [Fact]
        public async Task A_test_run_cannot_reach_the_persons_Azure_CLI_sign_in()
        {
            if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) return;   // the struct passwd layout read above is 64-bit Linux's
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
            var first = path.Select(d => Path.Combine(d, "az")).FirstOrDefault(File.Exists);
            if (first == null || File.ReadAllText(first).Contains(InteractiveSignIn.StubMarker)) return;   // no real Azure CLI to reach
            var person = AccountHome();
            Assert.False(string.IsNullOrWhiteSpace(person), "this account has no home folder in the password database");

            var fake = Path.Combine(_root, "fake-az");
            Directory.CreateDirectory(fake);
            var calls = Path.Combine(fake, "calls.log");
            File.WriteAllText(Path.Combine(fake, "az"),
                "#!/bin/sh\nprintf '%s|%s|%s\\n' \"$AZURE_CONFIG_DIR\" \"$HOME\" \"$*\" >> '" + calls + "'\necho 'ERROR: Please run az login to setup account.' >&2\nexit 1\n");
            File.SetUnixFileMode(Path.Combine(fake, "az"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var before = Environment.GetEnvironmentVariable("PATH");
            var stubs = Path.GetDirectoryName(Environment.GetEnvironmentVariable(InteractiveSignIn.StubLogVariable) ?? "");
            path.Insert(path.Count > 0 && path[0] == stubs ? 1 : 0, fake);   // after the run's own stubs, before the real az
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, path));
            try
            {
                using var engine = new LocalEngine(new SessionManager());
                await engine.OpenAsync(TinyBim.Write(Path.Combine(_root, "az", "Tiny.bim")));
                var workspace = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso", Database = "Contoso" };
                try { await engine.ApplyDiffAsync(new ModelRef { Kind = "session" }, workspace, new[] { "measure:Sales/A" }, false, "human"); } catch { }
                try { await engine.ProbeAuthPrerequisitesAsync("azcli", null); } catch { }
            }
            finally { Environment.SetEnvironmentVariable("PATH", before); }

            var own = Path.Combine(person, ".azure");
            var reached = (File.Exists(calls) ? File.ReadAllLines(calls) : Array.Empty<string>())
                .Select(l => l.Split('|'))
                .Where(f => f.Length == 3 && SemanticusHome.SameFolder(f[0].Length > 0 ? f[0] : Path.Combine(f[1], ".azure"), own))
                .Select(f => "az " + f[2])
                .ToList();
            Assert.True(reached.Count == 0,
                $"this test run started the Azure CLI on the account's own sign-in folder {own} ({first} is the real az): "
                + string.Join("; ", reached) + ". A push to a published model as the person gets a real Power BI token that way and connects to api.powerbi.com with it");
        }

        // ---- helpers ------------------------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }

        /// <summary>A real engine as an agent's MCP server (newline-delimited JSON-RPC on stdio), with a chosen HOME,
        /// no inherited SEMANTICUS_HOME or XDG folders (so nothing it resolves can reach a real one), and extra args.</summary>
        private sealed class EngineProc : IDisposable
        {
            private readonly Process _p;
            private readonly ConcurrentDictionary<int, JObject> _replies = new ConcurrentDictionary<int, JObject>();
            private readonly StringBuilder _err = new StringBuilder();
            private int _id;
            private EngineProc(Process p) { _p = p; }
            public string Errors { get { lock (_err) return _err.ToString(); } }

            public static EngineProc StartMcp(string apphost, string ws, string home, IDictionary<string, string> env, params string[] extra)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = apphost,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in new[] { "mcp", "--workspace", ws }.Concat(extra)) psi.ArgumentList.Add(a);
                var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
                psi.Environment["DOTNET_ROOT"] = runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;
                psi.Environment["HOME"] = home;
                psi.Environment["USERPROFILE"] = home;
                foreach (var k in new[] { SemanticusHome.Variable, "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
                    psi.Environment.Remove(k);
                psi.Environment["SEMANTICUS_DEV_PRO"] = "1";
                psi.Environment[ParentLifetime.GraceEnvironmentVariable] = "1000";
                foreach (var kv in env ?? new Dictionary<string, string>()) psi.Environment[kv.Key] = kv.Value;
                var client = new EngineProc(Process.Start(psi));
                client._p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (client._err) client._err.AppendLine(e.Data); };
                client._p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    try
                    {
                        var o = JObject.Parse(e.Data);
                        if (o["id"] != null && o["id"].Type == JTokenType.Integer) client._replies[o["id"].Value<int>()] = o;
                    }
                    catch { }
                };
                client._p.BeginErrorReadLine();
                client._p.BeginOutputReadLine();
                return client;
            }

            private async Task<JObject> RequestAsync(string method, JObject @params)
            {
                var id = Interlocked.Increment(ref _id);
                var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
                if (_p.HasExited) throw new InvalidOperationException("the MCP server exited:\n" + Errors);
                await SendAsync(msg.ToString(Newtonsoft.Json.Formatting.None));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    if (_replies.TryRemove(id, out var reply)) return reply;
                    if (_p.HasExited) throw new InvalidOperationException("the MCP server exited:\n" + Errors);
                    await Task.Delay(20);
                }
                throw new TimeoutException($"no reply to {method} within 60s:\n" + Errors);
            }

            public async Task InitializeAsync()
            {
                await RequestAsync("initialize", new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "state-home-verify", ["version"] = "0" },
                });
                var note = new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
                await SendAsync(note.ToString(Newtonsoft.Json.Formatting.None));
            }

            // A server that has already exited (it refused to start or to join) closes its end of stdin. Windows reports a write to it as an
            // IOException ("The pipe is being closed"); Linux lets the write land and the wait loop finds the exit. Both are the same answer.
            private async Task SendAsync(string line)
            {
                try
                {
                    await _p.StandardInput.WriteLineAsync(line);
                    await _p.StandardInput.FlushAsync();
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("the MCP server closed its input:\n" + Errors, ex);
                }
            }

            public async Task<(bool IsError, string Text)> CallToolAsync(string name, JObject arguments = null)
            {
                var reply = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() });
                if (reply["error"] != null) return (true, reply["error"].ToString());
                var result = reply["result"];
                var text = string.Join("\n", result?["content"]?.Select(c => (string)c["text"]) ?? Array.Empty<string>());
                return (result?["isError"]?.Value<bool>() == true, text);
            }

            public void Dispose()
            {
                try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
                try { _p.WaitForExit(5000); } catch { }
                try { _p.Dispose(); } catch { }
            }
        }
    }
}
