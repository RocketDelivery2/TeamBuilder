import { occurrencePath, safeReturnPath } from '../src/navigation/links';
test.each(['https://other.invalid', '//other.invalid', '/games/../../profile', '/games/123', '/profile?redirect=https://other.invalid'])('rejects unsafe return path %s', path => {
  expect(safeReturnPath(path)).toBe('/');
});
test('only accepts a valid occurrence identifier from notification data', () => {
  const path = '/games/12345678-1234-1234-1234-123456789abc';
  expect(occurrencePath(path.slice(7))).toBe(path);
  expect(safeReturnPath(path)).toBe(path);
  expect(occurrencePath({ url: 'https://other.invalid' })).toBeNull();
});
