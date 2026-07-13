using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>A preview or outcome for local editing with explicit test/query and publish destinations.</summary>
    public sealed class WorkingCopyResult
    {
        public string ConnectionId { get; set; }
        public string ModelName { get; set; }
        public string SourceConnectionId { get; set; }
        public string SourceModelName { get; set; }
        public string PublishConnectionId { get; set; }
        public string PublishModelName { get; set; }
        public string QueryConnectionId { get; set; }
        public string QueryModelName { get; set; }
        public string QueryKind { get; set; }
        public string TargetFolder { get; set; }
        public string DefinitionFolder { get; set; }
        public string Action { get; set; }
        public bool CanCommit { get; set; }
        public bool CommitRequested { get; set; }
        public bool Opened { get; set; }
        public bool QueryConnected { get; set; }
        public bool TwoCopiesInPlay { get; set; }
        public string Summary { get; set; }
        public string[] Benefits { get; set; } = Array.Empty<string>();
        public string[] Conflicts { get; set; } = Array.Empty<string>();
        public string NextAction { get; set; }
        public string Error { get; set; }
        public ConnectionContext Context { get; set; }
    }

    internal sealed class WorkingCopyMarker
    {
        public int Version { get; set; } = 1;
        public string ConnectionId { get; set; }
        public string ModelName { get; set; }
        public string CreatedUtc { get; set; }
    }

    /// <summary>
    /// Pure filesystem planner for a durable local copy. The ownership marker proves which remembered
    /// model owns the folder while persisting no endpoint, tenant, or credential detail (id, display
    /// name and a timestamp only).
    /// </summary>
    internal static class WorkingCopyPlanner
    {
        internal const string MarkerFile = ".semanticus-working-copy.json";
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        internal static WorkingCopyResult Plan(ModelConnectionRecord record, string parentFolder)
        {
            if (record == null) throw new InvalidOperationException("That remembered connection no longer exists. Choose a connection again.");
            var target = TargetFor(record, parentFolder);
            var modelName = DisplayName(record);
            var result = new WorkingCopyResult
            {
                ConnectionId = record.Id,
                ModelName = modelName,
                TargetFolder = target,
                DefinitionFolder = Path.Combine(target, "definition"),
                Action = "create",
                CanCommit = true,
                TwoCopiesInPlay = true,
                Benefits = new[]
                {
                    "Edits are saved as ordinary local model files you can review and version.",
                    "Tests and queries use the live connection you choose without changing the local files.",
                    "Nothing is pushed to the published model until you explicitly review and push changes."
                },
                NextAction = "Confirm to create the local working copy and connect its published model for queries."
            };

            if (!Directory.Exists(target))
            {
                result.Summary = $"Create a local working copy of {modelName} at {target}. You will edit the local copy. Two copies will be in play for different purposes.";
                return result;
            }

            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(target).ToArray(); }
            catch (Exception ex)
            {
                return Refuse(result, "The selected folder cannot be inspected: " + ex.Message);
            }

            if (entries.Length == 0)
            {
                result.Summary = $"Create a local working copy of {modelName} in the selected empty folder at {target}. You will edit the local copy.";
                return result;
            }

            var markerPath = Path.Combine(target, MarkerFile);
            if (!File.Exists(markerPath))
                return Refuse(result, "The target folder is not empty and is not owned by this working-copy workflow. No files will be changed.");

            WorkingCopyMarker marker;
            try { marker = JsonSerializer.Deserialize<WorkingCopyMarker>(File.ReadAllText(markerPath), JsonOptions); }
            catch
            {
                return Refuse(result, "The working-copy ownership marker cannot be read. No files will be changed.");
            }
            if (marker == null || marker.Version != 1 || !Same(marker.ConnectionId, record.Id))
                return Refuse(result, "This folder belongs to a different connection, or its ownership cannot be proven. No files will be changed.");
            if (!ModelPathResolver.IsTmdlRoot(result.DefinitionFolder))
                return Refuse(result, "The owned working copy is incomplete because its model definition is missing. It will not be overwritten.");

            result.Action = "open";
            result.Summary = $"Open the existing local working copy of {modelName} at {target}. You will edit these local files. The local copy will not be refreshed or overwritten.";
            result.NextAction = "Confirm to open the existing local working copy and connect its published model for queries.";
            return result;
        }

        internal static void WriteMarker(string targetFolder, ModelConnectionRecord record)
        {
            var marker = new WorkingCopyMarker
            {
                ConnectionId = record.Id,
                ModelName = DisplayName(record),
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            File.WriteAllText(Path.Combine(targetFolder, MarkerFile), JsonSerializer.Serialize(marker, JsonOptions));
        }

        internal static WorkingCopyMarker ReadMarker(string targetFolder) =>
            JsonSerializer.Deserialize<WorkingCopyMarker>(File.ReadAllText(Path.Combine(targetFolder, MarkerFile)), JsonOptions);

        private static WorkingCopyResult Refuse(WorkingCopyResult result, string conflict)
        {
            result.CanCommit = false;
            result.Conflicts = new[] { conflict };
            result.Summary = conflict;
            result.NextAction = "Choose a different parent folder. Existing files have been left untouched.";
            return result;
        }

        private static string TargetFor(ModelConnectionRecord record, string parentFolder)
        {
            if (string.IsNullOrWhiteSpace(parentFolder))
            {
                if (!string.IsNullOrWhiteSpace(record.WorkingFolder)) return Path.GetFullPath(record.WorkingFolder);
                throw new InvalidOperationException("Choose where the local working copy should live. Previewing does not create the folder.");
            }
            return Path.Combine(Path.GetFullPath(parentFolder), SafeFileName(DisplayName(record)) + ".SemanticModel");
        }

        private static string DisplayName(ModelConnectionRecord record) =>
            !string.IsNullOrWhiteSpace(record.ModelName) ? record.ModelName.Trim()
            : !string.IsNullOrWhiteSpace(record.Database) ? record.Database.Trim() : "Semantic model";

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) ? "SemanticModel" : name;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
