// Theme choice shared with Domenia 2, FILPRO 2 and PESTO 2: light, dark, high contrast or Windows.
// The shared settings theme stays "dark" or "light", which earlier versions accept; "Suivre Windows" and high contrast are
// interface preferences, and high contrast stays on the workstation (DeploymentConfiguration).
export const themes = [
  { id: 'light', label: 'Thème clair', icon: 'sun' },
  { id: 'dark', label: 'Thème sombre', icon: 'moon' },
  { id: 'contrast', label: 'Contraste élevé', icon: 'contrast' },
  { id: 'system', label: 'Thème : suivre Windows', icon: 'monitor' }
];
export const themeOptions = [{ value: 'system', label: 'Suivre Windows' }, { value: 'light', label: 'Clair' },
  { value: 'dark', label: 'Sombre' }, { value: 'contrast', label: 'Contraste élevé' }];
const darkScheme = matchMedia('(prefers-color-scheme: dark)');
const contrastScheme = matchMedia('(forced-colors: active), (prefers-contrast: more)');
export const themeQueries = [darkScheme, contrastScheme];

export function themePreference(settings) {
  const preferences = settings?.interface || {};
  if (preferences.followWindowsTheme) return 'system';
  if (preferences.contrastTheme) return 'contrast';
  return settings?.theme === 'light' ? 'light' : 'dark';
}
export function resolvedTheme(preference) {
  if (preference !== 'system') return preference;
  return contrastScheme.matches ? 'contrast' : darkScheme.matches ? 'dark' : 'light';
}
// app.appearance arguments. Following Windows or high contrast leaves the last light or dark theme unchanged.
export function themeChange(preference, preferences) {
  const shared = preference === 'light' || preference === 'dark';
  return { ...(shared ? { theme: preference } : {}),
    preferences: { ...preferences, followWindowsTheme: preference === 'system', contrastTheme: preference === 'contrast' } };
}
