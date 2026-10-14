import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, rpcPressed, copyText } from './bridge';
import { usePersistedState } from './hooks';
import { Button } from './workflows';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';
import { useConnection } from './connection';
import { AccountPicker } from './accountpicker';
import { isEditableElementTree, parseElementTree, updateElementSelection, type DataAgentElement } from './dataagent-schema.mjs';
import { mayStartSignIn, uiLabel } from './copy';
import { DraftMark } from './editbase';
import { draftModel, draftStore, heldDraft } from './drafts';

// ===================================================================================================
// Data Agent — the "Validate → Deploy → Consume" payoff tab (docs/data-agent-tab-plan.md §4). Deploy a
// Fabric Data Agent scoped to the model you just made AI-ready: SCOPE (add this model, element tree) →
// TEACH (AI instructions + few-shots) → SHIP (publish + the MCP endpoint to connect your assistant to).
//
// A Data Agent is a definition-based Fabric ITEM. Reads (list/get) are free; the Semanticus verb
// generate_data_agent_config_from_model (Pro) assembles a semantic_model source from the open session.
// EVERY cloud write goes dry-run FIRST (commit=false reports the exact request, sends nothing) and renders
// that RequestSummary before an explicit "Apply (writes to Fabric)" — the commit-token contract made visual.
// The engine holds no credentials and runs NO inference (golden rule #1): it never queries the agent — a
// published agent is an MCP server you connect your OWN assistant to. "your assistant", never "Claude".
// ===================================================================================================

// ---- wire shapes (camelCase, mirror Semanticus.Engine/Alm/DataAgentProtocol.cs + AlmProtocol.FabricWorkspace) ----
interface FabricWorkspace { id: string; displayName: string; description?: string; type?: string; capacityId?: string }
interface DataAgentInfo { id: string; name: string; description?: string; type?: string; published?: boolean | null }
interface DataAgentList { agents: DataAgentInfo[]; observedItemTypes: string[]; note?: string | null; error?: string | null }
interface DataAgentFewShot { id?: string | null; question: string; query: string }
interface DataAgentDataSource {
  folder: string; type: string; displayName: string; artifactId?: string; workspaceId?: string;
  userDescription?: string; dataSourceInstructions?: string; datasourceJson?: string | null; elementsJson?: string | null; fewShots: DataAgentFewShot[];
}
interface DataAgentStage { aiInstructions?: string | null; dataSources: DataAgentDataSource[]; publishDescription?: string | null }
interface DataAgentDetail { info: DataAgentInfo; draft: DataAgentStage; published?: DataAgentStage | null; note?: string | null; error?: string | null }
interface DataAgentConfig { datasourceJson: string; aiInstructions?: string | null; displayName: string; note?: string | null }
interface DataAgentWriteReport { status: string; agentId?: string | null; message?: string; requestSummary?: string }

const AI_INSTRUCTIONS_CAP = 15000;   // mirrors DAC-AI-INSTRUCTIONS-LEN — near the cap we warn, at the cap we block save.
const errMsg = (e: unknown) => String((e as Error).message ?? e);

// Typed words outlive the Teach editors (join 11, the drafts class, drafts.ts): a tab switch, another agent, a restarted engine and a
// reload all keep them. Each editor holds its draft with the value it was typed over, by agent (and source folder); a value equal to
// where it started is no draft, so the store drops it. A draft ends on a save the engine took, Discard, or Use the new one.
const holdDraft = (scope: string, value: unknown, base: unknown, isDraft: boolean) => {
  const m = draftModel();
  if (!m) return;
  if (isDraft) draftStore.set(m, scope, '', { value, base }); else draftStore.clear(m, scope);
};
const endDraft = (scope: string) => { const m = draftModel(); if (m) draftStore.clear(m, scope); };

// ---------------------------------------------------------------------------------------------------
export function DataAgentView() {
  const [workspaces, setWorkspaces] = useState<FabricWorkspace[] | null>(null);
  const [wsErr, setWsErr] = useState<string | null>(null);
  const [workspaceId, setWorkspaceId] = useState<string>('');   // remembered in component state (spec: last choice)
  const [list, setList] = useState<DataAgentList | null>(null);
  const [listErr, setListErr] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<DataAgentDetail | null>(null);
  const [detailErr, setDetailErr] = useState<string | null>(null);
  // A re-read of the agent on screen that fails (after a save, say) keeps the panels and the words in them on the page, with the error above them (sweep 3, site 7).
  // Only a first read of an agent that fails replaces the panels with the error.
  const [rereadErr, setRereadErr] = useState<string | null>(null);
  const detailOf = useRef<string | null>(null);   // the workspace and agent the detail on screen was read for
  const tier = useTier();   // shared entitlement read (pro.tsx) — drives the Pro badges on every write button
  // WHO signs in matters: azcli may be logged into a DIFFERENT tenant than the model's XMLA session (hit
  // live: the tab listed one tenant's workspaces while the model was bound to a different tenant). The user picks
  // the auth mode + tenant here, persisted, and every Fabric call on this tab carries the choice.
  const [authMode, setAuthMode] = usePersistedState<string>('dataagent.authMode', 'azcli');
  const [tenantId, setTenantId] = usePersistedState<string>('dataagent.tenantId', '');
  const tenant = tenantId.trim() || null;
  // Latest-wins guards for the workspace→agent→detail cascade: each loader claims a monotonic token before its await
  // and applies its result only while still newest — so a slower earlier response can't overwrite a newer selection.
  const wsSeq = useRef(0);
  const listSeq = useRef(0);
  const detailSeq = useRef(0);

  // A browser or device-code sign-in opens a window, so the list waits until the person presses Sign in for THIS choice.
  // Choosing the mode in the menu is not asking (UAT-J9-S10d-002). The command line and a service account never open one.
  const [signInAsked, setSignInAsked] = useState('');
  const signInKey = `${authMode}|${tenantId.trim()}`;
  const mayList = mayStartSignIn(authMode, signInAsked, signInKey);
  // Load workspaces; re-list when the identity (auth mode / tenant) changes.
  useEffect(() => {
    setWorkspaces(null); setWorkspaceId('');
    const seq = ++wsSeq.current;
    if (!mayList) { setWsErr(null); return; }
    // mayList says the person asked to sign in
    rpcPressed<FabricWorkspace[]>('listWorkspaces', authMode, tenant)
      .then((w) => { if (seq === wsSeq.current) { setWorkspaces(w ?? []); setWsErr(null); } })
      .catch((e) => { if (seq === wsSeq.current) setWsErr(errMsg(e)); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [authMode, tenantId, mayList]);

  // Auto-select the first workspace once the list lands (a real "no workspaces" empty state stays distinct).
  useEffect(() => {
    if (workspaceId || !workspaces || workspaces.length === 0) return;
    setWorkspaceId(workspaces[0].id);
  }, [workspaces, workspaceId]);

  // List the workspace's data agents whenever the chosen workspace changes.
  const loadAgents = () => {
    const seq = ++listSeq.current;   // bump BEFORE the early-return too, so clearing the workspace invalidates any in-flight list
    if (!workspaceId) { setList(null); setListErr(null); return; }   // no workspace ⇒ no list — drop the stale one, don't keep rendering it
    setList(null); setListErr(null);
    rpcPressed<DataAgentList>('listDataAgents', workspaceId, authMode, tenant)
      .then((l) => { if (seq === listSeq.current) setList(l); })
      .catch((e) => { if (seq === listSeq.current) setListErr(errMsg(e)); });
  };
  useEffect(() => { setSelected(null); setDetail(null); loadAgents();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId]);

  // Auto-select the first agent so the tab opens on a real agent, not a blank pane.
  useEffect(() => {
    if (selected || !list || list.agents.length === 0) return;
    setSelected(list.agents[0].id);
  }, [list, selected]);

  // Load the selected agent's decoded definition (draft + published stages).
  const loadDetail = () => {
    const seq = ++detailSeq.current;   // bump BEFORE the early-return too, so clearing the selection invalidates any in-flight detail
    if (!workspaceId || !selected) { setDetail(null); setDetailErr(null); setRereadErr(null); detailOf.current = null; return; }   // clear the error with the detail; a stale error must not stick to the next selection
    setDetailErr(null); setRereadErr(null);
    const key = `${workspaceId}|${selected}`;
    const shown = () => detailOf.current === key;
    rpc<DataAgentDetail>('getDataAgent', workspaceId, selected, authMode, tenant)
      .then((d) => {
        if (seq !== detailSeq.current) return;
        if (d?.error && shown()) { setRereadErr(d.error); return; }
        setDetail(d); detailOf.current = d?.error ? null : key;
      })
      .catch((e) => {
        if (seq !== detailSeq.current) return;
        if (shown()) { setRereadErr(errMsg(e)); return; }
        setDetail(null); detailOf.current = null; setDetailErr(errMsg(e));
      });
  };
  useEffect(() => { loadDetail();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId, selected]);

  const ws = workspaces?.find((w) => w.id === workspaceId) ?? null;

  return (
    <div className="sem-grid">
      <Header workspaces={workspaces} workspaceId={workspaceId} onWorkspace={setWorkspaceId} onRefresh={loadAgents} wsErr={wsErr}
        authMode={authMode} onAuthMode={setAuthMode} tenantId={tenantId} onTenantId={setTenantId} />
      {!workspaceId ? (
        <NoWorkspace workspaces={workspaces} wsErr={wsErr} waitingForSignIn={!mayList} onSignIn={() => setSignInAsked(signInKey)} />
      ) : (
        <>
          <AgentRail
            list={list} listErr={listErr} selected={selected} workspaceId={workspaceId}
            authMode={authMode} tenantId={tenant} tier={tier}
            onSelect={setSelected} onListChanged={loadAgents}
            onCreated={() => { loadAgents(); }}
          />
          <main className="sem-span-9 sem-stack-gap">
            {list && list.agents.length === 0 ? (
              <EmptyWorkspace list={list} />
            ) : !selected ? (
              <SelectPrompt />
            ) : detailErr ? (
              <AgentFetchError agentId={selected} error={detailErr} onRetry={loadDetail} />
            ) : !detail ? (
              <Panel><Muted>Loading agent…</Muted></Panel>
            ) : detail.error ? (
              <AgentFetchError agentId={selected} error={detail.error} onRetry={loadDetail} />
            ) : (
              <>
                {rereadErr && <Panel><Banner color="var(--sem-warn)">The agent could not be read again, so this page shows it as it was. Your typed words and ticks are kept. {rereadErr}</Banner>
                  <div className="mt-2"><Button onClick={loadDetail}>Read it again</Button></div></Panel>}
                <AgentHeader detail={detail} ws={ws} />
                <ScopePanel workspaceId={workspaceId} agentId={selected} detail={detail} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
                <TeachPanel workspaceId={workspaceId} agentId={selected} detail={detail} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
                <ShipPanel workspaceId={workspaceId} agentId={selected} detail={detail} ws={ws} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
              </>
            )}
          </main>
        </>
      )}
    </div>
  );
}

// ---- header: workspace picker + refresh --------------------------------------------------------
function Header({ workspaces, workspaceId, onWorkspace, onRefresh, wsErr, authMode, onAuthMode, tenantId, onTenantId }: {
  workspaces: FabricWorkspace[] | null; workspaceId: string; onWorkspace: (id: string) => void; onRefresh: () => void; wsErr: string | null;
  authMode: string; onAuthMode: (m: string) => void; tenantId: string; onTenantId: (t: string) => void;
}) {
  return (
    <div className="flex items-center gap-3 flex-wrap">
      <div className="sem-section-title">Data Agent</div>
      <div className="sem-text-meta">Publish a Fabric Data Agent scoped to this model.</div>
      <div className="ml-auto flex items-center gap-2">
        {/* The same choice Promote makes, so it is the same control. The mode ids and the persisted key
            here are unchanged: only the words a person reads, and where they live, moved. */}
        <AccountPicker mode={authMode} onMode={onAuthMode} tenantId={tenantId} onTenantId={onTenantId} />
        <span className="sem-text-meta">Workspace</span>
        <select value={workspaceId} onChange={(e) => onWorkspace(e.target.value)}
          disabled={!workspaces || workspaces.length === 0}
          className="sem-field" style={{ minWidth: 180 }}>
          {(!workspaces || workspaces.length === 0) && <option value="">{wsErr ? 'unavailable' : 'none'}</option>}
          {workspaces?.map((w) => <option key={w.id} value={w.id}>{w.displayName}</option>)}
        </select>
        <Button onClick={onRefresh} disabled={!workspaceId} title="Re-list the agents in this workspace">Refresh</Button>
      </div>
    </div>
  );
}

// ---- left rail: agents in the workspace --------------------------------------------------------
function AgentRail({ list, listErr, selected, workspaceId, authMode, tenantId, tier, onSelect, onListChanged, onCreated }: {
  list: DataAgentList | null; listErr: string | null; selected: string | null; workspaceId: string;
  authMode: string; tenantId: string | null; tier: string;
  onSelect: (id: string) => void; onListChanged: () => void; onCreated: () => void;
}) {
  return (
    <aside className="sem-card sem-card-flush sem-span-3 self-start">
      <div className="sem-card-head">
        <span className="sem-card-title">Agents</span>
        <span className="sem-text-meta">in this workspace</span>
      </div>
      <div className="sem-stack px-4 py-3" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
        <NewAgentForm workspaceId={workspaceId} authMode={authMode} tenantId={tenantId} tier={tier} onCreated={onCreated} />
        {listErr && <Banner color="var(--sem-bad)">{listErr}</Banner>}
      </div>
      {list == null ? (
        <Muted className="px-4 py-2">Loading agents…</Muted>
      ) : list.agents.length === 0 ? (
        <Muted className="px-4 py-2">No agents yet. Create one above.</Muted>
      ) : (
        list.agents.map((a) => <AgentCard key={a.id} a={a} active={a.id === selected} onClick={() => onSelect(a.id)} />)
      )}
    </aside>
  );
}

function AgentCard({ a, active, onClick }: { a: DataAgentInfo; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} aria-current={active ? 'true' : undefined}
      className="mx-4 text-left pl-2 py-2 transition-colors"
      style={{ width: 'calc(100% - var(--sem-space-8))', borderBottom: '1px solid var(--sem-rule)', boxShadow: active ? 'inset 2px 0 0 var(--sem-fg)' : undefined }}>
      <div className="flex items-center gap-2">
        <div className="text-(length:--sem-text-body) truncate flex-1" style={{ fontWeight: active ? 'var(--sem-weight-strong)' : undefined }}>{a.name}</div>
        {a.published === true
          ? <Pill tint="var(--sem-good)" title="A published (read-only) copy exists">published</Pill>
          : a.published === false
            ? <Pill title="Draft only, never published">draft</Pill>
            : null}
      </div>
      {a.description && <div className="sem-text-meta line-clamp-2">{a.description}</div>}
      <div className="sem-text-meta">{uiLabel(a.type, 'Data agent')}</div>
    </button>
  );
}

function NewAgentForm({ workspaceId, authMode, tenantId, tier, onCreated }: { workspaceId: string; authMode: string; tenantId: string | null; tier: string; onCreated: () => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  if (!open) {
    return (
      <button onClick={() => setOpen(true)} className="sem-btn self-start">
        + New agent<ProBadge show={tier === 'free'} />
      </button>
    );
  }
  return (
    <div className="sem-stack">
      <div className="sem-text-meta">New data agent</div>
      <input value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder="Agent name…" spellCheck={false} className="sem-field" />
      <WriteFlow
        label="Create…" applyLabel="Apply (writes to Fabric)" primary disabled={!name.trim()} tier={tier}
        run={(commit) => rpc<DataAgentWriteReport>('createDataAgent', workspaceId, name.trim(), null, commit, authMode, tenantId)}
        onCommitted={() => { setName(''); setOpen(false); onCreated(); }}
      />
      <button onClick={() => { setOpen(false); setName(''); }} className="sem-btn sem-btn-quiet sem-btn-sm self-start">Cancel</button>
    </div>
  );
}

// ---- selected-agent header ---------------------------------------------------------------------
function AgentHeader({ detail, ws }: { detail: DataAgentDetail; ws: FabricWorkspace | null }) {
  const info = detail.info;
  return (
    <Panel>
      <div className="flex items-start gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <div className="sem-card-title">{info.name}</div>
            <Pill>{uiLabel(info.type, 'Data agent')}</Pill>
            {detail.published
              ? <Pill tint="var(--sem-good)">published</Pill>
              : <Pill>draft only</Pill>}
          </div>
          {info.description && <div className="sem-text-body mt-1">{info.description}</div>}
          <div className="sem-text-meta sem-num mt-1">
            {info.id}{ws ? ` · ${ws.displayName}` : ''} · {detail.draft.dataSources.length} data source{detail.draft.dataSources.length === 1 ? '' : 's'}
          </div>
        </div>
      </div>
      {detail.note && <div className="mt-2"><Banner color="var(--sem-warn)">{detail.note}</Banner></div>}
    </Panel>
  );
}

// ---- panel 1: SCOPE (data sources) -------------------------------------------------------------
function ScopePanel({ workspaceId, agentId, detail, tier, authMode, tenantId, onChanged }: {
  workspaceId: string; agentId: string; detail: DataAgentDetail; tier: string; authMode: string; tenantId: string | null; onChanged: () => void;
}) {
  const { context, openConnections } = useConnection();
  const [config, setConfig] = useState<DataAgentConfig | null>(null);
  const [genErr, setGenErr] = useState<string | null>(null);
  const [genUpsell, setGenUpsell] = useState<string | null>(null);
  const [genBusy, setGenBusy] = useState(false);
  // The config is built for the publish destination of the moment. When another door changes the destination, a config with no ticks goes (build it again); a config with
  // ticks the person made stays, and the panel says the destination changed and asks, with nothing applied until they choose (sweep 3, site 7).
  const dest = context?.publishing?.connectionId;
  const [builtFor, setBuiltFor] = useState<string | undefined>(undefined);
  const [generatedJson, setGeneratedJson] = useState<string | null>(null);
  const [keptFor, setKeptFor] = useState<string | undefined | null>(null);   // the destination the person said Keep my ticks for
  const ticked = !!config && generatedJson != null && config.datasourceJson !== generatedJson;
  useEffect(() => { if (!ticked) setConfig(null); setGenErr(null); }, [dest]);   // eslint-disable-line react-hooks/exhaustive-deps
  const destMoved = !!config && builtFor !== dest && keptFor !== dest;

  const generate = async () => {
    setGenBusy(true); setGenErr(null); setGenUpsell(null);
    const forDest = dest;
    // Pro-gated, never pre-disabled: a free click gets the plain invitation; a real failure stays a real error.
    try { const c = await rpc<DataAgentConfig>('generateDataAgentConfig'); setConfig(c); setGeneratedJson(c?.datasourceJson ?? null); setBuiltFor(forDest); setKeptFor(null); }
    catch (e) {
      if (isEntitlementError(e)) setGenUpsell('Configure and publish data agents with Pro. Browsing agents stays free.');
      else setGenErr(errMsg(e));
    }
    finally { setGenBusy(false); }
  };

  const elements = useMemo(() => (config ? parseElementTree(config.datasourceJson) : []), [config]);
  const folder = config ? `semantic_model-${config.displayName}` : '';
  const changeGeneratedSelection = (path: number[], selected: boolean) => setConfig((current) => current
    ? { ...current, datasourceJson: updateElementSelection(current.datasourceJson, path, selected) }
    : current);

  return (
    <Panel>
      <SectionTitle>Scope <span style={{ color: 'var(--sem-muted)' }}>· data sources</span></SectionTitle>
      <div className="sem-text-meta mt-2">
        Build the source from editable <b style={{ color: 'var(--sem-fg)' }}>{context?.editing?.modelName || 'open model'}</b> metadata,
        then bind it to <b style={{ color: 'var(--sem-fg)' }}>{context?.publishing?.modelName || context?.publishing?.database || 'a publish destination you choose'}</b> for live use.
        Local files cannot serve agent queries themselves.
        <button type="button" onClick={openConnections} className="sem-btn sem-btn-quiet sem-btn-sm">Change publish destination</button>
      </div>

      {!config && (
        <div className="mt-3 flex items-center gap-2">
          <Button disabled={genBusy} onClick={generate}
            title={tier === 'pro' ? 'Assemble a semantic model source from the open model' : 'Configure and publish data agents with Pro. Browsing agents stays free.'}>
            {genBusy ? 'Building…' : '+ Add this model'}
            <ProBadge show={tier === 'free'} variant="accent" />
          </Button>
          <span className="sem-text-meta">Builds the config for review; writes nothing.</span>
        </div>
      )}
      {genUpsell && <div className="mt-3"><UpsellNotice onDismiss={() => setGenUpsell(null)}>{genUpsell}</UpsellNotice></div>}
      {genErr && <div className="mt-3"><Banner color="var(--sem-bad)">{genErr}</Banner></div>}

      {config && (
        <div className="mt-3 flex flex-col gap-3">
          {config.note && <Banner color="var(--sem-warn)">{config.note}</Banner>}
          {destMoved && <div role="alert" data-testid="config-dest-moved" className="sem-inset text-(length:--sem-text-meta)" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
            <div>The publish destination changed after this config was built. Your ticks are kept, and nothing is applied until you choose.</div>
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <button type="button" className="sem-btn sem-btn-sm" onClick={() => setKeptFor(dest)}>Keep my ticks</button>
              <button type="button" className="sem-btn sem-btn-sm" disabled={genBusy} onClick={() => void generate()}>Build it again (drops your ticks)</button>
            </div>
          </div>}
          {config.aiInstructions && (
            <div>
              <div className="sem-label mb-1">AI instructions (seeded)</div>
              <div className="sem-inset text-(length:--sem-text-meta) whitespace-pre-wrap">{config.aiInstructions}</div>
            </div>
          )}
          <div>
            <div className="flex items-center gap-2 mb-1">
              <div className="sem-label">Elements · {config.displayName}</div>
              <span className="sem-label">Click a row to change it · ✓ included · ✕ excluded</span>
            </div>
            <div className="sem-inset">
              {elements.length
                ? <ElementTree elements={elements} onSelectionChange={changeGeneratedSelection} />
                : <Muted>No elements in the generated config.</Muted>}
            </div>
          </div>
          <div className="sem-stack pt-3" style={{ borderTop: '1px solid var(--sem-rule)' }}>
            <div className="sem-text-meta">
              Apply this source into the draft under folder <span className="tnum">{folder}</span>.
            </div>
            {!destMoved && <WriteFlow
              key={config.datasourceJson}
              label="Apply to draft…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
              run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, folder, config.datasourceJson, null, commit, authMode, tenantId)}
              onCommitted={() => { setConfig(null); onChanged(); }}
            />}
          </div>
        </div>
      )}

      {detail.draft.dataSources.length > 0 && (
        <div className="mt-4">
          <div className="sem-label mb-2">Existing sources ({detail.draft.dataSources.length})</div>
          <div className="flex flex-col gap-2">
            {detail.draft.dataSources.map((ds) => <SourceCard key={ds.folder} ds={ds}
              workspaceId={workspaceId} agentId={agentId} tier={tier} authMode={authMode} tenantId={tenantId} onChanged={onChanged} />)}
          </div>
        </div>
      )}
    </Panel>
  );
}

function SourceCard({ ds, workspaceId, agentId, tier, authMode, tenantId, onChanged }: {
  ds: DataAgentDataSource; workspaceId: string; agentId: string; tier: string; authMode: string;
  tenantId: string | null; onChanged: () => void;
}) {
  const [open, setOpen] = useState(false);
  const sourceJson = ds.datasourceJson ?? '';
  const [schemaJson, setSchemaJson] = useState(sourceJson);
  const [savedJson, setSavedJson] = useState(sourceJson);
  // Ticks not saved yet are the person's words (join 11 solui round 2, U2 d): a stored schema that changes under them never replaces them. savedJson stays the document the ticks
  // started from, the save names it and the engine compares it, and the card says the stored schema moved and asks (Keep my ticks / Use the stored schema).
  const folder = useRef(ds.folder);
  useEffect(() => {
    if (folder.current !== ds.folder) { folder.current = ds.folder; setSchemaJson(sourceJson); setSavedJson(sourceJson); return; }
    if (schemaJson === savedJson || schemaJson === sourceJson) { setSchemaJson(sourceJson); setSavedJson(sourceJson); }
  }, [ds.folder, sourceJson]);
  const schemaElements = useMemo(() => parseElementTree(schemaJson), [schemaJson]);
  const fallbackElements = useMemo(() => parseElementTree(ds.elementsJson), [ds.elementsJson]);
  const semanticModel = ds.type === 'semantic_model' || ds.folder.startsWith('semantic_model-');
  const editableDocument = isEditableElementTree(schemaJson);
  const elements = editableDocument ? schemaElements : fallbackElements;
  const editable = semanticModel && editableDocument;
  const dirty = editable && schemaJson !== savedJson;
  const storedMoved = dirty && sourceJson !== savedJson;
  const changeSelection = (path: number[], selected: boolean) => setSchemaJson((current) => updateElementSelection(current, path, selected));
  return (
    <div className="sem-inset">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-(length:--sem-text-body) font-semibold">{ds.displayName || ds.folder}</span>
        <Pill>{uiLabel(ds.type)}</Pill>
        <span className="sem-text-meta sem-num ml-auto">{ds.folder}</span>
      </div>
      {ds.userDescription && <div className="sem-text-meta mt-1">{ds.userDescription}</div>}
      {elements.length > 0 && (
        <>
          <button onClick={() => setOpen((o) => !o)} aria-label={`${open ? 'Hide' : 'Show'} elements for ${ds.displayName || ds.folder}`}
            className="mt-2 flex items-center gap-1 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)' }}>
            <span className="inline-block transition-transform text-(length:--sem-text-label)" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
            {open ? 'Hide elements' : `Elements`}
          </button>
          {open && (
            <div className="mt-2 pt-2" style={{ borderTop: '1px solid var(--sem-rule)' }}>
              {editable && <Muted className="mb-1">Click a row to include or exclude it.</Muted>}
              <ElementTree elements={elements} onSelectionChange={editable ? changeSelection : undefined} />
              {semanticModel && !editable && (
                <div className="mt-2"><Banner color="var(--sem-warn)">
                  This schema cannot be edited because its source document is missing or malformed. Refresh the agent after correcting it in Fabric.
                </Banner></div>
              )}
              {!semanticModel && (
                <div className="mt-2 sem-text-meta">
                  Schema editing here is available for semantic models. Edit this source in Fabric.
                </div>
              )}
              {editable && (
                <div className="mt-2 pt-2" style={{ borderTop: '1px solid var(--sem-rule)' }}>
                  {storedMoved ? (
                    <div role="alert" data-testid="schema-moved" className="sem-stack">
                      <Banner color="var(--sem-warn)">The stored schema for this source changed while you were choosing fields. Your choices are kept and not saved.</Banner>
                      <div className="flex items-center gap-2">
                        <button type="button" className="sem-btn sem-btn-sm" onClick={() => setSavedJson(sourceJson)} title="Keep your choices. Saving then replaces the stored schema shown now.">Keep my choices</button>
                        <button type="button" className="sem-btn sem-btn-sm" onClick={() => { setSchemaJson(sourceJson); setSavedJson(sourceJson); }}>Use the stored schema</button>
                      </div>
                    </div>
                  ) : dirty ? (
                    <WriteFlow
                      key={schemaJson}
                      label="Save schema…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
                      run={(commit) => /* names the document the ticks started from; the engine compares it inside its write */ rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, ds.folder, schemaJson, null, commit, authMode, tenantId, null, 'schema', savedJson)}
                      onCommitted={() => { setSavedJson(schemaJson); onChanged(); }}
                    />
                  ) : <Muted>Choose which fields the agent can use.</Muted>}
                </div>
              )}
            </div>
          )}
        </>
      )}
      {elements.length === 0 && semanticModel && (
        <div className="mt-2">
          {editable ? <Muted>This source has no fields in its agent schema.</Muted> : <Banner color="var(--sem-warn)">This schema cannot be edited because its source document is missing or malformed. Refresh the agent after correcting it in Fabric.</Banner>}
        </div>
      )}
    </div>
  );
}

function ElementTree({ elements, depth = 0, path = [], onSelectionChange }: {
  elements: DataAgentElement[]; depth?: number; path?: number[];
  onSelectionChange?: (path: number[], selected: boolean) => void;
}) {
  return (
    <div className="flex flex-col">
      {elements.map((el, i) => <ElementRow key={(el.display_name ?? '') + i} el={el} depth={depth}
        path={[...path, i]} onSelectionChange={onSelectionChange} />)}
    </div>
  );
}
function ElementRow({ el, depth, path, onSelectionChange }: {
  el: DataAgentElement; depth: number; path: number[];
  onSelectionChange?: (path: number[], selected: boolean) => void;
}) {
  const selected = el.is_selected !== false;   // absent = included (§1)
  const kind = (el.type ?? '').split('.').pop() || '';
  const name = el.display_name || 'Unnamed element';
  const row = (
    <>
      <span className="text-(length:--sem-text-meta) w-3 shrink-0" style={{ color: 'var(--sem-fg)' }}>{selected ? '✓' : '✕'}</span>
      <span className="text-(length:--sem-text-meta) truncate" style={{ color: selected ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{name}</span>
      {kind && <span className="sem-text-meta">{kind}</span>}
      {!selected && <Pill tint="var(--sem-bad)">excluded</Pill>}
      {el.description && <span className="sem-text-meta truncate">· {el.description}</span>}
    </>
  );
  return (
    <>
      {onSelectionChange ? (
        <button type="button" aria-label={`${selected ? 'Exclude' : 'Include'} ${name}`}
          onClick={() => onSelectionChange(path, !selected)}
          className="w-full flex items-center gap-2 py-1 text-left rounded-sm hover:bg-[var(--sem-hover)]"
          style={{ paddingLeft: `calc(var(--sem-space-4) * ${depth})` }}>
          {row}
        </button>
      ) : (
        <div className="flex items-center gap-2 py-1" style={{ paddingLeft: `calc(var(--sem-space-4) * ${depth})` }}>{row}</div>
      )}
      {el.children && el.children.length > 0 && <ElementTree elements={el.children} depth={depth + 1}
        path={path} onSelectionChange={onSelectionChange} />}
    </>
  );
}

// ---- panel 2: TEACH (instructions + few-shots) -------------------------------------------------
function TeachPanel({ workspaceId, agentId, detail, tier, authMode, tenantId, onChanged }: {
  workspaceId: string; agentId: string; detail: DataAgentDetail; tier: string; authMode: string; tenantId: string | null; onChanged: () => void;
}) {
  const stored = detail.draft.aiInstructions ?? '';
  // The draft is the agent's: the scope names the agent the detail is for (the selection can run ahead of the detail while it loads).
  const scope = `dataagent|${detail.info.id}|teach`;
  const held = heldDraft<string>(scope);
  const [instr, setInstr] = useState(held?.value ?? stored);
  // The box remembers the instructions it was seeded with. A Save names them, and the engine compares them with the agent's draft inside its own write: the copy
  // this page holds can be behind the assistant (update_data_agent), and the write replaces the instructions whole. A held draft opens over the value it was typed over.
  const [seed, setSeed] = useState(held?.base ?? stored);
  const [theirs, setTheirs] = useState<string | null>(null);   // the newer stored instructions, while the person must choose
  const agentRef = useRef(detail.info.id);
  const live = useRef({ instr, seed }); live.current = { instr, seed };
  // A fresh detail loads (another agent, a committed save re-fetched, or a refused save re-read). Follow it when nothing is typed; keep the words and ask when something is.
  // Another agent opens on its own held draft, if it has one, and is then compared the same way.
  useEffect(() => {
    let { instr: typed, seed: from } = live.current;
    if (agentRef.current !== detail.info.id) {
      agentRef.current = detail.info.id;
      const h = heldDraft<string>(scope);
      typed = h?.value ?? stored; from = h?.base ?? stored;
      setInstr(typed); setSeed(from); setTheirs(null);
    }
    if (stored === from) { setTheirs(null); return; }
    if (typed === from) { setInstr(stored); setSeed(stored); setTheirs(null); }
    else if (typed === stored) { setSeed(stored); setTheirs(null); }
    else setTheirs(stored);
  }, [detail.info.id, stored]);
  // Hold what is typed. On the render where the agent changed, the box still shows the last agent's words: they are not this agent's draft.
  const heldScope = useRef(scope);
  useEffect(() => {
    if (heldScope.current !== scope) { heldScope.current = scope; return; }
    holdDraft(scope, instr, seed, instr !== seed);
  }, [scope, instr, seed]);
  const drafted = instr !== seed;
  const discard = () => { setInstr(seed); setTheirs(null); endDraft(scope); };
  // The write names the seed. A refusal says the instructions moved after the box was seeded: read the agent again, so the choice appears with the newer text.
  const saveInstructions = async (commit: boolean) => {
    const failed = (message: string) => { if (/changed in the model after you opened it/.test(message)) onChanged(); };
    let r: DataAgentWriteReport;
    try { r = await rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, instr, null, null, null, commit, authMode, tenantId, null, 'instructions', seed); }
    catch (e) { failed(errMsg(e)); throw e; }
    if (commit && r?.status === 'error') { failed(r.message ?? ''); throw new Error(r.message ?? 'The instructions were not saved.'); }
    return r;
  };

  const len = instr.length;
  const over = len > AI_INSTRUCTIONS_CAP;
  const near = !over && len > AI_INSTRUCTIONS_CAP * 0.9;
  const counterColor = over || near ? 'var(--sem-fg)' : 'var(--sem-muted)';

  return (
    <Panel>
      <SectionTitle>Teach <span style={{ color: 'var(--sem-muted)' }}>· instructions + examples</span></SectionTitle>

      <div className="mt-2">
        <div className="flex items-center gap-2 mb-1">
          <div className="sem-label">AI instructions</div>
          <DraftMark show={drafted} />
          <span className="ml-auto text-(length:--sem-text-meta) sem-num" style={{ color: counterColor }}>{len.toLocaleString()} / {AI_INSTRUCTIONS_CAP.toLocaleString()}</span>
        </div>
        <textarea value={instr} onChange={(e) => setInstr(e.target.value)} rows={6} spellCheck={false}
          placeholder="How should the agent interpret this model? Preferred measures, glossary, how to qualify results…"
          className="sem-field w-full resize-y"
          style={{ borderColor: over ? 'var(--sem-bad)' : undefined }} />
        {over && <div className="mt-1"><Banner color="var(--sem-bad)">Over the {AI_INSTRUCTIONS_CAP.toLocaleString()}-character cap. Trim before saving.</Banner></div>}
        {theirs !== null && (
          <div className="mt-2" role="alert">
            <Banner color="var(--sem-warn)">These instructions changed in Fabric while you were editing. Your words are still here, and nothing was saved.</Banner>
            <div className="mt-1 flex items-center gap-2">
              <Button onClick={() => { setSeed(theirs); setTheirs(null); }}>Keep mine</Button>
              <Button onClick={() => { setInstr(theirs); setSeed(theirs); setTheirs(null); }}>Use the new one</Button>
            </div>
          </div>
        )}
        <div className="mt-2 flex items-start gap-2">
          <WriteFlow
            label="Save instructions…" applyLabel="Apply (writes to Fabric)" primary disabled={over || theirs !== null} tier={tier}
            run={saveInstructions}
            onCommitted={() => { setSeed(instr); endDraft(scope); onChanged(); }}
          />
          {drafted && theirs === null && <Button onClick={discard} title="Throw away what you typed and show the saved instructions again.">Discard</Button>}
        </div>
      </div>

      <div className="mt-4">
        <div className="sem-label mb-2">Example question/query pairs · per source</div>
        {detail.draft.dataSources.length === 0 ? (
          <Muted>Add a data source in Scope first. Example question/query pairs attach to a source.</Muted>
        ) : (
          <div className="flex flex-col gap-3">
            {detail.draft.dataSources.map((ds) => (
              <FewShotEditor key={ds.folder} workspaceId={workspaceId} agentId={agentId} ds={ds} tier={tier} authMode={authMode} tenantId={tenantId} onChanged={onChanged}
                draftScope={`dataagent|${detail.info.id}|fewshots|${ds.folder}`} />
            ))}
          </div>
        )}
      </div>
    </Panel>
  );
}

// The words a row list holds, as the engine sees them: a row with neither a question nor a query is nothing yet, and an id is the engine's, not the person's.
// Two lists are the same work when this reads the same.
const fewShotWords = (rows: DataAgentFewShot[]) => JSON.stringify(rows.filter((r) => r.question.trim() || r.query.trim()).map((r) => [r.question, r.query]));

function FewShotEditor({ workspaceId, agentId, ds, tier, onChanged, authMode, tenantId, draftScope }: {
  workspaceId: string; agentId: string; ds: DataAgentDataSource; tier: string; authMode: string; tenantId: string | null; onChanged: () => void;
  /** Where this editor's typed rows are held: the agent and the source folder. */
  draftScope?: string;
}) {
  const stored = ds.fewShots ?? [];
  const scope = draftScope ?? `dataagent|${agentId}|fewshots|${ds.folder}`;
  const held = heldDraft<DataAgentFewShot[]>(scope);
  const [rows, setRows] = useState<DataAgentFewShot[]>(held?.value ?? stored);
  // The examples this editor was seeded with. A Save names them and the engine compares them with the agent's examples inside its own write.
  // A held draft opens over the examples it was typed over.
  const [seed, setSeed] = useState<DataAgentFewShot[]>(held?.base ?? stored);
  const [theirs, setTheirs] = useState<DataAgentFewShot[] | null>(null);   // the newer stored examples, while the person must choose
  const live = useRef({ rows, seed }); live.current = { rows, seed };
  const storedKey = JSON.stringify(stored);
  const scopeRef = useRef(scope);
  // A fresh detail loads (a Teach save, a refused save re-read, a Studio refresh). Follow it when no row was touched; keep the typed rows and ask when the stored examples moved under them.
  // Another agent with a source of the same folder keeps this editor on the page: it opens on that agent's own held rows, if any, and is compared the same way.
  useEffect(() => {
    let { rows: typed, seed: from } = live.current;
    if (scopeRef.current !== scope) {
      scopeRef.current = scope;
      const h = heldDraft<DataAgentFewShot[]>(scope);
      typed = h?.value ?? stored; from = h?.base ?? stored;
      setRows(typed); setSeed(from); setTheirs(null);
    }
    if (storedKey === JSON.stringify(from)) { setTheirs(null); return; }
    if (fewShotWords(stored) === fewShotWords(from)) { setSeed(stored); setTheirs(null); if (fewShotWords(typed) === fewShotWords(from)) setRows(stored); }   // only ids moved
    else if (fewShotWords(typed) === fewShotWords(from)) { setRows(stored); setSeed(stored); setTheirs(null); }
    else if (fewShotWords(typed) === fewShotWords(stored)) { setSeed(stored); setTheirs(null); }   // the stored examples are what was typed (this editor's own Save)
    else setTheirs(stored);
  }, [ds.folder, storedKey, scope]);
  // Hold the typed rows. A row with no words is nothing yet, so rows that read the same as where they started hold no draft.
  const drafted = fewShotWords(rows) !== fewShotWords(seed);
  const heldScope = useRef(scope);
  useEffect(() => {
    if (heldScope.current !== scope) { heldScope.current = scope; return; }
    holdDraft(scope, rows, seed, drafted);
  }, [scope, JSON.stringify(rows), JSON.stringify(seed)]);
  const discard = () => { setRows(seed); setTheirs(null); endDraft(scope); };
  const isSemanticModel = ds.type === 'semantic_model';

  const setRow = (i: number, patch: Partial<DataAgentFewShot>) => setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const add = () => setRows((r) => [...r, { question: '', query: '' }]);
  const remove = (i: number) => setRows((r) => r.filter((_, j) => j !== i));
  const fewshotsJson = JSON.stringify({ fewShots: rows.filter((r) => r.question.trim() || r.query.trim()) });
  const seededJson = JSON.stringify({ fewShots: seed });   // the save names the examples the rows were seeded with, never a newer copy; the engine compares them inside its write

  return (
    <div className="sem-inset">
      <div className="flex items-center gap-2">
        <span className="text-(length:--sem-text-body) font-semibold">{ds.displayName || ds.folder}</span>
        <Pill>{uiLabel(ds.type)}</Pill>
        <DraftMark show={drafted} />
      </div>
      {isSemanticModel && (
        <div className="sem-text-meta mt-2">
          Note: the Fabric portal does not support example question and query pairs for semantic-model sources yet, so pairs you save here do not change how the agent answers. The definition format allows them, so they are kept here, ready for when it does.
        </div>
      )}
      {theirs !== null && (
        <div className="mt-2" role="alert">
          <Banner color="var(--sem-warn)">These examples changed in Fabric while you were editing. Your rows are still here, and nothing was saved.</Banner>
          <div className="mt-1 flex items-center gap-2">
            <Button onClick={() => { setSeed(theirs); setTheirs(null); }}>Keep mine</Button>
            <Button onClick={() => { setRows(theirs); setSeed(theirs); setTheirs(null); }}>Use the new one</Button>
          </div>
        </div>
      )}
      <div className="mt-2 flex flex-col gap-2">
        {rows.length === 0 && <Muted>No examples yet.</Muted>}
        {rows.map((r, i) => (
          <div key={i} className="flex items-start gap-2">
            <div className="flex-1 flex flex-col gap-1">
              <input value={r.question} onChange={(e) => setRow(i, { question: e.target.value })} placeholder="Question…" spellCheck={false}
                className="sem-field" />
              <input value={r.query} onChange={(e) => setRow(i, { query: e.target.value })} placeholder="Query (DAX)…" spellCheck={false}
                className="sem-field" style={{ fontFamily: 'var(--sem-font-num)' }} />
            </div>
            <button onClick={() => remove(i)} title="Remove" className="sem-btn sem-btn-quiet">✕</button>
          </div>
        ))}
      </div>
      <div className="mt-2 flex items-center gap-2">
        <Button onClick={add}>+ Add example</Button>
        <WriteFlow
          label="Save examples…" applyLabel="Apply (writes to Fabric)" tier={tier} disabled={theirs !== null}
          run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, ds.folder, null, fewshotsJson, commit, authMode, tenantId, null, 'fewshots', seededJson)}
          onCommitted={() => { setSeed(rows.filter((r) => r.question.trim() || r.query.trim())); endDraft(scope); onChanged(); }}
        />
        {drafted && theirs === null && <Button onClick={discard} title="Throw away the rows you changed and show the saved examples again.">Discard</Button>}
      </div>
    </div>
  );
}

// ---- panel 3: SHIP (stage + publish) -----------------------------------------------------------
function ShipPanel({ workspaceId, agentId, detail, ws, tier, authMode, tenantId, onChanged }: {
  workspaceId: string; agentId: string; detail: DataAgentDetail; ws: FabricWorkspace | null; tier: string; authMode: string; tenantId: string | null; onChanged: () => void;
}) {
  const [desc, setDesc] = useState('');
  const published = detail.published;
  const mcpEndpoint = `https://api.fabric.microsoft.com/v1/mcp/workspaces/${ws?.id ?? workspaceId}/dataagents/${agentId}/agent`;

  return (
    <Panel>
      <SectionTitle>Publish <span style={{ color: 'var(--sem-muted)' }}>· draft, then live</span></SectionTitle>

      <div className="mt-2 sem-inset">
        {published ? (
          <div className="flex items-start gap-2">
            <div className="min-w-0">
              <div className="sem-chip sem-chip-ok" style={{ color: 'var(--sem-fg)' }}>Published</div>
              {published.publishDescription
                ? <div className="text-(length:--sem-text-meta) mt-1" style={{ color: 'var(--sem-fg)' }}>{published.publishDescription}</div>
                : <div className="text-(length:--sem-text-meta) mt-1" style={{ color: 'var(--sem-muted)' }}>(no publish description)</div>}
              <div className="sem-text-meta mt-1">A read-only copy is live; the draft above iterates independently.</div>
            </div>
          </div>
        ) : (
          <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Never published; the draft above is the only stage. Publish creates a read-only copy consumers query.</div>
        )}
      </div>

      {/* Publish */}
      <div className="mt-3 pt-3 sem-stack" style={{ borderTop: '1px solid var(--sem-rule)' }}>
        <div className="text-(length:--sem-text-body) font-semibold">{published ? 'Re-publish' : 'Publish'}</div>
        <input value={desc} onChange={(e) => setDesc(e.target.value)} placeholder="Publish description…" spellCheck={false} className="sem-field w-full" />
        <WriteFlow
          label="Publish…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
          run={(commit) => rpc<DataAgentWriteReport>('publishDataAgent', workspaceId, agentId, desc.trim() || null, commit, authMode, tenantId)}
          onCommitted={() => { setDesc(''); onChanged(); }}
        />
      </div>

      {/* MCP endpoint (after publish) */}
      {published && <McpEndpointCard endpoint={mcpEndpoint} />}

      {/* Delete (two-step: dry-run review, then explicit Apply) */}
      <div className="mt-3 pt-3 sem-stack" style={{ borderTop: '1px solid var(--sem-rule)' }}>
        <div className="text-(length:--sem-text-body) font-semibold">Delete agent</div>
        <div className="sem-text-meta">Removes the Fabric item entirely. Review the request, then apply. This cannot be undone.</div>
        <WriteFlow
          label="Delete agent…" applyLabel="Apply: delete (writes to Fabric)" danger tier={tier}
          run={(commit) => rpc<DataAgentWriteReport>('deleteDataAgent', workspaceId, agentId, commit, authMode, tenantId)}
          onCommitted={onChanged}
        />
      </div>
    </Panel>
  );
}

function McpEndpointCard({ endpoint }: { endpoint: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => { if (await copyText(endpoint)) { setCopied(true); setTimeout(() => setCopied(false), 1500); } };
  return (
    <div className="mt-3 sem-inset">
      <div className="text-(length:--sem-text-body) font-semibold">Connect your assistant</div>
      <div className="text-(length:--sem-text-meta) mt-1" style={{ color: 'var(--sem-fg)' }}>
        A published agent is an MCP server. Semanticus never queries the agent or runs inference. Connect your assistant to this endpoint instead.
      </div>
      <div className="mt-2 flex items-center gap-2">
        <code className="flex-1 min-w-0 truncate text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)', fontFamily: 'var(--sem-font-num)' }}>{endpoint}</code>
        <Button onClick={copy}>{copied ? 'Copied ✓' : 'Copy'}</Button>
      </div>
    </div>
  );
}

// ---- Review → Apply write flow (every cloud write goes dry-run first) ---------------------------
// Every data-agent write is Pro (reads stay free): the trigger wears a Pro pill on the free tier and a free
// click gets the plain invitation instead of the raw engine exception — the teach-don't-throw pattern.
type WriteRun = (commit: boolean) => Promise<DataAgentWriteReport>;
function WriteFlow({ label, applyLabel = 'Apply (writes to Fabric)', run, onCommitted, danger, primary, disabled, tier }: {
  label: string; applyLabel?: string; run: WriteRun; onCommitted?: (r: DataAgentWriteReport) => void;
  danger?: boolean; primary?: boolean; disabled?: boolean; tier?: string;
}) {
  const [phase, setPhase] = useState<'idle' | 'review' | 'done'>('idle');
  const [report, setReport] = useState<DataAgentWriteReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);

  const call = async (commit: boolean, next: 'review' | 'done') => {
    setBusy(true); setErr(null); setUpsell(null);
    // R-B: a report that says error is a refusal, not a commit: the words behind it (a name, a description, the rows) stay.
    try { const r = await run(commit); setReport(r); setPhase(next); if (commit && r?.status !== 'error') onCommitted?.(r); }
    catch (e) {
      // A Pro refusal teaches in plain English; a scrubbed REST error stays verbatim, never swallowed.
      if (isEntitlementError(e)) setUpsell('Configure and publish data agents with Pro. Browsing agents stays free.');
      else setErr(errMsg(e));
    }
    finally { setBusy(false); }
  };
  const reset = () => { setPhase('idle'); setReport(null); setErr(null); setUpsell(null); };

  // Only the fill differs from a plain .sem-btn. The border has to move with the fill, or a filled button
  // keeps the neutral ring the shared class gives it and reads as two controls stacked.
  // Instrument: the trigger is an ordinary outline button (a danger one outlined in the error colour); the one
  // filled control is the Apply that actually writes, shown only after the review. `primary` no longer fills.
  void primary;
  const triggerStyle = danger ? { borderColor: 'var(--sem-bad)' } : undefined;

  return (
    <div className="flex flex-col gap-2">
      {phase === 'idle' && (
        <button onClick={() => call(false, 'review')} disabled={disabled || busy}
          title={tier === 'free' ? 'Configure and publish data agents with Pro.' : undefined}
          className="sem-btn self-start" style={triggerStyle}>
          {busy ? 'Preparing…' : label}
          <ProBadge show={tier === 'free'} variant="accent" />
        </button>
      )}

      {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}

      {phase === 'review' && report && (
        <div className="pt-2" style={{ borderTop: '1px solid var(--sem-rule)' }}>
          <ReportView report={report} />
          <div className="mt-3 flex items-center gap-2">
            <button onClick={() => call(true, 'done')} disabled={busy}
              className={danger ? 'sem-btn' : 'sem-btn sem-btn-primary'}
              style={danger ? { background: 'var(--sem-bad)', color: '#fff', borderColor: 'var(--sem-bad)' } : undefined}>
              {busy ? 'Writing…' : applyLabel}
            </button>
            <Button onClick={reset} disabled={busy}>Cancel</Button>
          </div>
        </div>
      )}

      {phase === 'done' && report && (
        <div className="pt-2" style={{ borderTop: '1px solid var(--sem-rule)' }}>
          <ReportView report={report} />
          <div className="mt-3"><Button onClick={reset}>Done</Button></div>
        </div>
      )}
    </div>
  );
}

// The write report, rendered VERBATIM (status + message + the exact request summary — never the full payload).
function ReportView({ report }: { report: DataAgentWriteReport }) {
  const chip = report.status === 'error' ? 'sem-chip sem-chip-bad' : report.status === 'dry-run' ? 'sem-chip sem-chip-warn' : 'sem-chip sem-chip-ok';
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <span className={chip} style={{ color: 'var(--sem-fg)' }}>{uiLabel(report.status)}</span>
        {report.agentId && <span className="sem-text-meta sem-num">{report.agentId}</span>}
      </div>
      {report.message && <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)' }}>{report.message}</div>}
      {report.requestSummary && (
        <pre className="m-0 text-(length:--sem-text-meta) whitespace-pre-wrap" style={{ color: 'var(--sem-muted)', fontFamily: 'var(--sem-font-num)' }}>{report.requestSummary}</pre>
      )}
    </div>
  );
}

// ---- empty / edge states -----------------------------------------------------------------------
function NoWorkspace({ workspaces, wsErr, waitingForSignIn, onSignIn }: { workspaces: FabricWorkspace[] | null; wsErr: string | null; waitingForSignIn: boolean; onSignIn: () => void }) {
  if (waitingForSignIn) {
    return (
      <div className="sem-card sem-stack" data-testid="dataagent-sign-in">
        <div>
          <div className="sem-card-title">Sign in to see your workspaces</div>
          <div className="sem-text-meta">Nothing opens until you press the button. Pick a different way to sign in above if you prefer.</div>
        </div>
        <div><Button onClick={onSignIn}>Sign in</Button></div>
      </div>
    );
  }
  return (
    <div className="sem-card sem-stack">
      <div>
        <div className="sem-card-title">No workspace selected</div>
        <div className="sem-text-meta">
          {wsErr
            ? `Couldn't list Fabric workspaces: ${wsErr}`
            : workspaces && workspaces.length === 0
              ? 'No Fabric workspaces are available for this account. Sign in to Fabric (run az login) and refresh.'
              : 'Pick a Fabric workspace above to see its data agents.'}
        </div>
      </div>
    </div>
  );
}

function EmptyWorkspace({ list }: { list: DataAgentList }) {
  return (
    <div className="sem-card sem-stack">
      <div>
        <div className="sem-card-title">No data agents in this workspace</div>
        <div className="sem-text-meta">Create one from the list on the left, then scope it to this model, teach it, and publish.</div>
        {list.observedItemTypes.length > 0 && (
          <div className="sem-text-meta mt-3">
            Item types seen here: {list.observedItemTypes.join(' · ')}
          </div>
        )}
      </div>
    </div>
  );
}

function SelectPrompt() {
  return (
    <div className="sem-card sem-stack">
      <div>
        <div className="sem-card-title">Select an agent</div>
        <div className="sem-text-meta">Pick a data agent from the left to scope, teach and publish it, or create a new one.</div>
      </div>
    </div>
  );
}

function AgentFetchError({ agentId, error, onRetry }: { agentId: string; error: string; onRetry: () => void }) {
  return (
    <Panel>
      <Banner color="var(--sem-bad)"><b>Couldn’t load this agent</b></Banner>
      <div className="sem-text-meta sem-num mt-1">{agentId}</div>
      <pre className="sem-inset mt-2 m-0 text-(length:--sem-text-meta) whitespace-pre-wrap" style={{ color: 'var(--sem-fg)', fontFamily: 'var(--sem-font-num)' }}>{error}</pre>
      <div className="mt-2"><Button onClick={onRetry}>Retry</Button></div>
    </Panel>
  );
}

// ---- small helpers -----------------------------------------------------------------------------
function Muted({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`text-(length:--sem-text-meta) ${className}`} style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
// Instrument's parts for this page, drawn from the shared primitives: a card on the grid, its title, state as a
// word and a dot rather than a coloured pill, and a notice as a dotted line rather than a tinted box (a tinted
// box inside a card would be a card inside a card).
function Panel({ children }: { children: React.ReactNode }) {
  return <section className="sem-card">{children}</section>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="sem-card-title">{children}</div>;
}
function Pill({ children, tint, title }: { children: React.ReactNode; tint?: string; title?: string }) {
  const tone = !tint ? 'sem-chip-neutral' : /bad/.test(tint) ? 'sem-chip-bad' : /warn/.test(tint) ? 'sem-chip-warn' : /good/.test(tint) ? 'sem-chip-ok' : 'sem-chip-neutral';
  return <span title={title} className={`sem-chip ${tone}`}>{children}</span>;
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  const tone = /bad/.test(color) ? 'sem-chip-bad' : /warn/.test(color) ? 'sem-chip-warn' : 'sem-chip-ok';
  return (
    <div className="sem-inline text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)' }}>
      <span className={`sem-chip ${tone}`} aria-hidden="true" /><span className="min-w-0">{children}</span>
    </div>
  );
}
