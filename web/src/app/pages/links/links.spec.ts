import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { buttonWithText, find, provideTestApp, settle, textOf, typeInto } from '../../../testing/helpers';
import { UserUrlItem } from '../../core/api.models';
import { AuthService } from '../../core/auth.service';
import { Links } from './links';

describe('Links', () => {
  let http: HttpTestingController;

  const item = (code: string, expiresAt = '2099-01-01T00:00:00Z'): UserUrlItem => ({
    short_code: code,
    long_url: `https://example.com/${code}`,
    created_at: '2026-10-29T10:00:00Z',
    expires_at: expiresAt,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: provideTestApp() });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthService).acceptCredentials({ user_id: 'user 1', email: 'a@example.com', api_key: 'k' });
  });

  afterEach(() => http.verify());

  function render() {
    const fixture = TestBed.createComponent(Links);
    fixture.detectChanges();
    return fixture;
  }

  const listUrl = '/api/v1/users/user%201/urls';

  it('loads the first page for the signed-in user and shows each link with its copy and statistics actions', async () => {
    const fixture = render();

    const request = http.expectOne((r) => r.url === listUrl);
    expect(request.request.params.get('page_size')).toBe('20');
    expect(request.request.params.has('page_token')).toBe(false);
    request.flush({ items: [item('aaa'), item('bbb')], next_page_token: null });
    await settle(fixture);

    const text = textOf(fixture);
    expect(text).toContain('/aaa');
    expect(text).toContain('https://example.com/bbb');
    expect(fixture.nativeElement.querySelectorAll('li.link')).toHaveLength(2);
    expect(find(fixture, 'a[href="/links/aaa"]').textContent).toContain('Clicks');
    expect(text).not.toContain('Load more');
  });

  it('says so when there are no links yet', async () => {
    const fixture = render();

    http.expectOne((r) => r.url === listUrl).flush({ items: [], next_page_token: null });
    await settle(fixture);

    expect(textOf(fixture)).toContain('You have no links yet');
  });

  it('marks expired links', async () => {
    const fixture = render();

    http.expectOne((r) => r.url === listUrl).flush({ items: [item('old', '2000-01-01T00:00:00Z')], next_page_token: null });
    await settle(fixture);

    expect(find(fixture, '.badge').textContent).toContain('Expired');
  });

  it('appends the next page when asked, using the page token', async () => {
    const fixture = render();
    http.expectOne((r) => r.url === listUrl).flush({ items: [item('aaa')], next_page_token: 'page-2' });
    await settle(fixture);

    buttonWithText(fixture, 'Load more').click();
    const second = http.expectOne((r) => r.url === listUrl);
    expect(second.request.params.get('page_token')).toBe('page-2');
    second.flush({ items: [item('bbb')], next_page_token: null });
    await settle(fixture);

    const codes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('li.link .main code')].map((c) => c.textContent);
    expect(codes[0]).toContain('/aaa');
    expect(codes[1]).toContain('/bbb');
    expect(textOf(fixture)).not.toContain('Load more');
  });

  it('shows an error with a retry when loading fails', async () => {
    const fixture = render();

    http.expectOne((r) => r.url === listUrl).flush({ title: 'Down', detail: 'Storage is unavailable.' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);
    expect(textOf(fixture)).toContain('Storage is unavailable.');

    buttonWithText(fixture, 'Try again').click();
    http.expectOne((r) => r.url === listUrl).flush({ items: [item('aaa')], next_page_token: null });
    await settle(fixture);

    expect(textOf(fixture)).toContain('/aaa');
    expect(textOf(fixture)).not.toContain('Storage is unavailable.');
  });

  it('puts a newly created link at the top without asking the API again', async () => {
    const fixture = render();
    http.expectOne((r) => r.url === listUrl).flush({ items: [item('aaa')], next_page_token: null });
    await settle(fixture);

    typeInto(find(fixture, '#long-url'), 'https://example.com/new');
    find(fixture, 'button[type=submit]').click();
    http.expectOne('/api/v1/shorten').flush(
      {
        short_url: 'http://localhost/newcode',
        long_url: 'https://example.com/new',
        created_at: '2026-10-29T11:00:00Z',
        expires_at: '2027-10-29T11:00:00Z',
      },
      { status: 201, statusText: 'Created' },
    );
    await settle(fixture);

    const codes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('li.link .main code')].map((c) => c.textContent);
    expect(codes[0]).toContain('/newcode');
    expect(codes[1]).toContain('/aaa');
  });
});
