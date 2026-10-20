/** Drop Analysis Services markup tags such as olii so a query error shows the object name, not the tags. */
export function plainDaxError(text: string | null | undefined): string {
    if (!text) return text ?? '';
    return text.replace(/<\/?[A-Za-z]{2,8}>/g, '');
}

/** A DAX error as one plain line (UAT 18 Oct, N5). The server's words carry its position, its activity id and the query it was
 *  reading; none of that helps a person. A name the model does not have gets "Did you mean [Gross Profit]?" when one is close.
 *  `names` are the model's measure and column names. */
export function plainQueryError(text: string | null | undefined, names?: readonly string[]): string {
    if (!text) return text ?? '';
    let t = plainDaxError(text);
    t = t.replace(/\s*Technical Details:[\s\S]*$/i, '').replace(/^\s*Query \(\d+,\s*\d+\)\s*/, '').trim();
    const missing = /^The value for '((?:[^']|'')+)' cannot be determined/.exec(t) ?? /^Failed to resolve name '((?:[^']|'')+)'/.exec(t);
    if (missing) {
        const name = missing[1].replace(/''/g, "'");
        const near = nearestName(name, names);
        return `The model has nothing called "${name}".${near ? ` Did you mean [${near}]?` : ''}`;
    }
    const syntax = /^The syntax for '((?:[^']|'')+)' is incorrect/.exec(t);
    if (syntax) return `DAX does not understand "${syntax[1].replace(/''/g, "'")}" here.`;
    return t;
}

/** The model name closest to a word, when it is a slip of a letter or two; case is ignored. */
function nearestName(word: string, names?: readonly string[]): string | null {
    const w = word.toLowerCase();
    if (!names?.length || w.length < 4) return null;
    let best: string | null = null, bestD = Infinity;
    for (const n of names) {
        const d = distance(w, n.toLowerCase());
        if (d < bestD) { best = n; bestD = d; }
    }
    return best !== null && bestD > 0 && bestD <= Math.min(2, Math.floor(w.length / 3)) ? best : null;
}

function distance(a: string, b: string): number {
    if (Math.abs(a.length - b.length) > 2) return 99;
    let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
    for (let i = 1; i <= a.length; i++) {
        const row = [i];
        for (let j = 1; j <= b.length; j++) row[j] = Math.min(prev[j] + 1, row[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
        prev = row;
    }
    return prev[b.length];
}
