/** Shapes of the Shortener API's JSON (property names are snake_case, exactly as on the wire). */

/** Body of POST /api/v1/shorten. */
export interface CreateShortUrlRequest {
  long_url: string;
  /** 3-50 letters, digits, '-' or '_'; leave out for a generated code. */
  custom_alias?: string;
  /** ISO 8601 date-time; leave out for the default (one year). */
  expires_at?: string;
}

export interface ShortUrlResponse {
  short_url: string;
  long_url: string;
  expires_at: string;
  created_at: string;
}

export interface AnalyticsResponse {
  short_code: string;
  total_clicks: number;
  created_at: string;
  /** Null when the link was never followed. */
  last_accessed: string | null;
}

export interface UserUrlItem {
  short_code: string;
  long_url: string;
  created_at: string;
  expires_at: string;
}

export interface UserUrlsResponse {
  items: UserUrlItem[];
  /** Pass as page_token to get the next page; null on the last page. */
  next_page_token: string | null;
}

/** A newly issued API key. It is shown once and cannot be looked up later. */
export interface CredentialsResponse {
  user_id: string;
  email: string;
  api_key: string;
}

export interface AccountResponse {
  user_id: string;
  email: string;
  created_at: string;
}

/** What the app needs to remember about the signed-in user. */
export interface AccountSummary {
  user_id: string;
  email: string;
}
