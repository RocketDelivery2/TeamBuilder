import { Redirect } from 'expo-router';
import { useMutation } from '@tanstack/react-query';
import { usePlayer, useServices, useSession } from '../src/Providers';
import { Button, Card, ErrorNotice, Label, Loading, Screen } from '../src/components/ui';
import { useTheme, type ThemeChoice } from '../src/theme/Theme';
export default function Profile() {
  const state = useSession(), player = usePlayer(), { session } = useServices(), theme = useTheme();
  const logout = useMutation({ mutationFn: () => session.logout() });
  if (state === 'signed-out') return <Redirect href="/sign-in" />;
  if (!player.data) return <Screen>{player.error ? <ErrorNotice error={player.error} retry={() => void player.refetch()} /> : <Loading />}</Screen>;
  return <Screen onRefresh={() => void player.refetch()} refreshing={player.isRefetching}>
    <Label heading>{player.data.displayName || player.data.username}</Label><Label muted>@{player.data.username}</Label>
    <Card><Label heading>Appearance</Label>{(['system', 'light', 'dark'] as ThemeChoice[]).map(choice =>
      <Button key={choice} title={choice + (theme.choice === choice ? ' — selected' : '')} secondary onPress={() => theme.setChoice(choice)} />)}</Card>
    <Card><Label heading>Your privacy</Label><Label>Location is used only for a search you request. Game and roster data is kept in memory, not an offline database.</Label>
      <Label>Sign out before sharing this device. Contact your TeamBuilder operator for account data or deletion assistance; automated account deletion is not yet available in this app.</Label></Card>
    <Button title={logout.isPending ? 'Signing out…' : 'Sign out / switch account'} danger disabled={logout.isPending} onPress={() => logout.mutate()} />
  </Screen>;
}
