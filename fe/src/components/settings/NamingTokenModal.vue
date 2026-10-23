<template>
  <div class="modal-overlay" @click.self="$emit('close')">
    <div class="modal-content" role="dialog" aria-label="File Name Tokens">
      <div class="modal-header">
        <h2>File Name Tokens</h2>
        <button class="close-btn" title="Close" @click="$emit('close')"><PhX :size="24" /></button>
      </div>

      <div class="modal-body">
        <div class="style-row">
          <label>
            Separator
            <select v-model="space" data-testid="token-space">
              <option value="space">Space ( )</option>
              <option value="period">Period (.)</option>
              <option value="underscore">Underscore (_)</option>
              <option value="dash">Dash (-)</option>
            </select>
          </label>
          <label>
            Case
            <select v-model="caseStyle" data-testid="token-case">
              <option value="default">Default Case</option>
              <option value="lower">Lower Case</option>
              <option value="upper">Upper Case</option>
            </select>
          </label>
        </div>

        <p class="hint">
          Put separators <em>inside</em> the braces, like <code>{ - Narrator}</code>, and they only
          appear when the value exists. Click any token to add it to the pattern below.
        </p>

        <h3>Examples</h3>
        <table class="token-table">
          <thead>
            <tr><th>Pattern</th><th>Generic</th><th>The Gunslinger</th></tr>
          </thead>
          <tbody>
            <tr v-for="row in NAMING_EXAMPLES" :key="row.token">
              <td><button class="token-btn" @click="setDraft(row.token)">{{ row.token }}</button></td>
              <td>{{ row.generic }}</td>
              <td>{{ row.example }}</td>
            </tr>
          </tbody>
        </table>

        <template v-for="category in NAMING_TOKEN_CATEGORIES" :key="category.name">
          <h3>{{ category.name }}</h3>
          <table class="token-table">
            <tbody>
              <tr v-for="row in category.tokens" :key="row.token">
                <td>
                  <button class="token-btn" data-testid="token-insert" @click="insert(row.token)">
                    {{ styled(row.token) }}
                  </button>
                </td>
                <td>{{ row.generic }}</td>
                <td>{{ row.example || 'Empty' }}</td>
              </tr>
            </tbody>
          </table>
        </template>
        <p class="hint">
          Tokens that show "Empty" for the sample book are only filled in when the book's metadata or
          release provides a value; unfilled ones are omitted from the name.
        </p>
      </div>

      <div class="builder">
        <input
          ref="inputEl"
          v-model="draft"
          type="text"
          class="builder-input"
          placeholder="Build your pattern as you go"
          data-testid="token-draft"
        />
        <div class="builder-preview">
          <span>Preview:</span> <code data-testid="token-preview">{{ preview }}</code>
        </div>
        <div class="builder-actions">
          <button class="btn-secondary" @click="$emit('close')">Cancel</button>
          <button class="btn-primary" data-testid="token-apply" @click="$emit('apply', draft)">
            Use Pattern
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { PhX } from '@phosphor-icons/vue'
import {
  NAMING_EXAMPLES,
  NAMING_TOKEN_CATEGORIES,
  renderNamingPattern,
  styleToken,
  type CaseStyle,
  type SpaceStyle,
} from '@/utils/namingTokens'

const props = defineProps<{ initial: string; multiFile?: boolean }>()
defineEmits<{ close: []; apply: [value: string] }>()

const space = ref<SpaceStyle>('space')
const caseStyle = ref<CaseStyle>('default')
const draft = ref(props.initial)
const inputEl = ref<HTMLInputElement | null>(null)

const styled = (token: string) => styleToken(token, space.value, caseStyle.value)

const preview = computed(() => {
  if (!props.multiFile) return renderNamingPattern(draft.value)
  return [1, 2, 3].map((n) => renderNamingPattern(draft.value, n)).join(', ') + '...'
})

function setDraft(value: string) {
  draft.value = value
}

async function insert(token: string) {
  const text = styled(token)
  const el = inputEl.value
  const start = el?.selectionStart ?? draft.value.length
  const end = el?.selectionEnd ?? draft.value.length
  draft.value = draft.value.slice(0, start) + text + draft.value.slice(end)
  await nextTick()
  el?.focus()
  el?.setSelectionRange(start + text.length, start + text.length)
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}
.modal-content {
  background: #2a2a2a;
  border-radius: 8px;
  width: min(900px, 94vw);
  max-height: 90vh;
  display: flex;
  flex-direction: column;
}
.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem 1.5rem;
  border-bottom: 1px solid #444;
}
.modal-header h2 {
  margin: 0;
  font-size: 1.25rem;
}
.close-btn {
  background: none;
  border: none;
  color: #ccc;
  cursor: pointer;
}
.modal-body {
  padding: 1rem 1.5rem;
  overflow-y: auto;
  flex: 1;
}
.style-row {
  display: flex;
  gap: 1rem;
  margin-bottom: 0.75rem;
}
.style-row label {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.85rem;
}
.hint {
  color: #aaa;
  font-size: 0.85rem;
}
h3 {
  margin: 1rem 0 0.4rem;
  font-size: 1rem;
}
.token-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}
.token-table th,
.token-table td {
  text-align: left;
  padding: 0.3rem 0.5rem;
  border-bottom: 1px solid #3a3a3a;
}
.token-btn {
  background: #1f3b57;
  color: #fff;
  border: 1px solid #2196f3;
  border-radius: 4px;
  padding: 0.15rem 0.5rem;
  font-family: monospace;
  cursor: pointer;
  text-align: left;
}
.token-btn:hover {
  background: #2196f3;
}
.builder {
  border-top: 1px solid #444;
  padding: 0.75rem 1.5rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.builder-input {
  width: 100%;
  font-family: monospace;
}
.builder-preview {
  font-size: 0.85rem;
  color: #aaa;
}
.builder-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}
</style>
