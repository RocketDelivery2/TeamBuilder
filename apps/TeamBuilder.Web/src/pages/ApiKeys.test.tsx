import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { ApiKey } from '../api/types';
import { SessionContext } from '../session';
import { ApiKeys } from './ApiKeys';

const key = (id: string, overrides: Partial<ApiKey> = {}): ApiKey => ({
  id,
  name: `Key ${id}`,
  keyPrefix: `tbk_${id.padEnd(12, '0')}`,
  scopes: ['rosters:read'],
  createdAtUtc: '2026-10-20T08:00:00Z',
  expiresAtUtc: null,
  lastUsedAtUtc: null,
  revokedAtUtc: null,
  isActive: true,
  ...overrides,
});

function renderWith(api: Partial<TeamBuilderApi>) {
  return render(
    <SessionContext.Provider value={{ api: api as TeamBuilderApi, me: { id: 'me', username: 'me' }, signOut: () => {} }}>
      <ApiKeys />
    </SessionContext.Provider>,
  );
}

afterEach(() => vi.restoreAllMocks());

describe('ApiKeys', () => {
  it('creates a key, shows it once, and lists only its prefix', async () => {
    const secret = 'tbk_aaaaaaaaaaaa_SECRETSECRETSECRETSECRETSECRETSECRETSECRET123';
    const stored = key('aaaaaaaaaaaa', { name: 'League website', expiresAtUtc: '2027-01-18T08:00:00Z' });
    const apiKeys = vi.fn().mockResolvedValueOnce([]).mockResolvedValue([stored]);
    const createApiKey = vi.fn().mockResolvedValue({ ...stored, key: secret });

    renderWith({ apiKeys, createApiKey });
    await screen.findByText('No keys yet.');
    fireEvent.change(screen.getByPlaceholderText('League website'), { target: { value: '  League website ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));

    expect(await screen.findByLabelText('New API key')).toHaveValue(secret);
    expect(createApiKey).toHaveBeenCalledWith('League website', 90);
    const row = await screen.findByTestId('api-key');
    expect(row).toHaveTextContent('tbk_aaaaaaaaaaaa…');
    expect(row).not.toHaveTextContent('SECRET');

    fireEvent.click(screen.getByRole('button', { name: "I've saved it" }));
    expect(screen.queryByTestId('new-key')).not.toBeInTheDocument();
  });

  it('sends no expiry for "Never"', async () => {
    const createApiKey = vi.fn().mockResolvedValue({ ...key('b'), key: 'k' });
    renderWith({ apiKeys: vi.fn().mockResolvedValue([]), createApiKey });
    await screen.findByText('No keys yet.');

    fireEvent.change(screen.getByPlaceholderText('League website'), { target: { value: 'Scoreboard' } });
    fireEvent.change(screen.getByLabelText('Expires'), { target: { value: 'never' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));

    await waitFor(() => expect(createApiKey).toHaveBeenCalledWith('Scoreboard', undefined));
  });

  it('revokes an active key after confirmation and shows revoked keys without a button', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const active = key('c');
    const apiKeys = vi.fn()
      .mockResolvedValueOnce([active, key('d', { isActive: false, revokedAtUtc: '2026-10-19T00:00:00Z' })])
      .mockResolvedValue([{ ...active, isActive: false, revokedAtUtc: '2026-10-20T09:00:00Z' }]);
    const revokeApiKey = vi.fn(async () => {});

    renderWith({ apiKeys, revokeApiKey });
    expect(await screen.findAllByTestId('api-key')).toHaveLength(2);
    expect(screen.getAllByRole('button', { name: 'Revoke' })).toHaveLength(1);
    expect(screen.getByText('Revoked')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Revoke' }));

    await waitFor(() => expect(revokeApiKey).toHaveBeenCalledWith('c'));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument());
  });

  it('shows the server message when the key limit is reached', async () => {
    const createApiKey = vi.fn().mockRejectedValue(
      new ApiError(409, { code: 'ApiKeyLimitReached', detail: 'You already have 10 active API keys. Revoke one before creating another.' }),
    );
    renderWith({ apiKeys: vi.fn().mockResolvedValue([]), createApiKey });
    await screen.findByText('No keys yet.');

    fireEvent.change(screen.getByPlaceholderText('League website'), { target: { value: 'Eleventh' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create key' }));

    expect(await screen.findByText(/already have 10 active API keys/)).toBeInTheDocument();
  });
});
