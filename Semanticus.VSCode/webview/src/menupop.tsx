import { useEffect, useRef } from 'react';
import { useSurfaceEscape } from './surface-escape';

// A small menu: from a button (under it) or at the pointer (a number's right-click). Escape closes it first.
// One copy for every page that folds a rare action into a "..." button (Queries, Notes, Model quality).
export function MenuPop({ items, onClose, at, align }: {
  items: { label: string; detail?: string; disabled?: boolean; act: () => void }[];
  onClose: () => void; at?: { x: number; y: number }; align?: 'right';
}) {
  const ref = useRef<HTMLDivElement>(null);
  useSurfaceEscape(onClose);
  useEffect(() => { ref.current?.querySelector<HTMLElement>('button:not([disabled])')?.focus(); }, []);
  const style: React.CSSProperties = at
    ? { position: 'fixed', left: Math.max(8, Math.min(at.x, window.innerWidth - 300)), top: Math.max(8, Math.min(at.y, window.innerHeight - 180)), zIndex: 75 }
    : { position: 'absolute', top: '100%', [align === 'right' ? 'right' : 'left']: 0, marginTop: 'var(--sem-space-1)', zIndex: 30 };
  return (
    <>
      <div className="fixed inset-0" style={{ zIndex: at ? 74 : 29 }} onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose(); }} />
      <div ref={ref} role="menu" className="rounded-(--sem-radius-card) border p-2" style={{ ...style, width: 290, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
        {items.map((it) => (
          <button key={it.label} type="button" role="menuitem" disabled={it.disabled} onClick={() => { onClose(); it.act(); }}
            className="mb-1 block w-full rounded-(--sem-radius-control) px-2 py-1 text-left hover:bg-[var(--sem-hover)] disabled:opacity-60">
            <span className="block text-xs font-semibold">{it.label}</span>
            {it.detail && <span className="mt-1 block text-(length:--sem-text-label) leading-4" style={{ color: 'var(--sem-muted)' }}>{it.detail}</span>}
          </button>
        ))}
      </div>
    </>
  );
}
