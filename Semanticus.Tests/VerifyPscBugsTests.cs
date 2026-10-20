using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/psc-bugs (55d5a433). Attacks on the lane's four fixes: a first save never changes another project's
    /// model whatever shape the path takes; a relationship acceptance follows every kind of rename and the orphan count stays
    /// honest; FMT-SORTBY still counts real month and weekday labels.
    /// </summary>
    public sealed class VerifyPscBugsTests
    {
        private const string Why = "Kept off on purpose; a measure uses it.";

        private static string Scratch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vpsc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A PBIP project "Existing" in <paramref name="root"/>; returns its definition folder.</summary>
        private static string SeedProject(string root)
        {
            var def = Path.Combine(root, "Existing.SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(def, "tables"));
            File.WriteAllText(Path.Combine(root, "Existing.pbip"), "{}");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database Existing\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(def, "tables", "Keep.tmdl"), "table Keep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
            return def;
        }

        private static string Snapshot(string dir) => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(dir, p) + "=" + File.ReadAllText(p)));

        private static async Task<DecisionFixture> NewModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Brand New", 1604);
            await f.Engine.CreateTableAsync("Only", "human");
            return f;
        }

        // ---- item 1: a first save never changes another project's model ----------------------------------------------------

        /// <summary>The fix guards only the folder formats. A first save asked for as BIM still resolves the folder through the PBIP
        /// resolver to the other project's definition/, and the TMDL-root guard then turns the save back into TMDL over it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("BIM", "root")]
        [InlineData("BIM", "pbip")]
        [InlineData("BIM", "semanticmodel")]
        [InlineData("ModelSchemaOnly", "root")]
        public async Task A_first_save_in_any_format_never_replaces_another_projects_model(string format, string aim)
        {
            var root = Scratch();
            try
            {
                var def = SeedProject(root);
                var before = Snapshot(def);
                using var f = await NewModelAsync();
                var path = aim switch
                {
                    "pbip" => Path.Combine(root, "Existing.pbip"),
                    "semanticmodel" => Path.Combine(root, "Existing.SemanticModel"),
                    _ => root,
                };
                string landed = null;
                try { landed = (await f.Engine.SaveAsync(path, format, false, "human")).Path; }
                catch (Exception) { /* a refusal is a right answer */ }
                Assert.True(before == Snapshot(def), $"a first {format} save aimed at {aim} changed the other project's model (landed at {landed})");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_save_into_a_folder_holding_only_hidden_files_goes_into_the_models_own_folder()
        {
            var root = Scratch();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
                using var f = await NewModelAsync();
                var saved = await f.Engine.SaveAsync(root, "TMDL", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New"), saved.Path);
                Assert.Equal("ref: refs/heads/main\n", File.ReadAllText(Path.Combine(root, ".git", "HEAD")));
                Assert.False(File.Exists(Path.Combine(root, "model.tmdl")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_save_into_a_symlink_to_another_project_never_changes_that_project()
        {
            var root = Scratch();
            try
            {
                var other = Path.Combine(root, "other");
                Directory.CreateDirectory(other);
                var def = SeedProject(other);
                var before = Snapshot(def);
                var link = Path.Combine(root, "work");
                Directory.CreateSymbolicLink(link, other);
                using var f = await NewModelAsync();
                string landed = null;
                try { landed = (await f.Engine.SaveAsync(link, "TMDL", false, "human")).Path; } catch (InvalidOperationException) { }
                Assert.True(before == Snapshot(def), "the other project's model changed; landed at " + landed);
                if (landed != null) Assert.True(File.Exists(Path.Combine(landed, "model.tmdl")), "the result path is not where the model was written: " + landed);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_save_whose_model_folder_name_is_a_symlink_to_another_project_is_redirected_to_a_numbered_name_and_that_project_is_untouched()
        {
            var root = Scratch();
            try
            {
                var other = Path.Combine(root, "other");
                Directory.CreateDirectory(other);
                var def = SeedProject(other);
                var before = Snapshot(def);
                var work = Path.Combine(root, "work");
                Directory.CreateDirectory(work);
                Directory.CreateSymbolicLink(Path.Combine(work, "Brand New"), Path.Combine(other, "Existing.SemanticModel"));
                using var f = await NewModelAsync();
                var saved = await f.Engine.SaveAsync(work, "TMDL", false, "human");
                // Kane, 2 Oct: redirect, never refuse. The name the link holds is taken, so the model takes the next number.
                Assert.Equal(Path.Combine(work, "Brand New 2"), saved.Path);
                Assert.Contains("Brand New 2", saved.Note);
                Assert.True(before == Snapshot(def));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("semanticmodel")]
        [InlineData("definition")]
        public async Task A_first_save_aimed_inside_another_project_never_changes_its_files(string aim)
        {
            var root = Scratch();
            try
            {
                var def = SeedProject(root);
                var before = Snapshot(def);
                using var f = await NewModelAsync();
                var path = aim == "definition" ? def : Path.Combine(root, "Existing.SemanticModel");
                string landed = null;
                try { landed = (await f.Engine.SaveAsync(path, "TMDL", false, "human")).Path; } catch (InvalidOperationException) { }
                var after = string.Join("\n", Directory.EnumerateFiles(def, "*", SearchOption.AllDirectories)
                    .Where(p => landed == null || !p.StartsWith(landed + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(def, p) + "=" + File.ReadAllText(p)));
                Assert.True(before == after, "a file of the other project changed; landed at " + landed);
                // The other project still opens as itself: one table, Keep, and nothing of the new model.
                var reopened = await f.Engine.OpenAsync(Path.Combine(root, "Existing.pbip"), discardUnsaved: true);
                Assert.Equal(1, reopened.Tables);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("notes.txt")]
        [InlineData("Brand New")]
        public async Task A_first_save_aimed_at_a_file_or_onto_a_file_named_like_the_model_is_redirected_and_the_file_kept(string file)
        {
            var root = Scratch();
            try
            {
                var p = Path.Combine(root, file);
                File.WriteAllText(p, "mine");
                using var f = await NewModelAsync();
                var aim = file == "notes.txt" ? p : root;
                var saved = await f.Engine.SaveAsync(aim, "TMDL", false, "human");
                Assert.Equal("mine", File.ReadAllText(p));
                Assert.NotEqual(Path.GetFullPath(p), saved.Path);
                Assert.Equal(root, Path.GetDirectoryName(saved.Path));
                Assert.True(File.Exists(Path.Combine(saved.Path, "model.tmdl")) || File.Exists(Path.Combine(saved.Path, "database.tmdl")));
                Assert.Contains(saved.Path, saved.Note);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ---- item 2: a relationship acceptance follows a rename; the orphan count is honest ---------------------------------

        private static async Task<DecisionFixture> RelModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("VPsc2", 1604);
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "DUE_DT", "DateTime", null, "human");
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/INV_DT", "column:dim_date/CAL_DT", null, true, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/DUE_DT", "column:dim_date/CAL_DT", null, false, "human");
            var r = (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE").ObjectRef;
            await e.WaiveFindingAsync("air", "REL-INACTIVE", r, Why, "human");
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("from-column")]
        [InlineData("to-table")]
        [InlineData("both-tables")]
        [InlineData("both-columns-and-tables")]
        public async Task A_relationship_acceptance_follows_every_rename_and_nothing_reads_as_orphaned(string what)
        {
            using var f = await RelModelAsync();
            var e = f.Engine;
            if (what.Contains("columns")) { await e.RenameObjectAsync("column:fact_invoice/DUE_DT", "Due Date", "agent"); await e.RenameObjectAsync("column:dim_date/CAL_DT", "Day", "agent"); }
            if (what == "from-column") await e.RenameObjectAsync("column:fact_invoice/DUE_DT", "Due Date", "agent");
            if (what != "from-column") await e.RenameObjectAsync("table:dim_date", "Date", "agent");
            if (what.StartsWith("both")) await e.RenameObjectAsync("table:fact_invoice", "Invoices", "agent");
            var scan = await e.AiReadinessScanAsync();
            var row = scan.Findings.Single(x => x.RuleId == "REL-INACTIVE");
            Assert.True(row.Waived, $"the acceptance did not follow ({what}): {row.ObjectRef}");
            Assert.Equal(0, scan.OrphanedWaiverCount);
            Assert.Equal(0, (await e.BpaScanAsync()).OrphanedWaiverCount);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_relationship_acceptance_follows_a_rename_after_a_save_and_reopen()
        {
            var dir = Scratch();
            try
            {
                using var f = await RelModelAsync();
                var e = f.Engine;
                var target = Path.Combine(dir, "m");
                await e.SaveAsync(target, "TMDL", false, "human");
                await e.OpenAsync(target, discardUnsaved: true);
                await e.RenameObjectAsync("table:fact_invoice", "Invoices", "agent");
                await e.RenameObjectAsync("table:dim_date", "Date", "agent");
                var scan = await e.AiReadinessScanAsync();
                Assert.True(scan.Findings.Single(x => x.RuleId == "REL-INACTIVE").Waived, "the relationship's identity did not survive a save and reopen");
                Assert.Equal(0, scan.OrphanedWaiverCount);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deleted_relationships_acceptance_is_counted_once_as_orphaned_and_not_as_accepted()
        {
            using var f = await RelModelAsync();
            var e = f.Engine;
            var rel = (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE").ObjectRef;
            await e.DeleteObjectAsync(rel, "human");
            var scan = await e.AiReadinessScanAsync();
            Assert.Equal(1, scan.OrphanedWaiverCount);
            Assert.DoesNotContain(scan.Findings, x => x.RuleId == "REL-INACTIVE" && x.IsAccepted());
        }

        // ---- item 7: real month and weekday labels are still counted -----------------------------------------------------

        [Theory]
        [InlineData("Month Name")]
        public Task A_real_month_or_weekday_label_is_still_counted_by_the_sort_order_rule_sample(string name) => A_real_month_or_weekday_label_is_still_counted_by_the_sort_order_rule(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Weekday Name")]
        [InlineData("Month Name")]
        [InlineData("MonthName")]
        [InlineData("Day of Week Name")]
        [InlineData("Fiscal Month")]
        public async Task A_real_month_or_weekday_label_is_still_counted_by_the_sort_order_rule(string name)
        {
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.CreateModelAsync("SortLabel2", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", name, "String", name, "human");
            await e.CreateColumnAsync("table:Date", "IsMonthEnd", "String", "IsMonthEnd", "human");
            await e.CreateColumnAsync("table:Date", "Weekday YN", "String", "Weekday YN", "human");
            PrepForAiReader.Invalidate(sessions.Current.Model);
            var eval = ReadinessRuleSet.Default().Single(r => r.Id == "FMT-SORTBY").Evaluate(sessions.Current.Model);
            Assert.Equal(1, eval.Applicable);
            Assert.Equal(name, Assert.Single(eval.Violations).ObjectName);
        }
    }

    /// <summary>Builds a directory tree once per test class (an IClassFixture) and hands every test the same read-only template to copy.
    /// A first-save test needs a workspace of several models, and building it (a few engine calls and saves) is most of what a row costs;
    /// the rows only COPY it. The tree is deleted in Dispose, inside the run, never as the process exits.</summary>
    public sealed class DirTemplate : IDisposable
    {
        private readonly System.Threading.SemaphoreSlim _gate = new System.Threading.SemaphoreSlim(1, 1);
        private string _root, _built;

        /// <summary>The template's folder. <paramref name="build"/> gets a fresh empty folder to build in and returns the template path inside it.</summary>
        public async Task<string> GetAsync(Func<string, Task<string>> build)
        {
            await _gate.WaitAsync();
            try
            {
                if (_built != null) return _built;
                _root = Path.Combine(Path.GetTempPath(), "dtpl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_root);
                return _built = await build(_root);
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            try { if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Verifier, m11/psc-bugs round 2 (2a6ed904). The whole class of "a never-saved model's first save lands in another
    /// project": every save format, aimed at every shape of target (a workspace holding a .pbip project, the .pbip, the
    /// .SemanticModel envelope, its definition/ folder, a flat TMDL root, a folder with a .bim, the .bim itself, a Tabular
    /// Editor folder model, a path inside each, a symlink to each, a dangling symlink into one, a relative path, a path
    /// with .. segments and a trailing slash), through four doors: the engine, save_model with overwrite, the Studio RPC
    /// on an agent connection claiming the dialog flag, and the Studio RPC on a person's connection with the dialog flag.
    /// No other project's file may change or gain an entry (the one exception: a person who picked an existing .bim in the
    /// save dialog replaces that file), and a save that succeeds must name the place that really holds the model.
    /// </summary>
    public sealed class VerifyPscBugsRound2Tests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public VerifyPscBugsRound2Tests(DirTemplate tpl) => _tpl = tpl;

        private static readonly string[] Formats = { "TMDL", "BIM", "ModelSchemaOnly", "folder" };
        private static readonly string[] Protected = { "Existing.pbip", "Existing.SemanticModel", "flat", Path.Combine("bimdir", "model.bim"), "tefolder" };

        private static Task<SaveResult> SaveVia(string door, LocalEngine e, string path, string format) => door switch
        {
            "engine" => e.SaveAsync(path, format, false, "human"),
            "mcp-overwrite" => McpTools.SaveModel(e, path, format, true),
            "rpc-agent-dialog" => new EngineRpcTarget(e, "agent").save(path, format, true, true),
            _ => new EngineRpcTarget(e, "human").save(path, format, false, true),
        };

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        private static string Snap(string ws, bool skipBim)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var p in Protected)
            {
                if (skipBim && p.EndsWith(".bim", StringComparison.Ordinal)) continue;
                var full = Path.Combine(ws, p);
                if (File.Exists(full)) { lines.Add(p + "=" + File.ReadAllText(full).Replace("\n", "|NL|")); continue; }
                if (!Directory.Exists(full)) { lines.Add(p + " MISSING"); continue; }
                // A Tabular Editor folder model is not a shape the check knows: a new entry inside it is allowed (it still
                // opens as itself, A_first_save_aimed_at_or_inside_a_tabular_editor_folder_model_leaves_it_whole), but every file
                // it had must keep its bytes. Each line it had is marked so the comparison reads them as a subset.
                var te = p == "tefolder";
                foreach (var x in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                    if (!te || (File.Exists(x) && !x.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar)))
                        lines.Add((te ? "TE " : "") + Path.GetRelativePath(ws, x) + (File.Exists(x) ? "=" + File.ReadAllText(x).Replace("\n", "|NL|") : "/"));
            }
            return string.Join("\n", lines);
        }

        private static System.Collections.Generic.IEnumerable<(string Key, string Path)> Targets(string ws, string links)
        {
            var sep = Path.DirectorySeparatorChar.ToString();
            string W(params string[] parts) => Path.Combine(new[] { ws }.Concat(parts).ToArray());
            string L(params string[] parts) => Path.Combine(new[] { links }.Concat(parts).ToArray());
            string Rel(string p) => Path.GetRelativePath(Environment.CurrentDirectory, p);
            var sm = W("Existing.SemanticModel");
            var def = W("Existing.SemanticModel", "definition");
            yield return ("workspace", ws);
            yield return ("workspace/", ws + sep);
            yield return ("pbip", W("Existing.pbip"));
            yield return ("sm", sm);
            yield return ("sm/", sm + sep);
            yield return ("sm/definition.pbism", Path.Combine(sm, "definition.pbism"));
            yield return ("def", def);
            yield return ("def/", def + sep);
            yield return ("def/tables", Path.Combine(def, "tables"));
            yield return ("def/model.tmdl", Path.Combine(def, "model.tmdl"));
            yield return ("sm/fresh", Path.Combine(sm, "fresh"));
            yield return ("def/fresh", Path.Combine(def, "fresh"));
            yield return ("def/tables/fresh", Path.Combine(def, "tables", "fresh"));
            yield return ("sm/new.bim", Path.Combine(sm, "new.bim"));
            yield return ("def/new.bim", Path.Combine(def, "new.bim"));
            yield return ("flat", W("flat"));
            yield return ("flat/", W("flat") + sep);
            yield return ("flat/tables", W("flat", "tables"));
            yield return ("flat/fresh", W("flat", "fresh"));
            yield return ("flat/new.bim", W("flat", "new.bim"));
            yield return ("flat/model.tmdl", W("flat", "model.tmdl"));
            yield return ("bimdir", W("bimdir"));
            yield return ("bimdir/model.bim", W("bimdir", "model.bim"));
            yield return ("bimdir/fresh", W("bimdir", "fresh"));
            yield return ("tefolder", W("tefolder"));
            yield return ("tefolder/tables", W("tefolder", "tables"));
            yield return ("tefolder/fresh", W("tefolder", "fresh"));
            yield return ("tefolder/new.bim", W("tefolder", "new.bim"));
            yield return ("tefolder/database.json", W("tefolder", "database.json"));
            yield return ("dotdot flat/../sm", ws + sep + "flat" + sep + ".." + sep + "Existing.SemanticModel");
            yield return ("dotdot bimdir/../pbip", ws + sep + "bimdir" + sep + ".." + sep + "Existing.pbip");
            yield return ("dotdot ./bimdir/../flat/", ws + sep + "." + sep + "bimdir" + sep + ".." + sep + "flat" + sep);
            yield return ("dotdot def/../../flat/fresh", def + sep + ".." + sep + ".." + sep + "flat" + sep + "fresh");
            yield return ("rel workspace", Rel(ws));
            yield return ("rel sm", Rel(sm));
            yield return ("rel def", Rel(def));
            yield return ("rel flat/new.bim", Rel(W("flat", "new.bim")));
            yield return ("rel bimdir/model.bim", Rel(W("bimdir", "model.bim")));
            yield return ("link workspace", L("l-ws"));
            yield return ("link workspace/", L("l-ws") + sep);
            yield return ("link sm", L("l-sm"));
            yield return ("link def", L("l-def"));
            yield return ("link def/tables", L("l-def", "tables"));
            yield return ("link sm/fresh", L("l-sm", "fresh"));
            yield return ("link def/fresh", L("l-def", "fresh"));
            yield return ("link flat", L("l-flat"));
            yield return ("link flat/new.bim", L("l-flat", "new.bim"));
            yield return ("link bim", L("l-bim"));
            yield return ("link pbip", L("l-pbip"));
            yield return ("link tefolder", L("l-tef"));
            yield return ("link tefolder/tables", L("l-tef", "tables"));
            yield return ("link dangling into def", L("l-ghost"));
            yield return ("link dangling .bim into sm", L("l-ghost.bim"));
            yield return ("link ws/../sm via link", L("l-ws") + sep + "Existing.SemanticModel");
        }

        private static void MakeLinks(string ws, string links)
        {
            Directory.CreateDirectory(links);
            var def = Path.Combine(ws, "Existing.SemanticModel", "definition");
            Directory.CreateSymbolicLink(Path.Combine(links, "l-ws"), ws);
            Directory.CreateSymbolicLink(Path.Combine(links, "l-sm"), Path.Combine(ws, "Existing.SemanticModel"));
            Directory.CreateSymbolicLink(Path.Combine(links, "l-def"), def);
            Directory.CreateSymbolicLink(Path.Combine(links, "l-flat"), Path.Combine(ws, "flat"));
            Directory.CreateSymbolicLink(Path.Combine(links, "l-tef"), Path.Combine(ws, "tefolder"));
            File.CreateSymbolicLink(Path.Combine(links, "l-bim"), Path.Combine(ws, "bimdir", "model.bim"));
            File.CreateSymbolicLink(Path.Combine(links, "l-pbip"), Path.Combine(ws, "Existing.pbip"));
            File.CreateSymbolicLink(Path.Combine(links, "l-ghost"), Path.Combine(def, "ghost"));
            File.CreateSymbolicLink(Path.Combine(links, "l-ghost.bim"), Path.Combine(ws, "Existing.SemanticModel", "ghost.bim"));
        }

        /// <summary>The template workspace: a .pbip project, a flat TMDL root, a folder with a real .bim, and a real Tabular
        /// Editor folder model (the engine writes the last two, so they open).</summary>
        private static async Task<string> TemplateAsync(string root)
        {
            var ws = Path.Combine(root, "tmpl");
            var def = Path.Combine(ws, "Existing.SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(def, "tables"));
            File.WriteAllText(Path.Combine(ws, "Existing.pbip"), "{}");
            File.WriteAllText(Path.Combine(ws, "Existing.SemanticModel", "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database Existing\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(def, "tables", "Keep.tmdl"), "table Keep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
            var flat = Path.Combine(ws, "flat");
            Directory.CreateDirectory(Path.Combine(flat, "tables"));
            File.WriteAllText(Path.Combine(flat, "database.tmdl"), "database Flat\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(flat, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(flat, "tables", "FlatKeep.tmdl"), "table FlatKeep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
            using (var f = new DecisionFixture(TestEntitlements.Pro))
            {
                await f.Engine.CreateModelAsync("BimOne", 1604);
                await f.Engine.CreateTableAsync("BimKeep", "human");
                await f.Engine.SaveAsync(Path.Combine(ws, "bimdir", "model.bim"), "BIM", false, "human");
                File.WriteAllText(Path.Combine(ws, "bimdir", "readme.txt"), "mine");
                await f.Engine.CreateModelAsync("TeOne", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("TeKeep", "human");
                await f.Engine.CreateColumnAsync("table:TeKeep", "Id", "Int64", "Id", "human");
                await f.Engine.CreateMeasureAsync("table:TeKeep", "Rows", "COUNTROWS ( TeKeep )", "human");
                // A real Tabular Editor split folder (tables/<name>/<name>.json, columns and measures beside it), the shape
                // TE2 writes with its default folder options; the engine's own "folder" save keeps everything in database.json.
                var sess = f.Sessions.Current;
                await sess.RunAsync(() => { sess.Save(Path.Combine(ws, "tefolder"), TabularEditor.TOMWrapper.SaveFormat.TabularEditorFolder, TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder, false); return true; });
            }
            Assert.True(File.Exists(Path.Combine(ws, "bimdir", "model.bim")));
            Assert.True(File.Exists(Path.Combine(ws, "tefolder", "database.json")));
            Assert.True(Directory.Exists(Path.Combine(ws, "tefolder", "tables", "TeKeep")), "the template's Tabular Editor folder is not split: " + string.Join(", ", Directory.EnumerateFileSystemEntries(Path.Combine(ws, "tefolder"), "*", SearchOption.AllDirectories)));
            return ws;
        }

        /// <summary>Lean suite: the default tier runs one row per target shape family on one door (the full four-door matrix is the sweep).
        /// pbip file, flat TMDL root, a .bim in a folder, a Tabular Editor folder; three formats among them.</summary>
        [Fact]
        public Task A_first_save_at_one_target_of_each_shape_never_changes_another_projects_files_sample() => MatrixAsync("mcp-overwrite",
            (format, key) => (format, key) is ("TMDL", "pbip") or ("BIM", "flat") or ("folder", "bimdir/model.bim") or ("TMDL", "tefolder/tables"));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("engine")]
        [InlineData("mcp-overwrite")]
        [InlineData("rpc-agent-dialog")]
        [InlineData("rpc-human-dialog")]
        public Task A_first_save_in_any_format_at_any_shape_of_target_never_changes_another_projects_files(string door) => MatrixAsync(door, (_, _) => true);

        private async Task MatrixAsync(string door, Func<string, string, bool> pick)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            var log = new System.Collections.Generic.List<string>();
            var failures = new System.Collections.Generic.List<string>();
            try
            {
                var tmpl = await _tpl.GetAsync(TemplateAsync);
                using var f = new DecisionFixture(TestEntitlements.Pro);
                var n = 0;
                foreach (var format in Formats)
                foreach (var key in Targets("W", "L").Select(t => t.Key).ToArray())
                {
                    if (!pick(format, key)) continue;
                    var caseDir = Path.Combine(root, "c" + (n++));
                    var ws = Path.Combine(caseDir, "ws");
                    var links = Path.Combine(caseDir, "links");
                    CopyTree(tmpl, ws);
                    MakeLinks(ws, links);
                    var path = Targets(ws, links).Single(t => t.Key == key).Path;
                    var personPickedBim = door == "rpc-human-dialog" && (key.EndsWith("bimdir/model.bim", StringComparison.Ordinal) || key == "link bim");
                    var before = Snap(ws, personPickedBim);
                    await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                    await f.Engine.CreateTableAsync("Only", "human");
                    SaveResult r = null;
                    string outcome;
                    try { r = await SaveVia(door, f.Engine, path, format); outcome = "SAVED " + r.Format + " -> " + r.Path; }
                    catch (InvalidOperationException ex) { outcome = "REFUSED " + ex.Message; }
                    catch (Exception ex) { outcome = "ERROR " + ex.GetType().Name + ": " + ex.Message; }
                    log.Add($"{door} | {format} | {key} | {outcome}");
                    var after = Snap(ws, personPickedBim);
                    var b = before.Split('\n'); var a = after.Split('\n');
                    if (!b.Where(l => !l.StartsWith("TE ", StringComparison.Ordinal)).SequenceEqual(a.Where(l => !l.StartsWith("TE ", StringComparison.Ordinal)))
                        || b.Where(l => l.StartsWith("TE ", StringComparison.Ordinal)).Except(a).Any())
                    {
                        failures.Add($"{format} @ {key}: another project's files changed ({outcome}); gone: [{string.Join("; ", b.Except(a).Take(3))}] new: [{string.Join("; ", a.Except(b).Where(l => !l.StartsWith("TE ", StringComparison.Ordinal)).Take(3))}]");
                    }
                    if (r != null)
                    {
                        var p = r.Path;
                        var holds = File.Exists(p) ? File.ReadAllText(p).Contains("Brand New")
                            : Directory.Exists(p) && (File.Exists(Path.Combine(p, "database.tmdl")) || File.Exists(Path.Combine(p, "model.tmdl")) || File.Exists(Path.Combine(p, "database.json")));
                        if (!holds) failures.Add($"{format} @ {key}: the result names '{p}', which does not hold the saved model");
                        // A link the person picked is replaced by the file itself (the rename lands on the link), so only a
                        // direct pick must have written the file it names.
                        if (personPickedBim && key != "link bim" && !File.ReadAllText(Path.Combine(ws, "bimdir", "model.bim")).Contains("Brand New"))
                            failures.Add($"{format} @ {key}: the person's dialog pick was not written");
                    }
                    // Studio's file dialog always asks for BIM; a folder format aimed at a picked .bim may refuse.
                    else if (personPickedBim && (format == "BIM" || format == "ModelSchemaOnly"))
                        failures.Add($"{format} @ {key}: the person chose this .bim in the save dialog and the save was refused ({outcome})");
                }
            }
            finally
            {
                var dump = Environment.GetEnvironmentVariable("VPSC2_LOG_DIR");
                if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(Path.Combine(dump, door + ".log"), log.Concat(new[] { "---- failures" }).Concat(failures));
                try { Directory.Delete(root, true); } catch { }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} of {log.Count} cases:\n" + string.Join("\n", failures));
        }

        [Theory]
        [InlineData("BIM", "")]
        [InlineData("folder", "tables/TeKeep")]
        public Task A_first_save_aimed_at_or_inside_a_tabular_editor_folder_model_leaves_it_whole_sample(string format, string inside) => A_first_save_aimed_at_or_inside_a_tabular_editor_folder_model_leaves_it_whole(format, inside);

        /// <summary>A Tabular Editor folder model (database.json at its root, the engine's own "folder" format) is not one of the
        /// shapes the first-save check knows, so a first save aimed at it, or at a new name inside it, lands inside it. Whether
        /// that harms it: its own files keep their bytes, it still opens as itself, and its History keeps every entry.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("TMDL", "")]
        [InlineData("BIM", "")]
        [InlineData("folder", "")]
        [InlineData("TMDL", "fresh")]
        [InlineData("folder", "fresh")]
        [InlineData("BIM", "fresh.bim")]
        [InlineData("TMDL", "tables")]
        [InlineData("folder", "tables")]
        [InlineData("BIM", "tables")]
        [InlineData("TMDL", "tables/TeKeep")]
        [InlineData("folder", "tables/TeKeep")]
        public async Task A_first_save_aimed_at_or_inside_a_tabular_editor_folder_model_leaves_it_whole(string format, string inside)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                var ws = Path.Combine(root, "ws");
                CopyTree(await _tpl.GetAsync(TemplateAsync), ws);
                var te = Path.Combine(ws, "tefolder");
                var hist = Path.Combine(te, ".semanticus", "history.json");
                string Own() => string.Join("\n", Directory.EnumerateFiles(te, "*", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar) && !p.Contains("Brand New") && !p.Contains("fresh"))
                    .OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(te, p) + "=" + File.ReadAllText(p)));
                var before = Own();
                var histBefore = File.Exists(hist) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hist))!["models"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.ToJsonString()) : null;
                using (var f = new DecisionFixture(TestEntitlements.Pro))
                {
                    await f.Engine.CreateModelAsync("Brand New", 1604);
                    await f.Engine.CreateTableAsync("Only", "human");
                    var r = await f.Engine.SaveAsync(inside == "" ? te : Path.Combine(te, inside), format, false, "human");
                    Assert.StartsWith(te, r.Path);
                }
                var now = Own();
                Assert.True(!before.Split('\n').Except(now.Split('\n')).Any(), "a file of the Tabular Editor folder model changed; gone: [" + string.Join(" ;; ", before.Split('\n').Except(now.Split('\n')).Take(4)) + "] new: [" + string.Join(" ;; ", now.Split('\n').Except(before.Split('\n')).Take(4)) + "]");
                if (histBefore != null)
                {
                    var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hist))!["models"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.ToJsonString());
                    foreach (var kv in histBefore) Assert.True(after.TryGetValue(kv.Key, out var v) && v == kv.Value, "the folder model's History lost or changed entries for " + kv.Key);
                }
                using var check = new DecisionFixture(TestEntitlements.Pro);
                var reopened = await check.Engine.OpenAsync(te);
                Assert.Equal(1, reopened.Tables);
                Assert.Equal(new[] { "TeKeep" }, check.Sessions.Current.Model.Tables.Select(t => t.Name).ToArray());
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>An agent connection cannot borrow the dialog flag, and a model that has been saved keeps its Save and
        /// Save as exactly as before the lane (a save as onto an existing .bim still replaces it on every door).</summary>
        [Fact]
        public async Task The_dialog_flag_is_the_persons_only_and_a_saved_models_save_and_save_as_are_unchanged()
        {
            var root = Path.Combine(Path.GetTempPath(), "vp2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                var bim = Path.Combine(root, "theirs.bim");
                File.WriteAllText(bim, "{\"name\":\"Theirs\"}");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                // Redirect, never refuse (Kane, 2 Oct): neither an agent claiming the flag nor a person who did not pick the file replaces it.
                var viaAgent = await new EngineRpcTarget(f.Engine, "agent").save(bim, "BIM", true, true);
                Assert.NotEqual(Path.GetFullPath(bim), viaAgent.Path);
                Assert.Equal("{\"name\":\"Theirs\"}", File.ReadAllText(bim));
                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                var viaHuman = await new EngineRpcTarget(f.Engine, "human").save(bim, "BIM", true, false);
                Assert.NotEqual(Path.GetFullPath(bim), viaHuman.Path);
                Assert.Equal("{\"name\":\"Theirs\"}", File.ReadAllText(bim));
                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");

                var mine = Path.Combine(root, "mine");
                var first = await new EngineRpcTarget(f.Engine, "agent").save(mine, "TMDL");
                Assert.Equal(Path.GetFullPath(mine), first.Path);
                await f.Engine.CreateTableAsync("Second", "human");
                var again = await McpTools.SaveModel(f.Engine);
                Assert.Equal(Path.GetFullPath(mine), again.Path);
                Assert.True(File.Exists(Path.Combine(mine, "tables", "Second.tmdl")));
                // Save as: a copy goes only to a new file (b3e60c8a), so an existing .bim is refused untouched and a new one is written.
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(f.Engine, bim, "bim"));
                Assert.Contains("already exists", refused.Message);
                Assert.Equal("{\"name\":\"Theirs\"}", File.ReadAllText(bim));
                var fresh = Path.Combine(root, "fresh.bim");
                var saveAs = await McpTools.SaveModel(f.Engine, fresh, "bim");
                Assert.Equal(Path.GetFullPath(fresh), saveAs.Path);
                Assert.Contains("Brand New", File.ReadAllText(fresh));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }

    /// <summary>
    /// Verifier, m11/psc-bugs round 3 (c87a4750). Kane, 2 Oct: a first save aimed at or inside another model is REDIRECTED
    /// into a new place beside it with a plain note, never refused. Shapes the earlier matrix lacked: every folder a Tabular
    /// Editor folder's loader reads (tables/, a table's own folder and its columns/ and measures/, relationships/, roles/,
    /// perspectives/, cultures/, expressions/, dataSources/), a .pbip project nested inside a Tabular Editor folder, and a
    /// .SemanticModel nested inside a TMDL root. Each case, on four doors and in four formats: no file any model had changes,
    /// nothing new appears outside the place the result names, every model still opens with its own tables, the place the
    /// result names opens with the new model's table, and a save that did not go where it was aimed says so in its note.
    /// </summary>
    public sealed class VerifyPscBugsRound3Tests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public VerifyPscBugsRound3Tests(DirTemplate tpl) => _tpl = tpl;

        private static readonly string[] Formats = { "TMDL", "BIM", "ModelSchemaOnly", "folder" };

        private static Task<SaveResult> SaveVia(string door, LocalEngine e, string path, string format) => door switch
        {
            "engine" => e.SaveAsync(path, format, false, "human"),
            "mcp-overwrite" => McpTools.SaveModel(e, path, format, true),
            "rpc-agent-dialog" => new EngineRpcTarget(e, "agent").save(path, format, true, true),
            _ => new EngineRpcTarget(e, "human").save(path, format, false, true),
        };

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        private static void Project(string folder, string name, string table)
        {
            var def = Path.Combine(folder, name + ".SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(def, "tables"));
            File.WriteAllText(Path.Combine(folder, name + ".pbip"), "{}");
            File.WriteAllText(Path.Combine(folder, name + ".SemanticModel", "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database " + name + "\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(def, "tables", table + ".tmdl"), "table " + table + "\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
        }

        /// <summary>A workspace with a .pbip project, a flat TMDL root holding a .SemanticModel of its own, a folder with a real
        /// .bim, and a real Tabular Editor split folder carrying every folder its loader reads, with a .pbip project inside it.</summary>
        private static async Task<string> TemplateAsync(string root)
        {
            var ws = Path.Combine(root, "tmpl");
            Directory.CreateDirectory(ws);
            Project(ws, "Existing", "Keep");
            var flat = Path.Combine(ws, "flat");
            Directory.CreateDirectory(Path.Combine(flat, "tables"));
            File.WriteAllText(Path.Combine(flat, "database.tmdl"), "database Flat\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(flat, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(flat, "tables", "FlatKeep.tmdl"), "table FlatKeep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
            var inner = Path.Combine(flat, "Inner.SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(inner, "tables"));
            File.WriteAllText(Path.Combine(flat, "Inner.SemanticModel", "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(inner, "database.tmdl"), "database Inner\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(inner, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(inner, "tables", "InnerKeep.tmdl"), "table InnerKeep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
            using (var f = new DecisionFixture(TestEntitlements.Pro))
            {
                var e = f.Engine;
                await e.CreateModelAsync("BimOne", 1604);
                await e.CreateTableAsync("BimKeep", "human");
                await e.SaveAsync(Path.Combine(ws, "bimdir", "model.bim"), "BIM", false, "human");
                File.WriteAllText(Path.Combine(ws, "bimdir", "readme.txt"), "mine");
                await e.CreateModelAsync("TeOne", 1604, discardUnsaved: true);
                await e.CreateTableAsync("TeKeep", "human");
                await e.CreateColumnAsync("table:TeKeep", "Id", "Int64", "Id", "human");
                await e.CreateMeasureAsync("table:TeKeep", "Rows", "COUNTROWS ( TeKeep )", "human");
                await e.CreateTableAsync("TeOther", "human");
                await e.CreateColumnAsync("table:TeOther", "Id", "Int64", "Id", "human");
                await e.CreateRelationshipAsync("column:TeOther/Id", "column:TeKeep/Id", null, true, "human");
                await e.CreateRoleAsync("Reader", "Read", "human");
                await e.CreatePerspectiveAsync("Sales", "human");
                await e.CreateNamedExpressionAsync("Server", "\"localhost\"", "human");
                await e.CreateDataSourceAsync("Src", "localhost", "db", "human");
                var sess = f.Sessions.Current;
                await sess.RunAsync(() => { sess.Model.AddTranslation("da-DK"); return true; });
                await sess.RunAsync(() => { sess.Save(Path.Combine(ws, "te"), TabularEditor.TOMWrapper.SaveFormat.TabularEditorFolder, TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder, false); return true; });
            }
            var te = Path.Combine(ws, "te");
            foreach (var part in new[] { "tables", "relationships", "roles", "perspectives", "cultures", "expressions", "dataSources", Path.Combine("tables", "TeKeep", "columns"), Path.Combine("tables", "TeKeep", "measures") })
                Assert.True(Directory.Exists(Path.Combine(te, part)), "the template's Tabular Editor folder has no " + part + ": " + string.Join(", ", Directory.EnumerateFileSystemEntries(te, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(te, p))));
            Project(Path.Combine(te, "proj"), "Nested", "NestedKeep");
            return ws;
        }

        private static readonly string[] TargetKeys =
        {
            "te", "te/tables", "te/tables/TeKeep", "te/tables/TeKeep/columns", "te/tables/TeKeep/measures", "te/tables/fresh",
            "te/tables/TeKeep/fresh.bim", "te/relationships", "te/roles", "te/perspectives", "te/cultures", "te/expressions",
            "te/dataSources", "te/roles/fresh", "te/proj", "te/proj/Nested.pbip", "te/proj/Nested.SemanticModel",
            "te/proj/Nested.SemanticModel/definition/tables", "te/proj/fresh", "flat/Inner.SemanticModel",
            "flat/Inner.SemanticModel/definition", "flat/Inner.SemanticModel/definition/tables", "flat/Inner.SemanticModel/fresh",
            "flat/Inner.SemanticModel/definition.pbism", "", "Existing.SemanticModel", "bimdir", "bimdir/model.bim",
            "link:te/tables", "link:flat/Inner.SemanticModel", "link:te/proj/Nested.SemanticModel",
        };

        private static string Aim(string ws, string links, string key)
        {
            if (key.StartsWith("link:", StringComparison.Ordinal))
            {
                var link = Path.Combine(links, "l" + key.Substring(5).Replace('/', '_'));
                if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, Path.Combine(ws, key.Substring(5)));
                return link;
            }
            return key == "" ? ws : Path.Combine(ws, key);
        }

        /// <summary>Each model of the template, how to open it, and the tables it must open with.</summary>
        private static readonly (string Rel, string[] Tables)[] Models =
        {
            ("", new[] { "Keep" }), ("Existing.pbip", new[] { "Keep" }), ("flat", new[] { "FlatKeep" }),
            ("flat/Inner.SemanticModel", new[] { "InnerKeep" }), ("te", new[] { "TeKeep", "TeOther" }),
            ("te/proj", new[] { "NestedKeep" }), ("te/proj/Nested.pbip", new[] { "NestedKeep" }), ("bimdir/model.bim", new[] { "BimKeep" }),
        };

        private static System.Collections.Generic.Dictionary<string, string> Files(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(p => p, p => File.ReadAllText(p), StringComparer.Ordinal);

        private static async Task<string[]> TablesOf(DecisionFixture check, string path)
        {
            await check.Engine.OpenAsync(path, discardUnsaved: true);
            return check.Sessions.Current.Model.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Lean suite: the default tier runs the new shapes once (a Tabular Editor loader folder, a .pbip project inside one, a
        /// .SemanticModel inside a TMDL root) on one door; the full four-door, four-format matrix is the sweep.</summary>
        [Fact]
        public Task A_first_save_at_the_new_shapes_is_redirected_honestly_and_every_model_still_opens_whole_sample() => MatrixAsync("mcp-overwrite",
            (format, key) => (format, key) is ("TMDL", "te/tables") or ("BIM", "te/roles") or ("TMDL", "te/proj/Nested.SemanticModel") or ("BIM", "flat/Inner.SemanticModel/definition"));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("engine")]
        [InlineData("mcp-overwrite")]
        [InlineData("rpc-agent-dialog")]
        [InlineData("rpc-human-dialog")]
        public Task A_first_save_at_any_new_shape_is_redirected_honestly_and_every_model_still_opens_whole(string door) => MatrixAsync(door, (_, _) => true);

        private async Task MatrixAsync(string door, Func<string, string, bool> pick)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            var log = new System.Collections.Generic.List<string>();
            var failures = new System.Collections.Generic.List<string>();
            try
            {
                var tmpl = await _tpl.GetAsync(TemplateAsync);
                using var f = new DecisionFixture(TestEntitlements.Pro);
                using var check = new DecisionFixture(TestEntitlements.Pro);
                // Only a model that opens before any save must open after it. The flat TMDL root does not: the TMDL reader
                // reads every folder under it, the nested .SemanticModel's files too. Its bytes are still checked.
                var opens = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var (rel, tables) in Models)
                {
                    try { if ((await TablesOf(check, rel == "" ? tmpl : Path.Combine(tmpl, rel))).SequenceEqual(tables)) opens.Add(rel); }
                    catch (Exception ex) { log.Add($"template: '{rel}' does not open before any save ({ex.GetType().Name})"); }
                }
                Assert.Contains("te", opens);
                Assert.Contains("flat/Inner.SemanticModel", opens);
                Assert.Contains("te/proj", opens);
                var n = 0;
                foreach (var format in Formats)
                foreach (var key in TargetKeys)
                {
                    if (!pick(format, key)) continue;
                    var caseDir = Path.Combine(root, "c" + (n++));
                    var ws = Path.Combine(caseDir, "ws");
                    var links = Path.Combine(caseDir, "links");
                    CopyTree(tmpl, ws);
                    Directory.CreateDirectory(links);
                    var aim = Aim(ws, links, key);
                    var picked = door == "rpc-human-dialog" && key == "bimdir/model.bim";
                    var before = Files(caseDir);
                    await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                    await f.Engine.CreateTableAsync("Only", "human");
                    SaveResult r = null;
                    string outcome;
                    try { r = await SaveVia(door, f.Engine, aim, format); outcome = "SAVED " + r.Format + " -> " + r.Path + " | " + r.Note; }
                    catch (Exception ex) { outcome = "FAILED " + ex.GetType().Name + ": " + ex.Message; }
                    log.Add($"{door} | {format} | {key} | {outcome}");
                    var c = $"{format} @ '{key}'";
                    if (r == null)
                    {
                        // Studio's dialog always sends BIM; a folder format aimed at a .bim the person picked may fail.
                        if (!(picked && format == "folder")) failures.Add($"{c}: a first save failed instead of being redirected ({outcome})");
                    }
                    var after = Files(caseDir);
                    var bim = Path.Combine(ws, "bimdir", "model.bim");
                    foreach (var kv in before)
                    {
                        if (picked && r != null && kv.Key == bim) continue;
                        if (!after.TryGetValue(kv.Key, out var now)) failures.Add($"{c}: {Path.GetRelativePath(caseDir, kv.Key)} is gone ({outcome})");
                        else if (now != kv.Value) failures.Add($"{c}: {Path.GetRelativePath(caseDir, kv.Key)} changed ({outcome})");
                    }
                    var real = r == null ? null : Path.GetFullPath(r.Path);
                    foreach (var p in after.Keys.Where(p => !before.ContainsKey(p)))
                    {
                        var inside = real != null && (p == real || p.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                     || Path.GetFullPath(p).StartsWith(Path.GetFullPath(r.Path) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                        if (!inside) failures.Add($"{c}: a new file appeared outside the place the result names: {Path.GetRelativePath(caseDir, p)} ({outcome})");
                    }
                    if (Directory.EnumerateFileSystemEntries(caseDir, ".semanticus-save-*", SearchOption.AllDirectories).Any())
                        failures.Add($"{c}: a save's temporary folder was left behind");
                    if (r != null)
                    {
                        var aimed = Path.GetFullPath(aim).TrimEnd(Path.DirectorySeparatorChar);
                        if (r.Note == null && !string.Equals(r.Path.TrimEnd(Path.DirectorySeparatorChar), aimed, StringComparison.Ordinal))
                            failures.Add($"{c}: saved somewhere other than where it was aimed with no note ({outcome})");
                        if (r.Note != null && !r.Note.Contains(r.Path, StringComparison.Ordinal))
                            failures.Add($"{c}: the note does not name the place the model went ({outcome})");
                        if (r.Note != null && r.Note.Contains('—')) failures.Add($"{c}: the note carries an em dash");
                        try
                        {
                            var got = await TablesOf(check, r.Path);
                            if (!got.SequenceEqual(new[] { "Only" })) failures.Add($"{c}: the place the result names opens with [{string.Join(", ", got)}], not the new model ({outcome})");
                        }
                        catch (Exception ex) { failures.Add($"{c}: the place the result names does not open: {ex.Message} ({outcome})"); }
                    }
                    foreach (var (rel, tables) in Models)
                    {
                        if (!opens.Contains(rel) || (picked && r != null && rel == "bimdir/model.bim")) continue;
                        var p = rel == "" ? ws : Path.Combine(ws, rel);
                        try
                        {
                            var got = await TablesOf(check, p);
                            if (!got.SequenceEqual(tables)) failures.Add($"{c}: '{rel}' now opens with [{string.Join(", ", got)}], not [{string.Join(", ", tables)}] ({outcome})");
                        }
                        catch (Exception ex) { failures.Add($"{c}: '{rel}' no longer opens: {ex.Message} ({outcome})"); }
                    }
                }
            }
            finally
            {
                var dump = Environment.GetEnvironmentVariable("VPSC3_LOG_DIR");
                if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(Path.Combine(dump, door + ".log"), log.Concat(new[] { "---- failures" }).Concat(failures));
                try { Directory.Delete(root, true); } catch { }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures over {log.Count} cases:\n" + string.Join("\n", failures.Take(40)));
        }

        [Theory]
        [InlineData("rpc-human-dialog", "BIM")]
        public Task A_redirected_first_save_takes_the_next_free_number_and_says_so_sample(string door, string format) => A_redirected_first_save_takes_the_next_free_number_and_says_so(door, format);

        /// <summary>A taken name takes the next free number on both doors, in both kinds of save, and the note says so.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp-overwrite", "TMDL")]
        [InlineData("mcp-overwrite", "BIM")]
        [InlineData("rpc-human-dialog", "TMDL")]
        [InlineData("rpc-human-dialog", "BIM")]
        public async Task A_redirected_first_save_takes_the_next_free_number_and_says_so(string door, string format)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                Project(root, "Existing", "Keep");
                var ext = format == "BIM" ? ".bim" : "";
                foreach (var taken in new[] { "Brand New", "Brand New 2" })
                {
                    if (ext == "") { Directory.CreateDirectory(Path.Combine(root, taken)); File.WriteAllText(Path.Combine(root, taken, "mine.txt"), taken); }
                    else File.WriteAllText(Path.Combine(root, taken + ext), taken);
                }
                Directory.CreateDirectory(Path.Combine(root, "Brand New 3" + (ext == "" ? ".keep" : "")));  // an empty folder named like a file is not the name
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var r = await SaveVia(door, f.Engine, Path.Combine(root, "Existing.SemanticModel"), format);
                var expected = Path.Combine(root, "Brand New 3" + ext);
                Assert.Equal(expected, r.Path);
                Assert.Contains(expected, r.Note);
                Assert.Contains("'Brand New' was taken", r.Note);
                foreach (var taken in new[] { "Brand New", "Brand New 2" })
                    Assert.Equal(taken, ext == "" ? File.ReadAllText(Path.Combine(root, taken, "mine.txt")) : File.ReadAllText(Path.Combine(root, taken + ext)));
                using var check = new DecisionFixture(TestEntitlements.Pro);
                Assert.Equal(new[] { "Only" }, await TablesOf(check, r.Path));
                Assert.Equal(new[] { "Keep" }, await TablesOf(check, root));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Theory]
        [InlineData("a new path named definition in a Tabular Editor folder", "te-split", "Brand New", "definition", "TMDL")]
        [InlineData("engine folder format, a model named tables", "te-flat", "tables", "", "TMDL")]
        public Task A_first_save_never_takes_a_name_that_makes_the_folder_around_it_open_as_another_model_sample(string label, string shape, string modelName, string aim, string format) => A_first_save_never_takes_a_name_that_makes_the_folder_around_it_open_as_another_model(label, shape, modelName, aim, format);

        /// <summary>D4 (round 3). The place a first save picks is checked against the models around it, but the NAME of the new
        /// folder is not checked against what the folder it sits in reads by name. A project folder (or any folder) opens through
        /// a definition/ TMDL folder inside it, and a Tabular Editor folder reads tables/, roles/ and its other named folders, so a
        /// new folder with one of those names makes that model open as the new one, or not open at all. Its own files keep their
        /// bytes; what the person gets when they open it does not.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("project folder, a model named definition", "pbip", "definition", "", "TMDL")]
        [InlineData("redirected out of a .SemanticModel, a model named definition", "pbip", "definition", "Existing.SemanticModel", "TMDL")]
        [InlineData("a new path named definition in a project folder", "pbip", "Brand New", "definition", "TMDL")]
        [InlineData("a new path named definition in a Tabular Editor folder", "te-split", "Brand New", "definition", "TMDL")]
        [InlineData("Tabular Editor folder, a model named definition", "te-split", "definition", "", "TMDL")]
        [InlineData("engine folder format, a model named tables", "te-flat", "tables", "", "TMDL")]
        [InlineData("engine folder format, a model named tables (folder)", "te-flat", "tables", "", "folder")]
        [InlineData("engine folder format, a model named roles (folder)", "te-flat", "roles", "", "folder")]
        public async Task A_first_save_never_takes_a_name_that_makes_the_folder_around_it_open_as_another_model(string label, string shape, string modelName, string aim, string format)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                string other; string[] tables;
                if (shape == "pbip") { Project(root, "Existing", "Keep"); other = root; tables = new[] { "Keep" }; }
                else
                {
                    using var t = new DecisionFixture(TestEntitlements.Pro);
                    await t.Engine.CreateModelAsync("TeOne", 1604);
                    await t.Engine.CreateTableAsync("TeKeep", "human");
                    await t.Engine.CreateColumnAsync("table:TeKeep", "Id", "Int64", "Id", "human");
                    await t.Engine.CreateRoleAsync("Reader", "Read", "human");
                    other = Path.Combine(root, "te");
                    var sess = t.Sessions.Current;
                    var opts = shape == "te-split" ? TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder : TabularEditor.TOMWrapper.Serialization.SerializeOptions.Default;
                    await sess.RunAsync(() => { sess.Save(other, TabularEditor.TOMWrapper.SaveFormat.TabularEditorFolder, opts, false); return true; });
                    tables = new[] { "TeKeep" };
                }
                var before = Files(root);
                using var check = new DecisionFixture(TestEntitlements.Pro);
                Assert.Equal(tables, await TablesOf(check, other));
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync(modelName, 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var target = aim == "" ? other : Path.Combine(other, aim);
                var r = await McpTools.SaveModel(f.Engine, target, format, true);
                foreach (var kv in before) Assert.True(File.Exists(kv.Key) && File.ReadAllText(kv.Key) == kv.Value, $"{label}: {Path.GetRelativePath(root, kv.Key)} changed");
                Assert.Equal(new[] { "Only" }, await TablesOf(check, r.Path));
                string[] now;
                try { now = await TablesOf(check, other); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"{label}: the other model no longer opens after the save went to {r.Path}: {ex.Message}"); }
                Assert.True(now.SequenceEqual(tables), $"{label}: the other model now opens as [{string.Join(", ", now)}], not [{string.Join(", ", tables)}], after the save went to {r.Path} ({r.Note})");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }

    /// <summary>
    /// Verifier, m11/psc-bugs round 4 (11ba481c). The name rule, attacked with every leaf name the opener code reads inside a
    /// folder, taken from the code (Resolve, TE2's handler and its .pbip reader, the split-folder loader, PbipInfo), not from the
    /// guard's lists: as the place a first save is aimed at and as the model's own name, in six holder shapes (a TMDL .pbip
    /// project, a project folder with no .pbip, a Tabular Editor split folder, the engine's flat folder format, a legacy .bim
    /// .pbip project, a plain folder holding a Tabular Editor folder), in TMDL, BIM and folder format, through the agent's door
    /// and the person's. No model around the new one may change a byte, stop opening, or open as anything else.
    /// </summary>
    public sealed class VerifyPscBugsRound4Tests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public VerifyPscBugsRound4Tests(DirTemplate tpl) => _tpl = tpl;

        private static readonly string[] Formats = { "TMDL", "BIM", "folder" };
        private static readonly string[] Holders = { "pbip", "holder", "tes", "tef", "legacy", "plain" };

        /// <summary>Every name an opener reads inside a folder: Resolve (definition, *.SemanticModel, the TMDL root files), TE2's
        /// handler (*.pbip, the TMDL root files, database.json, .pbit), its .pbip reader (definition.pbir, model.bim, any *.bim),
        /// the split-folder loader (its seven folders, *.json), PbipInfo (.platform), GovernanceMode (.Dataset), plus case variants.</summary>
        private static readonly string[] Leaves =
        {
            "definition", "Definition", "database.tmdl", "database.tmd", "model.tmdl", "model.tmd", "database.json", "definition.pbism",
            "x.pbip", "x.SemanticModel", "model.bim", "x.bim", "definition.pbir", "x.pbit", "x.tmdl", "x.json", ".platform", "x.Dataset",
            "tables", "Tables", "dataSources", "expressions", "relationships", "cultures", "perspectives", "roles",
        };

        private static readonly (string Rel, string[] Tables)[] Models =
        {
            ("pbip", new[] { "Keep" }), ("pbip/Existing.pbip", new[] { "Keep" }), ("holder", new[] { "HeldKeep" }),
            ("tes", new[] { "TesKeep" }), ("tes/database.json", new[] { "TesKeep" }), ("tef", new[] { "TefKeep" }),
            ("tef/database.json", new[] { "TefKeep" }), ("legacy", new[] { "LegKeep" }), ("legacy/Existing.pbip", new[] { "LegKeep" }),
            ("plain/te", new[] { "PlainKeep" }),
        };

        private static Task<SaveResult> SaveVia(string door, LocalEngine e, string path, string format) => door == "mcp-overwrite"
            ? McpTools.SaveModel(e, path, format, true)
            : new EngineRpcTarget(e, "human").save(path, format, false, true);

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        private static void Project(string folder, string name, string table, bool pbip)
        {
            var def = Path.Combine(folder, name + ".SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(def, "tables"));
            if (pbip) File.WriteAllText(Path.Combine(folder, name + ".pbip"), "{}");
            File.WriteAllText(Path.Combine(folder, name + ".SemanticModel", "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database " + name + "\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(def, "tables", table + ".tmdl"), "table " + table + "\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
        }

        private static async Task<string> TemplateAsync(string root)
        {
            var ws = Path.Combine(root, "tmpl");
            Directory.CreateDirectory(ws);
            Project(Path.Combine(ws, "pbip"), "Existing", "Keep", pbip: true);
            Project(Path.Combine(ws, "holder"), "Held", "HeldKeep", pbip: false);
            using (var f = new DecisionFixture(TestEntitlements.Pro))
            {
                var e = f.Engine;
                async Task Te(string name, string table, string dir, bool split)
                {
                    await e.CreateModelAsync(name, 1604, discardUnsaved: true);
                    await e.CreateTableAsync(table, "human");
                    await e.CreateColumnAsync("table:" + table, "Id", "Int64", "Id", "human");
                    await e.CreateRoleAsync("Reader", "Read", "human");
                    var s = f.Sessions.Current;
                    var opts = split ? TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder : TabularEditor.TOMWrapper.Serialization.SerializeOptions.Default;
                    await s.RunAsync(() => { s.Save(dir, TabularEditor.TOMWrapper.SaveFormat.TabularEditorFolder, opts, false); return true; });
                }
                await Te("TeSplit", "TesKeep", Path.Combine(ws, "tes"), true);
                await Te("TeFlat", "TefKeep", Path.Combine(ws, "tef"), false);
                await Te("Inner", "PlainKeep", Path.Combine(ws, "plain", "te"), true);
                await e.CreateModelAsync("Legacy", 1604, discardUnsaved: true);
                await e.CreateTableAsync("LegKeep", "human");
                await e.SaveAsync(Path.Combine(ws, "legacy", "Existing.SemanticModel", "model.bim"), "BIM", false, "human");
            }
            // A legacy .bim project: the report points at its model by path, the way TE2's .pbip reader follows it.
            File.WriteAllText(Path.Combine(ws, "legacy", "Existing.pbip"), "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"Existing.Report\"}}]}");
            Directory.CreateDirectory(Path.Combine(ws, "legacy", "Existing.Report"));
            File.WriteAllText(Path.Combine(ws, "legacy", "Existing.Report", "definition.pbir"), "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Existing.SemanticModel\"}}}");
            return ws;
        }

        private static System.Collections.Generic.Dictionary<string, string> Files(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(p => p, p => File.ReadAllText(p), StringComparer.Ordinal);

        private static async Task<string[]> TablesOf(DecisionFixture check, string path)
        {
            await check.Engine.OpenAsync(path, discardUnsaved: true);
            return check.Sessions.Current.Model.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Lean suite: every leaf name an opener reads, once each (the holder, the mode and the format rotate through the
        /// leaves), on the agent's overwrite door; the person's dialog door gets three rows. The full cross product of holder, leaf, mode and
        /// format (936 cases a door) is the sweep. This is the first-save redirect's mutation witness.</summary>
        private static readonly (string Holder, string Leaf, string Mode, string Format)[] Sample = Leaves
            .Select((leaf, i) => (Holders[i % Holders.Length], leaf, i % 2 == 0 ? "aimed at" : "model named", Formats[i % Formats.Length])).ToArray();

        private static readonly (string Holder, string Leaf, string Mode, string Format)[] PersonSample =
        {
            ("pbip", "definition", "aimed at", "BIM"), ("tes", "database.tmdl", "model named", "TMDL"), ("plain", "tables", "aimed at", "folder"),
        };

        [Fact]
        public Task No_name_an_opener_reads_inside_a_folder_lets_a_first_save_change_how_the_models_around_it_open_sample() =>
            RunNamesAsync("mcp-overwrite", (h, l, m, f) => Sample.Contains((h, l, m, f)));

        [Fact]
        public Task No_name_an_opener_reads_inside_a_folder_lets_a_first_save_change_how_the_models_around_it_open_person_sample() =>
            RunNamesAsync("rpc-human-dialog", (h, l, m, f) => PersonSample.Contains((h, l, m, f)));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp-overwrite")]
        [InlineData("rpc-human-dialog")]
        public Task No_name_an_opener_reads_inside_a_folder_lets_a_first_save_change_how_the_models_around_it_open(string door) =>
            RunNamesAsync(door, (_, _, _, _) => true);

        private async Task RunNamesAsync(string door, Func<string, string, string, string, bool> pick)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp4-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            var log = new System.Collections.Generic.List<string>();
            var failures = new System.Collections.Generic.List<string>();
            var notes = new System.Collections.Generic.List<string>();
            try
            {
                var tmpl = await _tpl.GetAsync(TemplateAsync);
                using var f = new DecisionFixture(TestEntitlements.Pro);
                using var check = new DecisionFixture(TestEntitlements.Pro);
                foreach (var (rel, tables) in Models)
                    Assert.True((await TablesOf(check, Path.Combine(tmpl, rel))).SequenceEqual(tables), $"template: '{rel}' does not open as [{string.Join(", ", tables)}]");
                var n = 0;
                foreach (var holder in Holders)
                foreach (var leaf in Leaves)
                foreach (var mode in new[] { "aimed at", "model named" })
                foreach (var format in Formats)
                {
                    if (!pick(holder, leaf, mode, format)) continue;
                    var caseDir = Path.Combine(root, "c" + (n++));
                    var ws = Path.Combine(caseDir, "ws");
                    CopyTree(tmpl, ws);
                    var aim = mode == "aimed at" ? Path.Combine(ws, holder, leaf) : Path.Combine(ws, holder);
                    var before = Files(caseDir);
                    await f.Engine.CreateModelAsync(mode == "aimed at" ? "Brand New" : leaf, 1604, discardUnsaved: true);
                    await f.Engine.CreateTableAsync("Only", "human");
                    SaveResult r = null;
                    string outcome;
                    try { r = await SaveVia(door, f.Engine, aim, format); outcome = "SAVED " + r.Format + " -> " + Path.GetRelativePath(caseDir, r.Path) + " | " + r.Note; }
                    catch (Exception ex) { outcome = "FAILED " + ex.GetType().Name + ": " + ex.Message; }
                    log.Add($"{door} | {format} | {holder} | {mode} '{leaf}' | {outcome}");
                    var c = $"{format}, {mode} '{leaf}' in '{holder}'";
                    if (r == null) failures.Add($"{c}: a first save failed instead of being redirected ({outcome})");
                    var after = Files(caseDir);
                    foreach (var kv in before)
                    {
                        if (!after.TryGetValue(kv.Key, out var now)) failures.Add($"{c}: {Path.GetRelativePath(caseDir, kv.Key)} is gone ({outcome})");
                        else if (now != kv.Value) failures.Add($"{c}: {Path.GetRelativePath(caseDir, kv.Key)} changed ({outcome})");
                    }
                    if (r != null)
                    {
                        var real = Path.GetFullPath(r.Path).TrimEnd(Path.DirectorySeparatorChar);
                        foreach (var p in after.Keys.Where(p => !before.ContainsKey(p)))
                            if (p != real && !p.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                                failures.Add($"{c}: a new file appeared outside the place the result names: {Path.GetRelativePath(caseDir, p)} ({outcome})");
                        if (r.Note == null && !string.Equals(real, Path.GetFullPath(aim).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                            failures.Add($"{c}: saved somewhere other than where it was aimed with no note ({outcome})");
                        if (r.Note != null && !r.Note.Contains(r.Path, StringComparison.Ordinal)) failures.Add($"{c}: the note does not name the place the model went ({outcome})");
                        if (r.Note != null && r.Note.Contains('—')) failures.Add($"{c}: the note carries an em dash");
                        try
                        {
                            var got = await TablesOf(check, r.Path);
                            if (!got.SequenceEqual(new[] { "Only" })) failures.Add($"{c}: the place the result names opens with [{string.Join(", ", got)}], not the new model ({outcome})");
                        }
                        catch (Exception ex)
                        {
                            // A .bim saved under a .pbit name is the new model's own file and harms no other model: an item for the join.
                            (leaf == "x.pbit" ? notes : failures).Add($"{c}: the place the result names does not open: {ex.Message} ({outcome})");
                        }
                    }
                    if (Directory.EnumerateFileSystemEntries(caseDir, ".semanticus-save-*", SearchOption.AllDirectories).Any())
                        failures.Add($"{c}: a save's temporary folder was left behind");
                    foreach (var (rel, tables) in Models.Where(m => m.Rel == holder || m.Rel.StartsWith(holder + "/", StringComparison.Ordinal)))
                    {
                        try
                        {
                            var got = await TablesOf(check, Path.Combine(ws, rel));
                            if (!got.SequenceEqual(tables)) failures.Add($"{c}: '{rel}' now opens with [{string.Join(", ", got)}], not [{string.Join(", ", tables)}] ({outcome})");
                        }
                        catch (Exception ex) { failures.Add($"{c}: '{rel}' no longer opens: {ex.GetType().Name}: {ex.Message} ({outcome})"); }
                    }
                    try { Directory.Delete(caseDir, true); } catch { }
                }
            }
            finally
            {
                var dump = Environment.GetEnvironmentVariable("VPSC4_LOG_DIR");
                if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(Path.Combine(dump, "round4-" + door + ".log"), log.Concat(new[] { "---- failures" }).Concat(failures).Concat(new[] { "---- notes" }).Concat(notes));
                try { Directory.Delete(root, true); } catch { }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures over {log.Count} cases:\n" + string.Join("\n", failures.Take(40)));
        }

        /// <summary>D5 (round 4). A model FILE is checked by its extension only: a .bim save aimed at a new file with any other
        /// extension is written as aimed. When that name is one the opener reads inside a folder (a TMDL root's database.tmd[l] or
        /// model.tmd[l], or a .pbip), the folder around it stops opening: a project folder, a project folder with no .pbip and a
        /// Tabular Editor folder all fail with a TMDL format error or a null project, and TE2 walks up to seven folders from any
        /// folder it opens to find a TMDL root, so a Tabular Editor folder BELOW a plain folder stops opening too. Every file keeps
        /// its bytes; the person's own way in (the folder) is broken, and the new model's own file does not open either.</summary>
        [Theory]
        [InlineData("mcp-overwrite", "tes", "model.tmdl", "tes")]
        [InlineData("mcp-overwrite", "tef", "x.pbip", "tef")]
        [InlineData("mcp-overwrite", "pbip", "database.tmdl", "pbip")]
        [InlineData("mcp-overwrite", "holder", "database.tmd", "holder")]
        [InlineData("mcp-overwrite", "plain", "model.tmd", "plain/te")]
        [InlineData("mcp-overwrite", "legacy", "x.pbip", "legacy")]
        [InlineData("rpc-human-dialog", "pbip", "model.tmdl", "pbip")]
        [InlineData("rpc-human-dialog", "tes", "x.pbip", "tes")]
        public async Task A_bim_first_save_never_takes_a_file_name_that_makes_the_folder_around_it_stop_opening(string door, string holder, string leaf, string other)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp4-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                var ws = Path.Combine(root, "ws");
                CopyTree(await _tpl.GetAsync(TemplateAsync), ws);
                var tables = Models.Single(m => m.Rel == other).Tables;
                using var check = new DecisionFixture(TestEntitlements.Pro);
                Assert.Equal(tables, await TablesOf(check, Path.Combine(ws, other)));
                var before = Files(root);
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var r = await SaveVia(door, f.Engine, Path.Combine(ws, holder, leaf), "BIM");
                foreach (var kv in before) Assert.True(File.Exists(kv.Key) && File.ReadAllText(kv.Key) == kv.Value, $"{Path.GetRelativePath(root, kv.Key)} changed");
                string[] now;
                try { now = await TablesOf(check, Path.Combine(ws, other)); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"'{other}' no longer opens after a .bim first save went to {r.Path} ({r.Note ?? "no note"}): {ex.Message}"); }
                Assert.True(now.SequenceEqual(tables), $"'{other}' now opens as [{string.Join(", ", now)}] after the save went to {r.Path}");
                Assert.Equal(new[] { "Only" }, await TablesOf(check, r.Path));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
    /// <summary>
    /// Verifier, m11/psc-bugs round 5 (736da5a7). The rule: a BIM first save always writes a file named *.bim, whatever leaf it
    /// was aimed at (another extension, none, a dotted name, case variants, trailing dots or spaces), so no folder around it
    /// changes how it opens. And a note says "reserved" only for a name nothing was on, "taken" for one something was on.
    /// </summary>
    public sealed class VerifyPscBugsRound5Tests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public VerifyPscBugsRound5Tests(DirTemplate tpl) => _tpl = tpl;

        private static readonly string[] Formats = { "TMDL", "BIM", "folder" };
        private static readonly string[] Holders = { "pbip", "holder", "tes", "tef", "legacy", "plain" };

        /// <summary>Every name an opener reads inside a folder: Resolve (definition, *.SemanticModel, the TMDL root files), TE2's
        /// handler (*.pbip, the TMDL root files, database.json, .pbit), its .pbip reader (definition.pbir, model.bim, any *.bim),
        /// the split-folder loader (its seven folders, *.json), PbipInfo (.platform), GovernanceMode (.Dataset), plus case variants.</summary>
        private static readonly string[] Leaves =
        {
            "definition", "Definition", "database.tmdl", "database.tmd", "model.tmdl", "model.tmd", "database.json", "definition.pbism",
            "x.pbip", "x.SemanticModel", "model.bim", "x.bim", "definition.pbir", "x.pbit", "x.tmdl", "x.json", ".platform", "x.Dataset",
            "tables", "Tables", "dataSources", "expressions", "relationships", "cultures", "perspectives", "roles",
        };

        private static readonly (string Rel, string[] Tables)[] Models =
        {
            ("pbip", new[] { "Keep" }), ("pbip/Existing.pbip", new[] { "Keep" }), ("holder", new[] { "HeldKeep" }),
            ("tes", new[] { "TesKeep" }), ("tes/database.json", new[] { "TesKeep" }), ("tef", new[] { "TefKeep" }),
            ("tef/database.json", new[] { "TefKeep" }), ("legacy", new[] { "LegKeep" }), ("legacy/Existing.pbip", new[] { "LegKeep" }),
            ("plain/te", new[] { "PlainKeep" }),
        };

        private static Task<SaveResult> SaveVia(string door, LocalEngine e, string path, string format) => door == "mcp-overwrite"
            ? McpTools.SaveModel(e, path, format, true)
            : new EngineRpcTarget(e, "human").save(path, format, false, true);

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        private static void Project(string folder, string name, string table, bool pbip)
        {
            var def = Path.Combine(folder, name + ".SemanticModel", "definition");
            Directory.CreateDirectory(Path.Combine(def, "tables"));
            if (pbip) File.WriteAllText(Path.Combine(folder, name + ".pbip"), "{}");
            File.WriteAllText(Path.Combine(folder, name + ".SemanticModel", "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database " + name + "\n\tcompatibilityLevel: 1604\n");
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
            File.WriteAllText(Path.Combine(def, "tables", table + ".tmdl"), "table " + table + "\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
        }

        private static async Task<string> TemplateAsync(string root)
        {
            var ws = Path.Combine(root, "tmpl");
            Directory.CreateDirectory(ws);
            Project(Path.Combine(ws, "pbip"), "Existing", "Keep", pbip: true);
            Project(Path.Combine(ws, "holder"), "Held", "HeldKeep", pbip: false);
            using (var f = new DecisionFixture(TestEntitlements.Pro))
            {
                var e = f.Engine;
                async Task Te(string name, string table, string dir, bool split)
                {
                    await e.CreateModelAsync(name, 1604, discardUnsaved: true);
                    await e.CreateTableAsync(table, "human");
                    await e.CreateColumnAsync("table:" + table, "Id", "Int64", "Id", "human");
                    await e.CreateRoleAsync("Reader", "Read", "human");
                    var s = f.Sessions.Current;
                    var opts = split ? TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder : TabularEditor.TOMWrapper.Serialization.SerializeOptions.Default;
                    await s.RunAsync(() => { s.Save(dir, TabularEditor.TOMWrapper.SaveFormat.TabularEditorFolder, opts, false); return true; });
                }
                await Te("TeSplit", "TesKeep", Path.Combine(ws, "tes"), true);
                await Te("TeFlat", "TefKeep", Path.Combine(ws, "tef"), false);
                await Te("Inner", "PlainKeep", Path.Combine(ws, "plain", "te"), true);
                await e.CreateModelAsync("Legacy", 1604, discardUnsaved: true);
                await e.CreateTableAsync("LegKeep", "human");
                await e.SaveAsync(Path.Combine(ws, "legacy", "Existing.SemanticModel", "model.bim"), "BIM", false, "human");
            }
            // A legacy .bim project: the report points at its model by path, the way TE2's .pbip reader follows it.
            File.WriteAllText(Path.Combine(ws, "legacy", "Existing.pbip"), "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"Existing.Report\"}}]}");
            Directory.CreateDirectory(Path.Combine(ws, "legacy", "Existing.Report"));
            File.WriteAllText(Path.Combine(ws, "legacy", "Existing.Report", "definition.pbir"), "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Existing.SemanticModel\"}}}");
            return ws;
        }

        private static System.Collections.Generic.Dictionary<string, string> Files(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(p => p, p => File.ReadAllText(p), StringComparer.Ordinal);

        private static async Task<string[]> TablesOf(DecisionFixture check, string path)
        {
            await check.Engine.OpenAsync(path, discardUnsaved: true);
            return check.Sessions.Current.Model.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        private static readonly string[] OddLeaves =
        {
            "v1.2", "a.b.c", "noext", "x.pbit", "foo.", "foo ", "foo. .", "foo.bim.", "foo.bim ", ".bim", "X.BIM", "x.Bim",
            "database.tmdl", "Database.TMDL", "model.tmd", "MODEL.TMD", "x.pbip", "x.PBIP", "x.tmdl", "x.json", "database.json",
            "definition.pbism", "definition.pbir", ".platform", "x.SemanticModel", "x.Dataset", "Definition", "tables",
        };

        /// <summary>Lean suite: six new leaves (no extension, a dotted name, a case variant, a .pbip, a reserved folder name, a loader
        /// folder name), one in each of six holders, on the agent's overwrite door. All 28 leaves in all 7 holders on both doors is the sweep.</summary>
        [Fact]
        public Task A_bim_first_save_aimed_at_a_sample_of_new_leaves_writes_a_bim_and_every_model_around_it_still_opens_sample() => RunLeavesAsync("mcp-overwrite",
            (holder, leaf) => (holder, leaf) is ("pbip", "noext") or ("holder", "foo. .") or ("tes", "X.BIM") or ("tef", "x.pbip") or ("legacy", "Definition") or ("plain", "tables"));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp-overwrite")]
        [InlineData("rpc-human-dialog")]
        public Task A_bim_first_save_aimed_at_any_new_leaf_writes_a_bim_and_every_model_around_it_still_opens(string door) => RunLeavesAsync(door, (_, _) => true);

        private async Task RunLeavesAsync(string door, Func<string, string, bool> pick)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp5-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            var log = new System.Collections.Generic.List<string>();
            var failures = new System.Collections.Generic.List<string>();
            try
            {
                var tmpl = await _tpl.GetAsync(TemplateAsync);
                using var f = new DecisionFixture(TestEntitlements.Pro);
                using var check = new DecisionFixture(TestEntitlements.Pro);
                var n = 0;
                foreach (var holder in Holders.Concat(new[] { "" }))
                foreach (var leaf in OddLeaves)
                {
                    if (!pick(holder, leaf)) continue;
                    var caseDir = Path.Combine(root, "c" + (n++));
                    var ws = Path.Combine(caseDir, "ws");
                    CopyTree(tmpl, ws);
                    var aim = Path.Combine(ws, holder, leaf);
                    var before = Files(caseDir);
                    await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                    await f.Engine.CreateTableAsync("Only", "human");
                    SaveResult r = null;
                    string outcome;
                    try { r = await SaveVia(door, f.Engine, aim, "BIM"); outcome = "SAVED " + r.Format + " -> " + Path.GetRelativePath(caseDir, r.Path) + " | " + r.Note; }
                    catch (Exception ex) { outcome = "FAILED " + ex.GetType().Name + ": " + ex.Message; }
                    log.Add($"{door} | '{holder}' | '{leaf}' | {outcome}");
                    var c = $"BIM aimed at '{leaf}' in '{holder}'";
                    if (r == null) { failures.Add($"{c}: a first save failed instead of being redirected ({outcome})"); continue; }
                    var after = Files(caseDir);
                    foreach (var kv in before)
                        if (!after.TryGetValue(kv.Key, out var now) || now != kv.Value) failures.Add($"{c}: {Path.GetRelativePath(caseDir, kv.Key)} changed or is gone ({outcome})");
                    var real = Path.GetFullPath(r.Path);
                    if (!Path.GetFileName(real).EndsWith(".bim", StringComparison.OrdinalIgnoreCase)) failures.Add($"{c}: the model file is not named *.bim ({outcome})");
                    if (!File.Exists(real)) failures.Add($"{c}: the result names no file ({outcome})");
                    foreach (var p in after.Keys.Where(p => !before.ContainsKey(p)))
                        if (p != real) failures.Add($"{c}: a new file appeared other than the model file: {Path.GetRelativePath(caseDir, p)} ({outcome})");
                    if (r.Note == null && real != Path.GetFullPath(aim)) failures.Add($"{c}: saved somewhere other than where it was aimed with no note ({outcome})");
                    if (r.Note != null && !r.Note.Contains(r.Path, StringComparison.Ordinal)) failures.Add($"{c}: the note does not name the place the model went ({outcome})");
                    if (r.Note != null && (r.Note.Contains('—') || r.Note.Contains("reserved"))) failures.Add($"{c}: the note carries an em dash or says reserved for a file ({outcome})");
                    try
                    {
                        var got = await TablesOf(check, r.Path);
                        if (!got.SequenceEqual(new[] { "Only" })) failures.Add($"{c}: the model file opens with [{string.Join(", ", got)}] ({outcome})");
                    }
                    catch (Exception ex) { failures.Add($"{c}: the model file does not open: {ex.Message} ({outcome})"); }
                    foreach (var (rel, tables) in Models)
                    {
                        try
                        {
                            var got = await TablesOf(check, Path.Combine(ws, rel));
                            if (!got.SequenceEqual(tables)) failures.Add($"{c}: '{rel}' now opens with [{string.Join(", ", got)}] ({outcome})");
                        }
                        catch (Exception ex) { failures.Add($"{c}: '{rel}' no longer opens: {ex.GetType().Name}: {ex.Message} ({outcome})"); }
                    }
                    try { Directory.Delete(caseDir, true); } catch { }
                }
            }
            finally
            {
                var dump = Environment.GetEnvironmentVariable("VPSC4_LOG_DIR");
                if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(Path.Combine(dump, "round5-" + door + ".log"), log.Concat(new[] { "---- failures" }).Concat(failures));
                try { Directory.Delete(root, true); } catch { }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures over {log.Count} cases:\n" + string.Join("\n", failures.Take(40)));
        }

        /// <summary>"Reserved" is said only when nothing was on the name; a name something is on is "taken", in every format.</summary>
        [Theory]
        [InlineData("definition", "TMDL", "nothing", "reserved")]
        [InlineData("definition", "folder", "nothing", "reserved")]
        [InlineData("definition", "TMDL", "folder", "taken")]
        [InlineData("definition", "TMDL", "file", "taken")]
        [InlineData("Brand New", "TMDL", "folder", "taken")]
        [InlineData("Brand New", "folder", "folder", "taken")]
        [InlineData("Brand New", "BIM", "bim", "taken")]
        [InlineData("definition", "BIM", "nothing", "none")]
        public async Task A_note_says_reserved_only_when_nothing_was_on_the_name(string model, string format, string there, string word)
        {
            var root = Path.Combine(Path.GetTempPath(), "vp5-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "notes.txt"), "mine");
                if (there == "folder") { Directory.CreateDirectory(Path.Combine(root, model)); File.WriteAllText(Path.Combine(root, model, "a.txt"), "x"); }
                if (there == "file") File.WriteAllText(Path.Combine(root, model), "x");
                if (there == "bim") File.WriteAllText(Path.Combine(root, model + ".bim"), "{}");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync(model, 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var r = await f.Engine.SaveAsync(root, format, false, "human");
                Assert.NotNull(r.Note);
                if (word == "reserved") { Assert.Contains($"'{model}' was reserved", r.Note); Assert.DoesNotContain("taken", r.Note); }
                if (word == "taken") { Assert.Contains($"'{model}' was taken", r.Note); Assert.DoesNotContain("reserved", r.Note); }
                if (word == "none") { Assert.DoesNotContain("reserved", r.Note); Assert.DoesNotContain("taken", r.Note); }
                Assert.Contains(r.Path, r.Note);
                Assert.True(File.Exists(r.Path) || Directory.Exists(r.Path));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
