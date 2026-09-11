/** Only unchanged buffers follow an incoming saved version. A dirty buffer keeps the hash it was
 * edited against, so background refreshes cannot turn a stale write into an allowed overwrite. */
export function receiveDocument(previous, document) {
  if (!previous || previous.base.path !== document.path || previous.text === previous.base.exactText)
    return { base: document, latest: document, text: document.exactText };
  return { ...previous, latest: document };
}

/** Saving acknowledges the submitted text, not anything the person typed while it was on the wire. */
export function acceptDocumentSave(previous, document, submittedText, changed) {
  // A refusal also returns changed:false. Only equal source bytes acknowledge a no-op.
  if (!changed && submittedText !== document.exactText)
    return { ...previous, latest: document };
  return {
    base: document, latest: document,
    text: previous && previous.text !== submittedText ? previous.text : document.exactText,
  };
}

const sameSource = (left, right) => left.path === right.path && left.byteHash === right.byteHash && left.exactText === right.exactText;

/** Only an explicit review can move a dirty draft's write fence to the displayed saved source. */
export function rebaseDocument(previous, reviewed) {
  if (!previous || previous.base.library !== 'user' || previous.latest.library !== 'user'
    || reviewed.library !== 'user' || previous.base.path !== reviewed.path
    || previous.base.name !== reviewed.name || previous.latest.name !== reviewed.name
    || !sameSource(previous.latest, reviewed))
    throw new Error('The saved file no longer matches this review. Your edits are kept. Review the current project file again.');
  return { ...previous, base: reviewed, latest: reviewed };
}

/** An upgrade acts on saved source, so even a clean buffer must still match the latest disk version. */
export function canPreviewUpgrade(buffer) {
  return !!buffer && buffer.base.metadata.parses && buffer.text === buffer.base.exactText
    && sameSource(buffer.base, buffer.latest);
}

/** Preview changed:false is expected. Apply permission comes from canApply and the reviewed source fence. */
export function canApplyUpgrade(buffer, preview) {
  return canPreviewUpgrade(buffer) && !!preview && preview.dryRun && preview.canApply
    && !preview.parseError && typeof preview.proposedText === 'string'
    && buffer.base.library === 'user' && preview.document.library === 'user'
    && sameSource(buffer.base, preview.document) && preview.proposedText !== preview.document.exactText;
}

/** Textareas normalize line endings. Patch only the changed range back into the exact source so an
 * ordinary edit preserves BOMs, mixed line endings, blank lines and the untouched final newline. */
export function applyTextareaChange(exactText, nextValue) {
  if (exactText.charCodeAt(0) === 0xfeff) nextValue = exactText[0] + nextValue;
  const normalized = exactText.replace(/\r\n?/g, '\n');
  if (normalized === nextValue) return exactText;
  let start = 0;
  while (start < normalized.length && start < nextValue.length && normalized[start] === nextValue[start]) start++;
  let oldEnd = normalized.length;
  let newEnd = nextValue.length;
  while (oldEnd > start && newEnd > start && normalized[oldEnd - 1] === nextValue[newEnd - 1]) { oldEnd--; newEnd--; }
  const exactOffset = (offset) => {
    let index = 0;
    for (let count = 0; count < offset; count++, index++)
      if (exactText[index] === '\r' && exactText[index + 1] === '\n') index++;
    return index;
  };
  const ending = exactText.match(/\r\n|\r|\n/)?.[0] ?? '\n';
  return exactText.slice(0, exactOffset(start))
    + nextValue.slice(start, newEnd).replace(/\n/g, ending)
    + exactText.slice(exactOffset(oldEnd));
}

/** Selection changes, notifications and explicit reloads share one request sequence. */
export function createDocumentLoader({ read, onDocument, onError }) {
  let issued = 0;
  return {
    async load(name) {
      const ticket = ++issued;
      try {
        const document = await read(name);
        if (ticket !== issued) return null;
        onDocument(document);
        return document;
      } catch (error) {
        if (ticket === issued) onError(error);
        return null;
      }
    },
    invalidate() { issued++; },
  };
}
