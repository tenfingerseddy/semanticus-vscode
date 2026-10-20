// Type surface for periodorder.mjs.

export interface PeriodOptions { calendarMonths?: boolean }
export declare function periodKey(value: unknown, named: boolean, opts?: PeriodOptions): string | null;
export declare function periodKeys(values: unknown[], named: boolean, opts?: PeriodOptions): Map<string, string> | null;

export function axisLabelText(value: unknown): string;
