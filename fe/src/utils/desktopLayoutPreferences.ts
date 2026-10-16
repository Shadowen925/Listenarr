const SIDEBAR_KEY = 'listenarr.desktopSidebarVisible'
const SEARCH_KEY = 'listenarr.desktopSearchVisible'

function getPreference(key: string, defaultValue: boolean): boolean {
  try {
    const value = window.localStorage.getItem(key)
    return value === null ? defaultValue : value === 'true'
  } catch {
    return defaultValue
  }
}

function setPreference(key: string, value: boolean): void {
  try {
    window.localStorage.setItem(key, String(value))
  } catch {
    // The default visibility remains available for this session if storage is unavailable.
  }
}

export const getDesktopSidebarVisible = () => getPreference(SIDEBAR_KEY, true)
export const setDesktopSidebarVisible = (visible: boolean) => setPreference(SIDEBAR_KEY, visible)
export const getDesktopSearchVisible = () => getPreference(SEARCH_KEY, true)
export const setDesktopSearchVisible = (visible: boolean) => setPreference(SEARCH_KEY, visible)
