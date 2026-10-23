import type { PropsWithChildren } from 'react';
import { Redirect, usePathname } from 'expo-router';
import { usePlayer, useSession } from '../Providers';
import { ApiError } from '../api/http';
import { safeReturnPath } from '../navigation/links';
import { ErrorNotice, Loading, Screen } from './ui';
export function AccountGate({ children }: PropsWithChildren) {
  const state = useSession(), player = usePlayer(), returnTo = safeReturnPath(usePathname());
  if (state === 'restoring') return <Loading label="Restoring your session…" />;
  if (state === 'signed-out') return <Redirect href={{ pathname: '/sign-in', params: { returnTo } }} />;
  if (player.error instanceof ApiError && player.error.status === 404) return <Redirect href={{ pathname: '/onboarding', params: { returnTo } }} />;
  if (player.error) return <Screen><ErrorNotice error={player.error} retry={() => void player.refetch()} /></Screen>;
  if (!player.data) return <Loading />;
  return children;
}
