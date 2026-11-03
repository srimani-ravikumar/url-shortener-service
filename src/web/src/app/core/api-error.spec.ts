import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { ApiError, toApiError } from './api-error';

describe('toApiError', () => {
  it('reads the title, detail and field errors of a problem document', () => {
    const error = toApiError(
      new HttpErrorResponse({
        status: 400,
        error: { title: 'One or more validation errors occurred.', errors: { long_url: ['Not a URL.', 'Too long.'] } },
      }),
    );

    expect(error.status).toBe(400);
    expect(error.title).toBe('One or more validation errors occurred.');
    expect(error.fieldError('long_url')).toBe('Not a URL.');
    expect(error.fieldError('custom_alias')).toBeNull();
  });

  it('prefers the detail as the message shown to the user', () => {
    const error = toApiError(
      new HttpErrorResponse({ status: 409, error: { title: 'Alias already taken', detail: 'Choose another one.' } }),
    );

    expect(error.userMessage).toBe('Choose another one.');
  });

  it('falls back to the title when there is no detail', () => {
    const error = toApiError(new HttpErrorResponse({ status: 401, error: { title: 'Unauthorized' } }));

    expect(error.userMessage).toBe('Unauthorized');
  });

  it('reads how long to wait from Retry-After', () => {
    const error = toApiError(
      new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '42' }), error: null }),
    );

    expect(error.retryAfterSeconds).toBe(42);
  });

  it('ignores a Retry-After that is not a number', () => {
    const error = toApiError(
      new HttpErrorResponse({ status: 503, headers: new HttpHeaders({ 'Retry-After': 'soon' }), error: null }),
    );

    expect(error.retryAfterSeconds).toBeNull();
  });

  it('explains an unreachable server', () => {
    const error = toApiError(new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') }));

    expect(error.status).toBe(0);
    expect(error.title).toBe('Cannot reach the server');
  });

  it.each([
    [429, 'Too many requests'],
    [500, 'Something went wrong on our side'],
    [404, 'The request failed'],
  ])('gives a readable title to a %i without a body', (status, title) => {
    expect(toApiError(new HttpErrorResponse({ status, error: null })).title).toBe(title);
  });

  it('ignores a body that is not a problem document', () => {
    const error = toApiError(new HttpErrorResponse({ status: 502, error: '<html>Bad gateway</html>' }));

    expect(error.detail).toBeNull();
    expect(error.fieldErrors).toEqual({});
  });

  it('keeps only string messages in field errors', () => {
    const error = toApiError(new HttpErrorResponse({ status: 400, error: { errors: { email: ['bad', 7, null] } } }));

    expect(error.fieldErrors['email']).toEqual(['bad']);
  });

  it('is an Error, so it works with standard error handling', () => {
    const error = toApiError(new HttpErrorResponse({ status: 500, error: null }));

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toBeInstanceOf(Error);
  });
});
