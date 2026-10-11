using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        private readonly System.Threading.SemaphoreSlim _primerGate = new System.Threading.SemaphoreSlim(1, 1);

        private sealed class PrimerSuggestionState
        {
            public System.Collections.Generic.Dictionary<string, string> Decisions { get; set; } = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        }

        // The Primer's durable filename key. Cloud XMLA (live:) and disk (disk:) reuse the interview identity
        // scheme byte-for-byte. A LOCAL Power BI Desktop model is the one lane where that scheme rots: the endpoint
        // (localhost:port) AND the database (a per-session GUID) BOTH rotate every Desktop restart, so an
        // endpoint|database key computes a NEW filename each reload and orphans the primer (the reported bug).
        // A local model's identity is PROVENANCE captured ONCE at open_local/connect_local, a ladder:
        //   1. the full .pbix PATH (LiveOrigin.LocalPath) — NO LONGER STAMPED (the join's ruling, 30 Sep: Desktop's
        //      command line names the file Desktop was started with, not the one the window shows). The honest cost:
        //      a Primer an earlier build saved under a path key is no longer found (the key is the path's hash, which
        //      nothing now supplies), and one whose stamp claims a path refuses (this session cannot prove the same
        //      file) until it is saved again under the title. The path branches below stay for those older stamps;
        //   2. the window-title STEM (LiveOrigin.LocalName) — same-stem files collide, so writes stamp provenance
        //      beside the artifact and reads FAIL CLOSED on a proven mismatch (blank template + honest note, never
        //      another model's content); two same-stem models with NO recoverable path on either side remain a
        //      disclosed limitation (nothing distinguishes them);
        //   3. neither → the pre-fix endpoint|database key (works, just not restart-stable — degraded, never wrong).
        // NOT a content fingerprint: ordinary structural edits change that mid-session (the primer would move
        // between a set and a get) and fingerprint-equivalent clones would share one primer across two models.
        // The lane is chosen by HOW the model was opened (only the Desktop open paths stamp an identity), never by
        // loopback string-matching — a real SSAS on localhost has no Desktop workspace, gets no identity, and keeps
        // its perfectly stable live: endpoint|database key.
        // Identity derives ONLY from fields stamped once on the open path (no model read), is cached on the Session,
        // and identity + sidecar anchor + the attached-connection reference come from ONE captured SessionContext —
        // a structural edit can never move the primer, a concurrent open can never pair one model's identity with
        // another model's sidecar, and the write gates re-verify BOTH the context and the captured connection.
        // While a live open has published its session but not yet stamped LiveOrigin (_liveOriginStampPending), the
        // attached-connection fallback stands down: a stale _live would mint the WRONG identity for a session about
        // to gain its real one. PaneIdentity and its consumers (interview panes, evidence library) are untouched.
        private sealed class PrimerLocus
        {
            public string Identity;
            public string File;             // null = no sidecar to write to
            public LiveConnection Live;     // the attached connection AT DERIVATION — re-verified in the write gates
            public string LocalPath;        // the Desktop .pbix path, when this is a Desktop lane and it was captured
            public string LocalStem;        // the Desktop title stem, when captured
            public bool IsLocal;            // identity is a local: key (either rung)
        }

        private PrimerLocus PrimerLocusFor(SessionContext context)
        {
            var live = context.Live;
            var s = context.Session;
            string identity = null, path = null, stem = null;
            if (s != null)
            {
                if (s.LiveOrigin != null)
                {
                    path = s.LiveOrigin.LocalPath;
                    stem = s.LiveOrigin.LocalName;
                    identity = s.PrimerIdentityCache ??= LocalKey(path, stem) ?? LiveIdentityFor(s.LiveOrigin);
                }
                else if (!ExperienceStore.IsEphemeralAnchor(s.SourcePath))
                {
                    identity = s.PrimerIdentityCache ??= "disk:" + Sha8(CanonAnchor(s.SourcePath));
                }
            }
            // Attached-only fallback (no editable anchor): the attached connection's own provenance, consistent with
            // the session lanes. Stands down while a live open is mid-stamp (see the class comment).
            if (identity == null && live != null && System.Threading.Volatile.Read(ref _liveOriginStampPending) == 0)
            {
                path = live.DesktopPath;
                stem = live.DesktopName;
                identity = LocalKey(path, stem) ?? LiveIdentityFor(live);   // never session-cached: it follows the connection
            }
            if (string.IsNullOrWhiteSpace(identity)) return new PrimerLocus { Live = live };
            var anchor = s?.SourcePath;   // the SAME captured context as the identity — never a second Current read
            var sidecar = !ExperienceStore.IsEphemeralAnchor(anchor) ? LayoutStore.DirFor(anchor)
                : _workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName);
            return new PrimerLocus
            {
                Identity = identity,
                File = sidecar == null ? null : Path.Combine(sidecar, "primers", SafeKeyName(identity) + ".md"),
                Live = live,
                LocalPath = path,
                LocalStem = stem,
                IsLocal = identity.StartsWith("local:", StringComparison.Ordinal),
            };
        }

        private static string LocalKey(string path, string stem)
            => path != null ? "local:" + Sha8(CanonAnchor(path))
             : stem != null ? "local:" + Sha8(NormLivePart(stem))
             : null;

        private static string SafeKeyName(string identity)
            => new string(identity.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());

        // ---- local-lane provenance (the same-stem armor) ---------------------------------------------------
        // A stem key can collide across same-named .pbix files, so every LOCAL primer write stamps WHO the
        // artifact was written for beside it, and reads trust the stamp only when it can VOUCH for the exact
        // content (its ContentHash matches the md — a torn write, a crash between the two moves, or a mixed
        // sidecar copy can never positively authorize anything):
        //   VALID stamp: its claims are enforced. A path claim that contradicts the session's known path
        //     refuses (foreign). A path claim a PATHLESS session cannot verify refuses too, with a note that
        //     says how to claim (open the .pbix directly once — the path capture then proves it — or re-save).
        //     A pathless stamp under the same stem serves: the NARROWED disclosed residual (a pure dialog-open
        //     lifecycle keeps its primer; two pathless same-stem models stay indistinguishable).
        //   INVALID stamp (unreadable, hash-less, or hash-mismatched): UNPROVABLE — never served, never
        //     authorizes a twin; the note says to re-save.
        //   ABSENT stamp: no claim at all — a PATH key still governs (the canonical path key is strong evidence
        //     on its own), but a STEM key without a claim is REFUSED as unclaimed: serving it would let
        //     deleting a stamp launder an Invalid (refused) pair into a served one, reopening the same-stem hole.
        // Writes order temp-write both → move md → move stamp; a stamp-write failure is SURFACED as a save
        // warning. Whatever stamp already exists is LEFT IN PLACE on failure — deleting it could destroy a
        // concurrent engine's just-written valid claim; the hash mismatch already refuses the torn pair safely.
        private sealed class PrimerIdentityStamp
        {
            public string PbixPath { get; set; }     // full path when the writing session knew it
            public string Stem { get; set; }
            public string KeySource { get; set; }    // "path" | "title"
            public string ContentHash { get; set; }  // SHA-256 (hex) of the exact md content this stamp vouches for
        }

        private enum StampState { Absent, Invalid, Valid }

        private static string Sha256Hex(string content)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""))).ToLowerInvariant();
        }

        private static string StampFileFor(string primerFile) => Path.ChangeExtension(primerFile, ".identity.json");

        private static PrimerIdentityStamp ReadStampRaw(string primerFile)
        {
            var file = StampFileFor(primerFile);
            if (!File.Exists(file)) return null;
            try { return JsonSerializer.Deserialize<PrimerIdentityStamp>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return null; }
        }

        /// <summary>The stamp VALIDATED against the markdown it must vouch for. Present-but-unreadable,
        /// hash-less and hash-mismatched all read as Invalid: a torn pair never positively authorizes.</summary>
        private static (StampState State, PrimerIdentityStamp Stamp) ReadStampFor(string primerFile, string markdown)
        {
            if (!File.Exists(StampFileFor(primerFile))) return (StampState.Absent, null);
            var stamp = ReadStampRaw(primerFile);
            if (stamp == null || string.IsNullOrEmpty(stamp.ContentHash)
                || !string.Equals(stamp.ContentHash, Sha256Hex(markdown), StringComparison.OrdinalIgnoreCase))
                return (StampState.Invalid, null);
            return (StampState.Valid, stamp);
        }

        /// <summary>Write the primer + its provenance stamp as close to atomically as a filesystem allows:
        /// temp-write BOTH, move the md, move the stamp. Returns a WARNING string when the stamp could not be
        /// written (never swallowed — the caller surfaces it so the user knows provenance is stale), null on
        /// success. A reader landing between the two moves sees old-stamp/new-md = a hash mismatch = Unprovable.</summary>
        private static string WritePrimerWithStamp(string file, string markdown, PrimerLocus locus)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var md = markdown.Replace("\r\n", "\n");
            var mdTemp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(mdTemp, md, new System.Text.UTF8Encoding(false));
            string warning = null, stampTemp = null, stampFile = null;
            if (locus.IsLocal)   // cloud/disk keys carry their identity in the key itself — no stamp
            {
                stampFile = StampFileFor(file);
                try
                {
                    var stamp = new PrimerIdentityStamp
                    {
                        PbixPath = locus.LocalPath,
                        Stem = locus.LocalStem,
                        KeySource = locus.LocalPath != null ? "path" : "title",
                        ContentHash = Sha256Hex(md),
                    };
                    stampTemp = stampFile + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(stampTemp, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex) { stampTemp = null; warning = StampWriteWarning(ex); }
            }
            File.Move(mdTemp, file, true);
            if (stampTemp != null)
            {
                try { File.Move(stampTemp, stampFile, true); }
                catch (Exception ex) { warning = StampWriteWarning(ex); }
            }
            // Whatever stamp exists after a FAILED stamp write is deliberately left in place: another engine may
            // have just written a VALID pair (its stamp is its claim; deleting it would destroy a success it
            // already reported). The stale-stamp torn pair this leaves behind reads as Invalid — refused safely.
            return warning;
        }

        /// <summary>Write the Primer as a TRACKED change (F-353): one step on the model's undo timeline and one History
        /// entry with its real author and a before and after per changed section, from either door. Undo puts back the
        /// exact files that were there (or none), stamp included; redo puts the written pair back. The model itself
        /// is not edited, so a saved model stays saved. <paramref name="shown"/> is the Primer the writer was looking
        /// at (the served starter when there was no file), so the History compares what they saw with what they wrote.
        /// A write that changes nothing is written untracked: a History row saying nothing happened is noise.</summary>
        /// <param name="sideFile">The suggestion list, when this step also decides one suggestion in it: the step owns
        /// that ONE entry, <paramref name="sideId"/> set to <paramref name="sideDecision"/>, so undoing an accepted
        /// suggestion puts it back to waiting and redo accepts it again, and another suggestion rejected meanwhile (written
        /// off the timeline) stays rejected and does not lock Undo (verifier on m11/sol-fixes-1, D1).</param>
        private static async Task<string> CommitPrimerAsync(Session session, string file, string markdown, string shown, PrimerLocus locus, string origin, string label,
                                                            string sideFile = null, string sideId = null, string sideDecision = null)
        {
            var after = markdown.Replace("\r\n", "\n");
            var before = (shown ?? "").Replace("\r\n", "\n");
            if (sideFile == null && File.Exists(file) && string.Equals(before, after, StringComparison.Ordinal)) return WritePrimerWithStamp(file, after, locus);
            var stampFile = locus.IsLocal ? StampFileFor(file) : null;
            string warning = null;
            await session.MutateBesideModelAsync(string.IsNullOrWhiteSpace(origin) ? "human" : origin, label, m =>
            {
                var oldMd = ReadOrNull(file);
                var oldStamp = stampFile == null ? null : ReadOrNull(stampFile);
                var oldDecision = sideFile == null ? null : ReadSuggestionDecision(sideFile, sideId);
                warning = WritePrimerWithStamp(file, after, locus);
                if (sideFile != null) PutSuggestionDecision(sideFile, sideId, sideDecision);
                // Every file this step touched, with what it held before and after, so undo and redo refuse instead of
                // overwriting one that was edited on disk since (Sol on m11/integration 568435d3, finding 2). The after
                // text is read back, so it is what is on disk, not what was meant to be. Each is named by its path at
                // undo time, which a Save As carrying it to the new model's name moves (SidecarUndo.Rebase).
                var touched = new System.Collections.Generic.List<TabularEditor.TOMWrapper.Utils.SidecarFile>
                {
                    new TabularEditor.TOMWrapper.Utils.SidecarFile("The Primer file", file, oldMd, ReadOrNull(file), ReadOrNull, PutBack),
                };
                if (stampFile != null)
                    touched.Add(new TabularEditor.TOMWrapper.Utils.SidecarFile("The Primer's identity file", stampFile, oldStamp, ReadOrNull(stampFile), ReadOrNull, PutBack));
                if (sideFile != null)
                    touched.Add(new TabularEditor.TOMWrapper.Utils.SidecarFile("The Primer's suggestion list", sideFile, sideId, oldDecision,
                        ReadSuggestionDecision(sideFile, sideId), path => ReadSuggestionDecision(path, sideId), (path, value) => PutSuggestionDecision(path, sideId, value)));
                TabularEditor.TOMWrapper.Utils.SidecarUndo.Register(m, label, touched);
                foreach (var d in PrimerDeltas(before, after)) session.RecordDelta(d);
            });
            return warning;
        }

        /// <summary>One suggestion's decision in the suggestion list ("accepted|when"), or null when it has none. A list that
        /// is there but cannot be read gives <see cref="UnreadableList"/>, which no decision equals, so an undo that would
        /// rewrite it is refused rather than guessing.</summary>
        private static string ReadSuggestionDecision(string file, string id)
        {
            if (!File.Exists(file)) return null;
            try
            {
                var state = JsonSerializer.Deserialize<PrimerSuggestionState>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return state?.Decisions != null && state.Decisions.TryGetValue(id, out var v) ? v : null;
            }
            catch (Exception) { return UnreadableList; }
        }

        private const string UnreadableList = "(the suggestion list could not be read)";

        /// <summary>Set one suggestion's decision (null takes it away, so the suggestion waits again), leaving every other
        /// entry as it is on disk now. A list left with no decisions is removed, as it was before the first one.</summary>
        private static void PutSuggestionDecision(string file, string id, string decision)
        {
            var state = ReadSuggestionState(file);
            if (decision == null) state.Decisions.Remove(id);
            else state.Decisions[id] = decision;
            PutBack(file, state.Decisions.Count == 0 ? null : JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string ReadOrNull(string file) => File.Exists(file) ? File.ReadAllText(file) : null;

        // Undo and redo restore a file exactly: its text, or its absence. Temp-then-move, like the forward write.
        private static void PutBack(string file, string text)
        {
            if (text == null) { if (File.Exists(file)) File.Delete(file); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, file, true);
        }

        /// <summary>The History's before and after for a Primer write: one delta per part that changed (the title,
        /// then each of the six sections), so a one-line edit at the bottom is not hidden behind a capped whole file.</summary>
        internal static System.Collections.Generic.IEnumerable<ChangeDelta> PrimerDeltas(string before, string after)
        {
            var was = PrimerParts(before);
            var now = PrimerParts(after);
            foreach (var part in new[] { "Title" }.Concat(PrimerContract.Sections))
            {
                was.TryGetValue(part, out var b);
                now.TryGetValue(part, out var a);
                if (string.Equals(b, a, StringComparison.Ordinal)) continue;
                yield return new ChangeDelta
                {
                    Kind = "update", Ref = PrimerRef, Props = new[] { part },
                    Before = ChangeValues.Cap(b, ChangeValues.WireCap), After = ChangeValues.Cap(a, ChangeValues.WireCap),
                };
            }
        }

        /// <summary>The ref a Primer change carries in History and on the wire (get_plan_history objectRef="primer").</summary>
        public const string PrimerRef = "primer";

        private static System.Collections.Generic.Dictionary<string, string> PrimerParts(string markdown)
        {
            var parts = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            string current = "Title";
            var body = new System.Text.StringBuilder();
            void Flush() { parts[current] = body.ToString().Trim(); body.Clear(); }
            foreach (var line in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal)) { Flush(); current = line.Substring(3).Trim(); continue; }
                body.Append(line).Append('\n');
            }
            Flush();
            return parts;
        }

        // Fixed text across the door — an exception message can carry absolute sidecar/temp paths, which must not
        // leak to the caller. The diagnostic goes to the engine's internal stderr lane, secret-scrubbed.
        private static string StampWriteWarning(Exception ex)
        {
            try { Console.Error.WriteLine("[primer] provenance stamp write failed: " + XmlaAuthHint.Scrub(ex.Message)); } catch { }
            return "Warning: the Primer was saved, but its provenance stamp could not be written. Save it again to re-claim it.";
        }

        private static bool SamePbix(string a, string b)
            => a != null && b != null && string.Equals(CanonAnchor(a), CanonAnchor(b), StringComparison.Ordinal);

        /// <summary>The stem-keyed TWIN of a path-keyed locus (same model saved by a session that could not see the
        /// path), or null when there is no distinct twin to probe.</summary>
        private static string StemTwinFile(PrimerLocus locus)
        {
            if (!locus.IsLocal || locus.LocalPath == null || locus.LocalStem == null || locus.File == null) return null;
            var twin = Path.Combine(Path.GetDirectoryName(locus.File), SafeKeyName("local:" + Sha8(NormLivePart(locus.LocalStem))) + ".md");
            return string.Equals(twin, locus.File, StringComparison.OrdinalIgnoreCase) ? null : twin;
        }

        // Pre-fix local primers were keyed by endpoint|database (a live-*.md filename) and are orphaned by the key
        // change. They are NEVER silently adopted: a legacy file names no model, a shared workspace sidecar can hold
        // another (even a cloud) model's primer, and two engines racing an adopt-by-rename would be nondeterministic.
        // Surfaced honestly instead — the user re-saves to migrate. local: lane only: for cloud sessions live-*.md
        // IS the current key shape, so the note would misfire there.
        private static string LegacyPrimerNote(string identity, string file)
        {
            if (identity == null || !identity.StartsWith("local:", StringComparison.Ordinal)) return null;
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (dir == null || !Directory.Exists(dir) || Directory.GetFiles(dir, "live-*.md").Length == 0) return null;
                return "No Primer is saved under this model's key yet, but the project sidecar contains one or more Primers saved by an earlier version (primers/live-*.md). If one of them belongs to this model, save its content again (set_model_primer) to migrate it.";
            }
            catch { return null; }
        }

        private static string SuggestionFileFor(PrimerLocus locus)
            => locus.File == null ? null : Path.ChangeExtension(locus.File, ".suggestions.json");

        public Task<PrimerDocument> GetPrimerAsync()
        {
            var context = _sessions.CurrentContext;   // ONE capture: identity, sidecar and the name read agree on the model
            return GetPrimerCoreAsync(context, PrimerLocusFor(context));
        }

        // Every chained primer operation derives the locus ONCE and threads the SAME (context, locus) pair through
        // each helper it touches — no helper recomputes identity mid-operation (SessionContext.Live is mutable, so
        // a recompute could pair one connection's identity with another's files halfway through a decision).
        private async Task<PrimerDocument> GetPrimerCoreAsync(SessionContext context, PrimerLocus locus)
        {
            var session = context.Session;
            if (session == null) return new PrimerDocument { Note = "Open a model to read its Primer." };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var (identity, file) = (locus.Identity, locus.File);
            if (identity == null)
                return new PrimerDocument { ModelName = modelName, Markdown = PrimerContract.Template(modelName), Note = "Save or connect the model before writing its Primer." };
            if (file == null)
                return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, Markdown = PrimerContract.Template(modelName), Note = "Open a workspace before writing this live model's Primer." };
            if (!File.Exists(file))
            {
                // A path-keyed model's primer may sit under its STEM key (saved by an earlier session that could not
                // see the path). Serve it ONLY when a VALID stamp proves it is this model's file; a proven-foreign
                // stem file is silently ignored, and anything unprovable is flagged honestly, never served.
                var twin = StemTwinFile(locus);
                string twinNote = null;
                if (twin != null && File.Exists(twin))
                {
                    string twinMd = null;
                    try { twinMd = await File.ReadAllTextAsync(twin); } catch { /* unreadable twin = unprovable below */ }
                    var (twinState, twinStamp) = twinMd == null ? (StampState.Invalid, (PrimerIdentityStamp)null) : ReadStampFor(twin, twinMd);
                    if (twinState == StampState.Valid && twinStamp.PbixPath != null && SamePbix(twinStamp.PbixPath, locus.LocalPath))
                        return BuildPrimerDoc(twin, twinMd, modelName, identity,
                            "This Primer was saved under the model's display name before its file identity was known. Save it again to store it under the file identity.");
                    if (!(twinState == StampState.Valid && twinStamp.PbixPath != null))   // valid-with-OTHER-path = provably foreign: silent
                        twinNote = "A Primer saved under this model's display name exists (" + Path.GetFileName(twin) + ") but cannot be verified as this model's. If it is yours, save its content again to claim it for this model.";
                }
                var note = string.Join(" ", new[] { twinNote, LegacyPrimerNote(identity, file) }.Where(n => n != null));
                return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = PrimerContract.Template(modelName), Exists = false, Note = note.Length == 0 ? null : note };
            }
            string markdown;
            try { markdown = await File.ReadAllTextAsync(file); }
            catch (Exception ex) { return new PrimerDocument { ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = PrimerContract.Template(modelName), Note = "The Primer could not be read: " + ex.Message }; }
            if (locus.IsLocal)
            {
                // The stamp rule table for a LOCAL primary read (fail closed; the md was read FIRST so the stamp is
                // validated against the exact content it must vouch for):
                //   Invalid                                  → unprovable, refuse (re-save repairs).
                //   Absent, PATH-keyed locus                 → serve (the canonical path key is strong evidence alone).
                //   Absent, STEM-keyed locus                 → refuse as unclaimed: serving would let a deleted stamp
                //                                              launder an Invalid pair into a served one (same-stem hole).
                //   Valid, path claim, session path unknown  → refuse: this session cannot prove same-file.
                //   Valid, path claim, session path differs  → refuse: provably another model's (foreign).
                //   Valid pathless                           → serve (the disclosed same-stem residual).
                var (state, stamp) = ReadStampFor(file, markdown);
                if (state == StampState.Invalid)
                    return new PrimerDocument
                    {
                        ModelName = modelName, ModelIdentity = identity, FilePath = file,
                        Markdown = PrimerContract.Template(modelName), Exists = false,
                        Note = "This Primer's provenance stamp does not match its content (an interrupted save, or files copied from another sidecar), so it cannot be verified as this model's and is not shown. If it is yours, save the Primer again to repair it.",
                    };
                if (state == StampState.Absent && locus.LocalPath == null)
                    return new PrimerDocument
                    {
                        ModelName = modelName, ModelIdentity = identity, FilePath = file,
                        Markdown = PrimerContract.Template(modelName), Exists = false,
                        Note = "This Primer predates provenance stamping or its stamp was removed, so it cannot be verified as this model's and is not shown. If it is yours, save it again to claim it.",
                    };
                if (state == StampState.Valid && stamp.PbixPath != null)
                {
                    if (locus.LocalPath == null)
                        return new PrimerDocument
                        {
                            ModelName = modelName, ModelIdentity = identity, FilePath = file,
                            Markdown = PrimerContract.Template(modelName), Exists = false,
                            Note = "A Primer for \"" + Path.GetFileName(stamp.PbixPath) + "\" exists under this model's display name, but this session cannot prove it is the same file. If it is this model's, save the Primer again to claim it.",
                        };
                    if (!SamePbix(stamp.PbixPath, locus.LocalPath))
                        return new PrimerDocument
                        {
                            ModelName = modelName, ModelIdentity = identity, FilePath = file,
                            Markdown = PrimerContract.Template(modelName), Exists = false,
                            Note = "A Primer under this key belongs to a different model file (" + Path.GetFileName(stamp.PbixPath) + ") that shares this model's display name, so it is not shown. Saving this model's Primer will replace it.",
                        };
                }
            }
            return BuildPrimerDoc(file, markdown, modelName, identity, null);
        }

        private static PrimerDocument BuildPrimerDoc(string file, string markdown, string modelName, string identity, string note)
        {
            try { PrimerContract.Validate(markdown); }
            catch (Exception ex) { note = ex.Message + " Edit and save it to restore the fixed structure." + (note == null ? "" : " " + note); }
            return new PrimerDocument
            {
                ModelName = modelName, ModelIdentity = identity, FilePath = file, Markdown = markdown, Exists = true,
                UpdatedUtc = File.GetLastWriteTimeUtc(file).ToString("o"), Note = note,
            };
        }

        public async Task<PrimerDocument> SetPrimerAsync(string markdown, string origin, string expectedMarkdown = null)
        {
            GuardOutsideWrite(origin);
            PrimerContract.Validate(markdown);
            var context = _sessions.CurrentContext;
            var session = context.Session ?? throw new InvalidOperationException("Open a model before writing its Primer.");
            var locus = PrimerLocusFor(context);
            var file = locus.File;
            if (file == null) throw new InvalidOperationException("This model has no project location for its Primer. Save the model or open a workspace and retry.");
            string warning;
            await _primerGate.WaitAsync();
            try
            {
                // A model OR connection swapped in between the derivation and the write must abort — never write one
                // model's Primer under the key/sidecar derived for another. BOTH captured references are verified:
                // the context (session swap) and the connection the identity may have derived from (a concurrent
                // connect_local swaps context.Live without replacing the context).
                EnsureContextCurrent(context, locus.Live, "Primer save");
                var shown = (await GetPrimerCoreAsync(context, locus)).Markdown;
                // the Primer the person was asked about replacing: words they wrote since are theirs
                RequireApprovedBasis(nameof(SetPrimerAsync), GateHash(shown), "Primer");
                GuardExpectedValue("Primer", expectedMarkdown, shown, markdown);   // the editor's starting point against the Primer as it stands inside this write
                warning = await CommitPrimerAsync(session, file, markdown, shown, locus, origin, "Update the model Primer");
            }
            finally { _primerGate.Release(); }
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Kind = "set_model_primer", Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = "Updated the model Primer", Target = session.Id, Ok = true,
            });
            var doc = await GetPrimerCoreAsync(context, locus);   // the SAME captured pair, end to end
            if (warning != null) doc.Note = doc.Note == null ? warning : doc.Note + " " + warning;
            return doc;
        }

        public Task<PrimerSuggestionList> ListPrimerSuggestionsAsync()
        {
            var context = _sessions.CurrentContext;   // one capture for the whole read (identity + insights fingerprint)
            return ListPrimerSuggestionsCoreAsync(context, PrimerLocusFor(context));
        }

        private async Task<PrimerSuggestionList> ListPrimerSuggestionsCoreAsync(SessionContext context, PrimerLocus locus)
        {
            var primer = await GetPrimerCoreAsync(context, locus);
            if (context.Session == null)
                return new PrimerSuggestionList { IsPro = true, Note = "Open a model to review Primer suggestions." };

            // The shape fingerprint here is the INSIGHT-recall key (KnowledgeStore scoping), not the primer's
            // filename identity — insights are deliberately shape-scoped, primers provenance-scoped.
            var fp = await context.Session.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m).FingerprintKey);
            var handled = ReadSuggestionState(SuggestionFileFor(locus));
            var records = new System.Collections.Generic.List<InsightRecord>();
            foreach (var scope in new[] { "project", "global" })
            {
                var (live, _) = KnowledgeStore.Materialize(ScopeFile(scope), scope);
                records.AddRange(live.Where(x => KnowledgeStore.Recallable(x)
                    && string.Equals(x.Fingerprint, fp, StringComparison.Ordinal)));
            }
            var suggestions = records
                .Where(x => !handled.Decisions.ContainsKey(x.Id))
                .OrderByDescending(x => x.LastUsedUtc, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .Select(ToPrimerSuggestion)
                .ToArray();
            return new PrimerSuggestionList
            {
                IsPro = true,
                Suggestions = suggestions,
                Sections = PrimerContract.Sections.Select(section => new PrimerSectionFreshness
                {
                    Section = section,
                    SuggestedAdditions = suggestions.Count(x => x.Section == section),
                    PrimerUpdatedUtc = primer.UpdatedUtc,
                }).ToArray(),
                Note = suggestions.Length == 0 ? "No learning is waiting to update this model's Primer." : null,
            };
        }

        public Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin)
        {
            GuardOutsideWrite(origin);
            return DecidePrimerSuggestionAsync(id, true, origin);
        }

        public Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin)
        {
            GuardOutsideWrite(origin);
            return DecidePrimerSuggestionAsync(id, false, origin);
        }

        private async Task<PrimerSuggestionDecision> DecidePrimerSuggestionAsync(string id, bool accept, string origin)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Choose a Primer suggestion to accept or reject.");
            // ONE context, captured BEFORE the suggestion is located: the suggestion lookup, the primer write, the
            // state read and the state write all derive from the same identity, so the pair (primer file,
            // suggestions file) can never split across models — and a model swapped in AFTER this capture cannot
            // have a prior model's suggestion applied to it (the list below reflects the new model, so the id
            // misses; a swap after the list aborts at the gate).
            var context = _sessions.CurrentContext;
            var locus = PrimerLocusFor(context);
            var file = locus.File;
            var suggestionFile = SuggestionFileFor(locus);
            if (file == null) throw new InvalidOperationException("This model has no project location for its Primer. Save the model or open a workspace and retry.");
            // The SAME captured pair drives the list, the primer read, the write, and both state accesses — no
            // helper recomputes identity mid-decision (context.Live is mutable under a concurrent connect).
            var list = await ListPrimerSuggestionsCoreAsync(context, locus);
            var suggestion = list.Suggestions.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("This Primer suggestion is no longer waiting. Refresh the Primer and choose a current suggestion.");
            // UAT-S06a-1-002: the assistant cannot put its own unreviewed lesson into the Primer. A person's Accept is
            // the review, so the UI door stays one click; rejecting stays open to both doors.
            var byPerson = KnowledgeStore.ByPerson(string.IsNullOrWhiteSpace(origin) ? "human" : origin);
            if (accept && suggestion.NeedsReview && !byPerson)
                throw new InvalidOperationException("Your assistant wrote this lesson and no person has approved it yet, so only a person can add it to the Primer. Ask them to accept it in Model notes.");

            PrimerDocument primer = null;
            string warning = null;
            await _primerGate.WaitAsync();
            try
            {
                // Both captured references verified (see SetPrimerAsync): a session OR connection swap aborts.
                EnsureContextCurrent(context, locus.Live, "Primer suggestion decision");
                var state = ReadSuggestionState(suggestionFile);
                if (!accept) RequireApprovedBasis(nameof(RejectPrimerSuggestionAsync), SuggestionBasis(suggestion), "suggestion");
                state.Decisions[id] = (accept ? "accepted" : "rejected") + "|" + DateTime.UtcNow.ToString("o");
                if (accept)
                {
                    // The SERVED read, not the raw file: a same-key file the rule table refuses (foreign or
                    // unprovable) must not be adopted as the base — the suggestion then lands on the template.
                    // The decision rides the same undo step as the words, so undoing the accept puts the suggestion
                    // back to waiting and redo accepts it again (F-353).
                    if (suggestionFile == null) throw new InvalidOperationException("This model has no project location for Primer suggestions.");
                    var current = (await GetPrimerCoreAsync(context, locus)).Markdown;
                    RequireApprovedBasis(nameof(AcceptPrimerSuggestionAsync), GateHash(SuggestionBasis(suggestion), GateHash(current)), "suggestion or Primer");
                    warning = await CommitPrimerAsync(context.Session, file, PrimerContract.ApplySuggestion(current, suggestion), current, locus, origin,
                        "Add a suggestion to the Primer's " + suggestion.Section,
                        suggestionFile, id, state.Decisions[id]);
                }
                else await WriteSuggestionStateAsync(state, suggestionFile);
            }
            finally { _primerGate.Release(); }
            // The person just read and accepted the lesson, so recall stops calling it unreviewed too.
            if (accept && suggestion.NeedsReview && byPerson)
            {
                try { await ApproveInsightAsync(id, "human"); }
                catch (InvalidOperationException) { /* the lesson left the store meanwhile; the Primer already has the words */ }
            }
            // The whole of Model notes is free since 22 September, the queue and the document alike, so an
            // accepted suggestion returns the Primer it just changed. This used to be gated because
            // get_model_primer refused a free read of this exact file and returning it here was that same read
            // through a side entrance (Astra B1); neither half refuses now, so there is no side entrance left.
            if (accept)
                primer = await GetPrimerCoreAsync(context, locus);
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Kind = accept ? "accept_primer_suggestion" : "reject_primer_suggestion",
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Label = (accept ? "Accepted" : "Rejected") + " a suggested Primer update",
                Target = id, Ok = true,
            });
            return new PrimerSuggestionDecision
            {
                Changed = accept,
                Decision = accept ? "accepted" : "rejected",
                Primer = primer,
                Note = (accept ? "The reviewed suggestion was added to the " + suggestion.Section + " section." : "The suggestion was dismissed and will not be offered again for this model.")
                     + (warning == null ? "" : " " + warning),
            };
        }

        private static PrimerSuggestion ToPrimerSuggestion(InsightRecord insight)
        {
            var runs = (insight.Provenance?.SourceRunIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            var captured = insight.Provenance?.When;
            var evidenceCount = Math.Max(1, Math.Max(insight.Uses, runs.Length));
            var source = runs.Length > 0 ? runs.Length + " source run" + (runs.Length == 1 ? "" : "s") : "captured learning";
            var provenance = source + (string.IsNullOrWhiteSpace(captured) ? "" : " · " + captured);
            var text = (insight.Text ?? string.Empty).Replace("—", "-").Trim();
            var markdown = "- " + text + "\n  _Provenance: " + provenance + "._";
            return new PrimerSuggestion
            {
                Id = insight.Id,
                Section = PrimerContract.SuggestionSection(insight),
                Markdown = markdown,
                CapturedUtc = captured,
                Origin = insight.Provenance?.Origin,
                SourceRunIds = runs,
                EvidenceCount = evidenceCount,
                Provenance = provenance,
                NeedsReview = insight.Status != KnowledgeStore.Approved,   // no person has approved these words yet
            };
        }

        // State helpers take the FILE resolved from the caller's captured locus — they never recompute identity.
        private static PrimerSuggestionState ReadSuggestionState(string file)
        {
            if (file == null || !File.Exists(file)) return new PrimerSuggestionState();
            try { return JsonSerializer.Deserialize<PrimerSuggestionState>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrimerSuggestionState(); }
            catch { return new PrimerSuggestionState(); }
        }

        private static async Task WriteSuggestionStateAsync(PrimerSuggestionState state, string file)
        {
            if (file == null) throw new InvalidOperationException("This model has no project location for Primer suggestions.");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            File.Move(temp, file, true);
        }
    }
}
