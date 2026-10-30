// The calendar data check and the number check (1.7). Both are live reads that answer with one calculated value, so they move no source rows
// to the assistant. Neither ever reports a run that could not happen as a pass: no live model, a failed query or a slice that is blank on both
// sides is "notChecked".

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "check_calendar_data"), Description("Check the dates behind the calendars on the LIVE model. Does the calendar cover every date in the facts (a fact date the calendar does not hold gets no year or month and drops out of time intelligence), does the calendar run past the last date with data (measures that start from the last date of the calendar then read empty months), are there duplicate dates, blanks in a level, a quarter value that sits in two years, or a column and its other name that do not pair one to one. Checks every calendar and marked date table, or one table with tableRef. Each finding is a problem (a wrong number is likely) or a note (worth knowing). status is ok, found, or notChecked; notChecked (no live model, a query that failed) is NOT a pass. Free and read-only; it asks the model for single calculated values and returns no source rows.")]
        public static Task<CalendarDataCheckResult> CheckCalendarData(IEngine engine,
            [Description("Optional. A date table to check ('table:Date' or its name). Empty checks every calendar and date table.")] string tableRef = null)
            => engine.CheckCalendarDataAsync(tableRef);

        [McpServerTool(Name = "check_same_numbers"), Description("Do an old measure and the measure that replaces it give the same numbers? Use it after moving a classic time measure to a calendar, or after any rewrite. Pass two lists of the same length, oldMeasures and newMeasures: the first old one is compared with the first new one. It compares them on the LIVE model over every slice of 'by' (default: the date table's Year and Month), treating a blank as zero, and answers same, differs (with the five slices that differ most: the slice, then the old value and the new value) or notChecked. notChecked (no live model, a query that failed, both measures blank everywhere in the slice) is NOT a pass. Free and read-only; it asks for single calculated values and returns no source rows.")]
        public static Task<MeasureNumbersResult> CheckSameNumbers(IEngine engine,
            [Description("The old measures: 'measure:Table/Name' or the measure's name.")] string[] oldMeasures,
            [Description("The new measures, in the same order as the old ones.")] string[] newMeasures,
            [Description("Optional. The columns to compare by, as 'column:Table/Column' or 'Table'[Column]. Empty picks the date table's Year and Month.")] string[] by = null)
            => engine.CheckSameNumbersAsync(oldMeasures, newMeasures, by);
    }
}
