import { HttpErrorResponse } from '@angular/common/http';

/** An error from the API (or from reaching it), in a shape the UI can show without knowing HTTP. */
export class ApiError extends Error {
  constructor(
    /** HTTP status; 0 means the server could not be reached at all. */
    readonly status: number,
    readonly title: string,
    readonly detail: string | null,
    /** Messages per request field, keyed by the snake_case field name (e.g. "long_url"). */
    readonly fieldErrors: Readonly<Record<string, readonly string[]>>,
    /** Seconds the server asked the caller to wait (429 and 503), if it said. */
    readonly retryAfterSeconds: number | null,
  ) {
    super(detail ?? title);
    this.name = 'ApiError';
  }

  /** The first message for a request field, or null. */
  fieldError(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }

  /** A sentence that is safe to show to the user. */
  get userMessage(): string {
    return this.detail ?? this.title;
  }
}

/** The ApiError's message, or a generic one for anything unexpected (a bug must not show its internals). */
export function describeError(error: unknown): string {
  return error instanceof ApiError ? error.userMessage : 'Something went wrong. Please try again.';
}

/** The RFC 7807 problem document the API answers errors with (only the parts the UI reads). */
interface ProblemBody {
  title?: unknown;
  detail?: unknown;
  errors?: unknown;
}

/** Converts a failed HTTP call into an {@link ApiError}. */
export function toApiError(response: HttpErrorResponse): ApiError {
  const retryAfter = Number(response.headers?.get('Retry-After'));
  const retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : null;

  if (response.status === 0) {
    return new ApiError(0, 'Cannot reach the server', 'Check your connection and try again.', {}, null);
  }

  const body = isObject(response.error) ? (response.error as ProblemBody) : {};
  const title = typeof body.title === 'string' ? body.title : fallbackTitle(response.status);
  const detail = typeof body.detail === 'string' ? body.detail : null;

  return new ApiError(response.status, title, detail, readFieldErrors(body.errors), retryAfterSeconds);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

// Validation problems carry {"errors": {"field": ["message", ...]}}; anything else is ignored.
function readFieldErrors(errors: unknown): Record<string, string[]> {
  const result: Record<string, string[]> = {};
  if (!isObject(errors)) {
    return result;
  }

  for (const [field, messages] of Object.entries(errors)) {
    if (Array.isArray(messages)) {
      result[field] = messages.filter((message): message is string => typeof message === 'string');
    }
  }

  return result;
}

function fallbackTitle(status: number): string {
  if (status === 429) {
    return 'Too many requests';
  }

  return status >= 500 ? 'Something went wrong on our side' : 'The request failed';
}
