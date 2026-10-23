const id = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
const detail = new RegExp('^/(games|teams|series)/' + id + '$');
export function safeReturnPath(value: unknown): string {
  if (typeof value !== 'string') return '/';
  return detail.test(value) || /^\/(discover|my-games|create|teams|profile|notifications)$/.test(value) ? value : '/';
}
export function occurrencePath(value: unknown): string | null {
  if (typeof value !== 'string' || !new RegExp('^' + id + '$').test(value)) return null;
  return '/games/' + value;
}
