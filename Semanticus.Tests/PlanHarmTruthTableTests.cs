// THE CLEANUP OFFER RULE, version 2 (coordinator, 29 Sep 2026), acceptance item 1: the closed truth table.
//
// ROUND 5 (Kane, 30 Sep 2026): the row is a suggestion list, and the rule's verdict is now what STARTS TICKED, not what is listed.
// "Offered" below means ticked (a kept line Work it out proposes); a held column is still a LINE, unticked, with a reason. Every
// expected verdict is unchanged. What is new is that the listed set must equal the sweep's safe columns, both ways, and that every
// line carries its reason.
//
// A generated grid over the factors of the ruling. Table factors: its ROLE in the relationships (none, pure many side with an
// amount, pure many side without, looked up, looked up and many side, one-to-one end, many-to-many end), whether it is MARKED as
// the date table, whether it maps an AGGREGATION, whether its LABEL can be found (named for the table incl. a prefix, a plural and
// an abbreviation, named for the key's entity, a name word plus an entity, exactly Title, the default label, a label with no signal,
// no text at all), and a table BELT (a calendar by its key, by a hierarchy, by a date beside a calendar word, by two calendar
// words; a code table by its name, by its key). Column factors: TYPE (string, true/false, whole number, double, decimal, date),
// SIGNAL (none, IsKey, a relationship column, key word, REF, date word, ON, calendar word, name word, quantity word, named for
// its table, first text column, sort-by source, data category, perspective member, default image, built this session) and SWEEP
// state (safe, a visible measure uses it, only a hidden measure uses it, a read report shows it, RLS, OLS, a calculation item).
// The grid is a pairwise covering array (every pair of levels of every two factors appears together in at least one cell) plus
// every single-factor flip from the offerable base cell, plus a calculation group and a calculated table.
//
// The EXPECTED verdict of every column is computed here from the factors, by Expected() below, which never calls the guard and
// reads no name: each column carries the tags its factors gave it (Cal, Qty, Strong, ...) and the table's derived facts are
// worked out from those tags. The ACTUAL verdict is the whole engine's: each cell's tables are written as TMDL, opened fresh (so
// nothing is "built in this session" unless a cell says so), the Plan is read and Work it out proposes the cleanup's lines.
// PASS = the offered set equals the expected set, both ways, on Free and on Pro, with no report and with one report read; and the
// row's held-back count is exactly the unfiltered sweep's safe items minus the ones it offers.
//
// Several cells share one model (a chunk), each cell in tables of its own, so the whole grid is a handful of model opens. What
// fails is reported cell by cell as HARM (offered, must be held) or FRICTION (held, must be offered).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHarmTruthTableTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public PlanHarmTruthTableTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        // ---- the factors ---------------------------------------------------------------------------------------------

        private enum Role { None, ManyAmount, ManyNoAmount, LookedUp, LookedUpAndMany, OneToOne, ManyToMany }
        private enum Label { TableName, Prefixed, Plural, Abbreviation, KeyEntity, NamePlusEntity, ExactGeneric, DefaultLabel, Unsignalled, NoText }
        private enum Belt { None, CalKey, CalHierarchy, CalDatePlusWord, CalTwoWords, CodeName, CodeKey }
        private enum ColType { String, Boolean, Int64, Double, Decimal, DateTime }
        private enum Signal { None, IsKey, RelColumn, KeyWord, RefWord, DateWord, OnWord, CalWord, NameWord, QtyWord, NamedForTable, FirstText, SortBySource, DataCategory, InPerspective, DefaultImage, BuiltThisSession }
        private enum Sweep { Safe, VisibleMeasure, HiddenMeasureOnly, ReportShows, Rls, Ols, CalcItem }

        private sealed record Cell(Role Role, bool Marked, bool Agg, Label Label, Belt Belt, ColType Type, Signal Signal, Sweep Sweep)
        {
            public override string ToString() =>
                $"role={Role} marked={Marked} agg={Agg} label={Label} belt={Belt} | type={Type} signal={Signal} sweep={Sweep}";
        }

        private static readonly int[] Sizes =
            { Enum.GetValues<Role>().Length, 2, 2, Enum.GetValues<Label>().Length, Enum.GetValues<Belt>().Length,
              Enum.GetValues<ColType>().Length, Enum.GetValues<Signal>().Length, Enum.GetValues<Sweep>().Length };

        private static Cell CellOf(int[] x) =>
            new Cell((Role)x[0], x[1] == 1, x[2] == 1, (Label)x[3], (Belt)x[4], (ColType)x[5], (Signal)x[6], (Sweep)x[7]);

        /// <summary>The combinations that can be built at all (nothing else is skipped).</summary>
        private static bool Valid(Cell c) =>
            !((c.Belt == Belt.CalKey || c.Belt == Belt.CodeKey) && c.Role != Role.LookedUp && c.Role != Role.LookedUpAndMany)   // a key is read on the looked-up end
            && !(c.Signal == Signal.RelColumn && (c.Role == Role.None || c.Sweep != Sweep.Safe))
            && !(c.Signal == Signal.BuiltThisSession && c.Sweep != Sweep.Safe)
            && !(c.Signal == Signal.FirstText && c.Type != ColType.String)
            && !(c.Label == Label.KeyEntity && (c.Belt == Belt.CalKey || c.Belt == Belt.CodeKey));                              // the key has one name

        private static readonly Cell Base = new Cell(Role.LookedUp, false, false, Label.TableName, Belt.None, ColType.String, Signal.None, Sweep.Safe);

        /// <summary>The whole grid, deterministic: the same cells on every run and every machine.</summary>
        private static readonly Lazy<List<Cell>> Grid = new Lazy<List<Cell>>(BuildGrid);

        private static List<Cell> BuildGrid()
        {
            var cells = new List<Cell> { Base };
            // Every single-factor flip from the base cell.
            var baseInts = new[] { (int)Base.Role, 0, 0, (int)Base.Label, (int)Base.Belt, (int)Base.Type, (int)Base.Signal, (int)Base.Sweep };
            for (var f = 0; f < Sizes.Length; f++)
                for (var l = 0; l < Sizes[f]; l++)
                {
                    if (l == baseInts[f]) continue;
                    var x = (int[])baseInts.Clone();
                    x[f] = l;
                    var cell = CellOf(x);
                    if (Valid(cell)) cells.Add(cell);
                }
            // A pairwise covering array over every factor: most of its cells hold everything, because most combinations
            // carry some reason to hold. Then a second one over the OFFERABLE tables only (related, unmarked, no aggregation,
            // no belt), where the column and label factors decide, so the offers themselves are covered pairwise too.
            cells.AddRange(Pairwise(Valid, 20260929, cells));
            cells.AddRange(Pairwise(c => Valid(c) && c.Role != Role.None && !c.Marked && !c.Agg && c.Belt == Belt.None, 20260930, cells));
            return cells.Distinct().ToList();
        }

        private static IEnumerable<Cell> Pairwise(Func<Cell, bool> valid, int seed, IEnumerable<Cell> already)
        {
            var added = new List<Cell>();
            var rng = new Random(seed);
            int[] Random_() { var x = new int[Sizes.Length]; for (var i = 0; i < x.Length; i++) x[i] = rng.Next(Sizes[i]); return x; }
            var covered = new HashSet<(int, int, int, int)>();
            void Cover(Cell c)
            {
                var x = Ints(c);
                for (var i = 0; i < x.Length; i++) for (var j = i + 1; j < x.Length; j++) covered.Add((i, x[i], j, x[j]));
            }
            foreach (var c in already) if (valid(c)) Cover(c);

            // The pairs some valid cell can hold at all.
            var pairs = new List<(int, int, int, int)>();
            for (var i = 0; i < Sizes.Length; i++)
                for (var j = i + 1; j < Sizes.Length; j++)
                    for (var a = 0; a < Sizes[i]; a++)
                        for (var b = 0; b < Sizes[j]; b++)
                            if (Fill(valid, i, a, j, b, Random_, 400) != null) pairs.Add((i, a, j, b));

            while (true)
            {
                var open = pairs.Where(p => !covered.Contains(p)).ToList();
                if (open.Count == 0) break;
                // Best of a few hundred random fills, each forced to cover one open pair, so every round makes progress.
                int[] best = null; var bestScore = -1;
                for (var k = 0; k < 300; k++)
                {
                    var p = open[rng.Next(open.Count)];
                    var x = Fill(valid, p.Item1, p.Item2, p.Item3, p.Item4, Random_, 50);
                    if (x == null) continue;
                    var score = 0;
                    for (var i = 0; i < x.Length; i++) for (var j = i + 1; j < x.Length; j++) if (!covered.Contains((i, x[i], j, x[j]))) score++;
                    if (score > bestScore) { bestScore = score; best = x; }
                }
                var cell = CellOf(best);
                Cover(cell);
                added.Add(cell);
            }
            return added;
        }

        private static int[] Ints(Cell c) =>
            new[] { (int)c.Role, c.Marked ? 1 : 0, c.Agg ? 1 : 0, (int)c.Label, (int)c.Belt, (int)c.Type, (int)c.Signal, (int)c.Sweep };

        private static int[] Fill(Func<Cell, bool> valid, int fi, int li, int fj, int lj, Func<int[]> random, int tries)
        {
            for (var t = 0; t < tries; t++)
            {
                var x = random();
                x[fi] = li; x[fj] = lj;
                if (valid(CellOf(x))) return x;
            }
            // Random fills found none: search every completion, so "no cell can hold this pair" is exact, not a matter of luck.
            var free = Enumerable.Range(0, Sizes.Length).Where(i => i != fi && i != fj).ToArray();
            var y = new int[Sizes.Length];
            y[fi] = li; y[fj] = lj;
            bool Walk(int at)
            {
                if (at == free.Length) return valid(CellOf(y));
                for (var v = 0; v < Sizes[free[at]]; v++) { y[free[at]] = v; if (Walk(at + 1)) return true; }
                return false;
            }
            return Walk(0) ? (int[])y.Clone() : null;
        }

        // ---- the tables a cell is made of --------------------------------------------------------------------------------

        private sealed class Col
        {
            public string Name; public ColType Type;
            public bool IsKey, Rel, KeyNamed, KeyBelt, DateName, DateLast, Qty, NamedForTable, NameWord, Strong;
            public bool DefaultLabel, DefaultImage, SortBy, DataCat, Persp, Built;
            public string Cal;                  // the calendar word its name carries
            public bool Safe = true;            // the sweep can call it safe when nothing else changes: not structural, not used
            public bool ShownByReport;
            public string SortTarget;
            public bool Numeric => Type == ColType.Int64 || Type == ColType.Double || Type == ColType.Decimal;
            public bool KeyShaped => IsKey || Rel || KeyNamed;
        }

        private sealed class Spec
        {
            public string Name, Description;
            public bool Plain = true, Marked, Agg, CalHierarchy, CalKey, CodeName, CodeKey;
            public Role Role;
            public readonly List<Col> Cols = new List<Col>();
            public string Kind = "table";     // table | calcgroup | calculated
            public Col Probe;                   // the column the cell is about
            public string Extra = "";          // hierarchy and other table-level TMDL
            // Derived from the tags, never from the guard:
            public bool Related, CalendarShaped, CodeShaped, SkipLabel, LabelFound;
            public readonly HashSet<Col> LabelCandidates = new HashSet<Col>();

            public void Finish()
            {
                Related = Role != Role.None;
                var calWords = new HashSet<string>(Cols.Where(c => !c.DateName && c.Cal != null).Select(c => c.Cal));
                var hasDate = Cols.Any(c => c.Type == ColType.DateTime || c.DateName);
                CalendarShaped = CalKey || CalHierarchy || (hasDate && calWords.Count > 0) || calWords.Count >= 2;
                CodeShaped = CodeName || CodeKey;
                var identity = Role != Role.ManyAmount && Role != Role.ManyNoAmount;   // it is a one, a one-to-one or a many-to-many end somewhere
                SkipLabel = !identity && Cols.Any(c => c.Numeric && !c.KeyShaped);
                var text = Cols.Where(c => c.Type == ColType.String && !c.KeyShaped).ToList();
                LabelFound = text.Any(c => c.Strong);
                foreach (var c in text.Where(c => c.Strong || c.NameWord)) LabelCandidates.Add(c);
                if (text.Count > 0) LabelCandidates.Add(text[0]);
            }
        }

        private static string Ref(Spec t, Col c) => "column:" + t.Name + "/" + c.Name;

        /// <summary>The verdict the ruling gives one column, from its factors alone.</summary>
        private static bool Expected(Spec t, Col c, bool reportRead)
        {
            if (!t.Plain || c.Built || !c.Safe || (reportRead && c.ShownByReport)) return false;
            if (c.Type != ColType.String && c.Type != ColType.Boolean) return false;
            if (c.IsKey || c.Rel || c.SortBy || c.DataCat || c.Persp || c.DefaultLabel || c.DefaultImage) return false;
            if (!t.Related || t.Marked || t.Agg || t.CalendarShaped || t.CodeShaped) return false;
            if (c.KeyBelt || c.DateName || c.DateLast || c.Cal != null || c.Qty || c.NamedForTable) return false;
            if (t.SkipLabel) return !c.NameWord;
            return t.LabelFound && !t.LabelCandidates.Contains(c);
        }

        private static Col Column(string name, ColType type, Action<Col> tag = null)
        {
            var c = new Col { Name = name, Type = type };
            tag?.Invoke(c);
            return c;
        }

        private static Spec Cluster(int n, Cell cell)
        {
            var nn = n.ToString("00");
            var (baseName, stem, labelName, labelTags) = cell.Label switch
            {
                Label.TableName => ("Widget", "WIDGET", "Widget", "strong,named"),
                Label.Prefixed => ("dim_widget", "WIDGET", "Widget", "strong,named"),
                Label.Plural => ("Branches", "BRANCH", "Branch", "strong,named"),
                Label.Abbreviation => ("dim_project", "PROJECT", "PRJ", "strong,named"),
                Label.KeyEntity => ("People", "PERSON", "Person", "strong"),
                Label.NamePlusEntity => ("Client", "CLIENT", "CLNT_NM", "strong,named,name"),
                Label.ExactGeneric => ("Roster", "ROSTER", "Title", "strong,name"),
                Label.DefaultLabel => ("Roster", "ROSTER", "TAGLINE", "strong,default"),
                Label.Unsignalled => ("Roster", "ROSTER", "TAGLINE", ""),
                _ => ("Roster", "ROSTER", null, ""),
            };
            var t = new Spec
            {
                Name = (cell.Belt == Belt.CodeName ? "ref_" : "") + baseName + "_" + nn,
                Role = cell.Role, Marked = cell.Marked, Agg = cell.Agg,
                CalHierarchy = cell.Belt == Belt.CalHierarchy, CalKey = cell.Belt == Belt.CalKey,
                CodeName = cell.Belt == Belt.CodeName, CodeKey = cell.Belt == Belt.CodeKey,
                Description = cell.ToString(),
            };
            var stem4 = baseName.Split('_').Last().Substring(0, 4);   // "Widg", "Bran", "Peop": a word that matches the table's name

            // The key. Its type is the probe's when the probe IS the relationship column.
            var keyName = cell.Belt == Belt.CalKey ? "PERIOD_KEY" : cell.Belt == Belt.CodeKey ? stem + "_CD" : stem + "_KEY";
            var keyType = cell.Signal == Signal.RelColumn ? cell.Type : ColType.Int64;
            var key = Column(keyName, keyType, c =>
            {
                c.Rel = t.Related; c.KeyNamed = cell.Belt != Belt.CodeKey; c.KeyBelt = c.KeyNamed;
                if (cell.Belt == Belt.CalKey) c.Cal = "PERIOD";
            });
            t.Cols.Add(key);

            // The probe. It sits last, except a probe that must be the first text column.
            var probe = ProbeFor(cell, t, stem4, key);
            t.Probe = probe ?? key;
            if (probe != null && cell.Signal == Signal.FirstText) t.Cols.Add(probe);

            if (cell.Role == Role.ManyAmount) t.Cols.Add(Column("SPEND", ColType.Double));
            if (cell.Role == Role.LookedUpAndMany) t.Cols.Add(Column("OUTER_FK", ColType.Int64, c => { c.Rel = true; c.KeyNamed = true; c.KeyBelt = true; }));
            if (labelName != null)
                t.Cols.Add(Column(labelName, ColType.String, c =>
                {
                    c.Strong = labelTags.Contains("strong"); c.NamedForTable = labelTags.Contains("named");
                    c.NameWord = labelTags.Contains("name"); c.DefaultLabel = labelTags.Contains("default");
                }));
            switch (cell.Belt)
            {
                case Belt.CalHierarchy:
                    t.Cols.Add(Column("LVL_A", ColType.Int64, c => c.Safe = false));
                    t.Cols.Add(Column("LVL_B", ColType.Int64, c => c.Safe = false));
                    t.Extra += "\thierarchy Calendar\n\t\tlevel A\n\t\t\tcolumn: LVL_A\n\t\tlevel B\n\t\t\tcolumn: LVL_B\n\n";
                    break;
                case Belt.CalDatePlusWord:
                    t.Cols.Add(Column("EVENT_STAMP", ColType.DateTime));
                    t.Cols.Add(Column("WEEKDAY_FLG", ColType.Int64, c => c.Cal = "WEEKDAY"));
                    break;
                case Belt.CalTwoWords:
                    t.Cols.Add(Column("YEAR_LBL", ColType.Int64, c => c.Cal = "YEAR"));
                    t.Cols.Add(Column("MONTH_LBL", ColType.Int64, c => c.Cal = "MONTH"));
                    break;
            }
            if (cell.Signal == Signal.SortBySource) t.Cols.Add(Column("QUIRK_ORD", ColType.Int64, c => c.Safe = false));
            if (probe != null && cell.Signal != Signal.FirstText && cell.Signal != Signal.RelColumn) t.Cols.Add(probe);
            if (cell.Agg) t.Cols.Add(Column("AGG_GRP", ColType.String, c => c.Safe = false));
            t.Finish();
            return t;
        }

        private static Col ProbeFor(Cell cell, Spec t, string stem4, Col key)
        {
            if (cell.Signal == Signal.RelColumn) return null;   // the probe is the relationship column itself
            var name = cell.Signal switch
            {
                Signal.KeyWord => "QUIRK_ID",
                Signal.RefWord => "QUIRK_REF",
                Signal.DateWord => "QUIRK_DT",
                Signal.OnWord => "QUIRK_ON",
                Signal.CalWord => "QUIRK_SEMESTER",
                Signal.NameWord => "QUIRK_NM",
                Signal.QtyWord => "QUIRK_AMT",
                Signal.NamedForTable => stem4,
                _ => "QUIRK",
            };
            var p = new Col { Name = name, Type = cell.Type };
            switch (cell.Signal)
            {
                case Signal.IsKey: p.IsKey = true; break;
                case Signal.KeyWord: p.KeyNamed = true; p.KeyBelt = true; break;
                case Signal.RefWord: p.KeyBelt = true; break;
                case Signal.DateWord: p.DateName = true; break;
                case Signal.OnWord: p.DateLast = true; break;
                case Signal.CalWord: p.Cal = "SEMESTER"; break;
                case Signal.NameWord: p.NameWord = true; break;
                case Signal.QtyWord: p.Qty = true; break;
                case Signal.NamedForTable: p.NamedForTable = true; p.Strong = true; break;
                case Signal.SortBySource: p.SortBy = true; p.SortTarget = "QUIRK_ORD"; break;
                case Signal.DataCategory: p.DataCat = true; break;
                case Signal.InPerspective: p.Persp = true; break;
                case Signal.DefaultImage: p.DefaultImage = true; break;
                case Signal.BuiltThisSession: p.Built = true; break;
            }
            switch (cell.Sweep)
            {
                case Sweep.ReportShows: p.ShownByReport = true; break;
                case Sweep.VisibleMeasure: case Sweep.HiddenMeasureOnly: case Sweep.Rls: case Sweep.Ols: case Sweep.CalcItem: p.Safe = false; break;
            }
            return p;
        }

        // ---- the partner tables and the model around them -----------------------------------------------------------------

        private sealed class Model
        {
            public readonly List<Spec> Clusters = new List<Spec>();
            public readonly List<(Spec T, Col P, Sweep How)> Uses = new List<(Spec, Col, Sweep)>();
            public readonly List<string> Partners = new List<string>();   // full TMDL of the partner tables
            public readonly List<string> Relationships = new List<string>();
            public readonly List<(Spec T, Col C)> Perspective = new List<(Spec, Col)>();
        }

        private static string DataType(ColType t) => t switch
        {
            ColType.String => "string", ColType.Boolean => "boolean", ColType.Int64 => "int64",
            ColType.Double => "double", ColType.Decimal => "decimal", _ => "dateTime",
        };

        private static string EngineType(ColType t) => t.ToString();

        private static string Partner(string name, string col, ColType type) =>
            "table " + name + "\n\n\tcolumn " + col + "\n\t\tdataType: " + DataType(type) + "\n\t\tsummarizeBy: none\n\t\tsourceColumn: " + col
            + "\n\n\tpartition " + name + " = m\n\t\tmode: import\n\t\tsource = let Source = 1 in Source\n";

        private static void Relate(Model m, string from, string to, string fromCard = null, string toCard = null)
        {
            var sb = new StringBuilder("relationship r" + (m.Relationships.Count + 1).ToString("000") + "\n");
            if (fromCard != null) sb.Append("\tfromCardinality: " + fromCard + "\n");
            if (toCard != null) sb.Append("\ttoCardinality: " + toCard + "\n");
            if (fromCard == "many" && toCard == "many") sb.Append("\tcrossFilteringBehavior: bothDirections\n");
            sb.Append("\tfromColumn: " + from + "\n\ttoColumn: " + to + "\n");
            m.Relationships.Add(sb.ToString());
        }

        private static Model Assemble(IEnumerable<(int N, Cell Cell)> cells, IEnumerable<Spec> specials)
        {
            var m = new Model();
            foreach (var (n, cell) in cells)
            {
                var t = Cluster(n, cell);
                m.Clusters.Add(t);
                var nn = n.ToString("00");
                var key = t.Cols[0];
                switch (cell.Role)
                {
                    case Role.ManyAmount: case Role.ManyNoAmount:
                        m.Partners.Add(Partner("dimp_" + nn, "DK", key.Type));
                        Relate(m, t.Name + "." + key.Name, "dimp_" + nn + ".DK");
                        break;
                    case Role.LookedUp:
                        m.Partners.Add(Partner("fact_" + nn, "FK", key.Type));
                        Relate(m, "fact_" + nn + ".FK", t.Name + "." + key.Name);
                        break;
                    case Role.LookedUpAndMany:
                        m.Partners.Add(Partner("fact_" + nn, "FK", key.Type));
                        m.Partners.Add(Partner("dim2_" + nn, "DK", ColType.Int64));
                        Relate(m, "fact_" + nn + ".FK", t.Name + "." + key.Name);
                        Relate(m, t.Name + ".OUTER_FK", "dim2_" + nn + ".DK");
                        break;
                    case Role.OneToOne:
                        m.Partners.Add(Partner("fact_" + nn, "FK", key.Type));
                        Relate(m, "fact_" + nn + ".FK", t.Name + "." + key.Name, "one", "one");
                        break;
                    case Role.ManyToMany:
                        m.Partners.Add(Partner("fact_" + nn, "FK", key.Type));
                        Relate(m, "fact_" + nn + ".FK", t.Name + "." + key.Name, "many", "many");
                        break;
                }
                if (cell.Agg) m.Partners.Add(Partner("base_" + nn, "BASE_GRP", ColType.String));
                var probe = t.Probe;
                if (cell.Sweep != Sweep.Safe && cell.Sweep != Sweep.ReportShows) m.Uses.Add((t, probe, cell.Sweep));
                if (probe.Persp) m.Perspective.Add((t, probe));
            }
            foreach (var s in specials) m.Clusters.Add(s);
            return m;
        }

        // ---- TMDL ---------------------------------------------------------------------------------------------------------

        private static string TableTmdl(Spec t, Model m)
        {
            var sb = new StringBuilder();
            if (t.Kind == "calcgroup")
            {
                sb.Append("table " + t.Name + "\n\n\tcalculationGroup\n\n\t\tcalculationItem Only = SELECTEDMEASURE ()\n\n");
                foreach (var c in t.Cols)
                    sb.Append("\tcolumn " + c.Name + "\n\t\tdataType: " + DataType(c.Type) + "\n\t\tsummarizeBy: none\n\t\tsourceColumn: " + (c.Name == "Pick" ? "Name" : c.Name) + "\n\n");
                return sb.ToString();
            }
            if (t.Kind == "calculated")
            {
                sb.Append("table " + t.Name + "\n\n");
                foreach (var c in t.Cols)
                    sb.Append("\tcolumn " + c.Name + "\n\t\tdataType: " + DataType(c.Type) + "\n\t\tisDataTypeInferred\n\t\tisNameInferred\n\t\tsummarizeBy: none\n\t\tsourceColumn: [" + c.Name + "]\n\n");
                sb.Append("\tpartition " + t.Name + " = calculated\n\t\tmode: import\n\t\tsource = ROW ( \"A\", \"x\" )\n");
                return sb.ToString();
            }
            sb.Append("table " + t.Name + "\n");
            if (t.Marked) sb.Append("\tdataCategory: Time\n");
            sb.Append("\n");
            foreach (var c in t.Cols)
            {
                if (c.Built) continue;   // made after the model is opened
                sb.Append("\tcolumn " + c.Name + "\n\t\tdataType: " + DataType(c.Type) + "\n\t\tsummarizeBy: none\n\t\tsourceColumn: " + c.Name + "\n");
                if (c.IsKey) sb.Append("\t\tisKey\n");
                if (c.DataCat) sb.Append("\t\tdataCategory: City\n");
                if (c.DefaultLabel) sb.Append("\t\tisDefaultLabel\n");
                if (c.DefaultImage) sb.Append("\t\tisDefaultImage\n");
                if (c.SortBy) sb.Append("\t\tsortByColumn: " + c.SortTarget + "\n");
                if (c.Name == "AGG_GRP") sb.Append("\n\t\talternateOf\n\t\t\tsummarization: groupBy\n\t\t\tbaseColumn: base_" + t.Name.Split('_').Last() + ".BASE_GRP\n");
                sb.Append("\n");
            }
            sb.Append(t.Extra);
            sb.Append("\tpartition " + t.Name + " = m\n\t\tmode: import\n\t\tsource = let Source = 1 in Source\n");
            return sb.ToString();
        }

        private static void WriteModel(string dir, Model m)
        {
            Directory.CreateDirectory(Path.Combine(dir, "tables"));
            File.WriteAllText(Path.Combine(dir, "database.tmdl"), "database Truth\n\tcompatibilityLevel: 1604\n\tcompatibilityMode: powerBI\n");
            File.WriteAllText(Path.Combine(dir, "model.tmdl"), "model Model\n\tculture: en-US\n\tdefaultPowerBIDataSourceVersion: powerBI_V3\n");
            foreach (var t in m.Clusters) File.WriteAllText(Path.Combine(dir, "tables", t.Name + ".tmdl"), TableTmdl(t, m));
            var n = 0;
            foreach (var p in m.Partners) File.WriteAllText(Path.Combine(dir, "tables", "partner" + (++n).ToString("000") + ".tmdl"), p);

            // The measure table, the calculation group that reads columns, and the two roles.
            var fix = new StringBuilder("table _Fix\n\n\tcolumn ANCHOR\n\t\tdataType: string\n\t\tsummarizeBy: none\n\t\tsourceColumn: ANCHOR\n\n");
            var items = new StringBuilder();
            var rls = new StringBuilder(); var ols = new StringBuilder();
            var k = 0;
            foreach (var (t, p, how) in m.Uses)
            {
                k++;
                var use = "'" + t.Name + "'[" + p.Name + "]";
                switch (how)
                {
                    case Sweep.VisibleMeasure: fix.Append("\tmeasure V" + k + " = COUNTROWS ( DISTINCT ( " + use + " ) )\n\n"); break;
                    case Sweep.HiddenMeasureOnly: fix.Append("\tmeasure H" + k + " = COUNTROWS ( DISTINCT ( " + use + " ) )\n\t\tisHidden\n\n"); break;
                    case Sweep.CalcItem: items.Append("\t\tcalculationItem I" + k + " = CALCULATE ( SELECTEDMEASURE (), NOT ISBLANK ( " + use + " ) )\n\n"); break;
                    case Sweep.Rls: rls.Append("\ttablePermission " + t.Name + " = " + use + " = \"x\"\n\n"); break;
                    case Sweep.Ols: ols.Append("\ttablePermission " + t.Name + "\n\t\tcolumnPermission " + p.Name + " = none\n\n"); break;
                }
            }
            fix.Append("\tpartition _Fix = m\n\t\tmode: import\n\t\tsource = let Source = 1 in Source\n");
            File.WriteAllText(Path.Combine(dir, "tables", "_Fix.tmdl"), fix.ToString());
            File.WriteAllText(Path.Combine(dir, "tables", "cg_fix.tmdl"),
                "table cg_fix\n\n\tcalculationGroup\n\t\tprecedence: 10\n\n\t\tcalculationItem I0 = SELECTEDMEASURE ()\n\n" + items
                + "\tcolumn Name\n\t\tdataType: string\n\t\tsummarizeBy: none\n\t\tsourceColumn: Name\n\n");
            Directory.CreateDirectory(Path.Combine(dir, "roles"));
            if (rls.Length > 0) File.WriteAllText(Path.Combine(dir, "roles", "RLS.tmdl"), "role RLS\n\tmodelPermission: read\n\n" + rls);
            if (ols.Length > 0) File.WriteAllText(Path.Combine(dir, "roles", "OLS.tmdl"), "role OLS\n\tmodelPermission: read\n\n" + ols);
            if (m.Perspective.Count > 0)
            {
                var ps = new StringBuilder("perspective PickedColumns\n\n");
                foreach (var g in m.Perspective.GroupBy(x => x.T))
                {
                    ps.Append("\tperspectiveTable " + g.Key.Name + "\n\n");
                    foreach (var (_, c) in g) ps.Append("\t\tperspectiveColumn " + c.Name + "\n\n");
                }
                Directory.CreateDirectory(Path.Combine(dir, "perspectives"));
                File.WriteAllText(Path.Combine(dir, "perspectives", "PickedColumns.tmdl"), ps.ToString());
            }
            if (m.Relationships.Count > 0) File.WriteAllText(Path.Combine(dir, "relationships.tmdl"), string.Join("\n", m.Relationships));
        }

        /// <summary>A report folder that shows every column the sweep must treat as read, plus the anchor, so the reading matches
        /// the model and the columns it does not show stay candidates.</summary>
        private static string WriteReport(string root, Model m)
        {
            var projections = new List<string> { Projection("_Fix", "ANCHOR") };
            foreach (var t in m.Clusters) foreach (var c in t.Cols.Where(c => c.ShownByReport)) projections.Add(Projection(t.Name, c.Name));
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " + string.Join(", ", projections) + " ] } } } } }";
            var dir = Path.Combine(root, "Main.Report", "definition");
            Directory.CreateDirectory(Path.Combine(dir, "pages", "p1", "visuals", "v1"));
            File.WriteAllText(Path.Combine(dir, "report.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "pages", "p1", "visuals", "v1", "visual.json"), visual);
            return root;
        }

        private static string Projection(string table, string column) =>
            "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": " + JsonSerializer.Serialize(table)
            + " } }, \"Property\": " + JsonSerializer.Serialize(column) + " } } }";

        // ---- the chunks and the run ---------------------------------------------------------------------------------------

        private const int ClustersPerModel = 40;

        private static IEnumerable<Spec> Specials(int n)
        {
            var group = new Spec { Name = "cg_" + n.ToString("00"), Kind = "calcgroup", Plain = false, Role = Role.None, Description = "a calculation group whose own column was renamed" };
            group.Cols.Add(Column("Pick", ColType.String, c => c.Safe = false));   // the sweep never lists it; if it did, the row must still hold it
            group.Cols.Add(Column("Ordinal", ColType.Int64));
            group.Finish();
            var calc = new Spec { Name = "calc_" + n.ToString("00"), Kind = "calculated", Plain = false, Role = Role.None, Description = "a calculated table" };
            calc.Cols.Add(Column("A", ColType.String, c => c.Safe = false));
            calc.Finish();
            return new[] { group, calc };
        }

        private static int ChunkCount => (Grid.Value.Count + ClustersPerModel - 1) / ClustersPerModel;

        public static IEnumerable<object[]> Runs()
        {
            for (var chunk = 0; chunk < ChunkCount; chunk++)
                foreach (var pro in new[] { false, true })
                    foreach (var read in new[] { false, true })
                        yield return new object[] { chunk, pro, read };
        }

        [Fact]
        public void The_grid_covers_every_pair_and_every_single_flip_and_has_the_base_cell_in_every_model()
        {
            var cells = Grid.Value;
            Assert.Contains(Base, cells);
            Assert.All(cells, c => Assert.True(Valid(c), c.ToString()));
            // Every single-factor flip from the base cell.
            foreach (var role in Enum.GetValues<Role>()) if (Valid(Base with { Role = role })) Assert.Contains(Base with { Role = role }, cells);
            foreach (var label in Enum.GetValues<Label>()) Assert.Contains(Base with { Label = label }, cells);
            foreach (var belt in Enum.GetValues<Belt>()) if (Valid(Base with { Belt = belt })) Assert.Contains(Base with { Belt = belt }, cells);
            foreach (var signal in Enum.GetValues<Signal>()) if (Valid(Base with { Signal = signal })) Assert.Contains(Base with { Signal = signal }, cells);
            foreach (var sweep in Enum.GetValues<Sweep>()) Assert.Contains(Base with { Sweep = sweep }, cells);
            // The base cell offers its probe; the chunk that holds it always has a row to read (the first cell of every chunk is offerable).
            var baseSpec = Cluster(1, Base);
            Assert.True(Expected(baseSpec, baseSpec.Probe, false));
            // Pairs, over every factor and again over the offerable tables.
            var missing = new List<string>();
            foreach (var (valid, label) in new (Func<Cell, bool>, string)[] { (Valid, "all"), (c => Valid(c) && c.Role != Role.None && !c.Marked && !c.Agg && c.Belt == Belt.None, "offerable") })
            {
                var seen = new HashSet<(int, int, int, int)>();
                foreach (var c in cells.Where(valid))
                {
                    var x = Ints(c);
                    for (var i = 0; i < x.Length; i++) for (var j = i + 1; j < x.Length; j++) seen.Add((i, x[i], j, x[j]));
                }
                var rng = new Random(1);
                int[] R() { var x = new int[Sizes.Length]; for (var i = 0; i < x.Length; i++) x[i] = rng.Next(Sizes[i]); return x; }
                for (var i = 0; i < Sizes.Length; i++)
                    for (var j = i + 1; j < Sizes.Length; j++)
                        for (var a = 0; a < Sizes[i]; a++)
                            for (var b = 0; b < Sizes[j]; b++)
                                if (!seen.Contains((i, a, j, b)) && Fill(valid, i, a, j, b, R, 400) != null) missing.Add($"{label}({i}={a}, {j}={b})");
            }
            Assert.True(missing.Count == 0, "pairs no cell covers: " + string.Join(" ", missing.Take(20)));
        }

        [Fact]
        public async Task The_sweep_itself_never_lists_a_columns_own_aggregation_or_a_calculation_groups_column()
        {
            // The two LineageGraph fixes (unused_objects, the Lineage page and the cleanup row share this sweep): a column mapped
            // by its OWN aggregation is structural, and a calculation group's column is never a candidate.
            var model = Assemble(new[] { (1, Base with { Agg = true }) }, Specials(90));
            using var f = new DecisionFixture(TestEntitlements.Free);
            var dir = Path.Combine(f.Workspace, "TruthSweep");
            WriteModel(dir, model);
            await f.Engine.OpenAsync(dir, discardUnsaved: true);
            f.UseNull();
            var listed = (await f.Engine.UnusedObjectsAsync()).Items.Select(i => i.Ref).ToArray();
            Assert.DoesNotContain(listed, r => r.EndsWith("/AGG_GRP", StringComparison.Ordinal));
            Assert.DoesNotContain(listed, r => r.StartsWith("column:cg_90/", StringComparison.Ordinal));
            Assert.Contains("column:" + model.Clusters[0].Name + "/Widget", listed);   // the sweep still lists what nothing uses
        }

        [Fact]
        public async Task The_row_says_what_starts_ticked_in_one_constant_sentence_and_a_code_tables_column_says_why_it_is_unticked()
        {
            var model = Assemble(new[] { (1, Base) }, Specials(90));
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var dir = Path.Combine(f.Workspace, "TruthCopy");
            WriteModel(dir, model);
            await f.Engine.OpenAsync(dir, discardUnsaved: true);
            f.UseNull();
            var row = Assert.Single((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "cleanup/model");
            var count = Assert.Single(row.Why, w => Regex.IsMatch(w, @" unused columns? (is|are) listed"));
            Assert.Contains("start ticked", count, StringComparison.Ordinal);
            Assert.Contains(LocalEngine.CleanupPurposeSentence, row.Why);
            Assert.DoesNotContain("lookup", string.Join(" ", row.Why), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", string.Join(" ", row.Why), StringComparison.Ordinal);                // no em dash in copy
            Assert.DoesNotContain("Copilot", string.Join(" ", row.Why), StringComparison.OrdinalIgnoreCase);
        }

        [Theory, InlineData(0, true, false)]
        public Task The_cleanup_offers_exactly_what_the_ruling_says_on_every_cell_sample(int chunk, bool pro, bool reportRead) => The_cleanup_offers_exactly_what_the_ruling_says_on_every_cell(chunk, pro, reportRead);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, MemberData(nameof(Runs))]
        public async Task The_cleanup_offers_exactly_what_the_ruling_says_on_every_cell(int chunk, bool pro, bool reportRead)
        {
            var slice = Grid.Value.Skip(chunk * ClustersPerModel).Take(ClustersPerModel).ToList();
            // Every model holds the base cell first, so the row always exists to be read.
            if (chunk > 0) slice.Insert(0, Base);
            var numbered = slice.Select((c, i) => (i + 1, c)).ToList();
            var model = Assemble(numbered, Specials(90));

            using var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var dir = Path.Combine(f.Workspace, "Truth" + chunk);
            WriteModel(dir, model);
            await f.Engine.OpenAsync(dir, discardUnsaved: true);
            f.UseNull();
            foreach (var t in model.Clusters.Where(t => t.Plain))
                foreach (var c in t.Cols.Where(c => c.Built))
                    await f.Engine.CreateColumnAsync("table:" + t.Name, c.Name, EngineType(c.Type), null, "human");
            if (reportRead)
            {
                var reports = WriteReport(Path.Combine(f.Workspace, "Reports-Truth" + chunk), model);
                await f.Engine.SetReportScopeAsync(
                    new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Main", Path = reports } }, "human");
                await PlanHarmKit.CheckReportsAgainAsync(f);
            }

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var about = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in model.Clusters)
                foreach (var c in t.Cols)
                {
                    about[Ref(t, c)] = t.Name + " (" + t.Description + ")";
                    if (Expected(t, c, reportRead)) expected.Add(Ref(t, c));
                }
            Assert.NotEmpty(expected);

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var lines = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines;
            // OFFERED means TICKED. Every column the sweep calls unused is a line, and each says why.
            var offered = new HashSet<string>(lines.Where(l => l.Kept).Select(l => l.Target ?? ""), StringComparer.OrdinalIgnoreCase);
            var listed = new HashSet<string>(lines.Select(l => l.Target ?? ""), StringComparer.OrdinalIgnoreCase);
            var sweepColumns = (await f.Engine.UnusedObjectsCoreAsync(keepVisibleUntilReportsChecked: false)).Items.Where(i => (i.Verdict == "safe" || i.Verdict == "visibleAttribute") && i.Kind != "measure").Select(i => i.Ref)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string Describe(string r) => r + "  <-  " + (about.TryGetValue(r, out var d) ? d : "not in any cell");
            var harm = offered.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Select(Describe).ToList();
            var friction = expected.Except(offered, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Select(Describe).ToList();
            var held = sweepColumns.Count - offered.Count;

            var name = "chunk " + chunk + (pro ? " Pro" : " Free") + (reportRead ? " one report read" : " no report");
            _out.WriteLine($"cells {slice.Count}, tables {model.Clusters.Count}, expected ticked {expected.Count}, ticked {offered.Count}, unticked {held}");
            Assert.True(harm.Count == 0 && friction.Count == 0,
                name + ": " + harm.Count + " HARM (offered, the ruling holds them) and " + friction.Count + " FRICTION (held, the ruling offers them)\n"
                + "HARM:\n  " + string.Join("\n  ", harm) + "\nFRICTION:\n  " + string.Join("\n  ", friction));
            // Nothing is hidden: the list is the sweep's safe columns, both ways, and every line says why and where it starts.
            Assert.True(listed.SetEquals(sweepColumns), name + ": the list and the sweep's safe columns differ: "
                + string.Join(", ", listed.Except(sweepColumns).Concat(sweepColumns.Except(listed)).Take(10)));
            Assert.All(lines, l =>
            {
                Assert.False(string.IsNullOrWhiteSpace(l.Reason), name + ": " + l.Target + " has no reason");
                Assert.Equal(l.Kept, l.Suggested);
                Assert.Equal(l.Kept, !l.Reason.StartsWith("Not ticked: ", StringComparison.Ordinal));
            });
            // The row's own count sentence is exactly the ticked and unticked counts.
            Assert.Contains(LocalEngine.CleanupCountSentence(offered.Count, held), row.Why);
            Assert.Equal(offered.Count > 0 ? "Remove " + offered.Count + " unused " + (offered.Count == 1 ? "column" : "columns")
                                           : "Review " + held + " unused " + (held == 1 ? "column" : "columns"), row.Title);
        }
    }
}
