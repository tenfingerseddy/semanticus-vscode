using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine;

// Cuts per type: warn below FitBelow; Verified only at PassAtLeast. Chosen on the dev part of the l2 known-answer set
// (30 or more broken totals per type) and measured on its held-out third: 0 confident wrong passes (totals-report.md).
public sealed record TotalsSettings
{
    public double TypeUnclearBelow { get; init; } = .5;
    public double SemiAdditiveFitBelow { get; init; } = .5;
    public double AverageFitBelow { get; init; } = .65;
    public double BlankFitBelow { get; init; } = .5;
    public double SumFitBelow { get; init; } = .25;
    public double SumTypeAtLeast { get; init; } = .8;
    public double RecalcFitBelow { get; init; } = .3;
    public double RecalcTypeAtLeast { get; init; } = .8;
    public double SumPassAtLeast { get; init; } = .5;
    public double RecalcPassAtLeast { get; init; } = .7;
    public double SemiAdditivePassAtLeast { get; init; } = .5;
    public double AveragePassAtLeast { get; init; } = .65;
    public double BlankPassAtLeast { get; init; } = .5;

    internal void Validate()
    {
        var cuts = new[] { TypeUnclearBelow, SemiAdditiveFitBelow, AverageFitBelow, BlankFitBelow,
            SumFitBelow, SumTypeAtLeast, RecalcFitBelow, RecalcTypeAtLeast, SumPassAtLeast, RecalcPassAtLeast,
            SemiAdditivePassAtLeast, AveragePassAtLeast, BlankPassAtLeast };
        if (cuts.Any(c => !double.IsFinite(c) || c < 0 || c > 1))
            throw new ArgumentException("Totals limits must be numbers between 0 and 1.");
    }
    // (warn below, pass at least, type confidence at least) for a total rule Jev read.
    internal (double Warn, double Pass, double TypeAtLeast) Cuts(string rule) => rule switch {
        "sum_of_rows" => (SumFitBelow, SumPassAtLeast, SumTypeAtLeast),
        "recomputed_at_total" => (RecalcFitBelow, RecalcPassAtLeast, RecalcTypeAtLeast),
        "last_value_over_time" or "first_value_over_time" => (SemiAdditiveFitBelow, SemiAdditivePassAtLeast, TypeUnclearBelow),
        "average" => (AverageFitBelow, AveragePassAtLeast, TypeUnclearBelow),
        _ => (BlankFitBelow, BlankPassAtLeast, TypeUnclearBelow)
    };
    internal static TotalsSettings Read(string json)
    {
        try {
            var settings = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<TotalsSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            settings.Validate(); return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return new(); }
    }
}

public sealed record TotalProbeRow(string Member, double? Value);
public sealed record TotalProbe(string GroupedBy, TotalProbeRow[] Rows, double? Total)
{
    public string Context { get; init; } = "Grand total";
}
public sealed record TotalsInput(string Name, string Description, string Formula);
public sealed record TotalReading(string Rule, string Type, double Confidence, double? FitScore, bool Expected)
{
    public string Source => "The type came from Jev reading the name, the Description and the formula.";
}

public interface ITotalsScorer
{
    Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings,
        bool expected, CancellationToken cancellationToken);
}
