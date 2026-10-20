using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Explain This Number (feature #2). Contracts pinned here:
    ///  • THE NON-ADDITIVE GUARD: a sum-of-parts breakdown ships ONLY when Σ(parts) provably equals the
    ///    total — distinct-count/ratio-shaped numbers get Additive=false with EMPTY rows and a plain-language
    ///    refusal, never a misleading list. A truncated member scan also refuses (can't prove additivity).
    ///  • BLANK-CHECKLIST STRUCTURE LOGIC: reachability verdicts honor real filter-flow direction
    ///    (One→Many; Many→One only for both-directions), distinguish inactive-only paths, and name no-path.
    ///  • RLS is an ADVISORY (roles that WOULD filter the dep tables) — pure set intersection.
    ///  • FREE + OFFLINE DEGRADATION: no entitlement gate; with no live connection the dossier still carries
    ///    chain/lineage and says plainly that the value was not computed (Evidence.Available=false).
    /// </summary>
    public sealed class ExplainValueTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // ---- graph fixtures (pure reachability) ----

        private static ModelGraph Graph(params GraphRelationship[] rels) => new ModelGraph
        {
            Tables = rels.SelectMany(r => new[] { r.FromTable, r.ToTable }).Distinct()
                .Select(t => new GraphTable { Name = t, Ref = "table:" + t }).ToArray(),
            Relationships = rels,
        };

        // Many(from) → One(to), the standard star edge.
        private static GraphRelationship Rel(string manyTable, string oneTable, bool active = true, string crossFilter = "OneDirection") => new GraphRelationship
        {
            FromTable = manyTable, FromColumn = oneTable + "Key", FromCardinality = "Many",
            ToTable = oneTable, ToColumn = oneTable + "Key", ToCardinality = "One",
            CrossFilter = crossFilter, IsActive = active,
        };

        private static HashSet<string> Dep(params string[] tables) => new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void Reachability_filter_on_dimension_reaches_the_fact()
        {
            var g = Graph(Rel("Sales", "Product"));
            var (v, detail) = ExplainLogic.CheckReachability(g, "Product", Dep("Sales"));
            Assert.Equal(ReachVerdict.Connected, v);
            Assert.Contains("Product", detail);
            Assert.Contains("Sales", detail);
        }

        [Fact]
        public void Reachability_filter_on_the_many_side_is_direction_blocked_not_connected()
        {
            // filter on Sales cannot flow Many→One over a single-direction relationship
            var g = Graph(Rel("Sales", "Product"));
            var (v, _) = ExplainLogic.CheckReachability(g, "Sales", Dep("Product"));
            Assert.Equal(ReachVerdict.DirectionBlocked, v);
        }

        [Fact]
        public void Reachability_both_directions_lets_the_filter_flow_backwards()
        {
            var g = Graph(Rel("Sales", "Product", crossFilter: "BothDirections"));
            var (v, _) = ExplainLogic.CheckReachability(g, "Sales", Dep("Product"));
            Assert.Equal(ReachVerdict.Connected, v);
        }

        [Fact]
        public void Reachability_inactive_only_path_is_named_inactive()
        {
            var g = Graph(Rel("Sales", "Date", active: false));
            var (v, detail) = ExplainLogic.CheckReachability(g, "Date", Dep("Sales"));
            Assert.Equal(ReachVerdict.InactiveOnly, v);
            Assert.Contains("inactive", detail);
        }

        [Fact]
        public void Reachability_no_path_at_all()
        {
            var g = Graph(Rel("Sales", "Product"));
            var (v, _) = ExplainLogic.CheckReachability(g, "Geography", Dep("Sales"));
            Assert.Equal(ReachVerdict.NoPath, v);
        }

        [Fact]
        public void Reachability_filter_on_a_data_table_is_same_table()
        {
            var g = Graph(Rel("Sales", "Product"));
            var (v, _) = ExplainLogic.CheckReachability(g, "Sales", Dep("Sales"));
            Assert.Equal(ReachVerdict.SameTable, v);
        }

        [Fact]
        public void Reachability_two_hop_path_through_a_shared_dimension()
        {
            // Budget —(many)→ Date ←(many)— Sales : a filter on Date reaches both facts; Budget→Sales does not.
            var g = Graph(Rel("Sales", "Date"), Rel("Budget", "Date"));
            Assert.Equal(ReachVerdict.Connected, ExplainLogic.CheckReachability(g, "Date", Dep("Sales")).Verdict);
            Assert.Equal(ReachVerdict.DirectionBlocked, ExplainLogic.CheckReachability(g, "Budget", Dep("Sales")).Verdict);
        }

        // ---- the non-additive guard ----

        private static IReadOnlyList<KeyValuePair<string, double>> Members(params (string M, double V)[] xs)
            => xs.Select(x => new KeyValuePair<string, double>(x.M, x.V)).ToList();

        [Fact]
        public void Contributors_additive_ships_topK_with_pct_and_everything_else()
        {
            var c = ExplainLogic.BuildContributors("'Product'[Category]", 100.0,
                Members(("A", 50), ("B", 30), ("C", 15), ("D", 5)), truncated: false, topK: 2);
            Assert.True(c.Additive);
            Assert.Equal(3, c.Rows.Length);                       // top 2 + "(everything else)"
            Assert.Equal("A", c.Rows[0].Member);
            Assert.Equal(50.0, double.Parse(c.Rows[0].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(50.0, c.Rows[0].Pct);
            Assert.Contains("everything else", c.Rows[2].Member);
            Assert.Equal(20.0, double.Parse(c.Rows[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(c.Truncated);                             // more members exist than shown
        }

        [Fact]
        public void Contributors_distinct_count_shape_refuses_with_empty_rows()
        {
            // distinct counts overlap across members: Σ(parts)=150 but the total is 100
            var c = ExplainLogic.BuildContributors("'Product'[Category]", 100.0,
                Members(("A", 80), ("B", 70)), truncated: false, topK: 5);
            Assert.False(c.Additive);
            Assert.Empty(c.Rows);                                 // NEVER a misleading sum-of-parts list
            Assert.Contains("do not add up", c.Note);
            Assert.Contains("count of different things", c.Note);   // plain words, not "distinct counts"
        }

        [Fact]
        public void Contributors_ratio_shape_refuses_too()
        {
            // a ratio's per-member values don't sum to the total ratio
            var c = ExplainLogic.BuildContributors("'Date'[Year]", 0.35,
                Members(("2023", 0.32), ("2024", 0.38)), truncated: false, topK: 5);
            Assert.False(c.Additive);
            Assert.Empty(c.Rows);
        }

        [Fact]
        public void Contributors_truncated_member_scan_refuses_honestly()
        {
            var c = ExplainLogic.BuildContributors("'Customer'[Name]", 100.0,
                Members(("A", 60), ("B", 40)), truncated: true, topK: 5);
            Assert.False(c.Additive);
            Assert.Empty(c.Rows);
            Assert.Contains("too many values", c.Note);
        }

        [Fact]
        public void Contributors_float_summation_noise_is_still_additive()
        {
            var parts = Enumerable.Range(0, 10).Select(i => ("M" + i, 10.0 + 1e-11)).ToArray();
            var c = ExplainLogic.BuildContributors("'X'[Y]", 100.0, Members(parts), truncated: false, topK: 3);
            Assert.True(c.Additive);
        }

        [Fact]
        public void Contributors_zero_total_has_null_pct()
        {
            var c = ExplainLogic.BuildContributors("'X'[Y]", 0.0, Members(("A", 50), ("B", -50)), truncated: false, topK: 5);
            Assert.True(c.Additive);
            Assert.All(c.Rows, r => Assert.Null(r.Pct));
        }

        // ---- the split that explains the number (Kane, 17 Oct: "what makes it up seems fairly useless") ----

        private static SplitTrial Trial(string dim, params double[] values)
            => new SplitTrial { Dimension = dim, Members = values.Select((v, i) => new KeyValuePair<string, double>(dim + i, v)).ToList() };

        private static double Total(SplitTrial t) => t.Members.Sum(m => m.Value);

        [Fact]
        public void A_split_that_leaves_most_of_the_number_unexplained_is_never_chosen()
        {
            // Kane's cell: 225 cities, the top five hold 15%. Four quarters hold all of it.
            var city = Trial("'Store'[City]", Enumerable.Range(0, 225).Select(i => i < 5 ? 40.0 : 4.6).ToArray());
            var each = Total(city) / 4;
            var quarter = Trial("'Date'[Quarter]", each, each, each, each);
            var c = ExplainLogic.PickSplit(new[] { city, quarter }, Total(city), 5);
            Assert.True(c.Additive);
            Assert.Equal("'Date'[Quarter]", c.Dimension);
            Assert.DoesNotContain(c.Rows, r => r.Member.Contains("everything else"));
            Assert.True(ExplainLogic.SumMatchesTotal(c.Rows.Sum(r => double.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture)), Total(city)));
        }

        [Fact]
        public void When_no_column_explains_most_of_the_number_one_plain_line_says_so()
        {
            var flat = Trial("'Store'[City]", Enumerable.Repeat(5.0, 20).ToArray());   // 20 equal parts: the top five hold 25%
            var c = ExplainLogic.PickSplit(new[] { flat }, Total(flat), 5);
            Assert.Empty(c.Rows);
            Assert.Equal("No single column explains most of this number.", c.Note);
            Assert.Empty(c.Alternatives);
        }

        [Fact]
        public void A_column_with_few_values_beats_one_with_many_even_when_the_many_cover_more()
        {
            var many = Trial("'Store'[City]", Enumerable.Range(0, 20).Select(i => i < 5 ? 17.0 : 1.0).ToArray());   // top five: 85 of 100
            var few = Trial("'Product'[Category]", 22, 20, 15, 12, 10, 8, 7, 6);                                      // 8 values, top five: 79 of 100
            var c = ExplainLogic.PickSplit(new[] { many, few }, 100, 5);
            Assert.Equal("'Product'[Category]", c.Dimension);
            Assert.Equal(new[] { "'Store'[City]" }, c.Alternatives);
        }

        [Fact]
        public void Among_columns_with_few_values_the_top_parts_that_cover_the_most_win_and_ties_keep_the_model_order()
        {
            var weak = Trial("'Product'[Style]", 25, 20, 15, 12, 10, 8, 6, 4);          // top five: 82
            var strong = Trial("'Product'[Category]", 50, 30, 10, 5, 2, 1, 1, 1);       // top five: 97
            var tieA = Trial("'Date'[Quarter]", 25, 25, 25, 25);                           // all of it: 100
            var tieB = Trial("'Date'[Half]", 50, 50);                                      // all of it: 100
            var c = ExplainLogic.PickSplit(new[] { weak, strong, tieA, tieB }, 100, 5);
            Assert.Equal("'Date'[Quarter]", c.Dimension);   // 100 percent, and first in the model's order
            // The row goes one column per table first (the chosen table's second column waits), then a second per table.
            Assert.Equal(new[] { "'Product'[Category]", "'Date'[Half]", "'Product'[Style]" }, c.Alternatives);
        }

        [Fact]
        public void Columns_that_cut_the_number_the_same_way_are_one_choice()
        {
            var number = Trial("'Date'[Quarter number]", 40, 30, 20, 10);
            var name = Trial("'Date'[Quarter name]", 40, 30, 20, 10);       // the same four parts under other labels
            var other = Trial("'Store'[Region]", 50, 30, 20);
            var c = ExplainLogic.PickSplit(new[] { number, name, other }, 100, 5);
            Assert.Equal("'Date'[Quarter number]", c.Dimension);
            Assert.Equal(new[] { "'Store'[Region]" }, c.Alternatives);
        }

        [Fact]
        public void A_split_with_one_value_or_a_cut_off_scan_is_not_a_split()
        {
            var one = Trial("'Store'[Country]", 100);
            var cut = Trial("'Customer'[Name]", 60, 40); cut.Truncated = true;
            var good = Trial("'Product'[Category]", 60, 40);
            Assert.Equal("'Product'[Category]", ExplainLogic.PickSplit(new[] { one, cut, good }, 100, 5).Dimension);
        }

        [Fact]
        public void A_number_whose_parts_do_not_add_up_still_shows_how_it_differs_and_says_why()
        {
            var a = Trial("'Product'[Brand]", 0.3, 0.5, 0.2);
            var c = ExplainLogic.PickSplit(new[] { a }, 0.35, 5);
            Assert.False(c.Additive);
            Assert.Empty(c.Rows);
            Assert.Contains("do not add up", c.Note);
            Assert.NotEmpty(c.Spread);
        }

        // ---- blank-by-design + RLS advisory (pure) ----

        [Fact]
        public void BlankByDesign_flags_divide_and_isblank()
        {
            var signals = ExplainLogic.BlankByDesignSignals(new[]
            {
                new KeyValuePair<string, string>("[Margin %]", "DIVIDE ( [Margin], [Sales] )"),
                new KeyValuePair<string, string>("[Guarded]", "IF ( ISBLANK ( [X] ), BLANK (), [X] )"),
                new KeyValuePair<string, string>("[Plain]", "SUM ( 'Sales'[Amount] )"),
            });
            Assert.Equal(2, signals.Length);
            Assert.Contains("DIVIDE", signals[0]);
            Assert.Contains("[Guarded]", signals[1]);
        }

        [Fact]
        public void RlsAdvisory_lists_only_roles_touching_the_dep_tables()
        {
            var roles = new[]
            {
                new RoleInfo { Name = "Regional", TableFilters = new[] { new TablePermissionInfo { Table = "Sales", FilterExpression = "[Region] = \"AU\"" } } },
                new RoleInfo { Name = "HR", TableFilters = new[] { new TablePermissionInfo { Table = "Employee", FilterExpression = "1=1" } } },
            };
            var hits = ExplainLogic.RlsAdvisory(roles, Dep("Sales", "Date"));
            Assert.Single(hits);
            Assert.Equal("Regional", hits[0].Role);
            Assert.Equal("Sales", hits[0].Filters[0].Table);
        }

        // ---- ref parsing + query shape ----

        [Fact]
        public void TableOf_parses_quoted_and_bare_refs()
        {
            Assert.Equal("Sales Data", ExplainLogic.TableOf("'Sales Data'[Amount]"));
            Assert.Equal("Sales", ExplainLogic.TableOf("Sales[Amount]"));
            Assert.Equal("O'Brien", ExplainLogic.TableOf("'O''Brien'[Amount]"));
            Assert.Null(ExplainLogic.TableOf("[Amount]"));
            Assert.Null(ExplainLogic.TableOf(null));
        }

        [Fact]
        public void TablesInPredicate_finds_every_quoted_table()
        {
            var tables = ExplainLogic.TablesInPredicate("'Date'[Year] >= 2024 && 'Sales Data'[Amount] > 0").ToArray();
            Assert.Contains("Date", tables);
            Assert.Contains("Sales Data", tables);
        }

        [Fact]
        public void BuildExplainQuery_keeps_the_probe_shape_and_wraps_predicates_in_calculatetable()
        {
            var q = LocalEngine.BuildExplainQuery("[Total Sales]",
                new[] { "'Product'[Category]" },
                new[] { "TREATAS ( { 2024 }, 'Date'[Year] )" },
                new[] { "'Geo'[Country] = \"AU\"" });
            Assert.Contains("CALCULATETABLE(", q);
            Assert.Contains("SUMMARIZECOLUMNS(", q);
            Assert.Contains("TREATAS ( { 2024 }, 'Date'[Year] )", q);
            Assert.Contains("'Geo'[Country] = \"AU\"", q);
            Assert.Contains("\"__present\", 1", q);                     // sentinel: a blank cell stays observable
            Assert.Contains("ORDER BY 'Product'[Category]", q);
        }

        [Fact]
        public void BuildExplainQuery_without_predicates_is_a_plain_summarizecolumns()
        {
            var q = LocalEngine.BuildExplainQuery("[X]", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            Assert.DoesNotContain("CALCULATETABLE", q);
            Assert.DoesNotContain("ORDER BY", q);
            Assert.Contains("\"__present\", 1", q);
        }

        // ---- the op, offline (free tier, no live connection) ----

        private static async Task<LocalEngine> OpenAsync(bool pro = false)
        {
            var e = new LocalEngine(new SessionManager(), new Fake(pro));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        [Fact]
        public async Task Offline_dossier_degrades_honestly_and_is_free()
        {
            var e = await OpenAsync(pro: false);   // FREE tier — explain_value carries no gate
            var d = await e.ExplainValueAsync("measure:Date/Days In Current Quarter", null, true, null, 5, "human");
            Assert.Equal("ok", d.Status);
            Assert.Equal("measure:Date/Days In Current Quarter", d.Measure);
            Assert.False(d.ValueEvaluated);
            Assert.Null(d.IsBlank);
            Assert.Null(d.Blank);
            Assert.Null(d.Contributors);
            Assert.False(d.Evidence.Available);
            Assert.NotEmpty(d.Chain);                                  // the metadata half still ships
            Assert.Contains(d.Chain, n => n.Kind == "column");
            Assert.NotEmpty(d.Lineage);
            Assert.Contains("No live connection", d.Summary);
            Assert.NotNull(d.Evidence.Query);                          // the exact DAX it WOULD run, for transparency
        }

        // Hero 24 D1: a Studio card that rendered again and again asked the live model the same explain about 3,500 times in 13 minutes. The engine answers the
        // same request once, whoever asks and however often, and answers it again after a few seconds or when anything in the request differs.
        [Fact]
        public async Task The_same_explain_asked_in_a_storm_is_answered_once_by_the_engine()
        {
            var e = await OpenAsync();
            Task<ExplainDossier> Ask(ExplainFilterContext ctx) => e.ExplainValueAsync("measure:Date/Days In Current Quarter", ctx, true, null, 5, "human");
            var storm = Enumerable.Range(0, 200).Select(_ => Ask(new ExplainFilterContext { GroupBy = new[] { "'Date'[Year]" } })).ToArray();
            var all = await Task.WhenAll(storm);
            Assert.All(all, d => Assert.Same(all[0], d));
            Assert.Same(all[0], await Ask(new ExplainFilterContext { GroupBy = new[] { "'Date'[Year]" } }));   // a repeat just after: the same answer
            Assert.NotSame(all[0], await Ask(new ExplainFilterContext { GroupBy = new[] { "'Date'[Month]" } }));   // another context is another question
        }

        // Verifier v-fix-hero24: the repeat window must never serve the answer to a DIFFERENT request. An edit to the measure is a new
        // model revision, so an explain asked just after it must describe the measure as it is now, not as it was three seconds ago.
        [Fact]
        public async Task An_explain_asked_just_after_the_measure_was_edited_describes_the_edited_measure()
        {
            var e = await OpenAsync();
            const string m = "measure:Date/Days In Current Quarter";
            var before = await e.ExplainValueAsync(m, null, true, null, 5, "human");
            await e.SetDaxAsync(m, "1 + 41", "human");
            var after = await e.ExplainValueAsync(m, null, true, null, 5, "human");
            Assert.Equal("1 + 41", after.Expression?.Trim());
            Assert.NotSame(before, after);
        }

        [Fact]
        public async Task Bare_measure_name_resolves_when_unique()
        {
            var e = await OpenAsync();
            var d = await e.ExplainValueAsync("Days In Current Quarter", null, false, null, 5, "human");
            Assert.Equal("ok", d.Status);
            Assert.Equal("measure:Date/Days In Current Quarter", d.Measure);
        }

        [Fact]
        public async Task Unknown_measure_teaches_recovery()
        {
            var e = await OpenAsync();
            var d = await e.ExplainValueAsync("measure:Nope/Missing", null, true, null, 5, "human");
            Assert.Equal("error", d.Status);
            Assert.Contains("list_measures", d.Error);
        }

        [Fact]
        public async Task Context_filters_land_in_the_evidence_query()
        {
            var e = await OpenAsync();
            var ctx = new ExplainFilterContext
            {
                Filters = new[] { new ExplainFilter { Column = "'Date'[Calendar Year]", Members = new[] { "2024" } } },
                ExtraPredicates = new[] { "'Date'[Day Of Week] = 1" },
            };
            var d = await e.ExplainValueAsync("measure:Date/Days In Current Quarter", ctx, true, null, 5, "human");
            Assert.Equal("ok", d.Status);
            Assert.Contains("TREATAS ( { 2024 }, 'Date'[Calendar Year] )", d.Evidence.Query);
            Assert.Contains("CALCULATETABLE(", d.Evidence.Query);
            Assert.Same(ctx, d.Context);                               // the context is echoed for provenance
        }

        // ---- Finding A (PR #92 review): the cell must be re-derived in the VISUAL'S evaluation context ----

        [Fact]
        public async Task Value_query_reproduces_the_visuals_group_by_scope()
        {
            // A measure that branches on visual scope (ISINSCOPE / HASONEVALUE on an axis column) returns a
            // DIFFERENT value as a bare scalar under TREATAS than inside the SUMMARIZECOLUMNS group-by the
            // visual ran. The dossier's core promise is "the header value IS the clicked number", so when the
            // caller supplies GroupBy (the visual's axis), the cell query must carry those columns as
            // SUMMARIZECOLUMNS GROUP-BY args (putting them IN SCOPE), with the clicked coordinates pinned by
            // the single-member TREATAS filters — not evaluate a bare scalar and hope scope didn't matter.
            var e = await OpenAsync();
            var ctx = new ExplainFilterContext
            {
                GroupBy = new[] { "'Date'[Calendar Year]" },
                Filters = new[] { new ExplainFilter { Column = "'Date'[Calendar Year]", Members = new[] { "2024" } } },
            };
            var d = await e.ExplainValueAsync("measure:Date/Days In Current Quarter", ctx, false, null, 5, "human");
            Assert.Equal("ok", d.Status);
            // The axis column must appear as a group-by ARG (ORDER BY only ever emits for a non-empty axis),
            // alongside the TREATAS pin — the exact query shape the visual ran.
            Assert.Contains("ORDER BY 'Date'[Calendar Year]", d.Evidence.Query);
            Assert.Contains("TREATAS ( { 2024 }, 'Date'[Calendar Year] )", d.Evidence.Query);
        }

        [Fact]
        public void BuildExplainQuery_carries_axis_and_member_filters_together()
        {
            var q = LocalEngine.BuildExplainQuery("[X]",
                new[] { "'Product'[Category]" },
                new[] { "TREATAS ( { \"Audio\" }, 'Product'[Category] )" },
                Array.Empty<string>());
            var axisAt = q.IndexOf("    'Product'[Category],", StringComparison.Ordinal);
            Assert.True(axisAt >= 0, "the axis column must be a SUMMARIZECOLUMNS group-by arg");
            Assert.Contains("TREATAS ( { \"Audio\" }, 'Product'[Category] )", q);
            Assert.Contains("ORDER BY 'Product'[Category]", q);
        }

        // PickCellValue = the row-selection semantics behind the header value (pure, CI-safe):
        // exactly one row → the cell; zero rows with an axis → blank (the visual would not render the row);
        // >1 rows → ambiguous, refuse with recovery guidance (never silently pick a row).
        private static ResultSet Rs(params object[][] rows) => new ResultSet { Rows = rows, RowCount = rows.Length };

        [Fact]
        public void PickCellValue_single_row_is_the_cell()
        {
            var (value, isBlank, error, _) = LocalEngine.PickCellValue(Rs(new object[] { 2024, 42.0, 1 }), 1);
            Assert.Null(error);
            Assert.False(isBlank);
            Assert.Equal(42.0, value);
        }

        [Fact]
        public void PickCellValue_zero_rows_with_axis_is_an_honest_blank()
        {
            var (value, isBlank, error, note) = LocalEngine.PickCellValue(Rs(), 1);
            Assert.Null(error);
            Assert.True(isBlank);
            Assert.Null(value);
            Assert.Contains("no row", note);
        }

        [Fact]
        public void PickCellValue_multiple_rows_refuses_with_recovery_guidance()
        {
            var (_, _, error, _) = LocalEngine.PickCellValue(Rs(new object[] { 2023, 1.0, 1 }, new object[] { 2024, 2.0, 1 }), 1);
            Assert.NotNull(error);
            Assert.Contains("single-member", error);   // teaches: pin each group-by column to one member
        }

        // UAT 17 Oct: after Query > Explain the footer said the model had unsaved changes and closing asked to save, though the person changed
        // nothing. A read-only analysis keeps its record in SEMANTICUS_HOME, never in the model, and History still lists it. A real edit is unchanged.
        private static async Task WithScratchHomeAsync(Func<string, Task> body)
        {
            var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smx-ev-home-" + Guid.NewGuid().ToString("N"));
            var orig = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            try
            {
                System.IO.Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, System.IO.Path.Combine(home, ".semanticus"));
                await body(home);
            }
            finally
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, orig);
                try { System.IO.Directory.Delete(home, true); } catch { }
            }
        }

        private static string TempBimCopy()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smx-ev-" + Guid.NewGuid().ToString("N"));   // not "semanticus-*": that is the ephemeral marker
            System.IO.Directory.CreateDirectory(dir);
            var bim = System.IO.Path.Combine(dir, "AdventureWorks.bim");
            System.IO.File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        [Fact]
        public async Task An_explained_number_never_marks_the_model_unsaved_and_History_still_lists_it()
        {
            await WithScratchHomeAsync(async _ =>
            {
                var sm = new SessionManager();
                using var e = new LocalEngine(sm, new Fake(false));
                await e.OpenAsync(TempBimCopy());
                Assert.False((await e.SessionInfoAsync()).HasUnsavedEdits);

                for (var i = 0; i < 3; i++)
                    await e.FinishExplainAsync(sm.Current, new ExplainDossier
                    {
                        Status = "ok", Measure = "measure:Date/Days In Current Quarter", Name = "Days In Current Quarter",
                        ValueEvaluated = true, Value = "63", IsBlank = false, Summary = "It is 63.",
                    }, "human");

                Assert.False((await e.SessionInfoAsync()).HasUnsavedEdits, "asking about a number changed nothing, so nothing is unsaved");
                Assert.False(sm.Current.HasUnsavedChanges);
                Assert.Empty((await e.ListVerifiedEditsAsync()).Records);   // nothing was written into the model

                var rows = (await e.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "record" && r.Title == "explain_value").ToArray();
                Assert.Equal(3, rows.Length);
                Assert.All(rows, r => { Assert.Equal(0, r.Revision); Assert.Equal("explained", r.Verified); Assert.NotNull(r.Record); });
                Assert.Equal(3, rows.Select(r => r.Id).Distinct().Count());
            });
        }

        [Fact]
        public async Task A_real_edit_still_marks_the_model_unsaved_and_keeps_its_audit_record_inside_the_model()
        {
            await WithScratchHomeAsync(async _ =>
            {
                var sm = new SessionManager();
                using var e = new LocalEngine(sm, new Fake(true));
                await e.OpenAsync(TempBimCopy());
                await e.FinishExplainAsync(sm.Current, new ExplainDossier { Status = "ok", Measure = "measure:Date/Days In Current Quarter", ValueEvaluated = true, Value = "63", IsBlank = false, Summary = "It is 63." }, "human");
                Assert.False((await e.SessionInfoAsync()).HasUnsavedEdits);

                var mref = (await e.ListMeasuresAsync())[0].Ref;
                await e.SetVerifiedModeAsync(true, "human");
                await e.SetDaxAsync(mref, "1 + 1", "human");

                Assert.True((await e.SessionInfoAsync()).HasUnsavedEdits);
                var rec = Assert.Single((await e.ListVerifiedEditsAsync()).Records);   // the edit's record is in the model; the explain's is not
                Assert.Equal("set_dax", rec.Op);
                Assert.False(string.IsNullOrEmpty(rec.Hash));
            });
        }
    }
}
