using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nethereum.Util;

namespace Ancorhash.Infrastructure.Configuration;

/// <summary>
/// Validazione all'avvio della configurazione multi-chain e della chiave del relayer,
/// con un messaggio per ogni problema e la chiave di configurazione da correggere.
/// Con ValidateOnStart l'app non parte se un network è incompleto, se un indirizzo
/// del registry non è un indirizzo con checksum EIP-55, se manca la chiave del
/// relayer o se una chiave privata in chiaro convive con una rete che non è una testnet.
/// </summary>
public sealed partial class BlockchainOptionsValidator : IValidateOptions<BlockchainOptions>
{
    private const string KeyVaultUriKey = "Blockchain:Relayer:KeyVaultUri";
    private const string KeyNameKey = "Blockchain:Relayer:KeyName";
    private const string PrivateKeyKey = BlockchainOptions.RelayerPrivateKeyConfigKey;

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

        ValidateRelayerKey(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // Una sola chiave del relayer: quella in Key Vault oppure, solo con reti di test,
    // quella in chiaro. Con entrambe non sarebbe chiaro a quale indirizzo vanno intestati
    // i record.
    private static void ValidateRelayerKey(BlockchainOptions options, List<string> failures)
    {
        var relayer = options.Relayer;
        var hasPrivateKey = !string.IsNullOrWhiteSpace(options.RelayerPrivateKey);

        if (relayer.UsesKeyVault)
        {
            if (string.IsNullOrWhiteSpace(relayer.KeyVaultUri))
            {
                failures.Add($"{KeyVaultUriKey} mancante: serve insieme a {KeyNameKey}.");
            }
            else if (!IsVaultUrl(relayer.KeyVaultUri))
            {
                failures.Add(
                    $"{KeyVaultUriKey} deve essere l'URL https del vault, senza percorso: ad esempio https://<vault>.vault.azure.net/.");
            }

            if (string.IsNullOrWhiteSpace(relayer.KeyName))
            {
                failures.Add($"{KeyNameKey} mancante: serve insieme a {KeyVaultUriKey}.");
            }
            else if (!KeyVaultKeyNameRegex().IsMatch(relayer.KeyName))
            {
                failures.Add(
                    $"{KeyNameKey} deve essere il nome di una chiave Key Vault, senza versione: da 1 a 127 caratteri tra lettere, cifre e trattini.");
            }

            if (hasPrivateKey)
            {
                failures.Add(
                    $"{PrivateKeyKey} e Blockchain:Relayer sono alternative: con la chiave in Key Vault, {PrivateKeyKey} va tolta, anche come segreto Evm--RelayerPrivateKey del Key Vault.");
            }
        }
        else if (!hasPrivateKey)
        {
            failures.Add(
                $"Manca la chiave del relayer: impostare {KeyVaultUriKey} e {KeyNameKey} (chiave secp256k1 in Azure Key Vault), oppure {PrivateKeyKey} solo per lo sviluppo locale contro anvil.");
        }

        // Una chiave in chiaro è ammessa solo dove il gas non vale niente.
        if (hasPrivateKey)
        {
            foreach (var (chainId, _) in options.Networks.Where(network => !network.Value.IsTestnet))
            {
                failures.Add(
                    $"{PrivateKeyKey} è ammessa solo per lo sviluppo locale contro anvil, ma Blockchain:Networks:{chainId} non è una testnet (IsTestnet = false): usare una chiave in Key Vault (Blockchain:Relayer).");
            }
        }
    }

    // Un indirizzo tutto minuscolo è valido per la chain ma non ha checksum:
    // lo rifiutiamo, perché un errore di battitura passerebbe inosservato.
    private static bool IsChecksummedAddress(string address) =>
        AddressUtil.Current.IsValidEthereumAddressHexFormat(address)
        && AddressUtil.Current.IsChecksumAddress(address);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Il signer ricava l'URL della chiave dal vault: un percorso, ad esempio l'URL
    // della chiave incollato al posto di quello del vault, finirebbe in un 404.
    private static bool IsVaultUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0;

    // Nomi degli oggetti Key Vault: da 1 a 127 caratteri tra lettere, cifre e trattini.
    [GeneratedRegex("^[0-9a-zA-Z-]{1,127}$")]
    private static partial Regex KeyVaultKeyNameRegex();
}
