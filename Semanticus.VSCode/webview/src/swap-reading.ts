// "Reading <model>..." between an open and a page's own read of the new model (cx4, the 1.7.4-rc2 tester).
//
// After a live open the dialog closed, the tree named the new model, and for 5 to 8 s more the Plan kept the old model's grade
// and tasks and the footer kept its name: a wrong number shown as right. The host now names the model on 'reconnected'. Each page
// (App, the header and footer's ConnectionProvider) asks this hook with the session it shows: while it still shows the session it
// had when the swap came, and the swap was to another model, the hook gives the line to show in place of the old figures. The
// page's next session read ends it, whatever model that read finds, so the line can never stick.
import { useEffect, useRef, useState } from 'react';
import { onReconnect, type SwapModel } from './bridge';

export interface SwapReading { name: string; line: string }

export function useSwapReading(shownSessionId?: string | null): SwapReading | null {
  const shown = useRef(shownSessionId ?? null);
  shown.current = shownSessionId ?? null;
  // The model being read, and the session the page showed when the swap came.
  const [swap, setSwap] = useState<{ to: SwapModel; from: string | null } | null>(null);
  useEffect(() => onReconnect((_by, model) => {
    setSwap(model && model.sessionId !== shown.current ? { to: model, from: shown.current } : null);
  }), []);
  if (!swap || (shownSessionId ?? null) !== swap.from) return null;
  return { name: swap.to.name, line: `Reading ${swap.to.name}...` };
}
