// Framework-free lifecycle rules for the M Code workspace. The webview and the Node tests import this same
// module so revision conflicts, stale async completions, and request cancellation are exercised behaviorally.

/** A context identifies the exact editor document revision an async M operation was started against. */
export function mContextToken(table, query, revision) {
  return JSON.stringify([table ?? '', query ?? '', revision]);
}

/** A completion may update the editor only while its table, query, and text revision are still current. */
export function isMContextCurrent(captured, current) {
  return captured === current;
}

/** Polling persistence is independent of whether its disclosure is open. Empty text is an explicit clear. */
export function pollingExpressionForSave(value) {
  const text = String(value ?? '').trim();
  return text || null;
}

/** Save is permitted only when the server still has the exact M revision the editor originally loaded. */
export function serverRevisionMatches(loadedText, serverText) {
  return loadedText === serverText;
}

/** Decide whether Save should write, accept identical server text as already saved, or report a real conflict. */
export function reconcileSaveRevision(loadedText, savingText, serverText) {
  if (serverText === loadedText) return 'write';
  if (serverText === savingText) return 'already-saved';
  return 'conflict';
}

/** Transform failures belong to one table, one column (or the table bar), and one action. */
export function transformErrorKey(table, column, action) {
  return JSON.stringify([table ?? '', column ?? '*', action]);
}

/** Resolve the failure rendered beside one exact transform control. Never coalesce failures across actions. */
export function transformErrorForAction(errors, table, column, action) {
  return errors[transformErrorKey(table, column, action)] ?? null;
}

// These mirror @microsoft/powerquery-parser's IdentifierUtils/TextUtils rules: M identifiers are Unicode-aware and
// quoted identifiers use the same character escapes as text literals. The parser's text helper handles the named
// escapes; the lexer grammar also permits four- and eight-digit Unicode escapes, which are decoded here as well.
const M_IDENTIFIER_START = /[\p{L}\p{Nl}_]/u;
const M_IDENTIFIER_PART = /[\p{L}\p{Nl}\p{Nd}\p{M}\p{Pc}\p{Cf}]/u;

function mCodePointAt(text, index) {
  const value = text.codePointAt(index);
  return value == null ? '' : String.fromCodePoint(value);
}

function mEscapeSequenceValue(value, start, end) {
  const length = end - start;
  if (length === 1 && value[start] === '#') return '#';
  if (length === 2 && value[start] === 'c' && value[start + 1] === 'r') return '\r';
  if (length === 2 && value[start] === 'l' && value[start + 1] === 'f') return '\n';
  if (length === 3 && value[start] === 't' && value[start + 1] === 'a' && value[start + 2] === 'b') return '\t';
  if (length !== 4 && length !== 8) return null;

  let codePoint = 0;
  for (let i = start; i < end; i += 1) {
    const code = value.charCodeAt(i);
    let digit;
    if (code >= 48 && code <= 57) digit = code - 48;
    else if (code >= 65 && code <= 70) digit = code - 55;
    else if (code >= 97 && code <= 102) digit = code - 87;
    else return null;
    codePoint = (codePoint * 16) + digit;
  }
  return codePoint <= 0x10ffff ? String.fromCodePoint(codePoint) : null;
}

function mQuotedIdentifierValue(value) {
  const chunks = [];
  let literalStart = 0;
  let i = 0;
  while (i < value.length) {
    if (value[i] === '"' && value[i + 1] === '"') {
      chunks.push(value.slice(literalStart, i), '"');
      i += 2;
      literalStart = i;
      continue;
    }
    if (value[i] !== '#' || value[i + 1] !== '(') {
      i += 1;
      continue;
    }

    // Commit to the first closing parenthesis even when the body is invalid. This keeps the decoder's existing
    // over-matching bias while ensuring nested or unmatched "#(" text is consumed once instead of rescanned.
    const decoded = [];
    const doubledQuotes = [];
    let valid = true;
    let sequenceStart = i + 2;
    let cursor = sequenceStart;
    while (cursor < value.length && value[cursor] !== ')') {
      if (value[cursor] === '"' && value[cursor + 1] === '"') {
        doubledQuotes.push(cursor);
        cursor += 2;
        continue;
      }
      if (value[cursor] === ',') {
        const sequence = mEscapeSequenceValue(value, sequenceStart, cursor);
        if (sequence == null) valid = false;
        else decoded.push(sequence);
        sequenceStart = cursor + 1;
      } else if (cursor - sequenceStart >= 8) {
        valid = false;
      }
      cursor += 1;
    }
    if (cursor === value.length) {
      // A failed regex candidate previously left the unmatched escape literal but still decoded later doubled
      // quotes. Record those positions during the same walk so that compatibility does not require another scan.
      for (const quote of doubledQuotes) {
        chunks.push(value.slice(literalStart, quote), '"');
        literalStart = quote + 2;
      }
      i = cursor;
      break;
    }

    const sequence = mEscapeSequenceValue(value, sequenceStart, cursor);
    if (sequence == null) valid = false;
    else decoded.push(sequence);
    if (valid) {
      chunks.push(value.slice(literalStart, i), decoded.join(''));
      literalStart = cursor + 1;
    }
    i = cursor + 1;
  }
  chunks.push(value.slice(literalStart));
  return chunks.join('');
}

// Return identifier-shaped references while excluding comments and text literals. Quoted identifiers keep their
// decoded name so dependencies such as #"Shared Query" can be matched against the document's named expressions.
function mIdentifierReferences(source) {
  const text = String(source ?? '');
  const names = [];
  let i = 0;
  while (i < text.length) {
    if (text[i] === '/' && text[i + 1] === '/') {
      i += 2;
      while (i < text.length && text[i] !== '\n' && text[i] !== '\r') i += 1;
      continue;
    }
    if (text[i] === '/' && text[i + 1] === '*') {
      i += 2;
      let depth = 1;
      while (i < text.length && depth > 0) {
        if (text[i] === '/' && text[i + 1] === '*') { depth += 1; i += 2; }
        else if (text[i] === '*' && text[i + 1] === '/') { depth -= 1; i += 2; }
        else i += 1;
      }
      continue;
    }
    if (text[i] === '#' && text[i + 1] === '"') {
      i += 2;
      let name = '';
      while (i < text.length) {
        if (text[i] === '"' && text[i + 1] === '"') { name += '""'; i += 2; continue; }
        if (text[i] === '"') { i += 1; break; }
        name += text[i]; i += 1;
      }
      names.push(mQuotedIdentifierValue(name));
      continue;
    }
    if (text[i] === '"') {
      i += 1;
      while (i < text.length) {
        if (text[i] === '"' && text[i + 1] === '"') { i += 2; continue; }
        if (text[i] === '"') { i += 1; break; }
        i += 1;
      }
      continue;
    }
    const char = mCodePointAt(text, i);
    if (M_IDENTIFIER_START.test(char)) {
      const start = i;
      i += char.length;
      while (i < text.length) {
        const part = mCodePointAt(text, i);
        if (M_IDENTIFIER_PART.test(part)) { i += part.length; continue; }
        if (part === '.') {
          const next = mCodePointAt(text, i + 1);
          if (next && next !== '.' && M_IDENTIFIER_PART.test(next)) { i += 1; continue; }
        }
        break;
      }
      names.push(text.slice(start, i));
      continue;
    }
    i += char.length || 1;
  }
  return names;
}

/**
 * The profile belongs to the loaded table, not the query currently selected in the editor. Capture every M
 * partition on that table and the transitive named-expression documents referenced by those partitions. This
 * makes parameter repairs and other out-of-band dependency writes invalidate results without making unrelated
 * named-expression edits stale.
 */
export function profileSubjectToken(table, partitions, expressions) {
  const sources = (partitions ?? [])
    .filter((partition) => String(partition.sourceType ?? '').toLowerCase() === 'm')
    .map((partition) => [String(partition.name ?? ''), String(partition.source ?? '')]);
  const expressionByName = new Map(
    (expressions ?? [])
      .filter((expression) => String(expression.kind ?? 'M').toLowerCase() === 'm')
      .map((expression) => [String(expression.name ?? ''), String(expression.expression ?? '')]),
  );
  const referenced = new Set();
  const pendingSources = sources.map(([, source]) => source);
  while (pendingSources.length > 0) {
    const source = pendingSources.pop();
    for (const name of mIdentifierReferences(source)) {
      if (referenced.has(name) || !expressionByName.has(name)) continue;
      referenced.add(name);
      pendingSources.push(expressionByName.get(name));
    }
  }
  const dependencies = [...referenced].sort().map((name) => [name, expressionByName.get(name)]);
  return JSON.stringify([table ?? '', sources, dependencies]);
}

/** Advance the profile subject snapshot and report whether existing profile results must become stale. */
export function reconcileProfileSubject(previousToken, table, partitions, expressions) {
  const token = profileSubjectToken(table, partitions, expressions);
  return { token, profileInvalidated: previousToken != null && previousToken !== token };
}

/**
 * Unique busy-owner gate. reset() abandons the current owner without reusing its id, so an older completion can
 * neither act nor release a newer operation that happens to use the same resource and action.
 */
export function createBusyOwnerGate() {
  let sequence = 0;
  let owner = null;
  return {
    begin(operation) {
      if (owner) return null;
      owner = { ...operation, ownerId: ++sequence };
      return owner;
    },
    reset() { owner = null; },
    current() { return owner; },
    isOwner(candidate) { return !!candidate && owner?.ownerId === candidate.ownerId; },
    release(candidate) {
      if (!candidate || owner?.ownerId !== candidate.ownerId) return false;
      owner = null;
      return true;
    },
  };
}

/**
 * Busy-owner gate bound to an M context token. Beginning or syncing a different context abandons the previous
 * owner immediately, so its completion can neither act in nor release the newer context's operation.
 */
export function createContextBusyOwnerGate(initialContext = null) {
  const gate = createBusyOwnerGate();
  let context = initialContext;
  const resetContext = (nextContext) => {
    if (context === nextContext) return false;
    context = nextContext;
    gate.reset();
    return true;
  };
  return {
    begin(operation, operationContext) {
      resetContext(operationContext);
      return gate.begin({ ...operation, contextToken: operationContext });
    },
    resetContext,
    reset() { context = null; gate.reset(); },
    current() { return gate.current(); },
    isOwner(candidate) {
      return !!candidate && candidate.contextToken === context && gate.isOwner(candidate);
    },
    release(candidate) {
      if (!candidate || candidate.contextToken !== context) return false;
      return gate.release(candidate);
    },
  };
}

/**
 * Reconcile a server-side M text with the editor revision that was originally loaded.
 * Clean editors reload immediately. Dirty editors retain the user's text and expose the newer server text as a
 * conflict. Either outcome invalidates Profile because an out-of-band M write occurred.
 */
export function reconcileExternalM(editor, serverText) {
  if (serverText === editor.original) return { kind: 'unchanged', ...editor, profileInvalidated: false };
  if (editor.text === editor.original) {
    return { kind: 'reloaded', text: serverText, original: serverText, serverText: null, profileInvalidated: true };
  }
  return { kind: 'conflict', text: editor.text, original: editor.original, serverText, profileInvalidated: true };
}

/** Monotonic request gate. cancel() invalidates the active id, including during component unmount. */
export function createRequestGate() {
  let generation = 0;
  return {
    begin() { generation += 1; return generation; },
    cancel() { generation += 1; },
    isCurrent(request) { return request === generation; },
  };
}
