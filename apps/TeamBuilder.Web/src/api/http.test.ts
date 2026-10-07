import { describe, expect, it, vi } from 'vitest';
import { ApiError, HttpClient } from './http';

describe('HttpClient', () => {
  it('sends the bearer token and JSON body, and parses problem details with their code', async () => {
    const fetchImpl = vi.fn(async () =>
      new Response(JSON.stringify({ status: 409, title: 'Conflict', detail: 'Full.', code: 'RequirementFull' }), { status: 409 }),
    );
    const client = new HttpClient({ baseUrl: 'https://api.test/', getToken: () => 'tok', fetchImpl });

    const error = await client.request('POST', '/api/v1/x', { a: 1 }).catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 409, code: 'RequirementFull', message: 'Full.' });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('https://api.test/api/v1/x');
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer tok');
    expect(init.body).toBe('{"a":1}');
  });

  it('omits Authorization when there is no token', async () => {
    const fetchImpl = vi.fn(async () => new Response('{}', { status: 200 }));
    await new HttpClient({ baseUrl: '', getToken: () => null, fetchImpl }).request('GET', '/x');
    const [, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined();
  });
});
