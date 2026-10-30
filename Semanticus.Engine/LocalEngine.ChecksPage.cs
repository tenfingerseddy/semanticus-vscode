#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed record ChecksMeasure(string Ref, string Name, string Description)
{
    public string OneLine { get; init; }
    public string DescriptionProvenance { get; init; }
    public DescriptionMatch DescriptionMatch { get; init; }
    // The run plan ticks measures by table or display folder.
    public string Table { get; init; }
    public string Folder { get; init; }
}
public sealed record ChecksPageSummary(int Total, int NumbersDiffer, int KeyProblems, int TableProblems)
{
    public bool Attention => NumbersDiffer + KeyProblems + TableProblems > 0;
    public string Status => string.Join(" · ", new[] {
        NumbersDiffer > 0 ? $"{NumbersDiffer} number{(NumbersDiffer == 1 ? " differs" : "s differ")}" : null,
        KeyProblems > 0 ? $"{KeyProblems} key problem{(KeyProblems == 1 ? "" : "s")}" : null,
        TableProblems > 0 ? $"{TableProblems} table problem{(TableProblems == 1 ? "" : "s")}" : null,
        $"{Total} measure{(Total == 1 ? "" : "s")}"
    }.Where(x => x != null));
}
public sealed record ChecksPageView(string SessionId, long Revision, ChecksMeasure[] Measures, ChecksPageSummary Summary,
    TestSuiteInfo Numbers, Dictionary<string, ReconcileOutcome> Results, RelationshipIntegrityReport? Relationships,
    TableSourceMappingList Tables, string? When, Dictionary<string, string> ResultTimes, string? RelationshipsWhen)
{
    // What stops scoring now ("Pro advice is off for this model."), from the scoring state, not from a mark: the Checks page and
    // every card show it at open, before any run (wr1 F1). Null when nothing stops it.
    public string? Unscored { get; init; }
}

public sealed partial class LocalEngine
{
    private string? checksPageSession, checksPageWhen;
    private readonly Dictionary<string, ReconcileOutcome> checksPageResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> checksPageDefinitionVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> checksPageResultTimes = new(StringComparer.Ordinal);
    private RelationshipIntegrityReport? checksPageRelationships;
    private string? checksPageRelationshipsWhen;

    // Both doors read the same saved numbers, relationships, and measure list.
    public async Task<ChecksPageView> GetChecksPageAsync()
    {
        var session = _sessions.Require();
        var (revision, measures) = await session.ReadAsync(model => (session.Revision, model.AllMeasures
            .Select(m => new ChecksMeasure("measure:" + m.Table.Name + "/" + m.Name, m.Name, m.Description) {
                OneLine = DescriptionLine(m.Description), DescriptionProvenance = DescriptionSource(m),
                Table = m.Table.Name, Folder = m.DisplayFolder ?? "",
                DescriptionMatch = CachedDescriptionMatch(session, ObjectRefs.For(m), DescriptionInput(m))
            }).ToArray())).ConfigureAwait(false);
        var unscored = await UnscoredReasonsAsync().ConfigureAwait(false);
        // A Description score not asked yet says why, from the state now (fx29c).
        measures = measures.Select(m => m.DescriptionMatch is { Score: null, Reason: null }
            ? m with { DescriptionMatch = m.DescriptionMatch with { Reason = unscored.Description(m.Description) } } : m).ToArray();
        var numbers = await ListTestDefinitionsAsync().ConfigureAwait(false);
        var tables = await ListTableSourceMappingsAsync().ConfigureAwait(false);
        Dictionary<string, ReconcileOutcome> results;
        Dictionary<string, string> resultTimes;
        RelationshipIntegrityReport? relationships;
        string? when;
        string? relationshipsWhen;
        lock (_recentTestRuns)
        {
            results = checksPageSession == session.Id ? numbers.Definitions.Where(d => checksPageResults.ContainsKey(d.Id)
                    && checksPageDefinitionVersions.GetValueOrDefault(d.Id) == System.Text.Json.JsonSerializer.Serialize(d))
                .ToDictionary(d => d.Id, d => checksPageResults[d.Id], StringComparer.Ordinal) : new(StringComparer.Ordinal);
            relationships = checksPageSession == session.Id ? checksPageRelationships : null;
            when = checksPageSession == session.Id ? checksPageWhen : null;
            resultTimes = results.Keys.Where(checksPageResultTimes.ContainsKey).ToDictionary(id => id, id => checksPageResultTimes[id], StringComparer.Ordinal);
            relationshipsWhen = checksPageSession == session.Id ? checksPageRelationshipsWhen : null;
        }
        var ordered = measures.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var summary = SummarizeChecks(ordered, numbers.Definitions, results, relationships, tables);
        if (_sessions.Current != session || session.Revision != revision) throw new InvalidOperationException("The model changed. Open Checks again.");
        return new(session.Id, revision, ordered, summary, numbers, results, relationships, tables, when, resultTimes, relationshipsWhen)
            { Unscored = Stopping(unscored) };
    }

    /// <summary>The line a person can act on, of what stops scoring now. A service fault (the calm line) is not one.</summary>
    internal static string? Stopping(UnscoredReasons reasons) =>
        new[] { reasons.Totals, reasons.Described }.FirstOrDefault(ScoringCopy.StopsScoring);

    internal static ChecksPageSummary SummarizeChecks(ChecksMeasure[] measures, TestDefinition[] definitions,
        Dictionary<string, ReconcileOutcome> results, RelationshipIntegrityReport? relationships, TableSourceMappingList tables)
    {
        return new(measures.Length,
            definitions.Count(d => d.Enabled && results.TryGetValue(d.Id, out var r) && r.Verdict is Verdict.Fail or Verdict.Suspect),
            relationships?.Relationships.Count(r => new[] { r.DataTypeMatch, r.KeyUniqueness, r.ReferentialIntegrity }
                .Any(c => c.Verdict is Verdict.Fail or Verdict.Suspect)) ?? 0,
            tables.Rows.Count(r => r.LastVerdict is "Fail" or "Suspect" || r.LastModelCount.HasValue && r.LastSourceCount.HasValue
                && r.LastModelCount != r.LastSourceCount));
    }
}
