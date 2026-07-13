using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// Read-only view of a model's "Prep for AI" surface (a Power BI semantic-model feature). Storage reverse-
    /// engineered from a real Prep-for-AI-authored model. Two physical layers:
    ///   • LSDL (Culture.Content JSON) — available from TOM for ANY model with a linguistic schema:
    ///       - AI instructions  = top-level "CustomInstructions" string (≤10,000 chars)
    ///       - AI data schema   = per-entity Visibility {Value:"Hidden"} marks fields EXCLUDED from the schema
    ///   • Files beside the TMDL — only for an on-disk PBIP:
    ///       - Q&A enablement   = definition.pbism settings.qnaEnabled
    ///       - Verified answers = VerifiedAnswers/definitions/&lt;guid&gt;/definition.json
    /// Never writes. Tolerant of any IO/parse error. (Writers + "Approved for Copilot" service flag are out of scope.)
    /// </summary>
    public sealed class PrepForAiConfig
    {
        // From the LSDL (TOM — any model):
        public bool HasLinguisticSchema;    // a JSON linguistic schema (LSDL) exists — Q&A/AI-prep engaged
        public string SelectedCultureName;  // WHICH linguistic culture was read (see PrepForAiReader.SelectLinguisticCulture) — the writer targets the SAME one
        public bool LinguisticCultureAmbiguous; // more than one JSON linguistic culture existed, so the pick wasn't forced (surface which one was used)
        public string AiInstructions;       // LSDL CustomInstructions (null/empty if not set)
        public int AiInstructionsLength;
        public int AiSchemaExcludedFields;  // entities marked Visibility=Hidden (excluded from the AI data schema)
        // The LSDL entity NAMES counted by AiSchemaExcludedFields (same walk). LSDL entity keys may be a bare name
        // ("Amount") or a table-qualified form ("Sales.Amount") depending on how the model authored the schema — the
        // consumer (the Data Agent scope generator) matches on both. names.Length == AiSchemaExcludedFields.
        public string[] AiSchemaExcludedNames = Array.Empty<string>();

        // From files beside the TMDL (on-disk PBIP only):
        public bool SourceReadable;         // model came from an inspectable on-disk PBIP semantic model
        public bool? QnaEnabled;            // definition.pbism settings.qnaEnabled (null if pbism missing/unreadable)
        public bool VerifiedAnswersPresent;
        public int VerifiedAnswerCount;
        public string ModelFolder;

        // The new "Copilot Tooling Format" (GA ~May 2026): Copilot grounding metadata stored as text files in a
        // `copilot/` folder beside the model, distinct from the legacy linguistic-schema (LSDL) anchors above. We
        // DETECT its presence (the folder + its JSON) but do NOT yet parse it — the exact per-file schema is not
        // authoritatively published (MS Learn documents the feature, not the on-disk layout). Detection exists so a
        // migrated model is never silently scored as un-prepped on the stale LSDL signal. See DAC-COPILOT-TOOLING-FORMAT.
        public bool CopilotToolingFormatPresent;
        public int CopilotToolingFileCount;

        public static readonly PrepForAiConfig Unreadable = new PrepForAiConfig();
    }

    public static class PrepForAiReader
    {
        private static readonly ConditionalWeakTable<Model, PrepForAiConfig> _cache =
            new ConditionalWeakTable<Model, PrepForAiConfig>();

        /// <summary>Reads (and memoizes per Model) the Prep-for-AI surface. Always non-null; tolerant of any IO/parse error.</summary>
        public static PrepForAiConfig Read(Model model)
        {
            if (model == null) return PrepForAiConfig.Unreadable;
            return _cache.GetValue(model, Build);
        }

        /// <summary>Drop the memoized snapshot for a model so the next Read re-reads it. Called at the start of each
        /// readiness scan (so the per-Model memo behaves as a per-scan memo) — essential after an LSDL write like
        /// set_ai_instructions, whose effect must show on re-scan.</summary>
        public static void Invalidate(Model model)
        {
            if (model != null) _cache.Remove(model);
        }

        private static PrepForAiConfig Build(Model model)
        {
            var cfg = new PrepForAiConfig();
            try { ReadLsdl(model, cfg); } catch { /* LSDL is best-effort */ }
            try { ReadDiskArtifacts(model, cfg); } catch { /* disk is best-effort + capability-gated */ }
            return cfg;
        }

        /// <summary>The ONE linguistic-schema (LSDL) culture selection, SHARED by the Prep-for-AI reader (here) and
        /// the engine's AI-instructions writer (LocalEngine.FindLinguisticCulture) — so a scan and a write can never
        /// touch DIFFERENT cultures on a multi-culture model (the old "first JSON culture" pick did). Among the JSON
        /// linguistic cultures: prefer the one whose Name matches the model's default <c>Culture</c>; else the first
        /// that actually carries a CustomInstructions payload; else the first. <paramref name="ambiguous"/> is true
        /// when more than one candidate existed (the pick wasn't forced) — callers surface the chosen culture then.</summary>
        public static Culture SelectLinguisticCulture(Model model, out bool ambiguous)
        {
            ambiguous = false;
            if (model?.Cultures == null) return null;
            var candidates = model.Cultures
                .Where(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content))
                .ToList();
            if (candidates.Count == 0) return null;
            ambiguous = candidates.Count > 1;
            // 1) the model's own default culture, when it is itself a linguistic culture (the deterministic anchor).
            var byDefault = !string.IsNullOrEmpty(model.Culture)
                ? candidates.FirstOrDefault(c => string.Equals(c.Name, model.Culture, StringComparison.OrdinalIgnoreCase))
                : null;
            if (byDefault != null) return byDefault;
            // 2) the first culture that actually carries AI instructions (the payload a writer edits / a scan reads).
            var withInstructions = candidates.FirstOrDefault(HasCustomInstructions);
            if (withInstructions != null) return withInstructions;
            // 3) deterministic fallback: the first candidate.
            return candidates[0];
        }

        private static bool HasCustomInstructions(Culture c)
        {
            try
            {
                using var doc = JsonDocument.Parse(c.Content);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("CustomInstructions", out var ci)
                    && ci.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ci.GetString());
            }
            catch { return false; }
        }

        /// <summary>AI instructions + AI data schema from the LSDL (Culture.Content), via TOM — works for any model.</summary>
        private static void ReadLsdl(Model model, PrepForAiConfig cfg)
        {
            var culture = SelectLinguisticCulture(model, out var ambiguous);
            if (culture == null) return;
            cfg.HasLinguisticSchema = true;
            cfg.SelectedCultureName = culture.Name;
            cfg.LinguisticCultureAmbiguous = ambiguous;

            using var doc = JsonDocument.Parse(culture.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("CustomInstructions", out var ci) && ci.ValueKind == JsonValueKind.String)
            {
                cfg.AiInstructions = ci.GetString();
                cfg.AiInstructionsLength = cfg.AiInstructions?.Length ?? 0;
            }
            if (root.TryGetProperty("Entities", out var ents) && ents.ValueKind == JsonValueKind.Object)
            {
                var excluded = 0;
                var names = new List<string>();
                foreach (var e in ents.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.Object
                        && e.Value.TryGetProperty("Visibility", out var v) && v.ValueKind == JsonValueKind.Object
                        && v.TryGetProperty("Value", out var vv) && vv.ValueKind == JsonValueKind.String
                        && vv.GetString() == "Hidden")
                        { excluded++; names.Add(e.Name); }   // capture the name alongside the count (same walk)
                cfg.AiSchemaExcludedFields = excluded;
                cfg.AiSchemaExcludedNames = names.ToArray();
            }
        }

        /// <summary>Q&A enablement + verified answers from files beside the TMDL (on-disk PBIP only).</summary>
        private static void ReadDiskArtifacts(Model model, PrepForAiConfig cfg)
        {
            var folder = ResolveModelFolder(model);
            if (folder == null || !Directory.Exists(folder)) return;
            cfg.SourceReadable = true;
            cfg.ModelFolder = folder;

            // Q&A enablement: definition.pbism = { "settings": { "qnaEnabled": bool } }
            var pbism = Path.Combine(folder, "definition.pbism");
            if (File.Exists(pbism))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pbism));
                    if (doc.RootElement.TryGetProperty("settings", out var s)
                        && s.TryGetProperty("qnaEnabled", out var q)
                        && (q.ValueKind == JsonValueKind.True || q.ValueKind == JsonValueKind.False))
                        cfg.QnaEnabled = q.GetBoolean();
                }
                catch { /* leave QnaEnabled null on parse error */ }
            }

            // Verified answers: VerifiedAnswers/definitions/<guid>/definition.json (one folder per answer)
            var vaDefs = Path.Combine(folder, "VerifiedAnswers", "definitions");
            if (Directory.Exists(vaDefs))
            {
                cfg.VerifiedAnswersPresent = true;
                cfg.VerifiedAnswerCount = Directory.EnumerateDirectories(vaDefs)
                    .Count(d => File.Exists(Path.Combine(d, "definition.json")));
            }

            // New Copilot Tooling Format (GA ~May 2026): a `copilot/` folder of text files beside the model.
            // Presence-only detection (folder + its JSON). Conservative + provenance-tagged: a `copilot/` folder
            // next to a definition.pbism is, in practice, only the Copilot Tooling Format. We do not parse the
            // files (their exact schema isn't authoritatively published) — detection alone prevents a migrated
            // model from being silently mis-scored on the legacy LSDL signal (see DAC-COPILOT-TOOLING-FORMAT).
            var copilotDir = Path.Combine(folder, "copilot");
            if (Directory.Exists(copilotDir))
            {
                var jsonCount = Directory.EnumerateFiles(copilotDir, "*.json", SearchOption.AllDirectories).Count();
                if (jsonCount > 0) { cfg.CopilotToolingFormatPresent = true; cfg.CopilotToolingFileCount = jsonCount; }
            }
        }

        /// <summary>The .SemanticModel folder if the model was loaded from an on-disk PBIP, else null.</summary>
        private static string ResolveModelFolder(Model model)
        {
            var src = model.MetadataSource;
            if (src == null) return null;
            // Live/XMLA models have a connection string for Source, not a path — never touch the filesystem.
            if (src.SourceType == ModelSourceType.Database) return null;

            var pbip = src.Pbip;
            if (pbip != null && !string.IsNullOrEmpty(pbip.RootFolder) && !string.IsNullOrEmpty(pbip.Name))
            {
                var f = Path.Combine(pbip.RootFolder, pbip.Name);
                if (Directory.Exists(f)) return f;
            }

            var start = src.Source;
            if (string.IsNullOrEmpty(start)) return null;
            var dir = Directory.Exists(start) ? new DirectoryInfo(start) : new FileInfo(start).Directory;
            for (var i = 0; dir != null && i < 4; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "definition.pbism"))) return dir.FullName;
            return null;
        }
    }
}
