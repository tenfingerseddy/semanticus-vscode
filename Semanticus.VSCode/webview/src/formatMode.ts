/** Static is only the empty inherit state. Any stored format expression is Dynamic, including a quoted literal. */
export function inferFormatMode(value: string | null | undefined): 'static' | 'dynamic' {
    return (value ?? '').trim() ? 'dynamic' : 'static';
}
