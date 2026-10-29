import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { find, provideTestApp, settle, textOf, typeInto } from '../../../testing/helpers';
import { ToastService } from '../../core/toast.service';
import { MailMode, RequestMail } from './request-mail';

describe('RequestMail', () => {
  let http: HttpTestingController;

  function render(mode: MailMode) {
    TestBed.configureTestingModule({
      providers: [...provideTestApp(), { provide: ActivatedRoute, useValue: { snapshot: { data: { mode } } } }],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(RequestMail);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => http.verify());

  it('explains the sign-up flow', () => {
    expect(textOf(render('signup'))).toContain('Get an API key');
  });

  it('explains the recovery flow, including that the old key stops working', () => {
    const text = textOf(render('recover'));

    expect(text).toContain('Get a new API key');
    expect(text).toContain('the old key stops working');
  });

  it('asks for an address instead of calling the API when the field is empty', async () => {
    const fixture = render('signup');

    find(fixture, 'button[type=submit]').click();
    await settle(fixture);

    expect(textOf(fixture)).toContain('Enter your email address.');
  });

  it('sends a sign-up request for the trimmed address, then tells the user to check the inbox', async () => {
    const fixture = render('signup');

    typeInto(find(fixture, '#email'), '  a@example.com ');
    find(fixture, 'button[type=submit]').click();
    const request = http.expectOne('/api/v1/signup');
    expect(request.request.body).toEqual({ email: 'a@example.com' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('Check your inbox');
    expect(textOf(fixture)).toContain('a@example.com');
  });

  it('sends a recovery request in recovery mode', async () => {
    const fixture = render('recover');

    typeInto(find(fixture, '#email'), 'a@example.com');
    find(fixture, 'button[type=submit]').click();
    http.expectOne('/api/v1/recover').flush(null, { status: 202, statusText: 'Accepted' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('Check your inbox');
  });

  it('shows the API message next to the field for an invalid address', async () => {
    const fixture = render('signup');

    typeInto(find(fixture, '#email'), 'nope');
    find(fixture, 'button[type=submit]').click();
    http
      .expectOne('/api/v1/signup')
      .flush({ title: 'Bad', errors: { email: ['email is not a valid address.'] } }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);

    expect(find(fixture, '#email-error').textContent).toContain('email is not a valid address.');
    expect(textOf(fixture)).not.toContain('Check your inbox');
  });

  it('shows other failures, such as a rate limit, as a toast', async () => {
    const fixture = render('signup');

    typeInto(find(fixture, '#email'), 'a@example.com');
    find(fixture, 'button[type=submit]').click();
    http
      .expectOne('/api/v1/signup')
      .flush({ title: 'Too many requests', detail: 'Wait a moment.' }, { status: 429, statusText: 'Too Many Requests' });
    await settle(fixture);

    expect(TestBed.inject(ToastService).toasts().map((toast) => toast.message)).toEqual(['Wait a moment.']);
    expect(textOf(fixture)).not.toContain('Check your inbox');
  });
});
