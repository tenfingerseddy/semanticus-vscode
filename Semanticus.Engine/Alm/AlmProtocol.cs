using System;

namespace Semanticus.Engine
{
    // ============================================================================================
    // Wire DTOs for the ALM lane (source control + model compare + deploy). Source control here is
    // the LOCAL git CLI (Kane's decision): the engine shells `git` over the open model's working dir
    // so the user's own git config / credential helper / SSH drive auth. The OUTWARD-FACING / remote
    // writes are explicitly gated — commit defaults to a dry-run preview (commit=false) and push needs
    // confirm=true. The local working-tree verbs (checkout / pull --ff-only / branch) run directly and
    // rely on git's own safety (it aborts on a dirty tree / a non-fast-forward).
    // ============================================================================================

    public sealed class GitFileChange
    {
        public string Path { get; set; }
        public string Status { get; set; }   // M | A | D | R | C | U | ?? (untracked)
        public bool Staged { get; set; }      // true if the change is in the index (staged)
        public bool Worktree { get; set; }    // true if there's an unstaged worktree change
    }

    public sealed class GitStatus
    {
        public bool IsRepo { get; set; }
        public string RepoRoot { get; set; }      // the repository top-level (null when not a repo)
        public string WorkingDir { get; set; }    // the model's directory the ops run in
        public string Branch { get; set; }        // current branch (or null when detached)
        public bool Detached { get; set; }
        public string Upstream { get; set; }      // tracking branch, e.g. origin/main
        public int Ahead { get; set; }
        public int Behind { get; set; }
        public GitFileChange[] Files { get; set; } = Array.Empty<GitFileChange>();
        public bool ModelDirty { get; set; }      // the open session has unsaved in-memory edits (save before commit)
        public string Note { get; set; }          // human-readable hint (e.g. "not a git repo", "git not found")
    }

    public sealed class GitCommitResult
    {
        public bool Committed { get; set; }
        public string Hash { get; set; }
        public string Message { get; set; }
        public string[] Files { get; set; } = Array.Empty<string>();   // the files that would be / were committed
        public bool SavedModelFirst { get; set; }                      // the dirty session was saved to disk before commit
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>A durable Edit History marker. Git is the store: checkpoint metadata lives in commit trailers,
    /// so the repository remains the source of truth and a clone carries the same restore history.</summary>
    public sealed class HistoryCheckpoint
    {
        public string Hash { get; set; }
        public string ShortHash { get; set; }
        public string Label { get; set; }
        public string Author { get; set; }
        public string When { get; set; }
        public string ModelPath { get; set; }
        public string SessionId { get; set; }
        public long Revision { get; set; }
        public string AuditHead { get; set; }
    }

    public sealed class HistoryCheckpointList
    {
        public bool Supported { get; set; }
        public string Branch { get; set; }
        public bool ModelDirty { get; set; }
        public HistoryCheckpoint[] Checkpoints { get; set; } = Array.Empty<HistoryCheckpoint>();
        public string[] OwnedPaths { get; set; } = Array.Empty<string>();
        public string Note { get; set; }
    }

    public sealed class HistoryCheckpointResult
    {
        public bool Preview { get; set; }
        public bool Committed { get; set; }
        public HistoryCheckpoint Checkpoint { get; set; }
        public string[] Files { get; set; } = Array.Empty<string>();
        public bool SavedModelFirst { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class HistoryRestoreResult
    {
        public bool Preview { get; set; }
        public bool Restored { get; set; }
        public HistoryCheckpoint Target { get; set; }
        public HistoryCheckpoint RescueCheckpoint { get; set; }
        public HistoryCheckpoint RestoredCheckpoint { get; set; }
        public string[] Paths { get; set; } = Array.Empty<string>();
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class GitLogEntry
    {
        public string Hash { get; set; }
        public string ShortHash { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string Subject { get; set; }
    }

    public sealed class GitDiffResult
    {
        public string Path { get; set; }     // null = whole working tree
        public string Text { get; set; }      // unified text diff (the on-disk TMDL diff)
        public bool Empty { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of a git action (branch / checkout / pull / push / clone). A worktree-changing action
    /// (checkout/pull) leaves the open in-memory model STALE vs disk — <see cref="ModelReloadNeeded"/> flags it.</summary>
    public sealed class GitActionResult
    {
        public bool Ok { get; set; }
        public string Output { get; set; }   // trimmed, credential-scrubbed git stdout/stderr; auth stays out-of-band
        public string Error { get; set; }
        public string Branch { get; set; }    // resulting branch where relevant
        public string ModelPath { get; set; } // clone result, or the open model to reopen when ModelReloadNeeded
        public bool ModelReloadNeeded { get; set; }   // the on-disk model changed; reopen to load it
    }

    // ---- Model compare (ALM Toolkit / BISM Normalizer grade) ----------------------------------------

    /// <summary>Selects a model to load for a compare. Phase 1 supports session/file/gitref (offline); workspace
    /// (live XMLA snapshot) lands with the Fabric/cloud lanes.</summary>
    public sealed class ModelRef
    {
        public string Kind { get; set; }       // session | file | gitref | workspace
        public string Path { get; set; }        // file/.bim or TMDL/PBIP folder (file, gitref base)
        public string GitRef { get; set; }      // commit / branch / tag (gitref)
        public string Endpoint { get; set; }    // XMLA endpoint (workspace)
        public string Database { get; set; }    // dataset (workspace)
        public string AuthMode { get; set; }    // entra mode (workspace)
        public string Label { get; set; }       // display label
    }

    /// <summary>One object-level difference. <see cref="Action"/> is from the perspective of "make RIGHT (target)
    /// match LEFT (source)": Create = add the left object to the target; Delete = remove the right-only object;
    /// Update = the matched objects differ; Equal = identical.</summary>
    public sealed class ModelDiffItem
    {
        public string Ref { get; set; }         // table:Sales | measure:Sales/Total | relationship:Name | role:Name | ...
        public string ObjectType { get; set; }  // Table | Column | Measure | Partition | Hierarchy | Relationship | Role | Perspective | Culture | DataSource | Expression
        public string Name { get; set; }
        public string Table { get; set; }        // owning table for children (null for top-level)
        public string Action { get; set; }       // Create | Update | Delete | Equal
        public string LeftText { get; set; }     // serialized left object (null for Delete)
        public string RightText { get; set; }    // serialized right object (null for Create)
        // TARGET-side identity of the matched RIGHT object (null on a Create — no target exists yet). Apply resolves the
        // object to change/delete by THIS identity, NOT by the source-keyed Name/Table — so a source table renamed on
        // the target (matched by LineageTag) never makes a child ref land on an unrelated same-source-named target
        // object, and a lineage-matched rename applies as a rename (remove the OLD target name, add the new) rather than
        // duplicate+orphan. Falls back to Name/Table when no tag exists. See ModelCompare.ApplyTableChild.
        public string TargetTag { get; set; }        // the matched right object's LineageTag (columns/measures/hierarchies/tables)
        public string TargetName { get; set; }       // the matched right object's CURRENT name (differs from Name on a rename)
        public string TargetTable { get; set; }      // the matched right (target) owning-table's current name (children only)
        public string TargetTableTag { get; set; }   // the matched right (target) owning-table's LineageTag (children only)
        // Set only on Action == "Ambiguous": the target has >1 object sharing a LineageTag, so we CANNOT tell which the
        // source object refers to. Rather than guess (an Update landing on one twin while the other is swept as a
        // Delete), the item is surfaced as Ambiguous — never applied, never deleted — with this human reason.
        public string Reason { get; set; }
        // RETAG / REPUBLISH: a same-NAMED object exists on both sides but with DIFFERENT non-empty LineageTags. Match
        // (correctly) does NOT pair them, so the SAME logical object surfaces as a right-only Delete + a left-only
        // Create. Applying that Delete against a published model DROPS the live object and its data (partitions, rows)
        // and re-adds an empty shell — usually the sign the model was REPUBLISHED under us, not that the object was
        // removed. Both halves are flagged; the selective-push delete path REFUSES a flagged Delete by default.
        // RetaggedCounterpart is the ref of the paired item (on the Delete: the Create's ref, and vice versa).
        public bool LikelyRepublished { get; set; }
        public string RetaggedCounterpart { get; set; }
        // True when this object is matched across the two models by NAME ONLY (roles/perspectives/cultures/
        // datasources/named-expressions/partitions): correct within one lineage, but a rename reads as Delete+Create.
        // Relationships are matched STRUCTURALLY (by endpoints), so they are NOT name-matched. Lets the UI flag the
        // name-keyed groups for transparency. See docs/multimodel-plan.md §7.
        public bool MatchedByName { get; set; }
    }

    /// <summary>An IDENTITY-carrying delete target for the live-delete channel. The whole point: a delete ref STRING
    /// carries no identity, so resolving a live delete by parsing the ref (name) is a TOCTOU wrong-object hazard —
    /// SyncSessionToLive opens a FRESH connection and loads a THIRD live state neither drift snapshot saw, and a
    /// same-named-but-different object could be permanently deleted. This record carries the lineage identity captured
    /// at diff time so the live delete resolves the RIGHT object (or refuses).
    ///
    /// A REF IS AN IDENTIFIER FOR REPORTING, NEVER A RESOLVER. <see cref="Ref"/> is used only for audit/notes;
    /// resolution uses <see cref="Tag"/>/<see cref="TableTag"/> (authoritative for the tagged kinds — table / measure /
    /// column / hierarchy; a miss is TERMINAL) and <see cref="Name"/>/<see cref="Table"/> (name resolution, legitimate
    /// ONLY for the genuinely tag-less kinds — partition / role / perspective / culture / datasource / expression, and
    /// relationship whose identity is its structural endpoint signature carried in Name).</summary>
    public sealed class LiveDeleteTarget
    {
        public string Kind { get; set; }       // table | measure | column | hierarchy | partition | role | perspective | culture | datasource | expression | relationship
        public string Ref { get; set; }         // the object ref — REPORTING ONLY (audit/notes), never used to resolve
        public string Tag { get; set; }         // the object's own LineageTag (tagged kinds); null for tag-less kinds
        public string TableTag { get; set; }    // the owning table's LineageTag (children of a table); null for top-level
        public string Name { get; set; }        // current name at diff time (tag-less resolution + reporting); for a relationship: the structural endpoint signature
        public string Table { get; set; }       // the owning table's current name at diff time (children); null for top-level

        /// <summary>Build from a Delete <see cref="ModelDiffItem"/>, carrying the identity captured at diff time. This is
        /// the production path — it never parses <see cref="ModelDiffItem.Ref"/> for a tagged object (identity comes from
        /// the item's Target* fields). A relationship's identity IS its ref-borne structural signature (name-independent,
        /// compared whole via RelSig), so that one kind reads the signature off the ref — sanctioned, not name resolution.
        ///
        /// PROVENANCE (do NOT weaken the delete guards on the theory that publish-time tag churn causes false refusals):
        /// <see cref="Tag"/>/<see cref="TableTag"/> come from the item's Target* fields, which are the matched RIGHT
        /// object's LineageTag — and the right model is snapshot B, exported from the LIVE target seconds before the
        /// push. Both sides of every tag comparison come from the live model moments apart, NOT from the source artifact.
        /// So Power BI's Desktop→Service GUID churn cannot reach this resolver; a tag that differs from snapshot B means
        /// the target was REPUBLISHED under us, and refusing is correct. (Two independent reviewers assumed the tag came
        /// from the source and concluded the guard was unsound — it does not, and it is not.)</summary>
        public static LiveDeleteTarget FromDiffItem(ModelDiffItem it)
        {
            var kind = (it.ObjectType ?? "").ToLowerInvariant();
            if (kind == "relationship")
            {
                var sig = it.Ref != null && it.Ref.StartsWith("relationship:", StringComparison.Ordinal) ? it.Ref.Substring("relationship:".Length) : it.Ref;
                return new LiveDeleteTarget { Kind = kind, Ref = it.Ref, Name = sig };
            }
            return new LiveDeleteTarget
            {
                Kind = kind,
                Ref = it.Ref,
                Tag = string.IsNullOrEmpty(it.TargetTag) ? null : it.TargetTag,           // null for tag-less kinds (partition/role/…)
                TableTag = string.IsNullOrEmpty(it.TargetTableTag) ? null : it.TargetTableTag,
                Name = it.TargetName ?? it.Name,
                Table = it.TargetTable ?? it.Table,
            };
        }
    }

    public sealed class ModelDiff
    {
        public string LeftLabel { get; set; }
        public string RightLabel { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Equal { get; set; }
        public ModelDiffItem[] Items { get; set; } = Array.Empty<ModelDiffItem>();  // non-Equal by default
        public string Error { get; set; }
    }

    /// <summary>Result of cherry_pick — copying selected objects FROM another model INTO the open session,
    /// undoably (one batch). Dry-run (commit=false) classifies; commit applies + broadcasts.</summary>
    public sealed class CherryPickResult
    {
        public bool Applied { get; set; }
        public int Count { get; set; }                                    // copied (or that WOULD copy on a dry run)
        public string[] AppliedRefs { get; set; } = Array.Empty<string>();
        public string[] Conflicts { get; set; } = Array.Empty<string>();  // already exist in the open model — would be overwritten
        public string[] FailedRefs { get; set; } = Array.Empty<string>(); // unsupported / unresolvable — surfaced, never silently dropped
        public string Source { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of apply_diff (selectively merging chosen changes into a target).</summary>
    public sealed class ApplyDiffResult
    {
        public bool Applied { get; set; }
        public int Count { get; set; }            // changes applied (or that WOULD apply on a dry run)
        public string[] AppliedRefs { get; set; } = Array.Empty<string>();
        public string[] FailedRefs { get; set; } = Array.Empty<string>();   // selected but could not be applied — surfaced, never silently dropped
        public string Target { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
        public string RestorePointId { get; set; }   // the pre-push snapshot this write can be rolled back to (live pushes only)
    }

    /// <summary>Result of rollback_push (restoring a published model to a pre-push snapshot).</summary>
    public sealed class RollbackResult
    {
        public bool Applied { get; set; }            // false on a dry run, or on refusal
        public int Restored { get; set; }            // objects re-created or updated back
        public int Removed { get; set; }             // objects added by the push and now removed again
        public string[] RestoredRefs { get; set; } = Array.Empty<string>();
        public string[] RemovedRefs { get; set; } = Array.Empty<string>();
        public string[] FailedRefs { get; set; } = Array.Empty<string>();
        public string RestorePointId { get; set; }
        public string Target { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Deploy-readiness gate (Semanticus's wedge): a deploy/publish is BLOCKED when this fails unless
    /// overridden. Combines the BPA + AI-readiness scans with the model diff.</summary>
    public sealed class DeployGate
    {
        public bool Pass { get; set; }
        public string Grade { get; set; }             // AI-readiness grade
        public int BpaViolations { get; set; }
        public int BpaBlocking { get; set; }          // error-severity BPA violations (waived ones excluded — a waiver is an audited acceptance)
        public int BpaWaivedBlocking { get; set; }    // error-severity violations accepted via waiver (surfaced for honesty, never blocking)
        public string[] Blockers { get; set; } = Array.Empty<string>();
        public int Changes { get; set; }              // pending changes vs the compare target (when supplied)
        // ADVISORY interview replay (null when the model has no saved question pack — the shape is unchanged for
        // everyone else). Never contributes to Pass/Blockers: it reports per-question outcome deltas so a deploy
        // sees "these user questions changed since you last checked", and only ever informs.
        public InterviewGateAdvisory Interview { get; set; }
        public string Note { get; set; }
    }

    // ---- Fabric REST (the cloud ALM lane — read-only in this phase) -------------------------------------
    // Shapes mirror the Fabric Core REST API v1 (https://api.fabric.microsoft.com/v1). Conditionally-present
    // fields are nullable on purpose: presence depends on the caller's permissions (e.g. workspaceName only if the
    // caller can see the workspace). Open enums (Type / ItemType) are kept as strings — MS adds new values over time.

    public sealed class FabricWorkspace
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }              // Personal | Workspace | AdminWorkspace (open enum)
        public string CapacityId { get; set; }        // null unless assigned to a capacity
    }

    public sealed class DeploymentPipeline
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
    }

    public sealed class PipelineStage
    {
        public string Id { get; set; }
        public int Order { get; set; }                // 0-based (Dev=0, Test=1, Prod=2 in a 3-stage pipeline)
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string WorkspaceId { get; set; }       // null when no workspace is assigned
        public string WorkspaceName { get; set; }     // null when unassigned OR the caller lacks workspace access
        public bool IsPublic { get; set; }
    }

    public sealed class StageItem
    {
        public string ItemId { get; set; }
        public string ItemDisplayName { get; set; }
        public string ItemType { get; set; }          // SemanticModel | Report | … (open enum)
        public string SourceItemId { get; set; }      // null without contributor on the source-stage workspace
        public string TargetItemId { get; set; }      // null without contributor on the target-stage workspace
        public string LastDeploymentTime { get; set; }// null if never deployed (ISO-8601 string)
    }

    /// <summary>Fabric long-running-operation state (the 202 → poll path; used by the write/deploy lanes).</summary>
    public sealed class FabricOperationState
    {
        public string Status { get; set; }            // Undefined | NotStarted | Running | Succeeded | Failed (open)
        public int PercentComplete { get; set; }
        public string ErrorCode { get; set; }         // set only when Status == Failed
        public string ErrorMessage { get; set; }
    }

    // ---- Deployment Pipelines — preview + deploy (the gated write lane) + history -----------------------
    public sealed class DeployItemDiff
    {
        public string ItemId { get; set; }
        public string ItemDisplayName { get; set; }
        public string ItemType { get; set; }
        public string State { get; set; }   // New (no paired target) | Update (paired — may be identical; exact change is confirmed at deploy)
    }

    /// <summary>The client-computed pre-deploy diff (Fabric has NO preview endpoint — it's paired from the source
    /// stage's items by their target binding). 'Update' items might be identical; only a real deploy's executionPlan
    /// knows Different-vs-NoDifference, so this is best-effort New-vs-Update.</summary>
    public sealed class DeployPreview
    {
        public string PipelineId { get; set; }
        public string SourceStageId { get; set; }
        public string SourceStageName { get; set; }
        public string TargetStageId { get; set; }
        public string TargetStageName { get; set; }
        public bool TargetIsProd { get; set; }
        public DeployItemDiff[] Items { get; set; } = Array.Empty<DeployItemDiff>();
        public int NewCount { get; set; }
        public int UpdateCount { get; set; }
        public DeployGate Gate { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of deploy_stage. Dry-run by default (Committed=false = nothing deployed). A PROD target needs
    /// the <see cref="ConfirmToken"/> the human's own dry-run surfaces — which is never returned to the agent door —
    /// and an agent can never commit a prod promotion. A failing readiness gate blocks unless overridden.</summary>
    public sealed class DeployStageReport
    {
        public bool Committed { get; set; }
        public string PipelineId { get; set; }
        public string SourceStageId { get; set; }
        public string SourceStageName { get; set; }
        public string TargetStageId { get; set; }
        public string TargetStageName { get; set; }
        public bool TargetIsProd { get; set; }
        public int ItemCount { get; set; }              // selected items (0 in body = deploy ALL supported)
        public DeployItemDiff[] Items { get; set; } = Array.Empty<DeployItemDiff>();
        public int NewCount { get; set; }
        public int UpdateCount { get; set; }
        public DeployGate Gate { get; set; }
        public string ConfirmToken { get; set; }        // prod dry-run, HUMAN door only — the token a prod commit must echo
        public string Status { get; set; }              // LRO terminal status on commit (Succeeded / Failed)
        public string OperationId { get; set; }
        public string Plan { get; set; }                // one-line human summary of what would happen / happened
        public string Error { get; set; }               // refusal or failed-deploy reason; scrubbed; never thrown across a door
    }

    public sealed class DeploymentHistoryEntry
    {
        public string Id { get; set; }
        public string Type { get; set; }                // "Deploy"
        public string Status { get; set; }              // NotStarted | Running | Succeeded | Failed
        public string ExecutionStartTime { get; set; }
        public string ExecutionEndTime { get; set; }
        public string SourceStageId { get; set; }
        public string TargetStageId { get; set; }
        public DeployNote Note { get; set; }
        public DeployDiffCounts PreDeploymentDiffInformation { get; set; }
        public DeployPrincipal PerformedBy { get; set; }
    }
    public sealed class DeployNote { public string Content { get; set; } public bool IsTruncated { get; set; } }
    public sealed class DeployDiffCounts { public int NewItemsCount { get; set; } public int DifferentItemsCount { get; set; } public int NoDifferenceItemsCount { get; set; } }
    public sealed class DeployPrincipal { public string Id { get; set; } public string Type { get; set; } public string DisplayName { get; set; } }

    // ---- Fabric Git (workspace ⇄ git) ------------------------------------------------------------------
    public sealed class FabricGitChange
    {
        public string ObjectId { get; set; }
        public string LogicalId { get; set; }
        public string ItemType { get; set; }
        public string DisplayName { get; set; }
        public string WorkspaceChange { get; set; }   // Added | Deleted | Modified | null
        public string RemoteChange { get; set; }       // Added | Deleted | Modified | null
        public string ConflictType { get; set; }       // None | Conflict | SameChanges
    }

    public sealed class FabricGitStatus
    {
        public string WorkspaceHead { get; set; }      // the workspace's last-synced commit
        public string RemoteCommitHash { get; set; }   // the remote branch's HEAD
        public FabricGitChange[] Changes { get; set; } = Array.Empty<FabricGitChange>();
        public bool Conflicts { get; set; }            // any change with conflictType == Conflict
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class FabricGitConnection
    {
        public string State { get; set; }              // NotConnected | Connected | ConnectedAndInitialized
        public string ProviderType { get; set; }        // AzureDevOps | GitHub
        public string Organization { get; set; }        // org (ADO) / owner (GitHub)
        public string Project { get; set; }
        public string Repository { get; set; }
        public string Branch { get; set; }
        public string Directory { get; set; }
        public string Head { get; set; }
        public string LastSyncTime { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of a Fabric Git write (commit/update/connect/disconnect). Dry-run by default
    /// (Committed=false). Failures are reported here, never thrown across a door.</summary>
    public sealed class FabricGitResult
    {
        public bool Committed { get; set; }
        public string Action { get; set; }              // commit | update | connect | disconnect
        public string Direction { get; set; }            // "workspace→git" | "git→workspace" | ""
        public string Status { get; set; }               // Succeeded | Failed (LRO terminal)
        public string OperationId { get; set; }
        public int ChangeCount { get; set; }             // pending changes seen on the dry-run
        public bool Conflicts { get; set; }
        public string Plan { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of a CI/CD publish (the open model's on-disk definition → a Fabric workspace via the Items
    /// API updateDefinition). Dry-run by default (Committed=false enumerates the parts but POSTs nothing). Failures
    /// are reported here, never thrown across a door.</summary>
    public sealed class CicdPublishResult
    {
        public bool Committed { get; set; }
        public string Action { get; set; }              // "publish"
        public string WorkspaceId { get; set; }
        public string ItemId { get; set; }              // the target semantic-model item id
        public string ModelPath { get; set; }           // the local .SemanticModel folder that was enumerated
        public int PartCount { get; set; }              // number of definition parts that would be / were sent
        public string[] SampleParts { get; set; } = Array.Empty<string>();   // a few part paths, for the preview
        public string Status { get; set; }               // Succeeded | Failed (LRO terminal)
        public string OperationId { get; set; }
        public string Plan { get; set; }
        public string Error { get; set; }
    }

    /// <summary>One generated CI/CD scaffold file (repo-relative POSIX path + its full text content).</summary>
    public sealed class CicdFile
    {
        public string Path { get; set; }
        public string Content { get; set; }
    }

    /// <summary>The emitted fabric-cicd scaffold: a parameter.yml + a CI workflow (+ deploy script) that runs the
    /// REAL fabric-cicd in CI. PURE FILE AUTHORING — no live write, no gate. The engine returns the contents (and
    /// optionally writes them into the repo when write=true); the file-landing differs per door.</summary>
    public sealed class CicdScaffold
    {
        public CicdFile[] Files { get; set; } = Array.Empty<CicdFile>();
        public bool Written { get; set; }                // true if any files were written into the repo
        public string[] WrittenPaths { get; set; } = Array.Empty<string>();   // absolute paths written
        public string[] SkippedPaths { get; set; } = Array.Empty<string>();   // existing files NOT overwritten
        public string Note { get; set; }
        public string Error { get; set; }
    }
}
