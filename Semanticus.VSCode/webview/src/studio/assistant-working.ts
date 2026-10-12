import { useEffect, useReducer } from 'react';
import { assistantIsWorking, QUIET_GAP_MS, type DriverSnapshot } from './driver-store';

/** Whether the assistant is at work now. The answer ages out one quiet gap after its last call, so a timer wakes the bar then. The timer
 *  is armed only while the Studio is visible: a hidden page sets none and asks again when it shows. */
export function useAssistantWorking(driver: DriverSnapshot): boolean {
  const [, tick] = useReducer((n: number) => n + 1, 0);
  const working = assistantIsWorking(driver, Date.now());
  useEffect(() => {
    if (!working) return;
    let timer: number | undefined;
    const arm = () => {
      window.clearTimeout(timer);
      timer = undefined;
      if (document.hidden) return;
      const left = (driver.lastAssistantCall ?? 0) + QUIET_GAP_MS - Date.now();
      if (left > 0) timer = window.setTimeout(() => tick(), left + 25);
    };
    const shown = () => { if (!document.hidden) tick(); arm(); };
    arm();
    document.addEventListener('visibilitychange', shown);
    return () => { window.clearTimeout(timer); document.removeEventListener('visibilitychange', shown); };
  }, [working, driver.lastAssistantCall, driver.state]);
  return working;
}
