import { Redirect, router, useLocalSearchParams } from 'expo-router';
import { safeReturnPath } from '../src/navigation/links';
import { useMutation } from '@tanstack/react-query';
import { useServices, useSession } from '../src/Providers';
import { Button, Card, Label, Screen } from '../src/components/ui';
export default function SignIn() {
  const { session } = useServices(), state = useSession();
  const params = useLocalSearchParams(), returnTo = safeReturnPath(params.returnTo);
  const signIn = useMutation({ mutationFn: () => session.login(), onSuccess: () => router.replace(returnTo) });
  if (state === 'signed-in') return <Redirect href={returnTo} />;
  return <Screen><Label heading>Need a team?</Label><Label heading>Build one. Join one. Keep one going.</Label>
    <Label muted>Maximum playtime. Minimum downtime.</Label><Card><Label>Find your next game and be ready when a spot opens.</Label>
      <Button title={signIn.isPending ? 'Opening secure sign-in…' : 'Sign in or create an account'} disabled={signIn.isPending} onPress={() => signIn.mutate()} />
      {signIn.error && <Label>Sign-in could not finish. You can try again; your password is entered only with your identity provider.</Label>}
    </Card><Label muted>TeamBuilder requests your location and notification permission only when you choose those features.</Label></Screen>;
}
