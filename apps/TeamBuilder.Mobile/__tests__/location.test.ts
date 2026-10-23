import * as Location from 'expo-location';
import { foregroundPoint } from '../src/location/foreground';
jest.mock('expo-location', () => ({ requestForegroundPermissionsAsync: jest.fn(), getCurrentPositionAsync: jest.fn(), Accuracy: { Balanced: 3 } }));
test('denied permission never requests a position and explains the fallback', async () => {
  jest.mocked(Location.requestForegroundPermissionsAsync).mockResolvedValue({ granted: false } as Location.LocationPermissionResponse);
  await expect(foregroundPoint()).rejects.toThrow('Enter coordinates manually');
  expect(Location.getCurrentPositionAsync).not.toHaveBeenCalled();
});
