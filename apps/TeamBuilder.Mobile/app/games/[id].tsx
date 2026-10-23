import { useLocalSearchParams } from 'expo-router';
import { Share } from 'react-native';
import { useQuery } from '@tanstack/react-query';
import { useServices } from '../../src/Providers';
import { AccountGate } from '../../src/components/AccountGate';
import { Button, Card, ErrorNotice, Label, Loading, Screen } from '../../src/components/ui';
import { displayTime } from '../../src/utils/time';
import { assignmentStatusLabel, eventStatusLabel } from '../../src/api/types';
import { occurrencePath } from '../../src/navigation/links';
function Detail({ id }: { id: string }) {
  const { games, config } = useServices();
  const query = useQuery({ queryKey: ['game', id], queryFn: ({ signal }) => games.detail(id, signal), staleTime: 0 });
  if (query.isPending) return <Loading />;
  if (!query.data || query.error) return <Screen><ErrorNotice error={query.error} retry={() => void query.refetch()} /></Screen>;
  const game = query.data, venue = game.venue;
  return <Screen onRefresh={() => void query.refetch()} refreshing={query.isRefetching}>
    <Label heading>{game.name}</Label><Label>{game.category} · {eventStatusLabel[game.status]}</Label>
    <Label>{displayTime(game.scheduledStartUtc, venue?.timeZoneId)}{game.scheduledEndUtc ? ' – ' + displayTime(game.scheduledEndUtc, venue?.timeZoneId) : ''}</Label>
    <Label muted>{venue?.timeZoneId || 'Times shown in your device time zone'}</Label>
    {game.description && <Label>{game.description}</Label>}
    <Card><Label heading>{game.supplyCount} / {game.requiredCount}{game.isRosterReady ? ' READY' : ''}</Label>
      <Label>{game.openQuantity} available spots</Label><Label>Host: {game.hostDisplayName || game.hostUsername || 'Unassigned'}</Label>
      {game.myAssignmentStatus && <Label>Your status: {assignmentStatusLabel[game.myAssignmentStatus]}</Label>}
      {!game.acceptsRosterChanges && <Label>This game no longer accepts roster changes.</Label>}
    </Card>
    {venue && <Card><Label heading>{venue.name}</Label><Label>{[venue.city, venue.stateOrProvince].filter(Boolean).join(', ')}</Label>
      {venue.isAddressMasked ? <Label>Exact address is shared with the host and participants.</Label> : venue.addressLine1 && <Label>{venue.addressLine1}</Label>}
    </Card>}
    {!venue && game.location && <Label>{game.location}</Label>}
    <Card><Label heading>Roster</Label>{game.requirements.map(requirement => <Label key={requirement.id}>{requirement.displayPosition || requirement.roleCode}: {requirement.supplyCount} / {requirement.requiredCount}</Label>)}
      {game.participants.map(participant => <Label key={participant.assignmentId}>{participant.displayName || participant.username} · {assignmentStatusLabel[participant.status]}{participant.isHost ? ' · host' : ''}</Label>)}
    </Card>
    <Button title="Share game" secondary onPress={() => { void Share.share({ message: game.name + '\n' + new URL(config.redirectUri).protocol + '//games/' + id }).catch(() => {}); }} />
    <Label muted>Last refreshed {new Date(query.dataUpdatedAt).toLocaleTimeString()}. The API confirms availability when you join.</Label>
  </Screen>;
}
export default function GameDetail() {
  const params = useLocalSearchParams(), path = occurrencePath(params.id);
  return <AccountGate>{path ? <Detail id={String(params.id)} /> : <Screen><Label>Invalid game link.</Label></Screen>}</AccountGate>;
}
