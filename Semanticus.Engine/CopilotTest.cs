// The Copilot test (Kane, 15 Oct 2026): the model interview as a test loop. The person's own assistant writes a bank of questions with
// proven answers, a small "student" model in that assistant answers them seeing only what Copilot sees, and the engine grades each answer by
// number and names the kind of miss. This file is the deterministic KERNEL: the kinds, the miss classifier, the cause rules, the round store.
// No inference lives here (golden rule 1): the student runs in the person's assistant, on their own plan. The ops live in
// LocalEngine.CopilotTest.cs, the tools in McpTools.CopilotTest.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    // ---- the kinds of question, and the kinds of miss -----------------------------------------------------------------------------------

    public static class CopilotKinds
    {
        public const string Totals = "totals", Filters = "filters", Time = "time", Ranking = "ranking", Words = "words", Names = "names", Cannot = "cannot";
        public static readonly string[] All = { Totals, Filters, Time, Ranking, Words, Names, Cannot };

        /// <summary>The kind a word means, or null. The assistant writes these in its own words ("unclear words", "other names"), so the common spellings are taken.</summary>
        public static string Normalize(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            switch (k)
            {
                case "total": case "totals": return Totals;
                case "filter": case "filters": return Filters;
                case "time": case "dates": case "date": return Time;
                case "ranking": case "rank": case "top": return Ranking;
                case "words": case "unclear words": case "unclear": case "ambiguous": case "ambiguous words": return Words;
                case "names": case "other names": case "synonyms": case "synonym": return Names;
                case "cannot": case "cannot answer": case "no data": case "unanswerable": case "refusal": return Cannot;
                default: return null;
            }
        }
    }

    /// <summary>How an answer came out. Right and the seven kinds of miss, in the words the page uses.</summary>
    public static class CopilotMiss
    {
        public const string Right = "right";
        public const string DaxFailed = "dax_failed";
        public const string SaidNo = "said_no";                 // said it could not answer, and the model has the data
        public const string ShouldSayNo = "should_say_no";      // gave an answer for something the model does not hold
        public const string WrongField = "wrong_field";
        public const string WrongFilter = "wrong_filter";
        public const string WrongNumber = "wrong_number";
        public const string CouldNotCheck = "could_not_check";  // never counted as right or wrong

        public static readonly string[] Misses = { WrongField, WrongFilter, WrongNumber, SaidNo, ShouldSayNo, DaxFailed };

        public static string Words(string miss) => miss switch
        {
            Right => "Right",
            DaxFailed => "DAX failed",
            SaidNo => "Said no, could answer",
            ShouldSayNo => "Answered, should say no",
            WrongField => "Wrong field",
            WrongFilter => "Wrong filter",
            WrongNumber => "Wrong number",
            CouldNotCheck => "Could not check",
            _ => miss,
        };
    }

    // ---- what a piece of DAX reads ------------------------------------------------------------------------------------------------------

    /// <summary>The measures and columns a student's DAX reads, and what it filters by, found on this computer from the text alone. It is the
    /// whole of the "wrong field or wrong filter" rule: same fields and same filters but a different number is a wrong number.</summary>
    internal sealed class CopilotDaxShape
    {
        /// <summary>Measures and columns the answer shows or ranks by or splits by, as (table or null, name), as written. Compared without regard to case.</summary>
        public List<(string Table, string Name)> Fields = new List<(string, string)>();
        /// <summary>Columns the answer filters by, with the same shape.</summary>
        public List<(string Table, string Name)> FilterColumns = new List<(string, string)>();
        /// <summary>The values and numbers it compares with (a country, a year, a count for a top list), lower case.</summary>
        public SortedSet<string> Literals = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>Filter functions it uses (DATESYTD, SAMEPERIODLASTYEAR, KEEPFILTERS).</summary>
        public SortedSet<string> FilterFunctions = new SortedSet<string>(StringComparer.Ordinal);

        private enum K { Ref, Func, Str, Num, Cmp, Open, Close, Comma, Word, Other }
        private sealed class Tok { public K Kind; public string Table, Name; }
        private static readonly HashSet<string> CountArg = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TOPN" };

        private static readonly HashSet<string> FromSecondArg = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CALCULATE", "CALCULATETABLE", "FILTER", "TREATAS", "TOTALYTD", "TOTALQTD", "TOTALMTD", "KEEPFILTERS" };
        private static readonly HashSet<string> AllArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DATESYTD", "DATESQTD", "DATESMTD", "DATESBETWEEN", "DATESINPERIOD", "DATEADD", "SAMEPERIODLASTYEAR", "PARALLELPERIOD", "PREVIOUSYEAR",
            "PREVIOUSQUARTER", "PREVIOUSMONTH", "NEXTYEAR", "NEXTQUARTER", "NEXTMONTH", "ALL", "ALLEXCEPT", "ALLSELECTED", "REMOVEFILTERS", "KEEPFILTERS",
        };
        private static readonly HashSet<string> LabelFrames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ROW", "SUMMARIZECOLUMNS", "ADDCOLUMNS", "SELECTCOLUMNS", "SUMMARIZE", "GROUPBY" };

        public static CopilotDaxShape Of(string dax)
        {
            var shape = new CopilotDaxShape();
            var toks = Lex(dax ?? "");
            var frames = new Stack<(string Name, int Arg)>();
            var inSet = 0;   // depth of braces opened right after IN: { "A", "B" } is a list of filter values
            var braces = new Stack<bool>();
            var idx = 0;
            // A name the DAX gives a column of its own ("s" in ADDCOLUMNS(t, "s", ...)) and then reads back as [s] is no object of the model.
            var aliasEnd = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < toks.Count; i++)
            {
                idx = i;
                var t = toks[i];
                var prev = i > 0 ? toks[i - 1] : null;
                var next = i + 1 < toks.Count ? toks[i + 1] : null;
                switch (t.Kind)
                {
                    case K.Func:
                        if (AllArgs.Contains(t.Name) || FromSecondArg.Contains(t.Name)) shape.FilterFunctions.Add(t.Name.ToUpperInvariant());
                        break;
                    case K.Open:
                        frames.Push((prev != null && prev.Kind == K.Func ? prev.Name : "", 0));
                        break;
                    case K.Comma:
                        if (frames.Count > 0) { var f = frames.Pop(); frames.Push((f.Name, f.Arg + 1)); }
                        break;
                    case K.Close:
                        if (frames.Count > 0) frames.Pop();
                        break;
                    case K.Other when t.Name == "{":
                        braces.Push(prev != null && IsIn(prev));
                        if (braces.Peek()) inSet++;
                        break;
                    case K.Other when t.Name == "}":
                        if (braces.Count > 0 && braces.Pop()) inSet--;
                        break;
                    case K.Ref:
                    {
                        if (t.Table == null && aliasEnd.TryGetValue(t.Name, out var aliasDone) && i > aliasDone) break;
                        var cmp = (prev != null && (prev.Kind == K.Cmp || IsIn(prev))) || (next != null && (next.Kind == K.Cmp || IsIn(next)));
                        var inFilter = cmp;
                        if (!inFilter && frames.Count > 0)
                        {
                            var (name, arg) = frames.Peek();
                            if (AllArgs.Contains(name)) inFilter = true;
                            else if (FromSecondArg.Contains(name) && arg >= 1) inFilter = true;
                        }
                        (inFilter ? shape.FilterColumns : shape.Fields).Add((t.Table, t.Name));
                        break;
                    }
                    case K.Str:
                    {
                        // The name of a result column ("v" in ROW("v", ...)) is a label, not a value somebody filtered by.
                        var label = frames.Count > 0 && LabelFrames.Contains(frames.Peek().Name)
                            && (prev == null || prev.Kind == K.Open || prev.Kind == K.Comma) && next != null && next.Kind == K.Comma;
                        if (label) NoteAlias(i);
                        if (!label && IsFilterLiteral()) shape.Literals.Add(t.Name.ToLowerInvariant());
                        break;
                    }
                    case K.Num:
                        if (IsFilterLiteral()) shape.Literals.Add(t.Name);
                        break;
                }
            }
            return shape;

            // The label at <at> names the column the expression after it computes. Where that expression is only the same-named measure
            // ("Net Sales", [Net Sales]) nothing is renamed; otherwise the name is local to the query from the end of the expression on.
            void NoteAlias(int at)
            {
                var first = at + 2 < toks.Count ? toks[at + 2] : null;
                var end = at + 2; var depth = 0;
                for (; end < toks.Count; end++)
                {
                    var k = toks[end].Kind;
                    if (k == K.Open) depth++;
                    else if (k == K.Close) { if (depth == 0) break; depth--; }
                    else if (k == K.Comma && depth == 0) break;
                }
                var same = first != null && first.Kind == K.Ref && first.Table == null && end == at + 3
                    && string.Equals(first.Name, toks[at].Name, StringComparison.OrdinalIgnoreCase);
                if (!same && !aliasEnd.ContainsKey(toks[at].Name)) aliasEnd[toks[at].Name] = end;
            }

            // A literal is a filter value only in a filter position, the rule the refs follow: next to a comparison or IN, in a list after IN, in a
            // filter argument (or a function inside one), or the count of a top list. ROUND(x, 0) and x + 0 are arithmetic, not a filter.
            bool IsFilterLiteral()
            {
                var before = idx > 0 ? toks[idx - 1] : null;
                var after = idx + 1 < toks.Count ? toks[idx + 1] : null;
                if (inSet > 0) return true;
                if (before != null && (before.Kind == K.Cmp || IsIn(before))) return true;
                if (after != null && (after.Kind == K.Cmp || IsIn(after))) return true;
                var first = true;
                foreach (var (name, arg) in frames)
                {
                    if (AllArgs.Contains(name) || FromSecondArg.Contains(name) && arg >= 1) return true;
                    if (first && CountArg.Contains(name) && arg == 0) return true;
                    first = false;
                }
                return false;
            }
        }

        private static bool IsIn(Tok t) => t.Kind == K.Word && string.Equals(t.Name, "IN", StringComparison.OrdinalIgnoreCase);

        private static List<Tok> Lex(string s)
        {
            var toks = new List<Tok>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/' || c == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? s.Length : end + 2; continue; }
                if (c == '"') { toks.Add(new Tok { Kind = K.Str, Name = ReadQuoted(s, ref i, '"') }); continue; }
                if (c == '\'')
                {
                    var table = ReadQuoted(s, ref i, '\'');
                    if (i < s.Length && s[i] == '[') { var col = ReadBracket(s, ref i); toks.Add(new Tok { Kind = K.Ref, Table = table, Name = col }); }
                    else toks.Add(new Tok { Kind = K.Word, Name = table });
                    continue;
                }
                if (c == '[') { var name = ReadBracket(s, ref i); toks.Add(new Tok { Kind = K.Ref, Name = name }); continue; }
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                    var word = s.Substring(start, i - start);
                    if (i < s.Length && s[i] == '[') { var col = ReadBracket(s, ref i); toks.Add(new Tok { Kind = K.Ref, Table = word, Name = col }); continue; }
                    var j = i; while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                    toks.Add(new Tok { Kind = j < s.Length && s[j] == '(' ? K.Func : K.Word, Name = word });
                    continue;
                }
                if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    toks.Add(new Tok { Kind = K.Num, Name = s.Substring(start, i - start) });
                    continue;
                }
                if (c == '(') { toks.Add(new Tok { Kind = K.Open }); i++; continue; }
                if (c == ')') { toks.Add(new Tok { Kind = K.Close }); i++; continue; }
                if (c == ',') { toks.Add(new Tok { Kind = K.Comma }); i++; continue; }
                if (c == '=' || c == '<' || c == '>')
                {
                    i++;
                    if (i < s.Length && (s[i] == '=' || s[i] == '>')) i++;
                    toks.Add(new Tok { Kind = K.Cmp });
                    continue;
                }
                toks.Add(new Tok { Kind = K.Other, Name = c.ToString() }); i++;
            }
            return toks;
        }

        private static string ReadQuoted(string s, ref int i, char q)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                if (s[i] == q) { if (i + 1 < s.Length && s[i + 1] == q) { sb.Append(q); i += 2; continue; } i++; break; }
                sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        private static string ReadBracket(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                if (s[i] == ']') { if (i + 1 < s.Length && s[i + 1] == ']') { sb.Append(']'); i += 2; continue; } i++; break; }
                sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        /// <summary>A bare [Total Sales] and a Sales[Total Sales] are one thing when the names match; two tables with the same column name are not.</summary>
        internal static bool SameRef((string Table, string Name) a, (string Table, string Name) b) =>
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            && (a.Table == null || b.Table == null || string.Equals(a.Table, b.Table, StringComparison.OrdinalIgnoreCase));

        internal static bool SameRefs(IEnumerable<(string Table, string Name)> a, IEnumerable<(string Table, string Name)> b)
        {
            var left = a.ToList(); var right = b.ToList();
            return left.All(x => right.Any(y => SameRef(x, y))) && right.All(y => left.Any(x => SameRef(x, y)));
        }

        /// <summary>The refs of one side the other side does not have.</summary>
        internal static List<(string Table, string Name)> Missing(IEnumerable<(string Table, string Name)> from, IEnumerable<(string Table, string Name)> inOther)
        {
            var other = inOther.ToList();
            return from.Where(x => !other.Any(y => SameRef(x, y))).Distinct().ToList();
        }

        internal bool SameFilters(CopilotDaxShape other) =>
            SameRefs(FilterColumns, other.FilterColumns) && Literals.SetEquals(other.Literals) && FilterFunctions.SetEquals(other.FilterFunctions);
    }

    // ---- the grade of one answer -------------------------------------------------------------------------------------------------------------

    internal sealed class CopilotGrade
    {
        public string Miss;                 // CopilotMiss.*
        public string Detail;               // the engine's evidence, one sentence
        public string StudentAnswer;        // what the student's DAX returned, as text; null when it did not run
        public bool Right => Miss == CopilotMiss.Right;
    }

    internal static class CopilotGrader
    {
        /// <summary>Make a bare expression a runnable query. Students are asked for EVALUATE ROW(...), and a small model sometimes writes only the expression.</summary>
        public static string AsQuery(string dax)
        {
            var d = (dax ?? "").Trim();
            if (d.Length == 0) return d;
            var head = d.TrimStart();
            if (head.StartsWith("EVALUATE", StringComparison.OrdinalIgnoreCase) || head.StartsWith("DEFINE", StringComparison.OrdinalIgnoreCase)) return d;
            return "EVALUATE ROW(\"v\", " + d + ")";
        }

        /// <summary>Grade one answer. <paramref name="rs"/> is what the student's DAX returned (null when it was not run: said no, or offline).
        /// The scorer is the interview's own (number within max(1e-6, 1e-9 x value), blank is never 0, a table without regard to row order); this adds
        /// only the names of the misses.</summary>
        public static CopilotGrade Grade(InterviewQuestion q, string studentDax, bool saidNo, ResultSet rs)
        {
            var wantsNo = q.Tier == "refusal" || q.ExpectRefusal;
            var answered = !saidNo && !string.IsNullOrWhiteSpace(studentDax);
            if (wantsNo)
                return answered
                    ? new CopilotGrade { Miss = CopilotMiss.ShouldSayNo, Detail = "The model holds nothing to answer this with, and the student gave an answer." }
                    : new CopilotGrade { Miss = CopilotMiss.Right, Detail = "The student said it could not answer, which is right for this question." };
            if (!answered)
                return new CopilotGrade { Miss = CopilotMiss.SaidNo, Detail = "The student said it could not answer, and the model has the data." };
            if (rs == null)
                return new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = "Offline: the answer was not run, so it was not checked." };
            if (rs.PolicyRefused)
                return new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = "The agent policy stopped the answer from running, so it was not checked." };
            if (!string.IsNullOrEmpty(rs.Error))
                return rs.AuthFailed
                    ? new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = XmlaAuthHint.ProbeHint() }
                    : new CopilotGrade { Miss = CopilotMiss.DaxFailed, Detail = "The student's DAX did not run: " + Shorten(rs.Error, 200) };

            var (outcome, detail) = InterviewScoring.ScoreValue(q, rs);
            var shown = StudentText(rs);
            if (outcome == InterviewScoring.Correct) return new CopilotGrade { Miss = CopilotMiss.Right, Detail = detail, StudentAnswer = shown };
            if (outcome == InterviewScoring.Unverified)
            {
                // The reference was proven, so the shape of the answer is known. A student that returns another shape (no row, several rows, another column count)
                // gave a wrong answer: only a cut-off result is genuinely unchecked.
                if (rs.Truncated) return new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = detail, StudentAnswer = shown };
                return new CopilotGrade { Miss = CopilotMiss.WrongNumber, Detail = detail, StudentAnswer = shown };
            }
            var s = CopilotDaxShape.Of(studentDax);
            var r = CopilotDaxShape.Of(q.Query);
            string miss;
            if (!CopilotDaxShape.SameRefs(s.Fields, r.Fields)) miss = CopilotMiss.WrongField;
            else if (!s.SameFilters(r)) miss = CopilotMiss.WrongFilter;
            else miss = CopilotMiss.WrongNumber;
            return new CopilotGrade { Miss = miss, Detail = detail, StudentAnswer = shown };
        }

        /// <summary>The answer as the page shows it: one number or word, or "N rows".</summary>
        public static string StudentText(ResultSet rs)
        {
            if (rs == null || rs.Rows == null) return null;
            if (rs.Rows.Length == 0) return "no rows";
            if (rs.Rows.Length == 1 && rs.Rows[0].Length > 0) return DaxBench.Fmt(rs.Rows[0][rs.Rows[0].Length - 1]);
            return rs.Rows.Length + " rows";
        }

        /// <summary>The right answer as the page shows it: the stored number or word, or "N rows".</summary>
        public static string ReferenceText(InterviewQuestion q)
        {
            if (q.Tier == "refusal" || q.ExpectRefusal) return "Say no";
            if (q.ExpectedMatrix != null && q.ExpectedMatrix.Length > 0) return q.ExpectedMatrix.Length + " rows";
            return InterviewScoring.IsBlankSentinel(q.ExpectedValue) ? "blank" : q.ExpectedValue;
        }

        public static string Shorten(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }

    // ---- the cause rules, and the words Jev answers in -----------------------------------------------------------------------------------

    public static class CopilotCause
    {
        public const string UnknownWord = "unknown_word", CalendarVsFiscal = "calendar_vs_fiscal", SiblingMeasure = "sibling_measure",
            MissingDescription = "missing_description", NoData = "no_data", DaxSlip = "dax_slip", Other = "other";
        public static readonly string[] Causes = { UnknownWord, CalendarVsFiscal, SiblingMeasure, MissingDescription, NoData, DaxSlip, Other };

        public const string FixInstruction = "instruction", FixDescription = "description", FixOtherName = "other_name", FixAiSchema = "ai_schema", FixNone = "none";
        public static readonly string[] Fixes = { FixInstruction, FixDescription, FixOtherName, FixAiSchema, FixNone };

        public static string CauseWords(string cause) => cause switch
        {
            UnknownWord => "A word in the question is not a name the model knows",
            CalendarVsFiscal => "The calendar year was used, and the model runs on another year",
            SiblingMeasure => "A similar field was used in place of the right one",
            MissingDescription => "The right field has no description that says what it is",
            NoData => "The model has no data for this, and the student answered anyway",
            DaxSlip => "A mistake in the DAX that the model cannot fix",
            _ => "Another cause",
        };

        public static string FixWords(string fix) => fix switch
        {
            FixInstruction => "AI instructions",
            FixDescription => "A description",
            FixOtherName => "Other names",
            FixAiSchema => "What Copilot can see",
            _ => "No change",
        };
    }

    /// <summary>One miss, with what the grouping needs to read it.</summary>
    internal sealed class CopilotMissFacts
    {
        public int N;
        public string Question, Kind, Miss, StudentDax, ReferenceDax;
        /// <summary>Fields the student read that the right answer does not (and the reverse), by name.</summary>
        public List<string> StudentOnly = new List<string>(), ReferenceOnly = new List<string>();
        public List<string> FilterDifferences = new List<string>();
        /// <summary>Filter functions one side uses and the other does not, and "year end": signs of a cause, never named as objects.</summary>
        public List<string> FilterSignals = new List<string>();
    }

    internal sealed class CopilotGroupOut
    {
        public string Cause, Fix, Label, By;
        public List<int> Numbers = new List<int>();
        public List<string> Objects = new List<string>();
    }

    internal static class CopilotCauseRules
    {
        public static CopilotMissFacts Facts(int n, InterviewQuestion q, string studentDax, string miss)
        {
            var f = new CopilotMissFacts { N = n, Question = q.Question, Kind = q.Kind, Miss = miss, StudentDax = studentDax, ReferenceDax = q.Query };
            if (!string.IsNullOrWhiteSpace(studentDax) && !string.IsNullOrWhiteSpace(q.Query))
            {
                var s = CopilotDaxShape.Of(studentDax); var r = CopilotDaxShape.Of(q.Query);
                f.StudentOnly = CopilotDaxShape.Missing(s.Fields, r.Fields).Select(Name).ToList();
                f.ReferenceOnly = CopilotDaxShape.Missing(r.Fields, s.Fields).Select(Name).ToList();
                f.FilterDifferences = CopilotDaxShape.Missing(s.FilterColumns, r.FilterColumns).Concat(CopilotDaxShape.Missing(r.FilterColumns, s.FilterColumns)).Select(Name).ToList();
                foreach (var fn in s.FilterFunctions.Except(r.FilterFunctions).Concat(r.FilterFunctions.Except(s.FilterFunctions))) f.FilterSignals.Add(fn.ToLowerInvariant());
                if (r.Literals.Any(l => l.Contains("-")) && !s.Literals.SetEquals(r.Literals)) f.FilterSignals.Add("year end");
            }
            return f;
        }

        private static string Name((string Table, string Name) r) => r.Table == null ? r.Name : r.Table + "[" + r.Name + "]";

        private static string[] Words(string s) =>
            System.Text.RegularExpressions.Regex.Split((s ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 2).ToArray();

        /// <summary>The cause and the fix a plain rule gives one miss. Used when Jev is off, slow or not sure.</summary>
        public static (string Cause, string Fix) Rule(CopilotMissFacts f)
        {
            switch (f.Miss)
            {
                case CopilotMiss.ShouldSayNo: return (CopilotCause.NoData, CopilotCause.FixInstruction);
                case CopilotMiss.SaidNo: return (CopilotCause.UnknownWord, CopilotCause.FixOtherName);
                case CopilotMiss.DaxFailed: return (CopilotCause.DaxSlip, CopilotCause.FixNone);
                case CopilotMiss.WrongField:
                {
                    var question = Words(f.Question);
                    var right = f.ReferenceOnly.SelectMany(Words).ToArray();
                    var wrong = f.StudentOnly.SelectMany(Words).ToArray();
                    var shared = right.Intersect(wrong).Any();
                    var heard = right.Any(w => question.Contains(w));
                    if (!shared && !heard) return (CopilotCause.UnknownWord, CopilotCause.FixOtherName);
                    return (CopilotCause.SiblingMeasure, CopilotCause.FixDescription);
                }
                case CopilotMiss.WrongFilter:
                    return f.FilterDifferences.Concat(f.FilterSignals).Select(d => d.ToLowerInvariant()).Any(d => d.Contains("fiscal") || d == "year end" || d.StartsWith("dates") || d.Contains("year"))
                        ? (CopilotCause.CalendarVsFiscal, CopilotCause.FixInstruction)
                        : (CopilotCause.Other, CopilotCause.FixDescription);
                default: return (CopilotCause.Other, CopilotCause.FixInstruction);
            }
        }

        /// <summary>Group the misses by (cause, fix), naming the objects. The label is the engine's own sentence, whoever picked the cause.</summary>
        public static List<CopilotGroupOut> Group(IEnumerable<(CopilotMissFacts Facts, string Cause, string Fix, bool Jev)> misses)
        {
            var groups = new List<CopilotGroupOut>();
            var jev = new Dictionary<CopilotGroupOut, bool>();
            foreach (var m in misses)
            {
                var objects = m.Facts.ReferenceOnly.Concat(m.Facts.StudentOnly).Concat(m.Facts.FilterDifferences).Distinct().Take(4).ToList();
                var g = groups.FirstOrDefault(x => x.Cause == m.Cause && x.Fix == m.Fix);
                if (g == null) { groups.Add(g = new CopilotGroupOut { Cause = m.Cause, Fix = m.Fix }); jev[g] = false; }
                g.Numbers.Add(m.Facts.N);
                if (m.Jev) jev[g] = true;
                foreach (var o in objects) if (!g.Objects.Contains(o) && g.Objects.Count < 6) g.Objects.Add(o);
            }
            foreach (var g in groups)
            {
                g.By = jev[g] ? "jev" : "rules";
                g.Label = CopilotCause.CauseWords(g.Cause) + (g.Objects.Count > 0 ? ": " + string.Join(", ", g.Objects) : "");
            }
            return groups.OrderByDescending(g => g.Numbers.Count).ThenBy(g => g.Cause, StringComparer.Ordinal).ToList();
        }
    }

    // ---- the round store ---------------------------------------------------------------------------------------------------------------------

    /// <summary>A round and what happened in it, read back from the rounds file. A held-back answer keeps only its number and its kind of miss: no DAX, no
    /// value, so nothing the assistant could read back from this file tunes to them.</summary>
    internal sealed class CopilotRoundRecord
    {
        public string Id;
        public int Number;
        public string When, Student, BankKey;
        public int Total;
        public List<CopilotRoundChange> Changes = new List<CopilotRoundChange>();
        public List<CopilotAnswerRecord> Answers = new List<CopilotAnswerRecord>();
        public List<CopilotGroupRecord> Groups = new List<CopilotGroupRecord>();
        public string GroupedBy;
        public string Note;
    }

    public sealed class CopilotRoundChange
    {
        public string Title { get; set; }
        /// <summary>AI instructions | Description | Other names, the way the page says it.</summary>
        public string Where { get; set; }
        public string On { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        /// <summary>The numbers of the misses of the round before that this change is for.</summary>
        public int[] ForMisses { get; set; } = Array.Empty<int>();
    }

    internal sealed class CopilotAnswerRecord
    {
        public int N;
        public string Qid, Question, Kind, Miss, StudentDax, StudentAnswer, ReferenceDax, ReferenceAnswer, Detail;
        public bool Held;
    }

    internal sealed class CopilotGroupRecord
    {
        public string Cause { get; set; }
        public string Fix { get; set; }
        public string Label { get; set; }
        public string By { get; set; }
        public int[] Numbers { get; set; } = Array.Empty<int>();
        public string[] Objects { get; set; } = Array.Empty<string>();
    }

    internal static class CopilotRoundStore
    {
        public const string FileName = "rounds.jsonl";
        private const int MaxLineBytes = 60 * 1024;
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        private sealed class Line
        {
            public string Op { get; set; }              // round | answer | groups
            public string Round { get; set; }
            public string When { get; set; }
            // round
            public int? Number { get; set; }
            public string Student { get; set; }
            public string BankKey { get; set; }
            public int? Total { get; set; }
            public CopilotRoundChange[] Changes { get; set; }
            public string Note { get; set; }
            // answer
            public int? N { get; set; }
            public string Qid { get; set; }
            public string Question { get; set; }
            public string Kind { get; set; }
            public string Miss { get; set; }
            public string StudentDax { get; set; }
            public string StudentAnswer { get; set; }
            public string ReferenceDax { get; set; }
            public string ReferenceAnswer { get; set; }
            public string Detail { get; set; }
            public bool? Held { get; set; }
            // groups
            public string By { get; set; }
            public CopilotGroupRecord[] Groups { get; set; }
        }

        public static string NewId() => "rd-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

        private static bool Append(string file, Line line, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(file)) { why = "there is no store file to write (the model has no on-disk anchor)"; return false; }
            string text;
            try { text = JsonSerializer.Serialize(line, Json); } catch (Exception ex) { why = ex.Message; return false; }
            if (Encoding.UTF8.GetByteCount(text) > MaxLineBytes) { why = "a record is over the line size"; return false; }
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, text + "\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        public static bool AddRound(string file, CopilotRoundRecord r, out string why) => Append(file, new Line
        {
            Op = "round", Round = r.Id, When = r.When, Number = r.Number, Student = r.Student, BankKey = r.BankKey, Total = r.Total,
            Changes = r.Changes.ToArray(), Note = r.Note,
        }, out why);

        public static bool AddAnswer(string file, string round, CopilotAnswerRecord a, string when, out string why) => Append(file, new Line
        {
            Op = "answer", Round = round, When = when, N = a.N, Qid = a.Qid, Question = a.Held ? null : a.Question, Kind = a.Kind, Miss = a.Miss, Held = a.Held,
            // a held-back answer keeps its kind of miss and nothing else
            StudentDax = a.Held ? null : a.StudentDax, StudentAnswer = a.Held ? null : a.StudentAnswer,
            ReferenceDax = a.Held ? null : a.ReferenceDax, ReferenceAnswer = a.Held ? null : a.ReferenceAnswer, Detail = a.Held ? null : a.Detail,
        }, out why);

        public static bool SetGroups(string file, string round, string by, IEnumerable<CopilotGroupRecord> groups, string when, out string why) => Append(file, new Line
        {
            Op = "groups", Round = round, When = when, By = by, Groups = groups.ToArray(),
        }, out why);

        /// <summary>Replay the file into rounds, oldest first. A corrupt line is skipped; a later answer for the same number in a round replaces the earlier one.</summary>
        public static List<CopilotRoundRecord> Read(string file)
        {
            var rounds = new List<CopilotRoundRecord>();
            var byId = new Dictionary<string, CopilotRoundRecord>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return rounds;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { return rounds; }
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Line l;
                try { l = JsonSerializer.Deserialize<Line>(raw, Json); } catch { continue; }
                if (l == null || string.IsNullOrEmpty(l.Op) || string.IsNullOrEmpty(l.Round)) continue;
                switch (l.Op)
                {
                    case "round":
                        if (byId.ContainsKey(l.Round)) break;
                        var r = new CopilotRoundRecord
                        {
                            Id = l.Round, Number = l.Number ?? rounds.Count + 1, When = l.When, Student = l.Student, BankKey = l.BankKey, Total = l.Total ?? 0,
                            Changes = (l.Changes ?? Array.Empty<CopilotRoundChange>()).ToList(), Note = l.Note,
                        };
                        byId[l.Round] = r; rounds.Add(r);
                        break;
                    case "answer":
                        if (!byId.TryGetValue(l.Round, out var ar) || l.N == null) break;
                        ar.Answers.RemoveAll(x => x.N == l.N.Value);
                        ar.Answers.Add(new CopilotAnswerRecord
                        {
                            N = l.N.Value, Qid = l.Qid, Question = l.Question, Kind = l.Kind, Miss = l.Miss, StudentDax = l.StudentDax, StudentAnswer = l.StudentAnswer,
                            ReferenceDax = l.ReferenceDax, ReferenceAnswer = l.ReferenceAnswer, Detail = l.Detail, Held = l.Held ?? false,
                        });
                        break;
                    case "groups":
                        if (!byId.TryGetValue(l.Round, out var gr)) break;
                        gr.Groups = (l.Groups ?? Array.Empty<CopilotGroupRecord>()).ToList();
                        gr.GroupedBy = l.By;
                        break;
                }
            }
            foreach (var r in rounds) r.Answers.Sort((a, b) => a.N.CompareTo(b.N));
            return rounds;
        }
    }
}
