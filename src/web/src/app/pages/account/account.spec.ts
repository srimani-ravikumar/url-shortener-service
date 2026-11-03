import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { buttonWithText, find, provideTestApp, settle, textOf, typeInto } from '../../../testing/helpers';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { Account } from './account';

describe('Account', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  let navigate: ReturnType<typeof vi.spyOn>;

  function render() {
    const fixture = TestBed.createComponent(Account);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: provideTestApp() });
    http = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'old-key' });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });

  afterEach(() => http.verify());

  it('shows the email and user id', () => {
    const text = textOf(render());

    expect(text).toContain('a@example.com');
    expect(text).toContain('u1');
  });

  describe('replacing the key', () => {
    it('asks for confirmation first and can be cancelled', async () => {
      const fixture = render();

      buttonWithText(fixture, 'Replace key').click();
      await settle(fixture);
      expect(textOf(fixture)).toContain('Your current key stops working immediately');

      buttonWithText(fixture, 'Cancel').click();
      await settle(fixture);
      expect(textOf(fixture)).not.toContain('stops working immediately');
    });

    it('switches the session to the new key and shows it once', async () => {
      const fixture = render();
      buttonWithText(fixture, 'Replace key').click();
      await settle(fixture);

      buttonWithText(fixture, 'Replace key').click(); // the confirm button
      http.expectOne('/api/v1/users/me/api-key/rotate').flush({ user_id: 'u1', email: 'a@example.com', api_key: 'new-key' });
      await settle(fixture);

      expect(find(fixture, '[data-testid=api-key]').textContent).toBe('new-key');
      expect(auth.apiKey()).toBe('new-key');
    });

    it('keeps the old key and says why when replacing fails', async () => {
      const fixture = render();
      buttonWithText(fixture, 'Replace key').click();
      await settle(fixture);

      buttonWithText(fixture, 'Replace key').click();
      http.expectOne('/api/v1/users/me/api-key/rotate').flush({ title: 'Down', detail: 'Try later.' }, { status: 503, statusText: 'Unavailable' });
      await settle(fixture);

      expect(auth.apiKey()).toBe('old-key');
      expect(TestBed.inject(ToastService).toasts().map((toast) => toast.message)).toEqual(['Try later.']);
    });
  });

  describe('deleting the account', () => {
    it('stays disabled until the email is typed out', async () => {
      const fixture = render();
      const button = buttonWithText(fixture, 'Delete my account');
      expect(button.disabled).toBe(true);

      typeInto(find(fixture, '#confirm-email'), 'someone@else.com');
      await settle(fixture);
      expect(button.disabled).toBe(true);

      typeInto(find(fixture, '#confirm-email'), '  A@Example.com ');
      await settle(fixture);
      expect(button.disabled).toBe(false);
    });

    it('deletes, signs out, returns to the start page and confirms with a message', async () => {
      const fixture = render();
      typeInto(find(fixture, '#confirm-email'), 'a@example.com');
      await settle(fixture);

      buttonWithText(fixture, 'Delete my account').click();
      http.expectOne('/api/v1/users/me').flush(null, { status: 204, statusText: 'No Content' });
      await settle(fixture);

      expect(auth.signedIn()).toBe(false);
      expect(navigate).toHaveBeenCalledWith('/');
      expect(TestBed.inject(ToastService).toasts().map((toast) => toast.kind)).toEqual(['success']);
    });

    it('stays signed in and explains when deleting fails, so it can be tried again', async () => {
      const fixture = render();
      typeInto(find(fixture, '#confirm-email'), 'a@example.com');
      await settle(fixture);

      buttonWithText(fixture, 'Delete my account').click();
      http.expectOne('/api/v1/users/me').flush({ title: 'Down', detail: 'Storage is unavailable.' }, { status: 503, statusText: 'Unavailable' });
      await settle(fixture);

      expect(auth.signedIn()).toBe(true);
      expect(navigate).not.toHaveBeenCalled();
      expect(TestBed.inject(ToastService).toasts().map((toast) => toast.message)).toEqual(['Storage is unavailable.']);
    });
  });
});
