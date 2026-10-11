// "Apply mine anyway" on a script apply (join 6, J2c). The toast comes back some time after the person pressed Apply, and they may have kept typing in
// the same editor, so the second apply sends what the editor holds NOW, not the text of the first call: sending the older text would write words the
// person has already changed, over a newer change they were told they could overwrite on purpose. A script the person closed cannot be read again, so
// the text that was sent is the one that stands. Pure, so test/script-resend.test.mjs runs it.

export interface ScriptSource { isClosed: boolean; getText(): string }

/** The text "Apply mine anyway" sends: the editor's current text while it is open, otherwise the text the first apply sent. */
export function scriptToResend(source: ScriptSource | undefined, sent: string): string {
    if (!source) return sent;
    try { return source.isClosed ? sent : source.getText(); }
    catch { return sent; }
}
