import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { DiscoveredOccurrence } from '../api/types';
import { DiscoverCard } from './DiscoverCard';

const game = (overrides: Partial<DiscoveredOccurrence> = {}): DiscoveredOccurrence => ({
  occurrenceId: '11111111-1111-1111-1111-111111111111',
  name: 'Wednesday Basketball 8 PM',
  category: 'basketball',
  status: 1,
  scheduledStartUtc: '2026-10-15T01:00:00Z',
  scheduledEndUtc: '2026-10-15T03:00:00Z',
  timeZoneId: 'America/Chicago',
  venue: { venueId: 'v1', name: 'Union Park', city: 'Chicago', stateOrProvince: 'IL', addressLine1: '1501 W Randolph St', privacyLevel: 1, distanceMiles: 2.04, isAddressMasked: false },
  roster: { totalRequiredCount: 10, totalSupplyCount: 9, totalOpenQuantity: 1, isRosterReady: false, isFull: false },
  viewer: { isHost: false, isParticipating: false },
  ...overrides,
});

describe('DiscoverCard', () => {
  it('shows what, when, where, how far and the open spot, linking to the game', () => {
    render(<ul><DiscoverCard game={game()} /></ul>);
    expect(screen.getByText('Wednesday Basketball 8 PM')).toBeInTheDocument();
    expect(screen.getByText(/Basketball ·/)).toBeInTheDocument();
    expect(screen.getByTestId('distance')).toHaveTextContent('2.0 mi');
    expect(screen.getByTestId('spots')).toHaveTextContent('9/10 · 1 spot open');
    expect(screen.getByText(/1501 W Randolph St, Chicago, IL/)).toBeInTheDocument();
    expect(screen.getByRole('link')).toHaveAttribute('href', '/games/11111111-1111-1111-1111-111111111111');
  });

  it('shows READY and that I am playing', () => {
    render(<ul><DiscoverCard game={game({ roster: { totalRequiredCount: 10, totalSupplyCount: 10, totalOpenQuantity: 0, isRosterReady: true, isFull: true }, viewer: { isHost: false, isParticipating: true, participationStatus: 2 } })} /></ul>);
    expect(screen.getByTestId('spots')).toHaveTextContent('READY 10/10');
    expect(screen.getByText("You're playing")).toBeInTheDocument();
  });

  it('never shows a street for a private venue', () => {
    render(<ul><DiscoverCard game={game({ venue: { venueId: 'v2', name: "Sam's driveway", city: 'Chicago', stateOrProvince: 'IL', privacyLevel: 2, distanceMiles: 3.2, isAddressMasked: true }, viewer: { isHost: true, isParticipating: false } })} /></ul>);
    expect(screen.getByText('Private location')).toBeInTheDocument();
    expect(screen.getByTestId('distance')).toHaveTextContent('~3.2 mi');
    expect(screen.getByText("You're hosting")).toBeInTheDocument();
    expect(screen.queryByText(/Randolph/)).not.toBeInTheDocument();
  });

  it('keeps a private distance approximate even when the address is shown', () => {
    render(<ul><DiscoverCard game={game({ venue: { venueId: 'v3', name: "Sam's driveway", city: 'Chicago', stateOrProvince: 'IL', addressLine1: '2200 W Example Ave', privacyLevel: 2, distanceMiles: 1.9, isAddressMasked: false } })} /></ul>);
    expect(screen.getByTestId('distance')).toHaveTextContent('~1.9 mi');
    expect(screen.queryByText('Private location')).not.toBeInTheDocument();
  });
});
