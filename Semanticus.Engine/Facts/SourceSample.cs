// The sample read of a Fabric SQL source (L5 of the spec-first start, Kane's ruling K1 of 1 Oct).
//
// A new model starts from a SQL source with no model behind it, so there is nothing to connect to and ask. This reads
// a bounded sample of each source table over the engine's one SQL executor (FabricSqlQuery), as the signed-in person,
// and turns it into the same DERIVED facts a live model gets (counts, shares and flags; ValueFacts), keyed by the
// SOURCE table and column so they follow any rename the person or the plan makes later. The facts are kept beside the
// built model in .semanticus/source-facts.json and served from there when the model has no live connection.
//
// Privacy, the same rule as every other fact: values are read inside the engine and dropped there. This file holds
// no row, no value, no minimum or maximum, no first or last date. The fiscal year start is a month number and the
// name of the column that gave it away.
//
// Reads are gated by the caller (LocalEngine.ReadSourceSampleAsync: the QueryData gate on the SQL target, Fabric only).
// Nothing here opens a connection: the executor is handed in, so a test counts every query it is sent.
//
// Not measured is not zero: a table whose count or values could not be read (a timeout, a failure, a cancel) carries
// the reason and NO facts, and the facts built from it leave that table's columns unmeasured.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Facts
{
    internal sealed class SourceSampleTarget
    {
        public string Server;
        public string Database;
        public string AuthMode;
        public string TenantId;
    }

    /// <summary>One source table to sample: its schema, its name and the source column names to read.</summary>
    internal sealed class SourceSampleTable
    {
        public string Schema;
        public string Name;
        public string[] Columns = Array.Empty<string>();
    }

    /// <summary>The derived facts of one SOURCE column: <see cref="MeasuredFacts"/> field for field, plus the three internal
    /// flags the plan executor's data checks read (which the contract's snapshot never serializes, so the sidecar has to).
    /// One-to-one partners are named by source column.</summary>
    internal sealed class SourceColumnFacts
    {
        public string Source { get; set; }
        public long Distinct { get; set; }
        public double BlankShare { get; set; }
        public bool WholeNumbers { get; set; }
        public bool ExactInDouble { get; set; }
        public bool FitsFixedDecimal { get; set; }
        public bool? ContiguousDays { get; set; }
        public bool UniqueOnEveryRow { get; set; }
        public bool LooksLikeYear { get; set; }
        public bool LooksLikeMonthName { get; set; }
        public bool LooksLikeMonthNumber { get; set; }
        public bool LooksLikeDayName { get; set; }
        public bool LooksLikeQuarter { get; set; }
        public bool LooksLikePeriodNumber { get; set; }
        public double Country { get; set; }
        public double Region { get; set; }
        public double Postcode { get; set; }
        public double UrlShare { get; set; }
        public double ImageUrlShare { get; set; }
        public double EmailShare { get; set; }
        public string[] OneToOneWith { get; set; } = Array.Empty<string>();

        internal static SourceColumnFacts From(string source, MeasuredFacts m, IEnumerable<string> oneToOneSources) => new SourceColumnFacts
        {
            Source = source,
            Distinct = m.Distinct, BlankShare = m.BlankShare, WholeNumbers = m.WholeNumbers, ExactInDouble = m.ExactInDouble,
            FitsFixedDecimal = m.FitsFixedDecimal, ContiguousDays = m.ContiguousDays, UniqueOnEveryRow = m.UniqueOnEveryRow,
            LooksLikeYear = m.LooksLikeYear, LooksLikeMonthName = m.LooksLikeMonthName, LooksLikeMonthNumber = m.LooksLikeMonthNumber,
            LooksLikeDayName = m.LooksLikeDayName, LooksLikeQuarter = m.LooksLikeQuarter, LooksLikePeriodNumber = m.LooksLikePeriodNumber,
            Country = m.PlaceShare.Country, Region = m.PlaceShare.Region, Postcode = m.PlaceShare.Postcode,
            UrlShare = m.UrlShare, ImageUrlShare = m.ImageUrlShare, EmailShare = m.EmailShare,
            OneToOneWith = oneToOneSources.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        };

        /// <summary>The model's own shape of the same facts. <paramref name="oneToOneRefs"/> is already worked out by the
        /// caller, because partners are named by the model's column refs and those follow renames.</summary>
        internal MeasuredFacts ToMeasured(bool sampled, string[] oneToOneRefs) => new MeasuredFacts
        {
            Distinct = Distinct, BlankShare = BlankShare, WholeNumbers = WholeNumbers, ExactInDouble = ExactInDouble,
            FitsFixedDecimal = FitsFixedDecimal, ContiguousDays = ContiguousDays, UniqueOnEveryRow = UniqueOnEveryRow,
            LooksLikeYear = LooksLikeYear, LooksLikeMonthName = LooksLikeMonthName, LooksLikeMonthNumber = LooksLikeMonthNumber,
            LooksLikeDayName = LooksLikeDayName, LooksLikeQuarter = LooksLikeQuarter, LooksLikePeriodNumber = LooksLikePeriodNumber,
            PlaceShare = new PlaceShare { Country = Country, Region = Region, City = null, Postcode = Postcode },
            UrlShare = UrlShare, ImageUrlShare = ImageUrlShare, EmailShare = EmailShare,
            OneToOneWith = oneToOneRefs ?? Array.Empty<string>(),
            Sampled = sampled,
        };
    }

    internal sealed class SourceTableFacts
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        /// <summary>The source's row count, or null when it was not counted.</summary>
        public long? Rows { get; set; }
        /// <summary>The table had more rows than the budget reads, so the facts describe a bounded slice of it.</summary>
        public bool Sampled { get; set; }
        /// <summary>Why this table has no facts (a timeout, a refusal, an error), or null when it was measured.</summary>
        public string NotMeasured { get; set; }
        /// <summary>A limit on what was measured that is not a failure (a table too wide for the cell budget).</summary>
        public string Note { get; set; }
        public SourceColumnFacts[] Columns { get; set; } = Array.Empty<SourceColumnFacts>();
    }

    internal sealed class SourceSampleResult
    {
        public int Version { get; set; } = 1;
        public string Server { get; set; }
        public string Database { get; set; }
        public string MeasuredAtUtc { get; set; }
        public int RowLimit { get; set; }
        public int CellBudget { get; set; }
        public SourceTableFacts[] Tables { get; set; } = Array.Empty<SourceTableFacts>();
        public FiscalYearStartFact FiscalYearStart { get; set; }
        /// <summary>Set when the whole read was refused (the QueryData gate, not a Fabric endpoint, no sign-in): the
        /// sentence to show. Every table is then "not measured" with the same reason, and 0 queries ran.</summary>
        public string Refused { get; set; }
        /// <summary>How many queries the read sent. Counted here, so it is true whatever executor ran them.</summary>
        public int Queries { get; set; }
        /// <summary>The start plan this sample was read for, stamped when it is kept beside the model. It is how the person's permission is found again
        /// later (the grant is kept per plan): facts from this file go to Jev only while a stored grant for THIS plan covers the sample
        /// (LocalEngine.SampleDisclosedAsync). Null on a file nobody stamped, which reads as no grant.</summary>
        public string PlanId { get; set; }

        /// <summary>The table facts for a source table. A model table whose partition names no schema matches by name alone,
        /// and only when exactly one sampled table has that name: two schemas with the same table name are not guessed
        /// between, because the wrong table's facts shown as this one's would read as measured.</summary>
        /// <summary>This sample cut to what <paramref name="grant"/> names: the grant's source (server and database, as the grant keys them) and the tables it lists
        /// by "schema.table" that it lets the sample go for. A table outside it stays in the list as NOT MEASURED with no columns and no row count, so a name that two schemas share is never
        /// matched to the wrong one and nothing measured about it can be read. A grant for another source covers none of it, and no grant covers all of it.
        /// Returns this same instance when the grant covers every table, so a fully granted sample is served as it always was.</summary>
        internal SourceSampleResult RestrictedTo(StartPlan.DisclosedScope grant)
        {
            var sourceOk = grant != null && string.Equals(StartPlan.DisclosedScope.Norm((Server ?? "").Trim() + "/" + (Database ?? "").Trim()), grant.SourceKey, StringComparison.Ordinal);
            var allowed = new HashSet<string>(sourceOk ? grant.SampleTables : Array.Empty<string>(), StringComparer.Ordinal);   // the tables whose SAMPLE the person allowed (a table whose sample they refused is in the plan and sends no fact)
            string KeyOf(SourceTableFacts t) => StartPlan.DisclosedScope.Norm(string.IsNullOrWhiteSpace(t.Schema) ? t.Name : t.Schema.Trim() + "." + (t.Name ?? "").Trim());
            if (Tables.All(t => allowed.Contains(KeyOf(t)))) return this;
            return new SourceSampleResult
            {
                Version = Version, Server = Server, Database = Database, MeasuredAtUtc = MeasuredAtUtc, RowLimit = RowLimit, CellBudget = CellBudget,
                Tables = Tables.Select(t => allowed.Contains(KeyOf(t)) ? t
                    : new SourceTableFacts { Schema = t.Schema, Name = t.Name, NotMeasured = "Left out of what you allowed." }).ToArray(),
                FiscalYearStart = FiscalYearStart != null && Tables.Any(t => allowed.Contains(KeyOf(t)) && string.Equals(t.Name, FiscalYearStart.Table, StringComparison.OrdinalIgnoreCase)) ? FiscalYearStart : null,
                Refused = Refused, Queries = Queries, PlanId = PlanId,
            };
        }

        internal SourceTableFacts Find(string schema, string name)
        {
            var named = Tables.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(schema))
            {
                var exact = named.Where(t => string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)).ToList();
                return exact.Count == 1 ? exact[0] : null;
            }
            return named.Count == 1 ? named[0] : null;
        }
    }

    /// <summary>The fiscal year start of a source's date table, worked out locally: from the column NAMES first (is there a
    /// fiscal calendar at all, and does a name say the month), then from the sample's rows when there are some. A month
    /// number and the evidence's column names, never a date. Null month means it was not worked out, with the reason.</summary>
    internal sealed class FiscalYearStartFact
    {
        /// <summary>The calendar month the fiscal year starts in, 1 to 12, or null when it could not be worked out.</summary>
        public int? StartMonth { get; set; }
        /// <summary>The month it ends in: the one before the start.</summary>
        public int? EndMonth => StartMonth == null ? (int?)null : (StartMonth.Value + 10) % 12 + 1;
        /// <summary>names | sample | none.</summary>
        public string Source { get; set; }
        public string Table { get; set; }
        public string Why { get; set; }
    }

    internal static class FiscalYearStart
    {
        private enum Kind { None, Year, Quarter, Month }

        private static readonly string[] MonthNames =
            { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

        private static string[] Tokens(string name)
        {
            var n = Regex.Replace(name ?? "", "([a-z0-9])([A-Z])", "$1 $2");
            n = Regex.Replace(n, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            return Regex.Split(n.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0).ToArray();
        }

        private static int MonthOf(string token)
        {
            for (var i = 0; i < 12; i++)
                if (token == MonthNames[i] || token == MonthNames[i].Substring(0, 3) || (i == 8 && token == "sept")) return i + 1;
            return 0;
        }

        private static Kind KindOf(string[] tokens)
        {
            if (!tokens.Any(t => t == "fiscal" || t == "fy")) return Kind.None;
            if (tokens.Any(t => t == "quarter" || t == "qtr" || t == "qtrs" || t == "quarters")) return Kind.Quarter;
            if (tokens.Any(t => t == "month" || t == "mth" || t == "mo" || t == "period" || t == "per")) return Kind.Month;
            if (tokens.Any(t => t == "week" || t == "wk" || t == "day" || t == "half" || t == "start" || t == "end" || t == "starts" || t == "ends" || t == "starting" || t == "ending" || t == "begin" || t == "begins")) return Kind.None;
            return Kind.Year;
        }

        /// <summary>From the column names alone: is there a fiscal calendar, and does a name say when the year starts?
        /// "Ending June" is a July start, "Start April" is April. A bare month ("FY_Jul") does not say which end of the
        /// year it names, so it is not guessed.</summary>
        internal static FiscalYearStartFact FromNames(string table, IReadOnlyList<string> columns)
        {
            var fiscal = (columns ?? Array.Empty<string>()).Where(c => KindOf(Tokens(c)) != Kind.None || Tokens(c).Any(t => t == "fiscal" || t == "fy")).ToArray();
            if (fiscal.Length == 0)
                return new FiscalYearStartFact { Source = "none", Table = table, Why = "No column name in " + table + " points at a fiscal calendar." };

            foreach (var column in fiscal)
            {
                var tokens = Tokens(column);
                var month = tokens.Select(MonthOf).FirstOrDefault(m => m > 0);
                if (month == 0) continue;
                var starts = tokens.Any(t => t == "start" || t == "starts" || t == "starting" || t == "begin" || t == "begins" || t == "beginning");
                var ends = tokens.Any(t => t == "end" || t == "ends" || t == "ending");
                if (starts == ends) continue;
                var start = starts ? month : month % 12 + 1;
                return new FiscalYearStartFact
                {
                    StartMonth = start, Source = "names", Table = table,
                    Why = "The column name \"" + column + "\" says the fiscal year " + (starts ? "starts in " : "ends in ") + MonthNames[month - 1] + ".",
                };
            }
            return new FiscalYearStartFact
            {
                Source = "names", Table = table,
                Why = table + " has fiscal columns (" + string.Join(", ", fiscal.Take(3)) + "), but their names do not say when the year starts. Its rows are needed for that.",
            };
        }

        private static bool TryDate(object v, out DateTime d)
        {
            switch (v)
            {
                case DateTime dt: d = dt; return true;
                case string s when Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}"):
                    return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
                default: d = default; return false;
            }
        }

        private static bool TryInt(object v, out long n)
        {
            switch (v)
            {
                case long l: n = l; return true;
                case int i: n = i; return true;
                case short s: n = s; return true;
                case byte b: n = b; return true;
                case decimal m when m == decimal.Truncate(m): n = (long)m; return true;
                case double d when d == Math.Truncate(d) && !double.IsInfinity(d): n = (long)d; return true;
                case string s: return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                default: n = 0; return false;
            }
        }

        /// <summary>The last whole number in a text: 4 in "Q4", 2 in "FY24 Q2".</summary>
        private static bool TryTrailingInt(object v, out long n)
        {
            if (TryInt(v, out n)) return true;
            var m = v is string s ? Regex.Match(s, @"(\d+)\D*$") : null;
            if (m != null && m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return true;
            n = 0;
            return false;
        }

        /// <summary>From the rows: the calendar month in which a fiscal column starts over. The date column is the one whose
        /// values are dates; a fiscal month number "restarts at 1", a fiscal year label "changes", a fiscal quarter
        /// "starts again at 1". The column is read in date order and only the MONTH of each such day is kept. Month
        /// number is the most direct evidence, then year, then quarter. Where the columns that start over do so in
        /// different months, or not on the 1st, nothing is guessed. A start over counts only between two sampled days
        /// next to each other, so a sample with gaps never names a month it did not see the year start in.</summary>
        internal static FiscalYearStartFact FromRows(string table, string[] columns, object[][] rows)
        {
            columns ??= Array.Empty<string>();
            rows ??= Array.Empty<object[]>();
            var kinds = columns.Select(c => KindOf(Tokens(c))).ToArray();
            if (kinds.All(k => k == Kind.None))
                return new FiscalYearStartFact { Source = "none", Table = table, Why = "No column name in " + table + " points at a fiscal calendar." };

            object Cell(object[] r, int i) => r != null && i < r.Length ? r[i] : null;
            // The date column: the one with the most distinct dates, among those that are nearly all dates.
            var dateIdx = -1;
            var best = 0;
            for (var c = 0; c < columns.Length; c++)
            {
                var nonBlank = rows.Select(r => Cell(r, c)).Where(v => !ValueFacts.IsBlank(v)).ToList();
                if (nonBlank.Count == 0) continue;
                var dates = nonBlank.Count(v => TryDate(v, out _));
                if (dates < nonBlank.Count * 0.9) continue;
                var distinct = nonBlank.Where(v => TryDate(v, out _)).Select(v => { TryDate(v, out var d); return d.Date; }).Distinct().Count();
                if (distinct > best) { best = distinct; dateIdx = c; }
            }
            if (dateIdx < 0)
                return new FiscalYearStartFact { Source = "sample", Table = table, Why = table + " has fiscal columns, but its rows have no date column to read them against." };

            var ordered = rows.Select(r => (Row: r, Ok: TryDate(Cell(r, dateIdx), out var d), Date: d)).Where(x => x.Ok)
                .OrderBy(x => x.Date).GroupBy(x => x.Date.Date).Select(g => g.First()).ToList();

            var disagreements = new List<string>();
            var conclusions = new List<(int Month, string Column, int Hits)>();
            foreach (var kind in new[] { Kind.Month, Kind.Year, Kind.Quarter })
            {
                foreach (var c in Enumerable.Range(0, columns.Length).Where(i => kinds[i] == kind))
                {
                    var transitions = new List<DateTime>();
                    object previous = null; var havePrevious = false; var previousDay = default(DateTime);
                    var acrossGap = 0;
                    foreach (var x in ordered)
                    {
                        var v = Cell(x.Row, c);
                        if (ValueFacts.IsBlank(v)) continue;
                        if (havePrevious)
                        {
                            var changed = ValueFacts.Key(v) != ValueFacts.Key(previous);
                            var startsOver = kind == Kind.Year ? changed : changed && TryTrailingInt(v, out var n) && n == 1;
                            // A SAMPLE CAN HAVE GAPS (a TOP read comes back in no particular order). A value that changed across days the
                            // sample did not read says only that the year started somewhere in the gap, so it is never taken as the day it
                            // started: only a change between two days next to each other is.
                            if (startsOver && (x.Date.Date - previousDay).TotalDays == 1) transitions.Add(x.Date);
                            else if (startsOver) acrossGap++;
                        }
                        previous = v; havePrevious = true; previousDay = x.Date.Date;
                    }
                    if (transitions.Count == 0)
                    {
                        disagreements.Add(columns[c] + (acrossGap > 0
                            ? " starts over only between days the sample did not read, so the day it starts is not known"
                            : " never starts over in these rows (no transition was seen)"));
                        continue;
                    }
                    var months = transitions.Select(d => d.Month).Distinct().ToList();
                    if (months.Count > 1) { disagreements.Add(columns[c] + " starts over in " + months.Count + " different months, so it is not a month-start calendar"); continue; }
                    if (transitions.Any(d => d.Day != 1)) { disagreements.Add(columns[c] + " starts over part way through a month"); continue; }
                    conclusions.Add((months[0], columns[c], transitions.Count));
                }
            }

            if (conclusions.Count == 0)
                return new FiscalYearStartFact { Source = "sample", Table = table, Why = "The start could not be worked out: " + string.Join("; ", disagreements) + "." };

            var first = conclusions[0];
            var other = conclusions.Skip(1).FirstOrDefault(x => x.Month != first.Month);
            var why = first.Column + " starts over in " + MonthNames[first.Month - 1] + " (" + first.Hits + (first.Hits == 1 ? " time" : " times") + " in the rows)."
                + (other.Column != null ? " " + other.Column + " says " + MonthNames[other.Month - 1] + " instead, so check this one." : "");
            return new FiscalYearStartFact { StartMonth = first.Month, Source = "sample", Table = table, Why = why };
        }
    }

    internal static class SourceSample
    {
        /// <summary>A Fabric SQL endpoint host: the warehouse and lakehouse SQL endpoints are
        /// *.datawarehouse.fabric.microsoft.com (older ones *.datawarehouse.pbidedicated.windows.net). Only these are
        /// sampled in this release (SQL Server comes after it). A port or a tcp: prefix is ignored; anything that is not a
        /// plain host name is refused, so a path or a look-alike suffix cannot pass.</summary>
        internal static bool IsFabricEndpoint(string server)
        {
            if (string.IsNullOrWhiteSpace(server)) return false;
            var host = server.Trim().ToLowerInvariant();
            if (host.StartsWith("tcp:", StringComparison.Ordinal)) host = host.Substring(4);
            var cut = host.IndexOfAny(new[] { ',', ':' });
            if (cut >= 0) host = host.Substring(0, cut);
            if (host.Length == 0 || !Regex.IsMatch(host, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")) return false;
            return host.EndsWith(".fabric.microsoft.com", StringComparison.Ordinal)
                || host.EndsWith(".datawarehouse.pbidedicated.windows.net", StringComparison.Ordinal);
        }

        /// <summary>The whole read refused: every table is listed as not measured with the same reason, so the plan that
        /// follows still sees the source's shape and says plainly what it did not get.</summary>
        internal static SourceSampleResult Refuse(SourceSampleTarget target, IReadOnlyList<SourceSampleTable> tables, string reason, FactsBudget budget = null)
        {
            budget ??= new FactsBudget();
            return new SourceSampleResult
            {
                Server = target?.Server, Database = target?.Database, MeasuredAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                RowLimit = budget.RowLimit, CellBudget = budget.CellBudget,
                Refused = reason,
                Tables = (tables ?? Array.Empty<SourceSampleTable>()).Select(t => new SourceTableFacts { Schema = t.Schema, Name = t.Name, NotMeasured = reason }).ToArray(),
                FiscalYearStart = FiscalNamesOnly(tables),
            };
        }

        private static FiscalYearStartFact FiscalNamesOnly(IReadOnlyList<SourceSampleTable> tables) =>
            Best((tables ?? Array.Empty<SourceSampleTable>()).Select(t => FiscalYearStart.FromNames(t.Name, t.Columns)).ToList());

        /// <summary>The one fact for the source: a month worked out beats a name that points at fiscal columns, which beats
        /// nothing. Ties go to the first table, in the order the tables were given.</summary>
        private static FiscalYearStartFact Best(IReadOnlyList<FiscalYearStartFact> facts)
        {
            var pick = facts.FirstOrDefault(f => f.StartMonth != null && f.Source == "sample")
                    ?? facts.FirstOrDefault(f => f.StartMonth != null)
                    ?? facts.FirstOrDefault(f => f.Source == "sample")
                    ?? facts.FirstOrDefault(f => f.Source == "names");
            return pick ?? new FiscalYearStartFact { Source = "none", Why = "No table has a column name that points at a fiscal calendar." };
        }

        private static string Scrub(string message)
        {
            if (string.IsNullOrEmpty(message)) return "The read failed.";
            var clean = Regex.Replace(message, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]*", "$1=***").Replace('\r', ' ').Replace('\n', ' ');
            return clean.Length > 240 ? clean.Substring(0, 240) + "..." : clean;
        }

        /// <summary>Sample every table through <paramref name="execute"/> (count, then values), under the budget. Never
        /// throws: a table that could not be read is listed as not measured with the reason, and a cancel leaves the
        /// tables not yet read the same way. The caller has already passed the QueryData gate.</summary>
        internal static async Task<SourceSampleResult> ReadAsync(Func<string, int, Task<ResultSet>> execute, SourceSampleTarget target,
            IReadOnlyList<SourceSampleTable> tables, FactsBudget budget, CancellationToken ct)
        {
            budget ??= new FactsBudget();
            tables ??= Array.Empty<SourceSampleTable>();
            var queries = 0;
            Task<ResultSet> Counted(string sql, int max) { Interlocked.Increment(ref queries); return execute(sql, max); }

            var result = new SourceSampleResult
            {
                Server = target?.Server, Database = target?.Database, MeasuredAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                RowLimit = budget.RowLimit, CellBudget = budget.CellBudget,
            };
            var read = new List<SourceTableFacts>();
            var fiscal = new List<FiscalYearStartFact>();
            var maxColumns = Math.Max(1, budget.CellBudget / Math.Max(1, budget.MinRows));

            foreach (var table in tables)
            {
                var facts = new SourceTableFacts { Schema = table.Schema, Name = table.Name };
                read.Add(facts);
                var names = FiscalYearStart.FromNames(table.Name, table.Columns);
                fiscal.Add(names);
                if (ct.IsCancellationRequested) { facts.NotMeasured = "The read was cancelled."; continue; }

                var columns = table.Columns ?? Array.Empty<string>();
                if (columns.Length > maxColumns)
                {
                    facts.Note = "Only the first " + maxColumns.ToString("N0", CultureInfo.InvariantCulture) + " of " + columns.Length.ToString("N0", CultureInfo.InvariantCulture)
                        + " columns were read, to stay within the " + budget.CellBudget.ToString("N0", CultureInfo.InvariantCulture) + " cell budget.";
                    columns = columns.Take(maxColumns).ToArray();
                }

                var spec = new MeasureTable { Name = table.Name, Schema = table.Schema, Dialect = FactsDialect.TSql, Columns = columns };
                if (names.Source != "none") spec.Inspect = rows => fiscal[fiscal.Count - 1] = FiscalYearStart.FromRows(table.Name, columns, rows);

                MeasuredTable m;
                try { m = await FactsMeasurer.MeasureAsync(Counted, spec, budget, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { m = new MeasuredTable { Error = "The read timed out or was cancelled." }; }
                catch (Exception ex) { m = new MeasuredTable { Error = Scrub(ex.Message) }; }

                if (m.Error != null) { facts.NotMeasured = Scrub(m.Error); continue; }
                facts.Rows = m.Rows;
                facts.Sampled = m.Sampled;
                facts.Columns = new SourceColumnFacts[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                {
                    var measured = FactsMeasurer.ToMeasured(m.Columns[i], m.Sampled);
                    facts.Columns[i] = SourceColumnFacts.From(columns[i], measured, m.OneToOne[i].Select(j => columns[j]));
                }
            }

            result.Tables = read.ToArray();
            result.Queries = Volatile.Read(ref queries);
            result.FiscalYearStart = Best(fiscal);
            return result;
        }
    }

    /// <summary>The sample's facts as a file beside the built model: .semanticus/source-facts.json. Derived facts only.</summary>
    internal static class SourceFactsStore
    {
        internal const string FileName = "source-facts.json";
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        internal static string PathIn(string semanticusDir) => string.IsNullOrEmpty(semanticusDir) ? null : System.IO.Path.Combine(semanticusDir, FileName);

        /// <summary>Written whole or not at all (a temp file, then a move), so a model opened mid-write never reads half a file.
        /// A refused read is not saved: it holds nothing to serve.</summary>
        internal static string Save(string semanticusDir, SourceSampleResult result)
        {
            if (string.IsNullOrEmpty(semanticusDir)) throw new InvalidOperationException("The model has no on-disk location to keep source facts beside.");
            if (result == null) throw new ArgumentNullException(nameof(result));
            Directory.CreateDirectory(semanticusDir);
            var path = PathIn(semanticusDir);
            var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, Json), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return path;
        }

        /// <summary>The saved facts, or null when there is no file, it cannot be read, or it is a version this build does not
        /// know. Never throws: a damaged file must not stop a model from opening.</summary>
        internal static SourceSampleResult Load(string semanticusDir)
        {
            try
            {
                var path = PathIn(semanticusDir);
                if (path == null || !File.Exists(path)) return null;
                var r = JsonSerializer.Deserialize<SourceSampleResult>(File.ReadAllText(path), Json);
                return r == null || r.Version != 1 || r.Tables == null ? null : r;
            }
            catch { return null; }
        }

        /// <summary>Changes when the file does, so a cached snapshot is not served after the sample is replaced.</summary>
        internal static string Signature(string semanticusDir)
        {
            try
            {
                var info = new FileInfo(PathIn(semanticusDir) ?? "");
                return info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : null;
            }
            catch { return null; }
        }
    }
}
