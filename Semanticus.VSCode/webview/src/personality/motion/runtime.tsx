import { createContext, useContext, useEffect, useRef, useState, useSyncExternalStore, type ReactNode, type RefObject } from 'react';
import { motionPolicy, runFrames, type MotionOptions } from './state.mjs';

export type MotionSound = 'tick' | 'snap' | 'chime' | 'pop' | 'rewind' | 'settle';
export interface SoundApi { play: (name: MotionSound, options?: { k?: number }) => void }
// The link lane supplies its sound API through the provider. Missing sound is silent.
const silent: SoundApi = { play() {} };
const MotionContext = createContext<{ options: MotionOptions; sounds: SoundApi }>({ options: {}, sounds: silent });

export function MotionProvider({ options, sounds = silent, children }: { options: MotionOptions; sounds?: SoundApi; children: ReactNode }) {
  return <MotionContext.Provider value={{ options, sounds }}>{children}</MotionContext.Provider>;
}

export interface LinkMotionApi {
  getPersonalitySettings: () => MotionOptions;
  onPersonalitySettingsChange: (listener: () => void) => () => void;
  play: (name: 'tick' | 'snap' | 'chime' | 'pop', options?: { k?: number }) => void;
}

/** The link lane owns settings and audio. Optional sounds stay silent until its palette supports them. */
export function StudioMotionProvider({ link, children }: { link: LinkMotionApi; children: ReactNode }) {
  const options = useSyncExternalStore(link.onPersonalitySettingsChange, link.getPersonalitySettings, link.getPersonalitySettings);
  const sounds: SoundApi = { play(name, extra) { if (name !== 'rewind' && name !== 'settle') link.play(name, extra); } };
  return <MotionProvider options={options} sounds={sounds}>{children}</MotionProvider>;
}

export function useMotionEnvironment(ref: RefObject<Element | null>) {
  const { options, sounds } = useContext(MotionContext);
  const [environment, setEnvironment] = useState(() => ({
    visible: !document.hidden, reduced: matchMedia('(prefers-reduced-motion: reduce)').matches || document.body.classList.contains('vscode-reduce-motion'),
  }));
  useEffect(() => {
    const media = matchMedia('(prefers-reduced-motion: reduce)');
    let inView = true;
    const update = () => setEnvironment({ visible: !document.hidden && inView,
      reduced: media.matches || document.body.classList.contains('vscode-reduce-motion') });
    const observer = new IntersectionObserver(([entry]) => { inView = entry.isIntersecting; update(); });
    if (ref.current) observer.observe(ref.current);
    const classes = new MutationObserver(update);
    classes.observe(document.body, { attributes: true, attributeFilter: ['class'] });
    document.addEventListener('visibilitychange', update);
    media.addEventListener('change', update);
    update();
    return () => { observer.disconnect(); classes.disconnect(); document.removeEventListener('visibilitychange', update); media.removeEventListener('change', update); };
  }, [ref]);
  const policy = motionPolicy({ ...options, visible: environment.visible && options.visible !== false,
    reducedMotion: environment.reduced || options.reducedMotion });
  return { ...policy, play: (name: MotionSound, extra?: { k?: number }) => { if (policy.sound) sounds.play(name, extra); } };
}

/** Compositor-only keyframes. A hidden or reduced-motion surface goes to its final state. */
export function useMotionEffect(ref: RefObject<Element | null>, eventKey: unknown,
  build: (animate: (element: Element | null, frames: Keyframe[], timing?: KeyframeAnimationOptions) => void) => void) {
  const policy = useMotionEnvironment(ref);
  const handled = useRef<unknown>(Symbol());
  const buildRef = useRef(build); buildRef.current = build;
  useEffect(() => {
    if (handled.current === eventKey) return;
    handled.current = eventKey;
    if (!policy.moving) return;
    const animations: Animation[] = [];
    buildRef.current((element, frames, timing = {}) => {
      if (!element) return;
      animations.push(element.animate(frames, { duration: policy.quiet ? 160 : 420, easing: 'ease-out', ...timing,
        ...(policy.quiet ? { duration: 160, delay: 0 } : {}), fill: 'backwards' }));
    });
    return () => { animations.forEach(a => a.cancel()); };
  }, [eventKey, policy.moving, policy.quiet]);
  return policy;
}

/** Text changes use a local finite clock. They never re-render Studio or measure layout. */
export function useMotionClock(ref: RefObject<Element | null>, eventKey: unknown, frame: (progress: number) => void, duration?: number) {
  const policy = useMotionEnvironment(ref);
  const frameRef = useRef(frame); frameRef.current = frame;
  const handled = useRef<unknown>(Symbol());
  useEffect(() => {
    if (handled.current === eventKey) { if (!policy.moving) frameRef.current(1); return; }
    handled.current = eventKey;
    const clock = runFrames({ duration: policy.moving ? (policy.quiet ? 160 : duration ?? policy.duration) : 0,
      frame: k => frameRef.current(k), requestFrame: requestAnimationFrame, cancelFrame: cancelAnimationFrame, now: () => performance.now() });
    return () => { clock.stop(); frameRef.current(1); };
  }, [eventKey, policy.moving, policy.quiet, duration]);
  return policy;
}

/** A sound follows its visual peak. Preference changes never replay it. Cancellation stays silent. */
export function useMotionCue(ref: RefObject<Element | null>, eventKey: unknown, name: MotionSound, delay: number, enabled = true) {
  const policy = useMotionEnvironment(ref);
  const handled = useRef<unknown>(Symbol());
  useEffect(() => {
    if (handled.current === eventKey) return;
    handled.current = eventKey;
    if (!enabled || !policy.sound) return;
    const clock = runFrames({ duration: policy.moving ? (policy.quiet ? 160 : delay) : 0,
      frame: k => { if (k === 1) policy.play(name); }, requestFrame: requestAnimationFrame, cancelFrame: cancelAnimationFrame, now: () => performance.now() });
    return clock.stop;
  }, [eventKey, name, delay, enabled, policy.sound, policy.moving]);
}
