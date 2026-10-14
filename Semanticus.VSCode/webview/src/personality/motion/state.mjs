// Motion changes presentation only. Callers supply completed engine facts.
export function motionPolicy({ personality = 'on', reducedMotion = false, visible = true, sound = false } = {}) {
  const quiet = personality === 'quiet';
  return { quiet, moving: visible && !reducedMotion, sparkle: visible && !reducedMotion && !quiet,
    sound: visible && sound && !quiet, duration: reducedMotion || !visible ? 0 : quiet ? 160 : 1100 };
}

export function countAt(from, to, progress) {
  const k = Math.max(0, Math.min(1, progress));
  return k === 1 ? to : Math.round(from + (to - from) * (1 - Math.pow(1 - k, 3)));
}

export function gradeFrames(from, to) {
  const grades = ['F', 'D', 'C', 'B', 'A'];
  const a = grades.indexOf(from), b = grades.indexOf(to);
  if (a < 0 || b < 0 || a === b) return [to];
  const step = b > a ? 1 : -1;
  return Array.from({ length: Math.abs(b - a) + 1 }, (_, i) => grades[a + step * i]);
}

export function settleChoices(choices) {
  // Keep ties stable. A confidence is not proof that Jev selected the first row.
  return choices.map((choice, index) => ({ ...choice, index }))
    .sort((a, b) => b.confidence - a.confidence || a.index - b.index);
}

export function isBigWin(kind, { ok = true, production = false, destructive = false } = {}) {
  return ok && !production && !destructive && ['grade-a', 'zero-findings', 'first-publish'].includes(kind);
}

export function historyTransition(previous, current) {
  const was = new Map(previous.map(row => [row.id, row]));
  const changed = current.filter(row => was.has(row.id) && !!was.get(row.id).undone !== !!row.undone);
  if (!changed.length || (!changed.every(row => row.undone) && !changed.every(row => !row.undone))) return null;
  const latest = rows => {
    const active = rows.filter(row => !row.undone && row.revision != null);
    return active.length ? Math.max(...active.map(row => row.revision)) : undefined;
  };
  return { changes: [...changed].sort((a, b) => (a.revision ?? 0) - (b.revision ?? 0)),
    direction: changed[0].undone ? 'undo' : 'redo', fromRevision: latest(previous), toRevision: latest(current) };
}

/** One finite clock. Hidden/reduced state finishes it; no background timers. */
export function runFrames({ duration, frame, requestFrame, cancelFrame, now }) {
  let id, stopped = false;
  const start = now();
  const stop = () => { stopped = true; if (id !== undefined) cancelFrame(id); };
  const finish = () => { if (stopped) return; stop(); frame(1); };
  if (duration <= 0) { finish(); return { stop, finish }; }
  frame(0);
  const step = (time) => {
    if (stopped) return;
    const progress = Math.min(1, Math.max(0, (time - start) / duration));
    frame(progress);
    if (progress < 1 && !stopped) id = requestFrame(step);
    else stopped = true;
  };
  id = requestFrame(step);
  return { stop, finish };
}
