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
        //   1. the full .pbix PATH (LiveOrigin.LocalPath) — strongest, distinct across same-named files;
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
        private static async Task<string> WritePrimerWithStampAsync(string file, string markdown, PrimerLocus locus)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var md = markdown.Replace("\r\n", "\n");
            var mdTemp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(mdTemp, md, new System.Text.UTF8Encoding(false));
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
                    await File.WriteAllTextAsync(stampTemp, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
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
                            Note = "A Primer for \"" + Path.GetFileName(stamp.PbixPath) + "\" exists under this model's display name, but this session cannot prove it is the same file. Open the .pbix file directly (double-click it) once, or save the Primer again, to claim it.",
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

        public async Task<PrimerDocument> SetPrimerAsync(string markdown, string origin)
        {
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
                warning = await WritePrimerWithStampAsync(file, markdown, locus);
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
                return new PrimerSuggestionList { IsPro = _entitlement?.IsPro == true, Note = "Open a model to review Primer suggestions." };
            if (_entitlement?.IsPro != true)
                return new PrimerSuggestionList { IsPro = false, Note = "Suggested Primer updates are a Pro feature. You can still read and edit the Primer manually." };

            // The shape fingerprint here is the INSIGHT-recall key (KnowledgeStore scoping), not the primer's
            // filename identity — insights are deliberately shape-scoped, primers provenance-scoped.
            var fp = await context.Session.ReadAsync(m => KnowledgeStore.ComputeFingerprint(m).FingerprintKey);
            var handled = ReadSuggestionState(SuggestionFileFor(locus));
            var records = new System.Collections.Generic.List<InsightRecord>();
            foreach (var scope in new[] { "project", "global" })
            {
                var (live, _) = KnowledgeStore.Materialize(ScopeFile(scope), scope);
                records.AddRange(live.Where(x => string.Equals(x.Status, "approved", StringComparison.Ordinal)
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
                Note = suggestions.Length == 0 ? "No reviewed learning is waiting to update this model's Primer." : null,
            };
        }

        public Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin)
            => DecidePrimerSuggestionAsync(id, true, origin);

        public Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin)
            => DecidePrimerSuggestionAsync(id, false, origin);

        private async Task<PrimerSuggestionDecision> DecidePrimerSuggestionAsync(string id, bool accept, string origin)
        {
            if (_entitlement?.IsPro != true)
                return new PrimerSuggestionDecision { Changed = false, Note = "Suggested Primer updates are a Pro feature. Edit the Primer manually to make this change without Pro." };
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

            PrimerDocument primer = null;
            string warning = null;
            await _primerGate.WaitAsync();
            try
            {
                // Both captured references verified (see SetPrimerAsync): a session OR connection swap aborts.
                EnsureContextCurrent(context, locus.Live, "Primer suggestion decision");
                if (accept)
                {
                    // The SERVED read, not the raw file: a same-key file the rule table refuses (foreign or
                    // unprovable) must not be adopted as the base — the suggestion then lands on the template.
                    var current = (await GetPrimerCoreAsync(context, locus)).Markdown;
                    warning = await WritePrimerWithStampAsync(file, PrimerContract.ApplySuggestion(current, suggestion), locus);
                }
                var state = ReadSuggestionState(suggestionFile);
                state.Decisions[id] = (accept ? "accepted" : "rejected") + "|" + DateTime.UtcNow.ToString("o");
                await WriteSuggestionStateAsync(state, suggestionFile);
            }
            finally { _primerGate.Release(); }
            if (accept) primer = await GetPrimerCoreAsync(context, locus);
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
