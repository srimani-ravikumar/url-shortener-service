import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, finalize, throwError } from 'rxjs';
import { ApiError, toApiError } from './api-error';
import { AuthService } from './auth.service';
import { SILENT_AUTH_ERRORS } from './http-context';
import { LoadingService } from './loading.service';

const API_PREFIX = '/api/';

/**
 * Sends the API key with every call to our own API (and only ours), and ends the session when the
 * API says the key is no longer valid. Must be listed before {@link errorInterceptor} so it
 * sees the converted {@link ApiError}.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const apiKey = auth.apiKey();
  if (!apiKey || !request.url.startsWith(API_PREFIX)) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { 'X-Api-Key': apiKey } })).pipe(
    catchError((error: unknown) => {
      if (error instanceof ApiError && error.status === 401 && !request.context.get(SILENT_AUTH_ERRORS)) {
        auth.signOut();
        void router.navigateByUrl('/');
      }

      return throwError(() => error);
    }),
  );
};

/** Turns failed HTTP calls into {@link ApiError}s, so no page has to know about HttpErrorResponse. */
export const errorInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: unknown) =>
      throwError(() => (error instanceof HttpErrorResponse ? toApiError(error) : error)),
    ),
  );

/** Tells {@link LoadingService} how many calls are in flight, for the progress bar. */
export const loadingInterceptor: HttpInterceptorFn = (request, next) => {
  const loading = inject(LoadingService);

  loading.begin();
  return next(request).pipe(finalize(() => loading.end()));
};
