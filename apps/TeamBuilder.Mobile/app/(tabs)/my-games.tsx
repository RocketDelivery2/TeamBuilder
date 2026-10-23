import { useState } from 'react';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useServices } from '../../src/Providers';
import { Button, ErrorNotice, Label, Loading, Screen } from '../../src/components/ui';
import { GameCard } from '../../src/components/GameCard';
export default function MyGames() {
  const { games } = useServices();
  const [filter, setFilter] = useState<'upcoming' | 'hosted' | 'joined' | 'past'>('upcoming');
  const [pastEnd, setPastEnd] = useState(() => new Date().toISOString());
  const query = useInfiniteQuery({ queryKey: ['my-games', filter, filter === 'past' ? pastEnd : 'upcoming'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => games.mine({ cursor: pageParam, ...(filter === 'past' ? { fromUtc: '2000-01-01T00:00:00Z', toUtc: pastEnd, includeTerminal: true } : {}) }, signal),
    getNextPageParam: page => page.nextCursor || undefined, maxPages: 5 });
  const gamesShown = (query.data?.pages.flatMap(page => page.items) ?? []).filter(game =>
    filter === 'hosted' ? game.isHost : filter === 'joined' ? game.myAssignmentId != null : true);
  return <Screen onRefresh={() => void query.refetch()} refreshing={query.isRefetching}><Label heading>My games</Label>
    {(['upcoming', 'hosted', 'joined', 'past'] as const).map(value => <Button key={value} secondary title={value + (filter === value ? ' — selected' : '')} onPress={() => { setFilter(value); if (value === 'past') setPastEnd(new Date().toISOString()); }} />)}
    {query.isPending && <Loading />}{query.error && <ErrorNotice error={query.error} retry={() => void query.refetch()} />}
    {query.isSuccess && !gamesShown.length && <Label>{query.hasNextPage ? 'No matching games on this page. Load more to continue.' : 'No games here yet. Discover a game or create one.'}</Label>}
    {gamesShown.map(game => <GameCard key={game.occurrenceId} id={game.occurrenceId} name={game.name} start={game.scheduledStartUtc} venue={game.location}
      filled={game.totalSupplyCount} required={game.totalRequiredCount} ready={game.isRosterReady} />)}
    {query.hasNextPage && <Button title="Load more games" disabled={query.isFetchingNextPage} onPress={() => void query.fetchNextPage()} />}
    {query.dataUpdatedAt > 0 && <Label muted>Last refreshed {new Date(query.dataUpdatedAt).toLocaleTimeString()}</Label>}
  </Screen>;
}
