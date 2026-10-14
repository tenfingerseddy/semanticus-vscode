// More than six series, never the same colour twice (Kane, 13 Oct 2026: "chart colours are too similar here", 12 months as 12 series).
//
// The six palette steps (tokens.css) are green, blue, purple and a lighter or darker shift of each. A chart that needs more draws
// a ramp instead: one smooth path through the three brand hues, with lightness spread evenly along it, so a neighbour differs in
// hue AND in lightness and an ordered field (months, days, years) reads from one end to the other.
//   ordered:   the whole chart is the ramp.
//   unordered: the six steps first, then a ramp whose lightness sits clear of all six (the other side of the steps' band).
// Pure maths on rgb numbers (no DOM), so a unit test runs it. chartpalette.ts feeds it the theme's real colours.

const clamp01 = (x) => Math.min(1, Math.max(0, x));
const toLin = (c) => { const s = c / 255; return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
const fromLin = (v) => 255 * (v <= 0.0031308 ? 12.92 * v : 1.055 * v ** (1 / 2.4) - 0.055);

/** [r, g, b] (0 to 255) as OKLab [L, a, b]. */
export function toOklab([r, g, b]) {
  const lr = toLin(r), lg = toLin(g), lb = toLin(b);
  const l = Math.cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
  const m = Math.cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
  const s = Math.cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
  return [0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s, 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s];
}
const linOf = ([L, a, b]) => {
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3, m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3, s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s];
};
const inGamut = (v) => v.every((x) => x >= -1e-4 && x <= 1 + 1e-4);
/** OKLab as [r, g, b] (0 to 255), the chroma pulled in until the colour is one a screen can show. */
export function fromOklab([L, a, b]) {
  let k = 1;
  for (let i = 0; i < 40 && !inGamut(linOf([L, a * k, b * k])); i++) k *= 0.95;
  return linOf([L, a * k, b * k]).map((x) => Math.round(fromLin(clamp01(x))));
}

/** OKLab distance (the plain euclidean one; about 0.02 is the smallest step an eye can tell, 0.05 is easy). */
export function oklabDistance(p, q) {
  const x = toOklab(p), y = toOklab(q);
  return Math.hypot(x[0] - y[0], x[1] - y[1], x[2] - y[2]);
}
const luminance = (rgb) => { const [r, g, b] = rgb.map(toLin); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
/** WCAG contrast ratio of two [r, g, b] colours. */
export function contrastRatio(p, q) {
  const a = luminance(p), b = luminance(q);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** The path: green at 0, blue at 0.5, purple at 1, straight in OKLab between each pair (the chroma and hue of the three anchors). */
function pathAt(anchors, t) {
  const seg = t <= 0.5 ? 0 : 1, u = t <= 0.5 ? t * 2 : (t - 0.5) * 2;
  const p = anchors[seg], q = anchors[seg + 1];
  return [p[1] + (q[1] - p[1]) * u, p[2] + (q[2] - p[2]) * u];
}

/** The lightest and darkest a bar may be and still stand out from the page: contrast of `need` to 1 or more against `bg`. */
function lightnessLimits(bg, anchors, need) {
  const light = luminance(bg) > 0.4;
  const ok = (L) => anchors.every((an) => contrastRatio(fromOklab([L, an[1], an[2]]), bg) >= need);
  if (light) { let L = 0.95; while (L > 0.2 && !ok(L)) L -= 0.005; return { hi: L, lo: 0.2 }; }
  let L = 0.2; while (L < 0.95 && !ok(L)) L += 0.005;
  return { lo: L, hi: 0.95 };
}

/**
 * `n` colours as [r, g, b] for a chart on page colour `bg`.
 * @param {number[][]} steps the theme's six palette steps ([r, g, b] each), in order
 * @param {number[]} bg the page colour
 * @param {number} n how many series
 * @param {boolean} ordered whether the series have an order (months, days, years, numbers)
 * @param {number} [need] the contrast each bar needs against the page (default 3, the WCAG bar for a graphic)
 */
export function seriesRamp(steps, bg, n, ordered, need = 3) {
  if (n <= 0) return [];
  const anchors = steps.slice(0, 3).map(toOklab);
  const lim = lightnessLimits(bg, anchors, need);
  const dark = luminance(bg) <= 0.4;
  // The steps' own lightness band, so an unordered tail can sit clear of it.
  const ls = steps.map((s) => toOklab(s)[0]);
  const stepLo = Math.min(...ls), stepHi = Math.max(...ls);
  const place = (count, lo, hi, offset = 0) => Array.from({ length: count }, (_, i) => {
    const f = count === 1 ? 0.5 : i / (count - 1);
    const [a, b] = pathAt(anchors, f);
    return fromOklab([lo + (hi - lo) * f + offset, a, b]);
  });
  if (ordered || n <= steps.length) {
    if (!ordered) return steps.slice(0, n);
    // The ordered ramp spends the whole room the page allows (darkest that still holds `need`, lightest short of white), because
    // twelve neighbours need every bit of lightness to differ.
    return dark ? place(n, lim.lo, Math.min(lim.hi, 0.91)) : place(n, 0.27, lim.hi);
  }
  // Unordered: the six steps, then a ramp for the rest on the page side of the band where there is room, else past its other end.
  const rest = n - steps.length;
  const room = dark ? stepLo - lim.lo : lim.hi - stepHi;
  const gap = 0.06;
  const [lo, hi] = dark ? [lim.lo, Math.max(lim.lo, stepLo - gap)] : [Math.min(lim.hi, stepHi + gap), lim.hi];
  const tail = room > 0.04 ? place(rest, Math.min(lo, hi), Math.max(lo, hi)) : place(rest, dark ? Math.min(0.97, stepHi + gap) : Math.max(0.2, stepLo - gap), dark ? 0.97 : Math.max(0.2, stepLo - gap - 0.1));
  return [...steps, ...tail];
}

/** The smallest distance between neighbours (and between any two, with `all`) of a list of [r, g, b] colours. */
export function minDistance(colors, all = false) {
  let min = Infinity;
  for (let i = 0; i < colors.length; i++) for (let j = i + 1; j < (all ? colors.length : i + 2) && j < colors.length; j++) min = Math.min(min, oklabDistance(colors[i], colors[j]));
  return min;
}
