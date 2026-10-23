import { useState } from 'react';
import DateTimePicker from '@react-native-community/datetimepicker';
import { Platform, View } from 'react-native';
import { Button, Field } from './ui';
// The picker edits wall-clock fields in UTC; conversion to the selected IANA zone is separate.
export function DateTimeField({ date, time, onDate, onTime }: {
  date: string; time: string; onDate: (value: string) => void; onTime: (value: string) => void;
}) {
  const [mode, setMode] = useState<'date' | 'time' | null>(null);
  if (Platform.OS === 'web') return <View style={{ gap: 12 }}>
    <Field label="Date (YYYY-MM-DD)" value={date} onChangeText={onDate} />
    <Field label="Time (HH:mm)" value={time} onChangeText={onTime} />
  </View>;
  const value = new Date(date + 'T' + time + ':00Z');
  return <View style={{ gap: 10 }}>
    <Button title={'Date: ' + date} secondary onPress={() => setMode('date')} />
    <Button title={'Time: ' + time} secondary onPress={() => setMode('time')} />
    {mode && <><DateTimePicker value={Number.isFinite(value.getTime()) ? value : new Date()} mode={mode} timeZoneName="UTC"
      onChange={(event, selected) => {
        if (Platform.OS === 'android') setMode(null);
        if (event.type !== 'set' || !selected) return;
        if (mode === 'date') onDate(selected.toISOString().slice(0, 10));
        else onTime(selected.toISOString().slice(11, 16));
      }} />{Platform.OS === 'ios' && <Button title="Done choosing time" secondary onPress={() => setMode(null)} />}</>}
  </View>;
}
