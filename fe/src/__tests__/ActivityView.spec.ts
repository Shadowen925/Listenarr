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
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

type ActivityItem = {
  id: string
  title?: string
  status?: string
  downloadClientId?: string
  downloadClient?: string
  downloadClientType?: string
  progress?: number
  canRemove?: boolean
}

type ActivityViewVm = {
  allActivityItems: ActivityItem[]
  filteredQueue: ActivityItem[]
  filterText: string
  showRemoveModal: boolean
  clientHasQueueEntry: boolean | null
  queueHealthClients: Array<{ name: string; isUnavailable?: boolean }>
  applyQueueSnapshot: (payload: unknown) => void
  removeFromQueue: (item: ActivityItem) => Promise<void> | void
  removeError: string
  confirmRemove: (mode?: string) => Promise<void>
}

const mockSignalR = () => {
  vi.doMock('@/services/signalr', () => ({
    signalRService: {
      onQueueUpdate: vi.fn(() => () => undefined),
    },
  }))
}

const mockApi = (overrides: Record<string, unknown> = {}) => {
  const apiService = {
    getQueue: vi.fn(async () => []),
    removeFromQueue: vi.fn(async () => undefined),
    cancelDownload: vi.fn(async () => undefined),
    removeQueueItem: vi.fn(async () => ({ success: true, message: 'ok' })),
    ...overrides,
  }

  vi.doMock('@/services/api', () => ({
    apiService,
  }))

  return apiService
}

const mockConfigurationStore = (showCompletedExternalDownloads = false) => {
  vi.doMock('@/stores/configuration', () => ({
    useConfigurationStore: () => ({
      applicationSettings: { showCompletedExternalDownloads },
      loadApplicationSettings: vi.fn(async () => undefined),
    }),
  }))
}

const mockLibraryStore = (audiobooks: Array<{ id: number; title: string }> = []) => {
  vi.doMock('@/stores/library', () => ({
    useLibraryStore: () => ({
      audiobooks,
    }),
  }))
}

let currentMoveJobsStore: Record<string, unknown>

const mockMoveJobsStore = (overrides: Record<string, unknown> = {}) => {
  currentMoveJobsStore = {
    trackedJobs: [],
    start: vi.fn(),
    ...overrides,
  }

  return currentMoveJobsStore
}

const mockDownloadsStore = (overrides: Record<string, unknown> = {}) => {
  const store = {
    activeDownloads: [],
    completedDownloads: [],
    failedDownloads: [],
    loadDownloads: vi.fn(async () => undefined),
    ...overrides,
  }

  vi.doMock('@/stores/downloads', () => ({
    useDownloadsStore: () => store,
  }))

  return store
}

const mountActivityView = async () => {
  const { default: ActivityViewComponent } = await import('@/views/activity/ActivityView.vue')
  const wrapper = mount(ActivityViewComponent, {
    global: {
      stubs: {
        CustomSelect: true,
        RouterLink: { template: '<a><slot /></a>' },
        ManualImportModal: {
          props: ['isOpen', 'initialPath', 'sourceTitle'],
          template:
            '<div data-testid="manual-import-modal" :data-open="isOpen" :data-path="initialPath" :data-title="sourceTitle" />',
        },
      },
    },
  })

  await flushPromises()
  await new Promise((resolve) => setTimeout(resolve, 0))
  return wrapper
}

describe('ActivityView', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.clearAllMocks()
    mockMoveJobsStore()
    vi.doMock('@/stores/moveJobs', () => ({
      useMoveJobsStore: () => currentMoveJobsStore,
    }))
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(
      1 as unknown as ReturnType<typeof setInterval>,
    )
    vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows active library move progress in the unified activity list', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(false)
    mockLibraryStore([{ id: 42, title: 'Book' }])
    mockDownloadsStore()
    mockMoveJobsStore({
      trackedJobs: [
        {
          jobId: 'job-1',
          audiobookId: 42,
          status: 'Running',
          progress: 37.5,
          phase: 'Copying',
          target: '/library/book',
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const move = vm.allActivityItems.find((item) => item.id === 'move:job-1')

    expect(move).toMatchObject({ status: 'moving', progress: 37.5 })
    expect(wrapper.text()).toContain('38%')
    expect(wrapper.text()).toContain('Moving')
  })

  it('includes completed external downloads from the downloads store in the unified list', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      completedDownloads: [
        {
          id: 'd1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'One',
          downloadedSize: 1000,
          totalSize: 1000,
        },
        {
          id: 'd2',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
          title: 'Two',
          downloadedSize: 2000,
          totalSize: 2000,
        },
        {
          id: 'd3',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'transmission',
          startedAt: new Date().toISOString(),
          title: 'Three',
          downloadedSize: 3000,
          totalSize: 3000,
        },
        {
          id: 'd4',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'nzbget',
          startedAt: new Date().toISOString(),
          title: 'Four',
          downloadedSize: 4000,
          totalSize: 4000,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems.map((item) => item.id)).toEqual(
      expect.arrayContaining(['d1', 'd2', 'd3', 'd4']),
    )
    expect(vm.filteredQueue).toHaveLength(4)
    expect(wrapper.find('[data-testid="import-download-d1"]').exists()).toBe(false)
  })

  it('offers manual import for completed external downloads and resolves their Listenarr path', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [
          {
            id: 'sab-1',
            title: 'Project Hail Mary',
            status: 'completed',
            progress: 100,
            size: 100,
            downloaded: 100,
            downloadSpeed: 0,
            quality: '',
            downloadClient: 'SABnzbd',
            downloadClientId: 'sab-1',
            downloadClientType: 'external',
            addedAt: new Date().toISOString(),
            canPause: false,
            canRemove: true,
            localPath: 'C:\\downloads\\Project Hail Mary',
          },
        ],
        clients: [],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: false,
      })),
    })
    mockConfigurationStore(true)
    mockLibraryStore()
    mockDownloadsStore({
      completedDownloads: [
        {
          id: 'sab-1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'sab-1',
          startedAt: new Date().toISOString(),
          title: 'Project Hail Mary',
          downloadedSize: 100,
          totalSize: 100,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const importButton = wrapper.get('[data-testid="import-download-sab-1"]')
    await importButton.trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="manual-import-modal"]').attributes('data-open')).toBe('true')
    expect(wrapper.get('[data-testid="manual-import-modal"]').attributes('data-path')).toBe(
      'C:\\downloads\\Project Hail Mary',
    )
  })

  it('filters the unified activity list by text', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(true)
    mockLibraryStore()
    mockDownloadsStore({
      completedDownloads: [
        {
          id: 'd1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'One',
          downloadedSize: 1000,
          totalSize: 1000,
        },
        {
          id: 'd2',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
          title: 'Two',
          downloadedSize: 2000,
          totalSize: 2000,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    vm.filterText = 'two'
    await flushPromises()

    expect(vm.filteredQueue).toHaveLength(1)
    expect(vm.filteredQueue[0]?.id).toBe('d2')
  })

  it('removes a queue-backed item from the client', async () => {
    const queueItem = {
      id: 'q1',
      title: 'Queue Item',
      status: 'downloading',
      progress: 50,
      size: 1000,
      downloaded: 500,
      downloadClientId: 'qbittorrent',
      downloadClient: 'qbittorrent',
      canRemove: true,
    }

    mockSignalR()
    const apiService = mockApi({
      getQueue: vi.fn(async () => [queueItem]),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'q1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(true)

    await vm.confirmRemove('clientDeleteData')
    expect(apiService.removeQueueItem).toHaveBeenCalledWith('q1', 'clientDeleteData', 'qbittorrent')
  })

  it('keeps the dialog open and shows the reason when client removal fails', async () => {
    mockSignalR()
    const queueItem = {
      id: 'q1',
      title: 'Queue Item',
      status: 'downloading',
      progress: 10,
      size: 100,
      downloaded: 10,
      downloadSpeed: 0,
      eta: null,
      quality: 'MP3',
      canPause: false,
      canRemove: true,
      downloadClient: 'Deluge',
      downloadClientId: 'deluge',
      downloadClientType: 'deluge',
    }
    const apiService = mockApi({ getQueue: vi.fn(async () => [queueItem]) })
    apiService.removeQueueItem = vi.fn(async () => ({
      success: false,
      message: 'Client unavailable',
      errorCode: 'client_unavailable',
    }))
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'q1')

    await vm.removeFromQueue(item!)
    await vm.confirmRemove('clientKeepFiles')

    expect(vm.showRemoveModal).toBe(true)
    expect(vm.removeError).toBe('Client unavailable')
  })
  it('offers Listenarr-only removal when an external item is no longer in the client queue', async () => {
    mockSignalR()
    const apiService = mockApi({
      getQueue: vi.fn(async () => []),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    const downloadsStore = mockDownloadsStore({
      completedDownloads: [
        {
          id: 'ext-1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'Completed External',
          downloadedSize: 100,
          totalSize: 100,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'ext-1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(false)

    await vm.confirmRemove('listenarrOnly')
    expect(apiService.removeQueueItem).toHaveBeenCalledWith('ext-1', 'listenarrOnly', 'SABnzbd')
    expect(downloadsStore.loadDownloads).toHaveBeenCalled()
  })

  it('deduplicates failed queue items against failed download records', async () => {
    const queueFailed = {
      id: 'q1',
      title: 'Queue Failed',
      status: 'failed',
      progress: 0,
      size: 0,
      downloaded: 0,
      downloadClientId: 'qbittorrent',
      downloadClient: 'qbittorrent',
    }

    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => [queueFailed]),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      failedDownloads: [
        {
          id: 'q1',
          status: 'Failed',
          progress: 0,
          downloadClientId: 'qbittorrent',
          title: 'Queue Failed (DB copy)',
        },
        { id: 'd1', status: 'Failed', progress: 0, downloadClientId: 'DDL', title: 'DDL Failed' },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems).toHaveLength(2)
    expect(vm.allActivityItems.filter((item) => item.id === 'q1')).toHaveLength(1)
    expect(vm.allActivityItems.some((item) => item.id === 'd1')).toBe(true)
  })

  it('removes a failed DDL download through Listenarr cancellation', async () => {
    mockSignalR()
    const apiService = mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    const downloadsStore = mockDownloadsStore({
      failedDownloads: [
        { id: 'd1', status: 'Failed', progress: 0, downloadClientId: 'DDL', title: 'DDL Failed' },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'd1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.clientHasQueueEntry).toBe(true)

    await vm.confirmRemove()
    expect(apiService.cancelDownload).toHaveBeenCalledWith('d1')
    expect(downloadsStore.loadDownloads).toHaveBeenCalled()
  })

  it('maps ImportPending and ImportBlocked downloads to activity rows', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      activeDownloads: [
        {
          id: 'd-importpending',
          title: 'Import Pending',
          status: 'ImportPending',
          progress: 99,
          totalSize: 1000,
          downloadedSize: 990,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
        },
      ],
      failedDownloads: [
        {
          id: 'd-importblocked',
          title: 'Import Blocked',
          status: 'ImportBlocked',
          progress: 100,
          totalSize: 1000,
          downloadedSize: 1000,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems.find((item) => item.id === 'd-importpending')?.status).toBe(
      'importpending',
    )
    expect(vm.allActivityItems.find((item) => item.id === 'd-importblocked')?.status).toBe(
      'importblocked',
    )
  })

  it('shows unavailable client health even when no queue items are returned', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [],
        clients: [
          {
            clientId: 'qb-1',
            clientName: 'qBittorrent',
            clientType: 'qbittorrent',
            snapshotState: 'unavailable',
            isStaleSnapshot: false,
            isUnavailable: true,
            snapshotFailureReason: 'timeout',
            itemCount: 0,
          },
        ],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: true,
      })),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.queueHealthClients).toHaveLength(1)
    expect(vm.queueHealthClients[0]?.name).toBe('qBittorrent')
    expect(wrapper.text()).toContain('Some queue data is unavailable')
    expect(wrapper.text()).toContain('qBittorrent unavailable after a timeout')
  })

  it('keeps client health in a fixed-position expandable status control across recovery', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [
          {
            id: 'q1',
            title: 'Test audiobook',
            status: 'downloading',
            progress: 40,
            size: 1000,
            downloaded: 400,
            downloadClient: 'Deluge',
            downloadClientId: 'deluge-1',
            isStaleSnapshot: true,
            snapshotAgeSeconds: 5,
            snapshotFailureReason: 'client-error',
          },
        ],
        clients: [
          {
            clientId: 'deluge-1',
            clientName: 'Deluge',
            clientType: 'deluge',
            snapshotState: 'cached',
            isStaleSnapshot: true,
            isUnavailable: false,
            snapshotAgeSeconds: 5,
            snapshotFailureReason: 'client-error',
            itemCount: 1,
          },
        ],
        generatedAt: new Date().toISOString(),
        hasStaleData: true,
        hasUnavailableClients: false,
      })),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const status = wrapper.get('.queue-health-details')
    const queue = wrapper.get('.queue-grid-container')

    expect(status.element.tagName).toBe('DETAILS')
    expect(wrapper.find('.queue-health-banner').exists()).toBe(false)
    expect(wrapper.text()).toContain('Deluge using cached data 5s old after a client error')

    vm.applyQueueSnapshot({
      items: [
        {
          id: 'q1',
          title: 'Test audiobook',
          status: 'downloading',
          progress: 40,
          size: 1000,
          downloaded: 400,
          downloadClient: 'Deluge',
          downloadClientId: 'deluge-1',
        },
      ],
      clients: [
        {
          clientId: 'deluge-1',
          clientName: 'Deluge',
          clientType: 'deluge',
          snapshotState: 'live',
          isStaleSnapshot: false,
          isUnavailable: false,
          itemCount: 1,
        },
      ],
      generatedAt: new Date().toISOString(),
      hasStaleData: false,
      hasUnavailableClients: false,
    })
    await flushPromises()

    expect(wrapper.get('.queue-health-details').element).toBe(status.element)
    expect(wrapper.get('.queue-grid-container').element).toBe(queue.element)
    expect(wrapper.text()).toContain('All download clients healthy')
    expect(wrapper.find('.queue-health-banner').exists()).toBe(false)
  })

  it('prefers the queue snapshot over a DDL active download with the same tracked id', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [
          {
            id: 'ddl-alice',
            title: 'Alice in Wonderland',
            status: 'downloading',
            progress: 42,
            size: 1000,
            downloaded: 420,
            downloadSpeed: 0,
            quality: 'M4B',
            downloadClient: 'Direct Download',
            downloadClientId: 'DDL',
            downloadClientType: 'ddl',
            addedAt: new Date().toISOString(),
            canPause: false,
            canRemove: true,
          },
        ],
        clients: [],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: false,
      })),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      activeDownloads: [
        {
          id: 'ddl-alice',
          title: 'Alice in Wonderland',
          status: 'Queued',
          progress: 0,
          totalSize: 1000,
          downloadedSize: 0,
          downloadClientId: 'DDL',
          startedAt: new Date().toISOString(),
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems).toHaveLength(1)
    expect(vm.allActivityItems[0]?.id).toBe('ddl-alice')
    expect(vm.allActivityItems[0]?.status).toBe('downloading')
    expect(vm.allActivityItems[0]?.progress).toBe(42)
    expect(vm.allActivityItems[0]?.downloadClientType).toBe('ddl')
  })

  it('prefers the queue snapshot over an external active download with the same tracked id', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [
          {
            id: 'tracked-artemis',
            title: 'Artemis',
            status: 'completed',
            progress: 100,
            size: 489100000,
            downloaded: 489100000,
            downloadSpeed: 77300,
            quality: 'Unknown',
            downloadClient: 'QBIT',
            downloadClientId: 'qb-1',
            downloadClientType: 'qbittorrent',
            addedAt: new Date().toISOString(),
            canPause: false,
            canRemove: true,
          },
        ],
        clients: [],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: false,
      })),
    })
    mockConfigurationStore(true)
    mockLibraryStore()
    mockDownloadsStore({
      activeDownloads: [
        {
          id: 'tracked-artemis',
          title: 'Artemis',
          status: 'Downloading',
          progress: 100,
          totalSize: 489100000,
          downloadedSize: 489100000,
          downloadClientId: 'qb-1',
          startedAt: new Date().toISOString(),
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems).toHaveLength(1)
    expect(vm.allActivityItems[0]?.id).toBe('tracked-artemis')
    expect(vm.allActivityItems[0]?.status).toBe('completed')
    expect(vm.allActivityItems[0]?.title).toBe('Artemis')
  })
})
