import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SeriesResolutionDialog from '@/components/domain/collection/SeriesResolutionDialog.vue'

const { apiServiceMock } = vi.hoisted(() => ({
  apiServiceMock: {
    previewSeriesResolution: vi.fn(),
    mergeSeriesResolution: vi.fn(),
    refreshSeriesCatalogByAsin: vi.fn(),
  },
}))

vi.mock('@/services/api', () => ({ apiService: apiServiceMock }))

const preview = {
  seriesId: 'abs:abs-series-1',
  importedSeriesName: 'Dungeon Crawler Carl',
  importedBookCount: 2,
  booksWithoutAsin: [],
  unresolvedBooks: [],
  conflicts: [],
  isAmbiguous: false,
  candidates: [
    {
      asin: 'SERIES123',
      name: 'Dungeon Crawler Carl',
      confidence: 'medium',
      score: 65,
      existingLibraryBookCount: 1,
      proposedMergedBookCount: 3,
      overlappingBookCount: 1,
      evidence: ['Exact series name match'],
      conflicts: [],
    },
  ],
}

describe('SeriesResolutionDialog', () => {
  let wrapper: ReturnType<typeof mount> | undefined

  beforeEach(() => {
    vi.clearAllMocks()
    apiServiceMock.previewSeriesResolution.mockResolvedValue([preview])
    apiServiceMock.mergeSeriesResolution.mockResolvedValue({
      seriesId: preview.seriesId,
      canonicalSeriesAsin: 'SERIES123',
      canonicalSeriesName: 'Dungeon Crawler Carl',
      transferredMembershipCount: 2,
      deduplicatedMembershipCount: 0,
      mergedBookCount: 2,
      catalogWarning: null,
    })
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
  })

  it('requires an explicit candidate choice and confirmation before merging', async () => {
    wrapper = mount(SeriesResolutionDialog, {
      attachTo: document.body,
      props: { modelValue: true },
    })
    await flushPromises()

    const candidateSelect = document.body.querySelector<HTMLSelectElement>('select')
    expect(candidateSelect).not.toBeNull()
    candidateSelect!.value = 'SERIES123'
    candidateSelect!.dispatchEvent(new Event('change', { bubbles: true }))
    await flushPromises()

    const mergeButton = Array.from(document.body.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Merge selected'),
    )
    expect(mergeButton).toBeDefined()
    mergeButton!.click()
    await flushPromises()
    expect(apiServiceMock.mergeSeriesResolution).not.toHaveBeenCalled()

    const confirmButton = Array.from(document.body.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Confirm merge'),
    )
    expect(confirmButton).toBeDefined()
    confirmButton!.click()
    await flushPromises()

    expect(apiServiceMock.mergeSeriesResolution).toHaveBeenCalledWith({
      seriesId: preview.seriesId,
      candidateAsin: 'SERIES123',
      region: 'us',
    })
    expect(wrapper.emitted('merged')).toHaveLength(1)
  })

  it('does not auto-select ambiguous candidates with the high-confidence action', async () => {
    apiServiceMock.previewSeriesResolution.mockResolvedValue([{ ...preview, isAmbiguous: true }])
    wrapper = mount(SeriesResolutionDialog, {
      attachTo: document.body,
      props: { modelValue: true },
    })
    await flushPromises()

    const selectHigh = Array.from(document.body.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Select high-confidence'),
    )
    expect(selectHigh).toBeDefined()
    selectHigh!.click()

    expect(document.body.textContent).toContain('0 series selected')
  })
})
