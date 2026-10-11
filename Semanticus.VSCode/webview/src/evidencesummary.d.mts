// Type surface for evidencesummary.mjs.

export declare function evidenceCountsLine(item: {
  kind?: string; verdict?: string; verified?: number | null; total?: number | null; unknowns?: number | null;
  verdictCounts?: Record<string, number> | null;
} | null | undefined): string | null;
