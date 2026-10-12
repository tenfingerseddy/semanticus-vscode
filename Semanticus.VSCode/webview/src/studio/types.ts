// The Studio's notes and suggestions as the wire carries them (1.4, contract sections 5 and 6). C# twins:
// StudioNote, StudioStep, StudioSuggestion and StudioNotesView in Semanticus.Engine/Protocol.cs.

/** One thing the assistant said to the person while it works, or one line of the Learn tour. */
export type StudioNote = {
  /** "sn-<12 hex>" from the engine; "tour-<chapter>-<n>" from the tour. */
  id: string;
  /** The sentence for the person, 200 characters at most. */
  say: string;
  /** The place's "To do this yourself" sentence, filled from the place map (the tour fills it the same way). */
  how: string;
  /** A place id from the place map. */
  place: string;
  /** An object ref in the engine's ref form, optional. */
  focus?: string | null;
  /** A press id from the press map, optional. */
  press?: string | null;
  /** ISO time the engine took it. */
  at: string;
  by: 'assistant' | 'tour';
  /** The agent call id, so a note groups with that call's toasts. */
  call?: string | null;
  sessionId: string;
  /** The engine wrote it from the assistant's tool call (follow), because the assistant did not narrate. */
  auto?: boolean;
  /** An automatic note: how many calls of its kind it covers so far. */
  count?: number | null;
  /** A re-send of a note the Studio already has: replace its words, queue nothing, move nothing. */
  update?: boolean;
  proStep?: boolean;
  upgrade?: boolean;
  /** Studio only, never on the wire: how many earlier notes this one stands for, after they folded into it (notes-store.ts). */
  folded?: number;
};

/** One button of a suggestion: a short label and the words the person would type to the assistant. */
export type StudioStep = { label: string; prompt: string };

/** The session's one current suggestion (suggest_next). A new one replaces the last. */
export type StudioSuggestion = {
  id: string;
  say: string;
  steps: StudioStep[];
  at: string;
  sessionId: string;
};

/** What the Studio reads on mount (RPC getStudioNotes): the session's notes, newest last, and its current suggestion. */
export type StudioNotesView = {
  sessionId?: string | null;
  notes?: StudioNote[] | null;
  suggestion?: StudioSuggestion | null;
};

/** The words the engine's suggest_next uses when the assistant sends no lead line. */
export const DEFAULT_SUGGEST_SAY = 'Here is what I would do next.';
