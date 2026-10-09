import { useEffect, useState } from 'react';
import { interpretError } from '../api/conflicts';
import { ApiError } from '../api/http';
import { useSession } from '../session';
import {
  browserPushEnvironment,
  browserPushState,
  disableBrowserPush,
  enableBrowserPush,
  type BrowserPushState,
  type PushEnvironment,
} from '../lib/webPush';

/**
 * Optional browser alerts on top of the in-app bell. Shows nothing until the current state is
 * known, never prompts by itself (permission is requested only from the button), and explains
 * that alerts are hints: spots go to whoever claims first.
 */
export function PushOptIn({ env }: { env?: PushEnvironment }) {
  const { api } = useSession();
  const [state, setState] = useState<BrowserPushState>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [environment] = useState(() => env ?? browserPushEnvironment());

  useEffect(() => {
    let cancelled = false;
    browserPushState(api, environment).then(
      (next) => !cancelled && setState(next),
      () => !cancelled && setState('server-disabled'),
    );
    return () => {
      cancelled = true;
    };
  }, [api, environment]);

  const act = async (action: () => Promise<BrowserPushState>) => {
    setBusy(true);
    setError(undefined);
    try {
      setState(await action());
    } catch (err) {
      setError(err instanceof ApiError ? interpretError(err).message : 'This browser could not turn on alerts. Openings still appear in the bell.');
    } finally {
      setBusy(false);
    }
  };

  if (!state || state === 'server-disabled') return null;

  return (
    <div className="push-opt-in stack" data-testid="push-opt-in">
      {state === 'unsupported' && (
        <p className="muted small">This browser can't show alerts when TeamBuilder is closed. Openings still appear in the bell.</p>
      )}
      {state === 'available' && (
        <>
          <p className="muted small">
            Get a browser alert when a spot opens, even when TeamBuilder isn't open. Alerts are a heads-up, not a hold:
            the spot goes to whoever joins first.
          </p>
          <button disabled={busy} onClick={() => void act(() => enableBrowserPush(api, environment))}>
            Notify me when a spot opens
          </button>
        </>
      )}
      {state === 'enabled' && (
        <div className="row spread">
          <span className="notify-on small">Browser alerts on</span>
          <button
            className="link small"
            disabled={busy}
            onClick={() => void act(async () => {
              await disableBrowserPush(api, environment);
              return 'available';
            })}
          >
            Turn off browser alerts
          </button>
        </div>
      )}
      {state === 'denied' && (
        <p className="muted small">
          Alerts are blocked for this site in your browser settings. Openings still appear in the bell.
        </p>
      )}
      {error && <p className="notice error small" role="alert">{error}</p>}
    </div>
  );
}
