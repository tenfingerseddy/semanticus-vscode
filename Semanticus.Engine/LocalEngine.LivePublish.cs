using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;
using RawTom = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    // Live publish path: DeployLiveAsync, DeployGateAsync, the deploy gate scan they share,
    // and the apply-model-diff push (including the pre-push restore-point write). Split from
    // LocalEngine.cs so later live-write work does not contend with editor work on that file.
    public sealed partial class LocalEngine
    {
        // #141: is an agent-authored override of a RED deploy gate refused? A gate that RAN and returned RED
        // (gate != null && !gate.Pass) is HUMAN-only to clear (Kane's ruling). A scanner FAILURE (gate == null) is NOT
        // this gate — it proceeds+records elsewhere; a PASSING gate needs no override. Non-human origin fails closed
        // (only an exact "human" is the authority — see AgentPolicyGuard.IsHuman). Pure + offline-unit-testable.
        internal static bool IsAgentRedOverrideRefused(DeployGate gate, string origin) =>
            gate != null && !gate.Pass && !AgentPolicyGuard.IsHuman(origin);

        // Objects edited this session. The gate scores findings on this set; leftovers become OlderWarnings.
        private readonly object _gateTouchLock = new object();
        private string _gateTouchSessionId;
        private readonly HashSet<string> _gateTouched = new HashSet<string>(StringComparer.Ordinal);
        private int _gateTrackingAttached;

        internal void AttachDeployGateTracking()
        {
            if (System.Threading.Interlocked.Exchange(ref _gateTrackingAttached, 1) != 0) return;
            _sessions.Bus.Changed += RememberDeployGateTouch;
        }

        internal void RememberDeployGateTouch(ChangeNotification n)
        {
            if (n == null) return;
            lock (_gateTouchLock)
            {
                if (!string.Equals(_gateTouchSessionId, n.SessionId, StringComparison.Ordinal))
                {
                    _gateTouched.Clear();
                    _gateTouchSessionId = n.SessionId;
                }
                if (n.Deltas == null) return;
                foreach (var d in n.Deltas)
                    if (!string.IsNullOrEmpty(d.Ref)) _gateTouched.Add(d.Ref);
            }
        }

        internal string[] SnapshotDeployGateTouched(string sessionId)
        {
            lock (_gateTouchLock)
            {
                if (!string.Equals(_gateTouchSessionId, sessionId, StringComparison.Ordinal))
                    return Array.Empty<string>();
                return _gateTouched.ToArray();
            }
        }

        public async Task<DeployReport> DeployLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human", string overrideReason = null, string confirmToken = null, string[] deleteRefs = null)
        {
            // Push the OPEN SESSION's metadata back to the live model (metadata-only, LineageTag-matched). We
            // serialize the edited session to a temp .bim WITHOUT resetting the undo checkpoint (deploying is not
            // "saving to file"), then sync it to the live model via Model.SaveChanges. Dry-run unless commit=true.
            var s = _sessions.Require();
            // Deploy-to-source: when no endpoint is given, push back to the live model this session was opened
            // from (open_live/open_local bound the non-secret coordinates), or to the durable publish destination
            // linked to a reopened working copy. All-or-nothing — an explicit endpoint uses the explicit args
            // verbatim, so a new endpoint is never silently paired with the bound database. authMode/tenant fall
            // back to the bound values: the WRITE reuses the SAME identity the model was opened with (no re-prompt —
            // like Tabular Editor), unless the caller passes an explicit authMode (the MCP door).
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                var src = s.LiveOrigin;
                if (src != null)
                {
                    endpoint = src.Endpoint;
                    database = src.Database;
                    if (string.IsNullOrWhiteSpace(tenantId)) tenantId = src.TenantId;
                    // Reuse the SAME auth the model was opened with when the caller gave none (the Save-to-Live UI
                    // passes null) — no re-prompt, like Tabular Editor: the token comes from the session's cached
                    // credential (see AcquireLiveTokenAsync), which renews silently. An explicit authMode (e.g. the
                    // MCP agent forcing serviceprincipal) is still honoured.
                    if (string.IsNullOrWhiteSpace(authMode)) authMode = src.AuthMode;
                }
                else
                {
                    var publish = (await ConnectionContextAsync())?.Publishing;
                    if (publish == null || !publish.Available || string.IsNullOrWhiteSpace(publish.Endpoint)
                        || string.IsNullOrWhiteSpace(publish.Database))
                        throw new InvalidOperationException(
                            "The model is not connected to a publish destination. Open a live model, choose a publish destination, or pass an endpoint and database explicitly.");
                    endpoint = publish.Endpoint;
                    database = publish.Database;
                    if (string.IsNullOrWhiteSpace(tenantId)) tenantId = publish.TenantId;
                    if (string.IsNullOrWhiteSpace(authMode)) authMode = publish.AuthMode;
                }
            }
            // An explicit endpoint requires an explicit dataset — deploying to an inferred "first dataset" is
            // riskier than reading one (open_live can guess; a WRITE must not), so fail clearly instead of letting
            // FindByName surface a bare "Database '' not found". The deploy-to-source branch always set a concrete
            // database above, so this only guards the explicit-endpoint call. (Runs before any auth/network.)
            var coords = ConnectionInput.Parse(endpoint, database);
            // Safe == false has TWO causes since the T-3864 normalisation correction (a credential we will not echo,
            // OR a URI authority that names two hosts), so this publish path names both through the SHARED refusal
            // instead of the old password-only sentence, which would have been a false statement about an ambiguous
            // authority. Same helper the three intake doors use, so the wording cannot drift apart again.
            var unsafeCoordinate = UnsafeCoordinateRefusal(coords);
            if (unsafeCoordinate != null) throw new ArgumentException(unsafeCoordinate, nameof(endpoint));
            endpoint = coords.Endpoint;
            database = coords.Database;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException(
                    "deploy_live: a database (dataset) name is required when you pass an explicit endpoint (only deploy-to-source with an empty endpoint can infer it).");
            // A commit without the preview token never reaches policy, the readiness gate, or a live write.
            if (commit && string.IsNullOrWhiteSpace(confirmToken))
                throw new InvalidOperationException(ReviewFence.Missing);
            // ---- Agent-permissions gate (the deploy_live governance hole, now closed). deploy_stage forbade an agent
            // promoting to prod; deploy_live never did — an agent could commit an irreversible prod overwrite and clear
            // a RED readiness gate with its own overrideReason. The connection registry now gives this endpoint the
            // label the guard needs. Human deploys are never gated here; runs before auth/network (fail fast).
            var deleteList = LiveMatchCopy.NormalizeDeleteRefs(deleteRefs);
            if (commit)
            {
                // intentBasis = the op name: a deploy_live grant cannot be spent on apply_model_diff (or any sibling
                // DeployLive op) against the same target. A ticked delete escalates to DeployDelete so a policy can
                // forbid removing live objects even where it would permit an update. The finer refinement — binding
                // the session fingerprint so an approve-then-edit-then-push re-asks — waits until the deploy pipeline
                // exposes a cheap revision.
                var cap = deleteList.Length > 0 ? AgentCapability.DeployDelete : AgentCapability.DeployLive;
                var what = deleteList.Length > 0
                    ? $"deploy the open model to {database} on {endpoint} and remove {deleteList.Length} object(s)"
                    : $"deploy the open model to {database} on {endpoint}";
                var basis = deleteList.Length == 0
                    ? "deploy_live"
                    : "deploy_live\n#del:" + string.Join("\n", deleteList);
                var refusal = GuardAgent(cap, endpoint, database, origin, isCommit: true,
                    summary: what, intentBasis: basis);
                if (refusal != null) throw new InvalidOperationException("deploy_live: " + refusal);
            }
            // ---- Accountable checkpoint (Verified Edits). A live COMMIT runs the deploy gate (readiness hard-
            // gates + blocking BPA errors) — this was THE "enforcement is theater" hole: deploy_live used to bypass
            // the gate entirely. A RED gate PAUSES the deploy with the reasons; shipping anyway takes an explicit
            // overrideReason — never a hard wall (Kane's call 2026-07-01) — and the override is appended to the
            // model's append-only audit chain BEFORE the session is serialized below, so the record travels inside
            // the very artifact it authorized. Dry-runs are never gated; runs before any auth/network (fail fast).
            DeployGate gate = null; string gateScanError = null;
            if (commit)
            {
                // The scan failing is NOT a pass — but hard-failing every deploy on a scanner bug would be a
                // wall. Middle path: proceed, and carry the scrubbed scan error into the deploy's audit record
                // so a gate-less commit is visible, never silent.
                try { gate = await DeployGateAsync(null, origin); } catch (Exception ex) { gateScanError = ex.Message; }
                if (gate != null && !gate.Pass)
                {
                    // #141 (Kane's ruling): a gate that RAN and returned RED is HUMAN-only to clear. Even inside a
                    // granted deploy window an agent-authored overrideReason is refused — the grant approved a PLAN
                    // ("push these changes"), never "ship even if the gate turns red". Checked BEFORE the missing-reason
                    // branch so an agent gets ONE clear teaching refusal, not "pass a reason" then "reason refused". A
                    // scanner FAILURE (gate == null) never enters this block — it still proceeds+records below, unchanged.
                    if (IsAgentRedOverrideRefused(gate, origin))
                        throw new InvalidOperationException(
                            "deploy_live: the deploy gate is RED (" + string.Join("; ", gate.Blockers)
                            + ") and clearing a red readiness gate is HUMAN-only. An agent cannot override it. The permission "
                            + "to deploy does not authorize clearing a failed readiness gate. Fix the blockers (apply_safe_fixes "
                            + "/ apply_fix, then re-run ai_readiness_scan), or ask the user to run deploy_live with overrideReason themselves.");
                    if (string.IsNullOrWhiteSpace(overrideReason))
                    {
                        var extra = gate.OlderWarnings > 0
                            ? " The model also has " + Semanticus.Analysis.GateBlockerCopy.OlderPhrase(gate.OlderWarnings)
                              + " this change did not cause. See them on AI Readiness."
                            : "";
                        throw new InvalidOperationException(
                            "deploy_live: blocked by the deploy gate: " + string.Join("; ", gate.Blockers)
                            + extra
                            + ". Fix the problems this change introduced, or pass overrideReason to ship anyway (the override is recorded in the model's audit trail).");
                    }
                    await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                    {
                        SessionId = s.Id, Revision = 0, Origin = origin, Op = "deploy_live",   // 0: a deploy is not a model mutation
                        Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                        // The one line a PERSON reads off this record, on both doors. It used to open "gate RED
                        // (…): override accepted to deploy to …" — three pieces of engine vocabulary in the
                        // sentence a non-technical owner has to understand, and the thing that made Kane ask what
                        // all the gate stuff meant. Same facts, plain words: what the check found (the gate's own
                        // blocker text, unedited), that the publish still went ahead because a reason was written,
                        // and where it went. NO full stop after the destination: an endpoint is an address a
                        // person may copy, and a period glued to it changes it. The machine-readable fields below
                        // are untouched.
                        Summary = $"Red safety check ({string.Join("; ", gate.Blockers)}). Published anyway with a written reason, to {endpoint}/{database}",
                        Evidence = System.Text.Json.JsonSerializer.Serialize(new { gate.Grade, gate.BpaViolations, gate.BpaBlocking, gate.Blockers }),
                    });
                }
            }
            // A LOCAL instance (Power BI Desktop, loopback endpoint) deploys with integrated Windows auth — no
            // token, no secret. A cloud XMLA endpoint needs a bearer token: acquire it FIRST (fail fast — before
            // writing any temp file). A service principal is the reliable write principal for Fabric.
            // A test hook stands in for the XMLA write, so skip token acquire the same way a local endpoint does.
            var hook = DeployLiveSyncHook;
            var local = LiveDeploy.IsLocalEndpoint(endpoint);
            var tok = hook != null || local
                ? default
                : await AcquireLiveTokenAsync(s, authMode, rawToken, tenantId, origin, endpoint, database, System.Threading.CancellationToken.None);
            var bim = Path.Combine(Path.GetTempPath(), "semanticus-deploy", Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            Directory.CreateDirectory(Path.GetDirectoryName(bim));
            try
            {
                // Serialize the edited session WITHOUT resetting the undo checkpoint (deploying is not "saving to file").
                await s.RunAsync(() =>
                {
                    s.Save(bim, SaveFormat.ModelSchemaOnly, SerializeOptions.Default, resetCheckpoint: false);
                    return true;
                });
                // Preview first so the token covers the change set, including live before-values and ticked deletes.
                // A file hash of the session BIM is not stable across two saves of the same revision.
                DeployReport Run(bool write, bool captureRestorePoint = false) =>
                    hook != null
                        ? hook(bim, endpoint, database, write, deleteList)
                        : LiveDeploy.SyncSessionToLive(bim, endpoint, database, tok.Token, tok.ExpiresOn, write,
                            explicitDeleteTargets: null, identityStrict: false, captureRestorePoint: captureRestorePoint,
                            explicitDeleteRefs: deleteList);
                var previewRep = await Task.Run(() => Run(false));
                var expectedToken = ReviewFence.Mint(s.Id, s.Revision, ReviewFence.TargetIdentity(endpoint, database),
                    ReviewFence.ChangeSetFrom(previewRep).Concat(deleteList));
                DeployReport rep;
                if (commit)
                {
                    var fence = ReviewFence.Refusal(true, confirmToken, expectedToken);
                    if (fence != null) throw new InvalidOperationException(fence);
                    // Pre-write restore point. apply_model_diff already writes one; Save to Live / deploy_live
                    // did not, so Roll back had nothing to list (D-003). Capture BEFORE the live mutation.
                    // The recorded double supplies WorkspaceSnapshotHook; production captures on the same
                    // XMLA connection inside SyncSessionToLive so a dead-endpoint test still fails as a
                    // connection error, not a missing snapshot.
                    RestorePointRecord restorePoint = null;
                    string restoreError = null;
                    if (WorkspaceSnapshotHook != null)
                    {
                        try
                        {
                            var snap = await WorkspaceSnapshotHook();
                            restorePoint = RestorePointStore.Write(endpoint, database,
                                RawTom.JsonSerializer.SerializeDatabase(snap), "deploy_live",
                                deleteList.Length > 0 ? deleteList.Length + " delete(s)" : "before the live write",
                                Array.Empty<string>(), deleteList);
                        }
                        catch (Exception ex) { restoreError = FabricRest.Scrub(ex.Message); }
                    }
                    else if (deleteList.Length > 0 && DeployLiveSnapshotHook != null)
                    {
                        try
                        {
                            var json = DeployLiveSnapshotHook();
                            if (string.IsNullOrEmpty(json))
                                restoreError = "the live snapshot was not available";
                            else
                                restorePoint = RestorePointStore.Write(endpoint, database, json, "deploy_live",
                                    deleteList.Length + " delete(s)", Array.Empty<string>(), deleteList);
                        }
                        catch (Exception ex) { restoreError = FabricRest.Scrub(ex.Message); }
                    }
                    if (deleteList.Length > 0 && restorePoint == null && (WorkspaceSnapshotHook != null || DeployLiveSnapshotHook != null))
                    {
                        return new DeployReport
                        {
                            Endpoint = endpoint, Database = database,
                            Error = LiveMatchCopy.DeleteRefusedNoRestore(restoreError ?? "the live snapshot was not available"),
                            ConfirmToken = expectedToken
                        };
                    }
                    rep = await Task.Run(() => Run(true, captureRestorePoint: restorePoint == null));
                    if (rep != null)
                    {
                        if (string.IsNullOrEmpty(rep.RestorePointId) && restorePoint != null)
                            rep.RestorePointId = restorePoint.Id;
                        if (restoreError != null && deleteList.Length == 0)
                            rep.Changes = (rep.Changes ?? Array.Empty<string>()).Append(
                                "No restore point was written (" + restoreError + "). This write cannot be rolled back.").ToArray();
                    }
                }
                else rep = previewRep;
                if (rep != null)
                {
                    rep.ConfirmToken = expectedToken;
                    if (string.IsNullOrEmpty(rep.Error))
                        rep.MatchNote = LiveMatchCopy.ForPreview(rep.Database ?? database, rep.TotalChanges, rep.LiveOnly);
                }
                // Outcome record (local only — the NEXT deploy carries it): a committed ship is an audit event
                // whether or not the gate was red. Written after the push so a failed sync never logs "deployed".
                // Caught: the deploy SUCCEEDED — throwing here (e.g. the session was replaced mid-push and its
                // dispatcher is gone) would misreport a completed live write as a failure; surface it on the
                // report instead.
                if (rep != null && rep.Committed)
                    try
                    {
                        await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                        {
                            SessionId = s.Id, Revision = 0, Origin = origin, Op = "deploy_live",   // 0: a deploy is not a model mutation
                            Verdict = "deployed",
                            Summary = $"deployed to {endpoint}/{database}: {rep.TotalChanges} change(s); gate {(gate == null ? "unavailable" : gate.Pass ? "pass" : "RED (overridden)")}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { endpoint, database, rep.TotalChanges, gatePass = gate?.Pass, blockers = gate?.Blockers, olderWarnings = gate?.OlderWarnings, gateScanError }),
                        });
                    }
                    catch (Exception ex) { rep.Changes = rep.Changes.Append("audit: deployed, but the outcome record could not be written: " + ex.Message).ToArray(); }
                return rep;
            }
            finally { try { File.Delete(bim); } catch { /* temp */ } }
        }

        // --- Test seams for the workspace (selective-push) target. Live XMLA is not exercised offline, so tests
        // inject in-memory snapshots (A then B, for the drift check) and a fake push — exercising the merge / validate
        // / drift / gate / audit legs WITHOUT a real endpoint. Null (production) = the real live path. ---
        internal Func<Task<RawTom.Database>> WorkspaceSnapshotHook;
        internal Func<string, System.Collections.Generic.IReadOnlyCollection<LiveDeleteTarget>, DeployReport> WorkspacePushHook;
        // Whole-model publish path (deploy_live / Save to Live). Tests point this at an in-memory recorded target so a
        // commit is recorded without opening TOM.Server. Null (production) = the real live path.
        internal Func<string, string, string, bool, System.Collections.Generic.IReadOnlyCollection<string>, DeployReport> DeployLiveSyncHook;
        // Pre-delete snapshot of the fake live model, used only when DeployLiveSyncHook is set. Null in production.
        internal Func<string> DeployLiveSnapshotHook;

        // apply_diff into a PUBLISHED model on an XMLA endpoint — the ALM-Toolkit-style selective push. Merge the
        // chosen diff items from `left` onto a snapshot of the live target, then push ONLY those objects. Preview + a
        // single-object and multi-object pushes are all FREE (Kane 2026-09-15), the same as the file/session paths.
        // Deployment itself is never surcharged. A drift guard runs for BOTH tiers (safety is never paywalled): if the
        // target changed under us between the diff and the commit, we REFUSE unless overrideReason is supplied (the
        // accountable override, recorded on the audit trail). Explicitly-selected Delete refs ARE pushed (real removals
        // over XMLA — ALM Toolkit / Tabular Editor do the same; absence still never deletes).
        private async Task<ApplyDiffResult> ApplyDiffToWorkspaceAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason)
        {
            if (string.IsNullOrWhiteSpace(right.Endpoint))
                return new ApplyDiffResult { Error = "The target 'workspace' ref needs an XMLA endpoint (e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace)." };
            if (string.IsNullOrWhiteSpace(right.Database))
                return new ApplyDiffResult { Error = "The target 'workspace' ref needs a database (dataset) name. A push must name its target dataset explicitly." };

            var cleanups = new System.Collections.Generic.List<Action>();
            void CleanupAll() { foreach (var c in cleanups) { try { c(); } catch { } } }
            var (ldb, llabel, lclean) = await ResolveModelRefAsync(left ?? new ModelRef { Kind = "session" }, origin);
            cleanups.Add(lclean);
            try
            {
                var authMode = string.IsNullOrWhiteSpace(right.AuthMode) ? "azcli" : right.AuthMode;
                var tlabel = right.Label ?? right.Database;

                // Snapshot the CURRENT deployed metadata (read-only), loaded as raw TOM like a file — "snapshot A".
                // Capture the EFFECTIVE auth mode it authenticated with, so the eventual push reuses that same proven
                // credential instead of re-acquiring the mode the endpoint already rejected (see PushWorkspaceAsync).
                var (adb, effectiveMode) = await SnapshotWorkspaceAsync(right.Endpoint, right.Database, authMode, cleanups, origin, right.TenantId);

                var diff = ModelCompare.Diff(ldb.Model, adb.Model, llabel, tlabel);
                var selected = selectedRefs != null && selectedRefs.Length > 0 ? new System.Collections.Generic.HashSet<string>(selectedRefs) : null;
                var selectedItems = diff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref))).ToList();
                // RETAG/REPUBLISH: a selected item that exists on both sides under a DIFFERENT lineage tag — Match emits a
                // Delete + Create pair (sharing a name-based ref). Deleting/replacing would drop the LIVE object and its
                // data (usually the model was republished under us). REFUSED (both halves) — held OUT of the push pipeline
                // (so they never count toward drift / apply) and reported. Deduped by ref.
                var republishDeleteRefs = selectedItems.Where(i => i.LikelyRepublished).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();
                string RepublishReason(string r) => r + " (refused as a likely republish: a same-named object exists on the target with a DIFFERENT lineage tag; deleting or replacing it would drop the live object and its data. This usually means the model was republished under you. Re-diff against the current model, or push the update instead.)";
                var applicableItems = selectedItems.Where(i => !i.LikelyRepublished).ToList();
                var applicable = applicableItems.Select(i => i.Ref).ToArray();
                var deleteRefs = applicableItems.Where(i => i.Action == "Delete").Select(i => i.Ref).ToArray();
                var pushRefs = applicableItems.Where(i => i.Action != "Delete").Select(i => i.Ref).ToArray();

                // The preview MUST disclose deletes distinctly — a destructive, irreversible act on a published model
                // must never be discovered after the fact.
                string DeleteNote() => deleteRefs.Length == 0 ? "" : $" {deleteRefs.Length} object(s) will be DELETED from the published model: {string.Join(", ", deleteRefs)}.";
                string RepublishNote() => republishDeleteRefs.Length == 0 ? "" : $" {republishDeleteRefs.Length} selected item(s) will be REFUSED as a likely republish (same name, different lineage tag: deleting would drop live data): {string.Join(", ", republishDeleteRefs)}.";

                if (!commit)
                {
                    var note = $"Preview: {pushRefs.Length} object(s) would be pushed to {right.Database} on {right.Endpoint}." + DeleteNote() + RepublishNote() + " Pass commit=true to push.";
                    return new ApplyDiffResult { Applied = false, Count = applicable.Length, AppliedRefs = applicable, FailedRefs = republishDeleteRefs, Target = tlabel, Note = note };
                }

                // NOTHING TO PUSH: the selection matched no pending difference the push can carry. If the ONLY selection
                // was a refused republish, say THAT (not "nothing to push"). Either way, no live write happened.
                if (applicable.Length == 0)
                {
                    if (republishDeleteRefs.Length > 0)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = republishDeleteRefs.Select(RepublishReason).ToArray(), Target = tlabel,
                            Note = "Nothing was pushed." + RepublishNote() };
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel,
                        Note = $"Nothing to push: the selection matched no pending difference between the source and {right.Database} on {right.Endpoint}." };
                }

                // ---- Agent-permissions gate. Same governance the deploy_live hole exposed, on the selective-push path.
                // A push that DELETES escalates to the delete capability, so a policy can forbid an agent deleting from
                // prod even where it would permit an update. Runs before the drift snapshot → a refusal writes nothing.
                {
                    var cap = deleteRefs.Length > 0 ? AgentCapability.DeployDelete : AgentCapability.DeployLive;
                    var what = deleteRefs.Length > 0
                        ? $"push {applicable.Length} change(s) incl. {deleteRefs.Length} delete(s) to {right.Database} on {right.Endpoint}"
                        : $"push {applicable.Length} change(s) to {right.Database} on {right.Endpoint}";
                    // intentBasis = the exact ref set (deletes distinguished): the grant authorises THESE objects. An
                    // agent that re-plans to a different selection — same count, different objects — must re-ask.
                    var basis = "apply:" + string.Join("\n", applicable.OrderBy(r => r, StringComparer.Ordinal))
                        + "\n#del:" + string.Join("\n", deleteRefs.OrderBy(r => r, StringComparer.Ordinal));
                    var refusal = GuardAgent(cap, right.Endpoint, right.Database, origin, isCommit: true, summary: what, intentBasis: basis);
                    if (refusal != null)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel, Error = refusal };
                }

                // ---- Drift guard (ALWAYS ON, both tiers). Between the diff and this commit the live target can change
                // under us. Re-snapshot ("snapshot B" = the CURRENT live state) and compare each ref we're about to
                // overwrite (adds/updates AND deletes) A-vs-B, restricted to the SELECTED refs; any non-Equal ⇒ someone
                // edited a ref we intend to change since we diffed. Deletes are irreversible on a published model, so
                // they are guarded too. Snapshot A stays only the drift BASELINE — we no longer push it (see below).
                var (bdb, _) = await SnapshotWorkspaceAsync(right.Endpoint, right.Database, authMode, cleanups, origin, right.TenantId);
                // The RESTORE POINT is snapshot B, and it must be captured HERE: ModelCompare.Apply below merges the
                // selected changes INTO bdb in place, so serializing it any later would persist the post-push state and
                // silently make rollback a no-op. Cheap (in-memory) and it also works under the offline test hook.
                var restoreJson = RawTom.JsonSerializer.SerializeDatabase(bdb);
                var applicableSet = new System.Collections.Generic.HashSet<string>(applicable, StringComparer.Ordinal);
                var driftRefs = ModelCompare.Diff(adb.Model, bdb.Model, "before", "now").Items
                    .Where(i => i.Action != "Equal" && applicableSet.Contains(i.Ref)).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();

                if (driftRefs.Length > 0 && string.IsNullOrWhiteSpace(overrideReason))
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = Array.Empty<string>(), Target = tlabel,
                        Error = "Drift guard: the target changed under you since the diff: " + string.Join(", ", driftRefs)
                            + " differ on the live model now. Nothing was pushed. Re-run to diff against the current state, or pass overrideReason to push anyway (the override is recorded in the audit trail)." };

                // CORRECTNESS: merge into snapshot B (the CURRENT live), NEVER the stale A. SyncSessionToLive diffs the
                // pushed model against the current live and syncs EVERY difference (it is selection-unaware), so pushing
                // A+selection would REVERT any UNSELECTED object a colleague changed since A — a silent lost update the
                // drift guard can't catch (it only inspects the selected refs). Merging into B makes "pushed =
                // current-live + selected changes" literally true, so SyncSessionToLive emits changes ONLY for the
                // selected objects and unrelated concurrent edits are PRESERVED — the per-object last-writer-wins policy
                // of docs/op-routing-map.md, with the drift guard as the explicit revision-guard on the objects we touch.
                // The A-diff drives the preview count (what the user previewed is what they push);
                // the B-diff drives what is actually applied.
                var bdiff = ModelCompare.Diff(ldb.Model, bdb.Model, llabel, tlabel);
                var bItems = bdiff.Items.Where(i => i.Action != "Equal" && (selected == null || selected.Contains(i.Ref))).ToList();
                // REFUSE retag/republish items (same name, different lineage tag ⇒ Match emits a Delete + Create pair
                // that SHARE a name-based ref). Both halves are excluded from the push: deleting would drop the live
                // object + its data, and the paired Create can't add a same-named object while the live one exists. The
                // safe move is to push an UPDATE or re-diff. Reported (deduped by ref) in FailedRefs.
                var republishRefsB = bItems.Where(i => i.LikelyRepublished).Select(i => i.Ref).Distinct(StringComparer.Ordinal).ToArray();
                var deleteItemsB = bItems.Where(i => i.Action == "Delete" && !i.LikelyRepublished).ToList();
                var deleteRefsB = deleteItemsB.Select(i => i.Ref).ToArray();   // ref strings — for the preview/summary/evidence only
                // IDENTITY-carrying delete targets — what the live-delete channel actually resolves by (tag-terminal),
                // NOT the ref strings (a ref is for reporting, never a resolver). Captured from the B-diff items, whose
                // Target* identity was recorded at diff time, so the removal lands on the RIGHT object on the third live
                // state SyncSessionToLive loads — never a same-named impostor.
                var deleteTargetsB = deleteItemsB.Select(LiveDeleteTarget.FromDiffItem).ToArray();
                var pushRefsB = bItems.Where(i => i.Action != "Delete" && !i.LikelyRepublished).Select(i => i.Ref).ToArray();
                // A selected ref that is now Equal on B (a colleague made the same change, or it converged) has no
                // B-diff entry — it drops out as a NO-OP; report it, don't count it as applied.
                var bApplicableSet = new System.Collections.Generic.HashSet<string>(bItems.Select(i => i.Ref), StringComparer.Ordinal);
                var noOpRefs = applicable.Where(r => !bApplicableSet.Contains(r)).ToArray();

                // Merge the selected ADDS/UPDATES into snapshot B (deletes go via the explicit channel, never merged).
                // ModelCompare.Apply's contract is now null ⇒ all, EMPTY ⇒ none (the old "empty == apply-all" loaded gun
                // is gone), so a delete-only push (pushRefsB empty) with an empty applySelected would apply nothing
                // anyway. We still skip the call when there are no adds/updates — no point building the diff apply.
                var outcome = new ModelCompare.ApplyOutcome();
                if (pushRefsB.Length > 0)
                {
                    var applySelected = new System.Collections.Generic.HashSet<string>(pushRefsB, StringComparer.Ordinal);
                    outcome = ModelCompare.Apply(ldb.Model, bdb.Model, bdiff, applySelected);
                }
                var failed = new System.Collections.Generic.List<string>(outcome.Failed.Select(f => f.Ref + " (" + f.Reason + ")"));
                // Retag/republish items are refused with a clear, actionable reason (routed to FailedRefs). Never pushed.
                foreach (var r in republishRefsB) failed.Add(RepublishReason(r));

                // BLOCKER 1 (staging-collision coupling): a relationship Delete whose paired replacement Create was SELECTED
                // but did NOT stage — an in-place re-point keeps the same endpoint pair on one side so the merged Create
                // collides with the old relationship still in snapshot B, or the Create was otherwise refused — must NOT
                // reach the explicit-delete channel: removing the old relationship with no replacement is data loss. Abort
                // the whole push (atomic, like every delete refusal). A re-point that DID stage (its new endpoints resolve
                // in B) is carried, and its deploy-time failure is caught downstream by SyncSessionToLive's own coupling.
                // ROUND 6: this now also covers a CROSS-TABLE re-point (moved endpoint on a different table, or both endpoints
                // moving) that keeps the relationship NAME. ModelCompare couples such a pair by shared name (SameRelName), so
                // its Delete carries ReplacementCreateRefs; the merged Create still collides on the duplicate name in snapshot
                // B and lands in outcome.Failed (not stagedOk), so the check below refuses the Delete — no separate same-name
                // dependency is needed here because the coupling makes THIS guard fire on exactly that duplicate-name failure.
                var stagedOk = new System.Collections.Generic.HashSet<string>(outcome.Applied, StringComparer.Ordinal);
                var pushRefSet = new System.Collections.Generic.HashSet<string>(pushRefsB, StringComparer.Ordinal);
                // A delete is refused if ANY of its required replacement Creates was selected in this push (pushRefSet) but
                // failed to stage (not in stagedOk). One ref for a one-to-one re-point; the whole candidate group for an
                // ambiguous set (then the delete is safe only if every sibling replacement staged too — fail-closed).
                var replacementUnstaged = deleteItemsB
                    .Where(di => di.ReplacementCreateRefs != null && di.ReplacementCreateRefs.Any(rc =>
                                 pushRefSet.Contains(rc)          // the replacement Create was in this selection
                                 && !stagedOk.Contains(rc)))      // ...but it failed to stage
                    .Select(di => di.Ref).Distinct(StringComparer.Ordinal).ToArray();
                if (replacementUnstaged.Length > 0)
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(),
                        FailedRefs = failed.Concat(replacementUnstaged.Select(r => r + " (delete refused: its replacement relationship could not be staged in this push; removing the old one would leave no relationship. Re-diff.)")).ToArray(),
                        Target = tlabel, Note = $"Nothing reached {right.Database} on {right.Endpoint}. Delete refused (nothing pushed): {string.Join(", ", replacementUnstaged)}.",
                        Error = "Delete refused to avoid data loss: " + string.Join(", ", replacementUnstaged) + ". The replacement relationship could not be staged (its old form still occupies the model), so deleting the old one would leave no relationship. NOTHING was pushed. Re-diff against the current model." };

                // Validate the MERGED model in memory BEFORE any live write — the invariant the file branch holds: a
                // corrupt merge (an orphaned reference from a partial selection) throws here and we refuse to write.
                try { _ = RawTom.JsonSerializer.SerializeDatabase(bdb); }
                catch (Exception vex)
                {
                    var m = vex.Message; if (m.Length > 300) m = m.Substring(0, 300) + "…";
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = outcome.Applied.ToArray(), FailedRefs = failed.ToArray(), Target = tlabel,
                        Error = "The merge produced an invalid model (" + m + "). The target was NOT written. Re-run with a selection whose dependencies are included." };
                }

                // Serialize the merged model (current-live + selected changes) to a temp .bim and push. SyncSessionToLive
                // now emits ADDS/UPDATES only for the selected objects (everything else already matches live); the
                // explicit delete refs remove exactly the ticked objects — all inside one SaveChanges.
                var bim = CreatePushStagingPath(cleanups);
                System.IO.File.WriteAllText(bim, RawTom.JsonSerializer.SerializeDatabase(bdb));

                // ---- Accountable drift override (recorded BEFORE the push). Kane ratified the override as ACCOUNTABLE:
                // you cannot ship past the drift guard without its reason on the audit trail. This MATCHES deploy_live,
                // which appends the override record BEFORE it deploys, awaited and un-swallowed, so a failed append means
                // nothing ships. The prior post-success record here could be silently dropped — no session, or the audit
                // write threw — leaving an override with no record. So: if an override is in play (driftRefs.Length > 0)
                // we require an open session to carry the trail and write the "overridden" record NOW; if there's no
                // session, or the record can't be written, we REFUSE the push rather than mutate production
                // un-accountably. (Trade-off vs the old "no phantom override": if the push then fails, a recorded-but-
                // unshipped override can exist — the same property deploy_live accepts. An unrecorded SHIPPED override is
                // the worse outcome, so we prefer this. The non-override "deployed" record still lands post-success below,
                // where a failed audit write can't misreport an already-succeeded push. A non-override push needs no
                // session — nothing accountable to record before it — and proceeds as before.)
                if (driftRefs.Length > 0)
                {
                    var osess = _sessions.Current;
                    if (osess == null)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                            Error = "Drift override refused: an accountable override needs an open session to record its reason on the audit trail, and none is open. Nothing was pushed. Open the model (open_live / open_local) and retry, or re-run to diff against the current state." };
                    try
                    {
                        await RecordVerifiedEditAsync(osess, new VerifiedEditRecord
                        {
                            SessionId = osess.Id, Revision = 0, Origin = origin, Op = "apply_model_diff",   // 0: a push is not a local model mutation
                            Verdict = "overridden", OverrideReason = overrideReason.Trim(),
                            Summary = $"drift override: pushing {pushRefsB.Length + deleteRefsB.Length} selected change(s) to {right.Endpoint}/{right.Database} despite drift on: {string.Join(", ", driftRefs)}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { right.Endpoint, right.Database, drifted = driftRefs, willPush = pushRefsB, willDelete = deleteRefsB }),
                        });
                    }
                    catch (Exception ex)
                    {
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                            Error = "Drift override refused: its reason could not be recorded on the audit trail (" + FabricRest.Scrub(ex.Message) + "). Refusing to push an unrecorded override. Nothing was pushed." };
                    }
                }

                // ---- Pre-push RESTORE POINT. A live delete is PERMANENT: RemoveExplicit removes 11 object kinds and
                // SyncModels can only ever add back measures / calc columns / calc tables / named expressions, so a
                // relationship, role, perspective, hierarchy, partition, culture, datasource or data table is otherwise
                // gone for good. Kane's rule (2026-07-09): NO RESTORE POINT, NO DELETE — fail closed. A push with no
                // deletes is recoverable by re-pushing, so there a failed restore point is a warning, not a refusal.
                RestorePointRecord restorePoint = null;
                string restoreError = null;
                try
                {
                    restorePoint = RestorePointStore.Write(right.Endpoint, right.Database, restoreJson, "apply_model_diff",
                        $"{pushRefsB.Length} change(s), {deleteRefsB.Length} delete(s)", pushRefsB, deleteRefsB);
                }
                catch (Exception ex) { restoreError = FabricRest.Scrub(ex.Message); }

                if (restorePoint == null && deleteTargetsB.Length > 0)
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel,
                        Error = "Delete refused: a restore point could not be written (" + (restoreError ?? "unknown error")
                            + "), and a delete on a published model cannot be undone without one. Nothing was pushed. Free some disk space under ~/.semanticus/restore and retry, or de-select the deletes to push the remaining changes." };

                var rep = await PushWorkspaceAsync(bim, right.Endpoint, right.Database, effectiveMode, deleteTargetsB, DeployGuard.IsAgent(origin), right.TenantId);   // reuse the snapshot's effective mode (P2-5); tenant threaded (HIGH 6); non-interactive for an agent (round-3)
                // NOTHING committed (SaveChanges wrote nothing) AND an error ⇒ a true failure; never claim success
                // (surface it verbatim, incl. a rejected delete-of-a-table-with-dependents, which SaveChanges refuses
                // atomically). CRITICAL: this branches on rep.Committed, NOT rep.Error. SyncSessionToLive commits in TWO
                // steps — the metadata SaveChanges (sets Committed=true), then a SECOND SaveChanges that recalcs new
                // calc-tables. If the recalc fails, rep.Error is set BUT rep.Committed stays TRUE and the metadata is
                // ALREADY LIVE. Returning "nothing applied" there would understate a production mutation (the worst
                // tool-result-contract violation) and skip the audit record. So a partial success (Committed==true WITH
                // an Error) falls THROUGH to the normal reconciliation + audit below; its recalc warning is surfaced on
                // the result at the end. Only a genuinely-nothing-written failure returns here.
                if (rep != null && !rep.Committed && rep.Error != null)
                {
                    // A delete-refusal abort (drift, or a conflict/replacement) carries its refused refs on the report.
                    // The old return surfaced them ONLY in the free-form Error; the contract's structured fields
                    // (FailedRefs + Note) were left empty. Populate both so a caller reading the fields sees the refused
                    // refs, not just the prose. Nothing was committed, so Applied stays false.
                    var refusedAll = (rep.DeletesRefused ?? Array.Empty<string>()).Concat(rep.DeletesRefusedConflict ?? Array.Empty<string>()).ToArray();
                    if (refusedAll.Length == 0)
                        return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = failed.ToArray(), Target = tlabel, Error = rep.Error };
                    var abortFailed = new System.Collections.Generic.List<string>(failed);
                    foreach (var rr in rep.DeletesRefused ?? Array.Empty<string>())
                        abortFailed.Add(rr + " (delete refused: identity gone and a different object now bears the name; re-diff)");
                    foreach (var rr in rep.DeletesRefusedConflict ?? Array.Empty<string>())
                        abortFailed.Add(rr + " (delete refused: its replacement was not created, or the same object was just updated; re-diff)");
                    var abortNote = $"Nothing reached {right.Database} on {right.Endpoint}. Delete refused (nothing pushed): {string.Join(", ", refusedAll)}.";
                    return new ApplyDiffResult { Applied = false, Count = 0, AppliedRefs = Array.Empty<string>(), FailedRefs = abortFailed.ToArray(), Target = tlabel, Note = abortNote, Error = rep.Error };
                }

                // RECONCILE the local merge against what the live deploy ACTUALLY synced. outcome.Applied is only what
                // merged into the temp .bim; SyncSessionToLive carries a SUBSET of object types (e.g. relationships /
                // roles / perspectives aren't pushed by a metadata deploy) and reports others as Unmatched / Conflicts.
                // A merged ref counts as APPLIED only if the deploy synced it (rep.SyncedRefs) AND it didn't surface as
                // unsynced. Everything else the merge claimed moves to FailedRefs with the deploy's own reason — the
                // result must describe the LIVE model, never overstate success (docs/harness-engineering.md contract).
                var syncedSet = new System.Collections.Generic.HashSet<string>(rep?.SyncedRefs ?? Array.Empty<string>(), StringComparer.Ordinal);
                var unsyncedReports = (rep?.Unmatched ?? Array.Empty<string>()).Concat(rep?.Conflicts ?? Array.Empty<string>()).ToArray();
                var confirmed = new System.Collections.Generic.List<string>();
                foreach (var appliedRef in outcome.Applied)
                {
                    var reportEntry = unsyncedReports.FirstOrDefault(e => DeployEntryConcernsRef(e, appliedRef));
                    if (reportEntry != null)
                        failed.Add(appliedRef + " (live deploy did not sync it: " + reportEntry + ")");
                    else if (syncedSet.Contains(appliedRef))
                        confirmed.Add(appliedRef);
                    else
                        failed.Add(appliedRef + " (merged locally but the live deploy did not carry it: a metadata deploy doesn't push this object type; deploy it via TMDL/XMLA)");
                }

                // A REFUSED delete (the ticked object's identity is gone AND a different object now bears its name — the
                // live-delete channel refused to delete the wrong object) is surfaced as a Failed ref with a clear,
                // actionable reason. It is NOT an applied change and NOT a silent no-op.
                foreach (var rr in rep?.DeletesRefused ?? Array.Empty<string>())
                    failed.Add(rr + " (delete refused: the object's lineage identity no longer resolves on the live model and a different object now bears its name; re-diff before deleting)");

                var appliedCount = confirmed.Count + (rep?.Deleted ?? 0);
                var appliedRefs = confirmed.Concat(rep?.DeletedRefs ?? Array.Empty<string>()).ToArray();

                // Outcome record for a NON-override push (Verdict=deployed). The override's accountable "overridden"
                // record was written BEFORE the push (above); this post-success record is SKIPPED for the override path,
                // so a failed override push leaves no phantom "deployed". A failed audit write here can't misreport the
                // push — it already succeeded — so it's swallowed. Evidence records what CONFIRMED-synced, not what
                // merely merged locally.
                var sess = _sessions.Current;
                if (driftRefs.Length == 0 && sess != null && rep != null && rep.Committed)
                    try
                    {
                        await RecordVerifiedEditAsync(sess, new VerifiedEditRecord
                        {
                            SessionId = sess.Id, Revision = 0, Origin = origin, Op = "apply_model_diff",   // 0: a push is not a local model mutation
                            Verdict = "deployed",
                            Summary = $"pushed {rep.TotalChanges} change(s) to {right.Endpoint}/{right.Database}",
                            Evidence = System.Text.Json.JsonSerializer.Serialize(new { right.Endpoint, right.Database, rep.TotalChanges, pushed = confirmed, deleted = rep.DeletedRefs, deletesRefused = rep.DeletesRefused, noOp = noOpRefs, notSynced = failed }),
                        });
                    }
                    catch { /* the push succeeded; a failed audit write must not misreport it */ }

                // APPLIED is truthful — tied to the deploy: either it committed real change(s), or (nothing was left to
                // change AND nothing failed) it's a clean, verified no-op. A push where everything the merge claimed
                // failed to reach live is NOT a success.
                var applied = appliedCount > 0 ? (rep?.Committed == true) : failed.Count == 0;
                string noteOut;
                if (appliedCount > 0)
                    noteOut = $"Pushed {appliedCount} change(s) ({rep?.TotalChanges ?? 0} live edit(s)) to {right.Database} on {right.Endpoint}.";
                else if (failed.Count > 0)
                    noteOut = $"Nothing reached {right.Database} on {right.Endpoint}.";
                else
                    noteOut = $"No changes were needed on {right.Database} on {right.Endpoint} (already reconciled).";
                if ((rep?.DeletedRefs.Length ?? 0) > 0) noteOut += $" Deleted: {string.Join(", ", rep.DeletedRefs)}.";
                if ((rep?.DeletesAlreadyAbsent.Length ?? 0) > 0) noteOut += $" Already absent (no-op): {string.Join(", ", rep.DeletesAlreadyAbsent)}.";
                if ((rep?.DeletesRefused.Length ?? 0) > 0) noteOut += $" DELETE REFUSED (identity gone and a different object now bears the name; re-diff): {string.Join(", ", rep.DeletesRefused)}.";
                if ((rep?.DeletesRefusedConflict.Length ?? 0) > 0) noteOut += $" DELETE REFUSED to avoid data loss (its replacement was not created, or the same object was just updated; re-diff): {string.Join(", ", rep.DeletesRefusedConflict)}.";
                if (noOpRefs.Length > 0) noteOut += $" Already reconciled on the target since the diff. No-op ({noOpRefs.Length}): {string.Join(", ", noOpRefs)}.";
                if (failed.Count > 0) noteOut += " Not synced: " + string.Join("; ", failed);
                if (driftRefs.Length > 0) noteOut += $" Drift override accepted for: {string.Join(", ", driftRefs)}.";
                // Tell the caller how to undo what they just did — a restore point nobody knows about is not a safety net.
                if (restorePoint != null && applied) noteOut += $" Undo this with rollback_push('{restorePoint.Id}').";
                else if (restoreError != null) noteOut += $" No restore point was written ({restoreError}). This push cannot be rolled back.";
                // PARTIAL SUCCESS (A1): metadata committed but the calc-table recalc failed — the metadata IS live, so
                // Applied stays truthful, but the recalc warning is surfaced PROMINENTLY (Note + Error) so the caller
                // knows a new calc table exists-but-is-empty and needs a refresh. Never dropped.
                var partialErr = (rep != null && rep.Committed && rep.Error != null) ? rep.Error : null;
                if (partialErr != null) noteOut += " Warning: " + partialErr;
                return new ApplyDiffResult { Applied = applied, Count = appliedCount, AppliedRefs = appliedRefs, FailedRefs = failed.ToArray(), Target = tlabel, Note = noteOut, Error = partialErr,
                    RestorePointId = applied ? restorePoint?.Id : null };
            }
            finally { CleanupAll(); }
        }

        // The deploy gate (Semanticus's wedge): BPA + AI-readiness + (optionally) the pending-change count vs a
        // target. Read-only. Findings this change introduces or touches can block. Whole-model leftovers become
        // OlderWarnings (a count with a pointer to AI Readiness), never a block of their own (D-005).
        public async Task<DeployGate> DeployGateAsync(ModelRef compareTarget, string origin)
        {
            var session = _sessions.Require();
            var changed = SnapshotDeployGateTouched(session.Id);
            var wholeModel = DeployGateScope.IsWholeModelSession(session.SourcePath, session.LiveOrigin)
                || DeployGateScope.ForcesWholeModel(changed);
            var bpa = await BpaScanAsync();
            var card = await AiReadinessScanAsync();
            var blockers = new System.Collections.Generic.List<string>();
            var older = 0;
            if (card.GatedBy != null)
            {
                foreach (var g in card.GatedBy)
                {
                    if (DeployGateScope.ReadinessGateTouches(g, card.Findings, changed, wholeModel))
                        blockers.Add(g);
                    else
                        older++;
                }
            }
            // Error-severity BPA violations that can't be auto-fixed also block — a hard error shipped is a real risk.
            // WAIVED violations don't count: a waiver is an audited, reasoned acceptance (surfaced, never hidden), and a
            // gate that re-litigates it defeats the waiver lane. Readiness hard-gates (GatedBy above) stay RAW on
            // purpose — those are physical floors the waiver doctrine says you can't accept your way past.
            // ONE predicate, evaluated ONCE, and both the count and the message are built from that same list. It used
            // to be counted here and described by a bare "{n} blocking BPA error(s)" string, which named no rule, no
            // object and no action, and called a severity-2 warning an error (Bpa.cs:17 documents 2 as a warning).
            // Deriving the copy from the list the count came from is what stops the number and the message drifting.
            // The predicate itself is UNCHANGED: severity 2 still blocks, and GateBlockerCopy filters nothing.
            // D-005 scopes the set to objects this change introduces or touches; the rest become OlderWarnings.
            var blockingSet = bpa.Violations?.Where(v => v.Severity >= 2 && !v.CanAutoFix && !v.Waived).ToList()
                              ?? new System.Collections.Generic.List<Semanticus.Analysis.BpaViolation>();
            var scopedBlocking = blockingSet.Where(v => DeployGateScope.Touches(v.ObjectRef, changed, wholeModel)).ToList();
            older += blockingSet.Count - scopedBlocking.Count;
            var blocking = scopedBlocking.Count;
            var waived = bpa.Violations?.Count(v => v.Severity >= 2 && !v.CanAutoFix && v.Waived) ?? 0;
            if (blocking > 0) blockers.Add(Semanticus.Analysis.GateBlockerCopy.ForBlockingViolations(scopedBlocking));
            // A rule the scan could NOT evaluate is not a clean rule. Its evaluation aborted part way through a scope,
            // so the violations listed for it are a prefix and the rest of that scope is unknown — passing the gate on
            // that basis would be the gate reading a truncated scan as a green one. Only unknowns whose rule would
            // itself have blocked count here (BlocksGate mirrors this method's own Severity >= 2 && !CanAutoFix
            // predicate; an unknown's CanAutoFix is always false, so for them it reduces to Severity >= 2, because a fix
            // cannot repair coverage, and nothing applies one to an unknown in any case), and a waiver cannot clear
            // one either: you can accept a finding you can see, not one nobody checked.
            // Same shape as the violation half: one filter, and the copy is rendered from the very list that was
            // counted. The old message named the rule by its raw ID and carried our internal doctrine phrase
            // "coverage unknown, not clean"; GateBlockerCopy gives the rule's NAME and says the same thing in words a
            // person can act on, including that a waiver cannot clear one.
            var unknownSet = bpa.Unknowns?.Where(u => u.BlocksGate).ToList()
                             ?? new System.Collections.Generic.List<Semanticus.Analysis.BpaRuleUnknown>();
            var scopedUnknown = unknownSet.Where(u => DeployGateScope.UnknownTouches(u.Scope, changed, wholeModel)).ToList();
            older += unknownSet.Count - scopedUnknown.Count;
            var unknownBlocking = scopedUnknown.Count;
            if (unknownBlocking > 0) blockers.Add(Semanticus.Analysis.GateBlockerCopy.ForBlockingUnknowns(scopedUnknown));
            var changes = 0;
            if (compareTarget != null)
            {
                // Thread the CALLER's origin (T163 round 3). This used to hardcode "human", defended by the claim that a
                // compareTarget only ever arrives from the Studio because the MCP deploy_gate passes null. The MCP half was
                // true, but RemoteEngine IS the agent proxy: it handshakes as RpcConnectionRole.Agent, so an agent RPC
                // client could pass a device-code workspace target and the compare signed in interactively. Swallowing the
                // later failure never prevented the prompt, only hid it.
                try { var d = await CompareModelsAsync(new ModelRef { Kind = "session" }, compareTarget, false, origin); changes = d.Created + d.Updated + d.Deleted; }
                catch { /* compare target unavailable — gate still reports BPA/readiness */ }
            }
            // The interview leg is ADVISORY by contract: it replays the saved question pack (offline-honest —
            // Unverified is the ceiling, never a fabricated pass) and reports per-question deltas vs the last
            // recorded outcomes, but it NEVER lands in Pass/Blockers and its failure never breaks the gate.
            InterviewGateAdvisory interview = null;
            try { interview = await InterviewGateAdvisoryAsync(); }
            catch { /* advisory only — a broken pack/store must not block or distort the gate verdict */ }
            return new DeployGate
            {
                Pass = blockers.Count == 0,
                Grade = card.Grade,
                BpaViolations = bpa.ViolationCount,
                BpaBlocking = blocking,
                BpaWaivedBlocking = waived,
                BpaUnknownBlocking = unknownBlocking,
                Blockers = blockers.ToArray(),
                OlderWarnings = older,
                CheckLine = Semanticus.Analysis.GateBlockerCopy.CheckLine(blockers, older),
                Changes = changes,
                Interview = interview,
                // The waived sentence said "error-severity BPA finding(s)", which was wrong twice over: "BPA" is engine
                // vocabulary the UI may not speak, and the waived population is the same Severity >= 2 set, so most of
                // it is warnings. GateBlockerCopy.WaivedNote says "blocking finding(s)", which is what they are.
                Note = (blockers.Count == 0 ? "Gate passed." : "Gate blocked: " + string.Join("; ", blockers))
                     + Semanticus.Analysis.GateBlockerCopy.WaivedNote(waived),
            };
        }

    }
}
