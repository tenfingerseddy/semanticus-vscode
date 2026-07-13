using System;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>E2 (docs/tests-tab-spec.md): the suite store's contract — upsert/remove round-trip, corrupt
    /// lines disclosed never thrown, run-history retention, and the best-effort no-throw discipline.</summary>
    public sealed class TestSuiteStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sem-teststore-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

        private static TestDefinition Def(string title, string id = null) => new TestDefinition
        { Id = id, Kind = TestKinds.MeasureReconcile, Title = title, TargetTag = "lt-1", ParamsJson = "{}" };

        [Fact]
        public void Upsert_assigns_an_id_and_round_trips()
        {
            var stored = TestSuiteStore.Upsert(_dir, Def("Revenue ties to GL"), () => "id-1");
            Assert.Equal("id-1", stored.Id);
            var (defs, bad) = TestSuiteStore.LoadSuite(_dir);
            Assert.Equal(0, bad);
            Assert.Equal("Revenue ties to GL", defs.Single().Title);

            // Upsert by the same id REPLACES (a mutable set, not an append log).
            TestSuiteStore.Upsert(_dir, Def("Revenue ties to GL v2", "id-1"), () => throw new InvalidOperationException("no new id needed"));
            var (defs2, _) = TestSuiteStore.LoadSuite(_dir);
            Assert.Equal("Revenue ties to GL v2", defs2.Single().Title);
        }

        [Fact]
        public void Remove_deletes_only_the_named_definition()
        {
            TestSuiteStore.Upsert(_dir, Def("a"), () => "a");
            TestSuiteStore.Upsert(_dir, Def("b"), () => "b");
            Assert.True(TestSuiteStore.Remove(_dir, "a"));
            Assert.False(TestSuiteStore.Remove(_dir, "a"));   // second delete honestly reports absent
            Assert.Equal("b", TestSuiteStore.LoadSuite(_dir).Defs.Single().Id);
        }

        [Fact]
        public void Corrupt_lines_are_counted_never_thrown()
        {
            TestSuiteStore.Upsert(_dir, Def("good"), () => "g");
            File.AppendAllText(Path.Combine(_dir, TestSuiteStore.SubDir, TestSuiteStore.SuiteFile), "{not json\n");
            var (defs, bad) = TestSuiteStore.LoadSuite(_dir);
            Assert.Single(defs);
            Assert.Equal(1, bad);   // disclosed — the store never bricks on one bad line
        }

        [Fact]
        public void Run_history_appends_and_prunes_oldest_first()
        {
            for (var i = 0; i < TestSuiteStore.MaxRuns + 5; i++)
                Assert.True(TestSuiteStore.AppendRun(_dir, TestSuiteStore.Serialize(new TestRunRecord { RunId = "r" + i, When = DateTime.UtcNow.ToString("o") })));
            var lines = TestSuiteStore.ReadRunLines(_dir);
            Assert.Equal(TestSuiteStore.MaxRuns, lines.Count);
            var first = TestSuiteStore.Deserialize<TestRunRecord>(lines.First());
            Assert.Equal("r5", first.RunId);   // the 5 oldest were pruned, order preserved
        }

        [Fact]
        public void Null_dir_degrades_never_throws()
        {
            // A live-only session has no .semanticus home — every store call must degrade, not throw.
            Assert.Null(TestSuiteStore.Upsert(null, Def("x"), () => "x"));
            Assert.False(TestSuiteStore.Remove(null, "x"));
            Assert.False(TestSuiteStore.AppendRun(null, "{}"));
            Assert.Empty(TestSuiteStore.ReadRunLines(null));
            Assert.Empty(TestSuiteStore.LoadSuite(null).Defs);
        }
    }
}
