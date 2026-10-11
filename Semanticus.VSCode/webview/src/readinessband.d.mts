// Type surface for readinessband.mjs.

export type Band = 'good' | 'warn' | 'bad';
export declare function band(score: number): Band;
export declare function gradeBand(letter: string | null | undefined): Band | null;
