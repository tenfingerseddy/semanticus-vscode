using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TestObjectIdentityStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sem-test-identities-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static TestMeasureIdentity Snap(string reff, int ordinal = 0, string hash = "expr") => new TestMeasureIdentity
        {
            Ref = reff, TableName = "Measures", MeasureName = reff.Split('/').Last(),
            TableOrdinal = 0, MeasureOrdinal = ordinal, ExpressionHash = hash,
        };

        [Fact]
        public void Sidecar_identity_follows_a_unique_rename_witness_and_persists_the_new_ref()
        {
            var index = new TestObjectIdentityIndex();
            var id = index.Bind(null, Snap("measure:Measures/Old"));
            Assert.True(TestObjectIdentityStore.Save(_dir, index));

            var loaded = TestObjectIdentityStore.Load(_dir);
            var resolved = loaded.Resolve(id, new[] { Snap("measure:Measures/New") });

            Assert.Equal("measure:Measures/New", resolved.Ref);
            Assert.True(loaded.Dirty);
            Assert.True(TestObjectIdentityStore.Save(_dir, loaded));
            Assert.Equal("measure:Measures/New", TestObjectIdentityStore.Load(_dir).Entries.Single().Ref);
        }

        [Fact]
        public void Corrupt_sidecar_degrades_to_an_empty_index_instead_of_throwing()
        {
            var testsDir = Path.Combine(_dir, TestSuiteStore.SubDir);
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, TestObjectIdentityStore.FileName), "{ not json");

            var loaded = TestObjectIdentityStore.Load(_dir);

            Assert.NotNull(loaded);
            Assert.Empty(loaded.Entries);
        }

        [Fact]
        public void Sidecar_identity_refuses_a_shifted_different_object_instead_of_guessing()
        {
            var index = new TestObjectIdentityIndex();
            var id = index.Bind(null, Snap("measure:Measures/Old", hash: "old-expression"));

            var resolved = index.Resolve(id, new[] { Snap("measure:Measures/Replacement", hash: "different-expression") });

            Assert.Null(resolved);
        }

        [Fact]
        public void Sidecar_recovers_every_identity_from_a_validated_backup_and_repairs_the_primary()
        {
            var index = new TestObjectIdentityIndex();
            var first = index.Bind(null, Snap("measure:Measures/First", 0, "first-expression"));
            var second = index.Bind(null, Snap("measure:Measures/Second", 1, "second-expression"));
            Assert.True(TestObjectIdentityStore.Save(_dir, index));

            var testsDir = Path.Combine(_dir, TestSuiteStore.SubDir);
            var primary = Path.Combine(testsDir, TestObjectIdentityStore.FileName);
            var backup = Path.Combine(testsDir, TestObjectIdentityStore.BackupFileName);
            Assert.Equal(File.ReadAllBytes(primary), File.ReadAllBytes(backup));
            File.WriteAllText(primary, "{ corrupt");

            var recovered = TestObjectIdentityStore.Load(_dir);

            Assert.True(recovered.Dirty);
            Assert.Equal(new[] { first, second }, recovered.Entries.Select(e => e.Id));
            Assert.True(TestObjectIdentityStore.Save(_dir, recovered));
            Assert.False(recovered.Dirty);
            Assert.Equal(File.ReadAllBytes(primary), File.ReadAllBytes(backup));
            Assert.Equal(2, TestObjectIdentityStore.Load(_dir).Entries.Count);
        }

        [Fact]
        public void Sidecar_fails_honestly_when_primary_and_backup_are_both_unreadable()
        {
            var index = new TestObjectIdentityIndex();
            index.Bind(null, Snap("measure:Measures/Only"));
            Assert.True(TestObjectIdentityStore.Save(_dir, index));
            var testsDir = Path.Combine(_dir, TestSuiteStore.SubDir);
            File.WriteAllText(Path.Combine(testsDir, TestObjectIdentityStore.FileName), "not json");
            File.WriteAllText(Path.Combine(testsDir, TestObjectIdentityStore.BackupFileName), "also not json");

            var loaded = TestObjectIdentityStore.Load(_dir);

            Assert.Empty(loaded.Entries);
            Assert.False(loaded.Dirty);
        }

        [Fact]
        public async Task Saved_tagless_measure_test_survives_rename_in_the_ambient_suite()
        {
            Directory.CreateDirectory(_dir);
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro(), _dir);
            await engine.CreateModelAsync("Tagless", 1604);
            sessions.Current.SourcePath = Path.Combine(_dir, "tagless.bim");
            var table = await engine.CreateTableAsync("Measures", "human");
            var measureRef = await engine.CreateMeasureAsync(table, "Old name", "1", "human");
            await sessions.Current.MutateAsync("human", "fixture tagless measure", m =>
                ((TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(m, measureRef)).LineageTag = null);

            var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "Tagless mapping", TargetRef = measureRef,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measureRef, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");
            Assert.Null(stored.TargetTag);
            Assert.StartsWith("sid:", stored.TargetIdentity);
            Assert.Null(stored.BindingWarning);

            var renamed = await engine.RenameObjectAsync(measureRef, "New name", "human");
            var run = await engine.RunTestSuiteAsync(false, "human");

            var outcome = Assert.Single(run.Reconciles, o => o.DefId == stored.Id);
            Assert.False(outcome.Missing);
            Assert.Equal(renamed.NewRef, outcome.TargetRef);
            Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
            Assert.Equal(renamed.NewRef, TestSuiteStore.LoadSuite(Path.Combine(_dir, LayoutStore.DirName)).Defs.Single().TargetRef);
        }

        [Fact]
        public async Task Unresolved_name_fallback_returns_an_explicit_rename_warning()
        {
            Directory.CreateDirectory(_dir);
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro(), _dir);
            await engine.CreateModelAsync("Fallback", 1604);
            sessions.Current.SourcePath = Path.Combine(_dir, "fallback.bim");

            var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "Legacy mapping", TargetRef = "measure:Missing/Measure",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = "measure:Missing/Measure", Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

            Assert.Null(stored.TargetIdentity);
            Assert.Contains("Rename safety warning", stored.BindingWarning);
        }

        [Fact]
        public async Task Shared_workspace_suite_never_leaks_a_mapping_after_switching_live_models()
        {
            Directory.CreateDirectory(_dir);
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro(), _dir);
            await engine.CreateModelAsync("Editable", 1604);
            sessions.Current.LiveOrigin = new LiveOrigin("fabric.example.com", "ModelA", "tenant");
            var table = await engine.CreateTableAsync("Measures", "human");
            var measure = await engine.CreateMeasureAsync(table, "Total A", "1", "human");
            var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "A source mapping", TargetRef = measure,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measure, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

            Assert.Single((await engine.ListTestDefinitionsAsync()).Definitions, d => d.Id == stored.Id);
            sessions.Current.LiveOrigin = new LiveOrigin("fabric.example.com", "ModelB", "tenant");

            var modelB = await engine.ListTestDefinitionsAsync();
            Assert.Empty(modelB.Definitions);
            Assert.Contains("another model", modelB.Note);
            Assert.Equal(0, (await engine.RunTestSuiteAsync(false, "human")).DefinitionCount);
            Assert.False(await engine.DeleteTestDefinitionAsync(stored.Id, "human"));

            sessions.Current.LiveOrigin = new LiveOrigin("fabric.example.com", "ModelA", "tenant");
            Assert.Single((await engine.ListTestDefinitionsAsync()).Definitions, d => d.Id == stored.Id);
        }
    }
}
