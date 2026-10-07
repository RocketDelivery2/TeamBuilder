import { rosterLabel } from '../lib/format';

export function RosterBadge({ isRosterReady, supply, required }: { isRosterReady: boolean; supply: number; required: number }) {
  return (
    <span className={`badge ${isRosterReady ? 'ready' : 'open'}`} data-testid="roster-badge">
      {rosterLabel(isRosterReady, supply, required)}
    </span>
  );
}
