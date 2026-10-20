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
            // F-232: this run and every engine it starts use a scratch per-user home, never the real ~/.semanticus.
            Semanticus.Engine.SemanticusHome.IsolateForTestRun("rpcsmoke");
            var pipeName = "semanticus-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const string uiChallenge = "rpc-smoke-ui-challenge-0123456789abcdef";
            var sessions = new SessionManager();
            // Smoke harness runs as Pro so it exercises the BULK change-plan apply; the Pro GATE itself is covered
            // by Semanticus.Tests/EntitlementGateTests.
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, uiChallenge);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);

            Client ui = null, agent = null;
            try
            {
                // CRASH-01 (round 4 UAT): the owner's own front door, proven against the REAL engine binary. It runs
                // first because it needs no model and is the only leg that leaves this process.
                CheckUiChallengeArguments();

                var bim = FindTestBim();
                Console.WriteLine($"[i] pipe={pipeName}  model={Path.GetFileName(bim)}");

                ui = await Client.ConnectAsync(pipeName, "human", uiChallenge);
                agent = await Client.ConnectAsync(pipeName, "agent");
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
                // Mutation waits name origin+label (or a monotonic revision). DidChangeWaiter is a
                // consume-once cursor, so an unfiltered wait can take an own echo leftover from an
                // earlier mutation and pass the later check vacuously. Deadline stays 5s.
                var edited = (original ?? "") + " /* edit-by-ui */";
                var wait1 = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "set DAX");
                var set1 = await ui.Invoke<SetResult>("setDax", measureRef, edited);
                Check("setDax reported changed", set1.Changed);
                var n1 = await wait1.WaitAsync(TimeSpan.FromSeconds(5));
                Check("agent received model/didChange for UI edit", n1 != null);
                Check("notification references the edited measure", n1.Deltas != null && n1.Deltas.Any(d => d.Ref == measureRef));
                Check("notification revision advanced", n1.Revision == set1.Revision && n1.Revision > 0);
                var agentSees = await agent.Invoke<string>("getDax", measureRef);
                Check("AGENT reads the UI's edit on the same live session", agentSees == edited);

                // ---- REVISION FENCE (zero-dialog authoring, round 7): the header-save verified-resumption writes carry an
                // optional expectedRevision (5th positional arg). Prove the new wire shape end-to-end: a STALE revision is
                // refused engine-side even when the session id matches (the swap fence alone can't see a same-session
                // wrong-object write), and the CURRENT revision proceeds. Net-zero probe: the stale call must not mutate.
                var edited2 = "1 + 1 /* edit-by-agent */";
                var fenceInfo = await agent.Invoke<SessionInfo>("sessionInfo");
                bool staleRefused = false;
                try { await agent.Invoke<SetResult>("setDax", measureRef, edited2, "human", fenceInfo.SessionId, fenceInfo.Revision - 1); }
                catch { staleRefused = true; }
                Check("setDax with a STALE expectedRevision is refused over the wire", staleRefused);
                Check("the refused stale-revision write did not mutate", await agent.Invoke<string>("getDax", measureRef) == edited);

                // ---- DUAL-DRIVE #2: AGENT edits -> UI must see it live -------------------------
                var wait2 = ui.Notify.WaitNextAsync(n => n.Origin == "agent" && n.Label == "set DAX");
                // The CURRENT revision (+ live session id) passes both fences and lands exactly like the unfenced call.
                var set2 = await agent.Invoke<SetResult>("setDax", measureRef, edited2, "human", fenceInfo.SessionId, fenceInfo.Revision);
                var n2 = await wait2.WaitAsync(TimeSpan.FromSeconds(5));
                Check("UI received model/didChange for agent edit", n2 != null && n2.Deltas.Any(d => d.Ref == measureRef));
                var uiSees = await ui.Invoke<string>("getDax", measureRef);
                Check("UI reads the AGENT's edit on the same live session", uiSees == edited2);

                // ---- DUAL-DRIVE: the tree-paste RPC (duplicateObject + targetRef). The UI pastes a measure onto
                // ANOTHER table; the copy lands there under a collision-safe name and the AGENT sees the broadcast.
                var srcTable = measureRef.Substring("measure:".Length, measureRef.IndexOf('/') - "measure:".Length);
                var roots = await ui.Invoke<TreeNode[]>("listTree", (object)null);
                var otherTable = roots.First(t => t.Kind == "table" && t.Name != srcTable);
                // Predicate-filtered: a one-shot next-event waiter is consumed by a straggler
                // model/didChange from the previous mutation still in flight on the agent pipe
                // after that mutation's RPC response returned (RpcServer.Broadcast does not await
                // NotifyAsync). Same class as McpSmoke's workflow waiter. The deadline stays 5s.
                var waitDup = agent.Notify.WaitNextAsync(n => n.Origin == "human"
                    && (n.Label ?? "").StartsWith("duplicate ", StringComparison.Ordinal));
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

                // LEGACY wire shape: the pre-targetRef 3-arg positional call (objRef, newName, ignored origin slot).
                // The positional shape remains compatible, while connection authentication owns the real origin.
                var waitLegacy = ui.Notify.WaitNextAsync(n => n.Origin == "agent"
                    && (n.Label ?? "").StartsWith("duplicate ", StringComparison.Ordinal));
                var legacyRef = await agent.Invoke<string>("duplicateObject", measureRef, "Legacy Dup Smoke", "human");
                Check("LEGACY 3-arg duplicateObject stays in-place (3rd positional arg not misread as targetRef)",
                    legacyRef == "measure:" + srcTable + "/Legacy Dup Smoke");
                var nLegacy = await waitLegacy.WaitAsync(TimeSpan.FromSeconds(5));
                Check("LEGACY 3-arg duplicateObject cannot override the authenticated AGENT origin", nLegacy != null && nLegacy.Origin == "agent");
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

                // ---- ASK YOUR DATA over the wire: the Studio's askData and the history both doors read ----
                // The measure's own table is the split, so the reach check passes; no data model is connected, so the
                // one query shape is built and checked, not run. The agent's history read sees the UI's ask.
                var askTable = measureRef.Substring("measure:".Length, measureRef.IndexOf('/') - "measure:".Length);
                var askMeasure = measureRef.Substring(measureRef.IndexOf('/') + 1);
                var askCol = (await ui.Invoke<ColumnRow[]>("listColumns")).FirstOrDefault(c => c.Table == askTable);
                var asked = await ui.Invoke<Semanticus.Engine.Query.DataAnswer>("askData", new Semanticus.Engine.Query.DataAsk
                {
                    Measures = new[] { askMeasure }, GroupBy = askCol == null ? null : new[] { $"'{askTable}'[{askCol.Name}]" },
                });
                Check("askData over the pipe builds the one query shape and reads it back in plain words",
                    asked.Outcome == "built" && asked.Dax != null && asked.Dax.Contains("SUMMARIZECOLUMNS") && asked.Reading != null && asked.Reading.StartsWith(askMeasure));
                var askHistory = await agent.Invoke<Semanticus.Engine.Query.DataQuestionHistory>("listDataQuestions", 10);
                Check("the agent's listDataQuestions reads the UI's ask from the same history",
                    askHistory.Questions.Length >= 1 && askHistory.Questions[0].Reading == asked.Reading && askHistory.Questions[0].Origin == "human");

                // The Query notebook (1.6): the UI pins the row its ask left; the agent's queryNotebook reads the same pin.
                var uiPin = await ui.Invoke<Semanticus.Engine.Query.QueryNotebookView>("queryNotebook", "pin", asked.QuestionId);
                var agentNb = await agent.Invoke<Semanticus.Engine.Query.QueryNotebookView>("queryNotebook", "list", (string)null);
                Check("the UI's pin on a notebook row is the agent's queryNotebook read, one history for both doors",
                    uiPin.Done == "Pinned." && agentNb.Questions.Any(q => q.Id == asked.QuestionId && q.Pinned && q.InNotebook));

                // ---- DOCUMENTATION NARRATIVE: dual-drive round-trip (annotation, MutateAsync broadcast, undo) ----
                const string docCtx = "Revenue grain: one row per order line.";
                var waitDoc = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "set doc narrative");
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
                var waitDocU = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "undo");
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
                    var waitCard = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "set relationship cardinality");
                    var setCard = await ui.Invoke<SetResult>("setRelationshipCardinality", rel0.Name, target, "One", "human");
                    Check("setRelationshipCardinality returns a serializable SetResult (changed)", setCard != null && setCard.Changed);
                    await waitCard.WaitAsync(TimeSpan.FromSeconds(5));
                    var rel1 = (await agent.Invoke<ModelGraph>("getModelGraph")).Relationships.FirstOrDefault(r => r.Name == rel0.Name);
                    Check("AGENT sees the new cardinality on the shared session", rel1 != null && rel1.FromCardinality == target && rel1.ToCardinality == "One");
                    var waitCardU = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "undo");
                    await ui.Invoke<UndoState>("undo");                 // undo the cardinality change (net-zero on the undo stack)
                    await waitCardU.WaitAsync(TimeSpan.FromSeconds(5));
                    var rel2 = (await agent.Invoke<ModelGraph>("getModelGraph")).Relationships.FirstOrDefault(r => r.Name == rel0.Name);
                    Check("undo reverts the cardinality change", rel2 != null && rel2.FromCardinality == origFrom && rel2.ToCardinality == origTo);
                }

                // ---- SHARED UNDO across both drivers ------------------------------------------
                var waitU = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "undo");
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
                // Closes the documented dual-drive test gap: createLinguisticSchema / setSynonyms / setAiInstructions /
                // setAiDataSchema were WIRED on EngineRpcTarget but no smoke had ever driven them over the
                // wire — their RPC path was "asserted by construction" (a recurring honest residual). This
                // proves the args (string[] terms, the bool `included` flag, the instructions string) AND the
                // SetResult / SetInstructionsResult DTOs round-trip over JSON-RPC, each write APPLIES on the
                // shared session, and the OTHER client sees it live via model/didChange (golden rule #2).
                // LSDL is gated to CL>=1465, so this needs a modern model (AdventureWorks is 1200) — open the
                // same AllProperties.bim the AirSmoke LSDL tests use, on the SAME live session both clients share.
                // A scratch copy, never the vendored file: an edit writes the History beside the model it edits
                // (.semanticus/history.json, Plan-heart E4), and that must not land inside the submodule.
                var modern = ScratchCopy(FindTestData("AllProperties.bim"));
                if (modern == null)
                {
                    Console.WriteLine("[i] LSDL writers over RPC: AllProperties.bim not found — skipping (no modern model)");
                }
                else
                {
                    var openModern = await ui.Invoke<OpenResult>("open", modern, true);
                    Check("LSDL/RPC: opened a modern (CL>=1465) model so the linguistic schema is writable", openModern.Tables > 0);

                    // create_linguistic_schema over RPC: seeds the linguistic schema; the agent must see the SYN-SCHEMA
                    // finding clear on its own scan (proves the write crossed the session, not just the DTO).
                    var eq = await ui.Invoke<SetResult>("createLinguisticSchema", null, "human");
                    Check("LSDL/RPC: createLinguisticSchema returns a serializable SetResult and reports a change", eq != null && eq.Changed);
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
                        var synWait = agent.Notify.WaitNextAsync(n => n.Origin == "human" && n.Label == "set synonyms");
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
                //     — never a throw. The verdict semantics themselves are pinned in ValueBlameTests. Re-find a
                //     measure: the LSDL leg switched the shared session to the modern model, so the first model's
                //     ref names no measure here, and a ref with no measure and no history is refused (UAT-S05b-1-003). ---
                var historyRef = await FindAMeasure(ui) ?? measureRef;
                var blame = await ui.Invoke<BlameResult>("blameValue", historyRef, null, null, "human");
                Check("TimeMachine/RPC: blame_value round-trips and is honest with no history (ok + inconclusive)",
                    blame != null && blame.Status == "ok" && blame.Verdict == "inconclusive" && !string.IsNullOrEmpty(blame.Note));
                var vhist = await agent.Invoke<ValueHistoryResult>("listValueHistory", historyRef, null);
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

                // --- The report scope over the RPC door. The UI door must refuse exactly what the agent door
                //     refuses: a report was CHOSEN for this model and never read, so nothing here can be called
                //     unused and nothing may be swept. The sentence is the same on both doors on purpose. ---
                var scopeFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus_rpc_scope_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
                System.IO.Directory.CreateDirectory(scopeFolder);
                try
                {
                    var chosen = await ui.Invoke<Semanticus.Engine.Lineage.ReportScopeResult>("setReportScope",
                        new object[] { new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Warehouse ops", Path = scopeFolder } }, "human" });
                    Check("ReportScope/RPC: set_report_scope saves the choice and reads nothing (1 listed, 0 checked)",
                        chosen != null && chosen.Listed == 1 && chosen.Checked == 0 && chosen.Reports[0].State == "notChecked");

                    var listed = await agent.Invoke<Semanticus.Engine.Lineage.ReportScopeResult>("listReportScope");
                    Check("ReportScope/RPC: the OTHER client sees the same choice, still unread",
                        listed != null && listed.Listed == 1 && listed.Checked == 0
                        && listed.Gaps.Any(g => g == "The chosen reports still need checking."));

                    var refused = await ui.Invoke<RemoveSafeReport>("removeSafeObjects", new object[] { null, null, "human" });
                    Check("ReportScope/RPC: remove_safe_objects refuses to sweep while a chosen report is unread",
                        refused != null && refused.Count == 0
                        && refused.Note == "Nothing removed. The chosen reports still need checking.");

                    var scopedImpact = await ui.Invoke<Semanticus.Engine.Lineage.ImpactAssessmentResult>("impactAssessment",
                        new object[] { new Semanticus.Engine.Lineage.ImpactAssessmentRequest { ObjectRef = explainRef, Intent = "remove" } });
                    Check("ReportScope/RPC: the assessment says the chosen reports still need checking",
                        scopedImpact?.Checked != null && scopedImpact.Checked.ReportsListed == 1 && scopedImpact.Checked.ReportsChecked == 0
                        && scopedImpact.ReportSummary == "Report use is unknown. The chosen reports still need checking."
                        && !scopedImpact.Coverage.Any(c => c.Area == "interview"));

                    await ui.Invoke<Semanticus.Engine.Lineage.ReportScopeResult>("setReportScope",
                        new object[] { Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "human" });
                }
                finally { try { System.IO.Directory.Delete(scopeFolder, true); } catch { } }

                await HistoryInsideThePlanAsync(ui, agent, sessions);
                // ---- slice C: a model's SQL endpoint and clearing the publish destination, both clients ----------
                // The UI sets the open model's SQL endpoint; the agent client reads the same link through the same
                // connectionContext call; the agent-origin clear of a publish destination is read back as none by the UI.
                var endpointSource = SqlSourceRegistry.Save(null, "Contoso Retail warehouse",
                    "contoso-retail-sql.database.windows.net", "Warehouse", "interactive", null);
                var endpointSet = await ui.Invoke<ModelSqlEndpointResult>("setModelSqlEndpoint", endpointSource.Id, null, "human");
                var agentContext = await agent.Invoke<ConnectionContext>("connectionContext");
                Check("SqlEndpoint/RPC: the UI links the model's SQL endpoint and the agent client reads the same one",
                    endpointSet.SqlEndpoint?.SourceId == endpointSource.Id && agentContext.SqlEndpoint?.SourceId == endpointSource.Id
                    && agentContext.SqlEndpoint.Server == "contoso-retail-sql.database.windows.net");
                var agentList = await agent.Invoke<ModelSqlEndpointList>("listModelSqlEndpoints");
                Check("SqlEndpoint/RPC: listModelSqlEndpoints shows the link to both clients",
                    agentList.Rows.Any(r => r.SqlEndpoint?.SourceId == endpointSource.Id));
                var refusedDelete = await agent.Invoke<SqlSourceDeleteResult>("deleteSqlSource", endpointSource.Id, "agent");
                Check("SqlEndpoint/RPC: a source that is a model's endpoint is not removed, and the refusal names the model",
                    !refusedDelete.Deleted && refusedDelete.LinkedModels.Length == 1);
                var endpointCleared = await agent.Invoke<ModelSqlEndpointResult>("setModelSqlEndpoint", null, null, "agent");
                Check("SqlEndpoint/RPC: the agent client clears it and the UI reads none",
                    endpointCleared.SqlEndpoint == null && (await ui.Invoke<ConnectionContext>("connectionContext")).SqlEndpoint == null);

                var publishTarget = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso", "Contoso Retail", "Contoso Retail", null, "interactive");
                var publishSet = await ui.Invoke<ConnectionContext>("setPublishDestination", publishTarget.Id, "human", false);
                Check("PublishDestination/RPC: the UI links a publish destination", publishSet.Publishing.ConnectionId == publishTarget.Id);
                var publishCleared = await agent.Invoke<ConnectionContext>("setPublishDestination", null, "agent", true);
                Check("PublishDestination/RPC: the agent client clears it and the UI reads none",
                    !publishCleared.Publishing.Available && !(await ui.Invoke<ConnectionContext>("connectionContext")).Publishing.Available);

                Console.WriteLine();
                await PlanTasksSurviveARestartAsync(uiChallenge);
                Console.WriteLine();
                await TheQueueDeliversAsync(uiChallenge);
                Console.WriteLine();
                await TheDraftRunsAsync(uiChallenge);

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

        /// <summary>Plan at the heart, lane E1's finish line. A person adds two tasks with no assistant attached,
        /// holds one and moves it up; the engine restarts; an MCP client (the attached assistant's RemoteEngine,
        /// through the same McpTools method the MCP door serves) reads get_model_plan and sees the task with the
        /// same id, held, first in the order, with state word paused. The same controls from an agent connection
        /// are refused. A completed target from a pre-upgrade file migrates as a done aim with its original date.</summary>
        private static async Task PlanTasksSurviveARestartAsync(string challenge)
        {
            Console.WriteLine("---- Plan tasks across an engine restart (E1) ----");
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-e1-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(FindVendoredTestBim(), bim);
            var finished = new DateTimeOffset(2026, 9, 3, 17, 30, 0, TimeSpan.Zero);
            string heldId = null, otherId = null;
            try
            {
                // ---- the first engine: a person, and no assistant anywhere ----
                {
                    var pipe = "semanticus-smoke-e1a-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    using var sessions = new SessionManager();
                    using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
                    using var server = new RpcServer(sessions, engine, pipe, challenge);
                    using var stop = new CancellationTokenSource();
                    var serving = server.RunAsync(stop.Token);
                    using (var person = await Client.ConnectAsync(pipe, "human", challenge))
                    {
                        await person.Invoke<OpenResult>("open", bim);
                        otherId = (await person.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest { Words = "Describe the sales measures" })).ItemId;
                        heldId = (await person.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest { Words = "Add synonyms for Customer" })).ItemId;
                        await person.Invoke<ModelPlanView>("holdPlanItem", heldId, true);
                        var moved = await person.Invoke<ModelPlanView>("movePlanTaskUp", heldId);
                        Check("E1: the person added two tasks with no assistant attached",
                            server.AgentClientCount == 0 && moved.Items.Count(i => i.Id == heldId || i.Id == otherId) == 2);
                        Check("E1: Up put the held task first", moved.Items.First(i => i.Id == heldId).Order == 1);
                    }
                    stop.Cancel();
                    try { await serving; } catch { }
                }

                // A pre-upgrade entry, written the old way: a target the person finished on 3 September.
                var file = Directory.GetFiles(dir, PlanMemory.FileName, SearchOption.AllDirectories).Single();
                var whole = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
                var entry = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonObject)whole["models"]).Single().Value;
                entry["targets"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "a-marked-date-table", ["text"] = "A marked date table", ["section"] = "Structure",
                    ["when"] = finished.AddDays(-2).ToString("o"), ["doneUtc"] = finished.ToString("o"),
                });
                File.WriteAllText(file, whole.ToJsonString());

                // ---- the restarted engine: the person opens the model, the assistant attaches and reads ----
                {
                    var pipe = "semanticus-smoke-e1b-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    using var sessions = new SessionManager();
                    using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
                    using var server = new RpcServer(sessions, engine, pipe, challenge);
                    using var stop = new CancellationTokenSource();
                    var serving = server.RunAsync(stop.Token);
                    using (var person = await Client.ConnectAsync(pipe, "human", challenge))
                    using (var agentPipe = await Client.ConnectAsync(pipe, "agent"))
                    using (var remote = await RemoteEngine.ConnectAsync(pipe))
                    {
                        await person.Invoke<OpenResult>("open", bim);
                        var plan = await McpTools.GetModelPlan(remote);
                        var held = plan.Items.FirstOrDefault(i => i.Id == heldId);
                        var other = plan.Items.FirstOrDefault(i => i.Id == otherId);
                        Check("E1: after a restart the MCP read has the task under the same id", held != null && other != null);
                        Check("E1: it is still held, with state word paused",
                            held != null && held.Held && held.StateWord == PlanStateWords.Paused);
                        Check("E1: the order survived: held task 1, the other 2", held?.Order == 1 && other?.Order == 2);
                        Check("E1: a typed task's doer is your assistant", other?.Doer == PlanDoers.Assistant);

                        var aim = plan.Targets.FirstOrDefault(t => t.Text == "A marked date table");
                        var aimRow = aim == null ? null : plan.Items.FirstOrDefault(i => i.Id == aim.Id);
                        Check("E1: a completed pre-upgrade target migrated as a done aim with its original date",
                            aim != null && aim.Id.StartsWith(PlanTasks.IdPrefix, StringComparison.Ordinal)
                            && aim.DoneUtc == finished && aimRow?.StateWord == PlanStateWords.Done && aimRow.Doer == PlanDoers.You);

                        async Task<bool> Refused(Func<Task> call)
                        {
                            try { await call(); return false; }
                            catch (Exception ex) { return ex.Message.IndexOf("method", StringComparison.OrdinalIgnoreCase) >= 0; }
                        }
                        // Kane, 10 Oct 2026: the person's word in chat is enough, so the Plan page's controls are on the agent pipe too.
                        var byAgent = await agentPipe.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest { Words = "Added by Claude" });
                        var switched = await agentPipe.Invoke<ModelPlanView>("setAssistantQueue", "paused");
                        var switchedBack = await remote.SetAssistantQueueAsync("on");
                        Check("E1: an agent pipe adds a task and flips the switch, and the attached assistant's engine flips it back",
                            !string.IsNullOrEmpty(byAgent?.ItemId) && switched.AssistantQueue == PlanAssistantQueue.Paused && switchedBack.AssistantQueue == PlanAssistantQueue.On);
                        Check("E1: the Permissions page is still the person's: an agent pipe cannot change the policy",
                            await Refused(() => agentPipe.Invoke<AgentPolicy>("setAgentPolicyEnabled", false))
                            && await Refused(() => agentPipe.Invoke<ModelPlanView>("claimPlanTask", otherId)));
                        var after = await McpTools.GetModelPlan(remote);
                        Check("E1: what the agent added landed and the person's held row is unchanged",
                            after.Items.Any(i => i.Id == byAgent.ItemId) && after.Items.First(i => i.Id == heldId).Held
                            && after.AssistantQueue == PlanAssistantQueue.On);
                    }
                    stop.Cancel();
                    try { await serving; } catch { }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Plan at the heart, lane E3's finish line. A person adds two tasks on separate objects and one that
        /// waits for a Semanticus row, with no assistant attached. An MCP assistant attaches: its first call's result
        /// carries the Plan line offering the two tasks (never as independent: a free-form task's footprint is unknown,
        /// Sol 6 slice 7 finding 3) and naming the third as not offered yet; its second call
        /// carries no line; it claims one, edits, and completes. The person flips the switch to Paused: the next
        /// claim is refused with the paused sentence and the next result carries the paused line once.
        ///
        /// NOT HERE: "the edit is under that task in get_plan_history". get_plan_history and the Cause on a commit
        /// are lane E4's, which had not merged; this checks the working-on pointer the cause capture will read.</summary>
        private static async Task TheQueueDeliversAsync(string challenge)
        {
            Console.WriteLine("---- the queue delivers (E3) ----");
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-e3-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(FindVendoredTestBim(), bim);
            var pipe = "semanticus-smoke-e3-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipe, challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            try
            {
                using var person = await Client.ConnectAsync(pipe, "human", challenge);
                await person.Invoke<OpenResult>("open", bim);
                var before = await person.Invoke<ModelPlanView>("getModelPlan");
                var blocker = before.Items.FirstOrDefault(i => i.Doer == PlanDoers.Semanticus && i.FootprintKnown
                    && PlanStateWords.IsOpen(i.StateWord) && (i.Writes ?? Array.Empty<string>()).Any(w => w.Contains(':') && !w.StartsWith("model:")));
                var blocked = blocker?.Writes.First(w => w.Contains(':') && !w.StartsWith("model:"));
                Check("E3: the model carries a Semanticus row that changes an object", blocker != null);

                var first = (await person.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest
                    { Words = "Describe the smoke measure one", Objects = new[] { "measure:Smoke/One" } })).ItemId;
                var second = (await person.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest
                    { Words = "Describe the smoke measure two", Objects = new[] { "measure:Smoke/Two" } })).ItemId;
                var third = (await person.Invoke<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest
                    { Words = "Describe what the Semanticus row changes", Objects = new[] { blocked } })).ItemId;
                Check("E3: three tasks added with no assistant attached", server.AgentClientCount == 0 && third != null);

                using var remote = await RemoteEngine.ConnectAsync(pipe);   // the MCP assistant attaches
                async Task<string[]> Call(Func<Task> body = null)
                {
                    var r = await McpHealthAppender.InvokeAsync(remote, async () =>
                    {
                        if (body != null) await body();
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = new List<ModelContextProtocol.Protocol.ContentBlock> { new ModelContextProtocol.Protocol.TextContentBlock { Text = "ok" } },
                        };
                    });
                    return r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text).ToArray();
                }
                string PlanLine(string[] t) => t.SingleOrDefault(x => x.StartsWith("Plan:", StringComparison.Ordinal));

                var line = PlanLine(await Call());
                Check("E3: the first call offers two tasks and never calls free-form tasks independent",
                    line != null && line.StartsWith("Plan: 2 tasks are ready for you.\n", StringComparison.Ordinal)
                    && !line.Contains("They do not depend on each other")
                    && line.Contains("(" + first + ")") && line.Contains("(" + second + ")"));
                Check("E3: and the third as not offered yet, waiting for the Semanticus row",
                    line != null && line.Contains("Not offered yet: Describe what the Semanticus row changes. It waits for "));
                Check("E3: the second call carries no line", PlanLine(await Call()) == null);

                var claim = await McpTools.ClaimRequest(remote, first);
                Check("E3: the assistant claims one", claim.Ok && claim.Request?.ClaimToken != null);
                Check("E3: while it holds the claim, its pipe's edits point at the task",
                    Enumerable.Range(1, 32).Any(n => engine.WorkingTaskFor("rpc:" + n) == first));
                var measure = await FindAMeasure(person);
                await Call(() => McpTools.SetDescription(remote, measure, "Described while working the smoke task."));
                var done = await McpTools.CompleteRequest(remote, first, claim.Request.ClaimToken, "Described.");
                var after = await person.Invoke<ModelPlanView>("getModelPlan");
                Check("E3: it completes, and the person sees the reply",
                    done.Ok && after.Items.First(i => i.Id == first).StateWord == PlanStateWords.Done
                    && after.Items.First(i => i.Id == first).Reply == "Described.");
                // The finish line's last clause, with lane E4 merged: the edit is under that task in get_plan_history.
                var history = await McpTools.GetPlanHistory(remote);
                bool Touches(PlanHistoryRow r) => (r.Deltas ?? Array.Empty<ChangeDelta>()).Any(d => d.Ref == measure)
                                                  || (r.Children ?? Array.Empty<PlanHistoryRow>()).Any(Touches);
                var underTask = history.Rows.FirstOrDefault(r => r.Cause?.Kind == ChangeCauseKinds.Task && r.Cause.Ids.Contains(first));
                Check("E3: the edit is under that task in get_plan_history", underTask != null && Touches(underTask));

                await person.Invoke<ModelPlanView>("setAssistantQueue", "paused");
                var refused = await McpTools.ClaimRequest(remote, second);
                Check("E3: paused, the next claim is refused with the paused sentence",
                    refused.Refusal == Semanticus.Engine.Jev.HandoffRefusal.QueuePaused
                    && refused.Message == "The person paused your tasks. Do what they ask you directly.");
                var paused = PlanLine(await Call());
                Check("E3: the next result carries the paused line", paused == "Plan: the person paused your tasks. Do what they ask you directly.");
                Check("E3: once", PlanLine(await Call()) == null);
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Lane L4, both doors on one draft: the person composes in Studio, the attached assistant reads the
        /// same view, writes the part through the queue, and the approved draft runs as one change that one Undo takes
        /// back. An agent connection cannot give the person's Approve and run on a held draft.</summary>
        private static async Task TheDraftRunsAsync(string challenge)
        {
            Console.WriteLine("---- a draft on both doors (L4) ----");
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-l4-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(FindVendoredTestBim(), bim);
            var pipe = "semanticus-smoke-l4-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipe, challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            try
            {
                using var person = await Client.ConnectAsync(pipe, "human", challenge);
                await person.Invoke<OpenResult>("open", bim);
                var measure = await FindAMeasure(person);
                var table = "table:" + measure.Substring("measure:".Length, measure.IndexOf('/') - "measure:".Length);
                var steps = System.Text.Json.JsonSerializer.Serialize(new object[]
                {
                    new { id = "add", title = "Add L4 smoke count", op = "create_measure",
                          args = new { tableRef = table, name = "L4 smoke count", expression = "{{blanks.formula}}" },
                          blanks = new[] { new { name = "formula", kind = "dax", fills = "expression" } } },
                    new { id = "describe", title = "Describe L4 smoke count", op = "set_description",
                          args = new { objRef = table.Replace("table:", "measure:") + "/L4 smoke count", text = "Counts for the smoke." } },
                });
                var composed = await person.Invoke<Semanticus.Engine.Composer.DraftView>("composeTask", null, null, null, steps, "L4 smoke draft");
                Check("L4: the person composes a draft in Studio and nothing changes",
                    composed?.State == Semanticus.Engine.Composer.DraftStates.NeedsYou && composed.PlanWord == PlanStateWords.NeedsYou);

                using var remote = await RemoteEngine.ConnectAsync(pipe);   // the MCP assistant attaches
                var asAgent = await McpTools.GetDraft(remote, composed.DraftId);
                var asPerson = await person.Invoke<Semanticus.Engine.Composer.DraftView>("getDraft", composed.DraftId);
                Check("L4: both doors read the same draft view",
                    System.Text.Json.JsonSerializer.Serialize(asAgent) == System.Text.Json.JsonSerializer.Serialize(asPerson));

                var approved = await person.Invoke<Semanticus.Engine.Composer.DraftView>("runDraft", composed.DraftId);
                var part = approved.Blanks.Single(b => b.Name == "formula").RequestId;
                Check("L4: Run with your assistant records the approval and puts the part on the queue",
                    approved.Approved && approved.State == Semanticus.Engine.Composer.DraftStates.WaitingForAssistant && part != null);
                var claim = await McpTools.ClaimRequest(remote, part);
                var bad = await McpTools.CompleteRequest(remote, part, claim.Request?.ClaimToken, "SUM ( Ghost[Amount] )");
                if (bad.Refusal != Semanticus.Engine.Jev.HandoffRefusal.ChecksFailed)
                    Console.WriteLine("    claim: " + claim.Refusal + " " + claim.Message + " | complete: " + bad.Refusal + " " + bad.Message);
                Check("L4: a bad first try is refused, the check named, the claim kept",
                    bad.Refusal == Semanticus.Engine.Jev.HandoffRefusal.ChecksFailed && bad.Message.Contains("Ghost") && bad.Request?.AttemptsLeft == 2);
                var good = await McpTools.CompleteRequest(remote, part, claim.Request?.ClaimToken, "1");
                var ran = await person.Invoke<Semanticus.Engine.Composer.DraftView>("getDraft", composed.DraftId);
                var plan = await person.Invoke<ModelPlanView>("getModelPlan");
                var row = plan.Items.FirstOrDefault(i => i.Id == "draft/" + composed.DraftId);
                Check("L4: the last part passing runs the approved draft, and the Plan row agrees",
                    good.Ok && ran.State == Semanticus.Engine.Composer.DraftStates.Done && row?.StateWord == ran.PlanWord);
                var described = await person.Invoke<string>("getDax", table.Replace("table:", "measure:") + "/L4 smoke count");
                Check("L4: the person reads the measure the draft made", described == "1");

                await person.Invoke<UndoState>("undo");
                string gone = null;
                try { gone = await person.Invoke<string>("getDax", table.Replace("table:", "measure:") + "/L4 smoke count"); } catch { gone = null; }
                Check("L4: one Undo takes the whole draft back", gone == null);

                using var agentPipe = await Client.ConnectAsync(pipe, "agent");
                string refusal = null;
                try { await agentPipe.Invoke<Semanticus.Engine.Composer.DraftView>("approveDraft", composed.DraftId); }
                catch (Exception ex) { refusal = ex.Message; }
                // Kane, 10 Oct 2026: the agent pipe reaches Approve and run (the draft already ran and was undone, so the engine answers about the draft, not about a missing method).
                Check("L4: an agent connection reaches Approve and run (the method is there)",
                    refusal == null || refusal.IndexOf("method", StringComparison.OrdinalIgnoreCase) < 0);
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch { }
                try { Directory.Delete(dir, true); } catch { }
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

        /// <summary>
        /// CRASH-01 (round 4 UAT): an out-of-range --ui-challenge-stdin used to reach RpcServer's constructor
        /// unguarded, so the ArgumentException escaped Serve() and the runtime aborted the process (exit 134,
        /// "Unhandled exception", core dump). The owner must refuse with a plain message and a non-zero exit,
        /// exactly the way the missing-value case already does.
        /// </summary>
        private static void CheckUiChallengeArguments()
        {
            Check("engine refuses a short --ui-challenge-stdin without crashing", RefusesChallenge("short"));
            Check("engine refuses a long --ui-challenge-stdin without crashing", RefusesChallenge(new string('a', 129)));
            Check("engine refuses a missing --ui-challenge-stdin without crashing", RefusesChallenge(null));
        }

        /// <summary>Launches the REAL engine binary the way the VS Code extension does
        /// (<c>serve --workspace &lt;tmp&gt; --ui-challenge-stdin</c>) with one line on stdin, and requires a clean
        /// refusal: exit 2, a plain message naming the flag, and no unhandled-exception text on stderr. False on
        /// anything else, a hang included.</summary>
        private static bool RefusesChallenge(string challenge)
        {
            var dir = AppContext.BaseDirectory;
            var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "Semanticus.Engine.exe" : "Semanticus.Engine");
            var dll = Path.Combine(dir, "Semanticus.Engine.dll");
            if (!File.Exists(apphost) && !File.Exists(dll))
                throw new FileNotFoundException("The engine binary is not beside the smoke runner; build the solution first.", dll);

            var workspace = Path.Combine(Path.GetTempPath(), "semanticus-challenge-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workspace,
                };
                if (File.Exists(apphost)) psi.FileName = apphost;
                else { psi.FileName = "dotnet"; psi.ArgumentList.Add(dll); }
                psi.ArgumentList.Add("serve");
                psi.ArgumentList.Add("--workspace");
                psi.ArgumentList.Add(workspace);
                psi.ArgumentList.Add("--ui-challenge-stdin");

                using var child = System.Diagnostics.Process.Start(psi);
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderrTask = child.StandardError.ReadToEndAsync();
                try { child.StandardInput.WriteLine(challenge ?? string.Empty); child.StandardInput.Close(); }
                catch (IOException) { /* the child left before reading; its stderr still carries the verdict */ }
                var exited = child.WaitForExit(60000);
                if (!exited) { try { child.Kill(true); } catch { } return false; }
                var stderr = stderrTask.GetAwaiter().GetResult();
                _ = stdout.GetAwaiter().GetResult();   // drained so the child can never block on a full pipe

                var clean = child.ExitCode == 2
                    && stderr.Contains("--ui-challenge-stdin", StringComparison.Ordinal)
                    && !stderr.Contains("Unhandled exception", StringComparison.Ordinal);
                if (!clean)
                    Console.WriteLine($"[x] challenge {(challenge == null ? "(missing)" : challenge.Length + " chars")}: exit {child.ExitCode}; stderr {Trunc(stderr.Replace("\r", "").Replace("\n", " "))}");
                return clean;
            }
            finally { try { Directory.Delete(workspace, true); } catch { } }
        }


        // THE SMOKE OPENS A COPY. Opening the vendored AdventureWorks.bim in place writes the model's sidecar (plan
        // memory, evidence, report scope, workflow settings) into .semanticus/ beside it, which is inside the
        // TabularEditor submodule and left it dirty after every run. The copy lives in a scratch folder whose name
        // does NOT start with "semanticus-", because that prefix marks an ephemeral anchor (ExperienceLog).
        private static string _testBimCopy;
        /// <summary>Plan-heart E4 finish line, over two pipe clients on one session: the person edits, the assistant
        /// edits, the plan applies a batch; getPlanHistory from both doors gives the same three rows by you, assistant
        /// and semanticus, the batch nesting its edit under its items; undo to the person's edit takes all three back in
        /// one commit and one broadcast; and a second engine on the same file shows them under the seam with no Undo.</summary>
        private static async Task HistoryInsideThePlanAsync(Client ui, Client agent, SessionManager sessions)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-history-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                await ui.Invoke<OpenResult>("createModel", "Contoso", 1604, true);
                await ui.Invoke<string>("createTable", "Sales");
                await ui.Invoke<string>("createColumn", "table:Sales", "Amount", "Double");
                await ui.Invoke<string>("createColumn", "table:Sales", "CustomerKey", "Int64");
                await ui.Invoke<string>("createColumn", "table:Sales", "ProductKey", "Int64");
                await ui.Invoke<string>("createTable", "Customer");
                await ui.Invoke<string>("createColumn", "table:Customer", "CustomerKey", "Int64");
                await ui.Invoke<string>("createTable", "Product");
                await ui.Invoke<string>("createColumn", "table:Product", "ProductKey", "Int64");
                await ui.Invoke<string>("createRelationship", "column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true);
                await ui.Invoke<string>("createRelationship", "column:Sales/ProductKey", "column:Product/ProductKey", null, true);
                await ui.Invoke<SetResult>("setColumnMetadata", "column:Sales/ProductKey", null, "Sum", null, null);
                await ui.Invoke<string>("createMeasure", "table:Sales", "Total Sales", "SUM ( Sales[Amount] )");
                var file = Path.Combine(dir, "History.bim");
                await ui.Invoke<SaveResult>("save", file, "bim", false);

                await ui.Invoke<SetResult>("setDescription", "measure:Sales/Total Sales", "by the person");
                await agent.Invoke<SetResult>("setDescription", "measure:Sales/Total Sales", "by the assistant");
                var plan = await ui.Invoke<ModelPlanView>("getModelPlan");
                var ids = plan.Items.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)).Select(i => i.Id).Take(2).ToArray();
                foreach (var id in ids) await ui.Invoke<ModelPlanActionResult>("modelPlanAction", id, "propose");
                var ready = await ui.Invoke<ModelPlanView>("getModelPlan");
                var batch = await ui.Invoke<ApplyPlanItemsResult>("applyPlanItems",
                    new object[] { ids.Select(id => new PlanItemDigest { Id = id, Digest = ready.Items.First(i => i.Id == id).ChangeSet.Digest }).ToArray() });
                Check("History/RPC: the plan applied a batch of " + ids.Length + " items", ids.Length > 0 && batch.Performed);

                var fromUi = (await ui.Invoke<PlanHistoryView>("getPlanHistory")).Rows.Where(r => r.Kind != "decision").Take(3).ToArray();
                var fromAgent = (await agent.Invoke<PlanHistoryView>("getPlanHistory")).Rows.Where(r => r.Kind != "decision").Take(3).ToArray();
                Check("History/RPC: three rows by semanticus, assistant and you",
                    fromUi.Select(r => r.By).SequenceEqual(new[] { "semanticus", "assistant", "you" }));
                Check("History/RPC: both clients read the same three rows",
                    fromUi.Select(r => r.Id).SequenceEqual(fromAgent.Select(r => r.Id)));
                Check("History/RPC: the batch nests its edit under its items",
                    fromUi[0].Kind == "item" && fromUi[0].Children.Length == 1 && fromUi[0].Cause.Ids.OrderBy(x => x).SequenceEqual(ids.OrderBy(x => x)));

                var person = fromUi[2];
                var before = ui.Notify.All.Count;
                await agent.Invoke<UndoState>("undo", "agent", person.Id);
                await Task.Delay(500);   // RpcServer broadcasts without awaiting delivery; give a second broadcast time to show
                var walks = ui.Notify.All.Skip(before).Where(n => n.Deltas?.Length > 0).ToList();
                Check("History/RPC: undo to the person's edit is one commit and one broadcast",
                    walks.Count == 1 && walks[0].Label.StartsWith("Undo to ", StringComparison.Ordinal));
                var undone = (await ui.Invoke<PlanHistoryView>("getPlanHistory")).Rows.Where(r => r.Kind != "decision").Take(3).ToArray();
                Check("History/RPC: all three rows are undone and can be redone", undone.All(r => r.Undone && r.Redoable));
                await ui.Invoke<UndoState>("redo", "human", fromUi[0].Children[0].Id);   // a parent row walks by its edit's id

                await sessions.Ledger.FlushAsync();
                var restarted = new SessionManager();
                using (var second = new LocalEngine(restarted, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro()))
                {
                    await second.OpenAsync(file);
                    var seam = (await second.GetPlanHistoryAsync()).Rows.Where(r => r.Kind != "decision").Take(3).ToArray();
                    Check("History/RPC: after an engine restart the three rows are before this session, with no Undo",
                        seam.Length == 3 && seam.All(r => r.BeforeThisSession && !r.Undoable)
                        && seam.Select(r => r.By).SequenceEqual(new[] { "semanticus", "assistant", "you" }));
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string ScratchCopy(string source)
        {
            if (source == null) return null;
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(source));
            File.Copy(source, copy);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch { } };
            return copy;
        }

        private static string FindTestBim()
        {
            if (_testBimCopy != null) return _testBimCopy;
            var source = FindVendoredTestBim();
            var dir = Path.Combine(Path.GetTempPath(), "sem-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(source));
            File.Copy(source, copy);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch { } };
            return _testBimCopy = copy;
        }

        private static string FindVendoredTestBim() => FindTestData("AdventureWorks.bim")
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

        private static async Task ConfirmRpcRoleAsync(Stream stream)
        {
            var expected = "SEMANTICUS-RPC/1 accepted";
            var bytes = new byte[256];
            var one = new byte[1];
            var count = 0;
            while (count < bytes.Length)
            {
                if (await stream.ReadAsync(one, 0, 1) == 0) throw new EndOfStreamException("RPC server closed during role handshake.");
                if (one[0] == (byte)'\n') break;
                bytes[count++] = one[0];
            }
            if (count == bytes.Length || System.Text.Encoding.UTF8.GetString(bytes, 0, count) != expected)
                throw new InvalidDataException("RPC server rejected the role handshake.");
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

            public static async Task<Client> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                var preamble = role == "human"
                    ? $"SEMANTICUS-RPC/1 human {challenge}\n"
                    : "SEMANTICUS-RPC/1 agent\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(preamble);
                await pipe.WriteAsync(bytes, 0, bytes.Length);
                await pipe.FlushAsync();
                await ConfirmRpcRoleAsync(pipe);
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
            private readonly DidChangeWaiter _didChange = new DidChangeWaiter();
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
                lock (_gate) All.Add(n);
                _didChange.Observe(n);
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

            public Task<ChangeNotification> WaitNextAsync(Func<ChangeNotification, bool> match = null) =>
                _didChange.WaitAsync(match);

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
