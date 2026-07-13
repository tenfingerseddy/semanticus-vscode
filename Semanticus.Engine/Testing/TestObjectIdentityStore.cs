using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal sealed class TestMeasureIdentity
    {
        public string Ref { get; set; }
        public string TableName { get; set; }
        public string MeasureName { get; set; }
        public int TableOrdinal { get; set; }
        public int MeasureOrdinal { get; set; }
        public string ExpressionHash { get; set; }
    }

    internal sealed class TestObjectIdentityEntry
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Ref { get; set; }
        public string TableName { get; set; }
        public string MeasureName { get; set; }
        public int TableOrdinal { get; set; }
        public int MeasureOrdinal { get; set; }
        public string ExpressionHash { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal sealed class TestObjectIdentityIndex
    {
        public int SchemaVersion { get; set; } = 1;
        public List<TestObjectIdentityEntry> Entries { get; set; } = new List<TestObjectIdentityEntry>();
        [System.Text.Json.Serialization.JsonIgnore] public bool Dirty { get; set; }

        public string Bind(string existingId, TestMeasureIdentity snapshot)
        {
            var entry = !string.IsNullOrWhiteSpace(existingId)
                ? Entries.FirstOrDefault(e => string.Equals(e.Id, existingId, StringComparison.Ordinal))
                : null;
            entry ??= Entries.FirstOrDefault(e => string.Equals(e.Kind, "measure", StringComparison.Ordinal)
                && string.Equals(e.Ref, snapshot.Ref, StringComparison.OrdinalIgnoreCase)
                && e.TableOrdinal == snapshot.TableOrdinal && e.MeasureOrdinal == snapshot.MeasureOrdinal);
            if (entry == null)
            {
                entry = new TestObjectIdentityEntry { Id = "sid:" + Guid.NewGuid().ToString("N"), Kind = "measure" };
                Entries.Add(entry);
            }
            Update(entry, snapshot);
            return entry.Id;
        }

        public TestMeasureIdentity Resolve(string id, IReadOnlyList<TestMeasureIdentity> snapshots)
        {
            var entry = Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (entry == null) return null;

            var exact = snapshots.FirstOrDefault(s => string.Equals(s.Ref, entry.Ref, StringComparison.OrdinalIgnoreCase));
            TestMeasureIdentity resolved = null;
            if (exact != null && exact.TableOrdinal == entry.TableOrdinal && exact.MeasureOrdinal == entry.MeasureOrdinal)
                resolved = exact;
            else
            {
                var ordinal = snapshots.FirstOrDefault(s => s.TableOrdinal == entry.TableOrdinal && s.MeasureOrdinal == entry.MeasureOrdinal);
                if (ordinal != null && string.Equals(ordinal.ExpressionHash, entry.ExpressionHash, StringComparison.Ordinal))
                    resolved = ordinal;
                else
                {
                    var witnesses = snapshots.Where(s => string.Equals(s.ExpressionHash, entry.ExpressionHash, StringComparison.Ordinal)).Take(2).ToList();
                    if (witnesses.Count == 1) resolved = witnesses[0];
                }
            }
            if (resolved != null) Update(entry, resolved);
            return resolved;
        }

        private void Update(TestObjectIdentityEntry entry, TestMeasureIdentity snapshot)
        {
            if (entry.Ref == snapshot.Ref && entry.TableName == snapshot.TableName && entry.MeasureName == snapshot.MeasureName
                && entry.TableOrdinal == snapshot.TableOrdinal && entry.MeasureOrdinal == snapshot.MeasureOrdinal
                && entry.ExpressionHash == snapshot.ExpressionHash) return;
            entry.Ref = snapshot.Ref;
            entry.TableName = snapshot.TableName;
            entry.MeasureName = snapshot.MeasureName;
            entry.TableOrdinal = snapshot.TableOrdinal;
            entry.MeasureOrdinal = snapshot.MeasureOrdinal;
            entry.ExpressionHash = snapshot.ExpressionHash;
            entry.UpdatedUtc = DateTime.UtcNow.ToString("o");
            Dirty = true;
        }
    }

    /// <summary>Stable identities for tagless test targets, kept beside the suite rather than written into the model.</summary>
    internal static class TestObjectIdentityStore
    {
        internal const string FileName = "identities.json";
        internal const string BackupFileName = FileName + ".bak";
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        internal static TestObjectIdentityIndex Load(string testsDir)
        {
            var file = FileFor(testsDir);
            if (file == null) return new TestObjectIdentityIndex();
            lock (Gate)
            {
                if (TryLoad(file, out var primary)) return primary;
                if (TryLoad(BackupFor(file), out var recovered))
                {
                    // A recovered index must rewrite both copies on the next normal save; silently carrying a
                    // broken primary would make the next process repeat the same recovery forever.
                    recovered.Dirty = true;
                    return recovered;
                }
                return new TestObjectIdentityIndex();
            }
        }

        internal static bool Save(string testsDir, TestObjectIdentityIndex index)
        {
            if (index == null || !index.Dirty) return true;
            try
            {
                var file = FileFor(testsDir);
                if (file == null) return false;
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var json = JsonSerializer.Serialize(index, Json);
                    WriteAtomic(file, json);
                    WriteAtomic(BackupFor(file), json);
                    index.Dirty = false;
                }
                return true;
            }
            catch { return false; }
        }

        internal static IReadOnlyList<TestMeasureIdentity> Capture(Model model)
        {
            var result = new List<TestMeasureIdentity>();
            var tables = model.Tables.ToList();
            for (var ti = 0; ti < tables.Count; ti++)
            {
                var measures = tables[ti].Measures.ToList();
                for (var mi = 0; mi < measures.Count; mi++)
                    result.Add(new TestMeasureIdentity
                    {
                        Ref = ObjectRefs.For(measures[mi]), TableName = tables[ti].Name, MeasureName = measures[mi].Name,
                        TableOrdinal = ti, MeasureOrdinal = mi, ExpressionHash = Hash(measures[mi].Expression),
                    });
            }
            return result;
        }

        private static string FileFor(string testsDir) => string.IsNullOrWhiteSpace(testsDir)
            ? null : Path.Combine(testsDir, TestSuiteStore.SubDir, FileName);
        private static string BackupFor(string file) => file + ".bak";
        private static bool TryLoad(string file, out TestObjectIdentityIndex index)
        {
            index = null;
            try
            {
                if (!File.Exists(file)) return false;
                var candidate = JsonSerializer.Deserialize<TestObjectIdentityIndex>(File.ReadAllText(file), Json);
                if (candidate?.Entries == null || candidate.SchemaVersion != 1) return false;
                index = candidate;
                return true;
            }
            catch { return false; }
        }
        private static void WriteAtomic(string file, string json)
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Substring(0, 24).ToLowerInvariant();
        }
    }
}
