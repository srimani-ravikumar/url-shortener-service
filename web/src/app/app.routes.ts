import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth.guard';

/**
 * Page routes. Each page is loaded on demand, so the first visit downloads only what it needs.
 * Pages that need a signed-in user use `authGuard`; the sign-in page sends signed-in users on to their links.
 */
export const routes: Routes = [
  {
    path: '',
    canActivate: [guestGuard],
    title: 'Sign in - trysmartly.co',
    loadComponent: () => import('./pages/sign-in/sign-in').then((m) => m.SignIn),
  },
  {
    path: 'signup',
    data: { mode: 'signup' },
    title: 'Get an API key - trysmartly.co',
    loadComponent: () => import('./pages/request-mail/request-mail').then((m) => m.RequestMail),
  },
  {
    path: 'recover',
    data: { mode: 'recover' },
    title: 'Get a new API key - trysmartly.co',
    loadComponent: () => import('./pages/request-mail/request-mail').then((m) => m.RequestMail),
  },
  {
    path: 'verify',
    title: 'Confirm your email - trysmartly.co',
    loadComponent: () => import('./pages/verify/verify').then((m) => m.Verify),
  },
  {
    path: 'links',
    canActivate: [authGuard],
    title: 'My links - trysmartly.co',
    loadComponent: () => import('./pages/links/links').then((m) => m.Links),
  },
  {
    path: 'links/:code',
    canActivate: [authGuard],
    title: 'Link statistics - trysmartly.co',
    loadComponent: () => import('./pages/link-detail/link-detail').then((m) => m.LinkDetail),
  },
  {
    path: 'account',
    canActivate: [authGuard],
    title: 'Account - trysmartly.co',
    loadComponent: () => import('./pages/account/account').then((m) => m.Account),
  },
  { path: '**', redirectTo: '' },
];
