/** One text and one route for the tree model row and the native status bar. */
export interface ChecksCount { total: number; numbersDiffer?: number; keyProblems?: number; tableProblems?: number; }
export function checksCountView(s: ChecksCount) {
    const problems = [s.numbersDiffer && `${s.numbersDiffer} ${s.numbersDiffer === 1 ? 'number differs' : 'numbers differ'}`,
        s.keyProblems && `${s.keyProblems} key problem${s.keyProblems === 1 ? '' : 's'}`, s.tableProblems && `${s.tableProblems} table problem${s.tableProblems === 1 ? '' : 's'}`].filter(Boolean);
    return { text: [...problems, `${s.total} measure${s.total === 1 ? '' : 's'}`].join(' · '), attention: problems.length > 0,
        command: 'semanticus.studioGoTab', args: ['tests'] };
}
