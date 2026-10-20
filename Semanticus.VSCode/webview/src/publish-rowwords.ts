// The words of one row of the Publish list. A language (culture) row holds the model's AI instructions and its synonyms together, so the
// generic "en-US · language changed" told the person nothing. This says which of them changed. Every other row keeps publishcopy's words.
import { rowNote, type ChangeRow } from './publishcopy';

/** The text of the model's AI instructions inside a culture's JSON (the linguistic metadata's CustomInstructions), or null when it has none. */
function instructionsOf(text: string | null | undefined): string | null {
  try {
    const o = JSON.parse(text ?? '');
    let content = o?.linguisticMetadata?.content ?? o?.content;
    if (typeof content === 'string') content = JSON.parse(content);
    const v = content?.CustomInstructions;
    return typeof v === 'string' ? v : null;
  } catch { return null; }
}

/** What the row says: its name, and the note after the dot (empty when the name already says it all). */
export function rowWords(row: ChangeRow, destination: string): { name: string; note: string } {
  if (row.objectType === 'Culture' && row.action === 'Update') {
    return instructionsOf(row.leftText) !== instructionsOf(row.rightText)
      ? { name: 'AI instructions changed', note: '' }
      : { name: `${row.name}`, note: 'words changed' };
  }
  return { name: row.name, note: rowNote(row, destination) };
}
