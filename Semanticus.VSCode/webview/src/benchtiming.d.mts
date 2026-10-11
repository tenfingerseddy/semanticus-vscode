// Type surface for benchtiming.mjs.

export declare function measuredBench<T extends { runs: number; runsMs?: number[]; error?: string }>(result: T | null | undefined): T | null;
