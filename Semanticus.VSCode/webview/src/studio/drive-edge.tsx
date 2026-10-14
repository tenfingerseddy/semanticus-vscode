import { useEffect, useState, type CSSProperties } from 'react';
import { useAssistantWorking } from './assistant-working';
import { useDriver, type DriverSnapshot } from './driver-store';

/** The 2px purple line round the Studio while the assistant works and drives: the state the driver bar and its tile show, never a timer of its own. */
export function edgeIsOn(driver: DriverSnapshot, working: boolean): boolean {
  return working && driver.assistantConnected && driver.mode === 'claudeDrives' && driver.state !== 'personHasWheel';
}

export type EdgePhase = 'off' | 'on' | 'fade';
/** off to on fades the line in; on to off fades it out; a fade already under way keeps going when the wheel is handed back again. */
export function nextEdgePhase(phase: EdgePhase, on: boolean): EdgePhase {
  if (on) return 'on';
  return phase === 'on' ? 'fade' : phase;
}

const frame: CSSProperties = { position: 'fixed', inset: 0, pointerEvents: 'none' };

export function DriveEdgeView({ phase, runKey = 0, onFaded }: { phase: EdgePhase; runKey?: number; onFaded?: () => void }) {
  if (phase === 'off') return null;
  return <div key={runKey} className="sc-edge" data-testid="studio-drive-edge" data-edge={phase} aria-hidden="true" style={frame}
    onAnimationEnd={phase === 'fade' ? onFaded : undefined} />;
}

const reduced = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches || document.body.classList.contains('vscode-reduce-motion');

export function DriveEdge() {
  const driver = useDriver();
  const on = edgeIsOn(driver, useAssistantWorking(driver));
  const [state, setState] = useState<{ phase: EdgePhase; runKey: number }>({ phase: 'off', runKey: 0 });
  // Only a change of the state does any work here; while it stays put nothing runs.
  useEffect(() => {
    setState((s) => {
      const phase = nextEdgePhase(s.phase, on);
      if (phase === s.phase) return s;
      if (phase === 'on') return { phase, runKey: s.runKey + 1 };
      // Reduced motion has no fade: the still line is simply taken away.
      return { phase: reduced() ? 'off' : phase, runKey: s.runKey };
    });
  }, [on]);
  return <DriveEdgeView {...state} onFaded={() => setState((s) => s.phase === 'fade' ? { phase: 'off', runKey: s.runKey } : s)} />;
}
