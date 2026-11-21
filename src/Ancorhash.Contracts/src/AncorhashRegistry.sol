// SPDX-License-Identifier: Apache-2.0
pragma solidity 0.8.28;

import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {SignatureChecker} from "@openzeppelin/contracts/utils/cryptography/SignatureChecker.sol";

/// @title AncorhashRegistry
/// @author Alessio Malerba
/// @notice A public, append-only register of 32-byte digests. Each record proves
///         that `anchorer` authorised the anchoring of `digest` no later than the
///         recorded time.
/// @dev The registry does not know what a digest is. It can be the SHA-256 of a
///      file, of the output of a bot or an agent, or the root of a Merkle tree
///      that commits to every output of one run. That meaning lives in the
///      off-chain receipt, not here.
///
///      There are two ways to anchor, into the same register:
///      - directly: the record goes to msg.sender, who pays the gas;
///      - by signature: the anchorer signs an EIP-712 message, and anyone can
///        submit it and pay the gas. The anchorer can be an EOA, such as a
///        secp256k1 key that never leaves an HSM, or an ERC-1271 account, so an
///        organisation can rotate its keys without changing the address its
///        records live under.
///
///      Invariants:
///      1. Every record is bound to whoever authorised it: msg.sender for direct
///         calls, the verified signer for signed ones. Whoever submits a signed
///         call and pays for it gets no record of their own from it.
///      2. First write wins, per (digest, anchorer) pair. Anchoring the same
///         digest again, directly or by replaying a signature, is a no-op that
///         returns the original time: retries can neither fail nor move a record.
///      3. Records are isolated per anchorer. Whoever anchors your digest before
///         you, front-running included, writes their own record and cannot touch
///         yours.
///      4. No owner, no pause, no upgrade path. Nobody, including the deployer,
///         can change or delete a record once it is written.
///
///      Signed messages carry no nonce, on purpose. A replayed signature is a
///      no-op (invariant 2), the EIP-712 domain binds it to one chain and one
///      registry, and its deadline bounds how late it can land. Without nonces,
///      signed anchorings can be submitted in any order and in parallel.
contract AncorhashRegistry is EIP712 {
    /// @notice EIP-712 type of a signed single anchoring.
    bytes32 public constant ANCHOR_TYPEHASH = keccak256("Anchor(bytes32 digest,address anchorer,uint256 deadline)");

    /// @notice EIP-712 type of a signed batch: one signature for all the digests.
    bytes32 public constant ANCHOR_BATCH_TYPEHASH =
        keccak256("AnchorBatch(bytes32[] digests,address anchorer,uint256 deadline)");

    /// @dev digest => anchorer => block timestamp of the first anchoring.
    ///      Zero means "never anchored": block timestamps are never zero on a
    ///      live chain.
    mapping(bytes32 digest => mapping(address anchorer => uint256 timestamp)) private _anchoredAt;

    /// @notice Emitted once per (digest, anchorer) pair, on its first anchoring.
    ///         Retries and replays emit nothing, so the log is a clean list of
    ///         first anchorings.
    event Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp);

    /// @notice The zero digest is never a real hash: it is always a caller bug.
    error ZeroDigest();

    /// @notice An empty batch is always a caller bug.
    error EmptyBatch();

    /// @notice The signed message was only valid until `deadline`.
    error ExpiredSignature(uint256 deadline);

    /// @notice The signature does not come from the anchorer, or does not cover
    ///         exactly these arguments on this chain and this registry.
    error InvalidSignature();

    constructor() EIP712("Ancorhash", "1") {}

    // ---------------------------------------------------------------- direct

    /// @notice Anchors `digest` on behalf of msg.sender.
    /// @return timestamp Time of the first anchoring: the current block time if
    ///         this call wrote the record, the original time if it already existed.
    function anchor(bytes32 digest) external returns (uint256 timestamp) {
        (timestamp,) = _anchor(digest, msg.sender);
    }

    /// @notice Anchors several digests in one transaction on behalf of msg.sender.
    /// @dev Validation is atomic: one zero digest reverts the whole batch.
    ///      Digests already anchored by msg.sender, or repeated within the batch,
    ///      are skipped, so retrying a whole batch is as safe as retrying one.
    /// @return anchored How many digests this call anchored for the first time.
    function anchorBatch(bytes32[] calldata digests) external returns (uint256 anchored) {
        if (digests.length == 0) revert EmptyBatch();
        anchored = _anchorBatch(digests, msg.sender);
    }

    // ---------------------------------------------------------------- signed

    /// @notice Anchors `digest` on behalf of `anchorer`, who signed the EIP-712
    ///         message returned by {hashAnchor}. Anyone can submit it.
    /// @param signature A 65-byte ECDSA signature for an EOA anchorer, or whatever
    ///        the account accepts through ERC-1271 for a contract anchorer.
    /// @return timestamp Time of the first anchoring, as in {anchor}.
    function anchorBySig(bytes32 digest, address anchorer, uint256 deadline, bytes calldata signature)
        external
        returns (uint256 timestamp)
    {
        _verify(hashAnchor(digest, anchorer, deadline), anchorer, deadline, signature);
        (timestamp,) = _anchor(digest, anchorer);
    }

    /// @notice Anchors several digests on behalf of `anchorer`, who signed the
    ///         EIP-712 message returned by {hashAnchorBatch}: one signature for
    ///         the whole batch, bound to these digests in this order.
    /// @return anchored How many digests this call anchored for the first time.
    function anchorBatchBySig(bytes32[] calldata digests, address anchorer, uint256 deadline, bytes calldata signature)
        external
        returns (uint256 anchored)
    {
        if (digests.length == 0) revert EmptyBatch();
        _verify(hashAnchorBatch(digests, anchorer, deadline), anchorer, deadline, signature);
        anchored = _anchorBatch(digests, anchorer);
    }

    // ----------------------------------------------------------------- views

    /// @notice Time at which `anchorer` first anchored `digest`, or zero if never.
    function anchoredAt(bytes32 digest, address anchorer) external view returns (uint256) {
        return _anchoredAt[digest][anchorer];
    }

    /// @notice The EIP-712 hash the anchorer signs for {anchorBySig}. A signer
    ///         that takes a 32-byte digest, such as an HSM, can sign it as is.
    function hashAnchor(bytes32 digest, address anchorer, uint256 deadline) public view returns (bytes32) {
        return _hashTypedDataV4(keccak256(abi.encode(ANCHOR_TYPEHASH, digest, anchorer, deadline)));
    }

    /// @notice The EIP-712 hash the anchorer signs for {anchorBatchBySig}.
    function hashAnchorBatch(bytes32[] calldata digests, address anchorer, uint256 deadline)
        public
        view
        returns (bytes32)
    {
        return _hashTypedDataV4(
            keccak256(abi.encode(ANCHOR_BATCH_TYPEHASH, keccak256(abi.encodePacked(digests)), anchorer, deadline))
        );
    }

    // -------------------------------------------------------------- internal

    function _verify(bytes32 hash, address anchorer, uint256 deadline, bytes calldata signature) private view {
        if (block.timestamp > deadline) revert ExpiredSignature(deadline);
        if (!SignatureChecker.isValidSignatureNowCalldata(anchorer, hash, signature)) revert InvalidSignature();
    }

    function _anchorBatch(bytes32[] calldata digests, address anchorer) private returns (uint256 anchored) {
        uint256 length = digests.length;
        for (uint256 i; i < length; ++i) {
            (, bool written) = _anchor(digests[i], anchorer);
            if (written) ++anchored;
        }
    }

    function _anchor(bytes32 digest, address anchorer) private returns (uint256 timestamp, bool written) {
        if (digest == bytes32(0)) revert ZeroDigest();

        timestamp = _anchoredAt[digest][anchorer];
        if (timestamp != 0) return (timestamp, false);

        timestamp = block.timestamp;
        _anchoredAt[digest][anchorer] = timestamp;
        emit Anchored(digest, anchorer, timestamp);
        return (timestamp, true);
    }
}
