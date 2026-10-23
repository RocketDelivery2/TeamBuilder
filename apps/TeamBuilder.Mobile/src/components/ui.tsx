import type { PropsWithChildren } from 'react';
import { ActivityIndicator, Alert, KeyboardAvoidingView, Platform, Pressable, RefreshControl, ScrollView, Text, TextInput, View, type TextInputProps } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useTheme } from '../theme/Theme';
import { errorMessage } from '../api/http';
export function Label({ children, muted = false, heading = false }: PropsWithChildren<{ muted?: boolean; heading?: boolean }>) {
  const { colors } = useTheme();
  return <Text accessibilityRole={heading ? 'header' : undefined} style={{ color: muted ? colors.muted : colors.text, fontSize: heading ? 26 : 16, lineHeight: heading ? 34 : 24, fontWeight: heading ? '700' : '400' }}>{children}</Text>;
}
export function Screen({ children, refreshing = false, onRefresh }: PropsWithChildren<{ refreshing?: boolean; onRefresh?: () => void }>) {
  const { colors } = useTheme();
  return <SafeAreaView edges={['left', 'right', 'bottom']} style={{ flex: 1, backgroundColor: colors.background }}>
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ padding: 20, gap: 18, paddingBottom: 40, width: '100%', maxWidth: 760, alignSelf: 'center' }}
        refreshControl={onRefresh ? <RefreshControl refreshing={refreshing} onRefresh={onRefresh} tintColor={colors.accent} /> : undefined}>{children}</ScrollView>
    </KeyboardAvoidingView>
  </SafeAreaView>;
}
export function Card({ children }: PropsWithChildren) {
  const { colors } = useTheme();
  return <View style={{ backgroundColor: colors.surface, borderColor: colors.border, borderWidth: 1, borderRadius: 18, padding: 18, gap: 12 }}>{children}</View>;
}
export function Button({ title, onPress, disabled = false, secondary = false, danger = false }: {
  title: string; onPress: () => void; disabled?: boolean; secondary?: boolean; danger?: boolean;
}) {
  const { colors } = useTheme();
  return <Pressable accessibilityRole="button" accessibilityLabel={title} accessibilityState={{ disabled }} disabled={disabled} onPress={onPress}
    style={({ pressed }) => ({ minHeight: 48, justifyContent: 'center', alignItems: 'center', padding: 12, borderRadius: 12,
      borderWidth: 1, borderColor: danger ? colors.danger : colors.accent, opacity: disabled ? 0.5 : pressed ? 0.75 : 1,
      backgroundColor: secondary || danger ? colors.surface : colors.accent })}>
    <Text style={{ fontSize: 16, fontWeight: '700', color: danger ? colors.danger : secondary ? colors.text : colors.accentText }}>{title}</Text>
  </Pressable>;
}
export function Field({ label, ...props }: TextInputProps & { label: string }) {
  const { colors } = useTheme();
  return <View style={{ gap: 6 }}><Label>{label}</Label><TextInput accessibilityLabel={label} placeholderTextColor={colors.muted}
    {...props} style={[{ borderWidth: 1, borderColor: colors.border, borderRadius: 10, minHeight: 48, padding: 12, fontSize: 16, color: colors.text, backgroundColor: colors.surface }, props.style]} /></View>;
}
export function Loading({ label = 'Loading…' }: { label?: string }) {
  const { colors } = useTheme();
  return <View accessibilityLiveRegion="polite" style={{ padding: 20, gap: 10 }}><ActivityIndicator color={colors.accent} /><Label>{label}</Label></View>;
}
export function ErrorNotice({ error, retry }: { error: unknown; retry?: () => void }) {
  return <View accessibilityRole="alert" accessibilityLiveRegion="polite" style={{ gap: 8 }}><Label>{errorMessage(error)}</Label>{retry && <Button title="Retry" secondary onPress={retry} />}</View>;
}
export function confirm(title: string, message: string, action: () => void) {
  Alert.alert(title, message, [{ text: 'Keep current state', style: 'cancel' }, { text: 'Confirm', style: 'destructive', onPress: action }]);
}
