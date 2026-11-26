#!/usr/bin/env python3
"""Verify an Ancorhash receipt without trusting Ancorhash.

The script checks three things:

1. the SHA-256 of the file is the digest in the receipt;
2. for a receipt with a Merkle proof, the proof leads from that digest to the
   receipt's root (RFC 9162, section 2.1.3.2);
3. with --rpc, the registry on chain has recorded that digest or root under the
   receipt's anchorer, at the receipt's time.

It needs Python 3.9 or later and nothing else: no Ethereum library, no access
to Ancorhash.

Usage:
    verify_receipt.py RECEIPT.json --file OUTPUT.pdf [--rpc RPC_URL]
    verify_receipt.py RECEIPT.json --digest 0x... [--rpc RPC_URL]
"""

import argparse
import hashlib
import json
import sys
import urllib.request
from datetime import datetime, timezone

# First four bytes of keccak256("anchoredAt(bytes32,address)").
ANCHORED_AT_SELECTOR = "217a828a"
PROOF_ALGORITHM = "rfc9162-sha256"


class VerificationError(Exception):
    pass


def sha256(data):
    return hashlib.sha256(data).digest()


def parse_bytes32(value, field):
    try:
        raw = bytes.fromhex(value[2:] if value.startswith("0x") else value)
    except (AttributeError, ValueError):
        raise VerificationError(f"{field} is not hex: {value!r}")
    if len(raw) != 32:
        raise VerificationError(f"{field} is not 32 bytes long")
    return raw


def root_from_proof(digest, leaf_index, tree_size, audit_path):
    """Recompute the root from a leaf and its audit path (RFC 9162, 2.1.3.2)."""
    if not 0 <= leaf_index < tree_size:
        raise VerificationError("leaf_index is outside the tree")
    fn, sn = leaf_index, tree_size - 1
    node = sha256(b"\x00" + digest)
    for sibling in audit_path:
        if sn == 0:
            raise VerificationError("the audit path is too long for the tree")
        if fn & 1 or fn == sn:
            node = sha256(b"\x01" + sibling + node)
            while not fn & 1 and fn != 0:
                fn >>= 1
                sn >>= 1
        else:
            node = sha256(b"\x01" + node + sibling)
        fn >>= 1
        sn >>= 1
    if sn != 0:
        raise VerificationError("the audit path is too short for the tree")
    return node


def rpc_call(url, method, params):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    # Some public endpoints refuse the default user agent of urllib.
    headers = {"Content-Type": "application/json", "User-Agent": "ancorhash-verify-receipt/1"}
    try:
        request = urllib.request.Request(url, body, headers)
        with urllib.request.urlopen(request, timeout=30) as response:
            reply = json.load(response)
    except (OSError, ValueError) as error:
        raise VerificationError(f"the RPC did not answer {method}: {error}")
    if not isinstance(reply, dict) or "result" not in reply:
        error = reply.get("error") if isinstance(reply, dict) else reply
        raise VerificationError(f"{method} failed: {error}")
    return reply["result"]


def field(receipt, name):
    if not isinstance(receipt, dict) or receipt.get(name) is None:
        raise VerificationError(f"the receipt has no {name!r} field")
    return receipt[name]


def anchored_at_on_chain(url, registry, value, anchorer):
    address = anchorer[2:] if anchorer.startswith("0x") else anchorer
    if len(address) != 40:
        raise VerificationError("anchorer is not an address")
    data = "0x" + ANCHORED_AT_SELECTOR + value.hex() + address.lower().rjust(64, "0")
    result = rpc_call(url, "eth_call", [{"to": registry, "data": data}, "latest"])
    return int(result, 16)


def verify(receipt, digest, rpc_url=None):
    """Return the lines to print; raise VerificationError on any mismatch."""
    lines = []
    if digest != parse_bytes32(field(receipt, "digest"), "digest"):
        raise VerificationError("the file does not match the digest in the receipt")
    lines.append("digest: matches the receipt")

    proof = receipt.get("proof")
    if proof is None:
        anchored = digest
    else:
        if proof.get("algorithm") != PROOF_ALGORITHM:
            raise VerificationError(f"unknown proof algorithm: {proof.get('algorithm')!r}")
        path = [parse_bytes32(node, "audit_path") for node in field(proof, "audit_path")]
        anchored = root_from_proof(
            digest, int(field(proof, "leaf_index")), int(field(proof, "tree_size")), path
        )
        if anchored != parse_bytes32(field(receipt, "root"), "root"):
            raise VerificationError("the proof does not lead to the root in the receipt")
        lines.append(f"proof: leads to root 0x{anchored.hex()}")

    if rpc_url is None:
        lines.append("chain: not checked (pass --rpc to check the registry)")
        return lines

    expected_chain = int(field(receipt, "chain_id"))
    anchorer = field(receipt, "anchorer")
    chain_id = int(rpc_call(rpc_url, "eth_chainId", []), 16)
    if chain_id != expected_chain:
        raise VerificationError(
            f"the RPC serves chain {chain_id}, the receipt is for chain {expected_chain}"
        )
    timestamp = anchored_at_on_chain(rpc_url, field(receipt, "registry"), anchored, anchorer)
    if timestamp == 0:
        raise VerificationError("the registry has no record of it under this anchorer")
    expected = receipt.get("anchored_at_unix")
    if expected is not None and int(expected) != timestamp:
        raise VerificationError(f"the registry records {timestamp}, the receipt says {expected}")
    when = datetime.fromtimestamp(timestamp, timezone.utc).isoformat()
    lines.append(f"chain: recorded at {when} under {anchorer}")
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description="Verify an Ancorhash receipt.")
    parser.add_argument("receipt", help="path of the receipt (JSON)")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--file", help="the output the receipt is for")
    source.add_argument("--digest", help="its SHA-256, as 64 hex characters")
    parser.add_argument("--rpc", help="JSON-RPC URL of the receipt's chain")
    args = parser.parse_args(argv)

    try:
        with open(args.receipt, encoding="utf-8") as handle:
            receipt = json.load(handle)
        if args.file is not None:
            with open(args.file, "rb") as handle:
                digest = sha256(handle.read())
        else:
            digest = parse_bytes32(args.digest, "--digest")
        lines = verify(receipt, digest, args.rpc)
    except VerificationError as error:
        print(f"FAILED: {error}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as error:
        # A file that cannot be read, or a receipt that is not valid JSON.
        print(f"FAILED: {error}", file=sys.stderr)
        return 1
    for line in lines:
        print(line)
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
