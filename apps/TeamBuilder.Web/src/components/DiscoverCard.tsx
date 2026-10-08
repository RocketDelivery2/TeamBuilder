import { VenuePrivacyLevel, type DiscoveredOccurrence } from '../api/types';
import { formatDistance, formatWhenInZone, localityLabel, spotsLabel, viewerLabel } from '../lib/discover';
import { gamePath, navigate } from '../router';

const titleCase = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** One nearby game: when, where, how far, how many are in and how many spots are left. */
export function DiscoverCard({ game }: { game: DiscoveredOccurrence }) {
  const mine = viewerLabel(game);
  const locality = localityLabel(game);
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
          <span className="distance" data-testid="distance">
            {/* A private venue's search point is snapped, so its distance is approximate for everyone. */}
            {formatDistance(game.venue.distanceMiles, game.venue.privacyLevel === VenuePrivacyLevel.Private)}
          </span>
        </div>
        <div className="muted small">
          {game.category ? `${titleCase(game.category)} · ` : ''}
          {formatWhenInZone(game.scheduledStartUtc, game.scheduledEndUtc, game.timeZoneId)}
        </div>
        <div className="small">
          {game.venue.name}
          {locality && <span className="muted"> · {locality}</span>}
        </div>
        <div className="row gap wrap small">
          <span className={`badge ${game.roster.isRosterReady ? 'ready' : 'open'}`} data-testid="spots">
            {spotsLabel(game.roster)}
          </span>
          {mine && <span className="badge host">{mine}</span>}
          {game.venue.isAddressMasked && <span className="badge">Private location</span>}
        </div>
      </a>
    </li>
  );
}
