// Jev's answers, kept on this computer so a restart does not buy them again.
//
// WHY IT EXISTS. The session cache (Semanticus.Core/Decisions/DecisionCache.cs) lives and dies with one engine, so
// every restart asked every question again. On a Northstar-sized model the "what Copilot sees" family alone asks
// about every table, column and measure, and a restart cost about 80 requests and 79k tokens to learn nothing new
// (lane m11/m4-aischema, UAT lane p, 26 Sep). An answer is a fact about the EVIDENCE that was sent, and the request
// digest is that evidence's fingerprint (point, schema version, the object's snapshot, the questions), so an object
// whose facts have not changed is asked about again only when its answer ages out.
//
// KEYED BY MODEL, THEN BY DIGEST. One file per permission scope ("<identity>#<marker>", the same key consent is kept
// under), and in it one entry per digest. So an answer bought for one model is never served for another, and
// withdrawing permission for a model forgets that model's answers (Forget), while each entry also carries the
// permission GENERATION it was bought under and is served only under that same generation: a withdrawal made by
// another engine on this machine moves the generation, and every answer bought before it stops being served.
//
// THE MOVING ALIAS. The session cache serves an entry for ten minutes because the served model version is learned
// only from a reply. This store keeps an entry for DefaultKeepFor, and handles the alias this way instead: an entry
// carries the version that answered it, and once this engine has heard a reply from a DIFFERENT version, entries from
// the old one miss and are asked again. Right after a restart no version has been heard yet, and the stored one is
// trusted, which is the whole point. The honest cost: a vendor roll is noticed at the first ask whose evidence
// changed, or when the entries age out, not the moment it happens.
//
// WHAT IS IN THE FILE. The digest, the point name, the version that answered, when, the generation, and the answers
// under HASHED question names, as HASHED option ids and confidences. No name, no formula, no model content: the
// digest is a one-way hash, and so is every question name and every option id. Both can be model content: a
// measure's folder options are "folder:" and the model's own folder name (MeasureDefaultsPoint.FolderOptionId), and
// the value ranking names each question after the plan item it scores, which carries table and measure names
// ("value:starter/table:Sales", PlanRankPoint.QuestionFor). So the store never writes either as it is: it writes a
// keyed hash of it (HMAC-SHA256 under a random key made once per install and kept beside the files), and reads it
// back by hashing the questions and options of the request asked NOW and matching. A key nobody else holds is what
// stops a guess ("folder:Sales"?) being checked against the file. An answer whose hash matches no current question
// or option is a miss. The only plain words left are the point's registered name, the served version and the wire
// type, none of which comes from a model. It lives in the per-user home beside consent (~/.semanticus,
// ConnectionRegistry.Root, whose test seam keeps tests out of a real home), never in the model files.
//
// OLDER FILES. Entries from an older format held names as they were: before format 2 the option ids, and in format
// 2 the question names (verify round 3, R3-2). They are discarded when the file is read, and the file is rewritten
// without them, so a name kept by an older build does not stay on disk. Discarding one only means that question is
// asked again.
//
// FAILING SHUT. A file that cannot be read, or a line that cannot be parsed, is a miss, and a miss only means the
// question is asked again. Writes are appended one line per answer; the file is rewritten compactly when it grows past
// twice the entries it holds.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Semanticus.Decisions;

namespace Semanticus.Engine.Decisions
{
    public sealed class DecisionAnswerStore
    {
        /// <summary>How long a stored answer is served. A week: long enough that reopening a model through a working
        /// week asks nothing about what has not changed, short enough that a vendor roll nobody else noticed is picked
        /// up in bounded time.</summary>
        public static readonly TimeSpan DefaultKeepFor = TimeSpan.FromDays(7);

        /// <summary>The most answers one model keeps. The oldest go first.</summary>
        public const int MaxEntriesPerModel = 5000;

        private const string Folder = "decision-answers";

        /// <summary>The entry format this build writes: 3 = question names and option ids hashed under the install key
        /// (2 hashed only the option ids). An entry with any other number is discarded on read.</summary>
        private const int Format = 3;

        private const string KeyFile = "install.key";
        private const string HashPrefix = "h:";

        private readonly Func<DateTimeOffset> _now;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Loaded> _files = new Dictionary<string, Loaded>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Test seam. Runs while a new install key's file is still open, after its bytes are written and
        /// before the chmod after it, so a test can read the mode the file was CREATED with rather than the mode it
        /// ends with. Null on every production path.</summary>
        internal Action<string> KeyWrittenForTest;

        public DecisionAnswerStore(TimeSpan? keepFor = null, Func<DateTimeOffset> clock = null)
        {
            KeepFor = keepFor ?? DefaultKeepFor;
            if (KeepFor <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(keepFor));
            _now = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public TimeSpan KeepFor { get; }

        /// <summary>How many answers this store served instead of an ask. Tests read it.</summary>
        public int Served { get; private set; }

        private sealed class Entry
        {
            public int f { get; set; }             // entry format (Format)
            public string d { get; set; }          // request digest
            public string p { get; set; }          // point name
            public string v { get; set; }          // served model version
            public long g { get; set; }            // permission generation
            public DateTimeOffset t { get; set; }  // when it was bought
            public JsonElement a { get; set; }     // the answers in the wire shape, question names and option ids hashed (Wire)
        }

        private sealed class Loaded
        {
            public string Path;
            public Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            public int Lines;
        }

        /// <summary>A stored answer for this request under this model and permission, or false. The answer is read
        /// back through the same validation a reply gets, against the request asked NOW, so a stored answer that no
        /// longer fits the question is a miss rather than a wrong answer.</summary>
        public bool TryGet(string scope, long generation, DecisionRequest request, string currentServedVersion, out DecisionAnswers answers)
        {
            answers = null;
            if (string.IsNullOrEmpty(scope) || request == null) return false;
            try
            {
                Entry e;
                byte[] key;
                lock (_gate)
                {
                    var file = Load(scope);
                    if (file == null || !file.Entries.TryGetValue(request.Digest, out e)) return false;
                    key = InstallKey(create: false);
                }
                if (key == null || e.f != Format) return false;
                if (e.g != generation || !string.Equals(e.p, request.PointName, StringComparison.Ordinal)) return false;
                var now = _now();
                if (now - e.t >= KeepFor || e.t > now + TimeSpan.FromMinutes(5)) return false;
                if (currentServedVersion != null && !string.Equals(currentServedVersion, e.v, StringComparison.Ordinal)) return false;
                var wire = Unwire(request, e.a, key);
                if (wire == null) return false;
                var read = DecisionAnswerReader.Read(request, wire.Value, e.v);
                if (!read.Decided) return false;
                answers = read;
                lock (_gate) Served++;
                return true;
            }
            catch { answers = null; return false; }
        }

        /// <summary>Keep a decided answer for this model under the permission it was bought with. A "not decided" is
        /// never kept, for the same reason the session cache never keeps one.</summary>
        public void Put(string scope, long generation, DecisionRequest request, DecisionAnswers answers)
        {
            if (string.IsNullOrEmpty(scope) || request == null || answers == null || !answers.Complete) return;
            if (!string.Equals(answers.RequestDigest, request.Digest, StringComparison.Ordinal)) return;
            try
            {
                lock (_gate)
                {
                    // No key, nothing kept: an answer is never written with its option ids as they are.
                    var key = InstallKey(create: true);
                    if (key == null) return;
                    var entry = new Entry
                    {
                        f = Format, d = request.Digest, p = request.PointName, v = answers.ServedModelVersion, g = generation,
                        t = _now(), a = Wire(request, answers, key),
                    };
                    var file = Load(scope);
                    file.Entries[entry.d] = entry;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path));
                    if (file.Entries.Count > MaxEntriesPerModel || file.Lines + 1 > 2 * Math.Max(64, file.Entries.Count))
                        Rewrite(file);
                    else
                    {
                        File.AppendAllText(file.Path, JsonSerializer.Serialize(entry) + "\n");
                        file.Lines++;
                    }
                }
            }
            catch { }
        }

        /// <summary>Forget every answer kept for this model. Called when permission for it is withdrawn.</summary>
        public void Forget(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            lock (_gate)
            {
                _files.Remove(scope);
                try { File.Delete(PathOf(scope)); } catch { }
            }
        }

        // ---- the file -----------------------------------------------------------------------------------------

        private static string PathOf(string scope)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(scope));
            var name = string.Concat(hash.Take(12).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            return System.IO.Path.Combine(ConnectionRegistry.Root(), Folder, name + ".jsonl");
        }

        /// <summary>The file for a scope, read once per store and then kept in step with every write. Caller holds
        /// the gate. The root is read at each call, so a test seam set after construction is honoured.</summary>
        private Loaded Load(string scope)
        {
            var path = PathOf(scope);
            if (_files.TryGetValue(scope, out var known) && string.Equals(known.Path, path, StringComparison.Ordinal)) return known;
            var loaded = new Loaded { Path = path };
            var discarded = false;
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    loaded.Lines++;
                    try
                    {
                        var e = JsonSerializer.Deserialize<Entry>(line);
                        // An entry from an older format held names as they were: it is dropped, never served.
                        if (e?.d != null && e.f == Format) loaded.Entries[e.d] = e;
                        else discarded = true;
                    }
                    catch { discarded = true; }
                }
            }
            _files[scope] = loaded;
            // Rewritten at once without what was dropped, so an older build's names do not stay on disk.
            if (discarded)
            {
                try { Rewrite(loaded); } catch { }
            }
            return loaded;
        }

        private void Rewrite(Loaded file)
        {
            var now = _now();
            var keep = file.Entries.Values.Where(e => now - e.t < KeepFor)
                .OrderByDescending(e => e.t).Take(MaxEntriesPerModel).OrderBy(e => e.t).ToList();
            file.Entries = keep.ToDictionary(e => e.d, StringComparer.Ordinal);
            var tmp = file.Path + ".tmp";
            File.WriteAllText(tmp, string.Concat(keep.Select(e => JsonSerializer.Serialize(e) + "\n")));
            File.Move(tmp, file.Path, overwrite: true);
            file.Lines = keep.Count;
        }

        /// <summary>This install's key for hashing question names and option ids, made once (32 random bytes) and kept beside the answer
        /// files. Null when it cannot be read or made, and then nothing is kept and nothing is served. Caller holds the
        /// gate. The root is read at each call, like the files, so a test seam set after construction is honoured.</summary>
        private byte[] InstallKey(bool create)
        {
            var path = System.IO.Path.Combine(ConnectionRegistry.Root(), Folder, KeyFile);
            if (_keys.TryGetValue(path, out var known)) return known;
            try
            {
                if (!File.Exists(path))
                {
                    if (!create) return null;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    var fresh = RandomNumberGenerator.GetBytes(32);
                    try
                    {
                        // CreateNew: two engines making the key at once must end up with one key, not two.
                        // OWNER ONLY FROM THE FIRST BYTE. The mode is part of creating the file, not something done
                        // to it afterwards: setting it after the write left a window, however short, in which this
                        // install's key sat on disk readable by anyone on the machine (verify round 3). The chmod
                        // below still runs, because a umask can take permissions off the create mode but never
                        // add them, so it is the belt to the create mode's braces and never widens anything.
                        var options = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                        };
                        if (!OperatingSystem.IsWindows())
                            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        using (var fs = new FileStream(path, options))
                        {
                            fs.Write(fresh, 0, fresh.Length);
                            KeyWrittenForTest?.Invoke(path);
                        }
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (IOException) when (File.Exists(path)) { }
                }
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 32) return null;
                _keys[path] = bytes;
                return bytes;
            }
            catch { return null; }
        }

        /// <summary>One question name or option id as it is kept: a keyed hash, so the file never holds the text
        /// itself. The tag keeps the two apart, so a question and an option that share a text do not share a hash.</summary>
        private static string Keyed(string tag, string text, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(tag + "|" + (text ?? string.Empty)));
            return HashPrefix + string.Concat(hash.Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static string HashOption(string id, byte[] key) => Keyed("option", id, key);
        private static string HashQuestion(string name, byte[] key) => Keyed("question", name, key);

        /// <summary>A kept answer back in the wire shape, its hashed question names and ids resolved against the
        /// request asked now. Null when any hash names no current question or option: the stored answer does not fit
        /// this request.</summary>
        private static JsonElement? Unwire(DecisionRequest request, JsonElement stored, byte[] key)
        {
            if (stored.ValueKind != JsonValueKind.Object) return null;
            var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);
            foreach (var asked in request.Questions) questions.TryAdd(HashQuestion(asked.Name, key), asked);
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in stored.EnumerateObject())
            {
                if (!questions.TryGetValue(property.Name, out var q) || property.Value.ValueKind != JsonValueKind.Object) return null;
                var ids = q.Options.ToDictionary(opt => HashOption(opt.Id, key), opt => opt.Id, StringComparer.Ordinal);
                string Resolve(string hashed) => hashed != null && ids.TryGetValue(hashed, out var id) ? id : null;
                var w = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var field in property.Value.EnumerateObject())
                {
                    if ((field.Name == "choice" || field.Name == "score") && field.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = Resolve(field.Value.GetString());
                        if (id == null) return null;
                        w[field.Name] = id;
                    }
                    else if (field.Name == "probabilities" && field.Value.ValueKind == JsonValueKind.Object)
                    {
                        var probabilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                        foreach (var pr in field.Value.EnumerateObject())
                        {
                            var id = Resolve(pr.Name);
                            if (id == null) return null;
                            probabilities[id] = pr.Value.Clone();
                        }
                        w[field.Name] = probabilities;
                    }
                    else w[field.Name] = field.Value.Clone();
                }
                o[q.Name] = w;
            }
            return JsonSerializer.SerializeToElement(o);
        }

        /// <summary>The answers in the shape a reply carries them, every question name and option id hashed under the
        /// install key, so reading one back (Unwire) is the ordinary validation and the file holds no name as it is.</summary>
        private static JsonElement Wire(DecisionRequest request, DecisionAnswers answers, byte[] key)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var q in request.Questions)
            {
                var a = answers.For(q.Name);
                if (a == null) continue;
                var w = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = q.WireType };
                switch (a.Kind)
                {
                    case DecisionQuestionKind.Choice: w["choice"] = HashOption(a.Choice, key); w["confidence"] = a.Confidence; break;
                    case DecisionQuestionKind.Score: w["score"] = HashOption(a.Score, key); w["confidence"] = a.Confidence; break;
                    default: w["noul"] = a.Noul == true ? a.Confidence : 1 - a.Confidence; break;
                }
                if (a.Kind != DecisionQuestionKind.Noul && a.Probabilities.Count > 0)
                    w["probabilities"] = a.Probabilities.ToDictionary(kv => HashOption(kv.Key, key), kv => kv.Value, StringComparer.Ordinal);
                o[HashQuestion(q.Name, key)] = w;
            }
            return JsonSerializer.SerializeToElement(o);
        }
    }
}
