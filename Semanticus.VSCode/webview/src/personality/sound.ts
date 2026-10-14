// The sound palette (1.5, design page section 4). Every sound is made live with the Web Audio API: no sound files.
// One API: play('snap' | 'chime' | 'pop' | 'tick'). Callers call it freely; the rules live here, so no caller has to know them:
//   1. Sounds start off (semanticus.sounds). Personality Quiet mutes every sound.
//   2. Nothing plays before the person has clicked or typed in this page (browsers refuse audio before a gesture anyway, and an
//      AudioContext made too early sits suspended and warns).
//   3. No two sounds within 300 ms, except bulk ticks. Ticks are capped at 25 a second.
//   4. Silent when the VS Code window is not in focus.
//   5. No sound for an error, a warning, a delete or a question: the palette has no sound for them, so a caller cannot ask for one.
// The voices, levels and timings are the page's, copied exactly. One volume for all (default 40%); the tick's own peak is half of a pop's.
import { getPersonalitySettings, onPersonalitySettingsChange } from './settings';

export type SoundName = 'snap' | 'chime' | 'pop' | 'tick';
/** k: 0 to 1, how far a bulk count has got; the tick climbs in pitch with it. */
export type SoundOptions = { k?: number };

type Voice = (c: AudioContext, out: AudioNode, t: number, opts: SoundOptions) => void;

function env(g: GainNode, t: number, a: number, peak: number, d: number) {
  g.gain.setValueAtTime(0.0001, t);
  g.gain.exponentialRampToValueAtTime(peak, t + a);
  g.gain.exponentialRampToValueAtTime(0.0001, t + a + d);
}
function osc(c: AudioContext, out: AudioNode, type: OscillatorType, f0: number, f1: number, t: number, dur: number, a: number, peak: number) {
  const o = c.createOscillator(), g = c.createGain();
  o.type = type;
  o.frequency.setValueAtTime(f0, t);
  if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + dur);
  env(g, t, a, peak, dur);
  o.connect(g); g.connect(out);
  o.start(t); o.stop(t + a + dur + 0.05);
}
function noise(c: AudioContext, out: AudioNode, t: number, dur: number, peak: number, type: BiquadFilterType, f0: number, f1: number, q: number) {
  const n = Math.ceil(c.sampleRate * (dur + 0.05)), buf = c.createBuffer(1, n, c.sampleRate), d = buf.getChannelData(0);
  for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
  const s = c.createBufferSource(); s.buffer = buf;
  const f = c.createBiquadFilter(); f.type = type; f.Q.value = q;
  f.frequency.setValueAtTime(f0, t); f.frequency.exponentialRampToValueAtTime(f1, t + dur);
  const g = c.createGain(); env(g, t, 0.003, peak, dur);
  s.connect(f); f.connect(g); g.connect(out);
  s.start(t); s.stop(t + dur + 0.05);
}

export const VOICES: Record<SoundName, Voice> = {
  /** Relationship snap: a short, woody click with a small drop in pitch, like a plug going into a socket. */
  snap(c, o, t) {
    noise(c, o, t, 0.03, 0.45, 'bandpass', 3400, 1800, 1.2);
    osc(c, o, 'triangle', 1400, 600, t, 0.07, 0.002, 0.32);
    osc(c, o, 'sine', 230, 170, t + 0.004, 0.09, 0.002, 0.25);
  },
  /** Publish chime: two bell notes a fifth apart, rising. It lands when the S lands on its end dot. */
  chime(c, o, t) {
    for (const [f, d] of [[784, 0], [1175, 0.14]]) {
      osc(c, o, 'sine', f, f, t + d, 1.3, 0.006, 0.3);
      osc(c, o, 'sine', f * 2.01, f * 2.01, t + d, 0.5, 0.004, 0.06);
      osc(c, o, 'sine', f * 2.76, f * 2.76, t + d, 0.25, 0.003, 0.03);
    }
  },
  /** Grade A pop: a round pop that rises in pitch, then a tiny bright echo. */
  pop(c, o, t) {
    osc(c, o, 'sine', 320, 920, t, 0.09, 0.004, 0.55);
    osc(c, o, 'sine', 1240, 1520, t + 0.08, 0.08, 0.003, 0.16);
  },
  /** Bulk tick: soft ticks that climb in pitch as the count climbs. */
  tick(c, o, t, { k = 0 }) {
    const f = 1500 * (1 + Math.min(1, Math.max(0, k)) * 0.6);
    osc(c, o, 'triangle', f, f * 0.92, t, 0.025, 0.001, 0.08);
  },
};

export const SPACING_MS = 300;
export const TICK_SPACING_MS = 40;

/** The gate, pure so a test can drive it: may this sound play now? Updates `last` when it says yes. */
export function mayPlay(name: SoundName, now: number, s: { sound: boolean; personality: string }, gesture: boolean, focused: boolean,
  last: { any: number; tick: number }): boolean {
  if (!s.sound || s.personality === 'quiet' || !gesture || !focused) return false;
  if (name === 'tick') {
    if (now - last.tick < TICK_SPACING_MS) return false;
    last.tick = now;
    return true;
  }
  if (now - last.any < SPACING_MS) return false;
  last.any = now;
  return true;
}

let ctx: AudioContext | null = null;
let master: GainNode | null = null;
let gesture = false;
const last = { any: -Infinity, tick: -Infinity };

if (typeof window !== 'undefined') {
  const heard = () => {
    gesture = true;
    window.removeEventListener('pointerdown', heard, true);
    window.removeEventListener('keydown', heard, true);
  };
  window.addEventListener('pointerdown', heard, true);
  window.addEventListener('keydown', heard, true);
}

function ensure(): AudioContext | null {
  if (!ctx) {
    const C = (window as unknown as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext }).AudioContext
      ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!C) return null;
    ctx = new C();
    master = ctx.createGain();
    master.gain.value = getPersonalitySettings().volume;
    const comp = ctx.createDynamicsCompressor();
    master.connect(comp); comp.connect(ctx.destination);
    onPersonalitySettingsChange((s) => {
      if (master) master.gain.value = s.volume;
      // Sound off or Quiet: let the audio thread sleep rather than hold the device open.
      if (ctx && (!s.sound || s.personality === 'quiet') && ctx.state === 'running') void ctx.suspend();
    });
  }
  if (ctx.state === 'suspended') void ctx.resume();
  return ctx;
}

/** Play one sound from the palette, if the rules allow it now. Never throws. */
export function play(name: SoundName, opts: SoundOptions = {}): void {
  try {
    const focused = typeof document === 'undefined' || document.hasFocus();
    if (!mayPlay(name, performance.now(), getPersonalitySettings(), gesture, focused, last)) return;
    const c = ensure();
    if (!c || !master) return;
    VOICES[name](c, master, c.currentTime + 0.01, opts);
  } catch { /* a sound is never worth an error */ }
}
