export function formatWhen(startUtc: string, endUtc?: string | null, locale?: string): string {
  const start = new Date(startUtc);
  const day = start.toLocaleDateString(locale, { weekday: 'short', month: 'short', day: 'numeric' });
  const startTime = start.toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit' });
  if (!endUtc) return `${day} · ${startTime}`;
  const endTime = new Date(endUtc).toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit' });
  return `${day} · ${startTime}–${endTime}`;
}

/** "READY" when the API says the roster is ready, otherwise "supply/required". */
export function rosterLabel(isRosterReady: boolean, supply: number, required: number): string {
  if (isRosterReady) return 'READY';
  if (required === 0) return 'No roster';
  return `${supply}/${required}`;
}

export function displayName(name: { displayName?: string | null; username?: string | null }): string {
  return name.displayName?.trim() || name.username || 'Unknown player';
}
