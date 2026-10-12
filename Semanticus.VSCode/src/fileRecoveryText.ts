export interface FileRecovery { source: string; capturedUtc: string }

export function recoveryMessage(copy: FileRecovery): string {
    const parts = copy.source.replace(/[\\/]+$/, '').split(/[\\/]/).filter(Boolean);
    // A model kept as a folder of files ends in "definition", which names no model: the model is the folder above it ("Northstar.SemanticModel").
    if (parts.length > 1 && parts[parts.length - 1].toLowerCase() === 'definition') parts.pop();
    const name = (parts.pop() ?? '').replace(/\.(SemanticModel|Dataset)$/i, '') || 'your model';
    const time = new Date(copy.capturedUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
    return `Semanticus kept your unsaved changes to ${name} from ${time}.`;
}
