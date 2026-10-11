// Where the start plan is kept: one file per plan under the engine's state dir (SemanticusHome), written the way
// Composer/DraftStore.cs writes a draft (the per-file lock across processes, a temp file, then a move), so a restart
// keeps the document and a torn write never leaves half of one.
//
// WHY THE STATE DIR AND NOT THE MODEL'S FOLDER. A start plan exists BEFORE its model has anything worth keeping, and
// the build's rule is that the person's folder holds what the person asked for and nothing else. The plan, its scratch
// clone and its sample sidecar are the engine's own working state, so they live where the engine's other state lives.
// ONE plan per engine at a time: Newest() is how a restarted engine finds it again.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.StartPlan
{
    internal static class StartPlanStore
    {
        public const string DirName = "start-plans";

        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Dir() => Path.Combine(SemanticusHome.Root(), DirName);

        public static bool IsPlanId(string id) =>
            id != null && id.Length == 11 && id.StartsWith("sp-", StringComparison.Ordinal)
            && id.Skip(3).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        public static string NewId() => "sp-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string FileFor(string id) => Path.Combine(Dir(), id + ".json");

        /// <summary>Where this plan's scratch clone is serialized. Beside the document, never in the person's folder.</summary>
        public static string ScratchDirFor(string id) => Path.Combine(Dir(), id + ".scratch");

        /// <summary>Where the preview's own clone is serialized (a second clone, so a preview never disposes the decide loop's scratch).</summary>
        public static string PreviewDirFor(string id) => Path.Combine(Dir(), id + ".preview");

        /// <summary>The newest plan on disk (by its last update), or null. A file that cannot be read is skipped.</summary>
        public static StartPlanDoc Newest()
        {
            var dir = Dir();
            if (!Directory.Exists(dir)) return null;
            StartPlanDoc best = null;
            foreach (var file in Directory.EnumerateFiles(dir, "sp-*.json"))
            {
                var d = ReadFile(file);
                if (d == null || !IsPlanId(d.Id)) continue;
                if (best == null || d.UpdatedUtc > best.UpdatedUtc) best = d;
            }
            return best;
        }

        public static StartPlanDoc Read(string id) => IsPlanId(id) ? ReadFile(FileFor(id)) : null;

        private static StartPlanDoc ReadFile(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                return JsonSerializer.Deserialize<StartPlanDoc>(File.ReadAllText(file), Json);
            }
            catch { return null; }
        }

        /// <summary>Write one plan: under the cross-process lock for its file, to a temp file, then a move.</summary>
        public static void Write(StartPlanDoc d)
        {
            if (!IsPlanId(d?.Id)) throw new ArgumentException("A start plan needs an id before it is kept.");
            Directory.CreateDirectory(Dir());
            var file = FileFor(d.Id);
            using (SidecarFileLock.Acquire(file))
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json));
                File.Move(tmp, file, overwrite: true);
            }
        }

        /// <summary>Remove the plan's file and its scratch clone. Only ever this plan's own two paths.</summary>
        public static void Delete(string id)
        {
            if (!IsPlanId(id)) return;
            var file = FileFor(id);
            try { using (SidecarFileLock.Acquire(file)) if (File.Exists(file)) File.Delete(file); } catch { }
            var scratch = ScratchDirFor(id);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            var preview = PreviewDirFor(id);
            try { if (Directory.Exists(preview)) Directory.Delete(preview, true); } catch { }
        }

        /// <summary>A deep copy, so a reader never holds the document a writer is changing.</summary>
        public static StartPlanDoc Copy(StartPlanDoc d) =>
            d == null ? null : JsonSerializer.Deserialize<StartPlanDoc>(JsonSerializer.Serialize(d, Json), Json);
    }
}
