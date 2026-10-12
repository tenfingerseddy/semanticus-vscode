import type { CSSProperties } from 'react';
import { AssistantSign } from './assistant-sign';
import { useAssistantWorking } from './assistant-working';
import { assistantIsWorking, useDriver, type DriverSnapshot, type DriveMode } from './driver-store';
import { useLearn } from './learn-state';

const row: CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap',
  gap: 'var(--sem-gap)', padding: 'var(--sem-space-2) var(--sem-page-margin)',
  borderBottom: '1px solid var(--sem-border)', flexShrink: 0,
};

export function DriverBarView({ driver, onMode, onResume, touring = false, working = assistantIsWorking(driver, Date.now()) }: {
  driver: DriverSnapshot; onMode: (mode: DriveMode) => void; onResume: () => void; touring?: boolean; working?: boolean;
}) {
  if (touring) return <div data-testid="studio-driver-bar" data-studio-driver-bar style={row}><span role="status">The tour is playing.</span></div>;
  if (!driver.assistantConnected) return null;
  // The connection file is written but no assistant has started: say so, and do not claim Claude is driving.
  const waiting = driver.mode === 'claudeDrives' && !driver.assistantWorking;
  const person = !waiting && driver.mode === 'claudeDrives' && driver.state === 'personHasWheel';
  const color = driver.mode === 'iDrive' || waiting ? 'var(--sem-muted)'
    : person ? 'var(--sem-doer-you)' : 'var(--sem-doer-agent)';
  // Connected and idle: the sign is still and the words are quiet, like the sign.
  const connectedIdle = driver.mode === 'claudeDrives' && !waiting && !person && !working;
  // The name the engine read from the assistant's own connection (Claude Code, Codex, ...); "Your assistant" until it has.
  const name = driver.assistant?.name ?? 'Your assistant';
  // The buttons name who drives: "Claude" for Claude Code and Claude Desktop alike, else the assistant's own name.
  const driverName = driver.assistant?.key === 'claude' ? 'Claude' : name;
  const words = driver.mode === 'iDrive' ? `I drive. ${name} works but does not move your screen.`
    : waiting ? 'Waiting for your assistant. Open Claude Code in this folder to begin.'
    : person ? `You have the wheel. Your next message to ${name} gives it back.`
    : working ? `${name} is working. Click anything to take the wheel.` : `${name} is connected.`;
  return <div data-testid="studio-driver-bar" data-studio-driver-bar style={row}>
    <div className="flex items-center flex-wrap gap-2 min-w-0">
      {/* The sign shows once an assistant has really called, and never while the bar only waits for one. */}
      {driver.assistantWorking && <AssistantSign who={driver.assistant} working={working} />}
      <span role="status" style={{ color: connectedIdle ? 'var(--sem-muted)' : color }}>{words}</span>
      {person && <button type="button" className="sem-btn sem-btn-sm" data-testid="driver-resume" onClick={onResume}>Let {driverName === 'Your assistant' ? 'your assistant' : driverName} drive</button>}
    </div>
    <div role="group" aria-label="Who moves the screen" className="sem-seg shrink-0">
      {(['claudeDrives', 'iDrive'] as const).map((mode) => <button type="button" key={mode}
        className="sem-seg-item" data-testid={`driver-mode-${mode}`}
        aria-pressed={driver.mode === mode} onClick={() => onMode(mode)}
        // The one that is on is never grey, and carries a ring in its own colour, so I drive shows as clearly as Claude drives.
        style={driver.mode === mode
          ? { color: mode === 'iDrive' ? 'var(--sem-fg)' : waiting ? 'var(--sem-fg)' : color, background: 'var(--sem-surface-2)', boxShadow: 'inset 0 0 0 1px currentColor' }
          : { color: 'var(--sem-muted)' }}>
        {mode === 'claudeDrives' ? `${driverName} drives` : 'I drive'}
      </button>)}
    </div>
  </div>;
}

export function DriverBar({ onMode, onResume }: {
  onMode: (mode: DriveMode) => void; onResume: () => void;
}) {
  const { runtime } = useLearn();
  const driver = useDriver();
  const working = useAssistantWorking(driver);
  return <DriverBarView driver={driver} working={working} touring={runtime.phase === 'running' || runtime.phase === 'starting'} onMode={onMode} onResume={onResume} />;
}
