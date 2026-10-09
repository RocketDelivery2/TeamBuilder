import { describe, expect, it } from 'vitest';
import { parseRoute } from './router';

const id = '22222222-2222-2222-2222-222222222222';

describe('parseRoute', () => {
  it('parses a game without a visit marker', () => {
    expect(parseRoute(`/games/${id}`)).toEqual({ name: 'game', id, visit: undefined });
    expect(parseRoute(`/games/${id}`, '?utm=x')).toEqual({ name: 'game', id, visit: undefined });
  });

  it('marks each notification click as its own visit, so an open game tab re-reads', () => {
    const first = parseRoute(`/games/${id}`, '?n=abc&via=push&t=1');
    const second = parseRoute(`/games/${id}`, '?n=abc&via=push&t=2');
    expect(first).toMatchObject({ name: 'game', id, visit: '?n=abc&via=push&t=1' });
    expect(second).not.toEqual(first);
  });

  it('keeps the other routes', () => {
    expect(parseRoute('/')).toEqual({ name: 'my-games' });
    expect(parseRoute('/notifications')).toEqual({ name: 'notifications' });
    expect(parseRoute('/games/not-a-guid')).toEqual({ name: 'not-found' });
  });
});
