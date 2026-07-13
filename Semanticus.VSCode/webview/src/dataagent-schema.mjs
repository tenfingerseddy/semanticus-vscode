// Fabric owns this JSON shape and can add fields without a Semanticus release. Keep schema edits surgical:
// change only is_selected in the elements tree and serialize the complete original document back out.

function rootElements(value) {
  if (Array.isArray(value)) return value;
  if (value && typeof value === 'object' && Array.isArray(value.elements)) return value.elements;
  return [];
}

export function parseElementTree(json) {
  if (!json) return [];
  try { return rootElements(JSON.parse(json)); }
  catch { return []; }
}

export function isEditableElementTree(json) {
  if (!json) return false;
  try {
    const value = JSON.parse(json);
    return !!value && !Array.isArray(value) && typeof value === 'object' && Array.isArray(value.elements);
  } catch { return false; }
}

function setDescendants(element, selected) {
  element.is_selected = selected;
  if (Array.isArray(element.children)) {
    for (const child of element.children) setDescendants(child, selected);
  }
}

export function updateElementSelection(json, path, selected) {
  const value = JSON.parse(json);
  if (!value || Array.isArray(value) || typeof value !== 'object' || !Array.isArray(value.elements)) {
    throw new Error('The data source does not contain an editable elements tree.');
  }
  if (!Array.isArray(path) || path.length === 0) throw new Error('An element path is required.');

  let elements = value.elements;
  const ancestors = [];
  let target;
  for (let depth = 0; depth < path.length; depth++) {
    const index = path[depth];
    if (!Number.isInteger(index) || index < 0 || index >= elements.length) {
      throw new Error('The selected element no longer exists. Reload the agent and try again.');
    }
    target = elements[index];
    if (!target || typeof target !== 'object') throw new Error('The selected element is not editable.');
    if (depth < path.length - 1) {
      ancestors.push(target);
      elements = Array.isArray(target.children) ? target.children : [];
    }
  }

  setDescendants(target, selected);
  if (selected) for (const ancestor of ancestors) ancestor.is_selected = true;
  return JSON.stringify(value, null, 2);
}
