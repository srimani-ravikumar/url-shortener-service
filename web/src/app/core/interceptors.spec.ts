import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { FakeStorage } from '../../testing/fake-storage';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import { AuthService, KEY_STORAGE } from './auth.service';
import { authInterceptor, errorInterceptor, loadingInterceptor } from './interceptors';
import { LoadingService } from './loading.service';

describe('HTTP interceptors', () => {
  let storage: FakeStorage;
  let http: HttpTestingController;
  let client: HttpClient;
  let auth: AuthService;
  let navigate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    storage = new FakeStorage();
    navigate = vi.fn().mockResolvedValue(true);
    TestBed.configureTestingModule({
      providers: [
        // Same order as in app.config.ts.
        provideHttpClient(withInterceptors([loadingInterceptor, authInterceptor, errorInterceptor])),
        provideHttpClientTesting(),
        { provide: KEY_STORAGE, useValue: storage },
        { provide: Router, useValue: { navigateByUrl: navigate } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    auth = TestBed.inject(AuthService);
  });

  afterEach(() => http.verify());

  describe('API key', () => {
    it('is sent to our API when signed in', () => {
      auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'my-key' });

      client.get('/api/v1/users/me').subscribe();

      const request = http.expectOne('/api/v1/users/me');
      expect(request.request.headers.get('X-Api-Key')).toBe('my-key');
      request.flush({});
    });

    it('is not sent when signed out', () => {
      client.get('/api/v1/shorten').subscribe();

      const request = http.expectOne('/api/v1/shorten');
      expect(request.request.headers.has('X-Api-Key')).toBe(false);
      request.flush({});
    });

    it('is never sent to another address', () => {
      auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'my-key' });

      client.get('https://elsewhere.example/api/v1/anything').subscribe();

      const request = http.expectOne('https://elsewhere.example/api/v1/anything');
      expect(request.request.headers.has('X-Api-Key')).toBe(false);
      request.flush({});
    });
  });

  describe('401 handling', () => {
    it('signs the user out and goes to the start page', () => {
      auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'expired' });
      let error: unknown;

      client.get('/api/v1/users/me').subscribe({ error: (e: unknown) => (error = e) });
      http.expectOne('/api/v1/users/me').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

      expect(error).toBeInstanceOf(ApiError);
      expect(auth.signedIn()).toBe(false);
      expect(navigate).toHaveBeenCalledWith('/');
    });

    it('leaves a wrong typed key to the caller instead of ending a session', () => {
      auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'typed' });

      TestBed.inject(ApiClient).getMe(true).subscribe({ error: () => undefined });
      http.expectOne('/api/v1/users/me').flush({}, { status: 401, statusText: 'Unauthorized' });

      expect(auth.signedIn()).toBe(true);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('does nothing for a 401 when nobody is signed in', () => {
      client.get('/api/v1/users/me').subscribe({ error: () => undefined });
      http.expectOne('/api/v1/users/me').flush({}, { status: 401, statusText: 'Unauthorized' });

      expect(navigate).not.toHaveBeenCalled();
    });

    it('keeps the session on other errors', () => {
      auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'good' });

      client.get('/api/v1/users/me').subscribe({ error: () => undefined });
      http.expectOne('/api/v1/users/me').flush({}, { status: 503, statusText: 'Service Unavailable' });

      expect(auth.signedIn()).toBe(true);
    });
  });

  describe('errors', () => {
    it('arrive as ApiError with the problem details', () => {
      let error: unknown;

      client.post('/api/v1/shorten', {}).subscribe({ error: (e: unknown) => (error = e) });
      http
        .expectOne('/api/v1/shorten')
        .flush({ title: 'Alias already taken', detail: 'Choose another one.' }, { status: 409, statusText: 'Conflict' });

      expect(error).toBeInstanceOf(ApiError);
      expect((error as ApiError).status).toBe(409);
      expect((error as ApiError).userMessage).toBe('Choose another one.');
    });
  });

  describe('loading indicator', () => {
    it('is busy only while a call is in flight, also after a failure', () => {
      const loading = TestBed.inject(LoadingService);

      client.get('/api/v1/one').subscribe({ error: () => undefined });
      client.get('/api/v1/two').subscribe({ error: () => undefined });
      expect(loading.busy()).toBe(true);

      http.expectOne('/api/v1/one').flush({});
      expect(loading.busy()).toBe(true);

      http.expectOne('/api/v1/two').flush({}, { status: 500, statusText: 'Server Error' });
      expect(loading.busy()).toBe(false);
    });

    it('stops being busy when a call is cancelled', () => {
      const loading = TestBed.inject(LoadingService);

      const subscription = client.get('/api/v1/slow').subscribe();
      expect(loading.busy()).toBe(true);
      subscription.unsubscribe();

      expect(loading.busy()).toBe(false);
      expect(http.expectOne('/api/v1/slow').cancelled).toBe(true);
    });
  });
});
