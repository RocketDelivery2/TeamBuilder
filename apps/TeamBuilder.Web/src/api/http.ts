import type { ProblemDetails } from './types';

/** A non-2xx API response, carrying the HTTP status and the stable problem `code` if any. */
export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly problem?: ProblemDetails;

  constructor(status: number, problem?: ProblemDetails, fallbackMessage?: string) {
    super(problem?.detail ?? problem?.title ?? fallbackMessage ?? `Request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.code = problem?.code;
    this.problem = problem;
  }
}

export interface ApiResponse<T> {
  status: number;
  data: T;
}

export type TokenSource = () => Promise<string | null> | string | null;

export interface HttpClientOptions {
  baseUrl: string;
  getToken: TokenSource;
  fetchImpl?: typeof fetch;
}

/**
 * Minimal JSON client. Attaches the bearer token when one is available, parses problem
 * details on failure, and never retries by itself: retry policy lives in the callers that
 * know which conflicts are safe to retry.
 */
export class HttpClient {
  private readonly baseUrl: string;
  private readonly getToken: TokenSource;
  private readonly fetchImpl: typeof fetch;

  constructor(options: HttpClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '');
    this.getToken = options.getToken;
    this.fetchImpl = options.fetchImpl ?? ((input, init) => fetch(input, init));
  }

  async request<T>(method: string, path: string, body?: unknown): Promise<ApiResponse<T>> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    const token = await this.getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const response = await this.fetchImpl(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    const text = await response.text();
    const parsed = text ? safeJson(text) : undefined;

    if (!response.ok) {
      throw new ApiError(response.status, isProblem(parsed) ? parsed : undefined, response.statusText);
    }

    return { status: response.status, data: parsed as T };
  }
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function isProblem(value: unknown): value is ProblemDetails {
  return typeof value === 'object' && value !== null;
}
