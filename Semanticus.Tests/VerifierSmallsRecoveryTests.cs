using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // Verifier probes for the m15/smalls recovery change (older kept copy re-offered).
    // The restore root is one process-wide setting this class swaps; it runs in the restore-root collection, one after another with the classes that read it.
    [Collection("restore-root")]
    public sealed class VerifierSmallsRecoveryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "smx-vsr-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldRoot = RestorePointStore.RootOverride;
        public VerifierSmallsRecoveryTests()
        {
            Directory.CreateDirectory(_root);
            RestorePointStore.RootOverride = Path.Combine(_root, "state", "restore");
        }
        public void Dispose()
        {
            RestorePointStore.RootOverride = _oldRoot;
            RestorePointStore.WaitForWriters();
            try { Directory.Delete(_root, true); } catch { }
        }
        private string ModelPath(string name = "Sales")
        {
            var path = Path.Combine(_root, name + ".bim");
            File.Copy(TestModels.FindBim(), path);
            return path;
        }
        private static JToken? Offer(SessionInfo info) => JObject.FromObject(info)["FileRecovery"] is { Type: not JTokenType.Null } o ? o : null;
        private static async Task Resolve(LocalEngine engine, string session, bool restore)
        {
            var door = new EngineRpcTarget(engine);
            await (Task)door.GetType().GetMethod("resolveFileRecovery")!.Invoke(door, new object[] { session, restore })!;
        }
        private static Task Edit(SessionManager s, string d) => s.Require().MutateAsync("human", "edit description", m => m.Description = d);
        private string DiskText() => string.Join("\n", Directory.GetFiles(RestorePointStore.Root(), "unsaved*.json", SearchOption.AllDirectories).Select(File.ReadAllText));

        private async Task TwoClosesAsync(string path)
        {
            using (var s = new SessionManager()) { await s.OpenAsync(path); await Edit(s, "older work"); }
            using (var s = new SessionManager())
            using (var e = new LocalEngine(s))
            {
                await e.OpenAsync(path);
                Assert.NotNull(Offer(await e.SessionInfoAsync()));
                await Edit(s, "newer work");
            }
        }

        // Restore the newer set, let the debounce settle, dismiss the older one: the restored work must still be on disk
        // (a crash or power loss now must not lose it).
        [Fact]
        public async Task Probe_restored_work_stays_on_disk_after_the_older_copy_is_dismissed()
        {
            var path = ModelPath();
            await TwoClosesAsync(path);
            using var s = new SessionManager();
            using var e = new LocalEngine(s);
            await e.OpenAsync(path);
            var info = await e.SessionInfoAsync();
            await Resolve(e, info.SessionId, true);
            Assert.Equal("newer work", await s.Require().ReadAsync(m => m.Description));
            await Task.Delay(4000);   // the restore's debounced write has run
            Assert.NotNull(Offer(await e.SessionInfoAsync()));
            await Resolve(e, info.SessionId, false);   // dismiss the older copy
            await Task.Delay(4000);
            Assert.Contains("newer work", DiskText());
        }

        // Save-as while the newer offer is open and the older is parked: the older kept copy of the old file must survive.
        [Fact]
        public async Task Probe_save_as_keeps_the_older_kept_copy_of_the_old_file()
        {
            var path = ModelPath();
            await TwoClosesAsync(path);
            using (var s = new SessionManager())
            using (var e = new LocalEngine(s))
            {
                await e.OpenAsync(path);
                Assert.NotNull(Offer(await e.SessionInfoAsync()));
                await e.SaveAsync(Path.Combine(_root, "Copy.bim"), "BIM", false, "human");
            }
            Assert.Contains("older work", DiskText());
        }

        // A crash between "restored work written beside" and "older promoted": nothing lost on the next open.
        [Fact]
        public async Task Probe_crash_mid_restore_loses_nothing()
        {
            var path = ModelPath();
            await TwoClosesAsync(path);
            // State after WriteFileRecovery(beside) but before PromoteOlder: unsaved.json=newer, unsaved-latest.json=restored newer, older parked.
            var cur = RestorePointStore.UnsavedPath(path);
            var dir = Path.GetDirectoryName(cur)!;
            var latest = Path.Combine(dir, "unsaved-latest.json");
            File.Move(cur, Path.Combine(dir, "unsaved-older-" + Guid.NewGuid().ToString("N") + ".json"));   // what ReadUnsaved does on open
            File.Move(latest, cur);
            File.Copy(cur, latest);   // the restored work written beside, then the process died before PromoteOlder
            using var s = new SessionManager();
            using var e = new LocalEngine(s);
            await e.OpenAsync(path);
            var info = await e.SessionInfoAsync();
            await Resolve(e, info.SessionId, true);
            Assert.Equal("newer work", await s.Require().ReadAsync(m => m.Description));
            // Keep dismissing duplicates until the older set shows; it must be offered.
            for (var i = 0; i < 3 && Offer(await e.SessionInfoAsync()) != null; i++)
            {
                await Resolve(e, info.SessionId, true);
                if (await s.Require().ReadAsync(m => m.Description) == "older work") return;
            }
            Assert.Fail("the older set was never offered after a crash mid-restore");
        }
    }
}
