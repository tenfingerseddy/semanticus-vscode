// One band function for every AI readiness colour: the Hero letter and score, each category bar and score, and
// the grade on the Plan and start-from-SQL pages. The cut points are ReadinessAnalyzer.GradeFor's (90/80/70/60
// for A/B/C/D), folded to three bands: A and B good, C and D warn, F bad. Keeping one function is what stops a B
// model showing an amber bar.

/** @param {number} score 0 to 100 @returns {'good' | 'warn' | 'bad'} */
export function band(score) {
  return score >= 80 ? 'good' : score >= 60 ? 'warn' : 'bad';
}

/** @param {string | null | undefined} letter @returns {'good' | 'warn' | 'bad' | null} null for anything that is not a grade */
export function gradeBand(letter) {
  switch (letter) {
    case 'A': case 'B': return 'good';
    case 'C': case 'D': return 'warn';
    case 'F': return 'bad';
    default: return null;
  }
}
