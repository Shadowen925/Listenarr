import { describe, expect, it } from 'vitest'
import type { Audiobook } from '@/types'
import {
  getHeaderSearchDestination,
  getHeaderSearchSuggestions,
  getLibrarySearchDestination,
} from '@/utils/globalSearch'

const audiobooks: Audiobook[] = [
  {
    id: 1,
    title: 'A Parade of Horribles',
    authors: ['Matt Dinniman'],
    seriesMemberships: [
      { seriesName: 'Dungeon Crawler Carl', seriesNumber: '8', seriesAsin: 'SERIES-1' },
      { seriesName: 'Another Series', seriesNumber: '2', seriesAsin: 'SERIES-2' },
    ],
  },
  {
    id: 2,
    title: 'Dungeon Crawler Carl',
    authors: ['Matt Dinniman'],
    seriesMemberships: [
      { seriesName: 'Dungeon Crawler Carl', seriesNumber: '1', seriesAsin: 'series-1' },
    ],
  },
]

describe('global search', () => {
  it('returns each matching audiobook and one deduplicated series result', () => {
    const suggestions = getHeaderSearchSuggestions(audiobooks, 'Dungeon Crawler Carl')

    expect(suggestions).toEqual([
      {
        type: 'series',
        id: 'SERIES-1',
        title: 'Dungeon Crawler Carl',
        author: '',
        imageUrl: '',
      },
      {
        type: 'audiobook',
        id: 1,
        title: 'A Parade of Horribles',
        author: 'Matt Dinniman',
        imageUrl: '',
      },
      {
        type: 'audiobook',
        id: 2,
        title: 'Dungeon Crawler Carl',
        author: 'Matt Dinniman',
        imageUrl: '',
      },
    ])
  })

  it('matches audiobook titles and authors without adding unrelated series', () => {
    expect(
      getHeaderSearchSuggestions(audiobooks, 'Matt Dinniman').map((item) => item.type),
    ).toEqual(['audiobook', 'audiobook'])
  })

  it('opens series results by their identifier and audiobook results by audiobook id', () => {
    const [series, audiobook] = getHeaderSearchSuggestions(audiobooks, 'Dungeon Crawler Carl')

    expect(getHeaderSearchDestination(series!)).toEqual({
      name: 'collection',
      params: { type: 'series', name: 'SERIES-1' },
    })
    expect(getHeaderSearchDestination(audiobook!)).toEqual({
      name: 'audiobook-detail',
      params: { id: '1' },
    })
  })

  it('routes Enter searches to the library with the trimmed query', () => {
    expect(getLibrarySearchDestination('  Dungeon Crawler Carl  ')).toEqual({
      name: 'audiobooks',
      query: { q: 'Dungeon Crawler Carl' },
    })
  })
})
