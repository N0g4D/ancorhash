# Task 01: anchor through AncorhashRegistry

## Why

Today the relayer sends a 0-ETH transaction, with the hash in its `data`
field, to the `wallet_address` of the request, and the web app always passes
`MOCK_WALLET_ADDRESS` (the address of vitalik.eth). The proof exists, but
nobody can find it from the file alone: verifying needs the transaction hash. `AncorhashRegistry` (`src/Ancorhash.Contracts`) fixes that, and it is the
base for proving the outputs of bots. Read its README first.

## Decisions already taken

- Demo network: Ethereum Sepolia (chain id 11155111). The registry has the same
  address on every chain (CREATE2); the backend reads it from configuration.
- A digest is 32 bytes. The API accepts it as 64 hex characters (with or
  without `0x`, any case) or as Base64, which is what Power Automate Desktop's
  *Hash from file* returns.
- Anchoring is idempotent end to end. A digest already anchored by the same
  anchorer is a success (HTTP 200) that returns the original time. Bots retry,
  and a retry must never fail.
- `wallet_address` is removed from the API.
- In the cloud no private key lives in app memory or in a Key Vault secret. The
  relayer key, and an organisation's signing key when configured, are
  non-exportable secp256k1 keys in Azure Key Vault.

## Prerequisite, done by hand: the registry on Sepolia

This needs a funded key, so it is not part of the code changes.

```bash
# once: Sepolia ETH from a faucet on the deployer address, then
cast wallet import ancorhash-deployer --interactive

cd src/Ancorhash.Contracts
forge script script/DeployRegistry.s.sol \
  --rpc-url "$SEPOLIA_RPC_URL" --account ancorhash-deployer \
  --broadcast --skip-simulation \
  --verify --etherscan-api-key "$ETHERSCAN_API_KEY"
```

Done: the registry is at `0x929673BD5629c84453aA1d2d733aA31836E62fCE` on
Sepolia, block 12187372.

The script prints the address; it goes into `Blockchain:RegistryAddress`.

## Work, in three pull requests

Keep them separate and in this order: each one must build and pass its tests
on its own.

### PR 1: the relayer anchors through the registry

**Core**

- A `Digest` value type (32 bytes) with `TryParse(string, out Digest)`:
  accepts `^(0x)?[0-9a-fA-F]{64}$` or Base64 that decodes to exactly 32 bytes.
  Rejects anything else, and rejects the zero digest, which the contract would
  revert. Output form: lowercase hex with `0x`.
- Replace `IBlockchainRelayer.SendNotarizationAsync(chainId, walletAddress,
  documentHash)` with an anchoring call that takes one or more digests and
  returns one result per digest: the digest, `AnchoredAt` (`DateTimeOffset`),
  `TransactionHash` (null when it was already anchored), `AlreadyAnchored`.
- `NotarizationService` drops the wallet validation and uses `Digest`.
  Remove `IWalletAddressValidator` and its Nethereum implementation if nothing
  else uses them.

**Infrastructure**

- Typed Nethereum bindings for the registry, in
  `Infrastructure/Blockchain/Registry/`: function messages for `anchor`,
  `anchorBatch`, `anchorBySig`, `anchorBatchBySig`, the views `anchoredAt` and
  `hashAnchorBatch`, and an event DTO for
  `Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp)`.
  Generate them from `src/Ancorhash.Contracts/out/AncorhashRegistry.sol/AncorhashRegistry.json`
  after `forge build`, or write them by hand from the same ABI.
- The anchoring flow:
  1. For each digest, read `anchoredAt(digest, anchorer)`. Non-zero means
     already anchored: no transaction for it.
  2. If any digest is new, send `anchorBatch(newDigests)`, wait for the
     receipt and take the timestamps from the `Anchored` events.
  3. If the receipt does not arrive within 90 seconds, return the transaction
     hash as pending instead of failing. On Sepolia a block takes about 12
     seconds.
  4. Keep the current mapping of node errors to
     `BlockchainUnavailableException` and `BlockchainTransactionException`,
     and the EIP-1559 fees.
- Configuration: `Blockchain:RegistryAddress` for every network, with an
  optional override `Blockchain:Networks:{chainId}:RegistryAddress`, validated
  at startup as a checksummed address; `Blockchain:Networks:{chainId}:ExplorerUrl`
  for `explorer_url`.
- `EstimateFiatCostAsync` estimates a call to `anchor(bytes32)` instead of a
  plain transaction.
- No code path sends a plain transaction with the digest in its `data` field
  any more.

**Api**

- `POST /api/v1/notarize`: `document_id`, `document_hash` (hex or Base64),
  `chain_id`. No `wallet_address`.
- `POST /api/v1/notarize/batch`: `items` (1 to 100 of
  `{document_id, document_hash}`) and `chain_id`, anchored with one
  transaction.
- Response, for each digest: `document_id`, `digest`, `chain_id`, `registry`,
  `anchorer`, `anchored_at` (ISO 8601, UTC), `anchored_at_unix`, `tx_hash`
  (null when already anchored), `already_anchored`, `explorer_url`.
- HTTP 200 for new and already anchored digests alike; 202 with `tx_hash` and
  `status: "pending"` when the receipt did not arrive in time; 400 for an
  invalid digest. No 409.
- Leave Extract, Chains and the checkout flow alone in this task.

**Web**

- Stop sending `wallet_address`, remove `MOCK_WALLET_ADDRESS`, and show
  `anchored_at` and `already_anchored` from the new response.

### PR 2: keys in Azure Key Vault

- Add `Nethereum.Signer.AzureKeyVault` and `Azure.Security.KeyVault.Keys`, and
  move every Nethereum package to the same version as the signer package
  (today `Nethereum.Web3` is 6.1.0).
- The relayer account becomes
  `new ExternalAccount(new AzureKeyVaultExternalSigner(keyName, vaultUri, credential), chainId)`,
  initialised with `InitialiseAsync()` before use (`NethereumWeb3Factory`).
- Configuration: `Blockchain:Relayer:KeyVaultUri` and
  `Blockchain:Relayer:KeyName`. `Evm:RelayerPrivateKey` stays only for local
  development against anvil: startup must fail if it is set while any network
  has `IsTestnet = false`.
- Signed anchoring for an organisation moves to the task on Merkle roots.
  There the organisation signs a single root with `anchorBySig`, so no batch
  signature is needed.
- Document the Key Vault setup in the README:
  `az keyvault key create --kty EC-HSM --curve P-256K --ops sign verify`
  (`EC` instead of `EC-HSM` on the standard tier), and the "Key Vault Crypto
  User" role for the Function App's managed identity.

### PR 3: separate the public demo from the API

Today the endpoints use `AuthorizationLevel.Function` and the web app calls
them without a key, which works only locally: Core Tools does not check keys.

- `POST /api/v1/demo/notarize`: anonymous, Turnstile and IP rate limit as
  today (update the path check in `RateLimitingMiddleware`), single digest,
  only networks with `IsTestnet = true`.
- `POST /api/v1/notarize` and `/notarize/batch`: anonymous at the trigger, but
  they require an `X-Api-Key` header. Compare the SHA-256 of the presented key,
  in constant time, with a set of key hashes from configuration (Key Vault
  secret `Api--KeyHashes`). No Turnstile on these endpoints.
- Rate limit per API key on the integration endpoints. It stays in memory,
  so per instance: acceptable on a testnet, to revisit before mainnet.
- The web app calls the demo endpoint.

## Tests

- A new xUnit project, `tests/Ancorhash.Tests`, added to `Ancorhash.slnx`.
- Unit tests:
  - `Digest.TryParse` with hex, `0x`, uppercase, Base64, wrong lengths, the
    zero digest and garbage;
  - test vector: the SHA-256 of the ASCII string `ancorhash` is
    `0x10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1` in hex
    and `EP0rt3iqDIabIDrdQqO+wH505CgVM4i4DbNAqd9HRNE=` in Base64, and both must
    parse to the same digest;
  - response mapping, and idempotency against a fake relayer.
- Integration tests against anvil, marked `Category=Integration` and skipped
  when `anvil` is not on the PATH:
  - start anvil on a free port and deploy the registry with
    `forge script script/DeployRegistry.s.sol --broadcast` and anvil's account 0;
  - a new digest gets a transaction, and its timestamp matches `anchoredAt` on
    chain;
  - the same digest again returns `already_anchored = true`, and the relayer's
    nonce does not change;
  - a batch with duplicates reports what the contract reports;
  - the relayer signs through `ExternalAccount` with a local key standing in for
    Key Vault, anchors a digest on anvil, and the record is under that key's
    address;
- `dotnet test` passes without anvil (integration tests skipped) and with it.

## Done when

- All anchoring goes through the registry, and `MOCK_WALLET_ADDRESS` is gone
  from the code.
- The same digest sent twice returns 200 both times, with the same
  `anchored_at`, `tx_hash: null` the second time, and one transaction in total.
- The hex and Base64 forms of the test vector give the same `digest`.
- The cloud configuration holds no private key: only Key Vault keys.
- Startup fails when `Evm:RelayerPrivateKey` is set and a mainnet network is
  configured.
- `dotnet build`, `dotnet test` and `forge test` pass.

## Not in this task

The Verify page, the PAD subflow and the custom connector, mainnet, and the
cleanup of Extract and checkout.
