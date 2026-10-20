/** The one word beside the model's name in the footer when its changes are not on disk or on the service. On a model that lives on a service a save only keeps a copy,
 *  so once a copy holds every change the footer says that, and what is still true: the service is not changed until the person publishes (UAT 18 Oct N23). */
export function unsavedWord(s: { liveBound?: boolean; keptCopyPath?: string | null }): string {
  return s.liveBound && s.keptCopyPath ? 'copy kept, not published' : 'unsaved';
}
