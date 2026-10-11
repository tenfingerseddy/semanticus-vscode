using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>The sweep drives engines under a scratch state home and the process-wide policy overrides, so it runs
    /// with nothing else beside it.</summary>
    [CollectionDefinition("locked-sweep", DisableParallelization = true)]
    public sealed class LockedSweepCollection { }

    /// <summary>
    /// Lane m11/uat-undo, round 2 (security, D1): under Locked NO assistant tool writes the model or its files unasked.
    /// The tools are read off the tool list itself (the same reflection pass the workflow designer's picker uses), not
    /// named by hand: each one is driven once, as the assistant, against a fresh git-backed copy of a model with the
    /// person's own unsaved edit in it, and once against a clean copy, and afterwards the model file's bytes and the
    /// open model's contents must be what they were. A tool that cannot run here (it needs a live endpoint or a sign-in)
    /// is skipped by the parameters that say so, and the count of those is asserted small so the skip cannot grow
    /// into a hole. A tool that needs arguments this sweep cannot guess is simply not a finding either way, which is
    /// why the git, save, checkpoint and restore tools are also pinned by name below.
    /// </summary>
    [Collection("locked-sweep")]
    public sealed class UatLockedToolSweepTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _template;
        private readonly string _checkpointA;
        private readonly string _oldPolicyRoot, _oldApprovalRoot, _oldHome;

        // A tool with one of these parameters talks to a live service or a sign-in, which a sweep on one machine cannot
        // drive and which writes to that service, not to the model or its files.
        private static readonly string[] LiveParameters = { "endpoint", "workspaceId", "tenantId", "authMode", "rawToken", "accountProfileId", "loginHint", "dataSource", "pipelineId", "stageOrder", "reportIds" };

        public UatLockedToolSweepTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine("/tmp", "uls-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            if (!OperatingSystem.IsLinux()) _root = Path.Combine(Path.GetTempPath(), "uls-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(_root);
            _oldPolicyRoot = AgentPolicyStore.RootOverride; _oldApprovalRoot = ApprovalLedger.RootOverride;
            _oldHome = Environment.GetEnvironmentVariable("SEMANTICUS_HOME");
            Environment.SetEnvironmentVariable("SEMANTICUS_HOME", Path.Combine(_root, "home"));
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
            (_template, _checkpointA) = BuildTemplateAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _oldPolicyRoot; ApprovalLedger.RootOverride = _oldApprovalRoot;
            Environment.SetEnvironmentVariable("SEMANTICUS_HOME", _oldHome);
            try { Directory.Delete(_root, true); } catch { }
        }

        // A repository holding the model on main with two saved versions (A, then B), and a branch "other" whose model differs.
        private async Task<(string dir, string checkpointA)> BuildTemplateAsync()
        {
            var dir = Path.Combine(_root, "template");
            Directory.CreateDirectory(dir);
            var model = LedgerFixture.Write(dir, "model.bim");
            VerifyUatUndoClassTests.Git(dir, "init", "-q");
            VerifyUatUndoClassTests.Git(dir, "config", "user.email", "sweep@test.local");
            VerifyUatUndoClassTests.Git(dir, "config", "user.name", "Sweep Test");
            VerifyUatUndoClassTests.Git(dir, "config", "commit.gpgsign", "false");
            VerifyUatUndoClassTests.Git(dir, "add", "--", "model.bim");
            VerifyUatUndoClassTests.Git(dir, "commit", "-q", "-m", "initial");
            VerifyUatUndoClassTests.Git(dir, "branch", "-M", "main");
            VerifyUatUndoClassTests.Git(dir, "checkout", "-q", "-b", "other");
            var db = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(model));
            db.Model.Tables["Sales"].Measures["M2"].Description = "words that live on the other branch";
            File.WriteAllText(model, TOM.JsonSerializer.SerializeDatabase(db));
            VerifyUatUndoClassTests.Git(dir, "commit", "-q", "-am", "other branch");
            VerifyUatUndoClassTests.Git(dir, "checkout", "-q", "main");

            var sessions = new SessionManager();
            using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
            {
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "first words", "human");
                var a = await engine.CreateHistoryCheckpointAsync("checkpoint A", commit: true, "human");
                Assert.True(a.Committed, a.Error);
                await engine.SetDescriptionAsync("measure:Sales/M1", "second words", "human");
                var b = await engine.CreateHistoryCheckpointAsync("checkpoint B", commit: true, "human");
                Assert.True(b.Committed, b.Error);
                await sessions.Ledger.FlushAsync();
                return (dir, a.Checkpoint.Hash);
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
        }

        private async Task<(LocalEngine engine, SessionManager sessions, string dir, string model)> FreshAsync(string tag, bool personHasUnsavedEdit)
        {
            var dir = Path.Combine(_root, tag);
            CopyDir(_template, dir);
            var model = Path.Combine(dir, "model.bim");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(model);
            if (personHasUnsavedEdit) await engine.SetDescriptionAsync("measure:Sales/M3", "a person's unsaved words", "human");
            return (engine, sessions, dir, model);
        }

        private static async Task<string> ModelJsonAsync(SessionManager sessions) =>
            await sessions.Current.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase,
                new TOM.SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true }));

        private object Argument(ParameterInfo p, LocalEngine engine, string dir, string model)
        {
            var t = p.ParameterType;
            var n = p.Name ?? "";
            if (t == typeof(IEngine) || t == typeof(LocalEngine)) return engine;
            if (t == typeof(CancellationToken)) return CancellationToken.None;
            if (t == typeof(string))
            {
                var l = n.ToLowerInvariant();
                if (l.Contains("hash") || l.Contains("checkpoint")) return _checkpointA;
                if (l == "gitref" || l == "ref" && p.Member.Name.StartsWith("Git", StringComparison.Ordinal) || l == "branch") return "other";
                if (l.Contains("columnref") || l == "column") return "column:Sales/Amount";
                if (l.Contains("tableref") || l == "table") return "table:Sales";
                if (l.Contains("ref") || l == "object" || l == "target" || l == "measure") return "measure:Sales/M1";
                if (l == "path" || l == "file" || l == "folder" || l == "directory" || l == "dir" || l.EndsWith("path") || l.EndsWith("folder") || l.EndsWith("directory"))
                    return Path.Combine(dir, "out-" + n);
                if (l == "format") return "TMDL";
                if (l == "name" || l.EndsWith("name")) return "Zed";
                if (l.Contains("expression") || l.Contains("dax") || l.Contains("formula")) return "1";
                if (l == "url") return Path.Combine(dir, "no-such-repo");
                return "some words";
            }
            if (t == typeof(string[])) return new[] { "measure:Sales/M1" };
            if (t == typeof(bool))
            {
                var l = n.ToLowerInvariant();
                return !(l.Contains("dry") || l.Contains("preview") || l.Contains("discard") || l.Contains("cancel"));
            }
            if (t == typeof(bool?)) return true;
            if (p.HasDefaultValue && p.DefaultValue != null && (t == typeof(int) || t == typeof(long) || t == typeof(double))) return p.DefaultValue;   // a compatibility level of 1 is no model
            if (t == typeof(int)) return 1;
            if (t == typeof(long)) return 1L;
            if (t == typeof(double)) return 1.0;
            if (p.HasDefaultValue) return p.DefaultValue;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }

        /// <summary>The tools in the tool list that this sweep can drive, and how many it cannot.</summary>
        private static (List<(string Name, MethodInfo Method)> Drivable, List<string> Live) Tools()
        {
            var all = LocalEngine.OpSurface.Methods.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var drivable = new List<(string, MethodInfo)>();
            var live = new List<string>();
            foreach (var (name, m) in all)
            {
                if (m.GetParameters().Any(p => LiveParameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase))) live.Add(name);
                else drivable.Add((name, m));
            }
            return (drivable, live);
        }

        private async Task RunToolAsync(MethodInfo method, LocalEngine engine, string dir, string model)
        {
            var args = method.GetParameters().Select(p => Argument(p, engine, dir, model)).ToArray();
            try
            {
                var result = method.Invoke(null, args);
                if (result is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40)));
                    if (finished == task) await task;
                }
            }
            catch (Exception) { /* a refusal, a bad argument or an error is not a write */ }
        }

        [Fact]
        public void The_tool_list_is_read_and_few_tools_are_left_out_for_needing_a_live_service()
        {
            var (drivable, live) = Tools();
            _out.WriteLine("drivable " + drivable.Count + ", left out " + live.Count + ": " + string.Join(" ", live));
            Assert.True(drivable.Count > 250, "the sweep reads the tool list: " + drivable.Count);
            Assert.True(live.Count < 70, "too many tools are left out of the sweep: " + live.Count);
            Assert.Contains(drivable, t => t.Name == "git_commit");
            Assert.Contains(drivable, t => t.Name == "create_history_checkpoint");
            Assert.Contains(drivable, t => t.Name == "restore_history_checkpoint");
            Assert.Contains(drivable, t => t.Name == "save_model");
            Assert.Contains(drivable, t => t.Name == "set_description");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Under_Locked_no_tool_in_the_tool_list_writes_the_model_or_its_files_unasked(bool personHasUnsavedEdit)
        {
            var (drivable, _) = Tools();
            var wrote = new List<string>();
            var ran = 0;
            var previous = "(none)";
            // One open copy serves every tool that leaves it as it was (all of them, while Locked holds); a tool that did
            // change it, or swapped the open model, gets a fresh copy for the next, so one finding never hides another.
            (LocalEngine engine, SessionManager sessions, string dir, string model)? box = null;
            foreach (var (name, method) in drivable)
            {
                box ??= await FreshAsync("t" + ran, personHasUnsavedEdit);
                var (engine, sessions, dir, model) = box.Value;
                AgentPolicyStore.SetPreset("locked", "human");
                var fileBefore = File.ReadAllBytes(model);
                var sessionBefore = sessions.Current.Id;
                var jsonBefore = await ModelJsonAsync(sessions);
                var revisionBefore = sessions.Current.Revision;
                var policyBefore = AgentPolicyStore.Get().Preset;
                await RunToolAsync(method, engine, dir, model);
                ran++;
                var why = new List<string>();
                if (!File.Exists(model) || !fileBefore.SequenceEqual(File.ReadAllBytes(model))) why.Add("the model file changed");
                var sameSession = sessions.Current != null && sessions.Current.Id == sessionBefore;
                if (sameSession)
                {
                    var jsonAfter = await ModelJsonAsync(sessions);
                    if (jsonAfter != jsonBefore)
                    {
                        var at = Enumerable.Range(0, Math.Min(jsonBefore.Length, jsonAfter.Length)).FirstOrDefault(i => jsonBefore[i] != jsonAfter[i]);
                        why.Add("the open model changed, first at " + at + ": was [" + jsonBefore.Substring(Math.Max(0, at - 60), Math.Min(160, jsonBefore.Length - Math.Max(0, at - 60))).Replace("\n", " ")
                            + "] now [" + jsonAfter.Substring(Math.Max(0, at - 60), Math.Min(700, jsonAfter.Length - Math.Max(0, at - 60))).Replace("\n", " ") + "]; the tool before it was " + previous + "; revision " + revisionBefore + " to " + sessions.Current.Revision + "; policy " + policyBefore + " to " + AgentPolicyStore.Get().Preset + "; pending asks " + (await engine.ListPendingApprovalsAsync()).Length);
                    }
                }
                previous = name;
                if (why.Count > 0) wrote.Add(name + " (" + string.Join(", ", why) + (personHasUnsavedEdit ? "; person had unsaved edit" : "; clean") + ")");
                if (why.Count > 0 || !sameSession)
                {
                    // The same engine opens the model afresh, never a second engine beside it: the wrapper keeps one
                    // process-wide handler and an engine that has not wound down yet can still reach for it.
                    File.Copy(Path.Combine(_template, "model.bim"), model, true);
                    await engine.OpenAsync(model, discardUnsaved: true);
                    if (personHasUnsavedEdit) await engine.SetDescriptionAsync("measure:Sales/M3", "a person's unsaved words", "human");
                }
            }
            if (box != null) { box.Value.engine.Dispose(); try { Directory.Delete(box.Value.dir, true); } catch { } }
            _out.WriteLine("drove " + ran + " tools");
            Assert.True(wrote.Count == 0, "These tools wrote unasked under Locked: " + string.Join("; ", wrote));
        }

        // The tools that reach the files through another route are driven with the arguments that make them write,
        // so the sweep's guesses cannot be the only thing standing between them and a regression.
        // Lean suite (8 Oct): the default run keeps these rows; every row is the sweep.
        [Theory]
        [InlineData("save_model", true)]
        [InlineData("git_checkout", false)]
        public Task Under_Locked_the_tools_that_reach_the_files_ask_first_sample(string tool, bool personHasUnsavedEdit) => Under_Locked_the_tools_that_reach_the_files_ask_first(tool, personHasUnsavedEdit);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("git_commit", true)]
        [InlineData("create_history_checkpoint", true)]
        [InlineData("restore_history_checkpoint", true)]
        [InlineData("restore_history_checkpoint", false)]
        [InlineData("git_checkout", false)]
        [InlineData("git_branch", false)]
        [InlineData("save_model", true)]
        public async Task Under_Locked_the_tools_that_reach_the_files_ask_first(string tool, bool personHasUnsavedEdit)
        {
            var (engine, sessions, dir, model) = await FreshAsync("n" + tool.Length + (personHasUnsavedEdit ? "d" : "c"), personHasUnsavedEdit);
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var before = File.ReadAllBytes(model);
                var method = LocalEngine.OpSurface.Methods[tool];
                var named = tool == "git_branch";
                var args = method.GetParameters().Select(p =>
                    p.Name == "checkout" ? true : p.Name == "name" && named ? "other" : Argument(p, engine, dir, model)).ToArray();
                try { var r = method.Invoke(null, args); if (r is Task t) await t; } catch (Exception) { }
                Assert.True(before.SequenceEqual(File.ReadAllBytes(model)), tool + " changed the model file under Locked");
                Assert.NotEmpty(await engine.ListPendingApprovalsAsync());   // and it asked
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
