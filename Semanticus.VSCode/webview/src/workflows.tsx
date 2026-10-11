import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onWorkflowLibraryChange, onEntitlementChange } from './bridge';
import { DesignMode, heldDrafts, type AuthorSeed } from './workflowdesign';
import { heldPlaces } from './workflowheld.mjs';
import { createDefinitionLoader } from './workflowload.mjs';
import { liveRuns, type RunMapState } from './workflowruns.mjs';
import { DoerMark } from './plan-row';
import { ComposerDraft } from './composer-draft';
import { draftRowId, type DraftView } from './composer-model';
import * as M from './workflows-model';
import { LockMark, SideDrawer, TeamRulesDrawer, hasTeamRules, requiredWords } from './workflow-rules';
import {
  Banner, type WorkflowDef, type WorkflowEnforcement, type WorkflowInfo, type WorkflowPolicy, type WorkflowProfileInfo,
  type WorkflowProfileResult, type WorkflowRunView,
} from './workflow-ui';

// Every shared piece the other files import from here lives in workflow-ui.tsx now; the names stay reachable.
export * from './workflow-ui';
export { CHECKS_OFF_LINE } from './workflows-model';
export { ChecksSwitch, TeamRulesDrawer } from './workflow-rules';

// ===================================================================================================
// Workflows, one page (lane W2; Plan-heart contract 21, Kane 24 Sep: "yes to all five").
//
// The page is the list of YOUR workflows, and Author when you open one. That is all. The five-section rail
// (Home, Library, Runs, Governance, Author) went with its four lists of the same workflows: starters live under
// New, team rules in one drawer behind a quiet link, a workflow's own settings in its Settings drawer, and every
// run on the Plan as a row (R3), its receipt in History. Nothing the old page could do is lost: the checklist of
// all 73 things it did, and where each one lives now, is in the lane report and test/workflows-one-page.test.mjs.
//
// Both doors: every control here is one of the workflow ops the agent door has, on the same session. The page
// follows workflow/libraryDidChange, so a save or a rule the assistant makes shows here at once.
// ===================================================================================================

type View = { kind: 'list' } | { kind: 'author'; name: string | null; seed?: AuthorSeed | null } | { kind: 'draft'; draftId: string };

export function WorkflowsView({ navTarget, onNavConsumed, runs, onRunUpdate, markOwnRun, onOpenPlan, onGo }: {
  navTarget?: { kind: 'workflow' | 'section' | 'draft'; value: string; nonce: number } | null;
  onNavConsumed?: (nonce: number) => void;
  runs: RunMapState; onRunUpdate: (r: WorkflowRunView) => void;
  markOwnRun: (runId: string) => void; isOwnRun?: (runId: string) => boolean;
  /** Open the Plan with this row opened ("run/wfr-3", "draft/dr-..."). A run always lands there. */
  onOpenPlan?: (rowId: string | null) => void;
  /** Any other page, for the draft's own links (a step's object, History). */
  onGo?: (tool: string, target?: string) => void;
}) {
  const [library, setLibrary] = useState<WorkflowInfo[] | null>(null);
    const [rawTemplates, setRawTemplates] = useState<{ name: string; title: string; whenToUse?: string | null; slotCount?: number; error?: string | null }[] | null>(null);
  const [tier, setTier] = useState('free');
  const [libErr, setLibErr] = useState<string | null>(null);
  const [policy, setPolicy] = useState<WorkflowPolicy | null>(null);
  const [policyErr, setPolicyErr] = useState<string | null>(null);
  const [, setHeldTick] = useState(0);
  const [enforcement, setEnforcement] = useState<WorkflowEnforcement | null>(null);
  const [profiles, setProfiles] = useState<WorkflowProfileInfo[] | null>(null);
  const [defs, setDefs] = useState<Record<string, WorkflowDef>>({});
  const [lastRuns, setLastRuns] = useState<Map<string, M.LastRun>>(new Map());
  const [view, setView] = useState<View>({ kind: 'list' });
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<M.WfFilter | null>(null);
  const [newOpen, setNewOpen] = useState(false);
  // True while the New panel holds words the person typed (or an open Start blank box): the panel stays on the page whatever the library does (sweep 3, site 3).
  // The assistant saving the first workflow makes the library non-empty, which used to take the panel, and the words, away with no press.
  const [newHeld, setNewHeld] = useState(false);
  const [rulesOpen, setRulesOpen] = useState(false);
  const [using, setUsing] = useState<M.Starter | null>(null);
  const [rowErr, setRowErr] = useState<{ name: string; text: string } | null>(null);
  const [pageErr, setPageErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [def, setDef] = useState<WorkflowDef | null>(null);
  const selectedRef = useRef<string | null>(null);
  selectedRef.current = view.kind === 'author' ? view.name : null;
  const isPro = tier === 'pro';

  // ONE loader for the open workflow's definition (selection change, library notification, a save), so a slow
  // answer for a workflow the person has left can never overwrite what is on screen (workflowload.mjs).
  const defLoader = useRef<ReturnType<typeof createDefinitionLoader<WorkflowDef>> | null>(null);
  if (!defLoader.current) defLoader.current = createDefinitionLoader<WorkflowDef>({
    rpc: (method, ...args) => rpc<WorkflowDef>(method, ...(args as string[])), setDef, currentSelection: () => selectedRef.current,
  });
  const defLoad = defLoader.current;

  // Every read keeps the page honest about what the engine holds: a failed read says so, never "none".
  const refreshPolicy = () => rpc<WorkflowPolicy>('getWorkflowPolicy').then((p) => { setPolicy(p); setPolicyErr(null); })
    .catch((e) => { setPolicy(null); setPolicyErr(String((e as Error).message ?? e)); });
  const refreshLibrary = () => rpc<WorkflowInfo[]>('listWorkflows').then((l) => { setLibrary(l); setLibErr(null); })
    .catch((e) => setLibErr(String((e as Error).message ?? e)));
  const refreshEnforcement = () => rpc<WorkflowEnforcement>('getWorkflowEnforcement').then(setEnforcement).catch(() => undefined);
  const refreshProfiles = () => rpc<WorkflowProfileInfo[]>('listWorkflowProfiles').then(setProfiles).catch(() => setProfiles([]));
  const refreshHistory = () => rpc<{ rows?: M.RunHistoryRow[] }>('getPlanHistory', 'workflow', null, null, 200)
    .then((h) => setLastRuns(M.lastRuns(h?.rows))).catch(() => undefined);

  useEffect(() => {
    void refreshLibrary();
    rpc<typeof rawTemplates>('listWorkflowTemplates').then(setRawTemplates).catch(() => setRawTemplates([]));
    const readTier = () => rpc<{ tier?: string }>('getEntitlement').then((e) => setTier(e?.tier ?? 'free')).catch(() => undefined);
    void readTier();
    const offTier = onEntitlementChange(() => { void readTier(); });
    void refreshPolicy(); void refreshEnforcement(); void refreshProfiles(); void refreshHistory();
    const offLib = onWorkflowLibraryChange((v) => {
      setLibrary(v as WorkflowInfo[]); setLibErr(null); setDefs({});
      void refreshPolicy(); void refreshEnforcement(); void refreshProfiles();
      defLoad.loadAfterNotification();
    });
    return () => { offLib(); offTier(); };
  }, []);   // eslint-disable-line react-hooks/exhaustive-deps

  // The steps behind each row's marks. One read per workflow on the list, cached until the library changes.
  const rows = useMemo(() => M.filterRows(library, query, filter) as WorkflowInfo[], [library, query, filter]);
  useEffect(() => {
    const missing = rows.filter((w) => !w.error && !defs[w.name]).map((w) => w.name);
    if (missing.length === 0) return;
    let alive = true;
    void Promise.all(missing.map((n) => rpc<WorkflowDef>('getWorkflow', n).then((d) => [n, d] as const).catch(() => null))).then((got) => {
      if (!alive) return;
      setDefs((cur) => { const next = { ...cur }; for (const g of got) if (g) next[g[0]] = g[1]; return next; });
    });
    return () => { alive = false; };
  }, [rows, defs]);

  // A route can open a workflow (lineage hands off to one; Edit rules opens its Settings). An old route to
  // Workflows' Runs lands on the Plan, where runs are rows now.
  useEffect(() => {
    if (!navTarget) return;
    if (navTarget.kind === 'workflow' && navTarget.value) openAuthor(navTarget.value);
    else if (navTarget.kind === 'section' && navTarget.value === 'runs') onOpenPlan?.(null);
    else if (navTarget.kind === 'draft' && navTarget.value) { setNewOpen(false); setRulesOpen(false); setView({ kind: 'draft', draftId: navTarget.value }); }
    onNavConsumed?.(navTarget.nonce);
  }, [navTarget?.nonce]);   // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { if (view.kind === 'author') defLoad.loadForSelection(view.name); }, [view]);   // eslint-disable-line react-hooks/exhaustive-deps

  const openAuthor = (name: string | null, seed: AuthorSeed | null = null) => {
    setNewOpen(false); setRulesOpen(false); setUsing(null); setPageErr(null); setDef(null);
    setView({ kind: 'author', name, seed });
  };
  const backToList = () => { setView({ kind: 'list' }); setDef(null); void refreshHistory(); };

  // ---- Run: every run lands on the Plan, as a row (contract 11.3) ----
  const runWorkflow = async (name: string, known?: WorkflowDef | null) => {
    setBusy(`run:${name}`); setRowErr(null);
    try {
      const d = known ?? defs[name] ?? await rpc<WorkflowDef>('getWorkflow', name);
      if (M.runKind(d as unknown as M.WfDef) === 'draft') {
        const draft = await rpc<{ draftId: string }>('composeTask', null, name, null, null, d?.title ?? name);
        onOpenPlan?.(`draft/${draft.draftId}`);
      } else {
        const r = await rpc<WorkflowRunView>('startWorkflow', name, 'human');
        markOwnRun(r.runId); onRunUpdate(r); onOpenPlan?.(`run/${r.runId}`);
      }
    } catch (e) { setRowErr({ name, text: String((e as Error).message ?? e) }); }
    finally { setBusy(null); }
  };

  // ---- Run once (Plan-heart contract 21.5; lane author-gaps, B6): Author's unsaved text, not the saved file. A
  // workflow with a step Semanticus runs is drafted; any other starts as a guided run. Either lands on the Plan as a
  // row, like Run. Nothing is saved: the person still chooses Save.
  const runOnce = async (name: string, text: string, kind: 'draft' | 'guided', title: string) => {
    setBusy(`run:${name}`); setRowErr(null);
    try {
      if (kind === 'draft') {
        const draft = await rpc<{ draftId: string }>('composeTask', null, name, null, null, title, 'human', text);
        onOpenPlan?.(`draft/${draft.draftId}`);
      } else {
        const r = await rpc<WorkflowRunView>('startWorkflow', name, 'human', text);
        markOwnRun(r.runId); onRunUpdate(r); onOpenPlan?.(`run/${r.runId}`);
      }
    } catch (e) { setRowErr({ name, text: String((e as Error).message ?? e) }); }
    finally { setBusy(null); }
  };

  // ---- Use a starter (contract 21.2): copies it into your workflows, or opens the required one ----
  const useStarter = async (s: M.Starter, values?: Record<string, string>) => {
    if (s.required) { openAuthor(s.name); return; }
    if (s.template && !values) { setUsing(s); return; }
    setBusy(`use:${s.name}`); setPageErr(null);
    try {
      const name = s.template ? (M.copyName(s.name, library) ?? null) : M.copyName(s.name, library);
      const made = await rpc<WorkflowInfo[]>('instantiateWorkflowTemplate', s.name, name, values ? JSON.stringify(values) : null, 'human');
      const lib = await rpc<WorkflowInfo[]>('listWorkflows').catch(() => null);
      if (lib) setLibrary(lib);
      setUsing(null);
      openAuthor(made?.[0]?.name ?? name ?? s.name);
    } catch (e) { setPageErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };

  // ---- Describe it in words: AUTHORING (Kane's UAT on 25 Sep) ----
  // It used to draft on the Plan (compose_task), which put a "Needs you" row and a menu of every step there and left
  // the person on the Plan instead of in Author. Describing a workflow is writing one: the engine cuts the words into
  // steps (describeWorkflowSteps, which stores nothing and puts nothing on the Plan), and Author opens a new, unsaved
  // workflow with those steps. Only running a workflow makes Plan rows. Words typed on the Plan still draft there.
  // `stillHolds` asks the box whether it still holds exactly the words that were sent. Author takes the page over, so it opens only then;
  // words typed while the steps were being written stay in the box, and the person presses Write the steps again.
  const describe = async (words: string, stillHolds: () => boolean = () => true) => {
    setBusy('describe'); setPageErr(null);
    try {
      const d = await rpc<M.DescribedWorkflow>('describeWorkflowSteps', words);
      if (!stillHolds()) { setPageErr('You changed the words while the steps were being written, so nothing was opened. Your new words are still in the box.'); return; }
      setNewOpen(false);
      // The engine writes the description as a format 3 file (lane C7 on L5): each step's action, who does it, its
      // questions and its checks. Author opens that file unsaved; without one it seeds the steps as before.
      openAuthor(null, d.document
        ? { title: M.describedTitle(words), document: d.document, note: M.describedNote(d) }
        : { title: M.describedTitle(words), words, steps: d.steps ?? [], note: M.describedNote(d) });
    } catch (e) { setPageErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };

  // ---- team rules, through the same ops the agent door has ----
  // The controls hand over the whole list they drew. The change the person made is the difference from the list on the page,
  // and that difference is applied to the rule as it stands now: the assistant can add or drop a workflow in the same
  // rule (set_workflow_binding), and writing the page's older list back would silently undo it (UAT stale-copy class).
  const setBinding = async (op: string, require: string[], mode: string) => {
    setPageErr(null);
    try {
      const shown = policy?.bindings.find((b) => b.op === op)?.require ?? [];
      const live = (await rpc<WorkflowPolicy>('getWorkflowPolicy')).bindings.find((b) => b.op === op);
      const change = M.bindingChange(shown, live, require, mode);
      setLibrary(await rpc<WorkflowInfo[]>('setWorkflowBinding', op, change.require, change.mode, 'human'));
      await refreshPolicy();
    }
    catch (e) { setPageErr(String((e as Error).message ?? e)); await afterFailedChange(); }
  };
  // A change that errors may still have landed (the setting written, then the answer lost). The page never keeps
  // its own guess: it reads back what the engine now holds, so the switch and the rules show the real state.
  const afterFailedChange = () => Promise.all([refreshEnforcement(), refreshPolicy(), refreshLibrary()]);
  const setEnforced = async (on: boolean) => {
    setPageErr(null);
    try { setEnforcement(await rpc<WorkflowEnforcement>('setWorkflowEnforcement', on ? 'default' : 'off', 'human')); }
    catch (e) { setPageErr(String((e as Error).message ?? e)); await afterFailedChange(); }
  };
  const applyProfile = async (name: string): Promise<string> => {
    try {
      const r = await rpc<WorkflowProfileResult>('activateWorkflowProfile', name, 'human');
      if (r.workflows) setLibrary(r.workflows);
      await refreshPolicy(); await refreshProfiles();
      return r.note || 'Team rules applied.';
    } catch (e) { const t = String((e as Error).message ?? e); setPageErr(t); await afterFailedChange(); throw e; }
  };
  const setEnabled = async (name: string, on: boolean, clearRule = false) => {
    try {
      setLibrary(await rpc<WorkflowInfo[]>('setWorkflowEnabled', name, on, 'human'));
      // Always means always: a saved Only when rule is hidden while a workflow is off and would come back when it is
      // turned on, so it is cleared after the workflow is on, in that order (Sol on slice 10, F-287).
      if (clearRule) { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowActivation', name, '', '', 'human')); await refreshPolicy(); }
    }
    catch (e) { setPageErr(String((e as Error).message ?? e)); await afterFailedChange(); }
  };
  const setActivation = async (name: string, when: string, set: string) => {
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowActivation', name, when, set, 'human')); await refreshPolicy(); }
    catch (e) { setPageErr(String((e as Error).message ?? e)); await afterFailedChange(); }
  };

  const enforced = enforcement?.enforced !== false;
  const titleOf = useCallback((n: string) => library?.find((w) => w.name === n)?.title || n, [library]);
  const shelf = useMemo(() => M.starterShelf(library as M.WfInfo[] | null, rawTemplates), [library, rawTemplates]);
  const running = useMemo(() => new Map(liveRuns(runs).map((r) => [r.workflow, r])), [runs]);

  const teamRulesDrawer = (onOpenWorkflow: (name: string) => void) =>
    <TeamRulesDrawer library={library ?? []} policy={policy} policyError={policyErr} enforced={enforced} profiles={profiles} isPro={isPro}
      error={pageErr} onClose={() => setRulesOpen(false)} onSetEnforcement={(on) => void setEnforced(on)} onApplyProfile={applyProfile}
      onSetBinding={(op, req, mode) => void setBinding(op, req, mode)} onOpenWorkflow={onOpenWorkflow} />;

  if (view.kind === 'draft') {
    return <div className="sem-wf-page sem-wf-author-page" data-testid="wf-page" data-view="draft">
      <DraftInAuthor draftId={view.draftId} library={library ?? []} isPro={isPro} onBack={backToList}
        onOpenPlan={() => onOpenPlan?.(draftRowId(view.draftId))} onGo={(tool, target) => onGo?.(tool, target)}
        onMade={(name) => { rpc<WorkflowInfo[]>('listWorkflows').then(setLibrary).catch(() => undefined); openAuthor(name); }}
        onOpenDocument={(document, title) => openAuthor(null, { title, document })} />
    </div>;
  }

  if (view.kind === 'author') {
    const info = view.name ? library?.find((w) => w.name === view.name) ?? null : null;
    return <div className="sem-wf-page sem-wf-author-page" data-testid="wf-page" data-view="author">
      <DesignMode info={info} def={view.name && def?.name === view.name ? def : null} creating={!view.name} seed={view.seed ?? null}
        library={library ?? []} libraryReady={!!library} policy={policy} isPro={isPro} running={view.name ? running.get(view.name) ?? null : null}
        sessionId={runs.sessionId}
        onBack={backToList}
        onRun={(name, d) => void runWorkflow(name, d)} runError={rowErr && view.name && rowErr.name === view.name ? rowErr.text : null}
        onRunOnce={(name, text, kind, title) => void runOnce(name, text, kind, title)}
        onSaved={(n, note) => { if (!view.name || view.name !== n) setView({ kind: 'author', name: n, seed: note ? { note } : null }); defLoad.reload(n); }}
        onDeleted={backToList}
        onSetEnabled={(on, clearRule) => view.name && void setEnabled(view.name, on, clearRule)}
        onSetActivation={(when, set) => view.name && void setActivation(view.name, when, set)}
        onSetBinding={(op, req, mode) => void setBinding(op, req, mode)}
        onOpenTeamRules={() => setRulesOpen(true)}
        titleOf={titleOf} />
      {/* Team rules shows its own error over this page, so the toast stands aside while it is open (UAT-S02-2-006). */}
      {pageErr && !rulesOpen && <div className="sem-wf-toast" role="alert">{pageErr}</div>}
      {/* Team rules opens over Author, which stays mounted: its unsaved edits live only in its own state, so
          switching to the list to show the drawer threw them away (Sol on slice 10, F-285). */}
      {rulesOpen && teamRulesDrawer((n) => { setRulesOpen(false); if (n !== view.name) openAuthor(n, { settings: true }); })}
    </div>;
  }

  const empty = !!library && M.listRows(library).length === 0 && !query && !filter;
  const rulesExist = hasTeamRules(policy, enforced);
  const held = heldDrafts.list();
  const places = heldPlaces(held);
  // Kept work for a saved workflow this model no longer has (the assistant deleted it) opens as a new workflow with the
  // work: opened by its name it waited for a file that is gone, at "Opening the workflow" for good.
  const gone = (h: (typeof held)[number]) => !h.creating && !!library && !library.some((w) => w.name === h.name);
  const dropHeld = (key: string) => { heldDrafts.drop(key); setHeldTick((n) => n + 1); };
  return <div className="sem-wf-page" data-testid="wf-page" data-view="list">
    <div className="sem-wf-list-page">
      <header className="sem-wf-head">
        <h1 className="sem-page-title">Workflows</h1>
        <span className="sem-wf-note" data-testid="wf-count">{library ? countLine(M.listRows(library).length) : libErr ? 'Your workflows could not be read.' : 'Reading your workflows…'}</span>
        {!empty && <span className="sem-wf-head-r">
          <input className="sem-field sem-field-sm sem-wf-search" value={query} placeholder="Search your workflows" aria-label="Search your workflows"
            data-testid="wf-search" onChange={(e) => setQuery(e.target.value)} />
          {M.visibleFilters(library, filter).length > 0 && <span className="sem-seg" role="group" aria-label="Show only">
            {M.visibleFilters(library, filter).map((f) => <button key={f.id} type="button" className="sem-seg-item" aria-pressed={filter === f.id}
              data-testid={`wf-filter-${f.id}`} onClick={() => setFilter(filter === f.id ? null : f.id)}>{f.label}</button>)}
          </span>}
          <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="wf-new" disabled={!isPro}
            aria-expanded={newOpen} onClick={() => setNewOpen((o) => !o)}>New workflow</button>
        </span>}
      </header>
      {!isPro && <Banner color="var(--sem-muted)">{M.WORKFLOWS_PRO_LINE}</Banner>}
      {libErr && <Banner color="var(--sem-bad)">{libErr}</Banner>}
      {/* Once, not twice: while Team rules is open its drawer carries this error (UAT-S02-2-006). */}
      {pageErr && !rulesOpen && <Banner color="var(--sem-warn)">{pageErr}</Banner>}
      {held.length > 0 && <div className="sem-wf-held" data-testid="wf-held">
        {held.map((h) => <Banner key={h.key} color="var(--sem-warn)">
          <span className="sem-wf-held-line">Unsaved changes to <b>{h.title || h.name}</b>{M.heldPlaceWords(places.get(h.key))} are kept here.
            <button type="button" className="sem-wf-quiet-link" onClick={() => h.creating || gone(h) ? openAuthor(null, { title: h.title, resume: h.key }) : openAuthor(h.name, { resume: h.key })}>Open it</button>
            <button type="button" className="sem-wf-quiet-link" onClick={() => dropHeld(h.key)}>Throw it away</button></span>
        </Banner>)}
      </div>}

      {(empty || newOpen || newHeld) && <NewPanel lead={shelf.lead} more={shelf.more} isPro={isPro} busy={busy}
        onDescribe={(w, stillHolds) => void describe(w, stillHolds)} onUse={(s) => void useStarter(s)} onOpen={(n) => openAuthor(n)}
        onBlank={(title) => openAuthor(null, { title })} onClose={empty ? null : () => { setNewOpen(false); setNewHeld(false); }} onHeld={setNewHeld} />}

      {!empty && library && <div className="sem-wf-list" data-testid="wf-list">
        <div className="sem-wf-lhead" aria-hidden="true"><span>Name</span><span>Steps</span><span>Last run</span><span /><span /></div>
        {rows.length === 0 && <div className="sem-wf-note sem-wf-none">{filter ? `No workflow is ${filter === 'required' ? 'required' : filter === 'off' ? 'turned off' : 'hidden'} now.` : 'No workflow matches that search.'}</div>}
        {rows.map((w) => <WorkflowRow key={w.name} w={w} def={defs[w.name] ?? null} policy={policy} isPro={isPro}
          last={lastRuns.get(w.name)} running={running.get(w.name) ?? null} busy={busy === `run:${w.name}`}
          error={rowErr?.name === w.name ? rowErr.text : null}
          onOpen={() => openAuthor(w.name)} onRun={() => void runWorkflow(w.name)} onOpenPlan={(id) => onOpenPlan?.(id)} />)}
      </div>}

      <footer className="sem-wf-foot">
        <button type="button" className="sem-wf-quiet-link" data-testid="wf-team-rules-link" onClick={() => setRulesOpen(true)}>Team rules</button>
        <DoerLegend />
      </footer>
    </div>

    {rulesOpen && teamRulesDrawer((n) => { setRulesOpen(false); openAuthor(n, { settings: true }); })}
    {using && <UseDrawer starter={using} busy={busy === `use:${using.name}`} error={pageErr}
      onClose={() => setUsing(null)} onUse={(values) => void useStarter(using, values)} />}
    <span hidden data-testid="wf-rules-exist">{rulesExist ? 'yes' : 'no'}</span>
  </div>;
}

const countLine = (n: number) => n === 0 ? 'No workflows in this model yet' : n === 1 ? '1 workflow in this model' : `${n} workflows in this model`;

// ---- one row of the list ---------------------------------------------------------------------------------

export function WorkflowRow({ w, def, policy, isPro, last, running, busy, error, onOpen, onRun, onOpenPlan, now }: {
  w: WorkflowInfo; def: WorkflowDef | null; policy: WorkflowPolicy | null; isPro: boolean;
  last?: M.LastRun; running?: WorkflowRunView | null; busy?: boolean; error?: string | null;
  onOpen: () => void; onRun: () => void; onOpenPlan?: (rowId: string) => void; now?: Date;
}) {
  const state = M.stateWord(w);
  const required = requiredWords(w.name, policy) ?? (w.required ? 'Required by a team rule' : null);
  const line = M.lastRunLine(last, now);
  return <div className="sem-wf-row" data-testid={`wf-row-${w.name}`} data-workflow={w.name} data-state={state ?? 'on'}>
    <button type="button" className="sem-wf-row-name" onClick={onOpen} title={w.description || undefined}>
      <b>{w.title || w.name}</b>
      <span className="sem-wf-note">{w.error ? 'This file has an error. Open it to read why.' : M.subLine(w as M.WfInfo, def as unknown as M.WfDef)}</span>
    </button>
    <StepStrip def={def} count={w.stepCount} />
    <span className="sem-wf-last">
      {running ? <button type="button" className="sem-wf-quiet-link" onClick={() => onOpenPlan?.(`run/${running.runId}`)}>Running now, on the Plan</button>
        : <>{line.ok != null && <i className={line.ok ? 'sem-wf-ok' : 'sem-wf-notok'} aria-hidden="true" />}{line.text}</>}
    </span>
    <span className="sem-wf-marks">
      {required && <span className="sem-wf-req" data-testid="wf-required-mark"><LockMark />{required}</span>}
      {w.replacesRequired && <span className="sem-wf-req">{M.REPLACES_REQUIRED}</span>}
      {state && <span className="sem-wf-state" data-testid="wf-state-word">{state}</span>}
      {w.starter && !required && <span className="sem-wf-note">Starter</span>}
    </span>
    <span className="sem-wf-row-r">
      <button type="button" className="sem-btn sem-btn-sm" data-testid={`wf-run-${w.name}`}
        disabled={!isPro || busy || !!w.error || w.enabled === false} title={w.enabled === false ? 'Turned off. Turn it on in its Settings to run it.' : 'Runs as a row on the Plan'}
        onClick={onRun}>{busy ? 'Starting…' : 'Run'}</button>
    </span>
    {error && <div className="sem-wf-row-err" role="alert">{error}</div>}
  </div>;
}

/** One mark per step in the doer hues (Semanticus square, you circle, your assistant diamond), with a thin line
 *  where the steps after the change begin. Before the steps arrive, the count alone. */
export function StepStrip({ def, count }: { def: WorkflowDef | null; count: number }) {
  if (!def) return <span className="sem-wf-strip sem-wf-note">{count === 1 ? '1 step' : `${count} steps`}</span>;
  const { marks, afterAt } = M.stepMarks(def as unknown as M.WfDef);
  return <span className="sem-wf-strip" aria-label={`${marks.length} steps`} data-testid="wf-strip">
    {marks.map((m, i) => <span key={i} className="sem-wf-strip-i">
      {i === afterAt && <i className="sem-wf-strip-cut" title="After the change" />}
      <DoerMark doer={m} />
    </span>)}
  </span>;
}

export function DoerLegend() {
  return <span className="sem-wf-legend" aria-label="What the marks mean">
    <span><DoerMark doer="semanticus" />Semanticus</span>
    <span><DoerMark doer="you" />You choose</span>
    <span><DoerMark doer="assistant" />Your assistant writes</span>
  </span>;
}

// ---- New: Describe it first, then the starters, then Start blank --------------------------------------------

export function NewPanel({ lead, more, isPro, busy, onDescribe, onUse, onOpen, onBlank, onClose, onHeld }: {
  lead: M.Starter[]; more: M.Starter[]; isPro: boolean; busy: string | null;
  onDescribe: (words: string, stillHolds: () => boolean) => void; onUse: (s: M.Starter) => void; onOpen: (name: string) => void;
  onBlank: (title: string) => void; onClose: (() => void) | null;
  /** Told whether the panel holds typed words, so the page keeps it while it does. */
  onHeld?: (held: boolean) => void;
}) {
  const [words, setWords] = useState('');
  const wordsNow = useRef(''); wordsNow.current = words;   // what the box holds when the engine answers
  const [showMore, setShowMore] = useState(false);
  const [blank, setBlank] = useState<string | null>(null);
  const holds = words.trim() !== '' || blank != null;
  useEffect(() => { onHeld?.(holds); }, [holds]);   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => onHeld?.(false), []);   // eslint-disable-line react-hooks/exhaustive-deps
  return <section className="sem-wf-new" data-testid="wf-new-panel">
    <div className="sem-wf-desc">
      <label className="sem-wf-desc-l" htmlFor="wf-describe">Describe it in words</label>
      {isPro ? <form className="sem-wf-desc-row" onSubmit={(e) => { e.preventDefault(); const sent = words.trim(); if (sent) onDescribe(sent, () => wordsNow.current.trim() === sent); }}>
        <textarea id="wf-describe" className="sem-field sem-wf-desc-area" rows={2} value={words} data-testid="wf-describe"
          placeholder="Every month, refresh Sales, run the saved checks and publish the model." onChange={(e) => setWords(e.target.value)} />
        <button type="submit" className="sem-btn sem-btn-primary" disabled={!words.trim() || busy === 'describe'} data-testid="wf-draft-it">
          {busy === 'describe' ? 'Writing the steps…' : 'Write the steps'}</button>
      </form> : <span className="sem-wf-note">{M.DESCRIBE_PRO_LINE}</span>}
      <span className="sem-wf-note">{M.DESCRIBE_LINE}</span>
    </div>
    <div className="sem-wf-shelf-head">
      <b>Or start from a starter</b>
      <span className="sem-wf-note">You get your own copy.</span>
      {onClose && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet sem-wf-close" onClick={onClose}>Close</button>}
    </div>
    <div className="sem-wf-shelf">
      {lead.map((s) => <StarterCard key={s.name} s={s} isPro={isPro} busy={busy === `use:${s.name}`} onUse={() => onUse(s)} onOpen={() => onOpen(s.name)} />)}
    </div>
    {more.length > 0 && <div className="sem-wf-more">
      <button type="button" className="sem-wf-quiet-link" aria-expanded={showMore} onClick={() => setShowMore((o) => !o)} data-testid="wf-more-starters">
        {showMore ? 'Fewer starters' : `More starters (${more.length})`}</button>
      {showMore && <ul className="sem-wf-more-list">{more.map((s) => <li key={s.name}>
        <button type="button" className="sem-wf-quiet-link" onClick={() => onOpen(s.name)}>{s.title}</button>
        <span className="sem-wf-note">{s.description}</span>
        <button type="button" className="sem-btn sem-btn-sm" disabled={!isPro || busy === `use:${s.name}`} onClick={() => onUse(s)}>{s.required ? 'Open' : 'Use'}</button>
      </li>)}</ul>}
    </div>}
    <div className="sem-wf-new-foot">
      {blank == null
        ? <button type="button" className="sem-wf-quiet-link" disabled={!isPro} data-testid="wf-start-blank" onClick={() => setBlank('')}>Start blank</button>
        : <form className="sem-wf-acts" onSubmit={(e) => { e.preventDefault(); if (blank.trim()) onBlank(blank.trim()); }}>
          <input className="sem-field sem-field-sm" autoFocus value={blank} placeholder="Name it, in words" aria-label="Name the new workflow"
            data-testid="wf-blank-title" onChange={(e) => setBlank(e.target.value)} />
          <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!blank.trim()}>Start</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setBlank(null)}>Cancel</button>
        </form>}
    </div>
  </section>;
}

function StarterCard({ s, isPro, busy, onUse, onOpen }: { s: M.Starter; isPro: boolean; busy: boolean; onUse: () => void; onOpen: () => void }) {
  return <div className="sem-wf-card" data-testid={`wf-starter-${s.name}`}>
    <div className="sem-wf-card-top">
      <button type="button" className="sem-wf-card-title" onClick={onOpen} title="Read its steps">{s.title}</button>
      <button type="button" className="sem-btn sem-btn-sm" disabled={!isPro || busy} data-testid={`wf-use-${s.name}`}
        title={s.required ? 'A team rule requires this one, so it opens as it is and is never copied.' : 'Copy it into your workflows'}
        onClick={onUse}>{busy ? 'Copying…' : s.required ? 'Open' : 'Use'}</button>
    </div>
    <p className="sem-wf-note">{s.description}</p>
    <div className="sem-wf-card-foot">
      {s.template ? <span className="sem-wf-note">Asks a few questions once</span> : <span className="sem-wf-note">{s.stepCount === 1 ? '1 step' : `${s.stepCount} steps`}</span>}
      {s.required && <span className="sem-wf-req"><LockMark />Required here</span>}
    </div>
  </div>;
}

// ---- Use on a starter with setup questions (contract 21.1 row 19): asked once, then Author opens ------------

interface SlotDef { name: string; question: string; type?: string; required?: string; default?: string | null; example?: string | null; hint?: string | null; values?: string[] | null }

function UseDrawer({ starter, busy, error, onClose, onUse }: {
  starter: M.Starter; busy: boolean; error: string | null; onClose: () => void; onUse: (values: Record<string, string>) => void;
}) {
  const [slots, setSlots] = useState<SlotDef[] | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [loadErr, setLoadErr] = useState<string | null>(null);
  useEffect(() => {
    rpc<{ slots?: SlotDef[] }>('getWorkflowTemplate', starter.name).then((t) => setSlots(t?.slots ?? []))
      .catch((e) => setLoadErr(String((e as Error).message ?? e)));
  }, [starter.name]);
  const missing = (slots ?? []).filter((s) => s.required !== 'optional' && !(values[s.name] ?? '').trim());
  return <SideDrawer title={`Use ${starter.title}`} sub="asked once" testId="wf-use-drawer" onClose={onClose}>
    <p className="sem-wf-note">Answer these once. They go into your copy, which is yours to change afterwards.</p>
    {loadErr && <Banner color="var(--sem-bad)">{loadErr}</Banner>}
    {!slots && !loadErr && <span className="sem-wf-note" role="status">Reading its questions…</span>}
    {(slots ?? []).map((s) => <label key={s.name} className="sem-wf-slot">
      <span>{s.question || s.name}{s.required === 'optional' ? <span className="sem-wf-note"> (optional)</span> : null}</span>
      {s.type === 'enum' && (s.values?.length ?? 0) > 0
        ? <select className="sem-field sem-field-sm" value={values[s.name] ?? ''} data-example={s.example ?? ''} onChange={(e) => setValues({ ...values, [s.name]: e.target.value })}>
          <option value="">Choose</option>{s.values!.map((v) => <option key={v} value={v}>{v}</option>)}</select>
        : <input className="sem-field sem-field-sm" value={values[s.name] ?? ''} data-example={s.example ?? ''}
          placeholder={s.example ? `For example ${s.example}` : ''} onChange={(e) => setValues({ ...values, [s.name]: e.target.value })} />}
      {s.hint && <span className="sem-wf-note">{s.hint}</span>}
    </label>)}
    {error && <Banner color="var(--sem-warn)">{error}</Banner>}
    <div className="sem-wf-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="wf-use-confirm" disabled={busy || !slots || missing.length > 0}
        onClick={() => onUse(Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim())))}>{busy ? 'Copying…' : 'Use it'}</button>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onClose}>Cancel</button>
    </div>
  </SideDrawer>;
}

// ---- a draft, handed to Author (Edit in Workflows and Make it reusable on a draft, lane U5's route seed) ------

/** The draft's source names the saved workflow it was drafted from, or says it came from words, the Plan or the
 *  assistant (Composer DraftView.Source). */
export const draftWorkflowOf = (view: { source?: string | null } | null, library: { name: string; error?: string | null }[]): string | null =>
  view?.source && library.some((w) => w.name === view.source && !w.error) ? view.source : null;

/** Author on a composer draft: the same draft body the Plan row draws (composer-draft.tsx, lane U5), under Author's
 *  own back link and head. A draft drafted from a saved workflow is made reusable as a copy of that workflow, marked
 *  with the draft it came from. A draft built from words or steps is written as a format-3 workflow by the engine
 *  (open_in_author, lane author-gaps B8) and opened in Author, unsaved. */
function DraftInAuthor({ draftId, library, isPro, onBack, onOpenPlan, onGo, onMade, onOpenDocument }: {
  draftId: string; library: WorkflowInfo[]; isPro: boolean; onBack: () => void; onOpenPlan: () => void;
  onGo: (tool: string, target?: string) => void; onMade: (name: string) => void;
  onOpenDocument: (document: { name: string; text: string }, title: string) => void;
}) {
  const [view, setView] = useState<DraftView | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { rpc<DraftView>('getDraft', draftId).then(setView).catch((e) => setError(String((e as Error).message ?? e))); }, [draftId]);
  const source = draftWorkflowOf(view, library);
  // A draft from words, or from a list of steps, has no file behind it: the engine writes it as a format-3 workflow
  // (edit_draft open_in_author, lane author-gaps B8) and Author opens that as a new, unsaved workflow to save.
  const openAsWorkflow = async () => {
    if (!view) return;
    setBusy(true); setError(null);
    try {
      const v = await rpc<DraftView & { document?: { name: string; text: string } | null }>('editDraft', draftId, 'open_in_author', null, null, null, null);
      if (!v?.document) { setError('The draft could not be written as a workflow.'); return; }
      onOpenDocument(v.document, view.title);
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  const reusable = async () => {
    if (!source || !view) return;
    setBusy(true); setError(null);
    try {
      const doc = await rpc<{ exactText: string }>('getWorkflowDocument', source);
      const name = M.reusableName(source, library as M.WfInfo[]);
      await rpc('saveWorkflow', name, M.reusableCopy(doc.exactText, name, `${view.title}, from a draft`, draftId), 'human', true);
      onMade(name);
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  return <div className="sem-wf-author sem-wf-editor" data-testid="wf-author-draft" data-wf-view="draft">
    <div className="sem-wf-author-top"><button type="button" className="sem-wf-back" data-testid="wf-back" onClick={onBack}>Workflows</button></div>
    <div className="sem-wf-ahead">
      <h1 className="sem-page-title sem-wf-atitle">{view?.title ?? 'A draft'}</h1>
      <span className="sem-wf-saved sem-wf-saved-dirty">A draft, not saved as a workflow</span>
      <span className="sem-wf-author-acts">
        <button type="button" className="sem-btn sem-btn-sm" data-testid="wf-draft-open-plan" onClick={onOpenPlan}>Open on the Plan</button>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="wf-draft-reusable" disabled={busy || !isPro || !view}
          onClick={() => void (source ? reusable() : openAsWorkflow())}>{busy ? 'Opening…' : 'Make it reusable'}</button>
      </span>
    </div>
    <p className="sem-wf-summary">{source
      ? 'Drafted from one of your workflows. Make it reusable saves a copy of that workflow, marked with this draft.'
      : 'Make it reusable opens these steps as a new workflow of your own, not saved yet. The draft itself runs from here or from its Plan row.'}</p>
    {error && <Banner color="var(--sem-bad)">{error}</Banner>}
    <div className="sem-wf-draft-host"><ComposerDraft draftId={draftId} onGo={onGo} /></div>
  </div>;
}
