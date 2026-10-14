export interface MotionOptions { personality?: 'on' | 'quiet'; reducedMotion?: boolean; visible?: boolean; sound?: boolean }
export function motionPolicy(options?: MotionOptions): { quiet: boolean; moving: boolean; sparkle: boolean; sound: boolean; duration: number };
export function countAt(from: number, to: number, progress: number): number;
export function gradeFrames(from: string, to: string): string[];
export function settleChoices<T extends { confidence: number }>(choices: T[]): (T & { index: number })[];
export function isBigWin(kind?: string, context?: { ok?: boolean; production?: boolean; destructive?: boolean }): boolean;
export function historyTransition<T extends { id: string; undone?: boolean; revision?: number }>(previous: T[], current: T[]): { changes: T[]; direction: 'undo' | 'redo'; fromRevision?: number; toRevision?: number } | null;
export function runFrames(options: { duration: number; frame: (progress: number) => void; requestFrame: (callback: (time: number) => void) => number; cancelFrame: (id: number) => void; now: () => number }): { stop: () => void; finish: () => void };
