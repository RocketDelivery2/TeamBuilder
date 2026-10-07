import { createContext, useContext } from 'react';
import type { TeamBuilderApi } from './api/teamBuilderApi';
import type { PlayerProfile } from './api/types';

export interface Session {
  api: TeamBuilderApi;
  me: PlayerProfile;
  signOut: () => void;
}

export const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) throw new Error('useSession must be used inside a signed-in session.');
  return session;
}
