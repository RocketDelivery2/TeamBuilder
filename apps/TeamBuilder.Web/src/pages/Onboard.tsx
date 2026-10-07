import { useState } from 'react';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { PlayerProfile } from '../api/types';
import { interpretError } from '../api/conflicts';

export function Onboard({ api, onDone, onSignOut }: { api: TeamBuilderApi; onDone: (me: PlayerProfile) => void; onSignOut: () => void }) {
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  return (
    <section className="card stack">
      <h1>Create your player</h1>
      <p className="muted">You're signed in. Pick the name other players will see.</p>
      <form
        className="stack"
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError(undefined);
          try {
            onDone(await api.onboard(username.trim(), displayName.trim() || undefined));
          } catch (err) {
            setError(interpretError(err).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <label className="field">
          <span>Username</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} required maxLength={100} autoComplete="username" />
        </label>
        <label className="field">
          <span>Display name</span>
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} maxLength={200} autoComplete="name" />
        </label>
        {error && <p className="notice error">{error}</p>}
        <button type="submit" className="primary" disabled={busy || !username.trim()}>
          Continue
        </button>
        <button type="button" className="link" onClick={onSignOut}>
          Sign out
        </button>
      </form>
    </section>
  );
}
