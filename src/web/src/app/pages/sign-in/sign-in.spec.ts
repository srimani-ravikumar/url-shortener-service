import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { find, provideTestApp, settle, textOf, typeInto } from '../../../testing/helpers';
import { AuthService } from '../../core/auth.service';
import { SignIn } from './sign-in';

describe('SignIn', () => {
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  function render() {
    const fixture = TestBed.createComponent(SignIn);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: provideTestApp() });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });

  afterEach(() => http.verify());

  it('offers to get a key or recover a lost one', () => {
    const fixture = render();

    expect(find(fixture, 'a[href="/signup"]')).toBeTruthy();
    expect(find(fixture, 'a[href="/recover"]')).toBeTruthy();
  });

  it('hides the key while it is typed', () => {
    expect(find<HTMLInputElement>(render(), '#api-key').type).toBe('password');
  });

  it('asks for a key instead of calling the API when the field is empty', async () => {
    const fixture = render();

    find(fixture, 'button[type=submit]').click();
    await settle(fixture);

    expect(textOf(fixture)).toContain('Enter your API key.');
  });

  it('signs in with a valid key and goes to the links page', async () => {
    const fixture = render();

    typeInto(find(fixture, '#api-key'), 'good-key');
    find(fixture, 'button[type=submit]').click();
    const request = http.expectOne('/api/v1/users/me');
    request.flush({ user_id: 'u1', email: 'a@example.com', created_at: 'x' });
    await settle(fixture);

    expect(TestBed.inject(AuthService).signedIn()).toBe(true);
    expect(navigate).toHaveBeenCalledWith('/links');
  });

  it('says so, and stays signed out, when the key is wrong', async () => {
    const fixture = render();

    typeInto(find(fixture, '#api-key'), 'wrong');
    find(fixture, 'button[type=submit]').click();
    http.expectOne('/api/v1/users/me').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('That key is not valid.');
    expect(TestBed.inject(AuthService).signedIn()).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('shows the API message when the server is unavailable', async () => {
    const fixture = render();

    typeInto(find(fixture, '#api-key'), 'any');
    find(fixture, 'button[type=submit]').click();
    http.expectOne('/api/v1/users/me').flush({ title: 'Unavailable', detail: 'Try again shortly.' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('Try again shortly.');
  });
});
