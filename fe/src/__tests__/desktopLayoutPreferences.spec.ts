import { beforeEach, describe, expect, it } from 'vitest'
import {
  getDesktopSearchVisible,
  getDesktopSidebarVisible,
  setDesktopSearchVisible,
  setDesktopSidebarVisible,
} from '@/utils/desktopLayoutPreferences'

describe('desktop layout preferences', () => {
  beforeEach(() => window.localStorage.clear())

  it('defaults the desktop sidebar and search bar to visible', () => {
    expect(getDesktopSidebarVisible()).toBe(true)
    expect(getDesktopSearchVisible()).toBe(true)
  })

  it('persists visibility changes across reads', () => {
    setDesktopSidebarVisible(false)
    setDesktopSearchVisible(false)

    expect(getDesktopSidebarVisible()).toBe(false)
    expect(getDesktopSearchVisible()).toBe(false)

    setDesktopSidebarVisible(true)
    setDesktopSearchVisible(true)
    expect(getDesktopSidebarVisible()).toBe(true)
    expect(getDesktopSearchVisible()).toBe(true)
  })
})
