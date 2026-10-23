import { useState } from 'react';
import { Redirect, router, useLocalSearchParams } from 'expo-router';
import { safeReturnPath } from '../src/navigation/links';
import { useMutation } from '@tanstack/react-query';
import { useServices, useSession } from '../src/Providers';
import { Button, ErrorNotice, Field, Label, Screen } from '../src/components/ui';
import { ApiError } from '../src/api/http';
export default function Onboarding() {
  const { players, queryClient } = useServices(), state = useSession();
  const params = useLocalSearchParams(), returnTo = safeReturnPath(params.returnTo);
  const [username, setUsername] = useState(''), [displayName, setDisplayName] = useState('');
  const save = useMutation({ mutationFn: () => players.onboard(username.trim(), displayName.trim()),
    onSuccess: player => { queryClient.setQueryData(['me'], player); router.replace(returnTo); },
    onError: async error => { if (error instanceof ApiError && error.status === 409) {
      try { const player = await players.me(); queryClient.setQueryData(['me'], player); router.replace(returnTo); } catch { /* Keep the form for a username conflict. */ }
    } } });
  if (state === 'signed-out') return <Redirect href="/sign-in" />;
  return <Screen><Label heading>Make yourself known</Label><Label>Your account is linked by the identity provider. Choose how other players see you.</Label>
    <Field label="Username" value={username} onChangeText={setUsername} maxLength={100} autoCapitalize="none" autoCorrect={false} />
    <Field label="Display name" value={displayName} onChangeText={setDisplayName} maxLength={200} />
    {save.error && <ErrorNotice error={save.error} />}
    <Button title={save.isPending ? 'Creating profile…' : 'Create profile'} disabled={save.isPending || !username.trim()} onPress={() => save.mutate()} />
  </Screen>;
}
