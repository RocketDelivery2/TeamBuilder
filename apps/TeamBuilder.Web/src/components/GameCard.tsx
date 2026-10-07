import type { PlayerOccurrence } from '../api/types';
import { assignmentStatusLabel, eventStatusLabel } from '../api/types';
import { formatWhen } from '../lib/format';
import { gamePath, navigate } from '../router';
import { RosterBadge } from './RosterBadge';

/** One row of My Games. Organizer-only hosted games have no assignment of the caller. */
export function GameCard({ game }: { game: PlayerOccurrence }) {
  const myStatus = game.myAssignmentStatus != null ? assignmentStatusLabel[game.myAssignmentStatus] : 'Organizing';
  return (
    <li>
      <a
        className="card game-card"
        href={gamePath(game.occurrenceId)}
        onClick={(e) => {
          e.preventDefault();
          navigate(gamePath(game.occurrenceId));
        }}
      >
        <div className="row spread">
          <strong>{game.name}</strong>
          <RosterBadge isRosterReady={game.isRosterReady} supply={game.totalSupplyCount} required={game.totalRequiredCount} />
        </div>
        <div className="muted small">{formatWhen(game.scheduledStartUtc, game.scheduledEndUtc)}</div>
        {game.location && <div className="small">{game.location}</div>}
        <div className="row gap small">
          {game.isHost && <span className="badge host">Host</span>}
          <span className="badge">{myStatus}</span>
          {game.status !== 1 && game.status !== 2 && <span className="badge">{eventStatusLabel[game.status]}</span>}
        </div>
      </a>
    </li>
  );
}
