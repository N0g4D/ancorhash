// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.28;

import {Test} from "forge-std/Test.sol";
import {AncorhashRegistry} from "../src/AncorhashRegistry.sol";

/// Drives the registry through all four write paths with a few anchorers, a few
/// digests and a few submitters, so that retries, replays, duplicates, the same
/// digest from different anchorers and signed calls submitted by someone else
/// happen all the time. Keeps a ghost copy of what every record should hold.
contract RegistryHandler is Test {
    uint256 internal constant ACTORS = 3;
    uint256 internal constant OUTSIDERS = 2;
    uint256 internal constant DIGESTS = 8;
    uint256 internal constant MAX_BATCH = 16;

    AncorhashRegistry public registry;
    address[ACTORS] internal actors;
    uint256[ACTORS] internal actorKeys;
    address[OUTSIDERS] internal outsiders; // they only ever submit other people's signatures

    /// Ghost state: the time of the first anchoring of each pair, or zero.
    mapping(bytes32 digest => mapping(address anchorer => uint256 timestamp)) public expectedAt;

    constructor(AncorhashRegistry registry_) {
        registry = registry_;
        for (uint256 i; i < ACTORS; ++i) {
            (actors[i], actorKeys[i]) = makeAddrAndKey(string.concat("anchorer-", vm.toString(i)));
        }
        for (uint256 i; i < OUTSIDERS; ++i) {
            outsiders[i] = makeAddr(string.concat("relayer-", vm.toString(i)));
        }
    }

    // ----------------------------------------------------------- direct paths

    function anchor(uint256 actorSeed, uint256 digestSeed, uint32 elapsed) external {
        _elapse(elapsed);
        address actor = actorAt(actorSeed);
        bytes32 digest = digestAt(digestSeed);

        vm.prank(actor);
        uint256 returned = registry.anchor(digest);

        _record(digest, actor);
        assertEq(returned, expectedAt[digest][actor], "anchor() must return the first anchoring time");
    }

    function anchorBatch(uint256 actorSeed, uint256[] calldata digestSeeds, uint32 elapsed) external {
        if (digestSeeds.length == 0) return; // keep the input valid: an empty batch reverts by design
        _elapse(elapsed);
        address actor = actorAt(actorSeed);
        (bytes32[] memory batch, uint256 fresh) = _buildBatch(digestSeeds, actor);

        vm.prank(actor);
        uint256 anchored = registry.anchorBatch(batch);
        assertEq(anchored, fresh, "anchorBatch() must count first anchorings only");
    }

    // ----------------------------------------------------------- signed paths

    function anchorBySig(uint256 actorSeed, uint256 digestSeed, uint256 submitterSeed, uint32 elapsed) external {
        _elapse(elapsed);
        uint256 a = actorSeed % ACTORS;
        bytes32 digest = digestAt(digestSeed);
        uint256 deadline = block.timestamp + 1 hours;
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(actorKeys[a], registry.hashAnchor(digest, actors[a], deadline));

        vm.prank(submitterAt(submitterSeed));
        uint256 returned = registry.anchorBySig(digest, actors[a], deadline, abi.encodePacked(r, s, v));

        _record(digest, actors[a]);
        assertEq(returned, expectedAt[digest][actors[a]], "anchorBySig() must return the first anchoring time");
    }

    function anchorBatchBySig(uint256 actorSeed, uint256[] calldata digestSeeds, uint256 submitterSeed, uint32 elapsed)
        external
    {
        if (digestSeeds.length == 0) return;
        _elapse(elapsed);
        uint256 a = actorSeed % ACTORS;
        (bytes32[] memory batch, uint256 fresh) = _buildBatch(digestSeeds, actors[a]);
        uint256 deadline = block.timestamp + 1 hours;
        (uint8 v, bytes32 r, bytes32 s) = vm.sign(actorKeys[a], registry.hashAnchorBatch(batch, actors[a], deadline));

        vm.prank(submitterAt(submitterSeed));
        uint256 anchored = registry.anchorBatchBySig(batch, actors[a], deadline, abi.encodePacked(r, s, v));
        assertEq(anchored, fresh, "anchorBatchBySig() must count first anchorings only");
    }

    // ---------------------------------------------------------------- lookups

    function actorAt(uint256 seed) public view returns (address) {
        return actors[seed % ACTORS];
    }

    function outsiderAt(uint256 seed) public view returns (address) {
        return outsiders[seed % OUTSIDERS];
    }

    /// Anchorers submit each other's signatures too, not only outsiders.
    function submitterAt(uint256 seed) public view returns (address) {
        uint256 i = seed % (ACTORS + OUTSIDERS);
        return i < ACTORS ? actors[i] : outsiders[i - ACTORS];
    }

    function digestAt(uint256 seed) public pure returns (bytes32) {
        return keccak256(abi.encode("ancorhash.digest", seed % DIGESTS));
    }

    // -------------------------------------------------------------- internals

    function _buildBatch(uint256[] calldata digestSeeds, address anchorer)
        internal
        returns (bytes32[] memory batch, uint256 fresh)
    {
        uint256 length = digestSeeds.length < MAX_BATCH ? digestSeeds.length : MAX_BATCH;
        batch = new bytes32[](length);
        for (uint256 i; i < length; ++i) {
            batch[i] = digestAt(digestSeeds[i]);
            if (expectedAt[batch[i]][anchorer] == 0) {
                expectedAt[batch[i]][anchorer] = block.timestamp;
                ++fresh;
            }
        }
    }

    function _record(bytes32 digest, address anchorer) internal {
        if (expectedAt[digest][anchorer] == 0) expectedAt[digest][anchorer] = block.timestamp;
    }

    function _elapse(uint32 elapsed) internal {
        vm.warp(block.timestamp + elapsed % 7 days);
    }
}

contract AncorhashRegistryInvariantTest is Test {
    AncorhashRegistry internal registry;
    RegistryHandler internal handler;

    function setUp() public {
        vm.warp(1_795_219_200); // 2026-11-21 00:00:00 UTC
        registry = new AncorhashRegistry();
        handler = new RegistryHandler(registry);

        bytes4[] memory selectors = new bytes4[](4);
        selectors[0] = RegistryHandler.anchor.selector;
        selectors[1] = RegistryHandler.anchorBatch.selector;
        selectors[2] = RegistryHandler.anchorBySig.selector;
        selectors[3] = RegistryHandler.anchorBatchBySig.selector;
        targetSelector(FuzzSelector({addr: address(handler), selectors: selectors}));
        targetContract(address(handler));
    }

    /// Every pair holds exactly the time of its first anchoring, and pairs never
    /// anchored hold zero: retries, replays and other anchorers never move a
    /// record, and submitting someone else's signature never credits the submitter.
    function invariant_EveryRecordHoldsItsFirstAnchoringTime() public view {
        for (uint256 a; a < 3; ++a) {
            address actor = handler.actorAt(a);
            for (uint256 d; d < 8; ++d) {
                bytes32 digest = handler.digestAt(d);
                assertEq(registry.anchoredAt(digest, actor), handler.expectedAt(digest, actor));
            }
        }
    }

    /// Outsiders only ever pay for other people's signed anchorings.
    function invariant_SubmittersNeverGetARecordOfTheirOwn() public view {
        for (uint256 o; o < 2; ++o) {
            address outsider = handler.outsiderAt(o);
            for (uint256 d; d < 8; ++d) {
                assertEq(registry.anchoredAt(handler.digestAt(d), outsider), 0);
            }
        }
    }
}
