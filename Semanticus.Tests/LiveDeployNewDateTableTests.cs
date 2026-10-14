using System.Linq;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero 21, item 8 (N1): a new calculated Date table, its hierarchy, its calendar and the relationship to it must go to a live
    /// model in one push. The service builds a calculated table's columns only on the Calculate pass, so the table goes first,
    /// then Calculate, then the hierarchy, the relationship and the calendar. Offline: two in-memory TOM trees and a recorder
    /// that plays the service (it adds the derived columns when asked to Calculate).
    /// </summary>
    public sealed class LiveDeployNewDateTableTests
    {
        private static TOM.Model Live()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Orders", LineageTag = "tag-orders" };
            t.Partitions.Add(new TOM.Partition { Name = "Orders", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            t.Columns.Add(new TOM.DataColumn { Name = "OrderDate", SourceColumn = "OrderDate", DataType = TOM.DataType.DateTime, LineageTag = "tag-od" });
            db.Model.Tables.Add(t);
            return db.Model;
        }

        // What the Plan builds: level 1701, a calculated Date table marked as the date table, a hierarchy, a calendar, Orders[OrderDate] -> Date[Date].
        private static TOM.Model Source()
        {
            var m = Live();
            m.Database.CompatibilityLevel = 1701;
            var d = new TOM.Table { Name = "Date", LineageTag = "tag-date", DataCategory = "Time" };
            d.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.CalculatedPartitionSource { Expression = "CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2030, 12, 31 ) )" } });
            var date = new TOM.CalculatedTableColumn { Name = "Date", SourceColumn = "[Date]", DataType = TOM.DataType.DateTime, IsKey = true, LineageTag = "tag-dd" };
            // The calendar template's own columns are plain calculated columns on the table: the service does not derive them.
            var year = new TOM.CalculatedColumn { Name = "Year", Expression = "YEAR ( [Date] )", DataType = TOM.DataType.Int64, LineageTag = "tag-dy" };
            // Month Name sorts by Month Number, and the calendar lists Month Number again as an associated column (the template does this).
            // Power BI refuses that repeat, so the send leaves it out and both spellings read as the same calendar.
            var monthNo = new TOM.CalculatedColumn { Name = "Month Number", Expression = "MONTH ( [Date] )", DataType = TOM.DataType.Int64, LineageTag = "tag-dmn" };
            var monthName = new TOM.CalculatedColumn { Name = "Month Name", Expression = "FORMAT ( [Date], \"mmmm\" )", DataType = TOM.DataType.String, LineageTag = "tag-dmm" };
            d.Columns.Add(date); d.Columns.Add(year); d.Columns.Add(monthNo); d.Columns.Add(monthName);
            monthName.SortByColumn = monthNo;
            var h = new TOM.Hierarchy { Name = "Calendar", LineageTag = "tag-dh" };
            h.Levels.Add(new TOM.Level { Name = "Year", Ordinal = 0, Column = year });
            h.Levels.Add(new TOM.Level { Name = "Date", Ordinal = 1, Column = date });
            d.Hierarchies.Add(h);
            var cal = new TOM.Calendar { Name = "Gregorian", LineageTag = "cal-1" };
            cal.CalendarColumnGroups.Add(new TOM.TimeUnitColumnAssociation(TOM.TimeUnit.Date) { PrimaryColumn = date });
            var moy = new TOM.TimeUnitColumnAssociation(TOM.TimeUnit.MonthOfYear) { PrimaryColumn = monthName };
            moy.AssociatedColumns.Add(monthNo);
            cal.CalendarColumnGroups.Add(moy);
            d.Calendars.Add(cal);
            m.Tables.Add(d);
            m.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-1", FromColumn = m.Tables["Orders"].Columns["OrderDate"], ToColumn = date,
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });
            return m;
        }

        // The service: Calculate on a new calculated table derives its columns (no lineage tag of ours on them).
        private static void Calculate(TOM.Model live, System.Collections.Generic.IReadOnlyCollection<string> names)
        {
            foreach (var n in names)
            {
                var t = live.Tables[n];
                if (t.Columns.Count > 0) continue;
                t.Columns.Add(new TOM.CalculatedTableColumn { Name = "Date", SourceColumn = "[Date]", DataType = TOM.DataType.DateTime, LineageTag = "svc-1" });
            }
        }

        [Fact]
        public void The_preview_says_the_table_hierarchy_calendar_and_relationship_all_go()
        {
            var rep = LiveDeploy.SyncAndApply(Source(), Live(), commit: false, "ep", "db", null, () => { }, _ => { }, identityStrict: true, raiseCompatibility: _ => { });
            Assert.Empty(rep.Unmatched);
            Assert.Contains("table:Date", rep.SyncedRefs);
            Assert.Contains("hierarchy:Date/Calendar", rep.SyncedRefs);
            Assert.Contains(rep.SyncedRefs, r => r.StartsWith("relationship:"));
        }

        [Fact]
        public void One_push_creates_the_table_then_calculates_then_adds_hierarchy_relationship_and_calendar()
        {
            var live = Live(); var src = Source();
            var order = new System.Collections.Generic.List<string>(); var saved = 0;
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null,
                () => { saved++; order.Add("save"); },
                names => { order.Add("calculate"); Calculate(live, names); },
                identityStrict: true, raiseCompatibility: n => { order.Add("raise"); live.Database.CompatibilityLevel = n; });
            Assert.Null(rep.Error);
            Assert.True(rep.Committed);
            Assert.Empty(rep.Unmatched);
            Assert.Equal(new[] { "raise", "save", "calculate", "save", "calculate" }, order.ToArray());   // table, Calculate, the rest in a second save, then Calculate for the columns it added
            Assert.IsType<TOM.CalculatedColumn>(live.Tables["Date"].Columns["Year"]);
            Assert.Equal("YEAR ( [Date] )", ((TOM.CalculatedColumn)live.Tables["Date"].Columns["Year"]).Expression);
            var d = live.Tables["Date"];
            Assert.Equal("Time", d.DataCategory);
            Assert.Equal(2, d.Hierarchies["Calendar"].Levels.Count);
            Assert.Same(d.Columns["Year"], d.Hierarchies["Calendar"].Levels[0].Column);
            var calendar = Assert.Single(d.Calendars);
            var monthGroup = calendar.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>().Single(g => g.TimeUnit == TOM.TimeUnit.MonthOfYear);
            Assert.Equal("Month Name", monthGroup.PrimaryColumn.Name);
            Assert.Empty(monthGroup.AssociatedColumns);                                  // the sort-by column is not listed twice
            Assert.True(ModelCompare.CalendarsEqual(src.Tables["Date"], d));             // and the two spellings read as one calendar: no row comes back after the push
            Assert.Same(d.Columns["Month Number"], d.Columns["Month Name"].SortByColumn);
            var r = Assert.Single(live.Relationships.Cast<TOM.SingleColumnRelationship>());
            Assert.Same(d.Columns["Date"], r.ToColumn);
            Assert.Contains("table:Date", rep.SyncedRefs);
            Assert.Contains("hierarchy:Date/Calendar", rep.SyncedRefs);
            // Nothing about the new table comes back as a change after the push (the service hands back no lineage tag for its columns).
            var again = ModelCompare.Diff(src, live, "copy", "live");
            Assert.DoesNotContain(again.Items, i => i.Action == "Update" && (i.Ref.StartsWith("table:Date") || i.Ref.StartsWith("column:Date/") || i.Ref.StartsWith("partition:Date/")));
        }

        [Fact]
        public void A_second_save_that_fails_is_told_and_the_table_still_counts_as_written()
        {
            var live = Live(); var saved = 0;
            var rep = LiveDeploy.SyncAndApply(Source(), live, commit: true, "ep", "db", null,
                () => { if (++saved == 2) throw new System.InvalidOperationException("boom"); },
                names => Calculate(live, names),
                identityStrict: true, raiseCompatibility: n => live.Database.CompatibilityLevel = n);
            Assert.True(rep.Committed);                       // the table is live
            Assert.Contains("boom", rep.Error);
            Assert.DoesNotContain("hierarchy:Date/Calendar", rep.SyncedRefs);   // what did not go is not claimed
            Assert.DoesNotContain(rep.SyncedRefs, r => r.StartsWith("relationship:"));
        }
    }
}
