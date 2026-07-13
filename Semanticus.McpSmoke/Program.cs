using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using StreamJsonRpc;

namespace Semanticus.McpSmoke
{
    /// <summary>
    /// M1 Phase B proof: an MCP TOOL CALL edits the shared live session and the UI sees it, tagged
    /// as the agent. Wiring mirrors the real --mcp host attaching to a running engine:
    ///   owner engine (RpcServer) ── pipe ──┬── UI client (plain JSON-RPC, watches model/didChange)
    ///                                       └── RemoteEngine proxy ← McpTools.* (the actual MCP tools)
    /// Driving McpTools through the proxy is exactly what the SDK does (it injects IEngine and calls
    /// the tool methods), so this verifies the tool bodies + proxy + cross-process dual-drive + origin.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static async Task<int> Main()
        {
            var policyRoot = Path.Combine(Path.GetTempPath(), "semanticus-mcpsmoke-policy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var realApprovalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus", "agent-approvals.json");
            var realApprovalBefore = ReadSnapshot(realApprovalPath);
            AgentPolicyStore.RootOverride = policyRoot;
            ApprovalLedger.RootOverride = policyRoot;
            ConnectionRegistry.RootOverride = policyRoot;
            var pipeName = "semanticus-mcpsmoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            // Smoke harness runs as Pro so it exercises the BULK functionality; the Pro GATE itself is covered by
            // Semanticus.Tests/EntitlementGateTests.
            var owner = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, owner, pipeName);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);

            UiClient ui = null;
            RemoteEngine claude = null;
            try
            {
                var bim = FindTestBim();
                var open = await owner.OpenAsync(bim);   // VS-Code-side opens the model
                Check("owner opened the model", open.Tables > 0);
                Console.WriteLine($"[i] owner opened '{open.ModelName}': {open.Tables} tables, {open.Measures} measures");

                ui = await UiClient.ConnectAsync(pipeName);              // the "VS Code UI"
                claude = await RemoteEngine.ConnectAsync(pipeName);     // the IEngine the --mcp host injects

                // MCP tool: model_overview
                var ov = await McpTools.ModelOverview(claude);
                Check("MCP tool model_overview returns counts", ov != null && ov.Tables > 0);
                var primer = await McpTools.GetModelPrimer(claude);
                Check("MCP get_model_primer returns the six-section current-model Markdown through the shared door",
                    primer?.Markdown != null && PrimerContract.Sections.All(s => primer.Markdown.Contains("## " + s)));

                // MCP tool: export_test_report. A current-model run is required, then the Pro export returns
                // both formats without repeating either blob into model/activity. The human door sees the agent's read
                // live, proving the new tool follows the same activity/experience path as the other MCP reads.
                var testRun = await McpToolsTesting.RunTests(claude);
                Check("MCP run_tests produces current-model health before export", testRun?.Health != null && testRun.Error == null);
                var reportActivityWait = ui.WaitActivityAsync(e => e?.Kind == "export_test_report");
                var testReport = await McpToolsTesting.ExportTestReport(claude);
                Check("MCP export_test_report returns current-model Markdown + HTML",
                    testReport?.Error == null && !string.IsNullOrEmpty(testReport.Markdown)
                    && testReport.Markdown.Contains("# Semanticus test report")
                    && testReport.Html?.StartsWith("<!doctype html>", StringComparison.Ordinal) == true);
                var reportActivity = await reportActivityWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI receives export_test_report activity without the Markdown blob",
                    reportActivity != null && reportActivity.Ok && reportActivity.Origin == "agent"
                    && Convert.ToString(reportActivity.Result) == "Report artifacts generated"
                    && !Convert.ToString(reportActivity.Result).Contains("# Semanticus test report"));

                // MCP tool: list_objects (root -> tables -> measures)
                string measureRef = null;
                foreach (var t in await McpTools.ListObjects(claude, null))
                {
                    var kids = await McpTools.ListObjects(claude, t.Ref);
                    var m = kids.FirstOrDefault(x => x.Kind == "measure");
                    if (m != null) { measureRef = m.Ref; break; }
                }
                Check("MCP tool list_objects found a measure", measureRef != null);
                Console.WriteLine($"[i] claude target: {measureRef}");

                var impactActivityWait = ui.WaitActivityAsync(e => e?.Kind == "impact_assessment");
                var assessment = await McpToolsImpactAssessment.ImpactAssessment(claude, measureRef, "rename");
                Check("MCP impact_assessment composes blast radius + explicit report unknowns through the shared door",
                    assessment != null && assessment.ObjectRef == measureRef
                    && assessment.ModelImpact != null && assessment.Coverage.Any(c => c.Area == "model" && c.Status == "complete")
                    && assessment.Unknowns.Any(u => u.Contains("Published-report usage"))
                    && assessment.Verdict != "Verified" && assessment.SuggestedNextAction != null);
                var impactActivity = await impactActivityWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI receives the compact impact_assessment activity from the agent door",
                    impactActivity != null && impactActivity.Origin == "agent" && impactActivity.Target == measureRef
                    && impactActivity.Label == "Assessed rename impact");

                var before = await McpTools.GetDax(claude, measureRef);

                // MCP tool: update_measure  -> UI must see it, tagged origin=agent
                var newExpr = (before ?? "") + " /* edit-by-claude */";
                var wait = ui.Notify.WaitNextAsync();
                var res = await McpTools.UpdateMeasure(claude, measureRef, newExpr);
                Check("MCP tool update_measure reported changed", res.Changed);

                var n = await wait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI received model/didChange from the MCP edit", n != null && n.Deltas.Any(d => d.Ref == measureRef));
                Check("the MCP edit is tagged origin = \"agent\"", n.Origin == "agent");

                var claudeReads = await McpTools.GetDax(claude, measureRef);
                Check("Claude reads its own edit on the shared session", claudeReads == newExpr);

                var uiReads = await ui.GetDax(measureRef);
                Check("UI reads the Claude edit on the same shared session", uiReads == newExpr);

                // MCP tools: undo_change / redo_change on the SHARED undo timeline (golden rule #2 — the agent can
                // revert its own change, live to the UI). Undo then redo leaves the model exactly as it was here.
                var undoWait = ui.Notify.WaitNextAsync();
                var undone = await McpTools.UndoChange(claude);
                Check("MCP undo_change reverts on the shared session (Claude reads the original) + reports redo-able",
                    undone != null && undone.CanRedo && await McpTools.GetDax(claude, measureRef) == before);
                var un = await undoWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("the agent's undo broadcasts live to the UI, tagged origin=agent (other client sees the revert)",
                    un != null && un.Origin == "agent" && await ui.GetDax(measureRef) == before);
                var redone = await McpTools.RedoChange(claude);
                Check("MCP redo_change re-applies the edit (Claude + UI both read it again) + reports undo-able",
                    redone != null && redone.CanUndo
                    && await McpTools.GetDax(claude, measureRef) == newExpr && await ui.GetDax(measureRef) == newExpr);

                // ---- HEALTH DELTA (feature #4): a threshold-crossing agent edit (an undescribed measure = a
                // net-new Warning+ finding on the touched object) rides model/didChange to the UI client with
                // Health populated, and the agent door's tool-result block drains over the CROSS-PROCESS proxy
                // (pull_agent_health is exactly what the MCP success filter appends). The owner is Pro (DevPro),
                // so the probe is installed; earlier legs may have stashed blocks — drain first.
                await claude.PullAgentHealthAsync();
                var healthTableRef = "table:" + measureRef.Substring("measure:".Length, measureRef.LastIndexOf('/') - "measure:".Length);
                var healthWait = ui.Notify.WaitNextAsync();
                var healthRef = await McpTools.CreateMeasure(claude, healthTableRef, "Smoke Health Probe", "1");
                var hn = await healthWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("health delta rides model/didChange to the UI (Pro owner; net-new Warning+ finding on the touched object)",
                    hn?.Health != null && hn.Health.New != null && hn.Health.New.Contains("DESC-MEASURE") && (hn.Health.Findings ?? 0) >= 1);
                var pulledHealth = await claude.PullAgentHealthAsync();
                Check("PullAgentHealth returns the agent block across the pipe (the MCP success filter's data path)",
                    pulledHealth != null && pulledHealth.New != null && pulledHealth.New.Contains("DESC-MEASURE"));
                Check("PullAgentHealth is take-once (a second pull is null)", await claude.PullAgentHealthAsync() == null);
                await McpTools.UndoChange(claude);   // remove the probe measure so later legs see the model they expect

                // ---- NUMBER TIME-MACHINE (feature #3): blame_value / list_value_history through the MCP
                // proxy (McpTools body → RemoteEngine → RPC → owner). This model has no recorded history, so
                // the HONEST answers are 'inconclusive' and an empty series with a note — never a throw, never
                // a guess. Verdict semantics + ambient capture are pinned in Semanticus.Tests/ValueBlameTests.
                var blame = await McpTools.BlameValue(claude, measureRef);
                Check("MCP blame_value is honest with no recorded history (Status=ok, Verdict=inconclusive, Note says why)",
                    blame != null && blame.Status == "ok" && blame.Verdict == "inconclusive" && !string.IsNullOrEmpty(blame.Note));
                var vhist = await McpTools.ListValueHistory(claude, measureRef);
                Check("MCP list_value_history round-trips (Status=ok, 0 points, note explains how history builds)",
                    vhist != null && vhist.Status == "ok" && vhist.Points.Length == 0 && !string.IsNullOrEmpty(vhist.Note));

                // ---- EXPLAIN THIS NUMBER (feature #2): explain_value through the MCP proxy. FREE + read-only;
                // this session has no live connection, so the dossier must DEGRADE honestly (metadata-only:
                // chain ships, Evidence.Available=false, Summary says the value wasn't computed) — never a
                // throw, never a gate. Guard/checklist semantics are pinned in Semanticus.Tests/ExplainValueTests.
                var explain = await McpTools.ExplainValue(claude, measureRef,
                    new ExplainFilterContext { Filters = new[] { new ExplainFilter { Column = "'Date'[Calendar Year]", Members = new[] { "2024" } } } });
                Check("MCP explain_value degrades honestly offline (Status=ok, value not computed, evidence unavailable, summary says why)",
                    explain != null && explain.Status == "ok" && !explain.ValueEvaluated && explain.Evidence != null
                    && !explain.Evidence.Available && explain.Chain != null && !string.IsNullOrEmpty(explain.Summary)
                    && explain.Summary.Contains("No live connection"));
                Check("MCP explain_value echoes the cell's filter context into the evidence query (TREATAS shape)",
                    explain != null && explain.Evidence.Query != null && explain.Evidence.Query.Contains("TREATAS"));

                // MCP tool: search_model — read-only find over Name / Description / DAX Expression.
                var mName = measureRef.Substring(measureRef.LastIndexOf('/') + 1);
                var byName = await McpTools.SearchModel(claude, mName, 500);
                Check("MCP search_model finds the measure by name (correct ref + kind + Where=Name)",
                    byName.Hits.Any(h => h.Ref == measureRef && h.Kind == "measure" && h.Where == "Name"));
                var byExpr = await McpTools.SearchModel(claude, "edit-by-claude", 500);
                Check("MCP search_model finds the measure by a token in its DAX (Where=Expression, snippet carries it)",
                    byExpr.Hits.Any(h => h.Ref == measureRef && h.Where == "Expression" && h.Snippet.Contains("edit-by-claude")));
                var noneHits = await McpTools.SearchModel(claude, "zzz_no_such_token_zzz", 500);
                Check("MCP search_model returns no hits for an absent token", noneHits.Total == 0 && noneHits.Hits.Length == 0);
                // search now covers security roles too (by name) — find an existing role and confirm a role-kind hit.
                var roles = await McpTools.ListRoles(claude);
                if (roles.Length > 0)
                {
                    var byRole = await McpTools.SearchModel(claude, roles[0].Name, 500);
                    Check("MCP search_model finds a security role by name (Kind=role, ref=role:<name>)",
                        byRole.Hits.Any(h => h.Kind == "role" && h.Ref == "role:" + roles[0].Name));
                }
                else Console.WriteLine("[i] search_model role coverage: model has no roles, skipped");
                Check("MCP search_model is read-only (the measure's DAX is unchanged after searching)",
                    await McpTools.GetDax(claude, measureRef) == newExpr);

                // ---- DETAILED SEARCH (Phase 1): modes + wider surface + MatchClass + spans, all through the proxy.
                // Whole-word: a strict prefix of the name must NOT match; the full name must (proves boundaries).
                var wwPrefix = mName.Length > 3 ? mName.Substring(0, mName.Length - 1) : mName;
                var wwStrict = await McpTools.SearchModel(claude, wwPrefix, 500, wholeWord: true);
                var wwFull = await McpTools.SearchModel(claude, mName, 500, wholeWord: true);
                Check("MCP search_model(wholeWord): the full name matches; a strict prefix of it does not",
                    wwFull.Hits.Any(h => h.Ref == measureRef && h.Field == "name")
                    && !wwStrict.Hits.Any(h => h.Ref == measureRef && h.Field == "name"));
                // Case-sensitive: a wrong-case query finds nothing; the exact case finds it.
                var csWrong = await McpTools.SearchModel(claude, mName.ToUpperInvariant() + "_zz", 500, caseSensitive: true);
                Check("MCP search_model(caseSensitive): a wrong-case token does not match", !csWrong.Hits.Any(h => h.Ref == measureRef));
                // Regex: the DAX comment marker '/* edit-by-claude */' is found by a pattern; an invalid regex is REPORTED not thrown.
                var rx = await McpTools.SearchModel(claude, @"edit-by-\w+", 500, regex: true, fields: new[] { "expression" });
                Check("MCP search_model(regex): a pattern finds the token inside the DAX", rx.Hits.Any(h => h.Ref == measureRef));
                var rxBad = await McpTools.SearchModel(claude, "[unterminated", 500, regex: true);
                Check("MCP search_model(regex): an invalid pattern is reported in .Error, not thrown (fail-soft)",
                    !string.IsNullOrEmpty(rxBad.Error) && rxBad.Hits.Length == 0);
                // MatchClass: 'edit-by-claude' lives inside a /* */ comment → classified DaxComment (replaceable text),
                // and the hit carries raw-offset spans for highlighting. This is the classifier working over the proxy.
                var cls = await McpTools.SearchModel(claude, "edit-by-claude", 500, fields: new[] { "expression" });
                var clsHit = cls.Hits.FirstOrDefault(h => h.Ref == measureRef && h.Field == "expression");
                Check("MCP search_model: a DAX-comment match is classified DaxComment (replaceable) with spans + facet counts",
                    clsHit != null && clsHit.MatchClass == "DaxComment" && clsHit.Replaceable
                    && clsHit.Spans.Length > 0 && cls.ByMatchClass.Any(f => f.Key == "DaxComment" && f.Count >= 1));

                // MCP tool: list_measures — every measure with audit metadata; the edited measure is present + current.
                var measRows = await McpTools.ListMeasures(claude);
                Check("MCP list_measures returns the edited measure with its ref, table, and current expression",
                    measRows.Any(r => r.Ref == measureRef && r.Name == mName && !string.IsNullOrEmpty(r.Table) && r.Expression == newExpr));

                // MCP tool: list_columns — every column with audit metadata; refs resolve, RowNumber excluded.
                var colRows = await McpTools.ListColumns(claude);
                Check("MCP list_columns returns columns with a resolvable ref + table + data type (no internal RowNumber)",
                    colRows.Length > 0 && colRows.All(c => c.Ref.StartsWith("column:") && !string.IsNullOrEmpty(c.Table) && !string.IsNullOrEmpty(c.DataType)));

                // MCP tool: get_model_objects — the one-call object-browser dataset (backbone B for the Studio
                // Advanced-Modelling pickers). Tables + measures + columns + hierarchies in one read; the measure/
                // column projections MUST match list_measures/list_columns (shared builders), and every ref resolves.
                var objs = await McpTools.GetModelObjects(claude);
                Check("MCP get_model_objects returns the consolidated browser dataset (tables + measures + columns + hierarchies[])",
                    objs != null && objs.Tables.Length > 0 && objs.Columns.Length == colRows.Length
                    && objs.Measures.Length == measRows.Length && objs.Hierarchies != null
                    && objs.Tables.All(t => t.Ref.StartsWith("table:") && !string.IsNullOrEmpty(t.Name))
                    && objs.Hierarchies.All(h => h.Ref.StartsWith("hierarchy:") && !string.IsNullOrEmpty(h.Table) && h.Levels != null));

                // MCP tool: validate_dax — offline syntactic + reference check against the open model (the learning
                // from microsoft/powerbi-modeling-mcp's dax_query_operations.validate). Read-only; no live engine.
                var realCol = colRows[0];
                var okRef = await McpTools.ValidateDax(claude, $"SUM ( '{realCol.Table}'[{realCol.Name}] )");
                Check("MCP validate_dax: a balanced expr over a real table+column is Valid with no diagnostics",
                    okRef.Valid && okRef.Diagnostics.Length == 0);

                var unbalanced = await McpTools.ValidateDax(claude, "CALCULATE ( SUM ( Sales[Amount] )");
                Check("MCP validate_dax: unbalanced parentheses → Valid=false with an error diagnostic (line:col set)",
                    !unbalanced.Valid && unbalanced.Diagnostics.Any(d => d.Severity == "error" && d.Line >= 1 && d.Column >= 1));

                var unknownTbl = await McpTools.ValidateDax(claude, "COUNTROWS ( 'NoSuchTable_zzz' )");
                Check("MCP validate_dax: unknown table → a warning (still structurally Valid, not an error)",
                    unknownTbl.Valid && unknownTbl.Diagnostics.Any(d => d.Severity == "warning" && d.Message.Contains("NoSuchTable_zzz")));

                Check("MCP validate_dax is read-only (the measure's DAX is unchanged after validating)",
                    await McpTools.GetDax(claude, measureRef) == newExpr);

                // MCP tool: script_objects — read-only DAX/TMSL scripting of one OR many objects (powers the tree's
                // multi-select "Script ▸"). Proven read-only: the measure's DAX is identical afterward.
                var sDax = await McpTools.ScriptObjects(claude, new[] { measureRef }, "dax");
                Check("MCP script_objects(dax): emits the object ref header + its current DAX expression",
                    sDax.Contains(measureRef) && sDax.Contains(newExpr));
                // TMSL's unit is the table, so scripting a measure yields its containing table's createOrReplace.
                var sTmsl = await McpTools.ScriptObjects(claude, new[] { measureRef }, "tmsl");
                Check("MCP script_objects(tmsl): emits a createOrReplace TMSL command (table-level container)",
                    sTmsl.Contains("createOrReplace"));
                Check("MCP script_objects is read-only (the measure's DAX is unchanged after scripting)",
                    await McpTools.GetDax(claude, measureRef) == newExpr);
                // Multi-object: scripting several refs in one call yields one document capturing every selected object.
                var manyMeas = new System.Collections.Generic.List<string>();
                foreach (var t in await McpTools.ListObjects(claude, null))
                {
                    foreach (var c in await McpTools.ListObjects(claude, t.Ref))
                        if (c.Kind == "measure" && !manyMeas.Contains(c.Ref)) manyMeas.Add(c.Ref);
                    if (manyMeas.Count >= 2) break;
                }
                if (manyMeas.Count >= 2)
                    Check("MCP script_objects(multi): a single document captures every selected object",
                        (await McpTools.ScriptObjects(claude, manyMeas.Take(2).ToArray(), "dax")) is var sm
                        && sm.Contains(manyMeas[0]) && sm.Contains(manyMeas[1]));
                // 'tmdl' produces the model's native per-object TMDL (more readable/useful than TMSL).
                var measName = measureRef.Substring(measureRef.LastIndexOf('/') + 1);
                var sTmdl = await McpTools.ScriptObjects(claude, new[] { measureRef }, "tmdl");
                Check("MCP script_objects(tmdl): emits the per-object TMDL 'measure' block",
                    sTmdl.Contains("// @object " + measureRef) && sTmdl.Contains("measure") && sTmdl.Contains(measName));

                // apply_dax_script: the round-trip — edit the scripted DAX and apply it back (F5-style bulk apply).
                var editedExpr = "CALCULATE(" + newExpr + ")";
                var applyRes = await McpTools.ApplyDaxScript(claude, $"// @object {measureRef}\n{editedExpr}\n");
                Check("MCP apply_dax_script: applies the edited expression back (round-trip) + reports it applied",
                    applyRes.Applied.Contains(measureRef) && applyRes.Skipped.Length == 0
                    && await McpTools.GetDax(claude, measureRef) == editedExpr);
                await McpTools.UpdateMeasure(claude, measureRef, newExpr);   // restore for the checks below
                var skipRes = await McpTools.ApplyDaxScript(claude, "// @object measure:Nope/Missing\n1\n");
                Check("MCP apply_dax_script: a non-resolvable ref is skipped (surfaced, not applied, teaching the fix)",
                    skipRes.Skipped.Any(s => s.StartsWith("measure:Nope/Missing") && s.Contains("list_objects"))
                    && skipRes.Applied.Length == 0);   // the skip entry now carries the result-contract recovery text

                // apply_tmdl selected-measure round-trip over the MCP/RPC proxy. The native `ref table` wrapper keeps
                // every unselected sibling out of the document; undo makes this net-zero for the remaining smoke.
                var measureTmdl = sTmdl.Replace("\r\n", "\n").Split('\n').ToList();
                int selectedLine = measureTmdl.FindIndex(l => l.TrimStart().StartsWith("measure ") && l.Contains(measName));
                if (selectedLine >= 0)
                {
                    var mind = measureTmdl[selectedLine].Substring(0, measureTmdl[selectedLine].Length - measureTmdl[selectedLine].TrimStart().Length);
                    measureTmdl.Insert(selectedLine, mind + "/// selected measure via tmdl");
                    var measureApply = await McpTools.ApplyTmdl(claude, string.Join("\n", measureTmdl));
                    Check("MCP apply_tmdl: an individually scripted measure applies through the shared RPC door",
                        measureApply.Applied.SequenceEqual(new[] { measureRef }) && measureApply.Skipped.Length == 0
                        && (await McpTools.ScriptObjects(claude, new[] { measureRef }, "tmdl")).Contains("selected measure via tmdl"));
                    await McpTools.UndoChange(claude);
                    Check("MCP apply_tmdl: selected-measure apply is undoable without scripting its whole table",
                        !(await McpTools.ScriptObjects(claude, new[] { measureRef }, "tmdl")).Contains("selected measure via tmdl"));
                }

                // apply_tmdl: the TMDL round-trip — script a TABLE to TMDL, edit it (add a description to a measure),
                // apply it back IN PLACE (Power-BI-Desktop-style), and confirm it's TRACKED + UNDOABLE. Net-zero:
                // the description is added then undone, leaving the model exactly as found for the checks below.
                var tmdlTableRef = "table:" + measureRef.Substring("measure:".Length).Split('/')[0];
                var tableTmdl = (await McpTools.ScriptObjects(claude, new[] { tmdlTableRef }, "tmdl")).Replace("\r\n", "\n").Split('\n').ToList();
                int measLine = tableTmdl.FindIndex(l => l.TrimStart().StartsWith("measure ") && l.Contains(measName));
                if (measLine >= 0)
                {
                    var tind = tableTmdl[measLine].Substring(0, tableTmdl[measLine].Length - tableTmdl[measLine].TrimStart().Length);
                    tableTmdl.Insert(measLine, tind + "/// applied via tmdl");
                    var tmdlApply = await McpTools.ApplyTmdl(claude, string.Join("\n", tableTmdl));
                    Check("MCP apply_tmdl: applies the edited TMDL document back in place (round-trip)",
                        tmdlApply.Applied.Length == 1 && tmdlApply.Skipped.Length == 0
                        && (await McpTools.ScriptObjects(claude, new[] { tmdlTableRef }, "tmdl")).Contains("applied via tmdl"));
                    await McpTools.UndoChange(claude);
                    Check("MCP apply_tmdl: the apply is UNDOABLE on the shared timeline (undo removes the applied description)",
                        !(await McpTools.ScriptObjects(claude, new[] { tmdlTableRef }, "tmdl")).Contains("applied via tmdl"));
                }
                var tmdlSkip = await McpTools.ApplyTmdl(claude, "table 'No Such Table Xyz'\n\tlineageTag: x\n");
                Check("MCP apply_tmdl: a brand-new/unknown table is skipped (surfaced, not applied)",
                    tmdlSkip.Applied.Length == 0 && tmdlSkip.Skipped.Length == 1);

                // ATOMICITY: a doc whose object EXISTS but whose TMDL fails mid-apply (an invalid property) must be
                // REVERTED to its prior state (not left half-applied) and reported skipped — the table is unchanged.
                var origTbl = await McpTools.ScriptObjects(claude, new[] { tmdlTableRef }, "tmdl");
                var badLines = origTbl.Replace("\r\n", "\n").Split('\n').ToList();
                int anyMeas = badLines.FindIndex(l => l.TrimStart().StartsWith("measure "));
                if (anyMeas >= 0)
                {
                    var bind = badLines[anyMeas].Substring(0, badLines[anyMeas].Length - badLines[anyMeas].TrimStart().Length);
                    badLines.Insert(anyMeas, bind + "notARealTmdlProperty: 1");
                    var badApply = await McpTools.ApplyTmdl(claude, string.Join("\n", badLines));
                    Check("MCP apply_tmdl: a doc that fails mid-apply is REVERTED (atomic) + skipped, table unchanged",
                        badApply.Applied.Length == 0 && badApply.Skipped.Length == 1
                        && (await McpTools.ScriptObjects(claude, new[] { tmdlTableRef }, "tmdl")) == origTbl);
                }

                // MCP tool: set_relationship_active — toggle a relationship's active state (the RPC door already had
                // this via setRelationship; the MCP door lacked it). Net-zero: deactivate then reactivate, so the
                // model is left exactly as found and the readiness/BPA consistency checks below are undisturbed.
                var graph0 = await McpTools.GetModelGraph(claude);
                var activeRel = graph0.Relationships.FirstOrDefault(r => r.IsActive);
                if (activeRel != null)
                {
                    var off = await McpTools.SetRelationshipActive(claude, activeRel.Name, false);
                    var g1 = await McpTools.GetModelGraph(claude);
                    Check("MCP set_relationship_active(false) deactivates the relationship (reads back inactive)",
                        off.Changed && g1.Relationships.First(r => r.Name == activeRel.Name).IsActive == false);
                    var on = await McpTools.SetRelationshipActive(claude, activeRel.Name, true);
                    var g2 = await McpTools.GetModelGraph(claude);
                    Check("MCP set_relationship_active(true) reactivates it (net-zero — model restored)",
                        on.Changed && g2.Relationships.First(r => r.Name == activeRel.Name).IsActive);
                }

                // Token-efficient readiness surface: cheap summary (no findings list) + filtered scan.
                var full = await McpTools.AiReadinessScan(claude);
                var summary = await McpTools.AiReadinessSummary(claude);
                Check("MCP ai_readiness_summary: grade + total-finding count, no findings list dumped",
                    summary != null && !string.IsNullOrEmpty(summary.Grade)
                    && summary.TotalFindings == full.Findings.Length && summary.Categories != null);
                var firstCat = full.Findings.FirstOrDefault()?.Category;
                if (firstCat != null)
                {
                    var scoped = await McpTools.AiReadinessScan(claude, category: firstCat);
                    Check("MCP ai_readiness_scan(category): returns only that category's findings, full scores intact",
                        scoped.Findings.All(f => string.Equals(f.Category, firstCat, System.StringComparison.OrdinalIgnoreCase))
                        && scoped.Findings.Length <= full.Findings.Length
                        && scoped.Categories.Length == full.Categories.Length);
                }
                var capped = await McpTools.AiReadinessScan(claude, maxFindings: 3);
                Check("MCP ai_readiness_scan(maxFindings): caps the returned findings (explicit, not silent)",
                    capped.Findings.Length == System.Math.Min(3, full.Findings.Length)); // proves the cap actually triggers

                var high = await McpTools.AiReadinessScan(claude, severityMin: "High");
                Check("MCP ai_readiness_scan(severityMin): keeps High+Critical only (SevRank>=3), drops Info/Medium",
                    high.Findings.All(f => ReadinessSummary.SevRank(f.Severity) >= 3) && high.Findings.Length <= full.Findings.Length);
                var sevThrew = false;
                try { await McpTools.AiReadinessScan(claude, severityMin: "Low"); } catch (System.ArgumentException) { sevThrew = true; }
                Check("MCP ai_readiness_scan(severityMin): an unrecognised value ('Low') throws, not silently returns all", sevThrew);

                // Same token-efficient treatment for BPA: cheap summary + filtered scan.
                var bpaFull = await McpTools.BpaScan(claude);
                var bpaSum = await McpTools.BpaScanSummary(claude);
                Check("MCP bpa_summary: rule/violation counts, no violations list dumped",
                    bpaSum != null && bpaSum.ViolationCount == bpaFull.Violations.Length && bpaSum.ByCategory != null);
                if (bpaFull.Violations.Length > 0)
                {
                    var bRule = bpaFull.Violations[0].RuleId;
                    var bScoped = await McpTools.BpaScan(claude, ruleId: bRule);
                    Check("MCP bpa_scan(ruleId): returns only that rule's violations, full counts intact",
                        bScoped.Violations.All(v => string.Equals(v.RuleId, bRule, System.StringComparison.OrdinalIgnoreCase))
                        && bScoped.ViolationCount == bpaFull.ViolationCount);
                    var bFix = await McpTools.BpaScan(claude, autoFixableOnly: true);
                    Check("MCP bpa_scan(autoFixableOnly): returns only deterministically-fixable violations",
                        bFix.Violations.All(v => v.CanAutoFix));
                    var bCat = bpaFull.Violations[0].Category;
                    var bByCat = await McpTools.BpaScan(claude, category: bCat);
                    Check("MCP bpa_scan(category): returns only that category's violations",
                        bByCat.Violations.All(v => string.Equals(v.Category, bCat, System.StringComparison.OrdinalIgnoreCase)));
                    var bCap = await McpTools.BpaScan(claude, maxViolations: 2);
                    Check("MCP bpa_scan(maxViolations): caps the returned violations (explicit, not silent)",
                        bCap.Violations.Length == System.Math.Min(2, bpaFull.Violations.Length));
                }

                // BPA now defaults to the bundled Power BI STANDARD ruleset (not the small curated set).
                Check("MCP bpa_scan defaults to the bundled standard ruleset (the full set, not the ~9 curated)",
                    bpaFull.RuleCount == Semanticus.Analysis.BpaRuleSet.Standard().Count && bpaFull.RuleCount > 9);

                // load_bpa_rules: a custom inline rule layers on top of the standard set (persisted on the model,
                // undoable, dual-drive); reset_bpa_rules reverts to standard-only.
                var stdCount = bpaFull.RuleCount;
                const string customRule = "[{\"ID\":\"SMOKE_CUSTOM_RULE\",\"Name\":\"smoke custom\",\"Category\":\"Test\",\"Severity\":1,\"Scope\":\"Model\",\"Expression\":\"Name <> null\"}]";
                var loadWait = ui.Notify.WaitNextAsync();
                var loadedRules = await McpTools.LoadBpaRules(claude, customRule, false);
                Check("MCP load_bpa_rules (inline) layers a custom rule on top of the standard set",
                    loadedRules != null && loadedRules.Loaded == 1 && loadedRules.StandardRules == stdCount && loadedRules.ActiveRules == stdCount + 1);
                var loadNote = await loadWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("load_bpa_rules broadcasts model/didChange to the UI, tagged origin=agent (dual-drive, persisted)",
                    loadNote != null && loadNote.Origin == "agent");
                var afterLoad = await McpTools.BpaScan(claude);
                Check("the loaded custom rule is active in a scan (fires on the Model)",
                    afterLoad.RuleCount == stdCount + 1 && afterLoad.Violations.Any(v => v.RuleId == "SMOKE_CUSTOM_RULE"));
                // It is a normal undoable edit on the shared timeline: undo removes it, redo restores it.
                await McpTools.UndoChange(claude);
                Check("undo reverts load_bpa_rules (the custom rule is gone)", (await McpTools.BpaScan(claude)).RuleCount == stdCount);
                await McpTools.RedoChange(claude);
                Check("redo restores the loaded custom rule", (await McpTools.BpaScan(claude)).RuleCount == stdCount + 1);

                // File-path source: the same rule from a temp file (exercises File.ReadAllText + Source classification).
                var tmpRules = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus-bpa-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                try
                {
                    System.IO.File.WriteAllText(tmpRules, customRule);
                    var fromFile = await McpTools.LoadBpaRules(claude, tmpRules, true);   // replace=true -> exactly this set
                    Check("MCP load_bpa_rules (file path) loads from disk and reports Source=file",
                        fromFile.Source == "file" && fromFile.Loaded == 1 && fromFile.ActiveRules == stdCount + 1);
                }
                finally { try { System.IO.File.Delete(tmpRules); } catch { } }

                // Error path: an unresolvable source (not inline / not a URL / no such file) fails LOUDLY.
                var loadThrew = false;
                try { await McpTools.LoadBpaRules(claude, "C:/no/such/rules-file-x9z.json", false); } catch (Exception) { loadThrew = true; }
                Check("MCP load_bpa_rules with an unresolvable source throws a clear error (no silent no-op)", loadThrew);

                // Leniency: a hand-edited rule file (trailing comma + string-encoded number) still parses — matching
                // the Newtonsoft reader TabularEditor itself uses, so real-world community/org BPARules.json load here.
                var lenient = await McpTools.LoadBpaRules(claude,
                    "[{\"ID\":\"SMOKE_LENIENT\",\"Name\":\"x\",\"Category\":\"Test\",\"Severity\":\"1\",\"Scope\":\"Model\",\"Expression\":\"Name <> null\"},]", true);
                Check("MCP load_bpa_rules tolerates trailing commas + string-encoded numbers (TE-compatible leniency)",
                    lenient.Loaded == 1 && lenient.ActiveRules == stdCount + 1);

                var resetRules = await McpTools.ResetBpaRules(claude);
                var afterReset = await McpTools.BpaScan(claude);
                Check("MCP reset_bpa_rules reverts to the standard ruleset only",
                    resetRules.ActiveRules == stdCount && afterReset.RuleCount == stdCount
                    && afterReset.Violations.All(v => v.RuleId != "SMOKE_CUSTOM_RULE"));

                // ---- Custom READINESS rules (the readiness mirror of load_bpa_rules) over the agent door:
                // validate_rule previews without saving -> load layers additively -> scan carries provenance ->
                // undo reverts -> a built-in id collision refuses -> get_custom_rules lists -> reset clears.
                const string customAirRule = "[{\"ID\":\"SMOKE-AIR-CUSTOM\",\"Name\":\"smoke readiness custom\",\"Category\":\"BestPractice\",\"Severity\":\"Info\",\"Scope\":\"Model\",\"Expression\":\"Name <> null\",\"Message\":\"%object% flagged by the smoke rule\"}]";
                var airPreview = await McpTools.ValidateRule(claude, "readiness", customAirRule);
                Check("MCP validate_rule (readiness) compiles + test-runs against the open model without saving",
                    airPreview.AllValid && airPreview.RuleCount == 1 && airPreview.Rules[0].Applicable == 1 && airPreview.Rules[0].Violations == 1);
                var airBad = await McpTools.ValidateRule(claude, "readiness", customAirRule.Replace("BestPractice", "Sparkles"));
                Check("MCP validate_rule teaches on an invalid category (the error lists the valid ones)",
                    !airBad.AllValid && airBad.Rules[0].Errors.Any(e => e.Contains("Naming") && e.Contains("DataAgentConfig")));
                var airLoadWait = ui.Notify.WaitNextAsync();
                var airLoaded = await McpTools.LoadReadinessRules(claude, customAirRule, false);
                Check("MCP load_readiness_rules layers a custom rule (additive to the built-ins, never overriding)",
                    airLoaded.Loaded == 1 && airLoaded.ModelRules == 1 && airLoaded.ActiveRules == airLoaded.BuiltinRules + 1);
                var airNote = await airLoadWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("load_readiness_rules broadcasts model/didChange to the UI, tagged origin=agent (dual-drive, persisted)",
                    airNote != null && airNote.Origin == "agent");
                var airScan = await McpTools.AiReadinessScan(claude);
                Check("the custom readiness rule fires in ai_readiness_scan and carries custom provenance",
                    airScan.Findings.Any(f => f.RuleId == "SMOKE-AIR-CUSTOM" && f.Custom));
                await McpTools.UndoChange(claude);
                Check("undo reverts load_readiness_rules (the custom finding is gone)",
                    (await McpTools.AiReadinessScan(claude)).Findings.All(f => f.RuleId != "SMOKE-AIR-CUSTOM"));
                await McpTools.RedoChange(claude);
                var airCollided = false;
                try { await McpTools.LoadReadinessRules(claude, customAirRule.Replace("SMOKE-AIR-CUSTOM", "DESC-MEASURE"), false); }
                catch (Exception) { airCollided = true; }
                Check("MCP load_readiness_rules refuses a built-in rule id collision (teaching refusal)", airCollided);
                var customList = await McpTools.GetCustomRules(claude);
                Check("MCP get_custom_rules lists the model's custom rules + the category/scope vocabularies",
                    customList.Readiness.Length == 1 && customList.ReadinessCategories.Length > 0 && customList.Scopes.Length > 0);
                var airReset = await McpTools.ResetReadinessRules(claude);
                Check("MCP reset_readiness_rules reverts scans to the built-in rules only",
                    airReset.ModelRules == 0 && (await McpTools.AiReadinessScan(claude)).Findings.All(f => f.RuleId != "SMOKE-AIR-CUSTOM"));

                // ---- Row-Level Security (RLS) roles: create -> permission -> filter -> member -> list -> clear -> delete (net-zero).
                var rlsGraph = await McpTools.GetModelGraph(claude);
                var rlsTable = (rlsGraph.Tables.FirstOrDefault(t => !t.IsHidden) ?? rlsGraph.Tables.First()).Name;
                const string roleName = "Smoke RLS Role";
                var roleWait = ui.Notify.WaitNextAsync();
                var createdRole = await McpTools.CreateRole(claude, roleName, "Read");
                Check("MCP create_role returns the new role (Read permission)",
                    createdRole != null && createdRole.Name == roleName && createdRole.ModelPermission == "Read");
                var rcNote = await roleWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("create_role broadcasts model/didChange to the UI, tagged origin=agent (dual-drive)",
                    rcNote != null && rcNote.Origin == "agent");

                // set_role_permission mutates + is net-zero when unchanged.
                var bump = await McpTools.SetRolePermission(claude, roleName, "ReadRefresh");
                Check("MCP set_role_permission elevates the role's model permission",
                    bump.Changed && (await McpTools.ListRoles(claude)).First(r => r.Name == roleName).ModelPermission == "ReadRefresh");
                var bumpNoop = await McpTools.SetRolePermission(claude, roleName, "ReadRefresh");
                Check("MCP set_role_permission is net-zero when the permission is unchanged", !bumpNoop.Changed);

                var setPerm = await McpTools.SetTablePermission(claude, roleName, "table:" + rlsTable, "1 = 1");
                Check("MCP set_table_permission sets an RLS row-filter + echoes the resulting permission (no promotion on a ReadRefresh role)",
                    setPerm.Changed && !setPerm.Promoted && setPerm.ModelPermission == "ReadRefresh");

                var firstAdd = await McpTools.SetRoleMember(claude, roleName, "rls-smoke@example.com", true);
                var addAgain = await McpTools.SetRoleMember(claude, roleName, "rls-smoke@example.com", true);
                Check("MCP set_role_member add is applied once and idempotent (second add is net-zero)",
                    firstAdd.Changed && !addAgain.Changed);
                var role = (await McpTools.ListRoles(claude)).FirstOrDefault(r => r.Name == roleName);
                Check("MCP list_roles reflects the role with its exact RLS filter + a single member",
                    role != null && role.TableFilters.Any(f => f.Table == rlsTable && f.FilterExpression == "1 = 1")
                    && role.Members.Count(mn => mn == "rls-smoke@example.com") == 1);

                var dropMember = await McpTools.SetRoleMember(claude, roleName, "rls-smoke@example.com", false);
                Check("MCP set_role_member remove drops the member",
                    dropMember.Changed && (await McpTools.ListRoles(claude)).First(r => r.Name == roleName).Members.Length == 0);
                var dropAgain = await McpTools.SetRoleMember(claude, roleName, "rls-smoke@example.com", false);
                Check("MCP set_role_member remove of an absent member is net-zero", !dropAgain.Changed);

                var clearPerm = await McpTools.SetTablePermission(claude, roleName, "table:" + rlsTable, "");
                Check("MCP set_table_permission with an empty filter clears it",
                    clearPerm.Changed && (await McpTools.ListRoles(claude)).First(r => r.Name == roleName).TableFilters.Length == 0);

                // A role created with NO permission defaults to None; adding a filter auto-promotes it None->Read (echoed + flagged).
                const string promoRole = "Smoke RLS Promote";
                var created2 = await McpTools.CreateRole(claude, promoRole, null);
                Check("MCP create_role with no permission defaults to None", created2.ModelPermission == "None");
                var promo = await McpTools.SetTablePermission(claude, promoRole, "table:" + rlsTable, "1 = 1");
                Check("MCP set_table_permission auto-promotes a None role to Read (Promoted flag + echoed permission + list)",
                    promo.Changed && promo.Promoted && promo.ModelPermission == "Read"
                    && (await McpTools.ListRoles(claude)).First(r => r.Name == promoRole).ModelPermission == "Read");
                await McpTools.DeleteRole(claude, promoRole);

                var delAbsent = await McpTools.DeleteRole(claude, "no-such-role-xyz");
                Check("MCP delete_role of an absent role is net-zero", !delAbsent.Changed);
                var delRole = await McpTools.DeleteRole(claude, roleName);
                Check("MCP delete_role removes the role (net-zero — model restored)",
                    delRole.Changed && (await McpTools.ListRoles(claude)).All(r => r.Name != roleName));

                // ---- Object-Level Security (OLS) guard: AdventureWorks is CL 1200, so both OLS tools must refuse with
                // a clear compatibility-level error (the happy path runs on the CL-1570 AllProperties model in AirSmoke).
                // This proves the OLS tools are wired on the MCP door and the CL gate fires.
                await McpTools.CreateRole(claude, "OLS Guard", "Read");
                bool tableOlsGated = false, colOlsGated = false;
                try { await McpTools.SetTableOls(claude, "OLS Guard", "table:" + rlsTable, "None"); }
                catch (Exception ex) when (ex.Message.Contains("compatibility level")) { tableOlsGated = true; }
                try { await McpTools.SetColumnOls(claude, "OLS Guard", "column:" + rlsTable + "/AnyColumn", "None"); }
                catch (Exception ex) when (ex.Message.Contains("compatibility level")) { colOlsGated = true; }
                Check("MCP set_table_ols + set_column_ols refuse on a CL<1400 model (clear compatibility-level error)",
                    tableOlsGated && colOlsGated);
                await McpTools.DeleteRole(claude, "OLS Guard");

                // ---- Object authoring (create-from-tree): create -> verify -> delete (net-zero), dual-drive.
                var g0 = await McpTools.GetModelGraph(claude);
                int authTables0 = g0.Tables.Length, authRels0 = g0.Relationships.Length;
                var authWait = ui.Notify.WaitNextAsync();
                var tRef = await McpTools.CreateTable(claude, "SmokeAuthTbl");
                Check("MCP create_table returns 'table:SmokeAuthTbl'", tRef == "table:SmokeAuthTbl");
                var aNote = await authWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("create_table broadcasts model/didChange to the UI, tagged origin=agent (dual-drive)", aNote != null && aNote.Origin == "agent");
                Check("MCP create_column adds a data column",
                    await McpTools.CreateColumn(claude, tRef, "Key", "Int64", null) == "column:SmokeAuthTbl/Key");
                Check("MCP create_calculated_column adds a DAX column",
                    await McpTools.CreateCalculatedColumn(claude, tRef, "Doubled", "[Key] * 2") == "column:SmokeAuthTbl/Doubled");
                var hRef = await McpTools.CreateHierarchy(claude, tRef, "Levels", new[] { "Key", "Doubled" });
                Check("MCP create_hierarchy returns a resolvable ref + delete_object removes it BY that ref (round-trip)",
                    hRef == "hierarchy:SmokeAuthTbl/Levels"
                    && (await McpTools.DeleteObject(claude, hRef)).Changed
                    && !(await McpTools.DeleteObject(claude, hRef)).Changed);     // gone -> 2nd delete is net-zero
                Check("MCP get_dependencies(direction: dependents) lists the calc column that references a column",
                    (await McpTools.GetDependencies(claude, "column:SmokeAuthTbl/Key", "dependents")).Any(d => d.Name == "Doubled"));
                Check("MCP duplicate_object clones a column",
                    await McpTools.DuplicateObject(claude, "column:SmokeAuthTbl/Doubled", "DoubledCopy") == "column:SmokeAuthTbl/DoubledCopy");
                bool cgGated = false;
                try { await McpTools.CreateCalculationGroup(claude, "Should Fail CG"); }
                catch (Exception ex) when (ex.Message.Contains("compatibility level")) { cgGated = true; }
                Check("MCP create_calculation_group refuses on a CL<1470 model", cgGated);

                var t2Ref = await McpTools.CreateTable(claude, "SmokeAuthTbl2");
                await McpTools.CreateColumn(claude, t2Ref, "Key", "Int64", null);

                // ---- duplicate_object EXTENDED (the tree copy/paste engine): a measure pastes onto ANOTHER table
                // (targetRef) with a model-wide-collision-safe name; a table duplicates at model scope with its
                // children; a cross-table column paste refuses with a teaching error.
                await McpTools.CreateMeasure(claude, tRef, "SmokeDupTotal", "1");
                Check("MCP duplicate_object clones a measure onto ANOTHER table (targetRef), bumping the model-unique name",
                    await McpTools.DuplicateObject(claude, "measure:SmokeAuthTbl/SmokeDupTotal", null, t2Ref) == "measure:SmokeAuthTbl2/SmokeDupTotal 2");
                Check("MCP duplicate_object keeps an explicit free name on the target table",
                    await McpTools.DuplicateObject(claude, "measure:SmokeAuthTbl/SmokeDupTotal", "SmokeDupTotal B", t2Ref) == "measure:SmokeAuthTbl2/SmokeDupTotal B");
                Check("MCP duplicate_object clones a TABLE at model scope (children included)",
                    await McpTools.DuplicateObject(claude, tRef) == "table:SmokeAuthTbl Copy");
                bool colCross = false;
                try { await McpTools.DuplicateObject(claude, "column:SmokeAuthTbl/Doubled", null, t2Ref); }
                catch (Exception ex) when (ex.Message.Contains("its own table")) { colCross = true; }
                Check("MCP duplicate_object refuses a cross-table COLUMN paste with a teaching error", colCross);
                await McpTools.DeleteObject(claude, "table:SmokeAuthTbl Copy");   // net-zero for the graph check below

                var relRef = await McpTools.CreateRelationship(claude, "column:SmokeAuthTbl/Key", "column:SmokeAuthTbl2/Key", "OneDirection", true);
                Check("MCP create_relationship adds a relationship visible in the graph",
                    !string.IsNullOrEmpty(relRef) && (await McpTools.GetModelGraph(claude)).Relationships.Length == authRels0 + 1);
                bool dupActive = false;
                try { await McpTools.CreateRelationship(claude, "column:SmokeAuthTbl/Key", "column:SmokeAuthTbl2/Key", null, true); }
                catch { dupActive = true; }
                Check("MCP create_relationship rejects a second ACTIVE relationship on the same column pair", dupActive);
                bool badCf = false;
                try { await McpTools.CreateRelationship(claude, "column:SmokeAuthTbl/Doubled", "column:SmokeAuthTbl2/Key", "Bogus", false); }
                catch { badCf = true; }
                Check("MCP create_relationship rejects an invalid crossFilter value", badCf);

                Check("MCP delete_object of an absent ref is net-zero", !(await McpTools.DeleteObject(claude, "table:NoSuchTbl_xyz")).Changed);
                await McpTools.DeleteObject(claude, "table:SmokeAuthTbl");
                await McpTools.DeleteObject(claude, "table:SmokeAuthTbl2");
                var gZ = await McpTools.GetModelGraph(claude);
                Check("MCP delete_object removes the authored tables + their relationship (net-zero — graph restored)",
                    gZ.Tables.Length == authTables0 && gZ.Relationships.Length == authRels0);

                // ---- Property grid: reflect TOM properties -> descriptors -> set -> read-back (net-zero).
                var modelProps = await McpTools.GetProperties(claude, "model:");
                Check("MCP get_properties reaches model-level settings through the singleton model ref",
                    modelProps.Any(p => p.Name == "Description" && p.Kind == "string")
                    && modelProps.Any(p => p.Name == "Culture" && p.Kind == "string")
                    && modelProps.Any(p => p.Name == "DiscourageImplicitMeasures" && p.Kind == "bool"));
                var originalModelDescription = modelProps.First(p => p.Name == "Description").Value;
                var setModel = await McpTools.SetProperty(claude, "model:", "Description", "MCP model-property smoke");
                Check("MCP set_property edits a model-level setting on the shared undo timeline",
                    setModel.Changed
                    && (await McpTools.GetProperties(claude, "model:")).First(p => p.Name == "Description").Value == "MCP model-property smoke");
                await McpTools.UndoChange(claude);
                Check("MCP undo restores the model-level setting (net-zero)",
                    (await McpTools.GetProperties(claude, "model:")).First(p => p.Name == "Description").Value == originalModelDescription);

                await McpTools.CreateTable(claude, "SmokePgTbl");
                await McpTools.CreateColumn(claude, "table:SmokePgTbl", "Amount", "Decimal", null);
                var props = await McpTools.GetProperties(claude, "column:SmokePgTbl/Amount");
                Check("MCP get_properties reflects editable descriptors (DataType enum + IsHidden bool + Description string) for a column",
                    props.Any(p => p.Name == "DataType" && p.Kind == "enum" && p.Options.Contains("Int64"))
                    && props.Any(p => p.Name == "IsHidden" && p.Kind == "bool")
                    && props.Any(p => p.Name == "Description" && p.Kind == "string"));
                var setBool = await McpTools.SetProperty(claude, "column:SmokePgTbl/Amount", "IsHidden", "true");
                Check("MCP set_property mutates a bool property + read-back confirms",
                    setBool.Changed && (await McpTools.GetProperties(claude, "column:SmokePgTbl/Amount")).First(p => p.Name == "IsHidden").Value == "True");
                Check("MCP set_property is net-zero when the value is unchanged",
                    !(await McpTools.SetProperty(claude, "column:SmokePgTbl/Amount", "IsHidden", "true")).Changed);
                var setEnum = await McpTools.SetProperty(claude, "column:SmokePgTbl/Amount", "DataType", "Int64");
                Check("MCP set_property sets an enum property by name",
                    setEnum.Changed && (await McpTools.GetProperties(claude, "column:SmokePgTbl/Amount")).First(p => p.Name == "DataType").Value == "Int64");

                // ---- set_properties (#140): ONE property across MANY objects = one atomic, all-or-nothing, undoable change.
                await McpTools.CreateMeasure(claude, "table:SmokePgTbl", "PgM1", "1");
                await McpTools.CreateMeasure(claude, "table:SmokePgTbl", "PgM2", "2");
                var setMany = await McpTools.SetProperties(claude, new[] { "measure:SmokePgTbl/PgM1", "measure:SmokePgTbl/PgM2" }, "IsHidden", "true");
                Check("MCP set_properties hides BOTH measures in one change + read-back confirms both",
                    setMany.Changed
                    && (await McpTools.GetProperties(claude, "measure:SmokePgTbl/PgM1")).First(p => p.Name == "IsHidden").Value == "True"
                    && (await McpTools.GetProperties(claude, "measure:SmokePgTbl/PgM2")).First(p => p.Name == "IsHidden").Value == "True");
                // ONE undo reverts the WHOLE batch (both measures) — proof it was a single undo entry, not two.
                await McpTools.UndoChange(claude);
                Check("MCP set_properties is ONE undo entry — a single undo un-hides BOTH measures",
                    (await McpTools.GetProperties(claude, "measure:SmokePgTbl/PgM1")).First(p => p.Name == "IsHidden").Value == "False"
                    && (await McpTools.GetProperties(claude, "measure:SmokePgTbl/PgM2")).First(p => p.Name == "IsHidden").Value == "False");
                // All-or-nothing: a bad ref in the batch aborts it, names the object, and changes nothing.
                var manyFailed = false;
                try { await McpTools.SetProperties(claude, new[] { "measure:SmokePgTbl/PgM1", "measure:SmokePgTbl/Nope" }, "IsHidden", "true"); }
                catch (Exception ex) { manyFailed = ex.Message.Contains("measure:SmokePgTbl/Nope") && ex.Message.Contains("all-or-nothing"); }
                Check("MCP set_properties is all-or-nothing — a bad ref aborts, names the object, leaves PgM1 unchanged",
                    manyFailed && (await McpTools.GetProperties(claude, "measure:SmokePgTbl/PgM1")).First(p => p.Name == "IsHidden").Value == "False");

                await McpTools.DeleteObject(claude, "table:SmokePgTbl");

                // ---- FIND & REPLACE (Phase 2): the MatchClass-routed safe replace, dual-drive, net-zero -----------
                // Build a throwaway table with a calc column + a measure that REFERENCES it, then prove: (1) a
                // name replace RENAMES + FormulaFixup rewrites the reference; (2) a literal replace on a DAX identifier
                // is HARD-BLOCKED; (3) a plain-text (description) replace works and is undoable. Cleaned up at the end.
                await McpTools.CreateTable(claude, "SmokeFR");
                await McpTools.CreateColumn(claude, "table:SmokeFR", "Key", "Int64", null);
                var frCol = await McpTools.CreateCalculatedColumn(claude, "table:SmokeFR", "FrCol", "[Key] * 1");
                var frMeas = await McpTools.CreateMeasure(claude, "table:SmokeFR", "FrMeasure", "SUM ( 'SmokeFR'[FrCol] ) // FrCol note");

                var frWait = ui.Notify.WaitNextAsync();
                var frRen = await McpTools.ReplaceInObject(claude, frCol, "name", "FrCol", "FrRenamed", false, false, false, -1, 0, null);
                Check("MCP replace_in_object(name): renames the column + reports the new ref + MatchClass=ObjectName",
                    frRen.Changed && frRen.MatchClass == "ObjectName" && frRen.Ref == "column:SmokeFR/FrRenamed");
                var frNote = await frWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("replace_in_object broadcasts model/didChange to the UI, tagged origin=agent (dual-drive)",
                    frNote != null && frNote.Origin == "agent");
                var frExpr = await McpTools.GetDax(claude, "measure:SmokeFR/FrMeasure");
                Check("replace_in_object(name): FormulaFixup rewrote the DAX reference to the new name (old ref gone; DAX-only, so the comment is left)",
                    frExpr.Contains("[FrRenamed]") && !frExpr.Contains("[FrCol]") && frExpr.Contains("FrCol note"));

                // A literal replace on a DAX IDENTIFIER (the reference to [FrRenamed]) is refused — the model is untouched.
                string frBlock = null;
                try { await McpTools.ReplaceInObject(claude, "measure:SmokeFR/FrMeasure", "expression", "FrRenamed", "Nope", false, false, false, -1, 0, null); }
                catch (Exception ex) { frBlock = ex.Message; }
                Check("MCP replace_in_object: a DAX-reference match is HARD-BLOCKED (steers to rename), model unchanged",
                    frBlock != null && frBlock.IndexOf("rename", StringComparison.OrdinalIgnoreCase) >= 0
                    && (await McpTools.GetDax(claude, "measure:SmokeFR/FrMeasure")).Contains("[FrRenamed]"));

                // A plain-text (description) replace works and is undoable on the shared timeline.
                await McpTools.SetDescription(claude, frMeas, "quick brown fox");
                var frDesc = await McpTools.ReplaceInObject(claude, frMeas, "description", "quick", "slow", false, false, false, -1, 0, null);
                Check("MCP replace_in_object(description): plain-text substitution (MatchClass=PlainText) with before/after",
                    frDesc.Changed && frDesc.MatchClass == "PlainText" && frDesc.After == "slow brown fox");
                await McpTools.UndoChange(claude);
                Check("MCP replace_in_object: the edit is undoable on the shared timeline (description restored)",
                    (await McpTools.GetProperties(claude, frMeas)).First(p => p.Name == "Description").Value == "quick brown fox");

                // ---- FIND & REPLACE (Phase 2 preview + Phase 3 bulk plan) -----------------------------------------
                // preview=true rehearses a replace (before/after computed, NOTHING mutates).
                var frPv = await McpTools.ReplaceInObject(claude, frMeas, "description", "brown", "golden", false, false, false, -1, 0, null, preview: true);
                Check("MCP replace_in_object(preview): rehearses before/after without mutating",
                    frPv.Preview && !frPv.Changed && frPv.After == "quick golden fox"
                    && (await McpTools.GetProperties(claude, frMeas)).First(p => p.Name == "Description").Value == "quick brown fox");

                // propose_replace over an expression whose only match is a DAX REFERENCE: no items, an honest note.
                var frPlanRef = await McpTools.ProposeReplace(claude, "FrRenamed", "Nope", false, false, false, null, new[] { "expression" }, "table:SmokeFR");
                Check("MCP propose_replace: a reference-only match yields NO plan items + the note steers to rename",
                    frPlanRef.Items.Length == 0 && (frPlanRef.Note ?? "").IndexOf("reference", StringComparison.OrdinalIgnoreCase) >= 0);

                // propose_replace over a description: one pre-approved set_description item; a single-item apply is free.
                var frPlan = await McpTools.ProposeReplace(claude, "brown", "golden", false, false, false, null, new[] { "description" }, "table:SmokeFR");
                var frItem = frPlan.Items.FirstOrDefault(i => i.Kind == "set_description" && i.ObjectRef == frMeas);
                Check("MCP propose_replace(description): builds one pre-approved set_description item with before/after",
                    frPlan.Items.Length == 1 && frItem != null && frItem.Status == "approved" && frItem.After == "quick golden fox");
                var frApply = await McpTools.ApplyPlan(claude, new[] { frItem.Id }, null, null);
                Check("MCP apply_plan applies the single replace item (free path) and the text landed",
                    frApply.AppliedCount == 1 && frApply.FailedCount == 0
                    && (await McpTools.GetProperties(claude, frMeas)).First(p => p.Name == "Description").Value == "quick golden fox");
                await McpTools.UndoChange(claude);
                Check("MCP propose_replace -> apply_plan: the applied replace is undoable on the shared timeline",
                    (await McpTools.GetProperties(claude, frMeas)).First(p => p.Name == "Description").Value == "quick brown fox");
                await McpTools.ClearPlan(claude);

                await McpTools.DeleteObject(claude, "table:SmokeFR");   // net-zero
                Check("replace leg cleaned up (throwaway table removed)",
                    (await McpTools.GetModelGraph(claude)).Tables.All(t => t.Name != "SmokeFR"));

                // ---- UPDATE SCHEMA (Tabular-Editor-style: re-read source columns, diff, apply the accepted subset) ----
                // Three dual-drive ops on the shared session. The live source-read needs a reachable SQL endpoint, so
                // here we prove (a) it degrades GRACEFULLY (Reachable=false, no throw) on a non-SQL source, and (b) the
                // diff + apply-subset run fully offline via a SUPPLIED source schema — the manual/offline path the UI
                // falls back to with no connection. (The M-source parser + diff logic get exhaustive xUnit coverage.)
                var ssTableRef = await McpTools.CreateTable(claude, "SchemaSmoke");
                await McpTools.CreateColumn(claude, ssTableRef, "OrderId", "Int64", null);
                await McpTools.CreateColumn(claude, ssTableRef, "Amount", "Decimal", null);
                await McpTools.CreateColumn(claude, ssTableRef, "Region", "String", null);
                await McpTools.CreateColumn(claude, ssTableRef, "LegacyCode", "String", null);
                await McpTools.CreateCalculatedColumn(claude, ssTableRef, "Margin", "1");

                var ssSrc = await McpTools.GetSourceSchema(claude, ssTableRef);
                Check("MCP get_source_schema on a non-SQL source degrades GRACEFULLY (Reachable=false + 'source unreachable', no throw)",
                    ssSrc != null && !ssSrc.Reachable && (ssSrc.Error ?? "").Contains("source unreachable") && ssSrc.Columns.Length == 0);

                var ssSupplied = new[]
                {
                    new SourceColumn { Name = "OrderId", DataType = "Int64" },
                    new SourceColumn { Name = "Amount", DataType = "Double" },     // type drift
                    new SourceColumn { Name = "Region", DataType = "String" },
                    new SourceColumn { Name = "CustomerId", DataType = "Int64" },  // added; LegacyCode dropped → removed
                };
                var ssDiff = await McpTools.DiffSchema(claude, ssTableRef, ssSupplied);
                Check("MCP diff_schema (supplied source) finds 1 added + 1 removed + 1 type-changed; the calc column is ignored",
                    ssDiff.Reachable && ssDiff.Source == "supplied" && ssDiff.Added == 1 && ssDiff.Removed == 1 && ssDiff.TypeChanged == 1
                    && ssDiff.Items.Any(i => i.Change == "Added" && i.Column == "CustomerId")
                    && ssDiff.Items.All(i => i.Column != "Margin"));

                var ssWait = ui.Notify.WaitNextAsync();
                var ssApply = await McpTools.ApplySchemaUpdate(claude, ssTableRef, new[]
                {
                    new SchemaUpdateItem { Change = "Added", Column = "CustomerId", DataType = "Int64" },
                    new SchemaUpdateItem { Change = "TypeChanged", Column = "Amount", DataType = "Double" },
                    new SchemaUpdateItem { Change = "Removed", Column = "Margin" },   // calc column → skipped, not applied
                });
                Check("MCP apply_schema_update applies ONLY the accepted subset (1 add + 1 retype) and skips the calc column",
                    ssApply.Changed && ssApply.Added == 1 && ssApply.Retyped == 1 && ssApply.Removed == 0
                    && ssApply.Skipped.Any(s => s.StartsWith("Margin:")));
                var ssN = await ssWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("apply_schema_update broadcasts model/didChange to the UI, tagged origin = \"agent\"",
                    ssN != null && ssN.Origin == "agent");

                var ssCols = (await McpTools.ListColumns(claude)).Where(c => c.Table == "SchemaSmoke").ToDictionary(c => c.Name);
                Check("the accepted subset landed: CustomerId added (Int64), Amount retyped to Double, LegacyCode untouched (not accepted)",
                    ssCols.ContainsKey("CustomerId") && ssCols["CustomerId"].DataType == "Int64"
                    && ssCols["Amount"].DataType == "Double" && ssCols.ContainsKey("LegacyCode"));

                await McpTools.UndoChange(claude);
                var ssAfter = (await McpTools.ListColumns(claude)).Where(c => c.Table == "SchemaSmoke").ToDictionary(c => c.Name);
                Check("apply_schema_update is ONE undoable step on the shared timeline (undo reverts the whole batch)",
                    !ssAfter.ContainsKey("CustomerId") && ssAfter["Amount"].DataType == "Decimal");
                await McpTools.DeleteObject(claude, ssTableRef);   // net-zero cleanup

                // Cheap structural overview vs the full graph.
                var mg = await McpTools.GetModelGraph(claude);
                var mgSum = await McpTools.ModelGraphSummary(claude);
                Check("MCP model_graph_summary: counts match the full graph, no per-table detail dumped",
                    mgSum != null && mgSum.TableCount == mg.Tables.Length
                    && mgSum.RelationshipCount == mg.Relationships.Length && mgSum.DisconnectedTables != null);

                // get_model_summary: one-call orientation bundling overview + readiness + graph (P-Efficiency #3).
                var ms = await McpTools.GetModelSummary(claude);
                Check("MCP get_model_summary bundles overview + readiness + graph consistently in one call",
                    ms != null && ms.Overview != null && ms.Readiness != null && ms.Graph != null
                    && ms.Graph.TableCount == mg.Tables.Length
                    && ms.Readiness.TotalFindings == full.Findings.Length);

                // Live-source binding (deploy-to-source round-trip): a FILE-opened session is NOT live-bound, so
                // deploy_live with no endpoint must fail loudly — never silently no-op or guess a target.
                Check("MCP get_model_summary: a file-opened session reports LiveBound=false (no live endpoint)",
                    ms.Overview.LiveBound == false && ms.Overview.LiveEndpoint == null && ms.Overview.LiveDatabase == null);
                // Unified-open identity: a FILE open has no live QUERY connection either (distinct from LiveBound).
                // Also guards that SessionInfoAsync snapshots a null _live without NRE.
                Check("MCP model_overview: a file-opened session reports LiveConnected=false (no live query engine)",
                    ms.Overview.LiveConnected == false && ms.Overview.LiveKind == null && ms.Overview.LiveDataSource == null);
                var deployThrew = false; string deployErr = null;
                try { await McpTools.DeployLive(claude); }
                catch (Exception ex) { deployThrew = true; deployErr = ex.Message; }
                Check("MCP deploy_live with no endpoint on a non-live-bound session throws a clear error (no silent no-op)",
                    deployThrew && deployErr != null && deployErr.IndexOf("not live-bound", StringComparison.OrdinalIgnoreCase) >= 0);

                // An explicit endpoint with no dataset must fail clearly (a WRITE never guesses a "first dataset").
                // The guard runs before any auth/network, so it's offline-testable against a dummy endpoint.
                var dbThrew = false; string dbErr = null;
                try { await McpTools.DeployLive(claude, endpoint: "powerbi://api.powerbi.com/v1.0/myorg/Nope"); }
                catch (Exception ex) { dbThrew = true; dbErr = ex.Message; }
                Check("MCP deploy_live with an explicit endpoint but no database throws a clear 'database required' error",
                    dbThrew && dbErr != null
                    && dbErr.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0
                    && dbErr.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0);

                // Save-to-live auth routing (security-sensitive): a LOCAL loopback endpoint deploys with integrated
                // auth (no token); a CLOUD endpoint must NEVER be misread as local (which would skip the write token).
                Check("deploy auth: loopback endpoints classify as LOCAL (integrated, no token)",
                    Semanticus.Engine.LiveDeploy.IsLocalEndpoint("localhost:51234")
                    && Semanticus.Engine.LiveDeploy.IsLocalEndpoint("127.0.0.1:55001")
                    && Semanticus.Engine.LiveDeploy.IsLocalEndpoint("::1")
                    && Semanticus.Engine.LiveDeploy.IsLocalEndpoint("[::1]:51234"));
                Check("deploy auth: cloud/scheme + loopback-spoof endpoints are NEVER local (token required for the write)",
                    !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("powerbi://api.powerbi.com/v1.0/myorg/WS")
                    && !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("asazure://westus.asazure.windows.net/srv")
                    && !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("https://example/xmla")
                    && !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("localhost.evil.com")
                    && !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("127.0.0.1.evil.com")
                    && !Semanticus.Engine.LiveDeploy.IsLocalEndpoint(""));

                // ---- CLEAR CACHE (the cold/warm-benchmark primitive; non-destructive; gated on shared endpoints) ----
                var ccNone = await McpTools.ClearCache(claude, false);
                Check("clear_cache: 'Not connected' when no live connection (Cleared=false)",
                    ccNone != null && !ccNone.Cleared && (ccNone.Error ?? "").IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0);
                // The XMLA ClearCache command embeds the (XML-escaped) database id and round-trips as well-formed XML.
                var ccCmd = Semanticus.Engine.DaxCache.ClearCacheCommand("Sales DB <id>&1");
                Check("clear_cache: XMLA command embeds the escaped <DatabaseID> + is parseable",
                    ccCmd.Contains("ClearCache")
                    && System.Xml.Linq.XDocument.Parse(ccCmd).Descendants().Any(e => e.Name.LocalName == "DatabaseID" && e.Value == "Sales DB <id>&1"));
                // Gate basis (security-sensitive): a cloud endpoint is never local → the engine refuses clear without confirm.
                Check("clear_cache gate: cloud endpoint is not local (→ refused unless confirm=true)",
                    !Semanticus.Engine.LiveDeploy.IsLocalEndpoint("powerbi://api.powerbi.com/v1.0/myorg/WS"));

                // Cold/warm benchmark: reachable through the door; 'Not connected' without a live engine (no throw).
                var cw = await McpTools.BenchmarkDaxColdWarm(claude, "EVALUATE ROW(\"x\", 1)", 3, true, false);
                Check("benchmark_dax_coldwarm: 'Not connected' when no live connection (no throw)",
                    cw != null && (cw.Error ?? "").IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0);
                var qp = await McpTools.CaptureQueryPlan(claude, "EVALUATE ROW(\"x\", 1)");
                Check("capture_query_plan: 'Not connected' when no live connection (no throw)",
                    qp != null && (qp.Error ?? "").IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0);

                // ---- INCREMENTAL REFRESH POLICY (metadata only; both doors; never executes a refresh) ----------
                // (a) All three MCP tools are reachable through the RPC door. AdventureWorks (CL 1200) has no policy.
                var irRef = "table:" + colRows[0].Table;
                var irGet0 = await McpTools.GetIncrementalRefreshPolicy(claude, irRef);
                Check("MCP get_incremental_refresh_policy: a table with no policy reports enabled=false", !irGet0.Enabled);
                var irRem0 = await McpTools.RemoveIncrementalRefreshPolicy(claude, irRef);
                Check("MCP remove_incremental_refresh_policy: removing a non-existent policy is a no-op (Changed=false)", !irRem0.Changed);
                // The set tool refuses up front: AdventureWorks is CL 1200, below the 1450 incremental-refresh floor.
                var irGateBlocked = false; string irGateMsg = null;
                try { await McpTools.SetIncrementalRefreshPolicy(claude, irRef, "", null, null, null, null, null, null, null, true); }
                catch (Exception ex) { irGateBlocked = true; irGateMsg = ex.Message; }
                Check("MCP set_incremental_refresh_policy: refused (compatibility-level gate) — proves the tool is wired through RPC",
                    irGateBlocked && irGateMsg != null && irGateMsg.Contains("compatibility level"));

                // (b) Full round-trip on purpose-built fixtures (no shipped model has the RangeStart/RangeEnd prereqs).
                // Local helpers: load a fixture into a fresh in-process engine; assert a call throws with a phrase.
                async Task WithFixture(int cl, bool? realParams, Func<LocalEngine, Task> body)
                {
                    var path = Path.Combine(Path.GetTempPath(), "semanticus-ir-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
                    File.WriteAllText(path, IncrementalFixtureBim(cl, realParams));
                    var fxSessions = new SessionManager();
                    try { var fx = new LocalEngine(fxSessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro()); await fx.OpenAsync(path); await body(fx); }
                    finally { fxSessions.Dispose(); try { File.Delete(path); } catch { } }
                }
                async Task ExpectThrows(string label, Func<Task> act, string expect)
                {
                    var ok = false;
                    try { await act(); } catch (Exception ex) { ok = ex.Message != null && ex.Message.Contains(expect); }
                    Check(label, ok);
                }

                await WithFixture(1570, true, async fx =>
                {
                    var p0 = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: fixture starts with no policy on Sales", !p0.Enabled);

                    var set1 = await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, null, null, false, "agent");
                    Check("incremental: set with defaults reports Changed", set1.Changed);

                    var p1 = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: get round-trips the default policy (store 5 Year, refresh 10 Day, offset 0, Import, enabled)",
                        p1.Enabled && p1.RollingWindowPeriods == 5 && p1.RollingWindowGranularity == "Year"
                        && p1.IncrementalPeriods == 10 && p1.IncrementalGranularity == "Day" && p1.IncrementalPeriodsOffset == 0 && p1.Mode == "Import");

                    var set2 = await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, null, null, false, "agent");
                    Check("incremental: re-applying identical settings is a no-op (Changed=false, compare-before-assign)", !set2.Changed);

                    var custom = await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", 3, "Month", 14, "Day", 2, null, null, false, "agent");
                    var p2 = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: a custom set updates the full window (store 3 Month, refresh 14 Day, offset 2)",
                        custom.Changed && p2.RollingWindowPeriods == 3 && p2.RollingWindowGranularity == "Month"
                        && p2.IncrementalPeriods == 14 && p2.IncrementalGranularity == "Day" && p2.IncrementalPeriodsOffset == 2);

                    // Partial update: change ONLY mode→Hybrid; the custom window above must be PRESERVED (null=leave-unchanged).
                    var hyb = await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, "Hybrid", null, false, "agent");
                    var p3 = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: a partial update sets Mode=Hybrid AND preserves the existing window (no silent reset)",
                        hyb.Changed && p3.Mode == "Hybrid" && p3.RollingWindowPeriods == 3 && p3.RollingWindowGranularity == "Month"
                        && p3.IncrementalPeriods == 14 && p3.IncrementalPeriodsOffset == 2);

                    // PollingExpression (Detect Data Changes): set it, read it back, then prove null=preserve, ""=clear.
                    var poll = await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, null, "let x = 1 in x", false, "agent");
                    var pPoll = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: polling expression (Detect Data Changes) is set + read back",
                        poll.Changed && (pPoll.PollingExpression ?? "").Contains("let x = 1 in x"));
                    await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", 4, null, null, null, null, null, null, false, "agent");   // change a window knob; null polling
                    var pKeep = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: a null pollingExpression PRESERVES the existing one (and the knob changed)",
                        pKeep.RollingWindowPeriods == 4 && (pKeep.PollingExpression ?? "").Contains("let x = 1 in x"));
                    await fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, null, "", false, "agent");   // empty clears
                    var pClr = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: an empty pollingExpression CLEARS it", string.IsNullOrEmpty(pClr.PollingExpression));

                    var rem = await fx.RemoveIncrementalRefreshPolicyAsync("table:Sales", "agent");
                    var p4 = await fx.GetIncrementalRefreshPolicyAsync("table:Sales");
                    Check("incremental: remove clears the policy (enabled=false again)", rem.Changed && !p4.Enabled);

                    // Prerequisite gate (no filter at all): 'Raw' never references the parameters.
                    await ExpectThrows("incremental: refused when no partition filters on RangeStart/RangeEnd (Raw)",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Raw", "Id", null, null, null, null, null, null, null, false, "agent"), "filters on RangeStart");
                    // Soundness gate: 'Sneaky' mentions RangeStart/RangeEnd ONLY in a comment — not a real filter.
                    await ExpectThrows("incremental: a comment-only mention of RangeStart/RangeEnd does NOT satisfy the filter gate (Sneaky)",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sneaky", "Id", null, null, null, null, null, null, null, false, "agent"), "filters on RangeStart");
                    // A non-existent date column is rejected with a clear message.
                    await ExpectThrows("incremental: a non-existent date column is rejected",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sales", "NopeCol", null, null, null, null, null, null, null, false, "agent"), "not found");
                    // Enum inputs are validated (incl. numeric strings that Enum.TryParse would otherwise accept).
                    await ExpectThrows("incremental: an unknown granularity name is rejected",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", 5, "Decade", null, null, null, null, null, false, "agent"), "Granularity must be");
                    await ExpectThrows("incremental: an out-of-range numeric granularity is rejected (Enum.IsDefined guard)",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", 5, "99", null, null, null, null, null, false, "agent"), "Granularity must be");
                    await ExpectThrows("incremental: an unknown mode is rejected",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, "Sideways", null, false, "agent"), "Import");
                });

                // Hybrid is refused below CL 1565 (the fixture's params/filter are valid, so this is purely the mode gate).
                await WithFixture(1500, true, async fx =>
                    await ExpectThrows("incremental: Hybrid mode is refused below compatibility level 1565",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Sales", "Date", null, null, null, null, null, "Hybrid", null, false, "agent"), "1565"));

                // Manual mode still refuses incomplete prerequisites. autoWire upgrades same-name ordinary
                // expressions and appends the missing filter in one undoable model mutation.
                await WithFixture(1570, false, async fx =>
                {
                    await ExpectThrows("incremental: refused when RangeStart/RangeEnd exist but are not parameter queries",
                        () => fx.SetIncrementalRefreshPolicyAsync("table:Raw", "Date", null, null, null, null, null, null, null, false, "agent"), "parameter quer");
                    var upgraded = await fx.SetIncrementalRefreshPolicyAsync("table:Raw", "Date", null, null, null, null, null, null, null, true, "agent");
                    var expressions = await fx.ListNamedExpressionsAsync();
                    var rawM = await fx.GetPartitionMAsync("partition:Raw/Raw");
                    Check("incremental autoWire: upgrades same-name ordinary expressions into date/time parameters",
                        upgraded.Changed && expressions.Where(x => x.Name == "RangeStart" || x.Name == "RangeEnd").All(x => x.Expression.Contains("IsParameterQuery=true")));
                    Check("incremental autoWire: appends the half-open filter without replacing the existing let source",
                        rawM.Contains("Source = #table") && rawM.Contains("[Date] >= RangeStart") && rawM.Contains("[Date] < RangeEnd"));
                    await fx.UndoAsync("human");
                    expressions = await fx.ListNamedExpressionsAsync();
                    rawM = await fx.GetPartitionMAsync("partition:Raw/Raw");
                    var undonePolicy = await fx.GetIncrementalRefreshPolicyAsync("table:Raw");
                    Check("incremental autoWire: one undo restores both ordinary expressions, the original M, and the disabled policy",
                        expressions.Where(x => x.Name == "RangeStart" || x.Name == "RangeEnd").All(x => !x.Expression.Contains("IsParameterQuery=true"))
                        && !rawM.Contains("RangeStart") && !rawM.Contains("RangeEnd") && !undonePolicy.Enabled);
                });

                // Existing valid parameter values belong to the analyst. autoWire fills missing structure without
                // resetting a deliberately chosen development window.
                await WithFixture(1570, true, async fx =>
                {
                    var before = await fx.ListNamedExpressionsAsync();
                    await fx.SetIncrementalRefreshPolicyAsync("table:Raw", "Date", null, null, null, null, null, null, null, true, "agent");
                    var after = await fx.ListNamedExpressionsAsync();
                    Check("incremental autoWire: preserves existing valid RangeStart/RangeEnd parameter values",
                        before.Single(x => x.Name == "RangeStart").Expression == after.Single(x => x.Name == "RangeStart").Expression
                        && before.Single(x => x.Name == "RangeEnd").Expression == after.Single(x => x.Name == "RangeEnd").Expression);
                });

                // Missing parameters are created, bare M is wrapped explicitly, and a second identical call is a no-op.
                await WithFixture(1570, null, async fx =>
                {
                    var wired = await fx.SetIncrementalRefreshPolicyAsync("table:Bare", "Date", null, null, null, null, null, null, null, true, "agent");
                    var bareM = await fx.GetPartitionMAsync("partition:Bare/Bare");
                    var expressions = await fx.ListNamedExpressionsAsync();
                    Check("incremental autoWire: creates missing RangeStart/RangeEnd parameters", wired.Changed
                        && expressions.Count(x => (x.Name == "RangeStart" || x.Name == "RangeEnd") && x.Expression.Contains("IsParameterQuery=true")) == 2);
                    Check("incremental autoWire: wraps a bare source and preserves it before appending the filter",
                        bareM.StartsWith("let") && bareM.Contains("Source = #table") && bareM.Contains("Table.SelectRows(Source") && bareM.Contains("[Date] < RangeEnd"));
                    var again = await fx.SetIncrementalRefreshPolicyAsync("table:Bare", "Date", null, null, null, null, null, null, null, true, "agent");
                    Check("incremental autoWire: identical re-apply is idempotent", !again.Changed);
                });

                // ---- PARTITION REFRESH (process) — dry-run by default; commit is a LIVE data write -----------------
                // Everything here is headless (catalog + dry-run plan + guards). The actual live execution (commit=true,
                // RequestRefresh + SaveChanges against a server) is NOT headless-verifiable — it's Kane's supervised run.
                var rtypes = await McpTools.ListRefreshTypes(claude);
                Check("MCP list_refresh_types: catalog has Full (recommended, partition-level) + the table-level Defragment, all explained",
                    rtypes.Length >= 6 && rtypes.All(t => !string.IsNullOrEmpty(t.Explanation))
                    && rtypes.Any(t => t.Name == "Full" && t.Recommended && t.PartitionLevel)
                    && rtypes.Any(t => t.Name == "Defragment" && !t.PartitionLevel));

                // Find a real partition ref in AdventureWorks (a file model).
                string partRef = null;
                foreach (var t in await McpTools.ListObjects(claude, null))
                {
                    foreach (var kid in await McpTools.ListObjects(claude, t.Ref))
                        if (kid.Ref != null && kid.Ref.StartsWith("partition:")) { partRef = kid.Ref; break; }
                    if (partRef != null) break;
                }
                Check("MCP found a partition to target", partRef != null);

                async Task<string> RefreshThrows(Func<Task> act) { try { await act(); return null; } catch (Exception ex) { return ex.Message; } }

                // File model + no endpoint → refused (nothing to refresh).
                var m1 = await RefreshThrows(() => McpTools.RefreshPartition(claude, partRef, "Full", null, null, "serviceprincipal", null, false));
                Check("MCP refresh_partition: refused on a file model with no endpoint (not connected to a live model)",
                    m1 != null && m1.Contains("not connected to a live model"));
                // Unknown type → rejected.
                var m2 = await RefreshThrows(() => McpTools.RefreshPartition(claude, partRef, "Nonsense", "powerbi://fake", "DB", "serviceprincipal", null, false));
                Check("MCP refresh_partition: an unknown refresh type is rejected", m2 != null && m2.Contains("Unknown refresh type"));
                // Table-level type at the partition level → rejected.
                var m3 = await RefreshThrows(() => McpTools.RefreshPartition(claude, partRef, "Defragment", "powerbi://fake", "DB", "serviceprincipal", null, false));
                Check("MCP refresh_partition: a table-level type (Defragment) is rejected for a single partition", m3 != null && m3.Contains("table-level"));
                // Bad partition ref → rejected.
                var m4 = await RefreshThrows(() => McpTools.RefreshPartition(claude, "partition:Nope/Nope", "Full", "powerbi://fake", "DB", "serviceprincipal", null, false));
                Check("MCP refresh_partition: a non-existent partition ref is rejected", m4 != null && m4.Contains("is not a partition"));

                // DRY RUN with an explicit (fake) endpoint → returns the plan, Committed=false, and NEVER connects
                // (a fake endpoint would fail if it tried). Proves dry-run is pure-local + commit-gated.
                var dry = await McpTools.RefreshPartition(claude, partRef, "Full", "powerbi://fake-endpoint", "FakeDb", "serviceprincipal", null, false);
                Check("MCP refresh_partition DRY RUN: returns the plan (type + explanation) without connecting or committing",
                    dry != null && !dry.Committed && dry.RefreshType == "Full" && !string.IsNullOrEmpty(dry.Explanation)
                    && dry.Partition == partRef && dry.Endpoint == "powerbi://fake-endpoint" && string.IsNullOrEmpty(dry.Error));

                // COMMIT against an unreachable local endpoint (port 1 → connection refused fast, no server, no token):
                // exercises the real execute path and proves a failure is REPORTED (Committed=false + Error), not thrown.
                var commitFail = await McpTools.RefreshPartition(claude, partRef, "Full", "localhost:1", "NoDb", "serviceprincipal", null, true);
                Check("MCP refresh_partition COMMIT failure is reported via Error (Committed=false), not thrown across the door",
                    commitFail != null && !commitFail.Committed && !string.IsNullOrEmpty(commitFail.Error) && commitFail.Error.Contains("nothing was committed"));

                // ---- token-mode expiry: a supplied raw token's REAL JWT 'exp' is honoured (so the session's
                // reuse/skew logic can't be fooled into reusing a short-lived token). Non-JWT input falls back to a
                // short window (forcing re-supply, not a fabricated ~1h validity). Proves the live-auth-reuse fix.
                string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var realExp = DateTimeOffset.UtcNow.AddMinutes(42).ToUnixTimeSeconds();
                var jwt = B64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64Url($"{{\"aud\":\"x\",\"exp\":{realExp}}}") + ".sig";
                var jwtTok = await EntraToken.AcquireFullAsync("token", jwt, default);
                Check("token-mode auth reads the token's REAL JWT exp (not a fabricated 1h)",
                    Math.Abs((jwtTok.ExpiresOn - DateTimeOffset.FromUnixTimeSeconds(realExp)).TotalSeconds) < 2);
                var rawTok = await EntraToken.AcquireFullAsync("token", "not-a-jwt", default);
                var fallbackMins = (rawTok.ExpiresOn - DateTimeOffset.UtcNow).TotalMinutes;
                Check("token-mode auth falls back to a SHORT window for a non-JWT (forces re-supply, no fake 1h)",
                    fallbackMins > 1 && fallbackMins < 10);

                // ---- Payload lens (P-Efficiency #5): the cheap summaries vs the full reads, in serialized bytes.
                // Reuses results already fetched above (no extra round-trips) and asserts the token-efficiency property.
                int Bytes(object o) => System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(o));
                int sumAir = Bytes(summary), fullAir = Bytes(full);
                int sumBpa = Bytes(bpaSum), fullBpa = Bytes(bpaFull);
                int sumG = Bytes(mgSum), fullG = Bytes(mg);
                Console.WriteLine($"[lens] payload bytes (summary/full): readiness {sumAir}/{fullAir}  bpa {sumBpa}/{fullBpa}  graph {sumG}/{fullG}");
                Check("token lens: ai_readiness_summary is materially smaller than the full ai_readiness_scan", sumAir < fullAir);
                Check("token lens: model_graph_summary is smaller than the full get_model_graph", sumG < fullG);

                // ---- PRO-MODE WORKFLOW ENGINE (docs/pro-mode-spec.md §7 — the dual-drive workflow proof) ----------
                // The owner has AdventureWorks open (a saved model), so the STOCK library beside the McpSmoke binary
                // is what list_workflows sees; the harness runs DevPro so gated starts succeed. Drive McpTools through
                // the RemoteEngine proxy exactly like every section above — this exercises the tool bodies + proxy +
                // cross-process dual-drive against one live session.
                var wfList = await McpTools.ListWorkflows(claude);
                Check("MCP list_workflows returns the 15 stock workflows, all parsed clean (Error==null) and Gated (Pro to start)",
                    wfList.Length == 15 && wfList.All(w => w.Error == null) && wfList.All(w => w.Gated));

                var wfStart = await McpTools.StartWorkflow(claude, "new-measure");
                var wfRunId = wfStart.RunId;
                Check("MCP start_workflow('new-measure') returns a run on step-1 with VERBATIM instructions + 3 gate questions",
                    wfStart.Status == "active" && wfStart.CurrentStep != null && wfStart.CurrentStep.StepId == "step-1"
                    && !string.IsNullOrWhiteSpace(wfStart.CurrentStep.Instructions)
                    && wfStart.CurrentStep.Questions.Length == 3);

                // Submitting step-1 with NO answers is REJECTED — the rejection names each unanswered question verbatim
                // (the error text IS the steering mechanism). The measure's `verificationValue` question is quoted back.
                string wfReject = null;
                try { await McpTools.SubmitWorkflowStep(claude, wfRunId, "step-1", "{}"); }
                catch (Exception ex) { wfReject = ex.Message; }
                Check("MCP submit_workflow_step(step-1, {}) THROWS and names the question verbatim ('A known-good number')",
                    wfReject != null && wfReject.Contains("A known-good number"));

                // Submit step-1 with an explicit DECLINE for verificationValue (+ the two answer-or-decline text answers).
                // The decline is recorded on the step result — auditable, never silently dropped — and the run advances.
                var wfStep1 = await McpTools.SubmitWorkflowStep(claude, wfRunId, "step-1",
                    "{\"verificationValue\": {\"declined\": true, \"reason\": \"smoke has no reference figure\"}, \"intendedFilterContext\": \"none\", \"expectedGrain\": \"n/a\"}");
                Check("MCP submit_workflow_step(step-1, decline) records the decline (Declined + reason) and advances to step-2",
                    wfStep1.Steps[0].Answers.TryGetValue("verificationValue", out var wfDecline)
                    && wfDecline.Declined && wfDecline.DeclineReason == "smoke has no reference figure"
                    && wfStep1.CurrentStep != null && wfStep1.CurrentStep.StepId == "step-2");

                // step-2 is gateless → null answers advance to step-3.
                var wfStep2 = await McpTools.SubmitWorkflowStep(claude, wfRunId, "step-2", null);
                Check("MCP submit_workflow_step(step-2, gateless) advances to step-3",
                    wfStep2.CurrentStep != null && wfStep2.CurrentStep.StepId == "step-3");

                // step-3 (hard gate): pass an EXISTING measure as `target`. Expected honesty: dax_probe is SKIPPED (its
                // when-input verificationValue was declined) and bpa_clean scope:object PASSES (the measure pre-existed,
                // so its violations were snapshotted at start → none are NEW) → the run COMPLETES. Register the UI's
                // workflow/didChange waiter BEFORE the completing submit so the dual-drive broadcast is observed live.
                // The waiter names the event it wants (this run, completed) so a still-in-flight step-2 "active"
                // broadcast on the ui pipe can never consume it (there is no cross-pipe ordering guarantee).
                var wfWait = ui.Notify.WaitWorkflowAsync(v => v.RunId == wfRunId && v.Status == "completed");
                var wfStep3Answers = System.Text.Json.JsonSerializer.Serialize(
                    new System.Collections.Generic.Dictionary<string, string> { ["target"] = measureRef });
                var wfDone = await McpTools.SubmitWorkflowStep(claude, wfRunId, "step-3", wfStep3Answers);
                Check("MCP submit_workflow_step(step-3, existing measure) COMPLETES the run", wfDone.Status == "completed");
                var wfS3 = wfDone.Steps[2];
                Check("workflow verify honesty: step-3 dax_probe is SKIPPED (when-input declined) and bpa_clean PASSED (no NEW violations on the pre-existing measure)",
                    wfS3.VerifyResults.Any(v => v.Kind == "dax_probe" && v.Status == "skipped")
                    && wfS3.VerifyResults.Any(v => v.Kind == "bpa_clean" && v.Status == "passed"));

                // DUAL-DRIVE: the UI door saw the SAME completed run live over workflow/didChange (golden rule #2).
                var wfNote = await wfWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("the workflow run broadcasts workflow/didChange to the UI (dual-drive: same RunId, completed)",
                    wfNote != null && wfNote.RunId == wfRunId && wfNote.Status == "completed");

                // The terminal state round-trips over the proxy (get_workflow_run through the RemoteEngine).
                var wfRead = await McpTools.GetWorkflowRun(claude, wfRunId);
                Check("MCP get_workflow_run round-trips the terminal run state over the RPC proxy",
                    wfRead != null && wfRead.RunId == wfRunId && wfRead.Status == "completed");

                var wfEvidence = await McpTools.ExportWorkflowEvidence(claude, wfRunId);
                Check("MCP export_workflow_evidence returns the sealed current-model HTML+JSON artifact",
                    wfEvidence != null && wfEvidence.Json?.Contains("\"kind\":\"workflow-run\"") == true
                    && wfEvidence.Html?.Contains(wfEvidence.ContentHash) == true
                    && Semanticus.Engine.Evidence.EvidenceHash.HashOfJsonText(wfEvidence.Json) == wfEvidence.ContentHash);

                var savedTestEvidence = await McpToolsEvidence.SaveEvidence(claude, "tests");
                var savedWorkflowEvidence = await McpToolsEvidence.SaveEvidence(claude, "workflow", wfRunId);
                var evidenceLibrary = await McpToolsEvidence.ListEvidence(claude);
                Check("MCP save_evidence stores Test + Workflow artifacts in one verified model-scoped library",
                    savedTestEvidence.Saved && savedWorkflowEvidence.Saved
                    && evidenceLibrary.Items.Any(x => x.Valid && x.Kind == "test-suite")
                    && evidenceLibrary.Items.Any(x => x.Valid && x.Kind == "workflow-run" && x.Id == wfRunId));
                var savedWorkflowRead = await McpToolsEvidence.GetEvidence(claude, wfRunId);
                Check("MCP get_evidence re-verifies and returns the saved workflow artifact through the shared door",
                    savedWorkflowRead?.Json?.Contains("\"kind\":\"workflow-run\"") == true
                    && savedWorkflowRead.Html?.Contains(savedWorkflowRead.ContentHash) == true);

                // ---- WORKFLOW TEMPLATES (docs/pro-mode-spec.md §10-T1 — the customisation layer, dual-drive) --------
                // The template shelf lives in its OWN dir beside the binary, so it never leaks into the 15-workflow
                // library (asserted above at ==15). Drive the 5 template ops through the RemoteEngine proxy — the same
                // cross-process dual-drive path every section above uses. We instantiate ONE template into the model's
                // sidecar, prove the invariant refuses an injection, then delete it so the shared dir returns to 15.
                var tmplNames = new[] { "metric-certification", "month-end-close", "deploy-freeze-guard", "hard-measure" };
                var tmplList = await McpTools.ListWorkflowTemplates(claude);
                Check("MCP list_workflow_templates returns the 4 stock templates (parsed clean, with slots) and NONE leak into list_workflows",
                    tmplNames.All(n => tmplList.Any(t => t.Name == n && t.Error == null && t.SlotCount > 0))
                    && wfList.All(w => !tmplNames.Contains(w.Name)));

                var tmplGet = await McpTools.GetWorkflowTemplate(claude, "metric-certification");
                Check("MCP get_workflow_template returns slot declarations (each with an example) + the raw markdown body",
                    tmplGet != null && tmplGet.Slots.Length >= 4 && !string.IsNullOrWhiteSpace(tmplGet.Markdown)
                    && tmplGet.Slots.Any(s => s.Name == "surface_name" && !string.IsNullOrWhiteSpace(s.Example)));

                var goodVals = System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["surface_name"] = "the FY26 Exec Dashboard",
                    ["kpi_dictionary"] = "measure:Sales/Total — net sales — owner: CFO — from the board pack",
                    ["escalation_rule"] = "stop and raise with the owner — never adjust finance figures to match the model",
                    ["certification_tag"] = "Certified FY26-Q1",
                });
                var wfCountBefore = (await McpTools.ListWorkflows(claude)).Length;   // 15 — templates don't count
                try
                {
                    var instLib = await McpTools.InstantiateWorkflowTemplate(claude, "metric-certification", "smoke-metric-cert", goodVals);
                    var afterInst = await McpTools.ListWorkflows(claude);
                    Check("MCP instantiate_workflow_template renders a runnable workflow (#33) from the template, admission-clean",
                        wfCountBefore == 15 && afterInst.Length == wfCountBefore + 1
                        && afterInst.Any(w => w.Name == "smoke-metric-cert" && w.Error == null)
                        && instLib.Any(w => w.Name == "smoke-metric-cert"));

                    var instDef = await McpTools.GetWorkflow(claude, "smoke-metric-cert");
                    Check("the instance carries re-instantiable provenance (template + recorded slot_values)",
                        instDef.Provenance != null && instDef.Provenance.TryGetValue("template", out var pv) && pv == "metric-certification"
                        && instDef.Provenance.TryGetValue("slot_values", out var sv) && sv.Contains("FY26 Exec Dashboard"));

                    // THE STRUCTURE-PRESERVING INVARIANT: a slot value smuggling a new step is REFUSED (plain diff, nothing written).
                    var evilVals = System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["surface_name"] = "S", ["kpi_dictionary"] = "x\n\n## Step 9: Injected\nmalice",
                        ["escalation_rule"] = "r", ["certification_tag"] = "t",
                    });
                    string injMsg = null;
                    try { await McpTools.InstantiateWorkflowTemplate(claude, "metric-certification", "smoke-injection", evilVals); }
                    catch (Exception ex) { injMsg = ex.Message; }
                    var afterInj = await McpTools.ListWorkflows(claude);
                    Check("structure-preserving invariant REFUSES a step-injecting slot value (plain-language, nothing written)",
                        injMsg != null && injMsg.ToLowerInvariant().Contains("refused")
                        && !afterInj.Any(w => w.Name == "smoke-injection") && afterInj.Length == wfCountBefore + 1);
                }
                finally
                {
                    // Return the shared AdventureWorks sidecar to 15 so a re-run sees the same count (the ==15 assert above).
                    try { await McpTools.DeleteWorkflow(claude, "smoke-metric-cert"); } catch { }
                }
                Check("templates leg leaves the workflow library back at 15 (a shelf is not the board)",
                    (await McpTools.ListWorkflows(claude)).Length == 15);

                // ---- DYNAMIC WORKFLOW ACTIVATION (docs/pro-mode-spec.md §10.6 — 10-T2, dual-drive) ------------------
                // set_workflow_activation curates the MENU (show/hide a workflow when a condition holds). Drive it +
                // list/policy through the RemoteEngine proxy (cross-process — catches the "missed RemoteEngine
                // implementer" trap). The harness is DevPro, so the Pro write succeeds. governed-rename is a stock
                // workflow we curate then RESTORE (15 in, 15 out — activation adds/drops no content).
                const string actWf = "governed-rename";
                try
                {
                    // (1) A rule HIDES it (always-true `when`, set:off): list shows Active==false with a PLAIN reason.
                    // Predicate-matched (this workflow, now inactive) so an in-flight library rebroadcast from an
                    // earlier transition can never consume the waiter (no cross-pipe ordering guarantee).
                    var libWait1 = ui.Notify.WaitWorkflowLibraryAsync(l => l.Any(w => w.Name == actWf && !w.Active));
                    await McpTools.SetWorkflowActivation(claude, actWf, "date.dayOfMonth >= 1", "off");
                    var offList = await McpTools.ListWorkflows(claude);
                    var offWf = offList.Single(w => w.Name == actWf);
                    Check("MCP set_workflow_activation(set:off) deactivates the workflow with a PLAIN reason (no predicate echo)",
                        !offWf.Active && !string.IsNullOrWhiteSpace(offWf.ActiveReason) && !offWf.ActiveReason.Contains("dayOfMonth"));
                    Check("activation write leaves the library at exactly 15 (curation is not content)", offList.Length == 15);
                    var libNote1 = await libWait1.WaitAsync(TimeSpan.FromSeconds(5));
                    Check("set_workflow_activation broadcasts workflow/libraryDidChange to the UI door (dual-drive)",
                        libNote1 != null && libNote1.Any(w => w.Name == actWf && !w.Active));

                    // (2) Flip it back ON via the op — Active==true again, also broadcast live.
                    var libWait2 = ui.Notify.WaitWorkflowLibraryAsync(l => l.Any(w => w.Name == actWf && w.Active));
                    await McpTools.SetWorkflowActivation(claude, actWf, null, "on");
                    Check("MCP set_workflow_activation(set:on) flips it back Active==true",
                        (await McpTools.ListWorkflows(claude)).Single(w => w.Name == actWf).Active);
                    var libNote2 = await libWait2.WaitAsync(TimeSpan.FromSeconds(5));
                    Check("the flip-back is broadcast to the UI door too", libNote2 != null);

                    // (3) get_workflow_policy: per-workflow active + a lint on a PLANTED contradiction (dead rule —
                    // a rule turns it on while it's manually turned off; manual disable wins, the rule is dead).
                    await McpTools.SetWorkflowEnabled(claude, actWf, false);
                    await McpTools.SetWorkflowActivation(claude, actWf, "date.dayOfMonth >= 1", "on");
                    var pol = await McpTools.GetWorkflowPolicy(claude);
                    var polEntry = pol.Workflows.Single(w => w.Name == actWf);
                    Check("MCP get_workflow_policy surfaces per-workflow active + the dead-rule lint on the planted contradiction",
                        !polEntry.Active && !string.IsNullOrWhiteSpace(polEntry.ActiveReason)
                        && pol.Lints.Any(l => l.Message.Contains(actWf) && l.Message.Contains("no effect")));
                }
                finally
                {
                    // Restore the shared AdventureWorks sidecar so a re-run starts clean (15, re-enabled, un-curated).
                    try { await McpTools.SetWorkflowEnabled(claude, actWf, true); } catch { }
                    try { await McpTools.SetWorkflowActivation(claude, actWf, null, null); } catch { }
                }
                var actRestored = await McpTools.ListWorkflows(claude);
                Check("activation leg leaves the library back at exactly 15, the workflow re-enabled + active",
                    actRestored.Length == 15 && actRestored.Single(w => w.Name == actWf) is { Active: true, Enabled: true });

                // ---- THE MODEL INTERVIEW (docs/product-innovation-brainstorm.md §1 — dual-drive) --------------------
                // Drive all 4 ops through the RemoteEngine proxy (the cross-process door). Offline honesty is the
                // headline contract: a value-tier run with no live connection must come back Unverified (never a
                // fabricated pass), while the refusal tier is fully checkable offline. Clean up (delete) so the
                // shared AdventureWorks sidecar is left as found — the same courtesy as the workflow legs above.
                var ivqAdd = await McpTools.AddInterviewQuestion(claude, "What were total sales in 2024?", "value",
                    query: "EVALUATE ROW(\"v\", CALCULATE([Internet Total Sales], 'Date'[Calendar Year]=2024))",
                    expectedValue: "12345.67", seedSource: "user");
                Check("MCP add_interview_question persists a value-tier question (Pro) with its oracle + seedSource",
                    ivqAdd != null && ivqAdd.Id.StartsWith("iq-") && ivqAdd.Tier == "value" && ivqAdd.SeedSource == "user");
                try
                {
                    var ivList = await McpTools.ListInterviewQuestions(claude);
                    Check("MCP list_interview_questions shows the saved question with no last outcome yet",
                        ivList.Questions.Any(x => x.Id == ivqAdd.Id && x.LastRun == null));

                    var ivRun = await McpTools.RunInterview(claude, ivqAdd.Id);
                    Check("MCP run_interview OFFLINE is Unverified (never a fabricated pass) and records the outcome",
                        ivRun.Outcome == "Unverified" && ivRun.Detail.Contains("offline") && ivRun.Recorded);
                    Check("the recorded outcome shows on the saved question (the Studio card's data, no agent needed)",
                        (await McpTools.ListInterviewQuestions(claude)).Questions
                            .Single(x => x.Id == ivqAdd.Id).LastRun?.Outcome == "Unverified");

                    var ivRefuse = await McpTools.RunInterview(claude,
                        inlineJson: "{\"question\":\"What was churn last quarter?\",\"tier\":\"refusal\"}", abstained: true);
                    Check("MCP run_interview inline refusal tier grades fully offline (Refused; one-off, nothing persisted)",
                        ivRefuse.Outcome == "Refused" && ivRefuse.QuestionId == null && !ivRefuse.Recorded);
                }
                finally
                {
                    try { await McpTools.DeleteInterviewQuestion(claude, ivqAdd.Id); } catch { }
                }
                Check("MCP delete_interview_question leaves the pack as found",
                    (await McpTools.ListInterviewQuestions(claude)).Questions.All(x => x.Id != ivqAdd.Id));
                // The store is append-only (delete = a tombstone delta), so ALSO remove the JSONL itself — the
                // shared model lives in the vendored TabularEditor tree, which must stay clean between runs.
                try
                {
                    var ivFile = Path.Combine(Path.GetDirectoryName(bim), ".semanticus", "interview", "questions.jsonl");
                    if (File.Exists(ivFile)) { File.Delete(ivFile); Directory.Delete(Path.GetDirectoryName(ivFile)); }
                }
                catch { /* best-effort: a locked file must not fail the smoke */ }

                // ---- INTERVIEW SEEDS (the PR #84 fast-follows: verified answers + the built-in hard pack) -----------
                // Read-only over the same cross-process door. The verified-answers lane needs the Prep-for-AI disk
                // anchors beside the model, so the leg SNAPSHOTS whatever already exists and restores it byte-for-
                // byte in finally: pre-existing user/fixture files are never clobbered or deleted, and directory
                // pruning is non-recursive (only ever removes now-empty dirs the smoke itself created). Running the
                // smoke twice must leave the tree identical both times. No candidate carries an oracle: the engine
                // proposes, a human confirms — nothing fabricated.
                var seedModelDir = Path.GetDirectoryName(bim);
                var seedPbism = Path.Combine(seedModelDir, "definition.pbism");
                var seedVaRoot = Path.Combine(seedModelDir, "VerifiedAnswers");
                var seedVaDefs = Path.Combine(seedVaRoot, "definitions");
                var seedVaDir = Path.Combine(seedVaDefs, "smoke-va-1");
                var seedVaFile = Path.Combine(seedVaDir, "definition.json");
                var priorPbism = File.Exists(seedPbism) ? File.ReadAllBytes(seedPbism) : null;
                var priorVaFile = File.Exists(seedVaFile) ? File.ReadAllBytes(seedVaFile) : null;
                var vaRootExisted = Directory.Exists(seedVaRoot);
                var vaDefsExisted = Directory.Exists(seedVaDefs);
                var vaDirExisted = Directory.Exists(seedVaDir);
                try
                {
                    File.WriteAllText(seedPbism, "{ \"settings\": { \"qnaEnabled\": true } }");
                    Directory.CreateDirectory(seedVaDir);
                    File.WriteAllText(seedVaFile,
                        "{ \"name\": \"Smoke answer\", \"triggers\": [\"What were total internet sales?\", \"Total internet sales to date\"] }");

                    var seeds = await McpTools.ListInterviewSeeds(claude);
                    // >= 1, not == 1: a fixture tree may legitimately carry its own verified answers — this leg
                    // asserts OURS extracted, without denying theirs.
                    Check("MCP list_interview_seeds extracts the verified answer (first trigger = the question, alt phrasings kept, flow taught)",
                        seeds.VerifiedAnswersFound >= 1
                        && seeds.Candidates.Any(c => c.Source == "verified-answer" && c.Question == "What were total internet sales?"
                                                  && c.AltPhrasings.Contains("Total internet sales to date"))
                        && seeds.Note.Contains("add_interview_question"));
                    var packCands = seeds.Candidates.Where(c => c.Source == "hard-pack").ToArray();
                    var packSkips = seeds.Skipped.Where(s => s.Source == "hard-pack").ToArray();
                    Check("MCP list_interview_seeds hard pack binds-or-skips honestly (every template accounted for, nothing half-bound, every skip reasoned)",
                        packCands.Length + packSkips.Length == seeds.HardPackTemplates
                        && seeds.HardPackTemplates >= 10
                        && packCands.All(c => !c.Question.Contains('{') && !c.Query.Contains('{'))
                        && packSkips.All(s => !string.IsNullOrWhiteSpace(s.Reason)));
                }
                finally
                {
                    // Restore-exactly-or-remove-only-ours; each step best-effort so a locked file can't fail the smoke.
                    try { if (priorPbism != null) File.WriteAllBytes(seedPbism, priorPbism); else File.Delete(seedPbism); } catch { }
                    try { if (priorVaFile != null) File.WriteAllBytes(seedVaFile, priorVaFile); else File.Delete(seedVaFile); } catch { }
                    // Non-recursive deletes: they only succeed on EMPTY dirs, so pre-existing content is untouchable.
                    try { if (!vaDirExisted) Directory.Delete(seedVaDir); } catch { }
                    try { if (!vaDefsExisted) Directory.Delete(seedVaDefs); } catch { }
                    try { if (!vaRootExisted) Directory.Delete(seedVaRoot); } catch { }
                }

                // ---- FORMAT-STRING TEMPLATE LIBRARY (docs scratchpad format-string-template-library.md) --------------
                // The catalog is engine-side data served identically to the agent (MCP) and the webview (RPC). Drive
                // both ops through the RemoteEngine proxy like every section above. list_format_templates is read-only;
                // set_measure_format_expression writes a measure's DYNAMIC format string ("Format = Dynamic") — the gap
                // that set_measure_format is static-only. AdventureWorks is CL 1200 (< the 1601 floor), so here we prove
                // the WRITE op is wired + the CL gate fires (the full set/round-trip runs on a CL-1604 model in xUnit).
                var fmtAll = await McpTools.ListFormatTemplates(claude);
                Check("MCP list_format_templates returns the curated catalog (>=86 across 17 categories, static+dynamic, ~20 common)",
                    fmtAll.Length >= 86
                    && fmtAll.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 17
                    && fmtAll.Any(t => t.Kind == "static") && fmtAll.Any(t => t.Kind == "dynamic")
                    && fmtAll.Count(t => t.Common) >= 15
                    && fmtAll.All(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.FormatString)));
                var fmtPct = await McpTools.ListFormatTemplates(claude, "Percentages");
                Check("MCP list_format_templates(category) narrows to just that category (and it's smaller than the full set)",
                    fmtPct.Length > 0 && fmtPct.Length < fmtAll.Length && fmtPct.All(t => t.Category == "Percentages"));
                var fmtSearch = await McpTools.ListFormatTemplates(claude, null, "triangle");
                Check("MCP list_format_templates(search) finds the canonical zero-dependency triangle change-% (static, credited)",
                    fmtSearch.Any(t => t.Id == "chg-triangle-pct" && t.Kind == "static" && !string.IsNullOrEmpty(t.Credit)));

                string fmtGate = null;
                try { await McpTools.SetMeasureFormatExpression(claude, measureRef, "\"0.0%\""); }
                catch (Exception ex) { fmtGate = ex.Message; }
                Check("MCP set_measure_format_expression is wired through the proxy + refuses on a CL<1601 model (clear compatibility-level error)",
                    fmtGate != null && fmtGate.IndexOf("compatibility level", StringComparison.OrdinalIgnoreCase) >= 0);
                Check("MCP set_measure_format_expression: clearing (empty) on a below-floor model is a safe net-zero no-op (no throw)",
                    !(await McpTools.SetMeasureFormatExpression(claude, measureRef, "")).Changed);

                // MCP tool: review_reconcile_mapping — prove the read-only mapping review crosses the proxy and
                // emits the compact activity that lets the UI door observe the agent's review. This fixture has no
                // Fabric SQL partition metadata, so an honest no-coordinate review is the expected offline result.
                var mappingActivityWait = ui.WaitActivityAsync(e => e?.Kind == "review_reconcile_mapping");
                var mappingReview = await McpToolsTesting.ReviewReconcileMapping(claude,
                    new ReconcileMappingRequest { MeasureRef = measureRef });
                Check("MCP review_reconcile_mapping returns an honest offline review through the proxy",
                    mappingReview != null && mappingReview.Error == null && mappingReview.MeasureRef == measureRef
                    && !mappingReview.Tested && string.IsNullOrEmpty(mappingReview.EffectiveServer)
                    && !string.IsNullOrEmpty(mappingReview.Note));
                var mappingActivity = await mappingActivityWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI receives review_reconcile_mapping activity from the agent door",
                    mappingActivity != null && mappingActivity.Ok && mappingActivity.Origin == "agent"
                    && mappingActivity.Label == "Reviewed reconciliation SQL mapping");

                // MCP tool: reconcile_measure — the Tests-tab SQL↔DAX reconciler runner. The smoke model is OFFLINE
                // (no live XMLA/SQL), so we prove the op is reachable through the proxy + validates its request at the
                // boundary (a green needs a live endpoint — that path is on the live gate). The request contract:
                //  * a missing/unknown blank policy is an InputError (no silent BLANK/NULL/0 default);
                //  * a non-finite/negative tolerance is an InputError BEFORE a run is spent;
                //  * a groupBy entry that does not resolve to a REAL model column is refused, naming it (P1-3 — raw
                //    text is never spliced into DAX);
                //  * a well-formed request with no live connection degrades honestly (never a false green).
                // Find a real column ref so the resolvable-request path can be exercised offline.
                string columnRef = null;
                foreach (var t in await McpTools.ListObjects(claude, null))
                {
                    var kids = await McpTools.ListObjects(claude, t.Ref);
                    var col = kids.FirstOrDefault(x => x.Kind == "column");
                    if (col != null) { columnRef = col.Ref; break; }
                }
                Check("MCP reconcile_measure smoke found a real column ref", columnRef != null);

                var recBadPolicy = await McpTools.ReconcileMeasure(claude, measureRef, "SELECT 1", blankPolicy: "",
                    groupBy: new[] { columnRef });
                Check("MCP reconcile_measure refuses a missing blank policy as InputError (no silent BLANK/NULL/0 default)",
                    recBadPolicy != null && recBadPolicy.Status == ReconcileStatus.InputError
                    && !string.IsNullOrEmpty(recBadPolicy.Error) && recBadPolicy.Error.IndexOf("blank policy", StringComparison.OrdinalIgnoreCase) >= 0);

                var recBadTol = await McpTools.ReconcileMeasure(claude, measureRef, "SELECT 1", blankPolicy: "zero",
                    groupBy: new[] { columnRef }, toleranceRelative: -1);
                Check("MCP reconcile_measure rejects a negative tolerance as InputError before spending a run",
                    recBadTol != null && recBadTol.Status == ReconcileStatus.InputError
                    && recBadTol.Error != null && recBadTol.Error.IndexOf("tolerance", StringComparison.OrdinalIgnoreCase) >= 0);

                var recBadCol = await McpTools.ReconcileMeasure(claude, measureRef, "SELECT 1", blankPolicy: "zero",
                    groupBy: new[] { "'NoSuchTable'[Nope]" }, server: "srv", database: "db");
                Check("MCP reconcile_measure refuses an unresolvable groupBy column as InputError, naming it (P1-3)",
                    recBadCol != null && recBadCol.Status == ReconcileStatus.InputError
                    && recBadCol.Error != null && recBadCol.Error.IndexOf("NoSuchTable", StringComparison.OrdinalIgnoreCase) >= 0);

                // Security = the STANDARD QueryData gate on the SQL target (the permissions tab), same as preview_table
                // / run_dax. The SQL-target gate runs BEFORE the connection check, so it is exercisable offline. The
                // unlabelled 'srv'/'db' target resolves to prod; under the default (standard) preset QueryData@prod =
                // Ask, so an AGENT is refused pending approval — proving the gate is wired through the op offline.
                var priorPreset = (await owner.GetAgentPolicyAsync())?.Preset ?? "standard";
                await owner.SetAgentPolicyPresetAsync("standard", "human");
                try
                {
                    var recGate = await McpTools.ReconcileMeasure(claude, measureRef, "SELECT 1", blankPolicy: "zero",
                        groupBy: new[] { columnRef }, server: "srv", database: "db");
                    Check("MCP reconcile_measure gates an AGENT on the SQL target (QueryData Ask on unlabelled=prod) BEFORE any connection",
                        recGate != null && recGate.Status == ReconcileStatus.InputError && recGate.PolicyRefused
                        && recGate.Error != null && recGate.Error.IndexOf("connect", StringComparison.OrdinalIgnoreCase) < 0);

                    // Allow the target => the gate passes and the run honestly reports the missing live connection
                    // (never a false green) — proving Allow is not the blocker.
                    await owner.SetAgentPolicyPresetAsync("open", "human");
                    var recOffline = await McpTools.ReconcileMeasure(claude, measureRef, "SELECT 1", blankPolicy: "zero",
                        groupBy: new[] { columnRef }, server: "srv", database: "db");
                    Check("MCP reconcile_measure on an ALLOWED target passes the gate then degrades honestly with no live connection (never a false green)",
                        recOffline != null && !recOffline.PolicyRefused && recOffline.Status != ReconcileStatus.Reconciled && !recOffline.Complete
                        && recOffline.Error != null && recOffline.Error.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0
                        && !string.IsNullOrEmpty(recOffline.SuggestedNextAction) && !recOffline.CoverageKnown);
                }
                finally { await owner.SetAgentPolicyPresetAsync(priorPreset, "human"); }   // restore the user's actual preset

                // ---- MODERN CALENDARS: MCP edit -> UI observes -> UI undo -> MCP observes -----------------------
                // Calendar metadata rides a raw-TOM/TMDL undo seam because the pinned TOMWrapper predates Calendar.
                // That makes this cross-process check load-bearing: it proves the seam still reaches the ONE shared
                // session, emits exactly one attributed model/didChange, and participates in the other driver's undo.
                const string calendarTable = "Semanticus Calendar Smoke";
                const string calendarName = "Smoke Gregorian";
                var clWait = ui.Notify.WaitNextAsync();
                await McpTools.SetCompatibilityLevel(claude, 1701);
                var clNote = await clWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("calendar/MCP: compatibility upgrade crosses the remote door and broadcasts to the UI",
                    clNote != null && clNote.Origin == "agent");

                await Task.Delay(50);   // let this client's already-delivered upgrade broadcast settle before counting the next write
                var beforeCalendarBroadcasts = ui.Notify.DidChangeCount;
                var calendarWait = ui.Notify.WaitNextAsync();
                var definedCalendar = await McpTools.DefineCalendarFromTemplate(claude, "gregorian",
                    tableName: calendarTable, calendarName: calendarName);
                var calendarNote = await calendarWait.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);   // a duplicate broadcast, if introduced, must arrive before the exact-count assertion
                Check("calendar/MCP: define_calendar_from_template creates the modern calendar through the agent door",
                    definedCalendar != null && definedCalendar.Table == calendarTable && definedCalendar.Calendar == calendarName
                    && definedCalendar.Mappings.Any(m => m.Contains("Date")));
                Check("calendar/dual-drive: UI receives exactly one attributed model/didChange for the calendar batch",
                    calendarNote != null && calendarNote.Origin == "agent"
                    && calendarNote.Revision == definedCalendar.Revision
                    && ui.Notify.DidChangeCount == beforeCalendarBroadcasts + 1);

                var uiCalendars = await ui.Invoke<CalendarListResult>("listCalendars", calendarTable);
                Check("calendar/dual-drive: UI reads the agent-created calendar on the shared session",
                    uiCalendars.Calendars.Length == 1 && uiCalendars.Calendars[0].Name == calendarName
                    && uiCalendars.Calendars[0].Groups.Any(g => g.TimeUnit == "Date"));

                await ui.Invoke<UndoState>("undo");
                Check("calendar/dual-drive: one UI undo removes the agent's whole table+calendar batch",
                    !(await McpTools.ListCalendars(claude)).Calendars.Any(c => c.Name == calendarName));
                await ui.Invoke<UndoState>("redo");
                Check("calendar/dual-drive: UI redo restores the calendar and MCP reads it",
                    (await McpTools.ListCalendars(claude)).Calendars.Any(c => c.Name == calendarName));
                await ui.Invoke<UndoState>("undo");   // remove the calendar table
                await ui.Invoke<UndoState>("undo");   // restore the fixture's original compatibility level

                Check("MCP smoke keeps fixture approvals out of the developer ledger",
                    SameSnapshot(realApprovalBefore, ReadSnapshot(realApprovalPath)));
                Console.WriteLine();
                if (_failures == 0) { Console.WriteLine("==== M1 MCP DOOR: PASS ===="); return 0; }
                Console.WriteLine($"==== M1 MCP DOOR: {_failures} CHECK(S) FAILED ===="); return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n==== M1 MCP DOOR: EXCEPTION ====");
                Console.WriteLine(ex);
                return 2;
            }
            finally
            {
                claude?.Dispose();
                ui?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
                AgentPolicyStore.RootOverride = null;
                ApprovalLedger.RootOverride = null;
                ConnectionRegistry.RootOverride = null;
                try { if (Directory.Exists(policyRoot)) Directory.Delete(policyRoot, true); } catch { }
            }
        }

        private static byte[] ReadSnapshot(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
        private static bool SameSnapshot(byte[] left, byte[] right) =>
            left == null ? right == null : right != null && left.SequenceEqual(right);

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        // A minimal, self-contained PBI-V3 model with the incremental-refresh PREREQUISITES baked in. Three tables:
        // 'Sales' whose M partition filters [Date] on RangeStart/RangeEnd (the happy path), 'Raw' whose M never
        // mentions them, and 'Sneaky' that mentions them ONLY in a comment (the substring-false-allow negative).
        // Parameterised by compatibility level (for the Hybrid CL-1565 gate) and whether the params carry
        // IsParameterQuery=true (for the not-a-parameter gate). #table literals so nothing external is needed; no
        // shipped test model has these prereqs, so the round-trip creates them here.
        private static string IncrementalFixtureBim(int cl, bool? realParams)
        {
            var meta = realParams == true
                ? "meta [IsParameterQuery=true, Type=\\\"DateTime\\\", IsParameterQueryRequired=true]"
                : "meta [Type=\\\"DateTime\\\"]";
            var expressions = realParams.HasValue ? IncrementalExpressions.Replace("__META__", meta) : "";
            return IncrementalFixtureTemplate.Replace("__CL__", cl.ToString()).Replace("__EXPRESSIONS__", expressions);
        }

        private const string IncrementalExpressions = """
"expressions":[{"name":"RangeStart","kind":"m","expression":"#datetime(2020,1,1,0,0,0) __META__"},{"name":"RangeEnd","kind":"m","expression":"#datetime(2025,1,1,0,0,0) __META__"}],
""";

        private const string IncrementalFixtureTemplate = """{"name":"IncrementalReady","compatibilityLevel":__CL__,"model":{__EXPRESSIONS__"defaultPowerBIDataSourceVersion":"powerBI_V3","tables":[{"name":"Sales","columns":[{"name":"Date","dataType":"dateTime","sourceColumn":"Date"},{"name":"Amount","dataType":"double","sourceColumn":"Amount"}],"partitions":[{"name":"Sales","mode":"import","source":{"type":"m","expression":"let Source = #table(type table [Date = datetime, Amount = number], {}), Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered"}}]},{"name":"Raw","columns":[{"name":"Id","dataType":"int64","sourceColumn":"Id"},{"name":"Date","dataType":"dateTime","sourceColumn":"Date"}],"partitions":[{"name":"Raw","mode":"import","source":{"type":"m","expression":"let Source = #table(type table [Id = Int64.Type, Date = datetime], {}) in Source"}}]},{"name":"Bare","columns":[{"name":"Date","dataType":"dateTime","sourceColumn":"Date"}],"partitions":[{"name":"Bare","mode":"import","source":{"type":"m","expression":"#table(type table [Date = datetime], {})"}}]},{"name":"Sneaky","columns":[{"name":"Id","dataType":"int64","sourceColumn":"Id"}],"partitions":[{"name":"Sneaky","mode":"import","source":{"type":"m","expression":"let Source = #table(type table [Id = Int64.Type], {}) /* filter on RangeStart and RangeEnd someday */ in Source"}}]}]}}""";

        private static string FindTestBim()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var candidate = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", "AdventureWorks.bim");
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate AdventureWorks.bim from " + AppContext.BaseDirectory);
        }

        private sealed class UiClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            public NotifyCollector Notify { get; }

            private UiClient(NamedPipeClientStream pipe, JsonRpc rpc, NotifyCollector notify) { _pipe = pipe; _rpc = rpc; Notify = notify; }

            public static async Task<UiClient> ConnectAsync(string pipeName)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                var notify = new NotifyCollector();
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.AddLocalRpcTarget(notify);
                rpc.StartListening();
                return new UiClient(pipe, rpc, notify);
            }

            public Task<string> GetDax(string objRef) => _rpc.InvokeAsync<string>("getDax", objRef);
            public Task<T> Invoke<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);
            public Task<ActivityEvent> WaitActivityAsync(Func<ActivityEvent, bool> match) => Notify.WaitActivityAsync(match);
            public Task<WorkflowRunView> WaitWorkflowAsync(Func<WorkflowRunView, bool> match) => Notify.WaitWorkflowAsync(match);
            public Task<WorkflowInfo[]> WaitWorkflowLibraryAsync(Func<WorkflowInfo[], bool> match) => Notify.WaitWorkflowLibraryAsync(match);
            public void Dispose() { try { _rpc.Dispose(); } catch { } try { _pipe.Dispose(); } catch { } }
        }

        private sealed class NotifyCollector
        {
            private readonly object _gate = new object();
            private TaskCompletionSource<ChangeNotification> _next;
            private int _didChangeCount;
            public int DidChangeCount => System.Threading.Volatile.Read(ref _didChangeCount);

            [JsonRpcMethod("model/didChange")]
            public void OnDidChange(ChangeNotification n)
            {
                System.Threading.Interlocked.Increment(ref _didChangeCount);
                lock (_gate) { var t = _next; _next = null; t?.TrySetResult(n); }
            }

            public Task<ChangeNotification> WaitNextAsync()
            {
                lock (_gate)
                {
                    _next = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _next.Task;
                }
            }

            private readonly object _activityGate = new object();
            private TaskCompletionSource<ActivityEvent> _nextActivity;
            private Func<ActivityEvent, bool> _activityMatch;

            [JsonRpcMethod("model/activity")]
            public void OnActivity(ActivityEvent e)
            {
                lock (_activityGate)
                {
                    if (_nextActivity == null || (_activityMatch != null && !_activityMatch(e))) return;
                    var t = _nextActivity; _nextActivity = null; _activityMatch = null;
                    t.TrySetResult(e);
                }
            }

            public Task<ActivityEvent> WaitActivityAsync(Func<ActivityEvent, bool> match)
            {
                lock (_activityGate)
                {
                    _activityMatch = match;
                    _nextActivity = new TaskCompletionSource<ActivityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextActivity.Task;
                }
            }

            // workflow/didChange — the Pro-mode workflow engine's live-transition broadcast (same wiring shape as
            // model/didChange above): every run transition (start / submit / skip / abort / complete) fires it, so
            // the UI door and the agent door see the same run state (golden rule #2, dual-drive).
            //
            // The waiter is PREDICATE-FILTERED, not "next event": the ui and claude pipes are independent, so a
            // transition broadcast from the PREVIOUS step (e.g. step-2 "active") can still be in flight on the ui
            // pipe when the smoke — having seen the step-2 RPC response on the claude pipe — registers the waiter
            // for the completing transition. A one-shot next-event waiter gets CONSUMED by that straggler (resolving
            // with status "active"), and the completed event then arrives with no waiter armed. Cross-pipe ordering
            // is not a thing JSON-RPC gives us; the waiter must state WHICH event it is waiting to observe.
            private readonly object _wfGate = new object();
            private TaskCompletionSource<WorkflowRunView> _nextWf;
            private Func<WorkflowRunView, bool> _wfMatch;

            [JsonRpcMethod("workflow/didChange")]
            public void OnWorkflowDidChange(WorkflowRunView v)
            {
                lock (_wfGate)
                {
                    if (_nextWf == null || (_wfMatch != null && !_wfMatch(v))) return;   // a straggler never consumes the waiter
                    var t = _nextWf; _nextWf = null; _wfMatch = null;
                    t.TrySetResult(v);
                }
            }

            public Task<WorkflowRunView> WaitWorkflowAsync(Func<WorkflowRunView, bool> match)
            {
                lock (_wfGate)
                {
                    _wfMatch = match;
                    _nextWf = new TaskCompletionSource<WorkflowRunView>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextWf.Task;
                }
            }

            // workflow/libraryDidChange — the LIBRARY-level broadcast (save/delete/enable/bind/ACTIVATE from
            // either door). Carries the refreshed list so the UI re-lists without a refetch; 10-T2 uses it to prove
            // set_workflow_activation flips the menu live over the cross-process door (dual-drive, golden rule #2).
            // Predicate-filtered for the same straggler reason as the run channel above (library rebroadcasts also
            // fire on session/plan/connection transitions, so unrelated in-flight lists must not consume the waiter).
            private readonly object _libGate = new object();
            private TaskCompletionSource<WorkflowInfo[]> _nextLib;
            private Func<WorkflowInfo[], bool> _libMatch;

            [JsonRpcMethod("workflow/libraryDidChange")]
            public void OnWorkflowLibraryDidChange(WorkflowInfo[] v)
            {
                lock (_libGate)
                {
                    if (_nextLib == null || (_libMatch != null && !_libMatch(v))) return;   // a straggler never consumes the waiter
                    var t = _nextLib; _nextLib = null; _libMatch = null;
                    t.TrySetResult(v);
                }
            }

            public Task<WorkflowInfo[]> WaitWorkflowLibraryAsync(Func<WorkflowInfo[], bool> match)
            {
                lock (_libGate)
                {
                    _libMatch = match;
                    _nextLib = new TaskCompletionSource<WorkflowInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextLib.Task;
                }
            }
        }
    }
}
