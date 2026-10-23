import { router } from 'expo-router';
import { Button, Card, Label } from './ui';
import { displayTime } from '../utils/time';
export function GameCard({ id, name, start, filled, required, ready, venue, zone, distance }: {
  id: string; name: string; start: string; filled: number; required: number; ready: boolean;
  venue?: string | null; zone?: string | null; distance?: number | null;
}) {
  return <Card><Label heading>{name}</Label><Label>{displayTime(start, zone)}{zone ? ' · ' + zone : ''}</Label>
    {venue && <Label muted>{venue}{distance != null ? ' · ' + distance.toFixed(1) + ' miles' : ''}</Label>}
    <Label>{filled} / {required}{ready ? ' READY' : ' players'}</Label>
    <Button title={'Open ' + name} secondary onPress={() => router.push('/games/' + id)} /></Card>;
}
