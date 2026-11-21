# Ancorhash contracts

## AncorhashRegistry

A public, append-only register of 32-byte digests. One record says: **this
address submitted this digest no later than this time.** Nothing more, and
nobody can change it afterwards.

### What a record proves, and what it does not

- It proves that `anchorer` had `digest` at the block time of the anchoring,
  and that the record has not changed since. That holds without trusting
  Ancorhash: the contract has no owner and no upgrade path.
- It does not prove who `anchorer` is in the real world. An organisation makes
  its address meaningful by publishing it, in its own documentation or in the
  receipts it hands out.
- It does not prove that the content was *created* at that time, or that it is
  correct. Only that it existed, in that exact form.
- Block time is set by the chain's validators or sequencer within protocol
  bounds. It is solid evidence of "no later than" and of ordering, not a
  qualified timestamp in the eIDAS sense.

### Design

1. **Open writes.** Anyone can anchor, and every record is bound to the address
   that wrote it (`msg.sender`). Attribution comes from the signature, not from
   a field anyone can fill in.
2. **First write wins, per (digest, anchorer) pair.** Anchoring the same digest
   again is a no-op that returns the original time. A bot that retries after a
   timeout can neither fail nor move its own record. The same goes for a whole
   batch.
3. **Records are isolated per anchorer.** Whoever anchors your digest before
   you, front-running included, writes their own record and cannot touch yours.
4. **No owner, no pause, no upgrade path.** Nobody, including the deployer, can
   change or delete a record once it is written.

### What a digest can be

The registry treats every digest the same way. The off-chain receipt decides
what a digest stands for:

- **The SHA-256 of a file.** This is what the web app computes in the browser
  and what PowerShell's `Get-FileHash` returns, as hex. Power Automate Desktop's
  *Hash from file* returns the same 32 bytes in Base64.
- **The root of a Merkle tree over every output of one bot run.** One anchoring
  per run instead of one per output. Verifying a single output then needs its
  Merkle proof, which travels in the receipt.
- **A salted commitment, for low-entropy outputs.** A short JSON or a status
  line can be confirmed by guessing its content and hashing the guess. Commit
  to `H(salt ‖ content)` instead, and keep the salt in the receipt.

### Verifying

`anchoredAt(digest, anchorer)` is a free read from any public RPC node, or from
the *Read contract* tab of a block explorer once the source is verified. It
needs the content, to recompute the digest, and the anchorer's address: one
public address per organisation, not a receipt per document.

Finding every anchorer of a digest without knowing any of them means scanning
`Anchored` events. Most public RPC nodes cap the block range of a log query, so
at scale that lookup belongs in an indexer, not in a static page.

### Gas

Measured on a local chain, full transaction cost:

| Operation | Gas |
|---|---|
| `anchor`, first time | 46,167 |
| `anchor`, retry (no-op) | 24,197 |
| `anchorBatch`, 10 new digests | 27,178 per digest |
| `anchorBatch`, 100 new digests | 25,210 per digest |
| For reference: hash in the calldata of a plain transaction (previous approach) | ~21,500 |

### Deploying

The script deploys through the deterministic CREATE2 deployer
(`0x4e59b44847b379578588920cA78FbF26c0B4956C`, present on most EVM chains).
The same commit, compiler settings and salt give the same address on every
chain. It is idempotent: if the registry is already at the predicted address,
it does nothing.

Remappings are pinned in `remappings.txt`, with auto-detection off. A leftover
library in `lib/` would otherwise enter the compiler input, change the bytecode
and move the address.

```bash
# once: the key goes into an encrypted keystore, never into a file or an env var
cast wallet import ancorhash-deployer --interactive

forge script script/DeployRegistry.s.sol \
  --rpc-url <rpc-url> --account ancorhash-deployer --broadcast
# add --verify and an explorer API key to publish the source
```

### Development

```bash
forge build
forge test                    # unit, fuzz and invariant tests
forge test --gas-report
FOUNDRY_PROFILE=ci forge test # more fuzz and invariant runs, as in CI
```

Slither reports one informational finding: `_anchor` compares a stored
timestamp with zero. That comparison checks whether a record exists; it does
not make any decision that depends on the current time.
