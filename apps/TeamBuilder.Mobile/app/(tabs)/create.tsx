import { useRef, useState } from 'react';
import { router } from 'expo-router';
import { Switch, View } from 'react-native';
import { Temporal } from '@js-temporal/polyfill';
import { useServices } from '../../src/Providers';
import { ApiError, NetworkError } from '../../src/api/http';
import { Button, Card, ErrorNotice, Field, Label, Screen, confirm } from '../../src/components/ui';
import { DateTimeField } from '../../src/components/DateTimeField';
import { useAction } from '../../src/hooks/useAction';
import { foregroundPoint } from '../../src/location/foreground';
import { deviceZone, today } from '../../src/utils/time';
import { buildGame, buildVenue, type GameForm } from '../../src/features/games/create';
function initial(): GameForm {
  const now = Temporal.Now.zonedDateTimeISO();
  let date = Temporal.PlainDate.from(today()).add({ days: (3 - now.dayOfWeek + 7) % 7 });
  if (now.dayOfWeek === 3 && now.hour >= 20) date = date.add({ days: 7 });
  return { name: 'Wednesday Basketball', description: '', category: 'basketball', date: date.toString(), time: '20:00', timeZone: deviceZone(),
    duration: '120', quantity: '10', hostParticipates: true, venueEnabled: true, venueName: '', address: '', city: '',
    latitude: '', longitude: '', privateVenue: false, indoor: false };
}
export default function Create() {
  const { games, queryClient } = useServices(), [form, setForm] = useState(initial), [step, setStep] = useState(1);
  const [validation, setValidation] = useState(''), [uncertain, setUncertain] = useState(false);
  const venue = useRef<{ fingerprint: string; id: string } | null>(null);
  const set = <K extends keyof GameForm>(key: K, value: GameForm[K]) => setForm(previous => ({ ...previous, [key]: value }));
  const locate = useAction({ mutationFn: foregroundPoint, onSuccess: value => setForm(previous => ({ ...previous, latitude: String(value.lat), longitude: String(value.lon) })) });
  const save = useAction({ mutationFn: async () => {
    const event = buildGame(form), requestedVenue = buildVenue(form);
    if (requestedVenue) {
      const fingerprint = JSON.stringify(requestedVenue);
      if (venue.current?.fingerprint !== fingerprint) venue.current = { fingerprint, id: (await games.createVenue(requestedVenue)).id };
      event.venueId = venue.current.id;
    }
    return games.create(event);
  }, onSuccess: game => {
    void queryClient.invalidateQueries({ queryKey: ['my-games'] }); venue.current = null; setForm(initial()); setStep(1); router.push('/games/' + game.id);
  }, onError: error => { if (error instanceof NetworkError || error instanceof ApiError && error.status >= 500) setUncertain(true); } });
  const next = () => { try { buildGame(form); if (step === 2) buildVenue(form); setStep(step + 1); setValidation(''); } catch (error) { setValidation((error as Error).message); } };
  const toggle = (key: 'hostParticipates' | 'venueEnabled' | 'privateVenue' | 'indoor', label: string) =>
    <View><Label>{label}</Label><Switch accessibilityLabel={label} value={form[key]} onValueChange={value => set(key, value)} /></View>;
  return <Screen><Label heading>Create a game</Label><Label muted>Step {step} of 3</Label>
    {step === 1 && <Card>
      <Field label="Game name" value={form.name} onChangeText={value => set('name', value)} maxLength={200} />
      <Field label="Activity" value={form.category} onChangeText={value => set('category', value)} maxLength={100} />
      <DateTimeField date={form.date} time={form.time} onDate={value => set('date', value)} onTime={value => set('time', value)} />
      <Field label="Time zone (IANA)" value={form.timeZone} onChangeText={value => set('timeZone', value)} autoCapitalize="none" />
      <Field label="Duration in minutes" value={form.duration} onChangeText={value => set('duration', value)} keyboardType="number-pad" />
      <Field label="Players required" value={form.quantity} onChangeText={value => set('quantity', value)} keyboardType="number-pad" />
      <Field label="Description" value={form.description} onChangeText={value => set('description', value)} maxLength={2000} multiline />
      {toggle('hostParticipates', 'I am playing too')}
    </Card>}
    {step === 2 && <Card>{toggle('venueEnabled', 'Attach a venue so nearby players can discover this game')}
      {form.venueEnabled && <>
        <Field label="Venue name" value={form.venueName} onChangeText={value => set('venueName', value)} maxLength={200} />
        <Field label="Street address" value={form.address} onChangeText={value => set('address', value)} maxLength={200} />
        <Field label="City" value={form.city} onChangeText={value => set('city', value)} maxLength={100} />
        {toggle('privateVenue', 'Private address: visible only to host and participants')}{toggle('indoor', 'Indoor venue')}
        <Button title="Use my location for this venue" secondary disabled={locate.isPending} onPress={() => locate.run()} />
        <Label muted>Venue coordinates are saved as part of the venue. We do not look up coordinates from a street address.</Label>
        {locate.error && <Label>{locate.error.message}</Label>}
        <Field label="Venue latitude" value={form.latitude} onChangeText={value => set('latitude', value)} keyboardType="numbers-and-punctuation" />
        <Field label="Venue longitude" value={form.longitude} onChangeText={value => set('longitude', value)} keyboardType="numbers-and-punctuation" />
      </>}
    </Card>}
    {step === 3 && <Card><Label heading>{form.name}</Label><Label>{form.date} at {form.time} · {form.timeZone}</Label>
      <Label>{form.quantity} players · {form.duration} minutes</Label><Label>{form.hostParticipates ? 'You will take one roster spot.' : 'You will organize without taking a spot.'}</Label>
      <Label>{form.venueEnabled ? form.venueName + (form.privateVenue ? ' · private address' : ' · public address') : 'No venue. This game will not appear in nearby discovery.'}</Label>
      {save.error && (save.error instanceof ApiError || save.error instanceof NetworkError ? <ErrorNotice error={save.error} /> : <Label>{save.error.message}</Label>)}
      {uncertain && <><Label>The response was interrupted. The game may exist. Check My games before deciding to submit again.</Label>
        <Button title="Check My games" secondary onPress={() => router.push('/my-games')} />
        <Button title="I checked; allow another submission" secondary onPress={() => confirm('Retry creation?', 'A previous request may have created the game. Only retry after checking your schedule.', () => setUncertain(false))} /></>}
      <Button title={save.isPending ? 'Creating game…' : 'Create game'} disabled={save.isPending || uncertain} onPress={() => save.run()} />
    </Card>}
    {validation ? <Label>{validation}</Label> : null}
    {step < 3 && <Button title="Continue" onPress={next} />}
    {step > 1 && <Button title="Back" secondary disabled={save.isPending} onPress={() => setStep(step - 1)} />}
  </Screen>;
}
