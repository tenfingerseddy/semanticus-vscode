using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>THE FORMAT FILL RULE (coordinator, 28 Sep): the ONE answer to "may Semanticus write this format by itself?".
    /// Every automatic FILL of a blank format asks <see cref="MayFill"/> first, on both doors and both tiers: the analyzer's
    /// fix path (one press, Fix all, apply_safe_fixes, the change plan's seeding and re-check, the Plan's best-practice row,
    /// through CanAutoFix and ApplyFix) and Jev's format lines on the Plan (the measures and column-format families ask it
    /// when they prepare their asks, and a line it cannot prove arrives as a suggestion). What does NOT ask it, and is not a
    /// fill: a format a person or their assistant sets on purpose (set_measure_format, set_property), a measure the engine
    /// creates with a format of its own (the Plan's starter measures, whose template fixes what they show), and time intelligence copying its base measure's own format
    /// (PlanExecutorKinds). A blank format that is filled with the wrong kind
    /// changes what a person reads (#,0 shows a 73% utilisation as 1, drops the cents from money and the hours from
    /// half days, and shows a date as a serial number), so a fill is allowed only where the engine can PROVE the value
    /// is a whole number:
    /// a measure that is a count (COUNT, COUNTA, COUNTROWS, DISTINCTCOUNT, DISTINCTCOUNTNOBLANK), or a SUM of a
    /// whole-number column; or an Int64 column that is a quantity AND that the model is shown to sum (a SUM measure reads it,
    /// or its summarize is set to Sum on purpose; see <see cref="IsProvedSummed"/>). Nothing else is filled: not a ratio, not a sum of a
    /// decimal or currency column, not a MIN or MAX (years, keys and codes live there), not a date, not a measure whose
    /// formula the engine cannot classify, not any measure while a calculation group would show it through its own
    /// arithmetic (an item with no format of its own, or one whose format is the measure's own), and not a measure that applies
    /// a calculation item itself, and never an object with a format string expression (a static format beside a dynamic one) or any text
    /// in a format property, even blank spaces (what a person typed is left alone). What it cannot fill stays advice, a
    /// choice for the person or their assistant. This is the one check: every door, the Plan's rows and Jev's format lines
    /// ask it, and none has a second rule of its own. It is asked more than once for the same write, on purpose: when Jev's
    /// line is worked out, again each time the Plan is worked out after a model change, and once more at the moment the line
    /// is written (LocalEngine.JevFormatWriteRefusal), against the model as it is then. Only a line a person ticked may write
    /// a format this check does not prove, and a re-sync carries only that tick, never the engine's own approval.
    /// Addendum 3 (29 Sep): a person's tick belongs to the object and what it computes (<see cref="ComputeDigest"/>), never to a
    /// name; a format line is proved against the model as it will be at the END of its batch; a table whose date column is the one side
    /// of a relationship is a date table whether or not anyone marked it; and a calculation item whose format expression is only a
    /// comment has no format of its own.
    /// The proof is structural. A formula is read as <see cref="DaxScan"/> tokens, so a word in a comment or a string
    /// never counts, and a column is a quantity by what the model says about it (its type, keys, relationships, sort,
    /// hierarchy, category and summarising), never by its name. A name may only VETO: it can take a column out, never
    /// put one in.</summary>
    public static class FormatFill
    {
        /// <summary>True only when writing <paramref name="format"/> over <paramref name="obj"/>'s format is a fill the
        /// rule allows: the object has no format yet (its static format is blank and it has no format string
        /// expression), holds no text in either (a format of blank spaces is the person's, and is left alone), the
        /// format is a whole-number format, the object is proved to hold a whole number, and (for a measure) no
        /// calculation group would show it through arithmetic of its own.</summary>
        public static bool MayFill(ITabularNamedObject obj, string format) =>
            IsWholeNumberFormat(format) && HasNoFormat(obj) && HoldsNoFormatText(obj)
            && (obj is not Measure m || !CalculationGroupReformats(m.Model))
            && ProvenWholeNumber(obj) != null;

        /// <summary>No text at all in the object's format properties: null or empty, not even blank spaces. A blank-only
        /// format counts as "no format" for advice (<see cref="HasNoFormat"/>) but is never written over.</summary>
        public static bool HoldsNoFormatText(ITabularNamedObject obj) => obj switch
        {
            Measure m => string.IsNullOrEmpty(m.FormatString) && string.IsNullOrEmpty(m.FormatStringExpression),
            Column c => string.IsNullOrEmpty(c.FormatString),
            _ => false,
        };

        /// <summary>A calculation item, a no-selection expression or a multiple-or-empty-selection expression with no
        /// format of its OWN shows its result in the format of the measure it is applied to, so a count's #,0 would become
        /// the format of the group's arithmetic (375 orders per 30 days read 12.5, then 13). While the model has any such
        /// item, no measure is filled. A format string expression is "its own" only when it does not hand the measure's
        /// format back (see <see cref="PassesMeasureFormatThrough"/>): <c>SELECTEDMEASUREFORMATSTRING ()</c>, alone or in one
        /// branch of an IF, is exactly the measure's own format, so it is no format of the item's at all.</summary>
        public static bool CalculationGroupReformats(Model model)
        {
            foreach (var group in (model?.Tables ?? Enumerable.Empty<Table>()).OfType<CalculationGroupTable>())
            {
                if (group.CalculationItems.Any(i => !HasOwnFormat(i.FormatStringExpression))) return true;
                if (!string.IsNullOrWhiteSpace(group.NoSelectionExpression) && !HasOwnFormat(group.NoSelectionFormatStringExpression)) return true;
                if (!string.IsNullOrWhiteSpace(group.MultipleOrEmptySelectionExpression) && !HasOwnFormat(group.MultipleOrEmptySelectionFormatStringExpression)) return true;
            }
            return false;
        }

        // Read as tokens, like every other formula here: an expression that is only a comment (a commented-out format is the
        // common real form) has no token left, so it is no expression at all and the item has no format of its own.
        private static bool HasOwnFormat(string formatStringExpression) =>
            !string.IsNullOrWhiteSpace(formatStringExpression) && DaxScan.Tokenize(formatStringExpression).Count > 0
            && !PassesMeasureFormatThrough(formatStringExpression);

        /// <summary>True when a format string expression can return the format of the measure it is applied to. Read as
        /// tokens, so the function's name in a string or a comment does not count and its spelling (case, spaces before the
        /// brackets) does not matter. Any use, even in one branch, counts: the other branches only decide for some measures.</summary>
        public static bool PassesMeasureFormatThrough(string formatStringExpression) =>
            !string.IsNullOrWhiteSpace(formatStringExpression)
            && DaxScan.Tokenize(formatStringExpression).Any(t => t.Kind == DaxScan.Kind.Word
                && string.Equals(t.Text, "SELECTEDMEASUREFORMATSTRING", StringComparison.OrdinalIgnoreCase));

        /// <summary>The whole-number formats a fill may write. Each shows a whole number as itself, with or without
        /// thousands separators, so none of them changes the value a person reads.</summary>
        public static bool IsWholeNumberFormat(string format) => format is "#,0" or "#,##0" or "0";

        /// <summary>Blank static format (empty, or only blank characters) and no format string expression.</summary>
        public static bool HasNoFormat(ITabularNamedObject obj) => obj switch
        {
            Measure m => string.IsNullOrWhiteSpace(m.FormatString) && string.IsNullOrWhiteSpace(m.FormatStringExpression),
            Column c => string.IsNullOrWhiteSpace(c.FormatString),
            _ => false,
        };

        /// <summary>What the engine can prove the object holds, in words ("a count", "a sum of a whole-number
        /// column", "a whole-number column"), or null when it cannot prove a whole number. It speaks of the object's own
        /// value; <see cref="MayFill"/> adds the format-side checks.</summary>
        public static string ProvenWholeNumber(ITabularNamedObject obj) => obj switch
        {
            Measure m => MeasureKind(m, new HashSet<Measure>()),
            Column c => IsFillableWholeNumberColumn(c) && IsProvedSummed(c) ? "a whole-number column" : null,
            _ => null,
        };

        /// <summary>A COLUMN FILL NEEDS PROOF THE COLUMN IS SUMMED (v-rules-v1i, section 9, decision 2; coordinator, M11 join).
        /// An Int64 identifier with a neutral name, left at the default summarize and on no relationship (a store, a branch, a route
        /// or a terminal number) has exactly the structure of a quantity, so the structural proof alone let Run the plan write #,##0
        /// on it by itself and store 1234 read 1,234. What tells a quantity apart is that something adds it up: a measure that is a
        /// SUM of it (read as tokens, anywhere in the model, so a word in a comment or a string never counts), or a summarize set to
        /// Sum, which is a choice somebody made (Default is only the engine's own default for a number). Without that proof a
        /// column's format is a suggestion the person ticks. A SUM measure's own format is unchanged: summing is what it does.</summary>
        public static bool IsProvedSummed(Column c)
        {
            if (c == null) return false;
            if (c.SummarizeBy == AggregateFunction.Sum) return true;
            var name = c.Name ?? "";
            foreach (var m in c.Model?.AllMeasures ?? Enumerable.Empty<Measure>())
            {
                var dax = m.Expression;
                // A cheap first look: a formula that does not hold the column's name and the word SUM cannot sum it.
                if (string.IsNullOrEmpty(dax) || dax.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0
                    || dax.IndexOf("SUM", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var toks = DaxScan.Tokenize(dax);
                for (var i = 0; i + 1 < toks.Count; i++)
                {
                    if (toks[i].Kind != DaxScan.Kind.Word || !string.Equals(toks[i].Text, "SUM", StringComparison.OrdinalIgnoreCase)
                        || toks[i + 1].Kind != DaxScan.Kind.LParen) continue;
                    var args = DaxScan.ArgRanges(toks, i + 1);
                    if (args.Count == 1 && ReferenceEquals(ColumnAt(m, toks, args[0].start, args[0].end), c)) return true;
                }
            }
            return false;
        }

        private static readonly HashSet<string> Counts = new(StringComparer.OrdinalIgnoreCase)
            { "COUNT", "COUNTA", "COUNTROWS", "DISTINCTCOUNT", "DISTINCTCOUNTNOBLANK" };

        private static string MeasureKind(Measure m, HashSet<Measure> seen)
        {
            if (m == null || !seen.Add(m) || seen.Count > 16) return null;
            var toks = DaxScan.Tokenize(m.Expression, out var endsInLineComment);
            // A formula that ends inside a line comment cannot be trusted to be the whole formula: a lost line break may
            // have swallowed a division (the TOM wrapper strips every carriage return it is given). Advice, never a fill.
            return endsInLineComment ? null : ExpressionKind(m, toks, 0, toks.Count, seen);
        }

        // [start, end) must be exactly one of: a count call; SUM of one whole-number column (MIN and MAX are never proved:
        // a year, a key or a code is what they most often read, and no structure of the column can tell); CALCULATE whose
        // first argument is itself proved (its filters do not change the kind of number); a reference to a measure that is
        // itself proved; or one of those in brackets. Anything else (an operator, a literal, DIVIDE, VAR) is not proved.
        private static string ExpressionKind(Measure home, IReadOnlyList<DaxScan.Tok> toks, int start, int end, HashSet<Measure> seen)
        {
            while (end - start >= 2 && toks[start].Kind == DaxScan.Kind.LParen && DaxScan.MatchingRParen(toks, start) == end - 1)
            { start++; end--; }
            if (end - start == 1 && toks[start].Kind == DaxScan.Kind.Name && toks[start].Delim == '[')
                return MeasureKind(home.Model.AllMeasures.FirstOrDefault(x => string.Equals(x.Name, toks[start].Inner, StringComparison.OrdinalIgnoreCase)), seen);
            var call = DaxScan.RootCallName(toks, start, end);
            if (call == null) return null;
            var args = DaxScan.ArgRanges(toks, start + 1);
            if (Counts.Contains(call)) return "a count";
            if (call == "SUM")
                return args.Count == 1 && ColumnAt(home, toks, args[0].start, args[0].end) is Column c && IsFillableWholeNumberColumn(c)
                    ? "a sum of a whole-number column" : null;
            // CALCULATE of a proved measure keeps its kind only while its filters do not apply a calculation item: a filter on a
            // calculation group's column turns the count into the item's arithmetic (a ratio, a percentage), and that shows in
            // the measure's own format (the item's format applies to the measure a query asks for, not to one a formula wraps).
            if (call == "CALCULATE" && args.Count >= 1)
                return AppliesCalculationItem(home.Model, toks, args.Skip(1)) ? null : ExpressionKind(home, toks, args[0].start, args[0].end, seen);
            return null;
        }

        // Any mention of a calculation group's table in a filter argument (its column in a comparison, KEEPFILTERS, FILTER,
        // ALL, TREATAS): read as tokens, so a table named in a string or a comment does not count. Refusing on a mention that
        // only removes the group's filter is the safe side: nothing is filled that the engine cannot show to be a count.
        private static bool AppliesCalculationItem(Model model, IReadOnlyList<DaxScan.Tok> toks, IEnumerable<(int start, int end)> filterArgs)
        {
            var groups = new HashSet<string>((model?.Tables ?? Enumerable.Empty<Table>()).OfType<CalculationGroupTable>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);
            if (groups.Count == 0) return false;
            foreach (var (start, end) in filterArgs)
                for (var i = start; i < end; i++)
                {
                    var t = toks[i];
                    var table = t.Kind == DaxScan.Kind.Word ? t.Text : t.Kind == DaxScan.Kind.Name && t.Delim == '\'' ? t.Inner : null;
                    if (table != null && groups.Contains(table)) return true;
                }
            return false;
        }

        // Table[Column], 'Table'[Column], or [Column] of the measure's own table.
        private static Column ColumnAt(Measure home, IReadOnlyList<DaxScan.Tok> toks, int start, int end)
        {
            Table table; int nameAt;
            if (end - start == 2 && (toks[start].Kind == DaxScan.Kind.Word || (toks[start].Kind == DaxScan.Kind.Name && toks[start].Delim == '\'')))
            {
                var tableName = toks[start].Kind == DaxScan.Kind.Word ? toks[start].Text : toks[start].Inner;
                table = home.Model.Tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
                nameAt = start + 1;
            }
            else if (end - start == 1) { table = home.Table; nameAt = start; }
            else return null;
            var tok = toks[nameAt];
            if (table == null || tok.Kind != DaxScan.Kind.Name || tok.Delim != '[') return null;
            return table.Columns.FirstOrDefault(c => string.Equals(c.Name, tok.Inner, StringComparison.OrdinalIgnoreCase));
        }

        // NAMES ONLY VETO. These words and endings in a column name take a column OUT of the fill (a year 2024 would read
        // 2,024, a date key 20240131 would read 20,240,131, a code or an ID would lose its shape). No name puts a column IN:
        // a column is a quantity only by what the model says about it (see IsFillableWholeNumberColumn). A name list can
        // never be complete, which is why the proof is structural first and this is a second, cheaper net.
        private static readonly HashSet<string> NotAQuantity = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "key", "sk", "fk", "pk", "code", "cd", "flag", "is", "has", "no", "num", "number", "nbr", "nr", "nummer",
            "date", "datekey", "year", "yr", "quarter", "qtr", "month", "week", "day", "period", "time", "hour", "minute",
            "fy", "fiscal", "financial", "calendar", "yyyy", "yyyymm", "yyyymmdd", "yearmonth", "jahr", "año",
            "sort", "index", "idx", "zip", "postcode", "postal", "phone", "tel", "version", "seq", "sequence", "rank", "level",
            "sku", "ean", "isbn", "upc", "abn", "tfn", "bsb", "ssn", "centre", "center",
        };

        // The same words run into the end of a name with no separator (ordernumber, FISCALYEAR, customerkey, EMPNUM).
        // Long enough that an ordinary word does not end in one by chance; the two-letter endings are the upper-case
        // pattern in IsFillableWholeNumberColumn.
        private static readonly string[] NotAQuantityEndings =
            { "number", "code", "flag", "year", "date", "key", "num", "yyyy", "yyyymm", "period", "postcode", "zipcode" };

        /// <summary>An Int64 column that is a quantity, by what the model says about it: a data column (a calculated
        /// column's type is from its last processing, so it is never proved), not a key, not unique, no data category, on
        /// neither side of any relationship, not what another column sorts by or groups by nor sorted or grouped by another, not a hierarchy level, not in the date table,
        /// and summarised by sum (Default is the model's own sum for a number), so nothing marks it as an identifier or a
        /// label. Then a name that marks a key, ID, date part or code still vetoes it.</summary>
        public static bool IsFillableWholeNumberColumn(Column c)
        {
            if (c is not DataColumn || c.DataType != DataType.Int64 || c.IsKey || c.IsUnique) return false;
            if (!string.IsNullOrWhiteSpace(c.DataCategory)) return false;
            if (c.SummarizeBy is not (AggregateFunction.Sum or AggregateFunction.Default)) return false;
            if (c.UsedInRelationships.Any() || c.UsedInSortBy.Any() || c.SortByColumn != null || c.UsedInHierarchies.Any()) return false;
            // A column of the table the model marks as its date table is a date part (a year, a month number, a date key), and
            // no name list knows every language's word for one. A column on either side of a group-by is the identity behind a
            // label (an account number grouped by its customer), never a quantity.
            if (string.Equals(c.Table?.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) return false;
            // The same by structure, for a calendar nobody marked: a table whose date column is the ONE side of a relationship
            // is what the facts are filtered by, so its whole-number columns are date parts (a year, a month number, a date key
            // in any language), never quantities.
            if (IsDateTableByStructure(c.Table)) return false;
            if (c.GroupByColumns != null && c.GroupByColumns.Any()) return false;
            if (c.Model.Tables.Any(t => t.Columns.Any(o => o.GroupByColumns != null && o.GroupByColumns.Any(g => ReferenceEquals(g, c))))) return false;
            var name = c.Name ?? "";
            // THE ENGINE'S OWN READINGS VETO (addendum 4 item 27). A column the engine itself reads as a place (a county code 6037 would
            // read 6,037) or as a non-additive dimension (a year, a pin, a display order, an age) is never a quantity for a fill. The same
            // two readings drive CAT-GEO and SUMMARIZE-DIMENSION, so the rule and the engine cannot disagree about one column.
            if (ReadinessRuleSet.GeoCategory(name) != null || ReadinessRuleSet.IsNonAdditiveDimensionName(name)) return false;
            if (name.IndexOf('#') >= 0) return false;                                  // Invoice #, Order #
            if (Regex.IsMatch(name, "(ID|KEY|SK|CD|NO|NR)[0-9]*$")) return false;      // EMPID, CUSTKEY, CustomerID2: no separator
            var words = NameWords(name).Select(w => w.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')).ToList();
            if (words.Any(NotAQuantity.Contains)) return false;
            var joined = string.Concat(words).ToLowerInvariant();
            return !NotAQuantityEndings.Any(e => joined.EndsWith(e, StringComparison.Ordinal)) && !joined.StartsWith("fy", StringComparison.Ordinal);
        }

        /// <summary>WHAT AN OBJECT COMPUTES, as a short digest, for a person's keep and for the stamp a format line carries (addendum 3 item
        /// 12, widened by addendum 4 item 22). A keep belongs to the object and what it computes, never to a name. What a measure computes
        /// is its formula read as tokens (so a comment or the spacing changes nothing), EVERY measure the formula reads (recursively, so a
        /// ratio that a measure reads and that later becomes money moves the digest of the measure that reads it), and the data type of
        /// EVERY column it reads (a sum of a column that turned into a decimal computes something else). A measure it reads enters by what
        /// it computes, not by its name, so renaming a measure it reads is not a change in what it computes. The object's lineage tag is
        /// added when it has one so two measures with the same formula are still two objects. It carries no name of the object itself,
        /// so a rename alone does not void a keep, and a different object arriving under the name does. A column adds its type, source
        /// and formula. Null when the object is not a measure or a column.</summary>
        public static string ComputeDigest(ITabularNamedObject obj) => Digest(obj, types: true);

        /// <summary>The same digest with every data type left out: the object's own and those of the columns it reads. Two digests
        /// that differ while these agree differ ONLY by a data type, which is how a keep set aside by a type change made in the same
        /// step is told apart from one set aside because the object became something else (v-rules-v1i, decision 4).</summary>
        public static string ComputeDigestWithoutTypes(ITabularNamedObject obj) => Digest(obj, types: false);

        /// <summary>The digest with every column the formula reads known by its LINEAGE TAG instead of its name (a column without a tag keeps its name). The same object computing the
        /// same thing has the same identity digest after a rename of a column or a table, and a different one when the formula or what it reads is really different. The start plan
        /// uses it to tell "the assistant renamed it" (a keep stands) from "it computes something else now" (the keep is void and the person is asked again).</summary>
        public static string ComputeIdentityDigest(ITabularNamedObject obj) => Digest(obj, types: true, byIdentity: true);

        private static string Digest(ITabularNamedObject obj, bool types, bool byIdentity = false)
        {
            string basis = obj switch
            {
                Measure m => "measure|" + Computes(m.Model, m.Table, m.Expression, new Walk { Path = { m }, Types = types, ById = byIdentity }, 0) + "|" + m.LineageTag,
                CalculatedColumn cc => "calc|" + (types ? cc.DataType.ToString() : "") + "|" + Computes(cc.Model, cc.Table, cc.Expression, new Walk { Types = types, ById = byIdentity }, 0) + "|" + cc.LineageTag,
                Column c => "column|" + c.GetType().Name + "|" + (types ? c.DataType.ToString() : "") + "|" + (c as DataColumn)?.SourceColumn + "|" + c.LineageTag,
                _ => null,
            };
            if (basis == null) return null;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(basis))).Substring(0, 16);
        }

        // The formula as tokens, with every name it reads replaced by what that name IS: a column by its table-qualified spelling and its
        // data type, a measure by what THAT measure computes (its own formula, read the same way). A cycle or a very deep chain stops
        // with a marker, so a digest always exists and always moves when anything on the path moves.
        //
        // A measure read more than once (the usual shape: a base measure that many others build on) is worked out ONCE per digest and
        // remembered, so a model whose measures share their bases costs one pass over each measure, never one pass per path to it.
        private sealed class Walk
        {
            public readonly HashSet<Measure> Path = new HashSet<Measure>();
            /// <summary>Whether a column read counts its data type (always, except for ComputeDigestWithoutTypes).</summary>
            public bool Types = true;
            /// <summary>Key a column read by its lineage tag (see <see cref="ComputeIdentityDigest"/>).</summary>
            public bool ById;
            public readonly Dictionary<Measure, string> Done = new Dictionary<Measure, string>();
            private ILookup<string, Measure> _byName;
            public ILookup<string, Measure> ByName(Model model) =>
                _byName ??= (model?.AllMeasures ?? Enumerable.Empty<Measure>()).ToLookup(x => x.Name ?? "", StringComparer.OrdinalIgnoreCase);
        }

        private static string Computes(Model model, Table home, string dax, Walk walk, int depth)
        {
            var toks = DaxScan.Tokenize(dax);
            var parts = new List<string>(toks.Count);
            for (var i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (t.Kind != DaxScan.Kind.Name || t.Delim != '[')
                {
                    // By identity, the table a column is read through is the table, not its name: 'Sales'[Amount] after Sales is renamed is the same formula.
                    if (walk.ById && i + 1 < toks.Count && toks[i + 1].Kind == DaxScan.Kind.Name && toks[i + 1].Delim == '['
                        && (t.Kind == DaxScan.Kind.Word || (t.Kind == DaxScan.Kind.Name && t.Delim == '\''))
                        && model?.Tables.FirstOrDefault(x => string.Equals(x.Name, t.Kind == DaxScan.Kind.Word ? t.Text : t.Inner, StringComparison.OrdinalIgnoreCase)) is { } named
                        && !string.IsNullOrEmpty(named.LineageTag))
                    { parts.Add("tbl:" + named.LineageTag); continue; }
                    parts.Add((int)t.Kind + ":" + (t.Text ?? "") + (t.Inner ?? "")); continue;
                }
                // Table[Column] or 'Table'[Column]: the previous token names the table.
                Table table = null; var qualified = false;
                if (i > 0 && (toks[i - 1].Kind == DaxScan.Kind.Word || (toks[i - 1].Kind == DaxScan.Kind.Name && toks[i - 1].Delim == '\'')))
                {
                    var tableName = toks[i - 1].Kind == DaxScan.Kind.Word ? toks[i - 1].Text : toks[i - 1].Inner;
                    table = model?.Tables.FirstOrDefault(x => string.Equals(x.Name, tableName, StringComparison.OrdinalIgnoreCase));
                    qualified = table != null;
                }
                var column = (qualified ? table : home)?.Columns.FirstOrDefault(c => string.Equals(c.Name, t.Inner, StringComparison.OrdinalIgnoreCase));
                if (column != null && (qualified || !walk.ByName(model)[t.Inner ?? ""].Any()))
                { parts.Add("col:" + (walk.ById && !string.IsNullOrEmpty(column.LineageTag) ? column.LineageTag : t.Inner) + (walk.Types ? ":" + column.DataType : "")); continue; }
                var read = qualified
                    ? table.Measures.FirstOrDefault(x => string.Equals(x.Name, t.Inner, StringComparison.OrdinalIgnoreCase))
                    : walk.ByName(model)[t.Inner ?? ""].FirstOrDefault();
                if (read == null) { parts.Add("unresolved:" + t.Inner); continue; }
                if (walk.Done.TryGetValue(read, out var known)) { parts.Add(known); continue; }
                if (!walk.Path.Add(read) || depth >= 64) { parts.Add("measure:loop"); continue; }
                string inner;
                try { inner = "measure{" + Computes(read.Model, read.Table, read.Expression, walk, depth + 1) + "}"; }
                finally { walk.Path.Remove(read); }
                walk.Done[read] = inner;
                parts.Add(inner);
            }
            return string.Join(" ", parts);
        }

        /// <summary>A table on the one side of ANY relationship that also holds a DateTime column is a date table, whether or not anyone
        /// marked it (addendum 4 item 26, which widens addendum 3 item 15). The usual SQL star relates its facts on an Int64 date key
        /// (20240131) and keeps the date itself as a column no relationship uses, and its year, month number and date key are date parts
        /// in any language. The cost: a customer dimension with a birth date is one too, so its whole-number attributes stay advice.</summary>
        public static bool IsDateTableByStructure(Table table) =>
            table != null && table.Columns.Any(d => d.DataType == DataType.DateTime)
            && table.Columns.Any(c => c.UsedInRelationships.OfType<SingleColumnRelationship>().Any(r =>
                (ReferenceEquals(r.FromColumn, c) && r.FromCardinality == RelationshipEndCardinality.One)
                || (ReferenceEquals(r.ToColumn, c) && r.ToCardinality == RelationshipEndCardinality.One)));

        private static IEnumerable<string> NameWords(string name) =>
            Regex.Split(Regex.Replace(name ?? "", "([a-z0-9])([A-Z])", "$1 $2"), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0);
    }
}
