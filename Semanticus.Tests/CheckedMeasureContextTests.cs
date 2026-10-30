using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

public sealed class CheckedMeasureContextTests
{
    private const string Tailspin = "CALCULATE(SUMX(Sales, Sales[Quantity] * Sales[Net Price]), Product[Manufacturer] = \"Tailspin Toys\")";
    private static ContextColumn Col(string table, string name, bool numeric = false, bool key = false, bool time = false)
        => new(table, name, numeric, Key: key, Time: time);

    internal static ContextModel Contoso()
    {
        var columns = new[] {
            Col("Sales", "Quantity", true), Col("Sales", "Net Price", true),
            Col("Sales", "ProductKey", true, true), Col("Sales", "CustomerKey", true, true),
            Col("Sales", "StoreKey", true, true), Col("Sales", "DateKey", true, true),
            Col("Product", "ProductKey", true, true), Col("Product", "Manufacturer"), Col("Product", "Brand"), Col("Product", "Category"),
            Col("Customer", "CustomerKey", true, true), Col("Customer", "Continent"),
            Col("Store", "StoreKey", true, true), Col("Store", "Country"),
            Col("Date", "DateKey", true, true, true), Col("Date", "Date", time: true), Col("Date", "Year", true, time: true)
        };
        ContextColumn Find(string table, string name) => columns.Single(c => c.Table == table && c.Name == name);
        return new(columns, new[] {
            new ContextRelationship(Find("Sales", "ProductKey"), Find("Product", "ProductKey")),
            new ContextRelationship(Find("Sales", "CustomerKey"), Find("Customer", "CustomerKey")),
            new ContextRelationship(Find("Sales", "StoreKey"), Find("Store", "StoreKey")),
            new ContextRelationship(Find("Sales", "DateKey"), Find("Date", "DateKey"))
        }, new[] { new ContextMeasure("Sales", "Amount", "SUMX(Sales, Sales[Quantity] * Sales[Net Price])") }, new[] { "Date" });
    }

    private static MeasureContextPlan Plan(string formula, ContextModel? model = null)
        => new MeasureContextPlanner().Plan(new(model ?? Contoso(), new("Sales", "Sales Tailspin Toys", formula, "Sales for Tailspin Toys.")));

    [Fact]
    public void Tailspin_has_exactly_the_required_eight_contexts_and_plain_reasons()
    {
        var plan = Plan(Tailspin);
        Assert.Equal(new[] {
            "slice:Product/Manufacturer:single", "slice:Product/Manufacturer:several",
            "group:Product/Brand", "group:Product/Category", "group:Customer/Continent", "group:Store/Country",
            "total", "subtotal:Product/Brand"
        }, plan.Contexts.Select(c => c.Id));
        Assert.All(plan.Contexts, c => {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason));
            Assert.False(string.IsNullOrWhiteSpace(c.ExpectedBehaviour));
            Assert.DoesNotContain('\n', c.Reason);
        });
        Assert.Contains("overwrite", plan.Contexts[0].ExpectedBehaviour, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Customer" && c.ExpectedBehaviour.Contains("split"));
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Store" && c.ExpectedBehaviour.Contains("split"));
    }

    public static IEnumerable<object[]> ModifierFamilies()
    {
        yield return new object[] { "CALCULATE", "CALCULATE([Amount], Product[Brand] = \"Contoso\")", "Product", "Brand", "overwrite" };
        yield return new object[] { "CALCULATETABLE", "SUMX(CALCULATETABLE(Sales, Product[Brand] = \"Contoso\"), Sales[Quantity])", "Product", "Brand", "overwrite" };
        yield return new object[] { "ALL", "CALCULATE([Amount], ALL(Product[Brand]))", "Product", "Brand", "clear" };
        yield return new object[] { "ALLEXCEPT", "CALCULATE([Amount], ALLEXCEPT(Product, Product[Brand]))", "Product", "Brand", "keep" };
        yield return new object[] { "ALLSELECTED", "CALCULATE([Amount], ALLSELECTED(Product[Brand]))", "Product", "Brand", "outside" };
        yield return new object[] { "REMOVEFILTERS", "CALCULATE([Amount], REMOVEFILTERS(Product[Brand]))", "Product", "Brand", "clear" };
        yield return new object[] { "KEEPFILTERS", "CALCULATE([Amount], KEEPFILTERS(Product[Brand] = \"Contoso\"))", "Product", "Brand", "intersect" };
        yield return new object[] { "VALUES", "COUNTROWS(VALUES(Product[Brand]))", "Product", "Brand", "visible" };
        yield return new object[] { "FILTER", "SUMX(FILTER(Product, Product[Brand] = \"Contoso\"), [Amount])", "Product", "Brand", "rows" };
        yield return new object[] { "USERELATIONSHIP", "CALCULATE([Amount], USERELATIONSHIP(Sales[StoreKey], Store[StoreKey]))", "Store", "StoreKey", "relationship" };
        yield return new object[] { "CROSSFILTER", "CALCULATE([Amount], CROSSFILTER(Sales[StoreKey], Store[StoreKey], BOTH))", "Store", "StoreKey", "direction" };
        yield return new object[] { "TREATAS", "CALCULATE([Amount], TREATAS(VALUES(Customer[Continent]), Store[Country]))", "Store", "Country", "transfer" };
        yield return new object[] { "TIME", "TOTALYTD([Amount], 'Date'[Date])", "Date", "Date", "period" };
        yield return new object[] { "ITERATOR", "SUMX(Product, [Amount])", "Product", "Brand", "context transition" };
    }

    [Theory]
    [MemberData(nameof(ModifierFamilies))]
    public void Each_modifier_family_finds_its_target_and_expected_behaviour(string family, string formula, string table, string column, string expected)
    {
        var plan = Plan(formula);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == table && c.Column.Name == column
            && c.Families.Contains(family) && c.ExpectedBehaviour.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_time_logic_drops_year_even_when_it_is_named_or_in_a_string_or_comment()
    {
        foreach (var formula in new[] { Tailspin, "SUM(Sales[Quantity])", "CALCULATE([Amount], Product[Brand] = \"Contoso\")", "IF(TRUE(), [Amount], \"TOTALYTD Date Year\") /* SAMEPERIODLASTYEAR('Date'[Date]) */" })
        {
            var contexts = Plan(formula).Contexts;
            Assert.NotEmpty(contexts);
            Assert.DoesNotContain(contexts, c => c.Column?.Table == "Date" || c.Column?.Time == true);
        }
    }

    [Fact]
    public void Measure_references_follow_transitive_formulas_and_stop_cycles()
    {
        var model = Contoso();
        model = model with { Measures = model.Measures.Concat(new[] {
            new ContextMeasure("Sales", "Tailspin", Tailspin),
            new ContextMeasure("Sales", "Nested", "[Tailspin] + [Cycle]"),
            new ContextMeasure("Sales", "Cycle", "[Nested]")
        }).ToArray() };
        var plan = Plan("'Sales'[Nested]", model);
        Assert.Contains(plan.Contexts, c => c.Column?.Name == "Manufacturer" && c.Families.Contains("CALCULATE"));
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Date");
    }

    [Fact]
    public void Time_logic_in_a_dependency_keeps_the_marked_date_table()
    {
        var model = Contoso();
        model = model with { Measures = model.Measures.Append(new("Sales", "YTD", "TOTALYTD([Amount], 'Date'[Date])")).ToArray() };
        Assert.Contains(Plan("[YTD]", model).Contexts, c => c.Column?.Name == "Year" && c.Column.Table == "Date");
    }

    [Theory]
    [InlineData("CALCULATE([Amount], 'Date'[Year] = 2026)")]
    [InlineData("CALCULATE([Amount], REMOVEFILTERS('Date'))")]
    [InlineData("CALCULATE([Amount], USERELATIONSHIP(Sales[DateKey], 'Date'[DateKey]))")]
    public void Explicit_date_filters_and_date_relationships_are_time_logic(string formula)
    {
        var plan = Plan(formula);
        Assert.True(plan.UsesTimeLogic);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Date");
    }

    [Fact]
    public void Cap_is_fifteen_and_controls_and_totals_survive_a_wide_formula()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Concat(Enumerable.Range(0, 30).Select(i => Col("Product", "Contoso " + i)))
            .Concat(new[] { Col("Currency", "Code"), Col("Channel", "Name") }).ToArray() };
        var formula = "CALCULATE([Amount], " + string.Join(",", model.Columns.Where(c => c.Table == "Product" && !c.Key).Select(c => "Product[" + c.Name + "] = \"Contoso\"")) + ")";
        var plan = Plan(formula, model);
        Assert.Equal(15, plan.Contexts.Count);
        Assert.Contains(plan.Contexts, c => c.Id == "total");
        Assert.Contains(plan.Contexts, c => c.Shape == ContextShape.Subtotal);
        Assert.Equal(2, plan.Contexts.Count(c => c.Role == ContextRole.Control));
        Assert.Contains(plan.Contexts, c => c.Role == ContextRole.Related && c.Column?.Table == "Customer");
        Assert.Contains(plan.Contexts, c => c.Role == ContextRole.Related && c.Column?.Table == "Store");
        Assert.Equal(15, plan.Contexts.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Tiny_models_use_only_available_contexts_without_duplicates()
    {
        var model = new ContextModel(new[] { Col("Sales", "Quantity", true) }, Array.Empty<ContextRelationship>(), Array.Empty<ContextMeasure>(), Array.Empty<string>());
        var plan = Plan("SUM(Sales[Quantity])", model);
        Assert.InRange(plan.Contexts.Count, 2, 7);
        Assert.Equal(plan.Contexts.Count, plan.Contexts.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Relationship_paths_cover_direction_inactive_many_to_many_and_orphan_blanks()
    {
        var model = Contoso();
        model = model with { Relationships = model.Relationships.Select(r => r.To.Table == "Store" ? r with { Active = false, ManyToMany = true, TwoWay = true } : r).ToArray() };
        var plan = Plan(Tailspin, model);
        var store = Assert.Single(plan.Contexts.Where(c => c.Column?.Table == "Store" && c.Role == ContextRole.Related));
        Assert.NotEmpty(store.RelationshipPath);
        Assert.Contains("inactive", store.ExpectedBehaviour);
        Assert.Contains("many-to-many", store.Reason);
        Assert.Contains("blank", store.Reason);
        Assert.Contains("both", store.Reason);
    }

    private sealed class RecordingOrderer : IContextOrderer
    {
        public ContextOrderRequest? Seen;
        public IReadOnlyList<ContextCandidate> Order(ContextOrderRequest request)
        {
            Seen = request;
            return request.Candidates.Take(8).ToArray();
        }
    }

    [Fact]
    public void Ordering_seam_receives_intent_and_formula_and_marks_share_exactly_its_list()
    {
        var orderer = new RecordingOrderer();
        IMeasureContextPlanner planner = new MeasureContextPlanner(orderer);
        var measure = new ContextMeasure("Sales", "Sales Tailspin Toys", Tailspin, "Sales for Tailspin Toys.");
        var plan = planner.Plan(new(Contoso(), measure));
        Assert.NotNull(orderer.Seen);
        Assert.Equal(measure.Name, orderer.Seen.Name);
        Assert.Equal(measure.Formula, orderer.Seen.Formula);
        Assert.Equal(measure.Description, orderer.Seen.Description);
        Assert.NotEmpty(orderer.Seen.Candidates);
        foreach (var mark in new[] { "Filters", "Contexts", "Totals" })
            Assert.Same(plan.Contexts, plan.ForMark(mark));
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("REMOVEFILTERS")]
    [InlineData("ALLSELECTED")]
    public void Table_modifiers_include_the_expanded_one_side_dimensions(string family)
    {
        var plan = Plan("CALCULATE([Amount], " + family + "(Sales))");
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Product" && c.Families.Contains(family));
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Customer" && c.Families.Contains(family));
    }

    [Fact]
    public void Context_transition_over_a_fact_table_has_an_iterator_context()
    {
        var plan = Plan("SUMX(Sales, [Amount])");
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Sales" && c.Families.Contains("ITERATOR")
            && c.ExpectedBehaviour.Contains("context transition"));
    }

    [Theory]
    [InlineData("GEOMEANX(Product, [Amount])")]
    [InlineData("SELECTCOLUMNS(Product, \"Contoso\", [Amount])")]
    [InlineData("TOPN(5, Product, [Amount], DESC)")]
    [InlineData("SAMPLE(5, Product, [Amount], ASC)")]
    [InlineData("LINESTX(Product, [Amount], 1)")]
    public void Other_iterators_find_the_row_table_and_measure_transition(string formula)
    {
        Assert.Contains(Plan(formula).Contexts, c => c.Column?.Table == "Product" && c.Families.Contains("ITERATOR")
            && c.ExpectedBehaviour.Contains("context transition"));
    }

    [Fact]
    public void Raw_iterator_has_row_context_without_claiming_context_transition()
    {
        var plan = Plan("SUMX(Sales, Sales[Quantity] * Sales[Net Price])");
        Assert.Contains(plan.Contexts, c => c.Families.Contains("ITERATOR") && c.ExpectedBehaviour.Contains("row context"));
        Assert.DoesNotContain(plan.Contexts, c => c.ExpectedBehaviour.Contains("context transition"));
    }

    [Fact]
    public void Iterator_does_not_transition_a_measure_value_saved_before_the_iterator()
    {
        var plan = Plan("VAR Saved = [Amount] RETURN SUMX(Product, Saved)");
        Assert.DoesNotContain(plan.Contexts, c => c.ExpectedBehaviour.Contains("context transition"));
    }

    [Fact]
    public void A_qualified_column_with_a_measure_name_does_not_cause_context_transition()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Append(Col("Product", "Amount", true)).ToArray() };
        Assert.DoesNotContain(Plan("SUMX(Product, Product[Amount])", model).Contexts,
            c => c.ExpectedBehaviour.Contains("context transition"));
    }

    [Fact]
    public void A_non_date_lastnonblank_does_not_introduce_year()
    {
        var plan = Plan("LASTNONBLANK(Product[Brand], 1)");
        Assert.False(plan.UsesTimeLogic);
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Date");
    }

    [Fact]
    public void Numeric_models_reach_eight_when_the_available_columns_allow_it()
    {
        var model = new ContextModel(new[] { Col("Sales", "Quantity", true), Col("Sales", "Net Price", true) },
            Array.Empty<ContextRelationship>(), Array.Empty<ContextMeasure>(), Array.Empty<string>());
        Assert.Equal(8, Plan("SUM(Sales[Quantity])", model).Contexts.Count);
    }

    [Theory]
    [InlineData("COUNTROWS(Sales)", "")]
    [InlineData("SUM(Sales[Key A])", "")]
    [InlineData("COUNTROWS(FILTER(Sales, TRUE()))", "FILTER")]
    [InlineData("CALCULATE(COUNTROWS(Sales), REMOVEFILTERS())", "REMOVEFILTERS")]
    [InlineData("CALCULATE(COUNTROWS(Sales), ALLEXCEPT(Sales, Sales[Key A]))", "ALLEXCEPT")]
    [InlineData("SUMX(Sales, Sales[Key A])", "ITERATOR")]
    public void Visible_key_columns_supply_eight_contexts_when_the_model_allows_it(string formula, string family)
    {
        var model = new ContextModel(new[] { Col("Sales", "Key A", true, true), Col("Sales", "Key B", true, true) },
            Array.Empty<ContextRelationship>(), Array.Empty<ContextMeasure>(), Array.Empty<string>());
        var plan = Plan(formula, model);
        Assert.Equal(8, plan.Contexts.Count);
        if (family.Length > 0) Assert.Contains(plan.Contexts, c => c.Families.Contains(family));
    }

    [Fact]
    public void Constant_measures_can_use_visible_key_only_dimensions_as_controls()
    {
        var model = new ContextModel(new[] { Col("Product", "ProductKey", true, true), Col("Customer", "CustomerKey", true, true) },
            Array.Empty<ContextRelationship>(), Array.Empty<ContextMeasure>(), Array.Empty<string>());
        var plan = Plan("1", model);
        Assert.Equal(8, plan.Contexts.Count);
        Assert.Equal(2, plan.Contexts.Count(c => c.Role == ContextRole.Control));
    }

    [Fact]
    public void Time_functions_keep_the_visible_marked_date_key_as_a_context()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Select(c => c.Table == "Date" && c.Name == "Date" ? c with { Key = true } : c).ToArray() };
        Assert.Contains(Plan("TOTALYTD([Amount], 'Date'[Date])", model).Contexts,
            c => c.Column?.Table == "Date" && c.Column.Name == "Date" && c.Families.Contains("TIME"));
    }

    [Fact]
    public void No_time_logic_also_drops_the_foreign_key_to_the_marked_date_table()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Select(c => c.Table == "Sales" && c.Name == "DateKey" ? c with { Key = false } : c).ToArray() };
        var plan = Plan("SUMX(Sales, Sales[Quantity] * Sales[Net Price])", model);
        Assert.False(plan.UsesTimeLogic);
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Sales" && c.Column.Name == "DateKey");
    }

    [Fact]
    public void Time_logic_on_a_fact_date_still_includes_the_marked_date_table()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Append(Col("Sales", "Order Date", time: true)).ToArray() };
        var plan = Plan("TOTALYTD([Amount], Sales[Order Date])", model);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Date" && c.Column.Name == "Year" && c.Families.Contains("TIME"));
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Sales" && c.Column.Name == "Quantity" && c.Role == ContextRole.Time);
    }

    [Fact]
    public void Unqualified_columns_bind_to_the_iterator_row_table()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Append(Col("Sales", "Brand")).ToArray() };
        var plan = Plan("COUNTROWS(FILTER(Product, [Brand] = \"Contoso\"))", model);
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Sales" && c.Families.Contains("FILTER"));
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Product" && c.Column.Name == "Brand" && c.Families.Contains("FILTER"));
    }

    [Fact]
    public void Hidden_relationship_keys_still_test_a_visible_dimension_column()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Select(c => c.Key ? c with { Hidden = true } : c).ToArray() };
        var plan = Plan("CALCULATE([Amount], USERELATIONSHIP(Sales[StoreKey], Store[StoreKey]))", model);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Store" && c.Column.Name == "Country"
            && c.Families.Contains("USERELATIONSHIP"));
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Hidden == true);
    }

    [Fact]
    public void Crossfilter_none_expects_the_dimension_filter_to_stop_at_the_relationship()
    {
        var plan = Plan("CALCULATE([Amount], CROSSFILTER(Sales[StoreKey], Store[StoreKey], NONE))");
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Store" && c.Column.Name == "Country"
            && c.ExpectedBehaviour.Contains("unchanged"));
    }

    [Fact]
    public void A_reversed_path_only_splits_when_both_directions_are_enabled()
    {
        var model = Contoso();
        var quantity = model.Columns.Single(c => c.Table == "Sales" && c.Name == "Quantity");
        var product = model.Columns.Single(c => c.Table == "Product" && c.Name == "ProductKey");
        var sales = model.Columns.Single(c => c.Table == "Sales" && c.Name == "ProductKey");
        model = model with { Relationships = new[] { new ContextRelationship(product, sales) },
            Columns = model.Columns.Where(c => c.Table == "Product" || c == quantity || c == sales).ToArray() };
        var oneWay = Plan("SUM(Sales[Quantity])", model);
        Assert.Contains(oneWay.Contexts, c => c.Column?.Table == "Product" && c.ExpectedBehaviour.Contains("unchanged"));
        var both = Plan("SUM(Sales[Quantity])", model with { Relationships = new[] { new ContextRelationship(product, sales, TwoWay: true) } });
        Assert.Contains(both.Contexts, c => c.Column?.Table == "Product" && c.ExpectedBehaviour.Contains("split"));
    }

    [Fact]
    public void No_argument_clear_modifier_finds_filters_on_numeric_only_models()
    {
        var model = new ContextModel(new[] { Col("Sales", "Quantity", true) }, Array.Empty<ContextRelationship>(), Array.Empty<ContextMeasure>(), Array.Empty<string>());
        var plan = Plan("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS())", model);
        Assert.Contains(plan.Contexts, c => c.Column?.Name == "Quantity" && c.Families.Contains("REMOVEFILTERS"));
    }

    [Fact]
    public void Parser_handles_escaped_names_comments_and_table_variables()
    {
        var model = Contoso();
        model = model with { Columns = model.Columns.Append(Col("Contoso's Product", "Brand]Label")).ToArray() };
        var plan = Plan("VAR Items = 'Contoso''s Product' RETURN SUMX(FILTER(Items, 'Contoso''s Product'[Brand]]Label] = \"Contoso\"), 1) /* ALL('Date') */", model);
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Contoso's Product" && c.Column.Name == "Brand]Label" && c.Families.Contains("FILTER"));
        Assert.False(plan.UsesTimeLogic);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void Parser_errors_are_returned_with_the_plan()
    {
        var plan = Plan("CALCULATE([Amount], Product[Manufacturer] =");
        Assert.NotEmpty(plan.Diagnostics);
        Assert.Contains(plan.Contexts, c => c.Id == "total");
    }

    [Fact]
    public async Task Model_thread_capture_preserves_the_formula_description_and_date_metadata()
    {
        using var fixture = new DecisionFixture();
        await fixture.Engine.CreateModelAsync("Contoso", 1604);
        foreach (var table in new[] { "Sales", "Product", "Customer", "Store", "Date" })
            await fixture.Engine.CreateTableAsync(table, "human");
        foreach (var column in Contoso().Columns)
            await fixture.Engine.CreateColumnAsync("table:" + column.Table, column.Name,
                column.Name == "Date" ? "DateTime" : column.Numeric ? "Int64" : "String", null, "human");
        await fixture.Engine.MarkDateTableAsync("table:Date", "Date", "human");
        await fixture.Engine.CreateMeasureAsync("table:Sales", "Sales Tailspin Toys", Tailspin, "human");
        await fixture.Engine.SetDescriptionAsync("measure:Sales/Sales Tailspin Toys", "Sales for Tailspin Toys.", "human");
        var captured = await fixture.Sessions.Require().ReadAsync(ContextModelCapture.Capture);
        var measure = Assert.Single(captured.Measures);
        Assert.Equal(Tailspin, measure.Formula);
        Assert.Equal("Sales for Tailspin Toys.", measure.Description);
        Assert.Contains("Date", captured.DateTables);
        Assert.All(captured.Columns.Where(c => c.Table == "Date"), c => Assert.True(c.Time));
        var plan = new MeasureContextPlanner().Plan(new(captured, measure));
        Assert.Contains(plan.Contexts, c => c.Column?.Name == "Manufacturer");
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Date");
    }
}
