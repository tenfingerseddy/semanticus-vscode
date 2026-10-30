namespace Semanticus.Engine
{
    // The wire shapes of the calendar plan (propose_calendar, setup_calendars, add_time_measures). One plan, two doors: the
    // Calendars page and the user's assistant read and write the same objects.

    /// <summary>One level of a calendar, top to bottom. Column is the column that fills it; IsNew means setup adds that column.</summary>
    public sealed class CalendarLevel
    {
        public string Unit { get; set; }      // the TimeUnit name (Year, Quarter, QuarterOfYear, Month, MonthOfYear, Week, WeekOfYear, Date, DayOfWeek, DayOfMonth)
        public string Level { get; set; }     // the plain label: "Year", "Quarter of year", "Day"
        public string Column { get; set; }
        public bool IsNew { get; set; }
        public string AlsoColumn { get; set; }   // the number column that rides along (Month Number beside Month Name)
        public string SortBy { get; set; }       // the column this one sorts by in visuals
    }

    /// <summary>One calendar as a ladder of levels.</summary>
    public sealed class CalendarLadder
    {
        public string Kind { get; set; }      // year | fiscal | retail | iso | p13 | custom
        public string Name { get; set; }      // the name that shows in DAX: 'Calendar year'
        public string Hint { get; set; }      // "January to December"
        public CalendarLevel[] Levels { get; set; } = System.Array.Empty<CalendarLevel>();
        public string[] Other { get; set; } = System.Array.Empty<string>();   // time-related columns: working day, holiday
        public bool Exists { get; set; }      // already on the model (State set-up)
    }

    /// <summary>One calendar choice on the page: its kind, plain name, whether the plan has it, and whether it is fixed.</summary>
    public sealed class CalendarKindChoice
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Short { get; set; }
        public string Hint { get; set; }
        public bool On { get; set; }
        public bool Locked { get; set; }      // calendar year is always on (the old measures use it)
        public bool Exists { get; set; }
    }

    /// <summary>A measure written the old way, and the form it moves to.</summary>
    public sealed class MeasureMove
    {
        public string Measure { get; set; }
        public string Table { get; set; }
        public string Ref { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Kind { get; set; }      // the calendar kind it moves to
        public string Calendar { get; set; }  // that calendar's name
    }

    /// <summary>A measure that stays as it is, with the reason in plain words.</summary>
    public sealed class MeasureStay
    {
        public string Measure { get; set; }
        public string Why { get; set; }
    }

    /// <summary>A time measure setup could add once its calendar exists.</summary>
    public sealed class TimeMeasureProposal
    {
        public string Name { get; set; }
        public string Table { get; set; }
        public string Dax { get; set; }
        public string Kind { get; set; }
        public string Calendar { get; set; }
        public string Base { get; set; }
    }

    /// <summary>The page's first view: what Semanticus read, and what it will do. Pure read; nothing is written.</summary>
    public sealed class CalendarProposal
    {
        public string State { get; set; }     // ready | no-date-table | set-up | live | unavailable
        public string Note { get; set; }      // a plain sentence for the states that stop here
        public int CompatibilityLevel { get; set; }
        public bool WillRaiseLevel { get; set; }
        public string DateTable { get; set; }
        public string DateColumn { get; set; }
        public string[] OtherDateTables { get; set; } = System.Array.Empty<string>();
        public string DateSource { get; set; }    // no-date-table: the column the new Date table is cut from, "Sales[Order Date]"
        public CalendarKindChoice[] Choices { get; set; } = System.Array.Empty<CalendarKindChoice>();
        public CalendarLadder[] Calendars { get; set; } = System.Array.Empty<CalendarLadder>();
        public string[] NewColumns { get; set; } = System.Array.Empty<string>();
        public int FiscalStartMonth { get; set; }
        public string FiscalClue { get; set; }    // the plain sentence about where the fiscal start came from; null when nothing said
        public MeasureMove[] Moves { get; set; } = System.Array.Empty<MeasureMove>();
        public MeasureStay[] Stays { get; set; } = System.Array.Empty<MeasureStay>();
        public TimeMeasureProposal[] TimeMeasures { get; set; } = System.Array.Empty<TimeMeasureProposal>();
    }

    /// <summary>A person's change to one level of a plan: a column to use, or null to leave the level out.</summary>
    public sealed class CalendarLevelChoice
    {
        public string Calendar { get; set; }  // the kind: year | fiscal | retail | iso | p13
        public string Unit { get; set; }      // the level's TimeUnit name
        public string Column { get; set; }    // null leaves the level out
    }

    /// <summary>One measure's before and after on the number check.</summary>
    public sealed class CalendarNumberRow
    {
        public string Measure { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public bool Same { get; set; }
    }

    /// <summary>Did the moved measures give the same numbers? Without a live model the answer is "not-checked", said plainly.</summary>
    public sealed class CalendarNumberCheck
    {
        public string Status { get; set; }    // same | differ | not-checked
        public string Reason { get; set; }    // plain words for not-checked and differ
        public string Slice { get; set; }     // what the numbers were read on, "June 2026"
        public CalendarNumberRow[] Rows { get; set; } = System.Array.Empty<CalendarNumberRow>();
    }

    public sealed class CalendarSetupResult
    {
        public long Revision { get; set; }
        public string DateTable { get; set; }
        public bool CreatedTable { get; set; }
        public int FromLevel { get; set; }
        public int ToLevel { get; set; }
        public string[] Calendars { get; set; } = System.Array.Empty<string>();
        public string[] ColumnsAdded { get; set; } = System.Array.Empty<string>();
        public MeasureMove[] Moved { get; set; } = System.Array.Empty<MeasureMove>();
        public MeasureStay[] Stays { get; set; } = System.Array.Empty<MeasureStay>();
        public CalendarNumberCheck NumberCheck { get; set; }
        public string Note { get; set; }
    }

    public sealed class AddTimeMeasuresResult
    {
        public long Revision { get; set; }
        public GeneratedObject[] Created { get; set; } = System.Array.Empty<GeneratedObject>();
        public string[] Skipped { get; set; } = System.Array.Empty<string>();
        public string Note { get; set; }
    }
}
