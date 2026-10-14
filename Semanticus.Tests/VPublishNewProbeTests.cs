using System.Linq;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>Verifier probes for m15/publish-new (two-save order, what the report claims when a later step fails).</summary>
    public sealed class VPublishNewProbeTests
    {
        private static TOM.Model Live()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Orders", LineageTag = "tag-orders" };
            t.Partitions.Add(new TOM.Partition { Name = "Orders", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            t.Columns.Add(new TOM.DataColumn { Name = "OrderDate", SourceColumn = "OrderDate", DataType = TOM.DataType.DateTime, LineageTag = "tag-od" });
            db.Model.Tables.Add(t);
            var s = new TOM.Table { Name = "Ship Date", LineageTag = "tag-ship" };
            s.Partitions.Add(new TOM.Partition { Name = "Ship Date", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            s.Columns.Add(new TOM.DataColumn { Name = "Day", SourceColumn = "Day", DataType = TOM.DataType.DateTime, LineageTag = "tag-sd" });
            db.Model.Tables.Add(s);
            return db.Model;
        }

        private static TOM.Model Source(bool shipDescription = false)
        {
            var m = Live();
            m.Database.CompatibilityLevel = 1701;
            if (shipDescription) m.Tables["Ship Date"].Columns["Day"].Description = "The day it shipped";
            var d = new TOM.Table { Name = "Date", LineageTag = "tag-date", DataCategory = "Time" };
            d.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.CalculatedPartitionSource { Expression = "CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2030, 12, 31 ) )" } });
            var date = new TOM.CalculatedTableColumn { Name = "Date", SourceColumn = "[Date]", DataType = TOM.DataType.DateTime, IsKey = true, LineageTag = "tag-dd" };
            var year = new TOM.CalculatedColumn { Name = "Year", Expression = "YEAR ( [Date] )", DataType = TOM.DataType.Int64, LineageTag = "tag-dy" };
            d.Columns.Add(date); d.Columns.Add(year);
            var h = new TOM.Hierarchy { Name = "Calendar", LineageTag = "tag-dh" };
            h.Levels.Add(new TOM.Level { Name = "Year", Ordinal = 0, Column = year });
            h.Levels.Add(new TOM.Level { Name = "Date", Ordinal = 1, Column = date });
            d.Hierarchies.Add(h);
            m.Tables.Add(d);
            m.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-1", FromColumn = m.Tables["Orders"].Columns["OrderDate"], ToColumn = date,
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });
            return m;
        }

        private static void Calculate(TOM.Model live, System.Collections.Generic.IReadOnlyCollection<string> names)
        {
            foreach (var n in names)
            {
                var t = live.Tables[n];
                if (t.Columns.Count > 0) continue;
                t.Columns.Add(new TOM.CalculatedTableColumn { Name = "Date", SourceColumn = "[Date]", DataType = TOM.DataType.DateTime, LineageTag = "svc-1" });
            }
        }

        // P1 (harm: a partial live write reported as complete). The first Calculate fails: the table is live, but the hierarchy and the
        // relationship were never sent. They must not be claimed as synced (apply_model_diff turns SyncedRefs into AppliedRefs).
        [Fact]
        public void P1_first_calculate_fails_hierarchy_and_relationship_are_not_claimed()
        {
            var live = Live();
            var rep = LiveDeploy.SyncAndApply(Source(), live, commit: true, "ep", "db", null, () => { },
                _ => throw new System.InvalidOperationException("calc boom"),
                identityStrict: true, raiseCompatibility: n => live.Database.CompatibilityLevel = n);
            Assert.True(rep.Committed);
            Assert.Contains("calc boom", rep.Error);
            Assert.Empty(live.Tables["Date"].Hierarchies);                                 // never sent
            Assert.Empty(live.Relationships);                                              // never sent
            Assert.DoesNotContain("hierarchy:Date/Calendar", rep.SyncedRefs);
            Assert.DoesNotContain(rep.SyncedRefs, r => r.StartsWith("relationship:"));
        }

        // P2 (page truth, under-claim). The second save fails: the table went out, so it stays claimed as written.
        [Fact]
        public void P2_second_save_fails_the_table_ref_stays_claimed()
        {
            var live = Live(); var saved = 0;
            var rep = LiveDeploy.SyncAndApply(Source(), live, commit: true, "ep", "db", null,
                () => { if (++saved == 2) throw new System.InvalidOperationException("boom"); },
                names => Calculate(live, names), identityStrict: true, raiseCompatibility: n => live.Database.CompatibilityLevel = n);
            Assert.True(rep.Committed);
            Assert.Contains("table:Date", rep.SyncedRefs);
        }

        // P3 (under-claim by substring). A column on the matched table "Ship Date" went in the FIRST save; a second-save failure on the
        // new "Date" table must not withdraw it ("column:Ship Date/Day" contains "Date/").
        [Fact]
        public void P3_second_save_failure_does_not_withdraw_a_first_save_change_on_a_table_named_like_the_new_one()
        {
            var live = Live(); var saved = 0;
            var rep = LiveDeploy.SyncAndApply(Source(shipDescription: true), live, commit: true, "ep", "db", null,
                () => { if (++saved == 2) throw new System.InvalidOperationException("boom"); },
                names => Calculate(live, names), identityStrict: true, raiseCompatibility: n => live.Database.CompatibilityLevel = n);
            Assert.True(rep.Committed);
            Assert.Equal("The day it shipped", live.Tables["Ship Date"].Columns["Day"].Description);
            Assert.Contains(rep.SyncedRefs, r => r.StartsWith("column:Ship Date/Day"));
        }
    }
}
