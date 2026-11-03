import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { FakeStorage } from '../../testing/fake-storage';
import { ApiError } from './api-error';
import { authGuard, guestGuard } from './auth.guard';
import { AuthService, KEY_STORAGE } from './auth.service';
import { ToastService } from './toast.service';

describe('authGuard', () => {
  let storage: FakeStorage;
  let toasts: ToastService;
  let http: HttpTestingController;
  const startPage = { redirectedTo: '/' } as unknown as UrlTree;

  // Created on demand, so a test can put a key in storage before the service first reads it.
  const auth = () => TestBed.inject(AuthService);

  function runGuard() {
    return TestBed.runInInjectionContext(() => authGuard({} as never, {} as never)) as Promise<boolean | UrlTree>;
  }

  beforeEach(() => {
    storage = new FakeStorage();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: KEY_STORAGE, useValue: storage },
        { provide: Router, useValue: { parseUrl: () => startPage } },
      ],
    });
    toasts = TestBed.inject(ToastService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends a signed-out visitor to the start page without calling the API', async () => {
    expect(await runGuard()).toBe(startPage);
  });

  it('lets a signed-in user through once the account is known', async () => {
    auth().acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });

    expect(await runGuard()).toBe(true);
  });

  it('loads the account for a key kept from an earlier page load, then lets the user through', async () => {
    storage.setItem('trysmartly.apiKey', 'kept');

    const result = runGuard();
    // Let the guard reach its request before answering it.
    await Promise.resolve();
    http.expectOne('/api/v1/users/me').flush({ user_id: 'u1', email: 'a@example.com', created_at: 'x' });

    expect(await result).toBe(true);
  });

  it('redirects without a toast when the key is rejected (the session already ended)', async () => {
    auth().acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });
    vi.spyOn(auth(), 'ensureAccount').mockRejectedValue(new ApiError(401, 'Unauthorized', null, {}, null));

    expect(await runGuard()).toBe(startPage);
    expect(toasts.toasts()).toEqual([]);
  });

  it('redirects with an explanation when the account cannot be loaded for another reason', async () => {
    auth().acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });
    vi.spyOn(auth(), 'ensureAccount').mockRejectedValue(new ApiError(503, 'Unavailable', 'Try again shortly.', {}, 30));

    expect(await runGuard()).toBe(startPage);
    expect(toasts.toasts().map((toast) => toast.message)).toEqual(['Try again shortly.']);
  });
});

describe('guestGuard', () => {
  const linksPage = { redirectedTo: '/links' } as unknown as UrlTree;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: KEY_STORAGE, useValue: new FakeStorage() },
        { provide: Router, useValue: { parseUrl: (url: string) => (url === '/links' ? linksPage : undefined) } },
      ],
    });
  });

  it('lets a visitor see the sign-in page', () => {
    expect(TestBed.runInInjectionContext(() => guestGuard({} as never, {} as never))).toBe(true);
  });

  it('sends a signed-in user to their links', () => {
    TestBed.inject(AuthService).acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });

    expect(TestBed.runInInjectionContext(() => guestGuard({} as never, {} as never))).toBe(linksPage);
  });
});
