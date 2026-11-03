import { isExpired, shortCodeOf, shortUrlFor } from './short-links';

describe('short links', () => {
  it('builds the public address of a code on the given origin', () => {
    expect(shortUrlFor('abc', 'https://trysmartly.co')).toBe('https://trysmartly.co/abc');
  });

  it('reads the code from a short URL, however it is formed', () => {
    expect(shortCodeOf('http://localhost:8080/aB3dE9x')).toBe('aB3dE9x');
    expect(shortCodeOf('https://trysmartly.co/summer-sale')).toBe('summer-sale');
  });

  it('knows when a link has expired', () => {
    const now = Date.parse('2026-10-29T12:00:00Z');

    expect(isExpired('2026-10-29T11:59:59Z', now)).toBe(true);
    expect(isExpired('2026-10-29T12:00:00Z', now)).toBe(true);
    expect(isExpired('2026-10-29T12:00:01Z', now)).toBe(false);
  });
});
