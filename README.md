# Ancorhash

**Enterprise Web3 Document Notarization platform. Zero Data Leakage, Gas-Sponsored, Fiat-Ready.**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-18.2-61DAFB?logo=react&logoColor=black)](https://reactjs.org/)
[![Ethereum](https://img.shields.io/badge/Ethereum-3C3C3D?logo=ethereum&logoColor=white)](https://ethereum.org)
[![Azure AI](https://img.shields.io/badge/Azure%20AI-0078D4?logo=microsoftazure&logoColor=white)](https://azure.microsoft.com/en-us/products/ai-services/ai-document-intelligence)

---

## What is Ancorhash?

Ancorhash is a B2B SaaS platform designed to bring cryptographic proof of existence and data integrity to the Enterprise world (Banks, Healthcare, Public Administration) without the compliance and UX hurdles typical of Web3 applications.

Ancorhash allows users to timestamp and notarize documents on EVM-compatible blockchains (Ethereum, Polygon) providing mathematical certainty that a document existed at a specific time and has not been altered since. 

### The Enterprise Differentiators
1. **Zero Data Leakage:** Due to GDPR and corporate compliance, sensitive documents (contracts, medical records) cannot be uploaded to third-party cloud services. Ancorhash computes the SHA-256 cryptographic hash of the file **locally inside the user's browser**. Only the 32-byte hash is sent to the backend. The document never leaves the user's device.
2. **Gas-Sponsored Relayer:** Corporate clients cannot manage crypto wallets, private keys, or hold volatile cryptocurrencies on their balance sheets. Ancorhash uses a Relayer architecture: the backend signs and pays for the blockchain transaction (Gas) on behalf of the user.
3. **Fiat Billing (Stripe-Ready):** Users pay a predictable, standard fiat fee (e.g., in USD/EUR) via credit card for the service. The backend orchestrates a real-time decentralized pricing oracle to calculate the gas cost and applies a fixed service markup.

---

## System Architecture

The V2 architecture separates the local hashing and intelligence layer from the Web3 transactional layer, ensuring complete data privacy.

~~~mermaid
graph LR
    USER["User"] -->|"Drags PDF"| BROWSER["<b>React Frontend</b><br/><i>Local Hashing</i>"]
    
    BROWSER -.->|"Opt-in OCR"| AZURE["<b>Azure AI</b><br/><i>Extraction</i>"]
    BROWSER -->|"1. Check Costs"| API["<b>.NET 10 Backend</b><br/><i>Relayer Engine</i>"]
    BROWSER -->|"2. Fiat Checkout"| STRIPE["<b>Stripe</b><br/><i>Billing Mock</i>"]
    BROWSER -->|"3. POST /notarize"| API

    API -->|"Fetch USD Price"| ORACLE["<b>CoinGecko</b><br/><i>Oracle</i>"]
    API -->|"Simulate & Broadcast"| ETH["<b>Blockchain</b><br/><i>EVM Node</i>"]

    style BROWSER fill:#20232a,stroke:#61dafb,color:#fff
    style API fill:#512bd4,stroke:#512bd4,color:#fff
    style ETH fill:#3C3C3D,stroke:#627EEA,color:#fff
    style AZURE fill:#0078D4,stroke:#0078D4,color:#fff
    style STRIPE fill:#635bff,stroke:#635bff,color:#fff
~~~

### The Notarization Flow (Sequence)

~~~mermaid
sequenceDiagram
    participant B as React Frontend
    participant A as .NET Backend API
    participant O as CoinGecko Oracle
    participant R as EVM Blockchain Node
    
    B->>B: Computes SHA-256 locally (Zero Data Leakage)
    B->>A: GET /api/v1/chains (Request pricing)
    A->>O: Fetch MATIC/ETH USD price
    A->>R: Estimate Gas Limit & Price
    A-->>B: Returns supported chains & dynamic USD costs
    B->>B: Displays Checkout Mock (Gas Cost + Service Fee)
    B->>A: POST /api/v1/notarize (Hash, Turnstile Token, ChainId)
    A->>A: Validate Anti-bot & IP Rate Limit
    A->>R: anchoredAt(digest, relayer) on AncorhashRegistry
    A->>R: anchorBatch(new digests), EIP-1559 (skipped if already anchored)
    R-->>A: Receipt with Anchored events
    A-->>B: Receipt: anchored_at, tx_hash, explorer_url
~~~

---

## Technology Stack

### Backend (`src/Ancorhash.Api` / `Core` / `Infrastructure`)
Built with **C# .NET 10 (Isolated Azure Functions)** for maximum scalability and serverless execution.
- **Blockchain:** `Nethereum` for RPC interactions and EIP-1559 transaction building.
- **Security:** `Cloudflare Turnstile` server-side validation and custom IP-based `RateLimitingMiddleware` using `IMemoryCache` to prevent wallet draining.
- **Pricing Oracle:** Real-time fiat conversion via `CoinGecko` API, heavily cached and designed to fail-soft (fallback to $0) in case of rate limits.
- **Observability:** `OpenTelemetry` configured for Enterprise-grade logging.

### Frontend (`src/Ancorhash.Web`)
Built with **React, TypeScript, Vite, and Tailwind CSS**.
- **Local Hashing:** Fast, memory-safe client-side SHA-256 generation using `crypto.subtle`.
- **UI/UX:** Minimalist, monochromatic design optimized for B2B dashboards. Includes a Mock Checkout interface for live product demonstrations.
- **Anti-bot:** `@marsidev/react-turnstile` integrated seamlessly into the submission flow.

---

## Local Development Setup

### Prerequisites
- Node.js 20+
- .NET 10 SDK
- Azure Functions Core Tools (`func`)

### 1. Backend Setup
Navigate to the API project and restore dependencies:
~~~bash
cd src/Ancorhash.Api
dotnet restore
~~~
Create a `local.settings.json` file with standard RPC URLs, the address of `AncorhashRegistry` and the relayer key. The registry has the same address on every chain: `script/DeployRegistry.s.sol` prints it (see `src/Ancorhash.Contracts/README.md`), and the API refuses to start without a checksummed address. The relayer signs with a secp256k1 key in Azure Key Vault (see [Relayer Key in Azure Key Vault](#relayer-key-in-azure-key-vault)): locally, `DefaultAzureCredential` uses your `az login`, and your account needs the same role on the key as the Function App:
~~~json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Logging__LogLevel__Default": "Information",
    "Blockchain__Relayer__KeyVaultUri": "https://YOUR-VAULT.vault.azure.net/",
    "Blockchain__Relayer__KeyName": "ancorhash-relayer",
    "Turnstile__SecretKey": "1x0000000000000000000000000000000AA",
    "Blockchain__RegistryAddress": "0xYOUR_REGISTRY_ADDRESS",
    "Blockchain__Networks__11155111__RpcUrl": "https://sepolia.infura.io/v3/...",
    "Blockchain__Networks__11155111__Name": "Ethereum Sepolia",
    "Blockchain__Networks__11155111__NativeCoinId": "ethereum",
    "Blockchain__Networks__11155111__IsTestnet": "true",
    "Blockchain__Networks__11155111__ExplorerUrl": "https://sepolia.etherscan.io"
  },
  "Host": {
    "CORS": "http://localhost:5173",
    "CORSCredentials": false
  }
}
~~~
For local development on test networks, such as anvil or Sepolia with a throwaway key, `Evm__RelayerPrivateKey` can take the place of the two `Blockchain__Relayer__*` settings. The API refuses to start with it in Azure, next to the Key Vault settings, or while any network has `IsTestnet` false. It tells local from Azure by the `Development` environment, which Core Tools sets in `AZURE_FUNCTIONS_ENVIRONMENT` and `local.settings.json` cannot change; in Azure the environment is `Production` unless that setting says otherwise, so never set it to `Development` there.

Run the backend:
~~~bash
dotnet run
~~~

### 2. Frontend Setup
In a new terminal, navigate to the Web project:
~~~bash
cd src/Ancorhash.Web
npm install
~~~
Create a `.env` file for the Turnstile public test key:
~~~env
VITE_TURNSTILE_SITEKEY="1x00000000000000000000AA"
~~~
Start the Vite development server:
~~~bash
npm run dev
~~~
Open `http://localhost:5173` in your browser.

### 3. Tests
~~~bash
dotnet test Ancorhash.slnx
~~~
The integration tests start `anvil` on a free port and deploy the registry with `forge script`. The relayer signs either with a raw key or through `ExternalAccount`, the account used with Key Vault, with a local key in place of the vault. They are marked `Category=Integration` and skipped when `anvil` or `forge` is not on the `PATH`; `dotnet test --filter "Category!=Integration"` leaves them out explicitly.

---

## Relayer Key in Azure Key Vault

The relayer pays the gas and is the anchorer of every record. In the cloud it holds no private key: it signs with a non-exportable secp256k1 key in Azure Key Vault, and the private key never leaves the vault.

1. Create the key:
   ~~~bash
   az keyvault key create --vault-name <vault> --name ancorhash-relayer \
     --kty EC-HSM --curve P-256K --ops sign verify
   ~~~
   `P-256K` is Azure's name for secp256k1, and the relayer signs with the matching algorithm, `ES256K`. `EC-HSM` keys are protected by an HSM and need the Premium tier; on the Standard tier use `--kty EC`.

2. Give the Function App's managed identity the **Key Vault Crypto User** role on the key, which lets it read the public key and sign. The vault must use the Azure RBAC permission model:
   ~~~bash
   az role assignment create --role "Key Vault Crypto User" \
     --assignee-object-id "$(az functionapp identity show --name <function-app> --resource-group <rg> --query principalId -o tsv)" \
     --assignee-principal-type ServicePrincipal \
     --scope "$(az keyvault show --name <vault> --query id -o tsv)/keys/ancorhash-relayer"
   ~~~
   With a user-assigned identity, also set `AZURE_CLIENT_ID` to its client ID, so that `DefaultAzureCredential` picks it.

3. In the Function App settings, set `Blockchain__Relayer__KeyVaultUri` to `https://<vault>.vault.azure.net/` and `Blockchain__Relayer__KeyName` to `ancorhash-relayer`. Leave out `Evm__RelayerPrivateKey`, both as an app setting and as an `Evm--RelayerPrivateKey` secret in the vault that `KeyVault__Uri` loads: the API refuses to start with a raw key in Azure.

4. Fund the relayer's address on each network. The API logs it the first time it uses a network; to read it from the key beforehand (needs `python3` and Foundry's `cast`):
   ~~~bash
   PUBKEY=$(az keyvault key show --vault-name <vault> --name ancorhash-relayer --query key -o json \
     | python3 -c 'import base64, json, sys; k = json.load(sys.stdin); d = lambda v: base64.urlsafe_b64decode(v + "=" * (-len(v) % 4)); print("0x" + (d(k["x"]) + d(k["y"])).hex())')
   cast to-check-sum-address "0x$(cast keccak "$PUBKEY" | cut -c 27-)"
   ~~~

Do not create new versions of the key. The relayer always signs with the current version, and each version is a different key pair, so a different relayer address: receipts already issued stay verifiable under the old address, but a digest sent again would be anchored again under the new one, with a new time.

---

## Notarization API

Both endpoints take digests computed by the client: 32 bytes, as 64 hex characters (with or without `0x`, any case) or as Base64, which is what Power Automate Desktop's *Hash from file* returns. The zero digest is rejected.

- `POST /api/v1/notarize`: `document_id`, `document_hash`, `chain_id`.
- `POST /api/v1/notarize/batch`: `items` (1 to 100 of `{document_id, document_hash}`) and `chain_id`, anchored with one transaction. The response holds one entry per item, in the same order.

Each entry is a self-contained receipt:

~~~json
{
  "status": "success",
  "document_id": "invoice-42.pdf",
  "digest": "0x10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1",
  "chain_id": 11155111,
  "registry": "0x…",
  "anchorer": "0x…",
  "anchored_at": "2026-11-22T10:15:30Z",
  "anchored_at_unix": 1795342530,
  "tx_hash": "0x…",
  "already_anchored": false,
  "explorer_url": "https://sepolia.etherscan.io/tx/0x…"
}
~~~

Anchoring is idempotent. A digest the relayer has already anchored returns HTTP 200 with the original `anchored_at`, `tx_hash: null` and `already_anchored: true`, and costs no transaction, so retrying a request is always safe. When the transaction is sent but its receipt does not arrive within `Blockchain:ReceiptTimeoutSeconds` (90 by default), the response is HTTP 202 with `status: "pending"`, the `tx_hash` and `anchored_at: null`. Sending the same request again once that transaction is mined returns the time. A retry while it is still pending returns HTTP 202 with the same `tx_hash` and sends nothing: each instance remembers its pending transactions for 10 minutes, so a transaction dropped from the mempool does not block the digest for longer. With several instances, a retry can at worst send one more transaction, which anchors nothing new. An invalid digest is HTTP 400.

Anyone can verify a receipt without trusting Ancorhash: `anchoredAt(digest, anchorer)` on the registry returns `anchored_at_unix`.

---

## Security & Protections

Since Ancorhash pays the transaction fees, the `/api/v1/notarize` endpoint is a highly critical surface. It is protected by a multi-layered defense strategy:
1. **IP Rate Limiting:** Discards requests aggressively at the middleware layer if an IP exceeds the allowed threshold (HTTP 429), saving CPU and API calls.
2. **Cloudflare Turnstile:** Blocks headless browsers and automated scripts via cryptographic challenges (HTTP 403).
3. **Fail-Safe Orchestration:** Any failure in the RPC nodes or pricing oracles is caught, logged via OpenTelemetry, and handled gracefully without crashing the host process.

---

## Historical Note: The V1 PoC
Ancorhash originally started as a Python and FastAPI project (`azure-web3-notarizer`). That initial proof-of-concept acted as a pure API-first middleware. However, it relied on downloading the actual PDF payload to the server for processing. To meet stringent Enterprise data privacy requirements, the architecture was entirely reimagined and rewritten in .NET and React (V2) to achieve true Zero Data Leakage.

---
<p align="center">
  <b>Ancorhash</b> — Trust through Cryptography.
</p>