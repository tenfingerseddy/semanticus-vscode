using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>Where one SQL-reading check or table mapping will actually connect, and how it got there.</summary>
    public sealed class SqlSourceResolution
    {
        /// <summary>The saved source that was asked for, when one was. Null = this check carries its own coordinates.</summary>
        public string SqlSourceId { get; set; }
        public string SourceName { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
        /// <summary>True when a saved source supplied the coordinates. False = the check's own inline values, which is
        /// how every definition saved before named sources existed still works.</summary>
        public bool FromSavedSource { get; set; }
        /// <summary>True when the coordinates came from the MODEL's SQL endpoint because the check named none of its own.</summary>
        public bool FromModelEndpoint { get; set; }
        /// <summary>Plain-words reason this could not be resolved, or null. Set only when a named source has gone:
        /// we never quietly fall back to inline values then, because the check would run against a different place
        /// than the person chose.</summary>
        public string Error { get; set; }
    }

    /// <summary>Which named SQL source one saved check uses, for list_tests.</summary>
    public sealed class TestSqlSourceUse
    {
        public string DefId { get; set; }
        public string SqlSourceId { get; set; }
        public string SourceName { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        /// <summary>The check names a source that is no longer saved. Said out loud rather than run around.</summary>
        public bool Missing { get; set; }
    }

    /// <summary>
    /// The ONE rule for turning "which SQL" into "where and how to sign in", read by saved checks, direct
    /// reconciliations and table row counts so they can never disagree (contract 19.3):
    ///
    ///   1. The check's own choice wins: a named source first, then the check's own inline server and database.
    ///      This is the quiet per-check override, and every definition saved before named sources or model
    ///      endpoints existed keeps pointing exactly where it did.
    ///   2. A check that names nothing reads the MODEL's SQL endpoint, when the model has one.
    ///   3. Neither: nothing is resolved here, and the caller's old fallback (reading the model's own partitions)
    ///      decides, exactly as before.
    ///   A named source or an endpoint whose source has been removed is an ERROR, not a fallback. Falling back
    ///   would run the check against a different place than the person chose and still call it a result.
    ///
    /// Table row counts apply the same order with the table's own mapping as the per-check choice.
    /// </summary>
    public static class SqlSources
    {
        /// <summary>The full order: the check's named source, then its inline coordinates, then the model's endpoint
        /// (<paramref name="modelEndpointId"/>, the saved source id the model links to, or null).</summary>
        public static SqlSourceResolution Resolve(string sqlSourceId, string inlineServer, string inlineDatabase,
            string inlineAuthMode, string inlineTenantId, string modelEndpointId)
        {
            var checkChoice = Resolve(sqlSourceId, inlineServer, inlineDatabase, inlineAuthMode, inlineTenantId);
            // The check chose something of its own (a source, even a removed one, or an inline address): that stands.
            if (checkChoice.SqlSourceId != null || checkChoice.Server != null || checkChoice.Database != null)
                return checkChoice;
            var endpointId = string.IsNullOrWhiteSpace(modelEndpointId) ? null : modelEndpointId.Trim();
            if (endpointId == null) return checkChoice;

            var rec = SqlSourceRegistry.Find(endpointId);
            if (rec == null)
                return new SqlSourceResolution
                {
                    SqlSourceId = endpointId,
                    FromModelEndpoint = true,
                    Error = "this model's SQL endpoint is no longer saved. Open Connections and choose the model's SQL endpoint again",
                };
            return new SqlSourceResolution
            {
                SqlSourceId = rec.Id,
                SourceName = rec.Name,
                Server = rec.Server,
                Database = rec.Database,
                // A sign-in named on the check itself still applies to where the model's endpoint points; otherwise
                // the source's own sign-in, which is the point of saving it once.
                AuthMode = Clean(inlineAuthMode) ?? rec.AuthMode,
                TenantId = Clean(inlineTenantId) ?? rec.TenantId,
                FromSavedSource = true,
                FromModelEndpoint = true,
            };
        }

        public static SqlSourceResolution Resolve(string sqlSourceId, string inlineServer, string inlineDatabase,
            string inlineAuthMode, string inlineTenantId)
        {
            var id = string.IsNullOrWhiteSpace(sqlSourceId) ? null : sqlSourceId.Trim();
            if (id == null)
                return new SqlSourceResolution
                {
                    Server = Clean(inlineServer),
                    Database = Clean(inlineDatabase),
                    AuthMode = Clean(inlineAuthMode),
                    TenantId = Clean(inlineTenantId),
                    FromSavedSource = false,
                };

            var rec = SqlSourceRegistry.Find(id);
            if (rec == null)
                return new SqlSourceResolution
                {
                    SqlSourceId = id,
                    Error = "the SQL source this was set to use is no longer saved. Open Connections, pick a SQL source, then save this again",
                };

            return new SqlSourceResolution
            {
                SqlSourceId = rec.Id,
                SourceName = rec.Name,
                Server = rec.Server,
                Database = rec.Database,
                AuthMode = rec.AuthMode,
                TenantId = rec.TenantId,
                FromSavedSource = true,
            };
        }

        /// <summary>Resolve a saved reconciliation's coordinates ONTO its request, so everything downstream reads one
        /// set of fields and nothing else has to know named sources exist. Returns the resolution; check its
        /// <see cref="SqlSourceResolution.Error"/> before running.</summary>
        public static SqlSourceResolution Apply(ReconcileRequest request) => Apply(request, null);

        /// <summary>The same, with the model's SQL endpoint as the step after the check's own choice. Idempotent: once
        /// applied, the request carries the coordinates inline, and resolving it again lands on the same place.</summary>
        public static SqlSourceResolution Apply(ReconcileRequest request, string modelEndpointId)
        {
            if (request == null) return new SqlSourceResolution();
            var resolved = Resolve(request.SqlSourceId, request.Server, request.Database, request.AuthMode, request.TenantId, modelEndpointId);
            if (resolved.Error != null || !resolved.FromSavedSource) return resolved;
            request.Server = resolved.Server;
            request.Database = resolved.Database;
            request.AuthMode = resolved.AuthMode;
            request.TenantId = resolved.TenantId;
            return resolved;
        }

        /// <summary>What list_tests reports: for every saved reconciliation, the named source it uses, if any. A check
        /// pointing at a source that has gone is reported as missing rather than omitted.</summary>
        public static TestSqlSourceUse[] DescribeUse(IEnumerable<TestDefinition> defs)
        {
            if (defs == null) return Array.Empty<TestSqlSourceUse>();
            var uses = new List<TestSqlSourceUse>();
            foreach (var d in defs)
            {
                if (d == null || !string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)) continue;
                var request = string.IsNullOrWhiteSpace(d.ParamsJson) ? null : TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson);
                if (request == null) continue;
                var resolved = Resolve(request.SqlSourceId, request.Server, request.Database, request.AuthMode, request.TenantId);
                if (resolved.SqlSourceId == null && !resolved.FromSavedSource && resolved.Error == null
                    && string.IsNullOrWhiteSpace(resolved.Server) && string.IsNullOrWhiteSpace(resolved.Database))
                    continue;   // nothing to say: no named source and no inline coordinates either
                uses.Add(new TestSqlSourceUse
                {
                    DefId = d.Id,
                    SqlSourceId = resolved.SqlSourceId,
                    SourceName = resolved.SourceName,
                    Server = resolved.Server,
                    Database = resolved.Database,
                    Missing = resolved.Error != null,
                });
            }
            return uses.ToArray();
        }

        /// <summary>How many saved checks in this suite point at one named source. The number a delete refusal says
        /// out loud, so "still in use" is never an unexplained no.</summary>
        public static int CountChecksUsing(IEnumerable<TestDefinition> defs, string sqlSourceId)
        {
            if (defs == null || string.IsNullOrWhiteSpace(sqlSourceId)) return 0;
            return defs.Count(d => d != null
                && string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(d.ParamsJson)
                && string.Equals(TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson)?.SqlSourceId, sqlSourceId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The TITLES of the saved checks pointing at one named source, sorted. A refusal that names what
        /// needs repair is actionable; one that only counts it sends the person hunting.</summary>
        public static string[] TitlesOfChecksUsing(IEnumerable<TestDefinition> defs, string sqlSourceId)
        {
            if (defs == null || string.IsNullOrWhiteSpace(sqlSourceId)) return Array.Empty<string>();
            return defs.Where(d => d != null
                    && string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(d.ParamsJson)
                    && string.Equals(TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson)?.SqlSourceId, sqlSourceId, StringComparison.OrdinalIgnoreCase))
                .Select(d => string.IsNullOrWhiteSpace(d.Title) ? d.Id : d.Title)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// The upgrade's pick for one model (contract 19.3, migration by id): the saved source its checks and table
        /// mappings name most often. Ties go to the lowest id, so two processes decide the same way. Sources that are
        /// no longer saved are never picked. Null when nothing names a saved source. Every check keeps its own
        /// source id either way, so the one not picked stays that check's override and nothing changes where it points.
        /// </summary>
        public static string MostUsedSource(IEnumerable<TestDefinition> defs, IEnumerable<TableSourceMapping> mappings,
            Func<string, bool> isSaved)
        {
            var ids = new List<string>();
            foreach (var d in defs ?? Enumerable.Empty<TestDefinition>())
            {
                if (d == null || !string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(d.ParamsJson)) continue;
                var id = TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson)?.SqlSourceId;
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
            foreach (var m in mappings ?? Enumerable.Empty<TableSourceMapping>())
                if (!string.IsNullOrWhiteSpace(m?.SqlSourceId)) ids.Add(m.SqlSourceId.Trim());
            return ids.Where(id => isSaved == null || isSaved(id))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        /// <summary>True for a saved reconciliation that names no source and no server or database of its own: the
        /// checks the upgrade pins to the model's partitions.</summary>
        public static bool NamesNoPlace(TestDefinition d)
        {
            if (d == null || !string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(d.ParamsJson)) return false;
            var r = TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson);
            return r != null && string.IsNullOrWhiteSpace(r.SqlSourceId)
                && string.IsNullOrWhiteSpace(r.Server) && string.IsNullOrWhiteSpace(r.Database);
        }

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
