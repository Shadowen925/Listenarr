/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import { API_BASE_PATH } from '@/services/apiBase'
import { useLibraryStore } from '@/stores/library'
import { useScanNotificationsStore } from '@/stores/scanNotifications'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService, ensureImageCached } from '@/services/api'
import { signalRService } from '@/services/signalr'
import AudiobookDetailViewCmp from '@/views/library/AudiobookDetailView.vue'
const routerPushMock = vi.fn()
// Mock useRoute to provide params for the detail view
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: '5' } }),
  useRouter: () => ({ push: routerPushMock }),
}))

// Mock api service ensureImageCached and getImageUrl
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
    getLibrary: vi.fn(async () => []),
    getAutomaticSearchHistory: vi.fn(async () => []),
    scanAudiobook: vi.fn(),
    getWeakStorageMissingFiles: vi.fn(async () => ({ items: [] })),
    confirmWeakStorageMissingFiles: vi.fn(),
  },
  ensureImageCached: vi.fn(async () => true),
}))

// Mock signalr service to provide missing hooks (e.g., onScanJobUpdate)
vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onScanJobUpdate: vi.fn(() => () => undefined),
    onSearchProgress: vi.fn(() => () => undefined),
  },
}))

describe('AudiobookDetailView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
  })

  it('calls ensureImageCached for the audiobook cover on load', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const imagePath = `${API_BASE_PATH}/images/ASIN000005`
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 5, title: 'Detail Book', imageUrl: imagePath, files: [] },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    expect(ensureImageCached).toHaveBeenCalled()
    const ensureImageCachedMock = ensureImageCached as unknown as {
      mock: { calls: Array<[string]> }
    }
    expect(ensureImageCachedMock.mock.calls[0]?.[0]).toBe(imagePath)
  })

  it('shows persisted search history and exports only redacted history fields', async () => {
    vi.mocked(apiService.getAutomaticSearchHistory).mockResolvedValueOnce([
      {
        id: 8,
        audiobookId: 5,
        searchTimestamp: '2026-05-22T10:00:00Z',
        searchTrigger: 'Periodic',
        queriesAttempted: ['Dune Frank Herbert'],
        indexersQueried: ['Indexer A'],
        resultsReturned: 2,
        distinctCandidateCount: 1,
        identityMatchCount: 1,
        qualityRejectedCount: 0,
        indexerOutcomes: [
          {
            query: 'Dune Frank Herbert',
            indexerName: 'Indexer A',
            outcome: 'Succeeded',
            resultsReturned: 2,
            failureReason: null,
          },
        ],
        rejectionReasons: ['BelowQuality'],
        submissionAttempts: [
          {
            releaseTitle: 'Dune audiobook',
            clientType: 'sabnzbd',
            outcome: 'Failed',
            failureReason: 'ClientUnavailable',
          },
        ],
        selectedRelease: 'Dune audiobook',
        downloadClient: 'sabnzbd',
        finalOutcome: 'AllSubmissionsFailed',
        nextRetryAt: '2026-05-22T11:00:00Z',
      },
    ])
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:diagnostics')
    const anchorClick = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined)
    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.find('.search-history-list').text()).toContain('Periodic — AllSubmissionsFailed')
    expect(wrapper.find('.search-history-list').text()).toContain('Queries: Dune Frank Herbert')
    expect(wrapper.find('.search-history-list').text()).toContain(
      'Indexer A / Dune Frank Herbert: Succeeded',
    )
    expect(wrapper.text()).toContain('ClientUnavailable')
    expect(apiService.getAutomaticSearchHistory).toHaveBeenCalledWith(5, 25)

    await wrapper.find('.automatic-search-diagnostics button').trigger('click')
    expect(createObjectUrl).toHaveBeenCalledOnce()
    expect(anchorClick).toHaveBeenCalledOnce()
    const exportedBlob = createObjectUrl.mock.calls[0][0] as Blob
    const exportedText = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result))
      reader.onerror = () => reject(reader.error)
      reader.readAsText(exportedBlob)
    })
    expect(exportedText).toContain('Dune audiobook')
    expect(exportedText).toContain('Dune Frank Herbert')
    expect(exportedText).not.toContain('torrentUrl')
    expect(exportedText).not.toContain('magnet:')
    wrapper.unmount()
    createObjectUrl.mockRestore()
    anchorClick.mockRestore()
  })

  it('navigates to the author, narrator, publisher, series, and genre collections when their tags are clicked', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Brandon Sanderson'],
        narrators: ['Michael Kramer'],
        publisher: 'Tor Audio',
        series: 'Mistborn',
        seriesMemberships: [
          {
            seriesName: 'Mistborn',
            seriesNumber: '1',
            seriesAsin: 'MISTBORN-SERIES',
            seriesId: 'abs:mistborn-series',
            isPrimary: true,
          },
          {
            seriesName: 'Cosmere',
            seriesNumber: '3',
            seriesAsin: 'COSMERE-SERIES',
            seriesId: 'local:cosmere-series',
            isPrimary: false,
          },
        ],
        genres: ['Fantasy'],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const authorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Brandon Sanderson'))
    const narratorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Michael Kramer'))
    const publisherTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Tor Audio'))
    const seriesTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Mistborn'))
    const genreTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Fantasy'))

    expect(authorTag).toBeTruthy()
    expect(narratorTag).toBeTruthy()
    expect(publisherTag).toBeTruthy()
    expect(seriesTag).toBeTruthy()
    expect(genreTag).toBeTruthy()
    expect(wrapper.findAll('.detail-series-membership')).toHaveLength(2)
    expect(
      wrapper
        .findAll('.detail-series-membership')
        .find((membership) => membership.text().includes('Cosmere'))
        ?.text(),
    ).toContain('#3')

    await authorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/author/Brandon%20Sanderson')

    await narratorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/narrator/Michael%20Kramer')

    await publisherTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/publisher/Tor%20Audio')

    await seriesTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/abs%3Amistborn-series')

    const cosmereTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Cosmere'))
    expect(cosmereTag).toBeTruthy()
    await cosmereTag!.trigger('click')
    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/local%3Acosmere-series')

    await genreTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/genre/Fantasy')
  })

  it('does not render a series name without an identifier as a broken link', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        series: 'Unlinked Series',
        seriesNumber: '4',
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const membership = wrapper.find('.detail-series-membership')
    expect(membership.text()).toContain('Unlinked Series')
    expect(membership.text()).toContain('#4')
    expect(membership.find('button.detail-link-tag').exists()).toBe(false)
  })

  it('links a series subtitle only when it exactly matches a canonical membership', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Mistborn',
        subtitle: 'Mistborn #1',
        seriesMemberships: [
          {
            seriesName: 'Mistborn',
            seriesNumber: '1',
            seriesAsin: 'MISTBORN-SERIES',
            isPrimary: true,
          },
        ],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    await wrapper.find('.subtitle-series-link').trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/MISTBORN-SERIES')
  })

  it('uses the matching secondary series membership identity for a subtitle link', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'The Alloy of Law',
        subtitle: 'Mistborn',
        seriesMemberships: [
          {
            seriesName: 'Wax and Wayne',
            seriesNumber: '1',
            seriesId: 'local:wax-and-wayne',
            isPrimary: true,
          },
          {
            seriesName: 'Mistborn',
            seriesNumber: '4',
            seriesAsin: 'ABS:MISTBORN',
          },
        ],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    await wrapper.find('.subtitle-series-link').trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/ABS%3AMISTBORN')
  })

  it('does not render a series subtitle link for whitespace-only identifiers', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Book',
        subtitle: 'Unlinked Series',
        seriesMemberships: [
          {
            seriesName: 'Unlinked Series',
            seriesId: '   ',
          },
        ],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.find('.subtitle-series-link').exists()).toBe(false)
    expect(routerPushMock).not.toHaveBeenCalled()
  })

  it('does not turn arbitrary subtitle text into a series link', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Mistborn',
        subtitle: 'A Novel',
        seriesMemberships: [
          {
            seriesName: 'Mistborn',
            seriesNumber: '1',
            seriesAsin: 'MISTBORN-SERIES',
            isPrimary: true,
          },
        ],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.find('.subtitle-series-link').exists()).toBe(false)
    expect(wrapper.find('.subtitle').text()).toBe('A Novel')
  })

  it('opens the edit metadata modal from the detail view action', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Author One'],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, {
      global: {
        plugins: [pinia],
        stubs: {
          EditAudiobookModal: {
            name: 'EditAudiobookModal',
            props: ['isOpen'],
            template: '<div class="edit-audiobook-modal-stub" :data-open="String(isOpen)" />',
          },
        },
      },
    })
    await new Promise((r) => setTimeout(r, 10))

    const editButton = wrapper.find('button[aria-label="Edit Metadata"]')
    expect(editButton.exists()).toBe(true)

    await editButton.trigger('click')
    await new Promise((r) => setTimeout(r, 0))

    expect(wrapper.find('.edit-audiobook-modal-stub').attributes('data-open')).toBe('true')
  })

  it('updates the Files tab scan status from the shared scan state', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    expect(filesTab).toBeTruthy()
    await filesTab!.trigger('click')

    scanNotificationsStore.applyUpdate({
      jobId: 'internal-scan-5',
      audiobookId: 5,
      status: 'Processing',
    })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.scan-job-status').exists()).toBe(false)

    scanNotificationsStore.registerManualScan('scan-job-5', 5)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('scan-job-5')
    expect(wrapper.find('.scan-job-status').text()).toContain('Queued')

    scanNotificationsStore.applyUpdate({
      jobId: 'scan-job-5',
      audiobookId: 5,
      status: 'Completed',
      found: 2,
      created: 1,
    })
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('Completed')
    expect(wrapper.find('.scan-job-status').text()).not.toContain('Queued / Processing')

    scanNotificationsStore.clearFinished()
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').exists()).toBe(false)
  })

  it('shows the newest visible manual scan for the audiobook', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    await filesTab!.trigger('click')

    scanNotificationsStore.registerManualScan('older-scan', 5)
    scanNotificationsStore.applyUpdate({
      jobId: 'older-scan',
      audiobookId: 5,
      status: 'Completed',
    })
    await new Promise((resolve) => setTimeout(resolve, 2))
    scanNotificationsStore.registerManualScan('newer-scan', 5)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('newer-scan')
    expect(wrapper.find('.scan-job-status').text()).toContain('Queued')
    expect(wrapper.find('.scan-job-status').text()).not.toContain('older-scan')
  })

  it('registers an accepted Scan Folder job for global notification progress', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: true,
      filesystemStatus: 'Ready',
      filesystemPhase: null,
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    vi.mocked(apiService.scanAudiobook).mockResolvedValue({
      message: 'Scan enqueued',
      found: 0,
      created: 0,
      jobId: 'scan-job-5',
    })

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const scanButton = wrapper.find('button[aria-label="Scan Folder"]')
    expect(scanButton.exists()).toBe(true)
    await scanButton.trigger('click')
    await new Promise((r) => setTimeout(r, 0))

    expect(apiService.scanAudiobook).toHaveBeenCalledWith(5)
    expect(scanNotificationsStore.jobs).toHaveLength(1)
    expect(scanNotificationsStore.jobs[0]).toMatchObject({
      jobId: 'scan-job-5',
      audiobookId: 5,
      status: 'Queued',
      visible: true,
    })
  })

  it('keeps the newest weak-storage missing-file response when refreshes overlap', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    vi.mocked(apiService.getWeakStorageMissingFiles).mockResolvedValueOnce({ items: [] })

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const scanCallback = vi.mocked(signalRService.onScanJobUpdate).mock.calls[0]?.[0] as
      | ((job: { audiobookId: number; status: string }) => void)
      | undefined
    expect(scanCallback).toBeDefined()

    let resolveOlder!: (value: {
      scanToken: string
      items: Array<{ id: string; audiobookFileId: number; path: string }>
    }) => void
    let resolveNewer!: typeof resolveOlder
    const older = new Promise<Parameters<typeof resolveOlder>[0]>((resolve) => {
      resolveOlder = resolve
    })
    const newer = new Promise<Parameters<typeof resolveNewer>[0]>((resolve) => {
      resolveNewer = resolve
    })
    vi.mocked(apiService.getWeakStorageMissingFiles)
      .mockImplementationOnce(() => older)
      .mockImplementationOnce(() => newer)

    scanCallback!({ audiobookId: 5, status: 'Completed' })
    scanCallback!({ audiobookId: 5, status: 'Completed' })

    resolveNewer({
      scanToken: 'new-token',
      items: [{ id: 'new', audiobookFileId: 12, path: 'newer.m4b' }],
    })
    await Promise.resolve()
    resolveOlder({
      scanToken: 'old-token',
      items: [{ id: 'old', audiobookFileId: 11, path: 'older.m4b' }],
    })
    await new Promise((resolve) => setTimeout(resolve, 0))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    await filesTab!.trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.weak-storage-missing-files').text()).toContain('newer.m4b')
    expect(wrapper.find('.weak-storage-missing-files').text()).not.toContain('older.m4b')
  })

  it('disables Scan Folder while library filesystem initialization is incomplete', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: false,
      filesystemStatus: 'Running',
      filesystemPhase: 'AudiobookFileIdentities',
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const scanButton = wrapper.get('button[aria-label="Scan Folder"]')
    expect(scanButton.attributes('disabled')).toBeDefined()
    expect(scanButton.attributes('title')).toContain('filesystem initialization')
    await scanButton.trigger('click')
    expect(apiService.scanAudiobook).not.toHaveBeenCalled()
  })
})
