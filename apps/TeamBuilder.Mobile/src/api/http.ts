export interface ProblemDetails {
  status?: number; title?: string; detail?: string; code?: string; errors?: Record<string, string[]>;
}
export class ApiError extends Error {
  constructor(readonly status: number, readonly problem?: ProblemDetails,
    readonly retryAfterMs?: number, readonly correlationId?: string) {
    super(problem?.detail ?? problem?.title ?? ('Request failed (' + status + ')'));
    this.name = 'ApiError';
  }
  get code() { return this.problem?.code; }
}
export class NetworkError extends Error {
  constructor(readonly kind: 'offline' | 'timeout' | 'cancelled') { super(kind); this.name = 'NetworkError'; }
}
export interface ApiResponse<T> { status: number; data: T }
export interface HttpOptions {
  baseUrl: string;
  getToken: (refresh?: boolean) => Promise<string | null>;
  onUnauthorized: (token: string | null) => Promise<void>;
  isOnline?: () => Promise<boolean>;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}
export function retryAfter(value: string | null, now = Date.now()): number | undefined {
  if (!value) return undefined;
  const seconds = Number(value);
  const ms = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(value) - now;
  return Number.isFinite(ms) ? Math.max(0, ms) : undefined;
}
function problem(value: unknown): ProblemDetails | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const v = value as Record<string, unknown>;
  return { title: typeof v.title === 'string' ? v.title : undefined,
    detail: typeof v.detail === 'string' ? v.detail : undefined,
    code: typeof v.code === 'string' ? v.code : undefined };
}
export class HttpClient {
  private readonly fetchImpl: typeof fetch;
  private throttledUntil = 0;
  constructor(private readonly options: HttpOptions) { this.fetchImpl = options.fetchImpl ?? fetch; }
  async request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<ApiResponse<T>> {
    if (!path.startsWith('/api/v1/') || path.includes('://')) throw new Error('Invalid API route.');
    if (method !== 'GET' && this.options.isOnline && !await this.options.isOnline()) throw new NetworkError('offline');
    if (Date.now() < this.throttledUntil) throw new ApiError(429, undefined, this.throttledUntil - Date.now());
    for (let attempt = 0; attempt < 2; attempt++) {
      if (signal?.aborted) throw new NetworkError('cancelled');
      const token = await this.options.getToken(attempt === 1);
      if (signal?.aborted) throw new NetworkError('cancelled');
      const controller = new AbortController();
      const cancel = () => controller.abort();
      signal?.addEventListener('abort', cancel, { once: true });
      let timedOut = false;
      const timer = setTimeout(() => { timedOut = true; controller.abort(); }, this.options.timeoutMs ?? 15000);
      try {
        const response = await this.fetchImpl(this.options.baseUrl + path, {
          method, signal: controller.signal, redirect: 'error',
          headers: { Accept: 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}),
            ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
          body: body === undefined ? undefined : JSON.stringify(body),
        });
        const raw = await response.text();
        if (signal?.aborted) throw new NetworkError('cancelled');
        let data: unknown;
        try { data = raw ? JSON.parse(raw) : undefined; } catch {
          if (response.ok) throw new ApiError(502, { title: 'The server returned an unreadable response.' });
        }
        // Only a read can be replayed. Roster and creation mutations are never transport-retried.
        if (response.status === 401 && attempt === 0 && method === 'GET') continue;
        if (response.status === 401) await this.options.onUnauthorized(token);
        const delay = retryAfter(response.headers.get('Retry-After'));
        if (response.status === 429 && delay !== undefined) this.throttledUntil = Date.now() + delay;
        if (!response.ok) throw new ApiError(response.status, problem(data), delay,
          response.headers.get('X-Correlation-ID') ?? undefined);
        return { status: response.status, data: data as T };
      } catch (error) {
        if (error instanceof ApiError || error instanceof NetworkError) throw error;
        if (signal?.aborted) throw new NetworkError('cancelled');
        if (timedOut) throw new NetworkError('timeout');
        throw new NetworkError('offline');
      } finally {
        clearTimeout(timer);
        signal?.removeEventListener('abort', cancel);
      }
    }
    throw new ApiError(401);
  }
}
export function errorMessage(error: unknown): string {
  if (error instanceof NetworkError) return error.kind === 'timeout' ? 'The request timed out. Refresh before trying a change again.' :
    error.kind === 'cancelled' ? 'Request cancelled.' : 'Unable to connect. Check your internet connection and retry.';
  if (error instanceof ApiError) {
    if (error.status === 401) return 'Your session expired. Sign in again.';
    if (error.status === 403) return 'You do not have permission for this action. Refresh to check your current access.';
    if (error.status === 429) return error.retryAfterMs !== undefined ?
      'Too many requests. Try again in ' + Math.ceil(error.retryAfterMs / 1000) + ' seconds.' : 'Too many requests. Please wait before retrying.';
    const conflicts: Record<string, string> = {
      RequirementFull: 'This spot has already been taken. The roster has been refreshed.',
      AlreadyParticipating: 'You are already participating in this game.',
      RosterChanged: 'The roster changed. Review the refreshed roster and try again.',
      OccurrenceChanged: 'The game changed. Refresh before trying again.',
      OccurrenceClosed: 'This game no longer accepts roster changes.',
      AssignmentEnded: 'This participation has already ended.',
    };
    return (error.code && conflicts[error.code]) || (error.status === 409 ? 'This action conflicts with the current state. Refresh and try again.' :
      error.status === 400 ? 'Check your entries and try again.' : 'The server could not complete the request. Please retry.');
  }
  return 'Unable to complete this action. Please retry.';
}
