export function validityLabel(empty: boolean, checked: boolean, errors: number, warns: number): string {
    if (empty) return 'empty';
    if (!checked) return 'checking…';
    if (errors > 0) return `${errors} error${errors === 1 ? '' : 's'}`;
    if (warns > 0) return `${warns} warning${warns === 1 ? '' : 's'}`;
    return '✓ valid';
}

/** A count with issues can be opened; hover is not the only way to read the reason. */
export function validityCanReveal(empty: boolean, checked: boolean, errors: number, warns: number): boolean {
    return !empty && checked && (errors + warns) > 0;
}
