using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Ancorhash.Api.Functions;
using Ancorhash.Core.Abstractions;
using Ancorhash.Core.Models;
using Ancorhash.Core.Services;
using Ancorhash.Infrastructure.Blockchain;
using Ancorhash.Infrastructure.Blockchain.Registry;
using Ancorhash.Infrastructure.Configuration;
using Ancorhash.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Ancorhash.Tests.Integration;

/// <summary>Collection dei test che condividono un'unica istanza di anvil, eseguiti in sequenza.</summary>
[CollectionDefinition(Name)]
public sealed class AnvilCollection : ICollectionFixture<AnvilFixture>
{
    public const string Name = "Anvil";
}

/// <summary>L'account con cui firma il relayer nei test.</summary>
public enum RelayerAccount
{
    /// <summary>Account di Nethereum con la chiave in chiaro, come nello sviluppo locale.</summary>
    LocalKey,

    /// <summary>ExternalAccount, come con Azure Key Vault, con una chiave locale al posto del vault.</summary>
    ExternalSigner,
}

/// <summary>
/// Un anvil su una porta libera, con AncorhashRegistry deployato da
/// script/DeployRegistry.s.sol con l'account 0, come si fa su una rete vera.
/// Se anvil o forge non sono nel PATH i test vengono saltati, non falliscono.
/// </summary>
public sealed partial class AnvilFixture : IAsyncLifetime
{
    public const int ChainId = 31337;

    // Chiavi dei primi account di anvil, derivate dal mnemonic pubblico
    // "test test … junk": valgono solo su una chain locale.
    public const string DeployerPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    public const string RelayerPrivateKey = "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d";

    // La chiave dietro l'ExternalAccount (account 2) è diversa da quella del relayer
    // locale: un record intestato all'indirizzo sbagliato non passerebbe inosservato.
    public const string ExternalSignerPrivateKey = "0x5de4111afa1a4b94908f83103eb1f1706367c2e68ca870fc3fb9a804cdab365a";

    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(3);

    private readonly StringBuilder _anvilOutput = new();
    private Process? _anvil;

    /// <summary>Motivo per saltare i test, se anvil o forge mancano.</summary>
    public string? SkipReason { get; private set; }

    public string RpcUrl { get; private set; } = string.Empty;

    public string RegistryAddress { get; private set; } = string.Empty;

    public string RelayerAddress { get; } = new Account(RelayerPrivateKey).Address;

    public string ExternalSignerAddress { get; } = new Account(ExternalSignerPrivateKey).Address;

    /// <summary>Client senza account, per letture e comandi di anvil.</summary>
    public IWeb3 Web3 { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var missing = new[] { "anvil", "forge" }.Where(tool => !IsOnPath(tool)).ToList();
        if (missing.Count > 0)
        {
            SkipReason = missing.Count == 1
                ? $"{missing[0]} non trovato nel PATH: test di integrazione saltati."
                : $"{string.Join(" e ", missing)} non trovati nel PATH: test di integrazione saltati.";
            return;
        }

        var port = GetFreePort();
        RpcUrl = $"http://127.0.0.1:{port}";
        Web3 = new Web3(RpcUrl);
        _anvil = StartAnvil(port);
        await WaitForRpcAsync();
        RegistryAddress = await DeployRegistryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_anvil is null)
        {
            return;
        }

        if (!_anvil.HasExited)
        {
            _anvil.Kill(entireProcessTree: true);
            await _anvil.WaitForExitAsync();
        }

        _anvil.Dispose();
    }

    /// <summary>Indirizzo a cui sono intestati i record del relayer con questo account.</summary>
    public string AddressOf(RelayerAccount account) =>
        account == RelayerAccount.ExternalSigner ? ExternalSignerAddress : RelayerAddress;

    /// <summary>
    /// Relayer reale (Nethereum) sull'anvil: con <see cref="RelayerAccount.LocalKey"/>
    /// firma la chiave in chiaro dell'account 1, con <see cref="RelayerAccount.ExternalSigner"/>
    /// un ExternalAccount con la chiave dell'account 2, come farebbe Key Vault.
    /// </summary>
    public NethereumBlockchainRelayer CreateRelayer(
        int receiptTimeoutSeconds = 30,
        string? registryAddress = null,
        bool isTestnet = true,
        IPriceOracleService? priceOracle = null,
        PendingAnchorings? pendingAnchorings = null,
        RelayerAccount account = RelayerAccount.LocalKey)
    {
        var external = account == RelayerAccount.ExternalSigner;
        var options = Options.Create(new BlockchainOptions
        {
            RegistryAddress = registryAddress ?? RegistryAddress,
            ReceiptTimeoutSeconds = receiptTimeoutSeconds,
            // Con il signer esterno nessuna chiave in chiaro, come in cloud.
            RelayerPrivateKey = external ? string.Empty : RelayerPrivateKey,
            Networks =
            {
                [ChainId] = new NetworkConfig
                {
                    RpcUrl = RpcUrl,
                    Name = "anvil",
                    IsTestnet = isTestnet,
                    NativeCoinId = "ethereum",
                },
            },
        });

        return new NethereumBlockchainRelayer(
            new NethereumWeb3Factory(
                options,
                external ? new LocalKeyExternalSigner(ExternalSignerPrivateKey) : null,
                NullLogger<NethereumWeb3Factory>.Instance),
            priceOracle ?? new FixedPriceOracle(null),
            pendingAnchorings ?? new PendingAnchorings(TimeProvider.System),
            options,
            NullLogger<NethereumBlockchainRelayer>.Instance);
    }

    /// <summary>Le Function di notarizzazione sopra un relayer reale, come nell'host.</summary>
    public NotarizeFunction CreateNotarizeFunction(NethereumBlockchainRelayer relayer) => new(
        new NotarizationService(relayer, NullLogger<NotarizationService>.Instance),
        new FakeTurnstileValidator(),
        Options.Create(new BlockchainOptions
        {
            RegistryAddress = RegistryAddress,
            Networks = { [ChainId] = new NetworkConfig { RpcUrl = RpcUrl } },
        }),
        NullLogger<NotarizeFunction>.Instance);

    /// <summary><c>anchoredAt(digest, anchorer)</c> letto direttamente dal registry.</summary>
    public async Task<long> GetAnchoredAtAsync(Digest digest, string anchorer)
    {
        var query = new AnchoredAtFunction { Digest = digest.ToArray(), Anchorer = anchorer };
        var timestamp = await Web3.Eth.GetContractQueryHandler<AnchoredAtFunction>()
            .QueryAsync<BigInteger>(RegistryAddress, query, null);
        return (long)timestamp;
    }

    /// <summary>
    /// Quanti digest anchorBatch scriverebbe per <paramref name="from"/>, simulando
    /// la chiamata con eth_call: è il valore di ritorno del contratto.
    /// </summary>
    public async Task<int> SimulateAnchorBatchAsync(IEnumerable<Digest> digests, string from)
    {
        var call = new AnchorBatchFunction
        {
            Digests = digests.Select(digest => digest.ToArray()).ToList(),
            FromAddress = from,
        };
        var anchored = await Web3.Eth.GetContractQueryHandler<AnchorBatchFunction>()
            .QueryAsync<BigInteger>(RegistryAddress, call, null);
        return (int)anchored;
    }

    public async Task<BigInteger> GetNonceAsync(string address) =>
        (await Web3.Eth.Transactions.GetTransactionCount
            .SendRequestAsync(address, BlockParameter.CreatePending())).Value;

    public Task SetAutomineAsync(bool enabled) => SendAnvilCommandAsync("evm_setAutomine", enabled);

    public Task MineAsync() => SendAnvilCommandAsync("evm_mine");

    /// <summary>Toglie una transazione dalla mempool, come farebbe un nodo che la scarta.</summary>
    public Task DropTransactionAsync(string transactionHash) =>
        SendAnvilCommandAsync("anvil_dropTransaction", transactionHash);

    private Task SendAnvilCommandAsync(string method, params object[] parameters) =>
        Web3.Client.SendRequestAsync(new RpcRequest(Guid.NewGuid().ToString(), method, parameters));

    private Process StartAnvil(int port)
    {
        var anvil = new Process
        {
            StartInfo = new ProcessStartInfo("anvil")
            {
                ArgumentList = { "--port", port.ToString(CultureInfo.InvariantCulture), "--quiet" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        // L'output va consumato, altrimenti a buffer pieno anvil si blocca.
        anvil.OutputDataReceived += (_, e) => AppendAnvilOutput(e.Data);
        anvil.ErrorDataReceived += (_, e) => AppendAnvilOutput(e.Data);
        anvil.Start();
        anvil.BeginOutputReadLine();
        anvil.BeginErrorReadLine();
        return anvil;
    }

    private void AppendAnvilOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_anvilOutput)
        {
            _anvilOutput.AppendLine(line);
        }
    }

    private async Task WaitForRpcAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await Web3.Eth.ChainId.SendRequestAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline && _anvil is { HasExited: false })
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                string output;
                lock (_anvilOutput)
                {
                    output = _anvilOutput.ToString();
                }

                throw new InvalidOperationException($"anvil non risponde su {RpcUrl}. Output:\n{output}", ex);
            }
        }
    }

    private async Task<string> DeployRegistryAsync()
    {
        var output = await RunAsync(
            "forge",
            ["script", "script/DeployRegistry.s.sol", "--rpc-url", RpcUrl, "--broadcast", "--private-key", DeployerPrivateKey],
            FindContractsDirectory());

        var match = DeployedAtRegex().Match(output);
        return match.Success
            ? match.Groups["address"].Value
            : throw new InvalidOperationException($"Indirizzo del registry non trovato nell'output di forge:\n{output}");
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(DeployTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} non ha finito entro {DeployTimeout.TotalSeconds} secondi.");
        }

        var output = await stdout + await stderr;
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"{fileName} è uscito con codice {process.ExitCode}:\n{output}");
    }

    private static string FindContractsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ancorhash.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Ancorhash.Contracts");
            }
        }

        throw new DirectoryNotFoundException("Radice del repository (Ancorhash.slnx) non trovata.");
    }

    private static bool IsOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? [tool + ".exe", tool] : new[] { tool };
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"AncorhashRegistry (?:already )?deployed at (?<address>0x[0-9a-fA-F]{40})")]
    private static partial Regex DeployedAtRegex();
}
