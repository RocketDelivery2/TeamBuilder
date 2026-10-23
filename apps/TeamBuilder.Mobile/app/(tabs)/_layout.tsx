import { Tabs } from 'expo-router/js-tabs';
import { Ionicons } from '@expo/vector-icons';
import { AccountGate } from '../../src/components/AccountGate';
import { useTheme } from '../../src/theme/Theme';
export default function TabLayout() {
  const { colors } = useTheme();
  return <AccountGate><Tabs screenOptions={{ headerShown: false, tabBarActiveTintColor: colors.accent,
    tabBarInactiveTintColor: colors.muted, tabBarStyle: { backgroundColor: colors.surface, borderTopColor: colors.border } }}>
    <Tabs.Screen name="discover" options={{ title: 'Discover', tabBarIcon: ({ color, size }) => <Ionicons name="compass-outline" color={typeof color === 'string' ? color : colors.muted} size={size} /> }} />
    <Tabs.Screen name="my-games" options={{ title: 'My games', tabBarIcon: ({ color, size }) => <Ionicons name="basketball-outline" color={typeof color === 'string' ? color : colors.muted} size={size} /> }} />
    <Tabs.Screen name="create" options={{ title: 'Create', tabBarIcon: ({ color, size }) => <Ionicons name="add-circle-outline" color={typeof color === 'string' ? color : colors.muted} size={size} /> }} />
    <Tabs.Screen name="profile" options={{ title: 'Profile', tabBarIcon: ({ color, size }) => <Ionicons name="person-outline" color={typeof color === 'string' ? color : colors.muted} size={size} /> }} />
  </Tabs></AccountGate>;
}
