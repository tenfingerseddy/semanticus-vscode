using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Semanticus.Engine
{
    // "Your standard" (1.7): one readable YAML file that says how a model is held. A consultant keeps one per client, a team
    // keeps one in its repo. This file is the shape, the three built-in presets, the reader/writer and the resolution order.
    // What APPLYING does lives in StandardApply.cs. The standard never changes a grade: grades stay absolute (a Light standard
    // must not make a model look better than Copilot will find it). It only decides what reaches the Plan and what is changed now.

    /// <summary>The part names, in the order the page shows them.</summary>
    public static class StandardParts
    {
        public const string Checks = "checks";
        public const string Names = "names";
        public const string Formats = "formats";
        public const string Folders = "folders";
        public const string Descriptions = "descriptions";
        public const string Synonyms = "synonyms";
        public const string KeysAndTotals = "keys-and-totals";
        public const string AiInstructions = "ai-instructions";

        public static readonly string[] All = { Checks, Names, Formats, Folders, Descriptions, Synonyms, KeysAndTotals, AiInstructions };
    }

    /// <summary>The checks part. <see cref="Level"/> decides which findings become Plan tasks, never the grade.</summary>
    public sealed class StandardChecks
    {
        /// <summary>light: only checks that change answers. balanced: as shipped. strict: warnings are tasks too.</summary>
        public string Level { get; set; }
        /// <summary>Rule ids that are never a task and never on the Plan.</summary>
        public string[] Off { get; set; }
        /// <summary>Rule ids that are always a task, whatever the level.</summary>
        public string[] Must { get; set; }
        /// <summary>A rules file (Tabular Editor format) merged by id, read with load_rules.</summary>
        public string ExtraRules { get; set; }
    }

    /// <summary>A standard as written in a file or worked out. A null part is "not written here": it comes from further down
    /// the order (the pointed-to file, then the preset). After <see cref="StandardStore.Resolve"/> every part of the
    /// effective standard is set.</summary>
    public sealed class StandardSpec
    {
        public string Standard { get; set; }
        public string StartsFrom { get; set; }
        /// <summary>A path or an https address of a shared standard, read only. Parts written beside it win for this model.</summary>
        public string Use { get; set; }
        public StandardChecks Checks { get; set; }
        /// <summary>leave | title-case | title-case-no-prefix</summary>
        public string Names { get; set; }
        /// <summary>By measure kind: money, percent, count. A kind that is not written is left alone.</summary>
        public Dictionary<string, string> Formats { get; set; }
        /// <summary>none | by-subject, over N | by-subject, always</summary>
        public string Folders { get; set; }
        /// <summary>none | measures | measures, tables | all-visible</summary>
        public string Descriptions { get; set; }
        /// <summary>none | N, measures and tables | N, all-visible</summary>
        public string Synonyms { get; set; }
        /// <summary>leave | hide keys, no-sum ids | hide keys and technical, no-sum ids</summary>
        public string KeysAndTotals { get; set; }
        /// <summary>none | a Markdown file with {fiscal year start} and {currency} blanks</summary>
        public string AiInstructions { get; set; }

        public StandardSpec Clone() => new StandardSpec
        {
            Standard = Standard, StartsFrom = StartsFrom, Use = Use,
            Checks = Checks == null ? null : new StandardChecks
            {
                Level = Checks.Level, ExtraRules = Checks.ExtraRules,
                Off = Checks.Off == null ? null : (string[])Checks.Off.Clone(),
                Must = Checks.Must == null ? null : (string[])Checks.Must.Clone(),
            },
            Names = Names, Folders = Folders, Descriptions = Descriptions, Synonyms = Synonyms,
            KeysAndTotals = KeysAndTotals, AiInstructions = AiInstructions,
            Formats = Formats == null ? null : new Dictionary<string, string>(Formats, StringComparer.OrdinalIgnoreCase),
        };

        /// <summary>The parts written in <paramref name="over"/> replace the ones here. Lists and the formats map are replaced as a
        /// whole kind at a time (a format kind is one entry), so a patch that says money does not erase percent.</summary>
        public StandardSpec MergedWith(StandardSpec over)
        {
            var r = Clone();
            if (over == null) return r;
            if (!string.IsNullOrWhiteSpace(over.Standard)) r.Standard = over.Standard;
            if (!string.IsNullOrWhiteSpace(over.StartsFrom)) r.StartsFrom = over.StartsFrom;
            if (!string.IsNullOrWhiteSpace(over.Use)) r.Use = over.Use;
            if (over.Checks != null)
            {
                r.Checks ??= new StandardChecks();
                if (!string.IsNullOrWhiteSpace(over.Checks.Level)) r.Checks.Level = over.Checks.Level;
                if (over.Checks.Off != null) r.Checks.Off = over.Checks.Off;
                if (over.Checks.Must != null) r.Checks.Must = over.Checks.Must;
                if (over.Checks.ExtraRules != null) r.Checks.ExtraRules = over.Checks.ExtraRules;
            }
            if (!string.IsNullOrWhiteSpace(over.Names)) r.Names = over.Names;
            if (over.Formats != null)
            {
                r.Formats ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in over.Formats) r.Formats[kv.Key] = kv.Value;
            }
            if (!string.IsNullOrWhiteSpace(over.Folders)) r.Folders = over.Folders;
            if (!string.IsNullOrWhiteSpace(over.Descriptions)) r.Descriptions = over.Descriptions;
            if (!string.IsNullOrWhiteSpace(over.Synonyms)) r.Synonyms = over.Synonyms;
            if (!string.IsNullOrWhiteSpace(over.KeysAndTotals)) r.KeysAndTotals = over.KeysAndTotals;
            if (over.AiInstructions != null) r.AiInstructions = over.AiInstructions;
            return r;
        }
    }

    /// <summary>The plain-word values each part accepts, with their typed reading. One place, so the reader, the patch check and
    /// the apply step agree on what a value means.</summary>
    public static class StandardValues
    {
        public static readonly string[] Levels = { "light", "balanced", "strict" };
        public static readonly string[] NamesModes = { "leave", "title-case", "title-case-no-prefix" };
        public static readonly string[] DescriptionModes = { "none", "measures", "measures, tables", "all-visible" };
        public static readonly string[] KeysModes = { "leave", "hide keys, no-sum ids", "hide keys and technical, no-sum ids" };
        public static readonly string[] FormatKinds = { "money", "percent", "count" };

        /// <summary>The folder rule read from "none" | "by-subject, over N" | "by-subject, always". Over = the number of unfoldered
        /// visible measures above which folders are made (0 = always). Returns false with a plain reason when it cannot be read.</summary>
        public static bool TryFolders(string text, out bool on, out int over, out string why)
        {
            on = false; over = 0; why = null;
            var t = Norm(text);
            if (t == "" || t == "none") return true;
            if (t == "by-subject, always" || t == "by-subject") { on = true; return true; }
            if (t.StartsWith("by-subject, over ", StringComparison.Ordinal)
                && int.TryParse(t.Substring("by-subject, over ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out over) && over >= 0)
            { on = true; return true; }
            why = "folders is none, by-subject always, or by-subject over a number (for example: by-subject, over 10).";
            return false;
        }

        /// <summary>The synonyms rule read from "none" | "N, measures and tables" | "N, all-visible". Count = how many other names each object gets.</summary>
        public static bool TrySynonyms(string text, out int count, out bool allVisible, out string why)
        {
            count = 0; allVisible = false; why = null;
            var t = Norm(text);
            if (t == "" || t == "none") return true;
            var comma = t.IndexOf(',');
            if (comma > 0 && int.TryParse(t.Substring(0, comma).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0)
            {
                var rest = t.Substring(comma + 1).Trim();
                if (rest == "measures and tables") return true;
                if (rest == "all-visible") { allVisible = true; return true; }
            }
            why = "synonyms is none, or a number then measures and tables or all-visible (for example: 2, measures and tables).";
            return false;
        }

        public static string Norm(string s) => (s ?? string.Empty).Trim().ToLowerInvariant();
        public static bool OneOf(string value, string[] allowed) => allowed.Contains(Norm(value), StringComparer.Ordinal);
    }

    /// <summary>The three built-in standards. Balanced is what most people get without opening the page.</summary>
    public static class StandardPresets
    {
        public static StandardSpec Get(string name)
        {
            switch (StandardValues.Norm(name))
            {
                case "light":
                    return new StandardSpec
                    {
                        Standard = "Light", StartsFrom = "light",
                        Checks = new StandardChecks { Level = "light", Off = new string[0], Must = new string[0] },
                        Names = "leave", Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        Folders = "none", Descriptions = "none", Synonyms = "none",
                        KeysAndTotals = "hide keys, no-sum ids", AiInstructions = "none",
                    };
                case "strict":
                    return new StandardSpec
                    {
                        Standard = "Strict", StartsFrom = "strict",
                        Checks = new StandardChecks { Level = "strict", Off = new string[0], Must = new string[0] },
                        Names = "title-case",
                        Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["percent"] = "0.0%", ["count"] = "#,0" },
                        Folders = "by-subject, always", Descriptions = "all-visible", Synonyms = "2, all-visible",
                        KeysAndTotals = "hide keys and technical, no-sum ids", AiInstructions = "none",
                    };
                default:
                    return new StandardSpec
                    {
                        Standard = "Balanced", StartsFrom = "balanced",
                        Checks = new StandardChecks { Level = "balanced", Off = new string[0], Must = new string[0] },
                        Names = "leave", Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        Folders = "by-subject, over 10", Descriptions = "measures, tables", Synonyms = "2, measures and tables",
                        KeysAndTotals = "hide keys, no-sum ids", AiInstructions = "none",
                    };
            }
        }
    }

    /// <summary>Reads and writes the YAML. The reader is forgiving about layout (flow or block lists, quoted or bare values) and
    /// strict about meaning: a value it cannot read is named in plain words, never guessed.</summary>
    public static class StandardYaml
    {
        public static StandardSpec Parse(string text, out string[] problems)
        {
            var bad = new List<string>();
            var spec = new StandardSpec();
            YamlMappingNode root = null;
            try
            {
                var ys = new YamlStream();
                ys.Load(new StringReader(text ?? string.Empty));
                if (ys.Documents.Count > 0) root = ys.Documents[0].RootNode as YamlMappingNode;
            }
            catch (YamlException e) { bad.Add("The standard file is not valid YAML: " + e.Message); }
            if (root == null) { problems = bad.ToArray(); return spec; }

            foreach (var kv in root.Children)
            {
                var key = ((kv.Key as YamlScalarNode)?.Value ?? string.Empty).Trim().ToLowerInvariant();
                switch (key)
                {
                    case "standard": spec.Standard = Scalar(kv.Value); break;
                    case "starts-from": spec.StartsFrom = Scalar(kv.Value); break;
                    case "use": spec.Use = Scalar(kv.Value); break;
                    case "checks":
                        if (kv.Value is YamlMappingNode cm)
                        {
                            spec.Checks = new StandardChecks();
                            foreach (var c in cm.Children)
                            {
                                var ck = ((c.Key as YamlScalarNode)?.Value ?? string.Empty).Trim().ToLowerInvariant();
                                if (ck == "level") spec.Checks.Level = Scalar(c.Value);
                                else if (ck == "off") spec.Checks.Off = List(c.Value);
                                else if (ck == "must") spec.Checks.Must = List(c.Value);
                                else if (ck == "extra-rules") spec.Checks.ExtraRules = Scalar(c.Value);
                                else bad.Add("checks has no part called '" + ck + "'.");
                            }
                        }
                        else bad.Add("checks needs lines under it: level, off, must, extra-rules.");
                        break;
                    case "names": spec.Names = Scalar(kv.Value); break;
                    case "formats":
                        if (kv.Value is YamlMappingNode fm)
                        {
                            spec.Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var f in fm.Children) spec.Formats[(Scalar(f.Key) ?? string.Empty).Trim()] = Scalar(f.Value);
                        }
                        else bad.Add("formats needs a line for each kind: money, percent, count.");
                        break;
                    case "folders": spec.Folders = Scalar(kv.Value); break;
                    case "descriptions": spec.Descriptions = Scalar(kv.Value); break;
                    case "synonyms": spec.Synonyms = Scalar(kv.Value); break;
                    case "keys-and-totals": spec.KeysAndTotals = Scalar(kv.Value); break;
                    case "ai-instructions": spec.AiInstructions = Scalar(kv.Value); break;
                    default: bad.Add("'" + key + "' is not a part of a standard."); break;
                }
            }
            problems = bad.ToArray();
            return spec;
        }

        private static string Scalar(YamlNode n) => (n as YamlScalarNode)?.Value;

        private static string[] List(YamlNode n)
        {
            if (n is YamlSequenceNode seq) return seq.Children.Select(Scalar).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
            var one = Scalar(n);
            return string.IsNullOrWhiteSpace(one) ? new string[0] : one.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        }

        /// <summary>What is wrong with the values of a standard, in plain words, or an empty list. Used by the reader and by set_standard
        /// before it writes, so a bad patch changes nothing.</summary>
        public static string[] Check(StandardSpec s)
        {
            var bad = new List<string>();
            if (s == null) return bad.ToArray();
            if (!string.IsNullOrWhiteSpace(s.StartsFrom) && !StandardValues.OneOf(s.StartsFrom, StandardValues.Levels)) bad.Add("starts-from is light, balanced or strict.");
            if (s.Checks != null && !string.IsNullOrWhiteSpace(s.Checks.Level) && !StandardValues.OneOf(s.Checks.Level, StandardValues.Levels)) bad.Add("checks level is light, balanced or strict.");
            if (!string.IsNullOrWhiteSpace(s.Names) && !StandardValues.OneOf(s.Names, StandardValues.NamesModes)) bad.Add("names is leave, title-case or title-case-no-prefix.");
            if (!string.IsNullOrWhiteSpace(s.Descriptions) && !StandardValues.OneOf(s.Descriptions, StandardValues.DescriptionModes)) bad.Add("descriptions is none, measures, 'measures, tables' or all-visible.");
            if (!string.IsNullOrWhiteSpace(s.KeysAndTotals) && !StandardValues.OneOf(s.KeysAndTotals, StandardValues.KeysModes)) bad.Add("keys-and-totals is leave, 'hide keys, no-sum ids' or 'hide keys and technical, no-sum ids'.");
            if (!string.IsNullOrWhiteSpace(s.Folders) && !StandardValues.TryFolders(s.Folders, out _, out _, out var fw)) bad.Add(fw);
            if (!string.IsNullOrWhiteSpace(s.Synonyms) && !StandardValues.TrySynonyms(s.Synonyms, out _, out _, out var sw)) bad.Add(sw);
            if (s.Formats != null)
                foreach (var kv in s.Formats)
                {
                    if (!StandardValues.OneOf(kv.Key, StandardValues.FormatKinds)) bad.Add("formats has no kind called '" + kv.Key + "'. The kinds are money, percent and count.");
                    else if (!string.IsNullOrWhiteSpace(kv.Value) && StandardValues.Norm(kv.Value) != "leave" && kv.Value.IndexOfAny(new[] { '0', '#' }) < 0) bad.Add("The " + kv.Key + " format '" + kv.Value + "' has no digit place (0 or #).");
                }
            return bad.ToArray();
        }

        /// <summary>The file text for <paramref name="s"/>: only the parts that are written, in the order the page shows them.</summary>
        public static string Write(StandardSpec s, string header = null)
        {
            var sb = new StringBuilder();
            sb.Append(header ?? "# A Semanticus standard. Every model that uses this file is held to it.").Append('\n');
            void Line(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) sb.Append(key).Append(": ").Append(Q(value)).Append('\n'); }
            Line("standard", s.Standard);
            Line("use", s.Use);
            Line("starts-from", s.StartsFrom);
            if (s.Checks != null)
            {
                var any = !string.IsNullOrWhiteSpace(s.Checks.Level) || s.Checks.Off != null || s.Checks.Must != null || s.Checks.ExtraRules != null;
                if (any)
                {
                    sb.Append("checks:\n");
                    if (!string.IsNullOrWhiteSpace(s.Checks.Level)) sb.Append("  level: ").Append(Q(s.Checks.Level)).Append('\n');
                    if (s.Checks.Off != null) sb.Append("  off: [").Append(string.Join(", ", s.Checks.Off.Select(Q))).Append("]\n");
                    if (s.Checks.Must != null) sb.Append("  must: [").Append(string.Join(", ", s.Checks.Must.Select(Q))).Append("]\n");
                    if (s.Checks.ExtraRules != null) sb.Append("  extra-rules: ").Append(Q(s.Checks.ExtraRules)).Append('\n');
                }
            }
            Line("names", s.Names);
            if (s.Formats != null && s.Formats.Count > 0)
                sb.Append("formats: { ").Append(string.Join(", ", s.Formats.Select(kv => kv.Key + ": " + Q(kv.Value ?? "leave")))).Append(" }\n");
            Line("folders", s.Folders);
            Line("descriptions", s.Descriptions);
            Line("synonyms", s.Synonyms);
            Line("keys-and-totals", s.KeysAndTotals);
            if (s.AiInstructions != null) Line("ai-instructions", s.AiInstructions);
            return sb.ToString();
        }

        // Always double-quote: a format such as "$#,0.00" or "#,0" holds characters YAML treats as syntax or comments.
        private static string Q(string v)
        {
            var d = (char)34; var b = (char)92;
            return d + (v ?? string.Empty).Replace(b.ToString(), b.ToString() + b).Replace(d.ToString(), b.ToString() + d) + d;
        }
    }

    /// <summary>Where a standard came from, plus the standard worked out for one model.</summary>
    public sealed class StandardResolved
    {
        /// <summary>The standard in force: every part set.</summary>
        public StandardSpec Effective { get; set; }
        /// <summary>The model's own file (or the user default) as written, before the preset and the pointed-to file fill the gaps. Null when built in.</summary>
        public StandardSpec Written { get; set; }
        /// <summary>model-file | your-default | built-in</summary>
        public string Source { get; set; }
        /// <summary>The file read, or null when built in.</summary>
        public string Path { get; set; }
        /// <summary>Where a set_standard patch is written by default: the model's file when the model is saved, else your default.</summary>
        public string WritePath { get; set; }
        /// <summary>The address or path in the use: pointer, when there is one.</summary>
        public string Use { get; set; }
        /// <summary>A plain sentence when something could not be read (the pointed-to file, a bad value). The standard still resolves.</summary>
        public string[] Notes { get; set; } = new string[0];
        /// <summary>Short hash of the effective standard, recorded on the model so it can say "Checked against Contoso".</summary>
        public string Hash { get; set; }
        public string Name => Effective?.Standard;
    }

    /// <summary>Finds and reads the standard. First match wins: the model's .semanticus/standard.yaml, then ~/.semanticus/standard.yaml,
    /// then Balanced. A use: pointer reads a shared file (a path, or https) and the lines beside it win for this model.</summary>
    public static class StandardStore
    {
        public const string FileName = "standard.yaml";
        public const string Annotation = "Semanticus_Standard";

        public static string UserFile() => System.IO.Path.Combine(SemanticusHome.Root(), FileName);

        /// <summary>The model's sidecar file, or null when the model has never been saved.</summary>
        public static string ModelFile(string sourcePath)
        {
            var dir = LayoutStore.DirFor(sourcePath);
            return dir == null ? null : System.IO.Path.Combine(dir, FileName);
        }

        /// <summary>Works out the standard in force. A use: https address is read from the cache (this session's copy, then the sidecar's) and
        /// never from the network, unless <paramref name="fetch"/> is true or a <paramref name="readUrl"/> is given: only opening the standard,
        /// applying it and set_standard ask for that (1.7 threat check, DEC-0017). The Plan and the gate read the cache.</summary>
        public static StandardResolved Resolve(string sourcePath, Func<string, string> readUrl = null, StandardSpec writtenOverride = null, bool fetch = false)
        {
            var notes = new List<string>();
            var modelFile = ModelFile(sourcePath);
            string path = null, source = "built-in";
            StandardSpec written = null;

            if (modelFile != null && File.Exists(modelFile)) { path = modelFile; source = "model-file"; }
            else
            {
                string user = null;
                try { user = UserFile(); } catch (InvalidOperationException) { }
                if (user != null && File.Exists(user)) { path = user; source = "your-default"; }
            }
            if (path != null)
            {
                try
                {
                    written = StandardYaml.Parse(File.ReadAllText(path), out var problems);
                    notes.AddRange(problems);
                }
                catch (IOException e) { notes.Add("Semanticus could not read " + path + ": " + e.Message); }
                catch (UnauthorizedAccessException e) { notes.Add("Semanticus could not read " + path + ": " + e.Message); }
            }

            // A preview asks "what if this were written": the lines come from the caller, the file it would go in stays where it is.
            if (writtenOverride != null) written = writtenOverride;

            StandardSpec pointed = null;
            if (written != null && !string.IsNullOrWhiteSpace(written.Use))
            {
                pointed = ReadShared(written.Use, path ?? ModelFile(sourcePath) ?? SafeUserFile(), readUrl, fetch, sourcePath, notes);
            }

            var start = StandardValues.OneOf(written?.StartsFrom, StandardValues.Levels) ? written.StartsFrom
                      : StandardValues.OneOf(pointed?.StartsFrom, StandardValues.Levels) ? pointed.StartsFrom : "balanced";
            var effective = StandardPresets.Get(start).MergedWith(pointed).MergedWith(written);
            // A value that does not read keeps the preset's: the standard still works, and the note says what was skipped.
            var bad = StandardYaml.Check(effective);
            if (bad.Length > 0)
            {
                notes.AddRange(bad.Select(b => "Not used: " + b));
                var preset = StandardPresets.Get(start);
                if (!StandardValues.OneOf(effective.Names, StandardValues.NamesModes)) effective.Names = preset.Names;
                if (!StandardValues.OneOf(effective.Descriptions, StandardValues.DescriptionModes)) effective.Descriptions = preset.Descriptions;
                if (!StandardValues.OneOf(effective.KeysAndTotals, StandardValues.KeysModes)) effective.KeysAndTotals = preset.KeysAndTotals;
                if (!StandardValues.TryFolders(effective.Folders, out _, out _, out _)) effective.Folders = preset.Folders;
                if (!StandardValues.TrySynonyms(effective.Synonyms, out _, out _, out _)) effective.Synonyms = preset.Synonyms;
                if (effective.Checks != null && !StandardValues.OneOf(effective.Checks.Level, StandardValues.Levels)) effective.Checks.Level = preset.Checks.Level;
                if (effective.Formats != null)
                    foreach (var k in effective.Formats.Keys.Where(k => !StandardValues.OneOf(k, StandardValues.FormatKinds)).ToList()) effective.Formats.Remove(k);
            }
            if (string.IsNullOrWhiteSpace(effective.Standard)) effective.Standard = char.ToUpperInvariant(start[0]) + start.Substring(1);
            effective.StartsFrom = start;

            return new StandardResolved
            {
                Effective = effective, Written = written, Source = source, Path = path,
                WritePath = modelFile ?? SafeUserFile(), Use = written?.Use,
                Notes = notes.ToArray(), Hash = HashOf(effective),
            };
        }

        private static string SafeUserFile() { try { return UserFile(); } catch (InvalidOperationException) { return null; } }

        /// <summary>Test seam (InternalsVisibleTo): replaces the web request, so a test can count the requests the engine makes.</summary>
        internal static Func<string, string> UrlReaderForTest;

        // The last good copy of each shared address, for this session. The sidecar keeps one too, for offline use after a restart.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SharedCopies = new(StringComparer.OrdinalIgnoreCase);
        internal static void ForgetSharedCopies() => SharedCopies.Clear();
        public const string CacheFolder = "standard-cache";

        private static string CacheFile(string sourcePath, string url)
        {
            string dir;
            try { dir = LayoutStore.DirFor(sourcePath) ?? System.IO.Path.GetDirectoryName(UserFile()); } catch (InvalidOperationException) { return null; }
            if (string.IsNullOrEmpty(dir)) return null;
            using var sha = SHA256.Create();
            var h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url)), 0, 8).Replace("-", "").ToLowerInvariant();
            return System.IO.Path.Combine(dir, CacheFolder, h + ".yaml");
        }

        private static string CachedCopy(string sourcePath, string url)
        {
            if (SharedCopies.TryGetValue(url, out var mem)) return mem;
            try
            {
                var file = CacheFile(sourcePath, url);
                if (file != null && File.Exists(file)) return SharedCopies[url] = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return null;
        }

        private static void KeepCopy(string sourcePath, string url, string text)
        {
            SharedCopies[url] = text;
            try
            {
                var file = CacheFile(sourcePath, url);
                if (file == null) return;
                var dir = System.IO.Path.GetDirectoryName(file);
                Directory.CreateDirectory(dir);
                var side = System.IO.Path.GetDirectoryName(dir);
                if (string.Equals(System.IO.Path.GetFileName(side), LayoutStore.DirName, StringComparison.Ordinal))
                    try { LayoutStore.EnsureRuntimeIgnore(side); } catch { /* a courtesy; the copy is kept either way */ }
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, file, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* the session copy stands */ }
        }

        private static StandardSpec ReadShared(string use, string fromFile, Func<string, string> readUrl, bool fetch, string sourcePath, List<string> notes)
        {
            try
            {
                string text;
                if (use.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (fetch || readUrl != null)
                    {
                        try { text = (readUrl ?? UrlReaderForTest ?? FetchUrl)(use); KeepCopy(sourcePath, use, text); }
                        catch (Exception e)
                        {
                            text = CachedCopy(sourcePath, use);
                            if (text == null)
                            {
                                notes.Add("Semanticus could not fetch the shared standard at " + use + " (" + e.Message + "), so the standard in this model's own file and the preset are used.");
                                return null;
                            }
                            notes.Add("Semanticus could not fetch the shared standard at " + use + " (" + e.Message + "), so the last good copy is used.");
                        }
                    }
                    else
                    {
                        text = CachedCopy(sourcePath, use);
                        if (text == null)
                        {
                            notes.Add("The shared standard at " + use + " has not been fetched yet. Open or apply the standard to fetch it; until then the standard in this model's own file and the preset are used.");
                            return null;
                        }
                    }
                }
                else if (use.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("The shared standard at " + use + " is not read: use an https address.");
                    return null;
                }
                else
                {
                    var full = System.IO.Path.IsPathRooted(use) ? use : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fromFile) ?? ".", use));
                    if (!File.Exists(full)) { notes.Add("The shared standard " + use + " is not on this computer, so the standard in this model's own file and the preset are used."); return null; }
                    text = File.ReadAllText(full);
                }
                var shared = StandardYaml.Parse(text, out var problems);
                notes.AddRange(problems.Select(p => "In the shared standard: " + p));
                return shared;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                notes.Add("The shared standard " + use + " could not be read (" + e.Message + "), so the standard in this model's own file and the preset are used.");
                return null;
            }
        }

        private static string FetchUrl(string url)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }

        /// <summary>A short, stable fingerprint of what the standard says (not of how it is laid out).</summary>
        public static string HashOf(StandardSpec s)
        {
            var text = StandardYaml.Write(s, "");
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }
}
