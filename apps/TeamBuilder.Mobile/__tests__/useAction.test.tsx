import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import { useAction } from '../src/hooks/useAction';
test('two taps before a render dispatch only one creation', async () => {
  let complete!: () => void;
  const create = jest.fn(() => new Promise<void>(resolve => { complete = resolve; }));
  const client = new QueryClient({ defaultOptions: { mutations: { gcTime: 0 }, queries: { gcTime: 0 } } });
  const hook = await renderHook(() => useAction({ mutationFn: create }), {
    wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  await act(async () => { hook.result.current.run(); hook.result.current.run(); });
  expect(create).toHaveBeenCalledTimes(1);
  await act(async () => { complete(); });
  await hook.unmount();
  client.clear();
});
