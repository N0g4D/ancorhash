// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.28;

import {Test} from "forge-std/Test.sol";
import {AncorhashRegistry} from "../src/AncorhashRegistry.sol";

/// Drives the registry with a few anchorers and a few digests, so that retries,
/// duplicates and the same digest from different anchorers happen all the time.
/// Keeps a ghost copy of what every record should hold.
contract RegistryHandler is Test {
    uint256 internal constant ACTORS = 3;
    uint256 internal constant DIGESTS = 8;
    uint256 internal constant MAX_BATCH = 16;

    AncorhashRegistry public registry;
    address[ACTORS] internal actors;

    /// Ghost state: the time of the first anchoring of each pair, or zero.
    mapping(bytes32 digest => mapping(address anchorer => uint256 timestamp)) public expectedAt;

    constructor(AncorhashRegistry registry_) {
        registry = registry_;
        for (uint256 i; i < ACTORS; ++i) {
            actors[i] = makeAddr(string.concat("anchorer-", vm.toString(i)));
        }
    }

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

        uint256 length = digestSeeds.length < MAX_BATCH ? digestSeeds.length : MAX_BATCH;
        bytes32[] memory batch = new bytes32[](length);
        uint256 fresh;
        for (uint256 i; i < length; ++i) {
            batch[i] = digestAt(digestSeeds[i]);
            if (expectedAt[batch[i]][actor] == 0) {
                expectedAt[batch[i]][actor] = block.timestamp;
                ++fresh;
            }
        }

        vm.prank(actor);
        uint256 anchored = registry.anchorBatch(batch);
        assertEq(anchored, fresh, "anchorBatch() must count first anchorings only");
    }

    function actorAt(uint256 seed) public view returns (address) {
        return actors[seed % ACTORS];
    }

    function digestAt(uint256 seed) public pure returns (bytes32) {
        return keccak256(abi.encode("ancorhash.digest", seed % DIGESTS));
    }

    function _record(bytes32 digest, address actor) internal {
        if (expectedAt[digest][actor] == 0) expectedAt[digest][actor] = block.timestamp;
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

        bytes4[] memory selectors = new bytes4[](2);
        selectors[0] = RegistryHandler.anchor.selector;
        selectors[1] = RegistryHandler.anchorBatch.selector;
        targetSelector(FuzzSelector({addr: address(handler), selectors: selectors}));
        targetContract(address(handler));
    }

    /// Every pair holds exactly the time of its first anchoring, and pairs never
    /// anchored hold zero: retries and other anchorers never move a record.
    function invariant_EveryRecordHoldsItsFirstAnchoringTime() public view {
        for (uint256 a; a < 3; ++a) {
            address actor = handler.actorAt(a);
            for (uint256 d; d < 8; ++d) {
                bytes32 digest = handler.digestAt(d);
                assertEq(registry.anchoredAt(digest, actor), handler.expectedAt(digest, actor));
            }
        }
    }
}
