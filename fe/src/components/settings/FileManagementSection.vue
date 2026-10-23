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
  <div class="form-section">
    <h3><PhFolder /> File Management</h3>
    <div class="form-body">
      <FormRow label="Folder Naming Pattern" help="Pattern for organizing audiobook folders">
        <div class="input-group">
          <input
            v-model="folderPattern"
            type="text"
            placeholder="{Author}/{Series}/{Title}"
            @change="
              (e) => updateField('folderNamingPattern', (e.target as HTMLInputElement).value)
            "
          />
          <button class="help-button" @click="openModal('folder')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="folderPattern">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(folderPattern) }}</code>
        </div>
      </FormRow>

      <FormRow
        label="Single File Naming Pattern"
        help="Pattern for naming audiobooks as single files"
      >
        <div class="input-group">
          <input
            v-model="filePatternSingleFile"
            type="text"
            placeholder="{Title}"
            @change="(e) => updateField('fileNamingPattern', (e.target as HTMLInputElement).value)"
          />
          <button class="help-button" @click="openModal('single')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="filePatternSingleFile">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(filePatternSingleFile, 'file') }}.ext</code>
        </div>
      </FormRow>

      <FormRow
        label="Multi-File Naming Pattern"
        help="Pattern for naming audiobooks split across multiple files"
      >
        <div class="input-group">
          <input
            v-model="filePatternMultiFile"
            type="text"
            placeholder="{Title}-{DiskNumber:00}"
            @change="
              (e) => updateField('multiFileNamingPattern', (e.target as HTMLInputElement).value)
            "
          />
          <button class="help-button" @click="openModal('multi')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="filePatternMultiFile">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(filePatternMultiFile, 'file', true) }}</code>
        </div>
      </FormRow>

      <!-- Path length warning -->
      <div v-if="pathLengthWarning" class="path-length-warning">
        <PhWarning :size="18" />
        <div class="warning-content">
          <strong>Path may exceed Windows limit (260 characters)</strong>
          <p>{{ pathLengthWarning }}</p>
        </div>
      </div>
      <div v-else-if="estimatedPathLength > 0" class="path-length-ok">
        <span>Estimated sample path length: {{ estimatedPathLength }} / 259 characters</span>
      </div>

      <FormRow
        label="Completed File Action"
        help="Choose whether completed downloads should be moved into the library output path or copied and left in the client's folder."
      >
        <select
          :value="settings.completedFileAction"
          @change="(e) => updateField('completedFileAction', (e.target as HTMLSelectElement).value)"
        >
          <option value="move">Move</option>
          <option value="copy">Copy</option>
          <option value="hardlink/copy">Hardlink/Copy</option>
        </select>
      </FormRow>

      <FormRow
        label="Import Blacklist Extensions"
        help="Extensions to skip during library import and completed-download import. Separate values with commas or new lines, for example: .nfo, .sfv, .jpg"
      >
        <textarea
          class="blacklist-textarea"
          :value="formatExtensionList(settings.importBlacklistExtensions)"
          placeholder=".nfo&#10;.sfv&#10;.jpg"
          rows="4"
          @change="
            (e) =>
              updateField(
                'importBlacklistExtensions',
                parseExtensionList((e.target as HTMLTextAreaElement).value),
              )
          "
        />
      </FormRow>
    </div>

    <NamingTokenModal
      v-if="showModal"
      :key="activePatternType"
      :initial="activePatternValue"
      :multi-file="activePatternType === 'multi'"
      @close="closeModal"
      @apply="applyFromModal"
    />
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { ref, computed } from 'vue'
import { PhFolder, PhQuestion, PhWarning } from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'
import NamingTokenModal from '@/components/settings/NamingTokenModal.vue'
import { renderNamingPattern } from '@/utils/namingTokens'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

type PatternTarget = 'folder' | 'single' | 'multi'

const showModal = ref(false)
const activePatternType = ref<PatternTarget>('folder')
const folderPattern = ref(props.settings.folderNamingPattern || '')
const filePatternSingleFile = ref(props.settings.fileNamingPattern || '')
const filePatternMultiFile = ref(props.settings.multiFileNamingPattern || '{Title}-{DiskNumber:00}')

const activePatternValue = computed(() =>
  activePatternType.value === 'folder'
    ? folderPattern.value
    : activePatternType.value === 'single'
      ? filePatternSingleFile.value
      : filePatternMultiFile.value,
)

const PATTERN_FIELDS = {
  folder: 'folderNamingPattern',
  single: 'fileNamingPattern',
  multi: 'multiFileNamingPattern',
} as const

function applyFromModal(value: string) {
  if (activePatternType.value === 'folder') folderPattern.value = value
  else if (activePatternType.value === 'single') filePatternSingleFile.value = value
  else filePatternMultiFile.value = value
  updateField(PATTERN_FIELDS[activePatternType.value], value)
  showModal.value = false
}

function applyPattern(
  pattern: string,
  type: 'folder' | 'file' = 'folder',
  multiFile: boolean = false,
): string {
  if (!pattern) return ''

  if (type === 'file' && multiFile) {
    if (!/\{[^{}]*(DiskNumber|ChapterNumber|PartNumber)/i.test(pattern)) {
      return `⚠️ ${renderNamingPattern(pattern)}.ext (all files would have the same name!)`
    }
    return [1, 2, 3].map((n) => `${renderNamingPattern(pattern, n)}.ext`).join(', ') + '...'
  }

  return renderNamingPattern(pattern)
}

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function parseExtensionList(value: string): string[] {
  return value
    .split(/[\r\n,;]+/)
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => (entry.startsWith('.') ? entry : `.${entry}`))
}

function formatExtensionList(value: string[] | undefined): string {
  return Array.isArray(value) ? value.join('\n') : ''
}

// --- Path length estimation ---
const WINDOWS_MAX_PATH = 259

/** Build a sample full path from the current patterns and output path to estimate length. */
const estimatedPathLength = computed(() => {
  const outputPath = (props.settings.outputPath || 'D:\\Audiobooks').replace(/[\/\\]+$/, '')
  const folder = folderPattern.value ? applyPattern(folderPattern.value) : ''
  const file = filePatternSingleFile.value
    ? applyPattern(filePatternSingleFile.value, 'file') + '.m4b'
    : 'Unknown Title.m4b'

  const parts = [outputPath, folder, file].filter(Boolean)
  const fullPath = parts.join('\\')
  return fullPath.length
})

const pathLengthWarning = computed<string | null>(() => {
  const len = estimatedPathLength.value
  if (len <= WINDOWS_MAX_PATH) return null

  const excess = len - WINDOWS_MAX_PATH
  return (
    `With the sample values, the generated path is ${len} characters — ${excess} over the Windows MAX_PATH limit. ` +
    'Long author names, series, or titles will be automatically truncated by the server, but shorter patterns are recommended to avoid surprises.'
  )
})

function openModal(type: PatternTarget) {
  activePatternType.value = type
  showModal.value = true
}

function closeModal() {
  showModal.value = false
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0.5rem 0;
  font-size: 1.2rem;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 0.625rem;
  color: #fff;
  letter-spacing: 0.01em;
}

h3 svg {
  color: var(--brand-500);
  filter: drop-shadow(0 0 8px rgba(33, 150, 243, 0.3));
}

.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.blacklist-textarea {
  width: 100%;
  min-height: 7rem;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  font-family: inherit;
  resize: vertical;
}

.blacklist-textarea:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.08);
}

.form-group {
  margin-bottom: 1.25rem;
}
.form-group:last-child {
  margin-bottom: 0;
}

.form-group label {
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}

.input-group {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}

.input-group input[type='text'] {
  flex: 1;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.12s;
}

.input-group input[type='text']::placeholder {
  color: #6c757d;
}

.input-group input[type='text']:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.help-button {
  padding: 0.6rem 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #adb5bd;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
  flex-shrink: 0;
}

.help-button:hover {
  border-color: #666;
  color: #e0e0e0;
  background-color: rgba(255, 255, 255, 0.05);
}

.help-button:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.form-group select {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.12s;
}

.form-group select:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.pattern-preview {
  margin-top: 0.75rem;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border-left: 3px solid var(--brand-500);
  border-radius: 4px;
  font-size: 0.9rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.pattern-preview .preview-label {
  color: #adb5bd;
  font-weight: 500;
  white-space: nowrap;
}

.pattern-preview code {
  color: #4dd0e1;
  font-family: 'Courier New', monospace;
  flex: 1;
  word-break: break-all;
}

/* Path length feedback */
.path-length-warning {
  display: flex;
  align-items: flex-start;
  gap: 0.625rem;
  margin-top: 1rem;
  padding: 0.875rem 1rem;
  background-color: rgba(255, 152, 0, 0.08);
  border: 1px solid rgba(255, 152, 0, 0.35);
  border-radius: 6px;
  color: #ffb74d;
  font-size: 0.875rem;
  line-height: 1.5;
}

.path-length-warning svg {
  flex-shrink: 0;
  margin-top: 0.125rem;
}

.path-length-warning .warning-content {
  flex: 1;
}

.path-length-warning .warning-content strong {
  display: block;
  margin-bottom: 0.25rem;
  color: #ffa726;
}

.path-length-warning .warning-content p {
  margin: 0;
  color: #ffcc80;
}

.path-length-ok {
  margin-top: 0.75rem;
  font-size: 0.8rem;
  color: #6c757d;
}
</style>
