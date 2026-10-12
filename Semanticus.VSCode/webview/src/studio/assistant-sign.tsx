import type { AssistantWho } from './driver-store';
import './assistant-sign.css';

/** The assistant's own small tile (1.4, Kane 12 Oct: option A of the mockup). Drawn here, never a company logo: the tile carries two
 *  initials the engine mapped from the assistant's name, or three dots when it sent no usable name. It pulses softly while the
 *  assistant works and sits still and grey when it is connected and idle. The words beside it say the same thing, so the tile is
 *  decoration for a screen reader. */
export function AssistantSign({ who, working }: { who: AssistantWho | null; working: boolean }) {
  return <span className="as-sign" data-testid="assistant-sign" data-state={working ? 'working' : 'idle'}
    data-assistant={who?.key ?? 'unknown'} aria-hidden="true">
    <i className="as-halo" />
    <b className="as-tile">{who?.initials
      ? who.initials
      : <svg viewBox="0 0 14 14"><circle cx="2.6" cy="7" r="1.5" /><circle cx="7" cy="7" r="1.5" /><circle cx="11.4" cy="7" r="1.5" /></svg>}</b>
  </span>;
}
