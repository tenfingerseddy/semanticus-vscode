/// <reference types="vite/client" />
import type { ReactNode } from 'react';
import { StudioMotionProvider, type LinkMotionApi } from './runtime';

// Each lane builds on its own branch. Vite picks up the link lane's modules when the slices join.
// Until then, preferences use the shared defaults and audio is silent. No second settings store.
const settings = Object.values(import.meta.glob('../settings.ts', { eager: true }))[0] as Pick<LinkMotionApi, 'getPersonalitySettings' | 'onPersonalitySettingsChange'> | undefined;
const sound = Object.values(import.meta.glob('../sound.ts', { eager: true }))[0] as Pick<LinkMotionApi, 'play'> | undefined;
const defaults = { personality: 'on' as const, sound: false };
const link: LinkMotionApi = {
  getPersonalitySettings: settings?.getPersonalitySettings ?? (() => defaults),
  onPersonalitySettingsChange: settings?.onPersonalitySettingsChange ?? (() => () => {}),
  play: sound?.play ?? (() => {}),
};

export function StudioMotion({ children }: { children: ReactNode }) {
  return <StudioMotionProvider link={link}>{children}</StudioMotionProvider>;
}
