// Personality settings (1.5, The Link lane). One store the three personality lanes read: the Link and the working verbs, the voice
// (messages) and the motion touches. The values live in the person's VS Code settings, so they follow the person, not the model:
//   semanticus.personality  "on" | "quiet"  (default "on")
//   semanticus.sounds       boolean         (default false: a modelling tool sits next to music, calls and open offices)
//   semanticus.soundVolume  0 to 100        (default 40)
// Motion is not a Semanticus setting. It follows VS Code's workbench.reduceMotion, whose default "auto" follows the OS.
// The host posts 'personalitySettings' when the Studio is ready and whenever one of those settings changes; a change made on the
// page posts 'setPersonalitySetting' and the host writes the user setting, which comes back here as the next 'personalitySettings'.
import { useSyncExternalStore } from 'react';

export type Personality = 'on' | 'quiet';
export type PersonalitySettings = {
  personality: Personality;
  sound: boolean;
  /** 0 to 1. The page's slider is a percentage; every sound plays at this, ticks at half of it. */
  volume: number;
  /** True when motion should be cut to short fades: VS Code says so, or VS Code says auto and the OS asks for less motion. */
  reducedMotion: boolean;
};
export type ReduceMotionSetting = 'on' | 'off' | 'auto';

export const DEFAULT_PERSONALITY_SETTINGS: PersonalitySettings = { personality: 'on', sound: false, volume: 0.4, reducedMotion: false };

/** What the host posts. Every field is optional so an older host, or a test, can send part of it. */
export type PersonalitySettingsMessage = {
  type: 'personalitySettings'; personality?: unknown; sounds?: unknown; soundVolume?: unknown; reduceMotion?: unknown;
};

export function readMessage(msg: PersonalitySettingsMessage): { personality: Personality; sound: boolean; volume: number; reduceMotion: ReduceMotionSetting } {
  const personality: Personality = msg.personality === 'quiet' ? 'quiet' : 'on';
  const sound = msg.sounds === true;
  const n = Number(msg.soundVolume);
  const volume = Number.isFinite(n) ? Math.min(100, Math.max(0, n)) / 100 : DEFAULT_PERSONALITY_SETTINGS.volume;
  const reduceMotion: ReduceMotionSetting = msg.reduceMotion === 'on' || msg.reduceMotion === 'off' ? msg.reduceMotion : 'auto';
  return { personality, sound, volume, reduceMotion };
}

export function resolveReduced(setting: ReduceMotionSetting, osReduced: boolean): boolean {
  return setting === 'on' || (setting === 'auto' && osReduced);
}

export function createPersonalityStore(osReducedNow: () => boolean = () => false) {
  let reduceMotion: ReduceMotionSetting = 'auto';
  let settings: PersonalitySettings = { ...DEFAULT_PERSONALITY_SETTINGS, reducedMotion: resolveReduced('auto', osReducedNow()) };
  const listeners = new Set<(s: PersonalitySettings) => void>();
  const set = (next: PersonalitySettings) => {
    if (next.personality === settings.personality && next.sound === settings.sound && next.volume === settings.volume
      && next.reducedMotion === settings.reducedMotion) return;
    settings = next;
    for (const fn of [...listeners]) { try { fn(settings); } catch { /* one lane's listener never stops another's */ } }
  };
  return {
    get: () => settings,
    subscribe(fn: (s: PersonalitySettings) => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },
    /** The host's word. */
    receive(msg: PersonalitySettingsMessage) {
      const r = readMessage(msg);
      reduceMotion = r.reduceMotion;
      set({ personality: r.personality, sound: r.sound, volume: r.volume, reducedMotion: resolveReduced(reduceMotion, osReducedNow()) });
    },
    /** The OS changed its mind about motion. */
    osChanged() { set({ ...settings, reducedMotion: resolveReduced(reduceMotion, osReducedNow()) }); },
    /** A change made on the page shows at once; the host's echo then confirms it. */
    local(patch: Partial<Pick<PersonalitySettings, 'personality' | 'sound' | 'volume'>>) { set({ ...settings, ...patch }); },
  };
}

const media = typeof window !== 'undefined' && typeof window.matchMedia === 'function'
  ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
const store = createPersonalityStore(() => !!media?.matches);

if (typeof window !== 'undefined') {
  window.addEventListener('message', (event: MessageEvent) => {
    if (event.data?.type === 'personalitySettings') store.receive(event.data as PersonalitySettingsMessage);
  });
  media?.addEventListener?.('change', () => store.osChanged());
}

export type PersonalitySettingMessage = { type: 'setPersonalitySetting'; key: 'personality' | 'sounds' | 'soundVolume'; value: string | boolean | number };
// The page's one way to the host is the bridge, which App hands in (wirePersonality in link.tsx). This file imports no bridge, so any
// page part can read the settings, and its tests, without a VS Code host behind it.
let post: (message: PersonalitySettingMessage) => void = () => {};
export function setPersonalityPoster(fn: (message: PersonalitySettingMessage) => void): void { post = fn; }

// ---- The API the other lanes read ---------------------------------------------------------------------------------------------

export function getPersonalitySettings(): PersonalitySettings { return store.get(); }
export function onPersonalitySettingsChange(listener: (s: PersonalitySettings) => void): () => void { return store.subscribe(listener); }
export const isQuiet = (): boolean => store.get().personality === 'quiet';
export const prefersReducedMotion = (): boolean => store.get().reducedMotion;

export function setPersonality(personality: Personality): void {
  store.local({ personality });
  post({ type: 'setPersonalitySetting', key: 'personality', value: personality });
}
export function setSound(sound: boolean): void {
  store.local({ sound });
  post({ type: 'setPersonalitySetting', key: 'sounds', value: sound });
}
export function setSoundVolume(volume01: number): void {
  const volume = Math.min(1, Math.max(0, volume01));
  store.local({ volume });
  post({ type: 'setPersonalitySetting', key: 'soundVolume', value: Math.round(volume * 100) });
}

/** React: the settings, re-rendered when they change. */
const subscribeBare = (fn: () => void) => store.subscribe(() => fn());
export function usePersonalitySettings(): PersonalitySettings {
  return useSyncExternalStore(subscribeBare, store.get, store.get);
}

/** Tests only: the module's own store, to feed it a host message without a window. */
export const personalityStoreForTests = store;
