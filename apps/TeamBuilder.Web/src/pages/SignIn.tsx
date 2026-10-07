import { useState } from 'react';

export interface SignInProps {
  message?: string;
  oidcAvailable: boolean;
  devTokenAllowed: boolean;
  onOidcSignIn: () => void;
  onDevToken: (token: string) => void;
}

export function SignIn({ message, oidcAvailable, devTokenAllowed, onOidcSignIn, onDevToken }: SignInProps) {
  const [token, setToken] = useState('');

  return (
    <section className="card stack">
      <h1>Sign in</h1>
      {message && <p className="notice">{message}</p>}
      {oidcAvailable && (
        <button className="primary" onClick={onOidcSignIn}>
          Sign in
        </button>
      )}
      {devTokenAllowed && (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault();
            if (token.trim()) onDevToken(token);
          }}
        >
          <label className="field">
            <span>Developer token (local QA only)</span>
            <textarea
              rows={4}
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="Paste a bearer token issued for the TeamBuilder API"
              autoComplete="off"
              spellCheck={false}
            />
          </label>
          <p className="muted small">The token stays in this browser tab's session and is cleared when you sign out.</p>
          <button type="submit" className={oidcAvailable ? '' : 'primary'} disabled={!token.trim()}>
            Use token
          </button>
        </form>
      )}
      {!oidcAvailable && !devTokenAllowed && (
        <p className="notice error">
          Sign-in is not configured. Set VITE_OIDC_AUTHORITY and VITE_OIDC_CLIENT_ID for this build.
        </p>
      )}
    </section>
  );
}
