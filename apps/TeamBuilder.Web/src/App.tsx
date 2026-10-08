import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { HttpClient, ApiError } from './api/http';
import { TeamBuilderApi } from './api/teamBuilderApi';
import type { PlayerProfile } from './api/types';
import { DevTokenAuthAdapter, rememberReturnPath, takeReturnPath, type AuthAdapter } from './auth/authAdapter';
import { OidcAuthAdapter } from './auth/oidcAuthAdapter';
import { config } from './config';
import { navigate, useRoute } from './router';
import { SessionContext } from './session';
import { SignIn } from './pages/SignIn';
import { Onboard } from './pages/Onboard';
import { MyGames } from './pages/MyGames';
import { CreateGame } from './pages/CreateGame';
import { GameDetail } from './pages/GameDetail';
import { Discover } from './pages/Discover';

type Phase =
  | { name: 'starting' }
  | { name: 'signed-out'; message?: string }
  | { name: 'onboarding'; api: TeamBuilderApi; adapter: AuthAdapter }
  | { name: 'ready'; api: TeamBuilderApi; adapter: AuthAdapter; me: PlayerProfile }
  | { name: 'error'; message: string };

const oidc = config.oidc ? new OidcAuthAdapter(config.oidc) : null;

function apiFor(adapter: AuthAdapter): TeamBuilderApi {
  return new TeamBuilderApi(new HttpClient({ baseUrl: config.apiBaseUrl, getToken: () => adapter.getAccessToken() }));
}

export function App() {
  const route = useRoute();
  const [phase, setPhase] = useState<Phase>({ name: 'starting' });
  const started = useRef(false);

  const start = useCallback(async (adapter: AuthAdapter) => {
    const api = apiFor(adapter);
    try {
      setPhase({ name: 'ready', api, adapter, me: await api.getMe() });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) setPhase({ name: 'onboarding', api, adapter });
      else if (error instanceof ApiError && error.status === 401) {
        await adapter.signOut();
        setPhase({ name: 'signed-out', message: 'Your sign-in was not accepted or has expired. Please sign in again.' });
      } else setPhase({ name: 'error', message: error instanceof Error ? error.message : 'Could not reach TeamBuilder.' });
    }
  }, []);

  // Runs once at startup (the ref also keeps StrictMode from completing a sign-in twice).
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    (async () => {
      if (route.name === 'auth-callback' && oidc) {
        try {
          await oidc.completeSignIn();
          navigate(takeReturnPath());
        } catch {
          setPhase({ name: 'signed-out', message: 'Sign-in did not complete. Please try again.' });
          return;
        }
      }
      if (oidc && (await oidc.hasSession())) return start(oidc);
      if (config.devTokenAllowed && DevTokenAuthAdapter.hasToken()) return start(new DevTokenAuthAdapter());
      setPhase({ name: 'signed-out' });
    })();
  }, [route.name, start]);

  const signOut = useCallback(() => {
    if (phase.name === 'ready' || phase.name === 'onboarding') void phase.adapter.signOut();
    setPhase({ name: 'signed-out' });
  }, [phase]);

  const session = useMemo(
    () => (phase.name === 'ready' ? { api: phase.api, me: phase.me, signOut } : null),
    [phase, signOut],
  );

  const devBanner = config.devTokenAllowed && !import.meta.env.DEV && (
    <div className="dev-banner" role="status">Private QA build: manual token sign-in is enabled.</div>
  );

  let body: JSX.Element;
  switch (phase.name) {
    case 'starting':
      body = <p className="muted center">Loading…</p>;
      break;
    case 'error':
      body = <p className="notice error">{phase.message}</p>;
      break;
    case 'signed-out':
      body = (
        <SignIn
          message={phase.message}
          oidcAvailable={!!oidc}
          devTokenAllowed={config.devTokenAllowed}
          onOidcSignIn={() => {
            rememberReturnPath(window.location.pathname);
            void oidc?.signIn();
          }}
          onDevToken={(token) => {
            DevTokenAuthAdapter.save(token);
            void start(new DevTokenAuthAdapter());
          }}
        />
      );
      break;
    case 'onboarding':
      body = <Onboard api={phase.api} onDone={(me) => setPhase({ name: 'ready', api: phase.api, adapter: phase.adapter, me })} onSignOut={signOut} />;
      break;
    case 'ready':
      body =
        route.name === 'create' ? <CreateGame /> :
        route.name === 'discover' ? <Discover /> :
        route.name === 'game' ? <GameDetail occurrenceId={route.id} /> :
        route.name === 'not-found' ? <p className="notice">Page not found.</p> :
        <MyGames />;
      break;
  }

  return (
    <SessionContext.Provider value={session}>
      {devBanner}
      <header className="app-header">
        <a href="/" className="brand" onClick={(e) => { e.preventDefault(); navigate('/'); }}>TeamBuilder</a>
        {session && (
          <div className="header-actions">
            <a href="/discover" className="nav-link" onClick={(e) => { e.preventDefault(); navigate('/discover'); }}>Discover</a>
            <span className="muted small header-name">{session.me.displayName || session.me.username}</span>
            <button className="link" onClick={signOut}>Sign out</button>
          </div>
        )}
      </header>
      <main className="app-main">{body}</main>
    </SessionContext.Provider>
  );
}
