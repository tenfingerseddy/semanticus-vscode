import { plainNumber } from './resultformat.mjs';

// A number from a check (what SQL gave, what the model gave, how far apart they are), as the Tests page shows it. One reading with
// every other surface (plainNumber), so 0.0068 reads 0.0068 here as it does in Queries and never 0.01 (Sol delta, the verifier's
// second round). A fractional amount of money still keeps its cents, so a column of money never drops a trailing zero.
export const resultText = (value: number | null | undefined): string | null => {
  if (value == null) return null;
  const text = plainNumber(value);
  return !Number.isInteger(value) && Math.abs(value) >= 1 && /[.,]\d$/.test(text) ? text + '0' : text;
};
