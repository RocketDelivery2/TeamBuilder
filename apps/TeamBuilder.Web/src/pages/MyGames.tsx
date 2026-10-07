import { useCallback, useEffect, useState } from 'react';
import type { PlayerOccurrence } from '../api/types';
import { interpretError } from '../api/conflicts';
import { GameCard } from '../components/GameCard';
import { navigate } from '../router';
import { useSession } from '../session';

export function MyGames() {
  const { api } = useSession();
  const [games, setGames] = useState<PlayerOccurrence[]>();
  const [nextCursor, setNextCursor] = useState<string | null>();
  const [error, setError] = useState<string>();

  const load = useCallback(
    async (cursor?: string) => {
      try {
        const page = await api.myGames(true, cursor);
        setGames((current) => (cursor ? [...(current ?? []), ...page.items] : page.items));
        setNextCursor(page.nextCursor);
        setError(undefined);
      } catch (err) {
        setError(interpretError(err).message);
      }
    },
    [api],
  );

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section className="stack">
      <div className="row spread">
        <h1>My games</h1>
        <button className="primary" onClick={() => navigate('/new')}>
          New game
        </button>
      </div>
      {error && <p className="notice error">{error}</p>}
      {games === undefined && !error && <p className="muted">Loading…</p>}
      {games?.length === 0 && <p className="muted">No upcoming games. Create one or open a shared game link.</p>}
      {games && games.length > 0 && (
        <ul className="list">
          {games.map((game) => (
            <GameCard key={game.occurrenceId} game={game} />
          ))}
        </ul>
      )}
      {nextCursor && <button onClick={() => void load(nextCursor)}>Load more</button>}
    </section>
  );
}
