import { router } from 'expo-router';
import { Button, Label, Screen } from '../../src/components/ui';
export default function Callback() {
  return <Screen><Label heading>Completing sign-in</Label><Label>The secure browser session is returning to TeamBuilder.</Label>
    <Button title="Continue to TeamBuilder" onPress={() => router.replace('/')} /></Screen>;
}
