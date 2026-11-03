import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { FakeStorage } from '../testing/fake-storage';
import { App } from './app';
import { routes } from './app.routes';
import { KEY_STORAGE } from './core/auth.service';
import { LoadingService } from './core/loading.service';

describe('App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: KEY_STORAGE, useValue: new FakeStorage() },
      ],
    });
  });

  it('shows the header, the sign-in page and the toast area', async () => {
    const fixture = TestBed.createComponent(App);
    // The first navigation normally starts when the real app boots.
    await TestBed.inject(Router).navigateByUrl('/');
    await fixture.whenStable();
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('app-header')).not.toBeNull();
    expect(element.querySelector('app-toasts')).not.toBeNull();
    expect(element.querySelector('main h1')?.textContent).toContain('Sign in');
  });

  it('shows the progress bar only while the API is being called', async () => {
    const fixture = TestBed.createComponent(App);
    const loading = TestBed.inject(LoadingService);
    await fixture.whenStable();
    const bar = (fixture.nativeElement as HTMLElement).querySelector('.progress');
    expect(bar?.classList.contains('active')).toBe(false);

    loading.begin();
    await fixture.whenStable();
    expect(bar?.classList.contains('active')).toBe(true);

    loading.end();
    await fixture.whenStable();
    expect(bar?.classList.contains('active')).toBe(false);
  });

  it('sends an unknown address to the start page', async () => {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/no/such/page');
    await fixture.whenStable();

    expect(router.url).toBe('/');
  });
});
