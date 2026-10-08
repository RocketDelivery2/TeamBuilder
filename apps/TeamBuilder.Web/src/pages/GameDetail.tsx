import { useCallback, useEffect, useState } from 'react';
import type { HostAction } from '../api/teamBuilderApi';
import { claimWithRetry } from '../api/claim';
import { interpretError } from '../api/conflicts';
import { AssignmentStatus, EventStatus, assignmentStatusLabel, eventStatusLabel, type OccurrenceDetail, type OccurrenceParticipant } from '../api/types';
import { RosterBadge } from '../components/RosterBadge';
import { ShareLink } from '../components/ShareLink';
import { displayName } from '../lib/format';
import { formatWhenInZone } from '../lib/discover';
import { useSession } from '../session';

type Notice = { tone: 'info' | 'error'; text: string };

export function GameDetail({ occurrenceId }: { occurrenceId: string }) {
  const { api } = useSession();
  const [detail, setDetail] = useState<OccurrenceDetail>();
  const [notice, setNotice] = useState<Notice>();
  const [busy, setBusy] = useState(false);
  const [locked, setLocked] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const next = await api.getDetail(occurrenceId);
      setDetail(next);
      // Closed is terminal: once the API has said OccurrenceClosed, controls stay disabled.
      setLocked((wasLocked) => wasLocked || !next.acceptsRosterChanges);
    } catch (err) {
      setNotice({ tone: 'error', text: interpretError(err).message });
    }
  }, [api, occurrenceId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  /** Runs one mutation, then always re-reads the game so refills and counts show at once. */
  const run = async (action: () => Promise<Notice | undefined>) => {
    setBusy(true);
    setNotice(undefined);
    try {
      setNotice(await action());
    } catch (err) {
      const interpreted = interpretError(err);
      if (interpreted.disableMutations) setLocked(true);
      setNotice({ tone: 'error', text: interpreted.message });
    } finally {
      await refresh();
      setBusy(false);
    }
  };

  if (!detail) {
    return notice ? <p className="notice error">{notice.text}</p> : <p className="muted">Loading…</p>;
  }

  const canMutate = detail.acceptsRosterChanges && !locked && !busy;
  const joinable = detail.requirements.filter((r) => r.openQuantity > 0);

  const join = (requirementId: string) =>
    run(async () => {
      const outcome = await claimWithRetry(() => api.claim(occurrenceId, requirementId), { maxRetries: 2 });
      switch (outcome.kind) {
        case 'joined':
          return { tone: 'info', text: "You're in." };
        case 'already-joined':
          return { tone: 'info', text: "You're already on this roster." };
        case 'full':
          return { tone: 'error', text: 'The roster filled up before you joined.' };
        case 'roster-changed':
          return { tone: 'error', text: 'The roster kept changing while you joined. It has been refreshed; try again.' };
      }
    });

  const hostAction = (participant: OccurrenceParticipant, action: HostAction) =>
    run(async () => {
      await api.hostAction(occurrenceId, participant.assignmentId, action);
      return undefined;
    });

  return (
    <section className="stack">
      <div className="card stack">
        <div className="row spread">
          <h1>{detail.name}</h1>
          <RosterBadge isRosterReady={detail.isRosterReady} supply={detail.supplyCount} required={detail.requiredCount} />
        </div>
        <div>{formatWhenInZone(detail.scheduledStartUtc, detail.scheduledEndUtc, detail.venue?.timeZoneId)}</div>
        {detail.location && <div>{detail.location}</div>}
        {detail.venue && !detail.venue.isAddressMasked && (detail.venue.addressLine1 || detail.venue.city) && (
          <div className="muted small" data-testid="venue-address">
            {[detail.venue.addressLine1, detail.venue.addressLine2, detail.venue.city, detail.venue.stateOrProvince, detail.venue.postalCode].filter(Boolean).join(', ')}
          </div>
        )}
        {detail.venue?.isAddressMasked && (
          <div className="muted small" data-testid="venue-masked">
            {[detail.venue.city, detail.venue.stateOrProvince].filter(Boolean).join(', ')} · Private location: the address is shared with players in this game.
          </div>
        )}
        <div className="muted small">
          Hosted by {detail.hostPlayerId ? displayName({ displayName: detail.hostDisplayName, username: detail.hostUsername }) : 'nobody'}
          {detail.isHost && ' (you)'} · {eventStatusLabel[detail.status]}
        </div>
        {detail.requirements.map((r) => (
          <div key={r.id} className="row spread small" data-testid="requirement">
            <span>{r.roleCode === 'participant' ? 'Players' : r.roleCode}</span>
            <span>
              {r.supplyCount}/{r.requiredCount} filled · {r.openQuantity} open
            </span>
          </div>
        ))}
        {!detail.acceptsRosterChanges && <p className="notice">This game is closed; the roster can no longer change.</p>}
        {detail.isHost && detail.acceptsRosterChanges && (
          <div className="row gap wrap">
            {detail.status !== EventStatus.InProgress ? (
              <button disabled={!canMutate} onClick={() => run(async () => {
                await api.setStatus(occurrenceId, EventStatus.InProgress);
                return { tone: 'info', text: 'Game started. Activate players as they take the court.' };
              })}>
                Start game
              </button>
            ) : (
              <button disabled={!canMutate} onClick={() => run(async () => {
                await api.setStatus(occurrenceId, EventStatus.Completed);
                return { tone: 'info', text: 'Game completed. The roster history is kept.' };
              })}>
                Finish game
              </button>
            )}
          </div>
        )}
      </div>

      {notice && <p className={`notice ${notice.tone === 'error' ? 'error' : ''}`} role="status">{notice.text}</p>}

      <div className="card stack">
        {detail.myAssignmentId ? (
          <div className="row spread">
            <span>
              You're <strong>{assignmentStatusLabel[detail.myAssignmentStatus ?? AssignmentStatus.Confirmed].toLowerCase()}</strong>
            </span>
            <button disabled={!canMutate} onClick={() => run(async () => {
              await api.leave(occurrenceId, detail.myAssignmentId!);
              return { tone: 'info', text: 'You left the game. Your spot is open for someone else.' };
            })}>
              Leave game
            </button>
          </div>
        ) : joinable.length > 0 ? (
          joinable.map((r) => (
            <button key={r.id} className="primary" disabled={!canMutate} onClick={() => join(r.id)}>
              Join game{detail.requirements.length > 1 ? ` as ${r.roleCode}` : ''}
            </button>
          ))
        ) : (
          <p className="muted">{detail.requirements.length === 0 ? 'This game has no roster.' : 'The roster is full.'}</p>
        )}
      </div>

      <div className="card stack">
        <h2>Players ({detail.participants.length})</h2>
        {detail.participants.length === 0 && <p className="muted">Nobody has joined yet.</p>}
        <ul className="list">
          {detail.participants.map((p) => (
            <li key={p.assignmentId} className="participant">
              <div className="row spread">
                <span>
                  {displayName(p)}
                  {p.isHost && <span className="badge host">Host</span>}
                </span>
                <span className="badge">{assignmentStatusLabel[p.status]}</span>
              </div>
              {detail.isHost && (
                <div className="row gap wrap">
                  {p.status === AssignmentStatus.Confirmed && <button disabled={!canMutate} onClick={() => hostAction(p, 'check-in')}>Check in</button>}
                  {p.status === AssignmentStatus.CheckedIn && <button disabled={!canMutate} onClick={() => hostAction(p, 'activate')}>Activate</button>}
                  {(p.status === AssignmentStatus.Confirmed || p.status === AssignmentStatus.CheckedIn) && (
                    <button disabled={!canMutate} onClick={() => hostAction(p, 'no-show')}>No show</button>
                  )}
                  <button disabled={!canMutate} onClick={() => hostAction(p, 'remove')}>Remove</button>
                </div>
              )}
            </li>
          ))}
        </ul>
      </div>

      {detail.isHost && detail.acceptsRosterChanges && (
        <TransferHost
          disabled={!canMutate}
          participants={detail.participants.filter((p) => p.playerId !== detail.hostPlayerId)}
          onTransfer={(resolve) => run(async () => {
            const playerId = await resolve();
            await api.transferHost(occurrenceId, playerId);
            return { tone: 'info', text: 'Hosting transferred.' };
          })}
        />
      )}

      <div className="card">
        <ShareLink occurrenceId={occurrenceId} />
      </div>
    </section>
  );
}

function TransferHost({
  participants,
  disabled,
  onTransfer,
}: {
  participants: OccurrenceParticipant[];
  disabled: boolean;
  onTransfer: (resolvePlayerId: () => Promise<string>) => void;
}) {
  const { api } = useSession();
  const [target, setTarget] = useState('');
  const [username, setUsername] = useState('');

  return (
    <form
      className="card stack"
      onSubmit={(e) => {
        e.preventDefault();
        onTransfer(async () => (target ? target : (await api.findPlayerByUsername(username.trim())).id));
      }}
    >
      <h2>Transfer host</h2>
      <label className="field">
        <span>New host</span>
        <select value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value="">Someone else (by username)</option>
          {participants.map((p) => (
            <option key={p.playerId} value={p.playerId}>
              {displayName(p)}
            </option>
          ))}
        </select>
      </label>
      {!target && (
        <label className="field">
          <span>Username</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} />
        </label>
      )}
      <button type="submit" disabled={disabled || (!target && !username.trim())}>
        Transfer hosting
      </button>
    </form>
  );
}
