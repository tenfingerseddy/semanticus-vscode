// The engine writes its notes for two readers: the person's assistant, which needs the parameter name, and the diagnostics log. A person reading the Query
// page needs neither (hero 24 D8: "needs confirm=true", "[trace: sid=set spid=678939 warmup=2185ms raw=1]"). This turns the known ones into plain words
// and leaves every other note exactly as the engine wrote it.

const SHARED_BOX = 'Shared endpoint: clearing the cache affects all users';

/** A note split in two: the words a person reads, and the trace line that belongs in the log only (UAT F30). */
export function splitEngineNote(note: string | null | undefined): { text: string; technical: string } {
  const t = (note ?? '').trim();
  const at = t.search(/\[(?:trace|sid)[^\]]*\]/);
  if (at < 0) return { text: t, technical: '' };
  return { text: t.slice(0, at).trim(), technical: t.slice(at).trim() };
}

/** A note from a speed or plan run, in the words a person reads. */
export function plainEngineNote(note: string | null | undefined): string {
  let t = (note ?? '').trim();
  if (!t) return '';
  t = t.replace(/\s*\[(?:trace|sid)[^\]]*\]/g, '').trim();   // diagnostics, kept in the log and under Technical details
  if (/^No query plan captured\b/i.test(t)) return 'No query plan came back.';
  if (/needs confirm=true/i.test(t)) {
    const rest = t.replace(/^Cold runs skipped:[^.]*\.\s*(?:Showing warm-only\.\s*)?/i, '').trim();
    const more = noJargon(rest);
    return `Cold runs were skipped, so these are repeat runs only. To include them, tick "${SHARED_BOX}" and measure again.${more ? ' ' + more : ''}`;
  }
  // A note that is all engine talk ("Warm-only ... Storage engine 0 ms ...") is never shown as the engine wrote it: it becomes one plain line.
  const plain = noJargon(t);
  if (plain) return plain;
  return /warm-only|cold runs skipped|cache was not cleared/i.test(t) ? 'The cache was not cleared, so these are repeat runs only.' : 'The server gave no more detail about this run.';
}

/** Sentences about the engines and the trace mean nothing to a person (UAT 18 Oct, N12). */
function noJargon(t: string): string {
  return t.split(/(?<=\.)\s+/).filter((x) => !/(storage|formula) engine|trace missed|warm-only/i.test(x)).join(' ').trim();
}

/** A refused "clear kept results", in the words a person reads. A shared server needs the tick first; anything else is shown as the engine said it. */
export function plainClearRefusal(error: string | null | undefined): string {
  const t = (error ?? '').trim();
  if (!t) return 'The kept results were not cleared.';
  if (/confirm\s*=\s*true|shared/i.test(t)) return `Not cleared. This server is shared, so clearing would slow it for everyone using it. Tick "${SHARED_BOX}" to go ahead.`;
  return t;
}
