using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    // ============================================================================================
    // One SQL endpoint per model (contract 19.3, Kane 24 September 2026: "yes as recommended for sql
    // end points"). A model links to one saved SQL source; every check that names no source of its
    // own reads from it; a working copy inherits it. The link is keyed by the model's durable
    // identity, the same key its saved checks and table mappings carry, so the three can never
    // disagree about which model they belong to.
    //
    // Both doors land here: set_model_sql_endpoint / setModelSqlEndpoint, list_model_sql_endpoints /
    // listModelSqlEndpoints, and connection_context / connectionContext for the read back.
    // ============================================================================================
    public sealed partial class LocalEngine
    {
        /// <summary>The durable identity of a saved connection's model: the same key <see cref="PaneIdentity"/> gives
        /// the session once that model is open, so a link made from a row and a link made while it is open are one
        /// link. A file record is its path; anything else is its endpoint and database.</summary>
        internal static string IdentityForRecord(ModelConnectionRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Endpoint)) return null;
            return string.Equals(record.Kind, "file", StringComparison.OrdinalIgnoreCase)
                ? "disk:" + Sha8(CanonAnchor(record.Endpoint))
                : "live:" + Sha8(NormLivePart(record.Endpoint) + "|" + NormLivePart(record.Database));
        }

        /// <summary>The saved connection whose model has this identity, when there is one. Display only.</summary>
        private static ModelConnectionRecord RecordForIdentity(string identity, IReadOnlyList<ModelConnectionRecord> records = null)
        {
            if (string.IsNullOrWhiteSpace(identity)) return null;
            return (records ?? ConnectionRegistry.List()).FirstOrDefault(r => string.Equals(IdentityForRecord(r), identity, StringComparison.Ordinal));
        }

        /// <summary>
        /// The open model's endpoint link, running the one-time upgrade pass first when this model has never been
        /// looked at (contract 19.3, migration by id). The pass picks the saved source the model's checks and table
        /// mappings name most, links it, and marks the model as looked at either way, so a link the person later
        /// clears is never put back. It writes nothing to <c>sql-sources.json</c>, and it does nothing at all when no
        /// SQL source is saved on this machine, so a person who never used one never gets a file.
        /// </summary>
        private async Task<ModelSqlEndpointLink> EnsureModelSqlEndpointAsync(Session s)
        {
            var identity = s == null ? null : PaneIdentity(s, null);
            if (identity == null) return null;
            var link = ModelSqlEndpointRegistry.Find(identity);
            if (ModelSqlEndpointRegistry.IsMigrated(identity)) return link;

            var sources = SqlSourceRegistry.List();
            if (sources.Count == 0) return link;
            var defs = (await LoadScopedTestDefinitionsAsync(s)).Defs;
            var mappings = await ScopedTableMappingsAsync(s);
            var pick = SqlSources.MostUsedSource(defs, mappings,
                id => sources.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)));
            var name = await s.ReadAsync(m => m.Database?.Name ?? m.Name);
            // Kane's rule, no check changes where it points: a check that named no place read the partitions before
            // the upgrade, so it is pinned there; only checks saved after this read the model's endpoint.
            var sourceless = defs.Where(SqlSources.NamesNoPlace).Select(d => d.Id);
            // The same rule for row counts: a table with no mapping counted against what its own partitions read.
            var mapped = new HashSet<string>(mappings.Where(m => !string.IsNullOrWhiteSpace(m?.ModelTable)).Select(m => m.ModelTable),
                StringComparer.OrdinalIgnoreCase);
            var unmapped = await s.ReadAsync(m => SqlSourceDiscovery.Find(m).Select(c => c.ModelTable)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !mapped.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            return ModelSqlEndpointRegistry.MigrateOnce(identity, pick, name, RecordForIdentity(identity)?.Id, sourceless, unmapped);
        }

        /// <summary>The endpoint id one saved check falls back to: none for a check the upgrade pinned to the
        /// model's partitions, otherwise the model's endpoint.</summary>
        private string EndpointForCheck(Session s, string defId, string modelEndpointId)
        {
            if (modelEndpointId == null || string.IsNullOrWhiteSpace(defId) || s == null) return modelEndpointId;
            return ModelSqlEndpointRegistry.PinnedToPartitions(PaneIdentity(s, null)).Contains(defId) ? null : modelEndpointId;
        }

        /// <summary>The open model's unmapped tables whose row counts stay on its partitions (read after the upgrade pass).</summary>
        private static HashSet<string> TablesOnPartitions(Session s) =>
            ModelSqlEndpointRegistry.TablesOnPartitions(s == null ? null : PaneIdentity(s, null));

        /// <summary>The saved source id the open model's checks fall back to, or null. What the resolver takes.</summary>
        private async Task<string> ModelSqlEndpointIdAsync(Session s) => (await EnsureModelSqlEndpointAsync(s))?.SqlSourceId;

        /// <summary>The open model's endpoint, resolved through the same rule a check uses, for the row counts.</summary>
        private async Task<SqlSourceResolution> ModelSqlEndpointResolutionAsync(Session s)
        {
            var id = await ModelSqlEndpointIdAsync(s);
            return id == null ? null : SqlSources.Resolve(null, null, null, null, null, id);
        }

        private static SqlEndpointInfo EndpointInfo(ModelSqlEndpointLink link)
        {
            if (link == null) return null;
            var rec = SqlSourceRegistry.Find(link.SqlSourceId);
            return new SqlEndpointInfo
            {
                SourceId = link.SqlSourceId,
                Name = rec?.Name,
                Server = rec?.Server,
                Database = rec?.Database,
                LastTest = rec?.LastTestOk == null ? null : new SqlEndpointLastTest { TestedUtc = rec.LastTestedUtc, Ok = rec.LastTestOk.Value },
                Missing = rec == null,
                SetBy = link.SetBy,
                SetWhenUtc = link.SetWhenUtc,
            };
        }

        /// <summary>Fold the open model's endpoint into a connection context, and when it has none, what its own
        /// sources read from ("Found in this model's sources", contract 19.3).</summary>
        private async Task FoldSqlEndpointAsync(ConnectionContext ctx, Session s)
        {
            if (ctx == null || s == null) return;
            var link = await EnsureModelSqlEndpointAsync(s);
            ctx.SqlEndpoint = EndpointInfo(link);
            if (link != null) return;

            var found = await s.ReadAsync(m => SqlSourceDiscovery.Find(m)
                .Where(c => !string.IsNullOrWhiteSpace(c.Server) && !string.IsNullOrWhiteSpace(c.Database))
                .Select(c => (Server: c.Server.Trim(), Database: c.Database.Trim()))
                .ToList());
            var distinct = found.GroupBy(c => (c.Server.ToLowerInvariant(), c.Database.ToLowerInvariant())).Select(g => g.First()).ToList();
            if (distinct.Count != 1) return;   // none, or more than one: nothing honest to offer first
            var (server, database) = distinct[0];
            var match = SqlSourceRegistry.List().FirstOrDefault(r =>
                string.Equals(r.Server?.Trim(), server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Database?.Trim(), database, StringComparison.OrdinalIgnoreCase));
            ctx.SqlEndpointSuggestion = new SqlEndpointSuggestion
            {
                Server = server, Database = database, MatchingSourceId = match?.Id, MatchingSourceName = match?.Name,
            };
        }

        public async Task<ModelSqlEndpointResult> SetModelSqlEndpointAsync(string sqlSourceId, string connectionId = null, string origin = "agent")
        {
            GuardOutsideWrite(origin);
            origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
            string identity, name, recordId;
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                var record = ConnectionRegistry.Find(connectionId)
                    ?? throw new InvalidOperationException("There is no saved model with that connection id. Run list_connections to see the saved models.");
                identity = IdentityForRecord(record)
                    ?? throw new InvalidOperationException("That saved connection has no address, so it cannot hold a SQL endpoint.");
                name = record.ModelName ?? record.Database ?? ConnectionRegistry.WorkspaceNameFromEndpoint(record.Endpoint) ?? record.Endpoint;
                recordId = record.Id;
            }
            else
            {
                var s = _sessions.Current ?? throw new InvalidOperationException(
                    "No open model. Open a model, or pass the connectionId of a saved model from list_connections.");
                identity = PaneIdentity(s, null) ?? throw new InvalidOperationException(
                    "This unsaved model has no durable identity. Save it (save_model) before giving it a SQL endpoint.");
                name = await s.ReadAsync(m => m.Database?.Name ?? m.Name);
                recordId = RecordForIdentity(identity)?.Id;
            }

            // the registry as it was when the person was asked, checked at its first write (the upgrade pass below may write it first)
            using var endpointGuard = GuardRegistry(nameof(SetModelSqlEndpointAsync), "SQL endpoints", ModelSqlEndpointRegistry.StatePath());
            // Setting or clearing the open model's endpoint is also a first look: run the upgrade pass first so a check
            // that existed before is pinned to its partitions rather than moved by this call.
            if (string.IsNullOrWhiteSpace(connectionId)) await EnsureModelSqlEndpointAsync(_sessions.Current);

            ModelSqlEndpointResult result;
            if (string.IsNullOrWhiteSpace(sqlSourceId))
            {
                // From a row the model's checks were not read, so the clear is kept but the upgrade's look is not spent.
                var had = ModelSqlEndpointRegistry.Clear(identity, markLookedAt: string.IsNullOrWhiteSpace(connectionId));
                result = new ModelSqlEndpointResult
                {
                    ModelName = name, ConnectionId = recordId, Changed = had,
                    Note = had
                        ? $"{name} no longer has a SQL endpoint. A check that names no source reads what the model's own tables read from."
                        : $"{name} had no SQL endpoint, so nothing changed.",
                };
            }
            else
            {
                var source = SqlSourceRegistry.Find(sqlSourceId) ?? throw new InvalidOperationException(
                    "There is no saved SQL source with that id. Save it with save_sql_source first, or pick one from list_sql_sources.");
                var before = ModelSqlEndpointRegistry.Find(identity);
                var link = ModelSqlEndpointRegistry.Set(identity, source.Id, name, recordId, origin,
                    markLookedAt: string.IsNullOrWhiteSpace(connectionId));
                result = new ModelSqlEndpointResult
                {
                    ModelName = name, ConnectionId = recordId, SqlEndpoint = EndpointInfo(link),
                    Changed = !string.Equals(before?.SqlSourceId, source.Id, StringComparison.OrdinalIgnoreCase),
                    Note = $"{name} now reads its checks from '{source.Name}' unless a check names its own source.",
                };
            }

            // Every table with no mapping of its own counts against the model's endpoint, so a new link makes every old count old.
            ForgetTableCounts();
            await PublishActivityAsync(new ActivityEvent
            {
                Kind = "set_model_sql_endpoint", Origin = origin,
                Label = result.SqlEndpoint == null ? $"Cleared the SQL endpoint of {name}" : $"Set the SQL endpoint of {name} to {result.SqlEndpoint.Name}",
                Target = recordId, Ok = true,
            });
            return result;
        }

        /// <summary>The <see cref="UnlinkedSqlSource.UsageScope"/> of a count read from every saved model.</summary>
        public const string SavedModelsScope = "savedModels";

        public async Task<ModelSqlEndpointList> ListModelSqlEndpointsAsync()
        {
            var s = _sessions.Current;
            if (s != null) await EnsureModelSqlEndpointAsync(s);   // the open model's upgrade pass runs before it is listed

            var records = ConnectionRegistry.List();
            var links = ModelSqlEndpointRegistry.List();
            var rows = links.Select(l => new ModelSqlEndpointRow
            {
                ModelName = l.ModelName,
                ConnectionId = RecordForIdentity(l.ModelIdentity, records)?.Id ?? l.ConnectionId,
                SqlEndpoint = EndpointInfo(l),
            }).OrderBy(r => r.ModelName ?? "", StringComparer.OrdinalIgnoreCase).ToArray();

            var linked = new HashSet<string>(links.Select(l => l.SqlSourceId), StringComparer.OrdinalIgnoreCase);
            // Counted across every saved model, open or not, the same count delete_sql_source refuses on, so Remove
            // is never offered for a source a closed model's checks still use (Codex on pull request 394, F-265).
            var usage = await SqlSourceUsageAcrossSavedModelsAsync();
            var notLinked = SqlSourceRegistry.List().Where(r => !linked.Contains(r.Id)).Select(r =>
            {
                var u = usage.TryGetValue(r.Id, out var found) ? found : null;
                return new UnlinkedSqlSource
                {
                    Id = r.Id, Name = r.Name, Server = r.Server, Database = r.Database,
                    ChecksUsing = u?.CheckTitles.Count ?? 0,
                    TableMappingsUsing = u?.MappedTables.Count ?? 0,
                    UsageScope = SavedModelsScope,
                    UsingModels = u?.Models.ToArray() ?? Array.Empty<string>(),
                };
            }).ToArray();

            return new ModelSqlEndpointList
            {
                Rows = rows,
                NotLinked = notLinked,
                // Honest limit, said once: only the models this machine lists were read.
                Note = notLinked.Length == 0 ? null
                    : "Check and table mapping counts cover every saved model in Connections, open or not. A model saved to disk and never opened here is not counted.",
            };
        }
    }
}
