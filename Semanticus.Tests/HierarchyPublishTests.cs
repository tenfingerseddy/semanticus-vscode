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
    /// Kane, 17 Oct (1.5.104): Publish listed Geography, Calendar and Products as "hierarchy changed" and refused each with "Power BI
    /// does not take this kind of change from here". The levels were the same on both sides. The compare reads the whole hierarchy
    /// (description, folder, visibility, level text); the live push carried only levels and owned annotations. Every difference the
    /// compare reports must either reach the live model or never show. The push here is the REAL LiveDeploy.SyncAndApply against an
    /// in-memory live model, so what is checked is what landed.
    /// </summary>
    [Collection("restore-root")]
    public sealed class HierarchyPublishTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static TOM.Database Shape(int level, Action<TOM.Hierarchy> hierarchy = null)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = level, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Customer", LineageTag = "tag-cust" };
            t.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            var country = new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, LineageTag = "tag-country" };
            var city = new TOM.DataColumn { Name = "City", DataType = TOM.DataType.String, LineageTag = "tag-city" };
            t.Columns.Add(country); t.Columns.Add(city);
            var h = new TOM.Hierarchy { Name = "Geography", LineageTag = "tag-geo" };
            h.Levels.Add(new TOM.Level { Name = "Country", Ordinal = 0, Column = country, LineageTag = "tag-geo-0" });
            h.Levels.Add(new TOM.Level { Name = "City", Ordinal = 1, Column = city, LineageTag = "tag-geo-1" });
            hierarchy?.Invoke(h);
            t.Hierarchies.Add(h);
            db.Model.Tables.Add(t);
            return db;
        }

        private static string WriteBim(TOM.Database db)
        {
            var p = Path.Combine(Path.GetTempPath(), $"sem-hier-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        public static TheoryData<string> Carried => new()
        {
            "description", "folder", "hidden", "hideMembers", "levelDescription", "ownedAnnotation", "clearDescription",
        };

        public static TheoryData<string> NeverListed => new()
        {
            "levelTag", "annotation", "levelAnnotation",
        };

        // `side` is the copy being edited: the session (what the person changed) or the live model (what the service holds).
        private static void Edit(string edit, TOM.Hierarchy session, TOM.Hierarchy live)
        {
            switch (edit)
            {
                case "description": session.Description = "Where the customer lives"; break;
                case "folder": session.DisplayFolder = "Places"; break;
                case "hidden": session.IsHidden = true; break;
                case "hideMembers": session.HideMembers = TOM.HierarchyHideMembersType.HideBlankMembers; break;
                case "levelDescription": session.Levels["City"].Description = "The town"; break;
                case "ownedAnnotation": session.Annotations.Add(new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[]" }); break;
                case "clearDescription": live.Description = "Old words"; break;
                case "levelTag": session.Levels["City"].LineageTag = "tag-geo-1-new"; break;
                case "annotation": session.Annotations.Add(new TOM.Annotation { Name = "Note", Value = "x" }); break;
                case "levelAnnotation": session.Levels["City"].Annotations.Add(new TOM.Annotation { Name = "Note", Value = "x" }); break;
            }
        }

        private static string[] Changed(TOM.Database mine, TOM.Database theirs)
            => ModelCompare.Diff(mine.Model, theirs.Model, "src", "live").Items.Where(i => i.Action != "Equal").Select(i => i.Ref).ToArray();

        private static async Task<(ApplyDiffResult preview, ApplyDiffResult pushed, TOM.Database src, TOM.Database live)> Press(string edit, bool raise = false)
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var srcDb = Shape(raise ? 1701 : 1600); var live = Shape(1600);
            Edit(edit, srcDb.Model.Tables["Customer"].Hierarchies["Geography"], live.Model.Tables["Customer"].Hierarchies["Geography"]);
            var src = WriteBim(srcDb);
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
                return (preview, pushed, srcDb, live);
            }
            finally { File.Delete(src); }
        }

        [Theory]
        [MemberData(nameof(Carried))]
        public async Task A_hierarchy_change_reaches_the_live_model(string edit)
        {
            var (preview, pushed, src, live) = await Press(edit);
            Assert.True(preview.FailedRefs.Length == 0, edit + " preview refused: " + string.Join(" | ", preview.FailedRefs));
            Assert.Contains("hierarchy:Customer/Geography", preview.AppliedRefs);
            Assert.True(pushed.Applied, edit + " " + pushed.Error + " " + pushed.Note);
            Assert.True(pushed.FailedRefs.Length == 0, edit + " push failed rows: " + string.Join(" | ", pushed.FailedRefs));
            Assert.Contains("hierarchy:Customer/Geography", pushed.AppliedRefs);
            var left = Changed(src, live);
            Assert.True(left.Length == 0, edit + " still different after the push: " + string.Join(", ", left));
        }

        [Theory]
        [MemberData(nameof(NeverListed))]
        public async Task A_difference_the_push_never_writes_is_not_listed(string edit)
        {
            var (preview, _, src, live) = await Press(edit);
            Assert.True(preview.AppliedRefs.Length == 0, edit + " was listed as a change: " + string.Join(", ", preview.AppliedRefs));
            Assert.Empty(Changed(src, live));
        }

        // Kane's screen: the Plan wrote a description on each of three hierarchies and the copy was raised to 1701. One press sends all of it.
        [Fact]
        public async Task The_level_raise_and_three_hierarchy_descriptions_go_in_one_press()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            TOM.Database Build(int level, bool described)
            {
                var db = new TOM.Database("t") { CompatibilityLevel = level, Model = new TOM.Model() };
                foreach (var (table, hier, a, b) in new[] { ("Customer", "Geography", "Country", "City"), ("Date", "Calendar", "Year", "Month"), ("Product", "Products", "Category", "Product") })
                {
                    var t = new TOM.Table { Name = table, LineageTag = "tag-" + table };
                    t.Partitions.Add(new TOM.Partition { Name = table, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                    var ca = new TOM.DataColumn { Name = a, DataType = TOM.DataType.String, LineageTag = "tag-" + table + a };
                    var cb = new TOM.DataColumn { Name = b, DataType = TOM.DataType.String, LineageTag = "tag-" + table + b };
                    t.Columns.Add(ca); t.Columns.Add(cb);
                    var h = new TOM.Hierarchy { Name = hier, LineageTag = "tag-h-" + hier, Description = described ? "Drill from " + a + " to " + b : null };
                    h.Levels.Add(new TOM.Level { Name = a, Ordinal = 0, Column = ca, LineageTag = "tag-l0-" + hier });
                    h.Levels.Add(new TOM.Level { Name = b, Ordinal = 1, Column = cb, LineageTag = "tag-l1-" + hier });
                    t.Hierarchies.Add(h);
                    db.Model.Tables.Add(t);
                }
                return db;
            }
            var srcDb = Build(1701, true); var live = Build(1600, false);
            var src = WriteBim(srcDb);
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(live), null, AS.CompatibilityMode.PowerBI));
                engine.WorkspacePushHook = (bim, dels) =>
                {
                    var merged = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, AS.CompatibilityMode.PowerBI);
                    return LiveDeploy.SyncAndApply(merged.Model, live.Model, commit: true, "ep", "DS", dels, () => { }, _ => { }, identityStrict: true, raiseCompatibility: n => live.CompatibilityLevel = n);
                };
                var ws = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };
                var preview = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: false, "human");
                Assert.True(preview.FailedRefs.Length == 0, "preview refused: " + string.Join(" | ", preview.FailedRefs));
                var pushed = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, ws, null, commit: true, "human");
                Assert.True(pushed.Applied, pushed.Error + " " + pushed.Note);
                Assert.True(pushed.FailedRefs.Length == 0, "push failed rows: " + string.Join(" | ", pushed.FailedRefs));
                Assert.Equal(1701, live.CompatibilityLevel);
                Assert.Equal("Drill from Country to City", live.Model.Tables["Customer"].Hierarchies["Geography"].Description);
                Assert.Equal("Drill from Category to Product", live.Model.Tables["Product"].Hierarchies["Products"].Description);
                Assert.Empty(Changed(srcDb, live));
            }
            finally { File.Delete(src); }
        }
    }
}
