import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ApiError } from './api-error';
import { AuthService } from './auth.service';
import { ToastService } from './toast.service';

/** Keeps signed-in users off the sign-in page: they go straight to their links. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.signedIn() ? inject(Router).parseUrl('/links') : true;
};

/** Lets signed-in users through to a page; everyone else goes to the start page. */
export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const toasts = inject(ToastService);

  if (!auth.signedIn()) {
    return router.parseUrl('/');
  }

  try {
    // Pages need the user id, which a key kept from an earlier page load does not include.
    await auth.ensureAccount();
    return true;
  } catch (error) {
    // A 401 already signed the user out (and says so by the redirect); anything else deserves an explanation.
    if (!(error instanceof ApiError) || error.status !== 401) {
      toasts.error(error instanceof ApiError ? error.userMessage : 'Could not load your account.');
    }

    return router.parseUrl('/');
  }
};
