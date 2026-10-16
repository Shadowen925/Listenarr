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
import type { Audiobook, AudiobookSeriesMembership } from '@/types'
import type { RouteLocationRaw } from 'vue-router'

export type HeaderSearchSuggestion =
  | {
      type: 'audiobook'
      id: number
      title: string
      author: string
      imageUrl: string
    }
  | {
      type: 'series'
      id: string
      title: string
      author: ''
      imageUrl: string
    }

export function getHeaderSearchSuggestions(
  audiobooks: Audiobook[],
  query: string,
  limit = 8,
): HeaderSearchSuggestion[] {
  const normalizedQuery = query.trim().toLowerCase()
  if (!normalizedQuery || limit <= 0) return []

  const seriesById = new Map<string, HeaderSearchSuggestion & { type: 'series' }>()
  const matchingAudiobooks: HeaderSearchSuggestion[] = []
  const seenAudiobookIds = new Set<number>()

  for (const audiobook of audiobooks) {
    const memberships: AudiobookSeriesMembership[] =
      audiobook.seriesMemberships && audiobook.seriesMemberships.length > 0
        ? audiobook.seriesMemberships
        : audiobook.series
          ? [{ seriesName: audiobook.series, seriesAsin: undefined }]
          : []

    for (const membership of memberships) {
      const name = membership.seriesName?.trim()
      const id = membership.seriesAsin?.trim()
      if (!name) continue

      if (id && name.toLowerCase().includes(normalizedQuery)) {
        const normalizedId = id.toUpperCase()
        if (!seriesById.has(normalizedId)) {
          seriesById.set(normalizedId, {
            type: 'series',
            id,
            title: name,
            author: '',
            imageUrl: '',
          })
        }
      }
    }

    const matchesAudiobook =
      (audiobook.title || '').toLowerCase().includes(normalizedQuery) ||
      (audiobook.authors || []).some((author) =>
        (author || '').toLowerCase().includes(normalizedQuery),
      ) ||
      memberships.some((membership) =>
        (membership.seriesName || '').toLowerCase().includes(normalizedQuery),
      )

    if (!matchesAudiobook || seenAudiobookIds.has(audiobook.id)) continue
    seenAudiobookIds.add(audiobook.id)
    matchingAudiobooks.push({
      type: 'audiobook',
      id: audiobook.id,
      title: audiobook.title || 'Unknown',
      author: audiobook.authors?.[0] || '',
      imageUrl: audiobook.imageUrl || '',
    })
  }

  return [...seriesById.values(), ...matchingAudiobooks].slice(0, limit)
}

export function getHeaderSearchDestination(suggestion: HeaderSearchSuggestion): RouteLocationRaw {
  if (suggestion.type === 'series') {
    return { name: 'collection', params: { type: 'series', name: suggestion.id } }
  }
  return { name: 'audiobook-detail', params: { id: String(suggestion.id) } }
}

export function getLibrarySearchDestination(query: string): RouteLocationRaw {
  return { name: 'audiobooks', query: { q: query.trim() } }
}
