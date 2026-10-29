import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { FakeStorage } from '../../testing/fake-storage';
import { AuthService, KEY_STORAGE } from '../core/auth.service';
import { Header } from './header';

describe('Header', () => {
  let storage: FakeStorage;

  function render() {
    const fixture = TestBed.createComponent(Header);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    storage = new FakeStorage();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: KEY_STORAGE, useValue: storage },
      ],
    });
  });

  it('shows only the brand to a visitor', () => {
    const element: HTMLElement = render().nativeElement;

    expect(element.querySelector('.brand')?.textContent).toContain('trysmartly.co');
    expect(element.querySelector('button')).toBeNull();
  });

  it('shows the email and a sign-out button to a signed-in user', () => {
    TestBed.inject(AuthService).acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });

    const element: HTMLElement = render().nativeElement;

    expect(element.querySelector('.email')?.textContent).toContain('a@example.com');
    expect(element.querySelector('button')?.textContent).toContain('Sign out');
    expect([...element.querySelectorAll('nav a')].map((link) => link.getAttribute('href'))).toEqual(['/links', '/account']);
  });

  it('shows no navigation to a visitor', () => {
    expect(render().nativeElement.querySelector('nav')).toBeNull();
  });

  it('signs out and returns to the start page when the button is pressed', async () => {
    const auth = TestBed.inject(AuthService);
    auth.acceptCredentials({ user_id: 'u1', email: 'a@example.com', api_key: 'k' });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = render();

    (fixture.nativeElement as HTMLElement).querySelector('button')?.click();
    fixture.detectChanges();

    expect(auth.signedIn()).toBe(false);
    expect(navigate).toHaveBeenCalledWith('/');
    expect((fixture.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });
});
