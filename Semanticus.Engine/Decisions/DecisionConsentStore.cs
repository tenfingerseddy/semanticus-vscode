// Where consent lives.
//
// Outside the model files, always. Consent is a fact about a PERSON and their machine, not something that
// should travel in a .bim, a PBIP folder, a git commit or a deployment. It goes in the per-user home that
// already holds remembered connections, ~/.semanticus, and it shares that directory's test seam
// (ConnectionRegistry.RootOverride) so no test ever writes into a real home.
//
// But one file does NOT mean one permission. Every grant is bound to the model it was given for, keyed by the
// same stable identity an interview pack binds to: the live endpoint and database, else the full model path
// on disk. Agreeing on a sample model must never let a private project opened later that day send anything.
// A model with no stable identity (created, never saved, not connected) cannot be agreed for at all, because
// there would be nothing to tell it apart from the next brand-new model.
//
// AN ADDRESS IS NOT A MODEL, which is what F-177 was about. A file path and a live endpoint plus database are
// both addresses a model merely OCCUPIES: overwrite the file, or redeploy the dataset, and the address is
// unchanged while the model behind it is somebody else's. So a grant is keyed on the address AND a WITNESS
// taken from the model that was actually shown to the person, as "<identity>#<witness>". A replacement at the
// same address produces a key with no record behind it and the person is asked again. The engine computes the
// witness (LocalEngine.Decisions.cs) and this file only ever sees the finished key.
//
// One identity keeps at most ONE record, and that is a rule of this file rather than tidiness. Leaving an old
// witness behind would let an undo, a rename back or a restore return the model to a shape someone once
// agreed to, and be re-admitted by a record nobody remembers granting. Every write therefore sweeps the other
// keys under the same identity, so the newest answer for a model is the only answer for it.
//
// Each model's record also carries a GENERATION that goes up on every grant and every withdrawal. A request
// is admitted against one, and checks it again before sending and before returning, so a withdrawal made
// anywhere (this engine or another one on the same machine) is seen by a request already in flight.
//
// Failing shut is the whole point of the read path: an unreadable, missing or malformed file means NO
// consent, so nothing leaves the machine. Every other settings file in this product fails open, and this one
// deliberately does the opposite. The same rule covers the older one-record file this replaced: it has no
// per-model grants in it, so it reads as no permission for every model and the person is asked once more.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Semanticus.Decisions;

namespace Semanticus.Engine.Decisions
{
    public static class DecisionConsentStore
    {
        private const string FileName = "decision-consent.json";

        /// <summary>The file layout in force. Version 1 held one record for the whole machine; version 2 held
        /// one per ADDRESS, which is the reuse F-177 found. A file at either version has no key this build can
        /// match, so it reads as no permission rather than as permission for everything, and the person is
        /// asked once more. Bumped on purpose rather than left to a key that happens not to match.</summary>
        private const int FileVersion = 3;

        /// <summary>The one separator between the address and the witness. Neither half can contain it: an
        /// identity is "live:" or "disk:" plus eight hex characters (LocalEngine.Interview.cs) and a witness is
        /// eight hex characters, so the split is unambiguous and every key can be traced back to its model.</summary>
        private const char WitnessMark = '#';

        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private sealed class Record
        {
            public bool granted { get; set; }
            public int schemaVersion { get; set; }
            public List<string> approvedFields { get; set; }
            public DateTimeOffset? grantedUtc { get; set; }
            public DateTimeOffset? withdrawnUtc { get; set; }
            public long generation { get; set; }

            /// <summary>The content stamp of the files the model was loaded from when this record's marker was
            /// minted into it and not yet saved (LocalEngine.Decisions.cs, PendingConsentWitnessAsync). Null once
            /// nothing is pending. It names FILES, never a permission: it only lets the same files, opened again at
            /// the same address after the unsaved marker was discarded, be recognised as the model that was agreed
            /// for.</summary>
            public string pendingFiles { get; set; }
        }

        private sealed class Store
        {
            public int version { get; set; }
            public Dictionary<string, Record> models { get; set; }
        }

        internal static string FilePath() => Path.Combine(ConnectionRegistry.Root(), FileName);

        /// <summary>The key one grant is kept under: the address the model was open at, and the witness taken
        /// from the model itself. Null identity means there is nothing to key on and there is no scope, which
        /// every caller already treats as no permission.</summary>
        public static string ScopeKey(string identity, string witness) =>
            string.IsNullOrEmpty(identity) ? null
                : string.IsNullOrEmpty(witness) ? identity
                : identity + WitnessMark + witness;

        /// <summary>The address half of a key. A key written by an older build has no witness and is its own
        /// address, which keeps the sweep correct while such a record is still on disk.</summary>
        private static string IdentityOf(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return scope;
            var mark = scope.IndexOf(WitnessMark);
            return mark < 0 ? scope : scope.Substring(0, mark);
        }

        /// <summary>The sentence a person sees when the open model has no identity a permission could be bound
        /// to. Shared so the refusal and the skip say the same thing.</summary>
        internal const string NoScopeReason =
            "Save this model, or open it live, before agreeing to send its details for advice. Permission is kept for one model at a time, and an unsaved model has no name to keep it under.";

        /// <summary>Read the stored permission for ONE model. Anything unreadable, any other model's grant, and
        /// the older whole-machine file all read as NO permission, so a corrupt or outdated file can only ever
        /// make the product quieter, never chattier.</summary>
        public static DecisionConsent Read(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return DecisionConsent.None;

            lock (Gate)
            {
                try
                {
                    var store = LoadLocked();
                    Record record;
                    if (store.models == null || !store.models.TryGetValue(scope, out record) || record == null)
                        return DecisionConsent.None;
                    return new DecisionConsent(record.granted, record.schemaVersion, record.approvedFields,
                        record.grantedUtc, record.withdrawnUtc, record.generation);
                }
                catch
                {
                    return DecisionConsent.None;
                }
            }
        }

        /// <summary>Grant permission for exactly <paramref name="fields"/> at <paramref name="schemaVersion"/>,
        /// for the one model <paramref name="scope"/> names. A field that no registered point declares is
        /// refused rather than stored: consent has to describe the product that exists, or the settings list
        /// stops being the audit surface.</summary>
        public static DecisionConsent Grant(string scope, IEnumerable<string> fields, int schemaVersion, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(scope)) throw new InvalidOperationException(NoScopeReason);

            var asked = (fields ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (asked.Count == 0)
                throw new InvalidOperationException("Consent needs the list of details it covers. Pass the fields from get_consent.");

            if (schemaVersion != DecisionRegistry.ConsentSchemaVersion)
                throw new InvalidOperationException(
                    $"That permission was for version {schemaVersion} of the details list, and this build is on version {DecisionRegistry.ConsentSchemaVersion}. Read get_consent again and agree to the current list.");

            var declared = new HashSet<string>(DecisionRegistry.DeclaredFields, StringComparer.Ordinal);
            var unknown = asked.Where(f => !declared.Contains(f)).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"Nothing in this product sends {string.Join(", ", unknown)}. Agree to the list get_consent returns.");

            lock (Gate)
            {
                var consent = DecisionConsent.Grant(asked, schemaVersion, nowUtc, NextGenerationLocked(scope));
                WriteLocked(scope, consent);
                return consent;
            }
        }

        public static DecisionConsent Withdraw(string scope, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(scope)) return DecisionConsent.None;

            lock (Gate)
            {
                var current = ReadLocked(scope);
                var consent = current.Withdraw(nowUtc, NextGenerationLocked(scope));
                WriteLocked(scope, consent);
                return consent;
            }
        }

        private static DecisionConsent ReadLocked(string scope)
        {
            try
            {
                var store = LoadLocked();
                Record record;
                if (store.models == null || !store.models.TryGetValue(scope, out record) || record == null)
                    return DecisionConsent.None;
                return new DecisionConsent(record.granted, record.schemaVersion, record.approvedFields,
                    record.grantedUtc, record.withdrawnUtc, record.generation);
            }
            catch
            {
                return DecisionConsent.None;
            }
        }

        /// <summary>The next generation for this model. It comes off what is ON DISK, not off what this process
        /// last wrote, so two engines writing the same model's permission cannot both hand out the same number.
        /// An unreadable file restarts the count, which can only invalidate in-flight requests, never admit
        /// them: a request compares for equality and a fresh 1 will not match anything it was admitted under
        /// unless it was admitted under that very record.</summary>
        private static long NextGenerationLocked(string scope)
        {
            try
            {
                var store = LoadLocked();
                Record record;
                if (store.models != null && store.models.TryGetValue(scope, out record) && record != null)
                    return record.generation + 1;
            }
            catch { /* an unreadable file starts the count again, which only ever refuses more */ }
            return 1;
        }

        private static Store LoadLocked()
        {
            var path = FilePath();
            if (!File.Exists(path)) return new Store { version = FileVersion, models = new Dictionary<string, Record>(StringComparer.Ordinal) };

            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path), Json);
            if (store == null || store.version != FileVersion || store.models == null)
                return new Store { version = FileVersion, models = new Dictionary<string, Record>(StringComparer.Ordinal) };
            return store;
        }

        private static void WriteLocked(string scope, DecisionConsent consent)
        {
            Store store;
            try { store = LoadLocked(); }
            catch { store = new Store { version = FileVersion, models = new Dictionary<string, Record>(StringComparer.Ordinal) }; }

            if (store.models == null) store.models = new Dictionary<string, Record>(StringComparer.Ordinal);
            store.version = FileVersion;

            // One address keeps one answer. Any other witness under the same address is a model state someone
            // once agreed to and has since left; keeping it would let an undo or a restore walk back into a
            // grant nobody remembers. Other addresses are other models and are never touched, which is what
            // makes a withdrawal clear exactly one model's permission.
            var identity = IdentityOf(scope);
            var stale = store.models.Keys
                .Where(k => !string.Equals(k, scope, StringComparison.Ordinal)
                            && string.Equals(IdentityOf(k), identity, StringComparison.Ordinal))
                .ToList();
            foreach (var k in stale) store.models.Remove(k);

            // A grant or a withdrawal on the same key keeps what is pending for it: the files have not changed.
            string pending = null;
            if (store.models.TryGetValue(scope, out var previous) && previous != null) pending = previous.pendingFiles;

            store.models[scope] = new Record
            {
                pendingFiles = pending,
                granted = consent.Granted,
                schemaVersion = consent.SchemaVersion,
                approvedFields = consent.ApprovedFields.ToList(),
                grantedUtc = consent.GrantedUtc,
                withdrawnUtc = consent.WithdrawnUtc,
                generation = consent.Generation,
            };

            var root = ConnectionRegistry.Root();
            Directory.CreateDirectory(root);
            var path = FilePath();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, Json));
            File.Move(tmp, path, overwrite: true);
        }

        private static void SaveLocked(Store store)
        {
            var root = ConnectionRegistry.Root();
            Directory.CreateDirectory(root);
            var path = FilePath();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, Json));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Record that the marker in <paramref name="scope"/> was minted into a model loaded from files whose
        /// content stamp is <paramref name="filesStamp"/>, and is not saved into them yet. Only an existing record is
        /// marked; a blank stamp (a live model, or files that moved since they were loaded) clears it.</summary>
        public static void MarkPending(string scope, string filesStamp)
        {
            if (string.IsNullOrEmpty(scope)) return;
            lock (Gate)
            {
                try
                {
                    var store = LoadLocked();
                    if (store.models == null || !store.models.TryGetValue(scope, out var record) || record == null) return;
                    record.pendingFiles = string.IsNullOrEmpty(filesStamp) ? null : filesStamp;
                    SaveLocked(store);
                }
                catch { /* failing to remember only means the next open asks again, the safe direction */ }
            }
        }

        /// <summary>The witness (marker) of the one record at <paramref name="identity"/> whose marker is pending on
        /// exactly these files, or null. Unreadable, absent or any other files all read as null, which asks again.</summary>
        public static string PendingWitness(string identity, string filesStamp)
        {
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(filesStamp)) return null;
            lock (Gate)
            {
                try
                {
                    var store = LoadLocked();
                    foreach (var kv in store.models ?? new Dictionary<string, Record>())
                    {
                        if (kv.Value == null || !string.Equals(IdentityOf(kv.Key), identity, StringComparison.Ordinal)) continue;
                        if (!string.Equals(kv.Value.pendingFiles, filesStamp, StringComparison.Ordinal)) continue;
                        var mark = kv.Key.IndexOf(WitnessMark);
                        return mark < 0 ? null : kv.Key.Substring(mark + 1);
                    }
                }
                catch { }
                return null;
            }
        }

        /// <summary>Forget every pending stamp at <paramref name="identity"/>: the marker was undone, so the files it was
        /// minted on are no longer the model that was agreed for.</summary>
        public static void ClearPending(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return;
            lock (Gate)
            {
                try
                {
                    var store = LoadLocked();
                    var changed = false;
                    foreach (var kv in store.models ?? new Dictionary<string, Record>())
                        if (kv.Value != null && kv.Value.pendingFiles != null
                            && string.Equals(IdentityOf(kv.Key), identity, StringComparison.Ordinal))
                        {
                            kv.Value.pendingFiles = null;
                            changed = true;
                        }
                    if (changed) SaveLocked(store);
                }
                catch { /* an unreadable store already reads as no permission for every model */ }
            }
        }

        /// <summary>Whether any record at <paramref name="identity"/> has a marker pending, so a caller can skip reading
        /// the files when nothing could match.</summary>
        public static bool AnyPending(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            lock (Gate)
            {
                try
                {
                    var store = LoadLocked();
                    return (store.models ?? new Dictionary<string, Record>()).Any(kv => kv.Value != null
                        && !string.IsNullOrEmpty(kv.Value.pendingFiles)
                        && string.Equals(IdentityOf(kv.Key), identity, StringComparison.Ordinal));
                }
                catch { return false; }
            }
        }
    }
}
