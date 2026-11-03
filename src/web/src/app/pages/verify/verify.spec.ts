import { Location } from '@angular/common';
import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { find, provideTestApp, settle, textOf } from '../../../testing/helpers';
import { AuthService } from '../../core/auth.service';
import { Verify } from './verify';

describe('Verify', () => {
  let http: HttpTestingController;
  let replaceState: ReturnType<typeof vi.fn>;

  function render(fragment: string | null) {
    replaceState = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        ...provideTestApp(),
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
        { provide: Location, useValue: { replaceState } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(Verify);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => http.verify());

  it('spends the token, signs the user in and shows the key once', async () => {
    const fixture = render('token=abc123');

    const request = http.expectOne('/api/v1/verify');
    expect(request.request.body).toEqual({ token: 'abc123' });
    request.flush({ user_id: 'u1', email: 'a@example.com', api_key: 'the-new-key' });
    await settle(fixture);

    expect(find(fixture, '[data-testid=api-key]').textContent).toBe('the-new-key');
    expect(textOf(fixture)).toContain('the only time the key is shown');
    expect(TestBed.inject(AuthService).apiKey()).toBe('the-new-key');
  });

  it('removes the token from the address bar before anything else happens', () => {
    render('token=abc123');

    expect(replaceState).toHaveBeenCalledWith('/verify');
    http.expectOne('/api/v1/verify');
  });

  it('drops the key from the page and opens the links when the user continues', async () => {
    const fixture = render('token=abc123');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    http.expectOne('/api/v1/verify').flush({ user_id: 'u1', email: 'a@example.com', api_key: 'the-new-key' });
    await settle(fixture);

    find(fixture, 'button.btn-primary').click();
    await settle(fixture);

    expect(navigate).toHaveBeenCalledWith('/links');
    expect(textOf(fixture)).not.toContain('the-new-key');
  });

  it('explains a link without a token, without calling the API', async () => {
    const fixture = render(null);
    await settle(fixture);

    expect(textOf(fixture)).toContain('This link does not work');
  });

  it('explains an expired or used link', async () => {
    const fixture = render('token=old');

    http.expectOne('/api/v1/verify').flush({ title: 'Bad' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('This link does not work');
    expect(TestBed.inject(AuthService).signedIn()).toBe(false);
  });

  it('points to recovery when the address was registered in the meantime', async () => {
    const fixture = render('token=abc');

    http.expectOne('/api/v1/verify').flush({ title: 'Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('already has an account');
    expect(find(fixture, 'a[href="/recover"]')).toBeTruthy();
  });

  it('shows the API message for a server failure and offers a new link', async () => {
    const fixture = render('token=abc');

    http.expectOne('/api/v1/verify').flush({ title: 'Down', detail: 'Storage is unavailable.' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('Storage is unavailable.');
    expect(find(fixture, 'a[href="/signup"]')).toBeTruthy();
  });
});
