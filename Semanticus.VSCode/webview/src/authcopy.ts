// Shared detector for a sign-in stop the person can act on: cancel, timeout, or an expired live token.
// Kept in one place so Ship Review, Data, and DAX Lab cannot drift (D-014 / D-017).

export const SIGN_IN_AGAIN = 'Sign in again';

export function isSignInError(msg: string | null | undefined): boolean {
  return !!msg && /not signed in|run connect to sign in|sign-in cancelled|sign-in timed out|sign-in did not finish|sign in again|live sign-in expired/i.test(msg);
}
