import { rpc } from './bridge';
import type { TestDefinitionW } from './tests-authoring';

/** Saving and running one number uses the same saved-suite runner as Run checks. */
export async function saveTrustedNumber(def: TestDefinitionW, expectedJson: string | null): Promise<TestDefinitionW> {
  const saved = await rpc<TestDefinitionW>('saveTest', def, 'human', expectedJson);
  await rpc('runTests', false, 'human', [saved.id], null);
  return saved;
}
