import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AccountSummary, CredentialsResponse } from './api.models';
import { ApiClient } from './api-client';

/** Where the key is kept. A token so tests can swap in a fake instead of the real browser storage. */
export const KEY_STORAGE = new InjectionToken<Storage>('KEY_STORAGE', {
  providedIn: 'root',
  // sessionStorage ends with the tab, which limits how long a key can leak from a shared computer.
  factory: () => sessionStorage,
});

const STORAGE_KEY = 'trysmartly.apiKey';

/**
 * Who is signed in. The API key is the whole identity: it is kept in session storage, never in a URL or a
 * cookie, and removed on sign-out, on a 401 and after the key is rotated or the account deleted.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly storage = inject(KEY_STORAGE);
  private readonly api = inject(ApiClient);

  private readonly keySignal = signal<string | null>(this.storage.getItem(STORAGE_KEY));
  private readonly accountSignal = signal<AccountSummary | null>(null);

  /** The current key, or null when signed out. Read by the HTTP interceptor. */
  readonly apiKey = this.keySignal.asReadonly();

  readonly signedIn = computed(() => this.keySignal() !== null);

  /** The signed-in user; null until it has been loaded (see {@link ensureAccount}). */
  readonly account = this.accountSignal.asReadonly();

  /**
   * Signs in with a key the user typed. The key is checked with the API first;
   * a wrong key leaves the user signed out and rethrows the {@link ApiError}.
   */
  async signIn(apiKey: string): Promise<AccountSummary> {
    this.storeKey(apiKey.trim());

    try {
      const account = await firstValueFrom(this.api.getMe(true));
      this.accountSignal.set({ user_id: account.user_id, email: account.email });
      return this.accountSignal() as AccountSummary;
    } catch (error) {
      this.signOut();
      throw error;
    }
  }

  /** Signs in with a key the API has just issued (after verify or rotate); no extra check is needed. */
  acceptCredentials(credentials: CredentialsResponse): void {
    this.storeKey(credentials.api_key);
    this.accountSignal.set({ user_id: credentials.user_id, email: credentials.email });
  }

  /**
   * Makes sure the account is loaded, which a key stored from an earlier page load does not give us.
   * Throws when the key is no longer valid (the interceptor has then already signed the user out).
   */
  async ensureAccount(): Promise<AccountSummary> {
    const known = this.accountSignal();
    if (known) {
      return known;
    }

    const account = await firstValueFrom(this.api.getMe());
    const summary = { user_id: account.user_id, email: account.email };
    this.accountSignal.set(summary);
    return summary;
  }

  signOut(): void {
    this.storage.removeItem(STORAGE_KEY);
    this.keySignal.set(null);
    this.accountSignal.set(null);
  }

  private storeKey(apiKey: string): void {
    this.storage.setItem(STORAGE_KEY, apiKey);
    this.keySignal.set(apiKey);
    this.accountSignal.set(null);
  }
}
