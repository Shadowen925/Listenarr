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
import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import DownloadClientFormModal from '@/components/domain/download/DownloadClientFormModal.vue'
import { useConfigurationStore } from '@/stores/configuration'

describe('DownloadClientFormModal', () => {
  it('renders password input for qbittorrent', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    // Provide an editingClient prop to initialize formData for qbittorrent
    await wrapper.setProps({
      editingClient: {
        id: '1',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'qbittorrent.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings: {},
      },
    })
    await wrapper.vm.$nextTick()

    const passwordComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(passwordComponent.exists()).toBe(true)
  })

  it('renders api key input for sabnzbd', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '2',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sab.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings: {},
      },
    })
    await wrapper.vm.$nextTick()

    const apiKeyComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(apiKeyComponent.exists()).toBe(true)
  })

  it('does not treat a redacted saved SABnzbd key as an editable credential', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: 'saved-sab',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sab.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings: { apiKey: 'REDACTED' },
      },
    })
    await wrapper.vm.$nextTick()

    const apiKeyComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(apiKeyComponent.exists()).toBe(true)
    expect(apiKeyComponent.props('modelValue')).toBe('')
    const apiKeyInput = wrapper.find('#apiKey')
    expect(apiKeyInput.attributes('required')).toBeUndefined()
    expect(apiKeyInput.attributes('placeholder')).toContain('leave blank to keep it')
  })

  it('test button on modal uses current input values and includes ID for existing client fallback', async () => {
    const api = await import('@/services/api')
    ;(api.testDownloadClient as unknown) = vi.fn(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '3',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'original.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        settings: {},
        password: 'dbpass',
      },
    })
    await wrapper.vm.$nextTick()

    // change host input to a new value before testing
    const hostInput = wrapper.find('input[id="host"]')
    await hostInput.setValue('http://edited.local/nzbget')

    // click the Test button (use class selector to reliably find the correct button)
    const testButton = wrapper.find('button.btn-info')
    expect(testButton.exists()).toBe(true)
    await testButton.trigger('click')

    expect(api.testDownloadClient as unknown).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as unknown).mock.calls[0][0]
    expect(calledWith.host).toBe('edited.local')
    // Existing client id should be sent so backend can reuse saved credentials when needed.
    expect(calledWith.id).toBe('3')
  })

  it('shows a saved-password indicator without loading the stored credential into the form', async () => {
    const api = await import('@/services/api')
    ;(api.testDownloadClient as unknown) = vi.fn(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '4',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'host.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        settings: {},
        hasSavedPassword: true,
      },
    })
    await wrapper.vm.$nextTick()

    const passwordComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(passwordComponent.exists()).toBe(true)
    expect(passwordComponent.props('modelValue')).toBe('')
    expect(wrapper.text()).toContain('Password saved. Leave blank to keep it')
    expect(wrapper.find('#password').attributes('placeholder')).toContain('Enter a new password')

    // click Test
    const testButton = wrapper.find('button.btn-info')
    await testButton.trigger('click')

    expect(api.testDownloadClient as unknown).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as unknown).mock.calls[0][0]
    // Send only the client ID and blank input so the API reuses the stored credential.
    expect(calledWith.password).toBe('')
    expect(calledWith.id).toBe('4')
  })

  it('preserves Download Path through Save, Reload, and reopening the editor', async () => {
    const client = {
      id: 'deluge-1',
      name: 'Deluge',
      type: 'deluge' as const,
      host: 'deluge.local',
      port: 8112,
      isEnabled: true,
      useSSL: false,
      downloadPath: '/data/deluge/completed/',
      username: '',
      settings: {},
    }
    const savedClient = { ...client, downloadPath: '/data/deluge/new-completed/' }
    const pinia = createPinia()
    const configStore = useConfigurationStore(pinia)
    const saveSpy = vi
      .spyOn(configStore, 'saveDownloadClientConfiguration')
      .mockImplementation(async (config) => {
        expect(config.downloadPath).toBe(savedClient.downloadPath)
        configStore.downloadClientConfigurations = [savedClient]
      })

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [pinia] },
      props: { visible: true, editingClient: client },
    })
    await wrapper.vm.$nextTick()
    expect((wrapper.find('#downloadPath').element as HTMLInputElement).value).toBe(
      client.downloadPath,
    )

    await wrapper.find('#downloadPath').setValue(savedClient.downloadPath)
    await wrapper.find('button.btn-primary').trigger('click')

    expect(saveSpy).toHaveBeenCalled()
    expect(configStore.downloadClientConfigurations[0].downloadPath).toBe(savedClient.downloadPath)

    await wrapper.setProps({ editingClient: savedClient })
    await wrapper.vm.$nextTick()
    expect((wrapper.find('#downloadPath').element as HTMLInputElement).value).toBe(
      savedClient.downloadPath,
    )
    vi.restoreAllMocks()
  })

  it('round-trips inherited, enabled, and disabled stalled overrides through editing', async () => {
    const client = {
      id: 'deluge-overrides',
      name: 'Deluge',
      type: 'deluge' as const,
      host: 'deluge.local',
      port: 8112,
      isEnabled: true,
      useSSL: false,
      downloadPath: '',
      username: '',
      settings: {
        stalledEnabled: false,
        stalledIgnoreManuallyPaused: true,
      },
    }
    const pinia = createPinia()
    const configStore = useConfigurationStore(pinia)
    let reloadedClient = client
    vi.spyOn(configStore, 'saveDownloadClientConfiguration').mockImplementation(async (config) => {
      reloadedClient = { ...client, settings: config.settings }
      configStore.downloadClientConfigurations = [reloadedClient]
    })

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [pinia] },
      props: { visible: true, editingClient: client },
    })
    await wrapper.vm.$nextTick()
    expect((wrapper.find('#ovEnabled').element as HTMLSelectElement).value).toBe('false')
    expect((wrapper.find('#ovPaused').element as HTMLSelectElement).value).toBe('true')

    await wrapper.find('#ovEnabled').setValue('')
    await wrapper.find('#ovPaused').setValue('false')
    await wrapper.find('#ovBlocklist').setValue('true')
    await wrapper.find('button.btn-primary').trigger('click')

    expect(reloadedClient.settings).not.toHaveProperty('stalledEnabled')
    expect(reloadedClient.settings.stalledIgnoreManuallyPaused).toBe(false)
    expect(reloadedClient.settings.stalledAutoBlocklist).toBe(true)

    await wrapper.setProps({ editingClient: reloadedClient })
    await wrapper.vm.$nextTick()
    expect((wrapper.find('#ovEnabled').element as HTMLSelectElement).value).toBe('')
    expect((wrapper.find('#ovPaused').element as HTMLSelectElement).value).toBe('false')
    expect((wrapper.find('#ovBlocklist').element as HTMLSelectElement).value).toBe('true')
    vi.restoreAllMocks()
  })
})
