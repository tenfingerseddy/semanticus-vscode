import { useLayoutEffect } from 'react';
import { DriverBar } from './driver-bar';
import { driverStore, type DriveMode } from './driver-store';
import { onAssistantCall, onDriveMode, onReconnect, onStudioAnswer, onStudioShow, rpc } from '../bridge';
import { setAssistantAnswer } from './answer-store';
import { onPersonInput, postDriverMessage, setDriveMode } from './driver-wire';
import { NoteStrip } from './note-strip';
import { StudioCursor } from './cursor';
import { DriveEdge } from './drive-edge';
import { usePlanChrome } from '../plan-chrome';

export function isPersonInput(target: EventTarget | null): boolean {
  return target instanceof Element && !!target.closest('.studio-root')
    && !target.closest('[data-studio-driver-bar], [data-studio-note-strip]');
}

// The host says the drive mode once per page load (studioReady). A new model remounts this layer under App's session-keyed
// provider, so whether the mode has arrived lives here, not in the mount: a remounted layer still reports the wheel (hero15 H3).
let modeHeard = false;

/** Who is attached, read from the engine (the entitlement answer carries it, on both doors). A Studio that opens after the assistant's
 *  first call learns the name here; a live call carries it too. An engine one release behind has no such field, and then there is no name. */
async function loadAssistant(): Promise<void> {
  try { driverStore.setAssistant((await rpc<{ assistant?: unknown }>('getEntitlement'))?.assistant); } catch { /* no engine yet, or an older one */ }
}

export function StudioLayer() {
  // Layout effects install these before App's studioReady effect can flush buffered messages.
  useLayoutEffect(() => {
    let lastReport = '';
    const report = () => {
      if (!modeHeard) return;
      const { mode, state, lastPersonInput } = driverStore.getSnapshot();
      const key = `${mode}/${state}/${lastPersonInput}`;
      if (key === lastReport) return;
      lastReport = key;
      postDriverMessage({ type: 'driverState', mode, state, lastPersonInput });
    };
    const off = [
      driverStore.subscribe(report),
      onDriveMode((mode, assistantConnected) => {
        driverStore.setMode(mode);
        driverStore.setConnected(assistantConnected ?? mode === 'claudeDrives');
        modeHeard = true;
        report();
      }),
      onAssistantCall((call) => driverStore.noteAssistantCall(call.at, call.assistant)),
      onReconnect(() => { void loadAssistant(); }),
      onStudioShow((note) => { if (!note.update) driverStore.noteShow(); }),
      onStudioAnswer((answer) => setAssistantAnswer(answer)),
      onPersonInput((at) => driverStore.notePersonInput(at)),
    ];
    void loadAssistant();
    const input = (event: Event) => { if (isPersonInput(event.target)) driverStore.notePersonInput(); };
    document.addEventListener('pointerdown', input, true);
    document.addEventListener('keydown', input, true);
    report();
    return () => {
      off.forEach((unsubscribe) => unsubscribe());
      document.removeEventListener('pointerdown', input, true);
      document.removeEventListener('keydown', input, true);
    };
  }, []);
  // The Plan page folds the driver bar into its own header (1.5, pick C): same switch, same store, one place on that page.
  const calm = usePlanChrome().calm;
  const mode = (next: DriveMode) => setDriveMode(next);
  return <>
    {!calm && <DriverBar onMode={mode} onResume={driverStore.letClaudeDrive} />}
    <div data-studio-note-strip><NoteStrip /></div>
    <StudioCursor />
    <DriveEdge />
  </>;
}
