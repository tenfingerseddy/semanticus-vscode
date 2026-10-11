// The per-user state folder must never be polluted by throwaway runs, and must never lose what the person saved.
//
// Reported 27 Sep: an agent grading a copy of a model ran `Semanticus.Engine mcp --workspace /tmp/...`, and its first
// open_model rewrote the person's real ~/.semanticus/connections.json and connection-history.json. Both lists are
// capped, so the oldest entries rolled off with no copy kept. On the build laptop 30 of the 40 saved connections were
// /tmp paths left by agent and test runs. These pin the four parts of the fix:
//   1. --state-dir (or SEMANTICUS_HOME) moves every per-user store, for the engine and every process it starts;
//   2. the cap only ever evicts incidental local rows, never a cloud connection or anything the person labelled;
//   3. an open of a model under the system temp folder is not added to the saved connections;
//   4. each rewrite of either file keeps one previous copy (.bak) so a bad write can be undone.
// The verifier's second pass (StateHomeVerifyTests) found six ways round those; section 5 pins the edges of each fix
// that its tests leave open: every malformed spelling is refused, sign-ins and their token cache move with the home,
// the join rule, the throwaway bucket's own caps, and recovery from the good copy on both files.
// Section 6: with HOME naming a folder that does not exist, the engine used to write ./.semanticus (and
// ./Semanticus/auth) under whatever folder it was started in. It now uses the per-user folders or refuses.

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
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root and SEMANTICUS_HOME
    public sealed class StateHomeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public StateHomeTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-statehome-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            ThrowawayPaths.RootsOverride = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- 1. the override ------------------------------------------------------------------------------------

        [Fact]
        public void State_dir_on_the_command_line_moves_the_home_for_this_process_and_every_process_it_starts()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var target = Path.Combine(_root, "state");
            try
            {
                SemanticusHome.ApplyCommandLine(new[] { "mcp", "--workspace", _root, "--state-dir", target });
                Assert.Equal(Path.GetFullPath(target), SemanticusHome.Root());
                // The environment variable is what a child engine inherits (OwnerSpawner does not clear the env).
                Assert.Equal(Path.GetFullPath(target), Environment.GetEnvironmentVariable(SemanticusHome.Variable));
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        [Fact]
        public void No_state_dir_leaves_the_home_as_it_was_and_a_bare_flag_is_refused()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            try
            {
                SemanticusHome.ApplyCommandLine(new[] { "mcp", "--workspace", _root });
                Assert.Equal(before, Environment.GetEnvironmentVariable(SemanticusHome.Variable));
                Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir" }));
                Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir", "--workspace", _root }));
                Assert.Equal(before, Environment.GetEnvironmentVariable(SemanticusHome.Variable));
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        [Fact]
        public void The_license_file_is_read_from_the_same_home_as_every_other_store()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var home = Path.Combine(_root, "lic-home");
            try
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, home);
                Assert.Equal(Path.Combine(home, "license"), LicenseEntitlement.LicenseFilePath());
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        /// <summary>The whole chain, across processes, the way the report hit it: a real engine started as an agent's
        /// MCP server, with --state-dir, opening a model. HOME points at a decoy so "the real home" is a folder this
        /// test can watch; on Windows the profile folder does not follow HOME, so there the decoy half is vacuous and
        /// the state-dir half still proves the writes went where they were told.</summary>
        [EngineApphostFact]
        public async Task An_engine_started_with_state_dir_records_its_open_there_and_writes_nothing_under_home()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws");
            var decoyHome = Path.Combine(_root, "decoy-home");
            var state = Path.Combine(_root, "state-dir");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(decoyHome);
            var bim = Path.Combine(ws, "StateDir.bim");
            TinyBim.Write(bim);
            Mcp mcp = null;
            try
            {
                mcp = Mcp.Start(apphost, ws, decoyHome, "--state-dir", state);
                await mcp.InitializeAsync();
                var opened = await mcp.CallToolAsync("open_model", new JObject { ["path"] = bim });
                Assert.False(opened.IsError, "open_model failed: " + opened.Text + "\n" + mcp.Errors);

                var history = Path.Combine(state, "connection-history.json");
                Assert.True(File.Exists(history), "the open was not recorded in the state dir\n" + mcp.Errors);
                Assert.Contains("StateDir.bim", File.ReadAllText(history));
                Assert.False(Directory.Exists(Path.Combine(decoyHome, ".semanticus")),
                    "the engine wrote under the home folder although --state-dir was given");
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ---- 2. retention ---------------------------------------------------------------------------------------

        [Fact]
        public void Local_opens_never_evict_a_cloud_connection_even_when_there_are_more_than_the_cap()
        {
            var cloud = Enumerable.Range(0, ConnectionRegistry.MaxRecords + 5)
                .Select(i => ConnectionRegistry.Remember("xmla", $"powerbi://api.powerbi.com/v1.0/myorg/Contoso{i}", "Sales", "Sales"))
                .ToList();
            for (var i = 0; i < 100; i++)
                ConnectionRegistry.Remember("file", Path.Combine(_root, $"local{i}.bim"), null, $"local{i}");

            var list = ConnectionRegistry.List();
            foreach (var c in cloud) Assert.Contains(list, r => r.Id == c.Id);
            // The newest local opens are still there, so the one just opened can always be found again.
            Assert.Contains(list, r => r.Endpoint == Path.Combine(_root, "local99.bim"));
        }

        [Fact]
        public void The_cap_evicts_only_unlabelled_local_rows_oldest_first()
        {
            var labelledLocal = ConnectionRegistry.Remember("file", Path.Combine(_root, "labelled.bim"), null, "labelled");
            ConnectionRegistry.SetLabel(labelledLocal.Id, "dev", "human");
            var remembered = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso", "Sales", "Sales");
            var desktop = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "Model", "Model");
            for (var i = 0; i < 100; i++)
                ConnectionRegistry.Remember("file", Path.Combine(_root, $"local{i}.bim"), null, $"local{i}");

            var list = ConnectionRegistry.List();
            Assert.Contains(list, r => r.Id == labelledLocal.Id);
            Assert.Contains(list, r => r.Id == remembered.Id);
            Assert.DoesNotContain(list, r => r.Id == desktop.Id);   // an incidental local row, older than 100 opens
            Assert.Equal(ConnectionRegistry.MaxRecords, list.Count);
            var locals = list.Where(r => r.Kind == "file" && ConnectionRegistry.IsUnlabelled(r)).Select(r => r.Endpoint).ToList();
            var expected = Enumerable.Range(0, 100).Reverse().Take(ConnectionRegistry.MaxRecords - 2)
                .Select(i => Path.Combine(_root, $"local{i}.bim")).ToList();
            Assert.Equal(expected, locals);
        }

        // ---- 3. throwaway paths ---------------------------------------------------------------------------------

        [Fact]
        public void Paths_under_the_system_temp_folder_are_throwaway_and_others_are_not()
        {
            Assert.True(ThrowawayPaths.IsThrowaway(Path.Combine(Path.GetTempPath(), "x", "Model.bim")));
            Assert.True(ThrowawayPaths.IsThrowaway(Path.GetTempPath()));
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(ThrowawayPaths.IsThrowaway("/tmp/claude-1000/b2-grade/raw/model/Graphicus.SemanticModel"));
                Assert.True(ThrowawayPaths.IsThrowaway("/tmp"));
                Assert.False(ThrowawayPaths.IsThrowaway("/tmpfiles/Model.bim"));   // a prefix is not a parent
                Assert.False(ThrowawayPaths.IsThrowaway("/home/someone/repos/Contoso/Model.bim"));
            }
            Assert.False(ThrowawayPaths.IsThrowaway(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Contoso", "Model.bim")));
            Assert.False(ThrowawayPaths.IsThrowaway(null));
            Assert.False(ThrowawayPaths.IsThrowaway(""));
        }

        [Fact]
        public async Task Opening_a_temp_model_keeps_the_history_row_but_does_not_save_the_connection()
        {
            var bim = Path.Combine(_root, "Throwaway.bim");
            TinyBim.Write(bim);
            using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _root))
                await engine.OpenAsync(bim);

            Assert.DoesNotContain(ConnectionRegistry.List(), r => string.Equals(r.Endpoint, Path.GetFullPath(bim), StringComparison.Ordinal));
            Assert.Contains(ConnectionHistory.List(), e => e.Kind == "open" && string.Equals(e.Endpoint, Path.GetFullPath(bim), StringComparison.Ordinal));
        }

        [Fact]
        public async Task Opening_a_model_outside_the_temp_folders_still_saves_the_connection()
        {
            // Nothing in a test may write outside temp, so move the throwaway roots instead of the model.
            ThrowawayPaths.RootsOverride = new[] { Path.Combine(_root, "elsewhere") };
            var bim = Path.Combine(_root, "Kept.bim");
            TinyBim.Write(bim);
            using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _root))
                await engine.OpenAsync(bim);

            Assert.Contains(ConnectionRegistry.List(), r => r.Kind == "file" && string.Equals(r.Endpoint, Path.GetFullPath(bim), StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_temp_model_the_person_already_saved_is_still_refreshed_on_open()
        {
            var bim = Path.Combine(_root, "Saved.bim");
            TinyBim.Write(bim);
            var saved = ConnectionRegistry.Remember("file", Path.GetFullPath(bim), null, "Saved");
            ConnectionRegistry.SetLabel(saved.Id, "dev", "human");
            using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _root))
                await engine.OpenAsync(bim);

            var after = ConnectionRegistry.Find(saved.Id);
            Assert.NotNull(after);
            Assert.Equal(2, after.UseCount);
            Assert.Equal("dev", after.Label);
        }

        // ---- 4. one previous copy -------------------------------------------------------------------------------

        [Fact]
        public void Rewriting_the_connection_list_keeps_the_previous_file_as_a_backup()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/First", "Sales", "Sales");
            var path = Path.Combine(_root, "connections.json");
            var first = File.ReadAllBytes(path);
            Assert.False(File.Exists(path + ".bak"));   // nothing to keep on the very first write

            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Second", "Sales", "Sales");
            Assert.Equal(first, File.ReadAllBytes(path + ".bak"));
            Assert.Contains("Second", File.ReadAllText(path));
            Assert.DoesNotContain("Second", File.ReadAllText(path + ".bak"));

            // One copy, not a pile: the next write replaces the .bak with the file it just superseded.
            var second = File.ReadAllBytes(path);
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Third", "Sales", "Sales");
            Assert.Equal(second, File.ReadAllBytes(path + ".bak"));
            Assert.Empty(Directory.GetFiles(_root, "connections.json.bak*").Where(f => !f.EndsWith("connections.json.bak", StringComparison.Ordinal)));
        }

        [Fact]
        public void Rewriting_the_connection_history_keeps_the_previous_file_as_a_backup()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "first-endpoint", Ok = true });
            var path = Path.Combine(_root, "connection-history.json");
            var first = File.ReadAllBytes(path);
            Assert.False(File.Exists(path + ".bak"));

            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "second-endpoint", Ok = true });
            Assert.Equal(first, File.ReadAllBytes(path + ".bak"));
            Assert.Contains("second-endpoint", File.ReadAllText(path));
            Assert.DoesNotContain("second-endpoint", File.ReadAllText(path + ".bak"));

            var second = File.ReadAllBytes(path);
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "third-endpoint", Ok = true });
            Assert.Equal(second, File.ReadAllBytes(path + ".bak"));
        }

        // ---- 5. the second pass ---------------------------------------------------------------------------------

        [Fact]
        public void Every_malformed_state_dir_spelling_is_refused_and_the_equals_form_is_honoured()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var target = Path.Combine(_root, "state");
            try
            {
                foreach (var bad in new[] { "--STATE-DIR", "-state-dir", "--statedir", "--state_dir", "--state-dir:" + target, "--state-dirx" })
                {
                    var ex = Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", bad, target }));
                    Assert.Contains(bad, ex.Message);
                    Assert.Equal(before, Environment.GetEnvironmentVariable(SemanticusHome.Variable));
                }
                Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir=" }));
                Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir=  " }));
                // A folder that happens to look like the option is the space form's VALUE, not a second option.
                SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir", Path.Combine(_root, "statedir") });
                Assert.Equal(Path.GetFullPath(Path.Combine(_root, "statedir")), SemanticusHome.Root());
                SemanticusHome.ApplyCommandLine(new[] { "mcp", "--workspace", _root, "--state-dir=" + target });
                Assert.Equal(Path.GetFullPath(target), SemanticusHome.Root());
                // Given twice, in either form, the first one wins.
                SemanticusHome.ApplyCommandLine(new[] { "mcp", "--state-dir=" + target + "-1", "--state-dir", target + "-2" });
                Assert.Equal(Path.GetFullPath(target + "-1"), SemanticusHome.Root());
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        [Fact]
        public void A_moved_home_moves_the_sign_in_store_and_gives_its_token_cache_its_own_name()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var beforeSignIn = Environment.GetEnvironmentVariable(SemanticusHome.SignInVariable);
            var home = Path.Combine(_root, "state");
            try
            {
                Environment.SetEnvironmentVariable(SemanticusHome.SignInVariable, null);
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, home);
                Assert.Equal(Path.Combine(home, "auth"), SemanticusHome.SignInDir());
                // The person's store keeps the production cache name; any other store gets a name of its own, so a run
                // with its own home cannot read or refresh the person's cached tokens either.
                Assert.Equal("semanticus-xmla", EntraToken.CacheNameFor(SemanticusHome.DefaultSignInDir()));
                var moved = EntraToken.CacheNameFor(SemanticusHome.SignInDir());
                Assert.StartsWith("semanticus-xmla-", moved);
                Assert.Equal(moved, EntraToken.CacheNameFor(SemanticusHome.SignInDir() + Path.DirectorySeparatorChar));
                Assert.NotEqual(moved, EntraToken.CacheNameFor(Path.Combine(_root, "other", "auth")));
                // Only a caller that names a sign-in store on purpose gets that one. (A scratch one here: naming the
                // person's real store in-process would hand it to every test running beside this one.)
                var named = Path.Combine(_root, "named-auth");
                Environment.SetEnvironmentVariable(SemanticusHome.SignInVariable, named);
                Assert.Equal(Path.GetFullPath(named), SemanticusHome.SignInDir());
                Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(SemanticusHome.Root()));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SemanticusHome.SignInVariable, beforeSignIn);
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, before);
            }
        }

        [Fact]
        public void A_run_that_chose_its_home_never_joins_an_owner_that_keeps_state_elsewhere()
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var mine = Path.Combine(_root, "mine");
            var theirs = Path.Combine(_root, "theirs");
            try
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, mine);
                EngineInfo Owner(string home, string signIn) => new EngineInfo { PipeName = "p", Pid = 1, Home = home, SignInDir = signIn };
                Assert.False(EngineBroker.StateFolderMismatch(Owner(mine, Path.Combine(mine, "auth"))));
                Assert.False(EngineBroker.StateFolderMismatch(Owner(mine + Path.DirectorySeparatorChar, Path.Combine(mine, "auth"))));
                Assert.True(EngineBroker.StateFolderMismatch(Owner(theirs, Path.Combine(theirs, "auth"))));
                Assert.True(EngineBroker.StateFolderMismatch(Owner(mine, Path.Combine(theirs, "auth"))));
                // A record too old to say where it keeps state cannot prove it is the same folder, so it is not joined.
                Assert.True(EngineBroker.StateFolderMismatch(Owner(null, null)));

                // The record an owner publishes carries its folders, so the check has something to compare.
                var ws = Path.Combine(_root, "ws");
                EngineBroker.WriteInfo(ws, new EngineInfo { PipeName = "p", Pid = Environment.ProcessId, Workspace = ws });
                var published = EngineBroker.ReadInfo(ws);
                Assert.Equal(Path.GetFullPath(mine), published.Home);
                Assert.Equal(Path.GetFullPath(Path.Combine(mine, "auth")), published.SignInDir);
                Assert.False(EngineBroker.StateFolderMismatch(published));
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        [Fact]
        public void Throwaway_opens_are_capped_among_themselves_and_never_count_toward_the_total()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "switch", Account = "person@contoso.com", Ok = true });
            for (var i = 0; i < ConnectionHistory.MaxPerConnection + 10; i++)
                ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "/tmp/copy-" + i + ".bim", Ok = true, Throwaway = true });
            var rows = ConnectionHistory.List();
            Assert.Equal(ConnectionHistory.MaxPerConnection, rows.Count(e => e.Throwaway == true));
            Assert.Contains(rows, e => e.Kind == "switch");
            // The newest throwaway rows are the ones kept.
            Assert.Contains(rows, e => e.Endpoint == "/tmp/copy-" + (ConnectionHistory.MaxPerConnection + 9) + ".bim");
            Assert.DoesNotContain(rows, e => e.Endpoint == "/tmp/copy-0.bim");

            // A full timeline of the person's own rows keeps every one of them beside the throwaway bucket.
            var path = Path.Combine(_root, "connection-history.json");
            var own = Enumerable.Range(0, ConnectionHistory.MaxTotal).Select(i => new JObject
            {
                ["id"] = i.ToString("x8"), ["kind"] = "open", ["endpoint"] = "/models/m" + i + ".bim", ["ok"] = true,
                ["whenUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1 - i).ToString("O"),
            });
            File.WriteAllText(path, new JArray(own).ToString());
            for (var i = 0; i < 3; i++)
                ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "/tmp/late-" + i + ".bim", Ok = true, Throwaway = true });
            rows = ConnectionHistory.List();
            Assert.Equal(ConnectionHistory.MaxTotal, rows.Count(e => e.Throwaway != true));
            Assert.Equal(3, rows.Count(e => e.Throwaway == true));
        }

        [Fact]
        public void A_torn_connection_list_shows_the_previous_good_copy()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/KeptA", "Sales", "Sales");
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/KeptB", "Sales", "Sales");
            var path = Path.Combine(_root, "connections.json");
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);

            Assert.Equal(2, ConnectionRegistry.List().Count(r => r.Endpoint.Contains("/Kept", StringComparison.Ordinal)));
            // The first write after it starts from that copy, sets the torn bytes aside, and leaves .bak alone.
            var good = File.ReadAllBytes(path + ".bak");
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/After", "Sales", "Sales");
            Assert.Equal(3, ConnectionRegistry.List().Count);
            Assert.Equal(good, File.ReadAllBytes(path + ".bak"));
            Assert.Single(Directory.GetFiles(_root, "connections.json.corrupt-*"));
        }

        [Fact]
        public void After_a_torn_history_the_next_write_starts_from_the_previous_good_copy()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "switch", Account = "person@contoso.com", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "kept-endpoint", Ok = true });
            var path = Path.Combine(_root, "connection-history.json");
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);

            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "next-endpoint", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "after-endpoint", Ok = true });
            var rows = ConnectionHistory.List();
            Assert.Contains(rows, e => e.Kind == "switch");
            Assert.Contains(rows, e => e.Endpoint == "kept-endpoint");
            Assert.Contains(rows, e => e.Endpoint == "after-endpoint");
        }

        /// <summary>The timeline's display read shows .bak for an unreadable file before any write. Round 3 found
        /// nothing failed without that fallback (its T2).</summary>
        [Fact]
        public void A_torn_history_shows_the_previous_good_copy_before_any_write()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "switch", Account = "person@contoso.com", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Kind = "open", Endpoint = "kept-endpoint", Ok = true });
            var path = Path.Combine(_root, "connection-history.json");
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);

            var rows = ConnectionHistory.List();
            Assert.Contains(rows, e => e.Kind == "switch");
            Assert.Contains(rows, e => e.Endpoint == "kept-endpoint");
            Assert.Empty(Directory.GetFiles(_root, "connection-history.json.corrupt-*"));   // a read never moves anything
        }

        /// <summary>A write that fails after reading a torn list leaves the torn file where it was, so the list still
        /// shows the good copy and the next write recovers from it: only a write that lands sets the torn bytes aside
        /// (round 3, N3). Here the failure is a folder squatting on the .tmp path.</summary>
        [Fact]
        public void A_write_that_fails_after_a_torn_list_moves_nothing_aside()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/KeptA", "Sales", "Sales");
            var path = Path.Combine(_root, "connections.json");
            File.Copy(path, path + ".bak", overwrite: true);
            var torn = new byte[new FileInfo(path).Length];
            File.WriteAllBytes(path, torn);

            Directory.CreateDirectory(path + ".tmp");
            Assert.ThrowsAny<Exception>(() => ConnectionRegistry.Remember("file", Path.Combine(_root, "Next.bim"), null, "Next"));
            Directory.Delete(path + ".tmp");
            Assert.Equal(torn, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(_root, "connections.json.corrupt-*"));
            Assert.Contains(ConnectionRegistry.List(), r => r.Endpoint.EndsWith("/KeptA", StringComparison.Ordinal));
        }

        /// <summary>The token cache a real credential attaches is the moved store's own, never the person's production
        /// cache. Round 3 found the only test called CacheNameFor directly, so putting the production name back in the
        /// credential builders failed nothing (its T1). This test run's home is moved, exactly like an isolated run.</summary>
        [Fact]
        public void The_credentials_a_moved_home_builds_attach_its_own_token_cache_never_the_persons()
        {
            var prevName = EntraToken.TokenCacheNameOverride;
            var beforeSignIn = Environment.GetEnvironmentVariable(SemanticusHome.SignInVariable);
            try
            {
                EntraToken.TokenCacheNameOverride = null;
                Environment.SetEnvironmentVariable(SemanticusHome.SignInVariable, null);
                Assert.True(SemanticusHome.IsMoved);
                var own = EntraToken.CacheNameFor(SemanticusHome.SignInDir());
                Assert.NotEqual("semanticus-xmla", own);
                Assert.Equal(own, EntraToken.InteractiveOptionsForTests(null, false).TokenCachePersistenceOptions.Name);
                Assert.Equal(own, EntraToken.InteractiveOptionsForTests(null, true).TokenCachePersistenceOptions.Name);
                Assert.Equal(own, EntraToken.DeviceCodeOptionsForTests(null, false).TokenCachePersistenceOptions.Name);
            }
            finally
            {
                EntraToken.TokenCacheNameOverride = prevName;
                Environment.SetEnvironmentVariable(SemanticusHome.SignInVariable, beforeSignIn);
            }
        }

        /// <summary>The person's own engine (it chose no state folder) joins only an owner whose stamped home is the
        /// person's own. An owner an isolated run started with a folder of its own is refused, with a line that names
        /// both folders and the one way to join that folder on purpose (round 3, N1). An unstamped owner predates
        /// --state-dir and is joined as before, and the sign-in store is not compared, because its default follows
        /// XDG_DATA_HOME.</summary>
        [Fact]
        public void The_persons_own_engine_joins_only_an_owner_that_keeps_state_in_the_persons_home()
        {
            var mine = Path.Combine(_root, "person", ".semanticus");
            var theirs = Path.Combine(_root, "isolated");
            EngineInfo Owner(string home, string signIn) => new EngineInfo { PipeName = "p", Pid = 1, Home = home, SignInDir = signIn };
            string NotCompared() => throw new InvalidOperationException("the sign-in store is not compared for an engine that chose nothing");

            Assert.False(EngineBroker.StateFolderMismatch(Owner(mine, Path.Combine(_root, "elsewhere", "auth")), false, mine, NotCompared));
            Assert.False(EngineBroker.StateFolderMismatch(Owner(mine + Path.DirectorySeparatorChar, null), false, mine, NotCompared));
            Assert.False(EngineBroker.StateFolderMismatch(Owner(null, null), false, mine, NotCompared));
            Assert.True(EngineBroker.StateFolderMismatch(Owner(theirs, Path.Combine(theirs, "auth")), false, mine, NotCompared));
            Assert.True(EngineBroker.StateFolderMismatch(Owner(theirs, null), false, mine, NotCompared));

            var line = EngineBroker.McpOwnerUsesAnotherStateFolderMessage(theirs, mine);
            Assert.Contains(theirs, line);
            Assert.Contains(mine, line);
            Assert.Contains(SemanticusHome.Option + " " + theirs, line);
            Assert.DoesNotContain('\u2014', line);
            Assert.DoesNotContain('\u2013', line);
        }

        /// <summary>The locks left the home for a folder no state folder moves (round 3, N4), so an engine older than
        /// that change, still running during an update, takes them under ~/.semanticus/locks. The person's own engine
        /// takes that legacy lock as well, keeps a legacy run record, and reads an older engine's record as alive, so
        /// the two still exclude each other. A test run names its own lock folder, which no older engine shares, so it
        /// takes none of it.</summary>
        [Fact]
        public async Task The_persons_engine_still_excludes_an_older_engine_that_locks_under_its_home()
        {
            var legacyRoot = Path.Combine(_root, "person-home", ".semanticus", "locks");
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            Directory.CreateDirectory(legacyRoot);
            string Legacy(string lockPath) => Path.Combine(legacyRoot, Path.GetFileName(lockPath));
            SemanticusHome.LegacyLockRootForTests = () => legacyRoot;
            try
            {
                Assert.Equal(Legacy(SidecarFileLock.PathFor(sidecar)), SidecarFileLock.LegacyPathFor(sidecar));
                Task<IDisposable> current;
                using (new FileStream(Legacy(SidecarFileLock.PathFor(sidecar)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1))
                {
                    current = Task.Run(() => SidecarFileLock.Acquire(sidecar));      // an older engine is writing
                    Assert.False(current.Wait(TimeSpan.FromMilliseconds(500)), "a current engine took the History lock while an older one held it");
                }
                (await current).Dispose();

                using (new FileStream(Legacy(EngineRunLiveness.PathFor("run-old1")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    Assert.True(EngineRunLiveness.IsAlive("run-old1"), "an older engine's live run was read as gone");
                Assert.False(EngineRunLiveness.IsAlive("run-old1"));

                EngineRunLiveness.Hold("run-new1");
                try
                {
                    Assert.Throws<IOException>(() => new FileStream(Legacy(EngineRunLiveness.PathFor("run-new1")), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1));
                }
                finally { EngineRunLiveness.Release("run-new1"); }
                Assert.False(File.Exists(Legacy(EngineRunLiveness.PathFor("run-new1"))));
            }
            finally { SemanticusHome.LegacyLockRootForTests = null; }
            Assert.Null(SidecarFileLock.LegacyPathFor(sidecar));   // this test run names its lock folder
            Assert.Single(EngineRunLiveness.PathsFor("run-x"));
        }

        /// <summary>A run given a state folder of its own can open the person's real model while a RELEASED engine of the
        /// person's runs, which locks under ~/.semanticus/locks (Sol, 28 Sep). The run waits for that lock and reads that
        /// engine's live run as alive, READ-ONLY: it never creates, writes or deletes anything in the person's folder.</summary>
        [Fact]
        public async Task A_run_with_its_own_state_folder_waits_for_a_released_engine_and_writes_nothing_in_the_persons_home()
        {
            var personLocks = Path.Combine(_root, "person-home", ".semanticus", "locks");
            Directory.CreateDirectory(personLocks);
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            var released = Path.Combine(personLocks, Path.GetFileName(SidecarFileLock.PathFor(sidecar)));
            var releasedRun = Path.Combine(personLocks, Path.GetFileName(EngineRunLiveness.PathFor("run-old2")));
            SemanticusHome.PersonLegacyLockRootForTests = () => personLocks;
            try
            {
                SidecarFileLock.Acquire(sidecar).Dispose();                  // the released engine never wrote this model
                Assert.False(EngineRunLiveness.IsAlive("run-old2"));
                Assert.Empty(Directory.GetFileSystemEntries(personLocks));   // nothing was created there

                File.WriteAllBytes(released, Array.Empty<byte>());
                File.WriteAllBytes(releasedRun, Array.Empty<byte>());
                var stamp = File.GetLastWriteTimeUtc(released);
                Task<IDisposable> current;
                using (new FileStream(released, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1))
                {
                    current = Task.Run(() => SidecarFileLock.Acquire(sidecar));
                    Assert.False(current.Wait(TimeSpan.FromMilliseconds(500)), "the run took the History lock while a released engine held it");
                }
                (await current).Dispose();
                using (new FileStream(releasedRun, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1))
                    Assert.True(EngineRunLiveness.IsAlive("run-old2"), "a released engine's live run was read as gone");
                Assert.False(EngineRunLiveness.IsAlive("run-old2"));

                Assert.Equal(new[] { released, releasedRun }.OrderBy(x => x, StringComparer.Ordinal),
                    Directory.GetFileSystemEntries(personLocks).OrderBy(x => x, StringComparer.Ordinal));   // nothing added or deleted
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(released));
                Assert.Equal(0, new FileInfo(released).Length);
            }
            finally { SemanticusHome.PersonLegacyLockRootForTests = null; }
        }

        /// <summary>Two names for one lock folder (a symbolic link between them) must not make an engine take one lock
        /// file twice and wait on itself (Sol, 28 Sep).</summary>
        [Fact]
        public void A_legacy_lock_folder_that_is_the_lock_folder_by_another_name_is_taken_once()
        {
            if (OperatingSystem.IsWindows()) return;   // symbolic links need privileges there; the check is the same code
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            Directory.CreateDirectory(SemanticusHome.LockRoot());
            var alias = Path.Combine(_root, "locks-alias");
            Directory.CreateSymbolicLink(alias, SemanticusHome.LockRoot());
            SemanticusHome.LegacyLockRootForTests = () => alias;
            try
            {
                Assert.Null(SidecarFileLock.LegacyPathFor(sidecar));
                Assert.Single(EngineRunLiveness.PathsFor("run-alias"));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                SidecarFileLock.Acquire(sidecar).Dispose();
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the engine waited on its own lock");
            }
            finally { SemanticusHome.LegacyLockRootForTests = null; }
        }

        /// <summary>One Acquire takes each lock file once, even when no folder check saw that two of its names are one
        /// file. The folder checks (SamePlace, in LegacyPathFor and PersonLegacyLockRoot) drop a second name before it
        /// gets there, so this reaches the line behind them: the person's legacy folder is handed in as the lock folder
        /// itself, past those checks. Without the one-take guard the read-only open waits on this engine's own lock and
        /// then blames it (verifier, targeted round, L3: this guard had no test of its own).</summary>
        [Fact]
        public void One_acquire_takes_one_lock_file_once_even_past_the_folder_checks()
        {
            var sidecar = Path.Combine(_root, "Sales.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            Directory.CreateDirectory(SemanticusHome.LockRoot());
            SemanticusHome.PersonLegacyLockRootForTests = () => SemanticusHome.LockRoot();
            SidecarFileLock.WaitForTests = TimeSpan.FromSeconds(2);
            try
            {
                Assert.Equal(SidecarFileLock.PathFor(sidecar), SidecarFileLock.PersonLegacyPathFor(sidecar));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Exception failed = null;
                try { SidecarFileLock.Acquire(sidecar).Dispose(); }
                catch (Exception ex) { failed = ex; }
                Assert.True(failed == null && clock.Elapsed < TimeSpan.FromSeconds(1.5),
                    $"one lock file reached by two names in one Acquire was taken twice: waited {clock.Elapsed.TotalSeconds:F1}s"
                    + (failed == null ? "" : " and failed: " + failed.Message));
            }
            finally
            {
                SemanticusHome.PersonLegacyLockRootForTests = null;
                SidecarFileLock.WaitForTests = null;
            }
        }

        /// <summary>Two real engines on one model, each with its own state folder and its own XDG_DATA_HOME, and neither
        /// told where the locks go (a test run names its lock folder for every process it starts, which hid this: round
        /// 4, R3). Both take the model's History lock in the one per-user lock folder under HOME, never under a state
        /// folder and never under XDG_DATA_HOME, so while one holds it the other waits. Proved on the lock file itself:
        /// the first engine's edit creates it there, and while this test holds that file the second engine's edit cannot
        /// reach the History.</summary>
        [EngineApphostFact(unixOnly: true)]
        public async Task Engines_with_different_state_folders_lock_one_models_history_in_the_one_per_user_lock_folder()
        {
            var apphost = EngineApphost.Find();
            var decoy = Path.Combine(_root, "decoy-home-locks");
            var modelDir = Path.Combine(_root, "model");
            Directory.CreateDirectory(decoy);
            Directory.CreateDirectory(modelDir);
            var bim = Path.Combine(modelDir, "Locks.bim");
            TinyBim.Write(bim);
            var shared = Path.Combine(decoy, ".local", "share", "Semanticus", "locks");
            var history = Path.Combine(modelDir, ".semanticus", EditLedger.FileName);
            Mcp a = null, b = null;
            var owners = new List<string>();
            try
            {
                Mcp Engine(string name) => Mcp.StartWith(apphost, Path.Combine(_root, "ws-" + name), decoy,
                    new Dictionary<string, string> { [SemanticusHome.LockVariable] = null, ["XDG_DATA_HOME"] = Path.Combine(_root, "xdg-" + name) },
                    "--state-dir", Path.Combine(_root, "state-" + name));
                foreach (var n in new[] { "a", "b" }) { Directory.CreateDirectory(Path.Combine(_root, "ws-" + n)); owners.Add(Path.Combine(_root, "ws-" + n)); }

                a = Engine("a");
                await a.InitializeAsync();
                Assert.False((await a.CallToolAsync("open_model", new JObject { ["path"] = bim })).IsError, a.Errors);
                Assert.False((await a.CallToolAsync("set_description", new JObject { ["objRef"] = "table:Customer", ["text"] = "Written by engine A." })).IsError, a.Errors);
                string lockFile = null;
                for (var i = 0; i < 200 && lockFile == null; i++)
                {
                    lockFile = Directory.Exists(shared) ? Directory.GetFiles(shared, "sidecar-*.lock").FirstOrDefault() : null;
                    if (lockFile == null) await Task.Delay(50);
                }
                Assert.True(lockFile != null, $"engine A took no History lock in the per-user lock folder {shared}; lock files it made: "
                    + string.Join(", ", Directory.GetFiles(_root, "*.lock", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f))));
                Assert.False(Directory.Exists(Path.Combine(_root, "xdg-a", "Semanticus")), "engine A made a lock folder under XDG_DATA_HOME");
                for (var i = 0; i < 200 && !(File.Exists(history) && File.ReadAllText(history).Contains("Written by engine A.")); i++) await Task.Delay(50);
                Assert.Contains("Written by engine A.", File.ReadAllText(history));

                b = Engine("b");
                await b.InitializeAsync();
                Assert.False((await b.CallToolAsync("open_model", new JObject { ["path"] = bim })).IsError, b.Errors);
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // "the person's engine" holds the lock
                {
                    Assert.False((await b.CallToolAsync("set_description", new JObject { ["objRef"] = "table:Date", ["text"] = "Written by engine B." })).IsError, b.Errors);
                    await Task.Delay(3000);
                    Assert.False(File.ReadAllText(history).Contains("Written by engine B."),
                        "engine B wrote the model's History while the per-user lock was held: it took its lock somewhere else");
                }
                for (var i = 0; i < 300 && !File.ReadAllText(history).Contains("Written by engine B."); i++) await Task.Delay(50);
                Assert.True(File.ReadAllText(history).Contains("Written by engine B."), "engine B never wrote the History after the lock was let go\n" + b.Errors);
                Assert.False(Directory.Exists(Path.Combine(_root, "xdg-b", "Semanticus")), "engine B made a lock folder under XDG_DATA_HOME");
            }
            finally
            {
                var pids = owners.Select(w => EngineBroker.ReadInfo(w)?.Pid).Where(p => p.HasValue).Select(p => p.Value).ToList();
                b?.Dispose();
                a?.Dispose();
                foreach (var pid in pids.Where(p => p != Environment.ProcessId))
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        /// <summary>A wait that runs out on a lock this engine holds itself says so; it never blames another window
        /// (round 4: an engine waited on itself and named "another Semanticus window" that did not exist).</summary>
        [Fact]
        public void A_lock_this_engine_holds_itself_is_never_blamed_on_another_window()
        {
            var sidecar = Path.Combine(_root, "Self.SemanticModel", ".semanticus", "plan-memory.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar));
            using (SidecarFileLock.Acquire(sidecar))
            {
                SidecarFileLock.WaitForTests = TimeSpan.FromMilliseconds(200);
                try
                {
                    var ex = Assert.Throws<IOException>(() => SidecarFileLock.Acquire(sidecar));
                    Assert.DoesNotContain("window", ex.Message);
                    Assert.DoesNotContain("Another", ex.Message);
                    Assert.Contains("This Semanticus engine", ex.Message);
                }
                finally { SidecarFileLock.WaitForTests = null; }
            }
            SidecarFileLock.Acquire(sidecar).Dispose();   // and it is free again once let go
        }

        /// <summary>Characters that look like a dash but are not DashPunctuation, and invisible format characters in
        /// front of the option, used to leave the option ignored and the run on the real home (round 4, R6).</summary>
        [Theory]
        [InlineData("\u00AD\u00ADstate-dir")]      // two soft hyphens
        [InlineData("\u02D7\u02D7state-dir")]      // modifier minus
        [InlineData("\u207B\u207Bstate-dir")]      // superscript minus
        [InlineData("\u208B\u208Bstate-dir")]      // subscript minus
        [InlineData("\u200B--state-dir")]           // a zero-width space before it
        [InlineData("\uFEFF--state-dir")]           // a byte-order mark before it
        [InlineData("--state\u2060-dir")]           // a word joiner inside it
        public void A_state_dir_behind_a_look_alike_or_an_invisible_character_is_refused_never_ignored(string option)
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            try
            {
                Assert.Throws<ArgumentException>(() => SemanticusHome.ApplyCommandLine(new[] { "mcp", "--workspace", _root, option, Path.Combine(_root, "s") }));
            }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        /// <summary>An unlabelled loopback target reads as "local" at the gate, not prod. So a .bak one write behind a
        /// person who labelled it prod would loosen it by carrying no label: a recovered loopback row comes back as prod
        /// too (found by Sol, 28 Sep).</summary>
        [Fact]
        public void Recovering_a_list_never_loosens_a_loopback_target_the_person_labelled_prod()
        {
            var rec = ConnectionRegistry.Remember("localDesktop", "localhost:52640", "Sales", "Sales");
            Assert.Equal("local", LocalEngine.ResolveTargetLabel("localhost:52640", "Sales"));
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");          // .bak now holds it unlabelled
            File.WriteAllText(Path.Combine(_root, "connections.json"), "{ this is no longer the list");

            var shown = LocalEngine.ResolveTargetLabel("localhost:52640", "Sales");
            ConnectionRegistry.Remember("file", Path.Combine(_root, "Next.bim"), null, "Next");   // the next ordinary write
            var afterWrite = LocalEngine.ResolveTargetLabel("localhost:52640", "Sales");
            Assert.True(shown == "prod" && afterWrite == "prod",
                $"the person labelled this loopback target prod; recovered from .bak the gate reads '{shown}', and after the next write '{afterWrite}'");
        }

        // ---- 6. no per-user folder -------------------------------------------------------------------------------

        /// <summary>HOME names a folder that does not exist and nothing moves the home. .NET then answers "" for the
        /// profile and data folders, and every store used to resolve against the working folder instead. The engine
        /// must refuse with a plain message, and write nothing: not under its working folder, and not by creating
        /// the missing HOME either.</summary>
        [EngineApphostFact(unixOnly: true)]
        public async Task An_engine_whose_home_folder_is_missing_writes_nothing_under_its_working_folder()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws-nohome");
            var cwd = Path.Combine(_root, "cwd-nohome");
            var missingHome = Path.Combine(_root, "no-such-home");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(cwd);
            Mcp mcp = null;
            try
            {
                mcp = Mcp.StartIn(apphost, ws, missingHome, cwd);
                try
                {
                    await mcp.InitializeAsync();
                    await mcp.CallToolAsync("list_account_profiles");
                    await mcp.CallToolAsync("remember_xmla_connection", new JObject
                    {
                        ["endpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/NoHome",
                        ["database"] = "Sales",
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException) { /* refused to start: expected */ }

                Assert.False(Directory.Exists(Path.Combine(cwd, ".semanticus")), "the engine wrote ./.semanticus under its working folder\n" + mcp.Errors);
                Assert.False(Directory.Exists(Path.Combine(cwd, "Semanticus")), "the engine wrote ./Semanticus/auth under its working folder\n" + mcp.Errors);
                Assert.False(Directory.Exists(missingHome), "the engine created the missing HOME folder\n" + mcp.Errors);
                Assert.Contains("HOME", mcp.Errors);
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        /// <summary>A real HOME on a fresh machine may have no ~/.local/share yet. The sign-in store belongs there all
        /// the same (it is the platform per-user data folder), never under the folder the engine happened to start in.</summary>
        [EngineApphostFact(unixOnly: true)]
        public async Task A_home_with_no_local_share_folder_keeps_sign_ins_under_home_not_the_working_folder()
        {
            var apphost = EngineApphost.Find();
            var ws = Path.Combine(_root, "ws-noshare");
            var cwd = Path.Combine(_root, "cwd-noshare");
            var decoyHome = Path.Combine(_root, "home-noshare");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(decoyHome);
            Mcp mcp = null;
            try
            {
                mcp = Mcp.StartIn(apphost, ws, decoyHome, cwd);
                await mcp.InitializeAsync();
                var listed = await mcp.CallToolAsync("list_account_profiles");
                Assert.False(listed.IsError, listed.Text + "\n" + mcp.Errors);
                Assert.False(Directory.Exists(Path.Combine(cwd, "Semanticus")), "the sign-in store was created under the working folder\n" + mcp.Errors);
                Assert.False(Directory.Exists(Path.Combine(cwd, ".semanticus")), "the state folder was created under the working folder\n" + mcp.Errors);
                Assert.True(Directory.Exists(Path.Combine(decoyHome, ".local", "share", "Semanticus", "auth")),
                    "the sign-in store is not under HOME/.local/share\n" + mcp.Errors);
            }
            finally
            {
                var owner = EngineBroker.ReadInfo(ws);
                mcp?.Dispose();
                if (owner?.Pid is int pid && pid != Environment.ProcessId)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        /// <summary>A minimal MCP stdio client (newline-delimited JSON-RPC) that starts the engine with a chosen
        /// HOME and extra arguments, and with SEMANTICUS_HOME removed so only the flag under test can move it.</summary>
        private sealed class Mcp : IDisposable
        {
            private readonly Process _p;
            private readonly ConcurrentDictionary<int, JObject> _replies = new ConcurrentDictionary<int, JObject>();
            private readonly StringBuilder _err = new StringBuilder();
            private int _id;
            private Mcp(Process p) { _p = p; }
            public string Errors { get { lock (_err) return _err.ToString(); } }

            public static Mcp Start(string apphost, string ws, string home, params string[] extra) => StartIn(apphost, ws, home, null, extra);

            /// <summary>As <see cref="Start"/>, with environment variables set (a null value removes one) after the rest.</summary>
            public static Mcp StartWith(string apphost, string ws, string home, IDictionary<string, string> env, params string[] extra)
                => StartIn(apphost, ws, home, null, env, extra);

            public static Mcp StartIn(string apphost, string ws, string home, string cwd, params string[] extra) => StartIn(apphost, ws, home, cwd, null, extra);

            /// <summary>As <see cref="Start"/>, in a chosen working folder. The XDG folders are removed too, so the
            /// per-user folders can only come from HOME, which is the decoy.</summary>
            public static Mcp StartIn(string apphost, string ws, string home, string cwd, IDictionary<string, string> env, params string[] extra)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = apphost,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false,
                };
                if (cwd != null) psi.WorkingDirectory = cwd;
                foreach (var k in new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" }) psi.Environment.Remove(k);
                foreach (var a in new[] { "mcp", "--workspace", ws }.Concat(extra)) psi.ArgumentList.Add(a);
                var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
                psi.Environment["DOTNET_ROOT"] = runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;
                psi.Environment["HOME"] = home;
                psi.Environment["USERPROFILE"] = home;
                psi.Environment.Remove(SemanticusHome.Variable);
                psi.Environment["SEMANTICUS_DEV_PRO"] = "1";
                psi.Environment[ParentLifetime.GraceEnvironmentVariable] = "1000";
                foreach (var kv in env ?? new Dictionary<string, string>())
                    if (kv.Value == null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value;
                var client = new Mcp(Process.Start(psi));
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
                await _p.StandardInput.WriteLineAsync(msg.ToString(Newtonsoft.Json.Formatting.None));
                await _p.StandardInput.FlushAsync();
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
                    ["clientInfo"] = new JObject { ["name"] = "state-home-test", ["version"] = "0" },
                });
                var note = new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
                await _p.StandardInput.WriteLineAsync(note.ToString(Newtonsoft.Json.Formatting.None));
                await _p.StandardInput.FlushAsync();
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
