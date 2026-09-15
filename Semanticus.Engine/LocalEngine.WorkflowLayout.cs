using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed class WorkflowPosition
    {
        public double X { get; set; } = double.NaN;
        public double Y { get; set; } = double.NaN;
    }

    public sealed class WorkflowLayout
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; }
        public string Revision { get; set; }
        public Dictionary<string, WorkflowPosition> Positions { get; set; } = new();
    }

    public sealed partial class LocalEngine
    {
        private static readonly JsonSerializerOptions WorkflowLayoutJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        private (WorkflowDef def, byte[] definition, string file) WorkflowLayoutTarget(string name, string sidecar)
        {
            if (name == null || !KebabName.IsMatch(name) || KebabName.Match(name).Length != name.Length)
                throw new ArgumentException("Workflow name must be kebab-case.");
            var userFile = sidecar == null ? null : Path.Combine(sidecar, "workflows", name + ".md");
            var definitionFile = userFile != null && File.Exists(userFile) ? userFile
                : Path.Combine(AppContext.BaseDirectory, "workflows", name + ".md");
            if (!File.Exists(definitionFile)) throw new InvalidOperationException($"Workflow '{name}' not found.");
            // Parse the same byte snapshot we hash, including comments and other unparsed text.
            var definition = File.ReadAllBytes(definitionFile);
            using var reader = new StreamReader(new MemoryStream(definition));
            var def = WorkflowParser.Parse(reader.ReadToEnd());
            if (def.Error != null || def.Name != name || def.Kind == "template")
                throw new InvalidOperationException("Fix the workflow parse error before arranging its steps.");
            return (def, definition, sidecar == null ? null : Path.Combine(sidecar, "workflow-layouts", name + ".json"));
        }

        private static string WorkflowLayoutRevision(string file, byte[] definition, byte[] bytes)
        {
            // Identical files in another project are still a different target after Save As.
            var path = Encoding.UTF8.GetBytes(file == null ? string.Empty : Path.GetFullPath(file));
            return Convert.ToHexString(SHA256.HashData(SHA256.HashData(path)
                .Concat(SHA256.HashData(definition)).Concat(bytes ?? Array.Empty<byte>()).ToArray()));
        }

        private static WorkflowLayout ReadWorkflowLayout(WorkflowDef def, string file, byte[] definition, byte[] bytes)
        {
            var layout = bytes == null ? new WorkflowLayout() : JsonSerializer.Deserialize<WorkflowLayout>(bytes, WorkflowLayoutJson)
                ?? throw new InvalidOperationException("Workflow layout is empty or corrupt.");
            if (layout.Version != 1) throw new InvalidOperationException("Unsupported workflow layout version.");
            layout.Name = def.Name;
            // Fence definition edits too: otherwise a stale Canvas could put positional IDs on changed steps.
            layout.Revision = WorkflowLayoutRevision(file, definition, bytes);
            layout.Positions = CleanWorkflowPositions(def, layout.Positions);
            return layout;
        }

        private static Dictionary<string, WorkflowPosition> CleanWorkflowPositions(WorkflowDef def, Dictionary<string, WorkflowPosition> positions)
        {
            if (positions == null) throw new ArgumentException("positions must be an object; use {} to reset.");
            var ids = def.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var clean = new Dictionary<string, WorkflowPosition>(StringComparer.Ordinal);
            foreach (var (id, position) in positions)
            {
                if (position == null || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
                    throw new ArgumentException($"Position '{id}' must contain finite x and y coordinates.");
                if (ids.Contains(id)) clean[id] = new WorkflowPosition { X = position.X, Y = position.Y };
            }
            return clean;
        }

        public async Task<WorkflowLayout> GetWorkflowLayoutAsync(string name)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var sidecar = SidecarDir();
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow layout read");
                var (def, definition, file) = WorkflowLayoutTarget(name, sidecar);
                return ReadWorkflowLayout(def, file, definition, file != null && File.Exists(file) ? File.ReadAllBytes(file) : null);
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<WorkflowLayout> SaveWorkflowLayoutAsync(string name, Dictionary<string, WorkflowPosition> positions, string expectedRevision = null)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            // Save As can move SourcePath without replacing the context while this operation is queued.
            var sidecar = SidecarDir();
            // Context replacement drains this gate, keeping definition lookup, path and publication together.
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Workflow layout save");
                var (def, definition, file) = WorkflowLayoutTarget(name, sidecar);
                if (file == null) throw new InvalidOperationException("Open a saved model or workspace before saving a workflow layout.");
                var clean = CleanWorkflowPositions(def, positions);
                var bytes = File.Exists(file) ? File.ReadAllBytes(file) : null;
                if (expectedRevision != null && expectedRevision != WorkflowLayoutRevision(file, definition, bytes))
                    throw new InvalidOperationException("Workflow or layout changed. Reload the layout before saving again.");
                // Explicit {} reset also recovers a corrupt sidecar. Other writes must first read it successfully.
                if (positions.Count != 0) ReadWorkflowLayout(def, file, definition, bytes);
                var next = new WorkflowLayout { Name = name, Positions = clean };
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(next, WorkflowLayoutJson));
                    File.Move(temp, file, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                var saved = ReadWorkflowLayout(def, file, definition, File.ReadAllBytes(file));
                // A queued save may finish in the old project after Save As. Do not push it into the new Canvas.
                if (string.Equals(sidecar, SidecarDir(), StringComparison.Ordinal)) _sessions.Bus.PublishWorkflowLayout(saved);
                return saved;
            }
            finally { context.WorkflowGate.Release(); }
        }
    }
}
