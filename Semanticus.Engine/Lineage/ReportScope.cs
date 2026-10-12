using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// One report a person chose to check this model against. A CHOICE is a setting, so it is saved with the model's
    /// other sidecar data and comes back on reopen. It deliberately carries no result: what came of reading the
    /// report is evidence, it lives in the session, and it is never restored from disk as if it were still true.
    /// (Astra's critique, section 3: report choices are settings, check results are evidence.)
    /// </summary>
    public sealed class ReportScopeChoice
    {
        /// <summary>Stable key within one model: "published:&lt;workspaceId&gt;/&lt;reportId&gt;" or "local:&lt;path&gt;".
        /// Filled in by the engine when a caller leaves it empty.</summary>
        public string Id { get; set; }
        /// <summary>published | local</summary>
        public string Kind { get; set; } = "local";
        /// <summary>What a person calls it. For a published report, the report name; for a folder, the folder name.</summary>
        public string Name { get; set; }
        public string WorkspaceId { get; set; }
        public string WorkspaceName { get; set; }
        public string ReportId { get; set; }
        /// <summary>Local only: a PBIR "&lt;Report&gt;.Report" folder, its definition folder, a .pbip file, or a project root.</summary>
        public string Path { get; set; }
        /// <summary>Which model chose it. A sidecar can be shared, so a choice is claimed only by its own model.</summary>
        public string ModelIdentity { get; set; }
        public string ChosenWhenUtc { get; set; }
        public string ChosenBy { get; set; }
    }

    /// <summary>One chosen report as both doors see it: the choice, plus what came of reading it and when.</summary>
    public sealed class ReportScopeReport
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        /// <summary>Where it came from, in words: a workspace name, or the folder it was read from.</summary>
        public string Source { get; set; }
        /// <summary>A published report's OWN coordinates, carried on the wire so a caller can hand this list
        /// straight back to set_report_scope without losing them. They used to be left off this shape while the
        /// page rebuilt every save from it, so taking one of several chosen reports off the list saved the
        /// survivors with no workspace and no report id, and the next check then looked for each of them under
        /// an empty workspace by display name. Null for a local folder, which is found by its Source path.</summary>
        public string WorkspaceId { get; set; }
        public string ReportId { get; set; }
        /// <summary>checked | couldNotBeFullyChecked | notChecked | needsChecking</summary>
        public string State { get; set; } = ReportScopeStates.NotChecked;
        public string CheckedWhenUtc { get; set; }
        /// <summary>Plain words for a state that is not a clean "checked", or null.</summary>
        public string Note { get; set; }
        /// <summary>How many of the model's fields this report was found to use. Zero until it is checked.</summary>
        public int FieldsUsed { get; set; }
        public int Visuals { get; set; }

        /// <summary>A copy that can carry other counts, so a reading taken for one answer never edits the shared row.</summary>
        internal ReportScopeReport MemberwiseCloneForCounts() => (ReportScopeReport)MemberwiseClone();
    }

    public static class ReportScopeStates
    {
        public const string Checked = "checked";
        public const string CouldNotBeFullyChecked = "couldNotBeFullyChecked";
        public const string NotChecked = "notChecked";
        public const string NeedsChecking = "needsChecking";
    }

    /// <summary>
    /// Why a reading is not a clean "checked", in one plain sentence. Kane, 16 September, on a real Fabric
    /// workspace: the card said two reports "could not be fully checked" and there was no way to find out why.
    /// The page can only show a cause the engine recorded, so this is the ONE place that cause is written, and
    /// it has three jobs:
    ///   - it never comes back empty. A scrubbed transport message can be blank, and "?? fallback" does not
    ///     catch an empty string, so the state used to arrive with the empty string behind it;
    ///   - it names the cause that actually happened. A definition that parsed perfectly and then could not be
    ///     matched to this model is NOT a definition that could not be read, and saying so was false;
    ///   - it uses the same words the assessment's own unknowns use, so one fact reads the same way wherever
    ///     it turns up.
    /// </summary>
    public static class ReportCheckNotes
    {
        public const string PartsUnreadable = "Part of this report could not be read, so its use of this model is not fully known.";
        public const string Unreadable = "This report could not be read.";
        public const string NoCauseRecorded = "This report could not be fully read, and the reason was not recorded.";

        public static string Unmatched(int count) =>
            (count == 1 ? "1 thing this report shows" : count + " things this report shows")
            + " could not be matched to this model, so its use of this model is not fully known.";

        /// <summary>The cause behind one report's state. <paramref name="read"/> is whether the definition was
        /// read at all; the counts are that reading's own coverage gaps; <paramref name="error"/> is the
        /// transport's or the reader's own message, which may be null, empty, or whitespace.</summary>
        public static string For(bool read, int skippedParts, int unmatched, string error)
        {
            var given = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
            if (!read) return given ?? Unreadable;
            if (skippedParts > 0) return PartsUnreadable;
            if (unmatched > 0) return Unmatched(unmatched);
            return given ?? NoCauseRecorded;
        }
    }

    /// <summary>What this model's answers were checked against: the model itself, the named reports with their state
    /// and time, and every gap said as a plain sentence.</summary>
    public sealed class ReportScopeResult
    {
        /// <summary>The model the reports were checked against, by name.</summary>
        public string ModelName { get; set; }
        public ReportScopeReport[] Reports { get; set; } = Array.Empty<ReportScopeReport>();
        public int Listed { get; set; }
        public int Checked { get; set; }
        public int CouldNotBeFullyChecked { get; set; }
        public int NotChecked { get; set; }
        public int NeedsChecking { get; set; }
        /// <summary>The newest successful check across the listed reports, or null when nothing has been checked.</summary>
        public string CheckedWhenUtc { get; set; }
        /// <summary>Everything this scope does NOT establish, one plain sentence each.</summary>
        public string[] Gaps { get; set; } = Array.Empty<string>();
        /// <summary>One sentence for a surface that has room for only one.</summary>
        public string Summary { get; set; }
        /// <summary>Set when the choices could not be saved or read, in plain words.</summary>
        public string Note { get; set; }
        /// <summary>What the reading found, when this result came from a check. Null on a plain list: a listing
        /// reads nothing, and handing back the last reading there would let a stale answer look fresh. The page
        /// uses it to draw the report layer on the graph, so the picture and the list share one reading.</summary>
        public ReportAnalysisResult Usage { get; set; }
        /// <summary>Set by a check: how many report measures (formulas the report itself holds) were read, and how many
        /// model names in them were checked. Null on a plain list, which reads nothing.</summary>
        public string ReportMeasures { get; set; }

        /// <summary>The one sentence both doors say about report measures after a check.</summary>
        public static string DescribeReportMeasures(int measures, int references) =>
            measures == 0
                ? "These reports hold no report measures."
                : "Read " + Count(measures, "report measure") + " and checked " + Count(references, "reference") + " in their formulas.";

        public static ReportScopeResult From(string modelName, IEnumerable<ReportScopeReport> reports, string note = null)
        {
            var list = (reports ?? Array.Empty<ReportScopeReport>()).ToArray();
            var result = new ReportScopeResult
            {
                ModelName = modelName,
                Reports = list,
                Listed = list.Length,
                Checked = list.Count(r => r.State == ReportScopeStates.Checked),
                CouldNotBeFullyChecked = list.Count(r => r.State == ReportScopeStates.CouldNotBeFullyChecked),
                NotChecked = list.Count(r => r.State == ReportScopeStates.NotChecked),
                NeedsChecking = list.Count(r => r.State == ReportScopeStates.NeedsChecking),
                CheckedWhenUtc = list.Where(r => !string.IsNullOrEmpty(r.CheckedWhenUtc))
                    .Select(r => r.CheckedWhenUtc).OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault(),
                Note = note,
            };
            result.Gaps = BuildGaps(result);
            result.Summary = BuildSummary(result);
            return result;
        }

        // The honesty sentences. Each one states only what the reading established, never a total nobody counted.
        private static string[] BuildGaps(ReportScopeResult r)
        {
            var gaps = new List<string>();
            if (r.Listed == 0)
            {
                gaps.Add("Reports have not been checked. Choose reports to check this against.");
                return gaps.ToArray();
            }
            if (r.NeedsChecking > 0)
                gaps.Add("Needs checking again. The model changed after " + Count(r.NeedsChecking, "report") + " was last read.");
            if (r.NotChecked > 0)
                gaps.Add(Count(r.NotChecked, "listed report") + " " + (r.NotChecked == 1 ? "was" : "were") + " not checked: " + Names(r, ReportScopeStates.NotChecked) + ".");
            // One sentence, said the same way wherever incomplete coverage stops something: the scope's own gaps,
            // the assessment's unknowns, and the refusal a deletion gives. Astra's R1/ruling 3.
            if (r.NotChecked > 0 || r.NeedsChecking > 0)
                gaps.Add("The chosen reports still need checking.");
            if (r.CouldNotBeFullyChecked > 0)
                gaps.Add(Count(r.CouldNotBeFullyChecked, "listed report") + " could not be fully checked: " + Names(r, ReportScopeStates.CouldNotBeFullyChecked) + ".");
            if (r.Checked > 0)
                gaps.Add("Only the reports listed here were checked. A report that is not on this list can still use this field.");
            if (r.Checked == 0 && r.CouldNotBeFullyChecked == 0)
                gaps.Add("Report use is unknown until at least one listed report is read.");
            return gaps.ToArray();
        }

        private static string BuildSummary(ReportScopeResult r)
        {
            if (r.Listed == 0) return "Reports have not been checked.";
            if (r.NeedsChecking > 0 && r.Checked == 0) return "Needs checking again.";
            if (r.Checked == 0 && r.CouldNotBeFullyChecked == 0) return Count(r.Listed, "listed report") + " chosen, none checked yet.";
            if (r.Checked == r.Listed) return "All " + Count(r.Listed, "listed report") + " checked.";
            var parts = new List<string> { Count(r.Checked, "report") + " checked" };
            if (r.CouldNotBeFullyChecked > 0) parts.Add(Count(r.CouldNotBeFullyChecked, "report") + " could not be fully checked");
            if (r.NotChecked > 0) parts.Add(Count(r.NotChecked, "listed report") + " not checked");
            if (r.NeedsChecking > 0) parts.Add(Count(r.NeedsChecking, "report") + " needing checking again");
            return string.Join("; ", parts) + ".";
        }

        private static string Names(ReportScopeResult r, string state)
        {
            var names = r.Reports.Where(x => x.State == state).Select(x => x.Name ?? x.Id).ToArray();
            if (names.Length == 0) return "none";
            if (names.Length == 1) return names[0];
            if (names.Length <= 4) return string.Join(", ", names.Take(names.Length - 1)) + " and " + names[names.Length - 1];
            return string.Join(", ", names.Take(3)) + " and " + (names.Length - 3) + " more";
        }

        private static string Count(int n, string noun) => n == 1 ? "1 " + noun : n + " " + noun + "s";
    }

    /// <summary>
    /// Persistence for the report CHOICES. Clones the TestSuiteStore discipline exactly: plain JSONL beside the
    /// model's other sidecar data, best-effort, unreadable lines counted rather than thrown, atomic temp-then-move
    /// so a crash mid-write can never tear the file. Keyed by choice id within one model identity.
    /// </summary>
    public static class ReportScopeStore
    {
        public const string SubDir = "reports";
        public const string FileName = "report-scope.jsonl";

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>The sidecar file, for a witness that hashes it.</summary>
        public static string PathFor(string dir) => Path.Combine(dir, SubDir, FileName);

        /// <summary>Every choice in the sidecar. The caller filters to the open model's identity.</summary>
        public static (List<ReportScopeChoice> Choices, int Unreadable) Load(string dir)
        {
            lock (Gate) return LoadUnlocked(dir);
        }

        /// <summary>Replace this model's whole choice set. Returns what was stored, or null when it could not be
        /// written — never throws, like the suite store it sits beside.</summary>
        public static List<ReportScopeChoice> Replace(string dir, string modelIdentity, IEnumerable<ReportScopeChoice> choices, Action<string> beforeWrite = null)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(modelIdentity)) return null;
            var next = (choices ?? Array.Empty<ReportScopeChoice>()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id)).ToList();
            try
            {
                lock (Gate)
                {
                    beforeWrite?.Invoke(PathFor(dir));
                    var (all, _) = LoadUnlocked(dir);
                    all.RemoveAll(x => string.Equals(x?.ModelIdentity ?? "", modelIdentity, StringComparison.Ordinal));
                    all.AddRange(next);
                    WriteUnlocked(dir, all);
                }
                return next;
            }
            catch (Exception ex) when (ex is not StaleApprovalException) { return null; }
        }

        private static (List<ReportScopeChoice> Choices, int Unreadable) LoadUnlocked(string dir)
        {
            var all = new List<ReportScopeChoice>();
            var bad = 0;
            try
            {
                var file = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SubDir, FileName);
                if (file == null || !File.Exists(file)) return (all, 0);
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ReportScopeChoice c = null;
                    try { c = JsonSerializer.Deserialize<ReportScopeChoice>(line, JsonOpts); } catch { }
                    if (c != null && !string.IsNullOrWhiteSpace(c.Id)) all.Add(c); else bad++;
                }
            }
            catch { /* an unreadable store means no choices, never a throw */ }
            return (all, bad);
        }

        private static void WriteUnlocked(string dir, List<ReportScopeChoice> all)
        {
            var file = Path.Combine(dir, SubDir, FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var body = string.Join("\n", all.Select(c => JsonSerializer.Serialize(c, JsonOpts)));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, all.Count == 0 ? "" : body + "\n");
            File.Move(tmp, file, overwrite: true);
        }
    }
}
