// The Studio wire, engine half (1.4 "Claude drives, the Studio shows"; contract sections 1, 4, 5 and 6).
//
// The assistant says what it is doing (show_in_studio) and what it would do next (suggest_next). The engine checks the words,
// keeps the last notes of the session, and tells the Studio over the pipe. The engine never moves a screen: the Studio's
// driver store decides that, from the driver state the Studio reports back (SetStudioDriverAsync). Nothing here touches the
// model, its files or the settings, and none of it is written to History.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal const int StudioSayMax = 200;
        internal const int StudioNoteRing = 100;
        internal const string StudioDefaultSuggestSay = "Here is what I would do next.";

        private readonly object _studioGate = new object();
        private readonly Dictionary<string, List<StudioNote>> _studioNotes = new Dictionary<string, List<StudioNote>>(StringComparer.Ordinal);
        private readonly Dictionary<string, StudioSuggestion> _studioSuggestion = new Dictionary<string, StudioSuggestion>(StringComparer.Ordinal);
        // What the Studio last reported about its own screen. Until it reports, the defaults are the ones a fresh Studio starts in.
        private string _studioMode = StudioMaps.ClaudeDrives;
        // Follow (1.4): the engine narrates a tool call the assistant did not narrate itself.
        /// <summary>The clock the Studio's notes use. A test moves it; nothing else does.</summary>
        internal Func<DateTime> StudioClock { get; set; } = () => DateTime.UtcNow;
        internal static readonly TimeSpan StudioExplicitWins = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan StudioBurstGap = TimeSpan.FromSeconds(20);
        private DateTime _studioLastShow = DateTime.MinValue;
        private string _studioLastShowPress;
        private string _studioLastShowPlace;
        private AutoRun _studioRun;
        private readonly Dictionary<string, StudioAnswer> _studioAnswer = new Dictionary<string, StudioAnswer>(StringComparer.Ordinal);
        private readonly HashSet<string> _studioProNotices = new HashSet<string>(StringComparer.Ordinal);

        private sealed class AutoRun { public string NoteId; public string Say; public string Place; public int Count; public DateTime LastAt; }
        private string _studioState = StudioMaps.Idle;

        public Task<StudioShowResult> ShowInStudioAsync(StudioShowRequest r)
        {
            var place = StudioMaps.FindPlace(r?.Place?.Trim());
            if (place == null)
                throw new ArgumentException($"'{r?.Place}' is not a place the Studio knows. Use one of: {string.Join(", ", StudioMaps.PlaceIds)}.");
            var say = (r.Say ?? "").Trim();
            if (say.Length == 0)
                throw new ArgumentException($"say is required: one plain sentence for the person, 1 to {StudioSayMax} characters.");
            if (say.Length > StudioSayMax)
                throw new ArgumentException($"say is {say.Length} characters and the limit is {StudioSayMax}. Cut it to one plain sentence.");
            var focus = string.IsNullOrWhiteSpace(r.Focus) ? null : r.Focus.Trim();
            if (focus != null && focus.IndexOf(':') < 1)
                throw new ArgumentException($"focus '{focus}' is not an object ref. Use the form measure:Sales/Net Sales, table:Sales or model:.");
            var press = string.IsNullOrWhiteSpace(r.Press) ? null : r.Press.Trim();
            if (press != null && !StudioMaps.PressIds.Contains(press, StringComparer.Ordinal))
                throw new ArgumentException($"'{press}' is not a control the Studio can show pressed. Use one of: {string.Join(", ", StudioMaps.PressIds)}.");

            var sessionId = _sessions.SessionId;
            var note = new StudioNote
            {
                Id = "sn-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Say = say, How = place.How, Place = place.Id, Focus = focus, Press = press,
                At = StudioClock().ToString("o"), By = "assistant",
                Call = HealthCorrelation.CurrentId, SessionId = sessionId,
            };
            lock (_studioGate)
            {
                _studioLastShow = StudioClock();   // an explicit show speaks for itself: the engine stays quiet for a while
                _studioLastShowPress = press;
                _studioLastShowPlace = place.Id;
                _studioRun = null;
                var key = sessionId ?? "";
                if (!_studioNotes.TryGetValue(key, out var ring)) _studioNotes[key] = ring = new List<StudioNote>();
                ring.Add(note);
                if (ring.Count > StudioNoteRing) ring.RemoveRange(0, ring.Count - StudioNoteRing);
            }
            _sessions.Bus.PublishStudioShow(note);
            return Task.FromResult(new StudioShowResult { Id = note.Id, Place = place.Id, How = place.How, Screen = ScreenFor() });
        }

        // "moved" only when a Studio is attached, the mode is Claude drives and the person has not taken the wheel.
        private string ScreenFor()
        {
            if (_sessions.Bus.HumanClientAttached?.Invoke() != true) return "noStudio";
            lock (_studioGate)
                return _studioMode == StudioMaps.ClaudeDrives && _studioState != StudioMaps.PersonHasWheel ? "moved" : "noted";
        }

        public Task<StudioSuggestResult> SuggestNextAsync(StudioSuggestRequest r)
        {
            var steps = (r?.Steps ?? Array.Empty<StudioStep>()).Where(s => s != null).ToArray();
            if (steps.Length < 2 || steps.Length > 3)
                throw new ArgumentException($"steps must hold 2 or 3 next steps; you sent {steps.Length}.");
            var clean = new List<StudioStep>();
            foreach (var s in steps)
            {
                var label = (s.Label ?? "").Trim();
                var prompt = (s.Prompt ?? "").Trim();
                if (label.Length < 3 || label.Length > 40)
                    throw new ArgumentException($"A step label is 3 to 40 characters; '{label}' is {label.Length}.");
                if (!char.IsLetter(label[0]) || label.EndsWith("."))
                    throw new ArgumentException($"A step label starts with a verb and has no full stop; '{label}' does not.");
                if (prompt.Length < 1 || prompt.Length > 280)
                    throw new ArgumentException($"A step prompt is 1 to 280 characters, the words the person would type to you; the one for '{label}' is {prompt.Length}.");
                clean.Add(new StudioStep { Label = label, Prompt = prompt });
            }
            var say = string.IsNullOrWhiteSpace(r.Say) ? StudioDefaultSuggestSay : r.Say.Trim();
            if (say.Length > StudioSayMax)
                throw new ArgumentException($"say is {say.Length} characters and the limit is {StudioSayMax}.");
            var sessionId = _sessions.SessionId;
            var suggestion = new StudioSuggestion
            {
                Id = "sg-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Say = say, Steps = clean.ToArray(), At = DateTime.UtcNow.ToString("o"), SessionId = sessionId,
            };
            lock (_studioGate) _studioSuggestion[sessionId ?? ""] = suggestion;   // a new one replaces the last
            _sessions.Bus.PublishStudioSuggest(suggestion);
            return Task.FromResult(new StudioSuggestResult { Id = suggestion.Id, Shown = true });
        }

        public Task<StudioNotesView> GetStudioNotesAsync(string sessionId)
        {
            var key = string.IsNullOrEmpty(sessionId) ? _sessions.SessionId : sessionId;
            lock (_studioGate)
            {
                _studioNotes.TryGetValue(key ?? "", out var ring);
                _studioSuggestion.TryGetValue(key ?? "", out var suggestion);
                return Task.FromResult(new StudioNotesView
                {
                    SessionId = key,
                    Notes = ring?.ToArray() ?? Array.Empty<StudioNote>(),
                    Suggestion = suggestion,
                    Answer = _studioAnswer.TryGetValue(key ?? "", out var answer) ? answer : null,
                });
            }
        }

        public Task SetStudioDriverAsync(string mode, string state)
        {
            if (mode != StudioMaps.ClaudeDrives && mode != StudioMaps.IDrive)
                throw new ArgumentException($"mode '{mode}' is not one of {StudioMaps.ClaudeDrives}, {StudioMaps.IDrive}.");
            if (state != StudioMaps.Idle && state != StudioMaps.ClaudeDriving && state != StudioMaps.PersonHasWheel)
                throw new ArgumentException($"state '{state}' is not one of {StudioMaps.Idle}, {StudioMaps.ClaudeDriving}, {StudioMaps.PersonHasWheel}.");
            lock (_studioGate) { _studioMode = mode; _studioState = state; }
            return Task.CompletedTask;
        }

        // The assistant behind the MCP door, mapped once from its clientInfo (AssistantIdentity). A later call that names the same client
        // changes nothing; a call that names none keeps what was read; a different client replaces it.
        private AssistantInfo _assistant;
        private string _assistantClient;

        internal AssistantInfo CurrentAssistant { get { lock (_studioGate) return _assistant; } }

        private AssistantInfo ReadAssistant(string client, string version)
        {
            lock (_studioGate)
            {
                if (!string.IsNullOrWhiteSpace(client))
                {
                    var raw = client.Trim() + "|" + (version ?? "");
                    if (raw != _assistantClient) { _assistantClient = raw; _assistant = AssistantIdentity.Map(client, version); }
                }
                return _assistant;
            }
        }

        public Task NoteAgentCallAsync(string tool, string focus = null, string client = null, string clientVersion = null, bool? commit = null)
        {
            _sessions.Bus.PublishAgentCall(new AgentCallEvent
            {
                SessionId = _sessions.SessionId, Call = HealthCorrelation.CurrentId, Tool = tool, At = DateTime.UtcNow.ToString("o"),
                Assistant = ReadAssistant(client, clientVersion),
            });
            Follow(tool, focus, commit);
            return Task.CompletedTask;
        }

        /// <summary>Follow (1.4): the assistant made 190 calls and 2 shows, so the engine narrates for it. A call whose tool the place map lists
        /// adds one automatic note when no show spoke for that place in the last 10 seconds and the place or object differs from the last note's;
        /// a call that goes to another place always gets its note, however soon after the last (the Studio paces the moves, not the engine); a burst of the same kind on one place updates that note's count instead of adding more. An explicit show
        /// always wins. Nothing here moves a screen: the Studio's driver store decides that.
        /// Two kinds of call always get their own note (hero15): a call that presses a control on screen (the press map), so the Studio's cursor
        /// draws on it, unless the assistant's show already named that press; and a call that changes the plan (Always in the place map).</summary>
        private void Follow(string tool, string focus, bool? commit = null)
        {
            // These calls narrate their measured result after they commit, including rejected items and no-ops.
            if (tool is "waive_findings" or "write_words" or "edit_objects" or "create_measures" or "delete_objects" or "unwaive_findings" or "apply_fixes" or "duplicate_objects" or "set_doc_sections" or "set_perspective_members" or "set_object_permissions" or "set_plan_items" or "add_plan_items" or "edit_start_plans" or "plan_rows") return;
            var say = StudioMaps.AutoFor(tool);
            if (say == null || _sessions.Bus.HumanClientAttached?.Invoke() != true) return;
            focus = string.IsNullOrWhiteSpace(focus) || focus.IndexOf(':') < 1 ? null : focus.Trim();
            var press = StudioMaps.PressFor(tool);
            // A dry run presses nothing: the note names it as a preview and the cursor stays still (h17 P8: four previews read as four Publish presses).
            string previewSay = null;
            if (press != null && commit == false && !string.IsNullOrWhiteSpace(press.PreviewSay)) { previewSay = press.PreviewSay; press = null; }
            var place = (press != null ? StudioMaps.FindPlace(press.Place) : null) ?? say.Place;
            var always = press != null || say.Always;
            StudioNote publish;
            lock (_studioGate)
            {
                var now = StudioClock();
                // A show speaks for its own place only. A tool that works on another place is the assistant moving on, and the screen goes with it
                // (drive follow, Kane 13 Oct: "checking BPA but stay on AI readiness").
                if (now - _studioLastShow < StudioExplicitWins && place.Id == _studioLastShowPlace && (press == null || press.Id == _studioLastShowPress)) return;
                var sessionId = _sessions.SessionId;
                var key = sessionId ?? "";
                _studioNotes.TryGetValue(key, out var ring);
                var last = ring != null && ring.Count > 0 ? ring[ring.Count - 1] : null;
                var run = _studioRun;
                if (press == null && run != null && last != null && last.Id == run.NoteId && run.Say == say.Say && run.Place == place.Id && now - run.LastAt <= StudioBurstGap)
                {
                    run.Count++; run.LastAt = now;
                    var updated = Copy(last);
                    updated.Count = run.Count;
                    updated.Say = Clip(previewSay ?? say.CountSentence(run.Count));
                    ring[ring.Count - 1] = updated;
                    publish = Copy(updated);
                    publish.Update = true;
                }
                else
                {
                    if (!always && last != null && last.Place == place.Id && string.Equals(last.Focus, focus, StringComparison.Ordinal)) return;
                    var note = new StudioNote
                    {
                        Id = "sn-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                        Say = Clip(press?.Say ?? previewSay ?? say.Say), How = place.How, Place = place.Id, Focus = focus, Press = press?.Id,
                        At = now.ToString("o"), By = "assistant", Auto = true,
                        Call = HealthCorrelation.CurrentId, SessionId = sessionId,
                    };
                    if (ring == null) _studioNotes[key] = ring = new List<StudioNote>();
                    ring.Add(note);
                    if (ring.Count > StudioNoteRing) ring.RemoveRange(0, ring.Count - StudioNoteRing);
                    _studioRun = new AutoRun { NoteId = note.Id, Say = say.Say, Place = place.Id, Count = 1, LastAt = now };
                    publish = Copy(note);
                }
            }
            _sessions.Bus.PublishStudioShow(publish);
        }

        private void NoteBatch(string sessionId, string origin, int count, string verb, string noun, string placeId, ChangeWords words = null, bool undo = true)
        {
            if (origin != "agent" || count <= 0 || DryRunScope.Current != null || _sessions.Bus.HumanClientAttached?.Invoke() != true) return;
            var place = StudioMaps.FindPlace(placeId);
            if (count == 1) noun = noun.TrimEnd('s');
            StudioNote note;
            lock (_studioGate)
            {
                var key = sessionId ?? "";
                note = new StudioNote
                {
                    Id = "sn-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Say = (words?.Sentence ?? $"Your assistant {verb} {count} {noun}.") + (undo ? " Undo" : ""), Count = count,
                    How = place.How, Place = place.Id, At = StudioClock().ToString("o"), By = "assistant", Auto = true,
                    Call = HealthCorrelation.CurrentId, SessionId = sessionId,
                };
                if (!_studioNotes.TryGetValue(key, out var ring)) _studioNotes[key] = ring = new List<StudioNote>();
                ring.Add(note);
                if (ring.Count > StudioNoteRing) ring.RemoveRange(0, ring.Count - StudioNoteRing);
                _studioRun = null;
            }
            _sessions.Bus.PublishStudioShow(note);
        }

        private static string Clip(string text) => text.Length <= StudioSayMax ? text : text.Substring(0, StudioSayMax);

        private static StudioNote Copy(StudioNote n) => new StudioNote
        {
            Id = n.Id, Say = n.Say, How = n.How, Place = n.Place, Focus = n.Focus, Press = n.Press, At = n.At, By = n.By, Call = n.Call,
            SessionId = n.SessionId, Auto = n.Auto, Count = n.Count, Update = n.Update, ProStep = n.ProStep, Upgrade = n.Upgrade,
        };

        // A completed step has its own measured result. Tool-call burst counts cannot stand in for its fixes.
        private void NoteProStep(string sessionId, string rowId, string say, bool upgrade = false, string placeId = "plan", string by = "assistant")
        {
            var place = StudioMaps.FindPlace(placeId);
            StudioNote note;
            lock (_studioGate)
            {
                var key = sessionId ?? "";
                if (upgrade && !_studioProNotices.Add(key + ":" + rowId)) return;
                note = new StudioNote
                {
                    Id = "sn-" + Guid.NewGuid().ToString("N").Substring(0, 12), Say = Clip(say),
                    How = place.How, Place = place.Id, At = StudioClock().ToString("o"), By = by, Auto = true,
                    Call = HealthCorrelation.CurrentId, SessionId = sessionId, ProStep = true, Upgrade = upgrade,
                };
                if (!_studioNotes.TryGetValue(key, out var ring)) _studioNotes[key] = ring = new List<StudioNote>();
                ring.Add(note);
                if (ring.Count > StudioNoteRing) ring.RemoveRange(0, ring.Count - StudioNoteRing);
                _studioRun = null;
            }
            _sessions.Bus.PublishStudioShow(note);
        }

        private void NoteProFixes(string sessionId, ModelPlanItem item, int count, bool upgrade = false, string origin = "agent")
        {
            if (count <= 0 || (!IsSafeFixItem(item.Id) && !IsBpaFixItem(item.Id))) return;
            var kind = IsBpaFixItem(item.Id) ? "best-practice" : "safe";
            NoteProStep(sessionId, item.Id, (upgrade ? "Pro would apply " : "Pro applied ")
                + count + " " + kind + " " + (count == 1 ? "fix" : "fixes") + " in one step."
                + (upgrade ? "" : " One undo takes " + (count == 1 ? "it" : "them") + " back."), upgrade,
                by: DeployGuard.IsAgent(origin) ? "assistant" : "person");
        }

        private async Task NoteFreeProRowsAsync(Session s, IEnumerable<ModelPlanItem> rows, bool keptOnly = true)
        {
            var fixes = rows.Where(i => IsSafeFixItem(i.Id) || IsBpaFixItem(i.Id)).ToArray();
            if (fixes.Length == 0) return;
            var revision = s.Revision;
            var card = await PlanReadinessAsync(s).ConfigureAwait(false);
            var scope = ReportScopeSignature(s);
            foreach (var item in fixes)
            {
                lock (_studioGate)
                    if (_studioProNotices.Contains((s.Id ?? "") + ":" + item.Id)) continue;
                // Use the proposal's pure collector. This neither proposes nor writes a fix for a Free user.
                var count = keptOnly && item.ChangeSet?.Lines != null && !item.ChangeSet.Stale
                    ? item.ChangeSet.Lines.Count(l => l.Kept)
                    : await s.ReadAsync(m => CollectLinesForItem(m, item, card, null, scope, null).Seeds.Count).ConfigureAwait(false);
                if (ReferenceEquals(_sessions.Current, s) && s.Revision == revision) NoteProFixes(s.Id, item, count, upgrade: true);
            }
        }

        /// <summary>The assistant's data answer goes to the Ask page (the Studio's own asks already show there).</summary>
        private void PublishAskAnswer(Query.DataAsk ask, Query.DataAnswer answer, string origin)
        {
            if (origin != "agent" || answer == null) return;
            var sessionId = _sessions.SessionId;
            var shown = new StudioAnswer
            {
                Id = "sa-" + Guid.NewGuid().ToString("N").Substring(0, 12), SessionId = sessionId, At = StudioClock().ToString("o"),
                Question = string.IsNullOrWhiteSpace(ask?.Question) ? null : ask.Question.Trim(), Answer = answer,
            };
            lock (_studioGate) _studioAnswer[sessionId ?? ""] = shown;
            _sessions.Bus.PublishStudioAnswer(shown);
        }
    }

    /// <summary>The place map and the press map, read from the files the Studio's own code imports (one file, two readers). The place
    /// map is <c>webview/src/studio/places.json</c>; the press ids come from <c>presses.json</c> once that file is embedded (lane
    /// cursor), and from the contract's ten ids until then.</summary>
    internal static class StudioMaps
    {
        public const string ClaudeDrives = "claudeDrives";
        public const string IDrive = "iDrive";
        public const string Idle = "idle";
        public const string ClaudeDriving = "claudeDriving";
        public const string PersonHasWheel = "personHasWheel";

        internal sealed class Place
        {
            public string Id { get; set; }
            public string Tool { get; set; }
            public string Target { get; set; }
            public string Label { get; set; }
            public string How { get; set; }
            /// <summary>The MCP tools that work on this place. A call to one of them narrates itself in the Studio (follow).</summary>
            public string[] Tools { get; set; }
        }

        internal sealed class ToolSay
        {
            /// <summary>"Your assistant is writing descriptions."</summary>
            public string Say { get; set; }
            /// <summary>The sentence once a burst of the same tool has run on: "Your assistant has written {n} descriptions so far." Null: the Say with the count after it.</summary>
            public string Count { get; set; }
            /// <summary>A call that changes the plan: it always gets its own note, even right after another note on the same place.</summary>
            public bool Always { get; set; }
        }

        /// <summary>What the engine says by itself for a tool call, and the place it belongs to.</summary>
        internal sealed class AutoSay
        {
            public Place Place { get; set; }
            public string Say { get; set; }
            public string Count { get; set; }
            public bool Always { get; set; }
            public string CountSentence(int n) => Count != null ? Count.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Say;
        }

        private sealed class PlaceFile { public Place[] Places { get; set; } public Dictionary<string, ToolSay> ToolSays { get; set; } }
        internal sealed class PressRow
        {
            public string Id { get; set; }
            public string Place { get; set; }
            public string[] Tools { get; set; }
            /// <summary>The note for a press on a real button, naming it ("Your assistant pressed Run the plan."); null for a panel.</summary>
            public string Say { get; set; }
            /// <summary>The note for a dry run of the same tool (commit=false): a look at the change, not a press.</summary>
            public string PreviewSay { get; set; }
        }
        private sealed class PressFile { public PressRow[] Presses { get; set; } }

        private static readonly Lazy<PlaceFile> PlaceData = new Lazy<PlaceFile>(() => Load<PlaceFile>("studio-places.json", required: true) ?? new PlaceFile());
        private static readonly Lazy<Place[]> PlaceList = new Lazy<Place[]>(() => PlaceData.Value.Places ?? Array.Empty<Place>());
        private static readonly Lazy<Dictionary<string, AutoSay>> AutoMap = new Lazy<Dictionary<string, AutoSay>>(() =>
        {
            var map = new Dictionary<string, AutoSay>(StringComparer.Ordinal);
            var says = PlaceData.Value.ToolSays ?? new Dictionary<string, ToolSay>();
            foreach (var place in PlaceList.Value)
                foreach (var tool in place.Tools ?? Array.Empty<string>())
                    if (says.TryGetValue(tool, out var say) && !string.IsNullOrWhiteSpace(say?.Say))
                        map[tool] = new AutoSay { Place = place, Say = say.Say, Count = say.Count, Always = say.Always };
            return map;
        });
        private static readonly Lazy<PressRow[]> PressRows = new Lazy<PressRow[]>(() =>
            Load<PressFile>("studio-presses.json", required: false)?.Presses?.Where(p => !string.IsNullOrEmpty(p?.Id)).ToArray());
        private static readonly Lazy<string[]> PressList = new Lazy<string[]>(() => PressRows.Value?.Select(p => p.Id).ToArray() ?? ContractPresses);

        // The ten press ids of contract section 3, used until the cursor lane embeds presses.json.
        private static readonly string[] ContractPresses =
        {
            "read-source", "approve-build", "run-plan", "undo", "save", "publish", "publish-new", "ask", "explain", "lineage",
        };

        public static IReadOnlyList<Place> Places => PlaceList.Value;
        public static IReadOnlyList<string> PlaceIds => PlaceList.Value.Select(p => p.Id).ToArray();
        public static IReadOnlyList<string> PressIds => PressList.Value;
        /// <summary>The place and sentence for a tool the map lists, else null.</summary>
        public static AutoSay AutoFor(string tool) => !string.IsNullOrEmpty(tool) && AutoMap.Value.TryGetValue(tool, out var say) ? say : null;
        /// <summary>The control a tool presses on screen (presses.json, the cursor's own map), else null.</summary>
        public static PressRow PressFor(string tool) => string.IsNullOrEmpty(tool) ? null
            : PressRows.Value?.FirstOrDefault(p => p.Tools != null && p.Tools.Contains(tool, StringComparer.Ordinal));
        public static Place FindPlace(string id) => id == null ? null : PlaceList.Value.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        private static T Load<T>(string suffix, bool required) where T : class
        {
            var asm = typeof(StudioMaps).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                if (required) throw new InvalidOperationException(suffix + " is not embedded in Semanticus.Engine: the Studio place map is missing from the build.");
                return null;
            }
            using var stream = asm.GetManifestResourceStream(name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }
}
