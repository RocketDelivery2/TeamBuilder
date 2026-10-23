import { Stack, type ErrorBoundaryProps } from 'expo-router';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { StatusBar } from 'expo-status-bar';
import { runtimeConfig, type MobileConfig } from '../src/config';
import { Providers, useServices, useSession } from '../src/Providers';
import { ThemeProvider, useTheme } from '../src/theme/Theme';
import { Button, Label, Screen } from '../src/components/ui';
let config: MobileConfig | undefined;
let configurationError = '';
try { config = runtimeConfig(); } catch (error) { configurationError = error instanceof Error ? error.message : 'Configuration is incomplete.'; }
function Navigation() {
  const { colors, dark } = useTheme();
  const state = useSession(), { session } = useServices();
  if (state === 'unavailable') return <Screen><Label heading>Secure storage unavailable</Label>
    <Label>Unlock this device and retry before signing in. TeamBuilder cannot safely store or clear this session yet.</Label>
    <Button title="Retry secure storage" onPress={() => void session.restore()} /></Screen>;
  return <><StatusBar style={dark ? 'light' : 'dark'} /><Stack screenOptions={{ headerStyle: { backgroundColor: colors.surface },
    headerTintColor: colors.text, contentStyle: { backgroundColor: colors.background }, title: 'TeamBuilder' }} /></>;
}
export default function RootLayout() {
  return <SafeAreaProvider><ThemeProvider>{config ?
    <Providers config={config}><Navigation /></Providers> :
    <Screen><Label heading>TeamBuilder setup</Label><Label>{configurationError}</Label><Label muted>This build needs its environment and public sign-in configuration. See the mobile setup guide.</Label></Screen>}
  </ThemeProvider></SafeAreaProvider>;
}
export function ErrorBoundary({ retry }: ErrorBoundaryProps) {
  return <SafeAreaProvider><ThemeProvider><Screen><Label heading>Something went wrong</Label>
    <Label>Your changes may not have completed. Refresh the game before trying again.</Label><Button title="Reload screen" onPress={retry} />
  </Screen></ThemeProvider></SafeAreaProvider>;
}
