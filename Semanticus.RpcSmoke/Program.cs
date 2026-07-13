using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using StreamJsonRpc;

namespace Semanticus.RpcSmoke
{
    /// <summary>
    /// M1 dual-drive proof. Starts the engine's RpcServer in-process on a named pipe, connects TWO
    /// independent JSON-RPC clients ("UI" and "agent"), and proves they share ONE live session:
    /// an edit by one client is broadcast to the other and is visible on its next read, and undo is
    /// shared. (Phase B swaps the second client for a real MCP-driven Claude; the mechanism is identical.)
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static async Task<int> Main()
        {
            var pipeName = "semanticus-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            // Smoke harness runs as Pro so it exercises the BULK change-plan apply; the Pro GATE itself is covered
            // by Semanticus.Tests/EntitlementGateTests.
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);

            Client ui = null, agent = null;
            try
            {
                var bim = FindTestBim();
                Console.WriteLine($"[i] pipe={pipeName}  model={Path.GetFileName(bim)}");

                ui = await Client.ConnectAsync(pipeName);
                agent = await Client.ConnectAsync(pipeName);
                Check("two independent clients connected to one engine", ui != null && agent != null);

                // UI opens the model.
                var open = await ui.Invoke<OpenResult>("open", bim);
                Check("open: tables > 0", open.Tables > 0);
                Console.WriteLine($"[i] opened '{open.ModelName}': {open.Tables} tables, {open.Measures} measures");

                // UI navigates to find a measure.
                var measureRef = await FindAMeasure(ui);
                Check("found a measure to edit", measureRef != null);
                Console.WriteLine($"[i] target measure ref: {measureRef}");

                var original = await ui.Invoke<string>("getDax", measureRef);
                Console.WriteLine($"[i] original DAX: {Trunc(original)}");

                // ---- DUAL-DRIVE #1: UI edits -> AGENT must see it live -------------------------
                var edited = (original ?? "") + " /* edit-by-ui */";
                var wait1 = agent.Notify.WaitNextAsync();
                var set1 = await ui.Invoke<SetResult>("setDax", measureRef, edited);
                Check("setDax reported changed", set1.Changed);
                var n1 = await wait1.WaitAsync(TimeSpan.FromSeconds(5));
                Check("agent received model/didChange for UI edit", n1 != null);
                Check("notification references the edited measure", n1.Deltas != null && n1.Deltas.Any(d => d.Ref == measureRef));
                Check("notification revision advanced", n1.Revision == set1.Revision && n1.Revision > 0);
                var agentSees = await agent.Invoke<string>("getDax", measureRef);
                Check("AGENT reads the UI's edit on the same live session", agentSees == edited);

                // ---- DUAL-DRIVE #2: AGENT edits -> UI must see it live -------------------------
                var edited2 = "1 + 1 /* edit-by-agent */";
                var wait2 = ui.Notify.WaitNextAsync();
                var set2 = await agent.Invoke<SetResult>("setDax", measureRef, edited2);
                var n2 = await wait2.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI received model/didChange for agent edit", n2 != null && n2.Deltas.Any(d => d.Ref == measureRef));
                var uiSees = await ui.Invoke<string>("getDax", measureRef);
                Check("UI reads the AGENT's edit on the same live session", uiSees == edited2);

                // ---- DUAL-DRIVE: the tree-paste RPC (duplicateObject + targetRef). The UI pastes a measure onto
                // ANOTHER table; the copy lands there under a collision-safe name and the AGENT sees the broadcast.
                var srcTable = measureRef.Substring("measure:".Length, measureRef.IndexOf('/') - "measure:".Length);
                var roots = await ui.Invoke<TreeNode[]>("listTree", (object)null);
                var otherTable = roots.First(t => t.Kind == "table" && t.Name != srcTable);
                var waitDup = agent.Notify.WaitNextAsync();
                // NEW wire shape: (objRef, newName, origin, targetRef) — targetRef APPENDED after origin.
                var dupRef = await ui.Invoke<string>("duplicateObject", measureRef, null, "human", otherTable.Ref);
                Check("duplicateObject(targetRef) lands the copy on the OTHER table", dupRef.StartsWith("measure:" + otherTable.Name + "/"));
                var nDup = await waitDup.WaitAsync(TimeSpan.FromSeconds(5));
                Check("AGENT received model/didChange for the UI's paste", nDup != null && nDup.Origin == "human");
                Check("AGENT reads the pasted copy on the same live session", await agent.Invoke<string>("getDax", dupRef) == edited2);
                await ui.Invoke<UndoState>("undo");   // net-zero: remove the pasted copy before the next legs
                bool dupGone = false;
                try { await agent.Invoke<string>("getDax", dupRef); } catch { dupGone = true; }
                Check("undo removed the pasted copy (one batch incl. its in-batch rename)", dupGone);

                // LEGACY wire shape: the pre-targetRef 3-arg positional call (objRef, newName, origin) — an older
                // extension bundle attached to a newer engine. targetRef was appended AFTER origin precisely so
                // this keeps binding origin as origin and duplicating IN PLACE (never an unknown-target failure).
                var waitLegacy = ui.Notify.WaitNextAsync();
                var legacyRef = await agent.Invoke<string>("duplicateObject", measureRef, "Legacy Dup Smoke", "agent");
                Check("LEGACY 3-arg duplicateObject stays in-place (3rd positional arg not misread as targetRef)",
                    legacyRef == "measure:" + srcTable + "/Legacy Dup Smoke");
                var nLegacy = await waitLegacy.WaitAsync(TimeSpan.FromSeconds(5));
                Check("LEGACY 3-arg duplicateObject binds its 3rd positional arg to ORIGIN", nLegacy != null && nLegacy.Origin == "agent");
                await agent.Invoke<UndoState>("undo");   // net-zero

                // ---- DUAL-DRIVE #3: AGENT runs a READ op -> UI sees it live via model/activity -----
                // Non-mutating runs (run_dax/profile_dax/…) don't mutate the model, so they publish an ActivityEvent
                // that the engine re-broadcasts as model/activity — this is how the human's Studio reflects what Claude
                // is running. We invoke publishActivity directly (the same RPC McpTools calls after each read op).
                var waitAct = ui.Notify.WaitNextActivityAsync();
                await agent.Invoke<object>("publishActivity", new ActivityEvent
                {
                    Kind = "profile_dax", Origin = "agent", Label = "Profiled server timings",
                    Query = "EVALUATE ROW(\"x\", [Total Sales])", Ok = true, ElapsedMs = 42,
                });
                var act = await waitAct.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI received model/activity for the agent's read op", act != null && act.Kind == "profile_dax" && act.Origin == "agent");
                Check("model/activity carries the op's label + query", act.Label == "Profiled server timings" && (act.Query ?? "").Contains("Total Sales"));
                Check("model/activity Seq is stamped server-side for ordering", act.Seq > 0);

                // ---- DOCUMENTATION NARRATIVE: dual-drive round-trip (annotation, MutateAsync broadcast, undo) ----
                const string docCtx = "Revenue grain: one row per order line.";
                var waitDoc = agent.Notify.WaitNextAsync();
                var setDoc = await ui.Invoke<SetResult>("setDocSection", measureRef, "businessContext", docCtx, "human");
                Check("setDocSection reported changed", setDoc != null && setDoc.Changed);
                var ndoc = await waitDoc.WaitAsync(TimeSpan.FromSeconds(5));
                Check("AGENT received model/didChange for the UI's doc narrative", ndoc != null);
                var agentReadsDoc = await agent.Invoke<string>("getDocSection", measureRef, "businessContext");
                Check("AGENT reads the UI's doc narrative on the same live session", agentReadsDoc == docCtx);
                var outline = await agent.Invoke<DocOutline>("getDocOutline");
                Check("getDocOutline lists the model + tables, and marks the authored section",
                    outline?.Items != null && outline.Items.Any(i => i.Kind == "model") && outline.Items.Any(i => i.Kind == "table")
                    && outline.Items.Any(i => i.Ref == measureRef && i.Sections.Contains("businessContext")));

                // ---- getDocModel: the full doc snapshot composes existing + new readers AND carries the narrative ----
                var dm = await agent.Invoke<DocModelDto>("getDocModel", 50);
                Check("getDocModel returns a header (name, compat level, counts)",
                    dm?.Header != null && !string.IsNullOrEmpty(dm.Header.Name) && dm.Header.TableCount > 0);
                Check("getDocModel composes graph + measures + columns",
                    dm.Graph?.Tables?.Length > 0 && dm.Measures.Length > 0 && dm.Columns.Length > 0);
                Check("getDocModel surfaces per-table detail incl. partitions (new TOM reader)",
                    dm.Tables.Length == dm.Header.TableCount && dm.Tables.Any(t => t.Partitions.Length > 0));
                Check("getDocModel reflects the authored measure narrative",
                    dm.Measures.Any(r => r.Ref == measureRef && r.Narrative != null
                        && r.Narrative.Sections.Any(sec => sec.Key == "businessContext" && sec.Markdown == docCtx)));
                var waitDocU = agent.Notify.WaitNextAsync();
                await ui.Invoke<UndoState>("undo");                  // undo the doc-narrative annotation
                await waitDocU.WaitAsync(TimeSpan.FromSeconds(5));
                var afterDocUndo = await agent.Invoke<string>("getDocSection", measureRef, "businessContext");
                Check("undo reverts the doc narrative (annotation on the shared undo timeline)", string.IsNullOrEmpty(afterDocUndo));

                // ---- RELATIONSHIP CARDINALITY: the diagram properties-panel edit op (self-contained set+undo) ----
                var g0 = await agent.Invoke<ModelGraph>("getModelGraph");
                var rel0 = g0.Relationships.FirstOrDefault();
                Check("model has a relationship to test cardinality on", rel0 != null);
                if (rel0 != null)
                {
                    var origFrom = rel0.FromCardinality; var origTo = rel0.ToCardinality;
                    var target = origFrom == "One" ? "Many" : "One";   // pick a from-cardinality different from the current one
                    var waitCard = agent.Notify.WaitNextAsync();
                    var setCard = await ui.Invoke<SetResult>("setRelationshipCardinality", rel0.Name, target, "One", "human");
                    Check("setRelationshipCardinality returns a serializable SetResult (changed)", setCard != null && setCard.Changed);
                    await waitCard.WaitAsync(TimeSpan.FromSeconds(5));
                    var rel1 = (await agent.Invoke<ModelGraph>("getModelGraph")).Relationships.FirstOrDefault(r => r.Name == rel0.Name);
                    Check("AGENT sees the new cardinality on the shared session", rel1 != null && rel1.FromCardinality == target && rel1.ToCardinality == "One");
                    var waitCardU = agent.Notify.WaitNextAsync();
                    await ui.Invoke<UndoState>("undo");                 // undo the cardinality change (net-zero on the undo stack)
                    await waitCardU.WaitAsync(TimeSpan.FromSeconds(5));
                    var rel2 = (await agent.Invoke<ModelGraph>("getModelGraph")).Relationships.FirstOrDefault(r => r.Name == rel0.Name);
                    Check("undo reverts the cardinality change", rel2 != null && rel2.FromCardinality == origFrom && rel2.ToCardinality == origTo);
                }

                // ---- SHARED UNDO across both drivers ------------------------------------------
                var waitU = agent.Notify.WaitNextAsync();
                var undo = await ui.Invoke<UndoState>("undo");      // UI undoes the agent's edit
                await waitU.WaitAsync(TimeSpan.FromSeconds(5));
                var afterUndo = await agent.Invoke<string>("getDax", measureRef);
                Check("shared undo: UI's undo reverts the AGENT's edit, visible to agent", afterUndo == edited);

                // ---- session info reflects unsaved state --------------------------------------
                var info = await ui.Invoke<SessionInfo>("sessionInfo");
                Check("sessionInfo reports unsaved changes", info.HasUnsavedChanges);

                // ---- CHANGE-PLAN FLAGSHIP over the RPC door -----------------------------------
                // The Optimize "PR" tab's path: proposePlan -> getPlan -> addPlanItem -> applyPlan -> clearPlan,
                // all over JSON-RPC. Proves the flagship DTOs (ChangePlanView / ChangeItem[] / PlanSummary /
                // ApplyPlanReport) round-trip over the wire, the plan APPLIES on the UI door, and plan/didChange
                // reaches the other client (dual-drive). Previously only the MCP door was smoke-covered for plans.
                var plan = await ui.Invoke<ChangePlanView>("proposePlan", null, true, 40, "human");
                Check("proposePlan returns a serializable ChangePlanView (id + seeded items + consistent summary)",
                    plan != null && plan.PlanId != null && plan.Items != null && plan.Summary != null
                    && plan.Items.Length > 0 && plan.Summary.Total == plan.Items.Length);
                var roundTrip = await ui.Invoke<ChangePlanView>("getPlan");
                Check("getPlan round-trips the same plan over JSON-RPC (id + item count stable)",
                    roundTrip.PlanId == plan.PlanId && roundTrip.Items.Length == plan.Items.Length);

                // Add a deterministic, content-bearing item (set_description w/ content auto-approves) and prove
                // the OTHER client receives plan/didChange for it (the "watch the plan assemble" behavior).
                const string descText = "RPC plan-smoke description";
                var planWait = agent.Notify.WaitNextPlanAsync(v => v.Items.Any(i => i.After == descText));
                var added = await ui.Invoke<ChangePlanView>("addPlanItem", measureRef, "set_description", descText, "desc via RPC", null, null, "human");
                var newItem = added.Items.FirstOrDefault(i => i.After == descText);
                Check("addPlanItem (set_description w/ content) lands APPROVED, serialized over RPC",
                    newItem != null && newItem.Id != null && newItem.Status == "approved");
                var planNote = await planWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("AGENT received plan/didChange when UI added a plan item (plan-broadcast dual-drive)",
                    planNote != null && planNote.Items.Any(i => i.After == descText));

                // Exercise the human approve/reject verb (setPlanItem) over RPC — reject the item then re-approve
                // it (covers the proposed->rejected/approved path, not just auto-approve-on-content).
                var rejected = await ui.Invoke<ChangePlanView>("setPlanItem", newItem.Id, null, false, "human");
                Check("setPlanItem(approved:false) rejects the item over RPC",
                    rejected.Items.First(i => i.Id == newItem.Id).Status == "rejected");
                var reapproved = await ui.Invoke<ChangePlanView>("setPlanItem", newItem.Id, null, true, "human");
                Check("setPlanItem(approved:true) re-approves the item over RPC",
                    reapproved.Items.First(i => i.Id == newItem.Id).Status == "approved");

                // Apply just that item; prove ApplyPlanReport serializes AND the edit actually hit the model.
                var report = await ui.Invoke<ApplyPlanReport>("applyPlan", (object)new[] { newItem.Id }, "human");
                Check("applyPlan returns a serializable ApplyPlanReport (applied=1, 0 failed, grade before/after present)",
                    report != null && report.AppliedCount == 1 && report.FailedCount == 0
                    && report.GradeBefore != null && report.GradeAfter != null && report.Items.Length == 1);
                var changed = await agent.Invoke<ObjectInfo>("getObject", measureRef);
                Check("the applied plan item actually changed the model (description set, visible to the other client)",
                    changed.Properties.TryGetValue("description", out var dv) && dv != null && dv.ToString() == descText);

                var cleared = await ui.Invoke<ChangePlanView>("clearPlan", "human");
                Check("clearPlan returns an empty plan over RPC", cleared.Items.Length == 0 && cleared.Summary.Total == 0);

                // ---- LSDL (Prep-for-AI) WRITERS over the RPC door -----------------------------
                // Closes the documented dual-drive test gap: enableQna / setSynonyms / setAiInstructions /
                // setAiDataSchema were WIRED on EngineRpcTarget but no smoke had ever driven them over the
                // wire — their RPC path was "asserted by construction" (a recurring honest residual). This
                // proves the args (string[] terms, the bool `included` flag, the instructions string) AND the
                // SetResult / SetInstructionsResult DTOs round-trip over JSON-RPC, each write APPLIES on the
                // shared session, and the OTHER client sees it live via model/didChange (golden rule #2).
                // LSDL is gated to CL>=1465, so this needs a modern model (AdventureWorks is 1200) — open the
                // same AllProperties.bim the AirSmoke LSDL tests use, on the SAME live session both clients share.
                var modern = FindTestData("AllProperties.bim");
                if (modern == null)
                {
                    Console.WriteLine("[i] LSDL writers over RPC: AllProperties.bim not found — skipping (no modern model)");
                }
                else
                {
                    var openModern = await ui.Invoke<OpenResult>("open", modern);
                    Check("LSDL/RPC: opened a modern (CL>=1465) model so the linguistic schema is writable", openModern.Tables > 0);

                    // enable_qna over RPC: seeds the linguistic schema; the agent must see the SYN-SCHEMA
                    // finding clear on its own scan (proves the write crossed the session, not just the DTO).
                    var eq = await ui.Invoke<SetResult>("enableQna", null, "human");
                    Check("LSDL/RPC: enableQna returns a serializable SetResult and reports a change", eq != null && eq.Changed);
                    var agentScan = await agent.Invoke<Scorecard>("aiReadinessScan");
                    Check("LSDL/RPC: AGENT sees the linguistic schema seeded by the UI (SYN-SCHEMA cleared on the shared session)",
                        !agentScan.Findings.Any(f => f.RuleId == "SYN-SCHEMA"));

                    // Find a field to carry synonyms / a data-schema toggle.
                    var fieldRef = await FindAField(ui);
                    Check("LSDL/RPC: found a measure/column to carry synonyms", fieldRef != null);
                    if (fieldRef != null)
                    {
                        // setSynonyms over RPC: a string[] argument must marshal across the wire. Prove the
                        // OTHER client receives model/didChange for it (dual-drive broadcast of an LSDL write).
                        var synWait = agent.Notify.WaitNextAsync();
                        var syn = await ui.Invoke<SetResult>("setSynonyms", fieldRef, new[] { "revenue", "turnover" }, null, "human");
                        Check("LSDL/RPC: setSynonyms (string[] terms marshalled) applies, returns SetResult", syn != null && syn.Changed);
                        var synNote = await synWait.WaitAsync(TimeSpan.FromSeconds(5));
                        Check("LSDL/RPC: AGENT received model/didChange for the UI's setSynonyms (LSDL write broadcasts)", synNote != null);

                        // setAiDataSchema over RPC: exercise the bool `included` flag both ways + idempotency,
                        // all over the wire (this writer had NO RPC coverage at all before).
                        var ex1 = await ui.Invoke<SetResult>("setAiDataSchema", fieldRef, false, null, "human");
                        Check("LSDL/RPC: setAiDataSchema(included:false) excludes the field over the wire", ex1 != null && ex1.Changed);
                        var ex2 = await ui.Invoke<SetResult>("setAiDataSchema", fieldRef, false, null, "human");
                        Check("LSDL/RPC: setAiDataSchema re-exclude is an idempotent no-op (exclusion persisted)", ex2 != null && !ex2.Changed);
                        var inc1 = await ui.Invoke<SetResult>("setAiDataSchema", fieldRef, true, null, "human");
                        Check("LSDL/RPC: setAiDataSchema(included:true) toggles it back over the wire", inc1 != null && inc1.Changed);
                    }

                    // setAiInstructions over RPC: the model-level writer whose RPC path is the one the residual
                    // names explicitly. Prove the SetInstructionsResult DTO (Changed/Length/Note) round-trips,
                    // the length is reported, and the service-refresh caveat survives serialization.
                    const string instr = "Revenue means recognised revenue; the fiscal year starts in July.";
                    var setInstr = await ui.Invoke<SetInstructionsResult>("setAiInstructions", instr, null, "human");
                    Check("LSDL/RPC: setAiInstructions returns SetInstructionsResult with the right Length over the wire",
                        setInstr != null && setInstr.Changed && setInstr.Length == instr.Length);
                    Check("LSDL/RPC: the LSDL service-refresh caveat (Note) survives JSON-RPC serialization",
                        !string.IsNullOrEmpty(setInstr.Note) && setInstr.Note.Contains("refresh"));
                    // Shared undo timeline crosses the door: UI's undo reverts the instructions, agent sees it.
                    await ui.Invoke<UndoState>("undo");
                    var afterUndoScan = await agent.Invoke<Scorecard>("aiReadinessScan");
                    Check("LSDL/RPC: UI's undo reverts setAiInstructions on the shared timeline (DAC-AI-INSTRUCTIONS returns for the agent)",
                        afterUndoScan.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));
                }

                // --- Fabric REST ops are REACHABLE over RPC (the wire-name + arg-order contract). authMode="token"
                //     with no raw token throws in the engine BEFORE any network call, so this proves each method is
                //     wired (not a JSON-RPC method-not-found) and the args marshal — fully offline, no live tenant. ---
                string fabErr = null;
                try { await ui.Invoke<FabricWorkspace[]>("listWorkspaces", "token", null); }
                catch (Exception ex) { fabErr = ex.Message; }
                Check("Fabric/RPC: list_workspaces reachable over the wire (token-mode guard fires, not method-not-found)",
                    fabErr != null && fabErr.Contains("raw access token"));
                string fabErr2 = null;
                try { await ui.Invoke<StageItem[]>("getStageItems", "pid", "sid", "token", null); }
                catch (Exception ex) { fabErr2 = ex.Message; }
                Check("Fabric/RPC: get_stage_items marshals (pipelineId, stageId, authMode) across the wire",
                    fabErr2 != null && fabErr2.Contains("raw access token"));
                string fabErr3 = null;   // the two ops the Deploy tab actually calls
                try { await ui.Invoke<DeploymentPipeline[]>("listDeploymentPipelines", "token", null); }
                catch (Exception ex) { fabErr3 = ex.Message; }
                Check("Fabric/RPC: list_deployment_pipelines reachable over the wire", fabErr3 != null && fabErr3.Contains("raw access token"));
                string fabErr4 = null;
                try { await ui.Invoke<PipelineStage[]>("getPipelineStages", "pid", "token", null); }
                catch (Exception ex) { fabErr4 = ex.Message; }
                Check("Fabric/RPC: get_pipeline_stages marshals (pipelineId, authMode) across the wire", fabErr4 != null && fabErr4.Contains("raw access token"));
                // preview_deploy / deploy_stage are now DOOR-SAFE (review-fix): their setup reads no longer throw across
                // the wire — a failure (here the token-mode guard) is reported on the DTO's .Error. So the proof of
                // reachability + arg-marshalling is the RETURNED DTO carrying "raw access token" on .Error (not a throw).
                var prev = await ui.Invoke<DeployPreview>("previewDeploy", "p", "s0", "s1", "token", null);
                Check("Fabric/RPC: preview_deploy reachable + door-safe (.Error carries the guard, not thrown)", prev?.Error != null && prev.Error.Contains("raw access token"));
                var dsr = await ui.Invoke<DeployStageReport>("deployStage", "p", "s0", "s1", null, null, false, null, false, "token", null, "human");
                Check("Fabric/RPC: deploy_stage marshals its 11 args + is door-safe (.Error, not thrown)", dsr?.Error != null && dsr.Error.Contains("raw access token"));
                string depErr3 = null;   // history is a read that still throws (unchanged) — keep the throw-based proof
                try { await ui.Invoke<DeploymentHistoryEntry[]>("deploymentHistory", "p", "token", null); }
                catch (Exception ex) { depErr3 = ex.Message; }
                Check("Fabric/RPC: deployment_history reachable over the wire", depErr3 != null && depErr3.Contains("raw access token"));
                // Fabric Git ops over the wire — all now DOOR-SAFE (review-fix): reads + write dry-runs/commits report
                // the token-mode guard on the DTO's .Error instead of throwing, proving reachability + arg-marshalling
                // AND the rail-#2 contract (a failure is never thrown across a door). connect is reached via
                // AzureDevOps+commit=true (GitHub-without-connectionId is now refused locally, before auth).
                var gst = await ui.Invoke<FabricGitStatus>("fabricGitStatus", "w", "token", null);
                Check("Fabric/RPC: fabric_git_status reachable + door-safe (.Error, not thrown)", gst?.Error != null && gst.Error.Contains("raw access token"));
                var gcm = await ui.Invoke<FabricGitResult>("fabricGitCommit", "w", "msg", null, false, "token", null, "human");
                Check("Fabric/RPC: fabric_git_commit marshals (workspaceId, comment, items, commit, authMode) + door-safe", gcm?.Error != null && gcm.Error.Contains("raw access token"));
                var gu = await ui.Invoke<FabricGitResult>("fabricGitUpdate", "w", "PreferRemote", false, false, "token", null, "human");
                Check("Fabric/RPC: fabric_git_update marshals (workspaceId, conflictPolicy, allowOverride, commit, authMode) + door-safe", gu?.Error != null && gu.Error.Contains("raw access token"));
                var gcn = await ui.Invoke<FabricGitResult>("fabricGitConnect", "w", "AzureDevOps", "org", "proj", "repo", "main", "/", "conn-id", true, "token", null, "human");
                Check("Fabric/RPC: fabric_git_connect marshals its 12 args + door-safe", gcn?.Error != null && gcn.Error.Contains("raw access token"));
                // CI/CD ops over the wire. cicd_generate is pure file authoring — it returns a real scaffold (no token,
                // no session), proving reachability via a non-empty Files list. cicd_publish reports any failure in-band
                // (no model / bad folder / the token-mode guard) so the returned .Error proves reachability + door-safety.
                var scaf = await ui.Invoke<CicdScaffold>("cicdGenerate", "github", null, "PROD", false);
                Check("Fabric/RPC: cicd_generate reachable over the wire (returns a scaffold; pure authoring, no live write)", scaf?.Files != null && scaf.Files.Length >= 2 && scaf.Error == null);
                var pub = await ui.Invoke<CicdPublishResult>("cicdPublish", "w", "i", true, "token");
                Check("Fabric/RPC: cicd_publish marshals (workspaceId, itemId, commit, authMode) + is door-safe (.Error, not thrown)", pub?.Error != null);

                // --- Diagram-layout sidecar (D1) over the RPC door: prove saveLayout (LayoutNode[] in) + getLayout
                //     (LayoutData out) marshal AND round-trip CROSS-DOOR on the shared session (UI writes, agent reads
                //     the one engine-owned layout). Save the model to a TEMP dir first so the .semanticus/ sidecar never
                //     lands in the read-only test-data submodule; clean it up after.
                var layoutDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus_rpc_layout_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    await ui.Invoke<SaveResult>("save", layoutDir, "TMDL");          // re-anchors SourcePath into the temp dir
                    var firstTable = (await ui.Invoke<ModelGraph>("getModelGraph")).Tables.First().Name;
                    var layoutWait = agent.Notify.WaitNextLayoutAsync();             // the OTHER door should be told live
                    var saveLo = await ui.Invoke<SaveLayoutResult>("saveLayout", new[] { new LayoutNode { Name = firstTable, X = 12, Y = 34 } }, "human");
                    Check("Layout/RPC: save_layout marshals LayoutNode[] over the wire and persists the sidecar", saveLo != null && saveLo.Saved && saveLo.Count == 1);
                    var layoutNote = await layoutWait.WaitAsync(TimeSpan.FromSeconds(5));
                    Check("Layout/RPC: AGENT received layout/didChange when the UI saved layout (live dual-drive, origin tagged)",
                        layoutNote != null && layoutNote.Origin == "human" && layoutNote.Tables.Any(t => t.Name == firstTable && t.X == 12 && t.Y == 34));
                    var gotLo = await agent.Invoke<LayoutData>("getLayout");          // the OTHER client reads the same engine-owned layout
                    var node = gotLo?.Tables?.FirstOrDefault(t => t.Name == firstTable);
                    Check("Layout/RPC: get_layout returns the position to the OTHER client (dual-drive, one engine-owned layout)", node != null && node.X == 12 && node.Y == 34);
                }
                finally { try { System.IO.Directory.Delete(layoutDir, true); } catch { } }

                // --- Number time-machine (feature #3) over the RPC door: blame_value / list_value_history marshal
                //     their DTOs and answer HONESTLY on a model with no recorded history (inconclusive / 0 points)
                //     — never a throw. The verdict semantics themselves are pinned in ValueBlameTests. ---
                var blame = await ui.Invoke<BlameResult>("blameValue", measureRef, null, null, "human");
                Check("TimeMachine/RPC: blame_value round-trips and is honest with no history (ok + inconclusive)",
                    blame != null && blame.Status == "ok" && blame.Verdict == "inconclusive" && !string.IsNullOrEmpty(blame.Note));
                var vhist = await agent.Invoke<ValueHistoryResult>("listValueHistory", measureRef, null);
                Check("TimeMachine/RPC: list_value_history round-trips on the OTHER client (ok, 0 points, note says why)",
                    vhist != null && vhist.Status == "ok" && vhist.Points.Length == 0 && !string.IsNullOrEmpty(vhist.Note));

                // --- Explain This Number (feature #2) over the RPC door: explainValue marshals the
                //     ExplainFilterContext DTO + the dossier both ways and degrades honestly offline (no live
                //     connection here): metadata-only dossier, Evidence.Available=false — never a throw.
                //     (Re-find a measure: the LSDL leg switched the shared session to the modern model.) ---
                var explainRef = await FindAMeasure(ui) ?? measureRef;
                var explain = await ui.Invoke<ExplainDossier>("explainValue",
                    explainRef,
                    new ExplainFilterContext { Filters = new[] { new ExplainFilter { Column = "'Date'[Calendar Year]", Members = new[] { "2024" } } } },
                    true, null, 5, "human");
                Check("Explain/RPC: explain_value round-trips (ok, offline degrade, chain ships, TREATAS context echoed)",
                    explain != null && explain.Status == "ok" && !explain.ValueEvaluated
                    && explain.Evidence != null && !explain.Evidence.Available
                    && explain.Chain != null && explain.Evidence.Query != null && explain.Evidence.Query.Contains("TREATAS"));

                // --- export_vpax over the RPC door: the Dax.Vpax metadata export marshals + writes a real .vpax. ---
                var vpaxPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus_rpc_vpax_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".vpax");
                try
                {
                    var vpax = await ui.Invoke<VpaxExportResult>("exportVpax", vpaxPath);
                    Check("Vpax/RPC: export_vpax marshals over the wire + writes a .vpax (Exported, tables>0, file exists)",
                        vpax != null && vpax.Exported && vpax.Tables > 0 && System.IO.File.Exists(vpaxPath));
                }
                finally { try { System.IO.File.Delete(vpaxPath); } catch { } }

                Console.WriteLine();
                if (_failures == 0) { Console.WriteLine("==== M1 RPC DUAL-DRIVE: PASS ===="); return 0; }
                Console.WriteLine($"==== M1 RPC DUAL-DRIVE: {_failures} CHECK(S) FAILED ===="); return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n==== M1 RPC DUAL-DRIVE: EXCEPTION ====");
                Console.WriteLine(ex);
                return 2;
            }
            finally
            {
                ui?.Dispose();
                agent?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
            }
        }

        private static async Task<string> FindAMeasure(Client c)
        {
            var tables = await c.Invoke<TreeNode[]>("listTree", (object)null);
            foreach (var t in tables)
            {
                var children = await c.Invoke<TreeNode[]>("listTree", t.Ref);
                var m = children.FirstOrDefault(x => x.Kind == "measure");
                if (m != null) return m.Ref;
            }
            return null;
        }

        /// <summary>First measure or column on the open model (for synonym / AI-data-schema toggles).</summary>
        private static async Task<string> FindAField(Client c)
        {
            var tables = await c.Invoke<TreeNode[]>("listTree", (object)null);
            foreach (var t in tables)
            {
                var children = await c.Invoke<TreeNode[]>("listTree", t.Ref);
                var f = children.FirstOrDefault(x => x.Kind == "measure" || x.Kind == "column");
                if (f != null) return f.Ref;
            }
            return null;
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > 60 ? s.Substring(0, 60) + "..." : s);

        private static string FindTestBim() => FindTestData("AdventureWorks.bim")
            ?? throw new FileNotFoundException("Could not locate AdventureWorks.bim by walking up from " + AppContext.BaseDirectory);

        private static string FindTestData(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var candidate = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>A JSON-RPC client over the named pipe that also collects server notifications.</summary>
        private sealed class Client : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            public NotifyCollector Notify { get; }

            private Client(NamedPipeClientStream pipe, JsonRpc rpc, NotifyCollector notify)
            {
                _pipe = pipe; _rpc = rpc; Notify = notify;
            }

            public static async Task<Client> ConnectAsync(string pipeName)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                var notify = new NotifyCollector();
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.AddLocalRpcTarget(notify);
                rpc.StartListening();
                return new Client(pipe, rpc, notify);
            }

            public Task<T> Invoke<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose() { try { _rpc.Dispose(); } catch { } try { _pipe.Dispose(); } catch { } }
        }

        private sealed class NotifyCollector
        {
            private readonly object _gate = new object();
            private TaskCompletionSource<ChangeNotification> _next;
            private TaskCompletionSource<ChangePlanView> _nextPlan;
            private Func<ChangePlanView, bool> _nextPlanMatch;
            private TaskCompletionSource<ActivityEvent> _nextActivity;
            private TaskCompletionSource<LayoutChange> _nextLayout;
            public List<ChangeNotification> All { get; } = new List<ChangeNotification>();
            public List<ChangePlanView> AllPlans { get; } = new List<ChangePlanView>();
            public List<ActivityEvent> AllActivity { get; } = new List<ActivityEvent>();

            [JsonRpcMethod("model/didChange")]
            public void OnDidChange(ChangeNotification n)
            {
                lock (_gate)
                {
                    All.Add(n);
                    var t = _next; _next = null;
                    t?.TrySetResult(n);
                }
            }

            [JsonRpcMethod("plan/didChange")]
            public void OnPlanChange(ChangePlanView v)
            {
                lock (_gate)
                {
                    AllPlans.Add(v);
                    if (_nextPlan != null && (_nextPlanMatch == null || _nextPlanMatch(v)))
                    {
                        var t = _nextPlan;
                        _nextPlan = null;
                        _nextPlanMatch = null;
                        t.TrySetResult(v);
                    }
                }
            }

            [JsonRpcMethod("model/activity")]
            public void OnActivity(ActivityEvent e)
            {
                lock (_gate)
                {
                    AllActivity.Add(e);
                    var t = _nextActivity; _nextActivity = null;
                    t?.TrySetResult(e);
                }
            }

            public Task<ChangeNotification> WaitNextAsync()
            {
                lock (_gate)
                {
                    _next = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _next.Task;
                }
            }

            public Task<ChangePlanView> WaitNextPlanAsync(Func<ChangePlanView, bool> match = null)
            {
                lock (_gate)
                {
                    if (match != null)
                    {
                        var existing = AllPlans.LastOrDefault(match);
                        if (existing != null) return Task.FromResult(existing);
                    }
                    _nextPlan = new TaskCompletionSource<ChangePlanView>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _nextPlanMatch = match;
                    return _nextPlan.Task;
                }
            }

            public Task<ActivityEvent> WaitNextActivityAsync()
            {
                lock (_gate)
                {
                    _nextActivity = new TaskCompletionSource<ActivityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextActivity.Task;
                }
            }

            [JsonRpcMethod("layout/didChange")]
            public void OnLayoutChange(LayoutChange v)
            {
                lock (_gate)
                {
                    var t = _nextLayout; _nextLayout = null;
                    t?.TrySetResult(v);
                }
            }

            public Task<LayoutChange> WaitNextLayoutAsync()
            {
                lock (_gate)
                {
                    _nextLayout = new TaskCompletionSource<LayoutChange>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextLayout.Task;
                }
            }
        }
    }
}
