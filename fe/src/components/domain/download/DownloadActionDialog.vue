<template>
  <div
    v-if="open"
    class="dad-overlay"
    role="dialog"
    aria-modal="true"
    :aria-labelledby="titleId"
    @click.self="emitCancel"
    @keydown.esc="emitCancel"
  >
    <div class="dad-card" tabindex="-1" ref="card">
      <h3 :id="titleId">
        {{ variant === 'replace' ? 'Download This Instead' : 'Remove from Queue' }}
      </h3>

      <dl class="dad-info">
        <dt>Audiobook</dt>
        <dd data-test="dad-audiobook">{{ audiobookTitle || '-' }}</dd>
        <dt>{{ variant === 'replace' ? 'Current release' : 'Release' }}</dt>
        <dd data-test="dad-current">{{ currentTitle || '-' }}</dd>
        <dt>Client</dt>
        <dd>{{ clientName || '-' }}</dd>
        <dt>Progress</dt>
        <dd>
          {{ progress.toFixed(1) }}%<span v-if="dataSize"> · {{ dataSize }} on disk</span>
        </dd>
        <template v-if="variant === 'replace'">
          <dt>New release</dt>
          <dd data-test="dad-new">
            {{ newTitle }}<span v-if="newSize"> · {{ newSize }}</span
            ><span v-if="newQuality"> · {{ newQuality }}</span>
          </dd>
        </template>
      </dl>

      <p v-if="missingRemote" class="dad-warning" data-test="dad-missing">
        This item was not found in the download client. Only the Listenarr record can be removed.
      </p>

      <fieldset class="dad-options" :disabled="busy">
        <legend>What should happen to the existing download?</legend>
        <label v-for="opt in options" :key="opt.value" class="dad-option">
          <input v-model="selected" type="radio" name="dad-mode" :value="opt.value" />
          <span>
            <strong>{{ opt.label }}</strong>
            <small>{{ opt.help }}</small>
          </span>
        </label>
      </fieldset>

      <p v-if="error" class="dad-error" role="alert" data-test="dad-error">{{ error }}</p>

      <div class="dad-actions">
        <button type="button" class="btn btn-secondary" :disabled="busy" @click="emitCancel">
          Cancel
        </button>
        <button
          type="button"
          class="btn btn-danger"
          data-test="dad-confirm"
          :disabled="busy"
          @click="emit('confirm', selected)"
        >
          {{ busy ? 'Working...' : variant === 'replace' ? 'Replace' : 'Remove' }}
        </button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { DownloadRemovalMode } from '@/types'

const props = withDefaults(
  defineProps<{
    open: boolean
    variant: 'remove' | 'replace'
    audiobookTitle?: string
    currentTitle?: string
    clientName?: string
    progress?: number
    dataSize?: string
    newTitle?: string
    newSize?: string
    newQuality?: string
    missingRemote?: boolean
    busy?: boolean
    error?: string
  }>(),
  { progress: 0, missingRemote: false, busy: false },
)

const emit = defineEmits<{
  (e: 'confirm', mode: DownloadRemovalMode): void
  (e: 'cancel'): void
}>()

const titleId = `dad-title-${Math.random().toString(36).slice(2, 8)}`
const card = ref<HTMLElement | null>(null)
const selected = ref<DownloadRemovalMode>('clientKeepFiles')

const options = computed<{ value: DownloadRemovalMode; label: string; help: string }[]>(() => {
  const listenarrOnly = {
    value: 'listenarrOnly' as const,
    label: 'Listenarr only',
    help: 'Remove the Listenarr record. The download client is not contacted.',
  }
  if (props.missingRemote) return [listenarrOnly]
  return [
    {
      value: 'clientKeepFiles' as const,
      label: 'Remove from client and Listenarr, keep files',
      help: 'Partial data stays on disk.',
    },
    {
      value: 'clientDeleteData' as const,
      label: 'Remove from client and Listenarr, delete partial data',
      help: 'Asks the client to delete incomplete data. Never touches your library.',
    },
    listenarrOnly,
  ]
})

watch(
  () => [props.open, props.missingRemote],
  async () => {
    if (!props.open) return
    selected.value = props.missingRemote ? 'listenarrOnly' : 'clientKeepFiles'
    await nextTick()
    card.value?.focus()
  },
  { immediate: true },
)

function emitCancel() {
  if (!props.busy) emit('cancel')
}
</script>

<style scoped>
.dad-overlay {
  position: fixed;
  inset: 0;
  z-index: 3000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.6);
}
.dad-card {
  width: min(560px, 94vw);
  max-height: 90vh;
  overflow: auto;
  padding: 1.25rem;
  border-radius: 8px;
  background: #2a2a2a;
  color: #e9ecef;
  outline: none;
}
.dad-info {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.25rem 1rem;
  margin: 1rem 0;
}
.dad-info dt {
  color: #adb5bd;
}
.dad-info dd {
  margin: 0;
  word-break: break-word;
}
.dad-options {
  border: 1px solid #444;
  border-radius: 6px;
  padding: 0.75rem;
}
.dad-option {
  display: flex;
  gap: 0.5rem;
  padding: 0.35rem 0;
  cursor: pointer;
}
.dad-option small {
  display: block;
  color: #adb5bd;
}
.dad-warning {
  color: #ffc107;
}
.dad-error {
  color: #ff6b6b;
  margin-top: 0.75rem;
}
.dad-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1rem;
}
</style>
