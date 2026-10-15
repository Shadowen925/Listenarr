<template>
  <section class="tab-content abs-import">
    <div class="form-section">
      <h2>Audiobookshelf connection</h2>
      <p>Import existing records in place. Audiobookshelf metadata is treated as authoritative.</p>

      <div class="connection-fields">
        <label>
          Server URL
          <input
            v-model.trim="connection.url"
            type="url"
            placeholder="http://audiobookshelf:13378"
            autocomplete="url"
          />
        </label>
        <label>
          API key
          <input
            v-model="connection.apiKey"
            type="password"
            autocomplete="new-password"
            :placeholder="connection.hasSavedApiKey ? 'Saved key is protected' : 'API key'"
          />
        </label>
        <div class="actions">
          <button class="btn btn-primary" :disabled="saving" @click="saveConnection">
            {{ saving ? 'Saving…' : 'Save connection' }}
          </button>
          <button
            class="btn btn-secondary"
            :disabled="testing || !connection.hasSavedApiKey"
            @click="testConnection"
          >
            {{ testing ? 'Testing…' : 'Test connection' }}
          </button>
          <button
            class="btn btn-secondary"
            :disabled="loadingLibraries || !connection.hasSavedApiKey"
            @click="loadLibraries"
          >
            {{ loadingLibraries ? 'Loading…' : 'Discover libraries' }}
          </button>
        </div>
      </div>
      <p v-if="connection.hasSavedApiKey" class="muted">
        An API key is saved; it is never returned to the browser.
      </p>
    </div>

    <div v-if="libraries.length > 0" class="form-section">
      <h2>Map libraries to Listenarr root folders</h2>
      <p>Each Audiobookshelf library can map to only one Listenarr root folder.</p>
      <div v-for="root in rootFolders" :key="root.id" class="mapping-row">
        <strong>{{ root.name }}</strong>
        <select
          v-model="mappingDrafts[root.id]"
          :disabled="mappingRootId === root.id"
          :aria-label="`Audiobookshelf library for ${root.name}`"
          @change="saveMapping(root)"
        >
          <option value="">Not mapped</option>
          <option v-for="library in libraries" :key="library.id" :value="library.id">
            {{ library.name }}
          </option>
        </select>
        <span v-if="mappingRootId === root.id" class="muted">Saving…</span>
      </div>
      <p v-if="rootFolders.length === 0" class="muted">
        Add a Listenarr root folder before importing.
      </p>
    </div>

    <div v-if="libraries.length > 0" class="form-section">
      <h2>Import preview</h2>
      <div class="actions">
        <label>
          Audiobookshelf library
          <select v-model="selectedLibraryId">
            <option value="">Select a library</option>
            <option v-for="library in libraries" :key="library.id" :value="library.id">
              {{ library.name }}
            </option>
          </select>
        </label>
        <button
          class="btn btn-secondary"
          :disabled="!selectedLibraryId || previewing"
          @click="loadPreview"
        >
          {{ previewing ? 'Building preview…' : 'Preview import' }}
        </button>
        <button
          class="btn btn-secondary"
          :disabled="!selectedLibraryId || scanning"
          @click="scanLibrary"
        >
          {{ scanning ? 'Requesting scan…' : 'Scan Audiobookshelf library' }}
        </button>
      </div>

      <div v-if="preview" class="preview-summary" aria-live="polite">
        <span>Matched: {{ matchedCount }}</span>
        <span>Unmatched: {{ unmatchedCount }}</span>
        <span>Duplicates: {{ duplicateCount }}</span>
        <span>Missing ASIN/ISBN: {{ missingIdentifierCount }}</span>
      </div>

      <div v-if="preview" class="selection-toolbar" role="toolbar" aria-label="Import selection">
        <strong class="selected-count" data-testid="selected-count"
          >{{ selectedItemIds.size }} selected</strong
        >
        <button
          class="btn btn-primary"
          data-testid="import-selected"
          :disabled="selectedItemIds.size === 0 || importing"
          @click="importSelected"
        >
          {{ importing ? 'Importing…' : `Import ${selectedItemIds.size} selected` }}
        </button>
        <button
          class="btn btn-secondary"
          data-testid="select-all-matched"
          :disabled="matchedCount === 0"
          @click="selectAllMatched"
        >
          Select All Matched ({{ matchedCount }})
        </button>
        <button
          class="btn btn-secondary"
          data-testid="select-visible-matched"
          :disabled="visibleMatchedCount === 0"
          @click="selectVisibleMatched"
        >
          Select Visible Matched ({{ visibleMatchedCount }})
        </button>
        <button
          class="btn btn-secondary"
          data-testid="clear-selection"
          :disabled="selectedItemIds.size === 0"
          @click="clearSelection"
        >
          Clear selection
        </button>
      </div>

      <div v-if="preview" class="actions">
        <label>
          Search
          <input v-model.trim="searchText" type="search" placeholder="Title, author or path" />
        </label>
        <label>
          Status
          <select v-model="statusFilter">
            <option value="All">All</option>
            <option value="Matched">Matched</option>
            <option value="Unmatched">Unmatched</option>
            <option value="Duplicate">Duplicate</option>
            <option value="MissingIdentifiers">Missing ASIN/ISBN</option>
          </select>
        </label>
        <label class="inline-check">
          <input v-model="exportFilteredOnly" type="checkbox" />
          Export filtered only
        </label>
        <button
          class="btn btn-secondary"
          data-testid="export-unmatched"
          :disabled="exportUnmatchedCount === 0"
          @click="exportUnmatched"
        >
          Export Unmatched ({{ exportUnmatchedCount }})
        </button>
        <button class="btn btn-secondary" data-testid="export-full" @click="exportFullPreview">
          Export Full Preview
        </button>
      </div>

      <div v-if="preview?.items.length" class="preview-table-wrap">
        <table class="preview-table">
          <thead>
            <tr>
              <th scope="col">Import</th>
              <th scope="col">Title</th>
              <th scope="col">Author</th>
              <th scope="col">Status</th>
              <th scope="col">Listenarr path</th>
              <th scope="col">Identifiers</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in pagedItems" :key="item.itemId">
              <td>
                <input
                  v-if="item.status === 'Matched'"
                  type="checkbox"
                  :checked="selectedItemIds.has(item.itemId)"
                  :aria-label="`Import ${item.title}`"
                  @change="toggleSelection(item.itemId)"
                />
              </td>
              <td>{{ item.title }}</td>
              <td>{{ item.author || 'Unknown author' }}</td>
              <td :title="item.reason || undefined">{{ item.status }}</td>
              <td>{{ item.listenarrPath || item.reason || '—' }}</td>
              <td>{{ item.missingIdentifiers ? 'Missing ASIN/ISBN' : 'Available' }}</td>
            </tr>
          </tbody>
        </table>
        <div class="actions pager">
          <button class="btn btn-secondary" :disabled="page <= 1" @click="page--">Previous</button>
          <span data-testid="page-info"
            >Page {{ page }} of {{ pageCount }} ({{ filteredItems.length }} books)</span
          >
          <button class="btn btn-secondary" :disabled="page >= pageCount" @click="page++">
            Next
          </button>
        </div>
      </div>

      <div v-if="importResult" class="result-message" role="status">
        <strong>
          Imported {{ importResult.importedCount }}; skipped {{ importResult.skippedCount }}; failed
          {{ importResult.failedCount ?? 0 }}
          <template v-if="importResult.selectedCount !== undefined">
            (of {{ importResult.selectedCount }} selected)
          </template>
        </strong>
        <span v-if="importResult.importedAt" class="muted">
          at {{ importResult.importedAt }} · library {{ importResult.libraryId }}
        </span>
        <ul v-if="importResult.items?.length">
          <li v-for="item in importResult.items" :key="item.itemId">
            {{ item.outcome }} — {{ item.title
            }}<template v-if="item.reason">: {{ item.reason }}</template>
          </li>
        </ul>
        <ul v-else-if="importResult.messages.length">
          <li v-for="message in importResult.messages" :key="message">{{ message }}</li>
        </ul>
        <button class="btn btn-secondary" data-testid="export-results" @click="exportImportResults">
          Export import results
        </button>
      </div>
    </div>
    <p v-if="error" class="error-message" role="alert">{{ error }}</p>
    <p v-if="notice" class="success-message" role="status">{{ notice }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { apiService } from '@/services/api'
import {
  buildFullPreviewCsv,
  buildImportResultCsv,
  buildUnmatchedCsv,
  downloadCsv,
  exportFileName,
} from '@/utils/audiobookshelfImport'
import type {
  AudiobookshelfImportConnectionSettings,
  AudiobookshelfImportPreview,
  AudiobookshelfImportResult,
  AudiobookshelfLibrary,
  RootFolder,
} from '@/types'

const connection = reactive<AudiobookshelfImportConnectionSettings>({
  url: '',
  apiKey: '',
  hasSavedApiKey: false,
})
const libraries = ref<AudiobookshelfLibrary[]>([])
const rootFolders = ref<RootFolder[]>([])
const mappingDrafts = reactive<Record<number, string>>({})
const selectedLibraryId = ref('')
const preview = ref<AudiobookshelfImportPreview | null>(null)
const selectedItemIds = ref(new Set<string>())
const importResult = ref<AudiobookshelfImportResult | null>(null)
const searchText = ref('')
const statusFilter = ref('All')
const exportFilteredOnly = ref(false)
const page = ref(1)
const PAGE_SIZE = 50
const saving = ref(false)
const testing = ref(false)
const loadingLibraries = ref(false)
const mappingRootId = ref<number | null>(null)
const previewing = ref(false)
const importing = ref(false)
const scanning = ref(false)
const error = ref('')
const notice = ref('')

const matchedCount = computed(
  () => preview.value?.items.filter((item) => item.status === 'Matched').length ?? 0,
)
const unmatchedCount = computed(
  () => preview.value?.items.filter((item) => item.status === 'Unmatched').length ?? 0,
)
const duplicateCount = computed(
  () => preview.value?.items.filter((item) => item.status === 'Duplicate').length ?? 0,
)
const missingIdentifierCount = computed(
  () => preview.value?.items.filter((item) => item.missingIdentifiers).length ?? 0,
)

const filteredItems = computed(() => {
  const needle = searchText.value.toLowerCase()
  return (preview.value?.items ?? []).filter((item) => {
    if (statusFilter.value === 'MissingIdentifiers') {
      if (!item.missingIdentifiers) return false
    } else if (statusFilter.value !== 'All' && item.status !== statusFilter.value) {
      return false
    }
    if (!needle) return true
    return [item.title, item.author, item.audiobookshelfPath, item.listenarrPath]
      .filter(Boolean)
      .some((value) => String(value).toLowerCase().includes(needle))
  })
})
const pageCount = computed(() => Math.max(1, Math.ceil(filteredItems.value.length / PAGE_SIZE)))
const pagedItems = computed(() =>
  filteredItems.value.slice((page.value - 1) * PAGE_SIZE, page.value * PAGE_SIZE),
)
const visibleMatchedCount = computed(
  () => pagedItems.value.filter((item) => item.status === 'Matched').length,
)
const exportUnmatchedCount = computed(() => {
  const source = exportFilteredOnly.value ? filteredItems.value : (preview.value?.items ?? [])
  return source.filter((item) => item.status === 'Unmatched').length
})

// Filters change what is listed, never what is selected.
watch([searchText, statusFilter], () => {
  page.value = 1
})
function errorMessage(reason: unknown): string {
  return reason instanceof Error ? reason.message : 'Audiobookshelf request failed.'
}

async function loadInitialState() {
  error.value = ''
  try {
    const [settings, roots] = await Promise.all([
      apiService.getAudiobookshelfImportSettings(),
      apiService.getRootFolders(),
    ])
    connection.url = settings.url
    connection.hasSavedApiKey = settings.hasSavedApiKey
    rootFolders.value = roots
    for (const root of roots) {
      mappingDrafts[root.id] = root.audiobookshelfLibraryId ?? ''
    }
  } catch (reason) {
    error.value = errorMessage(reason)
  }
}

async function saveConnection() {
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const saved = await apiService.saveAudiobookshelfImportSettings({
      url: connection.url,
      apiKey: connection.apiKey || null,
      hasSavedApiKey: connection.hasSavedApiKey,
    })
    connection.url = saved.url
    connection.hasSavedApiKey = saved.hasSavedApiKey
    connection.apiKey = ''
    notice.value = 'Audiobookshelf connection settings saved.'
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    saving.value = false
  }
}

async function testConnection() {
  testing.value = true
  error.value = ''
  notice.value = ''
  try {
    const result = await apiService.testAudiobookshelfConnection()
    notice.value = result.message
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    testing.value = false
  }
}

async function loadLibraries() {
  loadingLibraries.value = true
  error.value = ''
  try {
    libraries.value = await apiService.getAudiobookshelfLibraries()
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    loadingLibraries.value = false
  }
}

async function saveMapping(root: RootFolder) {
  mappingRootId.value = root.id
  error.value = ''
  try {
    const updated = await apiService.updateRootFolderAudiobookshelfMapping(
      root.id,
      mappingDrafts[root.id] || null,
    )
    rootFolders.value = rootFolders.value.map((entry) => (entry.id === root.id ? updated : entry))
    mappingDrafts[root.id] = updated.audiobookshelfLibraryId ?? ''
  } catch (reason) {
    mappingDrafts[root.id] = root.audiobookshelfLibraryId ?? ''
    error.value = errorMessage(reason)
  } finally {
    mappingRootId.value = null
  }
}

async function loadPreview() {
  if (!selectedLibraryId.value) return
  previewing.value = true
  error.value = ''
  try {
    preview.value = await apiService.previewAudiobookshelfImport(selectedLibraryId.value)
    selectedItemIds.value = new Set()
    page.value = 1
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    previewing.value = false
  }
}

function toggleSelection(itemId: string) {
  const next = new Set(selectedItemIds.value)
  if (next.has(itemId)) next.delete(itemId)
  else next.add(itemId)
  selectedItemIds.value = next
}

function selectAllMatched() {
  selectedItemIds.value = new Set(
    (preview.value?.items ?? [])
      .filter((item) => item.status === 'Matched')
      .map((item) => item.itemId),
  )
}

function selectVisibleMatched() {
  const next = new Set(selectedItemIds.value)
  for (const item of pagedItems.value) {
    if (item.status === 'Matched') next.add(item.itemId)
  }
  selectedItemIds.value = next
}

function clearSelection() {
  selectedItemIds.value = new Set()
}

function runExport(kind: 'unmatched' | 'preview' | 'import-results', build: () => string) {
  error.value = ''
  try {
    downloadCsv(exportFileName(kind), build())
  } catch (reason) {
    error.value = `Export failed: ${errorMessage(reason)}`
  }
}

function exportUnmatched() {
  runExport('unmatched', () =>
    buildUnmatchedCsv(
      exportFilteredOnly.value ? filteredItems.value : (preview.value?.items ?? []),
    ),
  )
}

function exportFullPreview() {
  runExport('preview', () =>
    buildFullPreviewCsv(
      exportFilteredOnly.value ? filteredItems.value : (preview.value?.items ?? []),
    ),
  )
}

function exportImportResults() {
  if (importResult.value) {
    const result = importResult.value
    runExport('import-results', () => buildImportResultCsv(result))
  }
}

async function importSelected() {
  const count = selectedItemIds.value.size
  if (!selectedLibraryId.value || count === 0) return
  const noun = count === 1 ? 'audiobook' : 'audiobooks'
  const confirmed = window.confirm(
    `Import ${count} selected ${noun} from Audiobookshelf? Existing files will not be moved, renamed or modified.`,
  )
  if (!confirmed) return
  importing.value = true
  error.value = ''
  try {
    importResult.value = await apiService.importAudiobookshelfItems(selectedLibraryId.value, [
      ...selectedItemIds.value,
    ])
    await loadPreview()
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    importing.value = false
  }
}

async function scanLibrary() {
  if (!selectedLibraryId.value) return
  scanning.value = true
  error.value = ''
  notice.value = ''
  try {
    await apiService.triggerAudiobookshelfLibraryScan(selectedLibraryId.value)
    notice.value = 'Audiobookshelf library scan requested.'
  } catch (reason) {
    error.value = errorMessage(reason)
  } finally {
    scanning.value = false
  }
}

onMounted(loadInitialState)
</script>

<style scoped>
.abs-import {
  display: grid;
  gap: 1rem;
}

.form-section {
  display: grid;
  gap: 0.75rem;
  padding: 1rem;
  border: 1px solid var(--border-color, #333);
  border-radius: 0.75rem;
}

.form-section h2,
.form-section p {
  margin: 0;
}

.connection-fields {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
  gap: 0.75rem;
}

.connection-fields label,
.actions label {
  display: grid;
  gap: 0.35rem;
}

.connection-fields input,
.mapping-row select,
.actions select {
  min-height: 2.5rem;
  padding: 0.5rem;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: end;
  gap: 0.5rem;
}

.mapping-row {
  display: grid;
  grid-template-columns: minmax(10rem, 1fr) minmax(15rem, 2fr) auto;
  align-items: center;
  gap: 0.75rem;
}

.preview-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 1rem;
  font-weight: 600;
}

.preview-table-wrap {
  overflow-x: auto;
}

.preview-table {
  width: 100%;
  border-collapse: collapse;
}

.preview-table th,
.preview-table td {
  padding: 0.55rem;
  border-bottom: 1px solid var(--border-color, #333);
  text-align: left;
}

.preview-table td:nth-child(5) {
  max-width: 24rem;
  overflow-wrap: anywhere;
}

.selection-toolbar {
  position: sticky;
  top: 0;
  z-index: 2;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem;
  background: var(--card-bg, #1e1e1e);
  border: 1px solid var(--border-color, #333);
  border-radius: 0.5rem;
}

.inline-check {
  display: flex !important;
  align-items: center;
  grid-template-columns: none;
}
.muted {
  opacity: 0.75;
}

.error-message {
  color: var(--danger-color, #e66);
}

.success-message {
  color: var(--success-color, #6c6);
}

.result-message {
  overflow-wrap: anywhere;
}

@media (max-width: 640px) {
  .mapping-row {
    grid-template-columns: 1fr;
  }

  .actions {
    align-items: stretch;
  }
}
</style>
