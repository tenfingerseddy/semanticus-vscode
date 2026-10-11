using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>F-241 (Kane, 23 September, the twelfth drop). A new build installed over the old one at the SAME
    /// version and the SAME path left every new window attached to the engine the old build had started, because
    /// the only identity a window compared was the executable path, and that path had not changed. So every engine
    /// now records which BUILD it is, in engine.json and over its RPC door, and a window compares that instead.
    ///
    /// The identity is a hash of the Semanticus assemblies beside the engine, so VS Code can compute the same value
    /// from the files it would launch without starting anything. The vector below is pinned in BOTH languages
    /// (Semanticus.VSCode/test/engine-build-identity.test.mjs holds the same one): if the two recipes ever drift
    /// apart, every window would read every engine as a different build and replace it, so the drift has to fail
    /// a test on both sides rather than show up on a person's machine.</summary>
    public sealed class EngineBuildIdentityTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "semanticus-build-id-" + Guid.NewGuid().ToString("N"));

        public EngineBuildIdentityTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* temp */ }
        }

        private void Put(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

        /// <summary>The cross-language vector: sha256 over "name\nsha256(bytes)\n" for each Semanticus.*.dll, names
        /// lower-cased and sorted ordinally. Anything else in the folder (the runtime, a .pdb) is not part of it.</summary>
        [Fact]
        public void Identity_is_the_pinned_hash_of_the_semanticus_assemblies_only()
        {
            Put("Semanticus.Engine.dll", "engine-v1");
            Put("Semanticus.Core.dll", "core");
            Put("Newtonsoft.Json.dll", "not ours");
            Put("Semanticus.Engine.pdb", "symbols are not the build");
            Assert.Equal("15872dc41088e854ce60d35e09d280fd97b60d0107a5cdb0e026829106efa1ce", BuildIdentity.ForDirectory(_dir));
        }

        [Fact]
        public void Same_version_and_path_with_different_bytes_is_a_different_build()
        {
            Put("Semanticus.Core.dll", "core");
            Put("Semanticus.Engine.dll", "engine-v1");
            var first = BuildIdentity.ForDirectory(_dir);
            // The in-place reinstall Kane measured: same folder, same file names, new bytes.
            Put("Semanticus.Engine.dll", "engine-v2");
            var second = BuildIdentity.ForDirectory(_dir);
            Assert.NotEqual(first, second);
            Assert.Equal("f9a787070b482ff622d4de6ad7241dfc083131ea710c43f4d1c8930dfc199793", second);
        }

        [Fact]
        public void A_folder_with_no_semanticus_assembly_has_no_identity()
        {
            Put("Newtonsoft.Json.dll", "not ours");
            Assert.Null(BuildIdentity.ForDirectory(_dir));
            Assert.Null(BuildIdentity.ForDirectory(Path.Combine(_dir, "missing")));
        }

        [Fact]
        public void The_running_engine_reports_the_identity_of_its_own_folder()
        {
            Assert.False(string.IsNullOrEmpty(BuildIdentity.Current));
            Assert.Equal(BuildIdentity.ForDirectory(AppContext.BaseDirectory), BuildIdentity.Current);
        }

        [Fact]
        public void Engine_json_records_the_build()
        {
            EngineBroker.WriteInfo(_dir, new EngineInfo { PipeName = "semanticus-test", Pid = Environment.ProcessId, Workspace = _dir });
            var read = EngineBroker.ReadInfo(_dir);
            Assert.Equal(BuildIdentity.Current, read.BuildId);
            Assert.Contains("\"BuildId\"", File.ReadAllText(Path.Combine(EngineBroker.DirFor(_dir), "engine.json")));
        }

        [Fact]
        public async Task The_rpc_door_reports_the_build_to_any_connection()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            Assert.Equal(BuildIdentity.Current, await new EngineRpcTarget(engine).buildIdentity());
            Assert.Equal(BuildIdentity.Current, await new EngineRpcTarget(engine, "agent").buildIdentity());
        }

        // ---- F-244 (Sol 6, slice 5 review): the replaced engine stops itself, and only while clean ----------------

        [Fact]
        public async Task Stop_if_clean_refuses_an_engine_with_unsaved_work_and_nothing_stops()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), _dir);
            await engine.CreateModelAsync("Contoso", 1604);
            await engine.SaveAsync(Path.Combine(_dir, "Contoso.bim"), "bim", false, "human");
            await engine.CreateTableAsync("Sales", "human");
            Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);

            var stops = 0;
            using var cleanStop = new CleanStop(sessions, () => Interlocked.Increment(ref stops));
            var answer = await new HumanGovernanceRpcTarget(engine, null, cleanStop).stopIfClean();

            Assert.False(answer.Stopping);
            Assert.Equal(Environment.ProcessId, answer.Pid);
            Assert.False(string.IsNullOrWhiteSpace(answer.Reason));
            Assert.Equal(0, stops);
            // The engine carries on as before: the next edit lands.
            await engine.CreateTableAsync("Returns", "human");
        }

        [Fact]
        public async Task Stop_if_clean_is_one_step_so_no_edit_lands_after_the_clean_answer()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), _dir);
            await engine.CreateModelAsync("Contoso", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.SaveAsync(Path.Combine(_dir, "Contoso.bim"), "bim", false, "human");
            var revision = (await engine.SessionInfoAsync()).Revision;
            Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);

            var stops = 0;
            var cleanStop = new CleanStop(sessions, () => Interlocked.Increment(ref stops));
            Task late;
            try
            {
                var answer = await new HumanGovernanceRpcTarget(engine, null, cleanStop).stopIfClean();
                Assert.True(answer.Stopping);
                Assert.Equal(Environment.ProcessId, answer.Pid);
                Assert.Equal(1, stops);

                // An edit that arrives after the clean answer (another door, another window) waits behind the stop
                // and never runs while this engine is leaving, so "clean" cannot go stale before the exit.
                late = engine.CreateTableAsync("Returns", "human");
                await Task.Delay(500);
                Assert.False(late.IsCompleted);
                Assert.Equal(revision, sessions.Current.Revision);

                // A second request is told the same thing and stops nothing twice.
                await Task.WhenAny(new HumanGovernanceRpcTarget(engine, null, cleanStop).stopIfClean(), Task.Delay(500));
                Assert.Equal(1, stops);
            }
            finally
            {
                cleanStop.Dispose();   // a test's release; in the engine the process exits holding the dispatcher
            }
            await late;
        }

        [Fact]
        public async Task Stop_if_clean_is_a_human_only_method_and_a_host_without_it_refuses()
        {
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("stopIfClean"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("stopIfClean"));
            Assert.DoesNotContain(typeof(IEngine).GetMethods(), m => m.Name.Contains("Stop", StringComparison.Ordinal));

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            var answer = await new HumanGovernanceRpcTarget(engine).stopIfClean();
            Assert.False(answer.Stopping);
        }
    }
}
