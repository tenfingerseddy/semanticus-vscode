// The decision surface on the engine: the registry, consent, the budget, and the one proof point.
//
// Six of the seven operations here are settings and status reads. The seventh, propose_measure_defaults, is
// the whole of M0a's proof that a typed decision can travel from a registered point, through consent, the
// cache and the bar, to a proposal that BOTH doors see in the same shape and that nothing writes.
//
// Why the proposal is its own operation rather than a field on create_measure. Two lines in the ratified
// design forbid the alternative: docs/redesign/jev-plan.md:22 ("no provider wait sits on the model
// dispatcher") and docs/redesign/jev-infusion.md:212 ("no decision point sits inside a mutation"). Asking
// inside CreateMeasureAsync would put a four-second wait plus a retry inside a mutation on the single-writer
// dispatcher, which is exactly what both lines rule out. So create_measure is untouched and answers in the
// same time it always has, and the proposal is one further call that either door can make.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Said when a switch is set with no model open: every switch belongs to one model.</summary>
        internal const string NoModelForSwitchNote = "Open a model first. Each model keeps its own switches.";
        private readonly object _decisionGate = new object();
        private DecisionService _decisions;

        /// <summary>The session's decision service. Built once, and the route is settled there and then:
        /// SEMANTICUS_JEV_KEY gives the direct vendor client (development only), a verified Pro licence gives
        /// the hosted relay with that licence as the credential, and neither gives the null oracle, which is
        /// the default, the free session and the tested path. Tests replace it wholesale through
        /// <see cref="UseDecisionsForTest"/>.
        ///
        /// F-194: this used to read the vendor key and nothing else, so an INSTALLED Pro session built the null
        /// oracle and every decision point in the product was skipped without anyone being told. A licence is
        /// what an installed copy actually has, so the licence is what the production route uses.</summary>
        internal DecisionService Decisions
        {
            get
            {
                lock (_decisionGate)
                {
                    if (_decisions == null)
                    {
                        var budget = new DecisionBudget();
                        IDecisionOracle oracle = JevDecisionOracle.FromEnvironment(budget, RelayCredential());
                        if (oracle == null) oracle = NullDecisionOracle.Instance;
                        _decisions = BuildDecisions(oracle, budget);
                    }
                    return _decisions;
                }
            }
        }

        /// <summary>The service a real session gets, around whichever client the route above settled on. One place, so
        /// a test can put a recorded client inside exactly what production builds (and prove, say, that a restart is
        /// answered from the store) rather than a service the test assembled itself.
        ///
        /// The answers are kept on this computer across restarts (Decisions/DecisionAnswerStore.cs), so reopening a
        /// model asks Jev only about what changed. The null oracle never answers, so it never writes one.</summary>
        internal DecisionService BuildDecisions(IDecisionOracle oracle, DecisionBudget budget) =>
            new DecisionService(oracle, budget, answers: new DecisionAnswerStore())
            {
                ReadScope = DecisionConsentScopeAsync,
                StillEntitled = () => Entitlement.FeatureGrants.IsProTier(_entitlement),
                OracleAnswered = NoteTrialJevChoices,
            };

        /// <summary>The verified licence token, when this session has one a hosted service would accept.
        ///
        /// This is a CREDENTIAL read, not a tier read. It asks whether there is a token to present and never
        /// whether this person may do something; the tier gate is still FeatureGrants and this path does not
        /// touch it. The policy for which entitlements carry a credential lives with the entitlement, in
        /// <see cref="Entitlement.LicenseEntitlement.CloudCredentialOf"/>, so there is one sanctioned way to
        /// ask and a test double, a free tier and the dev escape all read as none.</summary>
        private string RelayCredential() => Entitlement.LicenseEntitlement.CloudCredentialOf(_entitlement);

        /// <summary>Test seam. No production path calls this, and there is no setting or argument that reaches
        /// it, so a real session can only ever get the service built above.</summary>
        internal void UseDecisionsForTest(DecisionService service)
        {
            lock (_decisionGate)
            {
                // A service handed in by a test still reads permission for the model this engine has open.
                // Leaving that to the test would let one forget, and a forgotten scope reads as permission for
                // nothing, which would quietly turn a real assertion into a vacuous one.
                if (service != null)
                {
                    service.ReadScope = DecisionConsentScopeAsync;
                    service.StillEntitled = () => Entitlement.FeatureGrants.IsProTier(_entitlement);
                    service.OracleAnswered = NoteTrialJevChoices;
                }
                _decisions = service;
            }
        }

        /// <summary>Which model this session's permission belongs to, or null when the open model has no stable
        /// identity to bind one to. A brand-new model that has never been saved and is not connected has none,
        /// and permission cannot be given for it, because there would be nothing to tell it apart from the next
        /// brand-new model.
        ///
        /// TWO HALVES, and the second one is F-177. The first is the ADDRESS, the same identity an interview
        /// pack binds to (<c>PaneIdentity</c>): the live endpoint and database when the session is live, else
        /// the full model path on disk, else an attached live connection. An address is only where a model
        /// sits, so on its own it let a file overwritten in place, or a dataset redeployed to the same
        /// endpoint, inherit the previous model's permission and send a stranger's metadata without asking.
        /// The second half is the MARKER this engine stamped into the model when permission was first given
        /// for it (see ConsentIdAnnotation), so a different model at the same address carries a different
        /// marker, or none, and is asked about.
        ///
        /// Async because the marker is read from the model, which is a hop to the single-writer dispatcher.
        /// The answer is cached against the session and its revision, so the hop happens once per change
        /// rather than once per call.</summary>
        internal async Task<string> DecisionConsentScopeAsync()
        {
            var identity = OpenModelIdentity();
            if (string.IsNullOrEmpty(identity)) return null;

            // An address with no open model cannot be witnessed, so there is nothing a permission could
            // describe and there is no scope. Only a connect_xmla with nothing open reaches this.
            var s = _sessions.Current;
            if (s == null) return null;

            var sessionId = s.Id;
            var revision = s.Revision;
            lock (_decisionGate)
            {
                if (_witnessScope != null
                    && string.Equals(_witnessSessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(_witnessIdentity, identity, StringComparison.Ordinal)
                    && _witnessRevision == revision)
                    return _witnessScope;
            }

            var onDisk = identity.StartsWith("disk:", StringComparison.Ordinal);
            var marker = await s.ReadAsync(m => ConsentIdOf(m) ?? (onDisk ? NameWitnessOf(m) : null)).ConfigureAwait(false)
                         ?? PendingConsentWitness(identity);
            // K19 (Kane, 8 Oct): permission is kept on this machine and the model is never edited to hold it. The marker was an unsaved
            // model edit, a live model has nowhere to save it, and a restart lost the permission with it. A marker an older build wrote
            // still counts. A model on disk is kept under its path AND its name (F-177 again): another model written over the same file
            // has another name and is asked about. A live address already names the dataset (endpoint and database), so it needs no more.
            var scope = DecisionConsentStore.ScopeKey(identity, marker);
            lock (_decisionGate)
            {
                _witnessSessionId = sessionId;
                _witnessIdentity = identity;
                _witnessRevision = revision;
                _witnessScope = scope;
            }
            return scope;
        }

        // The cached scope, and the two things allowed to make it stale. A re-open mints a fresh session id
        // and an edit bumps the revision, and between them they cover every way this engine can come to be
        // looking at a DIFFERENT model: a replacement on disk, or a redeploy, is only ever visible after a
        // re-open. The plan cache keys its own work the same way, for the same reason.
        private string _witnessScope;
        private string _witnessSessionId;
        private string _witnessIdentity;
        private long _witnessRevision = -1;

        /// <summary>The marker that says WHICH model this is: a value this engine stamped into the model
        /// the first time permission was given for it, and that travels with the model file or the live
        /// model from then on.
        ///
        /// WHY A MARKER AND NOT A DESCRIPTION OF THE MODEL. Two versions of this tried to tell models
        /// apart by describing them, and both were wrong. The first hashed the naming STYLE of the table
        /// names, and Astra broke it with two models whose tables are all capital-then-lower-case. The
        /// second hashed the model, table and column NAMES, and Codex broke that with two models spelled
        /// identically down to the column, differing only in their measures, which is exactly the
        /// metadata this permission is about. Any description can be matched by accident; only something
        /// this engine minted for one model is unique to it.
        ///
        /// IT IS AN IDENTITY AND NEVER A PERMISSION. It says which model, not that anybody agreed. The
        /// agreement stays in ~/.semanticus where it always was, so a model file carrying this marker,
        /// committed to a repository or copied to another machine, grants nothing there. That separation
        /// is the founding rule of DecisionConsentStore and a marker that carried permission would break
        /// it. DecisionConsentTests pins it.</summary>
        internal const string ConsentIdAnnotation = "Semanticus_ConsentId";

        /// <summary>Eight hex characters taken from the model's own name, or null when it has none. Not a secret and not a marker: it
        /// only lets a different model written over the same file be told apart. A rename asks once more, an ordinary edit never does.</summary>
        private static string NameWitnessOf(Model m)
        {
            var name = string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name;
            return string.IsNullOrWhiteSpace(name) ? null : Sha8(name.Trim());
        }

        /// <summary>This model's marker, or null when it has none yet. One annotation read on the
        /// dispatcher, and the caller caches it against the session and revision.</summary>
        private static string ConsentIdOf(Model m)
        {
            var value = m.GetAnnotation(ConsentIdAnnotation);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>Test seam for the advice fence. Runs after the one protected read has produced the snapshot
        /// AND the revision it was taken at, and before the fence is built, so a test can move the model in
        /// exactly that window and prove the stale snapshot is refused rather than blessed. Null on every
        /// production path.</summary>
        internal Func<Task> BetweenSnapshotAndFenceForTest;

        // ---- the registry ----------------------------------------------------------------------------

        public Task<DecisionPointInfo[]> GetDecisionPointsAsync() =>
            Task.FromResult(DecisionRegistry.Points.Select(Describe).ToArray());

        public Task<DecisionPointInfo[]> SetDecisionPointEnabledAsync(string name, bool enabled, string origin, string expectedSession = null)
        {
            GuardOutsideWrite(origin);
            var point = DecisionRegistry.Require(name);

            var current = _sessions.Current;
            // Studio names the model its rows were read for. When the other door opened another model since, a click on
            // those rows is about a model that is no longer open, so nothing is switched.
            if (expectedSession != null && current != null && !string.Equals(current.Id, expectedSession, StringComparison.Ordinal))
                throw new InvalidOperationException(SwitchForAnotherModelNote);
            var place = DecisionSwitchPlaceFor(current);
            if (place == null) throw new InvalidOperationException(NoModelForSwitchNote);
            string onNote = null;
            if (place.Unsaved != null)
            {
                // A live open publishes its session a moment before it says which live model it is. A switch set in that
                // moment would be kept as an unsaved model's and lost when the open finished, so it waits for the open.
                if (Volatile.Read(ref _liveOriginStampPending) != 0) throw new InvalidOperationException(ModelStillOpeningNote);
                lock (_decisionGate) place.Unsaved.UnsavedSwitches[point.Name] = enabled;
            }
            else
            {
                if (place.File == null) throw new InvalidOperationException(NowhereToKeepNote);
                var wasOff = enabled && place.Desktop && SwitchOf(place, point.Name).Value == false;
                WriteSwitch(place, point.Name, enabled);
                // An on is this identity's alone and passes every off next door by. The row says what that leaves off: another
                // Desktop file's off, or this model's own earlier off that other views of it still read.
                var remains = wasOff ? OffRemainsForOthers(place, point.Name) : OffLeft.None;
                if (remains == OffLeft.Others)
                    onNote = place.Known ? OnForFilesWithThisNameNote : OnForModelsWithNoNameNote;
                else if (remains == OffLeft.OwnOnly)
                    onNote = place.Known ? OnForThisNameAfterItsOwnOffNote : OnForNoNameAfterItsOwnOffNote;
                if (place.IgnoreDir != null)
                    try { LayoutStore.EnsureSwitchIgnore(place.IgnoreDir); } catch { /* the ignore is a courtesy; the switch is written */ }
            }

            // Anything cached was bought while the point was on. Drop it, so switching a point off stops it
            // proposing immediately rather than after the cache happens to expire.
            Decisions.ClearCache();
            // Switching off also stops the run that is asking: requests waiting to start would otherwise still send (Sol 6.1, 2.1).
            if (!enabled) CancelJevRun();
            // A start plan that waited because no point was live is free to go on once one is switched on again.
            if (enabled && _startPlan != null) _ = Task.Run(ReevaluateStartPlanAsync);

            var rows = DecisionRegistry.Points.Select(Describe).ToArray();
            if (onNote != null)
                foreach (var row in rows.Where(r => r.Name == point.Name && r.Enabled && r.Note == null)) row.Note = onNote;
            return Task.FromResult(rows);
        }

        private DecisionPointInfo Describe(DecisionPoint point)
        {
            var state = SwitchOf(DecisionSwitchPlaceFor(_sessions.Current), point.Name);
            return new DecisionPointInfo
            {
                Name = point.Name,
                Caller = point.Caller,
                Purpose = point.Purpose,
                SchemaVersion = point.SchemaVersion,
                Enabled = state.Value ?? true,
                Note = state.Note,
                Live = DecisionPointLiveInThisBuild(point.Name),
                ProOnly = point.ProOnly,
                ConfidenceBar = point.ConfidenceBar,
                LowConfidenceBehaviour = point.LowConfidenceBehaviour,
                Fields = point.SnapshotFields.Select(f => point.Name + ":" + f).ToArray(),
                Questions = point.Questions.Select(q => new DecisionQuestionInfo
                {
                    Name = q.Name,
                    Shape = q.Kind == DecisionQuestionKind.Choice ? "choice"
                          : q.Kind == DecisionQuestionKind.Score ? "score" : "noul",
                    OptionSource = q.OptionSource,
                    Options = q.FixedOptions == null ? Array.Empty<string>() : q.FixedOptions.ToArray(),
                }).ToArray(),
            };
        }

        /// <summary>Whether this build lets a point act at all: the replay bar the asking path itself reads. A point
        /// whose bar is unmet is never asked, whatever its switch says, so the list says it is not on yet rather than
        /// showing a switch that is on and does nothing. Read from the same constants the asking paths read.
        ///
        /// EVERY POINT HAS ITS OWN CASE (verifier round 2, 26 Sep): relationship.join and ai.schema fell through a
        /// "true" default and were listed live with their bars unmet. A point with no case now reads NOT live, so a
        /// new point is off in the list until someone writes down its bar here, and a test holds every registered
        /// point to having one.</summary>
        private bool DecisionPointLiveInThisBuild(string name) => DecisionPointBar(name) ?? false;

        /// <summary>A point's own bar in this build, or null for a name with no case.</summary>
        private bool? DecisionPointBar(string name) => name switch
        {
            // Asked wherever they are called; their replays are behind them (M2, M3, columns first).
            DecisionRegistry.MeasureFolderAndFormat => true,
            DecisionRegistry.PlanRank => true,
            DecisionRegistry.AskRoute => true,
            DecisionRegistry.ColumnFormat => true,
            // The column points read their own family's constant (colfair, 26 Sep): category failed its re-test and is off.
            DecisionRegistry.ColumnCategory => ColumnJevDecides(DecisionRegistry.ColumnCategory),
            DecisionRegistry.ColumnSummarize => ColumnJevDecides(DecisionRegistry.ColumnSummarize),
            // describe-jev split the one switch per door (26 Sep): the point is live while either door asks it.
            DecisionRegistry.ComposeDraft => JevDraftsOnWorkflows || JevDraftsOnPlan,
            DecisionRegistry.MeasureStarter => StarterJevDecides,
            DecisionRegistry.DateTable => DateTableJevDecides,
            DecisionRegistry.RelationshipJoin => RelationshipJevDecides,
            DecisionRegistry.AiSchema => AiSchemaJevDecides,
            DecisionRegistry.QuerySummarize => JevReadsData,
            DecisionRegistry.QueriesChartKind => JevPicksChart,
            // plan-harm round 5 (30 Sep): asked on every Pro Work it out with its switch on (AskCleanupJevAsync reads no build
            // constant), so it is live. It can only untick a column the rule ticked, and the person reviews every line before
            // anything is removed; its live run over the ten sealed models is in that lane's report.
            DecisionRegistry.CleanupSuggest => true,
            // 1.5, the Next line: it only chooses among the steps the rules already found eligible, and every word is the rules'.
            DecisionRegistry.NextStep => NextStepJevLive,
            _ => null,
        };

        internal bool? DecisionPointBarForTest(string name) => DecisionPointBar(name);

        // ---- where one model's switches are kept ---------------------------------------------------------

        /// <summary>Where one model's switches live. Every model has its own: a saved model a file named for it in its
        /// sidecar folder, a model opened live a file in the workspace named for which model it is, a model not saved yet
        /// its session, until its first save carries them to disk. Null when no model is open.
        ///
        /// WHY EACH MODEL HAS ITS OWN. Live and unsaved models used to fall back to the workspace's one file, and two
        /// saved models side by side in one folder shared that folder's one file, so switching a point on for one turned
        /// it back on for another whose owner had switched it off (the query-split and switch-scope verifiers, 28 Sep).
        ///
        /// WHY AN OFF IS ALSO WRITTEN NEXT DOOR. Sometimes nothing says for sure which model this is: Power BI Desktop says
        /// no file Semanticus can trust (only a window title, which two same-named files share, or nothing), and gives a new
        /// port and database name each time it starts; a model file can be renamed outside Semanticus. So an off is also
        /// kept, with the name of the model it was switched off for, in a file every model that might be that one reads
        /// (the neighbours below). A model's own file always wins; when it says nothing, an off next door that might be this
        /// model's reads as off, so "off means it is never asked" holds whichever way the model is reached. An on is never
        /// shared through a neighbour. A Desktop model's on is kept for its identity (its title, or nothing), so Desktop
        /// models Semanticus cannot tell apart share it, and the on's row says so.</summary>
        private sealed class DecisionSwitchPlace
        {
            /// <summary>The model's own settings file. Null for an unsaved model, and for a live model when the engine
            /// has no workspace to keep it in.</summary>
            public string File;
            /// <summary>An unsaved model's session, which holds its switches.</summary>
            public Session Unsaved;
            /// <summary>This model's name in a neighbour's list of whom an off was switched off for.</summary>
            public string Writer;
            /// <summary>The own file belongs to one run of Power BI Desktop, because a Desktop model is known by no file.</summary>
            public bool EndsWithDesktop;
            /// <summary>A Power BI Desktop model on this machine: the offs of files it cannot be told apart from are shared with it.</summary>
            public bool Desktop;
            /// <summary>The file that keeps a title-only or nameless Desktop model's on for good, because Desktop starts a new run each
            /// time and the run's own file is gone by then. Null for every other model.</summary>
            public string IdentityOn;
            /// <summary>The folder every switch file of the models here lives in.</summary>
            public string Folder;
            /// <summary>The hash of the title this model reads under, or null when Desktop named none.</summary>
            public string TitleKey;
            /// <summary>Semanticus can tell this Desktop file from files with other names: it has a title.</summary>
            public bool Known;
            /// <summary>The desktop-any.json that holds the twin of every off a known model records.</summary>
            public string AnyFile;
            /// <summary>The sidecar whose .gitignore keeps this place's per-run and shared Desktop files out of commits.</summary>
            public string IgnoreDir;
            public readonly List<SwitchNeighbour> Neighbours = new List<SwitchNeighbour>();
        }

        /// <summary>A file of offs another model may have left for this one. An off in it names whom it was switched off
        /// for; one written before switches were kept per model names no one, and was switched off for everyone there.</summary>
        private sealed class SwitchNeighbour
        {
            public string File;
            /// <summary>Whether this model writes its own offs here too.</summary>
            public bool Records;
            /// <summary>Given whom an off was switched off for (null: everyone, before per-model switches), what to tell the
            /// person when that off may be this model's, or null when it cannot be.</summary>
            public Func<string, string> OffNote;
        }

        private const string SwitchFolder = LayoutStore.DecisionSettingsFolder;
        private const string OffForKey = "offFor";
        private const string OffAtKey = "offAt";
        private const string AtKey = "at";
        private const string NoteKey = "note";

        private static long _lastSwitchStamp;

        /// <summary>When a switch was written, in UTC ticks, strictly rising within one engine so two writes in the same tick
        /// still have an order. A connection's own on and an off next door are compared by it (Sol 6.1, finding 2.2).</summary>
        private static long SwitchStamp()
        {
            while (true)
            {
                var last = Interlocked.Read(ref _lastSwitchStamp);
                var next = Math.Max(last + 1, DateTime.UtcNow.Ticks);
                if (Interlocked.CompareExchange(ref _lastSwitchStamp, next, last) == last) return next;
            }
        }

        internal const string ModelStillOpeningNote = "The model is still opening. Try again in a moment.";
        private const string EarlierOffNote = "Off because it was switched off for every model here before each model kept its own switches.";
        private const string SelfOffNote = "Off because it was switched off for this model.";
        private const string MovedOffNote = "Off because it was switched off for {0}, which is no longer in this folder.";
        private const string TitleOffNote = "Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file.";
        private const string UnnamedOffNote = "Off because it was switched off for a Power BI Desktop file whose name could not be read. Desktop does not say whether that was this file.";
        private const string UntilDesktopClosesNote = "On until Power BI Desktop closes. Desktop does not say which file this is, so after that it is off again.";
        private const string AnyDesktopOffNote = "Off because it was switched off for another Power BI Desktop file. Desktop does not say which file this is, so it may be that one.";
        // What an on for a Power BI Desktop model says. A Desktop model is known by its window title or by nothing, never by
        // the path on Desktop's command line (the join's ruling, 30 Sep; LocalDesktop's class comment), so an on reaches every
        // model Semanticus cannot tell apart from the one clicked, and the row says which those are.
        internal const string OnForFilesWithThisNameNote = "On for every Power BI Desktop file with this name, such as a copy in another folder, because Desktop does not say which file this is. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.";
        internal const string OnForModelsWithNoNameNote = "On for every Power BI Desktop model whose name Semanticus cannot read, because it cannot tell them apart. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.";
        internal const string OnForThisNameAfterItsOwnOffNote = "On for every Power BI Desktop file with this name, such as a copy in another folder, because Desktop does not say which file this is. A Power BI Desktop model whose name Semanticus cannot read may still be off from the earlier off, until you switch it on there.";
        internal const string OnForNoNameAfterItsOwnOffNote = "On for every Power BI Desktop model whose name Semanticus cannot read, because it cannot tell them apart. A Power BI Desktop file whose name it can read may still be off from the earlier off, until you switch it on there.";
        private const string CarriedOffNote = "Off because it was switched off for a model Semanticus took to be this one before it was saved.";
        internal const string SwitchForAnotherModelNote = "That switch was for a model that is no longer open, so nothing was changed. Look at the switches again for the model open now.";
        internal const string NowhereToKeepNote = "There is nowhere to keep this setting yet. Save the model to a file first, then try again.";
        private const string IdentityOnPrefix = "desktop-on-";
        private const string SwitchesNotKeptNote = "The model was saved, but the Pro suggestions switched off for it could not be kept beside it. Switch them off again under What Pro decides.";
        private const string SwitchesNotKeptFreeNote = "The model was saved. The Pro suggestions switched off for it could not be kept beside it, so switch them off again if you move to Pro.";

        private static readonly StringComparison NameComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private DecisionSwitchPlace DecisionSwitchPlaceFor(Session s)
        {
            if (s == null) return null;
            if (!ExperienceStore.IsEphemeralAnchor(s.SourcePath)) return SavedModelSwitchPlace(s.SourcePath);
            var place = new DecisionSwitchPlace { Unsaved = s.LiveOrigin == null ? s : null };
            if (_workspaceDir == null) return place;
            var sidecar = Path.Combine(_workspaceDir, LayoutStore.DirName);
            var folder = Path.Combine(sidecar, SwitchFolder);
            place.Folder = folder;
            var o = s.LiveOrigin;
            if (o != null)
            {
                // A model's own file is this run of it: a Desktop model's, because Desktop picks a new port and database each
                // start and is known by its window title or by nothing, never by the path on its command line (the join's
                // ruling, 30 Sep: that path names the file Desktop was started with, which the window may no longer show, so
                // an on kept under it reached another file; the switch-scope verifier, round 6, F2b). LiveOrigin.LocalPath is
                // ignored here for that reason. A cloud model's own file is its endpoint and database.
                var run = Sha8(NormSwitchPart(o.Endpoint) + "|" + NormSwitchPart(o.Database));
                place.File = Path.Combine(folder, "live-" + run + ".json");
                place.Writer = "run:" + run;
                place.EndsWithDesktop = o.LocalDesktop;
                place.IgnoreDir = sidecar;
                place.Desktop = o.LocalDesktop;
                var title = o.LocalName;
                if (!string.IsNullOrWhiteSpace(title))
                    place.Neighbours.Add(new SwitchNeighbour
                    {
                        File = Path.Combine(folder, "title-" + Sha8(NormLivePart(title)) + ".json"),
                        Records = true,
                        // Every Desktop file with this title is one Semanticus cannot tell apart, so any off for it may be this one's.
                        OffNote = _ => TitleOffNote,
                    });
                if (place.Desktop)
                {
                    // WHY EVERY DESKTOP OFF REACHES A DESKTOP MODEL KNOWN BY NOTHING. What Semanticus can read from Desktop is
                    // its window title, and a title may not be there (a minimized window) or not be readable (one Desktop words
                    // differently, such as a preview build's), so the same file can be known by its title one day and by
                    // nothing the next. Such a model might be any Desktop file, and any Desktop file might be one of those, so
                    // each reads the other's offs. An on never travels this way (the switch-scope verifier, round 2, E1).
                    var known = !string.IsNullOrWhiteSpace(title);
                    place.Known = known;
                    place.TitleKey = string.IsNullOrWhiteSpace(title) ? null : Sha8(NormLivePart(title));
                    // WHY AN ON IS KEPT UNDER ITS OWN IDENTITY ONLY. Every on used to clear the offs next door that read as this
                    // model's, and each rule for which offs it could clear left a way to reach a file Semanticus can tell apart
                    // (the switch-scope verifier, rounds 3 and 4; ruled 30 Sep: an on never clears). So an on is kept for the
                    // identity that clicked and nowhere else: a file known by its title keeps it in a file for that title, which
                    // every Desktop file with that name reads, and a model known by nothing in the one file every such model
                    // reads, because the run's own file is gone once Desktop restarts. Those are the models Semanticus cannot
                    // tell apart from the one clicked, and the on's row says so.
                    place.IdentityOn = Path.Combine(folder, IdentityOnPrefix + (known ? "title-" + place.TitleKey : "nothing") + ".json");
                    place.AnyFile = Path.Combine(folder, "desktop-any.json");
                    place.Neighbours.Add(new SwitchNeighbour
                    {
                        File = Path.Combine(folder, "desktop-unnamed.json"),
                        Records = !known,
                        OffNote = _ => UnnamedOffNote,
                    });
                    place.Neighbours.Add(new SwitchNeighbour
                    {
                        File = place.AnyFile,
                        Records = known,
                        OffNote = known ? (Func<string, string>)(_ => null) : _ => AnyDesktopOffNote,
                    });
                }
            }
            // What the workspace's one file switched off before per-model switches, still read so that an off an older
            // engine writes there is kept too. Nothing lifts it: a Desktop model's on passes it by. A live or unsaved model never writes it; a model saved in the workspace
            // folder itself writes offs there with its own name, and those are not a live model's.
            place.Neighbours.Add(new SwitchNeighbour
            {
                File = Path.Combine(sidecar, LayoutStore.DecisionSettingsFile),
                OffNote = w => w == null ? EarlierOffNote : null,
            });
            return place;
        }

        /// <summary>A saved model's own file is named for the model file, so two models in one folder differ. Its offs are
        /// also written to the folder's one file with its name, which is what keeps an off when the file is renamed
        /// outside Semanticus: an off switched off for a model no longer in the folder may be this one's.</summary>
        private static DecisionSwitchPlace SavedModelSwitchPlace(string source)
        {
            var sidecar = LayoutStore.DirFor(source);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            var leaf = Path.GetFileName(full);
            var home = Path.GetDirectoryName(full);
            var readable = SafeKeyName(leaf);
            if (readable.Length > 60) readable = readable.Substring(0, 60);
            var writer = "model:" + leaf;
            var place = new DecisionSwitchPlace
            {
                File = Path.Combine(sidecar, SwitchFolder, "model-" + readable + "-" + Sha8(OperatingSystem.IsWindows() ? leaf.ToUpperInvariant() : leaf) + ".json"),
                Writer = writer,
            };
            place.Neighbours.Add(new SwitchNeighbour
            {
                File = Path.Combine(sidecar, LayoutStore.DecisionSettingsFile),
                Records = true,
                OffNote = w => w == null ? EarlierOffNote
                    : string.Equals(w, writer, NameComparison) ? SelfOffNote
                    : w.StartsWith("model:", StringComparison.Ordinal) && ModelGone(home, w.Substring(6)) ? string.Format(MovedOffNote, w.Substring(6))
                    : null,
            });
            return place;
        }

        private static bool ModelGone(string home, string leaf)
        {
            try
            {
                if (home == null || Path.GetFileName(leaf) != leaf) return true;
                var path = Path.Combine(home, leaf);
                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch { return true; }
        }

        /// <summary>A live endpoint or database the way the switch compares it: the permission key's normalisation, plus a
        /// percent-encoded spelling read as the name it spells, so "Contoso%20Live" and "Contoso Live" are one model.</summary>
        private static string NormSwitchPart(string s)
        {
            try { s = s == null ? null : Uri.UnescapeDataString(s); } catch { }
            return NormLivePart(s);
        }

        /// <summary>Absent, unreadable or malformed reads as ON, the same fail-open the workflow settings use. It
        /// is safe here for a different reason: an enabled point still sends nothing without current consent, a
        /// Pro session and a configured service.</summary>
        internal bool DecisionPointEnabled(string name) => SwitchOf(DecisionSwitchPlaceFor(_sessions.Current), name).Value ?? true;

        /// <summary>What the model's switch says, or null for the default, and what to tell the person about it when it
        /// was not their own switch for this model that decided it.</summary>
        private (bool? Value, string Note) SwitchOf(DecisionSwitchPlace place, string name)
        {
            if (place == null) return (null, null);
            bool? own;
            string ownNote = null;
            if (place.Unsaved != null)
                lock (_decisionGate) own = place.Unsaved.UnsavedSwitches.TryGetValue(name, out var set) ? set : (bool?)null;
            else (own, ownNote) = OwnSwitchAndNote(place.File, name);
            if (own == false) return (false, ownNote);

            // The on this identity kept for good beats every off next door: an off written after it, by anyone who might be
            // this model, took it away when it was written (see RevokeIdentityOns).
            var identityOn = place.IdentityOn != null && OwnSwitch(place.IdentityOn, name) == true;
            string offNote = null;
            long? offAt = null;
            foreach (var neighbour in place.Neighbours)
            {
                var (note, at) = OffFrom(neighbour, name);
                if (note == null) continue;
                if (offNote == null) offNote = note;
                if (at != null && (offAt == null || at > offAt)) offAt = at;
            }
            // A connection's own on is older than an off written after it for the same title, so the off is the later word (Sol 6.1,
            // finding 2.2): window A switched on, window B of the same title switched off, A read again. An on written after the off
            // (a later explicit on) still wins, as does a place with no time to compare (an unsaved model, a switch written by an
            // older engine on the off side).
            if (own == true && place.Unsaved == null && offNote != null && offAt != null)
            {
                var ownAt = OwnSwitchAt(place.File, name);
                if (ownAt == null || ownAt < offAt) return (false, offNote);
            }
            if (own == true) return (true, offNote != null && place.EndsWithDesktop && !identityOn ? UntilDesktopClosesNote : null);
            if (identityOn) return (true, null);
            return offNote != null ? (false, offNote) : (null, null);
        }

        private static bool? OwnSwitch(string file, string name) => OwnSwitchAndNote(file, name).Value;

        private static (bool? Value, string Note) OwnSwitchAndNote(string file, string name)
        {
            try
            {
                if (file == null || !File.Exists(file)) return (null, null);
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    if (doc.RootElement.TryGetProperty("points", out var points)
                        && points.TryGetProperty(name, out var point)
                        && point.TryGetProperty("enabled", out var enabled)
                        && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
                    {
                        var note = point.TryGetProperty(NoteKey, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        return (enabled.GetBoolean(), string.IsNullOrWhiteSpace(note) ? null : note);
                    }
                }
            }
            catch { /* a malformed settings file must not change what the registry reports */ }
            return (null, null);
        }

        /// <summary>The off next door that may be this model's: what to tell the person, and when the newest applicable writer
        /// went off (null when the file was written by an engine that kept no times).</summary>
        private static (string Note, long? At) OffFrom(SwitchNeighbour neighbour, string name)
        {
            try
            {
                if (!File.Exists(neighbour.File)) return (null, null);
                using (var doc = JsonDocument.Parse(File.ReadAllText(neighbour.File)))
                {
                    if (!doc.RootElement.TryGetProperty("points", out var points)
                        || !points.TryGetProperty(name, out var point)
                        || !point.TryGetProperty("enabled", out var enabled)
                        || enabled.ValueKind != JsonValueKind.False)
                        return (null, null);
                    if (!point.TryGetProperty(OffForKey, out var writers) || writers.ValueKind != JsonValueKind.Array)
                        return (neighbour.OffNote(null), null);
                    var hasTimes = point.TryGetProperty(OffAtKey, out var times) && times.ValueKind == JsonValueKind.Object;
                    string found = null;
                    long? latest = null;
                    foreach (var w in writers.EnumerateArray())
                    {
                        var note = w.ValueKind == JsonValueKind.String ? neighbour.OffNote(w.GetString()) : null;
                        if (note == null) continue;
                        found ??= note;
                        if (hasTimes && times.TryGetProperty(w.GetString(), out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ticks)
                            && (latest == null || ticks > latest))
                            latest = ticks;
                    }
                    return (found, latest);
                }
            }
            catch { /* the same fail-open as the model's own file */ }
            return (null, null);
        }

        /// <summary>When the model's own file says the point was last written (UTC ticks), or null for a file an older engine wrote.</summary>
        private static long? OwnSwitchAt(string file, string name)
        {
            try
            {
                if (file == null || !File.Exists(file)) return null;
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                    if (doc.RootElement.TryGetProperty("points", out var points) && points.TryGetProperty(name, out var point)
                        && point.TryGetProperty(AtKey, out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ticks))
                        return ticks;
            }
            catch { }
            return null;
        }

        /// <summary>One switch, written for one model. An off goes next door first and an on goes to the model's own file
        /// first, so a write that stops halfway leaves the point off rather than on. An on takes nothing off next door: an
        /// off is shared with every model that might be the file it was switched off for, and an on is not.</summary>
        private void WriteSwitch(DecisionSwitchPlace place, string name, bool enabled, string note = null)
        {
            if (!enabled)
            {
                foreach (var neighbour in place.Neighbours.Where(n => n.Records)) RecordOff(neighbour.File, name, place.Writer, true);
                RevokeIdentityOns(place, name);
            }
            MutateDecisionSettings(place.File, root =>
            {
                var entry = new JsonObject { { "enabled", enabled }, { AtKey, SwitchStamp() } };
                if (!enabled && note != null) entry[NoteKey] = note;
                PointsOf(root)[name] = entry;
                return true;
            });
            if (enabled && place.IdentityOn != null)
                MutateDecisionSettings(place.IdentityOn, root =>
                {
                    PointsOf(root)[name] = new JsonObject { { "enabled", true } };
                    return true;
                });
        }

        private enum OffLeft { None, OwnOnly, Others }

        /// <summary>What off is still there after this model switched itself on: an off another writer left that this model's
        /// neighbours read, or only this model's own earlier off, which the views it was recorded for (a title view, a model
        /// known by nothing) still read because an on never clears a shared off.</summary>
        private static OffLeft OffRemainsForOthers(DecisionSwitchPlace place, string name)
        {
            var left = OffLeft.None;
            foreach (var neighbour in place.Neighbours)
            {
                try
                {
                    if (!File.Exists(neighbour.File)) continue;
                    using (var doc = JsonDocument.Parse(File.ReadAllText(neighbour.File)))
                    {
                        if (!doc.RootElement.TryGetProperty("points", out var points) || !points.TryGetProperty(name, out var point)
                            || !point.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False) continue;
                        if (!point.TryGetProperty(OffForKey, out var writers) || writers.ValueKind != JsonValueKind.Array)
                        {
                            if (neighbour.OffNote(null) != null) return OffLeft.Others;
                            continue;
                        }
                        foreach (var w in writers.EnumerateArray())
                        {
                            if (w.ValueKind != JsonValueKind.String) continue;
                            if (!string.Equals(w.GetString(), place.Writer, NameComparison))
                            {
                                if (neighbour.OffNote(w.GetString()) != null) return OffLeft.Others;
                            }
                            else if (neighbour.Records) left = OffLeft.OwnOnly;
                        }
                    }
                }
                catch { /* an unreadable file is not an off */ }
            }
            return left;
        }

        /// <summary>An off for a Desktop model takes away the ons kept for good by the identities that read that off, so the
        /// newer off wins: a file known by its title alone reads the offs written for its title, and a model known by nothing
        /// reads the offs of every Desktop file. It removes no off and no on nobody reads that off through.</summary>
        private void RevokeIdentityOns(DecisionSwitchPlace place, string name)
        {
            if (!place.Desktop || place.Folder == null) return;
            var files = new List<string> { Path.Combine(place.Folder, IdentityOnPrefix + "nothing.json") };
            if (place.TitleKey != null) files.Add(Path.Combine(place.Folder, IdentityOnPrefix + "title-" + place.TitleKey + ".json"));
            if (!place.Known && Directory.Exists(place.Folder))
                files.AddRange(Directory.GetFiles(place.Folder, IdentityOnPrefix + "title-*.json"));
            foreach (var file in files.Distinct(StringComparer.Ordinal))
                if (File.Exists(file)) MutateDecisionSettings(file, root => PointsOf(root).Remove(name));
        }

        /// <summary>Adds or removes this model's name on one point's off in a neighbour file. An off written before
        /// per-model switches names no one and was switched off for everyone there, so it is left exactly as it is.</summary>
        private void RecordOff(string file, string name, string writer, bool off)
        {
            if (!off && !File.Exists(file)) return;
            MutateDecisionSettings(file, root =>
            {
                var points = PointsOf(root);
                var entry = points[name] as JsonObject;
                var enabled = entry?["enabled"];
                if (entry != null && entry[OffForKey] == null && enabled != null && enabled.GetValueKind() == JsonValueKind.False) return false;
                var writers = (entry?[OffForKey] as JsonArray)?
                    .Select(w => w != null && w.GetValueKind() == JsonValueKind.String ? w.GetValue<string>() : null)
                    .Where(w => w != null).ToList() ?? new List<string>();
                var before = writers.Count;
                // When each writer went off, kept so a connection's own on can be told older or newer than the off (Sol 6.1, 2.2).
                var times = new Dictionary<string, long>(StringComparer.Ordinal);
                if (entry?[OffAtKey] is JsonObject stamped)
                    foreach (var kv in stamped)
                        if (kv.Value != null && kv.Value.GetValueKind() == JsonValueKind.Number && kv.Value.AsValue().TryGetValue<long>(out var t)) times[kv.Key] = t;
                writers.RemoveAll(w => string.Equals(w, writer, NameComparison));
                times.Remove(writer);
                if (off) { writers.Add(writer); times[writer] = SwitchStamp(); }
                if (writers.Count == 0)
                {
                    if (entry == null) return false;
                    points.Remove(name);
                    return true;
                }
                if (!off && writers.Count == before) return false;
                var at = new JsonObject();
                foreach (var w in writers) if (times.TryGetValue(w, out var t)) at[w] = t;
                points[name] = new JsonObject
                {
                    { "enabled", false },
                    { OffForKey, new JsonArray(writers.Select(w => (JsonNode)JsonValue.Create(w)).ToArray()) },
                    { OffAtKey, at },
                };
                return true;
            });
        }

        private static JsonObject PointsOf(JsonObject root)
        {
            if (root["points"] is JsonObject points) return points;
            points = new JsonObject();
            root["points"] = points;
            return points;
        }

        /// <summary>What a model's switches say before a save moves it, so the save can carry them to where it lands.</summary>
        internal sealed class DecisionSwitchesBeforeSave
        {
            public string File;
            public Dictionary<string, bool> Values;
            /// <summary>For an off the model did not set itself, the sentence that says where it came from.</summary>
            public Dictionary<string, string> Notes;
        }

        internal DecisionSwitchesBeforeSave DecisionSwitchesBefore(Session s)
        {
            var place = DecisionSwitchPlaceFor(s);
            if (place == null) return null;
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var point in DecisionRegistry.Points)
            {
                var value = SwitchOf(place, point.Name).Value;
                if (value == null) continue;
                values[point.Name] = value.Value;
                if (value != false) continue;
                // An off read next door becomes the model's own at the save, and says where it came from; an off the model
                // set itself keeps whatever note it had.
                var (own, ownNote) = place.Unsaved != null ? (place.Unsaved.UnsavedSwitches.TryGetValue(point.Name, out var set) ? set : (bool?)null, null)
                                                           : OwnSwitchAndNote(place.File, point.Name);
                notes[point.Name] = own == false ? ownNote : CarriedOffNote;
            }
            return new DecisionSwitchesBeforeSave { File = place.File, Values = values, Notes = notes };
        }

        /// <summary>A save that moves a model (a first save, or a save as) gives it a switch file of its own where it
        /// lands. What it said goes with it, so saving never turns a point back on, and nothing is written for any other
        /// model there. Returns what to tell the person when that could not be written, rather than throwing, so the save
        /// finishes everything else it does first.</summary>
        internal Exception CarryDecisionSwitches(Session s, DecisionSwitchesBeforeSave before)
        {
            if (before == null || before.Values.Count == 0) return null;
            var place = DecisionSwitchPlaceFor(s);
            if (place?.File == null || string.Equals(place.File, before.File, NameComparison)) return null;
            try
            {
                foreach (var value in before.Values)
                    WriteSwitch(place, value.Key, value.Value, before.Notes != null && before.Notes.TryGetValue(value.Key, out var note) ? note : null);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The operating system's words and the folder's path stay in the inner exception, not in the sentence.
                return new IOException(Entitlement.FeatureGrants.IsProTier(_entitlement) ? SwitchesNotKeptNote : SwitchesNotKeptFreeNote, ex);
            }
        }

        private void MutateDecisionSettings(string file, Func<JsonObject, bool> mutate)
        {
            lock (_decisionGate)
            {
                JsonObject root = null;
                if (File.Exists(file))
                {
                    try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
                    catch (JsonException) { root = null; }
                }
                if (root == null) root = new JsonObject();

                if (!mutate(root)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, file, overwrite: true);
            }
        }

        // ---- consent ---------------------------------------------------------------------------------

        public async Task<DecisionConsentInfo> GetConsentAsync()
        {
            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            return DescribeConsent(Decisions.ReadConsent(scope), scope);
        }

        /// <summary>Agree that this model's details may be sent.
        ///
        /// <paramref name="expectedScope"/> is which model the person was ANSWERING ABOUT, as get_consent
        /// reported it when the question was put. It is optional and it is the fix for Astra's second
        /// finding (2026-09-22): the permission card is one surface that outlives a model swap, so a card
        /// opened on one model could be answered after the other door opened another, and the grant
        /// landed on whichever model happened to be current. A question about one model is not an answer
        /// about another, so when the two disagree nothing is written and the person is told plainly.
        ///
        /// Left out, this behaves exactly as it always did: agree for whatever is open. The agent door
        /// may leave it out, and does. It is evidence the caller can offer, not a new requirement.</summary>
        public async Task<DecisionConsentInfo> SetConsentAsync(string[] fields, int schemaVersion, string origin, string expectedScope = null)
        {
            // THE PERSON'S WORD IN CHAT IS ENOUGH (Kane, 10 Oct 2026, 1.4 chatyes). Their assistant may turn Jev on for a model after the
            // person says yes in the conversation: no question in the Waiting for you list, no click in Studio. The grant is written the
            // way the person's own is and records who gave it (GrantedBy), so the consent card says "Turned on by your assistant", and
            // the person can turn it off again. The Permissions page (presets, cells, the guard switch, held actions) is still the person's.
            var grantedBy = IsAssistantOrigin(origin) ? "agent" : "human";
            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);

            // The expected scope is compared BEFORE the marker is minted, because it is what the person
            // was shown when the question was put, and an unstamped model is shown as unstamped.
            if (!string.IsNullOrEmpty(expectedScope) && !string.Equals(expectedScope, scope, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "That answer was about a model that is not the model open now, so nothing was changed. "
                    + "Open that model again to give permission for it, or answer the question again for this one.");

            var identity = OpenModelIdentity();
            if (string.IsNullOrEmpty(identity)) throw new InvalidOperationException(DecisionConsentStore.NoScopeReason);

            // Permission is kept on this machine under the scope read above and never written into the model (K19).
            var stamped = scope;
            var consent = DecisionConsentStore.Grant(stamped, fields, schemaVersion, DateTimeOffset.UtcNow, grantedBy);
            if (grantedBy == "human") NoteWindowGrant(fields, schemaVersion);   // this window has the person's OK (hero19 D11)
            PublishConsentChanged(origin, "permission given");
            TellThePlanConsentMoved();
            return DescribeConsent(consent, stamped);
        }

        /// <summary>Permission decides what an open Plan draft says (that Add more words asks Jev, or why it cannot), and the
        /// page reads a draft again only when the Plan moves. Giving permission moved it by chance (the marker is a model
        /// edit); withdrawing did not, so an open draft kept promising that Jev would be asked (verifier on m11/sol-fixes-1
        /// round 3, VR4w). Both now say the Plan moved; the agent door reads the change on its next call.</summary>
        private void TellThePlanConsentMoved()
        {
            try
            {
                var context = _sessions.CurrentContext;
                if (context != null) PublishPlan(context.Plans.Current, context.Session?.Revision ?? 0);
            }
            catch { }
            // A start plan that waited for permission may be free to go on now (or have a new reason to wait): look at it once, off this call's path.
            if (_startPlan != null) _ = Task.Run(ReevaluateStartPlanAsync);
        }

        // UNDO IS UNDO (coordinator, 26 Sep, verifier round 3). The session this engine last wrote a marker into, and
        // which marker. In that session the model is either marked, or the person undid the marker: it is never lent
        // the pending marker (that is only for the same files opened again, a restart's Discard), an undo takes the
        // permission back, and the pending stamp goes with it so a later restart asks again too. A redo puts both back.
        private string _markerSessionId;
        private string _markerIdentity;
        private string _markerValue;
        private string _markerObservedSessionId;
        private bool _markerPresent;   // whether the pending stamp stands for a marker the model holds right now (under _decisionGate)

        private void MarkerWritten(Session s, string identity, string marker)
        {
            bool attach;
            lock (_decisionGate)
            {
                _markerSessionId = s.Id;
                _markerIdentity = identity;
                _markerValue = marker;
                _markerPresent = true;
                attach = !string.Equals(_markerObservedSessionId, s.Id, StringComparison.Ordinal);
                if (attach) _markerObservedSessionId = s.Id;
            }
            // THE PERMISSION IS SETTLED INSIDE THE COMMIT THAT CHANGES IT (Sol 6.1 on the M11 delta, finding A2, the second strike of 2.3). Every
            // earlier version settled it AFTER the commit, from outside: a re-read off the bus, then one at the end of undo, redo and undo-to.
            // Each ran when the session might already be retired by another model opening, and a retired session refuses reads, so an undo
            // that was already running when the other model opened left the pending stamp behind and the same files, opened again, read it as a
            // fresh yes. An observer runs on the session's own dispatcher in the commit itself (every undo, redo, walk and edit is one), before
            // the call that made it returns and before the session's lifetime is released, so no model swap can come between the undo and
            // the settlement, and a history walk is covered by the same hook as a single step.
            if (attach) s.RegisterObserver(new MarkerSettler(this, s.Id));
        }

        /// <summary>Re-reads the permission marker in the commit that may have changed it. Dispatcher thread, inside the commit.</summary>
        private sealed class MarkerSettler : ISessionObserver
        {
            private readonly LocalEngine _engine;
            private readonly string _sessionId;
            public MarkerSettler(LocalEngine engine, string sessionId) { _engine = engine; _sessionId = sessionId; }
            public void BeforeCommit(Model model) { }
            public void AfterCommit(SessionCommit commit) => _engine.SettlePendingMarker(_sessionId, commit.Model);
        }

        /// <summary>In the session a marker was written into: marked means the pending stamp stands for the files it was
        /// loaded from (a redo puts it back); unmarked means the person undid it, so the pending stamp is cleared. Runs on the
        /// session's dispatcher inside the commit (<see cref="MarkerWritten"/>), so what it reads is what the commit left.</summary>
        private void SettlePendingMarker(string sessionId, Model model)
        {
            string identity, marker;
            bool present;
            lock (_decisionGate)
            {
                if (!string.Equals(_markerSessionId, sessionId, StringComparison.Ordinal)) return;
                identity = _markerIdentity; marker = _markerValue; present = _markerPresent;
            }
            var now = ConsentIdOf(model);
            var holds = string.Equals(now, marker, StringComparison.Ordinal);
            // Only a change in whether the model holds the marker needs the stamp touched; every other edit leaves it as it is, so the hash of the
            // model's files (the stamp) is not taken inside every commit.
            if (now != null && !holds) return;
            if (holds == present) return;
            var files = holds ? LoadedFilesUnchanged() : null;
            lock (_decisionGate)
            {
                if (!string.Equals(_markerSessionId, sessionId, StringComparison.Ordinal)) return;
                _markerPresent = holds;
                if (!holds) DecisionConsentStore.ClearPending(identity);
                else DecisionConsentStore.MarkPending(DecisionConsentStore.ScopeKey(identity, marker), files);
            }
        }

        /// <summary>The content stamp of the files the open model was loaded from, when those files are still exactly
        /// what it was loaded from; otherwise null. A live model has no files and is always null.</summary>
        private string LoadedFilesUnchanged()
        {
            var loaded = _loadedDiskStamp;
            if (string.IsNullOrEmpty(loaded)) return null;
            try { return string.Equals(ModelDiskStamp(), loaded, StringComparison.Ordinal) ? loaded : null; }
            catch { return null; }
        }

        /// <summary>The marker a grant at this address is pending on, when the open model is unmarked and was loaded
        /// from exactly the files that grant's marker was minted on; otherwise null, which asks again. Never in the
        /// session that wrote a marker: there an unmarked model means the person undid it, and the stamp is cleared.</summary>
        private string PendingConsentWitness(string identity)
        {
            var s = _sessions.Current;
            string sessionId;
            lock (_decisionGate) sessionId = _markerSessionId;
            if (s != null && string.Equals(s.Id, sessionId, StringComparison.Ordinal))
            {
                lock (_decisionGate) { _markerPresent = false; DecisionConsentStore.ClearPending(identity); }
                return null;
            }
            if (!DecisionConsentStore.AnyPending(identity)) return null;
            return DecisionConsentStore.PendingWitness(identity, LoadedFilesUnchanged());
        }

        // TE2's name for the annotations property (Properties.ANNOTATIONS, internal to the wrapper).
        private const string AnnotationsProperty = "Annotations";

        /// <summary>Before a save: an unmarked model whose permission is pending on the files it was loaded from gets
        /// its marker back, so the save carries it and the permission travels with the model from then on.
        ///
        /// PART OF THE SAVE, NOT A STEP (verifier round 4, D12). The marker goes back WITHOUT an undo step of its own.
        /// As an undoable step it landed on top of the timeline, after the person's own last edit, so their next plain
        /// Undo took back Semanticus's marker instead of their edit, and an undone marker withdraws the permission. The
        /// person never wrote this marker: the permission was already theirs (the pending stamp), and the save only makes
        /// the files carry it. So the annotation is written the way an audit record is (<see cref="AuditAnnotations"/>,
        /// no undo action), inside an ordinary commit that carries its one delta: both doors are told, History shows
        /// the row under Semanticus (origin "system", D11), and the ledger files it as a change that made no step
        /// (EditLedger: StepIndex 0), so neither a plain Undo nor "Undo to here" can reach it. The model reads unsaved
        /// until the save that follows lands. The marker an older build wrote when a person said OK
        /// stays an ordinary undoable edit, and undoing THAT one still takes the permission back.</summary>
        private async Task StampPendingConsentMarkerAsync()
        {
            var s = _sessions.Current;
            if (s == null) return;
            var identity = OpenModelIdentity();
            if (string.IsNullOrEmpty(identity) || !DecisionConsentStore.AnyPending(identity)) return;
            if (await s.ReadAsync(ConsentIdOf).ConfigureAwait(false) != null) return;
            var pending = PendingConsentWitness(identity);
            if (pending == null) return;
            await s.MutateAsync("system", "mark this model so permission can be kept for it", m =>
            {
                // The non-undoable write raises no change event, so the delta the undoable one would have raised is
                // recorded by hand, rendered the same way (ChangeValues).
                var (before, after) = ChangeValues.For(m, AnnotationsProperty, null, ConsentIdAnnotation + ":" + pending);
                AuditAnnotations.Set(m, ConsentIdAnnotation, pending);
                s.MarkConsentMarkerDirty();
                s.RecordDelta(new ChangeDelta
                {
                    Kind = "update",
                    Ref = ObjectRefs.For(m),
                    Props = new[] { AnnotationsProperty },
                    Before = before,
                    After = after,
                });
            }).ConfigureAwait(false);
            MarkerWritten(s, identity, pending);
        }

        public async Task<DecisionConsentInfo> WithdrawConsentAsync(string origin)
        {
            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            var consent = DecisionConsentStore.Withdraw(scope, DateTimeOffset.UtcNow);
            ForgetWindowGrant();   // the person's no beats the OK this window carried (hero19 D11)
            // What the person let a start plan's source disclose goes with the permission it was given under (the generation
            // already moved, so the record would not count; this also takes the plan's own copy and its Deciding state back).
            await RevokeStartDisclosureAsync(scope, origin).ConfigureAwait(false);
            // Order matters: the file says no BEFORE anything in flight is cancelled, so a request that squeaks
            // past the cancellation still fails the recheck when its reply lands.
            Decisions.OnConsentWithdrawn(scope);
            // The families' last run was decided under the permission just withdrawn. Forget it here, not at the next
            // plan read: a grant given before that read would otherwise put the old run's advice straight back.
            ForgetJevRun();
            PublishConsentChanged(origin, "permission withdrawn");
            TellThePlanConsentMoved();
            return DescribeConsent(consent, scope);
        }

        /// <summary>Permission lives in a file beside the model, not in it, so giving or withdrawing it moves no revision
        /// and made no change anybody heard, except the first grant, which marks the model. The other door's permission
        /// card and What Pro decides panel read again on a change, so every grant and withdrawal is broadcast as one
        /// with no deltas, the way a report-scope change is (LocalEngine.ReportScope PublishScopeChanged; verify, 28 Sep, L8).</summary>
        private void PublishConsentChanged(string origin, string label)
        {
            var s = _sessions.Current;
            if (s == null) return;
            try
            {
                _sessions.Bus.Publish(new ChangeNotification
                {
                    SessionId = s.Id, Revision = s.Revision, Origin = origin, Label = label,
                    Deltas = Array.Empty<ChangeDelta>(),
                });
            }
            catch { /* observational: a broken listener must never fail the answer the person gave */ }
        }

        // The scope is passed in rather than read again: it is the key the record beside it was just read or
        // written under, and reading it twice would let an edit between the two lines describe one model's
        // permission under another model's key.
        private DecisionConsentInfo DescribeConsent(DecisionConsent consent, string scope)
        {
            string reason;
            var current = consent.Covers(null, DecisionRegistry.ConsentSchemaVersion, out reason);
            if (string.IsNullOrEmpty(scope))
            {
                current = false;
                reason = DecisionConsentStore.NoScopeReason;
            }
            if (current)
            {
                var declared = new HashSet<string>(DecisionRegistry.DeclaredFields, StringComparer.Ordinal);
                var missing = declared.Where(f => !consent.ApprovedFields.Contains(f, StringComparer.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    current = false;
                    reason = $"permission does not cover every detail this build could send ({string.Join(", ", missing)})";
                }
            }

            return new DecisionConsentInfo
            {
                Granted = consent.Granted,
                SchemaVersion = consent.SchemaVersion,
                SchemaVersionInForce = DecisionRegistry.ConsentSchemaVersion,
                Current = current,
                Fields = consent.ApprovedFields.ToArray(),
                FieldsInForce = DecisionRegistry.DeclaredFields.ToArray(),
                GrantedUtc = consent.GrantedUtc,
                GrantedBy = consent.Granted ? (consent.GrantedBy ?? "human") : null,
                WithdrawnUtc = consent.WithdrawnUtc,
                AppliesTo = scope,
                Reason = current ? null : reason,
            };
        }

        // ---- the start plan's permission (lane sf-consent, design-spec-first-start-v2.md S1 and S2) ------------------------------
        // Two things live here, and neither is a capability only one door has. The DISCLOSURE grant is on both doors, like
        // set_consent: it says which source and tables Pro may see. The APPROVAL lets a start plan be built; since 10 Oct 2026
        // (Kane: the person's word in chat is enough) the assistant may give it too, through approve_start_plan.

        /// <summary>The document the consent verbs read and write. The engine sets it to a host over sf-core's store (see
        /// <c>StartPlanHostOverStore</c>); a test may swap it, and null reads as no plan. A seam rather than a field of the plan,
        /// so the consent rules never own the plan's storage.</summary>
        internal IStartPlanConsentHost StartPlanHost { get; set; }

        /// <summary>Told after a disclosure is given or taken back, so a decide loop waiting on the grant can start, and one running can stop.</summary>
        internal Action StartDisclosureChanged;

        private const string NoStartPlanNote = "There is no plan to agree to. Start a model from a SQL source first.";
        private const string StalePlanNote = "The plan changed after you looked at it, so nothing was saved. Look at it again.";

        /// <summary>The person says which tables of the hidden source Pro may see for this plan. Bound to the digest of the plan they
        /// were shown, so they agree to what was on the screen. Needs the model's own permission to be current first: this narrows
        /// it for a source, it never replaces it. Pro only (a free session asks nothing, so there is nothing to agree to).</summary>
        public async Task<StartPlanView> GrantStartDisclosureAsync(string digest, string origin)
        {
            // THE PERSON'S WORD IN CHAT IS ENOUGH (Kane, 10 and 11 Oct 2026, 1.4 chatyes), as for set_consent. The assistant may let Jev see the
            // plan's source and tables after the person says yes in the conversation: no question in the Waiting for you list, no click in Studio.
            // The grant is bound to the digest of the plan shown, so it covers exactly that source, those tables and that sample setting, and the
            // person can take it back by withdrawing the model's permission. The Permissions page stays the person's.
            if (IsAssistantOrigin(origin) && DryRunScope.Current?.FromTool == true) throw new InvalidOperationException(GateRegistry.CannotPreviewSentence);
            var host = StartPlanHost;
            var doc = host?.Current();
            if (doc == null) throw new InvalidOperationException(NoStartPlanNote);
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
                throw new InvalidOperationException("Pro suggestions are part of Semanticus Pro. This plan is made without them, so there is nothing to agree to.");
            if (!string.Equals(host.DigestOf(doc), digest, StringComparison.Ordinal)) throw new InvalidOperationException(StalePlanNote);

            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(scope)) throw new InvalidOperationException(DecisionConsentStore.NoScopeReason);
            var consent = Decisions.ReadConsent(scope);
            // D2 (launch video, 12 Oct): one "allow" gave nothing until the model's own permission was given too, so the person was asked twice. The yes
            // covers what the build needs: the model's permission (the same grant set_consent writes, with its giver on record) and the tables of this plan.
            if (!DescribeConsent(consent, scope).Current)
            {
                await SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, origin).ConfigureAwait(false);
                scope = await DecisionConsentScopeAsync().ConfigureAwait(false);   // as the grant's own read below does, after the stamp the permission may write
                consent = Decisions.ReadConsent(scope);
            }

            // The sample of rows is part of what the person agrees to see. While it is still being read the plan is not the plan it will be, so a grant now
            // would be for a picture that is about to change (the digest moves when the sample lands, and the person would be asked again anyway).
            if (string.Equals(doc.Sample?.State, StartSampleStatus.Reading, StringComparison.Ordinal))
                throw new InvalidOperationException("A sample of the rows is still being read. Wait a moment, then allow it, so you agree to the plan with the sample in it.");

            var sample = host.SampleFactsOn(doc);
            var granted = DisclosedScope.Of(doc, sample);
            var now = DateTime.UtcNow;
            StartDisclosureStore.Grant(scope, doc.Id, granted, consent.Generation, now);
            var tables = DisclosedScope.IncludedSourceTables(doc);
            var view = await host.UpdateAsync(d =>
            {
                // The plan's own copy, for the view. The record that counts is the one kept beside the model's permission above.
                d.Disclosure = new StartDisclosure { SourceKey = DisclosedScope.SourceKeyOf(d.Source), IncludedTables = tables, SampleFacts = sample, SampleTables = sample ? granted.SampleTables : null, Utc = now };
                // A person who earlier chose to carry on without Jev has now changed their mind: the plan is Jev's again.
                d.JevDeclined = false;
                d.Answers.Remove("grant");
                // The plan leaves its wait for the grant now (W7): the loop is kicked below, and a plan Jev does not decide (Free) is never in this state.
                if (d.Decide != null && (d.Decide.State == StartDecideState.WaitingGrant || (d.Decide.State == StartDecideState.Off && doc.JevDeclined)))
                {
                    d.Decide.State = StartDecideState.Deciding;
                    d.Decide.Reason = null;
                }
                return true;
            }).ConfigureAwait(false);
            PublishConsentChanged(origin, "start plan permission given");
            NotifyStartPlanChanged(view, origin);
            try { StartDisclosureChanged?.Invoke(); } catch { }
            try { await KickStartDecideOnPlanAsync().ConfigureAwait(false); }   // the loop starts (or says why it cannot), on the held plan
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] starting the decide after the grant failed: " + ex.Message); } catch { } }
            return await GetStartPlanAsync().ConfigureAwait(false);
        }

        /// <summary>Take back what a plan's source was allowed to show, when the model's permission is withdrawn.</summary>
        private async Task RevokeStartDisclosureAsync(string scope, string origin)
        {
            StartDisclosureStore.Clear(scope);
            var host = StartPlanHost;
            if (host?.Current() == null) return;
            try
            {
                var changed = false;
                var moved = await host.UpdateAsync(d =>
                {
                    if (d.Disclosure == null && !string.Equals(d.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal)) return false;
                    d.Disclosure = null;
                    if (d.Decide != null && (d.Decide.State == StartDecideState.Deciding || d.Decide.State == StartDecideState.WaitingGrant))
                    {
                        d.Decide.State = StartDecideState.WaitingGrant;
                        d.Decide.Reason = "You took permission back, so Pro has stopped. Say again which tables it may see to carry on.";
                    }
                    return changed = true;
                }).ConfigureAwait(false);
                if (changed) NotifyStartPlanChanged(moved, origin);
            }
            catch { /* the record above is gone, which is what stops the next ask; the plan's copy follows on its next change */ }
            try { StartDisclosureChanged?.Invoke(); } catch { }
        }

        /// <summary>Approve ONE exact plan: the person in Studio, or an assistant once the person said yes in chat (Kane, 10 Oct 2026). The
        /// approval is the digest of the plan that was shown; the build runs only while the plan's digest still equals it, so ANY edit voids it.
        /// It records who approved, so the build's History entry credits the assistant or the person.</summary>
        public async Task<StartPlanView> ApproveStartPlanAsync(string digest, string origin = "human")
        {
            // The approval is a writer of the plan like an edit or the build, so it waits for the one gate: it cannot land in the middle of a build or a words write.
            await _startPlanGate.WaitAsync().ConfigureAwait(false);
            try { return await ApproveStartPlanGatedAsync(digest, origin).ConfigureAwait(false); }
            finally { await EnsureStartDecideNotOrphanedAsync().ConfigureAwait(false); _startPlanGate.Release(); }
        }

        private async Task<StartPlanView> ApproveStartPlanGatedAsync(string digest, string origin)
        {
            var host = StartPlanHost;
            var doc = host?.Current();
            if (doc == null) throw new InvalidOperationException(NoStartPlanNote);
            if (string.IsNullOrEmpty(digest) || !string.Equals(host.DigestOf(doc), digest, StringComparison.Ordinal))
                throw new InvalidOperationException(StalePlanNote);
            var view = await host.UpdateAsync(d =>
            {
                // Read again under the document's own lock: an edit that landed since the line above must not be approved unseen.
                if (!string.Equals(host.DigestOf(d), digest, StringComparison.Ordinal)) throw new InvalidOperationException(StalePlanNote);
                d.Approval = new StartApproval { Digest = digest, Utc = DateTime.UtcNow, By = AgentPolicyGuard.IsHuman(origin) ? "human" : "agent" };
                return true;
            }).ConfigureAwait(false);
            NotifyStartPlanChanged(view, AgentPolicyGuard.IsHuman(origin) ? "human" : "agent");
            return view;
        }

        /// <summary>The fence every start-plan ask carries (finding S2). Built once per ask by the decide loop, which says which plan
        /// round and scratch it is asking from (<paramref name="loopNow"/> reads the loop's current ones) and what the ask is about.
        ///
        /// WHAT IT HOLDS. The session fence part: the person's open model, the scratch and the round must all still be the ones this
        /// ask began under, so a swap of model, a rebuilt scratch or a new round makes an ask in flight stale. The Refuse part is the
        /// permission: <see cref="StartPlanConsent"/>'s rules, read fresh each time, so withdrawing mid-loop, a table added after the
        /// grant and the sample switched on all stop the NEXT ask, and an excluded table's name in the request stops this one.</summary>
        internal async Task<DecisionSessionFence> StartAskFenceAsync(string scratchSessionId, int round,
                                                                    Func<(string ScratchId, int Round)> loopNow, StartAskScope ask)
        {
            var person = _sessions.Current;
            var personId = person?.Id;
            var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            string Compose(string id) => personId + "|" + id;

            Func<(string SessionId, long Revision)> readNow = () =>
            {
                var now = _sessions.Current;
                var loop = loopNow == null ? (ScratchId: scratchSessionId, Round: round) : loopNow();
                return (Compose(loop.ScratchId), now == null || !string.Equals(now.Id, personId, StringComparison.Ordinal) ? -1L : loop.Round);
            };

            return new DecisionSessionFence(Compose(scratchSessionId), round, readNow, request => StartAskRefusalAsync(scope, StartPlanHost?.Current(), ask, request));
        }

        /// <summary>The whole refusal for one start-plan ask, or for the loop's own start and end of a round (<paramref name="request"/> null): a clause saying why
        /// nothing may be sent, or null. ONE function, so the fence and the loop's bar can never disagree. Read fresh each time from the grant kept beside the
        /// model's permission (never the plan's own copy of it), and from the plan as it is NOW.</summary>
        private async Task<string> StartAskRefusalAsync(string scope, StartPlanDoc doc, StartAskScope ask, DecisionRequest request)
        {
            var host = StartPlanHost;
            if (doc == null || host == null) return "there is no start plan open";
            // The model's identity can move under an ask in flight (saved somewhere else, or another model at this address).
            var nowScope = await DecisionConsentScopeAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(scope) || !string.Equals(nowScope, scope, StringComparison.Ordinal))
                return "this is no longer the model you agreed for";
            var consent = Decisions.ReadConsent(scope);
            var granted = consent.Granted ? StartDisclosureStore.Read(scope, doc.Id, consent.Generation) : null;
            var planNow = DisclosedScope.Of(doc, host.SampleFactsOn(doc));
            return StartPlanConsent.WhyNot(granted, planNow, ask) ?? (request == null ? null : StartPlanConsent.WhyNotRequest(doc, granted, request));
        }

        // ---- the budget ------------------------------------------------------------------------------

        /// <summary>Two allowances, reported together and never mixed. The first four numbers are THIS session on
        /// THIS machine, counted locally and pessimistically. The monthly ones are the whole licence's, settled
        /// by the relay across every session everywhere it is used, so they can only be read from the relay and
        /// they are absent with a plain reason whenever it cannot be reached.
        ///
        /// This is the one read on either door that goes to a network. It is bounded by the oracle's own
        /// deadline, it never throws, and a relay that is down costs the caller a slow answer with a reason
        /// rather than a failed operation.</summary>
        public async Task<DecisionBudgetInfo> GetDecisionBudgetAsync()
        {
            var d = Decisions;
            var jev = d.Oracle as JevDecisionOracle;

            var info = new DecisionBudgetInfo
            {
                CapInputTokens = d.Budget.CapInputTokens,
                UsedInputTokens = d.Budget.UsedInputTokens,
                RemainingInputTokens = d.Budget.RemainingInputTokens,
                Requests = d.Budget.Requests,
                UsedUsd = d.Budget.UsedUsd,
                Exhausted = d.Budget.Exhausted,
                ServiceAvailable = d.Oracle.Available,
                Route = jev == null ? JevDecisionOracle.RouteNone : jev.Route,
            };

            if (jev == null || !jev.IsRelay)
            {
                info.MonthlyReason = jev == null
                    ? "there is no suggestion service set up for this session, so there is no monthly allowance to read"
                    : "this session is set up for development and goes straight to the service, so there is no monthly allowance to read";
                return info;
            }

            var reading = await jev.ReadBudgetAsync(CancellationToken.None).ConfigureAwait(false);
            if (reading == null || !reading.Read)
            {
                info.MonthlyReason = reading?.Reason ?? "the monthly allowance could not be read";
                return info;
            }

            info.MonthlyUsedInputTokens = reading.UsedInputTokens;
            info.MonthlyCapInputTokens = reading.CapInputTokens;
            info.MonthlyUsedUsd = JevPrice.UsdFor(reading.UsedInputTokens);
            info.MonthlyCapUsd = JevPrice.UsdFor(reading.CapInputTokens);
            info.MonthlyResetsAtUtc = reading.ResetsAtUtc;
            return info;
        }

        // ---- the proof point -------------------------------------------------------------------------

        /// <summary>Propose a display folder and a number format for a measure that has neither. Reads the model,
        /// leaves the dispatcher, asks, and returns the proposal. It writes nothing, and a skipped point is a
        /// normal result with a reason, never a refusal.</summary>
        public async Task<MeasureDefaultsProposal> ProposeMeasureDefaultsAsync(string objRef)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var s = _sessions.Require();

            var proposal = new MeasureDefaultsProposal
            {
                ObjectRef = objRef,
                ConfidenceBar = point.ConfidenceBar,
            };

            // One pass over the model, on the dispatcher, building only what the point declared. The ask happens
            // after this returns, OFF the dispatcher, which is the rule at docs/redesign/jev-plan.md:22.
            var prepared = await s.ReadAsync(m =>
            {
                var measure = ObjectRefs.Resolve(m, objRef) as Measure;
                if (measure == null) return new PreparedMeasureAsk();

                // The revision is read HERE, inside the one protected read, so it is the revision this exact
                // snapshot was taken at. Reading it afterwards let an edit that landed in between stamp an old
                // snapshot with a current revision, which is stale evidence wearing a fence that says fresh.
                var found = new PreparedMeasureAsk { Found = true, Ref = ObjectRefs.For(measure), Revision = s.Revision };

                bool wantsFolder, wantsFormat;
                if (!MeasureDefaultsPoint.WantsProposal(measure, out wantsFolder, out wantsFormat)) return found;

                var folders = DisplayFolderSuggestions(m, measure.Table);
                var uses = MeasureDefaultsPoint.FolderUses(m);
                var here = MeasureDefaultsPoint.FoldersOf(measure.Table);
                var questions = MeasureDefaultsPoint.Questions(folders, wantsFolder, wantsFormat, uses, here);
                if (wantsFolder)
                {
                    var ranked = MeasureDefaultsPoint.RankFolders(folders, uses, here);
                    found.FoldersNotOffered = ranked.Total - ranked.Offered.Count;
                }
                if (questions.Count == 0) return found;

                found.Snapshot = MeasureDefaultsPoint.Snapshot(measure);
                found.Questions = questions;
                return found;
            }).ConfigureAwait(false);

            var hook = BetweenSnapshotAndFenceForTest;
            if (hook != null) await hook().ConfigureAwait(false);

            if (!prepared.Found)
                throw new InvalidOperationException($"{objRef} is not a measure. Run list_measures to find the ref.");

            proposal.ObjectRef = prepared.Ref;
            proposal.FoldersNotOffered = prepared.FoldersNotOffered;

            if (prepared.Questions == null)
            {
                proposal.Reason = "this measure already has a folder and a format, so there was nothing to suggest";
                return proposal;
            }

            var fence = new DecisionSessionFence(s.Id, prepared.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });

            var outcome = await Decisions.AskAsync(
                point.Name, prepared.Snapshot, prepared.Questions,
                // Tier, read through FeatureGrants, the one place grants are computed. Not one named ProFeature:
                // a decision point is not one of the four Pro tabs, and naming one would be wrong the day a
                // narrower tier is sold. See FeatureGrants.IsProTier.
                isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                enabled: DecisionPointEnabled(point.Name),
                fence: fence,
                ct: CancellationToken.None).ConfigureAwait(false);

            if (!outcome.Asked)
            {
                proposal.Reason = outcome.SkippedReason;
                return proposal;
            }

            proposal.Asked = true;
            proposal.ServedModelVersion = outcome.Answers.ServedModelVersion;

            if (!outcome.Answers.Decided)
            {
                proposal.Reason = outcome.Answers.NotDecidedReason;
                return proposal;
            }

            double folderConfidence, formatConfidence;
            proposal.ProposedDisplayFolder = MeasureDefaultsPoint.ProposedFolder(outcome.Answers, point.ConfidenceBar, out folderConfidence);
            proposal.ProposedFormatFamily = MeasureDefaultsPoint.ProposedFormatFamily(outcome.Answers, point.ConfidenceBar, out formatConfidence);
            proposal.ProposedDisplayFolderConfidence = folderConfidence;
            proposal.ProposedFormatFamilyConfidence = formatConfidence;

            if (proposal.ProposedFormatFamily != null)
            {
                // The same rule as every other Jev format surface: only a whole-number format on a measure the engine PROVES holds a whole
                // number is a fill. Anything else is a suggestion, with the note, for the person to check.
                var proved = string.Equals(proposal.ProposedFormatFamily, "whole_number", StringComparison.Ordinal)
                    && await s.ReadAsync(m => ObjectRefs.Resolve(m, prepared.Ref) is Measure measure && Semanticus.Analysis.FormatFill.MayFill(measure, "#,0")).ConfigureAwait(false);
                proposal.ProposedFormatIsSuggestion = !proved;
                proposal.ProposedFormatNote = proved ? null : JevFormatSuggestion.Note;
            }

            if (proposal.ProposedDisplayFolder == null && proposal.ProposedFormatFamily == null)
                proposal.Reason = "nothing was sure enough to suggest";

            return proposal;
        }

        /// <summary>What the one read pass over the model produced. Questions stays null when there is nothing
        /// left to ask, which is a normal answer and not a failure.</summary>
        private sealed class PreparedMeasureAsk
        {
            public bool Found;
            public string Ref;

            /// <summary>The revision the snapshot below was taken at, read in the same protected pass.</summary>
            public long Revision;

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>How many of the model's folders the folder question left out (its cap of thirty).</summary>
            public int FoldersNotOffered;
        }
    }
}
