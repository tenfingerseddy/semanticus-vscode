// The Studio page's half of the version guard (Kane, 25 Sep). The extension maps a missing engine method to one
// plain sentence before it answers the page (src/versionGuard.ts). This is the net under that net: a page that is
// newer than the extension host still running beside it (an in-place reinstall the window has not reloaded for)
// gets the engine's raw "No method by the name 'x' is found." from that older host, and must not show it either.
// The sentence is the same one the extension uses; test/version-guard.test.mjs pins the two together.

export const ENGINE_UPDATED_TEXT = 'Semanticus was updated. Close VS Code and your assistant, then open them again to finish.';

/** The error text the page shows for a failed engine call: the plain sentence for a missing method, else as given. */
export function studioErrorText(error) {
  const text = typeof error === 'string' ? error : String(error ?? '');
  return /No method by the name '[^']*' is found|Unhandled method /.test(text) ? ENGINE_UPDATED_TEXT : text;
}
