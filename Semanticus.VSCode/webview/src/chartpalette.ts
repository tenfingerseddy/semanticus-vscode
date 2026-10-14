// The one chart palette, for a canvas. The tokens (--sem-series-1 to 6, --sem-series-neutral) live in tokens.css next to
// the actor tokens and follow the theme. A DOM surface reads them with var(--sem-series-N). A canvas chart (ECharts)
// cannot read a custom property and cannot parse oklab(from ...), so it asks here for a plain rgb colour instead.
// Outside a browser (a unit test) every call returns its fallback.

import { useEffect, useState } from 'react';
import { seriesRamp } from './chartramp.mjs';

/** A number that changes whenever the editor theme does (VS Code sets a class on <body> and the --vscode-* variables on <html>).
 *  A canvas chart bakes its colours in when it is drawn, so one that reads this draws again in the new theme. */
export function useThemeEpoch(): number {
  const [epoch, setEpoch] = useState(0);
  useEffect(() => {
    const bump = () => setEpoch((n) => n + 1);
    const mo = new MutationObserver(bump);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class'] });
    mo.observe(document.body, { attributes: true, attributeFilter: ['class', 'data-vscode-theme-id'] });
    return () => mo.disconnect();
  }, []);
  return epoch;
}

export const SERIES_TOKENS = ['--sem-series-1', '--sem-series-2', '--sem-series-3', '--sem-series-4', '--sem-series-5', '--sem-series-6'] as const;
const FALLBACKS = ['#3CC08F', '#59A4F9', '#B180D7', '#7FE0B8', '#9CC8FB', '#D0AEEA'];
export const seriesVar = (i: number): string => `var(${SERIES_TOKENS[i % SERIES_TOKENS.length]})`;

let canvas: CanvasRenderingContext2D | null | undefined;
function pixel(): CanvasRenderingContext2D | null {
  if (canvas !== undefined) return canvas;
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 1;
    canvas = c.getContext('2d', { willReadFrequently: true });
  } catch { canvas = null; }
  return canvas;
}

/** A token or var(--token) as 'rgb(r, g, b)': the browser works the token out (relative colours, color-mix and all),
 *  and a one-pixel canvas turns whatever form it prints into numbers a chart library can read. */
export function tokenColor(name: string, fallback: string, alpha = 1): string {
  if (typeof document === 'undefined') return fallback;
  const value = name.startsWith('var(') ? name : `var(${name})`;
  const g = pixel();
  if (!g) return fallback;
  const probe = document.createElement('span');
  try {
    probe.style.color = value;
    document.body.appendChild(probe);
    const printed = getComputedStyle(probe).color;
    if (!printed) return fallback;
    g.clearRect(0, 0, 1, 1);
    g.fillStyle = '#000000';
    g.fillStyle = printed;
    g.fillRect(0, 0, 1, 1);
    const [r, gr, b, a] = g.getImageData(0, 0, 1, 1).data;
    if (a === 0) return fallback;
    return alpha >= 1 ? `rgb(${r}, ${gr}, ${b})` : `rgba(${r}, ${gr}, ${b}, ${alpha})`;
  } catch { return fallback; } finally { probe.remove(); }
}

/** Any colour a surface names (a hex, an rgb, or var(--token)) as a colour a canvas can paint. */
export function paintColor(v: string | undefined, fallback: string): string {
  if (!v) return fallback;
  return v.startsWith('var(') ? tokenColor(v, fallback) : v;
}

/** The six series, in order, as canvas colours. */
export const seriesColors = (): string[] => SERIES_TOKENS.map((t, i) => tokenColor(t, FALLBACKS[i]));
/** Step i (0 to 5) as a canvas colour; past the sixth it starts again, and the series also differ by place and label. */
export const seriesColor = (i: number): string => seriesColors()[i % SERIES_TOKENS.length];
/** "Other" and "unused". */
export const neutralColor = (alpha = 1): string => tokenColor('--sem-series-neutral', 'rgb(140, 140, 160)', alpha);
/** The ink for text drawn on a series fill (the page colour, which every step holds 4.5:1 against). */
export const onSeriesInk = (): string => tokenColor('--sem-bg', '#1F1F1F');

const rgbOf = (c: string): number[] => c.startsWith('#')
  ? [1, 3, 5].map((k) => parseInt(c.slice(k, k + 2), 16))
  : (c.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
/** `n` series colours for one chart, as canvas colours. Up to six: the palette steps. More: no colour twice. An ordered field
 *  (months, days, years, numbers) gets one smooth ramp across green, blue and purple, lightness spread evenly, so neighbours
 *  differ and the order reads left to right; an unordered one gets the six steps and then a ramp (chartramp.mjs). */
export function seriesPalette(n: number, ordered: boolean): string[] {
  const steps = seriesColors();
  if (n <= steps.length) return steps.slice(0, Math.max(n, 0));
  const bg = rgbOf(tokenColor('--sem-bg', 'rgb(31, 31, 31)'));
  return seriesRamp(steps.map(rgbOf), bg, n, ordered).map(([r, g, b]) => `rgb(${r}, ${g}, ${b})`);
}
