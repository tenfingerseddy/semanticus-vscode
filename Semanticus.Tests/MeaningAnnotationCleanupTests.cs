using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class MeaningAnnotationCleanupTests
{
    private const string Recipe = "Semanticus.Checked.Meaning";
    private const string Words = "Semanticus.Checked.MeaningWords";
    private const string Description = "Contoso sales after discounts. Preserve these words exactly.";

    [Theory]
    [InlineData("bim")]
    [InlineData("tmdl")]
    [InlineData("folder")]
    public async Task Save_removes_both_annotations_from_every_measure_and_undo_restores_them(string format)
    {
        using var model = await Fixture.Open();
        var before = await model.State();
        var steps = (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps;
        var target = Path.Combine(model.Root, format == "bim" ? "clean.bim" : "clean-" + format);
        var saved = await model.Engine.SaveAsync(target, format, false, "human");
        Assert.All(await model.State(), row => {
            Assert.Null(row.Recipe);
            Assert.Null(row.Words);
            Assert.Equal(Description, row.Description);
            Assert.Equal("keep", row.Other);
        });
        Assert.Equal(steps + 1, (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps);
        Assert.Contains("Undo", saved.Note);
        Assert.Contains("Description", saved.Note);
        using (var reopened = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
        {
            await reopened.OpenAsync(saved.Path);
            var script = await reopened.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl");
            Assert.DoesNotContain(Recipe, script);
            Assert.DoesNotContain(Words, script);
            Assert.Contains(Description, script);
        }
        await model.Engine.UndoAsync("human");
        Assert.Equal(before, await model.State());
        await model.Engine.RedoAsync("human");
        Assert.All(await model.State(), row => Assert.Null(row.Recipe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Immediate_cleanup_on_both_doors_is_one_undo_step_and_then_a_no_op(bool rpc)
    {
        using var model = await Fixture.Open();
        var before = await model.State();
        var steps = (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps;
        var result = rpc ? await new EngineRpcTarget(model.Engine).cleanupMeaningAnnotations()
            : await McpTools.CleanupMeaningAnnotations(model.Engine);
        Assert.Equal(2, result.Measures);
        Assert.Equal(4, result.Annotations);
        Assert.Contains("Undo", result.Note);
        Assert.All(await model.State(), row => {
            Assert.Null(row.Recipe);
            Assert.Null(row.Words);
            Assert.Equal(Description, row.Description);
            Assert.Equal("keep", row.Other);
        });
        Assert.Equal(steps + 1, (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps);
        Assert.Equal(0, (await model.Engine.CleanupMeaningAnnotationsAsync("human")).Annotations);
        Assert.Equal(steps + 1, (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps);
        await model.Engine.UndoAsync("human");
        Assert.Equal(before, await model.State());
    }

    [Fact]
    public async Task Save_without_legacy_annotations_adds_no_undo_step()
    {
        using var model = await Fixture.Open();
        await model.Engine.CleanupMeaningAnnotationsAsync("human");
        var steps = (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps;
        await model.Engine.SaveAsync(null, "bim", false, "human");
        Assert.Equal(steps, (await model.Sessions.Current.ReadAsync(_ => model.Sessions.Current.UndoStateNow())).Steps);
        Assert.All(await model.State(), row => Assert.Equal(Description, row.Description));
    }

    [Fact]
    public async Task Cleanup_round_trips_through_the_named_pipe_and_undo_restores_the_annotations()
    {
        using var model = await Fixture.Open();
        var before = await model.State();
        var pipe = TestPipe.Mint("meaning-");
        using var server = new RpcServer(model.Sessions, model.Engine, pipe);
        using var cancellation = new CancellationTokenSource();
        var running = server.RunAsync(cancellation.Token);
        try
        {
            using var remote = await RemoteEngine.ConnectAsync(pipe, timeoutMs: 5000);
            var result = await remote.CleanupMeaningAnnotationsAsync("agent");
            Assert.Equal(4, result.Annotations);
            Assert.All(await model.State(), row => Assert.Null(row.Recipe));
            await remote.UndoAsync("agent");
            Assert.Equal(before, await model.State());
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "meaning-" + Guid.NewGuid().ToString("N")[..8]);
        public SessionManager Sessions { get; } = new();
        public LocalEngine Engine { get; }
        private Fixture() => Engine = new LocalEngine(Sessions, TestEntitlements.Pro);
        public static async Task<Fixture> Open()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            var path = Path.Combine(f.Root, "Contoso.bim");
            await File.WriteAllTextAsync(path, """
                {"name":"Contoso","compatibilityLevel":1600,"model":{"culture":"en-US","tables":[{"name":"Sales","measures":[
                {"name":"Sales Amount","expression":"1","description":"Contoso sales after discounts. Preserve these words exactly.","annotations":[{"name":"Semanticus.Checked.Meaning","value":"{}"},{"name":"Semanticus.Checked.MeaningWords","value":"Contoso sales"},{"name":"Contoso.Other","value":"keep"}]},
                {"name":"Total Cost","expression":"2","description":"Contoso sales after discounts. Preserve these words exactly.","annotations":[{"name":"Semanticus.Checked.Meaning","value":"old malformed recipe"},{"name":"Semanticus.Checked.MeaningWords","value":""},{"name":"Contoso.Other","value":"keep"}]}]}]}}
                """);
            await f.Engine.OpenAsync(path);
            return f;
        }
        public Task<(string Name, string Recipe, string Words, string Description, string Other)[]> State() =>
            Sessions.Current.ReadAsync(m => m.AllMeasures.OrderBy(x => x.Name).Select(x =>
                (x.Name, x.GetAnnotation(Recipe), x.GetAnnotation(Words), x.Description, x.GetAnnotation("Contoso.Other"))).ToArray());
        public void Dispose()
        {
            Engine.Dispose();
            Sessions.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
