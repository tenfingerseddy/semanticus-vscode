import type { ReactNode } from 'react';

/** Shared card frame for the Checks page and the Properties grid. */
export function MeasureCardShell({ name, icon, children, onClose }: { name: string; icon?: ReactNode; children?: ReactNode; onClose: () => void }) {
  return <section className="ck-card" aria-label={name}>
    <div className="ck-card-head"><strong>{icon}{name}</strong><button type="button" className="sem-btn sem-btn-sm" aria-label="Close card" onClick={onClose}>Close</button></div>
    {children}
  </section>;
}
