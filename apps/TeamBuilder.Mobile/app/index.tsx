import { Redirect } from 'expo-router';
import { usePlayer, useSession } from '../src/Providers';
import { ApiError } from '../src/api/http';
import { ErrorNotice, Loading, Screen } from '../src/components/ui';
export default function Index() {
  const state = useSession(), player = usePlayer();
  if (state === 'restoring') return <Loading label="Restoring your session…" />;
  if (state === 'signed-out') return <Redirect href="/sign-in" />;
  if (player.isPending) return <Loading />;
  if (player.error instanceof ApiError && player.error.status === 404) return <Redirect href="/onboarding" />;
  if (player.error) return <Screen><ErrorNotice error={player.error} retry={() => void player.refetch()} /></Screen>;
  return <Redirect href="/profile" />;
}
