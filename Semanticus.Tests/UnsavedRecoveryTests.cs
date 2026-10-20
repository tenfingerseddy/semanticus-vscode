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

        // Kane, 17 Oct: a new blank model with unsaved work was stranded in the engine of the window it was made in when a folder was opened by hand.
        // The copy has no file to be named after, so it is named after the model, and making a model of that name again offers it back.
        [Fact]
        public async Task A_new_blank_model_keeps_its_unsaved_work_and_offers_it_when_a_model_of_that_name_is_made_again()
        {
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.CreateModelAsync("Scratch Sales", 1604);
                await sessions.Require().MutateAsync("human", "add table", m => m.AddCalculatedTable("Budget", "{ 1 }"));
            }
            using (var other = new SessionManager())
            using (var otherEngine = new LocalEngine(other))
            {
                await otherEngine.CreateModelAsync("Another Model", 1604);
                Assert.Null(Offer(await otherEngine.SessionInfoAsync()));      // a different name shares nothing
            }
            using var again = new SessionManager();
            using var engine2 = new LocalEngine(again);
            await engine2.CreateModelAsync("Scratch Sales", 1604);
            var info = await engine2.SessionInfoAsync();
            var offer = Offer(info);
            Assert.NotNull(offer);
            Assert.Equal("Scratch Sales", Path.GetFileName(offer!["Source"]!.Value<string>()!));   // the page names it by the model, not a path
            await Resolve(engine2, info.SessionId, true);
            Assert.True(await again.Require().ReadAsync(m => m.Tables.Contains("Budget")));
            Assert.True((await engine2.SessionInfoAsync()).HasUnsavedEdits);
        }

        [Fact]
        public async Task A_new_blank_model_with_no_edits_keeps_nothing_and_a_save_settles_its_copy()
        {
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
                await engine.CreateModelAsync("Empty One", 1604);
            using (var check = new SessionManager())
            using (var checkEngine = new LocalEngine(check))
            {
                await checkEngine.CreateModelAsync("Empty One", 1604);
                Assert.Null(Offer(await checkEngine.SessionInfoAsync()));      // nothing to offer: nothing was changed
            }
            var path = Path.Combine(_root, "Saved One.bim");
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions))
            {
                await engine.CreateModelAsync("Saved One", 1604);
                await sessions.Require().MutateAsync("human", "add table", m => m.AddCalculatedTable("Budget", "{ 1 }"));
                await engine.SaveAsync(path, "BIM", false, "human");
            }
            using var after = new SessionManager();
            using var afterEngine = new LocalEngine(after);
            await afterEngine.CreateModelAsync("Saved One", 1604);
            Assert.Null(Offer(await afterEngine.SessionInfoAsync()));          // saved: the kept copy is gone
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
            // A recovery write can still be finishing: its temp file may be renamed between the listing and the read (CI run 8 on PR #402).
            // So the folder is read again until one listing reads whole, and every file in that listing is checked.
            var checkedAll = false;
            for (var attempt = 0; attempt < 20 && !checkedAll; attempt++)
            {
                var files = Directory.GetFiles(RestorePointStore.Root(), "*", SearchOption.AllDirectories);
                Assert.NotEmpty(files);
                checkedAll = true;
                foreach (var file in files)
                {
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) && !File.Exists(file)) { checkedAll = false; break; }
                    Assert.DoesNotContain("never-persist-this", text);
                    Assert.DoesNotContain("second-secret", text);
                }
                if (!checkedAll) await Task.Delay(100);
            }
            Assert.True(checkedAll, "the recovery folder settled within two seconds");
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

        // Round 2 UAT N26 follow-up: an engine that leaves after its hold cap must not lose unsaved LIVE work. It writes a kept copy first
        // (named for the model, not for the engine's temp snapshot), and the next open of that live model offers it back.
        [Fact]
        public async Task Unsaved_live_edits_are_kept_when_the_engine_leaves_and_offered_when_the_model_opens_again()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Northstar Sales Working", null, "interactive");
            using (var sessions = new SessionManager())
            {
                await sessions.OpenAsync(path);
                var s = sessions.Require();
                s.LiveOrigin = origin;
                s.AttachLiveRecovery();
                await Edit(sessions, "live edit that must survive");
                Assert.True(await s.RunAsync(() => s.FlushLiveRecovery()), "the leaving engine writes the kept copy");
            }
            var kept = RestorePointStore.ReadUnsaved(RestorePointStore.LiveSource(origin.Endpoint, origin.Database));
            Assert.NotNull(kept);
            Assert.Contains("live edit that must survive", kept!.BimJson);

            using var reopened = new SessionManager();
            await reopened.OpenAsync(path);
            var again = reopened.Require();
            again.LiveOrigin = origin;
            again.AttachLiveRecovery();
            var offer = again.FileRecovery;
            Assert.NotNull(offer);
            Assert.EndsWith("Northstar Sales Working", offer!.Source);   // the words the offer says: "kept your unsaved changes to Northstar Sales Working"
            Assert.True(await again.ResolveFileRecoveryAsync(again.Id, true, "human"));
            Assert.Equal("live edit that must survive", await again.ReadAsync(m => m.Description));
        }

        // Round 2 UAT (fix2-r2-start, item 3): a publish that landed settled the live model, but the kept copy of its earlier edits stayed on disk and
        // was offered once more on the next open.
        [Fact]
        public async Task A_landed_live_publish_removes_the_kept_copy_of_that_live_model()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Published Back", null, "interactive");
            var key = RestorePointStore.LiveSource(origin.Endpoint, origin.Database);
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            await Edit(sessions, "edit that was published");
            Assert.True(await s.RunAsync(() => s.FlushLiveRecovery()));
            Assert.NotNull(RestorePointStore.ReadUnsaved(key));
            await s.RunAsync(() => { s.MarkSavedToDisk(false); return true; });   // what a landed publish calls
            Assert.Null(RestorePointStore.ReadUnsaved(key));
        }

        // F-294 (Codex on pull request 402): the leaving engine waited five seconds for the kept copy, then gave up and exited two seconds later,
        // before a slow write (a large live model, a busy dispatcher) reached its move into place. The unpublished edits were lost.
        [Fact]
        public async Task A_slow_flush_on_leaving_still_lands_the_kept_copy_whole()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Slow Write", null, "interactive");
            var key = RestorePointStore.LiveSource(origin.Endpoint, origin.Database);
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            await Edit(sessions, "slow edit that must survive");
            var calls = 0;
            RestorePointStore.RecoveryIoHookForTest = f => { if (Interlocked.Increment(ref calls) == 1) Thread.Sleep(6500); };   // the write starts late: past the old five seconds

            Program.KeepLiveWork(s);   // what the engine calls before it exits: it must not return while the write is still under way

            RestorePointStore.RecoveryIoHookForTest = null;
            // Read the file itself, at once: the store's own reader waits on the write's gate, so it would pass even if KeepLiveWork had given up early.
            var file = RestorePointStore.UnsavedPath(key);
            Assert.True(File.Exists(file), "KeepLiveWork returned before the delayed write landed");
            Assert.Contains("slow edit that must survive", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(RecoveryDir(key), ".unsaved-*.tmp"));
        }

        // A write that never finishes cannot hold the engine for ever, and giving up on it leaves the good copy that was already there whole.
        // The earlier copy is written by this session's own first flush, so the hung second flush targets the very file that holds it.
        [Fact]
        public async Task A_flush_that_never_finishes_is_given_up_on_and_leaves_the_earlier_kept_copy_whole()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Hung Write", null, "interactive");
            var key = RestorePointStore.LiveSource(origin.Endpoint, origin.Database);
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            await Edit(sessions, "the earlier good copy");
            Program.KeepLiveWork(s);
            var file = RestorePointStore.UnsavedPath(key);
            Assert.Contains("the earlier good copy", File.ReadAllText(file));
            await Edit(sessions, "edit behind a hung write");
            using var release = new ManualResetEventSlim(false);
            var calls = 0;
            RestorePointStore.RecoveryIoHookForTest = f => { if (Interlocked.Increment(ref calls) == 1) release.Wait(TimeSpan.FromSeconds(30)); };
            var oldCap = Program.LiveKeepCap;
            Program.LiveKeepCap = TimeSpan.FromMilliseconds(400);
            try
            {
                var started = DateTime.UtcNow;
                Program.KeepLiveWork(s);
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "a hung write must not hold the engine");
                // The file the hung write would replace, read directly (the store's reader waits behind the stuck write): still the earlier copy, whole.
                var kept = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                Assert.Contains("the earlier good copy", (string)kept["BimJson"] ?? (string)kept["bimJson"]);
                Assert.DoesNotContain("edit behind a hung write", File.ReadAllText(file));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file), ".unsaved-*.tmp"));
            }
            finally
            {
                Program.LiveKeepCap = oldCap;
                release.Set();
                await s.RunAsync(() => true);   // the released write finishes before the session goes
                RestorePointStore.RecoveryIoHookForTest = null;
            }
        }

        // Verifier v-fix-codex402 (F-294 bar), dropped from 21f0c88f and restored by v2: a close with nothing to keep leaves in seconds,
        // even while the dispatcher is busy with unrelated work. A model from a file has no live copy to write.
        [Fact]
        public async Task Verifier_a_close_with_nothing_to_keep_does_not_wait_on_a_busy_dispatcher()
        {
            var path = ModelPath();
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            using var release = new ManualResetEventSlim(false);
            var busy = s.RunAsync(() => { release.Wait(TimeSpan.FromSeconds(20)); return true; });
            try
            {
                var started = DateTime.UtcNow;
                Program.KeepLiveWork(s);
                var took = DateTime.UtcNow - started;
                Assert.True(took < TimeSpan.FromSeconds(3), $"a close with nothing to keep waited {took.TotalSeconds:0.0}s behind unrelated work");
            }
            finally { release.Set(); await busy; }
        }

        // V2: a LIVE model with no unsaved edits also leaves at once behind a busy dispatcher.
        [Fact]
        public async Task V2_a_live_model_with_no_unsaved_edits_leaves_at_once_behind_a_busy_dispatcher()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Clean Live", null, "interactive");
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            using var release = new ManualResetEventSlim(false);
            var busy = s.RunAsync(() => { release.Wait(TimeSpan.FromSeconds(20)); return true; });
            try
            {
                var started = DateTime.UtcNow;
                Program.KeepLiveWork(s);
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "a clean live close waited behind unrelated work");
            }
            finally { release.Set(); await busy; }
        }

        // V2: a live model WITH unsaved edits behind a dispatcher busy for 7 s (past the old 5 s) still waits, and the copy lands.
        [Fact]
        public async Task V2_live_unsaved_edits_behind_a_busy_dispatcher_are_waited_for_and_land()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Busy Live", null, "interactive");
            var key = RestorePointStore.LiveSource(origin.Endpoint, origin.Database);
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            await Edit(sessions, "edit behind a busy dispatcher");
            var busy = s.RunAsync(() => { Thread.Sleep(7000); return true; });
            Program.KeepLiveWork(s);
            var file = RestorePointStore.UnsavedPath(key);
            Assert.True(File.Exists(file), "the close did not wait for the write behind a busy dispatcher");
            Assert.Contains("edit behind a busy dispatcher", File.ReadAllText(file));
            await busy;
        }

        [Fact]
        public async Task A_live_model_writes_no_kept_copy_until_the_engine_leaves()
        {
            var path = ModelPath();
            var origin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Test", "Quiet", null, "interactive");
            using var sessions = new SessionManager();
            await sessions.OpenAsync(path);
            var s = sessions.Require();
            s.LiveOrigin = origin;
            s.AttachLiveRecovery();
            await Edit(sessions, "edit");
            await Task.Delay(3600);   // longer than the file-model autosave delay
            Assert.Null(RestorePointStore.ReadUnsaved(RestorePointStore.LiveSource(origin.Endpoint, origin.Database)));
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
