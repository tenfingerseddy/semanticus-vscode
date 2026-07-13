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
        public string LastUsedUtc { get; set; }
        public int UseCount { get; set; }
    }

    public static class ConnectionRegistry
    {
        public const int MaxRecords = 40;
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

        public static string Root() => RootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus");

        private static string FilePath() => Path.Combine(Root(), FileName);

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
            string modelName = null, string tenantId = null, string authMode = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("A connection needs an endpoint.", nameof(endpoint));

            return Mutate(all =>
            {
                var id = IdFor(kind, endpoint, database);
                var rec = all.FirstOrDefault(r => r.Id == id);
                if (rec == null) rec = new ModelConnectionRecord { Id = id, Kind = kind, Endpoint = endpoint, Database = database };
                else all.Remove(rec);

                rec.ModelName = modelName ?? rec.ModelName;
                rec.TenantId = tenantId ?? rec.TenantId;
                rec.AuthMode = authMode ?? rec.AuthMode;
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
                        throw new InvalidOperationException("The final publish destination must be an XMLA connection.");
                }
                rec.WorkingFolder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
                rec.PublishConnectionId = publish?.Id;
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
                var all = ReadForWrite();
                var result = mutator(all);
                Save(all);
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
        private static List<ModelConnectionRecord> ReadForDisplay()
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return new List<ModelConnectionRecord>();
                return JsonSerializer.Deserialize<List<ModelConnectionRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<ModelConnectionRecord>();
            }
            catch { return new List<ModelConnectionRecord>(); }
        }

        // Read on the WRITE path. Distinct from the display read because a write follows: if the file exists but cannot
        // be read or parsed, we must NOT return empty and then Save over it — that would destroy the user's labels on a
        // transient read error or a denied ACL. Move the unreadable file ASIDE first (preserving it for recovery), so
        // the subsequent Save writes a fresh file and nothing is silently lost.
        private static List<ModelConnectionRecord> ReadForWrite()
        {
            var p = FilePath();
            if (!File.Exists(p)) return new List<ModelConnectionRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<ModelConnectionRecord>>(File.ReadAllText(p), JsonOpts) ?? new List<ModelConnectionRecord>();
            }
            catch
            {
                // Preserve the unreadable bytes BEFORE any write can replace them. If even the move fails, ABORT —
                // the old comment claimed "Save below will surface its own IO error", which was false: Save's atomic
                // temp+move succeeds regardless of whether the original parsed, so a swallowed failure here silently
                // destroyed the user's labels. Same rule as HomeFile: never overwrite a file we could not read.
                var aside = p + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try { File.Move(p, aside); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable connection registry at {p} (could not preserve it: {ex.Message}).", ex); }
                return new List<ModelConnectionRecord>();
            }
        }

        private static void Save(List<ModelConnectionRecord> all)
        {
            // Retention evicts the least-recently-used — but NEVER a record the user has invested in by labelling it or
            // giving it a working folder. Silently dropping a `prod` label would turn a declaration back into an
            // inference, which is the one thing this file exists to prevent.
            if (all.Count > MaxRecords)
            {
                var keep = all.Take(MaxRecords).ToList();
                var linkedPublishIds = all.Where(r => !string.IsNullOrWhiteSpace(r.PublishConnectionId))
                    .Select(r => r.PublishConnectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                keep.AddRange(all.Skip(MaxRecords).Where(r => !IsUnlabelled(r) || !string.IsNullOrEmpty(r.WorkingFolder)
                    || linkedPublishIds.Contains(r.Id)));
                all = keep;
            }

            Directory.CreateDirectory(Root());
            var tmp = FilePath() + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
            File.Move(tmp, FilePath(), overwrite: true);   // a crash mid-write leaves the old file intact + a .tmp, never a truncated registry
        }
    }
}
