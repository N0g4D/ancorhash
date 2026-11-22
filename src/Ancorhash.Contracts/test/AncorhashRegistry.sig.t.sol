// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.28;

import {Test} from "forge-std/Test.sol";
import {IERC1271} from "@openzeppelin/contracts/interfaces/IERC1271.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {AncorhashRegistry} from "../src/AncorhashRegistry.sol";

/// A minimal ERC-1271 account: valid signatures are those of its current owner
/// key, and the owner can hand the account over to a new key.
contract MockSmartAccount is IERC1271 {
    address public owner;

    constructor(address owner_) {
        owner = owner_;
    }

    function rotate(address newOwner) external {
        require(msg.sender == owner, "not the owner");
        owner = newOwner;
    }

    function isValidSignature(bytes32 hash, bytes calldata signature) external view returns (bytes4) {
        (address recovered, ECDSA.RecoverError err,) = ECDSA.tryRecoverCalldata(hash, signature);
        return err == ECDSA.RecoverError.NoError && recovered == owner ? this.isValidSignature.selector : bytes4(0);
    }
}

contract AncorhashRegistrySignedTest is Test {
    event Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp);

    uint256 internal constant START = 1_795_219_200; // 2026-11-21 00:00:00 UTC
    uint256 internal constant DEADLINE = START + 1 hours;
    bytes32 internal constant DIGEST = 0x10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1;

    AncorhashRegistry internal registry;

    address internal bank; // the organisation's key, e.g. held in an HSM
    uint256 internal bankKey;
    address internal relayer; // whoever submits and pays the gas
    address internal mallory;
    uint256 internal malloryKey;

    function setUp() public {
        vm.warp(START);
        registry = new AncorhashRegistry();
        (bank, bankKey) = makeAddrAndKey("bank");
        (mallory, malloryKey) = makeAddrAndKey("mallory");
        relayer = makeAddr("relayer");
    }

    // ------------------------------------------------------------- helpers

    function _sign(uint256 key, bytes32 hash) internal pure returns (bytes memory) {
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(key, hash);
        return abi.encodePacked(r, s, v);
    }

    function _signAnchor(uint256 key, bytes32 digest, address anchorer, uint256 deadline)
        internal
        view
        returns (bytes memory)
    {
        return _sign(key, registry.hashAnchor(digest, anchorer, deadline));
    }

    function _batch() internal pure returns (bytes32[] memory batch) {
        batch = new bytes32[](3);
        batch[0] = keccak256("run-42/report.xlsx");
        batch[1] = keccak256("run-42/log.json");
        batch[2] = keccak256("run-42/mail.eml");
    }

    // ------------------------------------------------------------- anchorBySig

    function test_AnchorBySig_RecordsUnderTheSignerNotTheRelayer() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);

        vm.expectEmit(address(registry));
        emit Anchored(DIGEST, bank, START);

        vm.prank(relayer);
        uint256 timestamp = registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        assertEq(timestamp, START);
        assertEq(registry.anchoredAt(DIGEST, bank), START);
        assertEq(registry.anchoredAt(DIGEST, relayer), 0, "paying the gas earns no record");
    }

    function test_AnchorBySig_ReplayIsANoOp() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        vm.warp(START + 10 minutes);
        vm.recordLogs();
        vm.prank(mallory);
        uint256 timestamp = registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        assertEq(timestamp, START, "a replay returns the original time");
        assertEq(registry.anchoredAt(DIGEST, bank), START);
        assertEq(vm.getRecordedLogs().length, 0, "a replay emits nothing");
    }

    function test_AnchorBySig_FrontRunningTheRelayerChangesNothing() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);

        // Mallory copies the signature from the relayer's pending transaction.
        vm.prank(mallory);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        vm.prank(relayer);
        uint256 timestamp = registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        assertEq(timestamp, START, "the relayer's call lands as a harmless no-op");
        assertEq(registry.anchoredAt(DIGEST, bank), START);
        assertEq(registry.anchoredAt(DIGEST, mallory), 0);
    }

    function test_AnchorBySig_SharesTheRecordWithDirectAnchoring() public {
        vm.prank(bank);
        registry.anchor(DIGEST);

        vm.warp(START + 1 minutes);
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.prank(relayer);
        uint256 timestamp = registry.anchorBySig(DIGEST, bank, DEADLINE, signature);

        assertEq(timestamp, START, "same anchorer, same record, whichever way it came in");
    }

    function test_AnchorBySig_AcceptsTheLastSecondBeforeTheDeadline() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.warp(DEADLINE);
        vm.prank(relayer);
        assertEq(registry.anchorBySig(DIGEST, bank, DEADLINE, signature), DEADLINE);
    }

    function test_AnchorBySig_RevertsAfterTheDeadline() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.warp(DEADLINE + 1);
        vm.expectRevert(abi.encodeWithSelector(AncorhashRegistry.ExpiredSignature.selector, DEADLINE));
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);
    }

    function test_AnchorBySig_RevertsWhenSignedByAnotherKey() public {
        bytes memory signature = _signAnchor(malloryKey, DIGEST, bank, DEADLINE);
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);
    }

    function test_AnchorBySig_RevertsWhenAnySignedFieldChanges() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.startPrank(relayer);

        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBySig(keccak256("another digest"), bank, DEADLINE, signature);

        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBySig(DIGEST, mallory, DEADLINE, signature);

        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBySig(DIGEST, bank, DEADLINE + 1, signature);

        vm.stopPrank();
    }

    function test_AnchorBySig_RevertsOnAnotherChain() public {
        bytes memory signature = _signAnchor(bankKey, DIGEST, bank, DEADLINE);
        vm.chainId(block.chainid + 1);
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);
    }

    function test_AnchorBySig_RevertsOnAnotherRegistry() public {
        AncorhashRegistry other = new AncorhashRegistry();
        bytes memory signature = _sign(bankKey, other.hashAnchor(DIGEST, bank, DEADLINE));
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, signature);
    }

    function test_AnchorBySig_RejectsTheMalleableTwinOfAValidSignature() public {
        bytes32 hash = registry.hashAnchor(DIGEST, bank, DEADLINE);
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(bankKey, hash);
        uint8 twinV = v == 27 ? 28 : 27;
        bytes32 twinS = bytes32(SECP256K1_ORDER - uint256(s));
        bytes memory twin = abi.encodePacked(r, twinS, twinV);

        // Plain ecrecover accepts the twin: the check must not rely on it alone.
        assertEq(ecrecover(hash, twinV, r, twinS), bank);

        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, bank, DEADLINE, twin);
    }

    function test_AnchorBySig_TheZeroAddressCannotBeImpersonated() public {
        // ecrecover returns address(0) for this garbage signature.
        bytes memory garbage = abi.encodePacked(bytes32(0), bytes32(0), uint8(27));
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, address(0), DEADLINE, garbage);
    }

    function test_AnchorBySig_RevertsOnZeroDigest() public {
        bytes memory signature = _signAnchor(bankKey, bytes32(0), bank, DEADLINE);
        vm.expectRevert(AncorhashRegistry.ZeroDigest.selector);
        vm.prank(relayer);
        registry.anchorBySig(bytes32(0), bank, DEADLINE, signature);
    }

    // ---------------------------------------------------------------- ERC-1271

    function test_AnchorBySig_SmartAccountKeepsItsAddressAcrossKeyRotation() public {
        MockSmartAccount account = new MockSmartAccount(bank);
        bytes memory signature = _signAnchor(bankKey, DIGEST, address(account), DEADLINE);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, address(account), DEADLINE, signature);
        assertEq(registry.anchoredAt(DIGEST, address(account)), START);

        // The bank rotates the key behind the account.
        (address newKey, uint256 newKeyPk) = makeAddrAndKey("bank-rotated");
        vm.prank(bank);
        account.rotate(newKey);

        bytes32 next = keccak256("run-43/report.xlsx");
        vm.warp(START + 1 days);
        vm.prank(relayer);
        registry.anchorBySig(
            next, address(account), START + 2 days, _signAnchor(newKeyPk, next, address(account), START + 2 days)
        );
        assertEq(registry.anchoredAt(next, address(account)), START + 1 days, "same address, new key");

        // The retired key no longer speaks for the account.
        bytes32 late = keccak256("signed with the retired key");
        bytes memory stale = _signAnchor(bankKey, late, address(account), START + 2 days);
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(late, address(account), START + 2 days, stale);
    }

    function test_AnchorBySig_SignatureForOneAccountDoesNotWorkForAnotherWithTheSameKey() public {
        MockSmartAccount accountA = new MockSmartAccount(bank);
        MockSmartAccount accountB = new MockSmartAccount(bank);
        bytes memory signature = _signAnchor(bankKey, DIGEST, address(accountA), DEADLINE);

        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        vm.prank(relayer);
        registry.anchorBySig(DIGEST, address(accountB), DEADLINE, signature);
    }

    // ------------------------------------------------------- anchorBatchBySig

    function test_AnchorBatchBySig_RecordsEveryDigestUnderTheSigner() public {
        bytes32[] memory batch = _batch();
        bytes memory signature = _sign(bankKey, registry.hashAnchorBatch(batch, bank, DEADLINE));

        vm.recordLogs();
        vm.prank(relayer);
        uint256 anchored = registry.anchorBatchBySig(batch, bank, DEADLINE, signature);

        assertEq(anchored, 3);
        assertEq(vm.getRecordedLogs().length, 3);
        for (uint256 i; i < batch.length; ++i) {
            assertEq(registry.anchoredAt(batch[i], bank), START);
            assertEq(registry.anchoredAt(batch[i], relayer), 0);
        }

        vm.warp(START + 1 minutes);
        vm.prank(mallory);
        assertEq(registry.anchorBatchBySig(batch, bank, DEADLINE, signature), 0, "a replayed batch is a no-op");
    }

    function test_AnchorBatchBySig_SignatureCoversTheExactListInOrder() public {
        bytes32[] memory batch = _batch();
        bytes memory signature = _sign(bankKey, registry.hashAnchorBatch(batch, bank, DEADLINE));
        vm.startPrank(relayer);

        bytes32[] memory reordered = _batch();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBatchBySig(reordered, bank, DEADLINE, signature);

        bytes32[] memory changed = _batch();
        changed[2] = keccak256("run-42/forged.xlsx");
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBatchBySig(changed, bank, DEADLINE, signature);

        bytes32[] memory shorter = new bytes32[](2);
        (shorter[0], shorter[1]) = (batch[0], batch[1]);
        vm.expectRevert(AncorhashRegistry.InvalidSignature.selector);
        registry.anchorBatchBySig(shorter, bank, DEADLINE, signature);

        vm.stopPrank();
    }

    function test_AnchorBatchBySig_RevertsOnEmptyBatch() public {
        bytes32[] memory empty = new bytes32[](0);
        bytes memory signature = _sign(bankKey, registry.hashAnchorBatch(empty, bank, DEADLINE));
        vm.expectRevert(AncorhashRegistry.EmptyBatch.selector);
        vm.prank(relayer);
        registry.anchorBatchBySig(empty, bank, DEADLINE, signature);
    }

    function test_AnchorBatchBySig_RevertsAfterTheDeadline() public {
        bytes32[] memory batch = _batch();
        bytes memory signature = _sign(bankKey, registry.hashAnchorBatch(batch, bank, DEADLINE));
        vm.warp(DEADLINE + 1);
        vm.expectRevert(abi.encodeWithSelector(AncorhashRegistry.ExpiredSignature.selector, DEADLINE));
        vm.prank(relayer);
        registry.anchorBatchBySig(batch, bank, DEADLINE, signature);
    }

    // ------------------------------------------- EIP-712 encoding, cross-checked

    /// The hashes must match an independent EIP-712 implementation (Foundry's),
    /// the same encoding wallets, viem, ethers and Nethereum follow.
    function test_HashAnchor_MatchesAnIndependentEip712Implementation() public view {
        string memory json = string.concat(
            '{"types":{',
            _domainType(),
            ',"Anchor":[{"name":"digest","type":"bytes32"},{"name":"anchorer","type":"address"},{"name":"deadline","type":"uint256"}]},',
            '"primaryType":"Anchor",',
            _domain(),
            ',"message":{"digest":"',
            vm.toString(DIGEST),
            '","anchorer":"',
            vm.toString(bank),
            '","deadline":"',
            vm.toString(DEADLINE),
            '"}}'
        );
        assertEq(registry.hashAnchor(DIGEST, bank, DEADLINE), vm.eip712HashTypedData(json));
    }

    function test_HashAnchorBatch_MatchesAnIndependentEip712Implementation() public view {
        bytes32[] memory batch = _batch();
        string memory json = string.concat(
            '{"types":{',
            _domainType(),
            ',"AnchorBatch":[{"name":"digests","type":"bytes32[]"},{"name":"anchorer","type":"address"},{"name":"deadline","type":"uint256"}]},',
            '"primaryType":"AnchorBatch",',
            _domain(),
            ',"message":{"digests":["',
            vm.toString(batch[0]),
            '","',
            vm.toString(batch[1]),
            '","',
            vm.toString(batch[2]),
            '"],"anchorer":"',
            vm.toString(bank),
            '","deadline":"',
            vm.toString(DEADLINE),
            '"}}'
        );
        assertEq(registry.hashAnchorBatch(batch, bank, DEADLINE), vm.eip712HashTypedData(json));
    }

    function test_Eip712Domain_IsDiscoverable() public view {
        (, string memory name, string memory version, uint256 chainId, address verifyingContract,,) =
            registry.eip712Domain();
        assertEq(name, "Ancorhash");
        assertEq(version, "1");
        assertEq(chainId, block.chainid);
        assertEq(verifyingContract, address(registry));
    }

    function _domainType() internal pure returns (string memory) {
        return '"EIP712Domain":[{"name":"name","type":"string"},{"name":"version","type":"string"},{"name":"chainId","type":"uint256"},{"name":"verifyingContract","type":"address"}]';
    }

    function _domain() internal view returns (string memory) {
        return string.concat(
            '"domain":{"name":"Ancorhash","version":"1","chainId":',
            vm.toString(block.chainid),
            ',"verifyingContract":"',
            vm.toString(address(registry)),
            '"}'
        );
    }

    // -------------------------------------------------------------------- fuzz

    function testFuzz_AnchorBySig_AnyKeyAnyRelayer(uint256 keySeed, bytes32 digest, address submitter, uint32 ttl)
        public
    {
        uint256 key = bound(keySeed, 1, SECP256K1_ORDER - 1);
        address signer = vm.addr(key);
        vm.assume(digest != bytes32(0) && submitter != signer);
        uint256 deadline = block.timestamp + ttl;

        vm.prank(submitter);
        registry.anchorBySig(digest, signer, deadline, _signAnchor(key, digest, signer, deadline));

        assertEq(registry.anchoredAt(digest, signer), block.timestamp);
        assertEq(registry.anchoredAt(digest, submitter), 0);
    }
}
