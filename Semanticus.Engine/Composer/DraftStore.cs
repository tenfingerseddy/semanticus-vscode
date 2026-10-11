// Where drafts are kept (composer contract 10.1): one file per draft under the model's `.semanticus/drafts/`,
// keyed by the model's durable identity like the Plan's own file, written with the same discipline (the per-file
// lock across processes, a temp file, then a move), so a restart keeps every draft and a torn write never leaves
// half of one.
//
// ONE FILE, NOT TWO, FOR NOW. The contract names a v3 document (`<id>.md`) beside the run state (`<id>.state.json`).
// The draft's steps, blanks, answers and run are all in the one JSON file here; the v3 document is a rendering of
// the same steps whose first reader is Author (lane L7, "Edit in Workflows"). Writing it before anything reads it
// would be a second copy that could drift, so it is left to the lane that opens it. Reversible: the JSON carries
// everything the rendering needs.
//
// A MODEL WITH NOWHERE TO KEEP THINGS (never saved, not live) keeps its drafts in engine memory for the session,
// the rule plan tasks follow (Plan-heart contract 2.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.Composer
{
    internal static class DraftStore
    {
        public const string DirName = "drafts";

        /// <summary>Closed drafts (done, or stopped and never run again) kept per model; the oldest go first.</summary>
        public const int MaxClosed = 50;

        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string DirIn(string sidecar) => string.IsNullOrEmpty(sidecar) ? null : Path.Combine(sidecar, DirName);

        public static bool IsDraftId(string id) =>
            id != null && id.Length == 11 && id.StartsWith("dr-", StringComparison.Ordinal)
            && id.Skip(3).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static string FileFor(string dir, string id) => Path.Combine(dir, id + ".json");

        /// <summary>Every draft of this model in the folder, oldest first. A file that cannot be read is skipped:
        /// one damaged draft must not hide the others.</summary>
        public static List<Draft> List(string dir, string identity)
        {
            var list = new List<Draft>();
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(identity) || !Directory.Exists(dir)) return list;
            foreach (var file in Directory.EnumerateFiles(dir, "dr-*.json"))
            {
                var d = ReadFile(file);
                if (d != null && string.Equals(d.ModelIdentity, identity, StringComparison.Ordinal)) list.Add(d);
            }
            return list.OrderBy(d => d.CreatedUtc).ToList();
        }

        public static Draft Read(string dir, string identity, string id)
        {
            if (string.IsNullOrEmpty(dir) || !IsDraftId(id)) return null;
            var d = ReadFile(FileFor(dir, id));
            return d != null && string.Equals(d.ModelIdentity, identity, StringComparison.Ordinal) ? d : null;
        }

        private static Draft ReadFile(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                return JsonSerializer.Deserialize<Draft>(File.ReadAllText(file), Json);
            }
            catch { return null; }
        }

        /// <summary>Write one draft: under the cross-process lock for its file, to a temp file, then a move.</summary>
        public static void Write(string dir, Draft d)
        {
            Directory.CreateDirectory(dir);
            var file = FileFor(dir, d.Id);
            using (SidecarFileLock.Acquire(file))
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json));
                File.Move(tmp, file, overwrite: true);
            }
            Prune(dir, d.ModelIdentity);
        }

        /// <summary>Delete one draft's file, under the same lock a write takes (a person removed its Plan row).</summary>
        public static void Delete(string dir, string id)
        {
            if (string.IsNullOrEmpty(dir) || !IsDraftId(id)) return;
            var file = FileFor(dir, id);
            using (SidecarFileLock.Acquire(file))
                if (File.Exists(file)) File.Delete(file);
        }

        /// <summary>Keep every open draft and the newest closed ones.</summary>
        private static void Prune(string dir, string identity)
        {
            try
            {
                var closed = List(dir, identity).Where(IsClosed)
                    .OrderByDescending(x => x.Run?.FinishedUtc ?? x.UpdatedUtc).Skip(MaxClosed).ToList();
                foreach (var old in closed)
                    try { File.Delete(FileFor(dir, old.Id)); } catch { }
            }
            catch { }
        }

        /// <summary>A draft that ran to the end. A stopped draft stays open: the person can run it again.</summary>
        public static bool IsClosed(Draft d) => d?.Run != null && d.Run.Outcome == "applied" && d.Run.FinishedUtc != null;

        /// <summary>A deep copy, so a reader never holds the list a writer is changing.</summary>
        public static Draft Copy(Draft d) => d == null ? null : JsonSerializer.Deserialize<Draft>(JsonSerializer.Serialize(d, Json), Json);
    }
}
