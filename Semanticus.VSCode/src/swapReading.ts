// What the window shows between an open's answer and its own read of the new model (cx4, the 1.7.4-rc2 tester).
//
// The open answers with the new model's session and name. Every per-model figure on screen (the status bar's measure count, the
// Plan's grade and tasks, Properties, the Studio footer) used to keep the old model's value until that surface read the new model,
// and on a live open those reads came 5 to 8 s later: pilot2's tables beside "8 measures" and "AI ready C 73.3" of the model before.
// Now each surface shows the new model's value, or this one short line, until its read comes back. Plain functions, so the node
// tests run them.

export interface SwapModel { sessionId: string; name: string }

/** The model an open's answer names, or nothing when the answer names no session (a restore, an older engine): then the window keeps its order. */
export function swapModelOf(result: any): SwapModel | undefined {
    const sessionId = result?.sessionId;
    if (typeof sessionId !== 'string' || !sessionId) return undefined;
    const name = result?.context?.editing?.modelName ?? result?.modelName ?? result?.destination?.name;
    return { sessionId, name: typeof name === 'string' && name ? name : 'the model' };
}

/** The one line a surface shows while it reads the new model. */
export function swapReadingLine(name: string): string { return `Reading ${name}...`; }

/** The status bar's checks item while the new model is read: no count, because the count on screen would be the old model's. */
export function swapReadingChip(name: string): { text: string; tooltip: string } {
    return { text: `$(sync~spin) ${swapReadingLine(name)}`, tooltip: `${swapReadingLine(name)} Its checks show here when it is read.` };
}
