using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// docs/harness-engineering.md §5 — the read-only `harness_report` op. Resolves the current session's
    /// L0 experience-log file via the existing private <c>CurrentExperienceFile()</c> helper (defined in
    /// LocalEngine.Orientation.cs — same partial class, so it is reused directly, no replication) and runs
    /// the pure <see cref="HarnessReport"/> analyzer over it. No mutation, no undo entry, fail-soft: a
    /// missing/empty/corrupt log yields an empty report with an honest note, never a throw.
    /// </summary>
    public sealed partial class LocalEngine
    {
        public Task<HarnessReportResult> HarnessReportAsync(int topN)
            => Task.FromResult(HarnessReport.FromFile(CurrentExperienceFile(), topN));
    }
}
