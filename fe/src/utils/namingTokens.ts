export interface NamingToken {
  token: string
  generic: string
  example: string
}

export interface NamingTokenCategory {
  name: string
  tokens: NamingToken[]
}

export type SpaceStyle = 'space' | 'period' | 'underscore' | 'dash'
export type CaseStyle = 'default' | 'lower' | 'upper'

const t = (token: string, generic: string, example: string): NamingToken => ({
  token,
  generic,
  example,
})

export const NAMING_EXAMPLES: NamingToken[] = [
  t(
    '{Author Name} - {Book Title} - {Quality Full}',
    "Story Author - The Story's Title!: Subtitle! - MP3 Proper",
    'Stephen King - The Gunslinger - MP3',
  ),
  t(
    '{Author.Name}.{Book.Title}.{Quality.Full}',
    'Story.Author.The.Storys.Title.Subtitle.MP3',
    'Stephen.King.The.Gunslinger.MP3',
  ),
  t(
    '{Author Name} - {Book Title}{( PartNumber)}',
    "Story Author - The Story's Title!: Subtitle! (2)",
    'Stephen King - The Gunslinger (2)',
  ),
  t(
    '{Author Name}{ - Book Series}{ - Narrator}/{Book Series}{ - BookSeries:00} - {Book Title}',
    'Story Author - Story Trilogy - Story Reader/Story Trilogy - Book 03 - Title',
    'Stephen King - The Dark Tower - George Guidall/The Dark Tower - Book 01 - The Gunslinger',
  ),
]

export const NAMING_TOKEN_CATEGORIES: NamingTokenCategory[] = [
  {
    name: 'Author',
    tokens: [
      t('{Author Name}', 'Story Author', 'Stephen King'),
      t('{Author NameFirstLast}', 'Story Author', 'Stephen King'),
      t('{Author NameThe}', 'Story Author', 'Stephen King'),
      t('{Author FirstInitial}', 'S', 'S'),
      t('{Author LastInitial}', 'A', 'K'),
      t('{Author CleanName}', 'Story Author', 'Stephen King'),
      t('{Author SortName}', 'Author, Story', 'King, Stephen'),
      t('{Author Disambiguation}', 'Disambiguation', 'Empty'),
    ],
  },
  {
    name: 'Book',
    tokens: [
      t('{Book Title}', "The Story's Title!: Subtitle!", 'The Gunslinger'),
      t('{Book TitleThe}', "Story's Title!, The: Subtitle!", 'Gunslinger, The'),
      t('{Book CleanTitle}', 'The Storys Title!: Subtitle', 'The Gunslinger'),
      t('{Book TitleNoSub}', "The Story's Title!", 'The Gunslinger'),
      t('{Book TitleTheNoSub}', "Story's Title!, The", 'Gunslinger, The'),
      t('{Book CleanTitleNoSub}', 'The Storys Title!', 'The Gunslinger'),
      t('{Book Subtitle}', 'Subtitle!', 'Empty'),
      t('{Book SubtitleThe}', 'Subtitle!', 'Empty'),
      t('{Book CleanSubtitle}', 'Subtitle', 'Empty'),
      t('{Book Disambiguation}', 'Disambiguation', 'Empty'),
    ],
  },
  {
    name: 'File / Part',
    tokens: [
      t('{PartNumber}', '2', '2'),
      t('{PartNumber:00}', '02', '02'),
      t('{PartNumber:smart}', '02', '02'),
      t('{PartCount}', '10', '10'),
      t('{PartCount:00}', '10', '10'),
      t('{PartCount:smart}', '10', '10'),
    ],
  },
  {
    name: 'Series',
    tokens: [
      t('{Series}', 'Story Trilogy', 'The Dark Tower'),
      t('{SeriesPosition}', '3', '1'),
      t('{SeriesPosition:00}', '03', '01'),
      t('{SeriesTitleNumber}', 'Story Trilogy #3', 'The Dark Tower #1'),
      t('{SeriesFolder}', 'Story Trilogy', 'The Dark Tower'),
      t('{BookTitle-WithSeries}', "The Story's Title!: Subtitle! - Story Trilogy #3", 'The Gunslinger - The Dark Tower #1'),
      t('{ - SeriesPosition}', '- 3', '- 1'),
      t('{ - SeriesPosition:00}', '- 03', '- 01'),
      t('{BookSeries}', 'Book 3', 'Book 1'),
      t('{BookSeries:00}', 'Book 03', 'Book 01'),
      t('{SeriesBook}', 'Book 3', 'Book 1'),
    ],
  },
  {
    name: 'Narrator',
    tokens: [
      t('{Narrator}', 'Story Reader', 'George Guidall'),
      t('{Narrator CleanName}', 'Story Reader', 'George Guidall'),
      t('{Narrator Initials}', 'SR', 'GG'),
      t('{Narrator First}', 'Story', 'George'),
      t('{Narrator Last}', 'Reader', 'Guidall'),
      t('{Narrators}', 'Story Reader, Second Reader', 'George Guidall'),
      t('{ - Narrator}', '- Story Reader', '- George Guidall'),
    ],
  },
  {
    name: 'Release Date',
    tokens: [
      t('{Release Year}', '2016', ''),
      t('{Release YearFirst}', '2015', ''),
      t('{Edition Year}', '2016', ''),
    ],
  },
  {
    name: 'Quality',
    tokens: [t('{Quality Full}', 'MP3 Proper', ''), t('{Quality Title}', 'MP3', '')],
  },
  {
    name: 'Media Info',
    tokens: [
      t('{MediaInfo AudioCodec}', 'MP3', ''),
      t('{MediaInfo AudioChannels}', '2', ''),
      t('{MediaInfo AudioBitRate}', '320kbps', ''),
      t('{MediaInfo AudioBitsPerSample}', '24bit', ''),
      t('{MediaInfo AudioSampleRate}', '44.1kHz', ''),
    ],
  },
  {
    name: 'Graphic Audio',
    tokens: [t('{GraphicAudio}', 'GraphicAudio', ''), t('{AudioProductionType}', 'GraphicAudio', '')],
  },
  {
    name: 'Other',
    tokens: [
      t('{Release Group}', 'Rls Grp', ''),
      t('{Custom Formats}', 'iNTERNAL', ''),
      t('{Original Title}', 'Story.Author.The.Storys.Title.2018.MP3-RELEASE', ''),
      t('{Original Filename}', '01 - The Storys Title', ''),
    ],
  },
  {
    name: 'Listenarr',
    tokens: [
      t('{Author}', 'Story Author', 'Stephen King'),
      t('{Title}', 'The Story\'s Title', 'The Gunslinger'),
      t('{Subtitle}', 'Subtitle', ''),
      t('{Edition}', 'Revised Edition', 'Revised Edition'),
      t('{Publisher}', 'Penguin Audio', 'Penguin Audio'),
      t('{Language}', 'English', 'English'),
      t('{Asin}', 'B000FC1R84', 'B000FC1R84'),
      t('{SeriesNumber:00}', '03', '01'),
      t('{Year}', '2016', '1982'),
      t('{DiskNumber:00}', '02', '02'),
      t('{ChapterNumber:00}', '02', '02'),
    ],
  },
]

// Sample values keyed by lower-case token name with separators removed.
const SAMPLE: Record<string, string> = {
  authorname: 'Stephen King',
  authornamefirstlast: 'Stephen King',
  authornamethe: 'Stephen King',
  authorfirstinitial: 'S',
  authorlastinitial: 'K',
  authorcleanname: 'Stephen King',
  authorsortname: 'King, Stephen',
  authordisambiguation: '',
  author: 'Stephen King',
  booktitle: 'The Gunslinger',
  booktitlethe: 'Gunslinger, The',
  bookcleantitle: 'The Gunslinger',
  booktitlenosub: 'The Gunslinger',
  booktitlethenosub: 'Gunslinger, The',
  bookcleantitlenosub: 'The Gunslinger',
  booksubtitle: '',
  booksubtitlethe: '',
  bookcleansubtitle: '',
  bookdisambiguation: '',
  title: 'The Gunslinger',
  subtitle: '',
  series: 'The Dark Tower',
  bookseries: 'Book 1',
  seriesbook: 'Book 1',
  seriesposition: '1',
  seriesnumber: '1',
  seriestitlenumber: 'The Dark Tower #1',
  seriesfolder: 'The Dark Tower',
  booktitlewithseries: 'The Gunslinger - The Dark Tower #1',
  narrator: 'George Guidall',
  narratorcleanname: 'George Guidall',
  narratorinitials: 'GG',
  narratorfirst: 'George',
  narratorlast: 'Guidall',
  narrators: 'George Guidall',
  edition: 'Revised Edition',
  publisher: 'Penguin Audio',
  language: 'English',
  asin: 'B000FC1R84',
  year: '1982',
  releaseyear: '1982',
  releaseyearfirst: '1982',
  editionyear: '1982',
  quality: '128kbps',
  qualityfull: 'MP3',
  qualitytitle: 'MP3',
  partcount: '10',
}

// Display spellings of known tokens, longest first, so "Author NameThe" wins over "Author Name".
const KNOWN_NAMES = Array.from(
  new Set([
    ...NAMING_TOKEN_CATEGORIES.flatMap((c) => c.tokens.map((tk) => tk.token)),
    '{Book Series}',
    '{Book SeriesPosition}',
    '{DiskNumber}',
    '{ChapterNumber}',
    '{PartNumber}',
  ]),
)
  .map((tk) => tk.replace(/^\{[^A-Za-z]*/, '').replace(/(:[^}]*)?\}$/, ''))
  .filter(Boolean)
  .sort((a, b) => b.length - a.length)

const tokenRegex = new RegExp(
  KNOWN_NAMES.map((n) =>
    n
      .split(' ')
      .map((p) => p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
      .join('[ ._-]'),
  ).join('|'),
  'gi',
)

const SENTINEL = '__EMPTY_VAR__'

function keyOf(name: string): string {
  // Chaptarr's "Book Series" (with a separator) is the series name; "BookSeries" is Listenarr's "Book 03".
  const chaptarr = /^book[ ._-]series(position)?$/i.exec(name)
  if (chaptarr) return chaptarr[1] ? 'seriesposition' : 'series'
  return name.toLowerCase().replace(/[ ._-]/g, '')
}

function formatValue(raw: string, format: string | undefined, part: number): string {
  if (!format) return raw
  const digits = format.toLowerCase() === 'smart' ? String(SAMPLE.partcount).length : format.length
  if (/^0+$/.test(format) || format.toLowerCase() === 'smart') {
    const text = raw.replace(/^Book /, '')
    const n = Number(text)
    if (Number.isNaN(n)) return raw
    const padded = String(n).padStart(digits, '0')
    return raw.startsWith('Book ') ? `Book ${padded}` : padded
  }
  return raw
}

function renderGroup(inner: string, part: number): string {
  const formatMatch = /:([^:}]+)$/.exec(inner)
  const format = formatMatch?.[1]
  const body = formatMatch ? inner.slice(0, formatMatch.index) : inner

  let hasToken = false
  let empty = false
  const literal = body.replace(tokenRegex, '').trim().length
  const out = body.replace(tokenRegex, (name) => {
    hasToken = true
    const key = keyOf(name)
    let value = key in SAMPLE ? SAMPLE[key] : undefined
    if (key === 'partnumber' || key === 'disknumber' || key === 'chapternumber') {
      value = String(part)
    }
    if (value === undefined || value === '') {
      empty = true
      return ''
    }
    value = formatValue(value, format, part)
    const letters = name.replace(/[^A-Za-z]/g, '')
    if (letters && letters === letters.toUpperCase() && letters.length > 1) value = value.toUpperCase()
    else if (letters && letters === letters.toLowerCase()) value = value.toLowerCase()
    const separator = /[A-Za-z0-9]([._-])[A-Za-z]/.exec(name)?.[1]
    return separator && key !== 'booktitlewithseries' ? value.replace(/ /g, separator) : value
  })

  if (!hasToken) return SENTINEL
  if (empty) return literal > 0 || /^\s|\s$/.test(body.replace(tokenRegex, '')) ? '' : SENTINEL
  return out.replace(/\//g, '-')
}

/** Browser-side preview of the server's naming engine using the sample book. */
export function renderNamingPattern(pattern: string, part = 3): string {
  let result = pattern.replace(/\{([^{}]*)\}/g, (_m, inner: string) => renderGroup(inner, part))
  result = result.replace(new RegExp(`[(\\[{]\\s*${SENTINEL}\\s*[)\\]}]`, 'g'), '')
  result = result.replace(new RegExp(`\\s*[-–—:_]\\s*${SENTINEL}`, 'g'), '')
  result = result.replace(new RegExp(`${SENTINEL}\\s*[-–—:_]\\s*`, 'g'), '')
  result = result.replace(new RegExp(`/?${SENTINEL}/?`, 'g'), '/')
  result = result.split(SENTINEL).join('')
  return result.replace(/\/{2,}/g, '/').replace(/ {2,}/g, ' ').replace(/^\/|\/$/g, '').trim()
}

/** Rewrites a token's spelling according to the Space/Case dropdowns. */
export function styleToken(token: string, space: SpaceStyle, caseStyle: CaseStyle): string {
  const match = /^\{(.*?)(:[^}]*)?\}$/.exec(token)
  if (!match) return token
  let name = match[1]
  const format = match[2] ?? ''
  const joiner = { space: ' ', period: '.', underscore: '_', dash: '-' }[space]
  name = name.replace(/(?<=[A-Za-z0-9]) (?=[A-Za-z])/g, joiner)
  if (caseStyle === 'lower') name = name.toLowerCase()
  if (caseStyle === 'upper') name = name.toUpperCase()
  return `{${name}${format}}`
}
