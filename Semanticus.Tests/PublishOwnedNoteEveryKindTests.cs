using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// UAT recheck 2, 19 Oct (D9): Claude accepted "remove roles with no members" on a role. That writes a BPA ignore note onto the role.
    /// One Publish press sent everything, but the note never reached the service, so the page then showed the role as "changed ... cannot
    /// send it to Power BI yet", in red, on every later visit. D1 was the same fault on a relationship. This closes the class: for EVERY
    /// object kind a waiver can write a note on, an object whose only difference is that note reads as published after ONE press, and
    /// shows no "cannot go" row before it. A real change on the same object still goes. A foreign note is never written or deleted.
    /// Offline, raw TOM, through the real engine path (preview, then press).
    /// </summary>
    [Collection("restore-root")]
    public sealed class PublishOwnedNoteEveryKindTests
    {
        private readonly RestoreRootFixture _fx;
        public PublishOwnedNoteEveryKindTests(RestoreRootFixture fx) => _fx = fx;

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const string Ignore = "BestPracticeAnalyzer_IgnoreRules";
        private const string Waivers = "Semanticus_Waivers";
        private const string NoteValue = "{\"RuleIDs\":[\"SOME_RULE\"]}";

        private static TOM.DataColumn Col(TOM.Table t, string name, string tag)
        {
            var c = new TOM.DataColumn { Name = name, DataType = TOM.DataType.String, SourceColumn = name, LineageTag = tag };
            t.Columns.Add(c);
            return c;
        }

        private static void MPartition(TOM.Table t) =>
            t.Partitions.Add(new TOM.Partition { Name = t.Name, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });

        /// <summary>One model with an object of every kind a waiver can write a note on.</summary>
        private static TOM.Database Build(Action<TOM.Model> change = null)
        {
            var m = new TOM.Model();
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            Col(sales, "Amount", "c-amount");
            var saleStore = Col(sales, "StoreKey", "c-sale-store");
            var region = Col(sales, "Region", "c-region");
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total", KPI = new TOM.KPI { TargetExpression = "100" } });
            var h = new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo" };
            h.Levels.Add(new TOM.Level { Name = "Region", Column = region });
            sales.Hierarchies.Add(h);
            MPartition(sales);
            m.Tables.Add(sales);

            var store = new TOM.Table { Name = "Store", LineageTag = "t-store" };
            var storeKey = Col(store, "StoreKey", "c-store-key");
            MPartition(store);
            m.Tables.Add(store);
            m.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "r1", FromColumn = saleStore, ToColumn = storeKey,
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One,
            });

            var cg = new TOM.Table { Name = "Time Calc", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup() };
            cg.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cg-name" });
            cg.Partitions.Add(new TOM.Partition { Name = "Time Calc", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "YTD", Expression = "SELECTEDMEASURE()" });
            m.Tables.Add(cg);

            var persp = new TOM.Perspective { Name = "Sales View" };
            persp.PerspectiveTables.Add(new TOM.PerspectiveTable { Table = sales });
            m.Perspectives.Add(persp);

            var role = new TOM.ModelRole { Name = "Regional Managers", ModelPermission = TOM.ModelPermission.Read };
            role.TablePermissions.Add(new TOM.TablePermission { Table = store, FilterExpression = "Store[StoreKey] = \"1\"" });
            m.Roles.Add(role);

            m.Cultures.Add(new TOM.Culture { Name = "en-US" });
            m.Expressions.Add(new TOM.NamedExpression { Name = "Param", Kind = TOM.ExpressionKind.M, Expression = "\"x\" meta [IsParameterQuery=true]", LineageTag = "e-param" });

            change?.Invoke(m);
            return new TOM.Database("t") { CompatibilityLevel = 1600, Model = m };
        }

        private static void Note(TOM.MetadataObject o, string name, string value = NoteValue)
        {
            dynamic d = o;
            d.Annotations.Add(new TOM.Annotation { Name = name, Value = value });
        }

        // Every kind in Waivers.AllObjects. A calculation item has no note collection of its own (TOMWrapper writes its note on the group), so the group covers it.
        private static readonly Dictionary<string, Action<TOM.Model, string>> Kinds = new()
        {
            ["model"] = (m, n) => Note(m, n),
            ["table"] = (m, n) => Note(m.Tables["Sales"], n),
            ["column"] = (m, n) => Note(m.Tables["Sales"].Columns["Amount"], n),
            ["measure"] = (m, n) => Note(m.Tables["Sales"].Measures["Total"], n),
            ["kpi"] = (m, n) => Note(m.Tables["Sales"].Measures["Total"].KPI, n),
            ["hierarchy"] = (m, n) => Note(m.Tables["Sales"].Hierarchies["Geo"], n),
            ["level"] = (m, n) => Note(m.Tables["Sales"].Hierarchies["Geo"].Levels["Region"], n),
            ["partition"] = (m, n) => Note(m.Tables["Sales"].Partitions["Sales"], n),
            ["relationship"] = (m, n) => Note(m.Relationships["r1"], n),
            ["calculationgroup"] = (m, n) => Note(m.Tables["Time Calc"].CalculationGroup, n),
            ["perspective"] = (m, n) => Note(m.Perspectives["Sales View"], n),
            ["role"] = (m, n) => Note(m.Roles["Regional Managers"], n),
            ["culture"] = (m, n) => Note(m.Cultures["en-US"], n),
            ["expression"] = (m, n) => Note(m.Expressions["Param"], n),
        };

        public static IEnumerable<object[]> Cases() =>
            Kinds.Keys.SelectMany(k => new[] { Ignore, Waivers }.Select(n => new object[] { k, n }));

        private static string Serialize(TOM.Database db) => TOM.JsonSerializer.SerializeDatabase(db);
        private static TOM.Database Load(string json) => TOM.JsonSerializer.DeserializeDatabase(json, null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI);

        private sealed class Press : IDisposable
        {
            public readonly LocalEngine Engine = new LocalEngine(new SessionManager(), new Pro());
            public readonly TOM.Database Live;
            public readonly TOM.Database Session;
            private readonly string _file = Path.Combine(Path.GetTempPath(), $"sem-everykind-{Guid.NewGuid():N}.bim");
            public readonly ModelRef Left;
            public readonly ModelRef Right = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };

            public Press(TOM.Database session, TOM.Database live)
            {
                Session = session; Live = live;
                File.WriteAllText(_file, Serialize(session));
                Left = new ModelRef { Kind = "file", Path = _file };
                Engine.WorkspaceSnapshotHook = () => Task.FromResult(Load(Serialize(Live)));
                Engine.WorkspacePushHook = (bim, _) =>
                {
                    var sent = Load(File.ReadAllText(bim));
                    var rep = LiveDeploy.SyncModels(sent.Model, Live.Model, apply: true, identityStrict: true);
                    rep.Committed = rep.TotalChanges > 0;
                    return rep;
                };
            }

            /// <summary>The rows the Publish page lists (and ticks): everything that differs, except what a rename carries.</summary>
            public string[] Shown() =>
                ModelCompare.Diff(Load(Serialize(Session)).Model, Load(Serialize(Live)).Model, "copy", "service").Items
                    .Where(i => i.Action != "Equal" && !i.CarriedByRename).Select(i => i.Ref).ToArray();

            public ModelDiffItem[] Remaining() =>
                ModelCompare.Diff(Load(Serialize(Session)).Model, Load(Serialize(Live)).Model, "copy", "service").Items
                    .Where(i => i.Action != "Equal" && !i.CarriedByRename).ToArray();

            public void Dispose() { Engine.Dispose(); try { File.Delete(_file); } catch { } }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task An_object_whose_only_difference_is_an_owned_note_goes_in_one_press_and_reads_as_published(string kind, string noteName)
        {
            using var p = new Press(Build(m => Kinds[kind](m, noteName)), Build());
            var shown = p.Shown();

            // Before the press: nothing the page lists may be a row that cannot go.
            var preview = await p.Engine.ApplyDiffAsync(p.Left, p.Right, shown, commit: false, "human");
            Assert.True(preview.FailedRefs.Length == 0, kind + " preview cannot go: " + string.Join(" | ", preview.FailedRefs));

            // ONE press.
            var r = await p.Engine.ApplyDiffAsync(p.Left, p.Right, shown, commit: true, "human");
            Assert.True(r.FailedRefs.Length == 0, kind + " press failed: " + string.Join(" | ", r.FailedRefs));

            // After the press: the service holds the note and the page lists nothing.
            Assert.True(p.Remaining().Length == 0, kind + " still listed after the press: " + string.Join(", ", p.Remaining().Select(i => i.ObjectType + " " + i.Ref)));
            var again = await p.Engine.ApplyDiffAsync(p.Left, p.Right, p.Shown(), commit: false, "human");
            Assert.Empty(again.FailedRefs);
        }

        [Fact]
        public async Task A_real_change_on_a_role_with_a_note_beside_it_is_listed_and_goes_with_the_note()
        {
            using var p = new Press(Build(m =>
            {
                var role = m.Roles["Regional Managers"];
                role.TablePermissions["Store"].FilterExpression = "Store[StoreKey] = \"2\"";
                Note(role, Ignore);
            }), Build());
            var shown = p.Shown();
            Assert.Contains("role:Regional Managers", shown);

            var r = await p.Engine.ApplyDiffAsync(p.Left, p.Right, shown, commit: true, "human");

            Assert.Empty(r.FailedRefs);
            var live = p.Live.Model.Roles["Regional Managers"];
            Assert.Equal("Store[StoreKey] = \"2\"", live.TablePermissions["Store"].FilterExpression);
            Assert.True(live.Annotations.Contains(Ignore));
            Assert.Empty(p.Remaining());
        }

        [Fact]
        public async Task A_real_change_to_a_measure_with_a_note_beside_it_is_listed_and_goes_with_the_note()
        {
            using var p = new Press(Build(m =>
            {
                var measure = m.Tables["Sales"].Measures["Total"];
                measure.Expression = "SUM(Sales[Amount]) + 1";
                Note(measure, Ignore);
            }), Build());
            var shown = p.Shown();
            Assert.Contains("measure:Sales/Total", shown);

            var r = await p.Engine.ApplyDiffAsync(p.Left, p.Right, shown, commit: true, "human");

            Assert.Empty(r.FailedRefs);
            var live = p.Live.Model.Tables["Sales"].Measures["Total"];
            Assert.Equal("SUM(Sales[Amount]) + 1", live.Expression);
            Assert.True(live.Annotations.Contains(Ignore));
            Assert.Empty(p.Remaining());
        }

        [Fact]
        public async Task A_foreign_note_on_a_role_is_never_written_to_the_service_and_one_already_there_is_never_deleted()
        {
            using var p = new Press(Build(m =>
            {
                var role = m.Roles["Regional Managers"];
                Note(role, Ignore);
                Note(role, "Client_CopyNote", "never send me");
            }), Build(m => Note(m.Roles["Regional Managers"], "Service_OwnNote", "keep me")));

            var r = await p.Engine.ApplyDiffAsync(p.Left, p.Right, p.Shown(), commit: true, "human");

            Assert.Empty(r.FailedRefs);
            var live = p.Live.Model.Roles["Regional Managers"];
            Assert.True(live.Annotations.Contains(Ignore));
            Assert.False(live.Annotations.Contains("Client_CopyNote"));
            Assert.Equal("keep me", live.Annotations["Service_OwnNote"].Value);
        }
    }
}
