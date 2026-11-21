// SPDX-License-Identifier: Apache-2.0
pragma solidity 0.8.28;

/// @title AncorhashRegistry
/// @author Alessio Malerba
/// @notice A public, append-only register of 32-byte digests. Each record proves
///         that `anchorer` submitted `digest` no later than the recorded time.
/// @dev The registry does not know what a digest is. It can be the SHA-256 of a
///      file, of the output of a bot or an agent, or the root of a Merkle tree
///      that commits to every output of one run. That meaning lives in the
///      off-chain receipt, not here.
///
///      Invariants:
///      1. Open writes. Anyone can anchor, and every record is bound to the
///         address that wrote it (msg.sender). Attribution is a property of the
///         signature, not a field anyone can fill in.
///      2. First write wins, per (digest, anchorer) pair. Anchoring the same
///         digest again is a no-op that returns the original time, so a bot that
///         retries after a timeout can neither fail nor move its own record.
///      3. Records are isolated per anchorer. Whoever anchors your digest before
///         you, front-running included, writes their own record and cannot touch
///         yours.
///      4. No owner, no pause, no upgrade path. Nobody, including the deployer,
///         can change or delete a record once it is written.
contract AncorhashRegistry {
    /// @dev digest => anchorer => block timestamp of the first anchoring.
    ///      Zero means "never anchored": block timestamps are never zero on a
    ///      live chain.
    mapping(bytes32 digest => mapping(address anchorer => uint256 timestamp)) private _anchoredAt;

    /// @notice Emitted once per (digest, anchorer) pair, on its first anchoring.
    ///         Retries emit nothing, so the log is a clean list of first anchorings.
    event Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp);

    /// @notice The zero digest is never a real hash: it is always a caller bug.
    error ZeroDigest();

    /// @notice An empty batch is always a caller bug.
    error EmptyBatch();

    /// @notice Anchors `digest` on behalf of msg.sender.
    /// @return timestamp Time of the first anchoring: the current block time if
    ///         this call wrote the record, the original time if it already existed.
    function anchor(bytes32 digest) external returns (uint256 timestamp) {
        (timestamp,) = _anchor(digest);
    }

    /// @notice Anchors several digests in one transaction on behalf of msg.sender.
    /// @dev Validation is atomic: one zero digest reverts the whole batch.
    ///      Digests already anchored by msg.sender, or repeated within the batch,
    ///      are skipped, so retrying a whole batch is as safe as retrying one.
    /// @return anchored How many digests this call anchored for the first time.
    function anchorBatch(bytes32[] calldata digests) external returns (uint256 anchored) {
        uint256 length = digests.length;
        if (length == 0) revert EmptyBatch();

        for (uint256 i; i < length; ++i) {
            (, bool written) = _anchor(digests[i]);
            if (written) ++anchored;
        }
    }

    /// @notice Time at which `anchorer` first anchored `digest`, or zero if never.
    function anchoredAt(bytes32 digest, address anchorer) external view returns (uint256) {
        return _anchoredAt[digest][anchorer];
    }

    function _anchor(bytes32 digest) private returns (uint256 timestamp, bool written) {
        if (digest == bytes32(0)) revert ZeroDigest();

        timestamp = _anchoredAt[digest][msg.sender];
        if (timestamp != 0) return (timestamp, false);

        timestamp = block.timestamp;
        _anchoredAt[digest][msg.sender] = timestamp;
        emit Anchored(digest, msg.sender, timestamp);
        return (timestamp, true);
    }
}
