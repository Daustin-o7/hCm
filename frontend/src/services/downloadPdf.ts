import api, { assertSessionGeneration, getSessionGeneration } from './api'

/** Download only an authenticated PDF response, never a protected URL or an error page. */
export async function downloadPdf(path: string, filename: string, signal?: AbortSignal): Promise<void> {
  if (!path.startsWith('/') || path.startsWith('//') || path.includes('\\')) {
    throw new Error('PDF download requires a relative API path.')
  }
  const generation = getSessionGeneration()
  signal?.throwIfAborted()
  const response = await api.get<Blob>(path, { responseType: 'blob', signal })
  const blob = response.data
  const mime = String(response.headers['content-type'] ?? '').split(';')[0].trim().toLowerCase()
  if (!(blob instanceof Blob) || mime !== 'application/pdf' || await blob.slice(0, 5).text() !== '%PDF-') {
    throw new Error('The server did not return a valid PDF. Please try again.')
  }
  assertSessionGeneration(generation)
  signal?.throwIfAborted()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  try {
    link.href = url
    link.download = filename
    document.body.appendChild(link)
    link.click()
  } finally {
    link.remove()
    // Let the browser consume the object URL before releasing its bytes.
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
}
