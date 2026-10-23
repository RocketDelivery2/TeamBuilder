import { useRef } from 'react';
import { useMutation, type UseMutationOptions } from '@tanstack/react-query';
// The immediate latch covers repeated taps before React renders the pending state.
export function useAction<T, V = void>(options: UseMutationOptions<T, Error, V>) {
  const busy = useRef(false);
  const mutation = useMutation({ ...options, retry: false, networkMode: 'always' });
  const run = (variables: V) => {
    if (busy.current) return;
    busy.current = true;
    void mutation.mutateAsync(variables).catch(() => {}).finally(() => { busy.current = false; });
  };
  return { ...mutation, run };
}
