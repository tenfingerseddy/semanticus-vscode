using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, round 3 (F2, by class): a write the person's policy refuses leaves the open model EXACTLY as it was.
    /// Every tool in the tool list is driven as the assistant under Locked, with arguments that name real objects of a model
    /// that has a perspective, a role, a culture, a hierarchy and a relationship (so a write reaches the question instead of
    /// stopping at "not found"), and before and after each one the whole model written as TMDL and as JSON, the undo and redo
    /// stacks, the History's length and the unsaved flag must be byte-equal. The rehearsal that works out what to ask rolls the
    /// write back through TE2's undo, which does not undo everything TE2 does (a perspective's table entry), so a refusal that
    /// is correct for ten kinds of write can still leave a trace for the eleventh; this is the check that finds the eleventh.
    /// </summary>
    [Collection("locked-sweep")]
    public sealed class UatUndoRefusedWriteSweepTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _oldPolicyRoot, _oldApprovalRoot, _oldHome;

        private static readonly string[] LiveParameters = { "endpoint", "workspaceId", "tenantId", "authMode", "rawToken", "accountProfileId", "loginHint", "dataSource", "pipelineId", "stageOrder", "reportIds" };

        public UatUndoRefusedWriteSweepTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine("/tmp", "urw-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            if (!OperatingSystem.IsLinux()) _root = Path.Combine(Path.GetTempPath(), "urw-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(_root);
            _oldPolicyRoot = AgentPolicyStore.RootOverride; _oldApprovalRoot = ApprovalLedger.RootOverride;
            _oldHome = Environment.GetEnvironmentVariable("SEMANTICUS_HOME");
            Environment.SetEnvironmentVariable("SEMANTICUS_HOME", Path.Combine(_root, "home"));
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _oldPolicyRoot; ApprovalLedger.RootOverride = _oldApprovalRoot;
            Environment.SetEnvironmentVariable("SEMANTICUS_HOME", _oldHome);
            try { Directory.Delete(_root, true); } catch { }
        }

        private string WriteModel(string tag)
        {
            var dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Trace") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-pi" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Hierarchies.Add(new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo", Levels = { new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 0 }, new TOM.Level { Name = "Code", Column = sales.Columns["Code"], Ordinal = 1 } } });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            role.TablePermissions.Add(new TOM.TablePermission { Table = sales, FilterExpression = "[Region] = \"A\"" });
            db.Model.Roles.Add(role);
            var view = new TOM.Perspective { Name = "View" };
            db.Model.Perspectives.Add(view);
            db.Model.Cultures.Add(new TOM.Culture { Name = "en-US" });
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        // Every way the model can show a trace, in one string: its JSON (every property TOM writes, none left out), the same
        // model as TMDL, the two undo stacks, the unsaved flag and how many rows the History holds.
        private static async Task<string> TraceAsync(SessionManager sessions)
        {
            var s = sessions.Current;
            var json = await s.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase, new TOM.SerializeOptions { IgnoreTimestamps = true }));
            var tmdl = await s.ReadAsync(m =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "tr-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    TOM.TmdlSerializer.SerializeDatabaseToFolder(m.Database.TOMDatabase, folder);
                    var sb = new StringBuilder();
                    foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                        sb.Append(Path.GetRelativePath(folder, f)).Append('\n').Append(File.ReadAllText(f)).Append('\n');
                    return sb.ToString();
                }
                finally { try { Directory.Delete(folder, true); } catch { } }
            });
            var undo = await s.ReadAsync(_ => s.UndoStateNow());
            return json + "\n=====\n" + tmdl + "\n=====\n" + JsonSerializer.Serialize(undo) + "\nunsaved=" + s.HasUnsavedEdits
                + "\nhistory=" + sessions.Ledger.ForModel(s.Id).Count + "\nrevision=" + s.Revision;
        }

        private static string FirstDifference(string a, string b)
        {
            var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i]);
            if (a.Length != b.Length && at == 0 && a.Length > 0 && b.Length > 0 && a[0] == b[0]) at = Math.Min(a.Length, b.Length);
            string Cut(string x) => x.Substring(Math.Max(0, at - 50), Math.Min(180, x.Length - Math.Max(0, at - 50))).Replace("\n", " ");
            return "first at " + at + ": was [" + Cut(a) + "] now [" + Cut(b) + "]";
        }

        // Arguments that name the model's real objects, by what the parameter is called. A tool is driven once for each kind of
        // object a reference could mean, so a write is not turned away for naming a measure where it wanted a column.
        private static object Argument(ParameterInfo p, LocalEngine engine, string model, string objectKind, int attempt, string pathAim = null)
        {
            var t = p.ParameterType;
            var n = (p.Name ?? "").ToLowerInvariant();
            if (t == typeof(IEngine) || t == typeof(LocalEngine)) return engine;
            if (t == typeof(CancellationToken)) return CancellationToken.None;
            if (t == typeof(string))
            {
                if (n == "sessionid") return null;   // a fence that names a session refuses before anything is written; leave it off
                if (n.Contains("partition")) return "partition:Sales/P1";
                if (n.Contains("relationship")) return "r1";
                if (n == "propertyname" || n == "property" || n == "field") return "Description";
                if (n.Contains("datatype") || n == "type") return "String";
                if (n.Contains("summar")) return "Sum";
                if (n.Contains("sortby")) return "Code";
                if (n.Contains("granularity")) return "Day";
                if (n == "permission" || n.EndsWith("permission") && n.Contains("model")) return "Read";
                if (n.Contains("perspective")) return n.EndsWith("ref") ? "perspective:View" : "View";
                if (n.Contains("role")) return n.EndsWith("ref") ? "role:Managers" : "Managers";
                if (n.Contains("culture") || n.Contains("locale") || n.Contains("language")) return "en-US";
                if (n == "member" || n == "members" || n.Contains("user")) return "alice@example.com";
                if (n.Contains("hash") || n.Contains("checkpoint")) return "0000000";
                if (n == "path" || n == "file" || n.EndsWith("path") || n.EndsWith("file") || n.EndsWith("folder") || n.EndsWith("directory") || n == "directory" || n == "dir")
                    return pathAim ?? Path.Combine(Path.GetDirectoryName(model)!, "out-" + n);
                if (n.Contains("tableref")) return "table:Sales";
                if (n.Contains("columnref")) return "column:Sales/Region";
                if (n.Contains("measureref")) return "measure:Sales/Total";
                if (n.Contains("ref") || n == "object" || n == "target" || n == "measure" || n == "column" || n == "from" || n == "to" || n == "item")
                    return objectKind switch { "column" => "column:Sales/Region", "table" => "table:Sales", "hierarchy" => "hierarchy:Sales/Geo", _ => "measure:Sales/Total" };
                if (n == "table" || n == "tablename") return "Sales";
                if (n == "format") return "TMDL";
                if (n == "name" || n == "newname" || n.EndsWith("name")) return attempt == 0 ? "Zed" : "Zed" + attempt;
                if (n.Contains("expression") || n.Contains("dax") || n.Contains("formula") || n.Contains("filter")) return "1";
                if (n.Contains("description") || n.Contains("text") || n.Contains("value") || n.Contains("instructions")) return "some words";
                if (n == "level") return "None";
                if (n == "url") return Path.Combine(Path.GetDirectoryName(model)!, "no-such-repo");
                return p.HasDefaultValue ? p.DefaultValue : "some words";
            }
            if (t == typeof(string[]))
                return n.Contains("level") ? new[] { "Region", "Code" } : n.Contains("synonym") ? new[] { "revenue" } : n.Contains("member") ? new[] { "alice@example.com" }
                    : objectKind switch { "column" => new[] { "column:Sales/Region" }, "table" => new[] { "table:Sales" }, "hierarchy" => new[] { "hierarchy:Sales/Geo" }, _ => new[] { "measure:Sales/Total" } };
            if (t == typeof(bool))
            {
                if (n.Contains("dry") || n.Contains("preview") || n.Contains("discard") || n.Contains("cancel")) return false;
                return true;
            }
            if (t == typeof(bool?)) return true;
            if (p.HasDefaultValue && p.DefaultValue != null && (t == typeof(int) || t == typeof(long) || t == typeof(double))) return p.DefaultValue;
            if (t == typeof(int)) return 1;
            if (t == typeof(long)) return 1L;
            if (t == typeof(double)) return 1.0;
            if (p.HasDefaultValue) return p.DefaultValue;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_a_write_that_is_refused_leaves_the_whole_model_and_its_undo_stack_exactly_as_it_was()
        {
            var all = LocalEngine.OpSurface.Methods.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Where(kv => !kv.Value.GetParameters().Any(p => LiveParameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase))).ToList();
            var path = WriteModel("base");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(path);
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");   // an unsaved edit of the person's, with a redo behind it
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's later words", "human");
                await engine.UndoAsync("human");
                var traces = new List<string>();
                var reached = new List<string>();
                var notReached = new Dictionary<string, string>();
                var ran = 0;
                foreach (var (name, method) in all)
                {
                    // Tools that replace the session or touch the machine rather than the model are the other sweeps' business.
                    if (name is "open_model" or "create_model" or "open_local" or "open_live" or "git_clone" or "git_pull" or "git_checkout" or "git_branch"
                        or "restore_history_checkpoint" or "restore_model_version") continue;
                    foreach (var kind in new[] { "measure", "column", "table", "hierarchy" })
                    {
                        if (kind != "measure" && !method.GetParameters().Any(p => (p.ParameterType == typeof(string) || p.ParameterType == typeof(string[]))
                            && ((p.Name ?? "").ToLowerInvariant().Contains("ref") || new[] { "object", "target", "measure", "column", "from", "to", "item" }.Contains((p.Name ?? "").ToLowerInvariant())))) break;
                        AgentPolicyStore.SetPreset("locked", "human");
                        var before = await TraceAsync(sessions);
                        var pendingBefore = (await engine.ListPendingApprovalsAsync()).Length;
                        var sessionId = sessions.Current.Id;
                        Exception thrown = null;
                        try
                        {
                            var args = method.GetParameters().Select(p => Argument(p, engine, path, kind, ran)).ToArray();
                            var r = method.Invoke(null, args);
                            if (r is Task task) { var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40))); if (done == task) await task; }
                        }
                        catch (TargetInvocationException tie) { thrown = tie.InnerException; }
                        catch (Exception ex) { thrown = ex; }
                        ran++;
                        if ((await engine.ListPendingApprovalsAsync()).Length > pendingBefore || thrown != null && thrown.Message.Contains("needs the user's approval", StringComparison.Ordinal)) reached.Add(name + "/" + kind);
                        else if (thrown != null) notReached[name] = thrown.GetType().Name + ": " + thrown.Message.Split('\n')[0];
                        if (sessions.Current == null || sessions.Current.Id != sessionId)
                        {
                            traces.Add(name + ": the open model was replaced");
                            await engine.OpenAsync(path, discardUnsaved: true);
                            continue;
                        }
                        var after = await TraceAsync(sessions);
                        if (after != before)
                        {
                            traces.Add(name + "/" + kind + ": " + FirstDifference(before, after) + (thrown == null ? "; it did not throw" : "; threw " + thrown.GetType().Name));
                            await engine.OpenAsync(path, discardUnsaved: true);   // one finding never hides another
                            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
                            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's later words", "human");
                            await engine.UndoAsync("human");
                        }
                    }
                }
                var distinct = reached.Select(r => r.Split('/')[0]).Distinct().ToList();
                _out.WriteLine("drove " + ran + " runs of " + all.Count + " tools; " + distinct.Count + " tools reached the question: " + string.Join(" ", distinct));
                Assert.True(traces.Count == 0, "A refused write left a trace:\n" + string.Join("\n", traces));
                var asked = new HashSet<string>(distinct);
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "urw-notreached.txt"), notReached.Where(kv => !asked.Contains(kv.Key)).Select(kv => kv.Key + " => " + kv.Value));
                Assert.True(distinct.Count >= 40, "too few tools reached the question for this to prove anything: " + distinct.Count + " (" + string.Join(" ", distinct) + ")");
            }
            finally { engine.Dispose(); }
        }

        // The engine's own notes beside a model (.semanticus: the Plan's memory, insights, layout, report scope, workflow
        // settings) are bookkeeping the engine keeps for whoever reads or works, a read tool included (get_model_plan makes
        // plan-memory.json on first read). They are not model files and not files the model is read from, so the Write
        // model files row is not about them; they are left out of this comparison and named in the lane report for the join.
        private static Dictionary<string, byte[]> Tree(string dir) =>
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(dir, f).StartsWith(".semanticus" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(f => f, File.ReadAllBytes, StringComparer.Ordinal);

        /// <summary>The same sweep, for files. Every tool is driven as the assistant under Locked with each path argument aimed
        /// three ways (a fresh path, the model's own file, a file the person already has), and the folder the model lives in is
        /// compared file by file afterwards. The only change allowed is a NEW file at a path the request named: anything else
        /// (a model file written over, a person's file replaced, a file made where nobody asked) was written unasked.</summary>
        [Fact]
        public async Task Under_Locked_no_tool_writes_a_file_unasked_unless_it_is_a_new_path_the_request_names()
        {
            var all = LocalEngine.OpSurface.Methods.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Where(kv => !kv.Value.GetParameters().Any(p => LiveParameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase))).ToList();
            var path = WriteModel("files");
            var dir = Path.GetDirectoryName(path)!;
            var keep = Path.Combine(dir, "keep.txt");
            File.WriteAllText(keep, "the person's own file");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(path);
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
                await sessions.Ledger.FlushAsync();
                var original = Tree(dir);
                var findings = new List<string>();
                var driven = 0;
                foreach (var (name, method) in all)
                {
                    if (name is "open_model" or "create_model" or "open_local" or "open_live") continue;
                    var hasPath = method.GetParameters().Any(p => p.ParameterType == typeof(string) && (new[] { "path", "file", "folder", "directory", "dir" }.Contains((p.Name ?? "").ToLowerInvariant())
                        || (p.Name ?? "").ToLowerInvariant().EndsWith("path") || (p.Name ?? "").ToLowerInvariant().EndsWith("file") || (p.Name ?? "").ToLowerInvariant().EndsWith("folder") || (p.Name ?? "").ToLowerInvariant().EndsWith("directory")));
                    foreach (var aim in hasPath ? new[] { (string)null, path, keep } : new string[] { null })
                    {
                        AgentPolicyStore.SetPreset("locked", "human");
                        var sessionId = sessions.Current.Id;
                        var named = new List<string>();
                        try
                        {
                            var args = method.GetParameters().Select(p =>
                            {
                                var v = Argument(p, engine, path, "measure", driven, aim);
                                if (v is string sv && sv.StartsWith(dir, StringComparison.Ordinal)) named.Add(sv);
                                return v;
                            }).ToArray();
                            var r = method.Invoke(null, args);
                            if (r is Task task) { var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40))); if (done == task) await task; }
                        }
                        catch (Exception) { /* a refusal is the point */ }
                        driven++;
                        await sessions.Ledger.FlushAsync();
                        var now = Tree(dir);
                        var wrong = new List<string>();
                        foreach (var kv in now)
                        {
                            if (original.TryGetValue(kv.Key, out var was)) { if (!was.SequenceEqual(kv.Value)) wrong.Add("changed " + Path.GetRelativePath(dir, kv.Key)); }
                            else if (!named.Any(n => kv.Key == n || kv.Key.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.Ordinal) || kv.Key.StartsWith(n + ".", StringComparison.Ordinal)))
                                wrong.Add("made " + Path.GetRelativePath(dir, kv.Key));
                        }
                        foreach (var kv in original) if (!now.ContainsKey(kv.Key)) wrong.Add("removed " + Path.GetRelativePath(dir, kv.Key));
                        if (wrong.Count > 0)
                        {
                            findings.Add(name + " aimed at " + (aim == null ? "a fresh path" : Path.GetFileName(aim)) + ": " + string.Join(", ", wrong.Take(4)));
                            foreach (var f in Tree(dir).Keys) File.Delete(f);
                            foreach (var d in Directory.GetDirectories(dir).Where(d => Path.GetFileName(d) != ".semanticus")) Directory.Delete(d, true);
                            foreach (var kv in original) { Directory.CreateDirectory(Path.GetDirectoryName(kv.Key)!); File.WriteAllBytes(kv.Key, kv.Value); }
                        }
                        else
                        {
                            // A new file the request named is the person's to keep or not; take it away so the next tool starts from the same folder.
                            foreach (var f in now.Keys.Where(k => !original.ContainsKey(k)).ToList()) { try { File.Delete(f); } catch { } }
                            foreach (var d in Directory.GetDirectories(dir).Where(d => Path.GetFileName(d) != ".semanticus" && !original.Keys.Any(k => k.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToList()) { try { Directory.Delete(d, true); } catch { } }
                        }
                        if (sessions.Current == null || sessions.Current.Id != sessionId)
                        {
                            await engine.OpenAsync(path, discardUnsaved: true);
                            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
                            await sessions.Ledger.FlushAsync();
                            original = Tree(dir);
                        }
                    }
                }
                _out.WriteLine("drove " + driven + " runs");
                Assert.True(findings.Count == 0, "Under Locked these wrote a file unasked:\n" + string.Join("\n", findings));
            }
            finally { engine.Dispose(); }
        }
    }
}
