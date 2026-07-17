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

        public async Task<ConnectionContext> SetPublishDestinationAsync(string connectionId, string origin = "agent")
        {
            origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
            var session = _sessions.Require();
            var publish = ConnectionRegistry.Find(connectionId)
                ?? throw new InvalidOperationException("The selected publish connection no longer exists. Choose it again.");
            if (!string.Equals(publish.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The final publish destination must be an XMLA connection.");

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

        public async Task<WorkingCopyResult> PrepareWorkingCopyAsync(string connectionId, string parentFolder, bool commit, string queryConnectionId = null, string publishConnectionId = null, string origin = "agent")
        {
            origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
            var sourceRecord = ConnectionRegistry.Find(connectionId);
            var result = WorkingCopyPlanner.Plan(sourceRecord, parentFolder);
            var queryRecord = string.IsNullOrWhiteSpace(queryConnectionId) ? sourceRecord : ConnectionRegistry.Find(queryConnectionId)
                ?? throw new InvalidOperationException("The selected test/query connection no longer exists. Choose it again.");
            var publishRecord = string.IsNullOrWhiteSpace(publishConnectionId)
                ? (string.Equals(sourceRecord.Kind, "xmla", StringComparison.OrdinalIgnoreCase) ? sourceRecord : null)
                : ConnectionRegistry.Find(publishConnectionId)
                    ?? throw new InvalidOperationException("The selected publish connection no longer exists. Choose it again.");
            if (publishRecord != null && !string.Equals(publishRecord.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The final publish destination must be an XMLA connection.");
            ExplainDestinations(result, sourceRecord, queryRecord, publishRecord);
            result.CommitRequested = commit;
            if (!commit || !result.CanCommit) return result;

            if (result.Action == "create")
                await CreateWorkingCopyFilesAsync(sourceRecord, result, origin);

            // Link only after a complete, owned model exists. Reopening never writes model files, which protects local
            // edits and makes reconnect a safe one-click action rather than an implicit refresh.
            ConnectionRegistry.SetWorkingCopy(sourceRecord.Id, result.TargetFolder, publishRecord?.Id);
            try
            {
                await OpenAsync(result.TargetFolder);
                result.Opened = true;
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
                    await ConnectXmlaAsync(queryRecord.Endpoint, queryRecord.Database, queryRecord.AuthMode, rawToken: null, tenantId: queryRecord.TenantId);
                result.QueryConnected = true;
            }
            catch (Exception ex)
            {
                var queryRole = string.Equals(queryRecord.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase)
                    ? "local test model" : "published query model";
                result.Error = $"The local working copy is open for editing, but the {queryRole} could not be connected: " + XmlaAuthHint.Scrub(ex.Message);
                result.NextAction = $"Keep editing locally, or reconnect the {queryRole}. No local files were changed by the failed connection.";
            }

            result.Context = await ConnectionContextAsync();
            if (result.QueryConnected)
            {
                result.Summary = result.Context.Summary;
                result.NextAction = "Edit the local working copy. Use Review changes before explicitly pushing anything to the published model.";
            }
            return result;
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
