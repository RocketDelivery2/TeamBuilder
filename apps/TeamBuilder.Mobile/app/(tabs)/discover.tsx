import { useState } from 'react';
import { Switch, View } from 'react-native';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useServices } from '../../src/Providers';
import { Button, Card, ErrorNotice, Field, Label, Loading, Screen } from '../../src/components/ui';
import { DateTimeField } from '../../src/components/DateTimeField';
import { GameCard } from '../../src/components/GameCard';
import { deviceZone, point, searchWindow, today } from '../../src/utils/time';
import { foregroundPoint } from '../../src/location/foreground';
import { useAction } from '../../src/hooks/useAction';
import type { DiscoverQuery } from '../../src/api/types';
export default function Discover() {
  const { games } = useServices();
  const [activity, setActivity] = useState('basketball'), [date, setDate] = useState(today()), [time, setTime] = useState('20:00');
  const [zone, setZone] = useState(deviceZone()), [atTime, setAtTime] = useState(false), [open, setOpen] = useState(true);
  const [lat, setLat] = useState(''), [lon, setLon] = useState(''), [radius, setRadius] = useState(25);
  const [search, setSearch] = useState<DiscoverQuery>(), [validation, setValidation] = useState('');
  const location = useAction({ mutationFn: foregroundPoint, onSuccess: value => { setLat(String(value.lat)); setLon(String(value.lon)); } });
  const results = useInfiniteQuery({ queryKey: ['discover', search], enabled: !!search, initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => games.discover({ ...search!, cursor: pageParam }, signal),
    getNextPageParam: page => page.nextCursor || undefined, maxPages: 5, gcTime: 0 });
  const submit = () => {
    try { setSearch({ ...point(lat, lon), ...searchWindow(date, atTime ? time : undefined, zone), radiusMiles: radius, activity: activity.trim(), openOnly: open, pageSize: 20 }); setValidation(''); }
    catch (error) { setValidation(error instanceof Error ? error.message : 'Check the search fields.'); }
  };
  const items = results.data?.pages.flatMap(page => page.items) ?? [];
  return <Screen onRefresh={search ? () => void results.refetch() : undefined} refreshing={results.isRefetching}>
    <Label heading>Find your next game</Label><Label muted>Maximum playtime. Minimum downtime.</Label>
    <Card><Field label="Activity" value={activity} onChangeText={setActivity} maxLength={100} />
      <DateTimeField date={date} time={time} onDate={setDate} onTime={setTime} />
      <Field label="Time zone (IANA)" value={zone} onChangeText={setZone} autoCapitalize="none" />
      <View><Label>Search two hours from the selected time</Label><Switch accessibilityLabel="Filter by selected time" value={atTime} onValueChange={setAtTime} /></View>
      <Button title={location.isPending ? 'Finding your location…' : 'Use my location for this search'} disabled={location.isPending} secondary onPress={() => location.run()} />
      {location.error && <Label>{location.error.message}</Label>}
      <Label muted>Or enter coordinates. Address lookup is not available. Your search point stays in memory only.</Label>
      <Field label="Latitude" value={lat} onChangeText={setLat} keyboardType="numbers-and-punctuation" />
      <Field label="Longitude" value={lon} onChangeText={setLon} keyboardType="numbers-and-punctuation" />
      <Label>Search radius</Label>{[15, 25, 50].map(value => <Button key={value} secondary title={value + ' miles' + (radius === value ? ' — selected' : '')} onPress={() => setRadius(value)} />)}
      <View><Label>Only games with open spots</Label><Switch accessibilityLabel="Only open spots" value={open} onValueChange={setOpen} /></View>
      {validation ? <Label>{validation}</Label> : null}<Button title="Find games" onPress={submit} />
    </Card>
    {search && results.isPending && <Loading />}
    {results.error && <ErrorNotice error={results.error} retry={() => void results.refetch()} />}
    {search && results.isSuccess && items.length === 0 && <Card><Label>No games match this search. Try a wider radius, another date, or include full games.</Label></Card>}
    {items.map(game => <GameCard key={game.occurrenceId} id={game.occurrenceId} name={game.name} start={game.scheduledStartUtc} zone={game.timeZoneId}
      venue={game.venue.name} distance={game.venue.distanceMiles} filled={game.roster.totalSupplyCount} required={game.roster.totalRequiredCount} ready={game.roster.isRosterReady} />)}
    {results.hasNextPage && <Button title="Load more games" secondary disabled={results.isFetchingNextPage} onPress={() => void results.fetchNextPage()} />}
    {results.dataUpdatedAt > 0 && <Label muted>Last refreshed {new Date(results.dataUpdatedAt).toLocaleTimeString()}. Availability can change before you join.</Label>}
  </Screen>;
}
