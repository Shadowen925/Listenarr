<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <div class="activity-view">
    <div class="page-header">
      <h1>
        <PhActivity />
        Activity
      </h1>
      <div class="activity-actions">
        <div class="filter-input-wrapper">
          <PhMagnifyingGlass class="filter-icon" />
          <input
            v-model="filterText"
            type="text"
            class="filter-input"
            placeholder="Filter activity..."
          />
          <button v-if="filterText" class="filter-clear" @click="filterText = ''">
            <PhX />
          </button>
        </div>
        <button class="btn btn-secondary" @click="refreshQueue" :disabled="loading">
          <component :is="loading ? PhSpinner : PhArrowClockwise" />
          Refresh
        </button>
      </div>
    </div>

    <details class="queue-health-details" :class="{ 'has-warning': queueHealthClients.length > 0 }">
      <summary :aria-label="`Download client health: ${queueHealthSummary}`">
        <PhWarningCircle v-if="queueHealthClients.length > 0" aria-hidden="true" />
        <PhCheckCircle v-else aria-hidden="true" />
        <span>{{ queueHealthSummary }}</span>
      </summary>
      <div class="queue-health-panel">
        <strong>{{ queueHealthTitle }}</strong>
        <span v-if="queueHealthClients.length > 0">{{ queueHealthMessage }}</span>
        <span v-else-if="queueClientStatuses.length > 0">
          All configured download clients are reporting live queue data.
        </span>
        <span v-else>No download-client status was included in the latest queue snapshot.</span>
      </div>
    </details>

    <div class="activity-summary" aria-live="polite">
      <span>{{ activeActivityCount }} active</span>
      <span>{{ attentionActivityCount }} need attention</span>
    </div>

    <div v-if="filteredQueue.length > 0" class="activity-list">
      <article v-for="item in filteredQueue" :key="item.id" class="activity-row">
        <div class="activity-row-main">
          <div class="activity-copy">
            <RouterLink
              v-if="item.audiobookId"
              :to="`/audiobooks/${item.audiobookId}`"
              class="activity-title"
              :title="getDisplayTitle(item)"
            >
              {{ getDisplayTitle(item) }}
            </RouterLink>
            <span v-else class="activity-title" :title="getDisplayTitle(item)">
              {{ getDisplayTitle(item) }}
            </span>
            <div class="activity-meta">
              <span>{{ item.downloadClient || 'Download client' }}</span>
              <span v-if="item.size > 0">
                {{ formatBytes(item.downloaded ?? 0) }} / {{ formatBytes(item.size) }}
              </span>
              <span v-if="item.downloadSpeed > 0" class="speed-text">
                {{ formatBytes(item.downloadSpeed) }}/s
              </span>
              <span v-if="item.eta" class="eta-text">ETA {{ formatEta(item.eta) }}</span>
            </div>
          </div>

          <div class="activity-controls">
            <span
              :class="['status-badge', item.status, statusClass(item)]"
              :title="item.recoveryReason || undefined"
              :aria-label="
                item.recoveryReason
                  ? `${displayStatus(item)}: ${item.recoveryReason}`
                  : displayStatus(item)
              "
            >
              {{ displayStatus(item) }}
            </span>
            <span
              v-if="item.isStaleSnapshot"
              class="stale-badge"
              :title="getSnapshotStatusTitle(item)"
            >
              Cached
            </span>
            <button
              v-if="canRetryRecoveredImport(item)"
              class="btn btn-secondary activity-import-button"
              :data-testid="`retry-import-${item.id}`"
              @click="retryRecoveredImport(item)"
            >
              Retry Import
            </button>
            <button
              v-else-if="canImportCompletedExternal(item) || canManuallyMatchRecovery(item)"
              class="btn btn-secondary activity-import-button"
              :data-testid="`import-download-${item.id}`"
              @click="openManualImport(item)"
            >
              {{ canManuallyMatchRecovery(item) ? 'Match / Import' : 'Import' }}
            </button>
            <details class="activity-details">
              <summary>Details</summary>
              <dl>
                <div>
                  <dt>Download ID</dt>
                  <dd>{{ item.id }}</dd>
                </div>
                <div v-if="item.quality && item.quality !== '*'">
                  <dt>Quality</dt>
                  <dd>{{ item.quality }}</dd>
                </div>
                <div v-if="item.language">
                  <dt>Language</dt>
                  <dd>{{ item.language }}</dd>
                </div>
                <div v-if="item.remotePath">
                  <dt>Remote path</dt>
                  <dd>{{ item.remotePath }}</dd>
                </div>
                <div v-if="item.localPath">
                  <dt>Local path</dt>
                  <dd>{{ item.localPath }}</dd>
                </div>
                <div v-if="item.contentPath">
                  <dt>Content path</dt>
                  <dd>{{ item.contentPath }}</dd>
                </div>
                <div v-if="item.seeders != null">
                  <dt>Seeders</dt>
                  <dd>{{ item.seeders }}</dd>
                </div>
                <div v-if="item.ratio != null">
                  <dt>Ratio</dt>
                  <dd>{{ item.ratio.toFixed(2) }}</dd>
                </div>
                <div v-if="item.recoveryReason">
                  <dt>Recovery</dt>
                  <dd>{{ item.recoveryReason }}</dd>
                </div>
                <div v-if="item.sourcePathUnavailableReason">
                  <dt>Source</dt>
                  <dd>{{ item.sourcePathUnavailableReason }}</dd>
                </div>
                <div v-if="item.errorMessage">
                  <dt>Error</dt>
                  <dd>{{ item.errorMessage }}</dd>
                </div>
                <div v-if="detailsLine(item)">
                  <dt>Diagnostics</dt>
                  <dd>{{ detailsLine(item) }}</dd>
                </div>
              </dl>
              <div class="activity-secondary-actions">
                <button
                  v-if="item.canRemove"
                  class="btn-icon btn-danger-icon"
                  @click="removeFromQueue(item)"
                  title="Remove from Queue"
                >
                  <PhX />
                  <span>Remove</span>
                </button>
                <button
                  class="btn-icon"
                  data-testid="download-event-history"
                  @click="openDownloadEvents(item)"
                  title="View download event history"
                  aria-label="View download event history"
                >
                  <PhClock />
                  <span>History</span>
                </button>
              </div>
            </details>
          </div>
        </div>
        <div class="activity-progress-row">
          <ProgressBar
            v-if="activityProgress(item) !== null"
            :value="activityProgress(item) ?? 0"
            :downloaded="item.downloaded"
            :total="item.size"
            variant="activity"
            height="small"
            :animating="item.status === 'downloading' || item.status === 'moving'"
          />
          <span v-else class="activity-progress-state">{{ displayStatus(item) }}</span>
          <span v-if="activityProgress(item) !== null" class="progress-label">
            {{ Math.round(activityProgress(item) ?? 0) }}%
          </span>
          <span
            v-if="item.sourcePathUnavailable || item.missingFromClient"
            class="stale-badge source-unavailable-badge"
          >
            {{ item.sourcePathUnavailable ? 'Source unavailable' : 'Missing from client' }}
          </span>
        </div>
      </article>
    </div>

    <!-- Empty State -->
    <EmptyState
      v-else-if="filteredQueue.length === 0 && !loading"
      :title="filterText ? 'No Matching Downloads' : 'No Active Downloads'"
      :message="
        filterText
          ? 'No downloads match your filter.'
          : 'Downloads will appear here when you send items to your download clients.'
      "
    >
      <template #icon>
        <PhQueue :size="48" />
      </template>
    </EmptyState>

    <!-- Loading State -->
    <LoadingState v-if="loading && queue.length === 0" message="Loading queue..." />

    <!-- Remove from Queue dialog -->
    <DownloadActionDialog
      variant="remove"
      :open="showRemoveModal"
      :audiobook-title="itemToRemove ? getDisplayTitle(itemToRemove) : ''"
      :current-title="itemToRemove?.title"
      :client-name="itemToRemove?.downloadClient"
      :progress="itemToRemove?.progress ?? 0"
      :data-size="itemToRemove?.size ? formatBytes(itemToRemove.downloaded ?? 0) : ''"
      :missing-remote="clientHasQueueEntry === false"
      :busy="removing"
      :error="removeError"
      @confirm="confirmRemove"
      @cancel="closeRemoveDialog"
    />
    <ManualImportModal
      v-if="manualImportOpen"
      :is-open="manualImportOpen"
      :initial-path="manualImportPath"
      :source-title="manualImportTitle"
      :initial-audiobook-id="manualImportAudiobookId"
      @close="manualImportOpen = false"
      @imported="handleManualImportCompleted"
    />
    <div
      v-if="eventHistoryOpen"
      class="event-history-backdrop"
      role="presentation"
      @click.self="eventHistoryOpen = false"
    >
      <section
        class="event-history-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="event-history-title"
      >
        <header>
          <h2 id="event-history-title">Download Event History</h2>
          <button
            class="btn-icon"
            aria-label="Close event history"
            @click="eventHistoryOpen = false"
          >
            <PhX />
          </button>
        </header>
        <p v-if="eventHistoryError" class="event-history-error" role="alert">
          {{ eventHistoryError }}
        </p>
        <p v-else-if="eventHistoryLoading">Loading download history...</p>
        <ol v-else-if="downloadEvents.length" class="event-history-list">
          <li v-for="event in downloadEvents" :key="event.id" :class="event.outcome.toLowerCase()">
            <time :datetime="event.timestamp">{{
              new Date(event.timestamp).toLocaleString()
            }}</time>
            <strong>{{ event.eventType }}</strong>
            <span>{{ event.message || event.outcome }}</span>
          </li>
        </ol>
        <p v-else>No download events have been recorded for this item.</p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, unref } from 'vue'
import {
  PhActivity,
  PhSpinner,
  PhArrowClockwise,
  PhX,
  PhQueue,
  PhCheckCircle,
  PhWarningCircle,
  PhMagnifyingGlass,
  PhClock,
} from '@phosphor-icons/vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useDownloadsStore } from '@/stores/downloads'
import { useLibraryStore } from '@/stores/library'
import { useMoveJobsStore, type TrackedMoveJob } from '@/stores/moveJobs'
import { EmptyState, LoadingState, ProgressBar } from '@/components/base'
import { useConfigurationStore } from '@/stores/configuration'
import type {
  QueueClientStatus,
  QueueItem,
  QueueUpdatePayload,
  Download,
  DownloadRemovalMode,
  DownloadEvent,
} from '@/types'
import DownloadActionDialog from '@/components/domain/download/DownloadActionDialog.vue'
import ManualImportModal from '@/components/feedback/ManualImportModal.vue'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

const downloadsStore = useDownloadsStore()
const libraryStore = useLibraryStore()
const moveJobsStore = useMoveJobsStore()
const configStore = useConfigurationStore()

const filterText = ref('')
const queue = ref<QueueItem[]>([])
const queueClientStatuses = ref<QueueClientStatus[]>([])
const loading = ref(false)
const showRemoveModal = ref(false)
const clientHasQueueEntry = ref<boolean | null>(null)
const itemToRemove = ref<QueueItem | null>(null)
const removing = ref(false)
const removeError = ref('')
const eventHistoryOpen = ref(false)
const eventHistoryLoading = ref(false)
const eventHistoryError = ref('')
const downloadEvents = ref<DownloadEvent[]>([])
const manualImportOpen = ref(false)
const manualImportPath = ref('')
const manualImportTitle = ref('')
const manualImportAudiobookId = ref<number | undefined>()
let unsubscribeQueue: (() => void) | null = null
let queueRefreshInterval: ReturnType<typeof setInterval> | null = null

const applyQueueSnapshot = (payload: QueueUpdatePayload | null | undefined) => {
  const snapshot = normalizeQueueSnapshot(payload)
  queue.value = snapshot.items
  queueClientStatuses.value = snapshot.clients
}

const formatSnapshotAge = (seconds?: number): string => {
  if (!seconds || seconds <= 0) return 'just now'
  if (seconds < 60) return `${seconds}s old`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m old`
  return `${Math.floor(seconds / 3600)}h old`
}

const formatSnapshotReason = (reason?: string): string => {
  if (!reason) return 'client issue'
  if (reason === 'timeout') return 'a timeout'
  if (reason === 'canceled') return 'a canceled request'
  return 'a client error'
}

const getSnapshotStatusTitle = (item: QueueItem): string => {
  if (!item.isStaleSnapshot) return ''

  const parts = ['Showing cached queue data']
  if (item.downloadClient) parts.push(`for ${item.downloadClient}`)
  if (item.snapshotAgeSeconds != null)
    parts.push(`captured ${formatSnapshotAge(item.snapshotAgeSeconds)}`)
  if (item.snapshotFailureReason)
    parts.push(`after ${formatSnapshotReason(item.snapshotFailureReason)}`)
  return parts.join(' ')
}

const queueHealthClients = computed(() => {
  if (queueClientStatuses.value.length > 0) {
    return queueClientStatuses.value
      .filter((client) => client.isStaleSnapshot || client.isUnavailable)
      .map((client) => ({
        id: client.clientId || client.clientName,
        name: client.clientName || 'Download client',
        ageSeconds: client.snapshotAgeSeconds,
        failureReason: client.snapshotFailureReason,
        snapshotState: client.snapshotState,
        isUnavailable: client.isUnavailable,
      }))
  }

  const map = new Map<
    string,
    {
      id: string
      name: string
      ageSeconds?: number
      failureReason?: string
      snapshotState?: string
      isUnavailable?: boolean
    }
  >()

  queue.value.forEach((item) => {
    if (!item.isStaleSnapshot) return

    const key = item.downloadClientId || item.downloadClient || item.id
    if (!map.has(key)) {
      map.set(key, {
        id: key,
        name: item.downloadClient || 'Download client',
        ageSeconds: item.snapshotAgeSeconds,
        failureReason: item.snapshotFailureReason,
        snapshotState: item.snapshotState,
        isUnavailable: false,
      })
    }
  })

  return Array.from(map.values())
})

const queueHealthTitle = computed(() => {
  if (queueHealthClients.value.some((client) => client.isUnavailable)) {
    return 'Some queue data is unavailable'
  }
  if (queueHealthClients.value.length > 0) {
    return 'Using cached queue data'
  }
  return queueClientStatuses.value.length > 0
    ? 'All download clients healthy'
    : 'No download client status is available'
})

const queueHealthSummary = computed(() => {
  const count = queueHealthClients.value.length
  if (count === 0) {
    return queueClientStatuses.value.length > 0 ? 'Clients healthy' : 'Client status unavailable'
  }
  return `${count} client${count === 1 ? '' : 's'} need attention`
})

const queueHealthMessage = computed(() => {
  return queueHealthClients.value
    .map((client) => {
      const parts = [client.name]
      if (client.isUnavailable) {
        parts.push('unavailable')
      } else if (client.snapshotState === 'cached') {
        parts.push('using cached data')
      }
      if (client.ageSeconds != null) parts.push(formatSnapshotAge(client.ageSeconds))
      if (client.failureReason) parts.push(`after ${formatSnapshotReason(client.failureReason)}`)
      return parts.join(' ')
    })
    .join(', ')
})

// Convert Download to QueueItem format for unified display
const convertDownloadToQueueItem = (download: Download): QueueItem => {
  const statusMap: Record<string, string> = {
    Queued: 'queued',
    Downloading: 'downloading',
    Paused: 'paused',
    Processing: 'processing',
    Completed: 'completed',
    Ready: 'completed',
    ImportPending: 'importpending',
    ImportBlocked: 'importblocked',
    Moved: 'imported',
    Failed: 'failed',
  }
  const status = statusMap[download.status] ?? 'downloading'

  const clientName = (download as unknown as Record<string, unknown>)['downloadClientName'] as
    | string
    | undefined
  const recoveryState = download.metadata?.['Recovery.State']
  const recoveryReason = download.metadata?.['Recovery.Reason']
  return {
    id: download.id,
    title: download.title,
    audiobookId: download.audiobookId,
    status: status,
    progress: download.progress,
    size: download.totalSize,
    downloaded: download.downloadedSize,
    downloadSpeed: 0,
    eta: undefined,
    quality: '',
    downloadClient: clientName ?? download.downloadClientId ?? 'Unknown Client',
    downloadClientId: download.downloadClientId,
    downloadClientType:
      (download.downloadClientId || '').toString().toUpperCase() === 'DDL' ? 'DDL' : 'external',
    addedAt: download.startedAt,
    localPath: download.downloadPath,
    missingFromClient: typeof recoveryState === 'string',
    recoveryState: typeof recoveryState === 'string' ? recoveryState : undefined,
    recoveryReason: typeof recoveryReason === 'string' ? recoveryReason : undefined,
    canPause: false,
    canRemove: true,
  }
}

const convertMoveJobToQueueItem = (job: TrackedMoveJob): QueueItem => ({
  id: `move:${job.jobId}`,
  title: 'Library move',
  audiobookId: job.audiobookId,
  status: job.status === 'Queued' || job.status === 'RetryScheduled' ? 'queued' : 'moving',
  progress: job.progress,
  size: 0,
  downloaded: 0,
  downloadSpeed: 0,
  eta: undefined,
  quality: '',
  downloadClient: job.phase ? `Library move · ${job.phase}` : 'Library move',
  downloadClientId: 'LISTENARR_MOVE',
  downloadClientType: 'move',
  addedAt: '',
  errorMessage: job.error,
  canPause: false,
  canRemove: false,
})

// Read user preference from configuration store
const showCompletedExternalDownloads = computed(
  () => configStore.applicationSettings?.showCompletedExternalDownloads ?? false,
)

// All activity items — unified list of queue + downloads
const isDirectDownload = (downloadClientId?: string): boolean =>
  (downloadClientId || '').toString().toUpperCase() === 'DDL'

const allActivityItems = computed(() => {
  const queueItems = queue.value.filter(
    (item) => !['moved', 'imported'].includes(item.status.toLowerCase()),
  )
  const trackedQueueIds = new Set(queueItems.map((item) => item.id))

  const activeDownloadsList = unref(downloadsStore.activeDownloads || [])
  const failedDownloadsList = unref(downloadsStore.failedDownloads || [])

  const ddlDownloadItems = activeDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const failedDDLItems = failedDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const externalActiveDownloads = activeDownloadsList
    .filter((d) => d.downloadClientId && !isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  let combined = [...queueItems, ...ddlDownloadItems, ...failedDDLItems, ...externalActiveDownloads]

  if (showCompletedExternalDownloads.value) {
    const completedExternal = (downloadsStore.completedDownloads || [])
      .filter(
        (d) => d.downloadClientId && (d.downloadClientId || '').toString().toUpperCase() !== 'DDL',
      )
      .filter((d) => d.status !== 'Moved')
      .map(convertDownloadToQueueItem)

    const map = new Map<string, QueueItem>()
    for (const it of combined) map.set(it.id, it)
    for (const it of completedExternal) {
      if (!map.has(it.id)) map.set(it.id, it)
    }

    combined = Array.from(map.values())
  } else {
    combined = combined.filter((it) => {
      if (
        (it.downloadClientType || '').toString().toLowerCase() !== 'ddl' &&
        it.status === 'completed'
      )
        return false
      return true
    })
  }

  const failedFromDownloads = (downloadsStore.failedDownloads || []).map(convertDownloadToQueueItem)

  const finalMap = new Map<string, QueueItem>()
  for (const job of moveJobsStore.trackedJobs) {
    if (
      job.status !== 'Completed' &&
      job.status !== 'Failed' &&
      job.status !== 'NeedsAttention' &&
      job.status !== 'Superseded'
    ) {
      const item = convertMoveJobToQueueItem(job)
      finalMap.set(item.id, item)
    }
  }
  for (const it of combined) finalMap.set(it.id, it)
  for (const it of failedFromDownloads) if (!finalMap.has(it.id)) finalMap.set(it.id, it)

  return Array.from(finalMap.values())
})

// Build a lookup map from audiobook ID -> title for resolving friendly names
const audiobookTitleMap = computed(() => {
  const map = new Map<number, string>()
  for (const ab of libraryStore.audiobooks) {
    if (ab.id && ab.title) map.set(ab.id, ab.title)
  }
  return map
})

const getDisplayTitle = (item: QueueItem): string => {
  if (item.audiobookId) {
    const abTitle = audiobookTitleMap.value.get(item.audiobookId)
    if (abTitle) return abTitle
  }
  return item.title
}

// Filter by text search across title, client, status
const filteredQueue = computed(() => {
  const text = filterText.value.trim().toLowerCase()
  if (!text) return allActivityItems.value

  return allActivityItems.value.filter((item) => {
    const displayTitle = getDisplayTitle(item)
    return (
      (displayTitle && displayTitle.toLowerCase().includes(text)) ||
      (item.title && item.title.toLowerCase().includes(text)) ||
      (item.downloadClient && item.downloadClient.toLowerCase().includes(text)) ||
      (item.status && item.status.toLowerCase().includes(text)) ||
      (item.quality && item.quality.toLowerCase().includes(text))
    )
  })
})

const activeActivityCount = computed(
  () =>
    allActivityItems.value.filter((item) =>
      ['queued', 'downloading', 'paused', 'processing', 'moving', 'importpending'].includes(
        item.status.toLowerCase(),
      ),
    ).length,
)

const attentionActivityCount = computed(
  () =>
    allActivityItems.value.filter(
      (item) =>
        ['failed', 'importblocked'].includes(item.status.toLowerCase()) ||
        ['ImportFailed', 'NeedsAttention', 'RecoveryPending'].includes(item.recoveryState ?? '') ||
        item.stallState === 'Stalled',
    ).length,
)

const activityProgress = (item: QueueItem): number | null => {
  const status = item.status.toLowerCase()
  if (
    item.downloadClientType === 'move' ||
    ['queued', 'downloading', 'paused', 'completed'].includes(status)
  ) {
    return Math.min(100, Math.max(0, item.progress ?? 0))
  }
  return null
}

const refreshQueue = async () => {
  loading.value = true
  try {
    const [queueSnapshot] = await Promise.all([
      apiService.getQueue(),
      downloadsStore.loadDownloads(),
    ])
    applyQueueSnapshot(queueSnapshot)
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'refreshQueue',
    })
  } finally {
    loading.value = false
  }
}

const isDdlItem = (item: QueueItem): boolean =>
  (item.downloadClientId || '').toString().toUpperCase() === 'DDL' ||
  (item.downloadClientType || '').toString().toUpperCase() === 'DDL'

const canImportCompletedExternal = (item: QueueItem): boolean =>
  showCompletedExternalDownloads.value &&
  item.status === 'completed' &&
  !isDdlItem(item) &&
  !item.sourcePathUnavailable &&
  (item.downloadClientType || '').toLowerCase() !== 'move' &&
  Boolean(item.downloadClientId)

const canManuallyMatchRecovery = (item: QueueItem): boolean =>
  (item.recoveryState === 'ImportPending' ||
    item.recoveryState === 'Recovered' ||
    item.recoveryState === 'NeedsAttention') &&
  Boolean(item.localPath)

const canRetryRecoveredImport = (item: QueueItem): boolean =>
  (item.status === 'importpending' && item.recoveryState === 'ImportPending') ||
  (item.status === 'importblocked' && item.recoveryState === 'ImportFailed')

const retryRecoveredImport = async (item: QueueItem) => {
  try {
    await apiService.retryDownloadImport(item.id)
    useToast().success('Import queued', `Retrying import for ${getDisplayTitle(item)}.`)
    await refreshQueue()
  } catch (error) {
    const message = (error as Error).message || 'Could not retry this import.'
    useToast().error('Import retry failed', message)
  }
}

const openManualImport = async (item: QueueItem) => {
  try {
    const snapshot = normalizeQueueSnapshot(await apiService.getQueue())
    const resolvedItem = snapshot.items.find((queueItem) => queueItem.id === item.id)
    const path = resolvedItem?.localPath || item.localPath
    if (!path) {
      useToast().error(
        'Import path unavailable',
        'Listenarr could not resolve this completed download path. Check the client path mapping and refresh Activity.',
      )
      return
    }

    manualImportPath.value = path
    manualImportTitle.value = item.title
    manualImportAudiobookId.value = item.audiobookId
    manualImportOpen.value = true
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'resolveManualImportPath',
    })
    useToast().error('Import unavailable', 'Could not resolve the completed download path.')
  }
}

const handleManualImportCompleted = async (result: { imported?: number }) => {
  const imported = result.imported ?? 0
  if (imported > 0) {
    useToast().success(
      'Import complete',
      `${imported} audio file${imported === 1 ? '' : 's'} imported.`,
    )
    await refreshQueue()
  } else {
    useToast().error(
      'Import incomplete',
      'No files were imported. The source download was retained.',
    )
  }
}

const removeFromQueue = async (item: QueueItem) => {
  itemToRemove.value = item
  removeError.value = ''
  clientHasQueueEntry.value = isDdlItem(item)
    ? true
    : !item.missingFromClient && queue.value.some((q) => q.id === item.id)
  showRemoveModal.value = true
}

const openDownloadEvents = async (item: QueueItem) => {
  eventHistoryOpen.value = true
  eventHistoryLoading.value = true
  eventHistoryError.value = ''
  downloadEvents.value = []
  try {
    downloadEvents.value = await apiService.getDownloadEvents(item.id, 100)
  } catch (err) {
    eventHistoryError.value = (err as Error).message || 'Could not load download event history.'
    useToast().error('History unavailable', eventHistoryError.value)
  } finally {
    eventHistoryLoading.value = false
  }
}

const closeRemoveDialog = () => {
  showRemoveModal.value = false
  itemToRemove.value = null
  clientHasQueueEntry.value = null
  removeError.value = ''
}

const confirmRemove = async (mode: DownloadRemovalMode) => {
  const item = itemToRemove.value
  if (!item) return

  removing.value = true
  removeError.value = ''
  try {
    if (isDdlItem(item)) {
      await apiService.cancelDownload(item.id)
    } else {
      const result = await apiService.removeQueueItem(item.id, mode, item.downloadClientId)
      if (!result.success) {
        removeError.value = result.message || 'The download could not be removed.'
        if (result.errorCode === 'missing_remote') clientHasQueueEntry.value = false
        useToast().error('Remove failed', removeError.value)
        return
      }
    }
    await downloadsStore.loadDownloads()
    await refreshQueue()
    closeRemoveDialog()
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'removeFromQueue',
    })
    removeError.value = (err as Error).message
    useToast().error('Remove failed', removeError.value)
  } finally {
    removing.value = false
  }
}

const statusClass = (item: QueueItem): string => {
  const label = displayStatus(item)
  return label === 'Stalled' ||
    label === 'Missing from client' ||
    label === 'Needs attention' ||
    label === 'Import Failed'
    ? 'stalled'
    : label === 'Replacing' || label === 'Retrieving metadata' || label === 'Recovery Pending'
      ? 'queued'
      : ''
}

const detailsLine = (item: QueueItem): string => {
  const parts: string[] = []
  if (item.size) parts.push(`${formatBytes(item.downloaded ?? 0)} / ${formatBytes(item.size)}`)
  if (item.downloadSpeed > 0) parts.push(`${formatBytes(item.downloadSpeed)}/s`)
  if (item.seeders !== undefined && item.seeders !== null) parts.push(`${item.seeders} seeds`)
  if (item.lastProgressAt) parts.push(`last progress ${formatDuration(item.lastProgressAt)} ago`)
  if (item.stallSince) parts.push(`stalled ${formatDuration(item.stallSince)}`)
  if (item.replacementAttempts) parts.push(`replacements ${item.replacementAttempts}`)
  const err = item.replacementError || item.stallReason || item.errorMessage
  if (err) parts.push(err)
  if (item.sourcePathUnavailableReason) parts.push(item.sourcePathUnavailableReason)
  return parts.join(' · ')
}

const formatBytes = (bytes: number): string => {
  if (!bytes || bytes < 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  return `${(bytes / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

const formatDuration = (from?: string | null): string => {
  if (!from) return '-'
  const ms = Date.now() - new Date(from).getTime()
  if (!Number.isFinite(ms) || ms < 0) return '-'
  const mins = Math.floor(ms / 60000)
  if (mins < 1) return '<1m'
  if (mins < 60) return `${mins}m`
  const h = Math.floor(mins / 60)
  return h < 24 ? `${h}h ${mins % 60}m` : `${Math.floor(h / 24)}d ${h % 24}h`
}

const displayStatus = (item: QueueItem): string => {
  if (item.recoveryState === 'ImportFailed') return 'Import Failed'
  if (item.recoveryState === 'RecoveryPending') return 'Recovery Pending'
  if (item.recoveryState === 'ImportPending') return 'Import Pending'
  if (item.recoveryState === 'MissingFromClient') return 'Missing from client'
  if (item.recoveryState === 'NeedsAttention') return 'Needs attention'
  if (item.replacementState === 'Removing' || item.replacementState === 'Removed')
    return 'Replacing'
  if (item.stallState === 'MissingFromClient') return 'Missing from client'
  if (item.stallState === 'Stalled') return 'Stalled'
  if (item.stallState === 'MetadataPending') return 'Retrieving metadata'
  return formatStatus(item.status)
}
const formatStatus = (status: string): string => {
  const labels: Record<string, string> = {
    downloading: 'Downloading',
    queued: 'Queued',
    paused: 'Paused',
    completed: 'Completed',
    failed: 'Failed',
    processing: 'Processing',
    moving: 'Moving',
    importpending: 'Import Pending',
    importblocked: 'Import Blocked',
    imported: 'Imported',
  }
  return labels[status] ?? status.charAt(0).toUpperCase() + status.slice(1)
}

const formatEta = (seconds: number): string => {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`
  return `${Math.floor(seconds / 86400)}d`
}

// Subscribe to SignalR for real-time updates
onMounted(async () => {
  moveJobsStore.start()

  await downloadsStore.loadDownloads()
  await configStore.loadApplicationSettings()

  unsubscribeQueue = signalRService.onQueueUpdate((updatedQueue) => {
    applyQueueSnapshot(updatedQueue)
  })
  await refreshQueue()
  await refreshQueue()

  queueRefreshInterval = setInterval(async () => {
    try {
      await refreshQueue()
    } catch (err) {
      errorTracking.captureException(err as Error, {
        component: 'ActivityView',
        operation: 'queueRefreshInterval',
      })
    }
  }, 30000)
})

onUnmounted(() => {
  if (unsubscribeQueue) {
    unsubscribeQueue()
  }

  if (queueRefreshInterval) {
    clearInterval(queueRefreshInterval)
    queueRefreshInterval = null
  }
})
</script>

<style scoped>
.activity-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  flex-wrap: wrap;
  gap: 0.75rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  width: 32px;
  height: 32px;
}

.activity-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.filter-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.filter-icon {
  position: absolute;
  left: 0.75rem;
  color: #868e96;
  width: 16px;
  height: 16px;
  pointer-events: none;
}

.filter-input {
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  color: #fff;
  padding: 0.5rem 2rem 0.5rem 2.25rem;
  font-size: 0.875rem;
  width: 220px;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}

.filter-input::placeholder {
  color: #868e96;
}

.filter-input:focus {
  outline: none;
  border-color: #4dabf7;
  box-shadow: 0 0 0 2px rgba(77, 171, 247, 0.15);
}

.filter-clear {
  position: absolute;
  right: 0.5rem;
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
}

.filter-clear:hover {
  color: #fa5252;
  background: rgba(250, 82, 82, 0.15);
}

.filter-clear svg {
  width: 14px;
  height: 14px;
}

/* Keep the client health status compact so queue updates cannot move the table. */
.queue-health-details {
  position: relative;
  flex: 0 0 auto;
  z-index: 2;
}

.queue-health-details summary {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  min-height: 2rem;
  padding: 0.25rem 0.55rem;
  border: 1px solid rgba(148, 163, 184, 0.25);
  border-radius: 999px;
  color: var(--color-text-muted, #94a3b8);
  cursor: pointer;
  font-size: 0.8rem;
  list-style: none;
  white-space: nowrap;
}

.queue-health-details summary::-webkit-details-marker {
  display: none;
}

.queue-health-details summary:focus-visible {
  outline: 2px solid var(--color-primary, #2196f3);
  outline-offset: 2px;
}

.queue-health-details.has-warning summary {
  border-color: rgba(217, 119, 6, 0.35);
  background: rgba(245, 158, 11, 0.1);
  color: var(--color-text, #f8fafc);
}

.queue-health-panel {
  position: absolute;
  top: calc(100% + 0.5rem);
  left: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  width: min(24rem, calc(100vw - 2rem));
  padding: 0.8rem 0.9rem;
  border: 1px solid rgba(148, 163, 184, 0.25);
  border-radius: 0.75rem;
  background: var(--color-surface, #1e1e1e);
  box-shadow: 0 0.75rem 2rem rgba(0, 0, 0, 0.28);
  color: var(--color-text, #f8fafc);
}

.queue-health-panel span {
  font-size: 0.85rem;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.activity-summary {
  display: flex;
  gap: 0.5rem;
  margin: 0.25rem 0 0.75rem;
  color: var(--color-text-muted, #94a3b8);
  font-size: 0.8rem;
}

.activity-summary span {
  padding: 0.25rem 0.55rem;
  border: 1px solid rgba(148, 163, 184, 0.2);
  border-radius: 999px;
}

.activity-summary span:last-child {
  color: #ffc078;
}

.activity-list {
  display: grid;
  gap: 0.6rem;
  max-height: calc(100vh - 240px);
  min-height: 0;
  overflow: auto;
  padding: 0.15rem 0.25rem 0.75rem 0;
}

.activity-row {
  min-width: 0;
  padding: 0.85rem 1rem 0.75rem;
  border: 1px solid rgba(148, 163, 184, 0.16);
  border-radius: 0.75rem;
  background: var(--color-surface, #1e1e1e);
  transition:
    border-color 0.15s,
    background-color 0.15s;
}

.activity-row:hover {
  border-color: rgba(148, 163, 184, 0.32);
  background: #222;
}

.activity-row-main {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: start;
  gap: 0.75rem 1rem;
}

.activity-copy {
  min-width: 0;
}

.activity-title {
  display: block;
  max-width: 100%;
  overflow: hidden;
  color: #f8fafc;
  font-weight: 600;
  text-decoration: none;
  text-overflow: ellipsis;
  white-space: nowrap;
}

a.activity-title:hover {
  color: #4dabf7;
}

.activity-meta {
  display: flex;
  min-width: 0;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.3rem 0.8rem;
  margin-top: 0.25rem;
  color: #94a3b8;
  font-size: 0.78rem;
}

.activity-controls {
  display: flex;
  max-width: 100%;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 0.45rem;
}

.activity-progress-row {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 0.65rem;
  margin-top: 0.7rem;
}

.activity-progress-row :deep(.progress-wrapper) {
  flex: 1;
  min-width: 2rem;
}

.activity-progress-state {
  flex: 1;
  color: #94a3b8;
  font-size: 0.78rem;
}

.activity-details {
  position: relative;
}

.activity-details summary {
  padding: 0.35rem 0.55rem;
  border: 1px solid rgba(148, 163, 184, 0.2);
  border-radius: 0.4rem;
  color: #cbd5e1;
  cursor: pointer;
  font-size: 0.78rem;
  list-style: none;
  white-space: nowrap;
}

.activity-details summary::-webkit-details-marker {
  display: none;
}

.activity-details summary::after {
  content: 'v';
  margin-left: 0.35rem;
  color: #94a3b8;
}

.activity-details[open] summary::after {
  content: '^';
}

.activity-details dl {
  position: absolute;
  top: calc(100% + 0.45rem);
  right: 0;
  z-index: 5;
  display: grid;
  gap: 0.45rem;
  width: min(36rem, calc(100vw - 2rem));
  max-height: min(65vh, 28rem);
  overflow: auto;
  margin: 0;
  padding: 0.9rem;
  border: 1px solid rgba(148, 163, 184, 0.25);
  border-radius: 0.65rem;
  background: #181818;
  box-shadow: 0 0.75rem 2rem rgba(0, 0, 0, 0.35);
}

.activity-details dl > div {
  display: grid;
  grid-template-columns: minmax(6rem, auto) minmax(0, 1fr);
  gap: 0.75rem;
  font-size: 0.78rem;
}

.activity-details dt {
  color: #94a3b8;
}

.activity-details dd {
  min-width: 0;
  margin: 0;
  color: #e2e8f0;
  overflow-wrap: anywhere;
}

.activity-secondary-actions {
  display: flex;
  gap: 0.4rem;
  margin-top: 0.65rem;
}

.activity-secondary-actions .btn-icon {
  display: inline-flex;
  width: auto;
  align-items: center;
  gap: 0.35rem;
  padding: 0.35rem 0.55rem;
}

.progress-label {
  font-size: 0.8rem;
  font-weight: 500;
  color: white;
  white-space: nowrap;
  min-width: 40px;
  text-align: right;
}

/* Size */
.size-text {
  font-size: 0.8rem;
  color: #868e96;
}

/* Speed */
.speed-text {
  color: #51cf66;
  font-size: 0.8rem;
}

/* ETA */
.eta-text {
  color: #ffd43b;
  font-size: 0.8rem;
}

.muted {
  color: #495057;
  font-size: 0.8rem;
}

/* Status badges */
.status-badge {
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.3px;
  display: inline-block;
}

.status-badge.completed {
  background-color: rgba(81, 207, 102, 0.15);
  color: #51cf66;
}

.status-badge.downloading {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
}

.status-badge.failed {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.paused {
  background-color: rgba(255, 212, 59, 0.15);
  color: #ffd43b;
}

.status-badge.stalled {
  background: rgba(255, 193, 7, 0.15);
  color: #ffc107;
}

.queue-details {
  font-size: 0.7rem;
  color: #868e96;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.status-badge.queued {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
}

.status-badge.processing,
.status-badge.moving {
  background-color: rgba(190, 75, 219, 0.15);
  color: #be4bdb;
}

.status-badge.importpending {
  background-color: rgba(255, 146, 43, 0.15);
  color: #ff922b;
}

.status-badge.importblocked {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.imported {
  background-color: rgba(32, 201, 151, 0.15);
  color: #20c997;
}

.stale-badge {
  display: inline-block;
  margin-left: 0.35rem;
  padding: 0.1rem 0.35rem;
  border-radius: 4px;
  background: rgba(245, 158, 11, 0.16);
  color: #b45309;
  font-size: 0.65rem;
  font-weight: 600;
}

/* Actions */
.btn-danger-icon {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.35rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}

.btn-danger-icon:hover {
  background: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.btn-danger-icon svg {
  width: 16px;
  height: 16px;
}

/* Modal Styles */
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.85);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  animation: fadeIn 0.2s ease;
  backdrop-filter: blur(4px);
}

.modal-content {
  background-color: #2a2a2a;
  border-radius: 6px;
  width: 90%;
  max-width: 500px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5);
  animation: slideUp 0.3s ease;
  border: 1px solid rgba(255, 255, 255, 0.1);
}

@keyframes slideUp {
  from {
    transform: translateY(20px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.modal-header .event-history-backdrop {
  position: fixed;
  inset: 0;
  z-index: 1200;
  display: grid;
  place-items: center;
  padding: 1rem;
  background: rgb(0 0 0 / 65%);
}

.event-history-dialog {
  width: min(720px, 95vw);
  max-height: 80vh;
  overflow: auto;
  padding: 1.25rem;
  border: 1px solid #444;
  border-radius: 8px;
  background: #232323;
  color: #fff;
}

.event-history-dialog header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.event-history-dialog h2 {
  margin: 0;
  font-size: 1.1rem;
}

.event-history-list {
  display: grid;
  gap: 0.75rem;
  padding: 0;
  list-style: none;
}

.event-history-list li {
  display: grid;
  grid-template-columns: minmax(10rem, auto) minmax(9rem, auto) 1fr;
  gap: 0.75rem;
  padding: 0.75rem;
  border-left: 3px solid #4dabf7;
  background: #1a1a1a;
}

.event-history-list li.failure {
  border-left-color: #fa5252;
}

.event-history-list time {
  color: #adb5bd;
}

.event-history-error {
  color: #ff8787;
}

@media (max-width: 640px) {
  .event-history-list li {
    grid-template-columns: 1fr;
    gap: 0.25rem;
  }
}

h3 {
  margin: 0;
  color: white;
  font-size: 1.25rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.625rem;
}

.modal-header h3 svg {
  color: #ffd43b;
  width: 24px;
  height: 24px;
}

.modal-close {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  line-height: 1;
}

.modal-close:hover {
  background-color: rgba(255, 255, 255, 0.08);
  color: white;
}

.modal-close svg {
  width: 20px;
  height: 20px;
}

.modal-body {
  padding: 1.5rem;
}

.modal-body > p:first-child {
  color: #adb5bd;
  margin: 0 0 1rem 0;
  font-size: 1rem;
  line-height: 1.5;
}

.remove-item-info {
  background-color: rgba(0, 0, 0, 0.3);
  border-left: 4px solid #ffd43b;
  padding: 1rem;
  border-radius: 6px;
  margin-bottom: 1rem;
}

.remove-item-info strong {
  color: white;
  display: block;
  margin-bottom: 0.5rem;
  font-size: 1rem;
  font-weight: 500;
}

.item-details {
  display: flex;
  gap: 1rem;
  font-size: 0.875rem;
  color: #868e96;
  flex-wrap: wrap;
}

.item-details span {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.item-details svg {
  width: 14px;
  height: 14px;
}

.warning-text {
  color: #ffd43b;
  font-size: 0.875rem;
  display: flex;
  align-items: flex-start;
  gap: 0.625rem;
  background-color: rgba(255, 212, 59, 0.1);
  padding: 0.875rem;
  border-radius: 6px;
  margin: 0;
  border: 1px solid rgba(255, 212, 59, 0.25);
  line-height: 1.5;
}

.warning-text svg {
  flex-shrink: 0;
  margin-top: 0.1rem;
  width: 18px;
  height: 18px;
}

.modal-footer {
  justify-content: flex-end;
  gap: 0.75rem;
}

.btn-danger {
  background: #fa5252;
  color: white;
  border: none;
  padding: 0.65rem 1.25rem;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
  font-size: 0.9rem;
  box-shadow: 0 2px 8px rgba(250, 82, 82, 0.3);
}

.btn-danger:hover:not(:disabled) {
  background: #e03131;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(250, 82, 82, 0.4);
}

.btn-danger:active:not(:disabled) {
  transform: translateY(0);
}

.btn-danger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  transform: none;
}

.btn-danger svg {
  width: 16px;
  height: 16px;
}

/* Mobile responsive */
@media (max-width: 768px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .activity-actions {
    width: 100%;
    flex-wrap: wrap;
  }

  .filter-input-wrapper {
    flex: 1;
    min-width: 0;
  }

  .filter-input {
    width: 100%;
  }

  .activity-summary {
    margin-top: 0;
  }

  .activity-list {
    max-height: none;
    overflow: visible;
    padding-right: 0;
  }

  .activity-row {
    padding: 0.75rem;
  }

  .activity-row-main {
    grid-template-columns: minmax(0, 1fr);
    gap: 0.6rem;
  }

  .activity-controls {
    justify-content: flex-start;
  }

  .activity-progress-row {
    gap: 0.45rem;
  }

  .progress-label {
    min-width: 2.5rem;
  }

  .activity-details dl {
    right: auto;
    left: 0;
    width: min(36rem, calc(100vw - 2rem));
  }
}
</style>
