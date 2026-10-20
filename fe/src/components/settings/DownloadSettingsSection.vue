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
    <h3><PhDownload /> Download Settings</h3>
    <div class="form-body">
      <FormRow
        label="Max Concurrent Downloads"
        help="Maximum number of simultaneous downloads (1-10)"
      >
        <input
          :value="settings.maxConcurrentDownloads"
          @input="
            (e) =>
              updateField(
                'maxConcurrentDownloads',
                Number((e.target as HTMLInputElement).value || 1),
              )
          "
          type="number"
          min="1"
          max="10"
        />
      </FormRow>

      <FormRow
        label="Unmatched Scan Concurrency"
        help="Number of concurrent ffprobe processes during an unmatched scan (1-8). Lower values reduce NAS/disk pressure; higher values speed up large libraries."
      >
        <input
          :value="settings.unmatchedScanConcurrency ?? 2"
          @input="
            (e) =>
              updateField(
                'unmatchedScanConcurrency',
                Number((e.target as HTMLInputElement).value || 2),
              )
          "
          type="number"
          min="1"
          max="8"
        />
      </FormRow>

      <FormRow
        label="Polling Interval (seconds)"
        help="How often to check download status (10-300 seconds)"
      >
        <input
          :value="settings.pollingIntervalSeconds"
          @input="
            (e) =>
              updateField(
                'pollingIntervalSeconds',
                Number((e.target as HTMLInputElement).value || 10),
              )
          "
          type="number"
          min="10"
          max="300"
        />
      </FormRow>

      <FormRow
        label="Automatic Search Interval (hours)"
        help="Minimum time between automatic searches for a monitored audiobook, including retries after temporary search failures (1-168 hours)."
      >
        <input
          :value="settings.automaticSearchIntervalHours ?? 6"
          @input="
            (e) =>
              updateField(
                'automaticSearchIntervalHours',
                Number((e.target as HTMLInputElement).value || 6),
              )
          "
          type="number"
          min="1"
          max="168"
        />
      </FormRow>

      <FormRow
        label="Download Completion Stability (seconds)"
        help="How long (seconds) a download must be seen as complete on the client before finalization begins. Increase for clients that post-process/extract after completion."
      >
        <input
          :value="settings.downloadCompletionStabilitySeconds"
          @input="
            (e) =>
              updateField(
                'downloadCompletionStabilitySeconds',
                Number((e.target as HTMLInputElement).value || 1),
              )
          "
          type="number"
          min="1"
          max="600"
        />
      </FormRow>

      <h4 class="stalled-heading">Completed Download Handling</h4>
      <CheckboxCard
        :modelValue="settings.enableCompletedDownloadReconciliation ?? true"
        @update:modelValue="
          (value: boolean) => updateField('enableCompletedDownloadReconciliation', value)
        "
        title="Reconcile missed download completions"
        description="Check tracked downloads against their configured client download directory when a completion event may have been missed."
      />
      <FormRow
        label="Reconciliation Interval (seconds)"
        help="How often to check tracked downloads that disappeared from a client."
      >
        <input
          :value="settings.completedDownloadReconciliationIntervalSeconds ?? 60"
          @input="
            (e) =>
              updateField(
                'completedDownloadReconciliationIntervalSeconds',
                Number((e.target as HTMLInputElement).value || 60),
              )
          "
          type="number"
          min="15"
          max="3600"
        />
      </FormRow>
      <FormRow
        label="Minimum File Stability (seconds)"
        help="Require all candidate audio files to remain unchanged for this long before recovery."
      >
        <input
          :value="settings.completedDownloadMinimumStabilitySeconds ?? 60"
          @input="
            (e) =>
              updateField(
                'completedDownloadMinimumStabilitySeconds',
                Number((e.target as HTMLInputElement).value || 60),
              )
          "
          type="number"
          min="15"
          max="3600"
        />
      </FormRow>
      <CheckboxCard
        :modelValue="settings.automaticImportReconciledDownloads ?? true"
        @update:modelValue="
          (value: boolean) => updateField('automaticImportReconciledDownloads', value)
        "
        :disabled="!(settings.enableCompletedDownloadReconciliation ?? true)"
        title="Automatically import confidently matched recoveries"
        description="Only recovered files with completion evidence and a strong audiobook identity match are queued for the existing import pipeline."
      />

      <FormRow
        label="Missing-source Retry Initial Delay (seconds)"
        help="Initial retry delay (seconds) used when files are not yet available at finalization time."
      >
        <input
          :value="settings.missingSourceRetryInitialDelaySeconds"
          @input="
            (e) =>
              updateField(
                'missingSourceRetryInitialDelaySeconds',
                Number((e.target as HTMLInputElement).value || 1),
              )
          "
          type="number"
          min="1"
          max="600"
        />
      </FormRow>

      <FormRow
        label="Missing-source Max Retries"
        help="Maximum number of retries to attempt if the finalized download's source files are missing."
      >
        <input
          :value="settings.missingSourceMaxRetries"
          @input="
            (e) =>
              updateField(
                'missingSourceMaxRetries',
                Number((e.target as HTMLInputElement).value || 0),
              )
          "
          type="number"
          min="0"
          max="20"
        />
      </FormRow>

      <CheckboxCard
        :modelValue="settings.failedDownloadHandlingEnabled"
        @update:modelValue="updateFailedDownloadHandlingEnabled"
        title="Enable Failed Download Handling"
        description="When a download fails, mark it as failed, remove it from the client, and record history."
      />

      <CheckboxCard
        :modelValue="settings.failedDownloadAutoSearch"
        @update:modelValue="updateFailedDownloadAutoSearch"
        :disabled="!settings.failedDownloadHandlingEnabled"
        title="Auto-search on Failed Downloads"
        description="Automatically search for a replacement when a download fails (requires failed download handling)."
      />

      <h4 class="stalled-heading">Stalled Downloads</h4>
      <CheckboxCard
        :modelValue="settings.stalledHandlingEnabled ?? true"
        @update:modelValue="(v: boolean) => updateField('stalledHandlingEnabled', v)"
        title="Detect Stalled Downloads"
        description="A download is stalled when its downloaded bytes have not increased for the timeout. Zero speed alone is never treated as a stall. Usenet repair/unpack is never stalled."
      />
      <FormRow
        label="Stalled Timeout (minutes)"
        help="Minutes without byte progress before a download is stalled."
      >
        <input
          :value="settings.stalledTimeoutMinutes ?? 60"
          @input="
            (e) =>
              updateField(
                'stalledTimeoutMinutes',
                Number((e.target as HTMLInputElement).value || 60),
              )
          "
          type="number"
          min="5"
          max="10080"
        />
      </FormRow>
      <FormRow
        label="Metadata Timeout (minutes)"
        help="Minutes a torrent may stay on 'Retrieving metadata'. Torrent clients only."
      >
        <input
          :value="settings.stalledMetadataTimeoutMinutes ?? 15"
          @input="
            (e) =>
              updateField(
                'stalledMetadataTimeoutMinutes',
                Number((e.target as HTMLInputElement).value || 15),
              )
          "
          type="number"
          min="1"
          max="1440"
        />
      </FormRow>
      <FormRow
        label="Max Replacement Attempts"
        help="Automatic replacements per audiobook before giving up."
      >
        <input
          :value="settings.stalledMaxReplacementAttempts ?? 3"
          @input="
            (e) =>
              updateField(
                'stalledMaxReplacementAttempts',
                Number((e.target as HTMLInputElement).value || 0),
              )
          "
          type="number"
          min="0"
          max="10"
        />
      </FormRow>
      <FormRow
        label="Minimum Progress (bytes)"
        help="Bytes that must be downloaded between checks to count as progress."
      >
        <input
          :value="settings.stalledMinimumProgressBytes ?? 1"
          @input="
            (e) =>
              updateField(
                'stalledMinimumProgressBytes',
                Number((e.target as HTMLInputElement).value || 1),
              )
          "
          type="number"
          min="1"
          max="1000000000"
        />
      </FormRow>
      <FormRow
        label="Retry Delay (minutes)"
        help="Minimum wait between automatic replacement attempts for the same audiobook."
      >
        <input
          :value="settings.stalledRetryDelayMinutes ?? 5"
          @input="
            (e) =>
              updateField(
                'stalledRetryDelayMinutes',
                Number((e.target as HTMLInputElement).value || 0),
              )
          "
          type="number"
          min="0"
          max="1440"
        />
      </FormRow>
      <CheckboxCard
        :modelValue="settings.stalledAutoSearchAlternatives ?? false"
        @update:modelValue="(v: boolean) => updateField('stalledAutoSearchAlternatives', v)"
        title="Automatically Replace Stalled Downloads (off by default)"
        description="Replace a stalled torrent with the next-best release. The same release is never retried. Never applies to Usenet clients."
      />
      <CheckboxCard
        :modelValue="settings.stalledIgnoreManuallyPaused ?? true"
        @update:modelValue="(v: boolean) => updateField('stalledIgnoreManuallyPaused', v)"
        title="Ignore Manually Paused Downloads"
        description="Paused downloads are never marked as stalled."
      />
      <CheckboxCard
        :modelValue="settings.stalledAutoBlocklist ?? true"
        @update:modelValue="(v: boolean) => updateField('stalledAutoBlocklist', v)"
        title="Blocklist Stalled Releases"
        description="Remember stalled releases durably so they are not selected again for this audiobook."
      />
      <FormRow
        label="Stalled Cleanup (off by default)"
        help="What to do with the stalled item in the client when replacing it. Delete only removes partial download data, never library files."
      >
        <select
          :value="settings.stalledCleanupBehavior ?? 'none'"
          @change="
            (e) => updateField('stalledCleanupBehavior', (e.target as HTMLSelectElement).value)
          "
        >
          <option value="none">Leave in client</option>
          <option value="remove">Remove from client, keep files</option>
          <option value="remove_and_delete">Remove from client, delete partial data</option>
        </select>
      </FormRow>
      <p v-if="stalledError" class="form-help stalled-error" role="alert">{{ stalledError }}</p>
      <p class="form-help effective-values" data-testid="stalled-effective">
        Effective: detection {{ effective.enabled ? 'on' : 'off' }}, timeout
        {{ effective.timeout }} min, metadata {{ effective.metadata }} min, replacement
        {{ effective.auto ? 'automatic' : 'manual only' }} (max {{ effective.attempts }}), cleanup
        {{ effective.cleanup }}. Per-client overrides are set in each download client's "Stalled
        Download Overrides" section and take precedence over these values.
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ApplicationSettings } from '@/types'
import { PhDownload } from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

const effective = computed(() => ({
  enabled: props.settings.stalledHandlingEnabled ?? true,
  timeout: props.settings.stalledTimeoutMinutes ?? 60,
  metadata: props.settings.stalledMetadataTimeoutMinutes ?? 15,
  attempts: props.settings.stalledMaxReplacementAttempts ?? 3,
  auto: props.settings.stalledAutoSearchAlternatives ?? false,
  cleanup: props.settings.stalledCleanupBehavior ?? 'none',
}))

const stalledError = computed(() => {
  const s = props.settings
  const checks: [string, number | null | undefined, number, number][] = [
    ['Stalled timeout', s.stalledTimeoutMinutes, 5, 10080],
    ['Metadata timeout', s.stalledMetadataTimeoutMinutes, 1, 1440],
    ['Max replacement attempts', s.stalledMaxReplacementAttempts, 0, 10],
    ['Retry delay', s.stalledRetryDelayMinutes, 0, 1440],
    ['Minimum progress', s.stalledMinimumProgressBytes, 1, 1_000_000_000],
  ]
  for (const [label, value, min, max] of checks) {
    if (value == null) continue
    if (!Number.isInteger(value) || value < min || value > max) {
      return `${label} must be a whole number between ${min} and ${max}.`
    }
  }
  return null
})

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function updateFailedDownloadHandlingEnabled(value: boolean) {
  updateField('failedDownloadHandlingEnabled', value)
}

function updateFailedDownloadAutoSearch(value: boolean) {
  updateField('failedDownloadAutoSearch', value)
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

/* Modal-like card and local form styles for Download Settings */
.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.form-row-label {
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}

.form-group input[type='number'],
.form-row-control input[type='number'] {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
}

.form-group input:focus,
.form-row-control input:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.08);
}
.form-group input::placeholder,
.form-row-control input::placeholder {
  color: #6c757d;
}

.form-help {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #adb5bd;
  line-height: 1.5;
}
</style>
