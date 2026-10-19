import type { Audiobook } from '@/types'

type EditionMetadata = Pick<
  Audiobook,
  'edition' | 'version' | 'abridged' | 'productionType' | 'partNumber' | 'totalParts'
>

export function productionTypeLabel(
  productionType: Audiobook['productionType'],
): string | undefined {
  switch (productionType) {
    case 'Dramatized':
      return 'Dramatized adaptation'
    case 'FullCast':
      return 'Full-cast production'
    case 'Standard':
      return 'Standard audiobook'
    default:
      return undefined
  }
}

export function editionClassificationLabels(book: EditionMetadata): string[] {
  const labels: string[] = []
  const addUnique = (value: string | undefined) => {
    const normalized = value?.trim()
    if (normalized && !labels.some((label) => label.toLowerCase() === normalized.toLowerCase())) {
      labels.push(normalized)
    }
  }

  addUnique(productionTypeLabel(book.productionType))
  if (book.abridged === true) {
    addUnique('Abridged')
  } else if (book.abridged === false) {
    addUnique('Unabridged')
  }

  const edition = book.edition?.trim()
  if (
    edition &&
    !(book.abridged === true && /abridged/i.test(edition)) &&
    !(book.abridged === false && /unabridged/i.test(edition))
  ) {
    addUnique(edition)
  }
  addUnique(book.version)

  if (book.partNumber) {
    addUnique(
      book.totalParts ? `Part ${book.partNumber} of ${book.totalParts}` : `Part ${book.partNumber}`,
    )
  } else if (book.totalParts) {
    addUnique(`${book.totalParts}-part production`)
  }

  return labels
}
