import { useState } from 'react';
import { interpretError } from '../api/conflicts';
import type { DiscoveredOccurrence } from '../api/types';
import { DiscoverCard } from '../components/DiscoverCard';
import { ACTIVITY_PRESETS } from '../lib/createGame';
import {
  RADIUS_PRESETS_MILES,
  buildDiscoverQuery,
  currentPosition,
  initialDiscoverForm,
  parseManualPoint,
  type DiscoverForm,
  type SearchPoint,
} from '../lib/discover';
import { useSession } from '../session';

/**
 * "Basketball near me, Wednesday around 8 PM". The search point comes from one explicit
 * "Use my location" tap (no tracking) or from typed coordinates, stays in this component's
 * memory and is sent only with the search itself.
 */
export function Discover() {
  const { api } = useSession();
  const [form, setForm] = useState<DiscoverForm>(() => initialDiscoverForm());
  const [point, setPoint] = useState<SearchPoint>();
  const [pointSource, setPointSource] = useState<'device' | 'manual'>();
  const [results, setResults] = useState<DiscoveredOccurrence[]>();
  const [nextCursor, setNextCursor] = useState<string | null>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [locating, setLocating] = useState(false);

  const update = <K extends keyof DiscoverForm>(key: K, value: DiscoverForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const resolvePoint = (): SearchPoint => {
    if (form.manualLat.trim() || form.manualLon.trim()) return parseManualPoint(form.manualLat, form.manualLon);
    if (point) return point;
    throw new Error('Tap "Use my location" or enter coordinates first.');
  };

  const search = async (cursor?: string) => {
    setError(undefined);
    setBusy(true);
    try {
      const page = await api.discover(buildDiscoverQuery(form, resolvePoint(), new Date(), cursor));
      setResults((current) => (cursor ? [...(current ?? []), ...page.items] : page.items));
      setNextCursor(page.nextCursor);
    } catch (err) {
      setError(err instanceof Error && !('status' in err) ? err.message : interpretError(err).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="stack">
      <h1>Find a game</h1>
      <form
        className="card stack"
        onSubmit={(e) => {
          e.preventDefault();
          void search();
        }}
      >
        <label className="field">
          <span>Activity</span>
          <select value={form.activity} onChange={(e) => update('activity', e.target.value)}>
            {ACTIVITY_PRESETS.filter((p) => p.value !== 'other').map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </label>
        <div className="grid-2">
          <label className="field">
            <span>Date</span>
            <input type="date" value={form.date} onChange={(e) => update('date', e.target.value)} required />
          </label>
          <label className="field">
            <span>Time</span>
            <select value={form.timeMode} onChange={(e) => update('timeMode', e.target.value as DiscoverForm['timeMode'])}>
              <option value="any">Any time</option>
              <option value="evening">Evening (5–11 PM)</option>
              <option value="around">Around…</option>
            </select>
          </label>
        </div>
        {form.timeMode === 'around' && (
          <label className="field">
            <span>Around (±1 hour)</span>
            <input type="time" value={form.aroundTime} onChange={(e) => update('aroundTime', e.target.value)} required />
          </label>
        )}
        <div className="field">
          <span>Distance</span>
          <div className="segmented" role="radiogroup" aria-label="Distance">
            {RADIUS_PRESETS_MILES.map((miles) => (
              <button
                key={miles}
                type="button"
                role="radio"
                aria-checked={form.radiusMiles === miles}
                className={form.radiusMiles === miles ? 'selected' : ''}
                onClick={() => update('radiusMiles', miles)}
              >
                {miles} mi
              </button>
            ))}
          </div>
        </div>
        <label className="check">
          <input type="checkbox" checked={form.openOnly} onChange={(e) => update('openOnly', e.target.checked)} />
          <span>Open spots only</span>
        </label>
        <div className="stack location">
          <div className="row gap wrap">
            <button
              type="button"
              disabled={locating}
              onClick={async () => {
                setError(undefined);
                setLocating(true);
                try {
                  setPoint(await currentPosition());
                  setPointSource('device');
                  setForm((f) => ({ ...f, manualLat: '', manualLon: '' }));
                } catch (err) {
                  setError(err instanceof Error ? err.message : 'Could not get your location.');
                } finally {
                  setLocating(false);
                }
              }}
            >
              {locating ? 'Locating…' : 'Use my location'}
            </button>
            {point && pointSource === 'device' && <span className="muted small">Using your location for this search</span>}
          </div>
          <details>
            <summary className="small">Enter coordinates instead</summary>
            <div className="grid-2">
              <label className="field">
                <span>Latitude</span>
                <input inputMode="decimal" value={form.manualLat} onChange={(e) => update('manualLat', e.target.value)} placeholder="41.8781" />
              </label>
              <label className="field">
                <span>Longitude</span>
                <input inputMode="decimal" value={form.manualLon} onChange={(e) => update('manualLon', e.target.value)} placeholder="-87.6298" />
              </label>
            </div>
          </details>
          <p className="muted small">Your location is used for this search only. It is not saved.</p>
        </div>
        <button type="submit" className="primary" disabled={busy}>
          {busy ? 'Searching…' : 'Search'}
        </button>
      </form>
      {error && <p className="notice error">{error}</p>}
      {results?.length === 0 && <p className="muted">No games within {form.radiusMiles} miles. Try a wider distance or another time.</p>}
      {results && results.length > 0 && (
        <ul className="list" aria-label="Nearby games">
          {results.map((game) => (
            <DiscoverCard key={game.occurrenceId} game={game} />
          ))}
        </ul>
      )}
      {nextCursor && (
        <button disabled={busy} onClick={() => void search(nextCursor)}>
          Load more
        </button>
      )}
    </section>
  );
}
