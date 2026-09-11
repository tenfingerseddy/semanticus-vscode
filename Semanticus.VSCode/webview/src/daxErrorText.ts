/** Drop Analysis Services markup tags such as olii so a query error shows the object name, not the tags. */
export function plainDaxError(text: string | null | undefined): string {
    if (!text) return text ?? '';
    return text.replace(/<\/?[A-Za-z]{2,8}>/g, '');
}
