# Task 02: anchor a run of outputs with one Merkle root

Start after task 01 is merged, all three pull requests: the batch endpoint
changed here sits behind the API key that task 01 adds.

## Why

Sepolia already runs Glamsterdam, and with EIP-8037 every newly anchored
digest pays 97,920 gas for its storage slot. Measured there (see the gas
section of `src/Ancorhash.Contracts/README.md`): `anchor` takes 128,238 gas,
and `anchorBatch` of 10 digests 114,641 per digest. A batch shares only the
fixed cost of the transaction, so a run of 100 outputs would take about
11.3 million gas. Anchored as the root of a Merkle tree, the same run takes
about as much as one digest, whatever its size. Mainnet still prices storage
the old way, but the cost of a run should not depend on when Glamsterdam
reaches it.

A root also keeps the run off chain: what is public is one 32-byte value, not
one digest per output.

## Decisions already taken

1. **The contract does not change.** A root is anchored like any other
   digest, on the same registry at the same address.
2. **The tree is the Merkle Tree Hash of RFC 9162, section 2.1, with SHA-256.**
   - Leaf hash: `SHA-256(0x00 || d)`, where `d` is the 32-byte digest of an
     output.
   - Node hash: `SHA-256(0x01 || left || right)`.
   - With n > 1 leaves, `k` is the largest power of two smaller than n, and the
     tree hash is the node hash of the tree of the first k leaves and the tree
     of the rest.
   - An inclusion proof is `leaf_index`, `tree_size` and the audit path
     (section 2.1.3.1), verified with the algorithm of section 2.1.3.2.

   It is a published standard, the one of Certificate Transparency, and
   anyone can verify it with plain SHA-256 in any language, without an
   Ethereum library. The prefixes keep an inner node from passing as a leaf.
   The contract never checks proofs, so keccak256 would bring nothing.
3. **The leaves are the distinct digests of the request, sorted in ascending
   byte order.** The root then depends only on the set of outputs: a retry
   with another order or with duplicates gives the same root, the same
   receipts and no new transaction (hard rule 5). The position of an output
   in the run means nothing. A client that needs the order hashes it into its
   outputs.
4. **Every request to the batch endpoint builds a tree**, even with a single
   distinct digest: the root is then `SHA-256(0x00 || d)`, with an empty audit
   path. One endpoint, one kind of receipt. A single output that must be
   verifiable from the file alone still goes through `/notarize`, which
   anchors the digest itself.
5. **Receipts go back to the caller, and Ancorhash stores nothing.** A receipt
   is self-contained: with the output and its receipt, anyone can verify it
   with SHA-256 and one `anchoredAt` call. Keeping receipts on the server, so
   that a file alone is enough to find its proof, is a decision for the task
   of the Verify page.
6. **`POST /api/v1/notarize/batch` changes meaning in place.** No client
   depends on it yet, and the web app does not call it. It anchors one root
   per request instead of one slot per digest.

## Work, in two pull requests

Keep them separate and in this order: each one must build and pass its tests
on its own.

### PR 1: runs and receipts

**Core**

- The Merkle tree, with no dependencies: it builds the tree of decision 2 from
  leaves of any length, returns the root and the inclusion proof of each leaf,
  and verifies a proof. The API only ever passes 32-byte digests, but the RFC
  test leaves are not 32 bytes long.
- `NotarizationService`, for the batch command: parse the digests as today,
  take the distinct ones in ascending byte order, build the tree, anchor the
  root through the relayer as a single digest, and build one receipt per
  item.
- `MaxBatchSize` goes from 100 to 1,000. At 1,000 items the audit paths are
  10 hashes long and the response is about 1 MB.

**Infrastructure**

No change: the relayer already anchors a list of digests, and here the list
holds the root.

**Api**

- `POST /api/v1/notarize/batch`: the request does not change (`chain_id`, and
  `items` with 1 to 1,000 `{document_id, document_hash}`). Response:

  ```json
  {
    "status": "success",
    "root": "0x03d7a8299a76ebf542ef82bf0dcdb249a83bca93ecd1a7ae1a0bd509dc591b8d",
    "tree_size": 3,
    "chain_id": 11155111,
    "registry": "0x929673BD5629c84453aA1d2d733aA31836E62fCE",
    "anchorer": "0x…",
    "anchored_at": "2026-12-01T10:15:30Z",
    "anchored_at_unix": 1796120130,
    "tx_hash": "0x…",
    "already_anchored": false,
    "explorer_url": "https://sepolia.etherscan.io/tx/0x…",
    "receipts": [ … ]
  }
  ```

- `receipts` holds one receipt per item, in the order of the request. Items
  with the same digest share a leaf, and each receipt keeps its own
  `document_id`. A receipt repeats what it needs, so that it can be saved
  alone next to its output:

  ```json
  {
    "version": 1,
    "document_id": "invoice-002.pdf",
    "digest": "0x4c657c5f883141bb460987e482a0f0a61cd10d57028c8a787c9ba03ac74e6873",
    "proof": {
      "algorithm": "rfc9162-sha256",
      "leaf_index": 0,
      "tree_size": 3,
      "audit_path": [
        "0xfdca6dcac6382576252d359ac846455d1775e9fde26dd363e5205c8326fa3d98",
        "0x1b3ba5d1b4325a9b3816fceedc638340d0b237b59d343ea4c03f67ee93dd9162"
      ]
    },
    "root": "0x03d7a8299a76ebf542ef82bf0dcdb249a83bca93ecd1a7ae1a0bd509dc591b8d",
    "chain_id": 11155111,
    "registry": "0x929673BD5629c84453aA1d2d733aA31836E62fCE",
    "anchorer": "0x…",
    "anchored_at": "2026-12-01T10:15:30Z",
    "anchored_at_unix": 1796120130,
    "tx_hash": "0x…",
    "explorer_url": "https://sepolia.etherscan.io/tx/0x…"
  }
  ```

- HTTP status codes as for `/notarize`: 200 for a new or an already anchored
  root (then `tx_hash: null` and `already_anchored: true`), 202 with
  `status: "pending"` when the receipt of the transaction did not arrive in
  time, 400 for an invalid digest or for 0 or more than 1,000 items. In a 202
  the proofs are already final and only `anchored_at` is missing: the same
  request sent again later returns 200 with the time and sends nothing.
  Clients store the receipts of the 200 response.
- The receipts of `/notarize` get `"version": 1` too, and no `proof`: there
  the digest itself is anchored.

**Tools and docs**

- `tools/verify_receipt.py` is already in the repository, committed together
  with this task. It needs Python 3.9 or later and nothing else. It checks the
  file against the digest, the proof against the root and, with `--rpc`, the
  registry on chain, including that the RPC serves the receipt's chain. A
  receipt without `proof`, such as those of `/notarize`, is checked against
  the digest itself. Change it only if the receipt format requires it, and
  keep its exit codes: 0 when everything matches, 1 otherwise.
- `docs/receipts.md`:
  - the format of a receipt, version 1;
  - how to verify one by hand: SHA-256 of the file, the proof up to the root
    with the algorithm of RFC 9162 section 2.1.3.2, then `anchoredAt(root,
    anchorer)` in Etherscan's Read Contract;
  - how to verify one with `tools/verify_receipt.py`;
  - the worked example of appendix B;
  - privacy: a receipt carries hashes of other leaves of the run, and the
    first element of the audit path is often the leaf hash of another output.
    For outputs with few possible values, such as "approved" or "rejected",
    anyone holding a receipt could test guesses against it. Such outputs
    should be salted before hashing, as the contracts README already says for
    single digests.
- README: in the Notarization API section, describe the new batch response
  and link `docs/receipts.md`. In "How it works", add runs: many outputs, one
  record, and a receipt for each output. In the roadmap, tick runs.

### PR 2: records under the organisation's key

- Optional configuration `Blockchain:Anchorer:KeyVaultUri` and
  `Blockchain:Anchorer:KeyName`: a secp256k1 key in Azure Key Vault (curve
  `P-256K`, signed with `ES256K`, as the relayer key) that belongs to the
  organisation running the bots. It never needs funds. Both settings or
  neither, validated at startup like `Blockchain:Relayer`.
- When it is set, every record goes under the address of that key, for
  `/notarize` and for runs alike. For each digest or root:
  1. the anchorer is that address, both for the `anchoredAt` checks and for
     the pending transactions;
  2. the deadline is now plus `PendingAnchorings.Lifetime`, 10 minutes. When
     the pending entry expires, the signature has expired too, so two valid
     signed transactions for the same digest never coexist. The contract
     accepts a signature up to its deadline, included;
  3. the anchorer key signs the EIP-712 typed data
     `Anchor(bytes32 digest,address anchorer,uint256 deadline)`, domain
     `{name: "Ancorhash", version: "1", chainId, verifyingContract: registry}`;
  4. the relayer sends `anchorBySig(digest, anchorer, deadline, signature)`
     and pays the gas, with the nonce handling of today.

  The service anchors one digest per call, either a single digest or a root,
  so no batch signature is needed.
- A transaction mined after its deadline reverts with `ExpiredSignature` and
  anchors nothing; the next request signs again.
- The cost estimate stays on `anchor(bytes32)`. Estimating `anchorBySig`
  would need a valid signature, and on anvil it costs about 9,300 gas more
  (55,536 against 46,218): within the precision of an estimate in fiat.
- README: how to create the anchorer key and give the Function App the
  "Key Vault Crypto User" role on it, as for the relayer key, and why it
  needs no funds.

## Tests

Wherever a test expects a receipt, use the default receipt timeout: with one
second the receipt is read only once (see `PendingAnchoringTests`).

**PR 1**

- Unit tests:
  - the RFC test leaves of appendix A: the root of every tree size from 1 to
    8;
  - every inclusion proof in those trees verifies, and fails with one byte
    of the audit path changed, with a wrong `leaf_index`, with a wrong
    `tree_size`, and with a path element added or removed;
  - the run of appendix B: the root and the three proofs, exactly;
  - the tree of one digest of appendix C;
  - the same set of digests in another order, or with duplicates, gives the
    same root and the same receipts;
  - 0 and 1,001 items are rejected, 1,000 are accepted;
  - the mapping of the batch response and of the receipts, against a fake
    relayer.
- Integration tests against anvil:
  - a run of 10 outputs sends one transaction; `anchoredAt(root, relayer)`
    equals the `anchored_at_unix` of every receipt, and nothing is recorded
    for the single digests;
  - the same run again, in another order, returns 200 with
    `already_anchored: true`, the same root and the same receipts, and sends
    nothing;
  - end to end: the receipts of an anchored run, saved next to their outputs,
    pass `python3 tools/verify_receipt.py RECEIPT --file OUTPUT --rpc <anvil>`.
    Skipped when `python3` is not on the PATH.

**PR 2**

- The EIP-712 hash computed by Nethereum equals `hashAnchor(digest, anchorer,
  deadline)` read from the contract.
- With an anchorer key, a local key standing in for Key Vault as in task 01,
  a single digest and a run are recorded under the anchorer's address and
  nothing under the relayer's; the relayer's nonce goes up and the
  anchorer's does not.
- The same digest again is already anchored under the anchorer, and sends
  nothing.
- A signature whose deadline has passed, with the clock of the test set
  before the block time, is refused by the contract: the request fails and
  nothing is recorded.

## Done when

- A run of N outputs costs one transaction and one storage slot, whatever N.
- Every receipt verifies with `tools/verify_receipt.py`, offline and against
  the chain.
- The same run sent twice, in any order, makes one transaction.
- With an anchorer key, the records are under its address and the relayer
  only pays.
- After PR 1 is merged, a run of three outputs anchored on Sepolia from the
  local API: its `gasUsed` goes into the Sepolia table of the contracts
  README, and the outputs with their receipts into `docs/examples`, next to
  the single-output example, with the README section "Verify it yourself"
  showing one of them. This step is manual.
- `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes` pass.

## Not in this task

The Verify page and any storage of receipts on the server, the Power Automate
Desktop subflow that saves receipts next to the outputs, the custom
connector, a helper for salted digests, mainnet.

## Appendix: test vectors

All hashes are SHA-256.

**A. RFC test leaves.** These are the leaves used by the reference
implementation of Certificate Transparency, in hex (the first one is empty):
`""`, `00`, `10`, `2021`, `3031`, `40414243`, `5051525354555657`,
`606162636465666768696a6b6c6d6e6f`. Roots of the trees of the first n leaves:

| n | Root |
|---|---|
| 1 | `6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d` |
| 2 | `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` |
| 3 | `aeb6bcfe274b70a14fb067a5e5578264db0fa9b51af5e0ba159158f329e06e77` |
| 4 | `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 5 | `4e3bbb1f7b478dcfe71fb631631519a3bca12c9aefca1612bfce4c13a86264d4` |
| 6 | `76e67dadbcdf1e10e1b74ddc608abd2f98dfb16fbce75277b5232a127f2087ef` |
| 7 | `ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c` |
| 8 | `5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328` |

**B. A run of three outputs.** The outputs are the UTF-8 strings below, with
no newline:

| Output | Digest | Leaf |
|---|---|---|
| `invoice-001.pdf contents` | `0x550c155c0bf2de10ae14412b0085913e4cc7adb020fbd09ae893844954cc62ed` | 1 |
| `invoice-002.pdf contents` | `0x4c657c5f883141bb460987e482a0f0a61cd10d57028c8a787c9ba03ac74e6873` | 0 |
| `invoice-003.pdf contents` | `0x64f18b8c4a73377acf0b32d7545683043123182274fc02ee6d9d1ce8ecaec174` | 2 |

Root: `0x03d7a8299a76ebf542ef82bf0dcdb249a83bca93ecd1a7ae1a0bd509dc591b8d`,
`tree_size` 3. Audit paths:

- leaf 0: `0xfdca6dcac6382576252d359ac846455d1775e9fde26dd363e5205c8326fa3d98`,
  `0x1b3ba5d1b4325a9b3816fceedc638340d0b237b59d343ea4c03f67ee93dd9162`;
- leaf 1: `0x8004565c9c44efd938f02224a6c1f8c261631f6134e833a8ce0af466735ffe9c`,
  `0x1b3ba5d1b4325a9b3816fceedc638340d0b237b59d343ea4c03f67ee93dd9162`;
- leaf 2: `0x230cbcf07481bcfb0d3abcbcd9b2a17cd0ba19231ff9c1730dda853c3385cb3d`.

**C. A tree of one digest.** The digest of task 01's test vector, the
SHA-256 of `ancorhash`
(`0x10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1`), gives
the root `0x63f1b9def4bc8a8ce93aea7690061ec96ce36a7617d1634e1a3b0b57e7a0a94d`,
with `leaf_index` 0, `tree_size` 1 and an empty audit path.
