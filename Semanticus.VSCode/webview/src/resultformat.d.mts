// Type surface for resultformat.mjs.

export declare function friendlyHeader(name: string): string;
export declare function plainNumber(v: number): string;
export declare function displayValue(v: unknown): string;
export declare function isDateColumn(rows: unknown[][], ci: number): boolean;
export declare function columnFormatter(rows: unknown[][], ci: number, name?: string): (v: unknown) => string;
export declare function isLabelName(name: string): boolean;
