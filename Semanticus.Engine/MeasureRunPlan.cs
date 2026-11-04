using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed record MeasureRunCandidate
{
    public string MeasureRef { get; init; }
    public string Table { get; init; }
    public string Folder { get; init; }
    public string Formula { get; init; }
    public string Description { get; init; }
    public string[] FactTables { get; init; } = Array.Empty<string>();
    public string Inputs { get; init; }
    public JsonElement? InputSnapshot { get; init; }
    public bool Changed { get; init; }
    public bool Helper { get; init; }
    public bool BaseMeasure { get; init; }
    public int Dependents { get; init; }
    public int ReportUses { get; init; }
    public string Risk { get; init; }
    public double? MatchScore { get; init; }
    public bool Suspect { get; init; }
    public DateTimeOffset? LastChecked { get; init; }
    public double? LastSeconds { get; init; }
    public int ContextChecks { get; init; } = 24;
    public bool IncludePerformance { get; init; }
}

public sealed record MeasureRunSelection
{
    public string Kind { get; init; } = "measures";
    public string[] Measures { get; init; } = Array.Empty<string>();
    public string Table { get; init; }
    public string Folder { get; init; }
}

public sealed record MeasureRunRequest
{
    public MeasureRunSelection Selection { get; init; }
    public int Cap { get; init; } = 30;
    public bool IncludePerformance { get; init; }
    public string[] PerformanceMeasures { get; init; } = Array.Empty<string>();
}

public sealed record MeasureRunPick(string MeasureRef, string Inputs, string Group, string Reason);

public sealed record MeasureRunPlan
{
    public string Id { get; init; }
    public int TotalMeasures { get; init; }
    public int BatchSize { get; init; }
    public double EstimatedSeconds { get; init; }
    public bool FirstRun { get; init; }
    public string Sentence { get; init; }
    public MeasureRunPick[] Picks { get; init; } = Array.Empty<MeasureRunPick>();
    public bool IncludePerformance { get; init; }
    public string[] PerformanceMeasures { get; init; } = Array.Empty<string>();
}

// The join can replace the rule order with a measured Jev order through this interface.
public interface IMeasureRunOrder
{
    IReadOnlyList<MeasureRunPick> Order(IReadOnlyList<MeasureRunCandidate> candidates, int cap);
}

// E3 and E4 implement this interface. Cancellation belongs to this run, never to another query.
public interface IMeasureRunExtras
{
    string Inputs { get; }
    Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken);
    Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken);
    Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken);
}

public sealed record MeasureCheckCache
{
    public string MeasureRef { get; init; }
    public string Inputs { get; init; }
    public JsonElement? InputSnapshot { get; init; }
    public JsonElement? CheapInputSnapshot { get; init; }
    public DateTimeOffset? LastChecked { get; init; }
    public double? Seconds { get; init; }
    public bool Stale { get; init; }
    public bool IncludedPerformance { get; init; }
    public string CheapInputs { get; init; }
    public double? MatchScore { get; init; }
    public MeasureMark[] Cheap { get; init; } = Array.Empty<MeasureMark>();
    public MeasureMark[] Marks { get; init; } = Array.Empty<MeasureMark>();
    // The engine's one check state per measure (L1 design): every write bumps Version, and readers drop an older one.
    public long Version { get; init; }
    public bool Running { get; init; }
    // The person's Keep of an edit: Equivalence does not apply while its inputs equal these. Saved with the cache.
    public MeasureKeep Keep { get; init; }
    public string Verdict { get; init; }
    // The checker inputs the marks were made with. A run on another checker does not keep them as last results.
    public string Checker { get; init; }
    // What a finished check of the whole measure ran on (session, revision, checker, saved checks, connection). The record is
    // fresh while these are what the model has now, also when no plan run digested its inputs.
    public string CheckedOn { get; init; }
}

public sealed record MeasureSweepOptions
{
    public bool Enabled { get; init; } = true;
    public double IdleMinutes { get; init; } = 5;
    public int BatchSize { get; init; } = 3;
    public bool AllowPerformance { get; init; }
}

public sealed record MeasureRunResult
{
    public string PlanId { get; init; }
    public bool StoppedOnEdit { get; init; }
    // The person pressed Stop (stopMeasureRun). Not an edit, so the run line says nothing.
    public bool Stopped { get; init; }
    public MeasureCheckCache[] Results { get; init; } = Array.Empty<MeasureCheckCache>();
}

public sealed class RuleMeasureRunOrder : IMeasureRunOrder
{
    public IReadOnlyList<MeasureRunPick> Order(IReadOnlyList<MeasureRunCandidate> candidates, int cap)
    {
        var eligible = candidates.Where(c => c.Changed || !c.Helper).ToArray();
        // Every changed measure is scheduled. Overflow runs in batches bounded by the requested cap.
        return eligible.OrderBy(Tier)
            .ThenByDescending(c => Tier(c) == 1 ? c.Dependents : Tier(c) == 2 ? c.ReportUses : 0)
            .ThenBy(c => Tier(c) == 4 ? c.MatchScore ?? 1 : 0)
            .ThenBy(c => Tier(c) == 5 ? c.LastChecked : null)
            .ThenBy(c => c.MeasureRef, StringComparer.Ordinal)
            .Take(Math.Max(Math.Clamp(cap, 25, 40), eligible.Count(c => c.Changed)))
            .Select(Pick).ToArray();
    }

    private static int Tier(MeasureRunCandidate c) => c.Changed ? 0 : c.BaseMeasure && c.Dependents > 0 ? 1
        : c.ReportUses > 0 ? 2 : !string.IsNullOrEmpty(c.Risk) ? 3 : c.MatchScore < .6 || c.Suspect ? 4 : 5;

    private static MeasureRunPick Pick(MeasureRunCandidate c) => Tier(c) switch
    {
        0 => new(c.MeasureRef, c.Inputs, "changed", "Changed since the last run, or uses a measure that changed."),
        1 => new(c.MeasureRef, c.Inputs, "core", $"Used by {c.Dependents} other measures."),
        2 => new(c.MeasureRef, c.Inputs, "reports", $"Used in {c.ReportUses} report visuals."),
        3 => new(c.MeasureRef, c.Inputs, "risky", "Formula uses " + c.Risk + "."),
        4 when c.MatchScore < .6 => new(c.MeasureRef, c.Inputs, "match", "Description match score is " + c.MatchScore.Value.ToString("0.00", CultureInfo.InvariantCulture) + "."),
        4 => new(c.MeasureRef, c.Inputs, "suspect", "A past mark is suspect."),
        _ => new(c.MeasureRef, c.Inputs, "oldest", c.LastChecked == null ? "Never checked." : "Oldest check: " + c.LastChecked.Value.ToString("u", CultureInfo.InvariantCulture) + ".")
    };
}

public static class MeasureRunPlanner
{
    public static MeasureRunPlan Build(IReadOnlyList<MeasureRunCandidate> candidates, MeasureRunRequest request = null,
        IMeasureRunOrder order = null)
    {
        request ??= new();
        var cap = Math.Clamp(request.Cap, 25, 40);
        var byRef = candidates.ToDictionary(c => c.MeasureRef, StringComparer.Ordinal);
        MeasureRunPick[] picks;
        if (request.Selection == null)
        {
            picks = (order ?? new RuleMeasureRunOrder()).Order(candidates, cap).ToArray();
            if (picks.Any(p => !byRef.ContainsKey(p.MeasureRef) || string.IsNullOrWhiteSpace(p.Reason))
                || picks.Select(p => p.MeasureRef).Distinct().Count() != picks.Length
                || picks.Length > Math.Max(cap, candidates.Count(c => c.Changed))
                || candidates.Any(c => c.Changed && !picks.Any(p => p.MeasureRef == c.MeasureRef)))
                throw new InvalidOperationException("The measure order must keep changed measures, the cap and a reason for each pick.");
            picks = picks.Select(p => p with { Inputs = byRef[p.MeasureRef].Inputs }).ToArray();
        }
        else
        {
            var selection = request.Selection;
            var kind = selection.Kind?.ToLowerInvariant();
            var refs = (selection.Measures ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            if (kind == "measures" && refs.Any(r => !byRef.ContainsKey(r)))
                throw new ArgumentException("A selected measure is no longer in this model.");
            var selected = kind switch
            {
                "measures" => (selection.Measures ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Select(r => byRef[r]),
                "table" when !string.IsNullOrWhiteSpace(selection.Table) => candidates.Where(c => c.Table == selection.Table),
                "folder" when !string.IsNullOrWhiteSpace(selection.Folder) => candidates.Where(c =>
                    (selection.Table == null || c.Table == selection.Table) && InFolder(c.Folder, selection.Folder)),
                "changed" => candidates.Where(c => c.Changed),
                _ => throw new ArgumentException("Choose measures, a folder, a table, or changed measures.")
            };
            var ordered = kind == "measures" ? selected : selected.OrderBy(c => c.MeasureRef, StringComparer.Ordinal);
            picks = ordered.Select(c =>
                new MeasureRunPick(c.MeasureRef, c.Inputs, kind == "changed" ? "changed" : "selected",
                    kind == "changed" ? "Changed since the last run." : "Selected by the person or their agent.")).ToArray();
        }
        var measured = candidates.Where(c => c.LastSeconds > 0 && double.IsFinite(c.LastSeconds.Value)).Select(c => c.LastSeconds.Value).OrderBy(x => x).ToArray();
        var firstRun = measured.Length == 0 && picks.Length > 0;
        // Allow two seconds of setup and half a second per context checked by each mark.
        // Eight to fifteen contexts across three marks puts twelve measures near the pilot's four minutes.
        var seconds = picks.Sum(p => byRef[p.MeasureRef].LastSeconds is > 0 and var value && double.IsFinite(value) ? value
            : measured.Length > 0 ? measured[measured.Length / 2] : 2 + .5 * Math.Max(1, byRef[p.MeasureRef].ContextChecks));
        var counts = string.Join(", ", picks.GroupBy(p => p.Group).Select(g => g.Count() + " " + g.Key));
        var time = seconds < 60 ? Math.Max(1, (int)Math.Ceiling(seconds)) + (seconds <= 1 ? " second" : " seconds")
            : Math.Max(1, (int)Math.Ceiling(seconds / 60)) + (seconds <= 60 ? " minute" : " minutes");
        var sentence = $"Checking {picks.Length} of {candidates.Count}" + (counts.Length == 0 ? "." : ": " + counts + ".")
            + " About " + time + ".";   // ", first run. The estimate improves after this run" is cut (spec section 5, cut 42)
        if (picks.Length > cap) sentence += $" In {(int)Math.Ceiling((double)picks.Length / cap)} batches of at most {cap}.";
        var performance = (request.PerformanceMeasures ?? Array.Empty<string>())
            .Concat(picks.Where(p => byRef[p.MeasureRef].IncludePerformance).Select(p => p.MeasureRef))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToArray();
        var basis = JsonSerializer.Serialize(new { Candidates = candidates.OrderBy(c => c.MeasureRef, StringComparer.Ordinal), picks, cap, request.IncludePerformance, performance });
        return new()
        {
            Id = Digest(basis), TotalMeasures = candidates.Count, BatchSize = cap, Picks = picks,
            EstimatedSeconds = seconds, FirstRun = firstRun, Sentence = sentence, IncludePerformance = request.IncludePerformance, PerformanceMeasures = performance
        };
    }

    private static bool InFolder(string actual, string requested) => (actual ?? "").Split(';').Any(folder =>
        folder.Equals(requested.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || folder.StartsWith(requested.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>The model's structure text, written into JSON and hashed once for every measure's fingerprint (cx4). A measure's fingerprint
    /// is the digest of its inputs as JSON with the whole structure text in them. Writing that JSON again for each measure held the model
    /// thread 2 to 4 s on a model with an 8 MB structure. Here the per-measure JSON carries a short slot, and the hash reads the structure's
    /// JSON bytes, made once, in its place: the same bytes, so the same fingerprint, and saved check results stay fresh.</summary>
    internal sealed class StructureInputs
    {
        internal const string Slot = "semanticus-structure-slot-5c1e";
        private static readonly string Marker = "\"structure\":" + JsonSerializer.Serialize(Slot);
        private readonly byte[] literal;
        internal string Digest { get; }

        internal StructureInputs(string structure)
        {
            literal = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(structure));
            Digest = MeasureRunPlanner.Digest(structure);
        }

        /// <summary>Digest(JsonSerializer.Serialize(payload)) with the structure text in place of the slot. The payload's `structure` property
        /// holds <see cref="Slot"/>. A JSON string cannot hold an unescaped quote, so the marker is found only at that property.</summary>
        internal string InputsOf(object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var at = json.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0 || json.IndexOf(Marker, at + Marker.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("The check inputs have no single structure slot.");
            var value = at + Marker.Length - JsonSerializer.Serialize(Slot).Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(json, 0, value));
            hash.AppendData(literal);
            hash.AppendData(Encoding.UTF8.GetBytes(json, at + Marker.Length, json.Length - at - Marker.Length));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
