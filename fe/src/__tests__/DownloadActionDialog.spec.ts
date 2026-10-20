import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'

import DownloadActionDialog from '@/components/domain/download/DownloadActionDialog.vue'
import DownloadSettingsSection from '@/components/settings/DownloadSettingsSection.vue'

const base = {
  open: true,
  variant: 'replace' as const,
  audiobookTitle: 'This Inevitable Ruin',
  currentTitle: 'Old Release',
  clientName: 'Deluge',
  progress: 12.5,
  dataSize: '40 MB',
  newTitle: 'New Release',
  newSize: '1.2 GB',
  newQuality: 'M4B',
}

describe('DownloadActionDialog', () => {
  it('shows the current and new release details for a replacement', () => {
    const wrapper = mount(DownloadActionDialog, { props: base })
    expect(wrapper.get('[data-test="dad-audiobook"]').text()).toBe('This Inevitable Ruin')
    expect(wrapper.get('[data-test="dad-current"]').text()).toBe('Old Release')
    expect(wrapper.get('[data-test="dad-new"]').text()).toContain('1.2 GB')
    expect(wrapper.text()).toContain('40 MB on disk')
  })

  it('offers the three removal choices and emits the selected mode', async () => {
    const wrapper = mount(DownloadActionDialog, { props: { ...base, variant: 'remove' } })
    expect(wrapper.findAll('input[type="radio"]')).toHaveLength(3)

    await wrapper.findAll('input[type="radio"]')[1]!.setValue()
    await wrapper.get('[data-test="dad-confirm"]').trigger('click')
    expect(wrapper.emitted('confirm')![0]).toEqual(['clientDeleteData'])
  })

  it('only offers Listenarr-only cleanup when the item is missing from the client', () => {
    const wrapper = mount(DownloadActionDialog, {
      props: { ...base, variant: 'remove', missingRemote: true },
    })
    expect(wrapper.findAll('input[type="radio"]')).toHaveLength(1)
    expect(wrapper.get('[data-test="dad-missing"]').exists()).toBe(true)
    wrapper.get('[data-test="dad-confirm"]').trigger('click')
    expect(wrapper.emitted('confirm')![0]).toEqual(['listenarrOnly'])
  })

  it('shows errors and disables actions while busy', async () => {
    const wrapper = mount(DownloadActionDialog, { props: { ...base, error: 'Client unavailable' } })
    expect(wrapper.get('[data-test="dad-error"]').text()).toBe('Client unavailable')
    await wrapper.setProps({ busy: true })
    expect(wrapper.get('[data-test="dad-confirm"]').attributes('disabled')).toBeDefined()
    await wrapper.get('.dad-overlay').trigger('click')
    expect(wrapper.emitted('cancel')).toBeUndefined()
  })

  it('cancels on Escape', async () => {
    const wrapper = mount(DownloadActionDialog, { props: base })
    await wrapper.get('.dad-overlay').trigger('keydown.esc')
    expect(wrapper.emitted('cancel')).toHaveLength(1)
  })
})

describe('DownloadSettingsSection stalled settings', () => {
  it('emits updated stalled settings without dropping other fields', async () => {
    const wrapper = mount(DownloadSettingsSection, {
      props: { settings: { maxConcurrentDownloads: 3 } },
    })
    const timeout = wrapper
      .findAll('input[type="number"]')
      .find((input) => input.attributes('max') === '10080')
    expect(timeout).toBeDefined()
    await timeout!.setValue('90')

    const emitted = wrapper.emitted('update:settings')!
    expect(emitted.at(-1)![0]).toMatchObject({
      maxConcurrentDownloads: 3,
      stalledTimeoutMinutes: 90,
    })
  })
})
