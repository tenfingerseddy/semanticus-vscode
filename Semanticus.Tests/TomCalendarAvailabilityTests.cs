using System;
using System.IO;
using System.Linq;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Proves the TOM surface the calendars lane depends on actually ships in the bumped AMO/TOM
    /// (19.114.0 — the Calendar classes land in 19.114+; see docs/tom-bump-gate.md + docs/calendars-redesign-plan.md).
    /// (a) the types resolve and Table.Calendars exists; (b) a Calendar with a TimeUnitColumnAssociation
    /// survives a TMDL folder round-trip via TOM's own TmdlSerializer. This is the concrete "step 0"
    /// unblock evidence: if this reds, the calendars redesign cannot start.
    /// </summary>
    public sealed class TomCalendarAvailabilityTests
    {
        [Fact]
        public void Calendar_types_and_Table_Calendars_collection_exist()
        {
            // The types must be present in the loaded TOM assembly (not just referenced at compile time).
            Assert.NotNull(typeof(TOM.Calendar));
            Assert.NotNull(typeof(TOM.CalendarColumnGroup));
            Assert.NotNull(typeof(TOM.TimeUnitColumnAssociation));
            Assert.NotNull(typeof(TOM.TimeRelatedColumnGroup));

            // Table.Calendars is the entry point the ops build on.
            var table = new TOM.Table { Name = "Dim Date" };
            Assert.NotNull(table.Calendars);
            Assert.IsType<TOM.CalendarCollection>(table.Calendars);

            // TimeUnit enum carries the categories the redesign maps columns to.
            Assert.True(Enum.IsDefined(typeof(TOM.TimeUnit), TOM.TimeUnit.Date));
            Assert.True(Enum.IsDefined(typeof(TOM.TimeUnit), TOM.TimeUnit.Year));
        }

        [Fact]
        public void Calendar_with_time_unit_association_survives_tmdl_round_trip()
        {
            // Build an in-memory model at CL 1701 (the calendar floor) with a date table + a Date column,
            // then tag that column to the Date TimeUnit via a Calendar/TimeUnitColumnAssociation.
            var db = new TOM.Database("cal-rt") { CompatibilityLevel = 1701, Model = new TOM.Model() };
            var table = new TOM.Table { Name = "Dim Date", LineageTag = "tag-dimdate" };
            table.Partitions.Add(new TOM.Partition
            {
                Name = "Dim Date",
                Source = new TOM.CalculatedPartitionSource { Expression = "CALENDAR ( DATE(2020,1,1), DATE(2020,12,31) )" }
            });
            var dateCol = new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, LineageTag = "tag-date" };
            table.Columns.Add(dateCol);

            var calendar = new TOM.Calendar { Name = "Gregorian" };
            var assoc = new TOM.TimeUnitColumnAssociation(TOM.TimeUnit.Date) { PrimaryColumn = dateCol };
            calendar.CalendarColumnGroups.Add(assoc);
            table.Calendars.Add(calendar);
            db.Model.Tables.Add(table);

            var dir = Path.Combine(Path.GetTempPath(), "semanticus-cal-rt-" + Guid.NewGuid().ToString("N"));
            try
            {
                TOM.TmdlSerializer.SerializeModelToFolder(db.Model, dir);
                var roundTripped = TOM.TmdlSerializer.DeserializeModelFromFolder(dir);

                var rtTable = roundTripped.Tables["Dim Date"];
                Assert.Single(rtTable.Calendars);
                var rtCal = rtTable.Calendars["Gregorian"];
                Assert.Equal("Gregorian", rtCal.Name);

                var rtGroup = Assert.Single(rtCal.CalendarColumnGroups);
                var rtAssoc = Assert.IsType<TOM.TimeUnitColumnAssociation>(rtGroup);
                Assert.Equal(TOM.TimeUnit.Date, rtAssoc.TimeUnit);
                // The column reference must re-resolve after the folder round-trip.
                Assert.NotNull(rtAssoc.PrimaryColumn);
                Assert.Equal("Date", rtAssoc.PrimaryColumn.Name);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
