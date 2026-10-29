import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AccountResponse,
  AnalyticsResponse,
  CreateShortUrlRequest,
  CredentialsResponse,
  ShortUrlResponse,
  UserUrlsResponse,
} from './api.models';
import { SILENT_AUTH_ERRORS } from './http-context';

// Relative, so the same build works behind nginx and (through the dev proxy) under `ng serve`.
const API = '/api/v1';

/**
 * One typed method per Shortener API endpoint. Failures arrive as {@link ApiError}
 * and the X-Api-Key header is added for signed-in users by the interceptors.
 */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  /** Asks for a sign-up mail. Always succeeds for a valid address, known or not. */
  signUp(email: string): Observable<void> {
    return this.http.post<void>(`${API}/signup`, { email });
  }

  /** Asks for a mail that lets the owner of the address replace a lost key. */
  recover(email: string): Observable<void> {
    return this.http.post<void>(`${API}/recover`, { email });
  }

  /** Spends the token from an emailed link; returns the new API key (shown only here). */
  verify(token: string): Observable<CredentialsResponse> {
    return this.http.post<CredentialsResponse>(`${API}/verify`, { token });
  }

  /**
   * The signed-in user's account. With `silent`, a 401 is just an error for the caller
   * instead of ending the session (used when checking a key that was typed in).
   */
  getMe(silent = false): Observable<AccountResponse> {
    const context = new HttpContext().set(SILENT_AUTH_ERRORS, silent);
    return this.http.get<AccountResponse>(`${API}/users/me`, { context });
  }

  /** Issues a new key and revokes the current one. */
  rotateKey(): Observable<CredentialsResponse> {
    return this.http.post<CredentialsResponse>(`${API}/users/me/api-key/rotate`, null);
  }

  /** Deletes the account and every link it owns. */
  deleteAccount(): Observable<void> {
    return this.http.delete<void>(`${API}/users/me`);
  }

  shorten(request: CreateShortUrlRequest): Observable<ShortUrlResponse> {
    return this.http.post<ShortUrlResponse>(`${API}/shorten`, request);
  }

  /** One page of the user's links, newest first. */
  listLinks(userId: string, pageSize = 50, pageToken?: string): Observable<UserUrlsResponse> {
    let params = new HttpParams().set('page_size', pageSize);
    if (pageToken) {
      params = params.set('page_token', pageToken);
    }

    return this.http.get<UserUrlsResponse>(`${API}/users/${encodeURIComponent(userId)}/urls`, { params });
  }

  getAnalytics(shortCode: string): Observable<AnalyticsResponse> {
    return this.http.get<AnalyticsResponse>(`${API}/analytics/${encodeURIComponent(shortCode)}`);
  }
}
