import { createContext, useContext, useEffect, useMemo, useSyncExternalStore, type PropsWithChildren } from 'react';
import { AppState, View } from 'react-native';
import { QueryClient, QueryClientProvider, focusManager, onlineManager, useQuery } from '@tanstack/react-query';
import * as Network from 'expo-network';
import type { MobileConfig } from './config';
import { createSession } from './auth/oidc';
import { ApiError, HttpClient, NetworkError } from './api/http';
import { PlayerApi } from './api/players';
import { GameApi } from './api/games';
import { Label } from './components/ui';
const createServices = (config: MobileConfig) => {
  const session = createSession(config);
  const queryClient = new QueryClient({ defaultOptions: {
    queries: { staleTime: 15000, gcTime: 300000, retry: (count, error) => count < 1 && (error instanceof NetworkError && error.kind !== 'cancelled' || error instanceof ApiError && error.status >= 500) },
    mutations: { retry: false, networkMode: 'always' },
  } });
  const http = new HttpClient({ baseUrl: config.apiBaseUrl, getToken: refresh => session.token(refresh),
    onUnauthorized: token => session.invalidate(token), isOnline: async () => (await Network.getNetworkStateAsync()).isConnected !== false });
  return { config, session, queryClient, http, players: new PlayerApi(http), games: new GameApi(http) };
};
type Services = ReturnType<typeof createServices>;
const Context = createContext<Services | null>(null);
export function Providers({ config, children }: PropsWithChildren<{ config: MobileConfig }>) {
  const services = useMemo(() => createServices(config), [config]);
  const network = Network.useNetworkState();
  useEffect(() => {
    const listener = AppState.addEventListener('change', state => focusManager.setFocused(state === 'active'));
    const unsubscribe = services.session.subscribe(() => {
      if (services.session.state === 'signed-out') {
        void services.queryClient.cancelQueries();
        services.queryClient.clear();
      }
    });
    void services.session.restore();
    return () => { listener.remove(); unsubscribe(); services.queryClient.clear(); };
  }, [services]);
  useEffect(() => { onlineManager.setOnline(network.isConnected !== false); }, [network.isConnected]);
  return <Context.Provider value={services}><QueryClientProvider client={services.queryClient}>
    {network.isConnected === false && <View accessibilityRole="alert" style={{ padding: 12 }}><Label>No internet connection. Changes require a connection.</Label></View>}
    {children}
  </QueryClientProvider></Context.Provider>;
}
export function useServices() { const services = useContext(Context); if (!services) throw new Error('Missing app services.'); return services; }
export function useSession() { const { session } = useServices(); return useSyncExternalStore(session.subscribe, session.snapshot, session.snapshot); }
export function usePlayer() {
  const { players } = useServices(), state = useSession();
  return useQuery({ queryKey: ['me'], queryFn: ({ signal }) => players.me(signal), enabled: state === 'signed-in', retry: false });
}
