import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, copyText } from './bridge';
import { usePersistedState } from './hooks';
import { Panel, Button, Banner, SectionTitle, Pill } from './workflows';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';
import { useConnection } from './connection';
import { isEditableElementTree, parseElementTree, updateElementSelection, type DataAgentElement } from './dataagent-schema.mjs';
import { uiLabel } from './copy';

// ===================================================================================================
// Data Agent — the "Validate → Deploy → Consume" payoff tab (docs/data-agent-tab-plan.md §4). Deploy a
// Fabric Data Agent scoped to the model you just made AI-ready: SCOPE (add this model, element tree) →
// TEACH (AI instructions + few-shots) → SHIP (publish + the MCP endpoint to connect your AI Assistant to).
//
// A Data Agent is a definition-based Fabric ITEM. Reads (list/get) are free; the Semanticus verb
// generate_data_agent_config_from_model (Pro) assembles a semantic_model source from the open session.
// EVERY cloud write goes dry-run FIRST (commit=false reports the exact request, sends nothing) and renders
// that RequestSummary before an explicit "Apply (writes to Fabric)" — the commit-token contract made visual.
// The engine holds no credentials and runs NO inference (golden rule #1): it never queries the agent — a
// published agent is an MCP server you connect your OWN AI Assistant to. "AI Assistant", never "Claude".
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
  const tier = useTier();   // shared entitlement read (pro.tsx) — drives the Pro badges on every write button
  // WHO signs in matters: azcli may be logged into a DIFFERENT tenant than the model's XMLA session (hit
  // live: the tab listed a client tenant's workspaces while the model was nexwave-bound). The user picks
  // the auth mode + tenant here, persisted, and every Fabric call on this tab carries the choice.
  const [authMode, setAuthMode] = usePersistedState<string>('dataagent.authMode', 'azcli');
  const [tenantId, setTenantId] = usePersistedState<string>('dataagent.tenantId', '');
  const tenant = tenantId.trim() || null;
  // Latest-wins guards for the workspace→agent→detail cascade: each loader claims a monotonic token before its await
  // and applies its result only while still newest — so a slower earlier response can't overwrite a newer selection.
  const wsSeq = useRef(0);
  const listSeq = useRef(0);
  const detailSeq = useRef(0);

  // Load workspaces; re-list when the identity (auth mode / tenant) changes.
  useEffect(() => {
    setWorkspaces(null); setWorkspaceId('');
    const seq = ++wsSeq.current;
    rpc<FabricWorkspace[]>('listWorkspaces', authMode, tenant)
      .then((w) => { if (seq === wsSeq.current) { setWorkspaces(w ?? []); setWsErr(null); } })
      .catch((e) => { if (seq === wsSeq.current) setWsErr(errMsg(e)); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [authMode, tenantId]);

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
    rpc<DataAgentList>('listDataAgents', workspaceId, authMode, tenant)
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
    if (!workspaceId || !selected) { setDetail(null); setDetailErr(null); return; }   // clear the error with the detail — a stale error must not stick to the next selection
    setDetailErr(null);
    rpc<DataAgentDetail>('getDataAgent', workspaceId, selected, authMode, tenant)
      .then((d) => { if (seq === detailSeq.current) setDetail(d); })
      .catch((e) => { if (seq === detailSeq.current) { setDetail(null); setDetailErr(errMsg(e)); } });
  };
  useEffect(() => { loadDetail();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId, selected]);

  const ws = workspaces?.find((w) => w.id === workspaceId) ?? null;

  return (
    <div className="h-full flex flex-col min-h-0">
      <Header workspaces={workspaces} workspaceId={workspaceId} onWorkspace={setWorkspaceId} onRefresh={loadAgents} wsErr={wsErr}
        authMode={authMode} onAuthMode={setAuthMode} tenantId={tenantId} onTenantId={setTenantId} />
      {!workspaceId ? (
        <NoWorkspace workspaces={workspaces} wsErr={wsErr} />
      ) : (
        <div className="flex-1 flex min-h-0">
          <AgentRail
            list={list} listErr={listErr} selected={selected} workspaceId={workspaceId}
            authMode={authMode} tenantId={tenant} tier={tier}
            onSelect={setSelected} onListChanged={loadAgents}
            onCreated={() => { loadAgents(); }}
          />
          <main className="flex-1 min-w-0 overflow-auto">
            {list && list.agents.length === 0 ? (
              <EmptyWorkspace list={list} />
            ) : !selected ? (
              <SelectPrompt />
            ) : detailErr ? (
              <div className="p-4"><AgentFetchError agentId={selected} error={detailErr} onRetry={loadDetail} /></div>
            ) : !detail ? (
              <div className="p-4"><Panel><Muted>Loading agent…</Muted></Panel></div>
            ) : detail.error ? (
              <div className="p-4"><AgentFetchError agentId={selected} error={detail.error} onRetry={loadDetail} /></div>
            ) : (
              <div className="flex flex-col gap-4 p-4 min-w-0">
                <AgentHeader detail={detail} ws={ws} />
                <ScopePanel workspaceId={workspaceId} agentId={selected} detail={detail} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
                <TeachPanel workspaceId={workspaceId} agentId={selected} detail={detail} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
                <ShipPanel workspaceId={workspaceId} agentId={selected} detail={detail} ws={ws} tier={tier} authMode={authMode} tenantId={tenant} onChanged={loadDetail} />
              </div>
            )}
          </main>
        </div>
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
    <div className="px-4 py-2.5 border-b flex items-center gap-3 shrink-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="text-[13px] font-semibold">Data Agent</div>
      <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Deploy a Fabric Data Agent scoped to this model.</div>
      <div className="ml-auto flex items-center gap-2">
        <select value={authMode} onChange={(e) => onAuthMode(e.target.value)}
          title="WHO signs in to Fabric here; az cli may be logged into a different tenant than the model's XMLA session"
          className="text-[12px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
          <option value="azcli">az cli</option>
          <option value="interactive">Entra (interactive)</option>
          <option value="devicecode">device code</option>
          <option value="serviceprincipal">service principal</option>
        </select>
        {authMode !== 'azcli' && (
          <input value={tenantId} onChange={(e) => onTenantId(e.target.value)} placeholder="tenant id / domain (optional)"
            title="The Entra tenant to sign in to; set this when the model lives in a different tenant than your default"
            className="text-[12px] px-2 py-1 rounded-md outline-none"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', width: 180 }} />
        )}
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Workspace</span>
        <select value={workspaceId} onChange={(e) => onWorkspace(e.target.value)}
          disabled={!workspaces || workspaces.length === 0}
          className="text-[12px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', minWidth: 180 }}>
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
    <aside className="w-[300px] shrink-0 border-r overflow-auto" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="px-3 py-2.5 border-b sticky top-0 z-10" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
        <div className="text-[13px] font-semibold">Agents</div>
        <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>Data agents in this workspace.</div>
      </div>
      <div className="p-2 flex flex-col gap-2">
        <NewAgentForm workspaceId={workspaceId} authMode={authMode} tenantId={tenantId} tier={tier} onCreated={onCreated} />
        {listErr && <Banner color="var(--sem-bad)">{listErr}</Banner>}
        {list?.note && <div className="text-[10.5px] px-1" style={{ color: 'var(--sem-muted)' }}>{list.note}</div>}
        {list == null ? (
          <Muted className="px-1 py-2">Loading agents…</Muted>
        ) : list.agents.length === 0 ? (
          <Muted className="px-1 py-2">No agents yet. Create one above.</Muted>
        ) : (
          list.agents.map((a) => <AgentCard key={a.id} a={a} active={a.id === selected} onClick={() => onSelect(a.id)} />)
        )}
      </div>
    </aside>
  );
}

function AgentCard({ a, active, onClick }: { a: DataAgentInfo; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick}
      className="text-left rounded-lg border px-3 py-2.5 transition-colors"
      style={active
        ? { background: 'var(--sem-accent-soft)', borderColor: 'color-mix(in srgb, var(--sem-accent) 45%, transparent)' }
        : { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <div className="text-[12.5px] font-semibold truncate flex-1">{a.name}</div>
        {a.published === true
          ? <Pill tint="var(--sem-good)" title="A published (read-only) copy exists">published</Pill>
          : a.published === false
            ? <Pill title="Draft only, never published">draft</Pill>
            : null}
      </div>
      {a.description && <div className="text-[11px] mt-1 line-clamp-2" style={{ color: 'var(--sem-muted)' }}>{a.description}</div>}
      <div className="flex items-center gap-1.5 mt-1.5">
        <Pill tint="var(--sem-accent)">{uiLabel(a.type, 'Data agent')}</Pill>
      </div>
    </button>
  );
}

function NewAgentForm({ workspaceId, authMode, tenantId, tier, onCreated }: { workspaceId: string; authMode: string; tenantId: string | null; tier: string; onCreated: () => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  if (!open) {
    return (
      <button onClick={() => setOpen(true)}
        className="text-[11px] px-2 py-1.5 rounded-md font-semibold"
        style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
        + New agent<ProBadge show={tier === 'free'} />
      </button>
    );
  }
  return (
    <div className="rounded-lg border p-2.5 flex flex-col gap-2" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="text-[11px] font-semibold">New data agent</div>
      <input value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder="Agent name…" spellCheck={false}
        className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      <WriteFlow
        label="Create…" applyLabel="Apply (writes to Fabric)" primary disabled={!name.trim()} tier={tier}
        run={(commit) => rpc<DataAgentWriteReport>('createDataAgent', workspaceId, name.trim(), null, commit, authMode, tenantId)}
        onCommitted={() => { setName(''); setOpen(false); onCreated(); }}
      />
      <button onClick={() => { setOpen(false); setName(''); }} className="text-[11px] self-start" style={{ color: 'var(--sem-muted)' }}>Cancel</button>
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
            <div className="text-[15px] font-semibold">{info.name}</div>
            <Pill tint="var(--sem-accent)">{uiLabel(info.type, 'Data agent')}</Pill>
            {detail.published
              ? <Pill tint="var(--sem-good)">published</Pill>
              : <Pill>draft only</Pill>}
          </div>
          {info.description && <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>{info.description}</div>}
          <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>
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
  useEffect(() => { setConfig(null); setGenErr(null); }, [context?.publishing?.connectionId]);

  const generate = async () => {
    setGenBusy(true); setGenErr(null); setGenUpsell(null);
    // Pro-gated, never pre-disabled: a free click gets the plain invitation; a real failure stays a real error.
    try { setConfig(await rpc<DataAgentConfig>('generateDataAgentConfig')); }
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
      <div className="text-[12px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>
        Build the source from editable <b style={{ color: 'var(--sem-fg)' }}>{context?.editing?.modelName || 'open model'}</b> metadata,
        then bind it to <b style={{ color: context?.publishing?.available ? 'var(--sem-fg)' : 'var(--sem-warn)' }}>{context?.publishing?.modelName || context?.publishing?.database || 'a publish destination you choose'}</b> for live use.
        Local files cannot serve agent queries themselves.
        <button type="button" onClick={openConnections} className="underline ml-1">Change publish destination</button>
      </div>

      {!config && (
        <div className="mt-3 flex items-center gap-2">
          <Button primary disabled={genBusy} onClick={generate}
            title={tier === 'pro' ? 'Assemble a semantic model source from the open model' : 'Configure and publish data agents with Pro. Browsing agents stays free.'}>
            {genBusy ? 'Building…' : '+ Add this model'}
            <ProBadge show={tier === 'free'} variant="onAccent" />
          </Button>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Builds the config for review; writes nothing.</span>
        </div>
      )}
      {genUpsell && <div className="mt-3"><UpsellNotice onDismiss={() => setGenUpsell(null)}>{genUpsell}</UpsellNotice></div>}
      {genErr && <div className="mt-3"><Banner color="var(--sem-bad)">{genErr}</Banner></div>}

      {config && (
        <div className="mt-3 flex flex-col gap-3">
          {config.note && <Banner color="var(--sem-warn)">{config.note}</Banner>}
          {config.aiInstructions && (
            <div>
              <div className="text-[10px] uppercase tracking-wide font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>AI instructions (seeded)</div>
              <div className="rounded-md px-2.5 py-2 text-[12px] whitespace-pre-wrap" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{config.aiInstructions}</div>
            </div>
          )}
          <div>
            <div className="flex items-center gap-2 mb-1">
              <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Elements · {config.displayName}</div>
              <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Click a row to change it · ✓ included · ✕ excluded</span>
            </div>
            <div className="rounded-lg border p-2.5" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
              {elements.length
                ? <ElementTree elements={elements} onSelectionChange={changeGeneratedSelection} />
                : <Muted>No elements in the generated config.</Muted>}
            </div>
          </div>
          <div className="rounded-lg border p-3" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
            <div className="text-[11px] mb-2" style={{ color: 'var(--sem-muted)' }}>
              Apply this source into the draft under folder <span className="tnum">{folder}</span>.
            </div>
            <WriteFlow
              key={config.datasourceJson}
              label="Apply to draft…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
              run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, folder, config.datasourceJson, null, commit, authMode, tenantId)}
              onCommitted={() => { setConfig(null); onChanged(); }}
            />
          </div>
        </div>
      )}

      {detail.draft.dataSources.length > 0 && (
        <div className="mt-4">
          <div className="text-[10px] uppercase tracking-wide font-semibold mb-1.5" style={{ color: 'var(--sem-muted)' }}>Existing sources ({detail.draft.dataSources.length})</div>
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
  useEffect(() => { setSchemaJson(sourceJson); setSavedJson(sourceJson); }, [ds.folder, sourceJson]);
  const schemaElements = useMemo(() => parseElementTree(schemaJson), [schemaJson]);
  const fallbackElements = useMemo(() => parseElementTree(ds.elementsJson), [ds.elementsJson]);
  const semanticModel = ds.type === 'semantic_model' || ds.folder.startsWith('semantic_model-');
  const editableDocument = isEditableElementTree(schemaJson);
  const elements = editableDocument ? schemaElements : fallbackElements;
  const editable = semanticModel && editableDocument;
  const dirty = editable && schemaJson !== savedJson;
  const changeSelection = (path: number[], selected: boolean) => setSchemaJson((current) => updateElementSelection(current, path, selected));
  return (
    <div className="rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[12.5px] font-semibold">{ds.displayName || ds.folder}</span>
        <Pill tint="var(--sem-accent)">{uiLabel(ds.type)}</Pill>
        <span className="text-[10.5px] tnum ml-auto" style={{ color: 'var(--sem-muted)' }}>{ds.folder}</span>
      </div>
      {ds.userDescription && <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>{ds.userDescription}</div>}
      {elements.length > 0 && (
        <>
          <button onClick={() => setOpen((o) => !o)} aria-label={`${open ? 'Hide' : 'Show'} elements for ${ds.displayName || ds.folder}`}
            className="mt-1.5 flex items-center gap-1 text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>
            <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
            {open ? 'Hide elements' : `Elements`}
          </button>
          {open && (
            <div className="mt-1.5 rounded-md border p-2" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
              {editable && <Muted className="mb-1">Click a row to include or exclude it.</Muted>}
              <ElementTree elements={elements} onSelectionChange={editable ? changeSelection : undefined} />
              {semanticModel && !editable && (
                <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-warn)' }}>
                  This schema cannot be edited because its source document is missing or malformed. Refresh the agent after correcting it in Fabric.
                </div>
              )}
              {!semanticModel && (
                <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
                  Schema editing here is available for semantic models. Edit this source in Fabric.
                </div>
              )}
              {editable && (
                <div className="mt-2 pt-2 border-t" style={{ borderColor: 'var(--sem-border)' }}>
                  {dirty ? (
                    <WriteFlow
                      key={schemaJson}
                      label="Save schema…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
                      run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, ds.folder, schemaJson, null, commit, authMode, tenantId)}
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
        <div className="mt-1.5 text-[11px]" style={{ color: editable ? 'var(--sem-muted)' : 'var(--sem-warn)' }}>
          {editable ? 'This source has no fields in its agent schema.' : 'This schema cannot be edited because its source document is missing or malformed. Refresh the agent after correcting it in Fabric.'}
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
      <span className="text-[11px] w-3 shrink-0" style={{ color: selected ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{selected ? '✓' : '✕'}</span>
      <span className="text-[12px] font-medium truncate">{name}</span>
      {kind && <Pill>{kind}</Pill>}
      {!selected && <Pill tint="var(--sem-bad)">excluded</Pill>}
      {el.description && <span className="text-[11px] truncate" style={{ color: 'var(--sem-muted)' }}>· {el.description}</span>}
    </>
  );
  return (
    <>
      {onSelectionChange ? (
        <button type="button" aria-label={`${selected ? 'Exclude' : 'Include'} ${name}`}
          onClick={() => onSelectionChange(path, !selected)}
          className="w-full flex items-center gap-2 py-0.5 text-left rounded-sm hover:bg-[var(--sem-surface-2)]"
          style={{ paddingLeft: depth * 18, opacity: selected ? 1 : 0.65 }}>
          {row}
        </button>
      ) : (
        <div className="flex items-center gap-2 py-0.5" style={{ paddingLeft: depth * 18, opacity: selected ? 1 : 0.55 }}>{row}</div>
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
  const [instr, setInstr] = useState(detail.draft.aiInstructions ?? '');
  // Re-seed the editor when a fresh detail loads (a different agent, or a committed save re-fetched).
  useEffect(() => { setInstr(detail.draft.aiInstructions ?? ''); }, [detail.info.id, detail.draft.aiInstructions]);

  const len = instr.length;
  const over = len > AI_INSTRUCTIONS_CAP;
  const near = !over && len > AI_INSTRUCTIONS_CAP * 0.9;
  const counterColor = over ? 'var(--sem-bad)' : near ? 'var(--sem-warn)' : 'var(--sem-muted)';

  return (
    <Panel>
      <SectionTitle>Teach <span style={{ color: 'var(--sem-muted)' }}>· instructions + examples</span></SectionTitle>

      <div className="mt-2">
        <div className="flex items-center gap-2 mb-1">
          <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>AI instructions</div>
          <span className="ml-auto text-[10.5px] tnum" style={{ color: counterColor }}>{len.toLocaleString()} / {AI_INSTRUCTIONS_CAP.toLocaleString()}</span>
        </div>
        <textarea value={instr} onChange={(e) => setInstr(e.target.value)} rows={6} spellCheck={false}
          placeholder="How should the agent interpret this model? Preferred measures, glossary, how to qualify results…"
          className="w-full text-[12px] px-2.5 py-2 rounded-md outline-none resize-y"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: `1px solid ${over ? 'color-mix(in srgb, var(--sem-bad) 50%, transparent)' : 'var(--sem-border)'}` }} />
        {over && <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-bad)' }}>Over the {AI_INSTRUCTIONS_CAP.toLocaleString()}-character cap. Trim before saving.</div>}
        <div className="mt-2">
          <WriteFlow
            label="Save instructions…" applyLabel="Apply (writes to Fabric)" primary disabled={over} tier={tier}
            run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, instr, null, null, null, commit, authMode, tenantId)}
            onCommitted={onChanged}
          />
        </div>
      </div>

      <div className="mt-4">
        <div className="text-[10px] uppercase tracking-wide font-semibold mb-1.5" style={{ color: 'var(--sem-muted)' }}>Example question/query pairs · per source</div>
        {detail.draft.dataSources.length === 0 ? (
          <Muted>Add a data source in Scope first. Example question/query pairs attach to a source.</Muted>
        ) : (
          <div className="flex flex-col gap-3">
            {detail.draft.dataSources.map((ds) => (
              <FewShotEditor key={ds.folder} workspaceId={workspaceId} agentId={agentId} ds={ds} tier={tier} authMode={authMode} tenantId={tenantId} onChanged={onChanged} />
            ))}
          </div>
        )}
      </div>
    </Panel>
  );
}

function FewShotEditor({ workspaceId, agentId, ds, tier, onChanged, authMode, tenantId }: {
  workspaceId: string; agentId: string; ds: DataAgentDataSource; tier: string; authMode: string; tenantId: string | null; onChanged: () => void;
}) {
  const [rows, setRows] = useState<DataAgentFewShot[]>(ds.fewShots ?? []);
  useEffect(() => { setRows(ds.fewShots ?? []); }, [ds.folder, ds.fewShots]);
  const isSemanticModel = ds.type === 'semantic_model';

  const setRow = (i: number, patch: Partial<DataAgentFewShot>) => setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const add = () => setRows((r) => [...r, { question: '', query: '' }]);
  const remove = (i: number) => setRows((r) => r.filter((_, j) => j !== i));
  const fewshotsJson = JSON.stringify({ fewShots: rows.filter((r) => r.question.trim() || r.query.trim()) });

  return (
    <div className="rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <span className="text-[12px] font-semibold">{ds.displayName || ds.folder}</span>
        <Pill tint="var(--sem-accent)">{uiLabel(ds.type)}</Pill>
      </div>
      {isSemanticModel && (
        <div className="text-[11px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>
          Note: the Fabric portal does not support example question and query pairs for semantic-model sources yet, so pairs you save here do not change how the agent answers. The definition format allows them, so they are kept here, ready for when it does.
        </div>
      )}
      <div className="mt-2 flex flex-col gap-2">
        {rows.length === 0 && <Muted>No examples yet.</Muted>}
        {rows.map((r, i) => (
          <div key={i} className="flex items-start gap-2">
            <div className="flex-1 flex flex-col gap-1">
              <input value={r.question} onChange={(e) => setRow(i, { question: e.target.value })} placeholder="Question…" spellCheck={false}
                className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
              <input value={r.query} onChange={(e) => setRow(i, { query: e.target.value })} placeholder="Query (DAX)…" spellCheck={false}
                className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }} />
            </div>
            <button onClick={() => remove(i)} title="Remove" className="text-[12px] px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-muted)' }}>✕</button>
          </div>
        ))}
      </div>
      <div className="mt-2 flex items-center gap-2">
        <Button onClick={add}>+ Add example</Button>
        <WriteFlow
          label="Save examples…" applyLabel="Apply (writes to Fabric)" tier={tier}
          run={(commit) => rpc<DataAgentWriteReport>('updateDataAgent', workspaceId, agentId, null, ds.folder, null, fewshotsJson, commit, authMode, tenantId)}
          onCommitted={onChanged}
        />
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
      <SectionTitle>Ship <span style={{ color: 'var(--sem-muted)' }}>· stage + publish</span></SectionTitle>

      <div className="mt-2 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
        {published ? (
          <div className="flex items-start gap-2">
            <span className="mt-0.5 text-[13px]" style={{ color: 'var(--sem-good)' }}>●</span>
            <div className="min-w-0">
              <div className="text-[12.5px] font-semibold" style={{ color: 'var(--sem-good)' }}>Published</div>
              {published.publishDescription
                ? <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-fg)' }}>{published.publishDescription}</div>
                : <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>(no publish description)</div>}
              <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>A read-only copy is live; the draft above iterates independently.</div>
            </div>
          </div>
        ) : (
          <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Never published; the draft above is the only stage. Publish creates a read-only copy consumers query.</div>
        )}
      </div>

      {/* Publish */}
      <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
        <div className="text-[12px] font-semibold mb-1.5">{published ? 'Re-publish' : 'Publish'}</div>
        <input value={desc} onChange={(e) => setDesc(e.target.value)} placeholder="Publish description…" spellCheck={false}
          className="w-full text-[12px] px-2 py-1 rounded-md outline-none mb-2" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <WriteFlow
          label="Publish…" applyLabel="Apply (writes to Fabric)" primary tier={tier}
          run={(commit) => rpc<DataAgentWriteReport>('publishDataAgent', workspaceId, agentId, desc.trim() || null, commit, authMode, tenantId)}
          onCommitted={() => { setDesc(''); onChanged(); }}
        />
      </div>

      {/* MCP endpoint (after publish) */}
      {published && <McpEndpointCard endpoint={mcpEndpoint} />}

      {/* Delete (two-step: dry-run review, then explicit Apply) */}
      <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-surface)', borderColor: 'color-mix(in srgb, var(--sem-bad) 30%, transparent)' }}>
        <div className="text-[12px] font-semibold mb-1" style={{ color: 'var(--sem-bad)' }}>Delete agent</div>
        <div className="text-[11px] mb-2" style={{ color: 'var(--sem-muted)' }}>Removes the Fabric item entirely. Review the request, then apply. This cannot be undone.</div>
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
    <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-accent-soft)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
      <div className="text-[12px] font-semibold" style={{ color: 'var(--sem-accent)' }}>Connect your AI Assistant</div>
      <div className="text-[11px] mt-1" style={{ color: 'var(--sem-fg)' }}>
        A published agent is an MCP server. Semanticus never queries the agent or runs inference. Connect your AI Assistant to this endpoint instead.
      </div>
      <div className="mt-2 flex items-center gap-2">
        <code className="flex-1 min-w-0 truncate text-[11px] px-2 py-1 rounded-md" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }}>{endpoint}</code>
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
    try { const r = await run(commit); setReport(r); setPhase(next); if (commit) onCommitted?.(r); }
    catch (e) {
      // A Pro refusal teaches in plain English; a scrubbed REST error stays verbatim, never swallowed.
      if (isEntitlementError(e)) setUpsell('Configure and publish data agents with Pro. Browsing agents stays free.');
      else setErr(errMsg(e));
    }
    finally { setBusy(false); }
  };
  const reset = () => { setPhase('idle'); setReport(null); setErr(null); setUpsell(null); };

  const triggerStyle = danger
    ? { background: 'var(--sem-bad)', color: '#fff' }
    : primary
      ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
      : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' };

  return (
    <div className="flex flex-col gap-2">
      {phase === 'idle' && (
        <button onClick={() => call(false, 'review')} disabled={disabled || busy}
          title={tier === 'free' ? 'Configure and publish data agents with Pro.' : undefined}
          className="self-start text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap" style={triggerStyle}>
          {busy ? 'Preparing…' : label}
          <ProBadge show={tier === 'free'} variant={danger || primary ? 'onAccent' : 'accent'} />
        </button>
      )}

      {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}

      {phase === 'review' && report && (
        <div className="rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'color-mix(in srgb, var(--sem-accent) 34%, transparent)' }}>
          <ReportView report={report} />
          <div className="mt-2.5 flex items-center gap-2">
            <button onClick={() => call(true, 'done')} disabled={busy}
              className="text-[12px] px-3 py-1.5 rounded-lg font-medium disabled:opacity-40 whitespace-nowrap"
              style={danger ? { background: 'var(--sem-bad)', color: '#fff' } : { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>
              {busy ? 'Writing…' : applyLabel}
            </button>
            <Button onClick={reset} disabled={busy}>Cancel</Button>
          </div>
        </div>
      )}

      {phase === 'done' && report && (
        <div className="rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
          <ReportView report={report} />
          <div className="mt-2.5"><Button onClick={reset}>Done</Button></div>
        </div>
      )}
    </div>
  );
}

// The write report, rendered VERBATIM (status + message + the exact request summary — never the full payload).
function ReportView({ report }: { report: DataAgentWriteReport }) {
  const color = report.status === 'error' ? 'var(--sem-bad)' : report.status === 'dry-run' ? 'var(--sem-warn)' : 'var(--sem-good)';
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color, background: `color-mix(in srgb, ${color} 16%, transparent)` }}>{uiLabel(report.status)}</span>
        {report.agentId && <span className="text-[10.5px] tnum" style={{ color: 'var(--sem-muted)' }}>{report.agentId}</span>}
      </div>
      {report.message && <div className="text-[12px]" style={{ color: 'var(--sem-fg)' }}>{report.message}</div>}
      {report.requestSummary && (
        <pre className="rounded-md px-2.5 py-2 text-[11px] whitespace-pre-wrap" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)', font: '11px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace' }}>{report.requestSummary}</pre>
      )}
    </div>
  );
}

// ---- empty / edge states -----------------------------------------------------------------------
function NoWorkspace({ workspaces, wsErr }: { workspaces: FabricWorkspace[] | null; wsErr: string | null }) {
  return (
    <div className="flex-1 flex items-center justify-center p-8">
      <div className="text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">No workspace selected</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>
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
    <div className="h-full flex items-center justify-center p-8">
      <div className="text-center max-w-md">
        <div className="text-[15px] font-semibold mb-1">No data agents in this workspace</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Create one from the list on the left, then scope it to this model, teach it, and publish.</div>
        {list.observedItemTypes.length > 0 && (
          <div className="text-[11px] mt-3" style={{ color: 'var(--sem-muted)' }}>
            Item types seen here: {list.observedItemTypes.join(' · ')}
          </div>
        )}
        {list.note && <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>{list.note}</div>}
      </div>
    </div>
  );
}

function SelectPrompt() {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">Select an agent</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Pick a data agent from the left to scope, teach and ship it, or create a new one.</div>
      </div>
    </div>
  );
}

function AgentFetchError({ agentId, error, onRetry }: { agentId: string; error: string; onRetry: () => void }) {
  return (
    <Panel>
      <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-bad)' }}>Couldn’t load this agent</div>
      <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>{agentId}</div>
      <pre className="mt-2 rounded-lg px-3 py-2 text-[12px] whitespace-pre-wrap" style={{ background: 'color-mix(in srgb, var(--sem-bad) 10%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 30%, transparent)', font: '12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace' }}>{error}</pre>
      <div className="mt-2"><Button onClick={onRetry}>Retry</Button></div>
    </Panel>
  );
}

// ---- small helpers -----------------------------------------------------------------------------
function Muted({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`text-[12px] ${className}`} style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
