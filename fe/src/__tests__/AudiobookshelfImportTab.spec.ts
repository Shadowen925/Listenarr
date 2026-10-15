import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import AudiobookshelfImportTab from '@/views/settings/AudiobookshelfImportTab.vue'
import { apiService } from '@/services/api'
import {
  buildFullPreviewCsv,
  buildUnmatchedCsv,
  escapeCsvField,
  exportFileName,
} from '@/utils/audiobookshelfImport'
import type { AudiobookshelfImportPreviewItem } from '@/types'

vi.mock('@/services/api', () => ({
  apiService: {
    getAudiobookshelfImportSettings: vi.fn(),
    getRootFolders: vi.fn(),
    getAudiobookshelfLibraries: vi.fn(),
    previewAudiobookshelfImport: vi.fn(),
    importAudiobookshelfItems: vi.fn(),
  },
}))

function item(
  index: number,
  status: AudiobookshelfImportPreviewItem['status'] = 'Matched',
): AudiobookshelfImportPreviewItem {
  return {
    itemId: `item-${index}`,
    title: `Book ${index}`,
    author: 'Author',
    audiobookshelfPath: `/audiobooks/Book ${index}`,
    listenarrPath: `/data/Book ${index}`,
    status,
    missingIdentifiers: false,
  }
}

async function mountWithPreview(items: AudiobookshelfImportPreviewItem[]) {
  vi.mocked(apiService.getAudiobookshelfImportSettings).mockResolvedValue({
    url: 'http://abs',
    hasSavedApiKey: true,
  })
  vi.mocked(apiService.getRootFolders).mockResolvedValue([])
  vi.mocked(apiService.getAudiobookshelfLibraries).mockResolvedValue([
    { id: 'lib', name: 'Books', folderPaths: [] },
  ])
  vi.mocked(apiService.previewAudiobookshelfImport).mockResolvedValue({ libraryId: 'lib', items })
  const wrapper = mount(AudiobookshelfImportTab)
  await flushPromises()
  const find = (label: string) => wrapper.findAll('button').find((b) => b.text() === label)!
  await find('Discover libraries')!.trigger('click')
  await flushPromises()
  await wrapper.find('select:not([aria-label])').setValue('lib')
  await find('Preview import').trigger('click')
  await flushPromises()
  return wrapper
}

describe('Audiobookshelf import preview selection', () => {
  beforeEach(() => vi.clearAllMocks())

  it('selects nothing initially, including after a refresh', async () => {
    const wrapper = await mountWithPreview(Array.from({ length: 120 }, (_, i) => item(i)))
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('0 selected')
    expect(wrapper.get('[data-testid="import-selected"]').attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="select-all-matched"]').trigger('click')
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('120 selected')

    const preview = wrapper.findAll('button').find((b) => b.text() === 'Preview import')!
    await preview.trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('0 selected')
  })

  it('distinguishes visible from all matched and keeps selection across paging and filtering', async () => {
    const wrapper = await mountWithPreview(Array.from({ length: 120 }, (_, i) => item(i)))
    await wrapper.get('[data-testid="select-visible-matched"]').trigger('click')
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('50 selected')

    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Next')!
      .trigger('click')
    await wrapper.get('input[type="search"]').setValue('Book 1')
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('50 selected')
  })

  it('handles 839 books: visible selects one page, all selects every matched book', async () => {
    const wrapper = await mountWithPreview(Array.from({ length: 839 }, (_, i) => item(i)))
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('0 selected')
    await wrapper.get('[data-testid="select-visible-matched"]').trigger('click')
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('50 selected')
    await wrapper.get('[data-testid="select-all-matched"]').trigger('click')
    expect(wrapper.get('[data-testid="selected-count"]').text()).toBe('839 selected')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await wrapper.get('[data-testid="import-selected"]').trigger('click')
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Import 839 selected'))
  })

  it('requires confirmation with the exact count before importing', async () => {
    const wrapper = await mountWithPreview([item(1), item(2)])
    await wrapper.get('[data-testid="select-all-matched"]').trigger('click')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await wrapper.get('[data-testid="import-selected"]').trigger('click')
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Import 2 selected'))
    expect(apiService.importAudiobookshelfItems).not.toHaveBeenCalled()
  })

  it('disables Export Unmatched when nothing is unmatched', async () => {
    const wrapper = await mountWithPreview([item(1)])
    expect(wrapper.get('[data-testid="export-unmatched"]').attributes('disabled')).toBeDefined()
  })
})

describe('Audiobookshelf CSV export', () => {
  it('escapes commas, quotes, line breaks and formula prefixes', () => {
    expect(escapeCsvField('a,b')).toBe('"a,b"')
    expect(escapeCsvField('say "hi"')).toBe('"say ""hi"""')
    expect(escapeCsvField('line1\nline2')).toBe('"line1\nline2"')
    expect(escapeCsvField('=SUM(A1)')).toBe("'=SUM(A1)")
    expect(escapeCsvField('+1')).toBe("'+1")
    expect(escapeCsvField('@cmd')).toBe("'@cmd")
    expect(escapeCsvField(null)).toBe('')
  })

  it('exports all unmatched rows with Unicode intact and no matched rows', () => {
    const unmatched = {
      ...item(1, 'Unmatched'),
      title: 'KrÃ¥korna â€“ æ±äº¬',
      failureCategory: 'AudioFilesMissing',
      reason: 'missing',
    }
    const csv = buildUnmatchedCsv([unmatched, item(2)])
    expect(csv).toContain('KrÃ¥korna â€“ æ±äº¬')
    expect(csv).toContain('Match failure category')
    expect(csv).not.toContain('Book 2')
    expect(buildFullPreviewCsv([unmatched, item(2)])).toContain('Book 2')
  })

  it('builds a dated descriptive file name', () => {
    expect(exportFileName('unmatched', new Date(2026, 0, 5))).toBe(
      'listenarr-audiobookshelf-unmatched-2026-01-05.csv',
    )
  })
})
