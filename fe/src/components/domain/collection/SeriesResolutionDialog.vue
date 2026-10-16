<template>
  <Teleport to="body">
    <div v-if="modelValue" class="series-resolution-backdrop" @click.self="close">
      <section
        class="series-resolution-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="series-resolution-title"
      >
        <header class="dialog-header">
          <div>
            <h2 id="series-resolution-title">Resolve Series Metadata</h2>
            <p>Review Audible matches. No memberships change until you confirm a merge.</p>
          </div>
          <button class="close-button" type="button" aria-label="Close" @click="close">×</button>
        </header>

        <div class="dialog-actions">
          <button type="button" :disabled="loading || merging" @click="loadPreviews">
            {{ loading ? 'Scanning Audible…' : 'Scan unresolved series' }}
          </button>
          <button
            type="button"
            :disabled="loading || merging || !previews.length"
            @click="selectHighConfidence"
          >
            Select high-confidence matches
          </button>
          <button
            type="button"
            :disabled="!previews.length || loading || merging"
            @click="exportResults"
          >
            Export results
          </button>
        </div>

        <p v-if="errorMessage" class="resolution-error" role="alert">{{ errorMessage }}</p>
        <p v-if="noticeMessage" class="resolution-notice" role="status">{{ noticeMessage }}</p>
        <p v-if="loading" class="resolution-loading">
          Checking imported book ASINs against Audible…
        </p>
        <p v-else-if="!previews.length" class="resolution-empty">
          No unresolved imported series were found.
        </p>

        <div v-else class="resolution-list">
          <article
            v-for="preview in previews"
            :key="preview.seriesId"
            class="resolution-card"
            :class="{ ambiguous: preview.isAmbiguous }"
          >
            <div class="card-heading">
              <div>
                <h3>{{ preview.importedSeriesName }}</h3>
                <code>{{ preview.seriesId }}</code>
              </div>
              <span class="resolution-status">
                {{
                  preview.isAmbiguous
                    ? 'Ambiguous'
                    : preview.candidates.length
                      ? 'Needs review'
                      : 'No match'
                }}
              </span>
            </div>

            <p class="book-counts">
              {{ preview.importedBookCount }} imported book(s)
              <span v-if="selectedCandidate(preview)">
                · {{ selectedCandidate(preview)?.existingLibraryBookCount }} already in canonical
                series · {{ selectedCandidate(preview)?.proposedMergedBookCount }} after merge
              </span>
            </p>

            <div v-if="preview.candidates.length" class="candidate-picker">
              <label :for="`candidate-${preview.seriesId}`">Audible catalog match</label>
              <select
                :id="`candidate-${preview.seriesId}`"
                v-model="selectedCandidates[preview.seriesId]"
              >
                <option value="">Choose a candidate…</option>
                <option
                  v-for="candidate in preview.candidates"
                  :key="candidate.asin"
                  :value="candidate.asin"
                >
                  {{ candidate.name }} · {{ candidate.asin }} · {{ candidate.confidence }} ({{
                    candidate.score
                  }}%)
                </option>
              </select>
              <button
                v-if="selectedCandidate(preview)"
                type="button"
                class="catalog-refresh-button"
                :disabled="refreshingAsin === selectedCandidate(preview)?.asin || merging"
                @click="refreshCandidateCatalog(preview)"
              >
                {{
                  refreshingAsin === selectedCandidate(preview)?.asin
                    ? 'Refreshing…'
                    : 'Refresh selected catalog'
                }}
              </button>
              <ul v-if="selectedCandidate(preview)" class="evidence-list">
                <li v-for="evidence in selectedCandidate(preview)?.evidence" :key="evidence">
                  {{ evidence }}
                </li>
              </ul>
            </div>
            <p v-else class="no-candidate">
              No matching Audible series was found. The imported series is unchanged.
            </p>

            <div
              v-if="preview.conflicts.length || selectedCandidate(preview)?.conflicts.length"
              class="conflict-list"
            >
              <strong>Review conflicts</strong>
              <ul>
                <li v-for="conflict in allConflicts(preview)" :key="conflict">{{ conflict }}</li>
              </ul>
            </div>

            <details v-if="preview.booksWithoutAsin.length || preview.unresolvedBooks.length">
              <summary>
                Books that could not be resolved ({{
                  preview.booksWithoutAsin.length + preview.unresolvedBooks.length
                }})
              </summary>
              <ul>
                <li v-for="book in preview.booksWithoutAsin" :key="`asin-${book}`">
                  {{ book }} — no ASIN
                </li>
                <li v-for="book in preview.unresolvedBooks" :key="`lookup-${book}`">
                  {{ book }} — no matching Audible membership
                </li>
              </ul>
            </details>
          </article>
        </div>

        <footer class="dialog-footer">
          <span>{{ selectedCount }} series selected</span>
          <button
            type="button"
            class="merge-button"
            :disabled="selectedCount === 0 || loading || merging"
            @click="confirming = true"
          >
            Merge selected
          </button>
          <button type="button" @click="close">Close</button>
        </footer>

        <div v-if="confirming" class="confirm-panel" role="alertdialog" aria-modal="true">
          <p>
            Confirm merging {{ selectedCount }} selected series into their explicitly chosen Audible
            catalogs? Audiobook records and files will not be changed.
          </p>
          <button type="button" class="merge-button" :disabled="merging" @click="mergeSelected">
            {{ merging ? 'Merging…' : 'Confirm merge' }}
          </button>
          <button type="button" :disabled="merging" @click="confirming = false">Cancel</button>
        </div>
      </section>
    </div>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { apiService } from '@/services/api'
import type {
  SeriesResolutionCandidate,
  SeriesResolutionMergeResult,
  SeriesResolutionPreview,
} from '@/types'

const props = withDefaults(
  defineProps<{
    modelValue: boolean
    seriesId?: string
    region?: string
  }>(),
  {
    seriesId: undefined,
    region: 'us',
  },
)

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  merged: [results: SeriesResolutionMergeResult[]]
}>()

const previews = ref<SeriesResolutionPreview[]>([])
const selectedCandidates = reactive<Record<string, string>>({})
const loading = ref(false)
const merging = ref(false)
const confirming = ref(false)
const refreshingAsin = ref<string | null>(null)
const errorMessage = ref('')
const noticeMessage = ref('')
const selectedCount = computed(() => Object.values(selectedCandidates).filter(Boolean).length)

watch(
  () => [props.modelValue, props.seriesId, props.region] as const,
  ([visible]) => {
    if (visible) void loadPreviews()
  },
  { immediate: true },
)

async function loadPreviews() {
  loading.value = true
  errorMessage.value = ''
  noticeMessage.value = ''
  confirming.value = false
  try {
    previews.value = await apiService.previewSeriesResolution(props.region, props.seriesId)
    for (const key of Object.keys(selectedCandidates)) delete selectedCandidates[key]
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Unable to scan imported series.'
  } finally {
    loading.value = false
  }
}

function selectedCandidate(
  preview: SeriesResolutionPreview,
): SeriesResolutionCandidate | undefined {
  return preview.candidates.find(
    (candidate) => candidate.asin === selectedCandidates[preview.seriesId],
  )
}

function allConflicts(preview: SeriesResolutionPreview): string[] {
  return [...new Set([...preview.conflicts, ...(selectedCandidate(preview)?.conflicts ?? [])])]
}

function selectHighConfidence() {
  for (const key of Object.keys(selectedCandidates)) delete selectedCandidates[key]
  for (const preview of previews.value) {
    if (preview.isAmbiguous) continue
    const highConfidence = preview.candidates.filter(
      (candidate) => candidate.confidence === 'high' && candidate.conflicts.length === 0,
    )
    if (highConfidence.length === 1) {
      selectedCandidates[preview.seriesId] = highConfidence[0]!.asin
    }
  }
  noticeMessage.value = `${selectedCount.value} unambiguous high-confidence match(es) selected for your review.`
}

async function refreshCandidateCatalog(preview: SeriesResolutionPreview) {
  const candidate = selectedCandidate(preview)
  if (!candidate) return
  refreshingAsin.value = candidate.asin
  errorMessage.value = ''
  noticeMessage.value = ''
  try {
    const catalog = await apiService.refreshSeriesCatalogByAsin({
      asin: candidate.asin,
      name: candidate.name,
      region: props.region,
    })
    noticeMessage.value = `Refreshed ${catalog.totalBooks} catalog book(s) for ${catalog.series.name}.`
  } catch (error) {
    errorMessage.value =
      error instanceof Error ? error.message : 'Unable to refresh the selected catalog.'
  } finally {
    refreshingAsin.value = null
  }
}

async function mergeSelected() {
  const selected = previews.value.flatMap((preview) => {
    const candidate = selectedCandidate(preview)
    return candidate ? [{ preview, candidate }] : []
  })
  if (!selected.length) return

  merging.value = true
  confirming.value = false
  errorMessage.value = ''
  noticeMessage.value = ''
  const results: SeriesResolutionMergeResult[] = []
  const failures: string[] = []
  try {
    for (const { preview, candidate } of selected) {
      try {
        results.push(
          await apiService.mergeSeriesResolution({
            seriesId: preview.seriesId,
            candidateAsin: candidate.asin,
            region: props.region,
          }),
        )
      } catch (error) {
        failures.push(
          `${preview.importedSeriesName}: ${error instanceof Error ? error.message : 'Merge failed.'}`,
        )
      }
    }

    if (results.length) emit('merged', results)
    await loadPreviews()
    noticeMessage.value = `${results.length} series merged; ${failures.length} failed.`
    if (failures.length) errorMessage.value = failures.join(' ')
  } finally {
    merging.value = false
  }
}

function exportResults() {
  const blob = new Blob([JSON.stringify(previews.value, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = 'listenarr-series-resolution-preview.json'
  anchor.click()
  URL.revokeObjectURL(url)
}

function close() {
  if (merging.value) return
  confirming.value = false
  emit('update:modelValue', false)
}
</script>

<style scoped>
.series-resolution-backdrop {
  position: fixed;
  z-index: 1200;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 1rem;
  background: rgb(0 0 0 / 68%);
}

.series-resolution-dialog {
  position: relative;
  display: flex;
  width: min(900px, 100%);
  max-height: min(90vh, 900px);
  flex-direction: column;
  gap: 1rem;
  overflow: hidden;
  padding: 1.25rem;
  border: 1px solid var(--border-color, #3f4652);
  border-radius: 12px;
  background: var(--surface-color, #171a20);
  color: var(--text-color, #f2f4f8);
  box-shadow: 0 24px 80px rgb(0 0 0 / 45%);
}

.dialog-header,
.dialog-actions,
.dialog-footer,
.card-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.dialog-header h2,
.card-heading h3,
.dialog-header p {
  margin: 0;
}

.dialog-header p,
.book-counts,
.resolution-empty,
.resolution-loading,
.no-candidate {
  color: var(--text-muted, #a8b0be);
}

.close-button {
  font-size: 1.6rem;
}

.dialog-actions,
.dialog-footer {
  flex-wrap: wrap;
}

.resolution-list {
  display: grid;
  gap: 0.75rem;
  overflow: auto;
  padding-right: 0.25rem;
}

.resolution-card {
  display: grid;
  gap: 0.75rem;
  padding: 1rem;
  border: 1px solid var(--border-color, #3f4652);
  border-radius: 8px;
}

.resolution-card.ambiguous {
  border-color: var(--warning-color, #d69a27);
}

.card-heading code {
  color: var(--text-muted, #a8b0be);
}

.resolution-status {
  font-size: 0.85rem;
  font-weight: 600;
}

.candidate-picker {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 0.5rem;
}

.candidate-picker label,
.candidate-picker select,
.evidence-list {
  grid-column: 1 / -1;
}

.candidate-picker select {
  min-width: 0;
  padding: 0.55rem;
}

.evidence-list,
.conflict-list ul,
.resolution-card details ul {
  margin: 0;
  padding-left: 1.25rem;
}

.conflict-list,
.resolution-error {
  color: var(--danger-color, #ff8a80);
}

.resolution-notice {
  color: var(--success-color, #8bd5a5);
}

.merge-button {
  background: var(--primary-color, #2196f3);
  color: white;
}

.confirm-panel {
  position: absolute;
  inset: auto 1rem 1rem;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem;
  border: 1px solid var(--warning-color, #d69a27);
  border-radius: 8px;
  background: var(--surface-color, #171a20);
  box-shadow: 0 8px 36px rgb(0 0 0 / 50%);
}

.confirm-panel p {
  flex-basis: 100%;
  margin: 0;
}

.dialog-actions button,
.dialog-footer button,
.close-button,
.catalog-refresh-button,
.confirm-panel button {
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--border-color, #3f4652);
  border-radius: 6px;
  background: var(--surface-raised, #242a33);
  color: inherit;
  cursor: pointer;
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

@media (max-width: 640px) {
  .series-resolution-dialog {
    max-height: 94vh;
    padding: 0.9rem;
  }

  .dialog-header,
  .card-heading {
    align-items: flex-start;
  }

  .candidate-picker {
    grid-template-columns: 1fr;
  }
}
</style>
