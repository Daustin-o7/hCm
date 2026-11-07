import { z } from 'zod'
import api, { assertSessionGeneration, getAccessToken, getSessionGeneration, isSessionActive } from './api'

// Managed, individually authenticated, encrypted devices only. Origin scripts can use
// these nonextractable keys; device/profile isolation and recovery remain rollout policy.
const DB_NAME = 'hospital_crm_outbox_v1'
const LEGACY_KEY = 'hospital_crm_offline_outbox'
const REPLAY_WINDOW_MS = 24 * 60 * 60 * 1000
const storageError = () => new Error('Offline storage is unavailable. Work is not confirmed queued. Keep this screen open and ask the clinic administrator for recovery help.')
const idSchema = z.string().uuid().refine(id => id !== '00000000-0000-0000-0000-000000000000')
const timeSchema = z.string().datetime({ offset: true })
const itemSchema = z.object({
  version: z.literal(1), id: idSchema, type: z.union([z.literal(1), z.literal(2)]),
  idempotencyKey: z.string().min(1).max(128), payloadJson: z.string().max(65536), createdAt: timeSchema,
})

export type OutboxItem = z.infer<typeof itemSchema>
export interface OutboxSession { userId: string; tenantId: string; clinicId: string; generation: number }
export type OutboxStatus = 'pending' | 'retry' | 'rejected' | 'unknown' | 'expired' | 'unreadable' | 'unsupported'
export interface OutboxEntry {
  id: string
  revision: string
  status: OutboxStatus
  message: string
  item?: OutboxItem
}
interface StoredItem {
  owner: string; id: string; type: number; idempotencyKey: string; version: number
  iv: Uint8Array<ArrayBuffer>; ciphertext: ArrayBuffer; revision: string
  outcome?: 'retry' | 'rejected' | 'unknown'
}
const messages: Record<OutboxStatus, string> = {
  pending: 'Waiting to sync.', retry: 'Delivery is uncertain. Retry uses the unchanged request and key.',
  rejected: 'The server rejected this request. Review the original registration or bill with your clinic administrator before correcting it.',
  unknown: 'Saved identity or evidence could not be verified. Retry unchanged to reconcile; do not create a replacement.',
  expired: 'Older than the automatic replay window. Retained for authorized recovery; contact your clinic administrator.',
  unreadable: 'Encrypted work could not be read. Retained for device/key recovery; contact your clinic administrator.',
  unsupported: 'This stored version cannot be submitted by this app. Retained for authorized recovery.',
}

/** Capture before delayed work. No caller-supplied owner or stored token is authority. */
export function captureOutboxSession(): OutboxSession {
  try {
    if (!isSessionActive()) throw new Error()
    const claims = JSON.parse(atob(getAccessToken()!.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')))
    if (claims.impersonation_id || claims.impersonated_by || claims.must_change_password === 'true'
      || claims.mfa_enrolment_required === 'true' || claims.read_only === true || claims.read_only === 'true'
      || !['clinicadmin', 'owner', 'receptionist', 'doctor'].includes(String(claims.role).toLowerCase())
      || typeof claims.exp !== 'number' || claims.exp * 1000 <= Date.now()) throw new Error()
    return { userId: idSchema.parse(claims.sub), tenantId: idSchema.parse(claims.tenant_id),
      clinicId: idSchema.parse(claims.clinicId ?? claims.clinic_id), generation: getSessionGeneration() }
  } catch {
    throw new Error('Offline work is paused. Sign in to the original user and clinic with write access; support sessions cannot open or sync it.')
  }
}

function ownerKey(session: OutboxSession) { return JSON.stringify([session.userId, session.tenantId, session.clinicId]) }
function assertOwner(session: OutboxSession) {
  assertSessionGeneration(session.generation)
  if (ownerKey(captureOutboxSession()) !== ownerKey(session)) throw new Error('The offline work belongs to another user or clinic.')
}

/** Presence only: never parse, hydrate, migrate, assign or remove unowned legacy bytes. */
export function legacyOutboxStatus(): 'present' | 'absent' | 'unavailable' {
  try { return localStorage.getItem(LEGACY_KEY) === null ? 'absent' : 'present' } catch { return 'unavailable' }
}

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    let failed = false
    const fail = () => { failed = true; reject(storageError()) }
    try {
      const request = indexedDB.open(DB_NAME, 1)
      request.onerror = fail
      request.onblocked = fail
      request.onupgradeneeded = () => {
        const db = request.result
        db.createObjectStore('keys')
        const items = db.createObjectStore('items', { keyPath: ['owner', 'id'] })
        items.createIndex('owner', 'owner')
        items.createIndex('identity', ['owner', 'type', 'idempotencyKey'], { unique: true })
      }
      request.onsuccess = () => {
        if (failed) request.result.close()
        else { request.result.onversionchange = () => request.result.close(); resolve(request.result) }
      }
    } catch { fail() }
  })
}

async function transaction<T>(session: OutboxSession, stores: string[], mode: IDBTransactionMode,
  work: (tx: IDBTransaction, done: (value: T) => void, guard: (action: () => void) => void) => void): Promise<T> {
  assertOwner(session)
  const db = await openDatabase()
  try {
    assertOwner(session)
    return await new Promise<T>((resolve, reject) => {
      const tx = db.transaction(stores, mode, { durability: 'strict' })
      let value: T
      let failure: unknown
      const guard = (action: () => void) => {
        try { assertOwner(session); action() } catch (error) { failure = error; tx.abort() }
      }
      tx.onabort = () => reject(failure ?? storageError())
      tx.onerror = () => { failure ??= storageError() }
      tx.oncomplete = () => {
        try { assertOwner(session); resolve(value) } catch (error) { reject(error) }
      }
      guard(() => work(tx, result => { value = result }, guard))
    })
  } catch (error) {
    assertOwner(session)
    throw error instanceof Error ? error : storageError()
  } finally { db.close() }
}

async function ownerCryptoKey(session: OutboxSession): Promise<CryptoKey> {
  const owner = ownerKey(session)
  const existing = await transaction<CryptoKey | undefined>(session, ['keys'], 'readonly', (tx, done) => {
    const request = tx.objectStore('keys').get(owner)
    request.onsuccess = () => done(request.result)
  })
  if (existing) return existing
  const candidate = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt'])
  return transaction<CryptoKey>(session, ['keys', 'items'], 'readwrite', (tx, done, guard) => {
    const keys = tx.objectStore('keys')
    const request = keys.get(owner)
    request.onsuccess = () => guard(() => {
      if (request.result) { done(request.result); return }
      const count = tx.objectStore('items').index('owner').count(owner)
      count.onsuccess = () => guard(() => {
        // A missing key must never be silently replaced over unrecoverable ciphertext.
        if (count.result) throw new Error(messages.unreadable)
        keys.add(candidate, owner)
        done(candidate)
      })
    })
  })
}

const aad = (row: StoredItem) => new TextEncoder().encode(JSON.stringify([row.owner, row.id, row.type, row.idempotencyKey, row.version, row.revision]))
function validItem(input: unknown): OutboxItem {
  const parsed = itemSchema.safeParse(input)
  if (!parsed.success) throw new Error('Offline work needs a supported version, stable identity, valid timestamp and a payload of at most 64KB.')
  const item = parsed.data
  const prefix = item.type === 1 ? 'PAT' : 'INV'
  if (!idSchema.safeParse(item.idempotencyKey).success && !new RegExp(`^IDEMP-${prefix}-[a-zA-Z0-9-]+$`).test(item.idempotencyKey)) {
    throw new Error('Keep the original registration or invoice idempotency key.')
  }
  let payload: Record<string, unknown>
  try {
    payload = JSON.parse(item.payloadJson)
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) throw new Error()
  } catch { throw new Error('The offline payload must be a JSON object.') }
  if (payload.idempotencyKey !== undefined && payload.idempotencyKey !== item.idempotencyKey) throw new Error('The request and outbox keys must match.')
  if (Date.parse(item.createdAt) > Date.now() + 60_000) throw new Error('The offline timestamp is in the future. Check the device clock.')
  return item
}

async function decrypt(row: StoredItem, key: CryptoKey): Promise<OutboxItem> {
  if (!key || key.extractable || key.algorithm.name !== 'AES-GCM') throw new Error(messages.unreadable)
  const plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: row.iv, additionalData: aad(row) }, key, row.ciphertext)
  const item = validItem(JSON.parse(new TextDecoder().decode(plaintext)))
  if (item.id !== row.id || item.type !== row.type || item.idempotencyKey !== row.idempotencyKey || item.version !== row.version) throw new Error(messages.unreadable)
  return item
}

/** Resolves only after strict IDB commit. Freeze and reuse the entire item on retries. */
export async function enqueueOutbox(input: OutboxItem, session = captureOutboxSession()): Promise<void> {
  assertOwner(session)
  const item = validItem(input)
  const key = await ownerCryptoKey(session)
  if (key.extractable || key.algorithm.name !== 'AES-GCM') throw new Error(messages.unreadable)
  const row: StoredItem = { owner: ownerKey(session), id: item.id, type: item.type, idempotencyKey: item.idempotencyKey,
    version: 1, revision: crypto.randomUUID(), iv: crypto.getRandomValues(new Uint8Array(12)), ciphertext: new ArrayBuffer(0) }
  row.ciphertext = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: row.iv, additionalData: aad(row) }, key, new TextEncoder().encode(JSON.stringify(item)))
  const existing = await transaction<StoredItem | undefined>(session, ['items'], 'readwrite', (tx, done, guard) => {
    const store = tx.objectStore('items')
    const byId = store.get([row.owner, row.id])
    byId.onsuccess = () => guard(() => {
      if (byId.result) { done(byId.result); return }
      const byKey = store.index('identity').get([row.owner, row.type, row.idempotencyKey])
      byKey.onsuccess = () => guard(() => {
        if (byKey.result) done(byKey.result)
        else { store.add(row); done(undefined) }
      })
    })
  })
  if (existing && JSON.stringify(await decrypt(existing, key)) !== JSON.stringify(item)) {
    throw new Error('This offline identity already holds a different request. Recover the original; never change a retry payload or key.')
  }
  assertOwner(session)
  window.dispatchEvent(new Event('samstack:outbox-changed'))
}

export function readOutbox(session = captureOutboxSession()): Promise<OutboxEntry[]> { return readEntries(session) }

async function readEntries(session: OutboxSession, id?: string): Promise<OutboxEntry[]> {
  const owner = ownerKey(session)
  const snapshot = await transaction<{ rows: StoredItem[]; key?: CryptoKey }>(session, ['items', 'keys'], 'readonly', (tx, done) => {
    const store = tx.objectStore('items')
    const rows = id === undefined ? store.index('owner').getAll(owner) : store.get([owner, id])
    const key = tx.objectStore('keys').get(owner)
    rows.onsuccess = key.onsuccess = () => {
      if (rows.readyState === 'done' && key.readyState === 'done') done({
        rows: id === undefined ? rows.result : rows.result ? [rows.result] : [], key: key.result,
      })
    }
  })
  const entries = await Promise.all(snapshot.rows.map(async row => {
    let status: OutboxStatus = 'unreadable'
    let item: OutboxItem | undefined
    if (row.version !== 1) status = 'unsupported'
    else {
      try {
        item = await decrypt(row, snapshot.key!)
        status = Date.now() - Date.parse(item.createdAt) > REPLAY_WINDOW_MS ? 'expired'
          : row.outcome === undefined ? 'pending'
            : ['retry', 'rejected', 'unknown'].includes(row.outcome) ? row.outcome : 'unknown'
      } catch { /* Preserve ciphertext and show a recovery state, never an empty queue. */ }
    }
    return { id: String(row.id), revision: row.revision, item, status, message: messages[status] }
  }))
  assertOwner(session)
  return entries
}

async function markOutcome(session: OutboxSession, entry: OutboxEntry, outcome?: StoredItem['outcome']) {
  await transaction<void>(session, ['items'], 'readwrite', (tx, done, guard) => {
    const store = tx.objectStore('items')
    const request = store.get([ownerKey(session), entry.id])
    request.onsuccess = () => guard(() => {
      if (request.result?.revision === entry.revision) store.put({ ...request.result, outcome })
      done()
    })
  })
}

/** Explicit reconciliation only; the encrypted request, timestamp and key never change. */
export async function retryOutboxItem(id: string, session = captureOutboxSession()): Promise<void> {
  const entry = (await readEntries(session, id))[0]
  if (!entry?.item || ['expired', 'unreadable', 'unsupported'].includes(entry.status)) throw new Error('This item needs authorized recovery before it can be retried.')
  await markOutcome(session, entry)
  window.dispatchEvent(new Event('samstack:outbox-changed'))
}

const resultSchema = z.object({ id: idSchema, idempotencyKey: z.string(), type: z.enum(['patient', 'invoice']),
  status: z.enum(['created', 'idempotent_duplicate']) })
const consentSchema = z.object({ id: idSchema, patientId: idSchema, purpose: z.string(), noticeVersion: z.string(),
  language: z.string(), captureMethod: z.string(), capturedBy: idSchema, capturedAt: timeSchema,
  guardianName: z.string().nullish(), guardianRelationship: z.string().nullish() })
const patientSchema = z.object({ id: idSchema, name: z.string(), phone: z.string(), uhid: z.string().trim().min(1),
  dob: z.string().nullish(), approxAge: z.number().nullish(), gender: z.string(), address: z.string().nullish(),
  createdAt: timeSchema, consents: z.array(consentSchema) })
const moneySchema = z.number().finite().nonnegative().refine(value => Number.isSafeInteger(Math.round(value * 100)) && Math.round(value * 100) / 100 === value)
const lineSchema = z.object({ description: z.string().trim().min(2).max(150), quantity: z.number().int().positive().max(1000000),
  unitPrice: moneySchema.refine(value => value > 0 && value <= 1000000),
  gstRate: moneySchema.refine(value => value <= 100), taxCategory: z.string().regex(/^(exempt|nil|taxable)$/i), hsnSacCode: z.string().nullish(),
  discountAmount: moneySchema.default(0) })
const savedLineSchema = lineSchema.extend({ discountAmount: moneySchema, taxableAmount: moneySchema, taxAmount: moneySchema, totalAmount: moneySchema })
const invoiceSchema = z.object({ invoiceId: idSchema, appointmentId: idSchema, invoiceNumber: z.string().trim().min(1),
  createdAt: timeSchema, status: z.string(), finalizedAt: timeSchema.nullish(), lineItems: z.array(savedLineSchema).min(1).max(50),
  subtotal: moneySchema, gstAmount: moneySchema, total: moneySchema, paidAmount: moneySchema, balanceDue: moneySchema,
  discountAmount: moneySchema,
  payments: z.array(z.object({ id: idSchema, method: z.string(), amount: moneySchema, status: z.string(),
    verificationStatus: z.string(), idempotencyKey: z.string().nullish(), confirmedAt: timeSchema.nullish() })) })

function savedEvidence(item: OutboxItem, result: z.infer<typeof resultSchema>, evidence: unknown): boolean {
  const payload = JSON.parse(item.payloadJson)
  if (item.type === 1) {
    const parsed = patientSchema.safeParse(evidence)
    if (!parsed.success) return false
    const saved = parsed.data
    const normalized = (value: unknown) => typeof value === 'string' ? value.trim() : ''
    const purpose = normalized(payload.consent?.purpose)
    const purposes = payload.whatsAppOptIn ? [purpose, 'whatsapp_opt_in'] : [purpose]
    return saved.id === result.id && saved.name === normalized(payload.name) && saved.phone === normalized(payload.phone)
      && (saved.dob ?? null) === (payload.dob ?? null) && (saved.approxAge ?? null) === (payload.approxAge ?? null)
      && saved.gender.toLowerCase() === (normalized(payload.gender).toLowerCase() || 'prefernottosay')
      && normalized(saved.address) === normalized(payload.address)
      && payload.consent?.accepted === true && ['care_delivery', 'direct_care'].includes(purpose)
      && purposes.every(purpose => saved.consents.some(c => c.patientId === saved.id
        && c.purpose === purpose && c.noticeVersion === (normalized(payload.noticeVersion) || 'v1.0')
        && c.language === (normalized(payload.language) || 'en') && c.captureMethod === (normalized(payload.captureMethod) || 'receptionist_assisted')
        && normalized(c.guardianName) === normalized(payload.guardianName) && normalized(c.guardianRelationship) === normalized(payload.guardianRelationship)))
  }
  const parsed = invoiceSchema.safeParse(evidence)
  const lines = z.array(lineSchema).min(1).max(50).safeParse(payload.lineItems)
  if (!parsed.success || !lines.success) return false
  const saved = parsed.data
  const remaining = [...saved.lineItems]
  for (const line of lines.data) {
    const index = remaining.findIndex(stored => stored.description === line.description.trim()
      && stored.quantity === line.quantity && stored.unitPrice === line.unitPrice && stored.discountAmount === line.discountAmount
      && stored.taxCategory.toLowerCase() === line.taxCategory.toLowerCase()
      && stored.gstRate === (['exempt', 'nil'].includes(line.taxCategory.toLowerCase()) ? 0 : line.gstRate)
      && (!line.hsnSacCode?.trim() || stored.hsnSacCode === line.hsnSacCode.trim()))
    if (index < 0) return false
    remaining.splice(index, 1)
  }
  if (remaining.length) return false
  const cents = (value: number) => Math.round(value * 100)
  let subtotal = 0, gst = 0
  for (const line of saved.lineItems) {
    const gross = line.quantity * cents(line.unitPrice)
    const base = gross - cents(line.discountAmount)
    // Match InvoiceAccounting's per-line decimal AwayFromZero rounding in integer cents.
    const tax = Math.floor((base * cents(line.gstRate) + 5000) / 10000)
    if (gross > 1000000000 || base < 0 || (line.taxCategory.toLowerCase() === 'taxable' ? line.gstRate === 0 : line.gstRate !== 0)
      || cents(line.taxableAmount) !== base
      || cents(line.taxAmount) !== tax || cents(line.totalAmount) !== base + tax) return false
    subtotal += base
    gst += tax
  }
  const total = subtotal + gst - cents(saved.discountAmount)
  const receipts = saved.payments.filter(p => p.status.toLowerCase() === 'completed'
    && p.verificationStatus.toLowerCase() === 'confirmed' && !!p.confirmedAt)
  // shortcut: later credits/refunds retain work for recovery until their authoritative evidence is reconciled here.
  if (cents(saved.subtotal) !== subtotal || cents(saved.gstAmount) !== gst || cents(saved.total) !== total
    || new Set(saved.payments.map(p => p.id)).size !== saved.payments.length
    || cents(saved.paidAmount) !== receipts.reduce((sum, p) => sum + cents(p.amount), 0)
    || cents(saved.balanceDue) !== total - cents(saved.paidAmount)) return false
  const status = String(payload.status ?? 'draft').toLowerCase()
  const savedStatus = saved.status.toLowerCase()
  if (saved.invoiceId !== result.id || saved.appointmentId !== payload.appointmentId
    || saved.discountAmount !== (payload.discountAmount ?? 0)
    || !['draft', 'unpaid', 'issued', 'finalized', 'paid'].includes(status)
    || !['draft', 'issued', 'finalized', 'partiallypaid', 'paid'].includes(savedStatus)
    || (savedStatus === 'draft' ? !!saved.finalizedAt || status !== 'draft' : !saved.finalizedAt)
    || (savedStatus === 'paid' ? saved.balanceDue !== 0
      : savedStatus === 'partiallypaid' ? saved.paidAmount === 0 || saved.balanceDue === 0 : saved.paidAmount !== 0)) return false
  if (status !== 'paid') return true
  if (String(payload.paymentMethod).toLowerCase() !== 'cash' || saved.balanceDue !== 0) return false
  if (saved.total === 0) return true
  if (saved.status.toLowerCase() !== 'paid') return false
  const paymentKey = `IDEMP-PAY-${saved.invoiceId.replace(/-/g, '')}`
  return saved.paidAmount === saved.total && saved.payments.filter(p => p.idempotencyKey === paymentKey).length === 1
    && saved.payments.some(p => p.idempotencyKey === paymentKey
    && p.method.toLowerCase() === 'cash' && p.status.toLowerCase() === 'completed'
    && p.verificationStatus.toLowerCase() === 'confirmed' && !!p.confirmedAt && p.amount === saved.total)
}

/** Only per-item identity plus authoritative saved readback permits deletion. */
export async function acknowledgeOutbox(item: OutboxItem, outcome: unknown, evidence: unknown,
  session = captureOutboxSession()): Promise<boolean> {
  assertOwner(session)
  try { item = validItem(item) } catch { return false }
  const parsed = resultSchema.safeParse(outcome)
  if (!parsed.success || parsed.data.idempotencyKey !== item.idempotencyKey
    || parsed.data.type !== (item.type === 1 ? 'patient' : 'invoice')
    || (parsed.data.status === 'created' && parsed.data.id !== item.id) || !savedEvidence(item, parsed.data, evidence)) return false
  const current = (await readEntries(session, item.id))[0]
  if (!current?.item || current.status === 'expired' || JSON.stringify(current.item) !== JSON.stringify(validItem(item))) return false
  const acknowledged = await transaction<boolean>(session, ['items'], 'readwrite', (tx, done, guard) => {
    const store = tx.objectStore('items')
    const request = store.get([ownerKey(session), item.id])
    request.onsuccess = () => guard(() => {
      if (request.result?.revision !== current.revision) { done(false); return }
      store.delete([ownerKey(session), item.id])
      done(true)
    })
  })
  if (acknowledged) window.dispatchEvent(new Event('samstack:outbox-changed'))
  return acknowledged
}

let flushInFlight: { generation: number; promise: Promise<void> } | undefined

/** Current-owner-only flush. Server authorization is checked before sending any PHI. */
export function flushOutbox(session = captureOutboxSession()): Promise<void> {
  assertOwner(session)
  if (flushInFlight?.generation === session.generation) return flushInFlight.promise
  const promise = flush(session).finally(() => { if (flushInFlight?.promise === promise) flushInFlight = undefined })
  flushInFlight = { generation: session.generation, promise }
  return promise
}

async function flush(session: OutboxSession) {
  if (!navigator.onLine) return
  const entries = (await readOutbox(session)).filter(entry => entry.item && ['pending', 'retry'].includes(entry.status))
  if (!entries.length) return
  try {
    // Existing middleware checks subscription, active membership, setup and permissions.
    const permission = await api.post('/sync/push', { items: [] })
    assertOwner(session)
    if (permission.data?.synced !== 0 || permission.data?.skipped !== 0 || !Array.isArray(permission.data?.results)
      || permission.data.results.length !== 0) throw new Error()
  } catch {
    assertOwner(session)
    throw new Error('Offline sync is paused: write access could not be confirmed. Check connectivity and clinic access, then retry. Stored work is retained.')
  }
  // Bound each batch to the server limit; new enqueues are handled by the next drain.
  for (let offset = 0; offset < entries.length; offset += 500) {
    const current = new Map((await readOutbox(session)).filter(row => ['pending', 'retry'].includes(row.status))
      .map(row => [row.id, row.revision]))
    const batch = entries.slice(offset, offset + 500).filter(entry => current.get(entry.id) === entry.revision)
    if (!batch.length) continue
    let data: unknown
    try {
      assertOwner(session)
      data = (await api.post('/sync/push', { items: batch.map(entry => entry.item) })).data
    } catch {
      assertOwner(session)
      for (const entry of batch) await markOutcome(session, entry, 'retry')
      throw new Error('Offline delivery is uncertain. Stored requests and keys are retained for an unchanged retry.')
    }
    const response = z.object({ results: z.array(z.unknown()) }).safeParse(data)
    for (const entry of batch) {
      assertOwner(session)
      const item = entry.item!
      const matches = response.success ? response.data.results.filter(value => value && typeof value === 'object'
        && 'idempotencyKey' in value && value.idempotencyKey === item.idempotencyKey) : []
      const outcome = matches.length === 1 ? resultSchema.safeParse(matches[0]) : null
      if (!outcome?.success || outcome.data.type !== (item.type === 1 ? 'patient' : 'invoice')) {
        const rejected = matches.length === 1 && z.object({ id: z.literal(item.id), type: z.literal(item.type === 1 ? 'patient' : 'invoice'),
          status: z.enum(['rejected', 'invalid_payload', 'invalid_key_format', 'unsupported_type', 'duplicate_patient', 'conflict']) }).safeParse(matches[0]).success
        const retry = matches.length === 1 && z.object({ id: z.literal(item.id), type: z.literal(item.type === 1 ? 'patient' : 'invoice'),
          status: z.literal('retry_required') }).safeParse(matches[0]).success
        await markOutcome(session, entry, rejected ? 'rejected' : retry ? 'retry' : 'unknown')
        continue
      }
      let evidence: unknown
      try {
        evidence = (await api.get(`/${item.type === 1 ? 'patients' : 'invoices'}/${outcome.data.id}`)).data
      } catch { assertOwner(session) }
      if (!await acknowledgeOutbox(item, outcome.data, evidence, session)) await markOutcome(session, entry, 'unknown')
    }
  }
}
