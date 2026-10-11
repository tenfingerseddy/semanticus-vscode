// A SCROLLBAR THAT TAKES WIDTH, for a browser that refuses to give one.
//
// WHY THIS EXISTS. A box that grows with its text is sized by measuring the text at the box's width. Every version
// of that measurement that starts with `height: auto` is wrong in a real window, and wrong in a way no screenshot
// shows: while the box is collapsed the column or pane around it stops overflowing and gives back its scrollbar's
// width (17 to 21 px in VS Code), so the text is measured WIDER than it will be and the height that comes back is
// too small. The box then holds less than its text with `overflow: hidden` and no scrollbar, so the end of the text
// is simply gone. Measured in a real VS Code window on 27 September 2026: 60 px of the AI instructions editor at a
// Studio text size of 80%, 800 px of a Docs narrative, and 60 px of a Docs narrative while plain typing at 100%.
//
// WHY IT IS AN EMULATION. Headless Chromium on this machine gives a scrollbar no layout width at all, and nothing
// changes that: measured here on 28 September 2026 across `--hide-scrollbars` on and off, a `::-webkit-scrollbar`
// rule with an explicit width, `scrollbar-width: auto`, `--disable-overlay-scrollbar`, and
// `--disable-features=` each of OverlayScrollbar, OverlayScrollbars, FluentScrollbar and FluentOverlayScrollbar,
// in /usr/bin/chromium and in the cached headless shell. An overflowing box reports the same 2 px (its border) in
// every one of them. The one thing that does take width is `scrollbar-gutter: stable`, which reserves 17 px --
// and reserves it ALWAYS, which is the fix rather than the fault.
//
// SO THE HARNESS FORCES THE ONE EFFECT THAT MATTERS: while a growing box is collapsed, it is that much wider.
// `height: auto` written to a box lands in its own style attribute, a CSS rule can match on it, and the match is
// made in the same style recalculation as the layout the measurement forces, so the widening is seen by the
// measurement exactly the way a disappearing scrollbar is.
//
// WHAT THIS HOLDS THE PRODUCT TO, stated plainly because it is stricter than the browser: a growing box is never
// collapsed to be measured. A fit that collapses the real box fails here even on a page whose column happens to
// keep its scrollbar throughout, because that page is one shorter neighbour away from the real fault. Measuring a
// hidden twin at the box's own width (webview/src/hooks.ts, media/propgrid/propgrid.js) satisfies it; re-fitting
// after a collapse does not, and cannot: the second fit collapses the box again and measures the same wrong width.
//
// fixtures/space-scrollbar.html is the self-test. It holds the collapsing fit and nothing else, so a run can prove
// this emulation still bites before it trusts a page that passes.

/** The width a classic scrollbar takes, the figure VS Code's own panes were measured at. */
export const SCROLLBAR_PX = 17;

export const CLASSIC_SCROLLBAR_CSS = `textarea[style*="height: auto"] { width: calc(100% + ${SCROLLBAR_PX}px) !important; }`;

/** Give this page scrollbars that take width, from the first paint of every document it loads. Installed before the
 *  app renders, because the first fit a box gets is as real as the ones after it. */
export async function installClassicScrollbars(page) {
  await page.evaluateOnNewDocument((css) => {
    // This runs before the document has a root element, and the parser then builds its own, so the style has to be
    // put in at the first moment there is somewhere to put it. `interactive` is that moment and it is still before
    // the page's own deferred scripts run, so the first fit any box gets already has the scrollbar.
    const add = () => {
      const root = document.head || document.documentElement;
      if (!root || document.getElementById('__sem_classic_scrollbars')) return;
      const style = document.createElement('style');
      style.id = '__sem_classic_scrollbars';
      style.textContent = css;
      root.appendChild(style);
    };
    add();
    document.addEventListener('readystatechange', add);
    document.addEventListener('DOMContentLoaded', add);
  }, CLASSIC_SCROLLBAR_CSS);
}

/** Whether the emulation reached this page. A driver asserts it rather than assuming it. */
export const hasClassicScrollbars = (page) => page.evaluate(() => !!document.getElementById('__sem_classic_scrollbars'));
