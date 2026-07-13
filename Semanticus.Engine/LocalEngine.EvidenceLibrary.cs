using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Evidence;

namespace Semanticus.Engine
{
    /// <summary>One model-scoped evidence record on disk. Invalid records stay visible so tampering or a partial
    /// source-control merge can never make evidence disappear from the browser.</summary>
    public sealed class EvidenceItem
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string CreatedUtc { get; set; }
        public string Producer { get; set; }
        public string ModelName { get; set; }
        public string Verdict { get; set; }
        public int? Verified { get; set; }
        public int? Total { get; set; }
        public int? Unknowns { get; set; }
        public string ContentHash { get; set; }
        public bool Valid { get; set; }
        public string Note { get; set; }
        public string JsonPath { get; set; }
        public string HtmlPath { get; set; }
        public string UpdatedUtc { get; set; }
    }

    public sealed class EvidenceLibrary
    {
        public string ModelIdentity { get; set; }
        public string ModelName { get; set; }
        public string DirectoryPath { get; set; }
        public EvidenceItem[] Items { get; set; } = Array.Empty<EvidenceItem>();
        public int InvalidCount { get; set; }
        public string Note { get; set; }
    }

    public sealed class EvidenceSaveResult
    {
        public bool Saved { get; set; }
        public EvidenceItem Item { get; set; }
        public EvidenceLibrary Library { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed partial class LocalEngine
    {
        private readonly SemaphoreSlim _evidenceGate = new SemaphoreSlim(1, 1);

        /// <summary>The identity subfolder is load-bearing: two model files can share one parent and live-only
        /// sessions can share one workspace. A disk key is relative to the sidecar so committed evidence remains
        /// discoverable after a repository clone; the absolute-path pane identity would strand it on one machine.</summary>
        private (string Identity, string Directory) EvidenceLibraryLocation()
        {
            var anchor = _sessions.Current?.SourcePath;
            var disk = !ExperienceStore.IsEphemeralAnchor(anchor);
            var paneIdentity = OpenModelIdentity();
            if (string.IsNullOrWhiteSpace(paneIdentity)) return (null, null);
            var sidecar = disk ? LayoutStore.DirFor(anchor) : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
            var identity = disk
                ? Directory.Exists(Path.GetFullPath(anchor)) ? "model" : "file:" + Path.GetFileName(anchor).ToLowerInvariant()
                : paneIdentity;
            if (sidecar == null) return (identity, null);
            var safe = new string(identity.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
            return (identity, Path.Combine(sidecar, "evidence", safe));
        }

        public async Task<EvidenceLibrary> ListEvidenceAsync()
        {
            var session = _sessions.Current;
            if (session == null) return new EvidenceLibrary { Note = "Open a model to browse its evidence." };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var (identity, directory) = EvidenceLibraryLocation();
            if (identity == null)
                return new EvidenceLibrary { ModelName = modelName, Note = "Save or connect the model before storing evidence." };
            if (directory == null)
                return new EvidenceLibrary { ModelName = modelName, ModelIdentity = identity, Note = "Open a workspace before storing evidence for this live model." };
            if (!Directory.Exists(directory))
                return new EvidenceLibrary { ModelName = modelName, ModelIdentity = identity, DirectoryPath = directory };

            var items = new List<EvidenceItem>();
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*" + EvidenceStore.JsonSuffix, SearchOption.TopDirectoryOnly))
                    items.Add(ReadEvidenceItem(file));
            }
            catch (Exception ex)
            {
                return new EvidenceLibrary
                {
                    ModelName = modelName,
                    ModelIdentity = identity,
                    DirectoryPath = directory,
                    Note = "The evidence library could not be read: " + ex.Message,
                };
            }
            var ordered = items.OrderByDescending(x => x.CreatedUtc ?? x.UpdatedUtc, StringComparer.Ordinal).ThenBy(x => x.Title ?? x.Id, StringComparer.Ordinal).ToArray();
            var invalid = ordered.Count(x => !x.Valid);
            return new EvidenceLibrary
            {
                ModelName = modelName,
                ModelIdentity = identity,
                DirectoryPath = directory,
                Items = ordered,
                InvalidCount = invalid,
                Note = invalid == 0 ? null : $"{invalid} evidence record(s) failed verification. They remain visible and are not trusted.",
            };
        }

        public async Task<EvidenceArtifact> GetEvidenceAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return new EvidenceArtifact { Error = "Choose an evidence record from list_evidence." };
            var library = await ListEvidenceAsync();
            var item = library.Items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (item == null) return new EvidenceArtifact { Note = $"Evidence '{id}' was not found for the open model. Run list_evidence to refresh the model-scoped library." };
            if (!item.Valid) return new EvidenceArtifact { Error = $"Evidence '{id}' is not trusted: {item.Note}" };
            try
            {
                if (!EvidenceStore.Verify(item.JsonPath, out var reason))
                    return new EvidenceArtifact { Error = $"Evidence '{id}' is not trusted: {reason}" };
                var json = await File.ReadAllTextAsync(item.JsonPath);
                var doc = EvidenceHash.Deserialize(json);
                doc.Validate();
                if (!string.Equals(doc.ContentHash, EvidenceHash.Compute(doc), StringComparison.Ordinal))
                    return new EvidenceArtifact { Error = $"Evidence '{id}' changed while it was being opened; refresh the library and retry." };
                var expectedHtml = EvidenceRenderer.Render(doc);
                var html = File.Exists(item.HtmlPath) ? await File.ReadAllTextAsync(item.HtmlPath) : expectedHtml;
                if (!string.Equals(html, expectedHtml, StringComparison.Ordinal))
                    return new EvidenceArtifact { Error = $"Evidence '{id}' changed while it was being opened; refresh the library and retry." };
                return new EvidenceArtifact
                {
                    Json = json,
                    Html = html,
                    ContentHash = doc.ContentHash,
                    Note = File.Exists(item.HtmlPath) ? null : "The canonical JSON verified; its HTML view was regenerated because the sibling report was absent.",
                };
            }
            catch (Exception ex) { return new EvidenceArtifact { Error = "The evidence record could not be read: " + ex.Message }; }
        }

        /// <summary>Persist an engine-owned artifact by source, never by accepting an arbitrary HTML/JSON blob from
        /// the caller. Saving is explicit and inherits the source's existing boundary: Tests report stays Pro-soft;
        /// terminal Workflow evidence stays free. Re-saving the same source id is an idempotent atomic overwrite.</summary>
        public async Task<EvidenceSaveResult> SaveEvidenceAsync(string source, string sourceId, string origin)
        {
            source = (source ?? "").Trim().ToLowerInvariant();
            EvidenceArtifact artifact;
            if (source == "tests" || source == "test" || source == "test-suite")
            {
                var report = await ExportTestReportAsync();
                artifact = new EvidenceArtifact { Json = report.Json, Html = report.Html, ContentHash = report.ContentHash, Note = report.Note, Error = report.Error };
            }
            else if (source == "workflow" || source == "workflow-run")
                artifact = await ExportWorkflowEvidenceAsync(sourceId);
            else
                return new EvidenceSaveResult { Error = "Source must be 'tests' or 'workflow'. Use tests for the latest current-model suite, or workflow with an optional terminal run id." };

            if (string.IsNullOrWhiteSpace(artifact.Json))
                return new EvidenceSaveResult { Note = artifact.Note, Error = artifact.Error, Library = await ListEvidenceAsync() };

            var (_, directory) = EvidenceLibraryLocation();
            if (directory == null)
                return new EvidenceSaveResult { Note = "This model has no project location for shared evidence. Save the model or open a workspace and retry.", Library = await ListEvidenceAsync() };

            EvidenceDoc doc;
            try
            {
                doc = EvidenceHash.Deserialize(artifact.Json);
                doc.Validate();
                var recomputed = EvidenceHash.Compute(doc);
                if (!string.Equals(doc.ContentHash, recomputed, StringComparison.Ordinal))
                    return new EvidenceSaveResult { Error = "The generated evidence does not match its content signature; nothing was saved.", Library = await ListEvidenceAsync() };
            }
            catch (Exception ex) { return new EvidenceSaveResult { Error = "The generated evidence is invalid; nothing was saved. " + ex.Message, Library = await ListEvidenceAsync() }; }

            string jsonPath = null;
            string writeError = null;
            await _evidenceGate.WaitAsync();
            try { (jsonPath, _) = EvidenceStore.Write(doc, directory); }
            catch (Exception ex) { writeError = "The evidence could not be saved: " + ex.Message; }
            finally { _evidenceGate.Release(); }

            if (writeError != null)
                return new EvidenceSaveResult { Error = writeError, Library = await ListEvidenceAsync() };

            var library = await ListEvidenceAsync();
            var item = library.Items.FirstOrDefault(x => string.Equals(x.Id, doc.Id, StringComparison.Ordinal));
            var result = new EvidenceSaveResult
            {
                Saved = item?.Valid == true,
                Item = item,
                Library = library,
                Note = item?.Valid == true ? "Saved with the model at " + jsonPath : item?.Note ?? "The evidence was written but did not verify on re-read.",
            };
            // MCP emits through its wrapper so the activity reaches an attached owner. The UI door has no wrapper,
            // so publish its equivalent here. Exactly one event is emitted per door.
            if (!string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase))
                await PublishActivityAsync(new ActivityEvent { Kind = "save_evidence", Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin, Label = "Saved evidence with the model", Target = doc.Id, Ok = result.Saved, Error = result.Saved ? null : result.Error ?? result.Note });
            return result;
        }

        private static EvidenceItem ReadEvidenceItem(string jsonPath)
        {
            var updated = File.GetLastWriteTimeUtc(jsonPath).ToString("o");
            var fallback = Path.GetFileName(jsonPath);
            if (fallback.EndsWith(EvidenceStore.JsonSuffix, StringComparison.OrdinalIgnoreCase))
                fallback = fallback.Substring(0, fallback.Length - EvidenceStore.JsonSuffix.Length);
            if (!EvidenceStore.Verify(jsonPath, out var reason))
                return new EvidenceItem { Id = fallback, Title = fallback, Valid = false, Note = reason, JsonPath = jsonPath, HtmlPath = HtmlPathFor(jsonPath), UpdatedUtc = updated };
            try
            {
                var doc = EvidenceHash.Deserialize(File.ReadAllText(jsonPath));
                return new EvidenceItem
                {
                    Id = doc.Id,
                    Kind = doc.Kind,
                    Title = doc.Title,
                    CreatedUtc = doc.CreatedUtc,
                    Producer = doc.Producer,
                    ModelName = doc.ModelName,
                    Verdict = doc.Verdict?.ToString(),
                    Verified = doc.Coverage?.Verified,
                    Total = doc.Coverage?.Total,
                    Unknowns = doc.Coverage?.Unknowns,
                    ContentHash = doc.ContentHash,
                    Valid = true,
                    Note = reason,
                    JsonPath = jsonPath,
                    HtmlPath = HtmlPathFor(jsonPath),
                    UpdatedUtc = updated,
                };
            }
            catch (Exception ex) { return new EvidenceItem { Id = fallback, Title = fallback, Valid = false, Note = "the verified record could not be projected: " + ex.Message, JsonPath = jsonPath, HtmlPath = HtmlPathFor(jsonPath), UpdatedUtc = updated }; }
        }

        private static string HtmlPathFor(string jsonPath)
            => jsonPath.Substring(0, jsonPath.Length - EvidenceStore.JsonSuffix.Length) + EvidenceStore.HtmlSuffix;
    }
}
