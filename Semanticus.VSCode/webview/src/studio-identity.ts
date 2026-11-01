// The Studio header's second line under the model name: the account and the tenant in use (f172 fault 2, Kane 31 Oct
// "Build as drawn"). Before it, the header named the model only, and the account and tenant needed Connections or the
// account menu. A phone has room for the short form; hover gives the long one.

export interface StudioIdentityLine { line: string; short: string; title: string }

export function studioIdentityLine(account?: string | null, tenant?: string | null): StudioIdentityLine | null {
  const who = (account || '').trim();
  if (!who) return null;
  const where = (tenant || '').trim();
  const at = who.indexOf('@');
  const name = at > 0 ? who.slice(0, at) : who;
  return {
    line: where ? `${who}, ${where}` : who,
    short: where ? `${name}, ${where}` : name,
    title: where ? `Signed in as ${who}, tenant ${where}` : `Signed in as ${who}`,
  };
}
