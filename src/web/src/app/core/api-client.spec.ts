import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiClient } from './api-client';
import { SILENT_AUTH_ERRORS } from './http-context';

describe('ApiClient', () => {
  let api: ApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts the email to sign up', () => {
    api.signUp('a@example.com').subscribe();

    const request = http.expectOne('/api/v1/signup');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'a@example.com' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('posts the email to recover a key', () => {
    api.recover('a@example.com').subscribe();

    const request = http.expectOne('/api/v1/recover');
    expect(request.request.body).toEqual({ email: 'a@example.com' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('posts the token to verify and returns the credentials', () => {
    let apiKey = '';
    api.verify('tok').subscribe((credentials) => (apiKey = credentials.api_key));

    const request = http.expectOne('/api/v1/verify');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ user_id: 'u1', email: 'a@example.com', api_key: 'secret' });
    expect(apiKey).toBe('secret');
  });

  it('reads the account, and marks a silent check', () => {
    api.getMe().subscribe();
    api.getMe(true).subscribe();

    const [normal, silent] = http.match('/api/v1/users/me');
    expect(normal.request.context.get(SILENT_AUTH_ERRORS)).toBe(false);
    expect(silent.request.context.get(SILENT_AUTH_ERRORS)).toBe(true);
    normal.flush({});
    silent.flush({});
  });

  it('rotates the key with a POST', () => {
    api.rotateKey().subscribe();

    const request = http.expectOne('/api/v1/users/me/api-key/rotate');
    expect(request.request.method).toBe('POST');
    request.flush({});
  });

  it('deletes the account with a DELETE', () => {
    api.deleteAccount().subscribe();

    const request = http.expectOne('/api/v1/users/me');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('posts a new link', () => {
    api.shorten({ long_url: 'https://example.com', custom_alias: 'my-alias' }).subscribe();

    const request = http.expectOne('/api/v1/shorten');
    expect(request.request.body).toEqual({ long_url: 'https://example.com', custom_alias: 'my-alias' });
    request.flush({});
  });

  it('lists links with the page size, and the page token only when given', () => {
    api.listLinks('user 1', 20).subscribe();
    api.listLinks('user 1', 20, 'next-page').subscribe();

    const [first, second] = http.match((request) => request.url === '/api/v1/users/user%201/urls');
    expect(first.request.params.get('page_size')).toBe('20');
    expect(first.request.params.has('page_token')).toBe(false);
    expect(second.request.params.get('page_token')).toBe('next-page');
    first.flush({});
    second.flush({});
  });

  it('encodes the short code in the analytics path', () => {
    api.getAnalytics('a/b').subscribe();

    http.expectOne('/api/v1/analytics/a%2Fb').flush({});
  });
});
