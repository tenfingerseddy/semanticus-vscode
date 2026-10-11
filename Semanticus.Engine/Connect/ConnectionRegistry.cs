using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// One durable record per model source the user has connected to. This is deliberately ONE object doing two jobs
    /// that were about to be built twice:
    ///
    ///   • <b>Connection management</b> — every surface (Compare, Deploy, the Studio connect bar, the open flow) reads
    ///     the same list instead of asking the user to retype an endpoint. The recent-XMLA list previously lived in VS
    ///     Code <c>globalState</c>, which the webview cannot reach — which is precisely why Compare made you paste a path.
    ///     Living in the engine, it is reachable from both doors.
    ///
    ///   • <b>The target registry</b> the agent-permissions matrix gates on. A connection is where the user declares
    ///     what a target IS (<c>dev|uat|prod|local</c>), because the engine cannot know. Two registries would drift.
    ///
    /// Holds NO secrets. <c>AuthMode</c> is a mode name, never a token; tokens stay in the encrypted MSAL cache
    /// (<see cref="EntraToken"/>), keyed by identity rather than by endpoint.
    /// </summary>
    public sealed class ModelConnectionRecord
    {
        public string Id { get; set; }              // stable: hash of kind|endpoint|database
        public string Kind { get; set; }            // xmla | localDesktop | file (machine-local identity only)
        public string Endpoint { get; set; }        // XMLA endpoint, or the local Analysis Services data source
        public string Database { get; set; }        // dataset resolved at open time
        public string ModelName { get; set; }
        public string TenantId { get; set; }
        public string AuthMode { get; set; }        // a mode NAME (azcli / interactive / serviceprincipal) — NOT a secret
        public string Label { get; set; }           // dev | uat | prod | local. NULL = unlabelled = treated as prod.
        public string WorkingFolder { get; set; }   // durable local copy for the query-live / edit-local pattern; null = transient snapshot
        public string PublishConnectionId { get; set; } // optional XMLA destination for this source's working folder
        // True once the person let go of the publish destination for this source's local copy (contract 19.2). It
        // exists because an XMLA source's copy publishes back to the source BY DEFAULT, so nulling the id alone
        // cannot say "nowhere". Null (omitted on disk) otherwise; choosing a destination again resets it.
        public bool? PublishCleared { get; set; }
        public string LastUsedUtc { get; set; }
        public int UseCount { get; set; }
        // The user identity (UPN) that LAST signed in to this target, captured from the MSAL AuthenticationRecord on a
        // successful interactive/device-code sign-in. A DISPLAY HINT ("as megan@contoso.com") so a surface can show which
        // account a connect will use — never an identity lock (the live token, per-tenant, still decides). Null for
        // azcli/serviceprincipal/token (no MSAL record) and for a target no one has interactively signed in to yet.
        public string LastAccount { get; set; }
        // Worked out on every read by list_connections (ConnectionJobs), never stored: why this model cannot answer
        // tests and queries, and why it cannot be a publish destination. Null when it can, and omitted from disk.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string TestsRefusal { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string PublishRefusal { get; set; }
    }

    /// <summary>
    /// One durable, NAMED SQL source the user saves once and then chooses by name from a check or a table mapping
    /// (Kane's Tests redesign, 2026-09-15). It is a fourth ROLE in Connections beside Editing, Tests and Publish to,
    /// and it is stored BESIDE the model records in its own file, never mixed into them: a model connection answers
    /// DAX, a SQL source answers the independent ground-truth query, and one list holding both would let a surface
    /// offer the wrong one.
    ///
    /// Holds NO secrets, exactly like <see cref="ModelConnectionRecord"/>: <see cref="AuthMode"/> is a mode NAME and
    /// there is no password field to fill. The token is acquired at use time through the same Entra helper the XMLA
    /// side uses, keyed by identity rather than stored here.
    /// </summary>
    public sealed class SqlSourceRecord
    {
        public string Id { get; set; }              // opaque and stable for life: a rename or a re-point keeps it, so references never break
        public string Name { get; set; }            // what the user calls it, and what a check's picker shows
        public string Server { get; set; }          // the SQL endpoint address only, never a connection string
        public string Database { get; set; }
        public string AuthMode { get; set; }        // interactive | devicecode | azcli | serviceprincipal — a mode NAME, never a secret
        public string TenantId { get; set; }
        public string CreatedUtc { get; set; }
        public string LastTestedUtc { get; set; }   // null = never tested
        public bool? LastTestOk { get; set; }       // null = never tested; false = the last test failed. Never rounded into "ok"
    }

    /// <summary>
    /// The named SQL sources, stored beside <c>connections.json</c> in their own <c>sql-sources.json</c> under the
    /// same <see cref="ConnectionRegistry.Root"/>, with the same cross-process discipline: one writer at a time, an
    /// unreadable file moved aside rather than overwritten, and a scrub of every string field before the bytes land.
    /// </summary>
    public static class SqlSourceRegistry
    {
        public const int MaxRecords = 50;
        private const string FileName = "sql-sources.json";

        /// <summary>The auth modes a SQL source may declare. "token" is deliberately absent: a raw token is a
        /// credential, and this record never holds one.</summary>
        public static readonly string[] AuthModes = { "interactive", "devicecode", "azcli", "serviceprincipal" };

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static string FilePath() => Path.Combine(ConnectionRegistry.Root(), FileName);
        internal static string StatePath() => FilePath();

        /// <summary>Newest first, so a picker reads sensibly. Read-only; a parse failure just yields an empty list.</summary>
        public static IReadOnlyList<SqlSourceRecord> List()
        {
            lock (Gate) return ReadForDisplay();
        }

        public static SqlSourceRecord Find(string id) =>
            string.IsNullOrWhiteSpace(id) ? null : List().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Create (no id) or update (an id that exists). The rules here are the reason this record can be handed to a
        /// token helper without a second thought: the mode is one of four NAMES, and neither address field may carry a
        /// connection string, which is the only shape a pasted password arrives in.
        /// </summary>
        public static SqlSourceRecord Save(string id, string name, string server, string database, string authMode, string tenantId, Action<SqlSourceRecord> guard = null)
        {
            name = (name ?? "").Trim();
            server = (server ?? "").Trim();
            database = (database ?? "").Trim();
            tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            if (name.Length == 0) throw new ArgumentException("A SQL source needs a name, so a check can pick it by name later.", nameof(name));
            if (server.Length == 0) throw new ArgumentException("A SQL source needs a server address.", nameof(server));
            if (database.Length == 0) throw new ArgumentException("A SQL source needs a database name.", nameof(database));

            // FAIL CLOSED on the only shape a secret arrives in. A bare address has no ';' and no '=': anything that
            // does is a pasted connection string, and we refuse it rather than try to strip a credential out of it.
            if (LooksLikeConnectionString(server) || XmlaAuthHint.ContainsSecret(server))
                throw new ArgumentException("Enter just the server address, like contoso-sql.database.windows.net, not a connection string. Semanticus signs in for you and never stores a password.", nameof(server));
            if (LooksLikeConnectionString(database) || XmlaAuthHint.ContainsSecret(database))
                throw new ArgumentException("Enter just the database name, not a connection string. Semanticus signs in for you and never stores a password.", nameof(database));

            var mode = (authMode ?? "").Trim().ToLowerInvariant();
            if (mode == "token")
                throw new ArgumentException("Semanticus never holds a password or a token for you. Pick how to sign in instead: sign in in a browser, sign in with a code, use the Azure CLI sign-in, or use an app registration.", nameof(authMode));
            if (!AuthModes.Contains(mode))
                throw new ArgumentException("Pick how to sign in to this source. Sign in with one of: interactive (a browser), devicecode (a code on another device), azcli (the Azure CLI sign-in on this machine), or serviceprincipal (an app registration).", nameof(authMode));

            return Mutate(all =>
            {
                var existing = string.IsNullOrWhiteSpace(id) ? null
                    : all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(id) && existing == null)
                    throw new InvalidOperationException("No SQL source with that id. Open Connections to see the saved sources.");
                if (existing != null) guard?.Invoke(existing);   // the editor's starting point against the record as it stands inside this write

                // One name, one source: the picker shows names, so two rows called the same thing would make the
                // person's choice a guess.
                var clash = all.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
                    && (existing == null || !string.Equals(r.Id, existing.Id, StringComparison.OrdinalIgnoreCase)));
                if (clash != null)
                    throw new ArgumentException($"There is already a SQL source called '{clash.Name}'. Give this one a different name.", nameof(name));

                if (existing == null && all.Count >= MaxRecords)
                    throw new InvalidOperationException($"You already have {MaxRecords} saved SQL sources, which is the limit. Remove one you no longer use, then add this one.");

                var rec = existing ?? new SqlSourceRecord { Id = NewId(), CreatedUtc = DateTimeOffset.UtcNow.ToString("O") };
                if (existing != null) all.Remove(existing);
                rec.Name = name;
                rec.Server = server;
                rec.Database = database;
                rec.AuthMode = mode;
                rec.TenantId = tenantId;
                all.Insert(0, rec);
                return rec;
            });
        }

        /// <summary>Remove a source. Returns false when the id was not present. Whether anything still REFERENCES it
        /// is the engine's question, not the file's: the engine refuses that delete before it reaches here.</summary>
        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (rec == null) return false;
                all.Remove(rec);
                return true;
            });
        }

        /// <summary>Stamp the outcome of a connection test. A failure is RECORDED as a failure; it is never rounded
        /// up to "ok", and "never tested" (both fields null) stays its own third state. Null when the id is gone.</summary>
        public static SqlSourceRecord RecordTest(string id, bool ok)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (rec == null) return null;
                rec.LastTestedUtc = DateTimeOffset.UtcNow.ToString("O");
                rec.LastTestOk = ok;
                return rec;
            });
        }

        private static string NewId() => "sql-" + Guid.NewGuid().ToString("N").Substring(0, 16);

        // A bare address never contains these. A pasted "Server=...;Password=..." always does.
        private static bool LooksLikeConnectionString(string value) =>
            value.IndexOf(';') >= 0 || value.IndexOf('=') >= 0;

        // ---- persistence (the ConnectionRegistry discipline, its own file) --------------------------------------

        private static T Mutate<T>(Func<List<SqlSourceRecord>, T> mutator)
        {
            lock (Gate)
            using (AcquireCrossProcessLock())
            {
                RegistryWriteGuard.Check(FilePath());
                var all = ReadForWrite();
                var result = mutator(all);
                Save(all);
                return result;
            }
        }

        private static IDisposable AcquireCrossProcessLock()
        {
            Directory.CreateDirectory(ConnectionRegistry.Root());
            var lockPath = FilePath() + ".lock";
            for (var i = 0; i < 200; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            throw new IOException("Could not acquire the SQL-source lock within 3 seconds. Another Semanticus process is holding it. Try again.");
        }

        private static List<SqlSourceRecord> ReadForDisplay()
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return new List<SqlSourceRecord>();
                return JsonSerializer.Deserialize<List<SqlSourceRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<SqlSourceRecord>();
            }
            catch { return new List<SqlSourceRecord>(); }
        }

        // Same rule as the model registry: never Save over a file we could not read — move it aside first, so a
        // transient read error cannot destroy every saved source.
        private static List<SqlSourceRecord> ReadForWrite()
        {
            var p = FilePath();
            if (!File.Exists(p)) return new List<SqlSourceRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<SqlSourceRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<SqlSourceRecord>();
            }
            catch
            {
                var aside = p + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try { File.Move(p, aside); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable SQL-source file at {p} (could not preserve it: {ex.Message}).", ex); }
                return new List<SqlSourceRecord>();
            }
        }

        private static void Save(List<SqlSourceRecord> all)
        {
            // The same serialization-boundary scrub the model registry uses: whatever a future field holds, a secret
            // never reaches the disk.
            foreach (var r in all) XmlaAuthHint.ScrubStringProperties(r);

            Directory.CreateDirectory(ConnectionRegistry.Root());
            var tmp = FilePath() + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
            File.Move(tmp, FilePath(), overwrite: true);
        }
    }

    /// <summary>
    /// One model's SQL endpoint: the saved SQL source its checks read from unless a check says otherwise (Kane,
    /// 24 September 2026: "yes as recommended for sql end points"). Keyed by the model's DURABLE identity (the same
    /// "live:" or "disk:" key its saved checks and table mappings carry), never by a connection id, because the
    /// checks that resolve through it are keyed that way too.
    ///
    /// It names the source by id only. The source record itself stays one record in <c>sql-sources.json</c> even when
    /// several models share it, so re-pointing a source re-points every model that uses it.
    /// </summary>
    public sealed class ModelSqlEndpointLink
    {
        public string ModelIdentity { get; set; }
        public string SqlSourceId { get; set; }
        /// <summary>A readable name for the model, captured when the link was made. Display only.</summary>
        public string ModelName { get; set; }
        /// <summary>The saved connection this model was reached through, when one is known. Display only.</summary>
        public string ConnectionId { get; set; }
        /// <summary>human | agent | migration | workingCopy: who or what made the link.</summary>
        public string SetBy { get; set; }
        public string SetWhenUtc { get; set; }
    }

    /// <summary>
    /// The model-to-endpoint links, in their own <c>model-sql-endpoints.json</c> beside <c>connections.json</c>, with
    /// the same write discipline as the two registries above it: one writer at a time across processes, an
    /// unreadable file moved aside rather than overwritten, a scrub of every string before the bytes land, and an
    /// atomic temp-then-move.
    ///
    /// It also remembers which models have had the one-time upgrade pass (<see cref="MigrateOnce"/>), so a link the
    /// person cleared is never quietly put back by a later read.
    /// </summary>
    public static class ModelSqlEndpointRegistry
    {
        public const string FileName = "model-sql-endpoints.json";

        private sealed class Store
        {
            public int Version { get; set; } = 1;
            public List<ModelSqlEndpointLink> Links { get; set; } = new List<ModelSqlEndpointLink>();
            /// <summary>Model identities the upgrade pass has already looked at, linked or not.</summary>
            public List<string> Migrated { get; set; } = new List<string>();
            /// <summary>Per model identity, the saved checks that existed at the upgrade with no source and no address
            /// of their own. They read the model's partitions before the upgrade and keep doing so (Kane's rule: no
            /// check changes where it points). Checks saved after the upgrade are never added here.</summary>
            public Dictionary<string, List<string>> PinnedToPartitions { get; set; } = new Dictionary<string, List<string>>();
            /// <summary>Model identities whose link a person set or cleared, from the open model or from a row. The
            /// upgrade never links one of these on its own. Kept apart from <see cref="Migrated"/> because a choice
            /// made from a row has not read that model's checks, so it cannot count as the upgrade's look.</summary>
            public List<string> Decided { get; set; } = new List<string>();
            /// <summary>Per model identity, the unmapped tables whose row counts read the model's own partitions when
            /// the UPGRADE chose the endpoint. They keep reading them (Kane's rule). A person choosing the endpoint on
            /// purpose, or clearing it, lets them go.</summary>
            public Dictionary<string, List<string>> TablesOnPartitions { get; set; } = new Dictionary<string, List<string>>();
        }

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static string FilePath() => Path.Combine(ConnectionRegistry.Root(), FileName);
        internal static string StatePath() => FilePath();

        /// <summary>Every link, newest first. Read-only; an unreadable file reads as no links.</summary>
        public static IReadOnlyList<ModelSqlEndpointLink> List()
        {
            lock (Gate) return ReadForDisplay().Links;
        }

        public static ModelSqlEndpointLink Find(string modelIdentity) =>
            string.IsNullOrWhiteSpace(modelIdentity) ? null
                : List().FirstOrDefault(l => string.Equals(l.ModelIdentity, modelIdentity, StringComparison.Ordinal));

        public static bool IsMigrated(string modelIdentity)
        {
            if (string.IsNullOrWhiteSpace(modelIdentity)) return false;
            lock (Gate) return ReadForDisplay().Migrated.Contains(modelIdentity, StringComparer.Ordinal);
        }

        /// <summary>The saved check ids of one model that the upgrade pinned to the model's own partitions.</summary>
        public static HashSet<string> PinnedToPartitions(string modelIdentity)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(modelIdentity)) return set;
            lock (Gate)
                if (ReadForDisplay().PinnedToPartitions.TryGetValue(modelIdentity, out var ids) && ids != null)
                    set.UnionWith(ids.Where(i => !string.IsNullOrWhiteSpace(i)));
            return set;
        }

        /// <summary>The unmapped tables of one model whose row counts stay on the model's own partitions because the
        /// upgrade, not a person, chose its endpoint.</summary>
        public static HashSet<string> TablesOnPartitions(string modelIdentity)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(modelIdentity)) return set;
            lock (Gate)
                if (ReadForDisplay().TablesOnPartitions.TryGetValue(modelIdentity, out var tables) && tables != null)
                    set.UnionWith(tables.Where(t => !string.IsNullOrWhiteSpace(t)));
            return set;
        }

        /// <summary>The links that point at one saved source.</summary>
        public static ModelSqlEndpointLink[] LinksTo(string sqlSourceId) =>
            string.IsNullOrWhiteSpace(sqlSourceId) ? Array.Empty<ModelSqlEndpointLink>()
                : List().Where(l => string.Equals(l.SqlSourceId, sqlSourceId, StringComparison.OrdinalIgnoreCase)).ToArray();

        /// <summary>Link one model to one saved source, replacing any link it had. The caller checks the source
        /// exists; this file only records the choice.</summary>
        public static ModelSqlEndpointLink Set(string modelIdentity, string sqlSourceId, string modelName, string connectionId, string setBy,
            bool markLookedAt = true)
        {
            if (string.IsNullOrWhiteSpace(modelIdentity)) throw new ArgumentException("A SQL endpoint belongs to a saved model.", nameof(modelIdentity));
            if (string.IsNullOrWhiteSpace(sqlSourceId)) throw new ArgumentException("Name the saved SQL source to use.", nameof(sqlSourceId));
            return Mutate(store =>
            {
                store.Links.RemoveAll(l => string.Equals(l.ModelIdentity, modelIdentity, StringComparison.Ordinal));
                var link = new ModelSqlEndpointLink
                {
                    ModelIdentity = modelIdentity, SqlSourceId = sqlSourceId.Trim(), ModelName = modelName,
                    ConnectionId = connectionId, SetBy = setBy, SetWhenUtc = DateTimeOffset.UtcNow.ToString("O"),
                };
                store.Links.Insert(0, link);
                // A person chose this endpoint, so the tables the upgrade kept on the partitions follow it now.
                store.TablesOnPartitions.Remove(modelIdentity);
                MarkDecided(store, modelIdentity);
                // A link set from a row, with the model not open, has not seen that model's checks: leave it unlooked
                // so its first open still pins the checks that named no place.
                if (markLookedAt) MarkMigrated(store, modelIdentity);
                return link;
            });
        }

        /// <summary>Remove a model's link. False when it had none. The model is marked as decided, so the upgrade
        /// pass never puts a cleared link back. It is marked as looked at only when the caller has run the upgrade's
        /// look first (<paramref name="markLookedAt"/>, the open model): a clear from a row has not read that model's
        /// checks, so its first open still pins the ones that named no place (Sol 6, slice C).</summary>
        public static bool Clear(string modelIdentity, bool markLookedAt = true)
        {
            if (string.IsNullOrWhiteSpace(modelIdentity)) return false;
            return Mutate(store =>
            {
                var removed = store.Links.RemoveAll(l => string.Equals(l.ModelIdentity, modelIdentity, StringComparison.Ordinal)) > 0;
                store.TablesOnPartitions.Remove(modelIdentity);
                MarkDecided(store, modelIdentity);
                if (markLookedAt) MarkMigrated(store, modelIdentity);
                return removed;
            });
        }

        /// <summary>
        /// The one-time upgrade pass for one model, atomic under the file lock: when the model has no link and has
        /// never been looked at, link it to <paramref name="pickedSourceId"/> (when there is one) and mark it looked
        /// at either way. A model that already has a link, or was already looked at, is left exactly as it is, so
        /// two processes racing here cannot both decide.
        /// </summary>
        public static ModelSqlEndpointLink MigrateOnce(string modelIdentity, string pickedSourceId, string modelName, string connectionId,
            IEnumerable<string> sourcelessCheckIds = null, IEnumerable<string> unmappedTables = null)
        {
            if (string.IsNullOrWhiteSpace(modelIdentity)) return null;
            return Mutate(store =>
            {
                var existing = store.Links.FirstOrDefault(l => string.Equals(l.ModelIdentity, modelIdentity, StringComparison.Ordinal));
                var looked = store.Migrated.Contains(modelIdentity, StringComparer.Ordinal);
                MarkMigrated(store, modelIdentity);
                // Pin only on the FIRST look: that is the moment these checks stop being "checks that named nothing"
                // and become "checks that read the partitions". A link set from a row before that look is kept.
                if (!looked)
                {
                    var pins = (sourcelessCheckIds ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
                    if (pins.Count > 0) store.PinnedToPartitions[modelIdentity] = pins;
                }
                var decided = store.Decided.Contains(modelIdentity, StringComparer.Ordinal);
                if (existing != null || looked || decided || string.IsNullOrWhiteSpace(pickedSourceId)) return existing;
                var link = new ModelSqlEndpointLink
                {
                    ModelIdentity = modelIdentity, SqlSourceId = pickedSourceId, ModelName = modelName,
                    ConnectionId = connectionId, SetBy = "migration", SetWhenUtc = DateTimeOffset.UtcNow.ToString("O"),
                };
                store.Links.Insert(0, link);
                // The upgrade chose this endpoint, possibly from ONE table's mapping. Every unmapped table that read its
                // own partitions until now keeps doing so; only a table added later reads the endpoint (Sol 6, slice C).
                var tables = (unmappedTables ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (tables.Count > 0) store.TablesOnPartitions[modelIdentity] = tables;
                return link;
            });
        }

        /// <summary>Give <paramref name="toIdentity"/> the same endpoint as <paramref name="fromIdentity"/>, unless it
        /// already has one of its own or has made its own choice. A working copy uses the endpoint of the model it
        /// came from only until a person sets or clears the copy's endpoint: a cleared copy has no link but is in
        /// <see cref="Store.Decided"/>, and copying the source's link back on every reopen would undo the clear
        /// (Codex on pull request 394, F-264).</summary>
        public static ModelSqlEndpointLink CopyIfAbsent(string fromIdentity, string toIdentity, string modelName, string connectionId)
        {
            if (string.IsNullOrWhiteSpace(fromIdentity) || string.IsNullOrWhiteSpace(toIdentity)
                || string.Equals(fromIdentity, toIdentity, StringComparison.Ordinal)) return null;
            if (Find(fromIdentity) == null) return null;
            return Mutate(store =>
            {
                var from = store.Links.FirstOrDefault(l => string.Equals(l.ModelIdentity, fromIdentity, StringComparison.Ordinal));
                var existing = store.Links.FirstOrDefault(l => string.Equals(l.ModelIdentity, toIdentity, StringComparison.Ordinal));
                if (existing != null || from == null || store.Decided.Contains(toIdentity, StringComparer.Ordinal)) return existing;
                var link = new ModelSqlEndpointLink
                {
                    ModelIdentity = toIdentity, SqlSourceId = from.SqlSourceId, ModelName = modelName,
                    ConnectionId = connectionId, SetBy = "workingCopy", SetWhenUtc = DateTimeOffset.UtcNow.ToString("O"),
                };
                // Not marked as looked at: the copy's own checks have not been read here. Its first look still pins
                // the ones that named no place, and keeps this link because it already exists (Sol 6, slice C).
                store.Links.Insert(0, link);
                return link;
            });
        }

        private static void MarkMigrated(Store store, string modelIdentity)
        {
            if (!store.Migrated.Contains(modelIdentity, StringComparer.Ordinal)) store.Migrated.Add(modelIdentity);
        }

        private static void MarkDecided(Store store, string modelIdentity)
        {
            if (!store.Decided.Contains(modelIdentity, StringComparer.Ordinal)) store.Decided.Add(modelIdentity);
        }

        // ---- persistence (the ConnectionRegistry discipline, its own file) --------------------------------------

        private static T Mutate<T>(Func<Store, T> mutator)
        {
            lock (Gate)
            using (AcquireCrossProcessLock())
            {
                RegistryWriteGuard.Check(FilePath());
                var store = ReadForWrite();
                var result = mutator(store);
                Save(store);
                return result;
            }
        }

        private static IDisposable AcquireCrossProcessLock()
        {
            Directory.CreateDirectory(ConnectionRegistry.Root());
            var lockPath = FilePath() + ".lock";
            for (var i = 0; i < 200; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            throw new IOException("Could not acquire the SQL endpoint lock within 3 seconds. Another Semanticus process is holding it. Try again.");
        }

        private static Store Normalize(Store s)
        {
            s ??= new Store();
            s.Links = (s.Links ?? new List<ModelSqlEndpointLink>()).Where(l => l != null && !string.IsNullOrWhiteSpace(l.ModelIdentity)).ToList();
            s.Migrated ??= new List<string>();
            s.PinnedToPartitions ??= new Dictionary<string, List<string>>();
            s.Decided ??= new List<string>();
            s.TablesOnPartitions ??= new Dictionary<string, List<string>>();
            return s;
        }

        private static Store ReadForDisplay()
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return new Store();
                return Normalize(JsonSerializer.Deserialize<Store>(File.ReadAllText(p), JsonOpts));
            }
            catch { return new Store(); }
        }

        // Never Save over a file we could not read: move it aside first, so a transient read error cannot destroy
        // every link.
        private static Store ReadForWrite()
        {
            var p = FilePath();
            if (!File.Exists(p)) return new Store();
            try
            {
                return Normalize(JsonSerializer.Deserialize<Store>(File.ReadAllText(p), JsonOpts));
            }
            catch
            {
                var aside = p + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try { File.Move(p, aside); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable SQL endpoint file at {p} (could not preserve it: {ex.Message}).", ex); }
                return new Store();
            }
        }

        private static void Save(Store store)
        {
            foreach (var l in store.Links) XmlaAuthHint.ScrubStringProperties(l);
            Directory.CreateDirectory(ConnectionRegistry.Root());
            var tmp = FilePath() + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, JsonOpts));
            File.Move(tmp, FilePath(), overwrite: true);
        }
    }

    public static class ConnectionRegistry
    {
        public const int MaxRecords = 40;
        /// <summary>How many of the newest incidental local rows survive even when kept rows alone fill MaxRecords.</summary>
        public const int MinRecentLocal = 10;
        private const string FileName = "connections.json";

        /// <summary>The labels a user may declare. Anything else — including a value hand-edited into the file — is not
        /// a label this product understands, and <see cref="EffectiveLabel"/> reads it as the strictest option.</summary>
        public static readonly string[] Labels = { "local", "dev", "uat", "prod" };

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Test seam: point the registry at a scratch dir so tests never touch the real home.</summary>
        public static string RootOverride { get; set; }

        public static string Root() => RootOverride ?? SemanticusHome.Root();

        private static string FilePath() => Path.Combine(Root(), FileName);
        internal static string StatePath() => FilePath();

        // Fail CLOSED on the origin: only an explicit "human" may perform a governance mutation. An unrecognised or
        // omitted origin is NOT trusted — a forgotten argument must never grant human rights. (The MCP door passes
        // "agent", the RPC/UI door passes "human"; there is no third caller, so this only ever hardens.)
        private static bool IsHuman(string origin) => string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The label a policy must gate on — and the ONLY reading a policy is allowed to use (never raw
        /// <see cref="ModelConnectionRecord.Label"/>, which can hold anything a hand-edited file put there). An
        /// unlabelled target, or one carrying a value outside <see cref="Labels"/>, is <b>prod</b>: the strictest
        /// option, and the only inference we make. We never read a label out of an endpoint name — one real client's
        /// <c>UAT_SEM</c> IS their dev workspace, and someone else's <c>SEM_TEST</c> is what the board reads. A name is
        /// a convention, not a fact about risk; the label is a declaration by the user, never a detection by us.
        /// </summary>
        public static string EffectiveLabel(ModelConnectionRecord r)
        {
            var l = r?.Label?.Trim().ToLowerInvariant();
            return !string.IsNullOrEmpty(l) && Labels.Contains(l) ? l : "prod";
        }

        public static bool IsUnlabelled(ModelConnectionRecord r) =>
            !(Labels.Contains(r?.Label?.Trim().ToLowerInvariant() ?? ""));

        /// <summary>
        /// The workspace an XMLA endpoint points at, in WORDS — "Contoso Fabric Monitoring", never the stored
        /// "Contoso%20Fabric%20Monitoring". A workspace-level target has no dataset to name it by, and the encoded last
        /// path segment is what every surface used to fall back to (Kane, 2026-09-15). This is the one honest name such
        /// a record has, so it is derived here, once, and read by both doors rather than re-invented per surface.
        ///
        /// Anchored to a TRAILING <c>/myorg/&lt;name&gt;</c> so a deeper path is never mistaken for the workspace, and
        /// malformed percent-encoding returns null rather than the raw segment: showing "Bad%ZZ" as a name is the exact
        /// defect being fixed, not a lesser version of it. Null for anything else (a local data source has no
        /// workspace). The webview's helper of the same name mirrors these rules so one endpoint reads as one string.
        /// </summary>
        public static string WorkspaceNameFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            var trimmed = endpoint.Trim().TrimEnd('/');
            const string marker = "/myorg/";
            var at = trimmed.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            var segment = trimmed.Substring(at + marker.Length);
            if (segment.Length == 0 || segment.IndexOfAny(new[] { '/', '?', '#' }) >= 0) return null;
            for (var i = 0; i < segment.Length; i++)
            {
                if (segment[i] != '%') continue;
                if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2])) return null;
            }
            return Uri.UnescapeDataString(segment);
        }

        // Length-prefix each component before hashing, so no pair of (kind, endpoint, database) can collide by moving a
        // delimiter across the boundary — e.g. endpoint "a|b"+db "c" must not hash the same as endpoint "a"+db "b|c".
        internal static string IdFor(string kind, string endpoint, string database)
        {
            var sb = new StringBuilder();
            foreach (var part in new[] { kind, endpoint, database })
                sb.Append((part ?? "").Length).Append(':').Append(part ?? "").Append('\0');
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>Newest first. Read-only; never writes, so a parse failure just yields an empty list.</summary>
        public static IReadOnlyList<ModelConnectionRecord> List()
        {
            lock (Gate) return ReadForDisplay();
        }

        public static ModelConnectionRecord Find(string id) =>
            string.IsNullOrWhiteSpace(id) ? null : List().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Find a target by its endpoint (+ dataset), independent of kind — for label resolution, where all we
        /// have is where a write/query is headed. Null when we've never connected there, which the caller reads as
        /// unlabelled ⇒ prod.</summary>
        public static ModelConnectionRecord FindByEndpoint(string endpoint, string database) =>
            string.IsNullOrWhiteSpace(endpoint) ? null : List().FirstOrDefault(r =>
                string.Equals(r.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Database ?? "", database ?? "", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Record a successful connect/open. Idempotent on (kind, endpoint, database): an existing record is refreshed
        /// and moved to the front, and its user-owned fields — <see cref="ModelConnectionRecord.Label"/> and
        /// <see cref="ModelConnectionRecord.WorkingFolder"/> — are PRESERVED. Connecting again must never silently
        /// unlabel a target: that would turn a `prod` declaration back into an inference.
        /// </summary>
        public static ModelConnectionRecord Remember(string kind, string endpoint, string database,
            string modelName = null, string tenantId = null, string authMode = null, string lastAccount = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("A connection needs an endpoint.", nameof(endpoint));

            // Last line of defence at the persistence boundary: reduce a pasted connection string to its safe address +
            // catalog and DROP any credential, so connections.json can never hold a Password=/token=, whatever the caller
            // passed. A bare endpoint passes through unchanged (stable id); only a credential-bearing input is rewritten.
            var coords = ConnectionInput.Parse(endpoint, database);
            // FAIL CLOSED: an input that carried a credential and could NOT be reduced to a recognized address is refused
            // outright — never write a raw, unparsed, possibly-secret-bearing string to disk. (A clean connection string
            // whose credential we cleanly stripped is Safe and proceeds; only the unparseable credential shape is refused.)
            if (!coords.Safe)
                throw new ArgumentException("This endpoint could not be reduced to a safe address without a credential. Pass the bare address only (e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace) and a dataset name; Semanticus signs in for you and never stores a credential.", nameof(endpoint));
            // Take the sanitized coordinates as-is — never fall back to the raw `database` argument, which the parser may
            // have DROPPED precisely because it hid a secret ("Sales;Password=SECRET").
            endpoint = coords.Endpoint;
            database = coords.Database;
            // Belt-and-braces: a Safe coordinate is already secret-free, but never write an endpoint the detector still
            // flags (a residual redacted "…&token=***" query key) as if it were a plain address — redact it.
            if (XmlaAuthHint.ContainsSecret(endpoint)) endpoint = ConnectionInput.Redacted;

            return Mutate(all =>
            {
                var id = IdFor(kind, endpoint, database);
                var rec = all.FirstOrDefault(r => r.Id == id);
                if (rec == null) rec = new ModelConnectionRecord { Id = id, Kind = kind, Endpoint = endpoint, Database = database };
                else all.Remove(rec);

                // A record that RESOLVED a dataset always carries a readable name, even when the caller passed none.
                // The two live-open sites already pass the database as the name; doing it here instead makes it true of
                // every caller, including remember_xmla_connection — the route Kane's encoded-name row came in by.
                // (Every workspace name in this file is the Contoso placeholder: the public-mirror gate refuses the real
                // tenant name, so the literal Kane saw is not written down here.) A caller-supplied name still wins, and a WORKSPACE-only record (no dataset) invents
                // nothing: its readable name comes from WorkspaceNameFromEndpoint at display time, not from storage.
                rec.ModelName = modelName ?? rec.ModelName ?? (string.IsNullOrWhiteSpace(database) ? null : database);
                rec.TenantId = tenantId ?? rec.TenantId;
                rec.AuthMode = authMode ?? rec.AuthMode;
                // Preserve a known account when this connect can't name one (azcli/sp) — like the label, a display hint the
                // user earned by signing in once shouldn't be erased by a later headless connect to the same target.
                rec.LastAccount = string.IsNullOrWhiteSpace(lastAccount) ? rec.LastAccount : lastAccount;
                rec.LastUsedUtc = DateTimeOffset.UtcNow.ToString("O");
                rec.UseCount++;

                all.Insert(0, rec);
                return rec;
            });
        }

        /// <summary>
        /// Declare what a target is. <paramref name="origin"/> is required and REFUSED when it is the agent: a label is
        /// a governance statement, and an agent that can relabel prod as dev has defeated the matrix it is gated by.
        /// Pass a null/empty label to clear it — which returns the target to unlabelled, i.e. treated as prod.
        /// </summary>
        public static ModelConnectionRecord SetLabel(string id, string label, string origin)
        {
            if (!IsHuman(origin))
                throw new InvalidOperationException("Only a human can label a target. An agent cannot. Labelling declares what an environment is, and the agent's permissions are gated on that label. Ask the user to set it.");

            var normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim().ToLowerInvariant();
            if (normalized != null && !Labels.Contains(normalized))
                throw new ArgumentException($"'{label}' is not a target label. Use one of: {string.Join(", ", Labels)} (or clear it, which means the target is treated as prod).");

            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("No connection with that id. Open Connections to see what is known.");
                rec.Label = normalized;
                return rec;
            });
        }

        /// <summary>Set (or clear) the durable local folder this connection's editable copy lives in.</summary>
        public static ModelConnectionRecord SetWorkingFolder(string id, string folder)
        {
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("No connection with that id.");
                rec.WorkingFolder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
                return rec;
            });
        }

        /// <summary>Link a live source to its durable local copy and, independently, the XMLA destination that copy
        /// will publish to. The publish id lives in the machine registry, never in the user's model or repository.</summary>
        public static ModelConnectionRecord SetWorkingCopy(string id, string folder, string publishConnectionId)
        {
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("No source connection with that id.");
                ModelConnectionRecord publish = null;
                if (!string.IsNullOrWhiteSpace(publishConnectionId))
                {
                    publish = all.FirstOrDefault(r => string.Equals(r.Id, publishConnectionId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("No publish connection with that id.");
                    if (!string.Equals(publish.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(ConnectionJobs.PublishNotWorkspace);
                }
                rec.WorkingFolder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
                rec.PublishConnectionId = publish?.Id;
                if (publish != null) rec.PublishCleared = null;   // a chosen destination is the answer again
                return rec;
            });
        }

        /// <summary>Let go of this source's publish destination: no explicit link, and no default back to the source
        /// either. The working folder stays linked. Returns the record, or throws when the id is not known.</summary>
        public static ModelConnectionRecord ClearPublish(string id)
        {
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("No source connection with that id.");
                rec.PublishConnectionId = null;
                rec.PublishCleared = true;
                return rec;
            });
        }

        /// <summary>Remove a connection. <paramref name="origin"/> is required: an agent may forget an unlabelled
        /// scratch connection, but NOT one the user labelled — the label is governance data, not the agent's to drop.
        /// (Forgetting fails closed regardless: a forgotten target is unlabelled, hence prod, hence stricter.)</summary>
        public static bool Forget(string id, string origin)
        {
            return Mutate(all =>
            {
                var rec = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (rec == null) return false;
                if (!IsHuman(origin) && !IsUnlabelled(rec))
                    throw new InvalidOperationException("Only a human can forget a labelled target. Its label is governance data, not the agent's to drop. Ask the user.");
                all.Remove(rec);
                foreach (var source in all.Where(r => string.Equals(r.PublishConnectionId, rec.Id, StringComparison.OrdinalIgnoreCase)))
                    source.PublishConnectionId = null;
                return true;
            });
        }

        // ---- persistence -------------------------------------------------------------------------------------

        // Every mutation runs inside BOTH the in-process gate AND a cross-process file lock, and re-reads the file
        // under that lock right before writing. Two engine processes share this file (the VS Code extension and the
        // MCP server each own one), so an in-process lock alone would let process B's Remember() clobber the
        // SetLabel("prod") process A just wrote — a target the user tightened silently loosening again.
        private static T Mutate<T>(Func<List<ModelConnectionRecord>, T> mutator)
        {
            lock (Gate)
            using (AcquireCrossProcessLock())
            {
                RegistryWriteGuard.Check(FilePath());
                var all = ReadForWrite(out var unreadable);
                var result = mutator(all);
                Save(all, unreadable);
                return result;
            }
        }

        private static IDisposable AcquireCrossProcessLock()
        {
            Directory.CreateDirectory(Root());
            var lockPath = FilePath() + ".lock";
            for (var i = 0; i < 200; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            // THROW rather than proceed unlocked (same rule as HomeFile). The old "proceed after 1.5s" fallback was
            // NOT fail-closed as its comment claimed: an unlocked stale write doesn't just drop labels, it can
            // RESURRECT a weaker one — process B re-saving its pre-SetLabel snapshot silently reverts the "prod" the
            // user just declared. A rare thrown retry under contention is recoverable; a silent downgrade is not.
            throw new IOException("Could not acquire the connection-registry lock within 3 seconds. Another Semanticus process is holding it. Try again.");
        }

        // Read for a read-only caller: any failure degrades to "no history" (the pre-registry behaviour). Never writes.
        // An unreadable file shows the previous good copy (.bak) instead, the same rows the next write will recover.
        private static List<ModelConnectionRecord> ReadForDisplay()
        {
            var p = FilePath();
            try
            {
                if (!File.Exists(p)) return new List<ModelConnectionRecord>();
                return JsonSerializer.Deserialize<List<ModelConnectionRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<ModelConnectionRecord>();
            }
            catch { return ReadBackup(p) ?? new List<ModelConnectionRecord>(); }
        }

        // The previous good copy, or null when there is none or it does not parse either. Every row in it comes back
        // labelled prod (RaiseRecoveredLabels), because .bak is one write behind the file it backs up.
        private static List<ModelConnectionRecord> ReadBackup(string p)
        {
            try
            {
                var bak = p + ".bak";
                return File.Exists(bak) ? RaiseRecoveredLabels(JsonSerializer.Deserialize<List<ModelConnectionRecord>>(File.ReadAllText(bak), JsonOpts)) : null;
            }
            catch { return null; }
        }

        // .bak holds the list as it was BEFORE the last write, and that write may have been the person tightening a
        // label (dev, then prod), or labelling a row for the first time. Handing .bak's label back, or its missing label,
        // would loosen the declaration the agent policy gates on, and the next write would make it permanent (found by
        // Sol, 27 Sep). So EVERY recovered row comes back labelled prod: the strictest label, which the person can loosen
        // again by hand. A label, not unlabelled, so an agent cannot forget the target either: an unlabelled row may be
        // forgotten by an agent (Forget), and the row .bak holds unlabelled may be one the person had just labelled prod
        // (found by Sol, 28 Sep, round 4). An unlabelled loopback row would also read as "local" to the gate
        // (LocalEngine.ResolveTargetLabel).
        private static List<ModelConnectionRecord> RaiseRecoveredLabels(List<ModelConnectionRecord> rows)
        {
            if (rows == null) return null;
            foreach (var r in rows)
                if (r != null) r.Label = "prod";
            return rows;
        }

        // Read on the WRITE path. Distinct from the display read because a write follows: if the file exists but cannot
        // be read or parsed, we must NOT return empty and then Save over it — that would destroy the user's labels on a
        // transient read error or a denied ACL. The rows start from the previous good copy (.bak) when there is one: a
        // torn write (a power cut after the rename, before the bytes reached the disk) leaves exactly that state, and
        // starting from empty lost every saved row two writes later, once .bak had been rotated too.
        // <paramref name="unreadable"/> tells Save to set the unreadable file ASIDE (never over .bak) in the same step that
        // puts the new list in place. Nothing is moved here: a write that is refused or fails after this read (an agent
        // forgetting a labelled target, a full disk) used to leave no file at all, so the list showed nothing and the
        // next two writes rotated the good .bak away (27 Sep, round 3). Untouched, the torn file keeps the display read
        // and the next write on .bak.
        private static List<ModelConnectionRecord> ReadForWrite(out bool unreadable)
        {
            unreadable = false;
            var p = FilePath();
            if (!File.Exists(p)) return new List<ModelConnectionRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<ModelConnectionRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<ModelConnectionRecord>();
            }
            catch
            {
                unreadable = true;
                return ReadBackup(p) ?? new List<ModelConnectionRecord>();
            }
        }

        private static void Save(List<ModelConnectionRecord> all, bool setAsideUnreadable = false)
        {
            // Retention trims ONLY incidental local rows: a .bim/.pbip/TMDL folder that was opened, or a Power BI
            // Desktop instance the discoverer saw. Everything else is kept whatever the count: a cloud (XMLA)
            // connection, which the person connected to or remembered on purpose (remember_xmla_connection), and any
            // row the person invested in by labelling it, giving it a working folder, or naming it as a publish
            // destination. Before 27 Sep a flood of local opens (an agent or test grading copies under /tmp) could push
            // cloud rows out once there were more of them than the budget left, with no copy kept (D-173 only ordered
            // them ahead of local rows). Silently dropping a `prod` label would also turn a declaration back into an
            // inference, which is the one thing this file exists to prevent.
            var linkedPublishIds = all.Where(r => !string.IsNullOrWhiteSpace(r.PublishConnectionId))
                .Select(r => r.PublishConnectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool IsIncidentalLocal(ModelConnectionRecord r) =>
                string.Equals(r.Kind, "file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase);

            bool IsEvictable(ModelConnectionRecord r) =>
                IsIncidentalLocal(r) && IsUnlabelled(r) && string.IsNullOrEmpty(r.WorkingFolder) && !linkedPublishIds.Contains(r.Id);

            if (all.Count > MaxRecords)
            {
                // The kept rows can exceed MaxRecords on their own; the newest MinRecentLocal local rows still survive
                // then, so the model just opened can always be found again by its id.
                var kept = all.Count(r => !IsEvictable(r));
                var localBudget = Math.Max(MaxRecords - kept, MinRecentLocal);
                var evictableSeen = 0;
                // `all` is newest first, so the oldest evictable rows are the ones past the budget.
                all = all.Where(r => !IsEvictable(r) || evictableSeen++ < localBudget).ToList();
            }

            // Serialization-boundary scrub (CRITICAL 1): redact any secret material from EVERY string field of every
            // record right before the bytes are written — modelName, a secret-shaped tenant, or a field added later that
            // a per-call-site sanitizer would miss. Endpoint/database are already reduced by ConnectionInput.Parse; this
            // is the chokepoint that makes a future regression impossible, not a substitute for that earlier parse.
            foreach (var r in all) XmlaAuthHint.ScrubStringProperties(r);

            Directory.CreateDirectory(Root());
            var tmp = FilePath() + ".tmp";
            WriteFlushed(tmp, JsonSerializer.Serialize(all, JsonOpts));
            if (setAsideUnreadable)
            {
                // Preserve the unreadable bytes as the new list takes their place. If that fails, ABORT: never overwrite
                // a file we could not read (same rule as HomeFile), and never rotate it into .bak, which holds the good copy.
                try { ReplaceSettingAside(tmp, FilePath()); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable connection registry at {FilePath()} (could not preserve it: {ex.Message}).", ex); }
            }
            else ReplaceKeepingBackup(tmp, FilePath());   // a crash mid-write leaves the old file intact + a .tmp, never a truncated registry
        }

        /// <summary>Write <paramref name="text"/> and flush it to the disk before the caller renames it into place. A
        /// rename is journalled while file data often is not (NTFS, ext4 data=writeback), so without the flush a power
        /// cut can leave the renamed file holding zeros: the torn list ReadForWrite recovers from.</summary>
        internal static void WriteFlushed(string path, string text)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        /// <summary>Put <paramref name="tmp"/> in place of <paramref name="path"/>, keeping the file it replaces as
        /// <c>&lt;path&gt;.bak</c>: ONE previous copy, so a bad write (a wrong eviction, a buggy caller) can be undone by
        /// hand. Both per-user lists are capped and rolled entries used to be gone for good. The swap is still atomic:
        /// File.Replace renames, and the first write (nothing to keep yet) is a plain move.</summary>
        internal static void ReplaceKeepingBackup(string tmp, string path)
        {
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }

        /// <summary>Put <paramref name="tmp"/> in place of an UNREADABLE <paramref name="path"/>, keeping the unreadable
        /// bytes as <c>&lt;path&gt;.corrupt-&lt;id&gt;</c> for a curious person and leaving <c>.bak</c>, the good copy the new
        /// list was built from, alone. One File.Replace, so there is no moment with no file at <paramref name="path"/>:
        /// the torn file stays until the new one is in place (Unix links the backup, then renames over the path).</summary>
        internal static void ReplaceSettingAside(string tmp, string path)
        {
            var aside = path + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (File.Exists(path)) File.Replace(tmp, path, aside, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }
}
