using System;
using System.IO;

namespace Semanticus.Engine
{
    /// <summary>One side of the connection mental model: what is being edited or what answers queries.</summary>
    public sealed class ConnectionContextModel
    {
        public bool Available { get; set; }
        public string ModelName { get; set; }
        public string Source { get; set; }
        public string ConnectionId { get; set; }
        public string SourceConnectionId { get; set; }
        public string Kind { get; set; }
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public string AuthMode { get; set; }
        public string Account { get; set; }      // the account (UPN) last signed in to this side, when known — the identity bar's "as <account>"
        public string TenantId { get; set; }     // the tenant this side authenticated against, when known
        public string Label { get; set; }
        public string EffectiveLabel { get; set; }
        public bool Unlabelled { get; set; }
        public string WorkingFolder { get; set; }
        public bool Live { get; set; }
        public bool SourceControlled { get; set; }
        public string RepositoryRoot { get; set; }
    }

    /// <summary>One target's silently-probed account (UPN) — what a picker shows as "as &lt;account&gt;" before a click.
    /// <see cref="Account"/> is what the NEXT open will actually use, and it is ONLY a live MSAL sign-in record on this
    /// device (a sign-in is remembered per (client, tenant), so that record IS the next identity). It is NULL — "account
    /// unknown" — when no record exists: a family that keeps none (azcli/serviceprincipal), or a deleted/corrupt record.
    /// It is NEVER guessed from the per-target last-used hint, which a later open won't necessarily use.
    /// <see cref="PreviousAccount"/> is the per-target last-used account, surfaced ONLY as provenance ("last opened as
    /// &lt;x&gt;") when it differs from <see cref="Account"/> — never as the prediction. Holds no credential; a missing
    /// entry means nothing is known about the target, not that it is signed out.</summary>
    public sealed class ConnectionAccountProbe
    {
        public string Id { get; set; }
        public string Account { get; set; }
        public string PreviousAccount { get; set; }
        public string TenantId { get; set; }
    }

    /// <summary>One prerequisite an ADVANCED sign-in mode needs (e.g. service-principal's client id) — PRESENCE + NAME
    /// only, never a secret VALUE. <see cref="EnvNames"/> lists every accepted env-var name (aliases) for this one
    /// requirement; <see cref="Present"/> is true when ANY of them is set (or, for a Tenant requirement, when the
    /// caller separately supplied a tenant id). A UI reads this to say which name(s) are still missing.</summary>
    public sealed class AuthPrereqRequirement
    {
        public string Label { get; set; }
        public string[] EnvNames { get; set; }
        public bool Present { get; set; }
    }

    /// <summary>Read-only PREVIEW of whether an ADVANCED sign-in mode can actually authenticate on this machine, so a
    /// picker can warn BEFORE a connect attempt instead of failing after one. PRESENCE + NAMES ONLY — NEVER a secret
    /// value — and the probe itself NEVER triggers an interactive prompt or a network sign-in (local reads, plus at
    /// most a local <c>az account show</c> for the azcli mode). For serviceprincipal, <see cref="Ready"/> is true when
    /// every <see cref="Requirements"/> entry is Present; for azcli it instead reflects whether a signed-in CLI session
    /// was detected (<see cref="CliAccount"/>/<see cref="CliTenant"/> then name it). <see cref="Detail"/> carries an
    /// honest, actionable note when NOT ready (e.g. "Run az login"). <see cref="KeyVaultSupported"/> is always false
    /// today — there is no Key Vault-backed credential source yet.</summary>
    public sealed class AuthPrerequisites
    {
        public string Mode { get; set; }
        public bool Ready { get; set; }
        public AuthPrereqRequirement[] Requirements { get; set; } = System.Array.Empty<AuthPrereqRequirement>();
        public string CliAccount { get; set; }
        public string CliTenant { get; set; }
        public string Detail { get; set; }
        public bool KeyVaultSupported { get; set; }   // always false today
    }

    /// <summary>
    /// Engine-owned explanation of the two identities a user may deliberately have in play. Relationship is one of
    /// none | editingOnly | queryingOnly | sameInstance | workingCopyAndPublished |
    /// workingCopyAndLocalRuntime | twoModels.
    /// </summary>
    public sealed class ConnectionContext
    {
        public ConnectionContextModel Editing { get; set; } = new ConnectionContextModel();
        public ConnectionContextModel Querying { get; set; } = new ConnectionContextModel();
        public ConnectionContextModel Publishing { get; set; } = new ConnectionContextModel();
        // The "copy FROM" model the Reference tree is browsing, when one is bound (engine-owned so BOTH the native
        // Reference tree and the Connections drawer name the same reference). Available=false when none is set.
        public ConnectionContextModel Reference { get; set; } = new ConnectionContextModel();
        public string Relationship { get; set; }
        public bool TwoModelsInPlay { get; set; }
        public bool PublishDestinationSeparateFromQuerying { get; set; }
        public string Summary { get; set; }
        /// <summary>The open model's SQL endpoint (contract 19.3), or null when it has none.</summary>
        public SqlEndpointInfo SqlEndpoint { get; set; }
        /// <summary>When the open model has no endpoint: the SQL endpoint its own sources read from, offered first
        /// by the add form ("Found in this model's sources"). Null when there is an endpoint or nothing to offer.</summary>
        public SqlEndpointSuggestion SqlEndpointSuggestion { get; set; }
        /// <summary>A plain sentence about the call that returned this context, when it has something to say (for
        /// example that clearing changed nothing because nothing was set). Null otherwise.</summary>
        public string Note { get; set; }
    }

    /// <summary>A model's SQL endpoint as both doors read it: the saved source it links to, named.</summary>
    public sealed class SqlEndpointInfo
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        /// <summary>The source's last connection test, or null when it has never been tested. Never rounded into ok.</summary>
        public SqlEndpointLastTest LastTest { get; set; }
        /// <summary>True when the link names a source that is no longer saved. Checks that would read through it say
        /// so rather than quietly reading somewhere else.</summary>
        public bool Missing { get; set; }
        /// <summary>human | agent | migration | workingCopy.</summary>
        public string SetBy { get; set; }
        public string SetWhenUtc { get; set; }
    }

    public sealed class SqlEndpointLastTest
    {
        public string TestedUtc { get; set; }
        public bool Ok { get; set; }
    }

    /// <summary>The endpoint a model's own partitions read from, and the saved source that already points there.</summary>
    public sealed class SqlEndpointSuggestion
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string MatchingSourceId { get; set; }
        public string MatchingSourceName { get; set; }
    }

    /// <summary>What set_model_sql_endpoint did.</summary>
    public sealed class ModelSqlEndpointResult
    {
        public string ModelName { get; set; }
        /// <summary>The saved connection the model was named by, when it was named that way or is one.</summary>
        public string ConnectionId { get; set; }
        /// <summary>The endpoint now in force, or null when it was cleared.</summary>
        public SqlEndpointInfo SqlEndpoint { get; set; }
        /// <summary>False when the call changed nothing (for example clearing a model that had no endpoint).</summary>
        public bool Changed { get; set; }
        public string Note { get; set; }
    }

    /// <summary>One model's endpoint, for the Connections list.</summary>
    public sealed class ModelSqlEndpointRow
    {
        public string ModelName { get; set; }
        public string ConnectionId { get; set; }
        public SqlEndpointInfo SqlEndpoint { get; set; }
    }

    /// <summary>A saved SQL source that no model uses as its endpoint, with what the open model still points at it.</summary>
    public sealed class UnlinkedSqlSource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public int ChecksUsing { get; set; }
        public int TableMappingsUsing { get; set; }
        /// <summary>What the two counts cover. "savedModels" means every saved model's checks and table mappings were
        /// read, open or not; a surface offers Remove only on a zero with this scope. Any other value, or none, is a
        /// count that does not prove the source is unused.</summary>
        public string UsageScope { get; set; }
        /// <summary>The saved models whose checks or table mappings use this source, by name, sorted.</summary>
        public string[] UsingModels { get; set; } = Array.Empty<string>();
    }

    public sealed class ModelSqlEndpointList
    {
        public ModelSqlEndpointRow[] Rows { get; set; } = Array.Empty<ModelSqlEndpointRow>();
        /// <summary>Listed once each: "Not linked to a model".</summary>
        public UnlinkedSqlSource[] NotLinked { get; set; } = Array.Empty<UnlinkedSqlSource>();
        public string Note { get; set; }
    }

    internal static class ConnectionContextBuilder
    {
        public static ConnectionContext Build(Session session, string modelName, LiveConnection live)
        {
            var editingRecord = FindEditingRecord(session);
            var queryRecord = live == null ? null : ConnectionRegistry.FindByEndpoint(live.DataSource, live.Database);
            var editing = EditingSide(session, modelName, editingRecord);
            var querying = QueryingSide(live, queryRecord);
            var publishing = PublishingSide(session, editingRecord);
            var relationship = RelationshipFor(session, live, editingRecord, queryRecord);
            var two = relationship == "workingCopyAndPublished" || relationship == "workingCopyAndLocalRuntime" || relationship == "twoModels";
            return new ConnectionContext
            {
                Editing = editing,
                Querying = querying,
                Publishing = publishing,
                Relationship = relationship,
                TwoModelsInPlay = two,
                PublishDestinationSeparateFromQuerying = publishing.Available && (!querying.Available || !Same(publishing.ConnectionId, querying.ConnectionId)),
                Summary = SummaryFor(relationship, editing, querying, publishing)
            };
        }

        internal static ModelConnectionRecord FindEditingRecord(Session session)
        {
            if (session?.LiveOrigin != null)
                return ConnectionRegistry.FindByEndpoint(session.LiveOrigin.Endpoint, session.LiveOrigin.Database);
            if (string.IsNullOrWhiteSpace(session?.SourcePath)) return null;
            foreach (var record in ConnectionRegistry.List())
                if (IsWithin(session.SourcePath, record.WorkingFolder)) return record;
            return null;
        }

        private static ConnectionContextModel EditingSide(Session session, string modelName, ModelConnectionRecord record)
        {
            if (session == null) return new ConnectionContextModel { Available = false };
            var origin = session.LiveOrigin;
            var side = origin == null ? new ConnectionContextModel
            {
                Available = true,
                ModelName = modelName,
                Source = session.SourcePath,
                Kind = "file",
                WorkingFolder = record?.WorkingFolder,
                SourceConnectionId = record?.Id,
                Live = false
            } : FromRecord(record, new ConnectionContextModel
            {
                Available = true,
                ModelName = modelName,
                Source = origin.Endpoint,
                Kind = record?.Kind ?? KindFor(origin.Endpoint),
                Endpoint = origin.Endpoint,
                Database = origin.Database,
                Live = true
            });
            side.RepositoryRoot = FindRepositoryRoot(session.SourcePath);
            side.SourceControlled = side.RepositoryRoot != null;
            return side;
        }

        private static ConnectionContextModel QueryingSide(LiveConnection live, ModelConnectionRecord record)
        {
            if (live == null) return new ConnectionContextModel { Available = false };
            return FromRecord(record, new ConnectionContextModel
            {
                Available = true,
                ModelName = NameOrWorkspace(record?.ModelName ?? live.Database, live.DataSource),
                Source = live.DataSource,
                Kind = record?.Kind ?? live.Kind,
                Endpoint = live.DataSource,
                Database = live.Database,
                Live = true
            });
        }

        private static ConnectionContextModel PublishingSide(Session session, ModelConnectionRecord editingRecord)
        {
            if (session == null) return new ConnectionContextModel { Available = false };
            ModelConnectionRecord publish = null;
            if (session.LiveOrigin != null && !LiveDeploy.IsLocalEndpoint(session.LiveOrigin.Endpoint))
                publish = ConnectionRegistry.FindByEndpoint(session.LiveOrigin.Endpoint, session.LiveOrigin.Database);
            publish = ConnectionRegistry.Find(editingRecord?.PublishConnectionId) ?? publish;
            // The person let go of the destination for this local copy (contract 19.2): that beats the default back
            // to an XMLA source below. A model open live is not a local copy, so the flag never applies to it: saving
            // goes straight back to the live model there, and clearing is refused for that case instead.
            if (session.LiveOrigin == null && editingRecord?.PublishCleared == true)
                return new ConnectionContextModel { Available = false };
            if (publish == null && string.Equals(editingRecord?.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                publish = editingRecord;
            // The origin guard at :174 keeps a loopback EDITING origin out, but both later routes can still SELECT a
            // legacy loopback row that is already on disk — the explicit publish id at :176, and the kind-only
            // fallback at :177-178. Judge the record that was actually selected, after both routes, so a legacy row
            // stays inert without being migrated or rewritten.
            if (publish == null || !string.Equals(publish.Kind, "xmla", StringComparison.OrdinalIgnoreCase)
                || LiveDeploy.IsLocalEndpoint(publish.Endpoint))
                return new ConnectionContextModel { Available = false };
            return FromRecord(publish, new ConnectionContextModel
            {
                Available = true,
                ModelName = NameOrWorkspace(publish.ModelName ?? publish.Database, publish.Endpoint),
                Source = publish.Endpoint,
                Kind = publish.Kind,
                Endpoint = publish.Endpoint,
                Database = publish.Database,
                Live = true
            });
        }

        // A WORKSPACE-level target has no dataset, so both sides above used to hand out an empty name and every surface
        // then showed the endpoint's encoded last segment ("Contoso%20Fabric%20Monitoring", a placeholder for the real
        // workspace name the public mirror refuses to carry). The decoded workspace is
        // the one honest name such a target has. Null when even that is unknown — a caller must be able to tell "no
        // name" from a name, and inventing one from a local data source would be worse than the defect.
        private static string NameOrWorkspace(string name, string endpoint) =>
            string.IsNullOrWhiteSpace(name) ? ConnectionRegistry.WorkspaceNameFromEndpoint(endpoint) : name;

        private static ConnectionContextModel FromRecord(ModelConnectionRecord record, ConnectionContextModel side)
        {
            side.ConnectionId = record?.Id;
            side.AuthMode = record?.AuthMode;
            side.Account = record?.LastAccount;
            side.TenantId = record?.TenantId;
            side.Label = record?.Label;
            side.EffectiveLabel = side.Available && side.Live ? ConnectionRegistry.EffectiveLabel(record) : record?.Label;
            side.Unlabelled = side.Available && side.Live && ConnectionRegistry.IsUnlabelled(record);
            side.WorkingFolder = record?.WorkingFolder;
            return side;
        }

        private static string RelationshipFor(Session session, LiveConnection live, ModelConnectionRecord editing, ModelConnectionRecord querying)
        {
            if (session == null && live == null) return "none";
            if (session != null && live == null) return "editingOnly";
            if (session == null) return "queryingOnly";
            var origin = session.LiveOrigin;
            if (origin != null && Same(origin.Endpoint, live.DataSource) && Same(origin.Database, live.Database))
                return "sameInstance";
            if (editing != null && querying != null && Same(editing.Id, querying.Id)
                && IsWithin(session.SourcePath, editing.WorkingFolder))
                return string.Equals(editing.Kind, "localDesktop", StringComparison.OrdinalIgnoreCase)
                    ? "workingCopyAndLocalRuntime" : "workingCopyAndPublished";
            return "twoModels";
        }

        private static string SummaryFor(string relationship, ConnectionContextModel editing, ConnectionContextModel querying, ConnectionContextModel publishing)
        {
            var e = Name(editing, "the open model");
            var q = Name(querying, "the connected model");
            var summary = relationship switch
            {
                "sameInstance" => $"Editing and querying {e} in the same live model.",
                "workingCopyAndPublished" => $"Editing the local working copy of {e}. Queries run against its published model. Two copies are in play for different purposes.",
                "workingCopyAndLocalRuntime" => $"Editing the local working copy of {e}. Tests and queries run against the linked local running model. Two copies are in play for different purposes.",
                "twoModels" => $"Editing {e}. Queries run against {q}. Two models are in play for different purposes.",
                "editingOnly" => $"Editing {e}. No model is connected for queries.",
                "queryingOnly" => $"Querying {q}. No model is open for editing.",
                _ => "No model is open for editing or connected for queries."
            };
            if (!publishing.Available)
                return editing.Available && !editing.Live
                    ? summary + " No XMLA publish destination is linked."
                    : summary;
            var p = Name(publishing, "the linked published model");
            return relationship == "sameInstance"
                ? summary + " Changes are pushed back only when you explicitly review and push."
                : summary + $" When ready, review and explicitly push local changes to {p}.";
        }

        private static string Name(ConnectionContextModel side, string fallback) =>
            !string.IsNullOrWhiteSpace(side?.ModelName) ? side.ModelName
            : !string.IsNullOrWhiteSpace(side?.Database) ? side.Database : fallback;

        private static bool Same(string a, string b) =>
            string.Equals(a?.Trim().TrimEnd('/', '\\'), b?.Trim().TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

        private static bool IsWithin(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                var p = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                var f = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                return Same(p, f) || p.StartsWith(f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ReadStart(string file)
        {
            try { using var r = new StreamReader(file); var buf = new char[16]; var n = r.Read(buf, 0, buf.Length); return new string(buf, 0, n); }
            catch { return ""; }
        }

        private static string FindRepositoryRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var dir = File.Exists(path) ? new DirectoryInfo(Path.GetDirectoryName(path)) : new DirectoryInfo(path);
                while (dir != null)
                {
                    // A repository, not just a folder called ".git": a git directory has a HEAD file, and a worktree
                    // or submodule keeps a ".git" FILE that names its git directory. An empty ".git" folder in a home
                    // directory made every model below it "in source control" with the home folder as its root (drop 14).
                    var marker = Path.Combine(dir.FullName, ".git");
                    if (File.Exists(Path.Combine(marker, "HEAD"))) return dir.FullName;
                    if (File.Exists(marker) && ReadStart(marker).StartsWith("gitdir:", StringComparison.Ordinal)) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static string KindFor(string endpoint) => LiveDeploy.IsLocalEndpoint(endpoint) ? "localDesktop" : "xmla";
    }
}
