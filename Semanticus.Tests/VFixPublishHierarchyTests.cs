using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier attacks on m16/fix-publish-hierarchy (17 Oct). The brief: every real hierarchy change a person makes publishes in one
    /// press, a difference the compare hides is never one a person would want published, and "stays in your copy" shows only for what
    /// truly cannot go. Same real push as HierarchyPublishTests (LiveDeploy.SyncAndApply against an in-memory live model).
    /// </summary>
    [Collection("restore-root")]
    public sealed class VFixPublishHierarchyTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static TOM.Database Shape()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Customer", LineageTag = "tag-cust" };
            t.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            foreach (var c in new[] { "Country", "State", "City" })
                t.Columns.Add(new TOM.DataColumn { Name = c, DataType = TOM.DataType.String, LineageTag = "tag-" + c });
            t.Measures.Add(new TOM.Measure { Name = "Customers", Expression = "COUNTROWS(Customer)", LineageTag = "tag-m" });
            var h = new TOM.Hierarchy { Name = "Geography", LineageTag = "tag-geo" };
            h.Levels.Add(new TOM.Level { Name = "Country", Ordinal = 0, Column = t.Columns["Country"], LineageTag = "tag-geo-0" });
            h.Levels.Add(new TOM.Level { Name = "City", Ordinal = 1, Column = t.Columns["City"], LineageTag = "tag-geo-1" });
            t.Hierarchies.Add(h);
            db.Model.Tables.Add(t);
            return db;
        }

        private static string[] Changed(TOM.Database mine, TOM.Database theirs)
            => ModelCompare.Diff(mine.Model, theirs.Model, "src", "live").Items.Where(i => i.Action != "Equal").Select(i => i.Ref).ToArray();

        private static async Task<(ApplyDiffResult preview, ApplyDiffResult pushed, string[] listed, TOM.Database src, TOM.Database live)> Press(Action<TOM.Table, TOM.Table> edit)
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var srcDb = Shape(); var live = Shape();
            edit(srcDb.Model.Tables["Customer"], live.Model.Tables["Customer"]);
            var listed = Changed(srcDb, live);
            var src = Path.Combine(Path.GetTempPath(), $"sem-vhier-{Guid.NewGuid():N}.bim");
            File.WriteAllText(src, TOM.JsonSerializer.SerializeDatabase(srcDb));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(live), null, AS.CompatibilityMode.PowerBI));
                engine.WorkspacePushHook = (bim, dels) =>
                {
                    var merged = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, AS.CompatibilityMode.PowerBI);
                    return LiveDeploy.SyncAndApply(merged.Model, live.Model, commit: true, "ep", "DS", dels, () => { }, _ => { }, identityStrict: true);
                };
                var ws = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };
                var preview = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: false, "human");
                var pushed = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: true, "human");
                return (preview, pushed, listed, srcDb, live);
            }
            finally { File.Delete(src); }
        }

        private static void Landed(string what, (ApplyDiffResult preview, ApplyDiffResult pushed, string[] listed, TOM.Database src, TOM.Database live) r)
        {
            Assert.True(r.listed.Length > 0, what + ": the change is not even listed");
            Assert.True(r.preview.FailedRefs.Length == 0, what + " preview refused: " + string.Join(" | ", r.preview.FailedRefs));
            Assert.True(r.pushed.Applied, what + " " + r.pushed.Error + " " + r.pushed.Note);
            Assert.True(r.pushed.FailedRefs.Length == 0, what + " push failed rows: " + string.Join(" | ", r.pushed.FailedRefs));
            var left = Changed(r.src, r.live);
            Assert.True(left.Length == 0, what + " still different after the push: " + string.Join(", ", left));
        }

        // A level added, levels reordered, a level re-pointed: real drill-path edits a person makes. They must go like the text does.
        [Fact]
        public async Task V1_a_new_level_publishes()
            => Landed("add level", await Press((s, _) => s.Hierarchies["Geography"].Levels.Add(new TOM.Level { Name = "State", Ordinal = 2, Column = s.Columns["State"], LineageTag = "tag-geo-2" })));

        [Fact]
        public async Task V2_reordered_levels_publish()
            => Landed("reorder", await Press((s, _) => { var h = s.Hierarchies["Geography"]; h.Levels["Country"].Ordinal = 9; h.Levels["City"].Ordinal = 0; h.Levels["Country"].Ordinal = 1; }));

        // The Plan and a measure change in the same press: the hierarchy text and the measure both land, nothing else is touched.
        [Fact]
        public async Task V3_hierarchy_text_and_a_measure_go_together()
        {
            var r = await Press((s, _) => { s.Hierarchies["Geography"].Description = "Where the customer lives"; s.Hierarchies["Geography"].DisplayFolder = "Places"; s.Measures["Customers"].Description = "How many customers"; });
            Landed("hier+measure", r);
            Assert.Contains("hierarchy:Customer/Geography", r.pushed.AppliedRefs);
            Assert.Equal("Places", r.live.Model.Tables["Customer"].Hierarchies["Geography"].DisplayFolder);
        }

        // A renamed hierarchy is a real change. It must land as ONE hierarchy under the new name, never a second copy beside the old.
        [Fact]
        public async Task V4_a_renamed_hierarchy_publishes_as_one()
        {
            var r = await Press((s, _) => s.Hierarchies["Geography"].Name = "Customer Geography");
            var names = r.live.Model.Tables["Customer"].Hierarchies.Select(h => h.Name).ToArray();
            Assert.True(names.Length == 1 && names[0] == "Customer Geography", "live hierarchies after the push: " + string.Join(", ", names)
                + " | failed: " + string.Join(" | ", r.pushed.FailedRefs));
        }

        // A renamed level (same column) is a real change, not a removed one.
        [Fact]
        public async Task V5_a_renamed_level_publishes()
            => Landed("rename level", await Press((s, _) => s.Hierarchies["Geography"].Levels["City"].Name = "Town"));

        // The person clears a level's description. The hierarchy's own description clears on the service (HierarchyPublishTests
        // clearDescription); the level's must too, or the page says nothing is left while the service still shows the old words.
        [Fact]
        public async Task V6_a_cleared_level_description_is_listed_or_cleared()
        {
            var r = await Press((s, l) => { l.Hierarchies["Geography"].Levels["City"].Description = "The town"; s.Hierarchies["Geography"].Levels["City"].Description = null; });
            Assert.True(r.listed.Length > 0 || string.IsNullOrEmpty(r.live.Model.Tables["Customer"].Hierarchies["Geography"].Levels["City"].Description),
                "the person cleared the level text, the page lists nothing, and the service still says: " + r.live.Model.Tables["Customer"].Hierarchies["Geography"].Levels["City"].Description);
        }

        // Round 2: the three edits and a measure in ONE press. All go; nothing is left different.
        [Fact]
        public async Task V8_rename_hierarchy_rename_level_clear_level_text_and_a_measure_in_one_press()
        {
            var r = await Press((s, l) =>
            {
                l.Hierarchies["Geography"].Levels["City"].Description = "The town";
                var h = s.Hierarchies["Geography"]; h.Name = "Customer Geography"; h.Levels["City"].Name = "Town"; h.Levels["Town"].Description = null;
                s.Measures["Customers"].Description = "How many customers";
            });
            Landed("combined", r);
            var live = r.live.Model.Tables["Customer"];
            Assert.Equal("Customer Geography", Assert.Single(live.Hierarchies).Name);
            Assert.NotNull(live.Hierarchies["Customer Geography"].Levels.Find("Town"));
            Assert.True(string.IsNullOrEmpty(live.Hierarchies["Customer Geography"].Levels["Town"].Description));
        }

        // A rename onto a name the service already uses cannot go. That one row says so in plain words, the press does not throw, the measure still goes.
        [Fact]
        public async Task V9_a_rename_onto_a_taken_name_is_one_plain_row_and_the_rest_goes()
        {
            var r = await Press((s, l) =>
            {
                l.Hierarchies.Add(new TOM.Hierarchy { Name = "Customer Geography", LineageTag = "tag-other" });
                l.Hierarchies["Customer Geography"].Levels.Add(new TOM.Level { Name = "Country", Ordinal = 0, Column = l.Columns["Country"], LineageTag = "tag-other-0" });
                s.Hierarchies["Geography"].Name = "Customer Geography";
                s.Measures["Customers"].Description = "How many customers";
            });
            Assert.True(r.pushed.Applied, r.pushed.Error + " " + r.pushed.Note);
            Assert.Contains("measure:Customer/Customers", r.pushed.AppliedRefs);
            Assert.Equal("How many customers", r.live.Model.Tables["Customer"].Measures["Customers"].Description);
            Assert.NotNull(r.live.Model.Tables["Customer"].Hierarchies.Find("Geography"));
        }

        // Round 2 attack: the person renames Geography and makes a NEW hierarchy under the old name. The service must end with both,
        // each with its own levels and words, and the press must not claim a hierarchy it did not send.
        [Fact]
        public async Task V10_a_renamed_hierarchy_and_a_new_one_under_the_old_name_both_publish()
        {
            var r = await Press((s, _) =>
            {
                var h = s.Hierarchies["Geography"]; h.Name = "Customer Geography"; h.Description = "Country to city";
                var n = new TOM.Hierarchy { Name = "Geography", LineageTag = "tag-geo-new", Description = "State to city" };
                n.Levels.Add(new TOM.Level { Name = "State", Ordinal = 0, Column = s.Columns["State"], LineageTag = "tag-geo-new-0" });
                n.Levels.Add(new TOM.Level { Name = "City", Ordinal = 1, Column = s.Columns["City"], LineageTag = "tag-geo-new-1" });
                s.Hierarchies.Add(n);
            });
            var live = r.live.Model.Tables["Customer"];
            var names = live.Hierarchies.Select(h => h.Name + "=" + h.Description + "[" + string.Join(",", h.Levels.OrderBy(l => l.Ordinal).Select(l => l.Name)) + "]").ToArray();
            Assert.True(live.Hierarchies.Find("Customer Geography")?.Description == "Country to city" && live.Hierarchies.Find("Geography")?.Description == "State to city",
                "service after one press: " + string.Join(" | ", names) + " ; applied: " + string.Join(", ", r.pushed.AppliedRefs) + " ; failed: " + string.Join(" | ", r.pushed.FailedRefs));
            Landed("rename plus new under the old name", r);
        }

        // Verifier (fix-followups): Undo publish after V10's press. The service must be back to ONE hierarchy, Geography, with its old
        // words, and the new hierarchy gone. If the rollback says it is back while the service keeps "Customer Geography", the page shows a
        // wrong state as right.
        [Fact]
        public async Task V11_undo_publish_after_a_rename_plus_a_new_one_under_the_old_name_puts_the_service_back()
        {
            // Its own restore folder, put back after: the press writes a restore point and the undo reads it, and a shared root swapped by another class between the two read "No restore point with that id".
            var savedRoot = RestorePointStore.RootOverride;
            var ownRoot = Path.Combine(Path.GetTempPath(), "sem-v11-rp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            RestorePointStore.RootOverride = ownRoot;
            try { await V11Body(); }
            finally { RestorePointStore.RootOverride = savedRoot; try { Directory.Delete(ownRoot, true); } catch { } }
        }

        private static async Task V11Body()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var srcDb = Shape(); var live = Shape();
            var before = TOM.JsonSerializer.SerializeDatabase(live);
            var s = srcDb.Model.Tables["Customer"];
            var h = s.Hierarchies["Geography"]; h.Name = "Customer Geography"; h.Description = "Country to city";
            var n = new TOM.Hierarchy { Name = "Geography", LineageTag = "tag-geo-new", Description = "State to city" };
            n.Levels.Add(new TOM.Level { Name = "State", Ordinal = 0, Column = s.Columns["State"], LineageTag = "tag-geo-new-0" });
            n.Levels.Add(new TOM.Level { Name = "City", Ordinal = 1, Column = s.Columns["City"], LineageTag = "tag-geo-new-1" });
            s.Hierarchies.Add(n);
            var src = Path.Combine(Path.GetTempPath(), $"sem-vhier-{Guid.NewGuid():N}.bim");
            File.WriteAllText(src, TOM.JsonSerializer.SerializeDatabase(srcDb));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(live), null, AS.CompatibilityMode.PowerBI));
                engine.WorkspacePushHook = (bim, dels) =>
                {
                    var merged = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, AS.CompatibilityMode.PowerBI);
                    return LiveDeploy.SyncAndApply(merged.Model, live.Model, commit: true, "ep", "DS", dels, () => { }, _ => { }, identityStrict: true);
                };
                var ws = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };
                await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: false, "human");
                var pushed = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: true, "human");
                Assert.True(pushed.Applied && !string.IsNullOrEmpty(pushed.RestorePointId), "the press did not land: " + pushed.Error + " " + pushed.Note);
                var preview = await engine.RollbackPushAsync(pushed.RestorePointId, commit: false);
                var undo = await engine.RollbackPushAsync(pushed.RestorePointId, commit: true);
                var names = live.Model.Tables["Customer"].Hierarchies.Select(x => x.Name + "=" + x.Description + "[" + x.LineageTag + "]").ToArray();
                var left = Changed(TOM.JsonSerializer.DeserializeDatabase(before, null, AS.CompatibilityMode.PowerBI), live);
                Assert.True(left.Length == 0 && names.Length == 1 && names[0].StartsWith("Geography="),
                    "after Undo publish the service holds: " + string.Join(" | ", names) + " ; still different: " + string.Join(", ", left)
                    + " ; preview restore " + string.Join(", ", preview.RestoredRefs ?? Array.Empty<string>()) + " remove " + string.Join(", ", preview.RemovedRefs ?? Array.Empty<string>()) + " failed " + string.Join(" | ", preview.FailedRefs ?? Array.Empty<string>())
                    + " ; undo applied=" + undo.Applied + " note: " + undo.Note + " error: " + undo.Error);
            }
            finally { File.Delete(src); }
        }

        // The one thing that truly cannot go: a removed level. It must be named, never claimed as published.
        [Fact]
        public async Task V7_a_removed_level_is_named_not_claimed()
        {
            var r = await Press((s, _) => s.Hierarchies["Geography"].Levels.Remove("City"));
            Assert.Contains("hierarchy:Customer/Geography", r.listed);
            Assert.True(r.pushed.FailedRefs.Length > 0 || !r.pushed.AppliedRefs.Contains("hierarchy:Customer/Geography"),
                "a removed level was claimed as published: " + string.Join(", ", r.pushed.AppliedRefs));
            Assert.NotNull(r.live.Model.Tables["Customer"].Hierarchies["Geography"].Levels.Find("City"));
            Console.WriteLine("V7 failed rows: " + string.Join(" | ", r.preview.FailedRefs));
        }
    }
}
