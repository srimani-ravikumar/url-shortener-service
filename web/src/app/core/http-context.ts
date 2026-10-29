import { HttpContextToken } from '@angular/common/http';

/**
 * Set on a request whose 401 must not sign the user out, for example the check of a key the user just typed:
 * a wrong key is an expected answer there, not an expired session.
 */
export const SILENT_AUTH_ERRORS = new HttpContextToken<boolean>(() => false);
