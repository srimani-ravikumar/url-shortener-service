/** The public address of a short code. Same origin as the app: nginx serves the redirector there. */
export function shortUrlFor(shortCode: string, origin: string = window.location.origin): string {
  return `${origin}/${shortCode}`;
}

/** The short code at the end of a short URL returned by the API. */
export function shortCodeOf(shortUrl: string): string {
  return new URL(shortUrl).pathname.replace(/^\/+/, '');
}

/** True once a link's expiry time has passed (the redirector then answers 410). */
export function isExpired(expiresAt: string, now: number = Date.now()): boolean {
  return Date.parse(expiresAt) <= now;
}
