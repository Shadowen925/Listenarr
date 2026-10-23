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
        <button v-if="activeSection === 'queue'" class="btn btn-secondary" @click="clearCompleted">
          Clear Completed
        </button>
        <button v-if="activeSection === 'queue'" class="btn btn-secondary" @click="clearFailed">
          Clear Failed / Dismissed
        </button>
        <div v-if="activeSection === 'queue'" class="filter-input-wrapper">
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
        <button class="btn btn-secondary" @click="refreshCurrentSection" :disabled="loading">
          <component :is="loading ? PhSpinner : PhArrowClockwise" />
          Refresh
        </button>
      </div>
    </div>

    <nav class="activity-subnav" role="tablist" aria-label="Activity sections">
      <button
        v-for="section in activitySections"
        :key="section.id"
        type="button"
        role="tab"
        :aria-selected="activeSection === section.id"
        :class="{ active: activeSection === section.id }"
        @click="selectActivitySection(section.id)"
      >
        {{ section.label }}
      </button>
    </nav>

    <section v-if="activeSection === 'queue'" class="activity-queue-panel" role="tabpanel">
      <details
        class="queue-health-details"
        :class="{ 'has-warning': queueHealthClients.length > 0 }"
      >
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
                <span v-if="item.activityStage">{{ item.activityStage }}</span>
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
                v-if="
                  canRetryRecoveredImport(item) ||
                  (item.activityOperationId && item.activityStage === 'Import Failed')
                "
                class="btn btn-secondary activity-import-button"
                :data-testid="`retry-import-${item.id}`"
                @click="
                  item.activityOperationId ? retryActivitySearch(item) : retryRecoveredImport(item)
                "
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
              <button
                v-if="
                  item.activityKey &&
                  item.status === 'failed' &&
                  item.activityStage !== 'Import Failed'
                "
                class="btn btn-secondary activity-action-button"
                :disabled="retryingActivityKeys.has(item.activityKey)"
                @click="retryActivitySearch(item)"
              >
                {{ retryingActivityKeys.has(item.activityKey) ? 'Retrying...' : 'Retry Search' }}
              </button>
              <RouterLink
                v-if="item.activityKey"
                class="btn btn-secondary activity-action-button"
                :to="{ name: 'audiobook-detail', params: { id: item.audiobookId } }"
              >
                Manual Search
              </RouterLink>
              <button
                class="btn btn-secondary activity-action-button"
                :aria-label="`Dismiss ${getDisplayTitle(item)} from Activity`"
                @click="dismissActivityItem(item)"
              >
                Dismiss
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
                  <div v-if="item.activityStage">
                    <dt>Operation</dt>
                    <dd>{{ item.activityStage }}</dd>
                  </div>
                  <div v-if="item.activityOperationId">
                    <dt>Operation ID</dt>
                    <dd>{{ item.activityOperationId }}</dd>
                  </div>
                  <div v-if="item.activityAssociatedDownloadIds?.length">
                    <dt>Associated download IDs</dt>
                    <dd>{{ item.activityAssociatedDownloadIds.join(', ') }}</dd>
                  </div>
                  <div v-if="item.activityAssociatedProcessingJobIds?.length">
                    <dt>Processing job IDs</dt>
                    <dd>{{ item.activityAssociatedProcessingJobIds.join(', ') }}</dd>
                  </div>
                  <div v-if="item.activityRetryCount != null">
                    <dt>Retry count</dt>
                    <dd>{{ item.activityRetryCount }}</dd>
                  </div>
                  <div v-if="item.activityLastAttemptAt">
                    <dt>Last attempt</dt>
                    <dd>{{ formatApiDateTime(item.activityLastAttemptAt) }}</dd>
                  </div>
                  <div v-if="item.activityLatestError">
                    <dt>Latest error</dt>
                    <dd>{{ item.activityLatestError }}</dd>
                  </div>
                  <div v-if="item.activityDiagnosticId">
                    <dt>Diagnostic ID</dt>
                    <dd>{{ item.activityDiagnosticId }}</dd>
                  </div>
                  <div v-if="item.activitySelectedRelease">
                    <dt>Selected release</dt>
                    <dd>{{ item.activitySelectedRelease }}</dd>
                  </div>
                  <div v-if="item.activityError">
                    <dt>Search details</dt>
                    <dd>{{ item.activityError }}</dd>
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
                  <button
                    v-if="
                      item.activityAssociatedDownloadIds?.length &&
                      ['failed', 'importblocked'].includes(item.status.toLowerCase())
                    "
                    class="btn btn-secondary activity-action-button"
                    @click="ignoreActivityItem(item)"
                  >
                    Ignore download
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
            <div
              v-else-if="item.activityIndeterminate"
              class="activity-indeterminate"
              role="progressbar"
              aria-label="Automatic search in progress"
            >
              <span />
            </div>
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
    </section>

    <section v-else-if="activeSection === 'history'" class="activity-section-panel" role="tabpanel">
      <div class="activity-history-filters">
        <input
          v-model="historySearch"
          type="search"
          aria-label="Search history"
          placeholder="Search history..."
          @keyup.enter="loadHistoryPage(0)"
        />
        <select
          v-model="historyEventType"
          aria-label="Filter by event type"
          @change="loadHistoryPage(0)"
        >
          <option value="">All event types</option>
          <option v-for="eventType in historyEventTypes" :key="eventType" :value="eventType">
            {{ eventType }}
          </option>
        </select>
        <select
          v-model="historyOutcome"
          aria-label="Filter by outcome"
          @change="loadHistoryPage(0)"
        >
          <option value="">All outcomes</option>
          <option value="Succeeded">Succeeded</option>
          <option value="Failed">Failed</option>
          <option value="Skipped">Skipped</option>
        </select>
        <select
          v-model="historyClientId"
          aria-label="Filter by download client"
          @change="loadHistoryPage(0)"
        >
          <option value="">All clients</option>
          <option v-for="client in historyClientIds" :key="client" :value="client">
            {{ client }}
          </option>
        </select>
        <input
          v-model.number="historyAudiobookId"
          type="number"
          min="1"
          aria-label="Filter by audiobook ID"
          placeholder="Audiobook ID"
          @keyup.enter="loadHistoryPage(0)"
        />
        <button class="btn btn-secondary" type="button" @click="loadHistoryPage(0)">Search</button>
      </div>
      <p v-if="historyLoading">Loading history...</p>
      <p v-else-if="historyError" role="alert">{{ historyError }}</p>
      <ol v-else class="activity-history-list">
        <li v-for="entry in historyEntries" :key="entry.id">
          <time :datetime="entry.timestamp">{{ formatApiDateTime(entry.timestamp) }}</time>
          <strong>{{ entry.eventType }}</strong>
          <RouterLink v-if="entry.audiobookId" :to="`/audiobooks/${entry.audiobookId}`">{{
            entry.audiobookTitle || `Audiobook ${entry.audiobookId}`
          }}</RouterLink>
          <span v-else>{{ entry.audiobookTitle || entry.sourceTitle || 'Activity' }}</span>
          <span :class="['history-outcome', (entry.outcome || '').toLowerCase()]">
            {{ entry.outcome || 'Unknown' }}
          </span>
          <p>{{ entry.error || entry.message }}</p>
          <small v-if="entry.downloadClientId">Client: {{ entry.downloadClientId }}</small>
        </li>
        <li v-if="historyEntries.length === 0">No history entries match these filters.</li>
      </ol>
      <div class="activity-pagination">
        <button
          class="btn btn-secondary"
          type="button"
          :disabled="historyLoading || historyOffset === 0"
          @click="loadHistoryPage(Math.max(0, historyOffset - historyPageSize))"
        >
          Previous
        </button>
        <span
          >{{ historyTotal === 0 ? 0 : historyOffset + 1 }}â€“{{
            Math.min(historyOffset + historyPageSize, historyTotal)
          }}
          of {{ historyTotal }}</span
        >
        <button
          class="btn btn-secondary"
          type="button"
          :disabled="historyLoading || historyOffset + historyPageSize >= historyTotal"
          @click="loadHistoryPage(historyOffset + historyPageSize)"
        >
          Next
        </button>
      </div>
    </section>

    <section
      v-else-if="activeSection === 'blocklist'"
      class="activity-section-panel"
      role="tabpanel"
    >
      <p v-if="blocklistLoading">Loading blocklist...</p>
      <p v-else-if="blocklistError" role="alert">{{ blocklistError }}</p>
      <ul v-else class="activity-record-list">
        <li v-for="rejection in releaseRejections" :key="rejection.id">
          <div>
            <strong>{{ rejection.releaseTitle || rejection.releaseKey }}</strong>
            <RouterLink v-if="rejection.audiobookId" :to="`/audiobooks/${rejection.audiobookId}`"
              >Audiobook {{ rejection.audiobookId }}</RouterLink
            >
            <p>{{ rejection.reason }}</p>
            <small
              >{{ rejection.source }} Â· {{ formatApiDateTime(rejection.createdAt) }}</small
            >
          </div>
          <button
            class="btn btn-secondary"
            type="button"
            @click="removeReleaseRejection(rejection.id)"
          >
            Remove
          </button>
        </li>
        <li v-if="releaseRejections.length === 0">No rejected releases are blocklisted.</li>
      </ul>
    </section>

    <section v-else class="activity-section-panel" role="tabpanel">
      <p v-if="ignoredLoading">Loading ignored downloads...</p>
      <p v-else-if="ignoredError" role="alert">{{ ignoredError }}</p>
      <ul v-else class="activity-record-list">
        <li v-for="download in ignoredDownloads" :key="download.id">
          <div>
            <strong>{{ download.title }}</strong>
            <RouterLink v-if="download.audiobookId" :to="`/audiobooks/${download.audiobookId}`"
              >Audiobook {{ download.audiobookId }}</RouterLink
            >
            <p>{{ download.downloadClientId }} Â· {{ download.externalId || download.id }}</p>
            <small v-if="download.sourceRetained"
              >Client source retained after verified import</small
            >
            <small v-else>{{ download.ignoreReason || download.status }}</small>
          </div>
          <button
            v-if="download.ignored"
            class="btn btn-secondary"
            type="button"
            @click="restoreIgnoredDownload(download.id)"
          >
            Restore monitoring
          </button>
        </li>
        <li v-if="ignoredDownloads.length === 0">No ignored or retained downloads.</li>
      </ul>
    </section>

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
              formatApiDateTime(event.timestamp)
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
import { ref, computed, onMounted, onUnmounted, unref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { formatApiDateTime } from '@/utils/dateTime'
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
  History,
  ReleaseRejection,
  AutomaticSearchDiagnosticHistoryItem,
  QuickAddOperationActivityItem,
} from '@/types'
import DownloadActionDialog from '@/components/domain/download/DownloadActionDialog.vue'
import ManualImportModal from '@/components/feedback/ManualImportModal.vue'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

const downloadsStore = useDownloadsStore()
const libraryStore = useLibraryStore()
const moveJobsStore = useMoveJobsStore()
const configStore = useConfigurationStore()

const filterText = ref('')
const route = useRoute() as ReturnType<typeof useRoute> | undefined
const router = useRouter() as ReturnType<typeof useRouter> | undefined
const activitySections = [
  { id: 'queue', label: 'Queue' },
  { id: 'history', label: 'History' },
  { id: 'blocklist', label: 'Blocklist' },
  { id: 'ignored', label: 'Ignored Downloads' },
] as const
type ActivitySection = (typeof activitySections)[number]['id']
const activeSection = ref<ActivitySection>('queue')
const queue = ref<QueueItem[]>([])
const recentHistory = ref<History[]>([])
const historyEntries = ref<History[]>([])
const historySearch = ref('')
const historyEventType = ref('')
const historyOutcome = ref('')
const historyClientId = ref('')
const historyAudiobookId = ref<number | undefined>()
const historyOffset = ref(0)
const historyTotal = ref(0)
const historyPageSize = 50
const historyLoading = ref(false)
const historyError = ref('')
const releaseRejections = ref<ReleaseRejection[]>([])
const blocklistLoading = ref(false)
const blocklistError = ref('')
const ignoredDownloads = ref<Awaited<ReturnType<typeof apiService.getIgnoredDownloads>>>([])
const ignoredLoading = ref(false)
const ignoredError = ref('')
const verifiedImportDownloadIds = ref(new Set<string>())
const activitySearchDiagnostics = ref(new Map<number, AutomaticSearchDiagnosticHistoryItem>())
const quickAddOperations = ref<QuickAddOperationActivityItem[]>([])
const dismissedActivityVersions = ref(new Map<string, string>())
const retryingActivityKeys = ref(new Set<string>())
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
  const externalId =
    download.metadata?.['ClientDownloadId'] ?? download.metadata?.['TorrentHash'] ?? download.id
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
    activityOperationIdentity: `${download.downloadClientId}:${externalId}`,
    activityAssociatedDownloadIds: [download.id],
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
  downloadClient: job.phase ? `Library move Â· ${job.phase}` : 'Library move',
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

// All activity items â€” unified list of queue + downloads
const isDirectDownload = (downloadClientId?: string): boolean =>
  (downloadClientId || '').toString().toUpperCase() === 'DDL'

const allActivityItems = computed(() => {
  const importedEntries = recentHistory.value.filter(
    (entry) =>
      entry.eventType === 'Imported' &&
      ['succeeded', '1'].includes(String(entry.outcome ?? '').toLowerCase()),
  )
  const importedDownloadIds = new Set([
    ...verifiedImportDownloadIds.value,
    ...importedEntries.map((entry) => entry.downloadId).filter((id): id is string => Boolean(id)),
  ])
  const isVerifiedImport = (item: QueueItem) =>
    importedDownloadIds.has(item.id) ||
    (item.audiobookId != null &&
      importedEntries.some(
        (entry) =>
          entry.audiobookId === item.audiobookId &&
          ['completed', 'imported', 'moved'].includes(item.status.toLowerCase()) &&
          (item.progress ?? 0) >= 100,
      ))
  const queueItems = queue.value.filter(
    (item) => !['moved', 'imported'].includes(item.status.toLowerCase()) && !isVerifiedImport(item),
  )
  const trackedQueueIds = new Set(queueItems.map((item) => item.id))

  const activeDownloadsList = unref(downloadsStore.activeDownloads || [])
  const failedDownloadsList = unref(downloadsStore.failedDownloads || [])

  const ddlDownloadItems = activeDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !importedDownloadIds.has(d.id))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const failedDDLItems = failedDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !importedDownloadIds.has(d.id))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const externalActiveDownloads = activeDownloadsList
    .filter((d) => d.downloadClientId && !isDirectDownload(d.downloadClientId))
    .filter((d) => !importedDownloadIds.has(d.id))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  let combined = [...queueItems, ...ddlDownloadItems, ...failedDDLItems, ...externalActiveDownloads]

  if (showCompletedExternalDownloads.value) {
    const completedExternal = (downloadsStore.completedDownloads || [])
      .filter(
        (d) => d.downloadClientId && (d.downloadClientId || '').toString().toUpperCase() !== 'DDL',
      )
      .filter((d) => d.status !== 'Moved')
      .filter((d) => !importedDownloadIds.has(d.id))
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
  for (const it of failedFromDownloads) {
    if (!finalMap.has(it.id) && !isVerifiedImport(it)) finalMap.set(it.id, it)
  }

  for (const operation of quickAddOperations.value) {
    if (operation.state === 'Imported') continue

    const activityKey = `quick-add:${operation.id}`

    const diagnostic = activitySearchDiagnostics.value.get(operation.audiobookId)
    const stageByState: Record<string, string> = {
      SearchPending: 'Search Pending',
      Searching: 'Searching Indexers',
      EvaluatingReleases: 'Evaluating Releases',
      DownloadSubmissionPending: 'Submitting to Download Client',
      Queued: 'Queued',
      Downloading: 'Downloading',
      DownloadCompleted: 'Download Completed',
      ImportPending: 'Import Pending',
      Importing: 'Importing',
      Verifying: 'Verifying Library Files',
      NoMatchingReleases: 'No Matching Releases',
      FailedNeedsAttention: operation.processingJobId ? 'Import Failed' : 'Search Failed',
    }
    const state = operation.state
    const stage = stageByState[state] ?? 'Needs Attention'
    const searchInProgress = [
      'SearchPending',
      'Searching',
      'EvaluatingReleases',
      'DownloadSubmissionPending',
    ].includes(state)
    const importInProgress = ['ImportPending', 'Importing', 'Verifying'].includes(state)
    const statusByState: Record<string, string> = {
      SearchPending: 'searching',
      Searching: 'searching',
      EvaluatingReleases: 'searching',
      DownloadSubmissionPending: 'searching',
      Queued: 'queued',
      Downloading: 'downloading',
      DownloadCompleted: 'completed',
      ImportPending: 'importpending',
      Importing: 'processing',
      Verifying: 'processing',
      NoMatchingReleases: 'failed',
      FailedNeedsAttention: 'failed',
    }
    const existing = operation.downloadId ? finalMap.get(operation.downloadId) : undefined
    if (existing) {
      existing.activityKey = activityKey
      existing.activityOperationId = operation.id
      existing.activityDiagnosticId = operation.diagnosticId ?? diagnostic?.id
      existing.activitySelectedRelease =
        operation.selectedRelease ?? diagnostic?.selectedRelease ?? undefined
      existing.activityStage = stage
      existing.activityError =
        operation.failureReason || diagnostic?.rejectionReasons?.join('; ') || undefined
      existing.activityIndeterminate = searchInProgress || importInProgress
      existing.title = operation.audiobookTitle || existing.title
      existing.activityOperationIdentity ??= operation.downloadId
        ? `${operation.downloadClientId ?? ''}:${operation.downloadId}`
        : undefined
      if (operation.processingJobId) {
        existing.activityAssociatedProcessingJobIds = Array.from(
          new Set([
            ...(existing.activityAssociatedProcessingJobIds ?? []),
            operation.processingJobId,
          ]),
        )
      }
      existing.activityRetryCount = Math.max(existing.activityRetryCount ?? 0, operation.attempt)
      existing.activityLastAttemptAt = operation.updatedAt
      existing.activityLatestError = operation.failureReason ?? existing.activityLatestError
      continue
    }

    finalMap.set(activityKey, {
      id: activityKey,
      activityKey,
      activityOperationId: operation.id,
      activityDiagnosticId: operation.diagnosticId ?? diagnostic?.id,
      activitySelectedRelease:
        operation.selectedRelease ?? diagnostic?.selectedRelease ?? undefined,
      activityStage: stage,
      activityError:
        operation.failureReason || diagnostic?.rejectionReasons?.join('; ') || undefined,
      activityIndeterminate: searchInProgress || importInProgress,
      title: operation.audiobookTitle || 'New audiobook',
      audiobookId: operation.audiobookId,
      status: statusByState[state] ?? 'failed',
      progress: state === 'DownloadCompleted' ? 100 : 0,
      size: 0,
      downloaded: 0,
      downloadSpeed: 0,
      quality: '',
      downloadClient: operation.downloadClientId ?? 'Quick Add',
      downloadClientId: operation.downloadClientId ?? 'LISTENARR_SEARCH',
      downloadClientType: 'operation',
      activityOperationIdentity: operation.downloadId
        ? `${operation.downloadClientId ?? ''}:${operation.downloadId}`
        : undefined,
      activityAssociatedDownloadIds: operation.downloadId ? [operation.downloadId] : [],
      activityAssociatedProcessingJobIds: operation.processingJobId
        ? [operation.processingJobId]
        : [],
      activityRetryCount: operation.attempt,
      activityLastAttemptAt: operation.updatedAt,
      activityLatestError: operation.failureReason ?? undefined,
      addedAt: operation.createdAt,
      errorMessage: operation.failureReason ?? undefined,
      canPause: false,
      canRemove: false,
    })
  }

  const grouped = new Map<string, QueueItem>()
  for (const item of finalMap.values()) {
    const key = activityKeyFor(item)
    const current = grouped.get(key)
    if (!current) {
      grouped.set(key, { ...item })
      continue
    }

    const mergedDownloadIds = new Set([
      ...(current.activityAssociatedDownloadIds ?? [current.id]),
      ...(item.activityAssociatedDownloadIds ?? [item.id]),
    ])
    const latest = Date.parse(item.addedAt) >= Date.parse(current.addedAt) ? item : current
    grouped.set(key, {
      ...current,
      ...latest,
      activityAssociatedDownloadIds: Array.from(mergedDownloadIds),
    })
  }

  for (const item of grouped.values()) {
    const downloadIds = new Set(item.activityAssociatedDownloadIds ?? [item.id])
    const operationHistory = recentHistory.value
      .filter(
        (entry) =>
          entry.downloadId != null &&
          downloadIds.has(entry.downloadId) &&
          ['ImportStarted', 'ImportRetry', 'ImportFailed'].includes(entry.eventType),
      )
      .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp))
    const processingJobIds = new Set(item.activityAssociatedProcessingJobIds ?? [])
    for (const entry of operationHistory) {
      if (!entry.data) continue
      try {
        const details = JSON.parse(entry.data) as {
          JobId?: string
          RetryCount?: number
        }
        if (details.JobId) processingJobIds.add(details.JobId)
        if (typeof details.RetryCount === 'number') {
          item.activityRetryCount = Math.max(item.activityRetryCount ?? 0, details.RetryCount)
        }
      } catch {
        continue
      }
    }
    const latestAttempt = operationHistory.at(-1)
    item.activityAssociatedProcessingJobIds = Array.from(processingJobIds)
    item.activityLastAttemptAt = latestAttempt?.timestamp ?? item.activityLastAttemptAt
    item.activityLatestError =
      [...operationHistory].reverse().find((entry) => entry.eventType === 'ImportFailed')
        ?.message ??
      item.activityLatestError ??
      item.errorMessage
  }

  // Imported downloads are history, not queue; completed ones leave once the client entry is gone
  // (Sonarr/Radarr/Chaptarr queue excludes Imported).
  const isQueueEntry = (item: QueueItem): boolean => {
    const status = item.status.toLowerCase()
    if (['moved', 'imported'].includes(status)) return false
    if (status === 'completed' && (item.sourcePathUnavailable || item.missingFromClient)) {
      return false
    }
    // The client no longer has the transfer and the book isn't wanted (or already has files): nothing left to act on.
    if (
      (!isLiveInClient(item) || item.sourcePathUnavailable || item.missingFromClient) &&
      !['processing', 'importpending', 'searching'].includes(status) &&
      item.audiobookId != null
    ) {
      const book = libraryStore.audiobooks.find((ab) => ab.id === item.audiobookId)
      if (
        book &&
        (book.wanted === false ||
          book.filePath ||
          (book.fileCount ?? 0) > 0 ||
          (book.files?.length ?? 0) > 0)
      ) {
        return false
      }
    }
    return true
  }
  return Array.from(grouped.entries())
    .filter(([key, item]) => !isActivityDismissed(key, item))
    .map(([, item]) => item)
    .filter(isQueueEntry)
})

const activityKeyFor = (item: QueueItem): string =>
  item.activityOperationIdentity
    ? `download-operation:${item.activityOperationIdentity}`
    : (item.activityKey ??
      (item.downloadClientType === 'move'
        ? item.id.startsWith('move:')
          ? item.id
          : `move:${item.id}`
        : `download:${item.downloadClientType}:${item.downloadClientId}:${item.id}`))

const activityVersionFor = (item: QueueItem): string =>
  JSON.stringify([
    item.status.toLowerCase(),
    item.recoveryState ?? '',
    item.activityStage ?? '',
    (item.activityLatestError ?? item.errorMessage ?? item.activityError ?? '').slice(0, 500),
  ])

// A row is live only while the download client actually reports it (DDL is owned by Listenarr).
const isLiveInClient = (item: QueueItem): boolean => {
  if (item.downloadClientType === 'DDL' || item.missingFromClient || item.sourcePathUnavailable) {
    return item.downloadClientType === 'DDL'
  }
  const ids = new Set([item.id, ...(item.activityAssociatedDownloadIds ?? [])])
  return queue.value.some((q) => ids.has(q.id))
}

// Dismissals stick while the status is unchanged; volatile stage/error text must not resurrect rows.
const isActivityDismissed = (key: string, item: QueueItem): boolean => {
  const active = ['downloading', 'queued', 'paused'].includes(item.status.toLowerCase())
  // Transfers the client really reports stay visible; client-lost ones can be dismissed.
  if (active && isLiveInClient(item)) return false
  const stored = dismissedActivityVersions.value.get(key)
  if (stored === undefined) return false
  if (stored === activityVersionFor(item)) return true
  try {
    const parsed = JSON.parse(stored) as unknown
    return Array.isArray(parsed) && parsed[0] === item.status.toLowerCase()
  } catch {
    return false
  }
}

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

const historyEventTypes = computed(() =>
  Array.from(
    new Set([...recentHistory.value, ...historyEntries.value].map((entry) => entry.eventType)),
  )
    .filter(Boolean)
    .sort(),
)

const historyClientIds = computed(() =>
  Array.from(
    new Set(
      [...recentHistory.value, ...historyEntries.value]
        .map((entry) => entry.downloadClientId)
        .filter((clientId): clientId is string => Boolean(clientId)),
    ),
  ).sort(),
)

const activeActivityCount = computed(
  () =>
    allActivityItems.value.filter((item) =>
      [
        'queued',
        'downloading',
        'paused',
        'processing',
        'moving',
        'importpending',
        'searching',
      ].includes(item.status.toLowerCase()),
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
    const [queueSnapshot, , historySnapshot, dismissals, importedDownloadIds, operations] =
      await Promise.all([
        apiService.getQueue(),
        downloadsStore.loadDownloads(),
        apiService.getRecentHistory(200),
        apiService.getActivityDismissals(),
        apiService.getVerifiedImportDownloadIds(),
        apiService.getQuickAddOperations(),
      ])
    applyQueueSnapshot(queueSnapshot)
    recentHistory.value = historySnapshot
    verifiedImportDownloadIds.value = new Set(importedDownloadIds)
    // Newest dismissal first: keep it so an older version for the same key can't override it.
    const latestDismissals = new Map<string, string>()
    for (const dismissal of dismissals) {
      if (!latestDismissals.has(dismissal.key)) latestDismissals.set(dismissal.key, dismissal.version)
    }
    dismissedActivityVersions.value = latestDismissals
    quickAddOperations.value = operations
    const quickAddIds = Array.from(new Set(operations.map((operation) => operation.audiobookId)))
    const diagnosticEntries = await Promise.all(
      quickAddIds.map(async (id) => {
        try {
          const diagnostics = await apiService.getAutomaticSearchHistory(id, 1)
          return [id, diagnostics[0]] as const
        } catch (error) {
          errorTracking.captureException(error as Error, {
            component: 'ActivityView',
            operation: 'loadQuickAddSearchHistory',
          })
          return [id, undefined] as const
        }
      }),
    )
    activitySearchDiagnostics.value = new Map(
      diagnosticEntries.filter(
        (entry): entry is readonly [number, AutomaticSearchDiagnosticHistoryItem] =>
          entry[1] !== undefined,
      ),
    )
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'refreshQueue',
    })
  } finally {
    loading.value = false
  }
}

const loadHistoryPage = async (offset: number) => {
  historyLoading.value = true
  historyError.value = ''
  try {
    const page = await apiService.getHistory(historyPageSize, offset, {
      searchTerm: historySearch.value,
      eventType: historyEventType.value,
      outcome: historyOutcome.value,
      downloadClientId: historyClientId.value,
      audiobookId: historyAudiobookId.value,
    })
    historyEntries.value = page.history
    historyOffset.value = page.offset
    historyTotal.value = page.total
  } catch (error) {
    historyError.value = 'Unable to load activity history.'
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'loadHistoryPage',
    })
  } finally {
    historyLoading.value = false
  }
}

const loadReleaseRejections = async () => {
  blocklistLoading.value = true
  blocklistError.value = ''
  try {
    releaseRejections.value = await apiService.getAllReleaseRejections()
  } catch (error) {
    blocklistError.value = 'Unable to load rejected releases.'
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'loadReleaseRejections',
    })
  } finally {
    blocklistLoading.value = false
  }
}

const loadIgnoredDownloads = async () => {
  ignoredLoading.value = true
  ignoredError.value = ''
  try {
    ignoredDownloads.value = await apiService.getIgnoredDownloads()
  } catch (error) {
    ignoredError.value = 'Unable to load ignored downloads.'
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'loadIgnoredDownloads',
    })
  } finally {
    ignoredLoading.value = false
  }
}

const isActivitySection = (value: unknown): value is ActivitySection =>
  activitySections.some((section) => section.id === value)

const sectionFromRoute = (): ActivitySection => {
  const value = route?.query?.section
  const candidate = Array.isArray(value) ? value[0] : value
  return isActivitySection(candidate) ? candidate : 'queue'
}

const selectActivitySection = async (section: ActivitySection) => {
  if (route && router && sectionFromRoute() !== section) {
    await router.push({ path: route.path, query: section === 'queue' ? {} : { section } })
    return
  }
  activeSection.value = section
  await refreshCurrentSection()
}

if (route) {
  watch(
    () => route.query.section,
    async () => {
      const next = sectionFromRoute()
      if (next === activeSection.value) return
      activeSection.value = next
      await refreshCurrentSection()
    },
  )
}

const refreshCurrentSection = async () => {
  switch (activeSection.value) {
    case 'queue':
      await refreshQueue()
      break
    case 'history':
      await loadHistoryPage(historyOffset.value)
      break
    case 'blocklist':
      await loadReleaseRejections()
      break
    case 'ignored':
      await loadIgnoredDownloads()
      break
  }
}

const removeReleaseRejection = async (id: number) => {
  try {
    await apiService.clearReleaseRejection(id)
    await loadReleaseRejections()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'removeReleaseRejection',
    })
    useToast().error('Blocklist update failed', 'Unable to remove this release from the blocklist.')
  }
}

const restoreIgnoredDownload = async (id: string) => {
  try {
    await apiService.restoreDownloadMonitoring(id)
    await Promise.all([loadIgnoredDownloads(), refreshQueue()])
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'restoreIgnoredDownload',
    })
    useToast().error('Monitoring restore failed', 'Unable to restore download monitoring.')
  }
}

const ignoreActivityItem = async (item: QueueItem) => {
  const downloadIds = Array.from(new Set(item.activityAssociatedDownloadIds ?? [item.id]))
  try {
    for (const id of downloadIds) {
      await apiService.ignoreDownload(id)
    }
    useToast().success('Download ignored', 'The operation will no longer be retried automatically.')
    await Promise.all([refreshQueue(), loadIgnoredDownloads()])
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'ActivityView',
      operation: 'ignoreActivityItem',
    })
    useToast().error('Unable to ignore download', (error as Error).message)
  }
}

const retryActivitySearch = async (item: QueueItem) => {
  if (
    !item.activityOperationId ||
    !item.activityKey ||
    retryingActivityKeys.value.has(item.activityKey)
  ) {
    return
  }

  retryingActivityKeys.value = new Set(retryingActivityKeys.value).add(item.activityKey)
  try {
    await apiService.retryQuickAddOperation(item.activityOperationId)
    useToast().success('Retry queued', `Retrying ${getDisplayTitle(item)}.`)
    await refreshQueue()
  } catch (error) {
    useToast().error('Search failed', (error as Error).message || 'Could not retry the search.')
  } finally {
    const remaining = new Set(retryingActivityKeys.value)
    remaining.delete(item.activityKey)
    retryingActivityKeys.value = remaining
  }
}

const dismissActivityItem = async (item: QueueItem) => {
  const key = activityKeyFor(item)
  const version = activityVersionFor(item)
  try {
    await apiService.dismissActivityEntries([
      {
        key,
        version,
        audiobookId: item.audiobookId,
        title: getDisplayTitle(item),
        downloadId: item.activityKey ? undefined : item.id,
      },
    ])
    dismissedActivityVersions.value = new Map(dismissedActivityVersions.value).set(key, version)
  } catch (error) {
    useToast().error('Dismiss failed', (error as Error).message || 'Could not dismiss this entry.')
  }
}

const dismissActivityItems = async (items: QueueItem[]) => {
  if (items.length === 0) {
    useToast().info('Nothing to clear', 'There are no matching Activity entries.')
    return
  }
  const entries = items.map((item) => ({
    key: activityKeyFor(item),
    version: activityVersionFor(item),
    audiobookId: item.audiobookId,
    title: getDisplayTitle(item),
    downloadId: item.activityKey ? undefined : item.id,
  }))
  try {
    await apiService.dismissActivityEntries(entries)
    const versions = new Map(dismissedActivityVersions.value)
    for (const entry of entries) versions.set(entry.key, entry.version)
    dismissedActivityVersions.value = versions
  } catch (error) {
    useToast().error(
      'Clear failed',
      (error as Error).message || 'Could not clear Activity entries.',
    )
  }
}

const clearCompleted = async () => {
  const completed = allActivityItems.value.filter((item) =>
    ['completed', 'imported'].includes(item.status.toLowerCase()),
  )
  if (
    completed.length > 0 &&
    !window.confirm(`Dismiss ${completed.length} completed Activity item(s)?`)
  ) {
    return
  }
  await dismissActivityItems(completed)
}

const clearFailed = async () => {
  const failed = allActivityItems.value.filter(
    (item) =>
      ['failed', 'importblocked'].includes(item.status.toLowerCase()) ||
      ['ImportFailed', 'NeedsAttention'].includes(item.recoveryState ?? ''),
  )
  if (failed.length > 0 && !window.confirm(`Dismiss ${failed.length} failed Activity item(s)?`)) {
    return
  }
  await dismissActivityItems(failed)
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
  return parts.join(' Â· ')
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
  if (item.activityStage === 'Import Failed') return 'Import Failed'
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
  activeSection.value = sectionFromRoute()

  await downloadsStore.loadDownloads()
  await configStore.loadApplicationSettings()

  unsubscribeQueue = signalRService.onQueueUpdate((updatedQueue) => {
    applyQueueSnapshot(updatedQueue)
  })
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
  min-width: 0;
  flex-wrap: wrap;
  gap: 0.75rem;
  align-items: center;
  justify-content: flex-end;
}

.activity-subnav {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35rem;
  margin-bottom: 0.85rem;
  border-bottom: 1px solid rgba(148, 163, 184, 0.2);
}

.activity-subnav button {
  min-height: 2.5rem;
  padding: 0.55rem 0.9rem;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: #adb5bd;
  cursor: pointer;
}

.activity-subnav button.active,
.activity-subnav button:hover {
  border-bottom-color: #4dabf7;
  color: #f8fafc;
}

.activity-section-panel {
  min-width: 0;
  color: #e2e8f0;
}

.activity-history-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-bottom: 0.8rem;
}

.activity-history-filters input,
.activity-history-filters select {
  min-width: 9rem;
  padding: 0.55rem 0.65rem;
  border: 1px solid #454b54;
  border-radius: 0.4rem;
  background: #20242b;
  color: #f8fafc;
}

.activity-history-list,
.activity-record-list {
  display: grid;
  gap: 0.55rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.activity-history-list li,
.activity-record-list li {
  display: grid;
  grid-template-columns: minmax(9rem, auto) minmax(8rem, auto) minmax(0, 1fr) auto;
  align-items: start;
  gap: 0.5rem 0.8rem;
  padding: 0.8rem;
  border: 1px solid rgba(148, 163, 184, 0.18);
  border-radius: 0.55rem;
  background: var(--color-surface, #1e1e1e);
}

.activity-history-list time,
.activity-record-list small {
  color: #94a3b8;
  font-size: 0.8rem;
}

.activity-history-list p,
.activity-record-list p {
  grid-column: 1 / -1;
  margin: 0;
  overflow-wrap: anywhere;
}

.activity-history-list small {
  color: #94a3b8;
}

.activity-pagination {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  margin-top: 0.85rem;
}

.history-outcome.failed {
  color: #fa5252;
}

.history-outcome.succeeded {
  color: #51cf66;
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

.activity-indeterminate {
  position: relative;
  flex: 1;
  height: 3px;
  overflow: hidden;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.22);
}

.activity-indeterminate span {
  position: absolute;
  width: 35%;
  height: 100%;
  border-radius: inherit;
  background: #4dabf7;
  animation: activity-indeterminate 1.4s ease-in-out infinite;
}

@keyframes activity-indeterminate {
  from {
    transform: translateX(-110%);
  }
  to {
    transform: translateX(300%);
  }
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
  .activity-history-list li,
  .activity-record-list li {
    grid-template-columns: 1fr;
  }

  .page-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .activity-actions {
    width: 100%;
    justify-content: flex-start;
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
