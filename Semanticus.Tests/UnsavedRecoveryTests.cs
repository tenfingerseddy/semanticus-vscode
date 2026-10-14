using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // The restore root is one process-wide setting. This class swaps it per test, so it must not run beside the classes that write restore points
    // (DeploySpeed, LiveDeploy, publish): in their collection they run one after another. CI run 5 (Windows) saw their folder here.
    [Collection("restore-root")]
    public sealed class UnsavedRecoveryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "smx-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldRoot = RestorePointStore.RootOverride;
        public UnsavedRecoveryTests()
        {
            Directory.CreateDirectory(_root);
            RestorePointStore.RootOverride = Path.Combine(_root, "state", "restore");
        }
        public void Dispose()
        {
            RestorePointStore.RecoveryIoHookForTest = null;
            RestorePointStore.RootOverride = _oldRoot;
            // PR #399 CI run 4: another test's session can be mid-write into this folder (the override is process wide), and Windows then
            // refuses to delete the open .unsaved-*.tmp. The store points away first, then waits for every writer, then deletes with a retry.
            RestorePointStore.WaitForWriters();
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(_root, true); return; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }

        private string RecoveryDir(string path) => Path.GetDirectoryName(RestorePointStore.UnsavedPath(path))!;

        [Fact]
        public void A_reader_waits_out_a_process_holding_the_recovery_copy_open()
        {
            var path = ModelPath();
            RestorePointStore.WriteUnsaved(path, "{\"name\":\"x\"}");
            using var held = new FileStream(RestorePointStore.UnsavedPath(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var release = Task.Run(() => { Thread.Sleep(300); held.Dispose(); });
            var copy = RestorePointStore.ReadUnsaved(path);     // used to throw "being used by another process"
            release.Wait();
            Assert.NotNull(copy);
            Assert.Equal("{\"name\":\"x\"}", copy!.BimJson);
        }

        [Fact]
        public void A_write_that_meets_a_sharing_violation_retries_and_leaves_no_temp_file()
        {
            var path = ModelPath();
            var tries = 0;
            RestorePointStore.RecoveryIoHookForTest = f =>
            {
                if (!f.StartsWith(_root, StringComparison.Ordinal) || f.EndsWith("unsaved-latest.json", StringComparison.Ordinal)) return;
                if (Interlocked.Increment(ref tries) <= 2) throw new IOException("The process cannot access the file because it is being used by another process.");
            };
            RestorePointStore.WriteUnsaved(path, "{\"name\":\"y\"}");
            Assert.True(tries >= 3);
            Assert.Equal("{\"name\":\"y\"}", RestorePointStore.ReadUnsaved(path)!.BimJson);
            Assert.Empty(Directory.GetFiles(RecoveryDir(path), ".unsaved-*.tmp"));
        }

        [Fact]
        public void A_write_that_never_succeeds_still_removes_its_temp_file()
        {
            var path = ModelPath();
            RestorePointStore.RecoveryIoHookForTest = f =>
            {
                if (f.StartsWith(_root, StringComparison.Ordinal) && f.EndsWith("unsaved.json", StringComparison.Ordinal)) throw new IOException("sharing violation");
            };
            Assert.Throws<IOException>(() => RestorePointStore.WriteUnsaved(path, "{}"));
            RestorePointStore.RecoveryIoHookForTest = null;
            Assert.Empty(Directory.GetFiles(RecoveryDir(path), ".unsaved-*.tmp"));
        }

        [Fact]
        public async Task Closing_a_session_waits_for_a_write_in_flight_and_leaves_no_temp_file()
        {
            var path = ModelPath();
            var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            await Edit(sessions, "in flight");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            RestorePointStore.RecoveryIoHookForTest = f =>
            {
                if (!f.StartsWith(_root, StringComparison.Ordinal)) return;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            var closing = Task.Run(() => sessions.Dispose());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(closing.Wait(300));                    // the close does not return while the write is under way
            release.Set();
            await closing;
            RestorePointStore.RecoveryIoHookForTest = null;
            Assert.Empty(Directory.GetFiles(RecoveryDir(path), ".unsaved-*.tmp"));
            Assert.NotNull(RestorePointStore.ReadUnsaved(path));
        }
        private string ModelPath(string name = "Sales")
        {
            var path = Path.Combine(_root, name + ".bim");
            File.Copy(TestModels.FindBim(), path);
            return path;
        }
        private static JObject Info(SessionInfo info) => JObject.FromObject(info);
        private static JToken? Offer(SessionInfo info) => Info(info)["FileRecovery"] is { Type: not JTokenType.Null } offer ? offer : null;
        private static async Task Resolve(LocalEngine engine, string session, bool restore)
        {
            var door = new EngineRpcTarget(engine);
            var method = door.GetType().GetMethod("resolveFileRecovery");
            Assert.NotNull(method);
            await (Task)method!.Invoke(door, new object[] { session, restore })!;
        }
        private static Task Edit(SessionManager sessions, string description) =>
            sessions.Require().MutateAsync("human", "edit description", m => m.Description = description);

        [Fact]
        public async Task Exit_keeps_exact_model_and_restore_is_unsaved_and_one_undo_step()
        {
            var path = ModelPath();
            var disk = File.ReadAllText(path);
            string expected;
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "recovered description");
                await sessions.Require().MutateAsync("human", "edit database description", m => m.Database.Description = "recovered database description");
                await sessions.Require().MutateAsync("human", "add table", m => m.AddCalculatedTable("RecoveredTable", "{ 1, 2 }"));
                expected = await sessions.Require().ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(sessions.Require().TomDatabase));
            }
            using var reopened = new SessionManager();
            using var engine = new LocalEngine(reopened);
            await engine.OpenAsync(path);
            var info = await engine.SessionInfoAsync();
            Assert.NotNull(Offer(info));
            await Resolve(engine, info.SessionId, true);
            var actual = await reopened.Require().ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(reopened.Require().TomDatabase));
            Assert.True(JToken.DeepEquals(JToken.Parse(expected), JToken.Parse(actual)));
            Assert.Equal(disk, File.ReadAllText(path));
            Assert.True((await engine.SessionInfoAsync()).HasUnsavedEdits);
            Assert.Equal(1, await reopened.Require().ReadAsync(m => reopened.Require().UndoStateNow().Steps));
            await engine.UndoAsync("human");
            Assert.False((await engine.SessionInfoAsync()).HasUnsavedEdits);
            Assert.False(await reopened.Require().ReadAsync(m => m.Tables.Contains("RecoveredTable")));
            await engine.RedoAsync("human");
            Assert.Equal("recovered description", await reopened.Require().ReadAsync(m => m.Description));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Save_or_discard_removes_copy(bool save)
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "keep or discard");
            }
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.OpenAsync(path);
                var info = await engine.SessionInfoAsync();
                Assert.NotNull(Offer(info));
                await Resolve(engine, info.SessionId, save);
                if (save) await engine.SaveAsync(null, "BIM", false, "human");
            }
            using var check = new SessionManager();
            using var checkEngine = new LocalEngine(check);
            await checkEngine.OpenAsync(path);
            Assert.Null(Offer(await checkEngine.SessionInfoAsync()));
            Assert.Equal(save ? "keep or discard" : string.Empty, await check.Require().ReadAsync(m => m.Description));
        }

        [Fact]
        public async Task Different_models_do_not_share_a_copy()
        {
            var a = ModelPath();
            Directory.CreateDirectory(Path.Combine(_root, "other"));
            var b = Path.Combine(_root, "other", "Sales.bim");
            File.Copy(a, b);
            foreach (var path in new[] { a, b })
            {
                using var sessions = new SessionManager();
                await sessions.OpenAsync(path);
                await Edit(sessions, path);
            }
            foreach (var path in new[] { a, b })
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                await engine.OpenAsync(path);
                var info = await engine.SessionInfoAsync();
                Assert.NotNull(Offer(info));
                await Resolve(engine, info.SessionId, true);
                Assert.Equal(path, await sessions.Require().ReadAsync(m => m.Description));
                await engine.SaveAsync(null, "BIM", false, "human");
            }
        }

        [Fact]
        public async Task Recovery_strips_passwords_even_when_model_requests_sensitive_saves()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await sessions.Require().MutateAsync("human", "secret source", m =>
                {
                    m.AddDataSource("SecretSource").ConnectionString = "Data Source=localhost;Pass" + "word=never-persist-this;P" + "wd=second-secret;User ID=test";   // split, so the release security scan does not read the fixture as a secret
                    m.SetAnnotation("TabularEditor_SaveSensitive", "1");
                });
            }
            var files = Directory.GetFiles(RestorePointStore.Root(), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("never-persist-this", text);
                Assert.DoesNotContain("second-secret", text);
            }
            Assert.Empty(Directory.GetFiles(_root, ".semanticus-unsaved*"));
        }

        // PR #399 Codex P1 (comment 4242940056): the recovery copy kept compound credential keys because the scrub matched six exact names.
        // Fixture values are split so the release security scan does not read them as secrets.
        [Theory]
        [InlineData("AccountKey")]
        [InlineData("SharedAccessKey")]
        [InlineData("SharedAccessSignature")]
        [InlineData("ClientSecret")]
        [InlineData("ApiKey")]
        public async Task Recovery_strips_compound_credential_keys_and_restore_keeps_the_edits(string key)
        {
            var path = ModelPath();
            var secret = "never-" + "persist-" + key.ToLowerInvariant();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await sessions.Require().MutateAsync("human", "compound secret source", m =>
                {
                    m.AddDataSource("CompoundSource").ConnectionString = "Data Source=localhost;" + key + "=" + secret + ";Initial Catalog=Sales";
                    m.Description = "edit that must come back";
                });
            }
            foreach (var file in Directory.GetFiles(RestorePointStore.Root(), "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(secret, File.ReadAllText(file));
            using var reopened = new SessionManager();
            using var engine = new LocalEngine(reopened);
            await engine.OpenAsync(path);
            var info = await engine.SessionInfoAsync();
            Assert.NotNull(Offer(info));
            await Resolve(engine, info.SessionId, true);
            Assert.Equal("edit that must come back", await reopened.Require().ReadAsync(m => m.Description));
            var restored = await reopened.Require().ReadAsync(m => ((TabularEditor.TOMWrapper.ProviderDataSource)m.DataSources["CompoundSource"]).ConnectionString);
            Assert.DoesNotContain(secret, restored);
            Assert.Contains("initial catalog=sales", restored, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Debounce_keeps_edits_without_waiting_for_exit_and_save_deletes_them()
        {
            var path = ModelPath();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(path);
            await Edit(sessions, "debounced changes");
            await Task.Delay(4000);
            Assert.True(Directory.Exists(RestorePointStore.Root()), "no recovery copy was written while the engine ran");
            // Found by the model they name: the root is one process-wide setting, so another live session can write a copy here too.
            Assert.Single(CopiesNaming(path));
            await engine.SaveAsync(null, "BIM", false, "human");
            Assert.Empty(CopiesNaming(path));
        }

        [Fact]
        public async Task Live_models_do_not_write_file_recovery()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                sessions.Require().LiveOrigin = new LiveOrigin("https://example.invalid", "Sales", null, "token");
                await Edit(sessions, "live changes");
            }
            // No recovery copy NAMES this live model (the root is shared, so another session's copy may sit there; that is not this model's).
            Assert.False(Directory.Exists(RestorePointStore.Root()) && CopiesNaming(path).Length > 0);
        }

        [Fact]
        public async Task Recovery_offer_is_available_on_the_assistant_door_and_rejects_another_session()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "assistant restore");
            }
            using var reopened = new SessionManager();
            using var engine = new LocalEngine(reopened);
            await engine.OpenAsync(path);
            var info = await McpTools.ModelOverview(engine);
            Assert.NotNull(Offer(info));
            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ResolveFileRecovery(engine, "another-session", true));
            Assert.True(await McpTools.ResolveFileRecovery(engine, info.SessionId, true));
            Assert.Equal("assistant restore", await reopened.Require().ReadAsync(m => m.Description));
            Assert.Equal(1, await reopened.Require().ReadAsync(m => reopened.Require().UndoStateNow().Steps));
        }

        [Fact]
        public async Task A_file_owner_can_exit_while_edits_are_unsaved()
        {
            using var sessions = new SessionManager();
            await sessions.OpenAsync(ModelPath());
            await Edit(sessions, "protected on disk");
            Assert.False(Program.KeepOwnerAlive(false, 0, sessions.Require()));
            Assert.True(Program.KeepOwnerAlive(true, 1, sessions.Require()));
        }

        [Fact]
        public async Task Restore_and_undo_preserve_edits_made_after_the_offer()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "kept description");
            }
            using var reopened = new SessionManager();
            using var engine = new LocalEngine(reopened);
            await engine.OpenAsync(path);
            var info = await engine.SessionInfoAsync();
            Assert.NotNull(Offer(info));
            var first = await reopened.Require().ReadAsync(m => (m.AllMeasures.First().Table.Name, m.AllMeasures.First().Name, m.AllMeasures.First().Expression));
            await reopened.Require().MutateAsync("human", "edit after offer", m => m.Tables[first.Item1].Measures[first.Item2].Expression = "42");
            await Resolve(engine, info.SessionId, true);
            await engine.UndoAsync("human");
            Assert.Equal("42", await reopened.Require().ReadAsync(m => m.Tables[first.Item1].Measures[first.Item2].Expression));
            await engine.UndoAsync("human");
            Assert.Equal(first.Item3, await reopened.Require().ReadAsync(m => m.Tables[first.Item1].Measures[first.Item2].Expression));
        }

        [Fact]
        public async Task A_new_model_keeps_later_edits_after_its_first_save()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("Sales", 1604);
            await engine.SaveAsync(Path.Combine(_root, "new.bim"), "BIM", false, "human", pickedInDialog: true);
            await Edit(sessions, "after first save");
            await Task.Delay(4000);
            Assert.True(Directory.Exists(RestorePointStore.Root()));
            // The model's own copies, found by the model they name. Counting every unsaved.json under the root also counted a copy that
            // some other live session wrote here while this test waited (the root is one process-wide setting), which is how Windows CI saw four.
            var mine = CopiesNaming(Path.Combine(_root, "new.bim"));
            Assert.Single(mine);
        }

        private static string[] CopiesNaming(string model) =>
            Directory.GetFiles(RestorePointStore.Root(), "unsaved.json", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetFullPath((string)JObject.Parse(File.ReadAllText(f))["source"]!), Path.GetFullPath(model),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();

        [Fact]
        public void One_model_has_one_copy_however_its_path_is_spelled()
        {
            var dir = Path.Combine(_root, "Spell");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "Model.bim"), "{}");
            var plain = RestorePointStore.UnsavedPath(Path.Combine(dir, "Model.bim"));
            Assert.Equal(plain, RestorePointStore.UnsavedPath(dir + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + "Model.bim"));
            Assert.Equal(plain, RestorePointStore.UnsavedPath(dir + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "Model.bim"));
            Assert.Equal(plain, RestorePointStore.UnsavedPath(dir + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + "Model.bim"));
            if (OperatingSystem.IsWindows())
            {
                // Windows: the other slash, the other case and the short 8.3 form all name the same file.
                Assert.Equal(plain, RestorePointStore.UnsavedPath(dir.Replace('\\', '/') + "/model.BIM"));
                Assert.Equal(plain, RestorePointStore.UnsavedPath(dir.ToUpperInvariant() + "\\MODEL.bim"));
            }
            else
            {
                // Everywhere else a name that differs only by case is another file, and keeps its own copy.
                Assert.NotEqual(plain, RestorePointStore.UnsavedPath(Path.Combine(dir, "model.BIM")));
            }
        }

        [Fact]
        public async Task Replacing_dirty_work_discards_its_copy_before_reopening_the_same_file()
        {
            var path = ModelPath();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(path);
            await Edit(sessions, "discarded on reopen");
            await Task.Delay(4000);
            await engine.OpenAsync(path, discardUnsaved: true);
            Assert.Null(Offer(await engine.SessionInfoAsync()));
            Assert.Empty(CopiesNaming(path));
        }

        [Fact]
        public async Task Discarding_current_work_before_restart_does_not_write_it_again_on_exit()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.OpenAsync(path);
                await Edit(sessions, "discarded on restart");
                await Resolve(engine, (await engine.SessionInfoAsync()).SessionId, false);
            }
            using var reopened = new SessionManager();
            using var check = new LocalEngine(reopened);
            await check.OpenAsync(path);
            Assert.Null(Offer(await check.SessionInfoAsync()));
        }

        [Fact]
        public async Task Edits_made_while_an_offer_is_open_survive_exit()
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "old offered work");
            }
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.OpenAsync(path);
                Assert.NotNull(Offer(await engine.SessionInfoAsync()));
                // The person ignores the offer and keeps editing, then the window closes.
                await sessions.Require().MutateAsync("human", "edit database description", m => m.Database.Description = "new work");
            }
            using var reopened = new SessionManager();
            using var check = new LocalEngine(reopened);
            await check.OpenAsync(path);
            var info = await check.SessionInfoAsync();
            Assert.NotNull(Offer(info));
            await Resolve(check, info.SessionId, true);
            Assert.Equal("new work", await reopened.Require().ReadAsync(m => m.Database.Description));
            // The older offered set is not lost: it becomes the next offer, until the person saves or discards.
            Assert.NotNull(Offer(await check.SessionInfoAsync()));
            await Resolve(check, info.SessionId, false);
            Assert.Empty(Directory.GetFiles(RestorePointStore.Root(), "unsaved*.json", SearchOption.AllDirectories));
        }

        [Theory]
        [InlineData("restore")]
        [InlineData("dismiss")]
        [InlineData("save")]
        public async Task The_older_kept_copy_is_offered_once_the_newer_one_is_settled(string how)
        {
            var path = ModelPath();
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                await Edit(sessions, "older work");
            }
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.OpenAsync(path);
                Assert.NotNull(Offer(await engine.SessionInfoAsync()));
                await Edit(sessions, "newer work");   // the banner is ignored and the person keeps editing, then closes
            }
            using var reopened = new SessionManager();
            using var check = new LocalEngine(reopened);
            await check.OpenAsync(path);
            var info = await check.SessionInfoAsync();
            Assert.NotNull(Offer(info));
            if (how == "save") { await Edit(reopened, "typed since"); await check.SaveAsync(null, "BIM", false, "human"); }
            else await Resolve(check, info.SessionId, how == "restore");
            if (how == "restore") Assert.Equal("newer work", await reopened.Require().ReadAsync(m => m.Description));

            Assert.NotNull(Offer(await check.SessionInfoAsync()));   // the older copy, in the same banner
            await Resolve(check, info.SessionId, true);
            Assert.Equal("older work", await reopened.Require().ReadAsync(m => m.Description));
            Assert.Null(Offer(await check.SessionInfoAsync()));
        }
    }
}
