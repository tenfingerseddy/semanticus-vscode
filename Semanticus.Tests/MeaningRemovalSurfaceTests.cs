using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

public sealed class MeaningRemovalSurfaceTests
{
    [Fact]
    public void Both_doors_expose_cleanup_and_have_no_recipe_or_old_verdict_api()
    {
        Assert.DoesNotContain(typeof(LocalEngine).Assembly.GetTypes(), t => t.Namespace == "Semanticus.Engine.Checked");
        var retired = new[] { "check_measures", "get_proof", "draft_checked_meaning", "keep_checked_meaning",
            "fix_checked_measure", "set_checked_mode", "write_checked_anyway" };
        var tools = typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>())).Where(x => x.Tool != null).ToArray();
        Assert.All(retired, name => Assert.DoesNotContain(tools, x => x.Tool.Name == name));
        Assert.Single(tools, x => x.Tool.Name == "cleanup_meaning_annotations");
        Assert.DoesNotContain(typeof(IEngine).GetMethods(), m => m.GetParameters().Any(p => p.Name is "meaning" or "meanings"));
        Assert.DoesNotContain(typeof(EngineRpcTarget).GetMethods(), m => m.GetParameters().Any(p => p.Name is "meaning" or "meanings"));
        Assert.DoesNotContain(tools, m => m.Method.GetParameters().Any(p => p.Name is "meaning" or "meanings"));
    }

    [Fact]
    public async Task Checks_page_and_tree_read_without_a_recipe_or_verdict()
    {
        using var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("Contoso", 1600);
        await engine.CreateTableAsync("Sales", "human");
        var reference = await engine.CreateMeasureAsync("table:Sales", "Sales Amount", "1", "human");
        await engine.SetDescriptionAsync(reference, "Contoso sales after discounts.", "human");
        var page = await new EngineRpcTarget(engine).getChecksPage();
        var measure = Assert.Single(page.Measures);
        Assert.Equal(reference, measure.Ref);
        Assert.Equal("Contoso sales after discounts.", measure.Description);
        Assert.Equal(1, page.Summary.Total);
        Assert.Equal("1 measure", page.Summary.Status);
        Assert.Single((await engine.ListTreeAsync("table:Sales")).Where(n => n.Kind == "measure"));
        Assert.DoesNotContain(typeof(ChecksMeasure).GetProperties(), p => p.Name == "Checked");
    }

    [Fact]
    public async Task Checks_page_names_each_measures_table_and_display_folder_for_the_run_plan_ticks()
    {
        using var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("Contoso", 1600);
        await engine.CreateTableAsync("Sales", "human");
        await engine.CreateMeasureAsync("table:Sales", "Sales Tailspin Toys", "1", "human", "Sales\\Brands");
        await engine.CreateMeasureAsync("table:Sales", "Total Quantity", "2", "human");
        var page = await new EngineRpcTarget(engine).getChecksPage();
        var brands = Assert.Single(page.Measures, m => m.Name == "Sales Tailspin Toys");
        Assert.Equal("Sales", brands.Table);
        Assert.Equal("Sales\\Brands", brands.Folder);
        Assert.Equal("", Assert.Single(page.Measures, m => m.Name == "Total Quantity").Folder);
    }
}
