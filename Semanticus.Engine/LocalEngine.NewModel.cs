using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    /// <summary>The answer to publish_new_model: where the new model is, and whether its data loaded.</summary>
    public sealed class NewModelResult
    {
        public string Workspace { get; set; }
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public bool Loaded { get; set; }
        public string LoadError { get; set; }
        /// <summary>True when the model was created but Power BI has no sign-in for its source yet: the person sets it
        /// in the Power BI service, then refreshes. A next step, not a failure.</summary>
        public bool SignInNeeded { get; set; }
        /// <summary>"Direct Lake" or "Import": how the new model reads its data.</summary>
        public string Mode { get; set; }
        /// <summary>True for a dry run (commit=false): nothing was contacted or written.</summary>
        public bool Preview { get; set; }
        /// <summary>The one line the person reads.</summary>
        public string Note { get; set; }
    }

    public sealed partial class LocalEngine
    {
        internal static void SessionToDirectLake(Model m, string server, string endpoint)
        {
            var used = new System.Collections.Generic.HashSet<string>();
            foreach (var t in m.Tables.ToList())
            {
                if (t.Partitions.Count != 1 || t.Partitions[0].SourceType != PartitionSourceType.M) continue;
                var old = t.Partitions[0];
                var hit = NewModelPublish.Navigation.Match(old.Expression ?? "");
                var expr = hit.Success ? m.Expressions.FirstOrDefault(e => e.Name == hit.Groups[1].Value) : null;
                if (expr == null) continue;
                var name = old.Name;
                var p = t.AddEntityPartition(TmpPartitionName, hit.Groups[3].Value);
                p.SchemaName = hit.Groups[2].Value;
                p.ExpressionSource = expr;
                p.Mode = ModeType.DirectLake;
                old.Delete();
                p.Name = name;
                used.Add(expr.Name);
            }
            foreach (var e in m.Expressions.Where(e => used.Contains(e.Name)))
                e.Expression = NewModelPublish.DirectLakeExpression(server, endpoint);
            RemoveUnusedPublishSources(m);
        }

        private static void RemoveUnusedPublishSources(Model m)
        {
            foreach (var ds in m.DataSources.ToList())
                if (!m.AllPartitions.Any(p => ReferenceEquals(p.DataSource, ds))) ds.Delete();
        }

        /// <summary>
        /// Publish the open model as a new model in a workspace, load its data, and make it the publish destination,
        /// so the next Publish updates it and Ask your data has rows to answer from. The sign-in is the one given,
        /// else the one the model's SQL source was read with, else the person's browser sign-in.
        /// </summary>
        public async Task<NewModelResult> PublishNewModelAsync(string workspace, string name, string authMode = null, string tenantId = null, string origin = "human", string workspaceId = null, bool commit = true)
        {
            if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("Choose a workspace for the new model.", nameof(workspace));
            var s = _sessions.Require();
            name = string.IsNullOrWhiteSpace(name) ? await s.ReadAsync(m => m.Database?.Name ?? m.Name ?? "Model") : name.Trim();
            var endpoint = NewModelPublish.Endpoint(workspace);
            // The dry run reads the open model only: no sign-in, no network, nothing written.
            if (!commit)
            {
                var willBeDirectLake = NewModelPublish.WillBeDirectLake(NewModelPublish.Prepare(await SchemaJsonAsync(s), name));
                return new NewModelResult
                {
                    Workspace = workspace.Trim(), Endpoint = endpoint, Database = name, Preview = true, Mode = willBeDirectLake ? "Direct Lake" : "Import",
                    Note = NewModelPublish.PreviewNote(name, workspace.Trim(), willBeDirectLake),
                };
            }
            if (DeployGuard.IsAgent(origin))
            {
                var refusal = GuardAgent(AgentCapability.DeployLive, endpoint, name, origin, isCommit: true,
                    summary: $"publish the open model as a new model {name} in {workspace}", intentBasis: "publish_new_model");
                if (refusal != null) throw new InvalidOperationException("publish_new_model: " + refusal);
            }
            StartSource source;
            lock (_startPlanLock) source = _startPlan?.Source;
            if (string.IsNullOrWhiteSpace(authMode)) { authMode = source?.AuthMode ?? "interactive"; tenantId ??= source?.TenantId; }

            var ct = System.Threading.CancellationToken.None;
            var fabricToken = await AcquireFabricTokenAsync(authMode, tenantId, origin, ct);
            if (string.IsNullOrWhiteSpace(workspaceId))
                workspaceId = (await FabricRest.ListWorkspacesAsync(fabricToken, ct))
                    .FirstOrDefault(w => string.Equals(w.DisplayName, workspace.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                    ?? throw new InvalidOperationException($"No workspace called {workspace} was found for this sign-in.");
            var tok = await AcquireLiveTokenAsync(s, authMode, null, tenantId, origin, endpoint, name, ct);
            NewModelPublish.Outcome outcome;
            try
            {
                var db = NewModelPublish.Prepare(await SchemaJsonAsync(s), name);
                outcome = await NewModelPublish.CreateAndLoadAsync(db, workspaceId, fabricToken, tok.Token, ct, workspace.Trim());
            }
            catch (Exception ex) { throw new InvalidOperationException(FabricRest.Scrub(ex.Message)); }

            // The local copy matches what was published (one undo step): Direct Lake when it went as Direct Lake, so the next
            // Publish compares like with like and never sends the old import partitions over the new model, and the culture
            // Power BI gave it.
            var directLake = outcome.DirectLakeEndpoint != null;
            var noCulture = await s.ReadAsync(m => string.IsNullOrWhiteSpace(m.Culture));
            var unusedSources = await s.ReadAsync(m => m.DataSources.Any(ds => !m.AllPartitions.Any(p => ReferenceEquals(p.DataSource, ds))));
            if (directLake || noCulture || unusedSources)
                try
                {
                    await s.MutateAsync(origin, directLake ? "publish as Direct Lake" : "publish as a new model", m =>
                    {
                        if (string.IsNullOrWhiteSpace(m.Culture)) m.Culture = NewModelPublish.DefaultCulture;
                        if (directLake) SessionToDirectLake(m, outcome.Server, outcome.DirectLakeEndpoint);
                        else RemoveUnusedPublishSources(m);
                    });
                }
                catch { /* the published model is right; the local copy keeps what it had */ }

            // The new model is now where this model publishes: the next Publish compares with it and updates it.
            var rec = ConnectionRegistry.Remember("xmla", endpoint, name, name, tenantId, authMode);
            string linkNote = null;
            try { await SetPublishDestinationAsync(rec.Id, origin); }
            catch (Exception ex) { linkNote = " It is not linked as this model's destination yet: " + ex.Message; }
            // Tests and queries read the new model, so a question gets an answer from its rows straight away (both doors).
            if (outcome.Loaded)
                try { await ConnectXmlaAsync(endpoint, name, authMode, null, tenantId, origin: origin); }
                catch { /* the model is published and linked; Tests can be attached from Connections */ }
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "publish_new_model", Origin = origin, Label = $"Published {name} as a new model in {workspace}", Target = rec.Id, Ok = true
            });
            return new NewModelResult
            {
                Workspace = workspace, Endpoint = endpoint, Database = name, Loaded = outcome.Loaded, LoadError = outcome.LoadError,
                SignInNeeded = outcome.SignInNeeded, Mode = outcome.DirectLake ? "Direct Lake" : "Import",
                Note = NewModelPublish.ResultNote(name, workspace, outcome) + linkNote,
            };
        }

        // The open model as Power BI takes it (model.bim JSON), written through the same serializer a save uses.
        private static async Task<string> SchemaJsonAsync(Session s)
        {
            var bim = Path.Combine(Path.GetTempPath(), "semanticus-deploy", Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            Directory.CreateDirectory(Path.GetDirectoryName(bim));
            try
            {
                await s.RunAsync(() => { s.Save(bim, SaveFormat.ModelSchemaOnly, SerializeOptions.Default, resetCheckpoint: false); return true; });
                return File.ReadAllText(bim);
            }
            finally { try { File.Delete(bim); } catch { /* temp */ } }
        }
    }
}
