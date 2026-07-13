using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        private readonly System.Threading.SemaphoreSlim _primerGate = new System.Threading.SemaphoreSlim(1, 1);

        private sealed class PrimerSuggestionState
        {
            public System.Collections.Generic.Dictionary<string, string> Decisions { get; set; } = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        }

        private (string identity, string file) PrimerFile()
        {
            var identity = OpenModelIdentity();
            if (string.IsNullOrWhiteSpace(identity)) return (null, null);
            var anchor = _sessions.Current?.SourcePath;
            var sidecar = !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
            if (sidecar == null) return (identity, null);
            var safe = new string(identity.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
            return (identity, Path.Combine(sidecar, "primers", safe + ".md"));
        }

        private string PrimerSuggestionFile()
        {
            var (_, file) = PrimerFile();
            return file == null ? null : Path.ChangeExtension(file, ".suggestions.json");
        }

        public async Task<PrimerDocument> GetPrimerAsync()
        {
            var session = _sessions.Current;
            if (session == null) return new PrimerDocument { Note = "Open a model to read its Primer." };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var (identity, file) = PrimerFile();
            if (identity == null)
                return new PrimerDocument { ModelName = modelName, Markdown = PrimerContract.Template(modelName), Note = "Save or connect the model before writing its Primer." };
            if (file == null)
                return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, Markdown = PrimerContract.Template(modelName), Note = "Open a workspace before writing this live model's Primer." };
            if (!File.Exists(file))
                return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = PrimerContract.Template(modelName), Exists = false };
            string markdown;
            string note = null;
            try { markdown = await File.ReadAllTextAsync(file); }
            catch (Exception ex) { return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = PrimerContract.Template(modelName), Note = "The Primer could not be read: " + ex.Message }; }
            try { PrimerContract.Validate(markdown); }
            catch (Exception ex) { note = ex.Message + " Edit and save it to restore the fixed structure."; }
            return new PrimerDocument
            {
                ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = markdown, Exists = true,
                UpdatedUtc = File.GetLastWriteTimeUtc(file).ToString("o"), Note = note,
            };
        }

        public async Task<PrimerDocument> SetPrimerAsync(string markdown, string origin)
        {
            PrimerContract.Validate(markdown);
            var session = _sessions.Current ?? throw new InvalidOperationException("Open a model before writing its Primer.");
            var (_, file) = PrimerFile();
            if (file == null) throw new InvalidOperationException("This model has no project location for its Primer. Save the model or open a workspace and retry.");
            await _primerGate.WaitAsync();
            try
            {
                await WritePrimerFileAsync(file, markdown);
            }
            finally { _primerGate.Release(); }
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Kind = "set_model_primer", Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = "Updated the model Primer", Target = session.Id, Ok = true,
            });
            return await GetPrimerAsync();
        }

        public async Task<PrimerSuggestionList> ListPrimerSuggestionsAsync()
        {
            var primer = await GetPrimerAsync();
            if (_sessions.Current == null)
                return new PrimerSuggestionList { IsPro = _entitlement?.IsPro == true, Note = "Open a model to review Primer suggestions." };
            if (_entitlement?.IsPro != true)
                return new PrimerSuggestionList { IsPro = false, Note = "Suggested Primer updates are a Pro feature. You can still read and edit the Primer manually." };

            var fp = await _sessions.Current.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m).FingerprintKey);
            var handled = ReadSuggestionState();
            var records = new System.Collections.Generic.List<InsightRecord>();
            foreach (var scope in new[] { "project", "global" })
            {
                var (live, _) = KnowledgeStore.Materialize(ScopeFile(scope), scope);
                records.AddRange(live.Where(x => string.Equals(x.Status, "approved", StringComparison.Ordinal)
                    && string.Equals(x.Fingerprint, fp, StringComparison.Ordinal)));
            }
            var suggestions = records
                .Where(x => !handled.Decisions.ContainsKey(x.Id))
                .OrderByDescending(x => x.LastUsedUtc, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .Select(ToPrimerSuggestion)
                .ToArray();
            return new PrimerSuggestionList
            {
                IsPro = true,
                Suggestions = suggestions,
                Sections = PrimerContract.Sections.Select(section => new PrimerSectionFreshness
                {
                    Section = section,
                    SuggestedAdditions = suggestions.Count(x => x.Section == section),
                    PrimerUpdatedUtc = primer.UpdatedUtc,
                }).ToArray(),
                Note = suggestions.Length == 0 ? "No reviewed learning is waiting to update this model's Primer." : null,
            };
        }

        public Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin)
            => DecidePrimerSuggestionAsync(id, true, origin);

        public Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin)
            => DecidePrimerSuggestionAsync(id, false, origin);

        private async Task<PrimerSuggestionDecision> DecidePrimerSuggestionAsync(string id, bool accept, string origin)
        {
            if (_entitlement?.IsPro != true)
                return new PrimerSuggestionDecision { Changed = false, Note = "Suggested Primer updates are a Pro feature. Edit the Primer manually to make this change without Pro." };
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Choose a Primer suggestion to accept or reject.");
            var list = await ListPrimerSuggestionsAsync();
            var suggestion = list.Suggestions.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("This Primer suggestion is no longer waiting. Refresh the Primer and choose a current suggestion.");
            var (_, file) = PrimerFile();
            if (file == null) throw new InvalidOperationException("This model has no project location for its Primer. Save the model or open a workspace and retry.");

            PrimerDocument primer = null;
            await _primerGate.WaitAsync();
            try
            {
                if (accept)
                {
                    var current = File.Exists(file) ? await File.ReadAllTextAsync(file) : (await GetPrimerAsync()).Markdown;
                    await WritePrimerFileAsync(file, PrimerContract.ApplySuggestion(current, suggestion));
                }
                var state = ReadSuggestionState();
                state.Decisions[id] = (accept ? "accepted" : "rejected") + "|" + DateTime.UtcNow.ToString("o");
                await WriteSuggestionStateAsync(state);
            }
            finally { _primerGate.Release(); }
            if (accept) primer = await GetPrimerAsync();
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Kind = accept ? "accept_primer_suggestion" : "reject_primer_suggestion",
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = (accept ? "Accepted" : "Rejected") + " a suggested Primer update",
                Target = id, Ok = true,
            });
            return new PrimerSuggestionDecision
            {
                Changed = accept,
                Decision = accept ? "accepted" : "rejected",
                Primer = primer,
                Note = accept ? "The reviewed suggestion was added to the " + suggestion.Section + " section." : "The suggestion was dismissed and will not be offered again for this model.",
            };
        }

        private static PrimerSuggestion ToPrimerSuggestion(InsightRecord insight)
        {
            var runs = (insight.Provenance?.SourceRunIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            var captured = insight.Provenance?.When;
            var evidenceCount = Math.Max(1, Math.Max(insight.Uses, runs.Length));
            var source = runs.Length > 0 ? runs.Length + " source run" + (runs.Length == 1 ? "" : "s") : "captured learning";
            var provenance = source + (string.IsNullOrWhiteSpace(captured) ? "" : " · " + captured);
            var text = (insight.Text ?? string.Empty).Replace("—", "-").Trim();
            var markdown = "- " + text + "\n  _Provenance: " + provenance + "._";
            return new PrimerSuggestion
            {
                Id = insight.Id,
                Section = PrimerContract.SuggestionSection(insight),
                Markdown = markdown,
                CapturedUtc = captured,
                Origin = insight.Provenance?.Origin,
                SourceRunIds = runs,
                EvidenceCount = evidenceCount,
                Provenance = provenance,
            };
        }

        private PrimerSuggestionState ReadSuggestionState()
        {
            var file = PrimerSuggestionFile();
            if (file == null || !File.Exists(file)) return new PrimerSuggestionState();
            try { return JsonSerializer.Deserialize<PrimerSuggestionState>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrimerSuggestionState(); }
            catch { return new PrimerSuggestionState(); }
        }

        private async Task WriteSuggestionStateAsync(PrimerSuggestionState state)
        {
            var file = PrimerSuggestionFile();
            if (file == null) throw new InvalidOperationException("This model has no project location for Primer suggestions.");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            File.Move(temp, file, true);
        }

        private static async Task WritePrimerFileAsync(string file, string markdown)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temp, markdown.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
            File.Move(temp, file, true);
        }
    }
}
