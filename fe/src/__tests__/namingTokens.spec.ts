import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import NamingTokenModal from '@/components/settings/NamingTokenModal.vue'
import { renderNamingPattern, styleToken } from '@/utils/namingTokens'

const USER_PATTERN =
  '{Author Name}{ - Book Series}{ - Narrator}/{Book Series}{ - Book SeriesPosition:00} - {Book Title}'

describe('namingTokens', () => {
  it('renders Chaptarr-style conditional separators', () => {
    expect(renderNamingPattern(USER_PATTERN)).toBe(
      'Stephen King - The Dark Tower - George Guidall/The Dark Tower - 01 - The Gunslinger',
    )
  })

  it('supports legacy tokens and BookSeries labels', () => {
    expect(renderNamingPattern('{Author}/{Series}/{Title}')).toBe(
      'Stephen King/The Dark Tower/The Gunslinger',
    )
    expect(renderNamingPattern('{BookSeries:00} - {Title}')).toBe('Book 01 - The Gunslinger')
    expect(renderNamingPattern('{SeriesBook} - {Title}')).toBe('Book 1 - The Gunslinger')
  })

  it('drops optional groups when empty and honours separator/case spelling', () => {
    expect(renderNamingPattern('{Title}{ - Book Subtitle}')).toBe('The Gunslinger')
    expect(renderNamingPattern('{Author.Name}.{Book.Title}')).toBe('Stephen.King.The.Gunslinger')
    expect(renderNamingPattern('{author name}')).toBe('stephen king')
  })

  it('pads part numbers', () => {
    expect(renderNamingPattern('{Title} {PartNumber:smart}', 2)).toBe('The Gunslinger 02')
  })

  it('styles tokens by space and case', () => {
    expect(styleToken('{Author Name}', 'period', 'lower')).toBe('{author.name}')
    expect(styleToken('{PartNumber:smart}', 'space', 'upper')).toBe('{PARTNUMBER:smart}')
  })
})

describe('NamingTokenModal', () => {
  it('inserts clicked tokens into the draft and applies it', async () => {
    const wrapper = mount(NamingTokenModal, { props: { initial: '' } })
    await wrapper.findAll('[data-testid="token-insert"]')[0].trigger('click')
    const draft = wrapper.get('[data-testid="token-draft"]').element as HTMLInputElement
    expect(draft.value).toBe('{Author Name}')

    await wrapper.get('[data-testid="token-space"]').setValue('period')
    await wrapper.findAll('[data-testid="token-insert"]')[8].trigger('click')
    expect(draft.value).toContain('{Book.Title}')

    await wrapper.get('[data-testid="token-apply"]').trigger('click')
    expect(wrapper.emitted('apply')?.[0]?.[0]).toBe(draft.value)
  })
})
