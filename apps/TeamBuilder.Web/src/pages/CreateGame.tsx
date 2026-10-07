import { useState } from 'react';
import { interpretError } from '../api/conflicts';
import { ACTIVITY_PRESETS, buildCreateEventRequest, defaultRequiredPlayersFor, initialCreateGameForm, type CreateGameForm } from '../lib/createGame';
import { gamePath, navigate } from '../router';
import { useSession } from '../session';

export function CreateGame() {
  const { api } = useSession();
  const [form, setForm] = useState<CreateGameForm>(() => initialCreateGameForm());
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

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
            const created = await api.createGame(buildCreateEventRequest(form));
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
