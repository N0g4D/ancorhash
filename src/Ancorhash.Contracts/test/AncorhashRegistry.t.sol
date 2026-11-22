// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.28;

import {Test} from "forge-std/Test.sol";
import {AncorhashRegistry} from "../src/AncorhashRegistry.sol";

contract AncorhashRegistryTest is Test {
    event Anchored(bytes32 indexed digest, address indexed anchorer, uint256 timestamp);

    AncorhashRegistry internal registry;

    address internal constant BANK_BOT = address(0xB07);
    address internal constant MALLORY = address(0xBAD);

    /// SHA-256 of the ASCII string "ancorhash", a shared vector for off-chain tests.
    /// For a file with that exact content, the browser (crypto.subtle) and
    /// PowerShell's Get-FileHash give it as hex; PAD's "Hash from file" gives the
    /// same 32 bytes in Base64: EP0rt3iqDIabIDrdQqO+wH505CgVM4i4DbNAqd9HRNE=
    bytes32 internal constant DIGEST = 0x10fd2bb778aa0c869b203add42a3bec07e74e428153388b80db340a9df4744d1;

    uint256 internal constant START = 1_795_219_200; // 2026-11-21 00:00:00 UTC

    function setUp() public {
        registry = new AncorhashRegistry();
        vm.warp(START);
    }

    // ------------------------------------------------------------------ anchor

    function test_Anchor_WritesRecordAndEmits() public {
        vm.expectEmit(address(registry));
        emit Anchored(DIGEST, BANK_BOT, START);

        vm.prank(BANK_BOT);
        uint256 timestamp = registry.anchor(DIGEST);

        assertEq(timestamp, START);
        assertEq(registry.anchoredAt(DIGEST, BANK_BOT), START);
    }

    function test_Anchor_RetryReturnsOriginalTimeAndEmitsNothing() public {
        vm.prank(BANK_BOT);
        registry.anchor(DIGEST);

        vm.warp(START + 1 days);
        vm.recordLogs();
        vm.prank(BANK_BOT);
        uint256 timestamp = registry.anchor(DIGEST);

        assertEq(timestamp, START, "a retry must return the original time");
        assertEq(registry.anchoredAt(DIGEST, BANK_BOT), START, "a retry must not move the record");
        assertEq(vm.getRecordedLogs().length, 0, "a retry must not emit");
    }

    function test_Anchor_RevertsOnZeroDigest() public {
        vm.expectRevert(AncorhashRegistry.ZeroDigest.selector);
        vm.prank(BANK_BOT);
        registry.anchor(bytes32(0));
    }

    function test_Anchor_FrontRunCannotTouchAnotherAnchorersRecord() public {
        // Mallory sees the bot's pending transaction and anchors the same digest first.
        vm.prank(MALLORY);
        registry.anchor(DIGEST);

        vm.warp(START + 12);
        vm.prank(BANK_BOT);
        uint256 timestamp = registry.anchor(DIGEST);

        assertEq(timestamp, START + 12, "the bot gets its own, fresh record");
        assertEq(registry.anchoredAt(DIGEST, BANK_BOT), START + 12);
        assertEq(registry.anchoredAt(DIGEST, MALLORY), START, "and Mallory only has hers");
    }

    function test_AnchoredAt_IsZeroForAPairNeverAnchored() public {
        vm.prank(MALLORY);
        registry.anchor(DIGEST);

        assertEq(registry.anchoredAt(DIGEST, BANK_BOT), 0);
        assertEq(registry.anchoredAt(keccak256("never anchored"), MALLORY), 0);
    }

    // ------------------------------------------------------------- anchorBatch

    function test_AnchorBatch_WritesNewDigestsAndSkipsDuplicates() public {
        bytes32 a = keccak256("output-a");
        bytes32 b = keccak256("output-b");
        bytes32 c = keccak256("output-c");

        vm.prank(BANK_BOT);
        registry.anchor(a);

        vm.warp(START + 60);
        bytes32[] memory batch = new bytes32[](4);
        batch[0] = a; // already anchored by the bot
        batch[1] = b;
        batch[2] = b; // repeated within the batch
        batch[3] = c;

        vm.recordLogs();
        vm.prank(BANK_BOT);
        uint256 anchored = registry.anchorBatch(batch);

        assertEq(anchored, 2);
        assertEq(vm.getRecordedLogs().length, 2, "one event per first anchoring");
        assertEq(registry.anchoredAt(a, BANK_BOT), START, "the earlier record is kept");
        assertEq(registry.anchoredAt(b, BANK_BOT), START + 60);
        assertEq(registry.anchoredAt(c, BANK_BOT), START + 60);
    }

    function test_AnchorBatch_RetryOfTheSameBatchIsANoOp() public {
        bytes32[] memory batch = new bytes32[](3);
        batch[0] = keccak256("run-42/report.xlsx");
        batch[1] = keccak256("run-42/log.json");
        batch[2] = keccak256("run-42/mail.eml");

        vm.prank(BANK_BOT);
        assertEq(registry.anchorBatch(batch), 3);

        vm.warp(START + 30);
        vm.recordLogs();
        vm.prank(BANK_BOT);
        assertEq(registry.anchorBatch(batch), 0);

        assertEq(vm.getRecordedLogs().length, 0);
        for (uint256 i; i < batch.length; ++i) {
            assertEq(registry.anchoredAt(batch[i], BANK_BOT), START);
        }
    }

    function test_AnchorBatch_RevertsOnEmptyBatch() public {
        vm.expectRevert(AncorhashRegistry.EmptyBatch.selector);
        vm.prank(BANK_BOT);
        registry.anchorBatch(new bytes32[](0));
    }

    function test_AnchorBatch_ZeroDigestRevertsTheWholeBatch() public {
        bytes32[] memory batch = new bytes32[](3);
        batch[0] = keccak256("first");
        batch[1] = keccak256("second");
        batch[2] = bytes32(0);

        vm.expectRevert(AncorhashRegistry.ZeroDigest.selector);
        vm.prank(BANK_BOT);
        registry.anchorBatch(batch);

        assertEq(registry.anchoredAt(batch[0], BANK_BOT), 0, "no partial writes");
        assertEq(registry.anchoredAt(batch[1], BANK_BOT), 0, "no partial writes");
    }

    // ------------------------------------------------------------- test vector

    function test_TestVector_DigestIsTheSha256OfTheContent() public pure {
        assertEq(sha256(bytes("ancorhash")), DIGEST);
    }

    // -------------------------------------------------------------------- fuzz

    function testFuzz_Anchor_FirstWriteWins(bytes32 digest, address anchorer, uint32 delay) public {
        vm.assume(digest != bytes32(0));

        vm.prank(anchorer);
        uint256 first = registry.anchor(digest);

        vm.warp(block.timestamp + delay);
        vm.prank(anchorer);
        uint256 again = registry.anchor(digest);

        assertEq(again, first);
        assertEq(registry.anchoredAt(digest, anchorer), first);
    }

    function testFuzz_Anchor_RecordsAreIsolatedPerAnchorer(bytes32 digest, address one, address other) public {
        vm.assume(digest != bytes32(0) && one != other);

        vm.prank(one);
        registry.anchor(digest);

        assertEq(registry.anchoredAt(digest, other), 0);
    }

    /// A batch must leave the same records, and report the same count, as the
    /// same digests anchored one at a time.
    function testFuzz_AnchorBatch_MatchesSingleAnchors(bytes32[] memory input, uint8 repeatEvery) public {
        vm.assume(input.length > 0);

        // Up to 32 digests, with forced repetitions so duplicates are exercised too.
        uint256 length = input.length < 32 ? input.length : 32;
        uint256 step = bound(repeatEvery, 2, 8);
        bytes32[] memory digests = new bytes32[](length);
        for (uint256 i; i < length; ++i) {
            digests[i] = i % step == step - 1 ? digests[i - 1] : input[i];
            if (digests[i] == bytes32(0)) digests[i] = bytes32(uint256(1));
        }

        AncorhashRegistry oneByOne = new AncorhashRegistry();
        uint256 expected;
        vm.startPrank(BANK_BOT);
        for (uint256 i; i < length; ++i) {
            if (oneByOne.anchoredAt(digests[i], BANK_BOT) == 0) ++expected;
            oneByOne.anchor(digests[i]);
        }
        uint256 anchored = registry.anchorBatch(digests);
        vm.stopPrank();

        assertEq(anchored, expected);
        for (uint256 i; i < length; ++i) {
            assertEq(registry.anchoredAt(digests[i], BANK_BOT), oneByOne.anchoredAt(digests[i], BANK_BOT));
        }
    }
}
