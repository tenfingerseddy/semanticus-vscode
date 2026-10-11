using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal Func<ModelConnectionRecord, Task> WorkingCopyConnectForTests;

        public async Task<ConnectionContext> SetPublishDestinationAsync(string connectionId, string origin = "agent", bool clear = false)
        {
            GuardOutsideWrite(origin);
            origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
            if (clear)
            {
                if (!string.IsNullOrWhiteSpace(connectionId))
                    throw new ArgumentException("Pass a destination to set, or clear=true to let go of the current one, not both.", nameof(connectionId));
                using var clearGuard = GuardRegistry(nameof(SetPublishDestinationAsync), "connections", ConnectionRegistry.StatePath());
                return await ClearPublishDestinationAsync(origin);
            }
            if (string.IsNullOrWhiteSpace(connectionId))
                throw new ArgumentException("Name the XMLA connection to publish to (list_connections), or pass clear=true to let go of the current one.", nameof(connectionId));
            var session = _sessions.Require();
            using var registryGuard = GuardRegistry(nameof(SetPublishDestinationAsync), "connections", ConnectionRegistry.StatePath());
            var publish = ConnectionRegistry.Find(connectionId)
                ?? throw new InvalidOperationException("The selected publish connection no longer exists. Choose it again.");
            EnsureRemoteXmlaPublish(publish);

            var modelName = await session.ReadAsync(m => m.Database?.Name ?? m.Name ?? "Model");
            var source = ConnectionContextBuilder.FindEditingRecord(session);
            var sourcePath = session.SourcePath;
            if (source == null)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                    throw new InvalidOperationException("Open the local model you want to publish before choosing its destination.");
                sourcePath = Path.GetFullPath(sourcePath);
                source = ConnectionRegistry.Remember("file", sourcePath, null, modelName);
            }

            // A user-owned file is keyed to that exact file, not its repository root. One repo may contain several
            // semantic models with different destinations; broad directory matching would cross-link them.
            var workingFolder = string.Equals(source.Kind, "file", StringComparison.OrdinalIgnoreCase) ? sourcePath
                : !string.IsNullOrWhiteSpace(source.WorkingFolder) ? source.WorkingFolder
                : Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(workingFolder))
                throw new InvalidOperationException("The open model does not have a stable local folder to link.");
            ConnectionRegistry.SetWorkingCopy(source.Id, workingFolder, publish.Id);
            // A role change belongs on the connection timeline too (MEDIUM 8/9), carrying the target's id so a
            // per-connection history filter finds it.
            RecordConnectionHistory(publish, "role", publish.LastAccount, publish.Endpoint, publish.Database, publish.TenantId, ok: true, detail: "set as the publish destination");
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "set_publish_destination", Origin = origin,
                Label = $"Linked {modelName} to publish destination {publish.ModelName ?? publish.Database}",
                Target = publish.Id, Ok = true
            });
            return await ConnectionContextAsync();
        }

        /// <summary>
        /// Let go of the open model's publish destination (contract 19.2). Recorded in the connection history against
        /// the destination that was let go, like a set. Nothing is deployed, deleted or written to the model.
        ///
        /// A model open LIVE from a workspace is refused: saving goes straight back to that model (deploy_live's
        /// default target is the live origin, not this setting), so "Not set" would be a statement the engine does
        /// not honour. The honest route there is a local copy.
        /// </summary>
        private async Task<ConnectionContext> ClearPublishDestinationAsync(string origin)
        {
            var session = _sessions.Require();
            if (session.LiveOrigin != null && !LiveDeploy.IsLocalEndpoint(session.LiveOrigin.Endpoint))
                throw new InvalidOperationException("This model is open live from its workspace, so saving goes straight back to it and its publish destination cannot be cleared. Work on a local copy to publish somewhere else, or nowhere.");

            var before = await ConnectionContextAsync();
            var editing = ConnectionContextBuilder.FindEditingRecord(session);
            if (!before.Publishing.Available || editing == null)
            {
                before.Note = "No publish destination was set, so nothing changed.";
                return before;
            }

            var released = ConnectionRegistry.Find(before.Publishing.ConnectionId);
            ConnectionRegistry.ClearPublish(editing.Id);
            RecordConnectionHistory(released, "role", released?.LastAccount, before.Publishing.Endpoint, before.Publishing.Database,
                released?.TenantId, ok: true, detail: "cleared as the publish destination");
            var modelName = before.Editing?.ModelName ?? "the open model";
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "set_publish_destination", Origin = origin,
                Label = $"Cleared the publish destination of {modelName}",
                Target = released?.Id, Ok = true
            });
            var after = await ConnectionContextAsync();
            after.Note = $"{modelName} no longer has a publish destination. Nothing was deployed or deleted.";
            return after;
        }

        public async Task<WorkingCopyResult> PrepareWorkingCopyAsync(string connectionId, string parentFolder, bool commit, string queryConnectionId = null, string publishConnectionId = null, string origin = "agent")
        {
            if (commit) GuardOutsideWrite(origin);
            origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
            var sourceRecord = ConnectionRegistry.Find(connectionId);
            var result = WorkingCopyPlanner.Plan(sourceRecord, parentFolder);
            var queryRecord = string.IsNullOrWhiteSpace(queryConnectionId) ? sourceRecord : ConnectionRegistry.Find(queryConnectionId)
                ?? throw new InvalidOperationException("The selected test/query connection no longer exists. Choose it again.");
            // A destination named on this call is a choice and always wins. With none named, the XMLA default (a copy
            // publishes back to the model it came from) applies ONLY when the copy is first created, and not after the
            // person let go of the destination (contract 19.2). A reopen is not a choice, so it keeps whatever is stored:
            // a destination set on purpose, a clear, or nothing (Sol 6, slice C: a reopen overwrote both).
            ModelConnectionRecord publishRecord;
            if (!string.IsNullOrWhiteSpace(publishConnectionId))
            {
                publishRecord = ConnectionRegistry.Find(publishConnectionId)
                    ?? throw new InvalidOperationException("The selected publish connection no longer exists. Choose it again.");
                EnsureRemoteXmlaPublish(publishRecord);
            }
            else if (result.Action == "create")
            {
                publishRecord = string.Equals(sourceRecord.Kind, "xmla", StringComparison.OrdinalIgnoreCase) && sourceRecord.PublishCleared != true
                    ? sourceRecord : null;
                EnsureRemoteXmlaPublish(publishRecord);
            }
            else
                // Kept as stored, unjudged here: the connection context already treats a legacy loopback row as inert,
                // and refusing it would make reopening the copy fail over a choice this call did not make.
                publishRecord = ConnectionRegistry.Find(sourceRecord.PublishConnectionId);
            ExplainDestinations(result, sourceRecord, queryRecord, publishRecord);
            result.CommitRequested = commit;
            if (!commit || !result.CanCommit) return result;

            if (result.Action == "create")
                await CreateWorkingCopyFilesAsync(sourceRecord, result, origin);

            // Link only after a complete, owned model exists. Reopening never writes model files, which protects local
            // edits and makes reconnect a safe one-click action rather than an implicit refresh.
            ConnectionRegistry.SetWorkingCopy(sourceRecord.Id, result.TargetFolder, publishRecord?.Id);
            OpenResult opened;
            try
            {
                opened = await OpenCoreAsync(result.TargetFolder);
                result.Opened = true;
                // The copy is its own model on disk with its own identity, and starts with the SQL endpoint of the
                // model it came from (contract 19.3). A copy that already has one of its own keeps it.
                ModelSqlEndpointRegistry.CopyIfAbsent(IdentityForRecord(sourceRecord), PaneIdentity(_sessions.Current, null),
                    sourceRecord.ModelName ?? sourceRecord.Database, null);
            }
            catch (Exception ex)
            {
                result.Error = "The local working copy is ready, but it could not be opened: " + ex.Message;
                result.NextAction = "Open the local folder manually. Its files were not removed or overwritten.";
                return result;
            }

            try
            {
                if (WorkingCopyConnectForTests != null)
                    await WorkingCopyConnectForTests(queryRecord);
                else if (string.Equals(queryRecord.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase))
                    await ConnectLocalAsync(queryRecord.Endpoint, queryRecord.Database);
                else
                    // Thread the CALLER's origin (T163). Omitting it let the parameter default to "human", so an
                    // agent-driven prepare_working_copy attached the query model with human authority: it skipped the
                    // agent interactive refusal and built a prompt-capable credential. A defaulted origin is fail-OPEN,
                    // which is the exact opposite of DeployGuard.IsAgent's fail-closed rule — so it is always passed.
                    await ConnectXmlaAsync(queryRecord.Endpoint, queryRecord.Database, queryRecord.AuthMode, rawToken: null, tenantId: queryRecord.TenantId, origin: origin);
                result.QueryConnected = true;
            }
            catch (Exception ex)
            {
                var queryRole = string.Equals(queryRecord.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase)
                    ? "local test model" : "published query model";
                result.Error = $"The local working copy is open for editing, but the {queryRole} could not be connected: " + XmlaAuthHint.Scrub(ex.Message);
                result.NextAction = $"Keep editing locally, or reconnect the {queryRole}. No local files were changed by the failed connection.";
            }

            // Announced once the query model is attached (or failed to), so the window reads the finished pairing.
            opened.LiveConnected = result.QueryConnected;
            PublishSessionSwap(opened, "working_copy");
            result.Context = await ConnectionContextAsync();
            if (result.QueryConnected)
            {
                result.Summary = result.Context.Summary;
                result.NextAction = "Edit the local working copy. Use Review changes before explicitly pushing anything to the published model.";
            }
            return result;
        }

        private static void EnsureRemoteXmlaPublish(ModelConnectionRecord publish)
        {
            // The same sentence list_connections shows beside a struck-out model (ConnectionJobs, contract 19.1).
            var refusal = ConnectionJobs.PublishRefusal(publish);
            if (refusal != null) throw new InvalidOperationException(refusal);
        }

        private static void ExplainDestinations(WorkingCopyResult result, ModelConnectionRecord source, ModelConnectionRecord query, ModelConnectionRecord publish)
        {
            result.SourceConnectionId = source.Id;
            result.SourceModelName = source.ModelName ?? source.Database;
            result.PublishConnectionId = publish?.Id;
            result.PublishModelName = publish?.ModelName ?? publish?.Database;
            result.QueryConnectionId = query.Id;
            result.QueryModelName = query.ModelName ?? query.Database;
            result.QueryKind = query.Kind;
            var queryName = result.QueryModelName ?? "the selected model";
            var querySentence = string.Equals(query.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase)
                ? $"Tests and queries will run against the local running model {queryName}. Local model files do not execute queries by themselves."
                : $"Tests and queries will run against the published model {queryName}.";
            var publishSentence = publish == null
                ? "No final publish destination is linked yet. Choose an XMLA destination before publishing."
                : $"When ready, review and explicitly push the local changes to {result.PublishModelName ?? "the linked published model"} through its XMLA connection.";
            result.Summary = result.Summary + " " + querySentence + " " + publishSentence;
            result.NextAction = result.CanCommit
                ? "Confirm to open the local working copy, connect the chosen test model, and preserve the separate publish destination."
                : result.NextAction;
        }

        private async Task CreateWorkingCopyFilesAsync(ModelConnectionRecord record, WorkingCopyResult result, string origin)
        {
            LiveModelExport.Snapshot snapshot = null;
            Session snapshotSession = null;
            var parent = Path.GetDirectoryName(result.TargetFolder)
                ?? throw new InvalidOperationException("The working-copy location needs a parent folder.");
            var createdParent = !Directory.Exists(parent);
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, ".semanticus-working-copy-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (string.Equals(record.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase))
                    snapshot = await LiveModelExport.ToBimLocalAsync(record.Endpoint, record.Database);
                else if (string.Equals(record.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                    snapshot = (await SnapshotWorkspaceMetadataAsync(record.Endpoint, record.Database, record.AuthMode, origin, record.TenantId)).snap;
                else
                    throw new InvalidOperationException("This connection type cannot create a local working copy.");

                var definition = Path.Combine(staging, "definition");
                Directory.CreateDirectory(definition);
                File.WriteAllText(Path.Combine(staging, "definition.pbism"), "{}");
                snapshotSession = await _sessions.BuildOpenAsync(snapshot.BimPath);
                await snapshotSession.RunAsync(() =>
                {
                    snapshotSession.Save(definition, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                    return true;
                });
                if (!ModelPathResolver.IsTmdlRoot(definition))
                    throw new InvalidOperationException("The published model could not be serialized as a local model definition.");
                WorkingCopyPlanner.WriteMarker(staging, record);

                // An empty target was accepted by the preview. Re-check immediately before replacing it so another
                // process cannot add files between preview and commit and have them silently removed.
                if (Directory.Exists(result.TargetFolder))
                {
                    if (Directory.EnumerateFileSystemEntries(result.TargetFolder).Any())
                        throw new InvalidOperationException("The target folder changed after preview and is no longer empty. No existing files were changed.");
                    Directory.Delete(result.TargetFolder);
                }
                Directory.Move(staging, result.TargetFolder);
            }
            finally
            {
                try { snapshotSession?.Dispose(); } catch { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                try
                {
                    DeleteUntrackedSnapshot(snapshot);
                }
                catch { }
                try
                {
                    if (createdParent && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent);
                }
                catch { }
            }
        }

        private static void DeleteUntrackedSnapshot(LiveModelExport.Snapshot snapshot)
        {
            var dir = snapshot?.BimPath == null ? null : Path.GetDirectoryName(LiveModelExport.Norm(snapshot.BimPath));
            var rootPrefix = LiveModelExport.Norm(LiveModelExport.TempRoot) + Path.DirectorySeparatorChar;
            if (dir != null && (dir + Path.DirectorySeparatorChar).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
