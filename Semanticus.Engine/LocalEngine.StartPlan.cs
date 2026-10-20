// The start plan's engine verbs (design-spec-first-start-v2.md). Lane sf-core owns the START, the DOCUMENT's life
// (keep, reload after a restart, discard), the PARK hook and the SCRATCH; the verbs at the bottom that other lanes own
// are still contract stubs here and throw NotImplemented until those lanes fill them in.
//
// THE RULES THIS FILE KEEPS.
//  - A start writes only into the folder it checked was empty and into the state dir. It never writes beside another
//    project (defect 1: a first save used to land on another PBIP's definition/). The folder must be empty or absent,
//    it is checked again after the schema read, and the save goes to <folder>/<Name>.SemanticModel/definition, a path
//    that did not exist a moment ago, so nothing can be replaced.
//  - The schema is read BEFORE anything is created. A failed sign-in, an unreachable server or an empty source leaves
//    the open model, the folder and the state dir exactly as they were. (The design lists the read after the first
//    save; reading first costs nothing and makes a failed read side-effect free.)
//  - The assistant's door never discards the person's open work: a start with unsaved changes is refused there, and
//    refused on the person's door too unless the person's own confirmation says to discard them.
//  - ONE plan at a time, and ONE start at a time: a second start (or a start over a live plan) is refused in plain
//    words rather than queued or replaced.
//  - Opening, creating or restoring a model parks the plan (the one call in SwapSessionCoreAsync): the decide loop's
//    token is cancelled and the scratch is drained and disposed BEFORE the replacement handler is built (S6). The
//    document is kept. Whether the plan is parked is always worked out from the model that is open NOW, so a failed
//    open does not leave it parked and opening the destination again resumes it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine : IStartPlanEngine
    {
        private readonly object _startPlanLock = new object();
        private volatile bool _startPlanDisposed;
        private readonly SemaphoreSlim _startPlanGate = new SemaphoreSlim(1, 1);
        private StartPlanDoc _startPlan;
        private StartPlanScratch _startScratch;
        private CancellationTokenSource _startLoop;
        // Bumped on every park or discard, so a scratch that finished building after one is dropped, not kept.
        private long _startPlanEpoch;
        // The id of a plan this engine read from disk that said "deciding" (the engine that decided it is gone, so no loop of this one is running it), until a decide is
        // started for it. A read of such a plan says it stopped (join 8, K2); a plan this engine decided itself is never named here.
        private string _startPlanLoadedDeciding;

        // Test seam (InternalsVisibleTo): replaces the sign-in and the TDS schema read, so the whole start is
        // exercised with no network. Production reads through AcquireSqlTokenAsync and FabricSqlSchema.
        internal Func<StartModelRequest, string, Task<FabricSchema>> StartPlanSchemaReaderForTest;
        /// <summary>Test seam: the same, for a read that can be ended (it is handed the token Cancel and the time limit use). Wins over the one above.</summary>
        internal Func<StartModelRequest, string, CancellationToken, Task<FabricSchema>> StartPlanSchemaReaderCtForTest;
        /// <summary>Test seam: how long the source read may take before the start ends on its own. Null keeps the real limit.</summary>
        internal TimeSpan? StartReadLimitForTest;

        // The read of the source (sign-in, then the schema) that a start is waiting on, so Cancel can end it. A typed endpoint opens a browser sign-in; when the person
        // closes that browser nothing ever comes back, and without this the start held the one start slot until VS Code restarted (hero run 5, 9 Oct).
        private CancellationTokenSource _startRead;
        private string _startReadOrigin;
        private static readonly TimeSpan StartSignInLimit = TimeSpan.FromMinutes(3);

        // Ends the read a start is waiting on. The person's Cancel ends any; an assistant ends only its own, so it cannot stop what the person started.
        private bool CancelStartRead(string origin)
        {
            CancellationTokenSource read; string by;
            lock (_startPlanLock) { read = _startRead; by = _startReadOrigin; }
            if (read == null) return false;
            var human = string.Equals(origin ?? "human", "human", StringComparison.OrdinalIgnoreCase);
            if (!human && !string.Equals(origin, by, StringComparison.OrdinalIgnoreCase)) return false;
            try { read.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
        }

        /// <summary>Test seam: a fixed identity seed, so two starts of the same source make the same relationship names and lineage tags, and a recorded Jev run
        /// replays against the second. Production draws a random seed.</summary>
        internal string StartPlanSeedForTest;

        internal StartPlanDoc StartPlanDocForTest() { lock (_startPlanLock) return StartPlanStore.Copy(_startPlan); }
        /// <summary>A test changes the held document. A test that moves the plan out of waiting_grant stands in for the person's grant, so the document it leaves
        /// carries a Disclosure that names exactly what the plan then discloses (the loop asks nothing without one). A test that needs the OTHER case
        /// (no grant) builds its own document, or never leaves waiting_grant.</summary>
        internal void StartPlanDocForTestMutate(Action<StartPlanDoc> change)
        {
            StartPlanDoc granted = null;
            lock (_startPlanLock)
            {
                change(_startPlan);
                var d = _startPlan;
                var asking = d.Decide != null && d.Decide.State != StartDecideState.WaitingGrant && d.Decide.State != StartDecideState.Off;
                if (asking && d.Disclosure == null) d.Disclosure = StartPlanDecide.DisclosureFor(d, StartSampleOn(d));
                if (asking) granted = StartPlanStore.Copy(d);
                StartPlanStore.Write(d);
            }
            // The grant the person gives is kept in TWO places: the plan's own copy (above, for the view) and the record beside the model's permission, which is the
            // one every ask is checked against. A test that stands in for the grant stands in for both, naming exactly what the plan's Disclosure names, so the
            // real fence admits what the plan discloses and refuses what it does not.
            if (granted?.Disclosure != null)
            {
                var scope = DecisionConsentScopeAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(scope))
                {
                    var consent = Decisions.ReadConsent(scope);
                    if (consent.Granted) StartDisclosureStore.Grant(scope, granted.Id, DisclosedScope.Of(granted.Disclosure), consent.Generation, DateTime.UtcNow);
                }
            }
        }

        // ---------------------------------------------------------------- start

        public async Task<StartPlanView> StartModelFromSqlAsync(StartModelRequest request, string origin)
        {
            GuardOutsideWrite(origin);
            if (request == null) throw new ArgumentException("A start needs a source and a place to keep the model.");
            var agent = !string.Equals(origin ?? "human", "human", StringComparison.OrdinalIgnoreCase);
            var server = request.Server?.Trim();
            var database = request.Database?.Trim();
            if (string.IsNullOrWhiteSpace(server)) throw new ArgumentException("A Fabric SQL endpoint is required.");
            if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("A Fabric warehouse or lakehouse database is required.");
            // D1 (launch video, 12 Oct): no sign-in named means the one that is ready (the person's saved sign-in, else a ready service principal), the pick the
            // endpoint listing makes. az is the last choice, kept for a person who has only that. A mode the caller names is theirs.
            if (string.IsNullOrWhiteSpace(request.AuthMode))
            {
                var (pickedMode, pickedTenant) = ResolveSqlListSignIn(null, request.TenantId);
                request.AuthMode = pickedMode ?? "azcli";
                request.TenantId = pickedTenant;
            }
            var storage = string.IsNullOrWhiteSpace(request.StorageMode) ? "import" : request.StorageMode.Trim();
            if (!string.Equals(storage, "import", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(storage, "directLake", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Storage mode must be Import or Direct Lake.");
            var name = request.Name?.Trim();
            var nameProblem = StartModelNameProblem(name);
            if (nameProblem != null) throw new ArgumentException(nameProblem);
            if (string.IsNullOrWhiteSpace(request.Folder) || !Path.IsPathRooted(request.Folder.Trim()))
                throw new ArgumentException("Give the full path of the folder the new model should go in.");
            var folder = Path.GetFullPath(request.Folder.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!await _startPlanGate.WaitAsync(0).ConfigureAwait(false))
                throw new InvalidOperationException("Another start is already running. Wait for it to finish, then try again.");
            try
            {
                var existing = LoadStartPlan();
                // A plan that was built and whose model still holds what it built is finished, and the new plan makes room for it. It is retired only AFTER the new
                // plan stands (below): a refusal or a failure on the way leaves its document, its words and its approval exactly where they were (Sol, join 6, A3).
                StartPlanDoc retiring = null;
                var replacing = false;
                if (existing != null && await StartPlanIsBuiltAsync(StartPlanStore.Copy(existing)).ConfigureAwait(false))
                {
                    retiring = existing;
                    existing = null;
                }
                // K24 (9 Oct): a plan that was never built is replaced by a new start; nothing is asked. Like a finished one it is retired only after the new
                // plan stands. The assistant still cannot throw away choices the person made.
                if (existing != null)
                {
                    if (agent && HoldsPersonsWork(existing))
                        throw new InvalidOperationException(
                            $"A plan for {existing.Destination?.Name} holds choices the person made, so only the person can start over it. Ask them to do it in Studio.");
                    replacing = true;
                    retiring = existing;
                    existing = null;
                }
                RefuseStartOverUnsavedWork(agent, request.DiscardUnsaved);
                // K24: the name is taken in this folder (an earlier start left Gold.SemanticModel): the new model takes the next free name and says so.
                string renamedNotice = null;
                var free = FreeStartName(folder, name);
                if (free != null)
                {
                    renamedNotice = $"A model called {name} is already in this folder, so this one is called {free}, in a new folder of that name.";
                    name = free;
                    folder = Path.Combine(folder, free);
                }
                var folderProblem = StartFolderProblem(folder);
                if (folderProblem != null) throw new InvalidOperationException(folderProblem);

                // Read the source BEFORE creating anything: a failed read changes nothing.
                FabricSchema schema;
                var read = new CancellationTokenSource();
                lock (_startPlanLock) { _startRead = read; _startReadOrigin = origin; }
                try { schema = await ReadStartSchemaAsync(request, server, database, origin, read.Token).ConfigureAwait(false); }
                finally
                {
                    lock (_startPlanLock) { _startRead = null; _startReadOrigin = null; }
                    read.Dispose();
                }
                if (schema.Tables == null || schema.Tables.Length == 0)
                    throw new InvalidOperationException("That database has no tables to build a model from. Nothing was created.");

                // Time passed during the read: look at everything again before the first write.
                RefuseStartOverUnsavedWork(agent, request.DiscardUnsaved);
                folderProblem = StartFolderProblem(folder);
                if (folderProblem != null) throw new InvalidOperationException(folderProblem);

                var modelPath = Path.Combine(folder, name + ".SemanticModel");
                var definition = Path.Combine(modelPath, "definition");
                var createdFolder = !Directory.Exists(folder);
                OpenResult created;
                // Starting replaces the open model, so say which one: a clean model is closed without asking (nothing
                // is lost, its files are untouched), and the answer must name it plainly (sf-core verifier, 1 Oct).
                var closedModel = await OpenModelNameForNoticeAsync().ConfigureAwait(false);
                try
                {
                    created = await CreateModelAsync(name, 0).ConfigureAwait(false);
                    if (!string.Equals(_sessions.Current?.Id, created.SessionId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Another model was opened while this one was being made, so nothing was saved.");
                    // The first save is the last thing that can fail, and it goes only into the folder checked above.
                    // The engine's own first save of the model it just made, inside a start the person (or the gate) already allowed: not asked a second time.
                    await SaveAsync(definition, "tmdl", false, "system").ConfigureAwait(false);
                }
                catch
                {
                    TryRemoveStartFolder(folder, createdFolder);
                    throw;
                }

                var session = _sessions.Current;
                var seed = StartPlanSeedForTest ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var pro = Entitlement.FeatureGrants.IsProTier(_entitlement);
                var now = DateTime.UtcNow;
                var doc = new StartPlanDoc
                {
                    Id = StartPlanStore.NewId(),
                    Version = 1,
                    Destination = new StartDestination { Folder = folder, Name = name, ModelPath = modelPath },
                    Source = new StartSource
                    {
                        Server = server, Database = database, AuthMode = string.IsNullOrWhiteSpace(request.AuthMode) ? "azcli" : request.AuthMode.Trim(),
                        TenantId = string.IsNullOrWhiteSpace(request.TenantId) ? null : request.TenantId.Trim(), StorageMode = storage,
                    },
                    Schema = schema,
                    IdentitySeed = seed,
                    // A source that declares no keys gets its links guessed from the names (LinkGuesser). The rows are not read yet, so the guess goes by names alone and says so;
                    // when the sample lands the guesses are reconsidered against it (SetStartSampleFlagLocked).
                    Manifest = StructureManifest.Derive(schema, null, null, seed, server, database, storage, LinkGuessFacts.NamesOnly()),
                    // Free keeps the same start with its own assistant's words; only Pro asks Jev, and only after a grant.
                    Decide = pro
                        ? new StartDecide { State = StartDecideState.WaitingGrant, Reason = "Jev needs your permission to see this source first." }
                        : new StartDecide { State = StartDecideState.Off },
                    BaselineRevision = session?.Revision ?? 0,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                // K1: a bounded sample of the source's rows is read once, in the background, so Jev and the assistant see derived facts and never rows. A plan
                // read with no network (the offline schema seam) reads none unless a test also gave the sample its own executor.
                var sampleStep = StartSampleStepApplies();
                if (sampleStep) doc.Sample = new StartSampleStatus { State = StartSampleStatus.Reading };
                if (replacing) await ParkStartPlanForSwapAsync().ConfigureAwait(false);   // the old plan's loop and scratch stop before the new one is held
                StartPlanStore.Write(doc);
                lock (_startPlanLock) { _startPlan = doc; _startSample = null; _startSamplePlanId = null; }
                if (retiring != null) StartPlanStore.Delete(retiring.Id);   // the new plan is written and held: the finished one goes
                var started = StartPlanViewBuilder.Build(StartPlanStore.Copy(doc), null);
                NotifyStartPlanChanged(started, origin);   // the Studio is pushed it, and any other assistant hears of it
                started.Notice = string.Join(" ", new[] { closedModel == null ? null : StartPlanDoors.ClosedModelNotice(closedModel), renamedNotice }.Where(n => n != null)) is { Length: > 0 } notice ? notice : null;
                if (sampleStep)
                    _startSampleTask = RunStartSampleStepAsync(doc.Id, new Facts.SourceSampleTarget { Server = server, Database = database, AuthMode = doc.Source.AuthMode, TenantId = doc.Source.TenantId },
                        schema.Tables.Select(t => new Facts.SourceSampleTable { Schema = t.Schema, Name = t.Name, Columns = (t.Columns ?? Array.Empty<FabricColumn>()).Select(c => c.Name).ToArray() }).ToList(),
                        origin);
                else ScheduleStartPreview();
                return started;
            }
            finally { _startPlanGate.Release(); }
        }

        // The person's open work is never replaced quietly. The assistant can never choose to discard it.
        private void RefuseStartOverUnsavedWork(bool agent, bool discardUnsaved)
        {
            var open = _sessions.Current;
            if (open == null || !open.HasUnsavedChanges) return;
            if (agent)
                throw new InvalidOperationException(
                    "The open model has changes that are not saved, so a new model was not started. Ask the person to save or undo them in Studio first.");
            if (!discardUnsaved)
                throw new InvalidOperationException("The open model has changes that are not saved. Save them first, or choose to discard them, then start again.");
        }

        private static string StartModelNameProblem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the new model a name.";
            if (name.Length > 80) return "Keep the model name to 80 characters or fewer.";
            if (name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.Contains('/') || name.Contains('\\') || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                return "The model name has a character a folder name cannot have. Use letters, numbers, spaces, dashes or underscores.";
            return null;
        }

        // The destination must be absent or an empty folder. ANY entry (a hidden file, another project, a sibling model)
        // refuses it: the engine cannot know what else lives there, so it writes nowhere near it.
        private static string StartFolderProblem(string folder)
        {
            if (File.Exists(folder)) return "That path is a file, not a folder. Choose an empty folder for the new model.";
            if (!Directory.Exists(folder)) return null;
            bool any;
            try { any = Directory.EnumerateFileSystemEntries(folder).Any(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return "That folder cannot be read, so it was not used. Choose another empty folder."; }
            return any ? "That folder is not empty, so nothing was written to it. Choose an empty folder for the new model." : null;
        }

        /// <summary>K24. A model of this name is already in the folder (an earlier start left Gold.SemanticModel there). One folder holds one model, so the new one goes in a new
        /// folder inside it, as a first save puts it, under the next free name: "Gold 2", "Gold 3". Null when the name is free.</summary>
        private static string FreeStartName(string folder, string name)
        {
            if (!Directory.Exists(Path.Combine(folder, name + ".SemanticModel"))) return null;
            for (var n = 2; ; n++)
            {
                var candidate = name + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!Directory.Exists(Path.Combine(folder, candidate)) && !File.Exists(Path.Combine(folder, candidate)) && !Directory.Exists(Path.Combine(folder, candidate + ".SemanticModel"))) return candidate;
            }
        }

        // Undo only what this start made: the folder it created (never one that was already there), and only if empty.
        private static void TryRemoveStartFolder(string folder, bool wasCreatedHere)
        {
            try
            {
                var model = Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, "*.SemanticModel").ToArray() : Array.Empty<string>();
                foreach (var m in model) if (!Directory.EnumerateFileSystemEntries(m).Any()) Directory.Delete(m);
                if (wasCreatedHere && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch { /* a cleanup that fails leaves an empty folder, never a lost file */ }
        }

        // The schema, through the engine's one sign-in path (AcquireSqlTokenAsync) and its one reader (FabricSqlSchema).
        private async Task<FabricSchema> ReadStartSchemaAsync(StartModelRequest request, string server, string database, string origin, CancellationToken cancel)
        {
            // The sign-in may wait for a person for a while, and for ever when they close the browser: it ends on Cancel, or on its own after the limit. The read of the
            // schema after it ends on Cancel only, so a big database is never cut short by a sign-in clock.
            // A server the person typed in Studio is theirs; an assistant's is asked about like any other new host. Asked before the sign-in
            // clock starts, so a question the person is slow to answer does not use up the time the sign-in has. The offline seams stand in
            // for the whole network, so they ask nothing.
            if (StartPlanSchemaReaderCtForTest == null && StartPlanSchemaReaderForTest == null)
            {
                var hostRefusal = await SqlHostRefusalAsync(server, database, request.AuthMode, request.TenantId, origin, typedByPerson: !DeployGuard.IsAgent(origin)).ConfigureAwait(false);
                if (hostRefusal != null) throw new InvalidOperationException(hostRefusal);
            }
            using var signIn = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            signIn.CancelAfter(StartReadLimitForTest ?? StartSignInLimit);
            if (StartPlanSchemaReaderCtForTest != null || StartPlanSchemaReaderForTest != null)
            {
                FabricSchema fake;
                try { fake = await (StartPlanSchemaReaderCtForTest != null ? StartPlanSchemaReaderCtForTest(request, origin, signIn.Token) : StartPlanSchemaReaderForTest(request, origin)).ConfigureAwait(false); }
                catch (OperationCanceledException) when (signIn.IsCancellationRequested) { throw new InvalidOperationException(StartSourceCopy.SignInClosed()); }
                if (!string.IsNullOrEmpty(fake?.Error)) throw new InvalidOperationException(LooksLikeAuthFailure(fake.Error) ? StartSourceCopy.SignInRefused() : StartSourceCopy.CannotRead(fake.Error));
                return fake ?? throw new InvalidOperationException("The source gave back nothing to read.");
            }
            string token;
            try { token = await AcquireSqlTokenAsync(request.AuthMode, request.TenantId, origin, signIn.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // A closed browser or a Cancel is not a failure to explain: one plain line, and the slot is free again.
                if (signIn.IsCancellationRequested) throw new InvalidOperationException(StartSourceCopy.SignInClosed(), ex);
                // The person reads plain words (StartSourceCopy). The reason the sign-in gave is for the log and for the inner exception, never for the form.
                try { Console.Error.WriteLine("[start-plan] sign-in failed: " + ScrubSchemaError(ex.Message)); } catch { }
                throw new InvalidOperationException(StartSourceCopy.NoSignIn(), ex);
            }
            FabricSchema schema;
            try { schema = await FabricSqlSchema.ReadAsync(server, database, token, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw new InvalidOperationException(StartSourceCopy.Cancelled()); }
            if (!string.IsNullOrEmpty(schema.Error))
            {
                try { Console.Error.WriteLine("[start-plan] reading the source failed: " + schema.Error); } catch { }
                throw new InvalidOperationException(LooksLikeAuthFailure(schema.Error) ? StartSourceCopy.SignInRefused() : StartSourceCopy.CannotRead(schema.Error));
            }
            return schema;
        }

        // ---------------------------------------------------------------- the sample step (K1)

        private Task _startSampleTask = Task.CompletedTask;
        private CancellationTokenSource _startSampleCts;

        /// <summary>Whether a start reads a sample. Production always does (the read itself says no when the gate or the source does). The offline schema seam
        /// stands in for the whole network, so a test that uses it reads a sample only when it also supplies the sample's own executor.</summary>
        private bool StartSampleStepApplies() => (StartPlanSchemaReaderForTest == null && StartPlanSchemaReaderCtForTest == null) || SourceSampleExecutorForTest != null;

        /// <summary>Wait until the sample step has finished. Tests only.</summary>
        internal async Task StartSampleSettledForTest() { Task t; lock (_startPlanLock) t = _startSampleTask; try { await t.ConfigureAwait(false); } catch { } }

        /// <summary>THE START'S SAMPLE STEP. Reads a bounded sample of the source tables (sf-sample: the QueryData gate on an assistant's start, 20,000 rows a table,
        /// derived facts only), hands the facts to the plan as its ONE sample state (<see cref="SetStartPlanSample"/>: the flag in the digest and the facts the scratch
        /// and the built model read), keeps them beside the saved model so they survive a restart and serve the model after the build, and tells both doors. A read that
        /// is refused or fails is not an error: the plan is made from names alone and says so in Needs you, in plain words. It runs in the background, so the person
        /// sees the plan at once; the grant waits for it (a grant on a plan that is about to change would be refused as stale anyway).</summary>
        private async Task RunStartSampleStepAsync(string planId, Facts.SourceSampleTarget target, List<Facts.SourceSampleTable> tables, string origin)
        {
            var cts = new CancellationTokenSource();
            lock (_startPlanLock) { try { _startSampleCts?.Cancel(); } catch { } _startSampleCts = cts; }
            Facts.SourceSampleResult result;
            try { result = await ReadSourceSampleAsync(target, tables, origin, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }   // the plan was discarded, parked or built over: nothing to say
            catch (Exception ex) { result = Facts.SourceSample.Refuse(target, tables, "The sample could not be read: " + ScrubSchemaError(ex.Message)); }
            if (cts.IsCancellationRequested) return;

            StartPlanView view = null;
            StartDestination destination = null;
            lock (_startPlanLock)
            {
                var live = _startPlan;
                if (live == null || !string.Equals(live.Id, planId, StringComparison.Ordinal)) return;
                var ok = result != null && result.Refused == null;
                _startSample = ok ? result : null; _startSamplePlanId = ok ? planId : null;
                SetStartSampleFlagLocked(planId, ok, result);
                destination = live.Destination;
                view = StartPlanViewBuilder.Build(StartPlanStore.Copy(live), null);
            }
            // After the first save: the sidecar beside the saved model, served as the model's facts when it has no live connection.
            if (result != null && result.Refused == null)
            {
                try
                {
                    var open = _sessions.Current;
                    if (open != null && IsStartDestination(open.SourcePath, destination)) SaveSourceFacts(open, result, planId);
                }
                catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] the sample could not be kept beside the model: " + ex.Message); } catch { } }
            }
            NotifyStartPlanChanged(view, null);
            ScheduleStartPreview();
        }

        // ---------------------------------------------------------------- read, park, discard

        private StartPlanDoc LoadStartPlan()
        {
            lock (_startPlanLock)
            {
                if (_startPlan == null)
                {
                    var found = StartPlanStore.Newest();
                    // A plan a past run BUILT is finished when the model it built was saved (its tables are on disk): it is retired, not offered as a plan to build.
                    // One whose build was never saved (the engine stopped first, so the unsaved build is gone) is a plan again, with its words and its approval.
                    if (found != null && found.BuiltUtc != null)
                    {
                        if (BuiltModelIsOnDisk(found)) { StartPlanStore.Delete(found.Id); found = null; }
                        else { found.BuiltUtc = null; found.BuiltDigest = null; try { StartPlanStore.Write(found); } catch { } }
                    }
                    _startPlan = found;
                    _startPlanLoadedDeciding = found != null && !found.Parked && found.BuiltUtc == null
                        && string.Equals(found.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal) ? found.Id : null;
                }
                return _startPlan;
            }
        }

        private static bool BuiltModelIsOnDisk(StartPlanDoc doc)
        {
            try
            {
                var tables = Path.Combine(doc.Destination?.ModelPath ?? "", "definition", "tables");
                return Directory.Exists(tables) && Directory.EnumerateFiles(tables).Any();
            }
            catch { return false; }
        }

        private static bool IsStartDestination(string sourcePath, StartDestination d)
        {
            if (string.IsNullOrEmpty(sourcePath) || d == null) return false;
            return SameSavePath(sourcePath, d.ModelPath) || SameSavePath(sourcePath, Path.Combine(d.ModelPath ?? "", "definition"));
        }

        public async Task<StartPlanView> GetStartPlanAsync()
        {
            var doc = LoadStartPlan();
            if (doc == null) return StartPlanViewBuilder.Empty();
            var copy = StartPlanStore.Copy(doc);
            // A plan whose model still holds what it built is BUILT: the answer says so (never "no plan", never the old plan as if it still waited to be built).
            var built = await StartPlanIsBuiltAsync(copy).ConfigureAwait(false);
            var because = built ? null : await StartPlanParkedReasonAsync(copy).ConfigureAwait(false);
            var unparked = false;
            if (copy.Parked != (because != null))
            {
                lock (_startPlanLock)
                    if (ReferenceEquals(_startPlan, doc) && doc.Parked != (because != null))
                    {
                        // Parking cancelled a loop that was running and left the plan "deciding", with nothing to finish it. Whoever UNPARKS it (this read, the
                        // first after its model opens again, is the only place that is noticed) starts the loop again, and only one caller does.
                        unparked = doc.Parked && because == null && string.Equals(doc.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal);
                        doc.Parked = because != null;
                        doc.UpdatedUtc = DateTime.UtcNow;
                        try { StartPlanStore.Write(doc); } catch { }
                    }
                copy.Parked = because != null;
            }
            if (unparked)
            {
                try { await KickStartDecideOnPlanAsync().ConfigureAwait(false); }
                catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] restarting the decide after unparking failed: " + ex.Message); } catch { } }
            }
            // A plan that said "deciding" when this engine read it from disk has no loop: the engine that ran it stopped, and no verb or unpark started one here. It reads as
            // stopped, with the reason, and the person carries on the way they do from any stopped plan (an edit, words, or Approve). THIS read does not start it again.
            // The one read that does is the unpark above: a plan PARKED because its model was closed, and read the first time its model is open again, starts the decide
            // again (KickStartDecideOnPlanAsync, Pro), since parking is what stopped it and the model coming back is the person's own doing.
            if (!built && because == null) StopStalledStartDecide(copy);
            var view = StartPlanViewBuilder.Build(copy, because, built);
            // A plan met with no current preview (after a restart, or one the person edited while nothing was making it) gets one made now.
            if (!built && because == null && !string.Equals(copy.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal)
                && !string.Equals(copy.Preview?.PlanDigest, view.Digest, StringComparison.Ordinal) && !string.Equals(copy.Sample?.State, StartSampleStatus.Reading, StringComparison.Ordinal))
                ScheduleStartPreviewIfIdle();
            return view;
        }

        /// <summary>The read-time half of "the plan says deciding only while a decide is running" (EnsureStartDecideNotOrphanedAsync is the verbs' half). True when the plan
        /// this engine read from disk said "deciding" and no decide has been started for it since: the held plan and <paramref name="copy"/> are both set to stopped
        /// and the plan file is written, so Approve is not greyed by a loop that does not exist and the other door reads the same.</summary>
        private bool StopStalledStartDecide(StartPlanDoc copy)
        {
            lock (_startPlanLock)
            {
                var live = _startPlan;
                if (live == null || !string.Equals(live.Id, _startPlanLoadedDeciding, StringComparison.Ordinal) || !string.Equals(live.Id, copy.Id, StringComparison.Ordinal)) return false;
                if (live.Parked || live.BuiltUtc != null || !string.Equals(live.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal)) return false;
                if (!StartDecideKickDisabledForTest) { lock (_startDecideGate) if (!_startDecideTask.IsCompleted) return false; }
                _startPlanLoadedDeciding = null;
                live.Decide.State = StartDecideState.Incomplete;
                live.Decide.Reason = StartDecideCopy.StoppedReason("Semanticus was closed while it was working");
                live.UpdatedUtc = DateTime.UtcNow;
                try { StartPlanStore.Write(live); } catch { }
                copy.Decide = StartPlanStore.Copy(live).Decide;
                copy.UpdatedUtc = live.UpdatedUtc;
                return true;
            }
        }

        // Why the plan cannot go on right now, in plain words, or null. Worked out from the model open NOW, never remembered.
        private async Task<string> StartPlanParkedReasonAsync(StartPlanDoc doc)
        {
            var name = doc.Destination?.Name ?? "the model";
            var open = _sessions.Current;
            if (open == null) return $"No model is open. Open {name} to carry on with this plan.";
            if (!IsStartDestination(open.SourcePath, doc.Destination))
                return $"Another model is open. Open {name} to carry on with this plan.";
            try
            {
                var tables = await open.ReadAsync(m => m.Tables.Count).ConfigureAwait(false);
                if (tables > 0) return $"{name} already has tables, so this plan can no longer be built into it. Discard the plan.";
            }
            catch { /* a model being replaced right now reads as parked on the next look */ }
            return null;
        }

        /// <summary>The ONE call SwapSessionCoreAsync makes, before it builds the replacement handler (S6): stop the decide
        /// loop, drain and drop the scratch, and mark the plan parked. Never throws: a swap must not fail because of this.</summary>
        private async Task ParkStartPlanForSwapAsync()
        {
            try
            {
                Interlocked.Increment(ref _startPlanEpoch);
                CancellationTokenSource loop;
                StartPlanScratch scratch;
                lock (_startPlanLock) { loop = _startLoop; _startLoop = null; scratch = _startScratch; _startScratch = null; }
                try { loop?.Cancel(); } catch { }
                try { loop?.Dispose(); } catch { }
                if (scratch != null) await scratch.DisposeAsync().ConfigureAwait(false);
                DropStartWordsScratch();   // the words preview's own copy goes before the replacement handler is built too (S6)
                StartPlanDoc doc;
                lock (_startPlanLock) doc = _startPlan;
                if (doc != null && !doc.Parked)
                    lock (_startPlanLock)
                    {
                        doc.Parked = true;
                        doc.UpdatedUtc = DateTime.UtcNow;
                        try { StartPlanStore.Write(doc); } catch { }
                    }
            }
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] parking the plan failed (the swap goes on): " + ex.Message); } catch { } }
        }

        /// <summary>For the decide loop (lane sf-decide): a token that is cancelled the moment the plan is parked, edited
        /// or discarded. A new call replaces and cancels the previous one.</summary>
        internal CancellationToken BeginStartLoop()
        {
            var next = new CancellationTokenSource();
            CancellationTokenSource old;
            lock (_startPlanLock) { old = _startLoop; _startLoop = next; }
            try { old?.Cancel(); old?.Dispose(); } catch { }
            return next.Token;
        }

        /// <summary>Clone the person's model into a private scratch (own bus, never published) for the decide loop and the
        /// preview. Replaces any earlier scratch. Refused while the plan is parked; a park that lands mid-build drops the
        /// clone rather than keeping it.</summary>
        internal async Task<StartPlanScratch> OpenStartScratchAsync()
        {
            var doc = LoadStartPlan() ?? throw new InvalidOperationException("There is no plan to work on.");
            var baseline = _sessions.Current;
            if (baseline == null || !IsStartDestination(baseline.SourcePath, doc.Destination))
                throw new InvalidOperationException("The plan's model is not the one that is open.");
            var epoch = Interlocked.Read(ref _startPlanEpoch);
            StartPlanScratch old;
            lock (_startPlanLock) { old = _startScratch; _startScratch = null; }
            if (old != null) await old.DisposeAsync().ConfigureAwait(false);
            var scratch = await StartPlanScratch.CloneAsync(baseline, StartPlanStore.ScratchDirFor(doc.Id)).ConfigureAwait(false);
            lock (_startPlanLock)
            {
                if (Interlocked.Read(ref _startPlanEpoch) == epoch) { _startScratch = scratch; return scratch; }
            }
            await scratch.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException("The plan was parked while its working copy was being made.");
        }

        /// <summary>A clone of the person's model that belongs to ITS caller (the words preview keeps one), not to the decide loop's slot: nothing here replaces or
        /// disposes <c>_startScratch</c>. Refused while the plan is parked, and a park that lands mid-build drops the clone rather than handing it back.</summary>
        internal async Task<StartPlanScratch> CloneStartScratchForAsync(string folderSuffix)
        {
            var doc = LoadStartPlan() ?? throw new InvalidOperationException("There is no plan to work on.");
            var baseline = _sessions.Current;
            if (baseline == null || !IsStartDestination(baseline.SourcePath, doc.Destination))
                throw new InvalidOperationException("The plan's model is not the one that is open.");
            var epoch = Interlocked.Read(ref _startPlanEpoch);
            var scratch = await StartPlanScratch.CloneAsync(baseline, StartPlanStore.ScratchDirFor(doc.Id) + folderSuffix).ConfigureAwait(false);
            if (Interlocked.Read(ref _startPlanEpoch) == epoch) return scratch;
            await scratch.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException("The plan was parked while its working copy was being made.");
        }

        internal bool StartScratchOpenForTest() { lock (_startPlanLock) return _startScratch != null; }

        /// <summary>The engine is going away: whatever the start plan still has running in the background (the decide loop, the sample read, the preview) is
        /// stopped and waited for, briefly, so nothing writes the plan's file or builds a copy of a model after its owner has gone.</summary>
        private void StopStartPlanWork()
        {
            _startPlanDisposed = true;
            Task[] running;
            lock (_startPlanLock)
            {
                try { _startSampleCts?.Cancel(); } catch { }
                try { _startPreviewCts?.Cancel(); } catch { }
                try { _startLoop?.Cancel(); } catch { }
                running = new[] { _startSampleTask, _startPreviewTask };
            }
            try { _startDecideCts?.Cancel(); } catch { }
            try { Task.WhenAll(running.Concat(new[] { _startDecideTask })).Wait(TimeSpan.FromSeconds(5)); } catch { }
            DropStartWordsScratch();
        }

        public async Task<StartPlanView> DiscardStartPlanAsync(string origin)
        {
            GuardOutsideWrite(origin);
            if (CancelStartRead(origin))
            {
                // A start still reading its source ends here and its slot comes free. That is all Cancel does: a plan that stood before the start is not thrown away.
                await _startPlanGate.WaitAsync().ConfigureAwait(false);
                _startPlanGate.Release();
                return await GetStartPlanAsync().ConfigureAwait(false);
            }
            await _startPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var doc = LoadStartPlan();
                if (doc == null) return StartPlanViewBuilder.Empty();
                var agent = !string.Equals(origin ?? "human", "human", StringComparison.OrdinalIgnoreCase);
                if (agent && HoldsPersonsWork(doc))
                    throw new InvalidOperationException(
                        "This plan holds choices the person made or approved, so only the person can discard it. Ask them to do it in Studio.");
                await ParkStartPlanForSwapAsync().ConfigureAwait(false);   // loop cancelled, scratch drained
                lock (_startPlanLock)
                {
                    try { _startSampleCts?.Cancel(); } catch { }
                    try { _startPreviewCts?.Cancel(); } catch { }
                    _startPlan = null; _startSample = null; _startSamplePlanId = null;
                }
                StartPlanStore.Delete(doc.Id);
                // The empty model the start saved stays where it is: it is the person's file now. Only the plan goes.
                var gone = StartPlanViewBuilder.Empty();
                NotifyStartPlanChanged(gone, origin);
                return gone;
            }
            finally { _startPlanGate.Release(); }
        }

        private static bool HoldsPersonsWork(StartPlanDoc d) =>
            (d.Pins?.Count ?? 0) > 0 || (d.Answers?.Count ?? 0) > 0 || d.Approval != null
            || (d.Lines?.Any(l => string.Equals(l.DecidedBy, StartDecidedBy.Person, StringComparison.Ordinal)) ?? false);

        // ---------------------------------------------------------------- the consent verbs' view of the document (sf-consent)

        // The sample flag is ONE state, the plan's own (StartPlanDoc.SampleFacts, in the digest), set by the start's sample step and by
        // SetStartPlanSample. NoteStartSampleRead is the small form for a caller that knows a sample was read (or taken away) and holds no
        // facts: it sets the same flag, so the digest moves, an approval is void and a grant given on the old digest is refused as stale.
        internal void NoteStartSampleRead(string planId, bool read)
        {
            if (string.IsNullOrEmpty(planId)) return;
            lock (_startPlanLock)
            {
                if (!read && string.Equals(_startSamplePlanId, planId, StringComparison.Ordinal)) { _startSample = null; _startSamplePlanId = null; }
                SetStartSampleFlagLocked(planId, read, null);
            }
        }

        private sealed class StartPlanHostOverStore : IStartPlanConsentHost
        {
            private readonly LocalEngine _e;
            public StartPlanHostOverStore(LocalEngine e) { _e = e; }

            // A copy, so a caller can never change the live document without the lock.
            public StartPlanDoc Current() => StartPlanStore.Copy(_e.LoadStartPlan());
            public string DigestOf(StartPlanDoc doc) => StartPlanViewBuilder.Digest(doc);
            public bool SampleFactsOn(StartPlanDoc doc) => doc != null && doc.SampleFacts;

            public Task<StartPlanView> UpdateAsync(Func<StartPlanDoc, bool> change)
            {
                lock (_e._startPlanLock)
                {
                    var live = _e.LoadStartPlan() ?? throw new InvalidOperationException("There is no plan to agree to. Start a model from a SQL source first.");
                    // Work on a copy: a change that throws or returns false leaves the live document exactly as it was.
                    var work = StartPlanStore.Copy(live);
                    if (!change(work)) return Task.FromResult(StartPlanViewBuilder.Build(live));
                    StartPlanConsent.ClearApprovalIfMoved(work, StartPlanViewBuilder.Digest(work));
                    work.UpdatedUtc = DateTime.UtcNow;
                    StartPlanStore.Write(work);
                    _e._startPlan = work;
                    return Task.FromResult(StartPlanViewBuilder.Build(work));
                }
            }
        }

        // ---------------------------------------------------------------- owned by other lanes (contract stubs)
        // GetStartWordsAsync and WriteStartWordsAsync live in StartPlan/StartPlanWords.cs (lane sf-words).

        public Task<StartPlanView> EditStartPlanAsync(StartPlanEdit edit, string origin) { GuardOutsideWrite(origin); return EditStartPlanCoreAsync(edit, origin); }   // lane sf-decide: StartPlan/LocalEngine.StartPlanDecide.cs
    }
}
