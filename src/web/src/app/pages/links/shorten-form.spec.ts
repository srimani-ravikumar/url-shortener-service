import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { find, provideTestApp, settle, textOf, typeInto } from '../../../testing/helpers';
import { ShortUrlResponse } from '../../core/api.models';
import { ToastService } from '../../core/toast.service';
import { ShortenForm } from './shorten-form';

describe('ShortenForm', () => {
  let http: HttpTestingController;
  let created: ShortUrlResponse[];

  const link: ShortUrlResponse = {
    short_url: 'http://short.test/abc1234',
    long_url: 'https://example.com/a',
    created_at: '2026-10-29T10:00:00Z',
    expires_at: '2027-10-29T10:00:00Z',
  };

  function render() {
    const fixture = TestBed.createComponent(ShortenForm);
    created = [];
    fixture.componentInstance.created.subscribe((value) => created.push(value));
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: provideTestApp() });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends only the long URL when the optional fields are empty', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), ' https://example.com/a ');
    find(fixture, 'button[type=submit]').click();
    const request = http.expectOne('/api/v1/shorten');

    expect(request.request.body).toEqual({ long_url: 'https://example.com/a' });
    request.flush(link, { status: 201, statusText: 'Created' });
    await settle(fixture);
  });

  it('sends the alias and the expiry as an absolute time', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'https://example.com/a');
    typeInto(find(fixture, '#custom-alias'), ' summer-sale ');
    typeInto(find(fixture, '#expires-at'), '2030-09-01T23:59');
    find(fixture, 'button[type=submit]').click();
    const request = http.expectOne('/api/v1/shorten');

    expect(request.request.body.custom_alias).toBe('summer-sale');
    expect(request.request.body.expires_at).toBe(new Date('2030-09-01T23:59').toISOString());
    request.flush(link, { status: 201, statusText: 'Created' });
    await settle(fixture);
  });

  it('shows the new link, lets it be copied, emits it and clears the form', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'https://example.com/a');
    find(fixture, 'button[type=submit]').click();
    http.expectOne('/api/v1/shorten').flush(link, { status: 201, statusText: 'Created' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('http://short.test/abc1234');
    expect(textOf(fixture)).toContain('Copy link');
    expect(created).toEqual([link]);
    expect(find<HTMLInputElement>(fixture, '#long-url').value).toBe('');
  });

  it('shows validation messages next to their fields and keeps what was typed', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'javascript:alert(1)');
    find(fixture, 'button[type=submit]').click();
    http
      .expectOne('/api/v1/shorten')
      .flush(
        { title: 'Bad', errors: { long_url: ['long_url must be http or https.'], custom_alias: ['custom_alias is reserved.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);

    expect(find(fixture, '#long-url-error').textContent).toContain('http or https');
    expect(find(fixture, '#custom-alias-error').textContent).toContain('reserved');
    expect(find<HTMLInputElement>(fixture, '#long-url').value).toBe('javascript:alert(1)');
    expect(created).toEqual([]);
  });

  it('shows a taken alias next to the alias field', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'https://example.com/a');
    typeInto(find(fixture, '#custom-alias'), 'taken');
    find(fixture, 'button[type=submit]').click();
    http
      .expectOne('/api/v1/shorten')
      .flush({ title: 'Alias already taken', detail: 'That custom alias is already in use.' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(find(fixture, '#custom-alias-error').textContent).toContain('already in use');
  });

  it('shows rate limits and outages as a toast', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'https://example.com/a');
    find(fixture, 'button[type=submit]').click();
    http
      .expectOne('/api/v1/shorten')
      .flush({ title: 'Too many requests', detail: 'Slow down.' }, { status: 429, statusText: 'Too Many Requests' });
    await settle(fixture);

    expect(TestBed.inject(ToastService).toasts().map((toast) => toast.message)).toEqual(['Slow down.']);
  });

  it('does not call the API for an expiry that is not a date', async () => {
    const fixture = render();

    typeInto(find(fixture, '#long-url'), 'https://example.com/a');
    // jsdom does not validate datetime-local, so an invalid value can reach the form.
    const control = fixture.componentInstance['form'].controls.expiresAt;
    control.setValue('not-a-date');
    find(fixture, 'button[type=submit]').click();
    await settle(fixture);

    expect(find(fixture, '#expires-at-error').textContent).toContain('valid date');
  });
});
