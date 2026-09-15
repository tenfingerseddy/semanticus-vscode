using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Semanticus.Engine
{
    /// <summary>
    /// Projects workflow source and applies typed edits to owned source spans. It never writes a file.
    /// The semantic parser remains authoritative; this locator only identifies syntax the parser read.
    /// </summary>
    internal static class WorkflowDocumentPatcher
    {
        internal sealed class PatchResult
        {
            public string Text;
            public WorkflowEditModel Model;
            public string ErrorCode;
            public string Error;
            public string Target;
            public string Field;
            public readonly List<WorkflowEditKeyChange> KeyChanges = new List<WorkflowEditKeyChange>();
        }

        private sealed class PatchException : Exception
        {
            public string Code { get; }
            public string Target { get; }
            public string Field { get; }
            public PatchException(string code, string message, string target = null, string field = null) : base(message)
            {
                Code = code; Target = target; Field = field;
            }
        }

        private sealed record Span(int Start, int End);
        private sealed record DiffTextLine(string Text, bool Terminated);
        private sealed class Line
        {
            public int Number;
            public int Start;
            public int ContentEnd;
            public int End;
            public string Text;
            public string Ending;
        }

        private abstract class YNode
        {
            public int Start;
            public int End;
            public int Line;
        }
        private sealed class YScalar : YNode
        {
            public string Value;
            public ScalarStyle Style;
        }
        private sealed class YSequence : YNode
        {
            public bool Flow;
            public readonly List<YNode> Items = new List<YNode>();
        }
        private sealed class YEntry
        {
            public string Key;
            public YScalar KeyNode;
            public YNode Value;
            public int Start;
            public int End;
        }
        private sealed class YMap : YNode
        {
            public bool Flow;
            public readonly List<YEntry> Entries = new List<YEntry>();
            public int InsertAt;
            public int Indent;
            public string Ending;
            public YEntry Entry(string key) => Entries.LastOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        }

        private sealed class ItemLoc
        {
            public string Key;
            public string Fingerprint;
            public int Start;
            public int End;
            public YMap Map;
            public bool FlowList;
            public object Value;
        }

        private sealed class StepLoc
        {
            public string Key;
            public string Fingerprint;
            public WorkflowStep Step;
            public int Start;
            public int End;
            public Span Number;
            public Span Title;
            public YMap Control;
            public Span ControlFence;
            public YMap Gate;
            public Span GateFence;
            public readonly List<Span> Fences = new List<Span>();
            public readonly List<Span> InstructionRegions = new List<Span>();
            public readonly List<ItemLoc> Inputs = new List<ItemLoc>();
            public readonly List<ItemLoc> Verify = new List<ItemLoc>();
        }

        private sealed class TargetLoc
        {
            public string Kind;
            public object Value;
            public YMap Map;
            public StepLoc Step;
            public ItemLoc Item;
        }

        private sealed class SourceMap
        {
            public string Text;
            public string ParserText;
            public int Offset;
            public List<Line> Lines;
            public WorkflowDef Def;
            public YMap Front;
            public int FrontClose;
            public string Ending;
            public readonly List<StepLoc> Steps = new List<StepLoc>();
            public readonly List<ItemLoc> Slots = new List<ItemLoc>();
            public readonly Dictionary<string, TargetLoc> Targets = new Dictionary<string, TargetLoc>(StringComparer.Ordinal);
            public readonly List<WorkflowEditRestriction> Restrictions = new List<WorkflowEditRestriction>();
        }

        private static readonly Regex Heading = new Regex(@"^##\s*Step\s+(\d+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex GateFence = new Regex(@"^```\s*yaml\s+gate\s*$", RegexOptions.Compiled);
        private static readonly Regex StepFence = new Regex(@"^```\s*yaml\s+step\s*$", RegexOptions.Compiled);
        private static readonly Regex StableStepId = new Regex(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);
        private static readonly Regex PositionalStepId = new Regex(@"^step-[0-9]+$", RegexOptions.Compiled);
        private static readonly HashSet<string> V1AuthoredFrontFields = new HashSet<string>(new[]
        {
            "schemaVersion", "name", "kind", "title", "description", "whenToUse", "version", "strictness", "triggers", "tags", "slots",
        }, StringComparer.Ordinal);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        internal static WorkflowEditModel Project(string text, WorkflowDef def)
        {
            if (def == null || def.Error != null) return null;
            try { return (WorkflowEditModel)Build(text, def).Targets["workflow"].Value; }
            catch (Exception ex)
            {
                return BuildModelWithoutLocations(def, new WorkflowEditRestriction
                {
                    Target = "workflow", Code = "source_map_mismatch",
                    Message = "The saved source parses, but its fields could not be located safely. Use Source to edit it. " + ex.Message,
                });
            }
        }

        internal static PatchResult Apply(string name, string text, string editsJson, bool allowEmptySkeleton = false)
        {
            var result = new PatchResult { Text = text };
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(editsJson) ? "[]" : editsJson);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    throw new PatchException("invalid_operation", "editsJson must be a JSON array.");
                var operations = json.RootElement.EnumerateArray().ToArray();
                var sourceDef = Parse(text);
                var emptySkeleton = allowEmptySkeleton && IsEmptySkeleton(sourceDef, name);
                if (sourceDef.Error != null && !emptySkeleton)
                    throw new PatchException("parse_error", sourceDef.Error, "workflow");

                var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
                var priorFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
                var candidate = text;
                foreach (var operation in operations)
                {
                    if (operation.ValueKind != JsonValueKind.Object)
                        throw new PatchException("invalid_operation", "Every workflow edit must be a JSON object.");
                    var op = RequiredString(operation, "op");
                    var def = Parse(candidate);
                    var map = Build(candidate, def, allowSemanticError: true);
                    var wholeDocumentRestriction = map.Restrictions.FirstOrDefault(r => r.Target == "workflow"
                        && string.IsNullOrEmpty(r.Field) && r.Code == "template_source_only");
                    if (wholeDocumentRestriction != null)
                        throw new PatchException(wholeDocumentRestriction.Code, wholeDocumentRestriction.Message,
                            wholeDocumentRestriction.Target, wholeDocumentRestriction.Field);
                    candidate = ApplyOne(name, candidate, map, operation, op, aliases, priorFingerprints, result.KeyChanges);
                }
                candidate = PreserveEnvelope(text, candidate);
                var finalDef = Parse(candidate);
                if (finalDef.Error != null)
                {
                    if (emptySkeleton && operations.Length == 0 && IsEmptySkeleton(finalDef, name))
                    {
                        result.Text = candidate;
                        result.Model = BuildModel(Build(candidate, finalDef, allowSemanticError: true));
                        return result;
                    }
                    result.Text = candidate;
                    result.ErrorCode = "parse_error";
                    result.Error = finalDef.Error;
                    return result;
                }
                if (!string.Equals(finalDef.Name, name, StringComparison.Ordinal))
                {
                    result.Text = candidate;
                    result.ErrorCode = "name_mismatch";
                    result.Error = $"frontmatter name '{finalDef.Name}' must equal the workflow name '{name}'.";
                    return result;
                }
                result.Text = candidate;
                result.Model = BuildModel(Build(candidate, finalDef));
                return result;
            }
            catch (JsonException ex)
            {
                result.KeyChanges.Clear();
                result.Model = Project(text, Parse(text));
                result.ErrorCode = "invalid_operation";
                result.Error = "editsJson is not valid JSON. " + ex.Message;
                return result;
            }
            catch (PatchException ex)
            {
                result.KeyChanges.Clear();
                result.Text = text;
                result.Model = Project(text, Parse(text));
                result.ErrorCode = ex.Code;
                result.Error = ex.Message;
                result.Target = ex.Target;
                result.Field = ex.Field;
                return result;
            }
            catch (Exception ex)
            {
                result.KeyChanges.Clear();
                result.Text = text;
                result.Model = Project(text, Parse(text));
                result.ErrorCode = "source_map_mismatch";
                result.Error = "The requested edit could not be localized without rewriting other source bytes. Use Source for this change. " + ex.Message;
                return result;
            }
        }

        private static string ApplyOne(string name, string text, SourceMap map, JsonElement operation, string op,
            Dictionary<string, string> aliases, Dictionary<string, string> priorFingerprints,
            List<WorkflowEditKeyChange> keyChanges)
        {
            ValidateOperationShape(operation, op);
            return op switch
            {
                "set_field" => SetField(text, map, operation, aliases, keyChanges),
                "set_map_entry" => SetMapEntry(text, map, operation, aliases),
                "remove_map_entry" => RemoveMapEntry(text, map, operation, aliases),
                "insert_item" => InsertItem(text, map, operation, aliases, keyChanges),
                "move_item" => MoveItem(text, map, operation, aliases),
                "remove_item" => RemoveItem(text, map, operation, aliases),
                "stabilize_step_ids" => Stabilize(name, text, map, aliases, priorFingerprints, keyChanges),
                "add_step" => AddStep(text, map, operation, aliases, keyChanges),
                "copy_step" => CopyStep(text, map, operation, aliases, keyChanges),
                "move_step" => MoveStep(text, map, operation, aliases),
                "remove_step" => RemoveStep(text, map, operation, aliases, priorFingerprints),
                _ => throw new PatchException("invalid_operation", $"Unknown workflow edit operation '{op}'."),
            };
        }

        private static void ValidateOperationShape(JsonElement operation, string op)
        {
            var allowed = op switch
            {
                "set_field" => new[] { "op", "target", "field", "expect", "value" },
                "set_map_entry" => new[] { "op", "target", "field", "name", "expect", "value" },
                "remove_map_entry" => new[] { "op", "target", "field", "name", "expect" },
                "insert_item" => new[] { "op", "target", "field", "after", "tempKey", "value" },
                "move_item" => new[] { "op", "target", "after", "expectOrder" },
                "remove_item" => new[] { "op", "target", "expectFingerprint" },
                "stabilize_step_ids" => new[] { "op" },
                "add_step" => new[] { "op", "after", "tempKey", "value" },
                "copy_step" => new[] { "op", "target", "after", "tempKey", "newId" },
                "move_step" => new[] { "op", "target", "after", "expectOrder" },
                "remove_step" => new[] { "op", "target", "expectFingerprint" },
                _ => throw new PatchException("invalid_operation", $"Unknown workflow edit operation '{op}'."),
            };
            RequireOnlyProperties(operation, op, allowed);
        }

        private static WorkflowDef Parse(string text)
        {
            var parserText = text != null && text.Length > 0 && text[0] == (char)0xfeff ? text.Substring(1) : text;
            return WorkflowParser.Parse(parserText);
        }

        private static bool IsEmptySkeleton(WorkflowDef def, string name) => def != null
            && def.SchemaVersion == 2 && def.Steps.Length == 0
            && string.Equals(def.Name, name, StringComparison.Ordinal)
            && string.Equals(def.Error, "no '## Step N:' headings found.", StringComparison.Ordinal);

        private static SourceMap Build(string text, WorkflowDef def, bool allowSemanticError = false)
        {
            if (text == null) throw new PatchException("source_map_mismatch", "Workflow source is missing.");
            if (!allowSemanticError && def.Error != null) throw new PatchException("parse_error", def.Error);
            var offset = text.Length > 0 && text[0] == (char)0xfeff ? 1 : 0;
            var parserText = offset == 1 ? text.Substring(1) : text;
            var lines = ReadLines(parserText);
            if (lines.Count == 0 || lines[0].Text != "---")
                throw new PatchException("source_map_mismatch", "The frontmatter opening line could not be located.");
            var close = lines.FindIndex(1, l => l.Text.TrimEnd() == "---");
            if (close < 0) throw new PatchException("source_map_mismatch", "The frontmatter closing line could not be located.");
            var ending = lines.FirstOrDefault(l => l.Ending.Length > 0)?.Ending ?? ((char)10).ToString();
            var map = new SourceMap
            {
                Text = text, ParserText = parserText, Offset = offset, Lines = lines, Def = def,
                FrontClose = lines[close].Start + offset, Ending = ending,
            };
            if (def.SchemaVersion < 2)
                map.Front = ParseV1Map(map, 1, close, 0, lines[close].Start + offset, flattenIndent: true);
            else
                map.Front = ParseYaml(parserText.Substring(lines[1].Start, lines[close].Start - lines[1].Start), lines[1].Start + offset,
                    lines, offset, lines[close].Start + offset, 0, ending) as YMap;
            map.Front ??= new YMap
                {
                    Start = lines[1].Start + offset, End = lines[close].Start + offset,
                    InsertAt = lines[close].Start + offset, Indent = 0, Ending = ending,
                };
            FinalizeMap(map.Front, map, lines[close].Start + offset, 0);
            AddMapDuplicateRestrictions(map, map.Front, "workflow");

            var headings = new List<(int Index, Match Match)>();
            for (var i = close + 1; i < lines.Count; i++)
            {
                var m = Heading.Match(lines[i].Text);
                if (m.Success) headings.Add((i, m));
            }
            for (var i = 0; i < headings.Count; i++)
            {
                var lineIndex = headings[i].Index;
                var line = lines[lineIndex];
                var end = i + 1 < headings.Count ? lines[headings[i + 1].Index].Start + offset : text.Length;
                var semantic = i < (def.Steps?.Length ?? 0) ? def.Steps[i] : new WorkflowStep
                {
                    Id = "step-" + (i + 1), Number = i + 1, Title = headings[i].Match.Groups[2].Value,
                };
                var key = StepKey(semantic, i);
                var step = new StepLoc
                {
                    Key = key, Step = semantic, Start = line.Start + offset, End = end,
                    Number = new Span(line.Start + offset + headings[i].Match.Groups[1].Index,
                        line.Start + offset + headings[i].Match.Groups[1].Index + headings[i].Match.Groups[1].Length),
                    Title = new Span(line.Start + offset + headings[i].Match.Groups[2].Index,
                        line.Start + offset + headings[i].Match.Groups[2].Index + headings[i].Match.Groups[2].Length),
                };
                LocateFences(map, step, lineIndex + 1, i + 1 < headings.Count ? headings[i + 1].Index : lines.Count, def.SchemaVersion >= 2);
                step.Fingerprint = Fingerprint(semantic);
                map.Steps.Add(step);
                map.Targets[key] = new TargetLoc { Kind = "step", Value = semantic, Step = step, Map = step.Control };
            }
            LocateSlots(map);
            foreach (var step in map.Steps) LocateGateItems(map, step);
            if (def.SchemaVersion < 2)
                foreach (var input in map.Steps.SelectMany(step => step.Inputs))
                    map.Restrictions.Add(new WorkflowEditRestriction
                    {
                        Target = input.Key, Field = "scope", Code = "unpreservable_spelling",
                        Message = "Input scope is inactive in version 1. Stabilize the workflow first, or make this change in Source with schemaVersion 2.",
                    });
            map.Targets["workflow"] = new TargetLoc { Kind = "workflow", Value = null, Map = map.Front };
            if (headings.Count != (def.Steps?.Length ?? 0) && !allowSemanticError)
                throw new PatchException("source_map_mismatch", "The parser and source locator found different step counts.");
            BuildModel(map);
            return map;
        }

        private static void LocateFences(SourceMap map, StepLoc step, int bodyStart, int bodyEnd, bool v2)
        {
            var occupied = new List<Span>();
            for (var i = bodyStart; i < bodyEnd; i++)
            {
                var isGate = GateFence.IsMatch(map.Lines[i].Text);
                var isControl = v2 && StepFence.IsMatch(map.Lines[i].Text);
                if (!isGate && !isControl) continue;
                var close = i + 1;
                while (close < bodyEnd && map.Lines[close].Text.TrimEnd() != "```") close++;
                if (close >= bodyEnd) continue;
                var fenceEnd = map.Lines[close].End + map.Offset;
                var span = new Span(map.Lines[i].Start + map.Offset, fenceEnd);
                var blockStart = map.Lines[i].End;
                var blockEnd = map.Lines[close].Start;
                YMap syntax = null;
                if (blockEnd > blockStart)
                {
                    syntax = v2
                        ? ParseYaml(map.ParserText.Substring(blockStart, blockEnd - blockStart), blockStart + map.Offset,
                            map.Lines, map.Offset, map.Lines[close].Start + map.Offset, 0, map.Ending) as YMap
                        : ParseV1Map(map, i + 1, close, 0, map.Lines[close].Start + map.Offset);
                }
                syntax ??= new YMap { Start = blockStart + map.Offset, End = blockEnd + map.Offset };
                FinalizeMap(syntax, map, map.Lines[close].Start + map.Offset, 0);
                if (isGate) { step.Gate = syntax; step.GateFence = span; }
                else { step.Control = syntax; step.ControlFence = span; }
                step.Fences.Add(span); occupied.Add(span);
                i = close;
            }
            var cursor = bodyStart < map.Lines.Count ? map.Lines[bodyStart].Start + map.Offset : step.End;
            foreach (var fence in occupied.OrderBy(s => s.Start))
            {
                if (cursor < fence.Start) step.InstructionRegions.Add(new Span(cursor, fence.Start));
                cursor = fence.End;
            }
            if (cursor < step.End) step.InstructionRegions.Add(new Span(cursor, step.End));
        }

        private static void LocateSlots(SourceMap map)
        {
            var entry = map.Front.Entry("slots");
            if (entry?.Value is not YSequence sequence) return;
            var items = ItemRanges(sequence, entry, map);
            for (var i = 0; i < Math.Min(items.Count, map.Def.Slots?.Length ?? 0); i++)
            {
                var slot = map.Def.Slots[i];
                var key = UniqueItemKey("slot:" + (slot.Name ?? "item"), map.Slots.Select(x => x.Key));
                var item = new ItemLoc { Key = key, Map = items[i].Map, FlowList = items[i].FlowList, Start = items[i].Start, End = items[i].End, Value = slot, Fingerprint = Fingerprint(slot) };
                map.Slots.Add(item);
                map.Targets[key] = new TargetLoc { Kind = "slot", Value = slot, Map = item.Map, Item = item };
            }
            AddDuplicateRestrictions(map, map.Slots, "slot");
        }

        private static void LocateGateItems(SourceMap map, StepLoc step)
        {
            if (step.Gate == null) return;
            AddMapDuplicateRestrictions(map, step.Gate, step.Key);
            LocateGateList(map, step, "inputs", step.Step.Gate?.Inputs ?? Array.Empty<GateInput>(), step.Inputs, "input");
            LocateGateList(map, step, "verify", step.Step.Gate?.Verify ?? Array.Empty<VerifySpec>(), step.Verify, "verify");
        }

        private static void LocateGateList<T>(SourceMap map, StepLoc step, string field, T[] semantic,
            List<ItemLoc> destination, string kind)
        {
            var entry = step.Gate.Entry(field);
            if (entry?.Value is not YSequence sequence) return;
            var ranges = ItemRanges(sequence, entry, map);
            for (var i = 0; i < Math.Min(ranges.Count, semantic.Length); i++)
            {
                var name = kind == "input" ? ((GateInput)(object)semantic[i]).Name : ((VerifySpec)(object)semantic[i]).Kind;
                var baseKey = step.Key + "/" + kind + ":" + (name ?? "item");
                var key = UniqueItemKey(baseKey, destination.Select(x => x.Key));
                var item = new ItemLoc { Key = key, Map = ranges[i].Map, FlowList = ranges[i].FlowList, Start = ranges[i].Start, End = ranges[i].End, Value = semantic[i], Fingerprint = Fingerprint(semantic[i]) };
                destination.Add(item);
                map.Targets[key] = new TargetLoc { Kind = kind, Value = semantic[i], Map = item.Map, Step = step, Item = item };
                AddMapDuplicateRestrictions(map, item.Map, key);
            }
            AddDuplicateRestrictions(map, destination, kind);
        }

        private static void AddMapDuplicateRestrictions(SourceMap source, YMap map, string target)
        {
            if (map == null) return;
            foreach (var group in map.Entries.GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
                source.Restrictions.Add(new WorkflowEditRestriction
                {
                    Target = target, Field = group.Key, Code = "ambiguous_duplicate",
                    Message = $"'{group.Key}' appears more than once in this version 1 scope. Edit that field in Source so the intended occurrence is clear.",
                });
        }

        private static void AddDuplicateRestrictions(SourceMap map, List<ItemLoc> items, string kind)
        {
            foreach (var group in items.GroupBy(x => kind == "slot" ? ((SlotDef)x.Value).Name : kind == "input" ? ((GateInput)x.Value).Name : ((VerifySpec)x.Value).Kind, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
                foreach (var item in group)
                    map.Restrictions.Add(new WorkflowEditRestriction
                    {
                        Target = item.Key, Code = "ambiguous_duplicate",
                        Message = $"This {kind} has the same identity as another item. Edit it in Source so the intended occurrence is clear.",
                    });
        }

        private static string UniqueItemKey(string basis, IEnumerable<string> existing)
        {
            var set = existing.ToHashSet(StringComparer.Ordinal);
            if (!set.Contains(basis)) return basis;
            var n = 2;
            while (set.Contains(basis + "#" + n)) n++;
            return basis + "#" + n;
        }

        private static List<ItemLoc> ItemRanges(YSequence sequence, YEntry owner, SourceMap source)
        {
            var result = new List<ItemLoc>();
            var flow = sequence.Flow;
            for (var i = 0; i < sequence.Items.Count; i++)
            {
                var node = sequence.Items[i];
                var start = flow ? node.Start : LineOf(source, node.Start).Start + source.Offset;
                var end = i + 1 < sequence.Items.Count
                    ? (flow ? sequence.Items[i + 1].Start : LineOf(source, sequence.Items[i + 1].Start).Start + source.Offset)
                    : owner.End;
                if (flow) end = node.End;
                var itemMap = node as YMap;
                if (itemMap != null) FinalizeMap(itemMap, source, end, flow ? 0 : LeadingIndent(source, start) + 2);
                result.Add(new ItemLoc { Start = start, End = end, Map = itemMap, FlowList = flow });
            }
            return result;
        }

        private static void FinalizeMap(YMap map, SourceMap source, int boundary, int fallbackIndent)
        {
            map.InsertAt = boundary;
            map.Indent = map.Entries.Count == 0 ? fallbackIndent : ColumnOf(source, map.Entries[0].KeyNode.Start);
            map.Ending = source.Ending;
            for (var i = 0; i < map.Entries.Count; i++)
            {
                var entry = map.Entries[i];
                entry.Start = LineOf(source, entry.KeyNode.Start).Start + source.Offset;
                entry.End = i + 1 < map.Entries.Count
                    ? LineOf(source, map.Entries[i + 1].KeyNode.Start).Start + source.Offset
                    : boundary;
            }
            foreach (var entry in map.Entries)
            {
                if (entry.Value is YMap child) FinalizeMap(child, source, entry.End, map.Indent + 2);
            }
        }

        private static YMap ParseV1Map(SourceMap source, int firstLine, int endLine, int indent, int boundary,
            bool flattenIndent = false)
        {
            var map = new YMap
            {
                Start = firstLine < source.Lines.Count ? source.Lines[firstLine].Start + source.Offset : boundary,
                End = boundary, InsertAt = boundary, Indent = indent, Ending = source.Ending,
            };
            for (var i = firstLine; i < endLine; i++)
            {
                var line = source.Lines[i];
                var leading = line.Text.TakeWhile(char.IsWhiteSpace).Count();
                if (string.IsNullOrWhiteSpace(line.Text) || !flattenIndent && leading != indent) continue;
                var lineIndent = flattenIndent ? leading : indent;
                var raw = line.Text.Substring(lineIndent);
                var colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                var key = raw.Substring(0, colon).Trim();
                var keyStartInRaw = raw.IndexOf(key, StringComparison.Ordinal);
                var keyNode = new YScalar
                {
                    Start = line.Start + source.Offset + lineIndent + keyStartInRaw,
                    End = line.Start + source.Offset + lineIndent + keyStartInRaw + key.Length,
                    Line = line.Number, Value = key, Style = ScalarStyle.Plain,
                };
                var rawValueStart = lineIndent + colon + 1;
                while (rawValueStart < line.Text.Length && char.IsWhiteSpace(line.Text[rawValueStart])) rawValueStart++;
                YNode value;
                if (rawValueStart >= line.Text.Length
                    && (key == "slots" || (!flattenIndent && (key is "inputs" or "verify"))))
                {
                    var blockEndLine = i + 1;
                    while (blockEndLine < endLine)
                    {
                        var candidate = source.Lines[blockEndLine];
                        var candidateIndent = candidate.Text.TakeWhile(char.IsWhiteSpace).Count();
                        if (!string.IsNullOrWhiteSpace(candidate.Text) && candidateIndent <= indent) break;
                        blockEndLine++;
                    }
                    var sequence = new YSequence { Start = line.End + source.Offset, Line = line.Number };
                    for (var itemLine = i + 1; itemLine < blockEndLine;)
                    {
                        var itemText = source.Lines[itemLine].Text;
                        var trimmed = itemText.TrimStart();
                        if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) { itemLine++; continue; }
                        var itemIndent = itemText.Length - trimmed.Length;
                        var next = itemLine + 1;
                        while (next < blockEndLine)
                        {
                            var nextText = source.Lines[next].Text;
                            if (nextText.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                                && nextText.Length - nextText.TrimStart().Length == itemIndent) break;
                            next++;
                        }
                        var itemBoundary = next < blockEndLine ? source.Lines[next].Start + source.Offset
                            : blockEndLine < source.Lines.Count ? source.Lines[blockEndLine].Start + source.Offset : boundary;
                        var itemMap = ParseV1ItemMap(source, itemLine, next, itemIndent, itemBoundary);
                        sequence.Items.Add(itemMap);
                        itemLine = next;
                    }
                    sequence.End = blockEndLine < source.Lines.Count ? source.Lines[blockEndLine].Start + source.Offset : boundary;
                    value = sequence;
                    i = blockEndLine - 1;
                }
                else
                {
                    value = V1Scalar(source, line, rawValueStart);
                }
                map.Entries.Add(new YEntry { Key = key, KeyNode = keyNode, Value = value });
            }
            return map;
        }

        private static YMap ParseV1ItemMap(SourceMap source, int firstLine, int endLine, int itemIndent, int boundary)
        {
            var map = new YMap { Start = source.Lines[firstLine].Start + source.Offset, End = boundary };
            for (var i = firstLine; i < endLine; i++)
            {
                var line = source.Lines[i];
                var trimmed = line.Text.TrimStart();
                var prefix = i == firstLine ? "- " : "";
                if (i == firstLine)
                {
                    if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    trimmed = trimmed.Substring(2);
                }
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed.Substring(0, colon).Trim();
                var keyAt = line.Text.IndexOf(key, StringComparison.Ordinal);
                var keyNode = new YScalar
                {
                    Start = line.Start + source.Offset + keyAt, End = line.Start + source.Offset + keyAt + key.Length,
                    Line = line.Number, Value = key, Style = ScalarStyle.Plain,
                };
                var valueAt = line.Text.IndexOf(':', keyAt + key.Length) + 1;
                while (valueAt < line.Text.Length && char.IsWhiteSpace(line.Text[valueAt])) valueAt++;
                map.Entries.Add(new YEntry { Key = key, KeyNode = keyNode, Value = V1Scalar(source, line, valueAt) });
            }
            return map;
        }

        private static YScalar V1Scalar(SourceMap source, Line line, int startInLine)
        {
            var end = line.ContentEnd;
            var style = ScalarStyle.Plain;
            if (startInLine < line.Text.Length && (line.Text[startInLine] == (char)39 || line.Text[startInLine] == '"'))
            {
                style = line.Text[startInLine] == (char)39 ? ScalarStyle.SingleQuoted : ScalarStyle.DoubleQuoted;
                var close = line.Text.IndexOf(line.Text[startInLine], startInLine + 1);
                if (close >= 0) end = line.Start + close + 1;
            }
            else
            {
                var comment = line.Text.IndexOf(" #", startInLine, StringComparison.Ordinal);
                if (comment >= 0) end = line.Start + comment;
                while (end > line.Start + startInLine && char.IsWhiteSpace(source.ParserText[end - 1])) end--;
            }
            return new YScalar
            {
                Start = line.Start + source.Offset + startInLine, End = end + source.Offset,
                Line = line.Number, Value = startInLine < line.Text.Length ? line.Text.Substring(startInLine, Math.Max(0, end - line.Start - startInLine)) : "",
                Style = style,
            };
        }

        private static YNode ParseYaml(string text, int absoluteStart, List<Line> lines, int sourceOffset,
            int boundary, int indent, string ending)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _)) return null;
            parser.Consume<DocumentStart>();
            var node = ReadYamlNode(parser, absoluteStart, text);
            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
            if (node is YMap map)
            {
                map.InsertAt = boundary; map.Indent = indent; map.Ending = ending;
            }
            return node;
        }

        private static YNode ReadYamlNode(IParser parser, int absoluteStart, string yaml)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
                return new YScalar
                {
                    Start = absoluteStart + (int)scalar.Start.Index,
                    End = absoluteStart + (int)scalar.End.Index,
                    Line = (int)scalar.Start.Line,
                    Value = scalar.Style == ScalarStyle.Plain && scalar.Value.Length == 0 ? null : scalar.Value,
                    Style = scalar.Style,
                };
            if (parser.TryConsume<SequenceStart>(out var sequenceStart))
            {
                var sequence = new YSequence
                {
                    Start = absoluteStart + (int)sequenceStart.Start.Index, Line = (int)sequenceStart.Start.Line,
                    Flow = sequenceStart.Style == SequenceStyle.Flow,
                };
                SequenceEnd sequenceEnd;
                while (!parser.TryConsume(out sequenceEnd)) sequence.Items.Add(ReadYamlNode(parser, absoluteStart, yaml));
                var sequenceEndIndex = (int)sequenceEnd.End.Index;
                if (sequenceEndIndex < yaml.Length && yaml[sequenceEndIndex] == ']') sequenceEndIndex++;
                sequence.End = absoluteStart + sequenceEndIndex;
                return sequence;
            }
            if (parser.TryConsume<MappingStart>(out var mapStart))
            {
                var map = new YMap
                {
                    Start = absoluteStart + (int)mapStart.Start.Index, Line = (int)mapStart.Start.Line,
                    Flow = mapStart.Style == MappingStyle.Flow,
                };
                MappingEnd mapEnd;
                while (!parser.TryConsume(out mapEnd))
                {
                    var key = ReadYamlNode(parser, absoluteStart, yaml) as YScalar
                        ?? throw new InvalidOperationException("A workflow YAML key was not scalar.");
                    var value = ReadYamlNode(parser, absoluteStart, yaml);
                    map.Entries.Add(new YEntry { Key = key.Value, KeyNode = key, Value = value });
                }
                var mapEndIndex = (int)mapEnd.End.Index;
                if (mapEndIndex < yaml.Length && yaml[mapEndIndex] == '}') mapEndIndex++;
                map.End = absoluteStart + mapEndIndex;
                return map;
            }
            throw new InvalidOperationException("The workflow YAML node could not be located.");
        }

        private static List<Line> ReadLines(string text)
        {
            var result = new List<Line>();
            var start = 0; var number = 1;
            while (start < text.Length)
            {
                var lf = text.IndexOf((char)10, start);
                if (lf < 0)
                {
                    result.Add(new Line { Number = number, Start = start, ContentEnd = text.Length, End = text.Length, Text = text.Substring(start), Ending = "" });
                    start = text.Length; break;
                }
                var contentEnd = lf > start && text[lf - 1] == (char)13 ? lf - 1 : lf;
                result.Add(new Line
                {
                    Number = number++, Start = start, ContentEnd = contentEnd, End = lf + 1,
                    Text = text.Substring(start, contentEnd - start), Ending = text.Substring(contentEnd, lf + 1 - contentEnd),
                });
                start = lf + 1;
            }
            if (text.Length == 0 || start == text.Length)
                result.Add(new Line { Number = number, Start = text.Length, ContentEnd = text.Length, End = text.Length, Text = "", Ending = "" });
            return result;
        }

        private static Line LineOf(SourceMap source, int absolute)
        {
            var local = Math.Max(0, absolute - source.Offset);
            var line = source.Lines.LastOrDefault(l => l.Start <= local) ?? source.Lines[0];
            return line;
        }

        private static string StepKey(WorkflowStep step, int index) =>
            step.HasExplicitId && !PositionalStepId.IsMatch(step.Id ?? "") ? "step:" + step.Id : "step-position:" + (index + 1);

        private static WorkflowEditModel BuildModel(SourceMap source)
        {
            var def = source.Def;
            var model = new WorkflowEditModel
            {
                Format = def.SchemaVersion >= 2 ? "v2" : "v1",
                Restrictions = source.Restrictions.ToArray(),
                Definition = new WorkflowEditDefinition
                {
                    Key = "workflow", SchemaVersion = def.SchemaVersion, Name = def.Name,
                    Kind = string.IsNullOrEmpty(def.Kind) ? "workflow" : def.Kind,
                    Title = def.Title, Description = def.Description, WhenToUse = def.WhenToUse,
                    Version = def.Version, Strictness = def.Strictness,
                    Triggers = def.Triggers ?? Array.Empty<string>(), Tags = def.Tags ?? Array.Empty<string>(),
                    Provenance = (def.Provenance ?? new Dictionary<string, string>()).Select(kv => new WorkflowEditMapEntry
                    { Key = "provenance:" + kv.Key, Name = kv.Key, Value = kv.Value }).ToArray(),
                    Slots = (def.Slots ?? Array.Empty<SlotDef>()).Select((slot, i) => new WorkflowEditSlot
                    {
                        Key = i < source.Slots.Count ? source.Slots[i].Key : "slot:" + slot.Name,
                        Fingerprint = Fingerprint(slot), Name = slot.Name, Question = slot.Question, Type = slot.Type,
                        Required = slot.Required, Default = slot.Default, Example = slot.Example, Hint = slot.Hint,
                        Values = slot.Values ?? Array.Empty<string>(),
                    }).ToArray(),
                    Steps = (def.Steps ?? Array.Empty<WorkflowStep>()).Select((step, i) => EditStep(source, step, i)).ToArray(),
                },
            };
            if (string.Equals(def.Kind, "template", StringComparison.Ordinal))
                model.Restrictions = model.Restrictions.Concat(new[] { new WorkflowEditRestriction
                {
                    Target = "workflow", Code = "template_source_only",
                    Message = "Template recipes use their existing Source editor. Structured saving here is only for runnable workflows.",
                } }).ToArray();
            source.Targets["workflow"].Value = model;
            return model;
        }

        private static WorkflowEditStep EditStep(SourceMap source, WorkflowStep step, int index)
        {
            var loc = index < source.Steps.Count ? source.Steps[index] : null;
            var key = loc?.Key ?? StepKey(step, index);
            var inputLocs = loc?.Inputs ?? new List<ItemLoc>();
            var verifyLocs = loc?.Verify ?? new List<ItemLoc>();
            return new WorkflowEditStep
            {
                Key = key, Fingerprint = Fingerprint(step), Id = step.Id,
                IdKind = !step.HasExplicitId ? "implicit-positional" : PositionalStepId.IsMatch(step.Id ?? "") ? "explicit-positional" : "explicit-stable",
                Number = step.Number, Title = step.Title, Instructions = step.Instructions, Ops = step.Ops ?? Array.Empty<string>(), When = step.When,
                ForEach = step.ForEach == null ? null : new WorkflowEditForEach
                {
                    Source = step.ForEach.InLiteral != null
                        ? new WorkflowEditLoopSource { Kind = "literal", Values = step.ForEach.InLiteral }
                        : new WorkflowEditLoopSource { Kind = "input", Name = step.ForEach.InInput },
                    As = step.ForEach.As, MaxIterations = step.ForEach.MaxIterations,
                },
                Call = step.Call == null ? null : new WorkflowEditCall
                {
                    Workflow = step.Call.Workflow,
                    With = (step.Call.With ?? new Dictionary<string, string>()).Select(kv => new WorkflowEditMapEntry
                    { Key = key + "/call.with:" + kv.Key, Name = kv.Key, Value = kv.Value }).ToArray(),
                    Returns = step.Call.Returns ?? Array.Empty<string>(),
                },
                Gate = step.Gate == null ? null : new WorkflowEditGate
                {
                    Strictness = step.Gate.Strictness,
                    Inputs = (step.Gate.Inputs ?? Array.Empty<GateInput>()).Select((input, i) => new WorkflowEditInput
                    {
                        Key = i < inputLocs.Count ? inputLocs[i].Key : key + "/input:" + input.Name,
                        Fingerprint = Fingerprint(input), Name = input.Name, Question = input.Question, Type = input.Type,
                        Required = input.Required, DaxPurity = input.DaxPurity, Scope = input.Scope,
                    }).ToArray(),
                    Verify = (step.Gate.Verify ?? Array.Empty<VerifySpec>()).Select((verify, i) => new WorkflowEditVerify
                    {
                        Key = i < verifyLocs.Count ? verifyLocs[i].Key : key + "/verify:" + verify.Kind,
                        Fingerprint = Fingerprint(verify), Kind = verify.Kind, When = verify.When, Probe = verify.Probe,
                        Scope = verify.Scope, Intent = verify.Intent, PinnedShapes = verify.PinnedShapes ?? Array.Empty<string>(),
                        OpenShapes = verify.OpenShapes ?? Array.Empty<string>(), OpenShapesFrom = verify.OpenShapesFrom,
                        OpenMismatch = verify.OpenMismatch, Anchors = verify.Anchors,
                    }).ToArray(),
                },
            };
        }

        private static WorkflowEditModel BuildModelWithoutLocations(WorkflowDef def, WorkflowEditRestriction restriction)
        {
            var source = new SourceMap { Def = def };
            source.Restrictions.Add(restriction);
            foreach (var step in def.Steps ?? Array.Empty<WorkflowStep>())
                source.Steps.Add(new StepLoc { Key = StepKey(step, source.Steps.Count), Step = step });
            source.Targets["workflow"] = new TargetLoc { Kind = "workflow" };
            return BuildModel(source);
        }

        private static string Fingerprint(object value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
            return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static string Resolve(Dictionary<string, string> aliases, string key)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (key != null && aliases.TryGetValue(key, out var next) && next != key)
            {
                if (!seen.Add(key))
                    throw new PatchException("stale_target", $"Edit target '{key}' became ambiguous after identity changes. Reproject the draft and retry.", key);
                key = next;
            }
            return key;
        }

        private static TargetLoc Target(SourceMap map, Dictionary<string, string> aliases, string key)
        {
            key = Resolve(aliases, key);
            if (key == null || !map.Targets.TryGetValue(key, out var target))
                throw new PatchException("stale_target", $"Edit target '{key ?? "(missing)"}' is not present in this draft. Reproject the draft and retry.", key);
            var restriction = map.Restrictions.FirstOrDefault(r => r.Target == key && string.IsNullOrEmpty(r.Field));
            if (restriction != null) throw new PatchException(restriction.Code, restriction.Message, key, restriction.Field);
            return target;
        }

        private static string SetField(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases,
            List<WorkflowEditKeyChange> keyChanges)
        {
            var key = RequiredString(operation, "target");
            var field = RequiredString(operation, "field");
            var target = Target(map, aliases, key);
            var restriction = map.Restrictions.FirstOrDefault(r => r.Target == Resolve(aliases, key)
                && (r.Field == field || target.Kind == "step" && r.Field == "strictness" && field == "gate.strictness"));
            if (restriction != null) throw new PatchException(restriction.Code, restriction.Message, key, field);
            if (!operation.TryGetProperty("expect", out var expect))
                throw new PatchException("invalid_operation", "set_field requires expect.", key, field);
            if (!operation.TryGetProperty("value", out var value))
                throw new PatchException("invalid_operation", "set_field requires value.", key, field);
            var current = CurrentField(target, field);
            if (!JsonEqual(expect, current))
                throw new PatchException("stale_value", $"The current value of '{field}' no longer matches expect. Reproject the draft and retry.", key, field);
            if (JsonEqual(value, current)) return text;

            string changed;
            switch (target.Kind)
            {
                case "workflow":
                    changed = field switch
                    {
                        "title" or "description" or "whenToUse" or "strictness" => PatchMapField(text, map.Front, field, value, map),
                        "version" => PatchMapField(text, map.Front, field, value, map),
                        "triggers" or "tags" => PatchMapField(text, map.Front, field, value, map),
                        _ => throw ReadOnlyOrUnknown(key, field),
                    };
                    break;
                case "slot":
                    changed = field switch
                    {
                        "name" or "question" or "type" or "required" or "default" or "example" or "hint" or "values" => PatchMapField(text, target.Map, field, value, map),
                        _ => throw ReadOnlyOrUnknown(key, field),
                    };
                    break;
                case "input":
                    changed = field switch
                    {
                        "name" or "question" or "type" or "required" or "daxPurity" or "scope" => PatchMapField(text, target.Map, field, value, map),
                        _ => throw ReadOnlyOrUnknown(key, field),
                    };
                    break;
                case "verify":
                    changed = field switch
                    {
                        "kind" or "when" or "probe" or "scope" or "intent" or "pinnedShapes" or "openShapes" or "openShapesFrom" or "openMismatch" or "anchors" => PatchMapField(text, target.Map, field, value, map),
                        _ => throw ReadOnlyOrUnknown(key, field),
                    };
                    break;
                case "step":
                    changed = SetStepField(text, map, target.Step, field, value);
                    break;
                default: throw ReadOnlyOrUnknown(key, field);
            }

            if ((target.Kind == "slot" || target.Kind == "input" || target.Kind == "verify") && field is "name" or "kind")
            {
                var changedDef = Parse(changed);
                if (changedDef.Error == null)
                {
                    var changedMap = Build(changed, changedDef);
                    var old = Resolve(aliases, key);
                    var matching = target.Kind == "slot" ? changedMap.Slots : target.Kind == "input"
                        ? changedMap.Steps.First(s => s.Key == target.Step.Key).Inputs
                        : changedMap.Steps.First(s => s.Key == target.Step.Key).Verify;
                    var index = target.Kind == "slot" ? map.Slots.IndexOf(target.Item)
                        : target.Kind == "input" ? target.Step.Inputs.IndexOf(target.Item) : target.Step.Verify.IndexOf(target.Item);
                    if (index >= 0 && index < matching.Count && matching[index].Key != old)
                        RegisterAlias(aliases, keyChanges, old, matching[index].Key);
                }
            }
            VerifyFieldEffect(changed, aliases, key, field, value);
            return changed;
        }

        private static void VerifyFieldEffect(string changed, Dictionary<string, string> aliases, string key,
            string field, JsonElement requested)
        {
            var def = Parse(changed);
            // Dependent edits may repair an intermediate reference. Container DTOs also carry null/default
            // members and opaque map-entry keys that are not authored values, so verify their final parse only.
            if (def.Error != null || field == "call" || field == "forEach" || field == "forEach.source") return;
            var changedMap = Build(changed, def);
            var resolved = Resolve(aliases, key);
            if (!changedMap.Targets.TryGetValue(resolved, out var changedTarget)) return;
            var actual = CurrentField(changedTarget, field);
            if (requested.ValueKind == JsonValueKind.Null
                && (field == "instructions" && string.Equals(actual as string, "", StringComparison.Ordinal)
                    || !IsFieldAuthored(changedTarget, field)))
                return;
            if (!JsonEqual(requested, actual))
                throw new PatchException("unpreservable_spelling",
                    $"The source spelling cannot represent the requested '{field}' value without changing its meaning. Use Source or stabilize the workflow first.",
                    resolved, field);
        }

        private static bool IsFieldAuthored(TargetLoc target, string field)
        {
            if (target.Kind is "workflow" or "slot" or "input" or "verify")
                return target.Map?.Entry(field) != null;
            if (target.Kind != "step") return false;
            return field switch
            {
                "title" or "instructions" => true,
                "ops" => target.Step.Gate?.Entry("ops") != null,
                "gate.strictness" => target.Step.Gate?.Entry("strictness") != null,
                "when" => target.Step.Control?.Entry("when") != null,
                "forEach" => target.Step.Control?.Entry("forEach") != null,
                "call" => target.Step.Control?.Entry("call") != null,
                "forEach.source" => (target.Step.Control?.Entry("forEach")?.Value as YMap)?.Entry("in") != null,
                "forEach.as" => (target.Step.Control?.Entry("forEach")?.Value as YMap)?.Entry("as") != null,
                "forEach.maxIterations" => (target.Step.Control?.Entry("forEach")?.Value as YMap)?.Entry("maxIterations") != null,
                "call.workflow" => (target.Step.Control?.Entry("call")?.Value as YMap)?.Entry("workflow") != null,
                "call.returns" => (target.Step.Control?.Entry("call")?.Value as YMap)?.Entry("returns") != null,
                _ => false,
            };
        }

        private static PatchException ReadOnlyOrUnknown(string target, string field) =>
            new PatchException("invalid_operation", $"Field '{field}' is not editable on target '{target}'.", target, field);

        private static object CurrentField(TargetLoc target, string field)
        {
            return target.Kind switch
            {
                "workflow" => CurrentWorkflowField(((WorkflowEditModel)target.Value).Definition, field),
                "slot" => PropertyValue((SlotDef)target.Value, field),
                "input" => PropertyValue((GateInput)target.Value, field),
                "verify" => PropertyValue((VerifySpec)target.Value, field),
                "step" => CurrentStepField((WorkflowStep)target.Value, field, target.Step.Key),
                _ => null,
            };
        }

        private static object CurrentWorkflowField(WorkflowEditDefinition def, string field) => field switch
        {
            "title" => def.Title, "description" => def.Description, "whenToUse" => def.WhenToUse,
            "version" => def.Version, "strictness" => def.Strictness, "triggers" => def.Triggers, "tags" => def.Tags,
            _ => null,
        };

        private static object PropertyValue(object value, string field)
        {
            var name = char.ToUpperInvariant(field[0]) + field.Substring(1);
            return value.GetType().GetProperty(name)?.GetValue(value);
        }

        private static object CurrentStepField(WorkflowStep step, string field, string stepKey) => field switch
        {
            "title" => step.Title, "instructions" => step.Instructions, "ops" => step.Ops, "when" => step.When,
            "forEach" => step.ForEach == null ? null : new WorkflowEditForEach
            {
                Source = step.ForEach.InLiteral != null ? new WorkflowEditLoopSource { Kind = "literal", Values = step.ForEach.InLiteral }
                    : new WorkflowEditLoopSource { Kind = "input", Name = step.ForEach.InInput },
                As = step.ForEach.As, MaxIterations = step.ForEach.MaxIterations,
            },
            "forEach.source" => step.ForEach == null ? null : step.ForEach.InLiteral != null
                ? new WorkflowEditLoopSource { Kind = "literal", Values = step.ForEach.InLiteral }
                : new WorkflowEditLoopSource { Kind = "input", Name = step.ForEach.InInput },
            "forEach.as" => step.ForEach?.As, "forEach.maxIterations" => step.ForEach?.MaxIterations,
            "call" => step.Call == null ? null : new WorkflowEditCall
            {
                Workflow = step.Call.Workflow,
                With = step.Call.With.Select(kv => new WorkflowEditMapEntry { Key = stepKey + "/call.with:" + kv.Key, Name = kv.Key, Value = kv.Value }).ToArray(),
                Returns = step.Call.Returns,
            },
            "call.workflow" => step.Call?.Workflow, "call.returns" => step.Call?.Returns,
            "gate.strictness" => step.Gate?.Strictness,
            _ => null,
        };

        private static string SetStepField(string text, SourceMap map, StepLoc step, string field, JsonElement value)
        {
            if (field == "title")
            {
                var title = JsonString(value);
                if (title.IndexOfAny(new[] { (char)10, (char)13 }) >= 0)
                    throw new PatchException("invalid_operation", "A step title must stay on one heading line.", step.Key, field);
                return Replace(text, step.Title.Start, step.Title.End, title);
            }
            if (field == "instructions") return PatchInstructions(text, map, step, value);
            if (field == "ops") return CleanupGate(PatchGateField(text, map, step, "ops", value), step.Key);
            if (field == "gate.strictness") return CleanupGate(PatchGateField(text, map, step, "strictness", value), step.Key);
            if (field == "when") return CleanupControl(PatchControlField(text, map, step, "when", value), step.Key);
            if (field == "forEach") return CleanupControl(PatchControlField(text, map, step, "forEach", value, EditForEachYaml), step.Key);
            if (field == "call") return CleanupControl(PatchControlField(text, map, step, "call", value, EditCallYaml), step.Key);
            if (field.StartsWith("forEach.", StringComparison.Ordinal))
            {
                var each = step.Control?.Entry("forEach")?.Value as YMap
                    ?? throw new PatchException("invalid_operation", "This step has no forEach block to edit.", step.Key, field);
                var nested = field.Substring("forEach.".Length);
                if (nested == "source") return PatchMapField(text, each, "in", value, map, EditLoopSourceYaml);
                if (nested == "as") return PatchMapField(text, each, "as", value, map);
                if (nested == "maxIterations") return PatchMapField(text, each, "maxIterations", value, map);
            }
            if (field.StartsWith("call.", StringComparison.Ordinal))
            {
                var call = step.Control?.Entry("call")?.Value as YMap
                    ?? throw new PatchException("invalid_operation", "This step has no call block to edit.", step.Key, field);
                var nested = field.Substring("call.".Length);
                if (nested == "workflow") return PatchMapField(text, call, "workflow", value, map);
                if (nested == "returns") return PatchMapField(text, call, "returns", value, map);
            }
            throw ReadOnlyOrUnknown(step.Key, field);
        }

        private static string CleanupGate(string text, string stepKey)
        {
            var def = Parse(text);
            if (def.Error != null) return text;
            var map = Build(text, def);
            var step = map.Steps.FirstOrDefault(s => s.Key == stepKey);
            if (step?.GateFence != null && step.Step.Gate == null && (step.Step.Ops?.Length ?? 0) == 0)
                return Replace(text, step.GateFence.Start, step.GateFence.End, "");
            return text;
        }

        private static string CleanupControl(string text, string stepKey)
        {
            var def = Parse(text);
            if (def.Error != null) return text;
            var map = Build(text, def);
            var step = map.Steps.FirstOrDefault(s => s.Key == stepKey);
            if (step?.ControlFence != null && !step.Step.HasExplicitId && step.Step.When == null
                && step.Step.ForEach == null && step.Step.Call == null)
                return Replace(text, step.ControlFence.Start, step.ControlFence.End, "");
            return text;
        }

        private static string PatchInstructions(string text, SourceMap map, StepLoc step, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Null)
                throw new PatchException("invalid_operation", "instructions must be text or null.", step.Key, "instructions");
            var replacement = value.ValueKind == JsonValueKind.Null ? "" : value.GetString() ?? "";
            replacement = NormalizeEndings(replacement, map.Ending);
            var contentRegions = step.InstructionRegions.Where(s => text.Substring(s.Start, s.End - s.Start).Any(ch => !char.IsWhiteSpace(ch))).ToArray();
            if (contentRegions.Length == 1)
            {
                var region = contentRegions[0];
                var first = region.Start;
                while (first < region.End && char.IsWhiteSpace(text[first])) first++;
                var last = region.End;
                while (last > first && char.IsWhiteSpace(text[last - 1])) last--;
                return Replace(text, first, last, replacement);
            }
            var ranges = step.InstructionRegions.OrderByDescending(s => s.Start).ToArray();
            var stripped = text;
            foreach (var region in ranges) stripped = Replace(stripped, region.Start, region.End, "");
            if (replacement.Length == 0) return stripped;
            var insertAt = step.Start + FirstLineLength(text, step.Start);
            if (step.ControlFence != null && step.ControlFence.Start == insertAt) insertAt = step.ControlFence.End;
            var inserted = map.Ending + replacement;
            if (!inserted.EndsWith(map.Ending, StringComparison.Ordinal)) inserted += map.Ending;
            return stripped.Insert(insertAt, inserted);
        }

        private static int FirstLineLength(string text, int start)
        {
            var lf = text.IndexOf((char)10, start);
            return (lf < 0 ? text.Length : lf + 1) - start;
        }

        private static string PatchControlField(string text, SourceMap map, StepLoc step, string key, JsonElement value,
            Func<JsonElement, string> encoder = null)
        {
            if (step.Control != null) return PatchMapField(text, step.Control, key, value, map, encoder);
            if (value.ValueKind == JsonValueKind.Null) return text;
            if (map.Def.SchemaVersion < 2)
                throw new PatchException("unpreservable_spelling", "This control needs version 2. Add stabilize_step_ids first, or edit it in Source.", step.Key, key);
            var encoded = (encoder ?? EncodeYaml)(value);
            var fence = CanonicalFence("step", key, encoded, map.Ending);
            var insert = step.Start + FirstLineLength(text, step.Start);
            return text.Insert(insert, EnsureLeadingEnding(text, insert, map.Ending) + fence);
        }

        private static string PatchGateField(string text, SourceMap map, StepLoc step, string key, JsonElement value)
        {
            if (step.Gate != null) return PatchMapField(text, step.Gate, key, value, map);
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) return text;
            var encoded = EncodeYaml(value);
            var fence = CanonicalFence("gate", key, encoded, map.Ending);
            return text.Insert(step.End, EnsureLeadingEnding(text, step.End, map.Ending) + fence);
        }

        private static string CanonicalFence(string kind, string key, string encoded, string ending)
        {
            return "```yaml " + kind + ending + key + ": " + encoded + ending + "```" + ending;
        }

        private static string EnsureLeadingEnding(string text, int at, string ending) =>
            at == 0 || text[at - 1] == (char)10 || text[at - 1] == (char)13 ? "" : ending;

        private static string PatchMapField(string text, YMap map, string key, JsonElement value, SourceMap source,
            Func<JsonElement, string> encoder = null, string emittedKey = null)
        {
            if (map == null) throw new PatchException("source_map_mismatch", $"The YAML map containing '{key}' was not located.", null, key);
            var entry = map.Entry(key);
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (entry == null) return text;
                if (IsFlowMap(source, map))
                    throw new PatchException("unpreservable_spelling", $"The flow-style map containing '{key}' cannot remove one field without rewriting its neighbours. Set the whole container or use Source.", null, key);
                return Replace(text, entry.Start, EntryRemovalEnd(source, entry), "");
            }
            var encoded = (encoder ?? EncodeYaml)(value);
            if (entry == null)
            {
                if (IsFlowMap(source, map))
                    throw new PatchException("unpreservable_spelling", $"The flow-style map containing '{key}' cannot receive a field without rewriting its other values. Set the whole container or use Source.", null, key);
                var line = new string(' ', map.Indent) + (emittedKey ?? key) + ": " + encoded + map.Ending;
                return text.Insert(map.InsertAt, line);
            }
            return Replace(text, entry.Value.Start, entry.Value.End, EncodeForExisting(entry.Value, value, encoded, source));
        }

        private static bool IsFlowMap(SourceMap source, YMap map) => map.Flow;

        private static int EntryRemovalEnd(SourceMap source, YEntry entry)
        {
            var last = Math.Max(entry.Value.Start, entry.Value.End - 1);
            return LineOf(source, last).End + source.Offset;
        }

        private static string EncodeForExisting(YNode node, JsonElement value, string canonical, SourceMap source)
        {
            if (node is YScalar && value.ValueKind == JsonValueKind.String)
                return EncodeStringForExisting(node, value.GetString() ?? "", source.Def.SchemaVersion < 2);
            if (LineOf(source, node.Start).Number != LineOf(source, Math.Max(node.Start, node.End - 1)).Number)
            {
                var indent = new string(' ', Math.Max(0, LineOf(source, node.Start).Text.TakeWhile(char.IsWhiteSpace).Count()));
                return canonical.Contains("\n", StringComparison.Ordinal)
                    ? string.Join(source.Ending, canonical.Split('\n').Select((line, i) => i == 0 ? line : indent + line))
                    : canonical;
            }
            return canonical;
        }

        private static string EncodeStringForExisting(YNode node, string value, bool versionOne = false)
        {
            if (versionOne) return EncodeVersionOneString(node, value);
            if (node is YScalar scalar)
            {
                if (scalar.Style == ScalarStyle.Plain && IsPlainSafe(value)) return value;
                if (scalar.Style == ScalarStyle.SingleQuoted) return "'" + value.Replace("'", "''") + "'";
                if (scalar.Style == ScalarStyle.DoubleQuoted) return JsonSerializer.Serialize(value, JsonOptions);
            }
            return EncodeYaml(JsonSerializer.SerializeToElement(value, JsonOptions));
        }

        private static string EncodeVersionOneString(YNode node, string value)
        {
            if (value.IndexOfAny(new[] { (char)10, (char)13 }) >= 0)
                throw new PatchException("unpreservable_spelling", "Version 1 cannot represent a multi-line value here. Use Source or upgrade this workflow to version 2.");
            if (node is YScalar { Style: ScalarStyle.SingleQuoted } && !value.Contains('\'')) return "'" + value + "'";
            if (node is YScalar { Style: ScalarStyle.DoubleQuoted } && !value.Contains('"')) return "\"" + value + "\"";
            if (IsPlainSafe(value)) return value;
            if (!value.Contains('\'')) return "'" + value + "'";
            if (!value.Contains('"')) return "\"" + value + "\"";
            throw new PatchException("unpreservable_spelling", "Version 1 cannot represent this value without changing it. Use Source or upgrade this workflow to version 2.");
        }

        private static string SetMapEntry(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases)
        {
            var targetKey = RequiredString(operation, "target");
            var field = RequiredString(operation, "field");
            var name = RequiredString(operation, "name");
            var target = Target(map, aliases, targetKey);
            if (!operation.TryGetProperty("expect", out var expect) || !operation.TryGetProperty("value", out var value))
                throw new PatchException("invalid_operation", "set_map_entry requires expect and value.", targetKey, field);
            var duplicate = map.Restrictions.FirstOrDefault(r => r.Target == Resolve(aliases, targetKey)
                && r.Field == name && r.Code == "ambiguous_duplicate");
            if (duplicate != null) throw new PatchException(duplicate.Code, duplicate.Message, targetKey, field);

            YMap bag; Dictionary<string, string> semantic;
            if (target.Kind == "workflow" && field == "provenance")
            {
                semantic = map.Def.Provenance;
                if (!semantic.TryGetValue(name, out var current)) current = null;
                if (!JsonEqual(expect, current)) throw StaleMap(targetKey, field, name);
                if (JsonEqual(value, current)) return text;

                if (map.Def.SchemaVersion < 2)
                {
                    if (V1AuthoredFrontFields.Contains(name))
                        throw new PatchException("invalid_operation", $"'{name}' is an authored workflow field in version 1, not a provenance entry.", targetKey, field);
                    return PatchMapField(text, map.Front, name, value, map, emittedKey: EncodeMapKey(name));
                }

                var provenanceEntry = map.Front.Entry("provenance");
                bag = provenanceEntry?.Value as YMap;
                if (bag == null)
                {
                    if (value.ValueKind == JsonValueKind.Null) return text;
                    if (provenanceEntry?.Value is YScalar { Value: null })
                        return InsertBareMapChild(text, map, provenanceEntry, name, value);
                    if (provenanceEntry != null)
                        throw new PatchException("unpreservable_spelling", "The provenance map cannot receive an entry in its current spelling. Use Source.", targetKey, field);
                    var block = "provenance:" + map.Ending + "  " + EncodeMapKey(name) + ": " + EncodeYaml(value) + map.Ending;
                    return text.Insert(map.Front.InsertAt, block);
                }
            }
            else if (target.Kind == "step" && field == "call.with")
            {
                var call = target.Step.Control?.Entry("call")?.Value as YMap;
                var withEntry = call?.Entry("with");
                bag = withEntry?.Value as YMap;
                semantic = target.Step.Step.Call?.With;
                if (semantic == null) throw new PatchException("invalid_operation", "This step has no call to receive a with entry.", targetKey, field);
                if (!semantic.TryGetValue(name, out var current)) current = null;
                if (!JsonEqual(expect, current)) throw StaleMap(targetKey, field, name);
                if (JsonEqual(value, current)) return text;
                if (bag == null)
                {
                    if (value.ValueKind == JsonValueKind.Null) return text;
                    if (call == null) throw new PatchException("source_map_mismatch", "The call map was not located.", targetKey, field);
                    if (withEntry?.Value is YScalar { Value: null })
                        return InsertBareMapChild(text, map, withEntry, name, value);
                    if (withEntry != null || IsFlowMap(map, call))
                        throw new PatchException("unpreservable_spelling", "The call.with map cannot receive an entry in its current spelling. Set the whole call or use Source.", targetKey, field);
                    var block = new string(' ', call.Indent) + "with:" + map.Ending
                        + new string(' ', call.Indent + 2) + EncodeMapKey(name) + ": " + EncodeYaml(value) + map.Ending;
                    return text.Insert(call.InsertAt, block);
                }
            }
            else throw ReadOnlyOrUnknown(targetKey, field);
            return PatchMapField(text, bag, name, value, map, emittedKey: EncodeMapKey(name));
        }

        private static string InsertBareMapChild(string text, SourceMap source, YEntry entry, string name, JsonElement value)
        {
            var line = LineOf(source, entry.KeyNode.Start);
            var at = line.End + source.Offset;
            var childIndent = LeadingIndent(source, entry.KeyNode.Start) + 2;
            var child = new string(' ', childIndent) + EncodeMapKey(name) + ": " + EncodeYaml(value) + source.Ending;
            return text.Insert(at, EnsureLeadingEnding(text, at, source.Ending) + child);
        }

        private static PatchException StaleMap(string target, string field, string name) =>
            new PatchException("stale_value", $"Map entry '{name}' no longer matches expect. Reproject the draft and retry.", target, field);

        private static string RemoveMapEntry(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases)
        {
            var copy = new Dictionary<string, JsonElement>();
            var target = RequiredString(operation, "target");
            var field = RequiredString(operation, "field");
            var name = RequiredString(operation, "name");
            if (!operation.TryGetProperty("expect", out var expect))
                throw new PatchException("invalid_operation", "remove_map_entry requires expect.", target, field);
            var operationJson = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["target"] = target, ["field"] = field, ["name"] = name,
                ["expect"] = JsonSerializer.Deserialize<object>(expect.GetRawText()), ["value"] = null,
            }, JsonOptions);
            return SetMapEntry(text, map, operationJson, aliases);
        }

        private static string InsertItem(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases,
            List<WorkflowEditKeyChange> keyChanges)
        {
            var targetKey = RequiredString(operation, "target");
            var field = RequiredString(operation, "field");
            var tempKey = RequiredString(operation, "tempKey");
            ValidateTempKey(tempKey, aliases, targetKey, field);
            if (!operation.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                throw new PatchException("invalid_operation", "insert_item requires an object value.", targetKey, field);
            var target = Target(map, aliases, targetKey);
            var after = OptionalString(operation, "after");
            List<ItemLoc> items; YMap owner; string section; string kind;
            if (target.Kind == "workflow" && field == "slots")
            { items = map.Slots; owner = map.Front; section = "slots"; kind = "slot"; }
            else if (target.Kind == "step" && field is "gate.inputs" or "gate.verify")
            {
                items = field == "gate.inputs" ? target.Step.Inputs : target.Step.Verify;
                owner = target.Step.Gate; section = field == "gate.inputs" ? "inputs" : "verify";
                kind = field == "gate.inputs" ? "input" : "verify";
                if (owner == null)
                {
                    var itemText = EmitListItem(value, 2, map.Ending, kind);
                    var fence = "```yaml gate" + map.Ending + section + ":" + map.Ending + itemText + "```" + map.Ending;
                    var changed = text.Insert(target.Step.End, EnsureLeadingEnding(text, target.Step.End, map.Ending) + fence);
                    RegisterAlias(aliases, keyChanges, tempKey, ExpectedInsertedKey(target.Step.Key, kind, value, items));
                    return changed;
                }
            }
            else throw ReadOnlyOrUnknown(targetKey, field);

            var duplicateSection = map.Restrictions.FirstOrDefault(r => r.Target == Resolve(aliases, targetKey)
                && r.Code == "ambiguous_duplicate" && (r.Field == section || r.Field == field));
            if (duplicateSection != null)
                throw new PatchException(duplicateSection.Code, duplicateSection.Message, targetKey, field);

            var sectionEntry = owner.Entry(section);
            if (sectionEntry == null)
            {
                var itemText = EmitListItem(value, owner.Indent + 2, map.Ending, kind);
                var block = new string(' ', owner.Indent) + section + ":" + map.Ending + itemText;
                RegisterAlias(aliases, keyChanges, tempKey,
                    ExpectedInsertedKey(target.Kind == "step" ? target.Step.Key : null, kind, value, items));
                return text.Insert(owner.InsertAt, block);
            }
            if (sectionEntry.Value is YScalar { Value: null })
            {
                var line = LineOf(map, sectionEntry.KeyNode.Start);
                var bareAt = line.End + map.Offset;
                var bareIndent = LeadingIndent(map, sectionEntry.KeyNode.Start) + 2;
                RegisterAlias(aliases, keyChanges, tempKey,
                    ExpectedInsertedKey(target.Kind == "step" ? target.Step.Key : null, kind, value, items));
                return text.Insert(bareAt, EnsureLeadingEnding(text, bareAt, map.Ending) + EmitListItem(value, bareIndent, map.Ending, kind));
            }
            if (sectionEntry.Value is not YSequence sequence)
                throw new PatchException("unpreservable_spelling", $"The {section} list cannot be edited item-by-item in its current spelling. Use Source.", targetKey, field);
            var flow = sequence.Flow;
            if (flow) throw new PatchException("unpreservable_spelling", $"The flow-style {section} list cannot receive an exact item insertion. Use Source or rewrite that field.", targetKey, field);
            var resolvedAfter = Resolve(aliases, after);
            var at = resolvedAfter == null ? (items.Count == 0 ? sectionEntry.End : items[0].Start)
                : items.FirstOrDefault(i => i.Key == resolvedAfter)?.End
                    ?? throw new PatchException("stale_target", $"The requested after target '{resolvedAfter}' is not in {section}.", targetKey, field);
            var indent = items.Count > 0 ? LeadingIndent(map, items[0].Start) : owner.Indent + 2;
            RegisterAlias(aliases, keyChanges, tempKey,
                ExpectedInsertedKey(target.Kind == "step" ? target.Step.Key : null, kind, value, items));
            return text.Insert(at, EmitListItem(value, indent, map.Ending, kind));
        }

        private static string ExpectedInsertedKey(string stepKey, string kind, JsonElement value, List<ItemLoc> existing)
        {
            var identity = value.TryGetProperty(kind == "verify" ? "kind" : "name", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : "item";
            var basis = kind == "slot" ? "slot:" + identity : stepKey + "/" + kind + ":" + identity;
            return UniqueItemKey(basis, existing.Select(item => item.Key));
        }

        private static string MoveItem(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases)
        {
            var targetKey = Resolve(aliases, RequiredString(operation, "target"));
            var target = Target(map, aliases, targetKey);
            if (target.Kind is not ("slot" or "input" or "verify")) throw new PatchException("invalid_operation", "move_item targets a slot, input, or verify item.", targetKey);
            var items = target.Kind == "slot" ? map.Slots : target.Kind == "input" ? target.Step.Inputs : target.Step.Verify;
            CheckOrder(operation, items.Select(i => i.Key), aliases, targetKey);
            var after = Resolve(aliases, OptionalString(operation, "after"));
            if (after == targetKey)
                throw new PatchException("invalid_operation", "An item cannot be moved after itself.", targetKey);
            var order = items.ToList();
            var moving = order.First(i => i.Key == targetKey);
            order.Remove(moving);
            var index = after == null ? 0 : order.FindIndex(i => i.Key == after) + 1;
            if (after != null && index == 0) throw new PatchException("stale_target", $"The after target '{after}' is not a sibling.", targetKey);
            order.Insert(index, moving);
            if (order.Select(i => i.Key).SequenceEqual(items.Select(i => i.Key))) return text;
            EnsureBlockItems(map, items, targetKey);
            var start = items.Min(i => i.Start); var end = items.Max(i => i.End);
            var chunks = items.ToDictionary(i => i.Key, i => text.Substring(i.Start, i.End - i.Start), StringComparer.Ordinal);
            return Replace(text, start, end, string.Concat(order.Select(i => chunks[i.Key])));
        }

        private static string RemoveItem(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases)
        {
            var targetKey = Resolve(aliases, RequiredString(operation, "target"));
            var target = Target(map, aliases, targetKey);
            if (target.Kind is not ("slot" or "input" or "verify")) throw new PatchException("invalid_operation", "remove_item targets a slot, input, or verify item.", targetKey);
            var fingerprint = RequiredString(operation, "expectFingerprint");
            if (target.Item.Fingerprint != fingerprint)
                throw new PatchException("stale_value", "The item no longer matches expectFingerprint. Reproject the draft and retry.", targetKey);
            var siblings = target.Kind == "slot" ? map.Slots : target.Kind == "input" ? target.Step.Inputs : target.Step.Verify;
            EnsureBlockItems(map, siblings, targetKey);
            if (siblings.Count > 1) return Replace(text, target.Item.Start, target.Item.End, "");
            var owner = target.Kind == "slot" ? map.Front : target.Step.Gate;
            var section = target.Kind == "slot" ? "slots" : target.Kind == "input" ? "inputs" : "verify";
            var entry = owner.Entry(section) ?? throw new PatchException("source_map_mismatch", "The item section was not located.", targetKey);
            if (target.Kind != "slot" && target.Step.Step.Ops.Length == 0 && target.Step.Step.Gate != null
                && target.Step.Step.Gate.Strictness == null
                && (target.Kind == "input" ? target.Step.Step.Gate.Verify.Length == 0 : target.Step.Step.Gate.Inputs.Length == 0))
                return Replace(text, target.Step.GateFence.Start, target.Step.GateFence.End, "");
            return Replace(text, entry.Start, EntryRemovalEnd(map, entry), "");
        }

        private static void EnsureBlockItems(SourceMap map, List<ItemLoc> items, string target)
        {
            if (items.Any(i => i.FlowList))
                throw new PatchException("unpreservable_spelling", "This flow-style list cannot be moved or removed item-by-item without rewriting its neighbours. Use Source.", target);
        }

        private static string Stabilize(string name, string text, SourceMap map, Dictionary<string, string> aliases,
            Dictionary<string, string> priorFingerprints, List<WorkflowEditKeyChange> changes)
        {
            if (map.Steps.All(IsStable)) return text;
            var originalFingerprints = map.Steps.ToDictionary(step => step.Key, step => step.Fingerprint, StringComparer.Ordinal);
            var candidate = text;
            if (map.Def.SchemaVersion < 2)
            {
                var document = new WorkflowDocumentResult
                {
                    Name = name, Library = "user", ExactText = text,
                    Metadata = new WorkflowDocumentMetadata
                    {
                        Parses = map.Def.Error == null, ParseError = map.Def.Error, SchemaVersion = map.Def.SchemaVersion,
                    },
                };
                var upgrade = WorkflowUpgrade.Preview(document, true);
                if (upgrade.ParseError != null || string.Equals(upgrade.ProposedText, text, StringComparison.Ordinal))
                    throw new PatchException("unpreservable_spelling", upgrade.Reason ?? "This version 1 source cannot be stabilized without changing opaque content.", "workflow", "schemaVersion");
                candidate = upgrade.ProposedText;
            }
            var def = Parse(candidate);
            if (def.Error != null) throw new PatchException("unpreservable_spelling", def.Error, "workflow", "schemaVersion");
            var current = Build(candidate, def);
            var used = current.Steps.Where(IsStable).Select(s => s.Step.Id).ToHashSet(StringComparer.Ordinal);
            var replacements = new List<(int Start, int End, string Value, StepLoc Step)>();
            foreach (var step in current.Steps.Where(s => !IsStable(s)))
            {
                var id = StableId(step.Step.Title, used);
                used.Add(id);
                var idEntry = step.Control?.Entry("id");
                if (idEntry == null)
                {
                    var at = step.Control == null ? step.Start + FirstLineLength(candidate, step.Start) : step.Control.InsertAt;
                    var fence = step.Control == null
                        ? EnsureLeadingEnding(candidate, at, current.Ending) + CanonicalFence("step", "id", id, current.Ending)
                        : new string(' ', step.Control.Indent) + "id: " + id + step.Control.Ending;
                    replacements.Add((at, at, fence, step));
                }
                else replacements.Add((idEntry.Value.Start, idEntry.Value.End, EncodeStringForExisting(idEntry.Value, id), step));
                var oldKey = step.Key; var newKey = "step:" + id;
                aliases[oldKey] = newKey;
                priorFingerprints[newKey] = originalFingerprints.TryGetValue(oldKey, out var originalFingerprint)
                    ? originalFingerprint : step.Fingerprint;
                changes.Add(new WorkflowEditKeyChange { OldKey = oldKey, NewKey = newKey, OldStepId = step.Step.Id, NewStepId = id });
            }
            foreach (var replacement in replacements.OrderByDescending(r => r.Start))
                candidate = Replace(candidate, replacement.Start, replacement.End, replacement.Value);
            var stableDef = Parse(candidate);
            if (stableDef.Error != null)
                throw new PatchException("unpreservable_spelling", stableDef.Error, "workflow", "schemaVersion");
            return candidate;
        }

        private static bool IsStable(StepLoc step) => step.Step.HasExplicitId && !PositionalStepId.IsMatch(step.Step.Id ?? "");

        private static string StableId(string title, HashSet<string> used)
        {
            var words = Regex.Matches((title ?? "").ToLowerInvariant(), "[a-z0-9]+")
                .Cast<Match>().Select(m => m.Value).Where(x => x.Length > 0).ToArray();
            var basis = words.Length == 0 ? "untitled" : string.Join("-", words);
            if (!char.IsLetter(basis[0])) basis = "do-" + basis;
            var id = basis; var n = 2;
            while (used.Contains(id) || PositionalStepId.IsMatch(id)) id = basis + "-" + n++;
            return id;
        }

        private static string AddStep(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases,
            List<WorkflowEditKeyChange> keyChanges)
        {
            var after = Resolve(aliases, OptionalString(operation, "after"));
            var tempKey = RequiredString(operation, "tempKey");
            ValidateTempKey(tempKey, aliases, "workflow", "steps");
            if (!operation.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                throw new PatchException("invalid_operation", "add_step requires an object value.");
            RequireStableForStructure(map, null, after != null || map.Steps.Count > 0);
            var id = value.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            ValidateNewId(map, id);
            var order = map.Steps.Select(s => (Key: s.Key, Slice: text.Substring(s.Start, s.End - s.Start))).ToList();
            var slice = EmitStep(value, map.Ending);
            var index = after == null ? 0 : order.FindIndex(x => x.Key == after) + 1;
            if (after != null && index == 0) throw new PatchException("stale_target", $"The after step '{after}' is not in this draft.");
            order.Insert(index, ("step:" + id, slice));
            RegisterAlias(aliases, keyChanges, tempKey, "step:" + id, newStepId: id);
            return RebuildSteps(text, map, order);
        }

        private static string CopyStep(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases,
            List<WorkflowEditKeyChange> keyChanges)
        {
            var targetKey = Resolve(aliases, RequiredString(operation, "target"));
            var target = Target(map, aliases, targetKey);
            if (target.Kind != "step") throw new PatchException("invalid_operation", "copy_step targets a step.", targetKey);
            RequireStableForStructure(map, null, true);
            var after = Resolve(aliases, OptionalString(operation, "after"));
            var newId = RequiredString(operation, "newId");
            var tempKey = RequiredString(operation, "tempKey");
            ValidateTempKey(tempKey, aliases, targetKey, "steps");
            ValidateNewId(map, newId);
            var slice = text.Substring(target.Step.Start, target.Step.End - target.Step.Start);
            var idEntry = target.Step.Control?.Entry("id")
                ?? throw new PatchException("source_map_mismatch", "The copied step's stable id was not located.", targetKey, "id");
            slice = Replace(slice, idEntry.Value.Start - target.Step.Start, idEntry.Value.End - target.Step.Start,
                EncodeStringForExisting(idEntry.Value, newId));
            var order = map.Steps.Select(s => (Key: s.Key, Slice: text.Substring(s.Start, s.End - s.Start))).ToList();
            var index = after == null ? 0 : order.FindIndex(x => x.Key == after) + 1;
            if (after != null && index == 0) throw new PatchException("stale_target", $"The after step '{after}' is not in this draft.");
            order.Insert(index, ("step:" + newId, slice));
            RegisterAlias(aliases, keyChanges, tempKey, "step:" + newId, newStepId: newId);
            return RebuildSteps(text, map, order);
        }

        private static string MoveStep(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases)
        {
            var targetKey = Resolve(aliases, RequiredString(operation, "target"));
            var target = Target(map, aliases, targetKey);
            if (target.Kind != "step") throw new PatchException("invalid_operation", "move_step targets a step.", targetKey);
            RequireStableForStructure(map, null, true);
            CheckOrder(operation, map.Steps.Select(s => s.Key), aliases, targetKey);
            var after = Resolve(aliases, OptionalString(operation, "after"));
            if (after == targetKey) throw new PatchException("invalid_operation", "A step cannot be moved after itself.", targetKey);
            var order = map.Steps.Select(s => (Key: s.Key, Slice: text.Substring(s.Start, s.End - s.Start))).ToList();
            var moving = order.First(x => x.Key == targetKey); order.Remove(moving);
            var index = after == null ? 0 : order.FindIndex(x => x.Key == after) + 1;
            if (after != null && index == 0) throw new PatchException("stale_target", $"The after step '{after}' is not in this draft.", targetKey);
            order.Insert(index, moving);
            if (order.Select(x => x.Key).SequenceEqual(map.Steps.Select(s => s.Key))) return text;
            return RebuildSteps(text, map, order);
        }

        private static string RemoveStep(string text, SourceMap map, JsonElement operation, Dictionary<string, string> aliases,
            Dictionary<string, string> priorFingerprints)
        {
            var requestedKey = RequiredString(operation, "target");
            var targetKey = Resolve(aliases, requestedKey);
            var target = Target(map, aliases, targetKey);
            if (target.Kind != "step") throw new PatchException("invalid_operation", "remove_step targets a step.", targetKey);
            var fingerprint = RequiredString(operation, "expectFingerprint");
            if (target.Step.Fingerprint != fingerprint
                && (!priorFingerprints.TryGetValue(targetKey, out var prior) || prior != fingerprint))
                throw new PatchException("stale_value", "The step no longer matches expectFingerprint. Reproject the draft and retry.", targetKey);
            RequireStableForStructure(map, target.Step, true);
            var order = map.Steps.Where(s => s != target.Step)
                .Select(s => (Key: s.Key, Slice: text.Substring(s.Start, s.End - s.Start))).ToList();
            return RebuildSteps(text, map, order);
        }

        private static void RequireStableForStructure(SourceMap map, StepLoc removed, bool structural)
        {
            if (structural && map.Steps.Any(s => s != removed && !IsStable(s)))
                throw new PatchException("unstable_step_ids", "This structural edit needs stabilize_step_ids earlier in the same preview so step addresses survive the change.", "workflow", "steps");
        }

        private static void RegisterAlias(Dictionary<string, string> aliases, List<WorkflowEditKeyChange> changes,
            string oldKey, string newKey, string oldStepId = null, string newStepId = null)
        {
            var oldResolved = Resolve(aliases, oldKey);
            var newResolved = Resolve(aliases, newKey);
            if (aliases.ContainsKey(newKey) && !string.Equals(newResolved, oldResolved, StringComparison.Ordinal))
                throw new PatchException("stale_target", $"Edit key '{newKey}' already identifies another item in this edit list. Reproject the draft and retry.", newKey);
            var redirects = aliases.Keys
                .Where(alias => string.Equals(Resolve(aliases, alias), oldResolved, StringComparison.Ordinal))
                .ToArray();
            aliases.Remove(newKey);
            foreach (var alias in redirects)
                if (!string.Equals(alias, newKey, StringComparison.Ordinal)) aliases[alias] = newKey;
            if (!string.Equals(oldResolved, newKey, StringComparison.Ordinal)) aliases[oldResolved] = newKey;
            changes.Add(new WorkflowEditKeyChange
            {
                OldKey = oldKey, NewKey = newKey, OldStepId = oldStepId, NewStepId = newStepId,
            });
        }

        private static void ValidateTempKey(string tempKey, Dictionary<string, string> aliases, string target, string field)
        {
            if (!tempKey.StartsWith("new:", StringComparison.Ordinal) || tempKey.Length == 4)
                throw new PatchException("invalid_operation", "tempKey must start with 'new:' and include a client id.", target, field);
            if (aliases.ContainsKey(tempKey))
                throw new PatchException("invalid_operation", $"tempKey '{tempKey}' is already used in this edit list.", target, field);
        }

        private static void ValidateNewId(SourceMap map, string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !StableStepId.IsMatch(id) || PositionalStepId.IsMatch(id))
                throw new PatchException("invalid_operation", $"New step id '{id ?? "(missing)"}' must be a unique non-positional lower-kebab id.", "workflow", "steps");
            if (map.Steps.Any(s => string.Equals(s.Step.Id, id, StringComparison.Ordinal)))
                throw new PatchException("invalid_operation", $"Step id '{id}' is already used.", "workflow", "steps");
        }

        private static string RebuildSteps(string text, SourceMap map, List<(string Key, string Slice)> order)
        {
            var start = map.Steps.Count > 0 ? map.Steps[0].Start : text.Length;
            var end = map.Steps.Count > 0 ? map.Steps[map.Steps.Count - 1].End : text.Length;
            var rebuilt = new StringBuilder();
            for (var i = 0; i < order.Count; i++)
            {
                if (rebuilt.Length > 0 && rebuilt[rebuilt.Length - 1] != (char)10 && rebuilt[rebuilt.Length - 1] != (char)13)
                    rebuilt.Append(map.Ending);
                rebuilt.Append(RenumberHeading(order[i].Slice, i + 1));
            }
            var hadFinalEnding = text.Length > 0 && (text[text.Length - 1] == (char)10 || text[text.Length - 1] == (char)13);
            if (!hadFinalEnding && rebuilt.Length >= map.Ending.Length
                && rebuilt.ToString().EndsWith(map.Ending, StringComparison.Ordinal))
                rebuilt.Length -= map.Ending.Length;
            return Replace(text, start, end, rebuilt.ToString());
        }

        private static string RenumberHeading(string slice, int number)
        {
            var lf = slice.IndexOf((char)10);
            var first = lf < 0 ? slice : slice.Substring(0, lf);
            var m = Heading.Match(first.TrimEnd((char)13));
            if (!m.Success) throw new PatchException("source_map_mismatch", "A moved step heading could not be renumbered.");
            return Replace(slice, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length, number.ToString());
        }

        private static string EmitStep(JsonElement value, string ending)
        {
            RequireOnlyProperties(value, "add_step value", "id", "title", "instructions", "ops", "when", "forEach", "call", "gate");
            var id = RequiredString(value, "id");
            var title = value.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : "Untitled";
            if ((title ?? "").IndexOfAny(new[] { (char)10, (char)13 }) >= 0)
                throw new PatchException("invalid_operation", "A step title must stay on one heading line.", "workflow", "steps");
            var instructions = value.TryGetProperty("instructions", out var instructionsNode) && instructionsNode.ValueKind == JsonValueKind.String
                ? NormalizeEndings(instructionsNode.GetString() ?? "", ending) : "";
            var b = new StringBuilder();
            b.Append("## Step 0: ").Append(title ?? "Untitled").Append(ending);
            b.Append("```yaml step").Append(ending).Append("id: ").Append(id).Append(ending);
            if (value.TryGetProperty("when", out var when) && when.ValueKind != JsonValueKind.Null) b.Append("when: ").Append(EncodeYaml(when)).Append(ending);
            if (value.TryGetProperty("forEach", out var each) && each.ValueKind != JsonValueKind.Null) b.Append("forEach: ").Append(EditForEachYaml(each)).Append(ending);
            if (value.TryGetProperty("call", out var call) && call.ValueKind != JsonValueKind.Null) b.Append("call: ").Append(EditCallYaml(call)).Append(ending);
            b.Append("```").Append(ending);
            if (instructions.Length > 0) b.Append(instructions).Append(instructions.EndsWith(ending, StringComparison.Ordinal) ? "" : ending);
            if (value.TryGetProperty("ops", out var ops) && ops.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new PatchException("invalid_operation", "add_step ops must be an array or null.", "workflow", "steps");
            var hasOps = value.TryGetProperty("ops", out ops) && ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() > 0;
            var hasGate = value.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.Object;
            if (value.TryGetProperty("gate", out gate) && gate.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new PatchException("invalid_operation", "add_step gate must be an object or null.", "workflow", "steps");
            if (hasGate) RequireOnlyProperties(gate, "add_step gate", "strictness", "inputs", "verify");
            JsonElement strict = default, inputs = default, verify = default;
            var hasStrictness = hasGate && gate.TryGetProperty("strictness", out strict) && strict.ValueKind != JsonValueKind.Null;
            if (hasGate && gate.TryGetProperty("inputs", out inputs) && inputs.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new PatchException("invalid_operation", "add_step gate.inputs must be an array or null.", "workflow", "steps");
            var hasInputs = hasGate && gate.TryGetProperty("inputs", out inputs)
                && inputs.ValueKind == JsonValueKind.Array && inputs.GetArrayLength() > 0;
            if (hasGate && gate.TryGetProperty("verify", out verify) && verify.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new PatchException("invalid_operation", "add_step gate.verify must be an array or null.", "workflow", "steps");
            var hasVerify = hasGate && gate.TryGetProperty("verify", out verify)
                && verify.ValueKind == JsonValueKind.Array && verify.GetArrayLength() > 0;
            if (hasOps || hasStrictness || hasInputs || hasVerify)
            {
                b.Append("```yaml gate").Append(ending);
                if (hasOps) b.Append("ops: ").Append(EncodeYaml(ops)).Append(ending);
                if (hasStrictness) b.Append("strictness: ").Append(EncodeYaml(strict)).Append(ending);
                if (hasInputs)
                {
                    b.Append("inputs:").Append(ending);
                    foreach (var input in inputs.EnumerateArray()) b.Append(EmitListItem(input, 2, ending, "input"));
                }
                if (hasVerify)
                {
                    b.Append("verify:").Append(ending);
                    foreach (var item in verify.EnumerateArray()) b.Append(EmitListItem(item, 2, ending, "verify"));
                }
                b.Append("```").Append(ending);
            }
            return b.ToString();
        }

        private static string EmitListItem(JsonElement value, int indent, string ending, string kind)
        {
            var order = kind == "slot"
                ? new[] { "name", "question", "type", "required", "default", "example", "hint", "values" }
                : kind == "input"
                    ? new[] { "name", "question", "type", "required", "daxPurity", "scope" }
                    : new[] { "kind", "when", "probe", "scope", "intent", "pinnedShapes", "openShapes", "openShapesFrom", "openMismatch", "anchors" };
            RequireOnlyProperties(value, "new " + kind, order);
            var props = order.Where(name => value.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null).ToArray();
            if (props.Length == 0) throw new PatchException("invalid_operation", $"A new {kind} needs its required identity field.");
            var b = new StringBuilder();
            for (var i = 0; i < props.Length; i++)
            {
                var v = value.GetProperty(props[i]);
                b.Append(new string(' ', indent)).Append(i == 0 ? "- " : "  ").Append(props[i]).Append(": ").Append(EncodeYaml(v)).Append(ending);
            }
            return b.ToString();
        }

        private static string EditForEachYaml(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new PatchException("invalid_operation", "forEach must be an object or null.");
            RequireOnlyProperties(value, "forEach", "source", "as", "maxIterations");
            var pieces = new List<string>();
            if (value.TryGetProperty("source", out var source)) pieces.Add("in: " + EditLoopSourceYaml(source));
            if (value.TryGetProperty("as", out var asNode)) pieces.Add("as: " + EncodeYaml(asNode));
            if (value.TryGetProperty("maxIterations", out var max)) pieces.Add("maxIterations: " + EncodeYaml(max));
            return "{ " + string.Join(", ", pieces) + " }";
        }

        private static string EditLoopSourceYaml(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new PatchException("invalid_operation", "forEach.source must be an object.");
            RequireOnlyProperties(value, "forEach.source", "kind", "values", "name");
            var kind = RequiredString(value, "kind");
            if (kind == "literal") return value.TryGetProperty("values", out var values) ? EncodeYaml(values) : "[]";
            if (kind == "input") return "inputs." + RequiredString(value, "name");
            throw new PatchException("invalid_operation", "forEach.source.kind must be 'literal' or 'input'.");
        }

        private static string EditCallYaml(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new PatchException("invalid_operation", "call must be an object or null.");
            RequireOnlyProperties(value, "call", "workflow", "with", "returns");
            var pieces = new List<string>();
            if (value.TryGetProperty("workflow", out var workflow)) pieces.Add("workflow: " + EncodeYaml(workflow));
            if (value.TryGetProperty("with", out var with) && with.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new PatchException("invalid_operation", "call.with must be an array or null.");
            if (value.TryGetProperty("with", out with) && with.ValueKind == JsonValueKind.Array)
            {
                var entries = with.EnumerateArray().Select(e =>
                {
                    RequireOnlyProperties(e, "call.with entry", "key", "name", "value");
                    return EncodeMapKey(RequiredString(e, "name")) + ": " + EncodeYaml(e.GetProperty("value"));
                });
                pieces.Add("with: { " + string.Join(", ", entries) + " }");
            }
            return value.TryGetProperty("returns", out var returns)
                ? "{ " + string.Join(", ", pieces.Append("returns: " + EncodeYaml(returns))) + " }"
                : "{ " + string.Join(", ", pieces) + " }";
        }

        private static string EncodeYaml(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => "null",
                JsonValueKind.String => IsPlainSafe(value.GetString() ?? "") ? value.GetString() : JsonSerializer.Serialize(value.GetString(), JsonOptions),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(EncodeFlowItem)) + "]",
                JsonValueKind.Object => "{ " + string.Join(", ", value.EnumerateObject().Select(p => p.Name + ": " + EncodeYaml(p.Value))) + " }",
                _ => throw new PatchException("invalid_operation", "This JSON value cannot be written as workflow YAML."),
            };
        }

        private static string EncodeFlowItem(JsonElement value) =>
            value.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(value.GetString(), JsonOptions) : EncodeYaml(value);

        private static string EncodeMapKey(string value) =>
            IsPlainSafe(value) && !value.Contains(':') ? value : JsonSerializer.Serialize(value, JsonOptions);

        private static bool IsPlainSafe(string value)
        {
            if (string.IsNullOrEmpty(value) || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])) return false;
            if (value is "null" or "Null" or "NULL" or "true" or "false" or "True" or "False" or "~") return false;
            if (value.StartsWith("- ", StringComparison.Ordinal) || value.StartsWith("? ", StringComparison.Ordinal) || value.StartsWith(": ", StringComparison.Ordinal)) return false;
            if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0])) return false;
            return !value.Contains(" #", StringComparison.Ordinal) && !value.Contains(": ", StringComparison.Ordinal)
                && value.IndexOfAny(new[] { (char)10, (char)13, (char)0 }) < 0;
        }

        private static bool JsonEqual(JsonElement element, object value)
        {
            var right = JsonSerializer.SerializeToElement(value, JsonOptions);
            return JsonElement.DeepEquals(element, right);
        }

        private static string JsonString(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null) return "";
            if (value.ValueKind != JsonValueKind.String) throw new PatchException("invalid_operation", "This field takes text.");
            return value.GetString() ?? "";
        }

        private static void RequireOnlyProperties(JsonElement element, string context, params string[] allowed)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new PatchException("invalid_operation", context + " must be a JSON object.");
            var names = allowed.ToHashSet(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Contains(property.Name))
                    throw new PatchException("invalid_operation", $"{context} has unknown field '{property.Name}'. Allowed fields: {string.Join(", ", allowed)}.");
        }

        private static string RequiredString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
                throw new PatchException("invalid_operation", $"Workflow edit operation requires a non-empty '{property}' string.");
            return value.GetString();
        }

        private static string OptionalString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new PatchException("invalid_operation", $"'{property}' must be a string or null.");
            return value.GetString();
        }

        private static void CheckOrder(JsonElement operation, IEnumerable<string> actual, Dictionary<string, string> aliases, string target)
        {
            if (!operation.TryGetProperty("expectOrder", out var order) || order.ValueKind != JsonValueKind.Array)
                throw new PatchException("invalid_operation", "This move requires expectOrder.", target);
            var expected = order.EnumerateArray().Select(x => Resolve(aliases, x.GetString())).ToArray();
            if (!expected.SequenceEqual(actual))
                throw new PatchException("stale_value", "The item order no longer matches expectOrder. Reproject the draft and retry.", target);
        }

        private static int LeadingIndent(SourceMap map, int absolute)
        {
            var line = LineOf(map, absolute);
            return line.Text.TakeWhile(char.IsWhiteSpace).Count();
        }

        private static int ColumnOf(SourceMap map, int absolute)
        {
            var line = LineOf(map, absolute);
            return Math.Max(0, absolute - map.Offset - line.Start);
        }

        private static string Replace(string text, int start, int end, string value) =>
            text.Substring(0, start) + value + text.Substring(end);

        private static string PreserveEnvelope(string original, string candidate)
        {
            if (original.Length > 0 && original[0] == (char)0xfeff && (candidate.Length == 0 || candidate[0] != (char)0xfeff))
                candidate = (char)0xfeff + candidate;
            if ((original.Length == 0 || original[0] != (char)0xfeff) && candidate.Length > 0 && candidate[0] == (char)0xfeff)
                candidate = candidate.Substring(1);
            var originalEnding = FinalEnding(original);
            var candidateEnding = FinalEnding(candidate);
            if (originalEnding.Length == 0 && candidateEnding.Length > 0)
                candidate = candidate.Substring(0, candidate.Length - candidateEnding.Length);
            else if (originalEnding.Length > 0 && candidateEnding.Length == 0)
                candidate += originalEnding;
            else if (originalEnding.Length > 0 && candidateEnding != originalEnding)
                candidate = candidate.Substring(0, candidate.Length - candidateEnding.Length) + originalEnding;
            return candidate;
        }

        private static string FinalEnding(string text)
        {
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) return "\r\n";
            if (text.EndsWith("\n", StringComparison.Ordinal)) return "\n";
            if (text.EndsWith("\r", StringComparison.Ordinal)) return "\r";
            return "";
        }

        internal static string Diff(string name, string before, string after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return "";
            var left = DiffLines(before ?? "");
            var right = DiffLines(after ?? "");
            var prefix = 0;
            while (prefix < left.Count && prefix < right.Count && left[prefix] == right[prefix]) prefix++;
            var suffix = 0;
            while (suffix < left.Count - prefix && suffix < right.Count - prefix
                && left[left.Count - 1 - suffix] == right[right.Count - 1 - suffix]) suffix++;
            var lf = ((char)10).ToString();
            var result = new StringBuilder();
            result.Append("--- a/").Append(name).Append(".md").Append(lf)
                .Append("+++ b/").Append(name).Append(".md").Append(lf)
                .Append("@@ -1,").Append(left.Count).Append(" +1,").Append(right.Count).Append(" @@").Append(lf);
            void Append(char kind, DiffTextLine line)
            {
                result.Append(kind).Append(line.Text);
                if (!line.Terminated)
                    result.Append(lf).Append((char)92).Append(" No newline at end of file").Append(lf);
            }
            for (var i = 0; i < prefix; i++) Append(' ', left[i]);
            for (var i = prefix; i < left.Count - suffix; i++) Append('-', left[i]);
            for (var i = prefix; i < right.Count - suffix; i++) Append('+', right[i]);
            for (var i = left.Count - suffix; i < left.Count; i++) Append(' ', left[i]);
            return result.ToString();
        }

        private static List<DiffTextLine> DiffLines(string text)
        {
            var lines = new List<DiffTextLine>();
            for (var start = 0; start < text.Length;)
            {
                var lf = text.IndexOf((char)10, start);
                if (lf < 0) { lines.Add(new DiffTextLine(text.Substring(start), false)); break; }
                lines.Add(new DiffTextLine(text.Substring(start, lf + 1 - start), true));
                start = lf + 1;
            }
            return lines;
        }

        private static string NormalizeEndings(string value, string ending)
        {
            return (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", ending);
        }
    }
}
