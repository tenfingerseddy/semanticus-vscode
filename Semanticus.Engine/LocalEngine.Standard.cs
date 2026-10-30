using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // "Your standard" (1.7). The four tools: get_standard, set_standard, preview_standard, apply_standard. The file, the presets and
    // the reader are in Standard/StandardSpec.cs; what a standard does to a model is in Standard/StandardApply.cs.
    public sealed partial class LocalEngine
    {
        private static readonly string[] StandardPresetNames = { "light", "balanced", "strict" };

        public async Task<GetStandardResult> GetStandardAsync()
        {
            var s = _sessions.Current;
            var resolved = StandardStore.Resolve(s?.SourcePath, fetch: true);   // opening the standard is when a web address is fetched
            var checkedAgainst = s == null ? null : await s.ReadAsync(m => m.GetAnnotation(StandardStore.Annotation)).ConfigureAwait(false);
            return new GetStandardResult
            {
                Standard = StateOf(resolved, checkedAgainst),
                Presets = StandardPresetNames.Select(StandardPresets.Get).ToArray(),
            };
        }

        private static StandardState StateOf(StandardResolved r, string recorded)
        {
            string text = null;
            if (r.Path != null)
                try { text = File.ReadAllText(r.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new StandardState
            {
                Name = r.Name, Source = r.Source, Path = r.Path, WritePath = r.WritePath, Use = r.Use, Hash = r.Hash,
                UserPath = SafeUserStandardFile(), UserExists = SafeUserStandardFile() is string u && File.Exists(u),
                Effective = r.Effective, Written = r.Written, FileText = text, Notes = r.Notes,
                CheckedAgainst = RecordedStandardSentence(recorded),
            };
        }

        /// <summary>"Checked against Contoso" from the annotation the apply step leaves on the model, or null.</summary>
        private static string RecordedStandardSentence(string annotation)
        {
            if (string.IsNullOrWhiteSpace(annotation)) return null;
            try
            {
                using var doc = JsonDocument.Parse(annotation);
                return doc.RootElement.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())
                    ? "Checked against " + n.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        // ---- set_standard -------------------------------------------------------------------------

        /// <summary>Change the standard in words. <paramref name="patch"/> is a JSON object using the file's own keys
        /// ({"names":"title-case","formats":{"money":"$#,0.00"},"checks":{"off":["REL-SNOWFLAKE"]}}); a part set to null goes back to what
        /// the preset gives. <paramref name="target"/> is "model" (beside the model; the default for a saved model) or "your-default".
        /// A bad value changes nothing and the sentence says which one. Writes a file only: the model is not touched.</summary>
        public async Task<SetStandardResult> SetStandardAsync(string patch, string target, string origin)
        {
            await Task.CompletedTask;   // the file write is plain IO; the signature is the engine's async shape
            GuardOutsideWrite(origin);   // the standard file lives beside the model or in the person's home: an assistant's write asks under a strict policy
            if (string.IsNullOrWhiteSpace(patch)) throw new ArgumentException("Say what to change, as a JSON object, for example {\"names\":\"title-case\"}.", nameof(patch));
            var session = _sessions.Current;
            var resolved = StandardStore.Resolve(session?.SourcePath);   // the copy kept so far: the fetch is the one below, after the write

            var path = StandardWritePath(session?.SourcePath, target);
            RequireApprovedBasis(nameof(SetStandardAsync), GateFileState(path), "standard file");

            // The lines already in that file are what a patch changes. With no file there yet, start from the standard in force, so the new
            // file holds the whole standard and nothing falls back to a different one.
            StandardSpec written;
            if (File.Exists(path)) written = StandardYaml.Parse(File.ReadAllText(path), out _);
            else if (resolved.Written != null) written = resolved.Written.Clone();
            else written = new StandardSpec { StartsFrom = resolved.Effective.StartsFrom };

            var changed = new List<string>();
            var problems = new List<string>();
            ApplyStandardPatch(written, patch, changed, problems);
            problems.AddRange(StandardYaml.Check(written));
            if (problems.Count > 0)
                throw new InvalidOperationException("Nothing was changed. " + string.Join(" ", problems.Distinct()));

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, StandardYaml.Write(written));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);

            // The Plan reads the standard each time it is worked out, but it only works itself out again on a change; tell it.
            try { if (session != null) TellThePlanMoved(BindPlanMemory()); } catch { /* no plan to tell */ }

            var now = StandardStore.Resolve(session?.SourcePath, fetch: true);   // set_standard fetches once, for the standard as it now reads
            return new SetStandardResult
            {
                Path = path, Changed = changed.ToArray(), Standard = StateOf(now, null),
                Sentence = changed.Count == 0 ? "The standard already says that." : "Changed " + string.Join(", ", changed) + ". It is " + now.Name + ".",
            };
        }

        /// <summary>The file set_standard writes: your default when asked, else the one beside the model, else your default for a model that was never saved.</summary>
        private static string StandardWritePath(string sourcePath, string target)
        {
            var wantsUser = string.Equals(target, "your-default", StringComparison.OrdinalIgnoreCase) || string.Equals(target, "user", StringComparison.OrdinalIgnoreCase);
            if (wantsUser) return SafeUserStandardFile() ?? throw new InvalidOperationException("Semanticus has no folder of yours to keep a default standard in.");
            return StandardStore.ModelFile(sourcePath) ?? SafeUserStandardFile()
                ?? throw new InvalidOperationException("Save the model first, or set target to your-default.");
        }

        /// <summary>The door's side of set_standard: the state of the file the write will replace.</summary>
        internal Task<string> GateBasisStandardFileAsync(GateCall call)
        {
            try { return Task.FromResult(GateFileState(StandardWritePath(_sessions.Current?.SourcePath, call.Arg<string>("target")))); }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        /// <summary>The door's side of apply_standard: the standard in force and the extra rules file it names, as they are now.</summary>
        internal Task<string> GateBasisStandardAsync()
        {
            try { return Task.FromResult(StandardBasis(StandardStore.Resolve(_sessions.Current?.SourcePath))); }
            catch (Exception ex) { return Task.FromResult("unreadable:" + ex.GetType().Name); }
        }

        private static string StandardBasis(StandardResolved r)
        {
            var extra = r.Effective.Checks?.ExtraRules;
            var extraState = string.IsNullOrWhiteSpace(extra) ? "none"
                : File.Exists(ExtraRulesSource(extra, r.Path)) ? GateFileState(ExtraRulesSource(extra, r.Path)) : GateHash("source", extra);
            return GateHash("standard", r.Hash, extraState);
        }

        private static string SafeUserStandardFile() { try { return StandardStore.UserFile(); } catch (InvalidOperationException) { return null; } }

        private static void ApplyStandardPatch(StandardSpec spec, string patch, List<string> changed, List<string> problems)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(patch); }
            catch (JsonException e) { problems.Add("The patch is not valid JSON: " + e.Message); return; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { problems.Add("The patch is one JSON object."); return; }
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var key = prop.Name.Trim().ToLowerInvariant().Replace("_", "-");
                    var v = prop.Value;
                    bool clear = v.ValueKind == JsonValueKind.Null;
                    string Str() => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                    void Set(string label, string current, Action<string> assign, Action unset)
                    {
                        var next = clear ? null : Str();
                        if (string.Equals(current, next, StringComparison.Ordinal)) return;
                        if (clear) unset(); else assign(next);
                        changed.Add(label);
                    }
                    switch (key)
                    {
                        case "standard": case "name": Set("the name", spec.Standard, x => spec.Standard = x, () => spec.Standard = null); break;
                        case "preset": case "starts-from": Set("what it starts from", spec.StartsFrom, x => spec.StartsFrom = x, () => spec.StartsFrom = null); break;
                        case "use": Set("the shared file", spec.Use, x => spec.Use = x, () => spec.Use = null); break;
                        case "names": Set("names", spec.Names, x => spec.Names = x, () => spec.Names = null); break;
                        case "folders": Set("folders", spec.Folders, x => spec.Folders = x, () => spec.Folders = null); break;
                        case "descriptions": Set("descriptions", spec.Descriptions, x => spec.Descriptions = x, () => spec.Descriptions = null); break;
                        case "synonyms": Set("synonyms", spec.Synonyms, x => spec.Synonyms = x, () => spec.Synonyms = null); break;
                        case "keys-and-totals": Set("keys and totals", spec.KeysAndTotals, x => spec.KeysAndTotals = x, () => spec.KeysAndTotals = null); break;
                        case "ai-instructions": Set("the AI instructions", spec.AiInstructions, x => spec.AiInstructions = x, () => spec.AiInstructions = null); break;
                        case "formats":
                            if (clear) { if (spec.Formats != null) { spec.Formats = null; changed.Add("formats"); } break; }
                            if (v.ValueKind != JsonValueKind.Object) { problems.Add("formats is an object with money, percent and count."); break; }
                            spec.Formats ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var f in v.EnumerateObject())
                            {
                                var kind = f.Name.Trim().ToLowerInvariant();
                                if (f.Value.ValueKind == JsonValueKind.Null) { if (spec.Formats.Remove(kind)) changed.Add("the " + kind + " format"); continue; }
                                var text = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() : f.Value.ToString();
                                if (!spec.Formats.TryGetValue(kind, out var was) || was != text) { spec.Formats[kind] = text; changed.Add("the " + kind + " format"); }
                            }
                            break;
                        case "checks":
                            if (clear) { if (spec.Checks != null) { spec.Checks = null; changed.Add("checks"); } break; }
                            if (v.ValueKind != JsonValueKind.Object) { problems.Add("checks is an object with level, off, must and extra-rules."); break; }
                            spec.Checks ??= new StandardChecks();
                            foreach (var c in v.EnumerateObject())
                            {
                                var ck = c.Name.Trim().ToLowerInvariant().Replace("_", "-");
                                if (ck == "level" && c.Value.ValueKind != JsonValueKind.Null) { var l = c.Value.GetString(); if (l != spec.Checks.Level) { spec.Checks.Level = l; changed.Add("the check level"); } }
                                else if (ck == "extra-rules") { var x = c.Value.ValueKind == JsonValueKind.Null ? null : c.Value.GetString(); if (x != spec.Checks.ExtraRules) { spec.Checks.ExtraRules = x; changed.Add("the extra rules"); } }
                                else if (ck == "off" || ck == "must")
                                {
                                    var ids = c.Value.ValueKind == JsonValueKind.Array ? c.Value.EnumerateArray().Select(e => e.GetString()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToArray()
                                            : c.Value.ValueKind == JsonValueKind.Null ? new string[0]
                                            : (c.Value.GetString() ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToArray();
                                    var was = ck == "off" ? spec.Checks.Off : spec.Checks.Must;
                                    if (was == null || !was.SequenceEqual(ids, StringComparer.Ordinal)) { if (ck == "off") spec.Checks.Off = ids; else spec.Checks.Must = ids; changed.Add(ck == "off" ? "the checks that are off" : "the checks that always count"); }
                                }
                                else if (ck != "level") problems.Add("checks has no part called '" + ck + "'.");
                            }
                            break;
                        default: problems.Add("'" + prop.Name + "' is not a part of a standard. The parts are checks, names, formats, folders, descriptions, synonyms, keys-and-totals and ai-instructions."); break;
                    }
                }
            }
        }

        // ---- preview_standard ---------------------------------------------------------------------

        /// <summary>The counts and examples for every part on THIS model, and the grade now and after the changes that would be made now.
        /// It runs the apply pass inside a rehearsal that is rolled back: nothing is written, no revision moves.</summary>
        public async Task<PreviewStandardResult> PreviewStandardAsync(string patch = null)
        {
            var s = _sessions.Require();
            var resolved = StandardStore.Resolve(s.SourcePath, fetch: true);
            if (!string.IsNullOrWhiteSpace(patch))
            {
                // "What if": the same patch set_standard takes, laid over the lines written now, worked out in memory. No file is touched.
                var written = resolved.Written != null ? resolved.Written.Clone() : new StandardSpec { StartsFrom = resolved.Effective.StartsFrom };
                var changed = new List<string>(); var problems = new List<string>();
                ApplyStandardPatch(written, patch, changed, problems);
                problems.AddRange(StandardYaml.Check(written));
                if (problems.Count > 0) throw new InvalidOperationException("Nothing was previewed. " + string.Join(" ", problems.Distinct()));
                resolved = StandardStore.Resolve(s.SourcePath, null, written);   // the copy fetched just above
            }
            var before = await AiReadinessScanAsync().ConfigureAwait(false);
            var instructions = await GetAiInstructionsAsync(null).ConfigureAwait(false);

            StandardWork work = null; Scorecard after = null;
            var previous = DryRunScope.Current;
            try
            {
                DryRunScope.Current = new DryRunCollector();
                await s.MutateAsync("engine", "preview standard", m =>
                {
                    work = StandardApply.Run(m, resolved.Effective, before.Findings, instructions.Present);
                    after = new ReadinessAnalyzer().Analyze(m);
                }).ConfigureAwait(false);
            }
            finally { DryRunScope.Current = previous; }

            var checkedAgainst = await s.ReadAsync(m => m.GetAnnotation(StandardStore.Annotation)).ConfigureAwait(false);
            var now = Score(before); var then = Score(after ?? before);
            var changes = work.Parts.Sum(p => p.ChangesNow); var tasks = work.Tasks.Count;
            return new PreviewStandardResult
            {
                Standard = StateOf(resolved, checkedAgainst), Parts = work.Parts.ToArray(),
                ChangesNow = changes, Tasks = tasks,
                GradeNow = before.Grade, GradeAfter = (after ?? before).Grade, ScoreNow = now, ScoreAfter = then,
                Sentence = PreviewSentence(resolved.Name, changes, tasks, before.Grade, (after ?? before).Grade),
            };
        }

        private static int Score(Scorecard c) => (int)Math.Round(c.Overall, MidpointRounding.AwayFromZero);

        private static string PreviewSentence(string name, int changes, int tasks, string gradeNow, string gradeAfter)
        {
            if (changes == 0 && tasks == 0) return "This model already meets " + name + ". Nothing to change.";
            return name + ": " + changes + (changes == 1 ? " change" : " changes") + " now, " + tasks + (tasks == 1 ? " task" : " tasks") + " for the Plan."
                 + (gradeNow != gradeAfter ? " Grade " + gradeNow + " to " + gradeAfter + " after the changes." : "");
        }

        // ---- apply_standard -----------------------------------------------------------------------

        /// <summary>Hold the open model to its standard. The sure changes are made now as ONE undo step; the renames and the words go to the Plan
        /// as tasks for the person's own assistant. No confirm: the preview is the safeguard and Undo puts everything back.</summary>
        public async Task<ApplyStandardResult> ApplyStandardAsync(string origin)
        {
            var s = _sessions.Require();
            var resolved = StandardStore.Resolve(s.SourcePath, fetch: true);
            RequireApprovedBasis(nameof(ApplyStandardAsync), StandardBasis(resolved), "standard");
            var before = await AiReadinessScanAsync().ConfigureAwait(false);
            var instructions = await GetAiInstructionsAsync(null).ConfigureAwait(false);

            // The extra rules file is read before the write (it may be a file or an address) and merged inside it, so it is part of the one undo step.
            List<BpaRule> extra = null;
            var extraSource = resolved.Effective.Checks?.ExtraRules;
            if (!string.IsNullOrWhiteSpace(extraSource))
            {
                var source = ExtraRulesSource(extraSource, resolved.Path);
                var json = await ResolveRuleSourceAsync(source).ConfigureAwait(false);
                extra = BpaRuleSet.Parse(json).ToList();
            }

            StandardWork work = null;
            var rev = await s.MutateAsync(origin, "apply standard " + resolved.Name, m =>
            {
                work = StandardApply.Run(m, resolved.Effective, before.Findings, instructions.Present);
                if (extra != null && extra.Count > 0)
                {
                    var byId = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
                    var existing = m.GetAnnotation("BestPracticeAnalyzer");
                    if (!string.IsNullOrWhiteSpace(existing))
                        try { foreach (var r in BpaRuleSet.Parse(existing)) byId[r.ID] = r; } catch (JsonException) { }
                    foreach (var r in extra) byId[r.ID] = r;
                    m.SetAnnotation("BestPracticeAnalyzer", JsonSerializer.Serialize(byId.Values.ToList()));
                    work.Applied.Add("Added " + extra.Count + " extra best-practice rules from " + extraSource);
                }
                m.SetAnnotation(StandardStore.Annotation, JsonSerializer.Serialize(new { name = resolved.Name, hash = resolved.Hash }));
            }).ConfigureAwait(false);

            var rows = new List<string>();
            var planBound = BindPlanMemory();
            var alreadyThere = ReadPlanState(planBound).Tasks.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var t in work.Tasks)
            {
                var added = await AddPlanTaskAsync(new PlanTaskRequest
                {
                    Words = t.Words, Brief = t.Brief, SourcePage = "readiness",
                    DoneWhen = PlanDoneWhen.AssistantSays,
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Page, Ref = "readiness" },
                    Objects = Array.Empty<string>(), SendKey = t.Key,
                }, compose: false).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(added.ItemId)) rows.Add(added.ItemId);
            }
            if (rows.Count > 0)
            {
                // A row this Apply made leaves the Plan when Undo takes the Apply back (ReconcileStandardRows). A row that was already on the Plan
                // belongs to whoever made it and stays.
                var made = rows.Where(id => !alreadyThere.Contains(id)).ToHashSet(StringComparer.Ordinal);
                if (made.Count > 0)
                    WritePlanTasks(planBound, state =>
                    {
                        foreach (var x in state.Tasks.Where(x => made.Contains(x.Id)))
                            x.MadeByStep = new PlanTaskStep { SessionId = s.Id, Revision = rev };
                    });
                TellThePlanMoved(planBound);
            }

            var after = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m)).ConfigureAwait(false);
            var changes = work.Parts.Sum(p => p.ChangesNow);
            var tasks = work.Tasks.Count;
            return new ApplyStandardResult
            {
                Revision = rev, Applied = work.Applied.ToArray(), Parts = work.Parts.ToArray(),
                ChangesNow = changes, Tasks = tasks, PlanRows = rows.ToArray(),
                GradeBefore = before.Grade, GradeAfter = after.Grade,
                Sentence = changes == 0 && tasks == 0 ? "This model already meets " + resolved.Name + "."
                    : "Held to " + resolved.Name + ": " + changes + (changes == 1 ? " change" : " changes") + " made, " + tasks + (tasks == 1 ? " task" : " tasks") + " on the Plan."
                      + (before.Grade != after.Grade ? " Grade " + before.Grade + " to " + after.Grade + "." : ""),
            };
        }

        /// <summary>The extra rules file as the engine reads it: an address or an absolute path as written, a relative path from the standard file's folder.</summary>
        private static string ExtraRulesSource(string source, string standardFile)
        {
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase) || source.TrimStart().StartsWith("[", StringComparison.Ordinal) || Path.IsPathRooted(source)) return source;
            var dir = standardFile == null ? null : Path.GetDirectoryName(standardFile);
            return dir == null ? source : Path.GetFullPath(Path.Combine(dir, source));
        }
    }
}
