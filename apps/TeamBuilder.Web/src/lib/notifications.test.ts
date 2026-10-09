import { describe, expect, it } from 'vitest';
import { unreadBadgeText } from './notifications';

describe('unreadBadgeText', () => {
  it('is blank at zero and caps at 9+', () => {
    expect(unreadBadgeText(0)).toBe('');
    expect(unreadBadgeText(-1)).toBe('');
    expect(unreadBadgeText(3)).toBe('3');
    expect(unreadBadgeText(9)).toBe('9');
    expect(unreadBadgeText(10)).toBe('9+');
  });
});
