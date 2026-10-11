using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AS = Microsoft.AnalysisServices;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    /// <summary>
    /// Publish the open model as a NEW model in a workspace (r1/newmodel, hero journey 5). The existing publish writes
    /// over a model that is already there; a model built from a SQL source has nowhere to go until this creates it.
    /// The Fabric items API creates the model, then the Power BI refresh API loads its data with the same sign-in, so
    /// the model answers questions as soon as it lands. Proven on Fabric 9 Oct: a model created over XMLA was refused a
    /// load ("default data connection without explicit connection credentials"); one created through the items API and
    /// loaded through the refresh API held its rows. Nothing is overwritten: a name already taken is refused first.
    /// </summary>
    internal static class NewModelPublish
    {
        internal const string PowerBiXmlaRoot = "powerbi://api.powerbi.com/v1.0/myorg/";
        internal const string PowerBiRestBase = "https://api.powerbi.com/v1.0/myorg/";
        internal const string DefaultCulture = "en-US";

        /// <summary>The workspace's XMLA address, from its name (the address Power BI shows under workspace settings).</summary>
        internal static string Endpoint(string workspaceName) => PowerBiXmlaRoot + (workspaceName ?? "").Trim();

        /// <summary>The model as Power BI will take it: named as chosen, and without a legacy data source no partition reads.
        /// A model built from a SQL source carries one ("FabricSql"); its tables read through the shared M expression, so it
        /// does nothing, and the service refuses it on a new model.</summary>
        internal static TOM.Database Prepare(string databaseJson, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the new model a name.", nameof(name));
            var db = TOM.JsonSerializer.DeserializeDatabase(databaseJson, null, AS.CompatibilityMode.PowerBI);
            db.Name = name.Trim();
            db.ID = name.Trim();
            // Power BI gives a model with no culture en-US. Saying so here, and on the local copy after the publish, keeps
            // the Changes list from showing a Model row that Power BI never takes.
            if (string.IsNullOrWhiteSpace(db.Model.Culture)) db.Model.Culture = DefaultCulture;
            var used = db.Model.Tables.SelectMany(t => t.Partitions)
                .Select(p => p.Source).OfType<TOM.QueryPartitionSource>()
                .Select(q => q.DataSource?.Name).Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var ds in db.Model.DataSources.Where(d => !used.Contains(d.Name)).ToList())
                db.Model.DataSources.Remove(ds);
            return db;
        }

        /// <summary>The Fabric create request: the model as model.bim, and the definition.pbism marker.</summary>
        internal static string CreateBody(TOM.Database db)
        {
            static object Part(string path, string text) => new { path, payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), payloadType = "InlineBase64" };
            return JsonSerializer.Serialize(new
            {
                displayName = db.Name,
                definition = new { parts = new[] { Part("model.bim", TOM.JsonSerializer.SerializeDatabase(db)), Part("definition.pbism", "{\"version\":\"1.0\"}") } },
            });
        }

        internal sealed class Outcome { public string ModelId; public bool Loaded; public string LoadError; public bool SignInNeeded; public bool DirectLake; public string Server; public string DirectLakeEndpoint; }

        internal const string SignInStep = "Set the data source sign-in in the Power BI service, then refresh.";

        /// <summary>The line the person reads after the publish. A source with no sign-in in the service is a next step, said plainly.</summary>
        internal static string ResultNote(string name, string workspace, Outcome o) =>
            o.Loaded ? $"Published {name} to {workspace}. Its data is loaded."
            : o.SignInNeeded ? $"Published {name} to {workspace}. To load its data: {SignInStep}"
            : $"Published {name} to {workspace}. Its data did not load: {o.LoadError}";

        /// <summary>The dry run's line: what a publish would create, and what comes after it.</summary>
        internal static string PreviewNote(string name, string workspace, bool directLake) =>
            directLake ? $"Would publish {name} to {workspace} as a new Direct Lake model. Its data loads with no sign-in step."
            : $"Would publish {name} to {workspace} as a new import model. After it is created: {SignInStep}";

        /// <summary>Whether a publish would go as Direct Lake: the source is a Fabric SQL endpoint and every table is a plain
        /// navigation of it. Reads a copy; the model passed in is left as it is.</summary>
        internal static bool WillBeDirectLake(TOM.Database db)
        {
            if (IsDirectLake(db)) return true;
            var (server, _) = SourceOf(db);
            return IsFabricSql(server) && ToDirectLake(db.Clone(), server, "preview");
        }

        /// <summary>Create the model in the workspace, then load its data. A load that fails leaves the model created and
        /// says why; the create itself fails loudly and writes nothing.</summary>
        /// <summary>The sentence for a name the workspace already has, or null. Creating a model is a POST of a new item, so a model with this name is never changed or replaced:
        /// the publish stops here, before anything is sent, and asks for another name.</summary>
        internal static string TakenNameProblem(string name, string workspaceName, System.Collections.Generic.IEnumerable<FabricSemanticModel> existing)
        {
            var hit = existing?.FirstOrDefault(m => string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            return hit == null ? null : $"A model called {hit.DisplayName} is already in {workspaceName}. Pick another name.";
        }

        internal static async Task<Outcome> CreateAndLoadAsync(TOM.Database db, string workspaceId, string fabricToken, string powerBiToken, CancellationToken ct, string workspaceName = null)
        {
            var existing = await FabricRest.ListSemanticModelsAsync(workspaceId, fabricToken, ct).ConfigureAwait(false);
            var taken = TakenNameProblem(db.Name, string.IsNullOrWhiteSpace(workspaceName) ? "this workspace" : workspaceName, existing);
            if (taken != null) throw new InvalidOperationException(taken);

            // A Fabric SQL source goes as Direct Lake: it reads the lake as the person asks, with no sign-in to set first.
            var (server, database) = SourceOf(db);
            string directLake = null;
            if (IsFabricSql(server))
            {
                var endpoint = await SqlEndpointIdAsync(workspaceId, database, fabricToken, ct).ConfigureAwait(false) ?? database;
                if (ToDirectLake(db, server, endpoint)) directLake = endpoint;
            }

            using (var http = FabricRest.NewClient(fabricToken))
            {
                var url = "workspaces/" + Uri.EscapeDataString(workspaceId) + "/semanticModels";
                var r = await FabricRest.SendAsync(http, HttpMethod.Post, url, CreateBody(db), ct).ConfigureAwait(false);
                if (r.Status == 202)
                {
                    var st = await FabricRest.PollFrom202Async(http, r, ct).ConfigureAwait(false);
                    if (st.Status != "Succeeded") throw new InvalidOperationException("Power BI did not create the model: " + (st.ErrorMessage ?? st.Status));
                }
                else if (!r.Ok) throw FabricRest.NonSuccessHttp(r);
            }
            var created = (await FabricRest.ListSemanticModelsAsync(workspaceId, fabricToken, ct).ConfigureAwait(false))
                .FirstOrDefault(m => string.Equals(m.DisplayName, db.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Power BI said the model was created, but it is not in the workspace yet. Look again in a minute.");

            var outcome = new Outcome { ModelId = created.Id, Server = server, DirectLakeEndpoint = directLake, DirectLake = IsDirectLake(db) };
            try { await RefreshAsync(workspaceId, created.Id, powerBiToken, ct).ConfigureAwait(false); outcome.Loaded = true; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome.SignInNeeded = NeedsSourceSignIn(ex.Message);
                outcome.LoadError = outcome.SignInNeeded ? SignInStep : FabricRest.Scrub(ex.Message);
            }
            return outcome;
        }

        /// <summary>An M navigation to one table of a shared source: #"Source", Data = Source{[Schema="dbo",Item="x"]}[Data].</summary>
        internal static readonly System.Text.RegularExpressions.Regex Navigation = new System.Text.RegularExpressions.Regex("#\"([^\"]+)\"\\s*,\\s*\\w+\\s*=\\s*\\w+\\{\\[Schema=\"([^\"]+)\",\\s*Item=\"([^\"]+)\"\\]\\}\\[Data\\]");

        internal static string DirectLakeExpression(string server, string endpoint) => $"let\n    database = Sql.Database(\"{server}\", \"{endpoint}\")\nin\n    database";

        /// <summary>A model that reads in Direct Lake already (a local copy turned Direct Lake by an earlier publish).</summary>
        internal static bool IsDirectLake(TOM.Database db) => db.Model.Tables.SelectMany(t => t.Partitions).Any(p => p.Mode == TOM.ModeType.DirectLake);

        /// <summary>A Fabric lakehouse or warehouse SQL endpoint (the address Fabric gives every one of them).</summary>
        internal static bool IsFabricSql(string server) =>
            !string.IsNullOrWhiteSpace(server) && server.Trim().EndsWith(".fabric.microsoft.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>Turn an import model over a Fabric SQL endpoint into Direct Lake: the shared source expression points at the
        /// SQL endpoint, and each table's M navigation becomes an entity partition in Direct Lake mode. A table that is not a
        /// plain navigation (a calculated table, a hand-written query) leaves the whole model as it is.</summary>
        internal static bool ToDirectLake(TOM.Database db, string server, string endpoint)
        {
            var nav = Navigation;
            var plan = new System.Collections.Generic.List<(TOM.Table Table, string Expr, string Schema, string Entity)>();
            foreach (var t in db.Model.Tables)
            {
                if (t.Partitions.Count != 1 || t.Partitions[0].Source is not TOM.MPartitionSource m) return false;
                var hit = nav.Match(m.Expression ?? "");
                if (!hit.Success || db.Model.Expressions.Find(hit.Groups[1].Value) == null) return false;
                if (t.Columns.Any(c => c is TOM.CalculatedColumn)) return false;
                plan.Add((t, hit.Groups[1].Value, hit.Groups[2].Value, hit.Groups[3].Value));
            }
            if (plan.Count == 0) return false;
            if (db.CompatibilityLevel < 1604) db.CompatibilityLevel = 1604;
            foreach (var name in plan.Select(p => p.Expr).Distinct())
                db.Model.Expressions[name].Expression = DirectLakeExpression(server, endpoint);
            foreach (var (t, expr, schema, entity) in plan)
            {
                var name = t.Partitions[0].Name;
                t.Partitions.Remove(t.Partitions[0]);
                t.Partitions.Add(new TOM.Partition
                {
                    Name = name, Mode = TOM.ModeType.DirectLake,
                    Source = new TOM.EntityPartitionSource { EntityName = entity, SchemaName = schema, ExpressionSource = db.Model.Expressions[expr] },
                });
            }
            return true;
        }

        // The SQL endpoint's id in this workspace, by the name the source was read with (Direct Lake names the endpoint by id).
        internal static async Task<string> SqlEndpointIdAsync(string workspaceId, string name, string fabricToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Guid.TryParse(name, out _)) return name;
            try
            {
                using var http = FabricRest.NewClient(fabricToken);
                var r = await FabricRest.SendAsync(http, HttpMethod.Get, "workspaces/" + Uri.EscapeDataString(workspaceId) + "/items?type=SQLEndpoint", ct).ConfigureAwait(false);
                if (!r.Ok) return null;
                using var doc = JsonDocument.Parse(r.Body);
                return doc.RootElement.GetProperty("value").EnumerateArray()
                    .Where(i => string.Equals(i.GetProperty("displayName").GetString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.GetProperty("id").GetString()).FirstOrDefault();
            }
            catch { return null; }
        }

        // Start a refresh and wait for it (status "Unknown" means it is still running). About a minute for a small model.
        internal static async Task RefreshAsync(string workspaceId, string modelId, string token, CancellationToken ct, int maxPolls = 150)
        {
            using var http = FabricRest.TestClientFactory?.Invoke() ?? FabricRest.CreateSharedClient(PowerBiRestBase);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var url = "groups/" + Uri.EscapeDataString(workspaceId) + "/datasets/" + Uri.EscapeDataString(modelId) + "/refreshes";
            var start = await FabricRest.SendAsync(http, HttpMethod.Post, url, "{\"notifyOption\":\"NoNotification\"}", ct).ConfigureAwait(false);
            if (!start.Ok) throw new InvalidOperationException(RefreshError(start.Body, start.Status));
            for (var i = 0; i < maxPolls; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var r = await FabricRest.SendAsync(http, HttpMethod.Get, url + "?$top=1", ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(RefreshError(r.Body, r.Status));
                using var doc = JsonDocument.Parse(r.Body);
                var last = doc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
                var status = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("status", out var s) ? s.GetString() : "Unknown";
                if (status == "Completed") return;
                if (status is "Failed" or "Cancelled" or "Disabled")
                    throw new InvalidOperationException(last.TryGetProperty("serviceExceptionJson", out var ex) ? ex.GetString() : "The data did not load (" + status + ").");
            }
            throw new InvalidOperationException("The data is still loading. Power BI goes on with it; look again in a few minutes.");
        }

        internal static bool NeedsSourceSignIn(string message) =>
            !string.IsNullOrEmpty(message) && (message.Contains("default data connection", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Premium_ASWL_Error", StringComparison.Ordinal) || message.Contains("credential", StringComparison.OrdinalIgnoreCase));

        /// <summary>The SQL server and database the model's shared source expression reads (Sql.Database("server", "db")).</summary>
        internal static (string Server, string Database) SourceOf(TOM.Database db)
        {
            foreach (var e in db.Model.Expressions)
            {
                var m = System.Text.RegularExpressions.Regex.Match(e.Expression ?? "", "Sql\\.Database\\(\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]+)\"");
                if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);
            }
            return (null, null);
        }

        private static string RefreshError(string body, int status) => FabricRest.Scrub($"Power BI {status}: " + (body ?? "").Substring(0, Math.Min((body ?? "").Length, 400)));
    }
}
