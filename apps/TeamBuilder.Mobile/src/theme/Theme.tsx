import { createContext, useContext, useState, type PropsWithChildren } from 'react';
import { useColorScheme } from 'react-native';
export type ThemeChoice = 'system' | 'light' | 'dark';
const palettes = {
  dark: { background: '#0b1220', surface: '#172236', text: '#f4f7fc', muted: '#b5c2d6', accent: '#a9f369', accentText: '#15200d', danger: '#ffaaaa', border: '#3d4b62' },
  light: { background: '#f5f7fb', surface: '#ffffff', text: '#152238', muted: '#4e6078', accent: '#376c10', accentText: '#ffffff', danger: '#a51e28', border: '#c5cfdd' },
};
const Context = createContext({ colors: palettes.light, choice: 'system' as ThemeChoice, setChoice: (_: ThemeChoice) => {}, dark: false });
export function ThemeProvider({ children }: PropsWithChildren) {
  const system = useColorScheme();
  const [choice, setChoice] = useState<ThemeChoice>('system');
  const dark = choice === 'dark' || (choice === 'system' && system === 'dark');
  return <Context.Provider value={{ colors: dark ? palettes.dark : palettes.light, choice, setChoice, dark }}>{children}</Context.Provider>;
}
export const useTheme = () => useContext(Context);
