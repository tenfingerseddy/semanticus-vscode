import { useEffect, useRef, useState } from 'react';
import { firstRunClose, firstRunConnect, onFirstRunSay, rpc } from './bridge';
import { ASSISTANT_PULSE_EVENT, firstRunOnce, holdForPulse, markFirstRunClosed, pulsesAssistantChip, returnsToCard } from './firstrun-gate';
import { startTour, useLearn } from './studio/learn-state';
import { LinkMark, type LinkState } from './personality/link';
import './firstrun.css';

// The first-run choice (1.4, direction decision 10). The host decides whether to show it (once per machine, and
// never in a folder that is already wired); this file draws it and runs the press. It replaces the old
// "Connect your assistant?" toast. UI only for the choice itself: the assistant side of a connection is the
// connection file and the skill, which one press of Connect Claude Code writes.

/** The first thing to say to Claude Code. A real first job for this product, in the person's own words. */
export const FIRST_SAY = 'Build a model from my SQL endpoint and get it AI ready.';

/** With a model already open the first thing to say is about that model, not about building one from nothing. */
export const FIRST_SAY_OPEN_MODEL = 'What are the top numbers in the model I have open?';
export const firstSay = (modelOpen: boolean): string => (modelOpen ? FIRST_SAY_OPEN_MODEL : FIRST_SAY);

export type FirstRunStage = 'choose' | 'working' | 'done';
export type FirstRunMode = 'assistant' | 'hand';

/** The Link at full size on the welcome card (1.5): it breathes while you choose, works while the connect runs, links up when it is
 *  done and then rests, and turns grey and still when the connect failed. */
export function firstRunLinkState(stage: FirstRunStage, error: string | null, settled: boolean): LinkState {
  if (stage === 'working') return 'working';
  if (stage === 'done') return settled ? 'idle' : 'success';
  return error ? 'error' : 'idle';
}

function FirstRunLink({ stage, error }: { stage: FirstRunStage; error: string | null }) {
  const [settled, setSettled] = useState(false);
  useEffect(() => {
    setSettled(false);
    if (stage !== 'done') return undefined;
    const t = setTimeout(() => setSettled(true), 1900);
    return () => clearTimeout(t);
  }, [stage]);
  return <div className="first-run-link"><LinkMark state={firstRunLinkState(stage, error, settled)} size={200} /></div>;
}

export function FirstRunCard({ stage, mode, onMode, onConnect, onClose, onAround, error, note, needsFolder, onChooseFolder, say = FIRST_SAY, folder }: {
  stage: FirstRunStage; mode: FirstRunMode; onMode: (m: FirstRunMode) => void; onConnect: () => void; onClose: (as?: FirstRunMode) => void; onAround?: () => void;
  error: string | null; note: string | null;
  /** The press could not use a folder: the main button asks for one instead of pressing the same thing again. */
  needsFolder?: boolean; onChooseFolder?: () => void;
  /** The first thing to say to the assistant (firstSay). */
  say?: string;
  /** The folder to open Claude Code in: how it reads (~/Semanticus/Model) and the full path the copy press puts on the clipboard. */
  folder?: { shown: string; path: string };
}) {
  const pressRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { pressRef.current?.focus({ preventScroll: true }); }, [stage]);
  const [copied, setCopied] = useState(false);
  const [copiedFolder, setCopiedFolder] = useState(false);
  const copyFolder = () => { try { void navigator.clipboard?.writeText(folder?.path ?? ''); setCopiedFolder(true); } catch { /* the path is on screen to type */ } };
  const copy = () => {
    try { void navigator.clipboard?.writeText(say); setCopied(true); } catch { /* the line is on screen to type */ }
  };
  if (stage === 'done') {
    return <div className="first-run-scrim"><div className="first-run" role="dialog" aria-label="Ready for your assistant"
      onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); onClose(); } }}>
      <FirstRunLink stage={stage} error={error} />
      <h2 className="first-run-title">Ready for your assistant</h2>
      {folder
        ? <>
          <p className="first-run-line">Open Claude Code in this folder:</p>
          <div className="first-run-say"><span data-testid="first-run-folder">{folder.shown}</span>
            <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="first-run-folder-copy" onClick={copyFolder}>{copiedFolder ? 'Copied' : 'Copy'}</button></div>
          <p className="first-run-line">Then say:</p>
        </>
        : <p className="first-run-line">Open Claude Code in this folder and say:</p>}
      <div className="first-run-say"><span>{say}</span>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={copy}>{copied ? 'Copied' : 'Copy'}</button></div>
      <p className="first-run-note">Claude Code reads new tools when it starts. If it is already open in this folder, restart it first. Keep VS Code open.</p>
      {note && <p className="first-run-note" role="status">{note}</p>}
      <div className="first-run-acts"><button ref={pressRef} type="button" className="sem-btn sem-btn-primary" onClick={() => onClose()}>Done</button></div>
    </div></div>;
  }
  const choices: { id: FirstRunMode; title: string; line: string; tag?: string }[] = [
    { id: 'assistant', title: 'With your assistant', tag: 'recommended', line: 'Tell your assistant what you want. Semanticus shows each step. You can click anything at any time.' },
    { id: 'hand', title: 'By hand', line: "Use the Studio's buttons. You can connect your assistant later from the header." },
  ];
  const working = stage === 'working';
  // With By hand chosen the person has said what they want, so pressing outside the card finishes it.
  const scrimCloses = mode === 'hand' && !working;
  return <div className="first-run-scrim" data-scrim-closes={scrimCloses ? 'true' : undefined}
    onMouseDown={(e) => { if (scrimCloses && e.target === e.currentTarget) onClose(); }}><div className="first-run" role="dialog" aria-label="How do you want to work?"
    onKeyDown={(e) => { if (e.key === 'Escape' && !working) { e.preventDefault(); onClose(); } }}>
    <FirstRunLink stage={stage} error={error} />
    <h2 className="first-run-title">How do you want to work?</h2>
    <div className="first-run-choices" role="radiogroup" aria-label="How do you want to work?">
      {choices.map((c) => <button key={c.id} type="button" role="radio" aria-checked={mode === c.id} disabled={working}
        className={`first-run-choice${mode === c.id ? ' is-on' : ''}`} onClick={() => onMode(c.id)}>
        <span className="first-run-choice-title">{c.title}{c.tag && <span className="first-run-tag">{c.tag}</span>}</span>
        <span className="first-run-choice-line">{c.line}</span>
      </button>)}
    </div>
    {error && <p className="first-run-error" role="alert">{error}</p>}
    <div className="first-run-acts">
      {onAround && <button type="button" className="sem-btn sem-btn-quiet first-run-around" disabled={working} onClick={onAround}>Show me around (3 minutes)</button>}
      {!working && <button type="button" className="sem-btn sem-btn-quiet" onClick={() => onClose('hand')}>Not now</button>}
      {mode === 'assistant' && needsFolder && onChooseFolder
        ? <button ref={pressRef} type="button" className="sem-btn sem-btn-primary" disabled={working} onClick={onChooseFolder}>Choose a folder</button>
        : mode === 'assistant'
        ? <button ref={pressRef} type="button" className="sem-btn sem-btn-primary" disabled={working} onClick={onConnect}>{working ? 'Connecting' : 'Connect Claude Code'}</button>
        : <button ref={pressRef} type="button" className="sem-btn sem-btn-primary" onClick={() => onClose()}>Start working</button>}
    </div>
  </div></div>;
}

/** Mounted once on the Studio. Asks the host, shows the choice if the host says to, and runs the press. */
export function FirstRun() {
  const [stage, setStage] = useState<FirstRunStage | null>(null);
  const [mode, setMode] = useState<FirstRunMode>('assistant');
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  // After a press that could not use a folder, the main button asks for one.
  const [needsFolder, setNeedsFolder] = useState(false);
  // The card has been on screen in this session. A tour from it then never brings it back.
  const seen = useRef(false);
  // Whether the person has connected, and whether they went to the tour from this card. Leave then brings the card back.
  const connected = useRef(false);
  const fromCard = useRef(false);
  const phase = useLearn().runtime.phase;
  const [modelOpen, setModelOpen] = useState(false);
  const [folder, setFolder] = useState<{ shown: string; path: string } | undefined>(undefined);
  // Read when the say card shows, so it fits what is open then (a model may have come back after a reload).
  useEffect(() => {
    if (stage !== 'done') return undefined;
    let alive = true;
    void rpc<{ sessionId?: string | null }>('sessionInfo').then((i) => { if (alive) setModelOpen(!!i?.sessionId); }, () => undefined);
    return () => { alive = false; };
  }, [stage]);
  useEffect(() => {
    let alive = true;
    void firstRunOnce().then((s) => { if (!alive) return; connected.current = !!s?.connected;
      if (s?.folder && s.folderPath) setFolder({ shown: s.folder, path: s.folderPath });
      if (s?.say) { seen.current = true; setNote(s.note ?? null); setStage('done'); }   // a connect finished while the window reloaded: say what to type next
      else if (s?.show) { seen.current = true; setStage('choose'); } });
    // The header and the palette press the same Connect. The host asks for the say card when it is done.
    const off = onFirstRunSay((n) => { connected.current = true; setNote(n ?? null); setStage('done'); });
    return () => { alive = false; off(); };
  }, []);
  useEffect(() => {
    const exit = { fromCard: fromCard.current, phase, connected: connected.current, open: stage !== null, seen: seen.current };
    if (returnsToCard(exit)) { fromCard.current = false; seen.current = true; setStage('choose'); }
    else if (pulsesAssistantChip(exit)) { fromCard.current = false; holdForPulse(); window.dispatchEvent(new CustomEvent(ASSISTANT_PULSE_EVENT)); }
  }, [phase, stage]);
  if (!stage) return null;
  const connect = (pickFolder = false) => {
    setError(null); setStage('working');
    void firstRunConnect(pickFolder).then((r) => {
      if (r.ok) { connected.current = true; setNeedsFolder(false); setNote(r.note ?? null); setStage('done'); }
      else { setNeedsFolder(r.needsFolder === true); setError(r.error ?? 'Semanticus could not connect your assistant.'); setStage('choose'); }
    });
  };
  // Done after a connect needs no flag (the host wrote it); By hand writes it here. Either way the card goes.
  const close = (as?: FirstRunMode) => { if (stage === 'choose') firstRunClose(as ?? mode); setStage(null); markFirstRunClosed(); };
  // Show me around closes the card with nothing chosen (the host writes the flag only) and starts the Plan tour.
  const around = () => { fromCard.current = true; firstRunClose('tour'); setStage(null); markFirstRunClosed(); startTour('plan'); };
  return <FirstRunCard stage={stage} mode={mode} onMode={setMode} onConnect={() => connect()} onChooseFolder={() => connect(true)} needsFolder={needsFolder} say={firstSay(modelOpen)} folder={folder} onClose={close} onAround={around} error={error} note={note} />;
}
