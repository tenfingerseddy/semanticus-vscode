using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The start plan's core (lane sf-core, design-spec-first-start-v2.md section 6, L6): the start, the empty-folder rule
    /// (defect 1), the unsaved-work guard on both doors, the private scratch, the park hook, the document's life across a
    /// restart, and the view both doors render. Offline: the sign-in and the schema read go through
    /// <see cref="LocalEngine.StartPlanSchemaReaderForTest"/>. Every engine runs with SEMANTICUS_HOME on a scratch folder.
    /// </summary>
    [Collection("restore-root")]
    public sealed class StartPlanCoreTests : IDisposable
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private readonly string _root;
        private readonly string _home;
        private readonly string _origHome;
        private readonly List<LocalEngine> _engines = new List<LocalEngine>();
        private readonly Dictionary<LocalEngine, SessionManager> _managers = new Dictionary<LocalEngine, SessionManager>();
        private SessionManager Mgr(LocalEngine e) => _managers[e];
        private int _reads;

        public StartPlanCoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _home = Path.Combine(_root, "home", ".semanticus");
            Directory.CreateDirectory(_home);
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _home);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            foreach (var e in _engines) { try { e.Dispose(); } catch { } }
            try { Directory.Delete(_root, true); } catch { }
        }

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
            Tables = new[]
            {
                new FabricTable
                {
                    Schema = "dbo", Name = "Sales", KeyColumns = new[] { "OrderId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "OrderId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "ProductId", DataType = "Int64", SqlType = "bigint", Ordinal = 2 },
                        new FabricColumn { Name = "Amount", DataType = "Decimal", SqlType = "decimal", Ordinal = 3 },
                    },
                },
                new FabricTable
                {
                    Schema = "dbo", Name = "Product", KeyColumns = new[] { "ProductId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "ProductId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "Name", DataType = "String", SqlType = "varchar", Ordinal = 2 },
                    },
                },
            },
            ForeignKeys = new[]
            {
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "ProductId", ToSchema = "dbo", ToTable = "Product", ToColumn = "ProductId" },
            },
        };

        private LocalEngine NewEngine(bool pro = true)
        {
            var mgr = new SessionManager();
            var e = new LocalEngine(mgr, new Fake(pro), _root);
            _managers[e] = mgr;
            e.StartPlanSchemaReaderForTest = (req, origin) => { Interlocked.Increment(ref _reads); return Task.FromResult(Schema()); };
            _engines.Add(e);
            return e;
        }

        private static StartModelRequest Req(string folder, string name = "Sales Model") => new StartModelRequest
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = folder, Name = name,
        };

        private string Dir(string name) { var d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }

        // A stand-in for another person's PBIP project: the exact thing the old first save overwrote.
        private string OtherProject()
        {
            var other = Dir("other");
            var def = Path.Combine(other, "Foo.SemanticModel", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(other, "Foo.pbip"), "{}");
            return other;
        }

        private static Dictionary<string, string> Snapshot(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(root, d) + "/"] = "dir";
            return map;
        }

        private static List<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
            after.Where(kv => !before.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key)
                .Concat(before.Keys.Where(k => !after.ContainsKey(k))).ToList();

        // ---- fails first 1: the destination ------------------------------------------------------------------

        [Fact]
        public async Task NonEmptyDestination_IsRefused_AndAnotherProjectIsNeverWritten()
        {
            var e = NewEngine();
            var other = OtherProject();
            var before = Snapshot(other);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(other), "human"));
            Assert.Contains("not empty", ex.Message);

            Assert.Empty(Changed(before, Snapshot(other)));            // not one byte of the other project moved
            Assert.Null(e.StartPlanDocForTest());
            Assert.Equal(0, _reads);                                     // refused before the source was even read
            Assert.False(Directory.Exists(Path.Combine(_home, StartPlanStore.DirName)));
            Assert.Null(await ModelOpen(e));
        }

        [Fact]
        public async Task FolderHoldingOnlyAHiddenFile_IsStillRefused()
        {
            var e = NewEngine();
            var d = Dir("hidden");
            File.WriteAllText(Path.Combine(d, ".gitignore"), "bin/");
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(d), "human"));
            Assert.Equal(new[] { ".gitignore" }, Directory.GetFileSystemEntries(d).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public async Task EmptyOrAbsentFolder_Starts_AndTheModelIsSavedThereAndOpen()
        {
            var e = NewEngine();
            var absent = Path.Combine(_root, "brand", "new");   // does not exist yet
            var view = await e.StartModelFromSqlAsync(Req(absent), "human");

            Assert.True(view.Exists);
            Assert.Equal(StartSectionId.All, view.Sections.Select(s => s.Id).ToArray());
            var def = Path.Combine(absent, "Sales Model.SemanticModel", "definition");
            Assert.True(ModelPathResolver.IsTmdlRoot(def), "the empty model was saved into the new folder");
            Assert.Equal(Path.GetFullPath(def), Path.GetFullPath(Mgr(e).Current.SourcePath));
            Assert.Equal("You have not approved this plan yet.", view.BlockedBecause);   // not parked: its own model is open
        }

        [Theory]
        [InlineData("relative/path", "Name")]
        [InlineData("/tmp/x", "a/b")]
        [InlineData("/tmp/x", "..")]
        [InlineData("/tmp/x", "")]
        public async Task BadFolderOrName_IsRefused_BeforeAnythingHappens(string folder, string name)
        {
            var e = NewEngine();
            await Assert.ThrowsAsync<ArgumentException>(() => e.StartModelFromSqlAsync(Req(folder, name), "human"));
            Assert.Equal(0, _reads);
        }

        [Fact]
        public async Task AFailedSchemaRead_ChangesNothing()
        {
            var e = NewEngine();
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(FabricSchema.FromError("Login failed for user"));
            var d = Dir("fail");
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(d), "human"));
            Assert.Empty(Directory.GetFileSystemEntries(d));
            Assert.Null(await ModelOpen(e));
            Assert.False(Directory.Exists(Path.Combine(_home, StartPlanStore.DirName)));
        }

        // ---- fails first 2: unsaved work, on each door -------------------------------------------------------

        private async Task<string> OpenDirtyModelAsync(LocalEngine e)
        {
            var open = await e.CreateModelAsync("Mine", 0);
            await Mgr(e).Current.MutateAsync("human", "add", m => { m.AddTable("Scratchpad"); });
            Assert.True(Mgr(e).Current.HasUnsavedChanges);
            return open.SessionId;
        }

        [Fact]
        public async Task AgentStart_WithUnsavedWork_IsRefused_AndTheOpenModelIsUntouched()
        {
            var e = NewEngine();
            var id = await OpenDirtyModelAsync(e);
            var d = Dir("agent");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(d), "agent"));
            Assert.Contains("not saved", ex.Message);
            Assert.Equal(id, Mgr(e).Current.Id);
            Assert.True(Mgr(e).Current.HasUnsavedChanges);
            Assert.Empty(Directory.GetFileSystemEntries(d));
            Assert.Equal(0, _reads);
        }

        [Fact]
        public async Task PersonStart_WithUnsavedWork_NeedsTheirOwnConfirmation_AndTheAgentNeverGetsOne()
        {
            var e = NewEngine();
            await OpenDirtyModelAsync(e);
            var d = Dir("person");
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(d), "human"));
            var agent = Req(d); agent.DiscardUnsaved = true;                       // a flag the agent's door ignores
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(agent, "agent"));
            Assert.Empty(Directory.GetFileSystemEntries(d));

            var confirmed = Req(d); confirmed.DiscardUnsaved = true;
            var view = await e.StartModelFromSqlAsync(confirmed, "human");
            Assert.True(view.Exists);
        }

        // ---- fails first 3: the scratch ----------------------------------------------------------------------

        [Fact]
        public async Task ScratchCommits_RaiseNothingOnTheSessionBus_AndNeverTouchThePersonsModel()
        {
            var e = NewEngine();
            await e.StartModelFromSqlAsync(Req(Dir("scratch")), "human");
            var sessions = Mgr(e);
            var person = sessions.Current;
            var rev = person.Revision;
            int changed = 0, swapped = 0, activity = 0;
            sessions.Bus.Changed += _ => Interlocked.Increment(ref changed);
            sessions.Bus.SessionChanged += _ => Interlocked.Increment(ref swapped);
            sessions.Bus.Activity += _ => Interlocked.Increment(ref activity);

            var scratch = await e.OpenStartScratchAsync();
            Assert.NotSame(person, scratch.Session);
            Assert.NotSame(sessions.Bus, scratch.Bus);
            int own = 0;
            scratch.Bus.Changed += _ => Interlocked.Increment(ref own);
            await scratch.Session.MutateAsync("agent", "plan", m => { m.AddTable("OnlyInTheScratch"); });

            Assert.Equal(1, own);                                         // the private bus heard it
            Assert.Equal(0, changed);                                     // SessionManager.Bus heard nothing
            Assert.Equal(0, swapped);
            Assert.Equal(0, activity);
            Assert.Equal(rev, person.Revision);
            Assert.Equal(0, await person.ReadAsync(m => m.Tables.Count));
            Assert.Same(person, sessions.Current);
        }

        // ---- fails first 4: where a start writes ------------------------------------------------------------

        [Fact]
        public async Task AStart_WritesOnlyInTheDestinationAndTheStateDir()
        {
            var e = NewEngine();
            OtherProject();
            Dir("work");
            var dest = Dir("dest");
            var before = Snapshot(_root);

            await e.StartModelFromSqlAsync(Req(dest), "human");
            await e.OpenStartScratchAsync();
            var changed = Changed(before, Snapshot(_root));

            Assert.NotEmpty(changed);
            var allowed = new[] { "dest" + Path.DirectorySeparatorChar, Path.Combine("home", ".semanticus") };
            var stray = changed.Where(c => !allowed.Any(a => c.StartsWith(a, StringComparison.Ordinal)) && c != "dest/").ToList();
            Assert.True(stray.Count == 0, "wrote outside the destination and the state dir: " + string.Join(", ", stray));
            Assert.True(Directory.EnumerateFiles(Path.Combine(_home, StartPlanStore.DirName), "sp-*.json").Any());
            // the working copy lives in the state dir, not in the person's folder
            Assert.DoesNotContain(Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories), p => p.Contains("scratch"));
        }

        // ---- fails first 5: parking --------------------------------------------------------------------------

        [Fact]
        public async Task OpeningAnotherModel_ParksThePlan_AndOpeningItsOwnModelResumesIt()
        {
            var e = NewEngine();
            var started = await e.StartModelFromSqlAsync(Req(Dir("park")), "human");
            var token = e.BeginStartLoop();
            await e.OpenStartScratchAsync();
            Assert.True(e.StartScratchOpenForTest());

            await e.CreateModelAsync("Somewhere Else", 0);

            Assert.True(token.IsCancellationRequested, "the decide loop was stopped");
            Assert.False(e.StartScratchOpenForTest(), "the scratch was drained and dropped");
            var parked = await e.GetStartPlanAsync();
            Assert.True(parked.Exists);
            Assert.False(parked.CanBuild);
            Assert.Contains("Another model is open", parked.BlockedBecause);
            Assert.True(e.StartPlanDocForTest().Parked);
            Assert.Equal(started.Digest, parked.Digest);                  // parking changes nothing the person approved
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.OpenStartScratchAsync());

            await e.OpenAsync(started.Destination.ModelPath);
            var resumed = await e.GetStartPlanAsync();
            Assert.DoesNotContain("Another model", resumed.BlockedBecause ?? "");
            Assert.False(e.StartPlanDocForTest().Parked);
            using var again = await e.OpenStartScratchAsync();
            Assert.Equal(0, await again.Session.ReadAsync(m => m.Tables.Count));
        }

        [Fact]
        public async Task AnOpenThatFails_DoesNotLeaveThePlanParked()
        {
            var e = NewEngine();
            await e.StartModelFromSqlAsync(Req(Dir("failopen")), "human");
            await Assert.ThrowsAnyAsync<Exception>(() => e.OpenAsync(Path.Combine(_root, "no-such-model.bim")));
            var view = await e.GetStartPlanAsync();
            Assert.DoesNotContain("Another model", view.BlockedBecause ?? "");
            Assert.False(e.StartPlanDocForTest().Parked);
        }

        // ---- fails first 6: a restart ------------------------------------------------------------------------

        [Fact]
        public async Task TheDocument_SurvivesARestart()
        {
            var first = NewEngine();
            var started = await first.StartModelFromSqlAsync(Req(Dir("restart")), "human");
            var doc = first.StartPlanDocForTest();
            first.Dispose();

            var second = NewEngine();                                      // a new process, the same state dir
            var back = await second.GetStartPlanAsync();
            Assert.True(back.Exists);
            Assert.Equal(started.Id, back.Id);
            Assert.Equal(started.Digest, back.Digest);
            Assert.Contains("No model is open", back.BlockedBecause);     // parked until its model is opened again
            var again = second.StartPlanDocForTest();
            Assert.Equal(doc.IdentitySeed, again.IdentitySeed);
            Assert.Equal(doc.Manifest.Tables.Select(t => t.Identity), again.Manifest.Tables.Select(t => t.Identity));
            Assert.Equal(2, again.Schema.Tables.Length);

            await second.OpenAsync(started.Destination.ModelPath);
            var resumed = await second.GetStartPlanAsync();
            Assert.DoesNotContain("Open", resumed.BlockedBecause ?? "");
            Assert.Equal(started.Digest, resumed.Digest);
        }

        // ---- verifier's list: two at once, one plan at a time, discard ---------------------------------------

        [Fact]
        public async Task TwoStartsAtOnce_OneRuns_TheOtherIsRefusedAndWritesNothing()
        {
            var e = NewEngine();
            var gate = new TaskCompletionSource<bool>();
            var entered = new TaskCompletionSource<bool>();
            e.StartPlanSchemaReaderForTest = async (r, o) => { entered.TrySetResult(true); await gate.Task; return Schema(); };
            var a = Dir("two-a");
            var b = Dir("two-b");

            var first = e.StartModelFromSqlAsync(Req(a, "A"), "human");
            await entered.Task;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(Req(b, "B"), "agent"));
            Assert.Contains("already running", ex.Message);
            gate.SetResult(true);
            Assert.True((await first).Exists);

            Assert.Empty(Directory.GetFileSystemEntries(b));
            Assert.Equal("A", e.StartPlanDocForTest().Destination.Name);
        }

        [Fact]
        public async Task OnePlanAtATime_AStartOverAnUnbuiltPlan_ReplacesIt()
        {
            // K24 (9 Oct): the old plan was never built, so a new start just starts fresh. Nothing to discard first.
            var e = NewEngine();
            var first = await e.StartModelFromSqlAsync(Req(Dir("one"), "Gold"), "human");
            var next = Dir("two");
            var second = await e.StartModelFromSqlAsync(Req(next, "Gold"), "human");
            Assert.True(second.Exists);
            Assert.Equal(next, e.StartPlanDocForTest().Destination.Folder);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(_home, StartPlanStore.DirName), "sp-*.json"));
            Assert.True(second.Notice == null || !second.Notice.Contains("Gold 2"));   // another folder: no clash, no rename
        }

        [Fact]
        public async Task AStartWhoseNameIsTakenInTheFolder_PicksAFreeNameAndSaysSo()
        {
            // K24: Gold.SemanticModel is already in the folder from an earlier test start. The new one is Gold 2, in one line, and the old model is untouched.
            var e = NewEngine();
            var folder = Dir("same");
            var first = await e.StartModelFromSqlAsync(Req(folder, "Gold"), "human");
            var before = Snapshot(first.Destination.ModelPath);

            var again = await e.StartModelFromSqlAsync(Req(folder, "Gold"), "human");

            Assert.Equal("Gold 2", again.Destination.Name);
            Assert.Equal(Path.Combine(folder, "Gold 2", "Gold 2.SemanticModel"), again.Destination.ModelPath);   // one folder holds one model, as a first save puts it
            Assert.Contains("Gold 2", again.Notice);
            Assert.Empty(Changed(before, Snapshot(first.Destination.ModelPath)));
            Assert.Equal("Gold 2", e.StartPlanDocForTest().Destination.Name);

            // And after the first plan was discarded, the same.
            await e.DiscardStartPlanAsync("human");
            var third = await e.StartModelFromSqlAsync(Req(folder, "Gold"), "human");
            Assert.Equal("Gold 3", third.Destination.Name);
        }

        [Fact]
        public async Task Discard_DropsThePlanAndItsScratch_ButKeepsThePersonsEmptyModel()
        {
            var e = NewEngine();
            var v = await e.StartModelFromSqlAsync(Req(Dir("disc")), "human");
            var scratch = await e.OpenStartScratchAsync();
            var scratchDir = scratch.Dir;
            Assert.True(Directory.Exists(scratchDir));

            await e.DiscardStartPlanAsync("human");

            Assert.False(Directory.Exists(scratchDir));
            Assert.False(e.StartScratchOpenForTest());
            Assert.True(ModelPathResolver.IsTmdlRoot(Path.Combine(v.Destination.ModelPath, "definition")));
            Assert.False((await e.GetStartPlanAsync()).Exists);
        }

        [Fact]
        public async Task TheAssistant_CannotDiscardAPlanThatHoldsThePersonsChoices()
        {
            var e = NewEngine();
            await e.StartModelFromSqlAsync(Req(Dir("keep")), "human");
            e.StartPlanDocForTestMutate(d => d.Pins["column:Sales/Amount"] = "Amount (USD)");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DiscardStartPlanAsync("agent"));
            Assert.Contains("only the person", ex.Message);
            Assert.True((await e.GetStartPlanAsync()).Exists);
            Assert.False((await e.DiscardStartPlanAsync("human")).Exists);
        }

        // ---- Free and Pro ------------------------------------------------------------------------------------

        [Fact]
        public async Task Pro_WaitsForTheGrant_Free_HasNoDecideLoop()
        {
            var pro = await NewEngine(pro: true).StartModelFromSqlAsync(Req(Dir("pro")), "human");
            Assert.Equal(StartDecideState.WaitingGrant, pro.Decide.State);
            Assert.Contains(pro.NeedsYou, n => n.Kind == "grant");
            Assert.Contains("Wh", pro.NeedsYou.First(n => n.Kind == "grant").Text);
        }

        [Fact]
        public async Task Free_GetsTheSameStart_WithoutJev()
        {
            var free = await NewEngine(pro: false).StartModelFromSqlAsync(Req(Dir("free")), "human");
            Assert.Equal(StartDecideState.Off, free.Decide.State);
            Assert.DoesNotContain(free.NeedsYou, n => n.Kind == "grant");
            Assert.Equal(StartSectionId.All, free.Sections.Select(s => s.Id).ToArray());
        }

        // ---- the view and its digest (pure) ------------------------------------------------------------------

        private static StartPlanDoc Doc() => new StartPlanDoc
        {
            Id = "sp-00000001", Destination = new StartDestination { Folder = "/f", Name = "M", ModelPath = "/f/M.SemanticModel" },
            Source = new StartSource { Server = "s", Database = "d" }, IdentitySeed = "seed",
            Manifest = new StructureManifest
            {
                Tables = { new ManifestTable { Name = "Sales", SourceName = "FactSales", SourceSchema = "dbo", Columns = { new ManifestColumn { Name = "Amount" } } } },
            },
            Lines =
            {
                Line("set_description", "column:Sales/Amount", "Total paid", StartDecidedBy.Assistant),
                Line("set_measure_format", "measure:Sales/Total", "#,0", StartDecidedBy.Jev),
                Line("bpa_fix", "column:Sales/Amount", "hide", StartDecidedBy.Rule),
                Line("rename", "table:Sales", "Sales", StartDecidedBy.Person),
            },
        };

        private static StartLine Line(string kind, string objectRef, string after, string by) => new StartLine
        {
            Item = new ChangeItem { Kind = kind, ObjectRef = objectRef, After = after, Target = kind }, DecidedBy = by,
        };

        [Fact]
        public void TheView_HasFiveSectionsInOrder_WithLinesCountedByWhoDecidedThem()
        {
            var v = StartPlanViewBuilder.Build(Doc());
            Assert.Equal(new[] { "Tables and columns", "Links and dates", "Measures", "Words for Copilot", "Best practice" }, v.Sections.Select(s => s.Title).ToArray());
            var words = v.Sections.Single(s => s.Id == StartSectionId.Words);
            Assert.Equal(1, words.Counts.Assistant);
            Assert.Equal(1, v.Sections.Single(s => s.Id == StartSectionId.Measures).Counts.Jev);
            Assert.Equal(1, v.Sections.Single(s => s.Id == StartSectionId.BestPractice).Counts.Rule);
            Assert.Equal(1, v.Sections.Single(s => s.Id == StartSectionId.Tables).Counts.You);
            Assert.Equal("FactSales", v.Sections.Single(s => s.Id == StartSectionId.Tables).Groups.First(g => g.Table == "Sales").SourceName);
        }

        [Fact]
        public void TheDigest_IsStable_AndMovesOnAnyEdit_AndIgnoresWhatIsNotThePlan()
        {
            var a = Doc();
            var d1 = StartPlanViewBuilder.Digest(a);
            Assert.Equal(d1, StartPlanViewBuilder.Digest(StartPlanStore.Copy(a)));

            var progress = StartPlanStore.Copy(a);
            progress.Decide.Round = 4; progress.UpdatedUtc = DateTime.UtcNow; progress.Preview.AiReadiness = 77;
            progress.Approval = new StartApproval { Digest = "x" };
            Assert.Equal(d1, StartPlanViewBuilder.Digest(progress));       // not the plan itself

            var edited = StartPlanStore.Copy(a); edited.Lines[0].Item.After = "Total paid in dollars";
            Assert.NotEqual(d1, StartPlanViewBuilder.Digest(edited));
            var dropped = StartPlanStore.Copy(a); dropped.Lines[1].Kept = false;
            Assert.NotEqual(d1, StartPlanViewBuilder.Digest(dropped));
            var pinned = StartPlanStore.Copy(a); pinned.Pins["x"] = "y";
            Assert.NotEqual(d1, StartPlanViewBuilder.Digest(pinned));
            var excluded = StartPlanStore.Copy(a); excluded.Manifest.Tables[0].Include = false;
            Assert.NotEqual(d1, StartPlanViewBuilder.Digest(excluded));
        }

        [Fact]
        public void CanBuild_NeedsAnApprovalOfTheExactDigest_AndAnEditVoidsIt()
        {
            var d = Doc();
            Assert.Equal("You have not approved this plan yet.", StartPlanViewBuilder.Build(d).BlockedBecause);
            d.Approval = new StartApproval { Digest = StartPlanViewBuilder.Digest(d), Utc = DateTime.UtcNow };
            Assert.Contains("working out", StartPlanViewBuilder.Build(d).BlockedBecause);   // approved, but the preview of what it builds is not made yet
            d.Preview = new StartPreview { ProjectionDigest = "p", PlanDigest = StartPlanViewBuilder.Digest(d) };
            var ok = StartPlanViewBuilder.Build(d);
            Assert.True(ok.CanBuild);
            Assert.Null(ok.BlockedBecause);

            d.Lines[0].Item.After = "different words";
            var stale = StartPlanViewBuilder.Build(d);
            Assert.False(stale.CanBuild);
            Assert.Contains("approve it again", stale.BlockedBecause);

            d.Approval = null;
            d.Decide.State = StartDecideState.Deciding;
            Assert.Contains("still working", StartPlanViewBuilder.Build(d).BlockedBecause);
            Assert.Contains("Another model", StartPlanViewBuilder.Build(d, "Another model is open.").BlockedBecause);
        }

        [Fact]
        public void ACompositeKey_IsOneNeedsYouLine_NotALink()
        {
            var d = Doc();
            d.Manifest.Links.Add(new ManifestLink
            {
                Identity = "k1", FromTable = "Sales", FromColumns = new[] { "TenantId", "ProductId" }, ToTable = "Product",
                ToColumns = new[] { "TenantId", "ProductId" }, Unresolved = true,
            });
            var v = StartPlanViewBuilder.Build(d);
            var need = Assert.Single(v.NeedsYou, n => n.Kind == "composite_key");
            Assert.Contains("TenantId", need.Text);
            Assert.Empty(v.Sections.Single(s => s.Id == StartSectionId.Links).Groups.SelectMany(g => g.Lines));
        }

        [Fact]
        public void ViewText_HasNoEmDashes()
        {
            var d = Doc();
            d.Decide.State = StartDecideState.WaitingGrant;
            var json = System.Text.Json.JsonSerializer.Serialize(StartPlanViewBuilder.Build(d, "No model is open. Open M to carry on with this plan."));
            Assert.DoesNotContain("—", json);
        }

        private Task<string> ModelOpen(LocalEngine e) => Task.FromResult(Mgr(e).Current?.Id);
    }
}
