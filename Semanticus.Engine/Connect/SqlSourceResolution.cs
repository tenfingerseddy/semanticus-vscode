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
    /// The ONE rule for turning "which SQL" into "where and how to sign in", read by saved checks and by table row
    /// counts so the two can never disagree:
    ///
    ///   1. A named source wins. Its server, database, sign-in mode and tenant replace anything inline.
    ///   2. No named source means the check's own inline values, exactly as before. Every definition saved before
    ///      named sources existed keeps working, untouched.
    ///   3. A named source that has been removed is an ERROR, not a fallback. Falling back would run the check
    ///      against a different place than the person chose and still call it a result.
    /// </summary>
    public static class SqlSources
    {
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
        public static SqlSourceResolution Apply(ReconcileRequest request)
        {
            if (request == null) return new SqlSourceResolution();
            var resolved = Resolve(request.SqlSourceId, request.Server, request.Database, request.AuthMode, request.TenantId);
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

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
