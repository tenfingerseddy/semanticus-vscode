// Connections: the Power BI Desktop instance picker and the workspace-then-model picker (slice C, lane C3).
//
// Both replace a guess with a list. "Running desktop model" used to attach the FIRST running copy it found, and a
// published model needed its XMLA address pasted. Now each lists what the engine can see and the person picks.
//
// The pickers only READ (listLocalInstances, listWorkspaces, listWorkspaceModels, probeAuthPrerequisites). Every
// open, test attach and connect is handed back to the host through a callback, because the host owns the rules that
// must not be duplicated: the unsaved-changes check before an open, the close-after-success contract, and the
// remembered record's label. A callback answers with the engine's sentence when it failed, or null when it worked,
// and the sentence is shown on the row that failed.
//
// Each picker is a pure view (rendered state by state in test/connect-pickers.test.mjs) plus a thin stateful shell
// that asks the engine. Neither view speaks engine words: no "XMLA", no "endpoint", no "dataset".
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { OpenBar, type OpenBarMessage } from './openbar';
import type { OpenRun } from './openbar-model';
import { onEngineState, rpc, rpcPressed, runHostCommand } from './bridge';
import {
  COPY, OPTION_STYLE, SIGN_IN_WAYS, restartFits, WORKSPACE_ENV_OPTIONS, engineSentence, instanceDetail, instanceName, isAdvancedSignIn, isEngineAway,
  signInSentence, sortModels, sortWorkspaces, waitForSignIn, workspaceChoice, workspaceName,
  type EngineView, type FabricWorkspace, type Load, type LocalInstance, type WorkspaceChoice, type WorkspaceModel,
} from './connectpickers-model';

export type { WorkspaceChoice } from './connectpickers-model';
/** A host action: resolves to the engine's sentence when it failed, or null when it worked. */
export type PickAction<T> = (value: T) => Promise<string | null>;

const BTN = 'sem-btn';
const BTN_PRIMARY = 'sem-btn sem-btn-primary';
const BTN_SM = 'sem-btn sem-btn-sm';
const BTN_QUIET = 'sem-btn sem-btn-sm sem-btn-quiet';

function Head({ title, onBack, children }: { title: string; onBack: () => void; children?: React.ReactNode }) {
  return (
    // Wraps at a narrow width, so the account line and its button move under the title instead of off the edge.
    <div className="sem-inline flex-wrap">
      <button type="button" className={BTN_QUIET} data-testid="pick-back" onClick={onBack}>{'‹'} Back</button>
      <h2 className="sem-section-title">{title}</h2>
      {children}
    </div>
  );
}

function Failure({ message, testid }: { message: string; testid?: string }) {
  return <p className="note-line note-bad sem-text-body" data-testid={testid} role="alert">{message}</p>;
}

// ================================================================================================================
// Power BI Desktop
// ================================================================================================================
export interface DesktopPickerViewProps {
  state: Load<LocalInstance>;
  address: string;
  addressOpen: boolean;
  /** The row being acted on ('typed' for the address box), so its buttons say so and nothing else moves. */
  busyKey: string | null;
  rowError: { key: string; message: string } | null;
  onBack: () => void;
  onRetry: () => void;
  onUseForTests: (key: string, dataSource: string) => void;
  onOpen: (key: string, dataSource: string) => void;
  onToggleAddress: () => void;
  onAddress: (value: string) => void;
}

export function DesktopPickerView(p: DesktopPickerViewProps) {
  const busy = p.busyKey != null;
  const actions = (key: string, dataSource: string) => (
    <>
      <button type="button" className={BTN_SM} data-testid="pick-desktop-tests" disabled={busy || !dataSource}
        onClick={() => p.onUseForTests(key, dataSource)}>{p.busyKey === key + ':tests' ? 'Connecting...' : 'Use for tests and queries'}</button>
      <button type="button" className={BTN_SM} data-testid="pick-desktop-open" disabled={busy || !dataSource}
        onClick={() => p.onOpen(key, dataSource)}>{p.busyKey === key + ':open' ? 'Opening...' : 'Open'}</button>
    </>
  );
  const rowError = (key: string) => (p.rowError && p.rowError.key === key
    ? <div className="sem-list-row"><Failure message={p.rowError.message} testid="pick-row-error" /></div> : null);

  return (
    <section className="sem-grid" data-testid="pick-desktop">
      <Head title={COPY.desktopTitle} onBack={p.onBack}>
        <button type="button" className={`${BTN_QUIET} sem-push`} data-testid="pick-desktop-retry" disabled={busy || p.state.kind === 'loading'} onClick={p.onRetry}>Look again</button>
      </Head>
      <p className="sem-text-meta">{COPY.desktopIntro}</p>

      {p.state.kind === 'loading' && <p className="sem-text-meta" aria-live="polite">{COPY.desktopLooking}</p>}
      {p.state.kind === 'failed' && <Failure message={p.state.message} testid="pick-desktop-failed" />}
      {p.state.kind === 'ready' && p.state.items.length === 0 && (
        <p className="note-line sem-text-body" data-testid="pick-desktop-none">{COPY.desktopNone}</p>
      )}
      {p.state.kind === 'ready' && p.state.items.length > 0 && (
        <div className="sem-card sem-card-flush" data-testid="pick-desktop-list">
          {p.state.items.map((inst) => {
            const ds = instanceDetail(inst);
            return (
              <div key={ds} className="hub-row">
                <div className="sem-list-row sem-list-row-2" data-testid="pick-desktop-row">
                  <div className="flex min-w-0 flex-1 flex-col">
                    <span className="hub-row-name block max-w-full truncate">{instanceName(inst)}</span>
                    <span className="sem-text-meta block max-w-full truncate">{ds}</span>
                  </div>
                  {actions(ds, ds)}
                </div>
                {rowError(ds)}
              </div>
            );
          })}
        </div>
      )}

      <div className="sem-inline">
        <button type="button" className={BTN_QUIET} data-testid="pick-desktop-address-toggle" aria-expanded={p.addressOpen}
          onClick={p.onToggleAddress}>Type an address instead</button>
      </div>
      {p.addressOpen && (
        <div className="sem-card sem-stack">
          <label className="sem-field-label">{COPY.desktopAddress}
            <input data-testid="pick-desktop-address" className="sem-field" value={p.address} autoComplete="off"
              placeholder="localhost:51000" onChange={(e) => p.onAddress(e.target.value)} />
            <span className="sem-text-meta">{COPY.desktopAddressHint}</span>
          </label>
          <div className="sem-inline"><span className="sem-push" />{actions('typed', p.address.trim())}</div>
          {p.rowError && p.rowError.key === 'typed' && <Failure message={p.rowError.message} testid="pick-row-error" />}
        </div>
      )}
    </section>
  );
}

export function DesktopPicker({ onBack, onUseForTests, onOpen }: {
  onBack: () => void; onUseForTests: PickAction<string>; onOpen: PickAction<string>;
}) {
  const [state, setState] = useState<Load<LocalInstance>>({ kind: 'loading' });
  const [address, setAddress] = useState('');
  const [addressOpen, setAddressOpen] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [rowError, setRowError] = useState<{ key: string; message: string } | null>(null);
  const gen = useRef(0);
  const live = useRef(true);
  useEffect(() => () => { live.current = false; }, []);

  const load = async () => {
    const mine = ++gen.current;
    setState({ kind: 'loading' }); setRowError(null);
    try {
      const list = (await rpc<LocalInstance[]>('listLocalInstances')) ?? [];
      if (mine === gen.current && live.current) setState({ kind: 'ready', items: list });
    } catch (e) { if (mine === gen.current && live.current) setState({ kind: 'failed', message: engineSentence(e) }); }
  };
  useEffect(() => { void load(); }, []);   // eslint-disable-line react-hooks/exhaustive-deps

  const act = async (key: string, dataSource: string, kind: 'tests' | 'open') => {
    if (!dataSource) return;
    setBusyKey(key + ':' + kind); setRowError(null);
    let failure: string | null;
    try { failure = await (kind === 'tests' ? onUseForTests : onOpen)(dataSource); }
    catch (e) { failure = engineSentence(e); }
    // A success usually closes Connections (the host's contract), so this view may already be gone.
    if (!live.current) return;
    setBusyKey(null);
    if (failure) setRowError({ key, message: failure });
  };

  return (
    <DesktopPickerView state={state} address={address} addressOpen={addressOpen} busyKey={busyKey} rowError={rowError}
      onBack={onBack} onRetry={() => void load()}
      onUseForTests={(key, ds) => void act(key, ds, 'tests')} onOpen={(key, ds) => void act(key, ds, 'open')}
      onToggleAddress={() => { setAddressOpen((v) => !v); setRowError(null); }} onAddress={setAddress} />
  );
}

// ================================================================================================================
// A workspace, then a model
// ================================================================================================================
interface AuthPrereqRequirement { label: string; envNames: string[]; present: boolean }
interface AuthPrerequisites { mode: string; ready: boolean; requirements: AuthPrereqRequirement[]; cliAccount?: string; cliTenant?: string; detail?: string }

/** The setup check for the command-line and service identity sign-ins: what the engine can see before you connect.
 *  Names and presence only, never a secret. A probe that fails falls back to plain guidance, never a dead end. */
export function SignInCheck({ authMode, tenant, failure }: { authMode: string; tenant: string; failure?: string | null }) {
  const [probe, setProbe] = useState<AuthPrerequisites | null>(null);
  const [checking, setChecking] = useState(true);
  useEffect(() => {
    let cancelled = false;
    setChecking(true);
    const t = setTimeout(async () => {
      try { const p = await rpc<AuthPrerequisites>('probeAuthPrerequisites', authMode, tenant.trim() || null); if (!cancelled) setProbe(p); }
      catch { if (!cancelled) setProbe(null); }
      finally { if (!cancelled) setChecking(false); }
    }, 250);
    return () => { cancelled = true; clearTimeout(t); };
  }, [authMode, tenant]);

  const sp = authMode === 'serviceprincipal';
  const failed = !!failure && /could not sign in|sign-in did not work|authentication failed|AADSTS/i.test(failure);
  return (
    <div className="sem-inset sem-stack sem-text-meta" data-testid="pick-signin-check">
      <div className="sem-inline">
        <span style={{ color: 'var(--sem-fg)' }}>{sp ? 'Service identity setup' : 'Azure command line'}</span>
        {checking ? <span className="sem-push">Checking...</span>
          : probe && <span className={`sem-chip sem-push ${probe.ready && !failed ? 'sem-chip-ok' : 'sem-chip-warn'}`}>{failed ? 'Sign-in failed' : probe.ready ? 'Ready' : 'Not ready'}</span>}
      </div>
      {sp ? (
        probe && probe.requirements.length > 0
          ? probe.requirements.map((r) => (
            <span key={r.label} className={`note-line ${r.present ? 'note-ok' : 'note-warn'}`}>
              {r.label}: {r.present ? 'found' : 'not set'} ({r.label === 'Tenant' && tenant.trim() ? 'from the tenant box' : r.envNames.join(' or ')})
            </span>))
          : <span>Set the client id, secret and tenant as environment variables (AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID) before you connect. Semanticus never stores the secret.</span>
      ) : (
        probe?.cliAccount
          ? <span style={{ color: 'var(--sem-fg)' }}>Signed in to Azure CLI as {probe.cliAccount}{probe.cliTenant ? `, ${probe.cliTenant}` : ''}.</span>
          : <span className="note-line note-warn">{probe?.detail || 'Not signed in to the Azure CLI. Run az login in a terminal, then look again.'}</span>
      )}
    </div>
  );
}

export interface WorkspacePickerViewProps {
  waitingToList?: boolean;
  account?: string | null;
  /** A new sign-in for another account is under way: the header asks for the account, not "as" the saved one. */
  askingAccount?: boolean;
  /** "Use another account" beside the account the list is read as. Absent for a sign-in way that has no accounts. */
  onAnotherAccount?: () => void;
  workspaces: Load<FabricWorkspace>;
  /** Where the engine is. Absent means fine. A starting engine is a wait; a failed one says why. */
  engine?: EngineView;
  wsId: string | null;
  models: Load<WorkspaceModel> | null;
  modelId: string | null;
  env: string;
  authMode: string;
  tenant: string;
  otherOpen: boolean;
  busy: boolean;
  connectError: string | null;
  onBack: () => void;
  onRetry: () => void;
  onPickWorkspace: (id: string) => void;
  onPickModel: (id: string) => void;
  onEnv: (value: string) => void;
  onAuthMode: (value: string) => void;
  onTenant: (value: string) => void;
  onToggleOther: () => void;
  onPasteAddress: () => void;
  onConnect: () => void;
  /** A browser sign-in the person must finish in a window of its own: waiting (Cancel shows) or ended without a result. */
  signIn?: 'waiting' | 'unfinished' | null;
  onCancelSignIn?: () => void;
  onOpenOther?: () => void;
  // ---- the bar (cx2, option A): Connect, the one account place with Change, the step and Cancel while it opens ----------------------
  /** The frame's bottom row: the bar is drawn there so it never scrolls. Absent (a test), it is drawn at the end of the screen. */
  barSlot?: HTMLElement | null;
  run?: OpenRun | null;
  now?: number;
  onCancel?: () => void;
  barMessage?: OpenBarMessage | null;
  /** Drawn above the bar's line: the unsaved-changes question. */
  barPop?: ReactNode;
  /** The signed-in accounts Change offers. Picking one reads its workspaces (tester note 1: the list belongs to one account). */
  profiles?: { id: string; username: string }[];
  accountMenuOpen?: boolean;
  onToggleAccountMenu?: () => void;
  onUseAccount?: (profileId: string) => void;
}

function PickRow({ testid, label, selected, onPick, disabled }: { testid: string; label: string; selected: boolean; onPick: () => void; disabled?: boolean }) {
  return (
    <div className="hub-row" data-selected={selected ? 'true' : undefined}>
      <div className={`sem-list-row${selected ? ' sem-row-selected' : ''}`}>
        <button type="button" data-testid={testid} aria-pressed={selected} disabled={disabled} onClick={onPick}
          className="flex min-w-0 flex-1 items-center text-left">
          <span className="hub-row-name block max-w-full truncate">{label}</span>
        </button>
      </div>
    </div>
  );
}

export function WorkspacePickerView(p: WorkspacePickerViewProps) {
  const ws = p.workspaces.kind === 'ready' ? p.workspaces.items.find((w) => w.id === p.wsId) ?? null : null;
  const advanced = isAdvancedSignIn(p.authMode);
  const canConnect = !!ws && !!p.modelId && p.models?.kind === 'ready' && !p.busy;

  const modelColumn = () => {
    if (!ws || !p.models) return <div className="sem-list-row sem-text-meta">{COPY.pickWorkspace}</div>;
    if (p.models.kind === 'loading') return <div className="sem-list-row sem-text-meta" aria-live="polite">{COPY.modelsLooking}</div>;
    if (p.models.kind === 'failed') return <div className="sem-list-row"><Failure message={p.models.message} testid="pick-models-failed" /></div>;
    if (p.models.items.length === 0) return <div className="sem-list-row sem-text-body" data-testid="pick-models-none">{COPY.noModels(workspaceName(ws))}</div>;
    return p.models.items.map((m) => (
      <PickRow key={m.id} testid="pick-model" label={m.displayName || m.id} selected={m.id === p.modelId} disabled={p.busy} onPick={() => p.onPickModel(m.id)} />
    ));
  };

  // The bar (cx2, option A): the picked workspace and model, "Connect as <account>" with Change, and Connect. The environment question
  // and Connect only make sense over a list; a refused list shows its reason alone, and the bar keeps only who the list is read as.
  const listed = p.workspaces.kind === 'ready' && p.workspaces.items.length > 0;
  const model = ws && p.models?.kind === 'ready' ? p.models.items.find((m) => m.id === p.modelId) ?? null : null;
  const who = [p.account ? `Connect as ${p.account}` : p.askingAccount ? COPY.anotherAccountAsk : 'You may be asked to sign in', p.tenant.trim() ? `tenant ${p.tenant.trim()}` : ''].filter(Boolean).join(', ');
  const accountMenu = p.accountMenuOpen ? (
    <div className="hub-pop hub-bar-menu" role="group" aria-label="Accounts" data-testid="pick-account-menu">
      {(p.profiles ?? []).map((a) => (
        <button key={a.id} type="button" className="hub-menu-item" data-testid="pick-account" onClick={() => p.onUseAccount?.(a.id)}>
          <span>{a.username === p.account ? '\u2713 ' : ''}{a.username}</span><em className="hub-job-why">signed in</em></button>
      ))}
      {p.onAnotherAccount && <button type="button" className="hub-menu-item" data-testid="pick-another-account" disabled={p.busy} onClick={p.onAnotherAccount}><span>{COPY.anotherAccount}</span></button>}
    </div>
  ) : null;
  const bar = OpenBar({
    pickName: model ? (model.displayName || model.id) : null,
    pickWhere: model && ws ? `in ${workspaceName(ws)}` : null,
    emptyText: ws ? `Pick a model in ${workspaceName(ws)}` : listed ? 'Pick a workspace, then a model' : 'No workspace list yet',
    accountText: who,
    onChange: p.onToggleAccountMenu && (p.onAnotherAccount || (p.profiles ?? []).length > 0) ? p.onToggleAccountMenu : undefined,
    changeOpen: !!p.accountMenuOpen,
    action: 'Connect', actTestId: 'pick-connect', hideAct: !listed, canAct: canConnect, onAct: p.onConnect,
    run: p.run ?? null, now: p.now ?? Date.now(), onCancel: p.onCancel ?? (() => undefined), interactive: p.authMode === 'interactive',
    message: p.barMessage ?? (p.connectError ? { kind: 'bad', text: p.connectError } : null),
    extra: listed ? (
      <label className="sem-field-label hub-bar-env">{COPY.envLabel}
        <select data-testid="pick-env" className="sem-field sem-field-sm" value={p.env} onChange={(e) => p.onEnv(e.target.value)}>
          {WORKSPACE_ENV_OPTIONS.map((o) => <option key={o.value} value={o.value} style={OPTION_STYLE}>{o.label}</option>)}
        </select>
      </label>) : null,
    pop: p.barPop ?? accountMenu,
  });

  return (
    <section className="sem-grid" data-testid="pick-workspace">
      <Head title={COPY.workspaceTitle} onBack={p.onBack} />

      {p.waitingToList ? (
        <p className="sem-text-meta">Choose a tenant, or list workspaces for your account.</p>
      ) : p.engine?.state === 'starting' ? (
        <div className="sem-list-row sem-text-meta" aria-live="polite" data-testid="pick-engine-starting">{COPY.engineStarting}</div>
      ) : p.engine?.state === 'failed' ? (
        <div className="sem-inline">
          <Failure message={p.engine.reason || COPY.engineFailedFallback} testid="pick-ws-failed" />
          <button type="button" className={`${BTN_SM} sem-push`} data-testid="pick-ws-retry" onClick={p.onRetry}>Try again</button>
          {restartFits(p.engine.reason) && <button type="button" className={BTN_QUIET} data-testid="pick-engine-restart" onClick={() => runHostCommand('semanticus.restartEngine')}>Restart engine</button>}
          <button type="button" className={BTN_QUIET} data-testid="pick-engine-details" onClick={() => runHostCommand('semanticus.showOutput')}>Show details</button>
        </div>
      ) : p.signIn === 'unfinished' ? (
        <div className="sem-inline" data-testid="pick-signin-unfinished">
          <span className="sem-text-body">{COPY.signInUnfinished}</span>
          <button type="button" className={`${BTN_SM} sem-push`} data-testid="pick-ws-retry" onClick={p.onRetry}>Try again</button>
          <button type="button" className={BTN_QUIET} data-testid="pick-signin-other" onClick={p.onOpenOther ?? p.onToggleOther}>Other ways to sign in</button>
        </div>
      ) : p.workspaces.kind === 'failed' ? (
        <div className="sem-inline">
          <Failure message={p.workspaces.message} testid="pick-ws-failed" />
          <button type="button" className={`${BTN_SM} sem-push`} data-testid="pick-ws-retry" onClick={p.onRetry}>Try again</button>
        </div>
      ) : (
        <>
          <div className="sem-card sem-card-flush sem-span-6" data-testid="pick-ws-list">
            <div className="sem-card-head"><h3 className="sem-card-title">Workspace</h3>{p.workspaces.kind === 'ready' && <span className="sem-text-meta sem-push">{p.workspaces.items.length}</span>}</div>
            <div className="hub-pick-scroll">
            {p.workspaces.kind === 'loading' && (p.signIn === 'waiting'
              ? (<div className="sem-list-row sem-inline sem-text-meta" aria-live="polite" data-testid="pick-signin-waiting">
                <span>{COPY.signInWaiting}</span>
                <button type="button" className={`${BTN_QUIET} sem-push`} data-testid="pick-signin-cancel" onClick={p.onCancelSignIn}>Cancel</button>
              </div>)
              : <div className="sem-list-row sem-text-meta" aria-live="polite">{COPY.workspaceLooking}</div>)}
            {p.workspaces.kind === 'ready' && p.workspaces.items.length === 0 && (
              <div className="sem-list-row sem-text-body" data-testid="pick-ws-none">{COPY.workspaceNone}</div>)}
            {p.workspaces.kind === 'ready' && p.workspaces.items.map((w) => (
              <PickRow key={w.id} testid="pick-ws" label={workspaceName(w)} selected={w.id === p.wsId} disabled={p.busy} onPick={() => p.onPickWorkspace(w.id)} />
            ))}
            </div>
          </div>
          <div className="sem-card sem-card-flush sem-span-6" data-testid="pick-model-list">
            <div className="sem-card-head"><h3 className="sem-card-title">Model</h3>{ws && p.models?.kind === 'ready' && <span className="sem-text-meta sem-push">{p.models.items.length} in {workspaceName(ws)}</span>}</div>
            <div className="hub-pick-scroll">{modelColumn()}</div>
          </div>
        </>
      )}


      {/* The "Sign-in did not finish" row above already offers it: one button with this name on screen, not two (r2 UAT N14). */}
      {p.signIn !== 'unfinished' && (
        <div className="sem-inline">
          <button type="button" className={BTN_QUIET} data-testid="pick-other-ways" aria-expanded={p.otherOpen} onClick={p.onToggleOther}>Other ways to sign in</button>
        </div>
      )}
      {p.otherOpen && (
        <div className="sem-card sem-stack" data-testid="pick-other">
          <div className="sem-inline">
            <label className="sem-field-label">Sign in with
              <select data-testid="pick-authmode" className="sem-field" value={p.authMode} onChange={(e) => p.onAuthMode(e.target.value)}>
                {SIGN_IN_WAYS.map((o) => <option key={o.value} value={o.value} style={OPTION_STYLE}>{o.label}</option>)}
              </select>
            </label>
            {/* Every way, a browser sign-in too: the tenant is how a model in a client's tenant is reached (cx1 blocker 1). */}
            <label className="sem-field-label">{p.authMode === 'serviceprincipal' ? 'Tenant' : 'Tenant (optional)'}
              <input data-testid="pick-tenant" className="sem-field" value={p.tenant} autoComplete="off" placeholder="contoso.com"
                onChange={(e) => p.onTenant(e.target.value)} />
            </label>
            <button type="button" className={`${BTN_SM} sem-push`} data-testid="pick-ws-relist" onClick={p.onRetry}>List workspaces</button>
          </div>
          {advanced && <SignInCheck authMode={p.authMode} tenant={p.tenant} failure={p.workspaces.kind === 'failed' ? p.workspaces.message : null} />}
          <div className="sem-inline">
            <span className="sem-text-meta">A workspace this list cannot show?</span>
            <button type="button" className={BTN_QUIET} data-testid="pick-paste-address" onClick={p.onPasteAddress}>Paste an address instead</button>
          </div>
        </div>
      )}
      {p.barSlot ? createPortal(bar, p.barSlot) : bar}
    </section>
  );
}

// Who the list is read as (si2). 'saved': the saved account, as before. 'another': the next read is a new browser sign-in
// that pins no saved account, so any account in any tenant can be picked. 'chosen': read as the account that sign-in
// saved (its profile id), so picking a workspace and Connect use it too. The saved default does not move.
type AccountPick = { mode: 'saved' | 'another' | 'chosen'; profileId: string | null; account: string | null };
const SAVED_ACCOUNT: AccountPick = { mode: 'saved', profileId: null, account: null };
type StartScreen = { pick: AccountPick; workspaces: Load<FabricWorkspace>; signIn: 'waiting' | 'unfinished' | null; waitingToList: boolean; otherOpen: boolean };

export function WorkspacePicker({ account, anotherAccount = false, pressedFirst = false, initialAuthMode = 'interactive', initialTenant = '', onBack, onPasteAddress, onConnect,
  barSlot, run, now, onCancel, barMessage, barPop, profiles }: {
  account?: string | null; anotherAccount?: boolean; pressedFirst?: boolean; initialAuthMode?: string; initialTenant?: string;
  onBack: () => void; onPasteAddress: () => void; onConnect: PickAction<WorkspaceChoice>;
  barSlot?: HTMLElement | null; run?: OpenRun | null; now?: number; onCancel?: () => void; barMessage?: OpenBarMessage | null; barPop?: ReactNode;
  profiles?: { id: string; username: string }[];
}) {
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  // Another account is a browser sign-in, whatever way the last model was opened.
  const startMode = anotherAccount ? 'interactive' : initialAuthMode;
  const [authMode, setAuthMode] = useState(startMode);
  const [tenant, setTenant] = useState(initialTenant);
  const [waitingToList, setWaitingToList] = useState(!initialTenant.trim());
  const [accountPick, setAccountPickState] = useState<AccountPick>(anotherAccount ? { ...SAVED_ACCOUNT, mode: 'another' } : SAVED_ACCOUNT);
  const accountPickRef = useRef(accountPick);
  const setAccountPick = (v: AccountPick) => { accountPickRef.current = v; setAccountPickState(v); };
  const [workspaces, setWorkspaces] = useState<Load<FabricWorkspace>>({ kind: 'loading' });
  const [wsId, setWsId] = useState<string | null>(null);
  const [models, setModels] = useState<Load<WorkspaceModel> | null>(null);
  const [modelId, setModelId] = useState<string | null>(null);
  const [env, setEnv] = useState('');
  const [otherOpen, setOtherOpen] = useState(!initialTenant.trim());
  const [busy, setBusy] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const [engine, setEngine] = useState<EngineView>({ state: 'ok' });
  const [signIn, setSignIn] = useState<'waiting' | 'unfinished' | null>(null);
  const cancelSignIn = useRef<() => void>(() => {});
  // The screen before "Use another account", in whatever state it was: a list, a refused list, a sign-in that did not finish,
  // or waiting for List workspaces. Cancel on that sign-in goes back to it (f172 fault 1, rc1 paths A and B), because no
  // sign-in runs any more and the line that asked to pick an account in the browser would be wrong.
  const beforeAnother = useRef<StartScreen | null>(null);
  const engineWhy = useRef<string | undefined>(undefined);
  const lastPressed = useRef(true);
  const wsGen = useRef(0);
  const modelGen = useRef(0);
  const live = useRef(true);
  useEffect(() => () => { live.current = false; }, []);
  // A tenant typed in the box is used by every sign-in way (cx1 blocker 1): a browser sign-in with no tenant lands in the
  // account's own tenant, so a model in a client's tenant was out of reach. The box and the header line show it.
  const tenantArg = () => (tenant.trim() || null);

  const loadWorkspaces = async (pressed = true) => {
    setWaitingToList(false);
    const mine = ++wsGen.current;
    ++modelGen.current;
    lastPressed.current = pressed;
    setWorkspaces({ kind: 'loading' }); setWsId(null); setModels(null); setModelId(null); setConnectError(null); setSignIn(null);
    // A browser sign-in lives in a window of its own. When the person closes it nothing answers, so the wait is bounded and can be cancelled.
    const bounded = pressed && authMode === 'interactive';
    const showWaiting = bounded ? setTimeout(() => { if (mine === wsGen.current && live.current) setSignIn('waiting'); }, 1500) : undefined;
    const asked = accountPickRef.current;
    // A chosen account whose list came back empty left no profile id behind, so a retry signs in again.
    const anotherAccount = asked.mode === 'another' || (asked.mode === 'chosen' && !asked.profileId);
    const accountProfileId = asked.mode === 'chosen' ? asked.profileId : null;
    try {
      const call = (pressed ? rpcPressed : rpc)<FabricWorkspace[]>('listWorkspaces', authMode, tenantArg(), 'human', anotherAccount, accountProfileId);
      let list: FabricWorkspace[];
      if (bounded) {
        const wait = waitForSignIn(call);
        cancelSignIn.current = wait.cancel;
        const out = await wait.done;
        if (mine !== wsGen.current || !live.current) return;
        if (out.kind === 'unfinished') { setSignIn('unfinished'); return; }
        list = out.value ?? [];
      } else list = (await call) ?? [];
      if (mine === wsGen.current && live.current) {
        if (asked.mode !== 'saved') {
          const as = list.find((w) => w.accountProfileId);
          setAccountPick({ mode: 'chosen', profileId: as?.accountProfileId ?? accountProfileId, account: as?.account ?? asked.account });
        }
        setEngine({ state: 'ok' }); setSignIn(null); setWorkspaces({ kind: 'ready', items: sortWorkspaces(list) });
      }
    } catch (e) {
      if (mine !== wsGen.current || !live.current) return;
      // The host has already waited for an engine that was starting. Seeing its not-connected answer now means the engine is still
      // coming up (wait, read again by ourselves) or could not start (say why, offer the way on). Never a bare "Engine not connected."
      if (isEngineAway(e)) setEngine(engineWhy.current ? { state: 'failed', reason: engineWhy.current } : { state: 'starting' });
      else setWorkspaces({ kind: 'failed', message: signInSentence(e) });
    } finally { if (showWaiting) clearTimeout(showWaiting); }
  };
  const cancelWorkspaces = () => {
    cancelSignIn.current();
    if (accountPickRef.current.mode !== 'another') return;   // the saved account's own sign-in: "Sign-in did not finish" stays the answer
    ++wsGen.current; ++modelGen.current;   // the ended sign-in's answer is not this screen's any more
    const back = beforeAnother.current;
    beforeAnother.current = null;
    setSignIn(null); setWsId(null); setModels(null); setModelId(null); setConnectError(null);
    if (back) { setAccountPick(back.pick); setWorkspaces(back.workspaces); setSignIn(back.signIn); setWaitingToList(back.waitingToList); setOtherOpen(back.otherOpen); }
    else { setOtherOpen(true); setWaitingToList(true); }   // opened as another account: back to its start, which waits for List workspaces
  };
  // The engine reports itself: up means read again at once; down with a reason means the wait is over and the reason is the answer.
  const engineState = useRef<EngineView['state']>('ok');
  engineState.current = engine.state;
  useEffect(() => onEngineState((connected, reason) => {
    engineWhy.current = connected ? undefined : reason;
    if (connected && engineState.current !== 'ok') void loadWorkspaces(false);   // the engine coming back is not a press: it never opens a sign-in window
    else if (!connected && reason && engineState.current === 'starting') setEngine({ state: 'failed', reason });
  }), []);   // eslint-disable-line react-hooks/exhaustive-deps
  // A start that gives no word is asked again every few seconds, for a minute, then called a failure.
  useEffect(() => {
    if (engine.state !== 'starting') return;
    let tries = 0;
    const t = setInterval(() => { if (++tries > 20) { clearInterval(t); setEngine({ state: 'failed', reason: engineWhy.current }); } else void loadWorkspaces(false); }, 3000);
    return () => clearInterval(t);
  }, [engine.state]);   // eslint-disable-line react-hooks/exhaustive-deps
  // The list is read as the chosen sign-in. A change of sign-in way reads it again; a typed tenant waits for
  // "List workspaces", so a half-typed domain is never sent.
  // A page opened by a deep link reads with the saved sign-in only. One a person opened with a button (pressedFirst), or choosing another way to sign in, is the person's press.
  const firstRead = useRef(true);
  useEffect(() => { const first = firstRead.current; firstRead.current = false; if (tenant.trim()) void loadWorkspaces(pressedFirst || !first); else { setOtherOpen(true); setWaitingToList(true); } }, [authMode]);   // eslint-disable-line react-hooks/exhaustive-deps

  const pickWorkspace = async (id: string) => {
    const mine = ++modelGen.current;
    setWsId(id); setModelId(null); setModels({ kind: 'loading' }); setConnectError(null);
    const as = workspaces.kind === 'ready' ? workspaces.items.find((w) => w.id === id)?.accountProfileId ?? null : null;
    try {
      const list = sortModels((await rpcPressed<WorkspaceModel[]>('listWorkspaceModels', id, authMode, tenantArg(), 'human', as)) ?? []);
      if (mine !== modelGen.current || !live.current) return;
      setModels({ kind: 'ready', items: list });
      if (list.length === 1) setModelId(list[0].id);   // one model: it is the pick, one click saved
    } catch (e) { if (mine === modelGen.current && live.current) setModels({ kind: 'failed', message: engineSentence(e) }); }
  };

  // Change in the bar, then a signed-in account: the lists are read again as that account, and Connect opens as it (tester note 1).
  const useAccount = (profileId: string) => {
    const chosen = (profiles ?? []).find((a) => a.id === profileId);
    setAccountMenuOpen(false);
    setAccountPick({ mode: 'chosen', profileId, account: chosen?.username ?? null });
    void loadWorkspaces();
  };
  const startAnotherAccount = () => {
    setAccountMenuOpen(false);
    if (accountPick.mode !== 'another') {
      // A saved read still under way is dropped by the new sign-in, so coming back to it means it did not finish (Try again).
      const inFlight = workspaces.kind === 'loading' && !waitingToList && signIn !== 'unfinished';
      beforeAnother.current = { pick: accountPick, workspaces, signIn: inFlight ? 'unfinished' : signIn, waitingToList, otherOpen };
    }
    setAccountPick({ ...SAVED_ACCOUNT, mode: 'another' });
    if (tenant.trim()) void loadWorkspaces(); else { setOtherOpen(true); setWaitingToList(true); }
  };
  // The header asks to pick the account in the browser only while that sign-in runs (rc1 path C): not before List workspaces,
  // not after Cancel, and not over a refused or unfinished read.
  const asking = accountPick.mode === 'another' && !waitingToList && workspaces.kind === 'loading' && signIn !== 'unfinished' && engine.state === 'ok';

  const connect = async () => {
    if (workspaces.kind !== 'ready' || models?.kind !== 'ready') return;
    const w = workspaces.items.find((x) => x.id === wsId);
    const m = models.items.find((x) => x.id === modelId);
    if (!w || !m) return;
    setBusy(true); setConnectError(null);
    let failure: string | null;
    try { failure = await onConnect(workspaceChoice(w, m, env, authMode, tenant, w.accountProfileId ?? null)); }
    catch (e) { failure = engineSentence(e); }
    if (!live.current) return;
    setBusy(false);
    setConnectError(failure);
  };

  return (
    <WorkspacePickerView account={accountPick.mode === 'saved' ? account : accountPick.account} askingAccount={asking}
      onAnotherAccount={authMode === 'interactive' ? startAnotherAccount : undefined}
      waitingToList={waitingToList}
      workspaces={workspaces} engine={engine} wsId={wsId} models={models} modelId={modelId} env={env}
      authMode={authMode} tenant={tenant} otherOpen={otherOpen} busy={busy} connectError={connectError}
      onBack={onBack} onRetry={() => void loadWorkspaces()} onPickWorkspace={(id) => void pickWorkspace(id)} onPickModel={setModelId}
      onEnv={setEnv} onAuthMode={(m) => { setAuthMode(m); if (m !== 'interactive') setAccountPick(SAVED_ACCOUNT); }} onTenant={setTenant}
      onToggleOther={() => setOtherOpen((v) => !v)} onPasteAddress={onPasteAddress} onConnect={() => void connect()}
      signIn={signIn} onCancelSignIn={cancelWorkspaces} onOpenOther={() => setOtherOpen(true)}
      barSlot={barSlot} run={run} now={now} onCancel={onCancel} barMessage={barMessage} barPop={barPop}
      profiles={profiles} accountMenuOpen={accountMenuOpen} onToggleAccountMenu={() => setAccountMenuOpen((v) => !v)} onUseAccount={useAccount} />
  );
}
