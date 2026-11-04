/**
 * The lines an open writes to the Semanticus output log (cx1, Kane 3 Nov). The engine puts each step of the open and its time on the
 * open result; the window adds its own steps after the engine answered. One total line names the slowest steps, so the next slow open
 * shows its cause in the log the person can send. Steps that ran beside the others ("alongside") are listed but not added to the total.
 */
export interface OpenStepTime { step: string; ms: number; alongside?: boolean; after?: boolean }

export function openTimingLines(method: string, result: { steps?: OpenStepTime[]; totalMs?: number } | null | undefined, windowSteps: OpenStepTime[]): string[] {
    const engine = Array.isArray(result?.steps) ? result!.steps! : [];
    const lines = engine.map((s) => `[open] engine step=${s.step} ms=${s.ms}${s.alongside ? ' alongside' : ''}`);
    if (engine.length && typeof result?.totalMs === 'number') lines.push(`[open] engine total ms=${result.totalMs}`);
    for (const s of windowSteps) lines.push(`[open] window step=${s.step} ms=${s.ms}${s.after ? ' after the answer' : ''}`);
    // The total is the time to the answer. A window step after the answer (Properties, the status bar: cx3) no longer holds the dialog.
    const total = windowSteps.filter((s) => !s.after).reduce((sum, s) => sum + s.ms, 0);
    // The engine's steps are inside "engine answered", so they are ranked in its place; the window's own steps are ranked with them (cx3: a
    // 6 s window step was left out of the slowest three).
    const own = windowSteps.filter((s) => !(engine.length && s.step === 'engine answered')).map((s) => ({ ...s, step: engine.length ? `window ${s.step}` : s.step }));
    const named = [...engine, ...own].filter((s) => !s.alongside);
    const slow = [...named].sort((a, b) => b.ms - a.ms).slice(0, 3).map((s) => `${s.step} ${s.ms}`);
    lines.push(`[open] done ${method} total ms=${total}${slow.length ? ` slowest: ${slow.join(', ')}` : ''}`);
    return lines;
}
