using Microsoft.Extensions.Options;
using Nethereum.Util;

namespace Ancorhash.Infrastructure.Configuration;

/// <summary>
/// Validazione all'avvio della configurazione multi-chain, con un messaggio per
/// ogni problema e la chiave di configurazione da correggere. Con ValidateOnStart
/// l'app non parte se un network è incompleto o se un indirizzo del registry non
/// è un indirizzo con checksum EIP-55.
/// </summary>
public sealed class BlockchainOptionsValidator : IValidateOptions<BlockchainOptions>
{
    public ValidateOptionsResult Validate(string? name, BlockchainOptions options)
    {
        var failures = new List<string>();

        if (options.Networks.Count == 0)
        {
            failures.Add("Almeno un network EVM deve essere configurato in Blockchain:Networks.");
        }

        if (!string.IsNullOrWhiteSpace(options.RegistryAddress)
            && !IsChecksummedAddress(options.RegistryAddress))
        {
            failures.Add("Blockchain:RegistryAddress deve essere un indirizzo con checksum EIP-55.");
        }

        foreach (var (chainId, network) in options.Networks)
        {
            var key = $"Blockchain:Networks:{chainId}";

            if (!Uri.IsWellFormedUriString(network.RpcUrl, UriKind.Absolute))
            {
                failures.Add($"{key}:RpcUrl deve essere un URL assoluto valido.");
            }

            if (!string.IsNullOrWhiteSpace(network.RegistryAddress)
                && !IsChecksummedAddress(network.RegistryAddress))
            {
                failures.Add($"{key}:RegistryAddress deve essere un indirizzo con checksum EIP-55.");
            }

            if (options.ResolveRegistryAddress(network) is null)
            {
                failures.Add(
                    $"{key}: manca l'indirizzo di AncorhashRegistry. Impostare Blockchain:RegistryAddress oppure {key}:RegistryAddress.");
            }

            if (!string.IsNullOrWhiteSpace(network.ExplorerUrl) && !IsHttpUrl(network.ExplorerUrl))
            {
                failures.Add($"{key}:ExplorerUrl deve essere un URL http(s) assoluto.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // Un indirizzo tutto minuscolo è valido per la chain ma non ha checksum:
    // lo rifiutiamo, perché un errore di battitura passerebbe inosservato.
    private static bool IsChecksummedAddress(string address) =>
        AddressUtil.Current.IsValidEthereumAddressHexFormat(address)
        && AddressUtil.Current.IsChecksumAddress(address);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
