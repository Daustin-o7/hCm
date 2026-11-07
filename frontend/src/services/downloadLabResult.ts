import api, { assertSessionGeneration, getSessionGeneration } from './api'
import { downloadPdf } from './downloadPdf'

const contentTypes: Record<string, string> = {
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png',
  '.dcm': 'application/dicom', '.csv': 'text/csv',
}

export async function downloadLabResult(orderId: string, resultId: string, version: number, extension: string | null, signal?: AbortSignal): Promise<void> {
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
  if (!guid.test(orderId) || !guid.test(resultId) || !Number.isSafeInteger(version) || version < 1) {
    throw new Error('The report reference is invalid. Please reopen the lab order.')
  }
  const path = `/lab-orders/${orderId}/results/${resultId}/file`
  const filename = `lab-result-v${version}${extension}`
  if (extension === '.pdf') return downloadPdf(path, filename, signal)
  if (!extension || !Object.hasOwn(contentTypes, extension)) throw new Error('This report format is not supported.')

  const generation = getSessionGeneration()
  signal?.throwIfAborted()
  const response = await api.get<Blob>(path, { responseType: 'blob', signal })
  const blob = response.data
  const mime = String(response.headers['content-type'] ?? '').split(';')[0].trim().toLowerCase()
  if (!(blob instanceof Blob) || !blob.size || blob.size > 25 * 1024 * 1024 || mime !== contentTypes[extension]) {
    throw new Error('The server did not return a valid report. Please try again.')
  }
  const header = new Uint8Array(await blob.slice(0, 512).arrayBuffer())
  const signature = extension === '.png' ? [137, 80, 78, 71, 13, 10, 26, 10]
    : extension === '.jpg' || extension === '.jpeg' ? [255, 216, 255] : []
  if (!signature.every((byte, index) => header[index] === byte) ||
      extension === '.csv' && /^[\s\uFEFF]*[<{\[]/.test(new TextDecoder().decode(header))) {
    throw new Error('The server did not return a valid report. Please try again.')
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
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
}
