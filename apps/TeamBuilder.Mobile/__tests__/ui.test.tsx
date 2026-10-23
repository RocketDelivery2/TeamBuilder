import { render, screen, userEvent } from '@testing-library/react-native';
import { Button, ErrorNotice, Field } from '../src/components/ui';
import { ThemeProvider } from '../src/theme/Theme';
import { ApiError } from '../src/api/http';
test('disabled actions are exposed to screen readers and cannot submit twice', async () => {
  const press = jest.fn();
  await render(<ThemeProvider><Button title="Join game" disabled onPress={press} /></ThemeProvider>);
  const button = screen.getByRole('button', { name: 'Join game' });
  expect(button).toBeDisabled();
  await userEvent.press(button);
  expect(press).not.toHaveBeenCalled();
});
test('fields have labels and conflict errors explain capacity', async () => {
  await render(<ThemeProvider><Field label="Username" value="" /><ErrorNotice error={new ApiError(409, { code: 'RequirementFull' })} /></ThemeProvider>);
  expect(screen.getByLabelText('Username')).toBeTruthy();
  expect(screen.getByText(/spot has already been taken/)).toBeTruthy();
});
