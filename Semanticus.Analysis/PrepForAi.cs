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
    ///       - indexing setting = definition.pbism settings.qnaEnabled (Power BI labels it Q&A; Copilot needs it)
    ///       - Verified answers = VerifiedAnswers/definitions/&lt;guid&gt;/definition.json
    /// Never writes. Tolerant of any IO/parse error. (Writers + "Approved for Copilot" service flag are out of scope.)
    /// </summary>
    public sealed class PrepForAiConfig
    {
        // From the LSDL (TOM — any model):
        public bool HasLinguisticSchema;    // a JSON linguistic schema (LSDL) exists — AI-prep engaged
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

        /// <summary>The indexing setting + verified answers from files beside the TMDL (on-disk PBIP only).</summary>
        private static void ReadDiskArtifacts(Model model, PrepForAiConfig cfg)
        {
            var folder = ResolveModelFolder(model);
            if (folder == null || !Directory.Exists(folder)) return;
            cfg.SourceReadable = true;
            cfg.ModelFolder = folder;

            // Indexing setting: definition.pbism = { "settings": { "qnaEnabled": bool } }
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

    /// <summary>
    /// The linguistic-schema term readers, shared by the Engine's writers (LsdlSynonyms) and the readiness rules so a
    /// scan and a write read terms the same way. It lives here because Analysis cannot reference Engine.
    /// <see cref="Normalize"/> (lower-case + camel-split + punctuation fold, no lemma guess) is how two FIELD names are
    /// compared for DAC-QNA-NAME-COLLISIONS, a Copilot ambiguity that refuses no write (27 Sep ruling: no source shows
    /// the validator refusing two visible objects that normalise alike; the one refusal on record was entities for
    /// hidden shadow columns, which set_synonyms prunes). <see cref="NormalizeFolded"/> adds a naive plural fold that
    /// can be WRONG (News is not the plural of New); no clash is reported on it any more. The name-term readers
    /// (<see cref="NameTerm"/>, <see cref="ReadsAsName"/>, <see cref="StaleNameTerm"/>) decide which terms spell an
    /// object's own name; they also read Power BI Desktop's own split ('CustomerPONumber' as "customer PO number",
    /// 'Column1' as "column 1"). Known gaps: <see cref="Normalize"/> has no letter-run acronym split (XMLParser stays one
    /// word while XML Parser is two), so two FIELD names are not compared that way, and there is no Unicode NFC/NFD
    /// normalisation (composed vs decomposed accents read as different terms).
    /// </summary>
    public static class QnaTerms
    {
        /// <summary>The exact tier: lower-case, camel-split, punctuation → space, collapse. No plural fold.</summary>
        public static string Normalize(string name) => Core(name, fold: false);

        /// <summary>The lemma-approximation tier: <see cref="Normalize"/> plus a naive per-word singular fold.</summary>
        public static string NormalizeFolded(string name) => Core(name, fold: true);

        /// <summary>Whether objects that share one term really collide. A table and ONE object inside that same table do
        /// not: that is Microsoft's own naming guidance for a dimension's label column (DIM-PRIMARY-NAME), and Desktop
        /// saves exactly that shape in AdventureWorks (table Customer and column Customer[Customer], both carrying the
        /// term "customer"), so a guard that refused it would refuse every synonym write on Microsoft's sample. The table
        /// is dropped from a group when every other member lives in it; what remains collides only if it is still two or
        /// more objects. Objects in other tables, and two objects inside one table, still collide. The rule and the
        /// set_synonyms guard both ask this, so a scan and a write keep agreeing (the Plan statement check, 28 Sep).</summary>
        public static bool Collides<T>(IEnumerable<T> members) where T : class, ITabularNamedObject
        {
            var list = members.Distinct().ToList();
            var remaining = list.Count(o => !(o is Table t
                && list.All(x => ReferenceEquals(x, t) || ReferenceEquals((x as ITabularTableObject)?.Table, t))));
            return remaining > 1;
        }

        private static string Core(string name, bool fold)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var sb = new System.Text.StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (char.IsLetterOrDigit(ch))
                {
                    // camelCase boundary (DateKey → date key): a lower/digit → UPPER transition opens a word.
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && char.IsUpper(ch) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                        sb.Append(' ');
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');   // '_' '.' and every other punctuation/space run reads as one word break
            }
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (fold)
                for (var i = 0; i < words.Length; i++)
                {
                    var w = words[i];
                    // Naive singular fold (Months → month). >3 spares short words ("is", "gas"); the 'ss' guard spares "class".
                    if (w.Length > 3 && w[w.Length - 1] == 's' && w[w.Length - 2] != 's')
                        words[i] = w.Substring(0, w.Length - 1);
                }
            return string.Join(" ", words);
        }

        /// <summary>The name term Power BI Desktop writes for an object: the name in lower case, with a camelCase hump
        /// split into its own word ('CustomerId' becomes "customer id") and every other character kept ('Margin %'
        /// stays "margin %"). A hump is split only before a capital that starts a word of two or more letters, so a
        /// trailing capital ('YoY') stays joined.</summary>
        public static string NameTerm(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (i > 0 && char.IsUpper(ch) && char.IsLower(name[i - 1]) && i + 1 < name.Length && char.IsLetter(name[i + 1]))
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Does a linguistic-schema term spell the object's own name? Power BI Desktop writes a name term as
        /// the name in lower case with camelCase split ("customer id" for 'CustomerId'), sometimes also as written
        /// ("CustomerId"), and a plural name as its lemma ("customer" for 'Customers', "industry" for 'Industries',
        /// "total child" for 'Total Children', "person signed up" for 'PeopleSignedUp'). Each of those is the name, not
        /// a synonym and not a stale word. The writer's own form (<see cref="NameTerm"/>) always reads as the name, by
        /// construction: a writer and reader that disagree make set_synonyms tombstone the name term it just wrote.
        /// Only the spelling is judged here; whether a term is a name term at all (its State) is
        /// <see cref="CountsAsNameTerm"/>'s question.</summary>
        public static bool ReadsAsName(string term, string name)
        {
            if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(name)) return false;
            if (string.Equals(term, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(term, NameTerm(name), StringComparison.OrdinalIgnoreCase)) return true;
            string a = Normalize(term), b = Normalize(name);
            if (a.Length == 0) return false;
            if (a == b) return true;
            // Word by word, each word against the other's lemmas. An acronym's plural ('IDs', 'KPIs') loses its 's' first,
            // since the lower-cased word ("ids") no longer shows it was one. Two word splits are tried: ours, and Power BI
            // Desktop's, which also breaks an acronym run before its last capital ('CustomerPONumber' is "customer PO
            // number") and a letter from a digit ('Column1' is "column 1").
            if (SameWords(Normalize(DropAcronymPlural(term)).Split(' '), Normalize(DropAcronymPlural(name)).Split(' '))
                || SameWords(DesktopWords(DropAcronymPlural(term)), DesktopWords(DropAcronymPlural(name)))) return true;
            // Desktop also writes a name without its trailing bracketed qualifier ("average lead time" for 'Average Lead
            // Time (Days)'), first and ahead of the full name.
            var bare = WithoutTrailingQualifier(name);
            return bare != null && ReadsAsName(term, bare);
        }

        private static bool SameWords(string[] aw, string[] bw)
        {
            if (aw.Length != bw.Length) return false;
            for (var i = 0; i < aw.Length; i++)
                if (aw[i] != bw[i] && !Lemmas(aw[i]).Overlaps(Lemmas(bw[i]))) return false;
            return true;
        }

        /// <summary>Does every word of <paramref name="term"/> spell (or lemmatise to) a word of the object's name?
        /// That is the shape of the words Power BI derives from a name: "child" for 'Total Children', "name" for
        /// 'Customer Name', "pipeline" for 'Revenue In Pipeline'. A word from an OLD name ("sales amount" beside 'Revenue',
        /// "PO number" beside 'Order Reference') is not.</summary>
        public static bool IsDerivedFromName(string term, string name)
        {
            if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(name)) return false;
            var nameWords = Normalize(DropAcronymPlural(name)).Split(' ').Concat(DesktopWords(DropAcronymPlural(name)))
                .Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            bool InName(string w) => nameWords.Any(n => n == w || Lemmas(n).Overlaps(Lemmas(w)));
            var ours = Normalize(DropAcronymPlural(term)).Split(' ').Where(w => w.Length > 0).ToArray();
            var desktop = DesktopWords(DropAcronymPlural(term));
            return ours.Length > 0 && (ours.All(InName) || (desktop.Length > 0 && desktop.All(InName)));
        }

        // Power BI Desktop's word split: lower case, a break at every non-letter-or-digit run, before a capital that
        // follows a lower-case letter or a digit, before the last capital of an acronym run that a lower-case letter
        // follows ('PONumber' is "po number"), and between a letter and a digit ('Column1' is "column 1").
        private static string[] DesktopWords(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 8);
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (!char.IsLetterOrDigit(ch)) { sb.Append(' '); continue; }
                if (i > 0 && char.IsLetterOrDigit(s[i - 1]))
                {
                    var prev = s[i - 1];
                    var brk = (char.IsUpper(ch) && (char.IsLower(prev) || char.IsDigit(prev)))
                              || (char.IsUpper(ch) && char.IsUpper(prev) && i + 1 < s.Length && char.IsLower(s[i + 1]))
                              || (char.IsDigit(ch) && char.IsLetter(prev));
                    if (brk) sb.Append(' ');
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // 'Average Lead Time (Days)' -> 'Average Lead Time'; null when the name carries no trailing bracketed part.
        private static string WithoutTrailingQualifier(string name)
        {
            var t = name.TrimEnd();
            if (!t.EndsWith(")", StringComparison.Ordinal)) return null;
            var open = t.LastIndexOf('(');
            if (open <= 0) return null;
            var bare = t.Substring(0, open).TrimEnd();
            return Normalize(bare).Length == 0 ? null : bare;
        }

        /// <summary>Is a term with this State one Power BI generated for the object's name? State Generated says so. So
        /// does no State with no LastModified stamp, but only when the word spells the name: after an author edits
        /// synonyms, Power BI Desktop rewrites its name term with no State ({"sales amount":{}}). A missing State means
        /// Authored in the published contract (Microsoft Learn, "Edit Q&A linguistic schema"; Tabular Editor's
        /// TermProperties), so a stateless word that does NOT spell the name is the author's synonym, and in a schema
        /// without Desktop's stamps it is never an old name (see <see cref="StaleNameTerm"/>). <paramref name="state"/>
        /// is null when the term has no State.</summary>
        public static bool CountsAsNameTerm(string word, string state, bool stamped, string name) =>
            (IsGeneratedState(state) || (state == null && !stamped)) && ReadsAsName(word, name);

        /// <summary>An explicit State Generated.</summary>
        public static bool IsGeneratedState(string state) => string.Equals(state, "Generated", StringComparison.OrdinalIgnoreCase);

        /// <summary>A word Power BI wrote rather than the author: State Generated, or, in a schema whose authored words
        /// carry Desktop's LastModified stamp (<see cref="AuthoredWordsStamped"/>), a stateless UNSTAMPED word. After an
        /// author edits synonyms, Desktop rewrites its own words with no State and stamps only the author's (Microsoft's
        /// Regional Sales sample: "pipeline" {Weight 0.97} beside the author's stamped "Rev In Pipeline"). In a schema
        /// without stamps a stateless word is the author's, as the published contract says.</summary>
        public static bool IsPowerBiWord(string state, bool stamped, bool desktopStamps) =>
            IsGeneratedState(state) || (desktopStamps && state == null && !stamped);

        /// <summary>Do the schema's authored words carry Desktop's stamp? True when some term is stamped and stamped
        /// terms are at least as many as explicit State Authored ones without a stamp (a hand-written schema's style).
        /// The scan and set_synonyms both read a schema this way, over every term of every entity.</summary>
        public static bool AuthoredWordsStamped(IEnumerable<(string State, bool Stamped)> terms)
        {
            int stamped = 0, plain = 0;
            foreach (var (state, st) in terms)
                if (st) stamped++;
                else if (string.Equals(state, "Authored", StringComparison.OrdinalIgnoreCase)) plain++;
            return stamped > 0 && stamped >= plain;
        }

        /// <summary>Is a word Power BI wrote a leftover of the entity's old name rather than one of its own words? It
        /// spells the stale name (<see cref="StaleNameTerm"/>, in any spelling Desktop wrote it: "customer key" and
        /// "CustomerKey"), or it is made of the stale name's words and not the current name's ("key" beside 'ClientCode'
        /// once "customer key" is stale). Every other word Power BI wrote is its own, including words not made of the
        /// name at all ("date" on a date column, "postal code" on 'Zip Code', "customer name" on 'Customer').</summary>
        public static bool IsOldWord(string term, string name, string stale) =>
            stale != null && (ReadsAsName(term, stale) || (IsDerivedFromName(term, stale) && !IsDerivedFromName(term, name)));

        /// <summary>The entity's STALE name term: the name as last recorded when nothing in the entity reads as the
        /// object's current name any more (a rename made without regenerating it). An entity that still carries its name
        /// term is not stale, whatever derived words sit beside it ("child" beside "total child"). Only a word Power BI
        /// wrote (<see cref="IsPowerBiWord"/>) can be the stale word, since the author's is never an old name; it is the
        /// highest-weight one (Weight defaults to 1.0; ties keep array order). Null when not stale. The scan
        /// (DAC-QNA-NAME-COLLISIONS) and the writer (set_synonyms, which clears the stale word a list leaves out) both
        /// call this, so they agree on which word is stale. Deleted and Suggested terms carry those States, so they
        /// neither count as the name nor become the stale word.</summary>
        public static string StaleNameTerm(IEnumerable<(string Word, string State, bool Stamped, double Weight)> terms, string name,
                                           bool desktopStamps = false)
        {
            string best = null;
            var bestWeight = double.MinValue;
            foreach (var t in terms)
            {
                if (CountsAsNameTerm(t.Word, t.State, t.Stamped, name)) return null;
                if (IsPowerBiWord(t.State, t.Stamped, desktopStamps) && (best == null || t.Weight > bestWeight)) { best = t.Word; bestWeight = t.Weight; }
            }
            return best;
        }

        // 'IDs' -> 'ID', 'Order KPIs' -> 'Order KPI': an 's' closing a run of two or more capitals.
        private static string DropAcronymPlural(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == 's' && i >= 2 && char.IsUpper(s[i - 1]) && char.IsUpper(s[i - 2]) && (i + 1 == s.Length || !char.IsLetter(s[i + 1])))
                    continue;
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // The irregular plurals Power BI Desktop lemmatises (observed: children, people), plus the common English ones.
        // A compound ending in one ('Grandchildren', 'Salespeople', 'Chairmen') is matched by its ending.
        private static readonly (string Plural, string Singular)[] Irregular =
        {
            ("children", "child"), ("people", "person"), ("men", "man"), ("criteria", "criterion"), ("phenomena", "phenomenon"),
            ("indices", "index"), ("matrices", "matrix"), ("vertices", "vertex"), ("appendices", "appendix"), ("mice", "mouse"),
            ("geese", "goose"), ("feet", "foot"), ("teeth", "tooth"),
        };

        // Every word this one could be the plural of, plus itself: two words read as one when their sets meet, so a
        // singular never has to be guessed right, only included. Wider than the fold tier's naive fold (which stays a
        // conservative approximation of the validator): -s, -es (statuses, heroes, quizzes via -zzes), -ies to -y and
        // to -ie (-s), -ves to -f and -fe, -ses to -sis (analyses), and the irregular list.
        private static HashSet<string> Lemmas(string w)
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { w };
            foreach (var (plural, singular) in Irregular)
                if (w.EndsWith(plural, StringComparison.Ordinal)) set.Add(w.Substring(0, w.Length - plural.Length) + singular);
            if (w.Length > 3 && w[w.Length - 1] == 's' && w[w.Length - 2] != 's') set.Add(w.Substring(0, w.Length - 1));
            if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal)) set.Add(w.Substring(0, w.Length - 2));
            if (w.Length > 5 && w.EndsWith("zzes", StringComparison.Ordinal)) set.Add(w.Substring(0, w.Length - 3));
            if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) set.Add(w.Substring(0, w.Length - 3) + "y");
            if (w.Length > 4 && w.EndsWith("ves", StringComparison.Ordinal)) { set.Add(w.Substring(0, w.Length - 3) + "f"); set.Add(w.Substring(0, w.Length - 3) + "fe"); }
            if (w.Length > 5 && w.EndsWith("ses", StringComparison.Ordinal)) set.Add(w.Substring(0, w.Length - 2) + "is");
            return set;
        }
    }
}
