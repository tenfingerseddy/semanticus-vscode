using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Checks;
using Xunit;

namespace Semanticus.Tests;

public sealed class PlaceReaderReuseTests
{
    private static ModelStructure Structure => new(new[] {
        new ModelColumn(new Axis("Product", "Category"), ColumnKind.Text),
        new ModelColumn(new Axis("Sales", "Amount"), ColumnKind.Decimal)
    }, new[] { new ModelRelationship(new Axis("Sales", "Product Key"), new Axis("Product", "Product Key"), true, false) },
        "", "", null, 12, "Contoso", false);

    [Fact]
    public async Task Place_runner_returns_numbers_with_typed_filters_without_a_second_calculation()
    {
        string query = null;
        using var connection = LiveConnection.ForMultiTest((dax, _) => {
            query = dax;
            var table = new DataTable();
            table.Columns.Add("__value", typeof(decimal));
            table.Rows.Add(42m);
            return table.CreateDataReader();
        });
        var snapshot = new ModelSnapshot(Structure, new MeasureDefinition("Sales", "Sales Amount", "SUM(Sales[Amount])", ""),
            Array.Empty<MeasureDefinition>(), true, null);
        var place = new Place("total", PlaceKind.GrandTotal, PlaceStatus.Required, "Grand total", Array.Empty<Axis>(),
            new[] { new Slicer("Product", "Category", new[] { new KeyPart(KeyKind.Value, "Contoso") }) });
        var result = await PlaceQueries.ExecuteAsync(connection, snapshot, new[] { place });
        Assert.Null(result.Sets.Single().Result.Error);
        Assert.Equal(42m, Assert.Single(result.Sets.Single().Result.Rows)[0]);
        Assert.Contains("MEASURE 'Sales'[Sales Amount] = SUM(Sales[Amount])", query);
        Assert.Contains("TREATAS({\"Contoso\"}, 'Product'[Category])", query);
        Assert.DoesNotContain("__expected", query);
    }

    [Fact]
    public async Task Member_reader_keeps_blank_members_and_counts_from_the_related_fact()
    {
        string query = null;
        using var connection = LiveConnection.ForMultiTest((dax, _) => {
            query = dax;
            var table = new DataTable();
            table.Columns.Add("__member", typeof(string)); table.Columns.Add("__rows", typeof(long));
            table.Rows.Add("Contoso", 8L); table.Rows.Add(DBNull.Value, 2L);
            return table.CreateDataReader();
        });
        var read = await MemberReader.ReadAsync(connection, Structure, new Axis("Product", "Category"), "Sales");
        Assert.True(read.Complete);
        Assert.Equal(new MemberSample(new KeyPart(KeyKind.Value, "Contoso"), 8), read.Members[0]);
        Assert.Equal(KeyKind.Blank, read.Members[1].Key.Kind);
        Assert.Contains("COUNTROWS('Sales')", query);
        Assert.True(MemberReader.HasFilterPath(Structure, "Product", "Sales"));
        Assert.False(MemberReader.HasFilterPath(Structure, "Sales", "Product"));
    }

    [Fact]
    public void Literal_member_reader_keeps_the_members_named_in_the_formula()
    {
        var axis = new Axis("Product", "Category");
        var snapshot = new ModelSnapshot(Structure, new MeasureDefinition("Sales", "Contoso Sales",
            "CALCULATE(SUM(Sales[Amount]), Product[Category] = \"Contoso\")", ""),
            Array.Empty<MeasureDefinition>(), true, null);
        var named = Assert.Single(NamedMemberReader.For(snapshot, new[] { axis }));
        Assert.Equal(axis, named.Axis);
        Assert.Equal(new KeyPart(KeyKind.Value, "Contoso"), Assert.Single(named.Keys));
    }

    [Fact]
    public void Member_cache_keeps_rows_detached_and_separates_refreshes()
    {
        var cache = new MemberReadCache();
        var key = new MemberReadKey("Contoso query", "Contoso shape", "local", "refresh-1");
        var input = new ResultSet { Rows = new[] { new object[] { "Contoso", 8L } } };
        cache.Put(key, input);
        input.Rows[0][0] = "changed";
        Assert.True(cache.TryGet(key, out var first));
        Assert.Equal("Contoso", first.Rows[0][0]);
        first.Rows[0][0] = "changed again";
        Assert.True(cache.TryGet(key, out var second));
        Assert.Equal("Contoso", second.Rows[0][0]);
        Assert.False(cache.TryGet(key with { DataAsOf = "refresh-2" }, out _));
        Assert.False(cache.TryGet(key with { DataAsOf = "" }, out _));
    }
}
