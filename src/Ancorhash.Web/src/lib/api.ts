/** Client tipizzato per il backend Ancorhash (Azure Functions .NET). */

const API_BASE_URL =
  (import.meta.env.VITE_API_BASE_URL as string | undefined) ??
  'http://localhost:7071'

export interface NotarizeRequest {
  document_id: string
  /** SHA-256 del file: 64 caratteri hex (con o senza 0x) oppure Base64. */
  document_hash: string
  chain_id: number
  turnstile_token: string
}

/**
 * Ricevuta di ancoraggio su AncorhashRegistry: 200 se il record è on-chain
 * (ancorato ora o già prima), 202 se la transazione attende ancora conferma.
 */
export interface NotarizeResponse {
  status: 'success' | 'pending'
  document_id: string
  /** Digest canonico: hex minuscolo con prefisso 0x. */
  digest: string
  chain_id: number
  registry: string
  anchorer: string
  /** Orario del primo ancoraggio, ISO 8601 UTC; null finché è pending. */
  anchored_at: string | null
  anchored_at_unix: number | null
  /** Null se il documento era già ancorato: vale il record originale. */
  tx_hash: string | null
  already_anchored: boolean
  explorer_url: string | null
}

export interface ExtractResponse {
  status: string
  file_name: string
  content: string
  key_value_pairs: Record<string, string>
  page_count: number
}

export interface Chain {
  chain_id: number
  name: string
  estimated_cost_usd: number
  is_free: boolean
}

interface ChainsResponse {
  status: string
  chains: Chain[]
}

interface ApiErrorResponse {
  status: string
  detail: string
}

export class ApiError extends Error {
  readonly statusCode: number

  constructor(statusCode: number, detail: string) {
    super(detail)
    this.name = 'ApiError'
    this.statusCode = statusCode
  }
}

async function parseOrThrow<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let detail = `Errore ${response.status} dal backend.`
    try {
      const body = (await response.json()) as ApiErrorResponse
      if (body.detail) detail = body.detail
    } catch {
      // body non JSON: teniamo il messaggio generico
    }
    throw new ApiError(response.status, detail)
  }
  return (await response.json()) as T
}

export async function notarize(
  payload: NotarizeRequest,
): Promise<NotarizeResponse> {
  const response = await fetch(`${API_BASE_URL}/api/v1/notarize`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
  return parseOrThrow<NotarizeResponse>(response)
}

/** Reti EVM configurate sul relayer (solo chain_id e nome, mai RPC URL). */
export async function getChains(): Promise<Chain[]> {
  const response = await fetch(`${API_BASE_URL}/api/v1/chains`)
  const body = await parseOrThrow<ChainsResponse>(response)
  return body.chains
}

/**
 * Estrazione OCR via Azure AI. Attenzione: a differenza della notarizzazione,
 * qui il file viene inviato al backend (l'utente lo vede in chiaro nella UI).
 */
export async function extract(file: File): Promise<ExtractResponse> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch(`${API_BASE_URL}/api/v1/extract`, {
    method: 'POST',
    body: formData,
  })
  return parseOrThrow<ExtractResponse>(response)
}
