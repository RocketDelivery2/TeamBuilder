import { useState } from 'react';
import { interpretError } from '../api/conflicts';
import {
  ACTIVITY_PRESETS,
  buildCreateEventRequest,
  buildCreateVenueRequest,
  defaultRequiredPlayersFor,
  initialCreateGameForm,
  type CreateGameForm,
} from '../lib/createGame';
import { browserTimeZone, currentPosition } from '../lib/discover';
import { gamePath, navigate } from '../router';
import { useSession } from '../session';

export function CreateGame() {
  const { api } = useSession();
  const [form, setForm] = useState<CreateGameForm>(() => initialCreateGameForm());
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [locating, setLocating] = useState(false);
  // A venue created by an earlier attempt whose game then failed: reuse it, never duplicate it.
  const [createdVenue, setCreatedVenue] = useState<{ key: string; id: string }>();

  const update = <K extends keyof CreateGameForm>(key: K, value: CreateGameForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  return (
    <section className="card stack">
      <h1>New pickup game</h1>
      <form
        className="stack"
        onSubmit={async (e) => {
          e.preventDefault();
          setError(undefined);
          setBusy(true);
          try {
            const venueRequest = buildCreateVenueRequest(form, browserTimeZone());
            let venueId: string | undefined;
            if (venueRequest) {
              const key = JSON.stringify(venueRequest);
              venueId = createdVenue?.key === key ? createdVenue.id : (await api.createVenue(venueRequest)).id;
              setCreatedVenue({ key, id: venueId });
            }
            const created = await api.createGame(buildCreateEventRequest(form, venueId));
            navigate(gamePath(created.id));
          } catch (err) {
            setError(err instanceof Error && !('status' in err) ? err.message : interpretError(err).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <label className="field">
          <span>Activity</span>
          <select
            value={form.activity}
            onChange={(e) => {
              update('activity', e.target.value);
              update('requiredPlayers', defaultRequiredPlayersFor(e.target.value));
            }}
          >
            {ACTIVITY_PRESETS.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Game title</span>
          <input value={form.title} onChange={(e) => update('title', e.target.value)} required maxLength={200} />
        </label>
        <div className="grid-2">
          <label className="field">
            <span>Date</span>
            <input type="date" value={form.date} onChange={(e) => update('date', e.target.value)} required />
          </label>
          <label className="field">
            <span>Start time</span>
            <input type="time" value={form.startTime} onChange={(e) => update('startTime', e.target.value)} required />
          </label>
        </div>
        <div className="grid-2">
          <label className="field">
            <span>Duration (minutes)</span>
            <input type="number" min={15} step={15} value={form.durationMinutes} onChange={(e) => update('durationMinutes', Number(e.target.value))} required />
          </label>
          <label className="field">
            <span>Players needed</span>
            <input type="number" min={1} max={100000} value={form.requiredPlayers} onChange={(e) => update('requiredPlayers', Number(e.target.value))} required />
          </label>
        </div>
        <label className="field">
          <span>Location</span>
          <input value={form.location} onChange={(e) => update('location', e.target.value)} maxLength={200} placeholder="e.g. Rec center, court 2" />
        </label>
        <label className="check">
          <input type="checkbox" checked={form.useVenue} onChange={(e) => update('useVenue', e.target.checked)} />
          <span>Add the venue so nearby players can find this game</span>
        </label>
        {form.useVenue && (
          <fieldset className="card stack venue">
            <legend className="small">Venue</legend>
            <label className="field">
              <span>Venue name</span>
              <input value={form.venueName} onChange={(e) => update('venueName', e.target.value)} maxLength={200} required placeholder="e.g. Union Park courts" />
            </label>
            <label className="field">
              <span>Street address (optional)</span>
              <input value={form.venueAddress} onChange={(e) => update('venueAddress', e.target.value)} maxLength={200} />
            </label>
            <div className="grid-2">
              <label className="field">
                <span>City</span>
                <input value={form.venueCity} onChange={(e) => update('venueCity', e.target.value)} maxLength={100} />
              </label>
              <label className="field">
                <span>State / province</span>
                <input value={form.venueState} onChange={(e) => update('venueState', e.target.value)} maxLength={100} />
              </label>
            </div>
            <label className="field">
              <span>Type</span>
              <select value={form.venueType} onChange={(e) => update('venueType', e.target.value as CreateGameForm['venueType'])}>
                <option value="outdoor">Outdoor</option>
                <option value="indoor">Indoor</option>
              </select>
            </label>
            <div className="field" role="radiogroup" aria-label="Address privacy">
              <span>Who sees the address</span>
              <label className="check">
                <input type="radio" name="venuePrivacy" checked={form.venuePrivacy === 'public'} onChange={() => update('venuePrivacy', 'public')} />
                <span>Everyone (a public court, park or gym)</span>
              </label>
              <label className="check">
                <input type="radio" name="venuePrivacy" checked={form.venuePrivacy === 'private'} onChange={() => update('venuePrivacy', 'private')} />
                <span>Only players in the game (a driveway, backyard or private club)</span>
              </label>
              {form.venuePrivacy === 'private' && (
                <p className="muted small">Others see the venue name, city and an approximate distance. Don't put the street address in the name.</p>
              )}
            </div>
            <div className="row gap wrap">
              <button
                type="button"
                disabled={locating}
                onClick={async () => {
                  setError(undefined);
                  setLocating(true);
                  try {
                    const here = await currentPosition();
                    setForm((f) => ({ ...f, venueLat: String(here.lat), venueLon: String(here.lon) }));
                  } catch (err) {
                    setError(err instanceof Error ? err.message : 'Could not get your location.');
                  } finally {
                    setLocating(false);
                  }
                }}
              >
                {locating ? 'Locating…' : "I'm at the venue: use my location"}
              </button>
            </div>
            <div className="grid-2">
              <label className="field">
                <span>Latitude</span>
                <input inputMode="decimal" value={form.venueLat} onChange={(e) => update('venueLat', e.target.value)} required />
              </label>
              <label className="field">
                <span>Longitude</span>
                <input inputMode="decimal" value={form.venueLon} onChange={(e) => update('venueLon', e.target.value)} required />
              </label>
            </div>
            <p className="muted small">Addresses are not looked up on a map yet, so the coordinates decide where the game shows up.</p>
          </fieldset>
        )}
        <label className="check">
          <input type="checkbox" checked={form.hostIsPlaying} onChange={(e) => update('hostIsPlaying', e.target.checked)} />
          <span>I'm playing too</span>
        </label>
        <p className="muted small">
          {form.hostIsPlaying ? 'You take one of the spots.' : "You're organizing only; all spots stay open for players."}
        </p>
        {error && <p className="notice error">{error}</p>}
        <div className="row gap">
          <button type="submit" className="primary" disabled={busy}>
            Create game
          </button>
          <button type="button" onClick={() => navigate('/')}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
