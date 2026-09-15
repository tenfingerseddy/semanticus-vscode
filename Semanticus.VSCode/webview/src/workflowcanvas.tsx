import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Background, Controls, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider,
  useNodesState, useReactFlow, type Node, type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { loadState, saveState, rpc, onWorkflowLayoutChange, onReconnect } from './bridge';
// Same shared control the rest of the editor uses, so a later control restyle reaches this surface too.
import { Button } from './workflows';
// The counts chip is the same chip the Diagram and Lineage float on their own canvases, so one canvas
// reads one way everywhere in Studio.
import { CanvasChip } from './toolrow';
import { workflowLayoutSession } from './workflowlayout.mjs';
import type { BoxesStepView } from './workflowboxes.mjs';
import { mapLayoutPositions, type WorkflowEditPreview } from './workflowdraft.mjs';

type StepNode = Node<{ step: BoxesStepView }, 'workflowStep'>;
type Positions = Record<string, { x: number; y: number }>;
const nodeId = (step: BoxesStepView) => step.id;
const defaultPosition = (index: number) => ({ x: index * 300, y: 30 });

// This used to be a row of text above the canvas, which cost height on every screen to say something a
// reader needs once. It is the canvas background's tooltip now, and the empty state says the same thing
// in the one case where it is actually news.
const CANVAS_HINT = 'Select a step to edit it. Steps run in this order.';

export interface CanvasCounts { steps: number; actions: number; checks: number }

function WorkflowStepNode({ data: { step }, selected }: NodeProps<StepNode>) {
  return (
    <div data-wf-canvas-step={step.order} className="rounded-xl border p-3 text-left"
      style={{ width: 248, background: 'var(--sem-surface)', color: 'var(--sem-fg)',
        borderColor: selected ? 'var(--sem-accent)' : 'var(--sem-border)',
        boxShadow: selected ? '0 0 0 2px var(--sem-accent-soft)' : undefined }}>
      <Handle type="target" position={Position.Left} isConnectable={false} />
      <div className="text-[10px] mb-1" style={{ color: 'var(--sem-muted)' }}>STEP {step.number}</div>
      <div className="text-[13px] font-semibold break-words">{step.title || 'Untitled'}</div>
      <div className="text-[10px] mt-1 break-words" style={{ color: 'var(--sem-muted)' }}>{step.idLabel}</div>
      <div className="flex flex-col gap-1 mt-2 text-[10px]" style={{ color: 'var(--sem-accent)' }}>
        {step.when && <span>When {step.when}</span>}
        {step.forEach && <span>For each {step.forEach.as || 'item'} in {step.forEach.in || 'a list'}</span>}
        {step.call && <span>Calls {step.call.workflow}</span>}
        {!step.when && !step.forEach && !step.call && <span>{step.ops.length} {step.ops.length === 1 ? 'action' : 'actions'}</span>}
      </div>
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  );
}

const nodeTypes = { workflowStep: WorkflowStepNode };

// A key press inside a field is the reader typing, never a shortcut.
const typing = (target: EventTarget | null) => {
  const el = target as HTMLElement | null;
  if (!el || !el.tagName) return false;
  return /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) || el.isContentEditable;
};

function Canvas({ steps, storageKey, workflowName, workflowTitle, selected, onSelect, onClear, keyChanges = [], onAddStep, addDisabled, addNote,
  counts, fullscreen = false, onFullscreen, fitSignal = 0 }: {
  steps: BoxesStepView[]; storageKey: string; workflowName?: string; workflowTitle?: string; selected: number; onSelect: (index: number) => void;
  // Clicking the background clears the selection, which is how the step editor is dismissed without
  // reaching for a control, and what makes Pin mean something.
  onClear?: () => void;
  keyChanges?: WorkflowEditPreview['keyChanges'];
  // The canvas owns its own add control. It must never depend on a step being selected, because with zero
  // steps nothing can be selected and the surface was a dead end (Kane, 1.2.0).
  onAddStep?: () => void; addDisabled?: boolean; addNote?: string | null;
  // Counts are computed by the editor from the draft, not from the box view, because a box view carries
  // no gate and the reader wants to know how many checks the workflow actually holds.
  counts?: CanvasCounts;
  // Full screen is the editor's state, not the canvas's: the Author header and the drawer answer to it
  // too. The canvas owns the control and the Shift+F path.
  fullscreen?: boolean; onFullscreen?: () => void;
  // Bumped whenever the space beside the canvas changes (the drawer opening or closing), so the steps
  // land back in the middle instead of sitting off to one side.
  fitSignal?: number;
}) {
  const [nodes, setNodes, onNodesChange] = useNodesState<StepNode>([]);
  const [saveError, setSaveError] = useState('');
  const [loading, setLoading] = useState(false);
  const [connection, setConnection] = useState(0);
  const layoutKey = workflowName ? `shared:${workflowName}` : storageKey;
  const session = useRef<ReturnType<typeof workflowLayoutSession> | null>(null);
  useEffect(() => onReconnect(() => {
    session.current?.dispose();
    setConnection(value => value + 1);
  }), []);
  const stepsRef = useRef(steps);
  const nodesRef = useRef(nodes);
  const appliedKeyChanges = useRef('');
  stepsRef.current = steps;
  nodesRef.current = nodes;
  const { fitView } = useReactFlow();
  const remember = useCallback((items: StepNode[]) => {
    const positions = Object.fromEntries(items.map((node) => [node.id, node.position]));
    if (session.current) void session.current.save(positions);
    else saveState(storageKey, positions);
  }, [storageKey]);

  useEffect(() => {
    const apply = (saved: Positions) => setNodes(stepsRef.current.map((step, index) => {
      const position = saved?.[nodeId(step)];
      return {
        id: nodeId(step), type: 'workflowStep', data: { step },
        position: position && Number.isFinite(position.x) && Number.isFinite(position.y)
          ? position : defaultPosition(index),
        ariaLabel: `Step ${step.number}: ${step.title || 'Untitled'}`,
      };
    }));
    setSaveError('');
    apply(workflowName ? {} : loadState<Positions>(storageKey, {}));
    if (!workflowName) { setLoading(false); return; }
    setLoading(true);
    let active = true;
    const current = workflowLayoutSession({ name: workflowName, rpc, subscribe: onWorkflowLayoutChange,
      apply, error: setSaveError });
    session.current = current;
    void current.ready.finally(() => { if (active) setLoading(false); });
    return () => { active = false; current.dispose(); session.current = null; };
  }, [layoutKey, workflowName, connection, setNodes]);

  // Definition refreshes update labels and prune deleted IDs without discarding unsaved positions.
  useEffect(() => {
    const currentPositions = Object.fromEntries(nodesRef.current.map((node) => [node.id, node.position]));
    const positions = mapLayoutPositions(currentPositions, keyChanges);
    const next = steps.map((step, index) => ({ id: nodeId(step), type: 'workflowStep' as const, data: { step },
      position: positions[nodeId(step)] ?? defaultPosition(index),
      ariaLabel: `Step ${step.number}: ${step.title || 'Untitled'}` }));
    setNodes(next);
    const migrationKey = JSON.stringify(keyChanges);
    if (keyChanges.length && migrationKey !== appliedKeyChanges.current && session.current) {
      appliedKeyChanges.current = migrationKey;
      void session.current.save(Object.fromEntries(next.map((node) => [node.id, node.position])));
    }
  }, [steps, keyChanges, setNodes]);

  const edges = useMemo(() => steps.slice(1).map((step, index) => ({
    id: `order:${index}`, source: nodeId(steps[index]), target: nodeId(step),
    type: 'smoothstep',
    ariaLabel: `File order: step ${steps[index].number} to step ${step.number}`,
    markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--sem-muted)' },
    style: { stroke: 'var(--sem-muted)' },
  })), [steps]);

  // The space beside the canvas changed (the drawer opening or closing, or full screen). React Flow has
  // re-measured by the next frame, so the refit lands on the new width instead of the old one.
  useEffect(() => {
    if (!fitSignal) return;
    const frame = requestAnimationFrame(() => { void fitView({ padding: 0.15, maxZoom: 1, duration: 200 }); });
    return () => cancelAnimationFrame(frame);
  }, [fitSignal, fullscreen, fitView]);

  const arrange = () => {
    const arranged = nodes.map((node, index) => ({ ...node, position: defaultPosition(index) }));
    setNodes(arranged); remember(arranged);
    requestAnimationFrame(() => { void fitView({ padding: 0.15, maxZoom: 1, duration: 200 }); });
  };

  const chip = 'sem-btn sem-btn-sm';
  return (
    <div className="sem-wf-canvas" data-wf-canvas="true" tabIndex={0} aria-label="Workflow canvas"
      title={steps.length ? CANVAS_HINT : undefined}
      onKeyDown={(event) => {
        if (event.defaultPrevented || typing(event.target)) return;
        if (event.shiftKey && (event.key === 'F' || event.key === 'f') && !event.ctrlKey && !event.metaKey && !event.altKey) {
          event.preventDefault(); onFullscreen?.();
        }
      }}>
      <div className="sem-wf-canvas-tools">
        {fullscreen && <span className="sem-wf-canvas-idchip">{workflowTitle || 'This workflow'} · Canvas · Esc to leave</span>}
        {onAddStep && <button type="button" className={chip} disabled={addDisabled} onClick={onAddStep}
          title={addNote ?? 'Add a step at the end.'}>+ Add step</button>}
        <span className="sem-wf-canvas-tool-sep" />
        <button type="button" className={chip} disabled={loading} onClick={arrange}
          title="Lay the steps out left to right in file order.">Arrange</button>
        <button type="button" className={chip} disabled={loading} onClick={() => {
          setNodes(nodes.map((node, index) => ({ ...node, position: defaultPosition(index) })));
          if (session.current) void session.current.save({}); else saveState(storageKey, {});
        }} title="Forget the saved positions and start from the plain order.">Reset</button>
        <button type="button" className={chip} onClick={() => { void fitView({ padding: 0.15, maxZoom: 1, duration: 200 }); }}
          title="Fit every step in view.">Fit all</button>
        <span className="sem-wf-canvas-tool-sep" />
        <button type="button" className={chip} aria-pressed={fullscreen} onClick={() => onFullscreen?.()}
          title="Give the canvas the whole tab. Shift and F does the same, and Escape leaves.">
          {fullscreen ? 'Leave full screen' : 'Full screen'}</button>
        {loading && <span className="sem-wf-canvas-idchip">Loading shared layout…</span>}
      </div>
      {saveError && <CanvasChip at="end" tone="bad" note role="alert">
        Layout not saved: {saveError} Your positions are still shown.{' '}
        <button type="button" onClick={() => { void session.current?.refresh(); }} className="underline">Reload shared layout</button>
      </CanvasChip>}
      {steps.length === 0 ? <div className="h-full flex flex-col items-center justify-center gap-2 px-6 text-center" data-wf-canvas-empty="true">
        <div className="text-[13px] font-semibold">No steps yet</div>
        <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>Steps run in this order. Add the first one to start.</div>
        {onAddStep && <Button primary disabled={addDisabled} onClick={onAddStep}>Add the first step</Button>}
      </div> : <>
        <ReactFlow<StepNode> nodes={nodes.map((node) => ({ ...node, selected: node.data.step.order === selected }))}
          edges={edges} nodeTypes={nodeTypes} onNodesChange={onNodesChange}
          onNodeClick={(_, node) => onSelect(node.data.step.order)}
          onPaneClick={() => onClear?.()}
          onNodeDragStop={(_, node) => remember(nodes.map((item) => item.id === node.id ? node : item))}
          nodesDraggable={!loading}
          nodesConnectable={false} edgesReconnectable={false} edgesFocusable={false}
          deleteKeyCode={null} multiSelectionKeyCode={null} selectionOnDrag={false}
          fitView fitViewOptions={{ padding: 0.15, maxZoom: 1 }} minZoom={0.1} maxZoom={1.8}
          proOptions={{ hideAttribution: true }}>
          <Background color="var(--sem-border)" gap={20} />
          <Controls showInteractive={false} />
        </ReactFlow>
        {counts && <CanvasChip at="start" data-wf-canvas-counts="true">
          <span><b>{counts.steps}</b> steps</span>
          <span><b>{counts.actions}</b> actions</span>
          <span><b>{counts.checks}</b> checks</span>
        </CanvasChip>}
      </>}
    </div>
  );
}

export function WorkflowCanvas(props: Parameters<typeof Canvas>[0]) {
  return <ReactFlowProvider><Canvas {...props} /></ReactFlowProvider>;
}
