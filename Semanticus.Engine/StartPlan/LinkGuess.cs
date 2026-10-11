// LINKS GUESSED FROM NAMES (M11 uat7-links; Kane, 3 Oct: "Guess links by name").
//
// A Fabric warehouse usually declares no foreign keys, so a start made from the declared keys found no fact table and built no measures (Sol's UAT, S1). When the
// source declares no key for a pair of tables, the manifest now guesses the link from the names, shows each guess in the review, and builds only the ones that are kept.
// It is a plain rule that a person can read in one go. No inference, no Jev: the same rule runs on Free and on Pro, and on Pro it is what stands when Jev has nothing to say
// (the relationship family is not live for Jev today: RelationshipFamily.JevDecides).
//
// THE RULE
//   1. Only for a pair of tables the source declares no key for, in either direction. A declared key always wins.
//   2. A column in table A points at table B when its name matches a key column of B:
//        same_name        CustomerKey and CustomerKey, with case and separators ignored (customer_key). The name must say it identifies something
//                         (ends in key, id, code, number, no, sk or guid) or be the name of B itself, so two tables that both have a Name column are not linked;
//        table_then_key   B's key is a plain Id or Key and A's column is B's name then that (CustomerId and Customers[Id]; plural and a leading dim or fact do not count);
//        ends_with        A's column ends with B's key name at a word boundary, for a role (OrderDateKey and Date[DateKey]). B's key name is four or more letters
//                         and says it identifies something.
//      DT and DTE, as a word of their own, are read as DATE in every name above (WORK_DT_KEY is a role of Date[DATE_KEY]); no other abbreviation is read.
//   3. The types match (Fabric maps every whole-number type to Int64, so int and bigint are the same), and are keys' types: whole number, text or date.
//   4. B's column is a key. The sample shows it unique on every row; or, when no row of B was read, it is B's declared primary key or is named like B's own key
//      (CustomerKey in Customer, Id in Customers). A sample that shows it repeating rules the link out, whatever the name says.
//   5. A's own key is never the many side, and a table is never linked to itself (Power BI cannot do it).
//   6. A column that two tables answer to keeps ONE link on, the one whose table the column is the own key name of (else the first), and the others are built switched off
//      with the reason. Two columns of A that point at one table (an order date and a ship date) are the existing role-playing rule: the first stays on. A link that would give a
//      table a second path is off, in the words the declared links use. Nothing is guessed between two tables in silence.
//
//   7. A source that declares its keys has said which tables are its dimensions (the ones its keys point at), so no role link is guessed from a dimension (ONBRD_DT_KEY on the
//      client table, HIRE_DT_KEY on the staff table) beside those keys. The facts' own date roles still are, and so is every link of a source that declares none.
//   8. From the names alone (no rows read, no key declared, nothing but dates to go on) nothing shows which table is a fact and which is a dimension. A role link is then on only
//      for a table named like a fact (fact_timesheet); any other is built off, with the reason, and comes on if the rows are read later and show the table is a fact.
//
// The links this makes are Guessed on the manifest. The review lists each as a line the person can drop (the plan then recomputes the table roles, the starter measures and
// the date table), and a dropped guess is not in the spec, so it is never built. What the review lists is what the build writes.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>What the sample says about the other end of a guessed link: for each table that was read, which columns were unique on every row. A table that is not here was not
    /// read, and its columns are "not measured", which is not the same as "repeating". Holds no row and no value.</summary>
    public sealed class LinkGuessFacts
    {
        private readonly Dictionary<string, Dictionary<string, bool>> _unique = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>No rows read: the rule goes by names alone and the review says so.</summary>
        public static LinkGuessFacts NamesOnly() => new LinkGuessFacts();

        /// <param name="schemaKey">The source table as "schema.name" (LocalEngine.SchemaKey).</param>
        /// <param name="uniqueByColumn">Source column name to whether the sample showed it unique on every row.</param>
        public LinkGuessFacts Measured(string schemaKey, IDictionary<string, bool> uniqueByColumn)
        {
            _unique[schemaKey ?? ""] = new Dictionary<string, bool>(uniqueByColumn ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            return this;
        }

        /// <summary>True when the sample measured at least one table. False for a plan made from names alone (no sample, or one that was refused).</summary>
        public bool AnyMeasured => _unique.Count > 0;

        /// <summary>True or false when the sample measured the column, null when it did not.</summary>
        public bool? IsUnique(string schemaKey, string column) =>
            _unique.TryGetValue(schemaKey ?? "", out var cols) && cols.TryGetValue(column ?? "", out var u) ? u : (bool?)null;

        /// <summary>The facts a sample gives. A table that could not be read, or has no rows, is left out (its columns stay "not measured"); the column facts are copied, never the sample.</summary>
        internal static LinkGuessFacts From(Facts.SourceSampleResult sample)
        {
            var facts = new LinkGuessFacts();
            if (sample == null || sample.Refused != null) return facts;
            foreach (var t in sample.Tables ?? Array.Empty<Facts.SourceTableFacts>())
            {
                if (t.NotMeasured != null || t.Rows == 0) continue;
                facts.Measured(LocalEngine.SchemaKey(t.Schema, t.Name), (t.Columns ?? Array.Empty<Facts.SourceColumnFacts>())
                    .GroupBy(c => c.Source ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().UniqueOnEveryRow, StringComparer.OrdinalIgnoreCase));
            }
            return facts;
        }
    }

    internal static class LinkGuesser
    {
        internal const string SameName = "same_name", TableThenKey = "table_then_key", EndsWith = "ends_with";
        internal const string Declared = "declared", Sample = "sample", Name = "name";

        /// <summary>One guess, in SOURCE names. <see cref="Also"/> is the other tables the same column matched; <see cref="Loser"/> is true when this one is switched off for that reason.</summary>
        internal sealed class Guess
        {
            public FabricTable From, To;
            public string FromColumn, ToColumn, Kind, Evidence;
            public bool Loser;
            public bool ToOwnKey;
            /// <summary>The match needed DT or DTE read as DATE.</summary>
            public bool Abbreviated;
            /// <summary>The sample shows the same column unique in both tables and nothing says which is the one side: the link is shown off, with that reason, and no link goes the other way.</summary>
            public bool Unclear;
            /// <summary>No rows were read and nothing else says which tables are the facts, so a role link from a table that is not named like a fact is shown off (plan statement check 5, P3).</summary>
            public bool Unproven;
            public Guess Winner;
            public List<Guess> Also = new List<Guess>();
        }

        internal static int KindRank(string kind) => kind == SameName ? 0 : kind == TableThenKey ? 1 : 2;
        private static int EvidenceRank(string e) => e == Declared ? 0 : e == Sample ? 1 : 2;
        private static bool KeyType(string dataType) => dataType == "Int64" || dataType == "String" || dataType == "DateTime";
        private static string Sk(FabricTable t) => LocalEngine.SchemaKey(t.Schema, t.Name);

        // ================================================================================================================
        // The guess
        // ================================================================================================================

        /// <param name="tables">The tables that are in the plan, with only the columns that are in it.</param>
        /// <param name="declaredPairs">Every pair of tables the source declares a key for, as <see cref="PairKey"/>.</param>
        /// <param name="declaredLinks">The same declared keys with their direction, by source key (the table that holds the key, the table it points at). A table a declared key points at is a dimension.</param>
        internal static List<Guess> GuessLinks(IReadOnlyList<FabricTable> tables, ISet<string> declaredPairs, LinkGuessFacts facts, IReadOnlyCollection<(string From, string To)> declaredLinks = null)
        {
            declaredLinks ??= Array.Empty<(string From, string To)>();
            facts ??= LinkGuessFacts.NamesOnly();
            var cmp = StringComparer.OrdinalIgnoreCase;
            var candidates = new List<Guess>();
            foreach (var a in tables)
            {
                foreach (var col in a.Columns ?? Array.Empty<FabricColumn>())
                {
                    if (!KeyType(col.DataType) || IsOwnKey(a, col.Name)) continue;
                    foreach (var b in tables)
                    {
                        if (cmp.Equals(Sk(a), Sk(b)) || declaredPairs.Contains(PairKey(Sk(a), Sk(b)))) continue;
                        Guess best = null;
                        foreach (var key in b.Columns ?? Array.Empty<FabricColumn>())
                        {
                            if (!cmp.Equals(key.DataType, col.DataType)) continue;
                            var evidence = EvidenceOf(b, key, facts);
                            if (evidence == null) continue;
                            var kind = MatchKind(col.Name, key.Name, b.Name, out var abbreviated);
                            if (kind == null) continue;
                            var g = new Guess { From = a, FromColumn = col.Name, To = b, ToColumn = key.Name, Kind = kind, Evidence = evidence, ToOwnKey = RelationshipFamily.OwnKeyName(key.Name, b.Name), Abbreviated = abbreviated };
                            if (best == null || (KindRank(g.Kind), EvidenceRank(g.Evidence)).CompareTo((KindRank(best.Kind), EvidenceRank(best.Evidence))) < 0) best = g;
                        }
                        if (best != null) candidates.Add(best);
                    }
                }
            }
            candidates = ResolveDirections(candidates);

            var result = new List<Guess>();
            foreach (var column in candidates.GroupBy(g => (g.From, g.FromColumn)))
            {
                var found = column.ToList();
                // One column, several tables: the closest name first, then the table the column is the own key name of, then the order the source lists them. The rest are off.
                var ordered = found.OrderBy(g => KindRank(g.Kind)).ThenBy(g => g.ToOwnKey ? 0 : 1).ThenBy(g => IndexOf(tables, g.To)).ToList();
                var winner = ordered[0];
                foreach (var g in ordered.Skip(1)) { g.Loser = true; g.Winner = winner; winner.Also.Add(g); }
                result.AddRange(ordered);
            }
            // A date role that only the abbreviation found, on a table other tables point at (a hire date on the staff table), is left out: the facts reach the date table
            // already, and the dimension's own date would be a second path, built switched off and counted against the model as an inactive link. The facts' own date roles
            // stay. A table nothing points at (no dimension for it to be) keeps its date link. So does a table that others point at when its date is the ONLY route: it has no other
            // date, and no table that points at it reaches the date table itself (a header fact that a line fact points at, where the line has no date of its own).
            // A source that declares its keys has already said which tables are the dimensions (the ones its keys point at), so the same holds there for every role link, however the
            // names matched: ONBRD_DT_KEY on the client table is not guessed beside a source's own keys (plan statement check 5, N2: four of them doubled the Pro build's warnings).
            var declaresKeys = declaredPairs.Count > 0;
            var byKey = tables.ToDictionary(Sk, t => t, StringComparer.OrdinalIgnoreCase);
            var pointedAt = new HashSet<FabricTable>(result.Where(g => !g.Abbreviated && !g.Loser && !g.Unclear).Select(g => g.To));
            foreach (var d in declaredLinks) if (byKey.TryGetValue(d.To, out var dimension)) pointedAt.Add(dimension);
            var standingDates = result.Where(g => !g.Loser && !g.Unclear && IsDateLink(g)).ToList();
            bool ReachesDate(FabricTable t) => standingDates.Any(d => ReferenceEquals(d.From, t)) || DeclaresDateLink(t, tables, declaredPairs);
            bool PointedAtByADatedTable(FabricTable t) =>
                result.Where(x => !x.Loser && !x.Unclear && ReferenceEquals(x.To, t)).Any(x => ReachesDate(x.From))
                || declaredLinks.Where(d => string.Equals(d.To, Sk(t), StringComparison.OrdinalIgnoreCase)).Any(d => byKey.TryGetValue(d.From, out var from) && ReachesDate(from));
            bool OnlyRoute(Guess g) =>
                standingDates.Count(d => ReferenceEquals(d.From, g.From)) == 1 && !DeclaresDateLink(g.From, tables, declaredPairs) && !PointedAtByADatedTable(g.From);
            result.RemoveAll(g => (g.Abbreviated || (declaresKeys && g.Kind == EndsWith)) && pointedAt.Contains(g.From) && !OnlyRoute(g));
            // From the names alone, with no key declared and no link but dates to go on, nothing shows which table is a fact and which is a dimension: a hire date on the staff table
            // reads the same as a work date on the timesheet. A role link is then on only for a table that is NAMED like a fact (fact_timesheet); the rest are built off, with the reason,
            // and come on by themselves if the rows are read later and show the table is a fact (Reconsider). A wrong link is worse than a missing one.
            var nothingShowsFacts = !facts.AnyMeasured && !declaresKeys && !result.Any(g => !g.Loser && !g.Unclear && !IsDateLink(g));
            if (nothingShowsFacts)
                foreach (var g in result.Where(g => g.Kind == EndsWith && !g.Loser && !g.Unclear && !NamedLikeAFact(g.From.Name))) g.Unproven = true;
            return result;
        }

        /// <summary>A table whose name says it holds the facts, by the fact naming convention only: a "fact" word (any case) followed by an underscore, a space or a capital letter (fact_invoice, Fact Sales,
        /// FactInternetSales). Not a word that merely starts with it: Factory, Factor, Factual, Facts. In a name with no lower-case letter a capital is just the next letter (FACTORY), so only a separator counts there.</summary>
        internal static bool NamedLikeAFact(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length <= 4 || !n.StartsWith("fact", StringComparison.OrdinalIgnoreCase)) return false;
            var next = n[4];
            return next == '_' || next == ' ' || (char.IsUpper(next) && n.Any(char.IsLower));
        }

        /// <summary>A link to a date table, by the names: the key it ends at says date (DATE_KEY, with DT and DTE read as DATE), or the table does (dim_date, Calendar).</summary>
        private static bool IsDateLink(Guess g) => SaysDate(g.ToColumn) || SaysDate(g.To.Name);

        private static bool SaysDate(string name)
        {
            var read = ReadAbbreviations(name) ?? "";
            return read.IndexOf("date", StringComparison.OrdinalIgnoreCase) >= 0 || read.IndexOf("calendar", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The source declares a key from this table to a table that says date.</summary>
        private static bool DeclaresDateLink(FabricTable t, IReadOnlyList<FabricTable> tables, ISet<string> declaredPairs) =>
            tables.Any(other => !ReferenceEquals(other, t) && SaysDate(other.Name) && declaredPairs.Contains(PairKey(Sk(t), Sk(other))));

        /// <summary>A column that is unique on both sides is guessed in both directions, and a young fact table whose foreign keys happen to be unique in a small sample is then pointed at by its own
        /// dimensions and stops being a fact (Sol, join 8, K13). One link per such pair: the side that is the key's own table (its name is the key's name), else the side that is not the table with
        /// the amounts, else neither is certain and the first is shown off with that reason. And a column that is itself the foreign key of a link that stands is not the key end of another.</summary>
        private static List<Guess> ResolveDirections(List<Guess> candidates)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var removed = new HashSet<Guess>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var g = candidates[i];
                if (removed.Contains(g)) continue;
                var r = candidates.FirstOrDefault(x => !ReferenceEquals(x, g) && !removed.Contains(x) && ReferenceEquals(x.From, g.To) && ReferenceEquals(x.To, g.From)
                                                       && cmp.Equals(x.FromColumn, g.ToColumn) && cmp.Equals(x.ToColumn, g.FromColumn));
                if (r == null) continue;
                // g is a.col to b.key and r is b.key to a.col: the same column pair, unique both ways.
                if (g.ToOwnKey != r.ToOwnKey) { removed.Add(g.ToOwnKey ? r : g); continue; }
                int gm = Amounts(g.From), rm = Amounts(r.From);
                if (gm != rm) { removed.Add(gm > rm ? r : g); continue; }
                g.Unclear = true;
                removed.Add(r);
            }
            var standing = candidates.Where(g => !removed.Contains(g)).ToList();
            // A column that is the foreign key of a link that stands does not also serve as the key end of another (an invoice's client id is not "the client" of the project that lists it).
            var sources = new HashSet<(FabricTable, string)>(standing.Where(g => !g.Unclear).Select(g => (g.From, g.FromColumn.ToLowerInvariant())));
            // Only when the table pointed at carries amounts, as many as the table that points or more: a dimension's extension (an address table keyed by the customer key) may still be a
            // candidate for a column two tables answer to, but a table with the amounts is the many side, never the one.
            return standing.Where(g => !(sources.Contains((g.To, g.ToColumn.ToLowerInvariant())) && Amounts(g.To) > 0 && Amounts(g.To) >= Amounts(g.From))).ToList();
        }

        /// <summary>How many amount-like columns a table has: a fact table carries them, a dimension rarely does.</summary>
        private static int Amounts(FabricTable t) => (t.Columns ?? Array.Empty<FabricColumn>()).Count(c => c.DataType == "Decimal" || c.DataType == "Double");

        private static int IndexOf(IReadOnlyList<FabricTable> tables, FabricTable t)
        {
            for (var i = 0; i < tables.Count; i++) if (ReferenceEquals(tables[i], t)) return i;
            return int.MaxValue;
        }

        /// <summary>The unordered pair of tables, so a key declared in either direction counts.</summary>
        internal static string PairKey(string a, string b) =>
            string.CompareOrdinal(a?.ToLowerInvariant(), b?.ToLowerInvariant()) <= 0 ? a?.ToLowerInvariant() + "\u001f" + b?.ToLowerInvariant() : b?.ToLowerInvariant() + "\u001f" + a?.ToLowerInvariant();

        /// <summary>A table's own key as a source of a link: the declared single-column key, or a column named for its own table. It is the one side, never the many.</summary>
        private static bool IsOwnKey(FabricTable t, string column) =>
            (t.KeyColumns?.Length == 1 && string.Equals(t.KeyColumns[0], column, StringComparison.OrdinalIgnoreCase)) || RelationshipFamily.OwnKeyName(column, t.Name);

        /// <summary>Whether a column of B can be the key end, and how we know. Null: it cannot. A sample that measured it repeating (or blank) rules it out, whatever its name or its
        /// declaration says; one that measured it unique makes it a key; with no measure, only a declared single-column key or a name like B's own key does.</summary>
        private static string EvidenceOf(FabricTable b, FabricColumn key, LinkGuessFacts facts)
        {
            if (!KeyType(key.DataType)) return null;
            var unique = facts.IsUnique(Sk(b), key.Name);
            if (unique == false) return null;
            if (unique == true) return Sample;
            if (b.KeyColumns?.Length == 1 && string.Equals(b.KeyColumns[0], key.Name, StringComparison.OrdinalIgnoreCase)) return Declared;
            return RelationshipFamily.OwnKeyName(key.Name, b.Name) ? Name : null;
        }

        /// <summary>The names as the rule reads them: DT and DTE, as a word of their own, read as DATE (WORK_DT_KEY, WorkDtKey, INV_DTE_KEY), because a warehouse that shortens its
        /// names does it for the date key too, and then a role date (WORK_DT_KEY) cannot match Date[DATE_KEY] by the letters. A word of its own means the name's separators and
        /// capital letters mark it off; DT inside a longer word (WORKDT, EDT) is left alone. Only these two are read, because only these two are the date's shortening in every
        /// warehouse that shortens it; every other abbreviation is a guess about one source, and a wrong link is worse than a missing one.</summary>
        internal static string ReadAbbreviations(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var words = new List<string>();
            var word = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c)) { if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); } continue; }
                if (word.Length > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) { words.Add(word.ToString()); word.Clear(); }
                word.Append(c);
            }
            if (word.Length > 0) words.Add(word.ToString());
            if (!words.Any(w => w.Equals("dt", StringComparison.OrdinalIgnoreCase) || w.Equals("dte", StringComparison.OrdinalIgnoreCase))) return name;
            return string.Join("_", words.Select(w => w.Equals("dt", StringComparison.OrdinalIgnoreCase) || w.Equals("dte", StringComparison.OrdinalIgnoreCase) ? "DATE" : w));
        }

        internal static string MatchKind(string column, string keyName, string keyTable) => MatchKind(column, keyName, keyTable, out _);

        /// <param name="abbreviated">True when the match needed DT or DTE read as DATE (see <see cref="ReadAbbreviations"/>).</param>
        internal static string MatchKind(string column, string keyName, string keyTable, out bool abbreviated)
        {
            var read = ReadAbbreviations(column);
            var readKey = ReadAbbreviations(keyName);
            var plain = MatchKindOf(column, keyName, keyTable);
            var kind = MatchKindOf(read, readKey, keyTable);
            abbreviated = kind != null && plain == null;
            return kind;
        }

        private static string MatchKindOf(string column, string keyName, string keyTable)
        {
            var nA = RelationshipFamily.Norm(column);
            var nB = RelationshipFamily.Norm(keyName);
            if (nA.Length == 0 || nB.Length == 0) return null;
            var tableNames = RelationshipFamily.TableNames(keyTable).ToList();
            if (nA == nB)
                return RelationshipFamily.IdentifierLike(column) || tableNames.Any(t => nA == t || nA == RelationshipFamily.Singular(t)) ? SameName : null;
            if ((nB == "id" || nB == "key") && tableNames.Any(t => nA == t + nB || nA == RelationshipFamily.Singular(t) + nB)) return TableThenKey;
            if (nB.Length >= 4 && RelationshipFamily.IdentifierLike(keyName) && nA.Length > nB.Length && nA.EndsWith(nB, StringComparison.Ordinal) && WordBoundaryBefore(column, nB.Length))
                return EndsWith;
            return null;
        }

        /// <summary>True when the last <paramref name="length"/> letters and digits of the name start a word: after a separator, or at a capital that follows a small letter.</summary>
        private static bool WordBoundaryBefore(string name, int length)
        {
            var counted = 0;
            for (var i = name.Length - 1; i >= 0; i--)
            {
                if (!char.IsLetterOrDigit(name[i])) continue;
                if (++counted < length) continue;
                if (i == 0) return false;
                var prev = name[i - 1];
                return !char.IsLetterOrDigit(prev) || (char.IsUpper(name[i]) && !char.IsUpper(prev));
            }
            return false;
        }

        // ================================================================================================================
        // The words
        // ================================================================================================================

        /// <summary>The line the review shows: "Linked Sales[CustomerKey] to Customer[CustomerKey]: same name, and CustomerKey is unique in Customer (from the sample)".</summary>
        internal static string Words(ManifestLink l)
        {
            var from = l.FromColumns.FirstOrDefault() ?? ""; var to = l.ToColumns.FirstOrDefault() ?? "";
            var how = l.GuessKind switch
            {
                SameName => "same name",
                TableThenKey => $"{from} is the name of {l.ToTable} followed by {to}",
                EndsWith => $"{from} ends with {to}",
                _ => "the names match",
            };
            var key = l.GuessEvidence switch
            {
                Sample => $"{to} is unique in {l.ToTable} (from the sample)",
                Declared => $"{to} is the declared key of {l.ToTable}",
                _ => $"{to} is named like the key of {l.ToTable} (rows not read)",
            };
            return $"Linked {l.FromTable}[{from}] to {l.ToTable}[{to}]: {how}, and {key}";
        }

        /// <summary>The reason shown under a guessed link: it is a guess, why it is off when it is, and what else the column matched when it is the one kept on.</summary>
        internal static string Why(ManifestLink l)
        {
            if (l.Dropped) return "A guessed link, left out by " + (l.DroppedBy == "assistant" ? "your assistant" : "you") + ". It is not built. Keep it to bring it back.";
            var guess = "A guess from the names: the source declares no key for these two tables. Drop it if it is wrong.";
            if (!l.Active && !string.IsNullOrWhiteSpace(l.Note)) return guess + " " + l.Note;
            if (l.GuessAlso != null && l.GuessAlso.Length > 0)
                return guess + $" {l.FromColumns.FirstOrDefault()} also matches {Plain(l.GuessAlso)}, which {(l.GuessAlso.Length == 1 ? "is" : "are")} off.";
            return guess;
        }

        private static string Plain(string[] names) => LocalEngine.JoinPlain(names);

        private static string LoserNote(ManifestLink l, string winnerTable, string winnerColumn, bool ownKey) =>
            $"Off because {l.FromTable}[{l.FromColumns[0]}] matches both {winnerTable} and {l.ToTable}, and the link to {winnerTable} was kept on instead ("
            + (ownKey ? $"{winnerColumn} is named like the key of {winnerTable}" : $"{winnerTable} is listed first") + "). Turn this one on instead if it is the right one. A measure can still use it with USERELATIONSHIP.";

        // ================================================================================================================
        // Making the links, placing them, and reconsidering them when the sample lands
        // ================================================================================================================

        internal static string LinkIdentityKey(Guess g) =>
            LocalEngine.SchemaKey(g.From.Schema, g.From.Name) + "\u001f" + g.FromColumn + "\u001f" + LocalEngine.SchemaKey(g.To.Schema, g.To.Name) + "\u001f" + g.ToColumn;

        /// <summary>The manifest links for guesses, one each, in the guesses' order. The loser of an ambiguous column is already off, with its reason.</summary>
        internal static List<ManifestLink> MakeLinks(IReadOnlyList<Guess> guesses, string seed, Func<FabricTable, string> tableName, Func<FabricTable, string, string> columnName)
        {
            var links = new List<ManifestLink>();
            var byGuess = new Dictionary<Guess, ManifestLink>();
            foreach (var g in guesses)
            {
                var link = new ManifestLink
                {
                    Identity = StartIdentity.Of(seed, "link", LinkIdentityKey(g)),
                    FromTable = tableName(g.From), FromColumns = new[] { columnName(g.From, g.FromColumn) },
                    ToTable = tableName(g.To), ToColumns = new[] { columnName(g.To, g.ToColumn) },
                    Active = !g.Loser && !g.Unclear && !g.Unproven, Guessed = true, GuessKind = g.Kind, GuessEvidence = g.Evidence,
                };
                byGuess[g] = link;
                links.Add(link);
            }
            foreach (var g in guesses)
            {
                var link = byGuess[g];
                link.GuessAlso = g.Also.Select(a => tableName(a.To)).ToArray();
                if (g.Loser) link.Note = LoserNote(link, tableName(g.Winner.To), columnName(g.Winner.To, g.Winner.ToColumn), g.Winner.ToOwnKey);
                else if (g.Unproven) link.Note = UnprovenNote(link);
                else if (g.Unclear) link.Note = $"Off because {link.FromColumns[0]} is unique in {link.FromTable} and in {link.ToTable} in the sample, so it cannot tell which one is the one side. Turn this one on if {link.ToTable} is the one side.";
            }
            return links;
        }

        /// <summary>The reason a role link guessed from the names alone is off (<see cref="Guess.Unproven"/>).</summary>
        internal static string UnprovenNote(ManifestLink l) =>
            $"Off because no rows were read, so nothing shows that {l.FromTable} is a fact table and not a dimension. Turn this one on if {l.FromTable} is a fact table.";

        /// <summary>True for the note a link carries when it was switched off for want of rows (<see cref="UnprovenNote"/>).</summary>
        private static bool IsUnprovenNote(string note) => note != null && note.StartsWith("Off because no rows were read", StringComparison.Ordinal);

        /// <summary>The spec shape of a link, for the second-path test and the date finder. A link that is off carries IsActive false.</summary>
        internal static SpecRelationship Rel(ManifestLink l) => new SpecRelationship
        {
            FromTable = l.FromTable, FromColumn = l.FromColumns[0], ToTable = l.ToTable, ToColumn = l.ToColumns[0],
            IsActive = l.Active ? (bool?)null : false, Note = l.Note,
        };

        /// <summary>Place new guessed links among the links already on: the declared rule's own two notes, in its own words. A link that already has an active one between the same two
        /// tables is a twin (an order date and a ship date) and the first stays on; one that would give a table a second path is off. The fact tables' own links to dimensions go first, then a fact's link to another fact (the one that goes off when both reach a dimension), then the
        /// order the guesses came in, which is the order of the table's columns. Mutates <paramref name="toPlace"/>.</summary>
        internal static void PlaceLinks(IEnumerable<SpecRelationship> alreadyOn, IReadOnlyList<SpecRelationship> toPlace, SpecTable[] tables, IReadOnlyList<int> closeness = null)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var facts = new HashSet<string>(tables.Where(t => t.Role == "fact").Select(t => t.Name), cmp);
            var on = new LocalEngine.ActiveLinks(cmp);
            foreach (var r in alreadyOn.Where(r => r.IsActive != false && !cmp.Equals(r.FromTable, r.ToTable))) on.Add(r);
            foreach (var r in toPlace.Select((r, i) => (r, i)).Where(x => x.r.IsActive != false && !cmp.Equals(x.r.FromTable, x.r.ToTable))
                         .OrderBy(x => facts.Contains(x.r.FromTable) ? (facts.Contains(x.r.ToTable) ? 1 : 0) : 2)   // a fact's links to dimensions first, a fact's link to another fact next (K23): of two paths, the link between the facts is the one off
                         .ThenBy(x => closeness != null && x.i < closeness.Count ? closeness[x.i] : 0)   // the closest name goes first: of two columns that answer to one table, the exact key is the link kept on
                         .ThenBy(x => x.i).Select(x => x.r))
            {
                var twin = on.Between(r.FromTable, r.ToTable);
                if (twin != null) { LocalEngine.MarkTwinOff(r, twin); continue; }
                var via = on.SecondPathVia(r.FromTable, r.ToTable);
                if (via == null) { on.Add(r); continue; }
                LocalEngine.MarkSecondPathOff(r, via.Value, facts);
            }
        }

        /// <summary>The declared keys with their direction, by source key, among the tables that are in: the table that holds the key, then the table it points at. A table's key to itself is left out.</summary>
        internal static List<(string From, string To)> DeclaredLinks(IEnumerable<FabricForeignKeyGroup> groups, ISet<string> includedKeys)
        {
            var links = new List<(string From, string To)>();
            foreach (var g in groups ?? Array.Empty<FabricForeignKeyGroup>())
            {
                var from = LocalEngine.SchemaKey(g.FromSchema, g.FromTable); var to = LocalEngine.SchemaKey(g.ToSchema, g.ToTable);
                if (includedKeys.Contains(from) && includedKeys.Contains(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) links.Add((from, to));
            }
            return links;
        }

        /// <summary>The unordered pairs of tables with a declared key, by source key, among the tables that are in.</summary>
        internal static HashSet<string> DeclaredPairs(IEnumerable<FabricForeignKeyGroup> groups, ISet<string> includedKeys)
        {
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups ?? Array.Empty<FabricForeignKeyGroup>())
            {
                var from = LocalEngine.SchemaKey(g.FromSchema, g.FromTable); var to = LocalEngine.SchemaKey(g.ToSchema, g.ToTable);
                if (includedKeys.Contains(from) && includedKeys.Contains(to)) pairs.Add(PairKey(from, to));
            }
            return pairs;
        }

        /// <summary>THE SAMPLE LANDED (or changed) after the plan was made: guess again from what the rows show, over the plan as it stands now. Declared links are never touched. A guessed
        /// link that is still guessed keeps what the person decided (a drop stays a drop); one the rows now rule out goes; one they now support is added; and the roles, the starter
        /// measures and the date table follow. Returns whether the plan changed.</summary>
        internal static bool Reconsider(StartPlanDoc doc, LinkGuessFacts facts)
        {
            var m = doc?.Manifest;
            if (m == null || doc.Schema == null) return false;
            var cmp = StringComparer.OrdinalIgnoreCase;
            // Every table the plan knows, in or out: a table left out is a choice the person can take back, and a guess that went with it must still be there when it returns
            // (its link then reads "not built: a table it needs is left out", as a declared one does).
            var rows = (m.Tables ?? new List<ManifestTable>()).ToList();
            var sourceTable = (doc.Schema.Tables ?? Array.Empty<FabricTable>()).ToDictionary(t => LocalEngine.SchemaKey(t.Schema, t.Name), t => t, cmp);
            var viewOf = new Dictionary<FabricTable, ManifestTable>();
            var tables = new List<FabricTable>();
            foreach (var r in rows)
            {
                sourceTable.TryGetValue(LocalEngine.SchemaKey(r.SourceSchema, r.SourceName), out var src);
                var view = new FabricTable
                {
                    Schema = r.SourceSchema, Name = r.SourceName, KeyColumns = src?.KeyColumns ?? Array.Empty<string>(),
                    Columns = r.Columns.Select(c => new FabricColumn { Name = c.SourceColumn, DataType = c.DataType }).ToArray(),
                };
                tables.Add(view); viewOf[view] = r;
            }
            var includedKeys = new HashSet<string>(tables.Select(Sk), cmp);
            var declared = DeclaredPairs(doc.Schema.ForeignKeyGroups(), includedKeys);
            var declaredLinks = DeclaredLinks(doc.Schema.ForeignKeyGroups(), includedKeys);
            var tableNameBySource = rows.ToDictionary(r => LocalEngine.SchemaKey(r.SourceSchema, r.SourceName), r => r.Name, cmp);
            string TableName(FabricTable t) => tableNameBySource[Sk(t)];
            string ColumnName(FabricTable t, string source) => viewOf[t].Columns.FirstOrDefault(c => cmp.Equals(c.SourceColumn, source))?.Name ?? source;
            // A declared link (one that is not a guess) between two tables keeps the pair out too, by its model names.
            foreach (var l in m.Links.Where(l => !l.Guessed))
            {
                var a = rows.FirstOrDefault(r => cmp.Equals(r.Name, l.FromTable)); var b = rows.FirstOrDefault(r => cmp.Equals(r.Name, l.ToTable));
                if (a != null && b != null)
                {
                    declared.Add(PairKey(LocalEngine.SchemaKey(a.SourceSchema, a.SourceName), LocalEngine.SchemaKey(b.SourceSchema, b.SourceName)));
                    declaredLinks.Add((LocalEngine.SchemaKey(a.SourceSchema, a.SourceName), LocalEngine.SchemaKey(b.SourceSchema, b.SourceName)));
                }
            }

            var guesses = GuessLinks(tables, declared, facts, declaredLinks);
            var wanted = MakeLinks(guesses, m.IdentitySeed ?? doc.IdentitySeed, TableName, ColumnName);
            var before = Snapshot(m);

            // Merge by the link's two ends. What is kept keeps its decisions; what the rows no longer support goes.
            string EndsOf(ManifestLink l) => l.FromTable + "\u001f" + l.FromColumns[0] + "\u001f" + l.ToTable + "\u001f" + l.ToColumns[0];
            var existing = m.Links.Where(l => l.Guessed).ToDictionary(EndsOf, l => l, cmp);
            var keep = new HashSet<ManifestLink>();
            var added = new List<ManifestLink>();
            var revived = new List<ManifestLink>();
            foreach (var w in wanted)
            {
                if (existing.TryGetValue(EndsOf(w), out var have))
                {
                    have.GuessKind = w.GuessKind; have.GuessEvidence = w.GuessEvidence; have.GuessAlso = w.GuessAlso;
                    keep.Add(have);
                    // The rows changed which table wins a column: a link we switched off for a winner that is gone comes on (placed again among the links that stand, below), and a
                    // link that is on but now loses is switched off, each with its reason. A link the person switched on or off by hand has no such note and is left as it is.
                    if (!have.Active && !have.Dropped && (IsLoserNote(have.Note) || IsUnprovenNote(have.Note)) && w.Active) revived.Add(have);
                    else if (have.Active && !have.Dropped && !w.Active && w.Note != null && !IsUnprovenNote(w.Note)) { have.Active = false; have.Note = w.Note; }   // never over a link the person turned on for want of rows
                    else if (!have.Active && (IsLoserNote(have.Note) || IsUnprovenNote(have.Note)) && !w.Active && w.Note != null) have.Note = w.Note;
                }
                else added.Add(w);
            }
            m.Links.RemoveAll(l => l.Guessed && !keep.Contains(l));

            // Place the new ones among the links that are on (the declared ones and the guesses that stayed). They are in the list first, so the roles the placing reads
            // already count them.
            var on = m.Links.Where(l => !l.Unresolved && !l.Dropped && l.Active && BothEndsIn(m, l)).Select(Rel).ToList();
            m.Links.AddRange(added);
            foreach (var l in revived) { l.Active = true; l.Note = null; }
            m.RefreshFromLinks();
            var placing = added.Concat(revived).Select(l => (Link: l, Rel: Rel(l))).ToList();
            PlaceLinks(on, placing.Select(p => p.Rel).ToList(), SpecTablesOf(m), placing.Select(p => KindRank(p.Link.GuessKind)).ToList());
            foreach (var (link, rel) in placing) { link.Active = rel.IsActive != false; link.Note = rel.Note ?? link.Note; }
            m.RefreshFromLinks();
            return Snapshot(m) != before;
        }

        /// <summary>True for the note a link carries when this rule switched it off because another table won its column (<see cref="LoserNote"/>).</summary>
        private static bool IsLoserNote(string note) => note != null && note.StartsWith("Off because ", StringComparison.Ordinal) && note.Contains(" matches both ", StringComparison.Ordinal);

        private static string Snapshot(StructureManifest m) => System.Text.Json.JsonSerializer.Serialize(new { m.Links, m.Tables.Count, m.BaseMeasures, m.DateTable, Roles = m.Tables.Select(t => t.Role) });

        internal static bool BothEndsIn(StructureManifest m, ManifestLink l)
        {
            bool EndIn(string table, string[] cols)
            {
                var t = m.Tables.FirstOrDefault(x => x.Include && StringComparer.OrdinalIgnoreCase.Equals(x.Name, table));
                return t != null && cols.All(c => t.Columns.Any(x => x.Include && StringComparer.OrdinalIgnoreCase.Equals(x.Name, c)));
            }
            return EndIn(l.FromTable, l.FromColumns) && EndIn(l.ToTable, l.ToColumns);
        }

        /// <summary>The manifest's included tables as the draft's own shapes, for the date finder and the link rule.</summary>
        internal static SpecTable[] SpecTablesOf(StructureManifest m) => m.Tables.Where(t => t.Include).Select(t => new SpecTable
        {
            Name = t.Name, Role = t.Role, Entity = t.SourceName, Schema = t.SourceSchema,
            Columns = t.Columns.Where(c => c.Include).Select(c => new SpecColumn
            {
                Name = c.Name, DataType = c.DataType, SourceColumn = c.SourceColumn, IsKey = c.IsKey, Hidden = c.Hidden, SummarizeBy = c.SummarizeBy,
            }).ToArray(),
        }).ToArray();

        // ================================================================================================================
        // A person's edit of one link
        // ================================================================================================================

        /// <summary>The link a drop, a keep or a switch is about: by its ref ("relationship:" and its identity), its identity, or "Sales.CustomerKey -> Customer.CustomerKey".</summary>
        internal static ManifestLink FindLink(StartPlanDoc doc, string target)
        {
            var t = (target ?? "").Trim();
            if (t.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase)) t = t.Substring("relationship:".Length);
            if (t.Length == 0) return null;
            return doc.Manifest.Links.FirstOrDefault(l => !string.IsNullOrEmpty(l.Identity) && string.Equals(l.Identity, t, StringComparison.OrdinalIgnoreCase))
                   ?? doc.Manifest.Links.FirstOrDefault(l => string.Equals(StartPlanEditing.LinkText(l), t, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A guessed link that comes back after a drop: if it is meant to be on and an active link now stands between the same two tables, or would give a table a second path,
        /// it comes back off, in the declared rule's words.</summary>
        internal static void PlaceAgain(StructureManifest m, ManifestLink link)
        {
            if (!link.Active) return;
            var cmp = StringComparer.OrdinalIgnoreCase;
            var rel = Rel(link);
            var on = new LocalEngine.ActiveLinks(cmp);
            foreach (var other in m.Links.Where(o => !ReferenceEquals(o, link) && !o.Unresolved && !o.Dropped && o.Active && BothEndsIn(m, o) && !cmp.Equals(o.FromTable, o.ToTable)))
                on.Add(Rel(other));
            if (cmp.Equals(rel.FromTable, rel.ToTable)) return;
            var facts = new HashSet<string>(m.Tables.Where(t => t.Role == "fact").Select(t => t.Name), cmp);
            var twin = on.Between(rel.FromTable, rel.ToTable);
            if (twin != null) LocalEngine.MarkTwinOff(rel, twin);
            else if (on.SecondPathVia(rel.FromTable, rel.ToTable) is { } via) LocalEngine.MarkSecondPathOff(rel, via, facts);
            else return;
            link.Active = false;
            link.Note = rel.Note;
        }

        /// <summary>What turning this link on would clash with, in plain words, or null when it can be on: another active link between the same two tables, or a second path.</summary>
        internal static string WhyNotOn(StructureManifest m, ManifestLink link)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var rel = Rel(link);
            var on = new LocalEngine.ActiveLinks(cmp);
            foreach (var other in m.Links.Where(o => !ReferenceEquals(o, link) && !o.Unresolved && !o.Dropped && o.Active && BothEndsIn(m, o) && !cmp.Equals(o.FromTable, o.ToTable)))
                on.Add(Rel(other));
            var twin = on.Between(rel.FromTable, rel.ToTable);
            var via = twin == null ? on.SecondPathVia(rel.FromTable, rel.ToTable) : null;
            if (cmp.Equals(rel.FromTable, rel.ToTable) || twin != null || via != null)
                return $"The link from {rel.FromTable} to {rel.ToTable} stays off, because turning it on would give {(twin != null ? rel.FromTable : via?.From ?? rel.FromTable)} a second path to {(twin != null ? rel.ToTable : via?.To ?? rel.ToTable)}. Turn the other link off first.";
            return null;
        }
    }
}
