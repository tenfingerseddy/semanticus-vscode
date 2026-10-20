// ask_data: a question about the model's numbers, answered with ONE fixed query shape (Kane, 26 Sep: "add to this
// release"). The wire types for both doors: the MCP op ask_data and the Studio's askData RPC.
//
// A question arrives either as words (the person's or their assistant's) or as parts (the builder, or an assistant
// that already knows which columns it means). Either way the engine settles the same parts, checks them, builds the
// DAX from one template (QueryTemplate), and runs it within a row cap. What comes back says plainly how the question
// was read, by whom, what was checked, and what it could not be sure of.

using System;

namespace Semanticus.Engine.Query
{
    /// <summary>What a person or their assistant asks. Words, parts, or both: parts given are taken as said and the
    /// words are then only the record of what was asked.</summary>
    public sealed class DataAsk
    {
        /// <summary>The question in plain words, for example "net sales by store region for 2025, top 10".</summary>
        public string Question { get; set; }

        /// <summary>The id of a row of the notebook this ask opens again (the page sends it when a person opens a folded row). The row
        /// is updated in place, with its pin and its place, and no new row is added to the history. Unknown ids add a row as usual.</summary>
        public string RecordAs { get; set; }

        /// <summary>Columns to split the numbers by, as 'Table'[Column], Table[Column] or column:Table/Column.</summary>
        public string[] GroupBy { get; set; }

        /// <summary>Measures to show, by name or as measure:Table/Name.</summary>
        public string[] Measures { get; set; }

        /// <summary>Filters. Each names a column, how it filters, and the words to find among its values. The words
        /// are matched against the column's values on this computer.</summary>
        public DataAskFilter[] Filters { get; set; }

        /// <summary>Keep only this many rows, ranked by <see cref="TopBy"/>. Null or 0 keeps every row.</summary>
        public int? Top { get; set; }

        /// <summary>The measure the top rows are ranked by. Defaults to the first measure.</summary>
        public string TopBy { get; set; }

        /// <summary>Rank from the smallest value instead of the largest (a bottom N).</summary>
        public bool Lowest { get; set; }

        /// <summary>A column or measure to sort by. Defaults to the ranking measure with a top, else the first column.</summary>
        public string SortBy { get; set; }

        /// <summary>Sort from the largest or last value down.</summary>
        public bool Descending { get; set; }

        /// <summary>The most rows to return. The engine caps it at <see cref="QueryTemplate.MaxRowCap"/>.</summary>
        public int MaxRows { get; set; } = QueryTemplate.DefaultRowCap;

        /// <summary>False builds and checks the query without running it.</summary>
        public bool Run { get; set; } = true;

        internal bool HasParts =>
            (GroupBy?.Length ?? 0) > 0 || (Measures?.Length ?? 0) > 0 || (Filters?.Length ?? 0) > 0;
    }

    /// <summary>One filter as asked. <see cref="Op"/> is one of is, is_not, after, from, before, up_to.</summary>
    public sealed class DataAskFilter
    {
        public string Column { get; set; }
        public string Op { get; set; } = QueryOps.Is;
        /// <summary>The words to find among the column's values: "Coast", "2025". Several words mean any of them.</summary>
        public string[] Values { get; set; } = Array.Empty<string>();
    }

    /// <summary>The six filter operators. Text columns take the first two only.</summary>
    public static class QueryOps
    {
        public const string Is = "is";
        public const string IsNot = "is_not";
        public const string After = "after";
        public const string From = "from";
        public const string Before = "before";
        public const string UpTo = "up_to";

        public static readonly string[] All = { Is, IsNot, After, From, Before, UpTo };
        public static readonly string[] ForText = { Is, IsNot };

        public static bool Compares(string op) => op == After || op == From || op == Before || op == UpTo;
    }

    /// <summary>The answer. <see cref="Outcome"/> says which of five things happened, and nothing ran unless it is
    /// "answered".</summary>
    public sealed class DataAnswer
    {
        public const string Answered = "answered";
        /// <summary>Built and checked, not run: the caller asked not to run it, or no model with data is connected.</summary>
        public const string Built = "built";
        /// <summary>The reading was not sure, or found nothing to read. Nothing ran; <see cref="Unsure"/> says what to settle.</summary>
        public const string NeedsYou = "needs-you";
        /// <summary>A check failed. Nothing ran; <see cref="Checks"/> says which and why.</summary>
        public const string Refused = "refused";
        public const string Failed = "error";

        public string Outcome { get; set; }

        /// <summary>The id of this question in the model's history (and the notebook), so a page or an assistant can pin, close or
        /// note it. Null when the model keeps no history.</summary>
        public string QuestionId { get; set; }

        /// <summary>What the answer shows, in one plain sentence: "This shows Net Sales for each Store Region, where Year is
        /// 2025." Built by the engine from the query that runs (StatedReading), the first line of every answer on both
        /// doors. Null when no reading was settled.</summary>
        public string Statement { get; set; }

        /// <summary>The question in one plain line, for example "Net Sales by Region, Year = 2025, top 10 by Net Sales".
        /// Built by the engine from the parts; never written by Jev.</summary>
        public string Reading { get; set; }

        /// <summary>Who settled the parts: "jev", or "you" when the parts were given.</summary>
        public string ReadBy { get; set; }

        /// <summary>One plain line about how it was read, when there is something to say.</summary>
        public string Note { get; set; }

        /// <summary>What the answer assumed where the words left room, in one plain line ("Metric Category is not filtered."),
        /// and only when it assumed something (Kane, 8 Oct, K16: answer first, say the assumption, let the person change it).
        /// Each part it can change back is one of <see cref="Others"/>, ready to ask. Null when nothing was assumed.</summary>
        public string Assumption { get; set; }

        /// <summary>The other good reading, when Jev was torn between two (Kane, 28 Sep): the top one is shown and this
        /// names the other, of the same kind (a measure for a measure, a column for a column), with its parts ready to
        /// ask again as they are. Empty when Jev was sure, and on every reading a person gave as parts.</summary>
        public DataOther[] Others { get; set; } = Array.Empty<DataOther>();

        /// <summary>The settled parts, in the same shape as the ask, so a person or an assistant can change one and ask
        /// again. Filter values here are the model's own values the words matched. A question refused before its parts
        /// were settled carries the parts as they were asked, so it can still be changed and asked again. Null only for
        /// a question in words that was never read into parts.</summary>
        public DataAsk Parts { get; set; }

        /// <summary>Set when the engine's own check named something the reading leaves out of the question (a narrowing, a period, a
        /// share): one plain line saying what it could not read. A page shows that line and no figure, so a number that answers
        /// another question is never shown as the answer (UAT 17 Oct, P21). The rows still come back for the table and the
        /// assistant, whose <see cref="Assumption"/> carries the same line.</summary>
        public string Flagged { get; set; }

        /// <summary>The DAX the engine built, when it got that far.</summary>
        public string Dax { get; set; }

        public DataCheck[] Checks { get; set; } = Array.Empty<DataCheck>();

        /// <summary>What the reading could not be sure of, one plain line each.</summary>
        public string[] Unsure { get; set; } = Array.Empty<string>();

        public ColumnDef[] Columns { get; set; } = Array.Empty<ColumnDef>();
        /// <summary>The model's own format string of each column's measure (the same order as <see cref="Columns"/>; null for a
        /// column that is not a measure), so a page shows a percent measure as a percent. Null when no measure carries one.</summary>
        public string[] Formats { get; set; }
        public object[][] Rows { get; set; } = Array.Empty<object[]>();
        /// <summary>The explanation of the number, when the question asked WHY about one number ("why is Margin % 35.8% in 2025?"):
        /// the same dossier Explain this number gives, in the same filters. The page shows it in place of the table; an assistant
        /// reads it as explain_value's own answer. Null for every other question (Kane, 8 Oct, hero journey 6).</summary>
        public ExplainDossier Why { get; set; }
        public int RowCount { get; set; }
        /// <summary>True when there were more rows than the cap.</summary>
        public bool Truncated { get; set; }
        public long ElapsedMs { get; set; }

        public string Error { get; set; }

        /// <summary>True when the only thing stopping Jev from reading the question is that this model has no permission yet and the
        /// person can give it (K19, Kane 8 Oct). The page draws one Allow button beside the note; one press gives it and asks again.</summary>
        public bool NeedsPermission { get; set; }

        /// <summary>Set when the agent policy asks a person to approve the data read first.</summary>
        public string ApprovalId { get; set; }
    }

    /// <summary>The other reading of a question Jev was torn over. Asking <see cref="Parts"/> runs it with no Jev call.</summary>
    public sealed class DataOther
    {
        /// <summary>The plain line, for example "Showing Net Sales. You may have meant Units."</summary>
        public string Line { get; set; }
        /// <summary>The name of the other measure or column, for a button: "Show Units instead".</summary>
        public string Instead { get; set; }
        /// <summary>The other reading in one plain line, built the way <see cref="DataAnswer.Reading"/> is.</summary>
        public string Reading { get; set; }
        /// <summary>The whole question with that one part changed, in the ask's own shape.</summary>
        public DataAsk Parts { get; set; }
        /// <summary>True for the reading Semanticus stopped on the check's word: it is offered as it is ("Show it anyway"),
        /// run from these parts with no Jev call.</summary>
        public bool Anyway { get; set; }
    }

    /// <summary>One check the engine ran before running the query.</summary>
    public sealed class DataCheck
    {
        /// <summary>parts, reach, values, dax or rows.</summary>
        public string Name { get; set; }
        public bool Passed { get; set; }
        public string Line { get; set; }
    }
}
