import { describe, expect, it } from 'vitest'
import { editionClassificationLabels } from '@/utils/audiobookEdition'

describe('audiobook edition classification', () => {
  it('keeps production type and multipart numbering independent of series position', () => {
    expect(
      editionClassificationLabels({
        productionType: 'FullCast',
        partNumber: 1,
        totalParts: 2,
      }),
    ).toEqual(['Full-cast production', 'Part 1 of 2'])
  })

  it('does not duplicate abridged information already represented by the edition', () => {
    expect(
      editionClassificationLabels({
        edition: 'Abridged Edition',
        abridged: true,
        productionType: 'Dramatized',
      }),
    ).toEqual(['Dramatized adaptation', 'Abridged'])
  })

  it('labels an unabridged edition and a known part total', () => {
    expect(
      editionClassificationLabels({
        abridged: false,
        totalParts: 3,
      }),
    ).toEqual(['Unabridged', '3-part production'])
  })
})
