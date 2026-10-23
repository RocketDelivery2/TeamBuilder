import { ApiError, HttpClient, NetworkError, retryAfter } from '../src/api/http';
function setup(responses: Response[]) {
  const fetchImpl = jest.fn<ReturnType<typeof fetch>, Parameters<typeof fetch>>().mockImplementation(async () => responses.shift()!);
  const getToken = jest.fn(async () => 'fixture-token');
  const onUnauthorized = jest.fn(async () => {});
  return { http: new HttpClient({ baseUrl: 'https://example.invalid/api', getToken, onUnauthorized, fetchImpl }), fetchImpl, getToken, onUnauthorized };
}
test('uses the user API and preserves the configured proxy prefix', async () => {
  const f = setup([new Response('{"id":"p"}', { status: 200 })]);
  expect((await f.http.request<{ id: string }>('GET', '/api/v1/players/me')).data.id).toBe('p');
  expect(f.fetchImpl.mock.calls[0]?.[0]).toBe('https://example.invalid/api/api/v1/players/me');
  await expect(f.http.request('GET', 'https://other.invalid')).rejects.toThrow('Invalid API route');
});
test('401 read refreshes exactly once, then invalidates session', async () => {
  const f = setup([new Response('', { status: 401 }), new Response('', { status: 401 })]);
  await expect(f.http.request('GET', '/api/v1/players/me')).rejects.toMatchObject({ status: 401 });
  expect(f.getToken.mock.calls).toEqual([[false], [true]]);
  expect(f.onUnauthorized).toHaveBeenCalledTimes(1);
});
test.each([401, 403, 409, 429, 500])('mutations never retry status %s', async status => {
  const f = setup([new Response('{"code":"RequirementFull"}', { status })]);
  await expect(f.http.request('POST', '/api/v1/events/g/roster/claims', {})).rejects.toBeInstanceOf(ApiError);
  expect(f.fetchImpl).toHaveBeenCalledTimes(1);
});
test('honors Retry-After before another request', async () => {
  const f = setup([new Response('', { status: 429, headers: { 'Retry-After': '60' } })]);
  await expect(f.http.request('GET', '/api/v1/players/me')).rejects.toMatchObject({ status: 429, retryAfterMs: 60000 });
  await expect(f.http.request('GET', '/api/v1/players/me')).rejects.toMatchObject({ status: 429 });
  expect(f.fetchImpl).toHaveBeenCalledTimes(1);
  expect(retryAfter('not a date')).toBeUndefined();
  expect(retryAfter('Thu, 01 Jan 1970 00:00:10 GMT', 1000)).toBe(9000);
});
test('cancellation does not dispatch a request', async () => {
  const f = setup([]);
  const controller = new AbortController(); controller.abort();
  await expect(f.http.request('GET', '/api/v1/players/me', undefined, controller.signal)).rejects.toMatchObject({ kind: 'cancelled' });
  expect(f.fetchImpl).not.toHaveBeenCalled();
});
test('offline mutations fail instead of entering a queue', async () => {
  const fetchImpl = jest.fn<ReturnType<typeof fetch>, Parameters<typeof fetch>>();
  const http = new HttpClient({ baseUrl: 'https://example.invalid', fetchImpl, getToken: async () => null,
    onUnauthorized: async () => {}, isOnline: async () => false });
  await expect(http.request('POST', '/api/v1/events', {})).rejects.toBeInstanceOf(NetworkError);
  expect(fetchImpl).not.toHaveBeenCalled();
});
test('timeouts abort the transport', async () => {
  const fetchImpl: typeof fetch = async (_, options) => new Promise((_, reject) => {
    options?.signal?.addEventListener('abort', () => reject(new Error('aborted')));
  });
  const http = new HttpClient({ baseUrl: 'https://example.invalid', fetchImpl, getToken: async () => null, onUnauthorized: async () => {}, timeoutMs: 5 });
  await expect(http.request('GET', '/api/v1/players/me')).rejects.toMatchObject({ kind: 'timeout' });
});
