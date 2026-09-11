using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests;

public sealed class WorkflowLayoutTests : IDisposable
{
    private sealed class Free : IEntitlement
    {
        public bool IsPro => false;
        public EntitlementInfo Info => new() { Tier = "free" };
    }
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "smx-wflayout-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _sessions = new();
    private readonly LocalEngine _engine;
    private const string Markdown = """
        ---
        schemaVersion: 2
        name: layout-test
        title: Layout test
        ---
        ## Step 1: First
        Read this step.
        ```yaml step
        id: first
        ```
        ## Step 2: Second
        Read the next step.
        """;

    public WorkflowLayoutTests()
    {
        Directory.CreateDirectory(_workspace);
        _engine = new LocalEngine(_sessions, new Free(), _workspace);
    }
    private string FileFor(string name = "layout-test") => Path.Combine(_workspace, ".semanticus", "workflow-layouts", name + ".json");
    private static Dictionary<string, WorkflowPosition> Positions(string id = "first", double x = 123) =>
        new() { [id] = new() { X = x, Y = -45.5 } };
    private Task Create() => _engine.SaveWorkflowAsync("layout-test", Markdown, "human");

    [Fact]
    public async Task Shared_layout_roundtrips_broadcasts_and_never_changes_model_revision()
    {
        await Create();
        var model = await _sessions.CreateAsync("layout-test", 1604);
        var revision = model.Revision;
        WorkflowLayout broadcast = null;
        _sessions.Bus.WorkflowLayoutChanged += value => broadcast = value;
        var initial = await McpTools.GetWorkflowLayout(_engine, "layout-test");
        Assert.Empty(initial.Positions);
        var positions = Positions(); positions["deleted"] = new() { X = 1, Y = 2 };
        var saved = await McpTools.SaveWorkflowLayout(_engine, "layout-test", positions, initial.Revision);
        Assert.NotEqual(initial.Revision, saved.Revision);
        Assert.Equal(saved.Revision, broadcast.Revision);
        Assert.Single(saved.Positions);
        Assert.Equal(123, (await _engine.GetWorkflowLayoutAsync("layout-test")).Positions["first"].X);
        Assert.Equal(revision, model.Revision);
        Assert.True(File.Exists(FileFor()));
        var reset = await _engine.SaveWorkflowLayoutAsync("layout-test", new(), saved.Revision);
        Assert.Empty(reset.Positions);
        Assert.Empty((await _engine.GetWorkflowLayoutAsync("layout-test")).Positions);
    }

    [Fact]
    public async Task Stock_override_does_not_shadow_content_and_layouts_are_isolated()
    {
        await Create();
        var stock = await _engine.GetWorkflowAsync("new-measure");
        var original = File.ReadAllBytes(stock.FilePath);
        await _engine.SaveWorkflowLayoutAsync(stock.Name, Positions(stock.Steps[0].Id));
        Assert.Equal("stock", (await _engine.GetWorkflowAsync(stock.Name)).Source);
        Assert.Equal(original, File.ReadAllBytes(stock.FilePath));
        Assert.False(File.Exists(Path.Combine(_workspace, ".semanticus", "workflows", stock.Name + ".md")));
        Assert.Empty((await _engine.GetWorkflowLayoutAsync("layout-test")).Positions);
        Assert.Single((await _engine.GetWorkflowLayoutAsync(stock.Name)).Positions);
    }

    [Fact]
    public async Task Saved_models_use_their_own_sidecar_and_return_to_workspace_fallback_when_unsaved()
    {
        const string name = "new-measure";
        var stock = await _engine.GetWorkflowAsync(name);
        await _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 11));
        await _sessions.CreateAsync("saved-layout", 1604);
        var modelDir = Path.Combine(_workspace, "saved-model");
        await _engine.SaveAsync(modelDir, "TMDL");
        Assert.Empty((await _engine.GetWorkflowLayoutAsync(name)).Positions);
        await _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 22));
        Assert.True(File.Exists(Path.Combine(modelDir, ".semanticus", "workflow-layouts", name + ".json")));
        await _sessions.CreateAsync("unsaved-layout", 1604);
        Assert.Equal(11, (await _engine.GetWorkflowLayoutAsync(name)).Positions[stock.Steps[0].Id].X);
    }

    [Fact]
    public async Task Concurrent_writes_with_one_revision_have_exactly_one_winner()
    {
        await Create();
        var revision = (await _engine.GetWorkflowLayoutAsync("layout-test")).Revision;
        async Task<bool> Save(double x)
        {
            try { await _engine.SaveWorkflowLayoutAsync("layout-test", Positions(x: x), revision); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Save(1)), Task.Run(() => Save(2)));
        Assert.Single(Array.FindAll(results, won => won));
    }

    [Fact]
    public async Task Queued_layout_operations_keep_their_location_across_same_session_save_as()
    {
        const string name = "new-measure";
        var stock = await _engine.GetWorkflowAsync(name);
        await _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 11));
        await _sessions.CreateAsync("queued-layout", 1604);
        var context = _sessions.CurrentContext;
        var broadcasts = 0;
        _sessions.Bus.WorkflowLayoutChanged += _ => broadcasts++;
        await context.WorkflowGate.WaitAsync();
        Task<WorkflowLayout> read, save;
        var modelDir = Path.Combine(_workspace, "saved-while-queued");
        try
        {
            read = _engine.GetWorkflowLayoutAsync(name);
            save = _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 22));
            Assert.False(read.IsCompleted);
            Assert.False(save.IsCompleted);
            await _engine.SaveAsync(modelDir, "TMDL");
        }
        finally { context.WorkflowGate.Release(); }
        Assert.Equal(11, (await read).Positions[stock.Steps[0].Id].X);
        Assert.Equal(22, (await save).Positions[stock.Steps[0].Id].X);
        Assert.Equal(0, broadcasts);
        Assert.False(File.Exists(Path.Combine(modelDir, ".semanticus", "workflow-layouts", name + ".json")));
        Assert.Empty((await _engine.GetWorkflowLayoutAsync(name)).Positions);
        await _sessions.CreateAsync("back-at-workspace", 1604);
        Assert.Equal(22, (await _engine.GetWorkflowLayoutAsync(name)).Positions[stock.Steps[0].Id].X);
    }

    [Fact]
    public async Task Revision_from_another_project_is_refused_even_when_definition_and_layout_bytes_match()
    {
        const string name = "new-measure";
        var stock = await _engine.GetWorkflowAsync(name);
        await _sessions.CreateAsync("project-layout", 1604);
        var projectA = Path.Combine(_workspace, "project-a");
        var projectB = Path.Combine(_workspace, "project-b");
        await _engine.SaveAsync(projectA, "TMDL");
        await _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 11));
        var readA = await _engine.GetWorkflowLayoutAsync(name);
        var fileA = Path.Combine(projectA, ".semanticus", "workflow-layouts", name + ".json");
        await _engine.SaveAsync(projectB, "TMDL");
        var fileB = Path.Combine(projectB, ".semanticus", "workflow-layouts", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(fileB));
        File.Copy(fileA, fileB, overwrite: true);
        var before = File.ReadAllBytes(fileB);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 22), readA.Revision));
        Assert.Equal(before, File.ReadAllBytes(fileB));
        var readB = await _engine.GetWorkflowLayoutAsync(name);
        Assert.NotEqual(readA.Revision, readB.Revision);
        // The optional revision remains optional for callers intentionally replacing the current layout.
        await _engine.SaveWorkflowLayoutAsync(name, Positions(stock.Steps[0].Id, 33));
        Assert.Equal(33, (await _engine.GetWorkflowLayoutAsync(name)).Positions[stock.Steps[0].Id].X);
    }

    [Fact]
    public async Task Revision_fences_both_layout_changes_and_exact_document_edits()
    {
        await Create();
        var initial = await _engine.GetWorkflowLayoutAsync("layout-test");
        var saved = await _engine.SaveWorkflowLayoutAsync("layout-test", Positions(), initial.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", new(), initial.Revision));
        // A comment in YAML changes the document without changing the parsed definition.
        await _engine.SaveWorkflowAsync("layout-test", Markdown.Replace("title: Layout test", "title: Layout test # edited"), "human");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", new(), saved.Revision));
    }

    [Fact]
    public async Task Deleted_step_ids_are_pruned_on_read_and_cannot_be_restored_by_a_stale_save()
    {
        await Create();
        var positions = Positions(); positions["step-2"] = new() { X = 500, Y = 30 };
        var saved = await _engine.SaveWorkflowLayoutAsync("layout-test", positions);
        await _engine.SaveWorkflowAsync("layout-test", Markdown[..Markdown.IndexOf("## Step 2", StringComparison.Ordinal)], "human");
        var pruned = await _engine.GetWorkflowLayoutAsync("layout-test");
        Assert.Equal("first", Assert.Single(pruned.Positions).Key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", positions, saved.Revision));
    }

    [Fact]
    public async Task Invalid_names_and_nonfinite_or_missing_coordinates_are_refused_without_writes()
    {
        await Create();
        foreach (var name in new[] { "../layout-test", "Layout-Test", "layout-test/child", "layout-test" + (char)10, "", null })
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.GetWorkflowLayoutAsync(name));
        foreach (var x in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", Positions(x: x)));
        await Assert.ThrowsAsync<ArgumentException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", new() { ["first"] = new() }));
        await Assert.ThrowsAsync<ArgumentException>(() => _engine.SaveWorkflowLayoutAsync("layout-test", null));
        Assert.False(File.Exists(FileFor()));
    }

    [Fact]
    public async Task Corrupt_sidecar_can_be_explicitly_reset_but_not_silently_overwritten()
    {
        await Create(); Directory.CreateDirectory(Path.GetDirectoryName(FileFor()));
        File.WriteAllText(FileFor(), "broken JSON");
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.GetWorkflowLayoutAsync("layout-test"));
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.SaveWorkflowLayoutAsync("layout-test", Positions()));
        Assert.Empty((await _engine.SaveWorkflowLayoutAsync("layout-test", new())).Positions);
        Assert.Empty((await _engine.GetWorkflowLayoutAsync("layout-test")).Positions);
    }

    [Fact]
    public async Task Remote_agent_and_ui_rpc_share_positions_and_deliver_the_precise_notification()
    {
        await Create();
        var pipeName = "smx-layout-" + Guid.NewGuid().ToString("N");
        const string challenge = "workflow-layout-ui-challenge-0123456789abcdef";
        using var server = new RpcServer(_sessions, _engine, pipeName, challenge);
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, challenge);
            await RpcHandshake.ReadAcceptedAsync(pipe);
            using var ui = new JsonRpc(RpcServer.CreateHandler(pipe));
            var notification = new TaskCompletionSource<WorkflowLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.AddLocalRpcMethod("workflow/layoutDidChange", new Action<WorkflowLayout>(v => notification.TrySetResult(v)));
            ui.StartListening();
            using var agent = await RemoteEngine.ConnectAsync(pipeName);
            var initial = await agent.GetWorkflowLayoutAsync("layout-test");
            var saved = await ui.InvokeAsync<WorkflowLayout>("saveWorkflowLayout", "layout-test", Positions(), initial.Revision);
            var pushed = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(saved.Revision, pushed.Revision);
            Assert.Equal(saved.Revision, (await McpTools.GetWorkflowLayout(agent, "layout-test")).Revision);
            await McpTools.SaveWorkflowLayout(agent, "layout-test", Positions(x: 456), saved.Revision);
            Assert.Equal(456, (await ui.InvokeAsync<WorkflowLayout>("getWorkflowLayout", "layout-test")).Positions["first"].X);
        }
        finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
    }

    public void Dispose()
    {
        _engine.Dispose(); _sessions.Dispose(); Directory.Delete(_workspace, true);
    }
}
