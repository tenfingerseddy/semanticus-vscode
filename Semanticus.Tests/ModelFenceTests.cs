// m11/drafts-sol round 2, C1: ONE fence at the person's door. Every write a page sends over the RPC door carries the model identity that page was showing
// (a top-level `modelIdentity` member of the request), and the door refuses it when another model is open. It is decided in one place (PersonFence), from the
// gate registry (ModelFence.Classify), so no editor can forget it and a method added tomorrow is fenced until someone writes down why it is not.
//   1  the check's D1, over the real wire: a Save typed for A that lands while B is open is refused, B keeps its words (a control writes, no identity writes as before).
//   2  every write op the person door can reach is covered: run every method of both RPC targets with another model named, and only reads and written exemptions get through.
//   3  the classification itself is complete and honest (a row for every method, a reason for every exemption, the editors' ops named one by one).
//   4  an unsaved model that gets its first Save is still the same model for a page that was showing it a moment ago.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ModelFenceTests : IDisposable
    {
        private const string Challenge = "model-fence-test-challenge-0123456789abcdef";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-mf-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public ModelFenceTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        /// <summary>A small model file of its own name. The fence tests ask who a request names, not what a model holds, so each one opens a
        /// few-object model (lean suite) instead of copying and opening AdventureWorks.</summary>
        private string Copy(string name)
        {
            var p = Path.Combine(_root, name);
            var db = new Microsoft.AnalysisServices.Tabular.Database(Path.GetFileNameWithoutExtension(name)) { CompatibilityLevel = 1600, Model = new Microsoft.AnalysisServices.Tabular.Model() };
            var sales = new Microsoft.AnalysisServices.Tabular.Table { Name = "Sales", LineageTag = "t-" + name };
            sales.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "Amount", DataType = Microsoft.AnalysisServices.Tabular.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-" + name });
            sales.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Total", Expression = "SUM ( Sales[Amount] )", LineageTag = "m-" + name });
            sales.Partitions.Add(new Microsoft.AnalysisServices.Tabular.Partition { Name = "P1", Source = new Microsoft.AnalysisServices.Tabular.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            File.WriteAllText(p, Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        // ---- the wire: a request exactly as the extension sends it, with the extra top-level member -----------------------------------------------------------------

        private sealed class Wire : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private int _id;
            private Wire(NamedPipeClientStream pipe) { _pipe = pipe; }

            public static async Task<Wire> ConnectAsync(string pipeName)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, Challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                return new Wire(pipe);
            }

            /// <summary>One request. Returns the error message (null on success) and the result.</summary>
            public async Task<(string Error, JToken Result)> CallAsync(string method, object[] args, string modelIdentity, bool namesModel = false)
            {
                var id = ++_id;
                var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = JArray.FromObject(args) };
                if (modelIdentity != null) msg["modelIdentity"] = modelIdentity;
                if (namesModel) msg["namesModel"] = true;   // the extension's own writer says so on every request (round 3)
                var body = Encoding.UTF8.GetBytes(msg.ToString(Newtonsoft.Json.Formatting.None));
                var head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
                await _pipe.WriteAsync(head, 0, head.Length);
                await _pipe.WriteAsync(body, 0, body.Length);
                await _pipe.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var reply = await ReadMessageAsync(cts.Token);
                    if (reply["id"] == null || (int?)reply["id"] != id) continue;   // a notification (model/didChange, ...) is not the answer
                    return ((string)reply["error"]?["message"], reply["result"]);
                }
            }

            private async Task<JObject> ReadMessageAsync(CancellationToken ct)
            {
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await _pipe.ReadAsync(one, 0, 1, ct) == 0) throw new EndOfStreamException();
                    header.Append((char)one[0]);
                }
                var length = int.Parse(System.Text.RegularExpressions.Regex.Match(header.ToString(), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
                var buf = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await _pipe.ReadAsync(buf, read, length - read, ct);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
                return JObject.Parse(Encoding.UTF8.GetString(buf));
            }

            public void Dispose() { try { _pipe.Dispose(); } catch { } }
        }

        private sealed class Door : IDisposable
        {
            public readonly SessionManager Sessions = new SessionManager();
            public readonly LocalEngine Engine;
            private readonly RpcServer _server;
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Task _serving;
            public readonly string PipeName = TestPipe.Mint("semanticus-fence-");

            public Door()
            {
                Engine = new LocalEngine(Sessions, TestEntitlements.Pro);
                _server = new RpcServer(Sessions, Engine, PipeName, Challenge);
                _serving = _server.RunAsync(_stop.Token);
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                _server.Dispose();
                Sessions.Dispose();
            }
        }

        private async Task<string> OpenAsync(LocalEngine e, string name)
        {
            await e.OpenAsync(Copy(name), discardUnsaved: true);
            await e.SetCompatibilityLevelAsync(1600, "human");   // AI instructions need 1465+
            return (await e.SessionInfoAsync()).ModelIdentity;
        }

        // ---- 1  the check's D1 over the real wire ---------------------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_Save_typed_for_A_that_lands_while_B_is_open_is_refused_at_the_door_and_B_keeps_its_words()
        {
            using var door = new Door();
            var a = await OpenAsync(door.Engine, "A.bim");
            var b = await OpenAsync(door.Engine, "B.bim");
            Assert.NotEqual(a, b);
            var bBefore = (await door.Engine.GetAiInstructionsAsync(null)).Instructions ?? "";
            using var wire = await Wire.ConnectAsync(door.PipeName);

            // exactly what instructions.tsx sends: the words, no culture, the person, and the value the Save read as the one it replaces
            var refused = await wire.CallAsync("setAiInstructions", new object[] { "Words typed for A", null, "human", bBefore }, a);
            Assert.NotNull(refused.Error);
            Assert.Contains("another model", refused.Error);
            Assert.Equal(bBefore, (await door.Engine.GetAiInstructionsAsync(null)).Instructions ?? "");

            // the control: the same write, named for the model that IS open, is written
            var written = await wire.CallAsync("setAiInstructions", new object[] { "Words typed for B", null, "human", bBefore }, b);
            Assert.Null(written.Error);
            Assert.Equal("Words typed for B", (await door.Engine.GetAiInstructionsAsync(null)).Instructions);

            // a request that names no model keeps today's behaviour (an older client, a native command): it is not fenced
            var plain = await wire.CallAsync("setAiInstructions", new object[] { "Plain words", null, "human", "Words typed for B" }, null);
            Assert.Null(plain.Error);
            Assert.Equal("Plain words", (await door.Engine.GetAiInstructionsAsync(null)).Instructions);
            var empty = await wire.CallAsync("setAiInstructions", new object[] { "Empty named", null, "human", "Plain words" }, "");
            Assert.Null(empty.Error);
        }

        [Fact]
        public async Task A_read_is_never_fenced_and_a_delete_or_a_batch_for_another_model_is_refused_over_the_wire()
        {
            using var door = new Door();
            var a = await OpenAsync(door.Engine, "A.bim");
            await OpenAsync(door.Engine, "B.bim");
            using var wire = await Wire.ConnectAsync(door.PipeName);

            var read = await wire.CallAsync("getAiInstructions", Array.Empty<object>(), a);   // a page that is a moment behind may still look
            Assert.Null(read.Error);

            var measure = (await door.Engine.ListMeasuresAsync()).First();
            var del = await wire.CallAsync("deleteObject", new object[] { measure.Ref, "human" }, a);
            Assert.Contains("another model", del.Error);
            var batch = await wire.CallAsync("setObjectProperties", new object[] { new[] { measure.Ref }, "Description", "x", "human", null, null, null }, a);
            Assert.Contains("another model", batch.Error);
            Assert.Contains(await door.Engine.ListMeasuresAsync(), m => m.Name == measure.Name);   // nothing was deleted
        }

        // ---- 2  every write the person door can reach is covered ---------------------------------------------------------------------------------------------------

        /// <summary>An engine that records which methods the fence let through, and answers every one with an empty success.</summary>
        internal class Recording : DispatchProxy
        {
            public List<string> Reached = new List<string>();
            protected override object Invoke(MethodInfo target, object[] args)
            {
                lock (Reached) Reached.Add(target.Name);
                var rt = target.ReturnType;
                if (rt == typeof(Task)) return Task.CompletedTask;
                var inner = rt.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(inner).Invoke(null, new object[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }
        }

        private static object DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        /// <summary>Calls every public method of the two person-door targets with default arguments while ANOTHER model is open. Returns, per RPC method, the engine methods that got through.</summary>
        private static Task<Dictionary<string, List<string>>> SweepAsync() => Sweep.Value;
        private static readonly Lazy<Task<Dictionary<string, List<string>>>> Sweep = new Lazy<Task<Dictionary<string, List<string>>>>(() => Task.Run(() => RunSweepAsync("file:/some/other/model.bim", false)));

        /// <summary>The same sweep for a request from the extension's own writer that names NO model while one is open (round 3, the strict rule).</summary>
        private static Task<Dictionary<string, List<string>>> StrictUnnamedSweepAsync() => StrictSweep.Value;
        private static readonly Lazy<Task<Dictionary<string, List<string>>>> StrictSweep = new Lazy<Task<Dictionary<string, List<string>>>>(() => Task.Run(() => RunSweepAsync(null, true)));

        private static async Task<Dictionary<string, List<string>>> RunSweepAsync(string named, bool strict)
        {
            var recording = DispatchProxy.Create<IEngine, Recording>();
            var log = ((Recording)(object)recording).Reached;
            var served = PersonFence.For(recording, identity => false, () => true);   // no named model is the open one, and a model is open
            var targets = new object[] { new EngineRpcTarget(recording, "human", null, served), new HumanGovernanceRpcTarget(recording, null, null, served) };
            var got = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (RequestModelIdentity.Begin(named, strict))
            {
                foreach (var target in targets)
                    foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        List<string> reached;
                        lock (log) { log.Clear(); }
                        try
                        {
                            var task = m.Invoke(target, m.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType)).ToArray()) as Task;
                            if (task != null) { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { throw; } catch { /* refused or failed: what matters is what reached the engine */ } }
                        }
                        catch (TargetInvocationException) { /* a method that needs its arguments to run says so before it reaches the engine */ }
                        lock (log) { reached = log.ToList(); }
                        got[target.GetType().Name + "." + m.Name] = reached;
                    }
            }
            return got;
        }

        [Fact]
        public async Task Every_method_of_the_person_door_that_writes_is_refused_when_the_request_names_another_model()
        {
            var got = await SweepAsync();
            Assert.True(got.Count > 300, "the sweep covers the whole person-door surface (" + got.Count + " methods)");
            var leaks = new List<string>();
            foreach (var (rpc, reached) in got)
                foreach (var engineMethod in reached)
                {
                    var cls = ModelFence.Classify(engineMethod);
                    var row = GateRegistry.Find(engineMethod);
                    var plainRead = row != null && (row.Kind == GateKind.Data || (row.Kind == GateKind.Pass && row.CanPreview));
                    // what got through must be a read, or written down as an exemption or a signal; anything else is a write that skipped the fence
                    if (cls == FenceClass.Fenced || (!plainRead && !ModelFence.Exemptions.ContainsKey(engineMethod) && !ModelFence.Signals.ContainsKey(engineMethod)))
                        leaks.Add(rpc + " reached " + engineMethod + " (" + cls + ")");
                }
            Assert.True(leaks.Count == 0, "writes that got past the fence:\n" + string.Join("\n", leaks));
        }

        [Fact]
        public async Task The_sweep_is_not_vacuous_a_write_reaches_the_engine_when_the_fence_is_off_and_the_named_model_is_open()
        {
            var recording = DispatchProxy.Create<IEngine, Recording>();
            var log = ((Recording)(object)recording).Reached;
            var open = new EngineRpcTarget(recording, "human", null, PersonFence.For(recording, identity => identity == "file:/m/A.bim"));
            using (RequestModelIdentity.Begin("file:/m/A.bim")) await open.deleteObject("measure:T/M", "human");
            Assert.Contains("DeleteObjectAsync", log);   // the named model IS the open one: it goes through
            log.Clear();
            using (RequestModelIdentity.Begin("file:/m/B.bim")) await Assert.ThrowsAsync<InvalidOperationException>(() => open.deleteObject("measure:T/M", "human"));
            Assert.Empty(log);                           // another model: it never reached the engine
            using (RequestModelIdentity.Begin(null)) await open.deleteObject("measure:T/M", "human");
            Assert.Contains("DeleteObjectAsync", log);   // no model named: today's behaviour
        }

        // ---- 2b  round 3 (the check's T1): a request that names NO model, from the extension's own writer, is refused while a model is open -------------------------------

        [Fact]
        public async Task C1_Every_write_a_request_from_the_extensions_writer_makes_with_no_model_named_is_refused_while_a_model_is_open_except_the_written_Unnamed_rows()
        {
            var got = await StrictUnnamedSweepAsync();
            Assert.True(got.Count > 300, "the sweep covers the whole person-door surface (" + got.Count + " methods)");
            var leaks = new List<string>();
            foreach (var (rpc, reached) in got)
                foreach (var engineMethod in reached)
                {
                    if (ModelFence.Classify(engineMethod) != FenceClass.Fenced) continue;   // a read, an exemption or a signal: never refused for this
                    if (ModelFence.Unnamed.ContainsKey(engineMethod)) continue;
                    leaks.Add(rpc + " reached " + engineMethod + " with no model named");
                }
            Assert.True(leaks.Count == 0, "writes that got past the strict rule:\n" + string.Join("\n", leaks));
        }

        [Fact]
        public void C1_Every_Unnamed_row_has_a_reason_names_a_fenced_method_and_there_are_few()
        {
            var names = typeof(IEngine).GetMethods().Select(m => m.Name).Distinct().ToList();
            foreach (var (name, why) in ModelFence.Unnamed)
            {
                Assert.Contains(name, names);
                Assert.Equal(FenceClass.Fenced, ModelFence.Classify(name));   // a row for a method that is not fenced anyway is noise
                Assert.False(string.IsNullOrWhiteSpace(why), name + " is Unnamed with no reason");
            }
            Assert.True(ModelFence.Unnamed.Count <= 6, "more than 6 Unnamed rows (" + ModelFence.Unnamed.Count + ") means a command or page stamp was missed: stop and find it");
        }

        [Fact]
        public async Task C1_The_strict_sweep_is_not_vacuous_an_unnamed_write_goes_through_in_process_and_with_no_model_open_and_a_named_open_model_goes_through()
        {
            var recording = DispatchProxy.Create<IEngine, Recording>();
            var log = ((Recording)(object)recording).Reached;
            var open = new EngineRpcTarget(recording, "human", null, PersonFence.For(recording, identity => identity == "file:/m/A.bim", () => true));
            using (RequestModelIdentity.Begin(null)) await open.deleteObject("measure:T/M", "human");
            Assert.Contains("DeleteObjectAsync", log);   // in-process, not the extension's writer: today's behaviour, unchanged
            log.Clear();
            using (RequestModelIdentity.Begin(null, strict: true)) await Assert.ThrowsAsync<InvalidOperationException>(() => open.deleteObject("measure:T/M", "human"));
            Assert.Empty(log);                           // the extension's writer, no model named, a model open: refused, nothing reached the engine
            using (RequestModelIdentity.Begin("file:/m/A.bim", strict: true)) await open.deleteObject("measure:T/M", "human");
            Assert.Contains("DeleteObjectAsync", log);   // named, and it is the open model
            log.Clear();
            var none = new EngineRpcTarget(recording, "human", null, PersonFence.For(recording, identity => false, () => false));
            using (RequestModelIdentity.Begin(null, strict: true)) await none.deleteObject("measure:T/M", "human");
            Assert.Contains("DeleteObjectAsync", log);   // no model open: nothing to hit, so nothing to refuse
        }

        // ---- 2c  C2: over the real wire, RpcServer and a real LocalEngine ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task C2_Over_the_wire_a_write_with_namesModel_and_no_identity_is_refused_a_named_open_model_passes_and_a_request_without_namesModel_passes()
        {
            using var door = new Door();
            var a = await OpenAsync(door.Engine, "A.bim");
            var before = (await door.Engine.GetAiInstructionsAsync(null)).Instructions ?? "";
            using var wire = await Wire.ConnectAsync(door.PipeName);

            var refused = await wire.CallAsync("setAiInstructions", new object[] { "Unnamed words", null, "human", before }, null, namesModel: true);
            Assert.NotNull(refused.Error);
            Assert.Contains("another model", refused.Error);
            Assert.Equal(before, (await door.Engine.GetAiInstructionsAsync(null)).Instructions ?? "");

            var named = await wire.CallAsync("setAiInstructions", new object[] { "Named words", null, "human", before }, a, namesModel: true);
            Assert.Null(named.Error);
            Assert.Equal("Named words", (await door.Engine.GetAiInstructionsAsync(null)).Instructions);

            var plain = await wire.CallAsync("setAiInstructions", new object[] { "Older client words", null, "human", "Named words" }, null);
            Assert.Null(plain.Error);   // an older client (no namesModel): today's behaviour
            Assert.Equal("Older client words", (await door.Engine.GetAiInstructionsAsync(null)).Instructions);

            var read = await wire.CallAsync("getAiInstructions", Array.Empty<object>(), null, namesModel: true);
            Assert.Null(read.Error);    // a read is never refused for naming nothing
        }

        [Fact]
        public async Task C2_Over_the_wire_the_check_T1_shape_a_rename_that_names_no_model_while_B_is_open_changes_nothing_and_with_no_model_open_nothing_is_refused()
        {
            using var door = new Door();
            using var wire = await Wire.ConnectAsync(door.PipeName);
            var b = await OpenAsync(door.Engine, "B.bim");
            var measure = (await door.Engine.ListMeasuresAsync()).First();
            var rename = await wire.CallAsync("renameObject", new object[] { measure.Ref, "Renamed on A", "human" }, null, namesModel: true);
            Assert.Contains("another model", rename.Error);
            Assert.Contains(await door.Engine.ListMeasuresAsync(), m => m.Name == measure.Name);   // B still has its own
            Assert.DoesNotContain(await door.Engine.ListMeasuresAsync(), m => m.Name == "Renamed on A");
            var ok = await wire.CallAsync("renameObject", new object[] { measure.Ref, "Renamed on B", "human" }, b, namesModel: true);
            Assert.Null(ok.Error);
        }

        // ---- 2d  C3: Save As keeps the identities the model had ----------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task C3_After_Save_As_a_page_that_named_the_old_file_still_names_the_model_and_another_model_that_file_is_refused()
        {
            using var door = new Door();
            var a = await OpenAsync(door.Engine, "A.bim");
            Assert.StartsWith("file:", a);
            await door.Engine.SaveAsync(Path.Combine(_root, "A2.bim"), "bim", true, "human");
            var a2 = (await door.Engine.SessionInfoAsync()).ModelIdentity;
            Assert.NotEqual(a, a2);
            using var wire = await Wire.ConnectAsync(door.PipeName);
            var before = (await door.Engine.GetAiInstructionsAsync(null)).Instructions ?? "";
            var passes = await wire.CallAsync("setAiInstructions", new object[] { "Typed before the Save As", null, "human", before }, a, namesModel: true);
            Assert.Null(passes.Error);   // the page was a moment behind: same model, so the write is written
            Assert.Equal("Typed before the Save As", (await door.Engine.GetAiInstructionsAsync(null)).Instructions);
            Assert.True(door.Engine.IsModelIdentity(a2));

            var c = await OpenAsync(door.Engine, "C.bim");   // another model opens
            Assert.False(door.Engine.IsModelIdentity(a), "the old file's name belongs to the old session, not to the model open now");
            var refused = await wire.CallAsync("setAiInstructions", new object[] { "Typed for A", null, "human", "" }, a, namesModel: true);
            Assert.Contains("another model", refused.Error);
            Assert.True(door.Engine.IsModelIdentity(c));
        }

        // ---- 3  the classification is complete and honest -----------------------------------------------------------------------------------------------------------

        [Fact]
        public void Every_engine_method_has_a_class_and_every_exemption_names_a_real_method_and_says_why()
        {
            var names = typeof(IEngine).GetMethods().Select(m => m.Name).Distinct().ToList();
            foreach (var n in names) Assert.True(Enum.IsDefined(typeof(FenceClass), ModelFence.Classify(n)), n);
            foreach (var (name, why) in ModelFence.Exemptions.Concat(ModelFence.Signals))
            {
                Assert.Contains(name, names);
                Assert.False(string.IsNullOrWhiteSpace(why), name + " is exempt with no reason");
            }
            Assert.Empty(ModelFence.Exemptions.Keys.Intersect(ModelFence.Signals.Keys));
        }

        [Fact]
        public void A_method_with_no_registry_row_is_fenced_not_skipped()
        {
            Assert.Equal(FenceClass.Fenced, ModelFence.Classify("SomeFutureWriteAsync"));
        }

        [Fact]
        public void Everything_the_registry_calls_a_write_is_fenced_unless_it_is_written_down()
        {
            var problems = new List<string>();
            foreach (var row in GateRegistry.All)
            {
                var write = row.Kind != GateKind.Pass && row.Kind != GateKind.Data || (row.Kind == GateKind.Pass && !row.CanPreview);
                if (!write) continue;
                var cls = ModelFence.Classify(row.Method);
                if (cls == FenceClass.Fenced) continue;
                if (cls == FenceClass.Exempt && ModelFence.Exemptions.ContainsKey(row.Method)) continue;
                if (cls == FenceClass.ReadOnly && ModelFence.Signals.ContainsKey(row.Method)) continue;
                problems.Add(row.Method + " is a write in the registry and is " + cls + ", with no entry that says why");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// <summary>The ops each editor that holds a draft writes with (the useEditBase editors and the Properties grid), by the RPC name the page sends.</summary>
        public static IEnumerable<object[]> EditorWrites() => new[]
        {
            // AI instructions, the model notes (lessons), the primer
            "setAiInstructions", "setPrimer", "acceptPrimerSuggestion", "rejectPrimerSuggestion",
            "addInsight", "editInsight", "deleteInsight", "approveInsight", "purgeKnowledge",
            // calculation items (DAX and format), row filters, roles
            "setDax", "setCalcItemFormatString", "setTablePermission", "setRolePermission", "setRoleMember", "createCalculationItem",
            // custom rules
            "loadBpaRules", "loadReadinessRules", "resetBpaRules", "resetReadinessRules",
            // the SQL source, the table mapping and the publish destination
            "saveSqlSource", "deleteSqlSource", "setTableSourceMapping", "clearTableSourceMapping", "setModelSqlEndpoint", "setPublishDestination",
            // the M policy and the M query
            "setIncrementalRefreshPolicy", "removeIncrementalRefreshPolicy", "setPartitionM", "updateNamedExpression", "createNamedExpression",
            // the spec and the tests
            "setSpec", "saveSpec", "loadSpec", "clearSpec", "buildModelFromSpec", "saveTest", "deleteTest",
            // documentation and the Properties grid
            "setDocSection", "setObjectProperty", "setObjectProperties", "setMeasureFormatExpression", "setColumnDataType", "setDescription", "renameObject",
            // the start plan
            "editStartPlan", "writeStartWords", "buildStartPlan", "discardStartPlan",
        }.Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(EditorWrites))]
        public async Task Every_editor_write_is_a_method_of_the_person_door_and_is_refused_for_another_model(string rpcName)
        {
            var got = await SweepAsync();
            var key = got.Keys.FirstOrDefault(k => k.EndsWith("." + rpcName, StringComparison.Ordinal));
            Assert.True(key != null, rpcName + " is not a method of the person door: the editor's op is named wrongly here, or was renamed");
            Assert.True(got[key].Count == 0, rpcName + " reached the engine with another model named: " + string.Join(", ", got[key]));
        }

        [Fact]
        public async Task A_Desktop_model_is_named_the_same_before_and_after_its_window_title_is_known_and_another_instance_is_not()
        {
            using var door = new Door();
            await OpenAsync(door.Engine, "D.bim");
            door.Sessions.Current.LiveOrigin = LocalEngine.LocalOriginFor("localhost:51234", "guid-one", new LocalDesktop.DesktopIdentity { Stem = "Contoso Sales" });
            var now = (await door.Engine.SessionInfoAsync()).ModelIdentity;
            Assert.StartsWith("desktop:", now);
            Assert.True(door.Engine.IsModelIdentity(now));
            Assert.True(door.Engine.IsModelIdentity("live:localhost:51234|guid-one"), "a page that named it before the window title was captured still names this model");
            Assert.False(door.Engine.IsModelIdentity("live:localhost:60321|guid-two"), "another Desktop instance is another model");
            Assert.False(door.Engine.IsModelIdentity("desktop:contoso sales|localhost:60321|guid-two"));
        }

        // ---- 4  an unsaved model that gets its first Save is the same model ------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_page_that_named_an_unsaved_model_still_names_it_after_its_first_Save_and_another_model_is_still_refused()
        {
            using var door = new Door();
            await door.Engine.CreateModelAsync("Fresh", 1604, true);
            var unsaved = (await door.Engine.SessionInfoAsync()).ModelIdentity;
            Assert.StartsWith("unsaved:", unsaved);
            await door.Engine.SaveAsync(Path.Combine(_root, "Fresh.bim"), "bim", true, "human");
            var saved = (await door.Engine.SessionInfoAsync()).ModelIdentity;
            Assert.StartsWith("file:", saved);
            Assert.True(door.Engine.IsModelIdentity(unsaved), "the identity the page was showing before the Save still names this model");
            Assert.True(door.Engine.IsModelIdentity(saved));
            Assert.False(door.Engine.IsModelIdentity("unsaved:00000000000000000000000000000000"));
            Assert.False(door.Engine.IsModelIdentity("file:/not/this.bim"));
            Assert.False(door.Engine.IsModelIdentity(""));
        }
    }
}
