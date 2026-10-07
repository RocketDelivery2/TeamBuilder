import { useState } from 'react';
import { gameUrl } from '../router';

export function ShareLink({ occurrenceId }: { occurrenceId: string }) {
  const url = gameUrl(occurrenceId);
  const [copied, setCopied] = useState(false);

  return (
    <div className="share">
      <label className="field">
        <span>Share this game</span>
        <input readOnly value={url} onFocus={(e) => e.currentTarget.select()} aria-label="Game link" />
      </label>
      <button
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(url);
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
          } catch {
            setCopied(false);
          }
        }}
      >
        {copied ? 'Copied' : 'Copy link'}
      </button>
    </div>
  );
}
