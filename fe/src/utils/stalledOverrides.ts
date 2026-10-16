export type StalledOverrideValues = Record<string, string>

export const STALLED_OVERRIDE_KEYS = [
  'stalledEnabled',
  'stalledTimeoutMinutes',
  'stalledMetadataTimeoutMinutes',
  'stalledMaxReplacementAttempts',
  'stalledAutoSearchAlternatives',
  'stalledCleanupBehavior',
  'stalledMinimumProgressBytes',
  'stalledRetryDelayMinutes',
  'stalledIgnoreManuallyPaused',
  'stalledAutoBlocklist',
] as const

const NUMERIC_RANGES: Record<string, [number, number]> = {
  stalledTimeoutMinutes: [5, 10080],
  stalledMetadataTimeoutMinutes: [1, 1440],
  stalledMaxReplacementAttempts: [0, 10],
  stalledRetryDelayMinutes: [0, 1440],
  stalledMinimumProgressBytes: [1, 1_000_000_000],
}

const CLEANUP_VALUES = ['none', 'remove', 'remove_and_delete']

export function readStalledOverrides(settings: Record<string, unknown> | undefined | null) {
  const values: StalledOverrideValues = {}
  for (const key of STALLED_OVERRIDE_KEYS) {
    const raw = settings?.[key]
    if (raw !== undefined && raw !== null && String(raw) !== '') values[key] = String(raw)
  }
  return values
}

export function buildStalledOverrides(values: StalledOverrideValues) {
  const result: Record<string, string | number | boolean> = {}
  for (const key of STALLED_OVERRIDE_KEYS) {
    const raw = (values[key] ?? '').trim()
    if (!raw) continue
    if (key in NUMERIC_RANGES) result[key] = Number(raw)
    else if (key === 'stalledCleanupBehavior') result[key] = raw
    else result[key] = raw === 'true'
  }
  return result
}

/** Returns a message for the first invalid override, or null when all values are acceptable. */
export function validateStalledOverrides(values: StalledOverrideValues): string | null {
  for (const [key, [min, max]] of Object.entries(NUMERIC_RANGES)) {
    const raw = (values[key] ?? '').trim()
    if (!raw) continue
    const n = Number(raw)
    if (!Number.isInteger(n) || n < min || n > max) {
      return `${key} must be a whole number between ${min} and ${max}.`
    }
  }
  const cleanup = (values.stalledCleanupBehavior ?? '').trim()
  if (cleanup && !CLEANUP_VALUES.includes(cleanup)) {
    return 'stalledCleanupBehavior must be none, remove or remove_and_delete.'
  }
  return null
}
