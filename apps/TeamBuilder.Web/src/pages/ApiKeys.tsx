import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '../api/http';
import { interpretError } from '../api/conflicts';
import type { ApiKey, CreatedApiKey } from '../api/types';
import { useSession } from '../session';

const EXPIRY_OPTIONS = [
  { label: '30 days', days: 30 },
  { label: '90 days', days: 90 },
  { label: '1 year', days: 365 },
  { label: 'Never', days: undefined },
] as const;

function messageFor(error: unknown): string {
  if (error instanceof ApiError && error.status === 429) return 'Too many key changes. Wait a while and try again.';
  return interpretError(error).message;
}

function when(value?: string | null): string {
  return value ? new Date(value).toLocaleDateString() : '';
}

/**
 * The caller's integration API keys: another app (a league site, a scoreboard) uses one to read
 * the games this player hosts. A new key is shown once, here, and never again.
 */
export function ApiKeys() {
  const { api } = useSession();
  const [keys, setKeys] = useState<ApiKey[]>();
  const [error, setError] = useState<string>();
  const [name, setName] = useState('');
  const [expiry, setExpiry] = useState<number | undefined>(90);
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<CreatedApiKey>();
  const [copied, setCopied] = useState(false);

  const refresh = useCallback(async () => {
    try {
      setKeys(await api.apiKeys());
      setError(undefined);
    } catch (err) {
      setError(messageFor(err));
    }
  }, [api]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const create = async () => {
    setBusy(true);
    try {
      const key = await api.createApiKey(name.trim(), expiry);
      setCreated(key);
      setCopied(false);
      setName('');
      await refresh();
    } catch (err) {
      setError(messageFor(err));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (key: ApiKey) => {
    if (!window.confirm(`Revoke "${key.name}"? Apps using it stop working at once.`)) return;
    try {
      await api.revokeApiKey(key.id);
      if (created?.id === key.id) setCreated(undefined);
      await refresh();
    } catch (err) {
      setError(messageFor(err));
    }
  };

  return (
    <section className="stack">
      <h1>API keys</h1>
      <p className="muted">
        An API key lets another app read the games you host and who is on each roster. Keys are read-only and can be
        revoked at any time.
      </p>
      {error && <p className="notice error">{error}</p>}

      {created && (
        <div className="card stack" data-testid="new-key">
          <strong>Copy your new key now. You won't be able to see it again.</strong>
          <input readOnly value={created.key} aria-label="New API key" onFocus={(e) => e.currentTarget.select()} />
          <button
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(created.key);
                setCopied(true);
              } catch {
                setCopied(false);
              }
            }}
          >
            {copied ? 'Copied' : 'Copy key'}
          </button>
          <button className="link small" onClick={() => setCreated(undefined)}>
            I've saved it
          </button>
        </div>
      )}

      <form
        className="card stack"
        onSubmit={(e) => {
          e.preventDefault();
          void create();
        }}
      >
        <label className="field">
          <span>Name</span>
          <input value={name} maxLength={80} placeholder="League website" onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="field">
          <span>Expires</span>
          <select
            value={expiry ?? 'never'}
            onChange={(e) => setExpiry(e.target.value === 'never' ? undefined : Number(e.target.value))}
          >
            {EXPIRY_OPTIONS.map((option) => (
              <option key={option.label} value={option.days ?? 'never'}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
        <button className="primary" type="submit" disabled={busy || name.trim().length === 0}>
          Create key
        </button>
      </form>

      {!keys && !error && <p className="muted">Loading…</p>}
      {keys?.length === 0 && <p className="muted">No keys yet.</p>}
      <ul className="list">
        {keys?.map((key) => (
          <li key={key.id} className="card stack" data-testid="api-key">
            <div className="row spread">
              <strong>{key.name}</strong>
              {key.isActive ? (
                <button className="link small" onClick={() => void revoke(key)}>
                  Revoke
                </button>
              ) : (
                <span className="muted small">{key.revokedAtUtc ? 'Revoked' : 'Expired'}</span>
              )}
            </div>
            <code className="small">{key.keyPrefix}…</code>
            <span className="muted small">
              Created {when(key.createdAtUtc)}
              {key.expiresAtUtc ? ` · expires ${when(key.expiresAtUtc)}` : ' · never expires'}
              {key.lastUsedAtUtc ? ` · last used ${when(key.lastUsedAtUtc)}` : ' · not used yet'}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
