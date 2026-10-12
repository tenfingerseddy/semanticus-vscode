using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed class FindingWaiverItem
    {
        public string System { get; set; }
        public string RuleId { get; set; }
        public string ObjRef { get; set; }
        public string Reason { get; set; }
    }

    public sealed class WordsWriteItem
    {
        public string ObjRef { get; set; }
        public string Description { get; set; }
        public string[] Synonyms { get; set; }
        public string DisplayFolder { get; set; }
    }

    public sealed class ObjectEditItem
    {
        public string ObjRef { get; set; }
        public Dictionary<string, JsonElement> Set { get; set; }
    }

    public sealed class MeasureCreateItem
    {
        public string TableRef { get; set; }
        public string Name { get; set; }
        public string Expression { get; set; }
        public string DisplayFolder { get; set; }
    }

    public sealed class BatchRejection
    {
        public int Index { get; set; }
        public string ObjRef { get; set; }
        public string Reason { get; set; }
    }

    public class BatchEditResult
    {
        public long Revision { get; set; }
        public int Applied { get; set; }
        public BatchRejection[] Rejected { get; set; }
        public string Warning { get; set; }
        public string[] Created { get; set; }
        public ChangeWords Words { get; set; }
    }

    public sealed partial class LocalEngine
    {
        public async Task<BatchEditResult> CreateMeasuresAsync(MeasureCreateItem[] items, string origin)
        {
            RequireProFeature();
            await EnforceBindingAsync("create_measure", origin);
            var s = _sessions.Require();
            var verified = _verifiedMode;
            var made = new (string Ref, string Expression)?[items?.Length ?? 0];
            var result = await WriteBatchAsync(s, items, origin, "create measures", x => x?.TableRef + "/" + x?.Name, (m, item, i) =>
            {
                made[i] = null;
                if (item == null) throw new ArgumentException("A measure is required.");
                if (ObjectRefs.Resolve(m, item.TableRef) is not Table table) throw new InvalidOperationException($"{item.TableRef} is not a table.");
                var measure = CreateMeasureCore(table, item.Name, item.Expression, NormalizeFolderPath(item.DisplayFolder));
                made[i] = (ObjectRefs.For(measure), item.Expression);
                return (true, null);
            }, words: (accepted, refs) => ChangeWords.For("created", "measures", made.Where(x => x != null).Select(x => x.Value.Ref).ToArray()));
            var created = made.Where(x => x != null).Select(x => x.Value).ToList();
            result.Created = created.Select(x => x.Ref).ToArray();
            if (DryRunScope.Current == null) foreach (var item in created)
            {
                await RecordVerifiedModeEditAsync(verified && !string.IsNullOrWhiteSpace(item.Expression), s, "create_measure", item.Ref, item.Expression, result.Revision, origin);
                await RecordWorkflowAuthoredMeasureAsync("create_measure", item.Ref, item.Expression);
            }
            NoteBatch(s.Id, origin, result.Applied, "created", "measures", "edit/properties", result.Words);
            return result;
        }
        public async Task<BatchEditResult> WaiveFindingsAsync(FindingWaiverItem[] items, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            var result = await WriteBatchAsync(s, items, origin, "waive findings", x => x?.ObjRef, (m, item, _) =>
            {
                if (item == null) throw new ArgumentException("A finding is required.");
                ApplyFindingWaiver(m, item.System, item.RuleId, item.ObjRef, item.Reason, origin, out var warning);
                return (true, warning);
            }, (m, x) => ((x?.System ?? "").Trim().ToLowerInvariant(), (x?.RuleId ?? "").Trim().ToLowerInvariant(),
                WaiverStore.IsRuleLevel(x?.ObjRef) ? "" : WaiverKey(m, x.ObjRef).ToLowerInvariant()),
                (accepted, refs) => ChangeWords.For("waived", "findings", refs));
            if (result.Applied > 0) await SyncPlanWithWaiversAsync(context, s);
            NoteBatch(s.Id, origin, result.Applied, "waived", "findings", "plan", result.Words);
            return result;
        }

        public async Task<BatchEditResult> WriteWordsAsync(WordsWriteItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            var synonymSlots = new LsdlSynonyms.SynonymWrite[items?.Length ?? 0];
            var result = await WriteBatchAsync(s, items, origin, "write words", x => x?.ObjRef, (m, item, i) =>
            {
                synonymSlots[i] = null;
                if (item == null) throw new ArgumentException("An object is required.");
                var obj = ObjectRefs.Resolve(m, item.ObjRef) ?? throw new InvalidOperationException($"{item.ObjRef} not found.");
                LsdlSynonyms.SynonymWrite write = null;
                if (item.Synonyms != null)
                {
                    if (obj is not TabularNamedObject named) throw new InvalidOperationException($"{item.ObjRef} cannot have synonyms.");
                    LsdlSynonyms.RefuseIfUnwritable(named);
                    write = new LsdlSynonyms.SynonymWrite { Object = named, Synonyms = string.Join(",", item.Synonyms) };
                }
                var changed = false;
                if (item.Description != null) changed |= ApplyDescription(obj, item.Description);
                if (item.DisplayFolder != null) changed |= ApplyWordsFolder(obj, item.DisplayFolder);
                synonymSlots[i] = write;
                return (changed, null);
            }, (m, x) => ObjectRefs.Resolve(m, x?.ObjRef), (accepted, refs) => ChangeWords.For(
                accepted.All(x => x.Synonyms != null && x.Description == null && x.DisplayFolder == null)
                    ? accepted.All(x => x.Synonyms.Length == 0) ? "removed synonyms from"
                    : accepted.Any(x => x.Synonyms.Length == 0) ? "changed synonyms for" : "added synonyms to"
                : accepted.All(x => x.Description != null && x.Synonyms == null && x.DisplayFolder == null) ? "changed the description of"
                : "wrote words for", "fields", refs), m =>
            {
                var synonymItems = Enumerable.Range(0, synonymSlots.Length).Where(k => synonymSlots[k] != null).Select(k => (Index: k, Write: synonymSlots[k])).ToList();
                if (synonymItems.Count == 0) return (Array.Empty<int>(), new Dictionary<int, string>(), null);
                var culture = EnsureLinguisticCulture(m, null, out _);
                var outcome = LsdlSynonyms.SetSynonymsMany(culture, synonymItems.Select(x => x.Write).ToList());
                return (outcome.Changed.Select(i => synonymItems[i].Index).ToArray(),
                    outcome.Refused.ToDictionary(x => synonymItems[x.Key].Index, x => x.Value), outcome.Warning);
            });
            NoteBatch(s.Id, origin, result.Applied, "wrote words for", "fields", "plan", result.Words);
            return result;
        }

        public async Task<BatchEditResult> EditObjectsAsync(ObjectEditItem[] items, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            // A rename updates workflow target aliases under the same lock as rename_object.
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Edit objects");
                var aliases = new List<(WorkflowRunState Run, TabularNamedObject Obj, string OldRef)>[items?.Length ?? 0];
                Dictionary<ObjectEditItem, ITabularNamedObject> targets = null;
                var result = await WriteBatchAsync(s, items, origin, "edit objects", x => x?.ObjRef, (m, item, i) =>
                {
                    aliases[i] = null;
                    if (item == null || item.Set == null) throw new ArgumentException("An object and its properties are required.");
                    // Resolve before any rename, so a table rename does not invalidate its children's input refs.
                    targets ??= items.Where(x => x != null).Distinct().ToDictionary(x => x, x => ObjectRefs.Resolve(m, x.ObjRef));
                    var obj = targets[item] ?? throw new InvalidOperationException($"{item.ObjRef} not found.");
                    var oldRef = ObjectRefs.For(obj);
                    var runs = obj is Measure target ? context.WorkflowRuns.ActiveRuns().Where(run => run.CoverageSurface != null
                        && ReferenceEquals(ObjectRefs.Resolve(m, LatestObjectRefAnswer(run, WorkflowRunner.AllAnswers(run))), target)).ToArray() : Array.Empty<WorkflowRunState>();
                    var changed = false;
                    string reportWarn = null;
                    foreach (var pair in item.Set)
                    {
                        var property = BatchProperty(pair.Key);
                        var value = pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString()
                            : pair.Value.ValueKind == JsonValueKind.Null ? null : pair.Value.GetRawText();
                        if (property == "Description") changed |= ApplyDescription(obj, value);
                        else if (property == "DisplayFolder") changed |= ApplyWordsFolder(obj, value);
                        else if (property == "FormatStringExpression") changed |= ApplyMeasureFormatExpression(m, ObjectRefs.For(obj), value);
                        else if (property == "AiSchema")
                        {
                            if (!bool.TryParse(value, out var included)) throw new ArgumentException("aiSchema must be true or false.");
                            var (table, prop) = ResolveSchemaTarget(m, ObjectRefs.For(obj));
                            changed |= ApplyAiDataSchema(m, null, table, prop, included);
                        }
                        else if (property == "DataType")
                        {
                            if (obj is not DataColumn col) throw new InvalidOperationException("Only physical/source columns have a settable data type.");
                            var next = ParseDataType(value);
                            if (col.DataType != next) { col.DataType = next; changed = true; }
                        }
                        else
                        {
                            changed |= ApplyObjectProperty(m, s, obj, property, value, out var touchesReports);
                            reportWarn ??= touchesReports;
                        }
                    }
                    if (changed && obj is Measure renamed && oldRef != ObjectRefs.For(obj))
                        aliases[i] = runs.Select(run => (run, (TabularNamedObject)renamed, oldRef)).ToList();
                    return (changed, reportWarn);
                }, (m, x) => ObjectRefs.Resolve(m, x?.ObjRef), (accepted, refs) => ChangeWords.For(
                    accepted.All(x => x.Set.Count == 1 && x.Set.Keys.All(k => BatchProperty(k) == "FormatString")) ? "changed the format of" : "changed",
                    refs.All(r => r.StartsWith("measure:")) ? "measures" : refs.All(r => r.StartsWith("column:")) ? "columns" : "fields", refs));
                if (DryRunScope.Current == null)
                    foreach (var alias in aliases.Where(x => x != null).SelectMany(x => x)) alias.Run.ObjectRefAliases[alias.OldRef] = ObjectRefs.For(alias.Obj);
                NoteBatch(s.Id, origin, result.Applied, "edited", "fields", "edit/properties", result.Words);
                return result;
            }
            finally { context.WorkflowGate.Release(); }
        }

        private static string BatchProperty(string property) => (property ?? "").ToLowerInvariant() switch
        {
            "name" => "Name", "description" => "Description", "formatstring" => "FormatString",
            "displayfolder" => "DisplayFolder", "hidden" or "ishidden" => "IsHidden",
            "datacategory" => "DataCategory", "summarizeby" => "SummarizeBy",
            "sortbycolumn" => "SortByColumn", "datatype" => "DataType",
            "formatstringexpression" => "FormatStringExpression", "aischema" => "AiSchema",
            _ => throw new InvalidOperationException($"Property '{property}' is not supported here. Use set_property for other properties."),
        };

        /// <summary>One commit for a whole bulk call. Each item is written by <paramref name="write"/>, which gets the item's index and
        /// must be safe to run again: a rejected item takes back the items before it since the last snapshot and runs the good ones
        /// again (<see cref="Session.BatchItems"/>), so anything it keeps beside the model goes in a slot for that index.</summary>
        private async Task<BatchEditResult> WriteBatchAsync<T>(Session s, T[] items, string origin, string label,
            Func<T, string> reference, Func<Model, T, int, (bool Changed, string Warning)> write, Func<Model, T, object> identity = null,
            Func<T[], string[], ChangeWords> words = null,
            Func<Model, (int[] Changed, Dictionary<int, string> Refused, string Warning)> finish = null)
        {
            if (items == null) throw new ArgumentException("Pass an items list.");
            var applied = 0;
            var rejected = new List<BatchRejection>();
            var warnings = new List<string>();
            var cascade = new List<CascadeWarning>();
            ChangeWords summary = null;
            long revision;
            try
            {
                revision = await s.MutateAsync(origin, label, m =>
                {
                    var identities = items.Select(x => identity?.Invoke(m, x)).ToArray();
                    var outcomes = new (bool Changed, string Warning)[items.Length];
                    var failures = new Exception[items.Length];
                    s.BatchItems(items.Length, i => outcomes[i] = write(m, items[i], i),
                        ex => ex is ArgumentException or InvalidOperationException or TargetInvocationException or FormatException or OverflowException or Newtonsoft.Json.JsonException or JsonException,
                        (i, ex) => failures[i] = ex);
                    var changed = new HashSet<object>();
                    var accepted = new List<T>();
                    var targets = new Dictionary<object, string>();
                    for (var i = 0; i < items.Length; i++)
                    {
                        if (failures[i] != null)
                        {
                            var real = (failures[i] as TargetInvocationException)?.InnerException ?? failures[i];
                            rejected.Add(new BatchRejection { Index = i, ObjRef = reference(items[i]), Reason = real.Message });
                            continue;
                        }
                        if (outcomes[i].Changed)
                        {
                            var key = identities[i] ?? i;
                            if (changed.Add(key)) applied++;
                            accepted.Add(items[i]);
                            targets[key] = label == "waive findings"
                                ? JsonSerializer.Serialize(key, new JsonSerializerOptions { IncludeFields = true })
                                : key is ITabularObject obj ? ObjectRefs.For(obj) : reference(items[i]);
                        }
                        if (outcomes[i].Warning != null) warnings.Add(outcomes[i].Warning);
                    }
                    if (finish != null)
                    {
                        var rest = finish(m);
                        // The items finish wrote count here, so the words that name them see them too.
                        foreach (var at in rest.Changed.Where(x => !rest.Refused.ContainsKey(x)))
                        {
                            var key = identities[at] ?? at;
                            if (changed.Add(key)) applied++;
                            if (!accepted.Contains(items[at])) accepted.Add(items[at]);
                            targets[key] = key is ITabularObject obj ? ObjectRefs.For(obj) : reference(items[at]);
                        }
                        foreach (var refusal in rest.Refused.OrderBy(x => x.Key))
                            rejected.Add(new BatchRejection { Index = refusal.Key, ObjRef = reference(items[refusal.Key]), Reason = refusal.Value });
                        if (rest.Warning != null) warnings.Add(rest.Warning);
                    }
                    if (applied == 0) throw new NoopMutationException();
                    summary = words?.Invoke(accepted.ToArray(), targets.Values.ToArray());
                    s.RecordWords(summary);
                }, cascade);
            }
            catch (NoopMutationException) { revision = s.Revision; }
            warnings.AddRange(cascade.Select(x => x.Text));
            return new BatchEditResult { Revision = revision, Applied = applied, Words = summary, Rejected = rejected.ToArray(), Warning = warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct()) };
        }

        private static bool ApplyWordsFolder(ITabularNamedObject obj, string folder)
        {
            var target = NormalizeFolderPath(folder);
            switch (obj)
            {
                case Measure me: if (me.DisplayFolder == target) return false; me.DisplayFolder = target; return true;
                case Column col: if (col.DisplayFolder == target) return false; col.DisplayFolder = target; return true;
                case Hierarchy h: if (h.DisplayFolder == target) return false; h.DisplayFolder = target; return true;
                default: throw new InvalidOperationException("Only measures, columns and hierarchies have a display folder.");
            }
        }
    }
}
