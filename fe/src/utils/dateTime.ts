const HAS_TIMEZONE = /(Z|[+-]\d{2}:?\d{2})$/i

// The API stores UTC timestamps; some are serialized without an offset, which
// the browser would otherwise read as local time and display hours off.
export function parseApiDate(value: string | number | Date): Date {
  if (typeof value !== 'string') return new Date(value)
  const trimmed = value.trim()
  const isDateTime = /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/.test(trimmed)
  return new Date(isDateTime && !HAS_TIMEZONE.test(trimmed) ? `${trimmed.replace(' ', 'T')}Z` : trimmed)
}

export function formatApiDateTime(value: string | number | Date): string {
  return parseApiDate(value).toLocaleString()
}
