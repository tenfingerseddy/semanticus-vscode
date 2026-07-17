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
                ModelName = record?.ModelName ?? live.Database,
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
            if (publish == null && string.Equals(editingRecord?.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                publish = editingRecord;
            if (publish == null || !string.Equals(publish.Kind, "xmla", StringComparison.OrdinalIgnoreCase))
                return new ConnectionContextModel { Available = false };
            return FromRecord(publish, new ConnectionContextModel
            {
                Available = true,
                ModelName = publish.ModelName ?? publish.Database,
                Source = publish.Endpoint,
                Kind = publish.Kind,
                Endpoint = publish.Endpoint,
                Database = publish.Database,
                Live = true
            });
        }

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

        private static string FindRepositoryRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var dir = File.Exists(path) ? new DirectoryInfo(Path.GetDirectoryName(path)) : new DirectoryInfo(path);
                while (dir != null)
                {
                    var marker = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(marker) || File.Exists(marker)) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static string KindFor(string endpoint) => LiveDeploy.IsLocalEndpoint(endpoint) ? "localDesktop" : "xmla";
    }
}
