// Type surface for chartramp.mjs.
export type Rgb = number[];
export declare function toOklab(rgb: Rgb): number[];
export declare function fromOklab(lab: number[]): Rgb;
export declare function oklabDistance(p: Rgb, q: Rgb): number;
export declare function contrastRatio(p: Rgb, q: Rgb): number;
export declare function seriesRamp(steps: Rgb[], bg: Rgb, n: number, ordered: boolean, need?: number): Rgb[];
export declare function minDistance(colors: Rgb[], all?: boolean): number;
