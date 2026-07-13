using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;

namespace Semanticus.CicdSmoke
{
    /// <summary>
    /// Smoke test for the ALM lane (source control + model compare + deploy), the same path the user's Claude drives
    /// over MCP. Phase 1: local git round-trip (status/diff/commit/log) + the dry-run gate (commit=false mutates
    /// nothing) + save-then-commit. Phase 1B will add ModelCompare. The cloud lanes (Fabric REST/Git/Pipelines) are
    /// covered two ways: a MOCKED-HTTP section (pagination/LRO/error-parse/scrub, always-on) AND a READ-ONLY LIVE
    /// section gated on a configured service principal (FABRIC_CLIENT/SECRET/TENANT) — it runs against the real tenant
    /// in CI when the SP secrets are set, and skips (offline-green) otherwise. No live writes are ever performed.
    /// Exit code 0 = all checks passed.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static async Task<int> Main()
        {
            var sessions = new SessionManager();
            // Smoke harness runs as Pro so it exercises the BULK apply_model_diff / cherry_pick; the Pro GATE itself
            // is covered by Semanticus.Tests/EntitlementGateTests.
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            try
            {
                var bim = FindTestData("AdventureWorks.bim") ?? throw new FileNotFoundException("AdventureWorks.bim not found.");

                // ---- LOCAL GIT round-trip -------------------------------------------------------
                // Needs the `git` CLI. If it's absent, skip gracefully (like the live blocks elsewhere).
                var gitAvailable = true;
                try { await GitCli.RunAsync(Path.GetTempPath(), "--version"); }
                catch (Exception ex) { gitAvailable = false; Console.WriteLine("[i] git not available — skipping the git round-trip: " + ex.Message); }

                if (gitAvailable)
                {
                    var repo = Path.Combine(Path.GetTempPath(), "semanticus_git_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(repo);
                    var modelDir = Path.Combine(repo, "Sales.SemanticModel");
                    try
                    {
                        // A repo with the model saved as TMDL inside it.
                        await GitCli.RunAsync(repo, "init", "-b", "main");
                        await GitCli.RunAsync(repo, "config", "user.email", "smoke@semanticus.local");
                        await GitCli.RunAsync(repo, "config", "user.name", "Semanticus Smoke");
                        await engine.OpenAsync(bim);
                        await engine.SaveAsync(modelDir, "TMDL");   // SourcePath now anchors to modelDir (inside the repo)
                        Console.WriteLine($"[i] saved AdventureWorks as TMDL into a temp repo: {modelDir}");

                        var st0 = await engine.GitStatusAsync();
                        Check("git_status: detects the repo, a branch, and untracked TMDL files",
                            st0.IsRepo && !string.IsNullOrEmpty(st0.Branch) && st0.Files.Length > 0 && st0.Files.Any(f => f.Path.EndsWith(".tmdl")));

                        // DRY RUN must not commit.
                        var preview = await engine.GitCommitAsync("initial", null, false, "agent");
                        Check("git_commit (dry-run): previews the file set and commits nothing",
                            !preview.Committed && preview.Files.Length > 0);
                        Check("git_commit (dry-run): the working tree is still dirty afterwards",
                            (await engine.GitStatusAsync()).Files.Length > 0);

                        // Real commit.
                        var c1 = await engine.GitCommitAsync("initial model", null, true, "agent");
                        Check("git_commit: committed (hash + files)", c1.Committed && !string.IsNullOrEmpty(c1.Hash) && c1.Files.Length > 0);
                        var st1 = await engine.GitStatusAsync();
                        Check("git_commit: the working tree is clean after commit", st1.Files.Length == 0 && !st1.ModelDirty);

                        // Edit the model in-memory -> the session is dirty but disk is unchanged yet.
                        var measure = (await engine.ListMeasuresAsync()).FirstOrDefault();
                        Check("git: the model has a measure to edit", measure != null);
                        if (measure != null)
                        {
                            await engine.SetDescriptionAsync(measure.Ref, "Edited for the git smoke.", "agent");
                            Check("git_status: an in-memory edit flags the model dirty (commit will save it first)",
                                (await engine.GitStatusAsync()).ModelDirty);

                            // Commit saves the dirty model to disk first, then commits the changed TMDL.
                            var c2 = await engine.GitCommitAsync("describe a measure", null, true, "agent");
                            Check("git_commit: a dirty model is saved to disk before commit, then committed",
                                c2.Committed && c2.SavedModelFirst && c2.Files.Any(f => f.EndsWith(".tmdl")));

                            var diff = await engine.GitDiffAsync(null, false);
                            Check("git_diff: clean working tree after the commit (no diff)", diff.Empty);

                            // ModelCompare: an in-memory edit shows as a semantic measure Update vs git HEAD
                            // (exercises the session snapshot + the gitref worktree path).
                            await engine.SetDaxAsync(measure.Ref, "2 + 2 /* model compare smoke */", "agent");
                            var sdiff = await engine.CompareModelsAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "gitref", GitRef = "HEAD" });
                            Console.WriteLine($"[i] session-vs-HEAD diff: {sdiff.Created}c {sdiff.Updated}u {sdiff.Deleted}d");
                            Check("compare_models: session vs git HEAD detects the in-memory measure edit as Update",
                                sdiff.Items.Any(i => i.ObjectType == "Measure" && i.Action == "Update"));
                            // Low-noise: TMDL-vs-TMDL should report ONLY the real change, not format churn.
                            Check("compare_models: the session-vs-HEAD diff is low-noise (just the edited object, no format churn)",
                                sdiff.Created == 0 && sdiff.Deleted == 0 && sdiff.Updated <= 2);
                        }

                        var log = await engine.GitLogAsync(10);
                        Check("git_log: round-trips the commits (>= 2, with subjects)",
                            log.Length >= 2 && log.All(e => !string.IsNullOrEmpty(e.Hash) && !string.IsNullOrEmpty(e.Subject)));

                        // git_push dry-run: no remote configured, but the preview must not throw and must not push.
                        var pushPreview = await engine.GitPushAsync(null, null, false, "agent");
                        Check("git_push (dry-run): previews without pushing", pushPreview.Ok && pushPreview.Output != null);

                        Console.WriteLine($"[i] git round-trip: {log.Length} commits, branch '{st1.Branch}'");
                    }
                    finally
                    {
                        try { Directory.Delete(repo, true); } catch { /* best-effort temp cleanup */ }
                    }
                }

                // ---- MODEL COMPARE (ALM toolkit) — file vs file + selective apply + deploy gate (offline) ----
                {
                    var baseDir = Path.Combine(Path.GetTempPath(), "semanticus_base_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var modDir = Path.Combine(Path.GetTempPath(), "semanticus_mod_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        await engine.SaveAsync(baseDir, "TMDL");                 // the baseline on disk
                        var m = (await engine.ListMeasuresAsync()).First();
                        var tbl = m.Table;
                        await engine.SetDaxAsync(m.Ref, "1 + 1 /* modified */", "agent");
                        await engine.CreateMeasureAsync("table:" + tbl, "Smoke New Measure", "42", "agent");
                        await engine.SaveAsync(modDir, "TMDL");                  // the modified version on disk
                        var updRef = "measure:" + tbl + "/" + m.Name;

                        var diff = await engine.CompareModelsAsync(new ModelRef { Kind = "file", Path = modDir }, new ModelRef { Kind = "file", Path = baseDir });
                        Check("compare_models (file vs file): the edited measure is Update and the added measure is Create",
                            diff.Items.Any(i => i.Ref == updRef && i.Action == "Update")
                            && diff.Items.Any(i => i.ObjectType == "Measure" && i.Name == "Smoke New Measure" && i.Action == "Create"));
                        Console.WriteLine($"[i] model compare: {diff.Created} create · {diff.Updated} update · {diff.Deleted} delete");

                        var prev = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = modDir }, new ModelRef { Kind = "file", Path = baseDir }, new[] { updRef }, false, "agent");
                        Check("apply_diff (dry-run): previews the selected change and writes nothing", !prev.Applied && prev.Count == 1);
                        var diffStill = await engine.CompareModelsAsync(new ModelRef { Kind = "file", Path = modDir }, new ModelRef { Kind = "file", Path = baseDir });
                        Check("apply_diff (dry-run): the target file is unchanged", diffStill.Items.Any(i => i.Ref == updRef && i.Action == "Update"));

                        var ap = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = modDir }, new ModelRef { Kind = "file", Path = baseDir }, new[] { updRef }, true, "agent");
                        Check("apply_diff: merged ONLY the selected measure into the target file, with no failures",
                            ap.Applied && ap.Count == 1 && ap.AppliedRefs.Contains(updRef) && (ap.FailedRefs == null || ap.FailedRefs.Length == 0) && string.IsNullOrEmpty(ap.Error));
                        var after = await engine.CompareModelsAsync(new ModelRef { Kind = "file", Path = modDir }, new ModelRef { Kind = "file", Path = baseDir });
                        Check("apply_diff: the merged measure now matches (gone from the diff); the unselected new measure is still pending",
                            !after.Items.Any(i => i.Ref == updRef) && after.Items.Any(i => i.Name == "Smoke New Measure" && i.Action == "Create"));

                        var gate = await engine.DeployGateAsync(null);
                        Check("deploy_gate: returns a readiness grade, BPA counts, and a pass/block decision",
                            !string.IsNullOrEmpty(gate.Grade) && gate.Note != null);
                        Console.WriteLine($"[i] deploy gate: grade {gate.Grade}, BPA {gate.BpaViolations} ({gate.BpaBlocking} blocking), pass={gate.Pass}");
                    }
                    finally
                    {
                        try { Directory.Delete(baseDir, true); } catch { }
                        try { Directory.Delete(modDir, true); } catch { }
                    }
                }

                // ---- MODEL COMPARE: structural relationship matching (Phase 2 false-diff hardening) ----
                // A relationship is keyed by its ENDPOINTS, not its (auto-assigned) name: a property change reads as
                // ONE structural Update whose Ref is the endpoint signature (NOT "relationship:<name>") — so two
                // independently-named copies of the same relationship can never show as a spurious Create+Delete pair.
                // Also exercises MatchedByName (relationship = false/structural; role = true/name-keyed) + the apply.
                {
                    var relBase = Path.Combine(Path.GetTempPath(), "semanticus_relbase_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var relMod = Path.Combine(Path.GetTempPath(), "semanticus_relmod_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        await engine.SaveAsync(relBase, "TMDL");                         // baseline
                        var rel = (await engine.GetModelGraphAsync()).Relationships.First();
                        var newFrom = rel.FromCardinality == "Many" ? "One" : "Many";
                        // CARDINALITY-only change — guards the RelEqual whitelist (cardinality must read as Update,
                        // not be silently dropped to Equal as it would if RelEqual omitted FromCardinality/ToCardinality).
                        await engine.SetRelationshipCardinalityAsync(rel.Name, newFrom, rel.ToCardinality, "agent");
                        await engine.CreateRoleAsync("Smoke Analyst", "Read", "agent");                     // a name-keyed object
                        await engine.SaveAsync(relMod, "TMDL");

                        var rdiff = await engine.CompareModelsAsync(new ModelRef { Kind = "file", Path = relMod }, new ModelRef { Kind = "file", Path = relBase });
                        var relItems = rdiff.Items.Where(i => i.ObjectType == "Relationship").ToList();
                        Check("compare: a cardinality change is ONE structural Update — NOT silently Equal, NOT a spurious Create+Delete pair",
                            relItems.Count == 1 && relItems[0].Action == "Update"
                            && rdiff.Items.Count(i => i.ObjectType == "Relationship" && (i.Action == "Create" || i.Action == "Delete")) == 0);
                        Check("compare: the relationship is keyed STRUCTURALLY — endpoint ref + readable name + MatchedByName=false (NOT relationship:<name>)",
                            relItems.Count == 1 && !relItems[0].MatchedByName && relItems[0].Name.Contains("[")
                            && relItems[0].Ref.Contains("]->") && relItems[0].Ref != "relationship:" + rel.Name);
                        Check("compare: a role is matched by NAME (MatchedByName flag set)",
                            rdiff.Items.Any(i => i.ObjectType == "Role" && i.Name == "Smoke Analyst" && i.Action == "Create" && i.MatchedByName));

                        var relRef = relItems[0].Ref;
                        var rap = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = relMod }, new ModelRef { Kind = "file", Path = relBase }, new[] { relRef }, true, "agent");
                        Check("apply_diff: the relationship merges into the target by endpoint signature (no failures)",
                            rap.Applied && rap.AppliedRefs.Contains(relRef) && (rap.FailedRefs == null || rap.FailedRefs.Length == 0) && string.IsNullOrEmpty(rap.Error));
                        var rafter = await engine.CompareModelsAsync(new ModelRef { Kind = "file", Path = relMod }, new ModelRef { Kind = "file", Path = relBase });
                        Check("apply_diff: the relationship now matches (gone from the diff)", !rafter.Items.Any(i => i.ObjectType == "Relationship"));
                        Console.WriteLine($"[i] relationship structural match: rel creates={rdiff.Items.Count(i => i.ObjectType == "Relationship" && i.Action == "Create")} deletes={rdiff.Items.Count(i => i.ObjectType == "Relationship" && i.Action == "Delete")} updates={relItems.Count}");
                    }
                    finally
                    {
                        try { Directory.Delete(relBase, true); } catch { }
                        try { Directory.Delete(relMod, true); } catch { }
                    }
                }

                // ---- CHERRY PICK: copy a measure FROM another model INTO the open session, undoably (Phase 3) ----
                {
                    var cpSrc = Path.Combine(Path.GetTempPath(), "semanticus_cpsrc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        var tbl = (await engine.ListMeasuresAsync()).First().Table;
                        await engine.CreateMeasureAsync("table:" + tbl, "Cherry Pick Probe", "COUNTROWS(" + tbl + ")", "agent");
                        await engine.SetMeasureFormatAsync("measure:" + tbl + "/Cherry Pick Probe", "#,0", "agent");
                        await engine.SaveAsync(cpSrc, "TMDL");                  // SOURCE model B (has the probe measure)
                        await engine.OpenAsync(bim);                           // re-open A fresh — no probe measure
                        var cpRef = "measure:" + tbl + "/Cherry Pick Probe";

                        var cprev = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = cpSrc }, new[] { cpRef }, true, false, "agent");
                        Check("cherry_pick (dry-run): previews 1 measure to copy — nothing applied, no conflict/failure",
                            !cprev.Applied && cprev.Count == 1 && (cprev.Conflicts == null || cprev.Conflicts.Length == 0) && (cprev.FailedRefs == null || cprev.FailedRefs.Length == 0));
                        Check("cherry_pick (dry-run): the open model is unchanged", !(await engine.ListMeasuresAsync()).Any(x => x.Name == "Cherry Pick Probe"));

                        var cap = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = cpSrc }, new[] { cpRef }, true, true, "agent");
                        Check("cherry_pick: copies the measure into the open model (no failures)",
                            cap.Applied && cap.Count == 1 && cap.AppliedRefs.Contains(cpRef) && (cap.FailedRefs == null || cap.FailedRefs.Length == 0));
                        var cm = (await engine.ListMeasuresAsync()).FirstOrDefault(x => x.Name == "Cherry Pick Probe");
                        Check("cherry_pick: the copied measure is present with its source format string", cm != null && cm.FormatString == "#,0");

                        await engine.UndoAsync("agent");
                        Check("cherry_pick: the copy is UNDOABLE — one Ctrl+Z removes it", !(await engine.ListMeasuresAsync()).Any(x => x.Name == "Cherry Pick Probe"));

                        var cbad = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = cpSrc }, new[] { "table:DoesNotExist", "measure:" + tbl + "/Nope" }, true, false, "agent");
                        Check("cherry_pick (dry-run): an unsupported kind + a missing measure both surface in FailedRefs (never silently dropped)",
                            cbad.FailedRefs != null && cbad.FailedRefs.Length == 2 && cbad.Count == 0);
                        Console.WriteLine($"[i] cherry_pick: copied 1 measure (undo removed it); {cbad.FailedRefs.Length} bad refs surfaced");
                    }
                    finally { try { Directory.Delete(cpSrc, true); } catch { } }
                }

                // ---- CHERRY PICK a CALCULATED COLUMN (Phase 3c) + refuse a data column (no DAX to copy) ----
                {
                    var ccSrc = Path.Combine(Path.GetTempPath(), "semanticus_ccsrc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        var tbl = (await engine.ListMeasuresAsync()).First().Table;
                        await engine.CreateCalculatedColumnAsync("table:" + tbl, "Cherry Calc Col", "1", "agent");
                        await engine.SaveAsync(ccSrc, "TMDL");                  // SOURCE = bim + a calculated column
                        await engine.OpenAsync(bim);                           // open model = plain bim
                        var ccRef = "column:" + tbl + "/Cherry Calc Col";

                        var ccap = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = ccSrc }, new[] { ccRef }, true, true, "agent");
                        Check("cherry_pick: copies a calculated column into the open model (no failures)",
                            ccap.Applied && ccap.Count == 1 && (ccap.FailedRefs == null || ccap.FailedRefs.Length == 0));
                        Check("cherry_pick: the calculated column is present", (await engine.ListColumnsAsync()).Any(c => c.Name == "Cherry Calc Col"));
                        await engine.UndoAsync("agent");
                        Check("cherry_pick: the calculated-column copy is undoable", !(await engine.ListColumnsAsync()).Any(c => c.Name == "Cherry Calc Col"));

                        var dataCol = (await engine.ListColumnsAsync()).First(c => c.Table == tbl);   // a data column (no DAX)
                        var dbad = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = ccSrc }, new[] { "column:" + tbl + "/" + dataCol.Name }, true, false, "agent");
                        Check("cherry_pick: a DATA column (no expression) is reported in FailedRefs, not copied",
                            dbad.FailedRefs != null && dbad.FailedRefs.Length == 1 && dbad.Count == 0);
                        Console.WriteLine("[i] cherry_pick: copied a calculated column (undo removed it); a data column was refused");
                    }
                    finally { try { Directory.Delete(ccSrc, true); } catch { } }
                }

                // ---- LIST REFERENCE TREE: browse another model's copyable objects (Phase 4b) ----
                {
                    var rtSrc = Path.Combine(Path.GetTempPath(), "semanticus_rtsrc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        var tbl = (await engine.ListMeasuresAsync()).First().Table;
                        await engine.CreateMeasureAsync("table:" + tbl, "Ref Tree Probe", "1", "agent");
                        await engine.SaveAsync(rtSrc, "TMDL");
                        var tree = await engine.ListReferenceTreeAsync(new ModelRef { Kind = "file", Path = rtSrc });
                        Check("list_reference_tree: returns the reference model's tables", tree.Any(n => n.Ref == "table:" + tbl && (n.Kind == "table" || n.Kind == "calcgroup")));
                        Check("list_reference_tree: a table's measures appear as copyable child nodes with cherry_pick-compatible refs",
                            tree.Any(n => n.Ref == "measure:" + tbl + "/Ref Tree Probe" && n.Kind == "measure"));
                        Console.WriteLine($"[i] list_reference_tree: {tree.Count(n => n.Kind == "table" || n.Kind == "calcgroup")} tables, {tree.Length} nodes total");
                    }
                    finally { try { Directory.Delete(rtSrc, true); } catch { } }
                }

                // ---- REVIEW-FIX coverage (Phase 4 adversarial review): the input classes the happy-path smokes miss ----
                {
                    var rfSrc = Path.Combine(Path.GetTempPath(), "semanticus_rfsrc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var rfOut = Path.Combine(Path.GetTempPath(), "semanticus_rfout_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        // SOURCE: a measure with a DYNAMIC format string (dropped by the old 5-prop copy), plus a table
                        // whose NAME contains '/', with a measure under it (the ref-delimiter escaping case).
                        await engine.OpenAsync(bim);
                        await engine.SetCompatibilityLevelAsync(1604, "agent");   // dynamic format strings (FormatStringDefinition) need CL ≥ 1601
                        var tbl = (await engine.ListMeasuresAsync()).First().Table;
                        await engine.CreateMeasureAsync("table:" + tbl, "Fmt Probe", "1", "agent");
                        await engine.SetObjectPropertyAsync("measure:" + tbl + "/Fmt Probe", "FormatStringExpression", "\"FMTPROBE \" & FORMAT(1,\"0\")", "agent");
                        await engine.CreateTableAsync("Rev/Exp", "agent");
                        await engine.CreateMeasureAsync("table:Rev/Exp", "Net", "42", "agent");
                        await engine.SaveAsync(rfSrc, "TMDL");

                        // (L2) the reference tree escapes the '/' in refs but keeps the real display name.
                        var rtree = await engine.ListReferenceTreeAsync(new ModelRef { Kind = "file", Path = rfSrc });
                        Check("review-fix L2: a '/'-named table is escaped in its ref (~1) yet keeps its real display name",
                            rtree.Any(n => n.Ref == "table:Rev~1Exp" && n.Name == "Rev/Exp") && rtree.Any(n => n.Ref == "measure:Rev~1Exp/Net" && n.Name == "Net"));

                        // Fresh open model: has the Rev/Exp table (so the measure can land) + a lowercase clash for L3.
                        await engine.OpenAsync(bim);
                        await engine.SetCompatibilityLevelAsync(1604, "agent");   // the target must also accept the dynamic format string
                        await engine.CreateTableAsync("Rev/Exp", "agent");
                        await engine.CreateMeasureAsync("table:" + tbl, "fmt probe", "999", "agent");   // case-only clash with source "Fmt Probe"

                        // (L3) dry-run: a case-only clash classifies as OVERWRITE, not a clean create (pre-fix: ordinal == missed it).
                        var dry = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = rfSrc }, new[] { "measure:" + tbl + "/Fmt Probe" }, true, false, "agent");
                        Check("review-fix L3: a case-only name clash is previewed as an overwrite (not a silent auto-rename)",
                            dry.Conflicts != null && dry.Conflicts.Contains("measure:" + tbl + "/Fmt Probe") && (dry.FailedRefs == null || dry.FailedRefs.Length == 0));

                        // Commit: copy the dynamic-format measure (over the clash) + the '/'-table measure via its escaped ref.
                        var cp = await engine.CherryPickAsync(new ModelRef { Kind = "file", Path = rfSrc },
                            new[] { "measure:" + tbl + "/Fmt Probe", "measure:Rev~1Exp/Net" }, true, true, "agent");
                        Check("review-fix L2: a measure under a '/'-named table copies via its escaped ref (ParseChildRef round-trips)",
                            cp.Applied && cp.AppliedRefs.Contains("measure:Rev~1Exp/Net") && (cp.FailedRefs == null || cp.FailedRefs.Length == 0));

                        var clash = (await engine.ListMeasuresAsync()).Where(mr => mr.Table == tbl && string.Equals(mr.Name, "fmt probe", StringComparison.OrdinalIgnoreCase)).ToList();
                        Check("review-fix L3: the overwrite reuses the existing measure (no duplicate auto-renamed copy)", clash.Count == 1);
                        Check("review-fix L3: the overwrite applied the source expression", clash.Count == 1 && (clash[0].Expression ?? "").Trim() == "1");

                        // (H2) the dynamic format string survived the copy — save + confirm it serialized into the target TMDL.
                        await engine.SaveAsync(rfOut, "TMDL");
                        var tmdlHasFmt = Directory.EnumerateFiles(rfOut, "*.tmdl", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("FMTPROBE"));
                        Check("review-fix H2: the dynamic format string copied across (not dropped by the old 5-prop copy)", tmdlHasFmt);
                        Console.WriteLine("[i] review-fix coverage: dynamic-format fidelity + '/'-table ref escaping + case-only overwrite all hold");
                    }
                    finally { try { Directory.Delete(rfSrc, true); } catch { } try { Directory.Delete(rfOut, true); } catch { } }
                }

                // ---- APPLY DIFF into the open SESSION (Phase 3b): merge selected changes into the live model, undoably ----
                {
                    var mSrc = Path.Combine(Path.GetTempPath(), "semanticus_mergesrc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        await engine.OpenAsync(bim);
                        var tbl = (await engine.ListMeasuresAsync()).First().Table;
                        await engine.CreateMeasureAsync("table:" + tbl, "Merge Probe", "1", "agent");
                        await engine.SaveAsync(mSrc, "TMDL");                     // SOURCE = bim + Merge Probe
                        await engine.OpenAsync(bim);                             // open model = plain bim …
                        await engine.CreateMeasureAsync("table:" + tbl, "Stale Probe", "2", "agent");   // … plus a measure the source lacks
                        var mergeRef = "measure:" + tbl + "/Merge Probe";
                        var staleRef = "measure:" + tbl + "/Stale Probe";

                        var mprev = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = mSrc }, new ModelRef { Kind = "session" }, new[] { mergeRef, staleRef }, false, "agent");
                        Check("apply_diff (session, dry-run): previews 2 changes to merge into the open model — nothing applied, no failures",
                            !mprev.Applied && mprev.Count == 2 && (mprev.FailedRefs == null || mprev.FailedRefs.Length == 0));
                        var before = (await engine.ListMeasuresAsync()).Select(x => x.Name).ToHashSet();
                        Check("apply_diff (session, dry-run): the open model is unchanged", before.Contains("Stale Probe") && !before.Contains("Merge Probe"));

                        var map = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = mSrc }, new ModelRef { Kind = "session" }, new[] { mergeRef, staleRef }, true, "agent");
                        Check("apply_diff (session): merges the selected changes into the open model (no failures)",
                            map.Applied && map.Count == 2 && (map.FailedRefs == null || map.FailedRefs.Length == 0));
                        var after = (await engine.ListMeasuresAsync()).Select(x => x.Name).ToHashSet();
                        Check("apply_diff (session): the create was copied IN and the delete was removed", after.Contains("Merge Probe") && !after.Contains("Stale Probe"));

                        await engine.UndoAsync("agent");
                        var undone = (await engine.ListMeasuresAsync()).Select(x => x.Name).ToHashSet();
                        Check("apply_diff (session): the whole merge is ONE undo step (create gone, delete restored)",
                            !undone.Contains("Merge Probe") && undone.Contains("Stale Probe"));
                        Console.WriteLine("[i] apply_diff session-target: merged 1 create + 1 delete in one undoable batch");
                    }
                    finally { try { Directory.Delete(mSrc, true); } catch { } }
                }

                // ---- FABRIC REST: pagination + LRO poller + error parse + scrub (mocked HttpMessageHandler, no tenant) ----
                {
                    // 1) Pagination — page 1 carries a continuationToken/Uri, page 2 ends it; all items collected and
                    //    the 2nd request follows the continuation.
                    var pageHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"a\",\"displayName\":\"WS A\"},{\"id\":\"b\",\"displayName\":\"WS B\"}],\"continuationToken\":\"TOK\",\"continuationUri\":\"https://api.fabric.microsoft.com/v1/workspaces?continuationToken=TOK\"}"),
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"c\",\"displayName\":\"WS C\"}]}"),
                    });
                    using (var http = new HttpClient(pageHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var ws = await FabricRest.GetAllPagesAsync<FabricWorkspace>(http, "workspaces", CancellationToken.None);
                        Check("fabric pagination: collects items across continuationToken pages", ws.Count == 3 && ws.Any(w => w.DisplayName == "WS C"));
                        Check("fabric pagination: the 2nd request follows the continuation", pageHandler.Requests.Count == 2 && pageHandler.Requests[1].Contains("continuationToken=TOK"));
                    }

                    // 1b) continuationToken WITHOUT a continuationUri uses the appended-query fallback.
                    var tokenOnly = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"a\"}],\"continuationToken\":\"TOK2\"}"),
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"b\"}]}"),
                    });
                    using (var http = new HttpClient(tokenOnly) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var ws = await FabricRest.GetAllPagesAsync<FabricWorkspace>(http, "workspaces", CancellationToken.None);
                        Check("fabric pagination: a token without a continuationUri uses the appended-query fallback", ws.Count == 2 && tokenOnly.Requests.Count == 2 && tokenOnly.Requests[1].Contains("continuationToken=TOK2"));
                    }

                    // 1c) Defense-in-depth — an OFF-HOST continuationUri is ignored (the Bearer never leaves the Fabric host).
                    var offHost = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"a\"}],\"continuationToken\":\"T3\",\"continuationUri\":\"https://evil.example/steal?continuationToken=T3\"}"),
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"b\"}]}"),
                    });
                    using (var http = new HttpClient(offHost) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        await FabricRest.GetAllPagesAsync<FabricWorkspace>(http, "workspaces", CancellationToken.None);
                        Check("fabric pagination: an off-host continuationUri is ignored (token stays on api.fabric.microsoft.com)",
                            offHost.Requests.Count == 2 && !offHost.Requests[1].Contains("evil.example") && offHost.Requests[1].Contains("api.fabric.microsoft.com"));
                    }

                    // 1d) Empty page — zero items, no crash; and an unknown (open) itemType deserializes as a string.
                    var emptyPage = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.OK, "{\"value\":[]}") });
                    using (var http = new HttpClient(emptyPage) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                        Check("fabric pagination: an empty page returns zero items without error", (await FabricRest.GetAllPagesAsync<DeploymentPipeline>(http, "deploymentPipelines", CancellationToken.None)).Count == 0);
                    var enumHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.OK, "{\"value\":[{\"itemId\":\"i1\",\"itemDisplayName\":\"X\",\"itemType\":\"BrandNewItemKind\"}]}") });
                    using (var http = new HttpClient(enumHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var items = await FabricRest.GetAllPagesAsync<StageItem>(http, "deploymentPipelines/p/stages/s/items", CancellationToken.None);
                        Check("fabric open-enum: an unknown itemType deserializes as a string (no throw)", items.Count == 1 && items[0].ItemType == "BrandNewItemKind");
                    }

                    // 2) LRO — a 202 (x-ms-operation-id + Retry-After) then poll operations/{id}: Running -> Succeeded.
                    var lroHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-123"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\",\"percentComplete\":40}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"percentComplete\":100}"),
                    });
                    using (var http = new HttpClient(lroHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var started = await FabricRest.SendAsync(http, HttpMethod.Post, "deploymentPipelines/x/deploy", CancellationToken.None);
                        Check("fabric LRO: a 202 surfaces the operation id (x-ms-operation-id)", started.Status == 202 && started.OperationId == "op-123");
                        var state = await FabricRest.PollFrom202Async(http, started, CancellationToken.None);
                        Check("fabric LRO: polls operations/{id} to a terminal Succeeded", state.Status == "Succeeded" && state.PercentComplete == 100);
                        Check("fabric LRO: polled the operation twice (Running -> Succeeded)", lroHandler.Requests.Count(u => u.Contains("/operations/op-123")) == 2);
                    }

                    // 3) Error — a 403 with the stable errorCode is parsed + surfaced with an actionable hint (not swallowed).
                    var errHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => Json(HttpStatusCode.Forbidden, "{\"errorCode\":\"InsufficientPrivileges\",\"message\":\"The caller lacks permission.\",\"requestId\":\"req-1\"}"),
                    });
                    using (var http = new HttpClient(errHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        string err = null;
                        try { await FabricRest.GetAllPagesAsync<DeploymentPipeline>(http, "deploymentPipelines", CancellationToken.None); }
                        catch (Exception ex) { err = ex.Message; }
                        Check("fabric error: a 403 surfaces the stable errorCode + a role hint", err != null && err.Contains("403") && err.Contains("InsufficientPrivileges") && err.Contains("role"));
                    }

                    // 4) Scrub — a Bearer token in an error message never crosses the door.
                    var scrubbed = FabricRest.ParseError("{\"message\":\"request failed: Authorization: Bearer eyJ0eXASECRET123.abc\"}", 500);
                    Check("fabric scrub: a Bearer token is redacted from surfaced errors", !scrubbed.Contains("eyJ0eXASECRET123") && scrubbed.Contains("Bearer ***"));
                    Console.WriteLine("[i] fabric REST: pagination + LRO + error-parse + scrub verified against a mocked handler (no live tenant)");
                }

                // ---- DEPLOY (write lane): the gate matrix (pure) + the POST/202/poll mechanics + history (mocked) ----
                {
                    // The SAFETY decision (DeployGuard) — pure, exhaustive, fully offline. This is the gate that makes a
                    // prod promotion impossible for an agent to satisfy alone.
                    const string secret = "smoke-secret";
                    var tok = DeployGuard.MintToken("p", "s0", "s1", new[] { "a", "b" }, secret);
                    Check("deploy guard: MintToken is deterministic for the same intent", tok == DeployGuard.MintToken("p", "s0", "s1", new[] { "b", "a" }, secret) && tok.StartsWith("PROD-"));
                    Check("deploy guard: MintToken changes with the item set (no replay of a smaller preview's token)", tok != DeployGuard.MintToken("p", "s0", "s1", new[] { "a", "b", "c" }, secret));

                    Check("deploy guard: a dry-run is never blocked", DeployGuard.Refusal(true, false, "agent", null, tok, false, false, false) == null);
                    Check("deploy guard: an agent CAN deploy to a non-prod stage (gate passing)", DeployGuard.Refusal(false, true, "agent", null, tok, true, false, false) == null);
                    Check("deploy guard: an agent CANNOT promote to prod", (DeployGuard.Refusal(true, true, "agent", tok, tok, true, false, false) ?? "").Contains("agent cannot promote"));
                    Check("deploy guard: a human prod deploy with the WRONG token is refused", (DeployGuard.Refusal(true, true, "human", "PROD-wrong", tok, true, false, false) ?? "").Contains("confirmToken"));
                    Check("deploy guard: a human prod deploy with the RIGHT token proceeds", DeployGuard.Refusal(true, true, "human", tok, tok, true, false, false) == null);
                    Check("deploy guard: a failing readiness gate blocks a commit", (DeployGuard.Refusal(false, true, "human", null, tok, false, false, false) ?? "").Contains("readiness gate"));
                    Check("deploy guard: a REASONED forceOverride lets a failing-gate (non-prod) deploy through", DeployGuard.Refusal(false, true, "human", null, tok, false, true, true) == null);
                    Check("deploy guard: forceOverride WITHOUT a reason is refused (accountable override)", (DeployGuard.Refusal(false, true, "human", null, tok, false, true, false) ?? "").Contains("overrideReason"));
                    Check("deploy guard: forceOverride CANNOT bypass the agent+prod block", (DeployGuard.Refusal(true, true, "agent", tok, tok, true, true, true) ?? "").Contains("agent cannot promote"));

                    // The token-withholding control (pins the && so a ||-mutation that leaks the prod token to an agent fails here).
                    Check("deploy token: surfaced to a HUMAN prod dry-run", DeployGuard.SurfaceConfirmToken(true, "human"));
                    Check("deploy token: NEVER surfaced to the agent door (prod)", !DeployGuard.SurfaceConfirmToken(true, "agent"));
                    Check("deploy token: never surfaced for a non-prod target", !DeployGuard.SurfaceConfirmToken(false, "human") && !DeployGuard.SurfaceConfirmToken(false, "agent"));

                    // ProdInfo classification — the prod heuristic the gate's agent-block depends on.
                    var pipeStages = new[]
                    {
                        new PipelineStage { Id = "d", Order = 0, DisplayName = "Development", IsPublic = false },
                        new PipelineStage { Id = "t", Order = 1, DisplayName = "Test", IsPublic = false },
                        new PipelineStage { Id = "p", Order = 2, DisplayName = "Production", IsPublic = true },
                    };
                    Check("prod info: the public / highest-order / 'Production' stage is prod", LocalEngine.ProdInfo(pipeStages, "p").isProd);
                    Check("prod info: a mid-pipeline Test stage is NOT prod", !LocalEngine.ProdInfo(pipeStages, "t").isProd);
                    Check("prod info: an unknown stage id is treated as non-prod (the deploy then 404s rather than mis-gating)", !LocalEngine.ProdInfo(pipeStages, "?").isProd);
                    Check("prod info: a stage NAMED prod but not highest-order is still prod (name heuristic)",
                        LocalEngine.ProdInfo(new[] { new PipelineStage { Id = "x", Order = 0, DisplayName = "Prod-hotfix" }, new PipelineStage { Id = "y", Order = 1, DisplayName = "Staging" } }, "x").isProd);

                    // BuildDeployBody — the selective/all + note-cap shape.
                    var selBody = LocalEngine.BuildDeployBody("s0", "s1", new[] { new DeployItemDiff { ItemId = "i1", ItemType = "SemanticModel" } }, "ship it");
                    Check("deploy body: selective items carry sourceItemId + itemType", selBody.Contains("\"sourceItemId\":\"i1\"") && selBody.Contains("\"itemType\":\"SemanticModel\"") && selBody.Contains("\"sourceStageId\":\"s0\"") && selBody.Contains("\"note\":\"ship it\""));
                    var allBody = LocalEngine.BuildDeployBody("s0", "s1", null, null);
                    Check("deploy body: a null item set deploys ALL (no items array, no note)", !allBody.Contains("\"items\"") && !allBody.Contains("\"note\"") && allBody.Contains("\"targetStageId\":\"s1\""));
                    var longNote = LocalEngine.BuildDeployBody("s0", "s1", null, new string('x', 2000));
                    using (var d = System.Text.Json.JsonDocument.Parse(longNote))
                        Check("deploy body: the note is capped at the API's 1024 limit", d.RootElement.GetProperty("note").GetString().Length == 1024);

                    // The deploy POST mechanics: a JSON body POST -> 202 (x-ms-operation-id + deployment-id) -> poll to Succeeded.
                    var deployHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-d1"); r.Headers.Add("deployment-id", "dep-1"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\",\"percentComplete\":50}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"percentComplete\":100}"),
                    });
                    using (var http = new HttpClient(deployHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var started = await FabricRest.SendAsync(http, HttpMethod.Post, "deploymentPipelines/p/deploy", "{\"sourceStageId\":\"s0\",\"targetStageId\":\"s1\"}", CancellationToken.None);
                        Check("fabric deploy: a POST with a JSON body returns 202 + operation id + deployment-id", started.Status == 202 && started.OperationId == "op-d1" && started.DeploymentId == "dep-1" && deployHandler.Requests[0].StartsWith("POST"));
                        var state = await FabricRest.PollFrom202Async(http, started, CancellationToken.None);
                        Check("fabric deploy: the LRO polls the deploy to Succeeded", state.Status == "Succeeded");
                    }

                    // Deployment history parsing (nested note / diff counts / performedBy).
                    var histHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"op1\",\"type\":\"Deploy\",\"status\":\"Succeeded\",\"sourceStageId\":\"s0\",\"targetStageId\":\"s1\",\"note\":{\"content\":\"nightly\",\"isTruncated\":false},\"preDeploymentDiffInformation\":{\"newItemsCount\":2,\"differentItemsCount\":1,\"noDifferenceItemsCount\":3},\"performedBy\":{\"id\":\"u1\",\"type\":\"User\"}}]}"),
                    });
                    using (var http = new HttpClient(histHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var hist = await FabricRest.GetAllPagesAsync<DeploymentHistoryEntry>(http, "deploymentPipelines/p/operations", CancellationToken.None);
                        Check("fabric history: parses status + diff counts + note + performedBy",
                            hist.Count == 1 && hist[0].Status == "Succeeded" && hist[0].PreDeploymentDiffInformation.NewItemsCount == 2 && hist[0].Note.Content == "nightly" && hist[0].PerformedBy.Type == "User");
                    }
                    Console.WriteLine("[i] deploy lane: gate matrix + POST/202/poll + history verified offline (no live deploy executed)");
                }

                // ---- FABRIC GIT (workspace ⇄ git): parsing + the LRO result-leg + POST status (mocked) ----
                {
                    // ParseGitStatus flattens the nested itemMetadata.itemIdentifier and flags conflicts.
                    var gs = FabricRest.ParseGitStatus("{\"workspaceHead\":\"aaa\",\"remoteCommitHash\":\"bbb\",\"changes\":[{\"itemMetadata\":{\"itemIdentifier\":{\"objectId\":\"o1\",\"logicalId\":\"l1\"},\"itemType\":\"SemanticModel\",\"displayName\":\"Sales\"},\"workspaceChange\":\"Modified\",\"conflictType\":\"Conflict\"}]}");
                    Check("fabric git: ParseGitStatus flattens itemIdentifier + flags conflicts",
                        gs.WorkspaceHead == "aaa" && gs.RemoteCommitHash == "bbb" && gs.Changes.Length == 1 && gs.Changes[0].ObjectId == "o1" && gs.Changes[0].ItemType == "SemanticModel" && gs.Changes[0].WorkspaceChange == "Modified" && gs.Conflicts);

                    var gc = FabricRest.ParseGitConnection("{\"gitConnectionState\":\"ConnectedAndInitialized\",\"gitProviderDetails\":{\"gitProviderType\":\"AzureDevOps\",\"organizationName\":\"Contoso\",\"projectName\":\"BI\",\"repositoryName\":\"models\",\"branchName\":\"main\",\"directoryName\":\"/\"},\"gitSyncDetails\":{\"head\":\"abc123\",\"lastSyncTime\":\"2026-06-20T10:00:00Z\"}}");
                    Check("fabric git: ParseGitConnection flattens provider + sync details",
                        gc.State == "ConnectedAndInitialized" && gc.ProviderType == "AzureDevOps" && gc.Organization == "Contoso" && gc.Repository == "models" && gc.Branch == "main" && gc.Head == "abc123");

                    // The LRO RESULT leg (git status / getDefinition): GET → 202 → poll → operations/{id}/result body.
                    var resultHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-g"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\",\"percentComplete\":50}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"percentComplete\":100}"),
                        _ => Json(HttpStatusCode.OK, "{\"workspaceHead\":\"zzz\",\"changes\":[]}"),
                    });
                    using (var http = new HttpClient(resultHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var body = await FabricRest.GetForResultAsync(http, "workspaces/w/git/status", CancellationToken.None);
                        Check("fabric git: a 202 GET polls then fetches operations/{id}/result", body.Contains("\"workspaceHead\":\"zzz\"") && resultHandler.Requests.Any(u => u.Contains("/operations/op-g/result")));
                    }
                    // A 200-synchronous GET returns the body directly (no poll).
                    using (var http = new HttpClient(new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.OK, "{\"gitConnectionState\":\"Connected\"}") }) { }) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                        Check("fabric git: a 200 GET returns the body directly (no LRO poll)", (await FabricRest.GetForResultAsync(http, "workspaces/w/git/connection", CancellationToken.None)).Contains("Connected"));

                    // PostForStatusAsync: a commit POST → 202 → poll → Succeeded; and a 200-sync; and a 4xx error.
                    var commitHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-c"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"percentComplete\":100}"),
                    });
                    using (var http = new HttpClient(commitHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var o = await FabricRest.PostForStatusAsync(http, "workspaces/w/git/commitToGit", "{\"mode\":\"All\"}", CancellationToken.None);
                        Check("fabric git: a commitToGit POST resolves the 202 LRO to Succeeded", o.Status == "Succeeded" && commitHandler.Requests[0].StartsWith("POST"));
                    }
                    using (var http = new HttpClient(new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.OK, "") }) { }) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                        Check("fabric git: a 200-sync POST is Succeeded", (await FabricRest.PostForStatusAsync(http, "workspaces/w/git/disconnect", "{}", CancellationToken.None)).Status == "Succeeded");
                    using (var http = new HttpClient(new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[] { _ => Json(HttpStatusCode.Conflict, "{\"errorCode\":\"WorkspaceHeadMismatch\",\"message\":\"stale head\"}") }) { }) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var o = await FabricRest.PostForStatusAsync(http, "workspaces/w/git/commitToGit", "{}", CancellationToken.None);
                        Check("fabric git: a 409 POST surfaces the errorCode (not swallowed)", o.Status == "Failed" && o.Error != null && o.Error.Contains("WorkspaceHeadMismatch"));
                    }
                    Console.WriteLine("[i] fabric git: parsing + the 202 result-leg + POST-status verified offline (no live sync)");
                }

                // ---- REVIEW-FIX (phase 4): door-safety + agent gates + LRO error surfacing (all offline) ----
                {
                    // Door-safety (golden rule #2): a read / dry-run whose setup fails reports the failure on the DTO's
                    // .Error instead of THROWING across the door. authMode="token" with no raw token throws inside
                    // AcquireFabricAsync before any network — the engine must catch it and report it in-band.
                    var stRead = await engine.FabricGitStatusAsync("w", "token", null);
                    Check("review-fix: a failing git status read reports .Error (not thrown across the door)", stRead.Error != null && (stRead.Changes?.Length ?? 0) == 0);
                    var connRead = await engine.FabricGitConnectionAsync("w", "token", null);
                    Check("review-fix: a failing git connection read reports .Error", connRead.Error != null);
                    var commitDry = await engine.FabricGitCommitAsync("w", null, null, false, "token", null, "human");
                    Check("review-fix: a commit DRY-RUN whose status read fails reports .Error (not thrown)", commitDry.Error != null && !commitDry.Committed);

                    // Agent gate: an agent must not force-overwrite the workspace (update+allowOverride) or disconnect —
                    // REFUSED on the DTO before any token/network. A plain agent update (no override) passes the gate.
                    var agentForce = await engine.FabricGitUpdateAsync("w", "PreferRemote", true, true, "token", null, "agent");
                    Check("review-fix: an AGENT update+allowOverride+commit is REFUSED (no force-overwrite)", !agentForce.Committed && agentForce.Error != null && agentForce.Error.Contains("agent"));
                    var agentDisc = await engine.FabricGitDisconnectAsync("w", true, "token", null, "agent");
                    Check("review-fix: an AGENT disconnect+commit is REFUSED", !agentDisc.Committed && agentDisc.Error != null && agentDisc.Error.Contains("agent"));
                    var agentSoft = await engine.FabricGitUpdateAsync("w", "PreferRemote", false, true, "token", null, "agent");
                    Check("review-fix: an AGENT update WITHOUT allowOverride passes the gate (fails later on auth, not a refusal)", agentSoft.Error != null && !agentSoft.Error.Contains("force-overwrite"));

                    // GitHub connect without a connectionId is refused LOCALLY (Automatic creds are blocked for GitHub) —
                    // no doomed live POST.
                    var ghConnect = await engine.FabricGitConnectAsync("w", "GitHub", "owner", null, "repo", "main", null, null, true, "token", null, "human");
                    Check("review-fix: a GitHub connect without a connectionId is refused locally", !ghConnect.Committed && ghConnect.Error != null && ghConnect.Error.Contains("connectionId"));

                    // LRO error surfacing: a Failed terminal now surfaces errorCode + the operation id on .Error.
                    var failLro = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-f"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Failed\",\"error\":{\"errorCode\":\"PublishFailed\",\"message\":\"boom\"}}"),
                    });
                    using (var http = new HttpClient(failLro) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var o = await FabricRest.PostForStatusAsync(http, "workspaces/w/items/i/updateDefinition", "{}", CancellationToken.None);
                        Check("review-fix: a Failed LRO terminal surfaces errorCode + operation id on .Error", o.Status == "Failed" && o.Error != null && o.Error.Contains("PublishFailed") && o.Error.Contains("op-f"));
                    }
                    // LRO timeout: the poll window exhausting returns Running WITH an actionable message (no longer dropped).
                    var runningLro = new ScriptedHandler(Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(
                        _ => { var r = Json(HttpStatusCode.OK, "{\"status\":\"Running\",\"percentComplete\":10}"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; }, 4));
                    using (var http = new HttpClient(runningLro) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var stt = await FabricRest.PollOperationAsync(http, "op-r", CancellationToken.None, 0, 2);
                        Check("review-fix: a poll-window timeout returns Running with an actionable message", stt.Status == "Running" && stt.ErrorMessage != null && stt.ErrorMessage.Contains("poll window"));
                    }
                    Console.WriteLine("[i] review-fix: door-safety + agent gates + LRO error surfacing verified offline");
                }

                // ---- PHASE 5: CI/CD publish (Items API, gated) + cicd_generate (fabric-cicd scaffold) ----
                {
                    // cicd_generate is PURE FILE AUTHORING (no gate, no token, no network). GitHub target.
                    var gh = await engine.CicdGenerateAsync("github", "ws-123", "TEST", false);
                    var ghParam = gh.Files.FirstOrDefault(f => f.Path == "parameter.yml");
                    var ghFlow = gh.Files.FirstOrDefault(f => f.Path == ".github/workflows/fabric-cicd.yml");
                    var ghPy = gh.Files.FirstOrDefault(f => f.Path == ".deploy/deploy.py");
                    Check("cicd generate (github): emits parameter.yml + the GH Actions workflow + deploy.py",
                        gh.Error == null && !gh.Written && ghParam != null && ghFlow != null && ghPy != null);
                    Check("cicd generate (github): parameter.yml carries the QUOTED env key + the $workspace.$id token + the seeded workspace id",
                        ghParam.Content.Contains("\"TEST\":") && ghParam.Content.Contains("$workspace.$id") && ghParam.Content.Contains("ws-123"));
                    Check("cicd generate (github): the workflow installs + the deploy script runs the REAL fabric-cicd",
                        ghFlow.Content.Contains("pip install fabric-cicd") && ghFlow.Content.Contains("python .deploy/deploy.py") && ghPy.Content.Contains("publish_all_items"));
                    // A YAML-special / coercible env name is rejected (it becomes a YAML key) rather than emitting a broken file.
                    var badEnv = await engine.CicdGenerateAsync("github", null, "prod: east", false);
                    Check("cicd generate: an invalid environment name is refused (no broken YAML emitted)",
                        badEnv.Error != null && (badEnv.Files == null || badEnv.Files.Length == 0));

                    // ADO target swaps the workflow for an azure-pipelines.yml (AzureCLI@2).
                    var ado = await engine.CicdGenerateAsync("ado", null, "PROD", false);
                    var adoPipe = ado.Files.FirstOrDefault(f => f.Path == "azure-pipelines.yml");
                    Check("cicd generate (ado): emits azure-pipelines.yml (AzureCLI@2) instead of a GH workflow",
                        ado.Error == null && adoPipe != null && adoPipe.Content.Contains("AzureCLI@2") && adoPipe.Content.Contains("pip install fabric-cicd")
                        && ado.Files.All(f => f.Path != ".github/workflows/fabric-cicd.yml"));

                    // EnumerateModelParts: POSIX paths, InlineBase64 payloads (base64 of the file bytes); the .pbi cache,
                    // the .platform item-envelope, and a stale model.bim beside definition/ are all EXCLUDED (not Fabric parts).
                    var partRoot = Path.Combine(Path.GetTempPath(), "semanticus_parts_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(Path.Combine(partRoot, "definition", "tables"));
                    Directory.CreateDirectory(Path.Combine(partRoot, ".pbi"));
                    File.WriteAllText(Path.Combine(partRoot, "definition", "model.tmdl"), "model Model");
                    File.WriteAllText(Path.Combine(partRoot, "definition", "tables", "Sales.tmdl"), "table Sales");
                    File.WriteAllText(Path.Combine(partRoot, "definition.pbism"), "{\"version\":\"4.0\"}");
                    File.WriteAllText(Path.Combine(partRoot, ".platform"), "{\"metadata\":{\"type\":\"SemanticModel\"}}");
                    File.WriteAllText(Path.Combine(partRoot, "model.bim"), "{}");   // stale TMSL beside the TMDL definition/ — must be dropped
                    File.WriteAllText(Path.Combine(partRoot, ".pbi", "localSettings.json"), "{}");
                    var parts = LocalEngine.EnumerateModelParts(partRoot);
                    var tablePart = parts.FirstOrDefault(p => p.path == "definition/tables/Sales.tmdl");
                    Check("cicd publish: EnumerateModelParts → POSIX/InlineBase64 parts, excluding .pbi + .platform + a stale model.bim",
                        parts.Count == 3 && parts.All(p => !p.path.Contains("\\") && p.payloadType == "InlineBase64")
                        && tablePart.payload == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("table Sales"))
                        && parts.All(p => p.path != ".platform" && p.path != "model.bim" && !p.path.StartsWith(".pbi/")));
                    Check("cicd publish: EnumeratePartPaths (the dry-run path) returns the same set without reading bytes",
                        LocalEngine.EnumeratePartPaths(partRoot).OrderBy(p => p).SequenceEqual(parts.Select(p => p.path).OrderBy(p => p)));
                    try { Directory.Delete(partRoot, true); } catch { }

                    // cicd_publish agent gate: an agent can NEVER run the live publish (full overwrite, no recovery) —
                    // refused before any session/token. (Precedes the session check, so no open model is needed.)
                    var pubAgent = await engine.CicdPublishAsync("ws", "item", true, "azcli", null, "agent");
                    Check("cicd publish: an AGENT live publish (commit=true) is REFUSED (a human confirms)",
                        !pubAgent.Committed && pubAgent.Error != null && pubAgent.Error.Contains("agent"));

                    // A folder with a definition/ tree but MISSING the required definition.pbism is refused pre-flight
                    // (it would otherwise POST an incomplete payload → CorruptedPayload after the destructive override).
                    var noPbism = Path.Combine(Path.GetTempPath(), "semanticus_nopbism_" + Guid.NewGuid().ToString("N").Substring(0, 8), "M.SemanticModel");
                    Directory.CreateDirectory(Path.Combine(noPbism, "definition"));
                    File.WriteAllText(Path.Combine(noPbism, "definition", "model.tmdl"), "model M");
                    await engine.OpenAsync(bim);
                    await engine.SaveAsync(noPbism, "TMDL");   // re-anchor SourcePath to this folder
                    // SaveAsync flattened tmdl into noPbism root + still no definition.pbism → not a complete .SemanticModel.
                    var pubNoPbism = await engine.CicdPublishAsync("ws", "item", false, "azcli", null, "human");
                    Check("cicd publish: a folder missing definition.pbism is refused (incomplete .SemanticModel)",
                        pubNoPbism.Error != null && pubNoPbism.Error.Contains("definition.pbism"));
                    try { Directory.Delete(Path.GetDirectoryName(noPbism), true); } catch { }

                    // cicd_publish needs a Fabric .SemanticModel layout (definition/ + definition.pbism) — a flat raw-TMDL dump is refused.
                    var pubModelRoot = Path.Combine(Path.GetTempPath(), "semanticus_pub_" + Guid.NewGuid().ToString("N").Substring(0, 8), "AdventureWorks.SemanticModel");
                    Directory.CreateDirectory(pubModelRoot);
                    await engine.OpenAsync(bim);
                    await engine.SaveAsync(pubModelRoot, "TMDL");   // flat raw-TMDL (no definition/) — NOT a Fabric item layout
                    var pubFlat = await engine.CicdPublishAsync("ws", "item", false, "azcli", null, "human");
                    Check("cicd publish: a flat raw-TMDL folder (no definition/) is refused with a clear message",
                        pubFlat.Error != null && pubFlat.Error.Contains("definition/"));

                    // Reshape into a real Fabric .SemanticModel (nest the tmdl under definition/ + add definition.pbism/.platform),
                    // then a dry-run enumerates the parts + POSTs nothing.
                    var defDir = Path.Combine(pubModelRoot, "definition");
                    Directory.CreateDirectory(defDir);
                    foreach (var f in Directory.EnumerateFiles(pubModelRoot, "*.tmdl", SearchOption.TopDirectoryOnly).ToArray()) File.Move(f, Path.Combine(defDir, Path.GetFileName(f)));
                    foreach (var d in Directory.EnumerateDirectories(pubModelRoot).Where(d => !Path.GetFileName(d).Equals("definition", StringComparison.OrdinalIgnoreCase)).ToArray()) Directory.Move(d, Path.Combine(defDir, Path.GetFileName(d)));
                    File.WriteAllText(Path.Combine(pubModelRoot, "definition.pbism"), "{\"version\":\"4.0\",\"settings\":{}}");
                    File.WriteAllText(Path.Combine(pubModelRoot, ".platform"), "{\"metadata\":{\"type\":\"SemanticModel\",\"displayName\":\"AdventureWorks\"}}");
                    var pubDry = await engine.CicdPublishAsync("ws-1", "model-1", false, "azcli", null, "human");
                    Check("cicd publish: a dry-run on a .SemanticModel folder enumerates the parts + targets the model (POSTs nothing)",
                        !pubDry.Committed && pubDry.Error == null && pubDry.PartCount > 5 && pubDry.ModelPath == Path.GetFullPath(pubModelRoot)
                        && pubDry.SampleParts.All(p => !p.Contains("\\")) && pubDry.Plan != null && pubDry.Plan.Contains("model-1"));
                    try { Directory.Delete(Path.GetDirectoryName(pubModelRoot), true); } catch { }

                    // The Items-API updateDefinition POST resolves a 202 LRO to Succeeded against the right URL.
                    var pubHandler = new ScriptedHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { var r = Json(HttpStatusCode.Accepted, ""); r.Headers.Add("x-ms-operation-id", "op-p"); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                        _ => Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"percentComplete\":100}"),
                    });
                    using (var http = new HttpClient(pubHandler) { BaseAddress = new Uri(FabricRest.BaseUrl) })
                    {
                        var o = await FabricRest.PostForStatusAsync(http, "workspaces/w/semanticModels/m/updateDefinition", "{\"definition\":{\"parts\":[]}}", CancellationToken.None);
                        Check("cicd publish: the updateDefinition POST resolves the 202 LRO to Succeeded on the right URL",
                            o.Status == "Succeeded" && pubHandler.Requests[0].StartsWith("POST") && pubHandler.Requests[0].Contains("/semanticModels/m/updateDefinition"));
                    }
                    Console.WriteLine("[i] cicd: scaffold generation + parts enumeration + the publish gate/dry-run + the updateDefinition POST verified offline");
                }

                // ---- LIVE Fabric REST verification (READ-ONLY) — gated on a configured service principal -----------
                // Runs ONLY when the SP env vars are present (FABRIC_CLIENT/SECRET/TENANT, or the AZURE_* equivalents),
                // so offline CI skips it and stays green; CI WITH the service principal secrets verifies the live Fabric
                // auth + read surface end-to-end against the real tenant. STRICTLY read-only — no deploy_stage / git_commit
                // / git_update / cicd_publish (those are confirm+SEMANTICUS_LIVE_WRITES_OK gated and never run here).
                if (!HasServicePrincipal())
                    Console.WriteLine("[i] live Fabric: no service principal env (FABRIC_CLIENT/SECRET/TENANT) — skipping live verification (offline-green).");
                else
                {
                    try
                    {
                        Console.WriteLine("[i] live Fabric: service principal detected — verifying the READ-ONLY Fabric REST surface against the tenant…");
                        var ws = await engine.ListWorkspacesAsync("serviceprincipal", null);
                        Check("live Fabric: list_workspaces returns over the wire (SP authenticated; request + pagination + parse OK)", ws != null && ws.Length >= 0);
                        var pipes = await engine.ListDeploymentPipelinesAsync("serviceprincipal", null);
                        Check("live Fabric: list_deployment_pipelines returns over the wire", pipes != null);
                        if (pipes != null && pipes.Length > 0)
                        {
                            var stages = await engine.GetPipelineStagesAsync(pipes[0].Id, "serviceprincipal", null);
                            Check("live Fabric: get_pipeline_stages returns a real pipeline's stage board", stages != null && stages.Length > 0);
                            if (stages != null && stages.Length >= 2)
                            {
                                // The rail-#2 door-safe contract, verified LIVE: a Fabric 4xx (e.g. a stage with no assigned
                                // workspace) comes back on .Error, NOT as a thrown exception across the door.
                                var ordered = stages.OrderBy(s => s.Order).ToArray();
                                var prev = await engine.PreviewDeployAsync(pipes[0].Id, ordered[0].Id, ordered[1].Id, "serviceprincipal", null);
                                Check("live Fabric: preview_deploy is door-safe live (returns a DTO; a Fabric 4xx lands on .Error, never thrown)", prev != null);
                            }
                        }
                        Console.WriteLine("[i] live Fabric: read-only surface verified against the tenant (no writes performed).");
                    }
                    catch (Exception ex) { Check("live Fabric: read-only verification completed without an unhandled error — " + ex.Message.Split('\n')[0], false); }
                }

                Console.WriteLine();
                if (_failures == 0) { Console.WriteLine("==== CICD SMOKE: PASS ===="); return 0; }
                Console.WriteLine($"==== CICD SMOKE: {_failures} CHECK(S) FAILED ===="); return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n==== CICD SMOKE: EXCEPTION ====");
                Console.WriteLine(ex);
                return 2;
            }
            finally { try { sessions.Dispose(); } catch { } }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        // True when a service principal is configured (the same env vars EntraToken.ClientSecret reads). Gates the
        // live Fabric block so it runs ONLY where creds exist — offline CI skips it; CI with the SP secrets runs it.
        private static bool HasServicePrincipal()
        {
            static bool Set(params string[] names) => names.Any(n => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n)));
            return Set("FABRIC_CLIENT", "AZURE_CLIENT_ID", "POWERBI_CLIENT_ID")
                && Set("FABRIC_SECRET", "AZURE_CLIENT_SECRET", "POWERBI_CLIENT_SECRET")
                && Set("FABRIC_TENANT", "AZURE_TENANT_ID", "POWERBI_TENANT_ID");
        }

        private static string FindTestData(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                // submodule (external/TabularEditor) first so a fresh clone / CI runner finds the data; sibling fallback.
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var c = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", fileName);
                    if (File.Exists(c)) return c;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        // A hand-rolled HttpMessageHandler (no test framework in this project) that dequeues canned responses in
        // order and records the requested URLs — lets the smoke drive FabricRest's pagination / LRO / error paths
        // offline, with zero live tenant.
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
            public List<string> Requests { get; } = new();
            public ScriptedHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) => _responses = new(responses);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request.Method + " " + (request.RequestUri?.ToString() ?? ""));
                var fn = _responses.Count > 0 ? _responses.Dequeue() : (_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(fn(request));
            }
        }
    }
}
