import * as Location from 'expo-location';
export async function foregroundPoint() {
  const permission = await Location.requestForegroundPermissionsAsync();
  if (!permission.granted) throw new Error('Location permission was not granted. Enter coordinates manually instead.');
  let timer: ReturnType<typeof setTimeout>;
  const timeout = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Location timed out. Try again or enter coordinates.')), 20000); });
  try {
    const result = await Promise.race([Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.Balanced }), timeout]);
    return { lat: result.coords.latitude, lon: result.coords.longitude };
  } finally { clearTimeout(timer!); }
}
