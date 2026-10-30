using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private Session performancePreferenceSession;
    private PerformancePreferences performancePreferences;
    private sealed class PerformancePreferences
    {
        public PerformancePreferences() { }
        public MeasurePerformanceSettings Settings { get; set; }
        public Dictionary<string, bool> Measures { get; set; } = new(StringComparer.Ordinal);
    }

    // Preferences belong beside the check cache. Reading or timing a measure never changes TOM or its undo stack.
    private void LoadPerformancePreferences(Session session)
    {
        if (performancePreferenceSession == session) return;
        performancePreferenceSession = session;
        performancePreferences = new();
        try
        {
            var file = MeasureCacheFile(session);
            if (file != null && File.Exists(file + ".performance.json"))
                performancePreferences = JsonSerializer.Deserialize<PerformancePreferences>(File.ReadAllText(file + ".performance.json")) ?? new();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private MeasureMarkSnapshot CaptureCheckSnapshot(Model model, Session session)
    {
        var snapshot = MeasureMarkSnapshot.Capture(model);
        lock (measureRunLock)
        {
            LoadPerformancePreferences(session);
            if (performancePreferences.Settings is { } settings)
            {
                snapshot.PerformanceSettings.SlowThresholdMs = settings.SlowThresholdMs;
                snapshot.PerformanceSettings.RegressionPercent = settings.RegressionPercent;
                snapshot.PerformanceSettings.RegressionMs = settings.RegressionMs;
                snapshot.PerformanceSettings.AllowBackground = settings.AllowBackground;
                snapshot.PerformanceSettings.Shared = settings.Shared;
            }
            foreach (var pair in performancePreferences.Measures)
                if (pair.Value) snapshot.TimedMeasures.Add(pair.Key);
                else snapshot.TimedMeasures.Remove(pair.Key);
        }
        return snapshot;
    }

    public async Task<MeasurePerformanceSettings> SetMeasurePerformanceAsync(MeasurePerformanceSettings settings = null,
        string measureRef = null, bool? includePerformance = null, string origin = "human")
    {
        if (settings != null && (!double.IsFinite(settings.SlowThresholdMs) || settings.SlowThresholdMs < 0
            || !double.IsFinite(settings.RegressionPercent) || settings.RegressionPercent < 0
            || !double.IsFinite(settings.RegressionMs) || settings.RegressionMs < 0))
            throw new ArgumentException("Performance limits must be finite, non-negative numbers.");
        if (includePerformance.HasValue && string.IsNullOrWhiteSpace(measureRef))
            throw new ArgumentException("Choose a measure to change Include performance.");
        var session = _sessions.Require();
        var result = await session.ReadAsync(model =>
        {
            var measure = string.IsNullOrWhiteSpace(measureRef) ? null : ObjectRefs.Resolve(model, measureRef) as Measure
                ?? throw new ArgumentException("Choose a measure to change Include performance.");
            if (DryRunScope.Current == null)
                lock (measureRunLock)
                {
                    LoadPerformancePreferences(session);
                    if (settings != null) performancePreferences.Settings = JsonSerializer.Deserialize<MeasurePerformanceSettings>(JsonSerializer.Serialize(settings));
                    if (includePerformance.HasValue) performancePreferences.Measures[ObjectRefs.For(measure)] = includePerformance.Value;
                    preparedMeasureRuns.Clear();
                    var file = MeasureCacheFile(session);
                    if (file != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllText(file + ".performance.json", JsonSerializer.Serialize(performancePreferences));
                    }
                }
            return CaptureCheckSnapshot(model, session).PerformanceSettings;
        }).ConfigureAwait(false);
        if (DryRunScope.Current == null)
        {
            lock (marksLock)
            {
                if (settings != null) measureMarks.Clear();
                else if (measureRef != null) measureMarks.Remove(measureRef);
            }
            // Timing settings are check inputs outside the model's revision: a check made before them is no longer fresh.
            lock (measureRunLock)
                foreach (var key in measureCheckCache.Keys.Where(k => settings != null || k == measureRef).ToArray())
                    measureCheckCache[key] = measureCheckCache[key] with { CheckedOn = null };
            _sessions.Bus.Publish(new ChangeNotification { SessionId = session.Id, Revision = session.Revision,
                Origin = origin, Label = "Performance check settings updated.", Deltas = Array.Empty<ChangeDelta>() });
        }
        return result;
    }

    public Task<MeasureMarksResult> GetMeasureMarksAsync(string measureRef, string primaryAxis = null, bool refresh = false, string origin = "human", bool includePerformance = false, bool fullPerformanceSweep = false)
        => ReadMeasureMarksAsync(measureRef, primaryAxis, refresh, includePerformance, fullPerformanceSweep, origin);
}
