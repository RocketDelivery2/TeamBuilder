import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { PlayerOccurrence } from '../api/types';
import { GameCard } from './GameCard';
import { RosterBadge } from './RosterBadge';

const game = (overrides: Partial<PlayerOccurrence> = {}): PlayerOccurrence => ({
  occurrenceId: '11111111-1111-1111-1111-111111111111',
  name: 'Wednesday Night Hoops',
  scheduledStartUtc: '2026-10-15T00:00:00Z',
  scheduledEndUtc: '2026-10-15T02:00:00Z',
  status: 1,
  location: 'Rec Center',
  isHost: false,
  myAssignmentId: 'a1',
  myAssignmentStatus: 2,
  isRosterReady: false,
  totalRequiredCount: 10,
  totalSupplyCount: 7,
  totalOpenQuantity: 3,
  ...overrides,
});

describe('RosterBadge', () => {
  it('shows supply/required while open', () => {
    render(<RosterBadge isRosterReady={false} supply={7} required={10} />);
    expect(screen.getByTestId('roster-badge')).toHaveTextContent('7/10');
  });

  it('shows READY when the API says the roster is ready', () => {
    render(<RosterBadge isRosterReady supply={10} required={10} />);
    expect(screen.getByTestId('roster-badge')).toHaveTextContent('READY');
  });
});

describe('GameCard', () => {
  it('renders a game I play in with my status', () => {
    render(<ul><GameCard game={game()} /></ul>);
    expect(screen.getByText('Wednesday Night Hoops')).toBeInTheDocument();
    expect(screen.getByText('Rec Center')).toBeInTheDocument();
    expect(screen.getByText('Confirmed')).toBeInTheDocument();
    expect(screen.queryByText('Host')).not.toBeInTheDocument();
    expect(screen.getByTestId('roster-badge')).toHaveTextContent('7/10');
  });

  it('renders an organizer-only hosted game with a host badge and no assignment', () => {
    render(<ul><GameCard game={game({ isHost: true, myAssignmentId: null, myAssignmentStatus: null, totalSupplyCount: 0, totalOpenQuantity: 10 })} /></ul>);
    expect(screen.getByText('Host')).toBeInTheDocument();
    expect(screen.getByText('Organizing')).toBeInTheDocument();
    expect(screen.getByTestId('roster-badge')).toHaveTextContent('0/10');
  });

  it('renders READY for a full roster', () => {
    render(<ul><GameCard game={game({ isRosterReady: true, totalSupplyCount: 10, totalOpenQuantity: 0 })} /></ul>);
    expect(screen.getByTestId('roster-badge')).toHaveTextContent('READY');
  });
});
