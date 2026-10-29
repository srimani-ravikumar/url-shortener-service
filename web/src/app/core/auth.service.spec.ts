import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FakeStorage } from '../../testing/fake-storage';
import { AuthService, KEY_STORAGE } from './auth.service';

describe('AuthService', () => {
  let storage: FakeStorage;
  let http: HttpTestingController;

  function createService(): AuthService {
    return TestBed.inject(AuthService);
  }

  beforeEach(() => {
    storage = new FakeStorage();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: KEY_STORAGE, useValue: storage }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('starts signed out when no key is stored', () => {
    const auth = createService();

    expect(auth.signedIn()).toBe(false);
    expect(auth.apiKey()).toBeNull();
  });

  it('starts signed in when a key was stored earlier in the session', () => {
    storage.setItem('trysmartly.apiKey', 'stored-key');

    const auth = createService();

    expect(auth.signedIn()).toBe(true);
    expect(auth.apiKey()).toBe('stored-key');
  });

  it('signs in with a typed key after the API has accepted it', async () => {
    const auth = createService();

    const result = auth.signIn('  typed-key  ');
    const request = http.expectOne('/api/v1/users/me');
    request.flush({ user_id: 'u1', email: 'a@example.com', created_at: '2026-01-01T00:00:00Z' });
    const account = await result;

    expect(account).toEqual({ user_id: 'u1', email: 'a@example.com' });
    expect(auth.apiKey()).toBe('typed-key');
    expect(storage.getItem('trysmartly.apiKey')).toBe('typed-key');
    expect(auth.account()).toEqual(account);
  });

  it('stays signed out and rethrows when the typed key is wrong', async () => {
    const auth = createService();

    const result = auth.signIn('wrong');
    http.expectOne('/api/v1/users/me').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toBeDefined();
    expect(auth.signedIn()).toBe(false);
    expect(storage.getItem('trysmartly.apiKey')).toBeNull();
  });

  it('accepts the credentials of a new key without asking the API again', () => {
    const auth = createService();

    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'fresh' });

    expect(auth.apiKey()).toBe('fresh');
    expect(storage.getItem('trysmartly.apiKey')).toBe('fresh');
    expect(auth.account()).toEqual({ user_id: 'u1', email: 'a@example.com' });
  });

  it('replaces the previous key and account when new credentials arrive', () => {
    const auth = createService();
    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'first' });

    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'second' });

    expect(auth.apiKey()).toBe('second');
    expect(storage.getItem('trysmartly.apiKey')).toBe('second');
  });

  it('loads the account once for a key kept from an earlier page load', async () => {
    storage.setItem('trysmartly.apiKey', 'stored-key');
    const auth = createService();

    const first = auth.ensureAccount();
    http.expectOne('/api/v1/users/me').flush({ user_id: 'u1', email: 'a@example.com', created_at: 'x' });
    await first;
    const second = await auth.ensureAccount(); // answered from memory: no second request

    expect(second).toEqual({ user_id: 'u1', email: 'a@example.com' });
  });

  it('forgets everything on sign-out', () => {
    const auth = createService();
    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'fresh' });

    auth.signOut();

    expect(auth.signedIn()).toBe(false);
    expect(auth.account()).toBeNull();
    expect(storage.getItem('trysmartly.apiKey')).toBeNull();
  });
});
