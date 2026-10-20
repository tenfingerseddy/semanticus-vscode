/** Who an entry in the live feed belongs to. The feed holds what the person pressed as well as what the assistant ran (UAT 18 Oct N15: the person's own
 *  "Labelled connection" showed under "your assistant"), so each entry says whose it is, by the origin the engine stamped when the press arrived. */
export function actorOf(e: { origin?: string }): 'You' | 'Your assistant' {
  return e.origin === 'human' ? 'You' : 'Your assistant';
}
