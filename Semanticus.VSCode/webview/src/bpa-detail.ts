// The line beside a Model quality category bar (hero19 D18). The score weighs errors and warnings only, so a category holding
// Info findings can read 100 beside "44 of 82". The line says how many findings count and that Info does not, so the number and
// the bar tell one story.

/** BPA severities: 1 is Info, 2 a warning, 3 an error. Only the last two weigh on the score. */
export const isInfo = (severity: number): boolean => severity <= 1;

/** "3 of 82", "3 of 82, 41 Info not counted", or "44 Info, not counted" when nothing but Info is open. */
export function categoryDetail(open: readonly { severity: number }[], objectsChecked: number): string {
  const info = open.filter((v) => isInfo(v.severity)).length;
  const counted = open.length - info;
  if (info === 0) return `${counted} of ${objectsChecked}`;
  return counted === 0 ? `${info} Info, not counted` : `${counted} of ${objectsChecked}, ${info} Info not counted`;
}

/** What one Fix all press did, in the unit the button named. The button says "Fix 119" and counts findings, so the line counts findings
 *  too ("Fixed 119 issues"), then the changes it took to do it ("143 changes") when that is a different number (hero 20 D11).
 *  `pressed` is the number on the button, `left` the number the button shows after the press (the findings it still counts, so a finding whose fix is
 *  withheld is not in it), `applied` the engine's count of changes. */
export function fixAllLine(pressed: number, applied: number, left: number): string {
  if (applied <= 0) return 'Fix all changed nothing.';
  const changes = `${applied} ${applied === 1 ? 'change' : 'changes'}`;
  const fixed = pressed - left;
  if (fixed <= 0) return `Made ${changes}.`;
  const issues = `${fixed} ${fixed === 1 ? 'issue' : 'issues'}`;
  return fixed === applied ? `Fixed ${issues}.` : `Fixed ${issues} (${changes}).`;
}
