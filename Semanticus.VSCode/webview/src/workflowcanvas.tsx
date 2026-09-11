import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Background, Controls, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider,
  useNodesState, useReactFlow, type Node, type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { loadState, saveState, rpc, onWorkflowLayoutChange, onReconnect } from './bridge';
import { workflowLayoutSession } from './workflowlayout.mjs';
import type { BoxesStepView } from './workflowboxes.mjs';

type StepNode = Node<{ step: BoxesStepView }, 'workflowStep'>;
type Positions = Record<string, { x: number; y: number }>;
const nodeId = (step: BoxesStepView) => step.id;
const defaultPosition = (index: number) => ({ x: index * 300, y: 30 });

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
      <div className="flex flex-wrap gap-1 mt-2 text-[10px]" style={{ color: 'var(--sem-accent)' }}>
        {step.when && <span>Condition</span>}
        {step.forEach && <span>Repeat for each {step.forEach.as || 'item'}</span>}
        {step.call && <span>Call {step.call.workflow}</span>}
        {!step.when && !step.forEach && !step.call && <span>{step.ops.length} actions</span>}
      </div>
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  );
}

const nodeTypes = { workflowStep: WorkflowStepNode };

function Canvas({ steps, storageKey, workflowName, selected, onSelect }: {
  steps: BoxesStepView[]; storageKey: string; workflowName?: string; selected: number; onSelect: (index: number) => void;
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
  stepsRef.current = steps;
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
    setNodes(current => steps.map((step, index) => {
      const existing = current.find(node => node.id === nodeId(step));
      return { id: nodeId(step), type: 'workflowStep', data: { step },
        position: existing?.position ?? defaultPosition(index),
        ariaLabel: `Step ${step.number}: ${step.title || 'Untitled'}` };
    }));
  }, [steps, setNodes]);

  const edges = useMemo(() => steps.slice(1).map((step, index) => ({
    id: `order:${index}`, source: nodeId(steps[index]), target: nodeId(step),
    type: 'smoothstep', label: 'file order',
    ariaLabel: `File order: step ${steps[index].number} to step ${step.number}`,
    markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--sem-muted)' },
    style: { stroke: 'var(--sem-muted)' },
    labelStyle: { fill: 'var(--sem-muted)', fontSize: 10 },
    labelBgStyle: { fill: 'var(--sem-surface-2)' },
  })), [steps]);

  const arrange = () => {
    const arranged = nodes.map((node, index) => ({ ...node, position: defaultPosition(index) }));
    setNodes(arranged); remember(arranged);
    requestAnimationFrame(() => { void fitView({ padding: 0.15, maxZoom: 1, duration: 200 }); });
  };

  return (
    <div className="rounded-xl border overflow-hidden mb-3" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-3 px-3 py-2 text-[11px]" style={{ background: 'var(--sem-surface)' }}>
        <span className="flex-1" style={{ color: 'var(--sem-muted)' }}>{loading ? 'Loading shared layout…' : 'Drag steps to arrange. Select a step to read it below.'}</span>
        <button type="button" disabled={loading} onClick={arrange} className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Arrange</button>
        <button type="button" disabled={loading} onClick={() => {
          setNodes(nodes.map((node, index) => ({ ...node, position: defaultPosition(index) })));
          if (session.current) void session.current.save({}); else saveState(storageKey, {});
        }} className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Reset</button>
        <button type="button" onClick={() => { void fitView({ padding: 0.15, maxZoom: 1, duration: 200 }); }}
          className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Fit all</button>
      </div>
      {saveError && <div role="alert" className="px-3 py-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>
        Layout not saved: {saveError} Your positions are still shown.{' '}
        <button type="button" onClick={() => { void session.current?.refresh(); }} className="underline">Reload shared layout</button>
      </div>}
      <div style={{ height: 340, background: 'var(--sem-surface-2)' }} data-wf-canvas="true">
        <ReactFlow<StepNode> nodes={nodes.map((node) => ({ ...node, selected: node.data.step.order === selected }))}
          edges={edges} nodeTypes={nodeTypes} onNodesChange={onNodesChange}
          onNodeClick={(_, node) => onSelect(node.data.step.order)}
          onNodeDragStop={(_, node) => remember(nodes.map((item) => item.id === node.id ? node : item))}
          nodesDraggable={!loading}
          nodesConnectable={false} edgesReconnectable={false} edgesFocusable={false}
          deleteKeyCode={null} multiSelectionKeyCode={null} selectionOnDrag={false}
          fitView fitViewOptions={{ padding: 0.15, maxZoom: 1 }} minZoom={0.1} maxZoom={1.8}
          proOptions={{ hideAttribution: true }}>
          <Background color="var(--sem-border)" gap={20} />
          <Controls showInteractive={false} />
        </ReactFlow>
      </div>
    </div>
  );
}

export function WorkflowCanvas(props: Parameters<typeof Canvas>[0]) {
  return <ReactFlowProvider><Canvas {...props} /></ReactFlowProvider>;
}
