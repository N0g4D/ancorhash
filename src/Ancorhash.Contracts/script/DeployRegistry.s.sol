// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.28;

import {Script, console} from "forge-std/Script.sol";
import {AncorhashRegistry} from "../src/AncorhashRegistry.sol";

/// Deploys AncorhashRegistry through the deterministic CREATE2 deployer, so the
/// same bytecode and salt give the same address on every chain. Idempotent: if
/// the registry already sits at the predicted address, it does nothing.
///
///   forge script script/DeployRegistry.s.sol --rpc-url <rpc> --account <keystore> --broadcast
contract DeployRegistry is Script {
    bytes32 public constant SALT = keccak256("ancorhash.registry.v1");

    function run() external returns (AncorhashRegistry registry) {
        address predicted = vm.computeCreate2Address(SALT, keccak256(type(AncorhashRegistry).creationCode));

        if (predicted.code.length != 0) {
            console.log("AncorhashRegistry already deployed at", predicted);
            return AncorhashRegistry(predicted);
        }

        vm.startBroadcast();
        registry = new AncorhashRegistry{salt: SALT}();
        vm.stopBroadcast();

        require(address(registry) == predicted, "DeployRegistry: unexpected address");
        console.log("AncorhashRegistry deployed at", address(registry));
    }
}
