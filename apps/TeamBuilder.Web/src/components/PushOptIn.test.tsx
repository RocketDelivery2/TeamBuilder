import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { PushEnvironment } from '../lib/webPush';
import { SessionContext } from '../session';
import { PushOptIn } from './PushOptIn';

const KEY = 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4';

function setup(options: { enabled?: boolean; permission?: NotificationPermission; answer?: NotificationPermission; subscribeFails?: boolean } = {}) {
  let subscription: unknown = null;
  const pushManager = {
    getSubscription: vi.fn(async () => subscription),
    subscribe: vi.fn(async () => {
      if (options.subscribeFails) throw new DOMException('Registration failed - push service error', 'AbortError');
      subscription = {
        endpoint: 'https://fcm.googleapis.com/fcm/send/x',
        toJSON: () => ({ endpoint: 'https://fcm.googleapis.com/fcm/send/x', keys: { p256dh: 'p', auth: 'a' } }),
        unsubscribe: vi.fn(async () => true),
      };
      return subscription;
    }),
  };
  const requestPermission = vi.fn(async () => options.answer ?? 'granted');
  const env: PushEnvironment = {
    notification: { permission: options.permission ?? 'default', requestPermission },
    serviceWorker: { register: vi.fn(async () => ({ pushManager })), getRegistration: vi.fn(async () => ({ pushManager })) } as unknown as PushEnvironment['serviceWorker'],
    pushSupported: true,
  };
  const api = {
    pushConfig: vi.fn(async () => ({ enabled: options.enabled ?? true, vapidPublicKey: options.enabled === false ? null : KEY })),
    registerPush: vi.fn(async () => ({})),
    unregisterPush: vi.fn(async () => {}),
  } as unknown as TeamBuilderApi;
  render(
    <SessionContext.Provider value={{ api, me: { id: 'me', username: 'me' }, signOut: () => {} }}>
      <PushOptIn env={env} />
    </SessionContext.Provider>,
  );
  return { requestPermission, api };
}

describe('PushOptIn', () => {
  it('explains the alert and does not prompt until the button is pressed', async () => {
    const { requestPermission } = setup();
    const button = await screen.findByRole('button', { name: 'Notify me when a spot opens' });
    expect(screen.getByText(/not a hold/)).toBeInTheDocument();
    expect(requestPermission).not.toHaveBeenCalled();

    fireEvent.click(button);
    expect(await screen.findByText('Browser alerts on')).toBeInTheDocument();
    expect(requestPermission).toHaveBeenCalledTimes(1);
  });

  it('renders nothing when the server has Web Push off', async () => {
    const { api } = setup({ enabled: false });
    await waitFor(() => expect(api.pushConfig).toHaveBeenCalled());
    expect(screen.queryByTestId('push-opt-in')).not.toBeInTheDocument();
  });

  it('explains a blocked permission without offering a prompt', async () => {
    setup({ permission: 'denied' });
    expect(await screen.findByText(/blocked for this site/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('handles a denial at the prompt gracefully', async () => {
    setup({ answer: 'denied' });
    fireEvent.click(await screen.findByRole('button', { name: 'Notify me when a spot opens' }));
    expect(await screen.findByText(/blocked for this site/)).toBeInTheDocument();
  });

  it('keeps the app usable when the browser cannot subscribe', async () => {
    setup({ subscribeFails: true });
    fireEvent.click(await screen.findByRole('button', { name: 'Notify me when a spot opens' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Openings still appear in the bell');
    expect(screen.getByRole('button', { name: 'Notify me when a spot opens' })).toBeEnabled();
  });
});
