import type {
  AudiobookshelfImportItemResult,
  AudiobookshelfImportPreviewItem,
  AudiobookshelfImportResult,
  AudiobookshelfSeriesSyncPreviewItem,
} from '@/types'

const FORMULA_PREFIX = /^[=+\-@\t\r]/

/** Escapes one CSV field: neutralizes spreadsheet formulas and quotes commas, quotes and line breaks. */
export function escapeCsvField(value: string | number | null | undefined): string {
  let text = value === null || value === undefined ? '' : String(value)
  if (FORMULA_PREFIX.test(text)) {
    text = `'${text}`
  }
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

export function toCsv(
  headers: string[],
  rows: Array<Array<string | number | null | undefined>>,
): string {
  return [headers, ...rows].map((row) => row.map(escapeCsvField).join(',')).join('\r\n') + '\r\n'
}

const PREVIEW_HEADERS = [
  'Audiobookshelf item ID',
  'Book title',
  'Author',
  'Series',
  'Series position',
  'Original Audiobookshelf path',
  'Translated Listenarr path',
  'File count',
  'File formats',
  'ASIN',
  'ISBN',
  'Match failure category',
  'Detailed failure reason',
]

function previewRow(item: AudiobookshelfImportPreviewItem) {
  return [
    item.itemId,
    item.title,
    item.author,
    item.series,
    item.seriesPosition,
    item.audiobookshelfPath,
    item.listenarrPath,
    item.fileCount ?? 0,
    (item.fileFormats ?? []).join('; '),
    item.asin,
    (item.isbn ?? []).join('; '),
    item.failureCategory,
    item.reason,
  ]
}

export function buildUnmatchedCsv(items: AudiobookshelfImportPreviewItem[]): string {
  return toCsv(PREVIEW_HEADERS, items.filter((item) => item.status === 'Unmatched').map(previewRow))
}

export function buildFullPreviewCsv(items: AudiobookshelfImportPreviewItem[]): string {
  return toCsv(
    ['Match status', ...PREVIEW_HEADERS],
    items.map((item) => [item.status, ...previewRow(item)]),
  )
}

export function buildImportResultCsv(result: AudiobookshelfImportResult): string {
  const rows = (result.items ?? []).map((item: AudiobookshelfImportItemResult) => [
    result.importedAt,
    result.libraryId,
    item.itemId,
    item.title,
    item.outcome,
    item.reason,
  ])
  return toCsv(['Import timestamp', 'Library ID', 'Item ID', 'Title', 'Outcome', 'Reason'], rows)
}

export function buildSeriesSyncCsv(items: AudiobookshelfSeriesSyncPreviewItem[]): string {
  return toCsv(
    [
      'Audiobookshelf item ID',
      'Listenarr audiobook ID',
      'Title',
      'Author',
      'Status',
      'Listenarr series',
      'Source series',
      'Proposed series',
      'Source series count',
      'Parsed series count',
      'Listenarr series count',
      'Failure reason',
    ],
    items.map((item) => [
      item.itemId,
      item.audiobookId,
      item.title,
      item.author,
      item.status,
      (item.currentSeries ?? [])
        .map(
          (membership) =>
            `${membership.name}${membership.position ? ` #${membership.position}` : ''}`,
        )
        .join('; '),
      (item.audiobookshelfSeries ?? [])
        .map(
          (membership) =>
            `${membership.name}${membership.position ? ` #${membership.position}` : ''}`,
        )
        .join('; '),
      (item.proposedSeries ?? [])
        .map(
          (membership) =>
            `${membership.name}${membership.position ? ` #${membership.position}` : ''}`,
        )
        .join('; '),
      item.sourceSeriesCount,
      item.parsedSeriesCount,
      item.listenarrSeriesCount,
      item.failureReason,
    ]),
  )
}

export function exportFileName(
  kind: 'unmatched' | 'preview' | 'import-results' | 'series-diagnostics',
  extensionOrNow: 'csv' | 'json' | Date = 'csv',
  now = new Date(),
) {
  const extension = extensionOrNow instanceof Date ? 'csv' : extensionOrNow
  if (extensionOrNow instanceof Date) now = extensionOrNow
  const pad = (value: number) => String(value).padStart(2, '0')
  const date = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
  return `listenarr-audiobookshelf-${kind}-${date}.${extension}`
}

/** Downloads CSV text with a UTF-8 BOM so spreadsheet apps preserve Unicode. */
export function downloadCsv(fileName: string, csv: string) {
  const blob = new Blob(['\uFEFF', csv], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  try {
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}

export function downloadJson(fileName: string, json: string) {
  const blob = new Blob([json], { type: 'application/json;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  try {
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}
