using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using Ancorhash.Core.Abstractions;
using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Models;
using Ancorhash.Infrastructure.Blockchain.Registry;
using Ancorhash.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.Contracts;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;

namespace Ancorhash.Infrastructure.Blockchain;

/// <summary>
/// Relayer EVM-agnostic su AncorhashRegistry. Il relayer è l'anchorer: firma con
/// la sua chiave (in Azure Key Vault, o in chiaro solo nello sviluppo locale),
/// paga il gas e i record sono intestati al suo indirizzo.
/// Per ogni richiesta:
/// <list type="number">
/// <item>legge anchoredAt(digest, relayer) per ogni digest distinto: un valore
/// diverso da zero vuol dire già ancorato, e per quel digest non parte nulla;</item>
/// <item>un digest ancora a zero ma con una transazione di questa istanza in
/// attesa (<see cref="PendingAnchorings"/>) torna pending con quella transazione;</item>
/// <item>se resta almeno un digest nuovo, invia anchorBatch con i soli digest
/// nuovi in una transazione EIP-1559;</item>
/// <item>attende la receipt fino a ReceiptTimeoutSeconds e prende gli orari
/// dagli eventi Anchored; oltre il timeout restituisce la transazione come pending.</item>
/// </list>
/// Il nonce si legge dal nodo a ogni invio; stima gas e fee 1559 restano al
/// TransactionManager di Nethereum. Il client Web3 per network arriva dalla
/// factory (cache singleton).
/// </summary>
public sealed class NethereumBlockchainRelayer(
    IWeb3Factory web3Factory,
    IPriceOracleService priceOracle,
    PendingAnchorings pendingAnchorings,
    IOptions<BlockchainOptions> options,
    ILogger<NethereumBlockchainRelayer> logger) : IBlockchainRelayer
{
    private const int MaxSendAttempts = 3;

    // Letture anchoredAt in parallelo, ma senza sommergere i nodi RPC pubblici:
    // un batch da 100 digest costa 100 eth_call.
    private const int MaxConcurrentReads = 8;

    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReceiptPollingInterval = TimeSpan.FromSeconds(1);

    private readonly BlockchainOptions _options = options.Value;

    // Chain su cui il codice del registry è già stato trovato: il controllo costa
    // una chiamata RPC per chain, una volta per processo.
    private readonly ConcurrentDictionary<int, bool> _deployedRegistries = new();

    // Un invio alla volta per chain in questa istanza: lettura del nonce e broadcast
    // non si sovrappongono.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _sendLocks = new();

    public async Task<AnchoringOutcome> AnchorAsync(
        int chainId,
        IReadOnlyList<Digest> digests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        if (digests.Count == 0)
        {
            throw new ArgumentException("Serve almeno un digest da ancorare.", nameof(digests));
        }

        var registry = _options.GetRegistryAddress(chainId);
        var web3 = await web3Factory.GetWeb3Async(chainId, cancellationToken).ConfigureAwait(false);
        var anchorer = web3.TransactionManager.Account.Address;

        await EnsureRegistryDeployedAsync(web3, chainId, registry, cancellationToken).ConfigureAwait(false);

        // 1. Una lettura per digest distinto: quelli già ancorati restano fuori dalla
        //    transazione, e se erano in attesa di conferma la conferma è arrivata.
        var distinct = digests.Distinct().ToArray();
        var anchoredAt = await ReadAnchoredAtAsync(web3, chainId, registry, anchorer, distinct, cancellationToken)
            .ConfigureAwait(false);
        pendingAnchorings.Forget(chainId, anchorer, anchoredAt.Keys);

        // Fra i non ancorati, quelli con una transazione di questa istanza ancora in
        // attesa non ripartono: tornano pending con lo stesso hash.
        var awaiting = new Dictionary<Digest, string>();
        var newDigests = new List<Digest>();
        foreach (var digest in distinct.Where(digest => !anchoredAt.ContainsKey(digest)))
        {
            if (pendingAnchorings.TryGetTransaction(chainId, anchorer, digest, out var pendingTransaction))
            {
                awaiting[digest] = pendingTransaction;
            }
            else
            {
                newDigests.Add(digest);
            }
        }

        if (newDigests.Count == 0)
        {
            logger.LogInformation(
                "Ancoraggio su chain {ChainId}: nessuna transazione, {ExistingCount} digest già ancorati e {AwaitingCount} in attesa di una transazione precedente",
                chainId, anchoredAt.Count, awaiting.Count);
            return Outcome(digests.Select(KnownResult));
        }

        // 2. Una sola transazione per tutti i digest nuovi, che da qui risultano pending.
        var transactionHash = await SendAnchorBatchAsync(web3, chainId, registry, newDigests, cancellationToken)
            .ConfigureAwait(false);
        pendingAnchorings.Track(chainId, anchorer, newDigests, transactionHash);

        // 3. Receipt entro il timeout, altrimenti i digest nuovi tornano come pending.
        var receipt = await WaitForReceiptAsync(web3, chainId, transactionHash, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            logger.LogWarning(
                "Receipt di {TransactionHash} non arrivata entro {TimeoutSeconds}s su chain {ChainId}: {PendingCount} digest restituiti come pending",
                transactionHash, _options.ReceiptTimeoutSeconds, chainId, newDigests.Count);
            foreach (var digest in newDigests)
            {
                awaiting[digest] = transactionHash;
            }

            return Outcome(digests.Select(KnownResult));
        }

        pendingAnchorings.Forget(chainId, anchorer, newDigests);
        if (receipt.Status?.Value.IsZero == true)
        {
            throw new BlockchainTransactionException(
                $"La transazione {transactionHash} è stata revertita (chain {chainId}).");
        }

        var written = DecodeAnchoredEvents(receipt, registry, anchorer);
        foreach (var (digest, timestamp) in written)
        {
            anchoredAt[digest] = timestamp;
        }

        // Un digest nuovo senza evento l'ha ancorato un'altra transazione del relayer
        // tra la lettura e il mining, ad esempio due richieste concorrenti con lo
        // stesso digest: per questa richiesta è già ancorato, e l'orario va riletto.
        var missing = newDigests.Where(digest => !written.ContainsKey(digest)).ToArray();
        if (missing.Length > 0)
        {
            var reread = await ReadAnchoredAtAsync(web3, chainId, registry, anchorer, missing, cancellationToken)
                .ConfigureAwait(false);
            foreach (var digest in missing)
            {
                anchoredAt[digest] = reread.TryGetValue(digest, out var timestamp)
                    ? timestamp
                    : throw new BlockchainTransactionException(
                        $"La transazione {transactionHash} non ha registrato il digest {digest} sul registry {registry} (chain {chainId}).");
            }
        }

        logger.LogInformation(
            "Ancoraggio su chain {ChainId} confermato in {TransactionHash}: {AnchoredCount} digest ancorati ora, {ExistingCount} già ancorati",
            chainId, transactionHash, written.Count, distinct.Length - written.Count);

        // 4. Un esito per digest richiesto, nell'ordine ricevuto. Come nel contratto,
        //    solo la prima occorrenza di un digest scritto da questa transazione
        //    risulta ancorata ora: le sue ripetizioni risultano già ancorate.
        var results = new List<AnchoringResult>(digests.Count);
        var reported = new HashSet<Digest>();
        foreach (var digest in digests)
        {
            results.Add(written.ContainsKey(digest) && reported.Add(digest)
                ? AnchoringResult.Anchored(digest, anchoredAt[digest], transactionHash)
                : KnownResult(digest));
        }

        return Outcome(results);

        // Esito di un digest che questa transazione non ha scritto: già ancorato,
        // oppure in attesa di una transazione ancora senza receipt.
        AnchoringResult KnownResult(Digest digest) =>
            anchoredAt.TryGetValue(digest, out var timestamp)
                ? AnchoringResult.AlreadyAnchoredAt(digest, timestamp)
                : AnchoringResult.Pending(digest, awaiting[digest]);

        AnchoringOutcome Outcome(IEnumerable<AnchoringResult> outcomeResults) =>
            new(chainId, registry, anchorer, outcomeResults.ToArray());
    }

    public async Task<decimal> EstimateFiatCostAsync(
        int chainId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Networks.TryGetValue(chainId, out var network))
        {
            throw new UnsupportedChainException(chainId);
        }

        logger.LogInformation(
            "Stima costo chain {ChainId}: IsTestnet={IsTestnet}, NativeCoinId={CoinId}",
            chainId, network.IsTestnet, network.NativeCoinId ?? "(null)");

        // Testnet: il gas non ha valore reale → notarizzazione gratuita.
        if (network.IsTestnet)
        {
            logger.LogInformation(
                "Stima costo chain {ChainId}: testnet, costo 0 senza chiamate RPC", chainId);
            return 0m;
        }

        var registry = _options.GetRegistryAddress(chainId);
        var web3 = await web3Factory.GetWeb3Async(chainId, cancellationToken).ConfigureAwait(false);
        await EnsureRegistryDeployedAsync(web3, chainId, registry, cancellationToken).ConfigureAwait(false);

        // Chiamata rappresentativa: anchor(bytes32) con un digest casuale, quindi mai
        // ancorato. La stima è quella di un primo ancoraggio, non di un retry no-op.
        var probe = new AnchorFunction { Digest = RandomNumberGenerator.GetBytes(Digest.Length) };

        try
        {
            var gasLimit = await web3.Eth.GetContractTransactionHandler<AnchorFunction>()
                .EstimateGasAsync(registry, probe)
                .ConfigureAwait(false);
            var gasPrice = await web3.Eth.GasPrice
                .SendRequestAsync()
                .ConfigureAwait(false);

            logger.LogInformation(
                "Stima costo chain {ChainId}: gasLimit={GasLimit}, gasPrice={GasPrice} wei",
                chainId, gasLimit.Value, gasPrice.Value);

            // Costo nativo (in ETH/MATIC/…): wei → unità intera (18 decimali).
            var nativeCost = Web3.Convert.FromWei(gasLimit.Value * gasPrice.Value);

            var usdPrice = network.NativeCoinId is { Length: > 0 } coinId
                ? await priceOracle.GetUsdPriceAsync(coinId, cancellationToken).ConfigureAwait(false)
                : null;

            logger.LogInformation(
                "Stima costo chain {ChainId}: costo nativo={NativeCost}, prezzo USD={UsdPrice}",
                chainId, nativeCost, usdPrice?.ToString() ?? "(null)");

            if (usdPrice is null)
            {
                logger.LogWarning(
                    "Stima costo chain {ChainId}: prezzo USD non disponibile (coin {CoinId})",
                    chainId, network.NativeCoinId ?? "n/d");
                return 0m;
            }

            // 8 decimali: su Polygon una notarizzazione costa ~$0.0007, quindi
            // arrotondare al quarto decimale sarebbe già al limite del troncamento a 0.
            var costUsd = decimal.Round(nativeCost * usdPrice.Value, 8, MidpointRounding.AwayFromZero);
            logger.LogInformation(
                "Stima costo chain {ChainId}: {NativeCost} native × {UsdPrice} USD = {CostUsd} USD",
                chainId, nativeCost, usdPrice.Value, costUsd);
            return costUsd;
        }
        catch (Exception ex) when (
            ex is RpcResponseException || IsConnectionFailure(ex, cancellationToken))
        {
            // LogCritical: forzato in output anche con filtri di log restrittivi.
            logger.LogCritical(ex,
                "Stima costo chain {ChainId} fallita: {ExceptionType}", chainId, ex.GetType().Name);
            throw new BlockchainUnavailableException(
                $"Impossibile stimare il costo del gas sulla chain {chainId}.", ex);
        }
    }

    private async Task EnsureRegistryDeployedAsync(
        IWeb3 web3,
        int chainId,
        string registry,
        CancellationToken cancellationToken)
    {
        if (_deployedRegistries.ContainsKey(chainId))
        {
            return;
        }

        string code;
        try
        {
            code = await web3.Eth.GetCode.SendRequestAsync(registry).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is RpcResponseException || IsConnectionFailure(ex, cancellationToken))
        {
            throw new BlockchainUnavailableException(
                $"Impossibile comunicare con il nodo RPC della chain {chainId}.", ex);
        }

        // Senza codice all'indirizzo, anchorBatch "riuscirebbe" senza scrivere nulla:
        // sarebbe una transazione qualunque con i digest nel solo campo data.
        if (string.IsNullOrEmpty(code) || code is "0x" or "0x0")
        {
            throw new InvalidOperationException(
                $"AncorhashRegistry non risulta deployato all'indirizzo {registry} sulla chain {chainId}: verificare Blockchain:RegistryAddress.");
        }

        _deployedRegistries.TryAdd(chainId, true);
    }

    private static async Task<Dictionary<Digest, DateTimeOffset>> ReadAnchoredAtAsync(
        IWeb3 web3,
        int chainId,
        string registry,
        string anchorer,
        IReadOnlyCollection<Digest> digests,
        CancellationToken cancellationToken)
    {
        var handler = web3.Eth.GetContractQueryHandler<AnchoredAtFunction>();
        var anchored = new ConcurrentDictionary<Digest, DateTimeOffset>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentReads,
            CancellationToken = cancellationToken,
        };

        try
        {
            await Parallel.ForEachAsync(digests, parallelOptions, async (digest, _) =>
            {
                var query = new AnchoredAtFunction { Digest = digest.ToArray(), Anchorer = anchorer };
                var timestamp = await handler.QueryAsync<BigInteger>(registry, query, null).ConfigureAwait(false);
                if (!timestamp.IsZero)
                {
                    anchored[digest] = ToDateTimeOffset(timestamp);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is RpcResponseException || IsConnectionFailure(ex, cancellationToken))
        {
            throw new BlockchainUnavailableException(
                $"Impossibile leggere il registry dal nodo RPC della chain {chainId}.", ex);
        }

        return new Dictionary<Digest, DateTimeOffset>(anchored);
    }

    private async Task<string> SendAnchorBatchAsync(
        IWeb3 web3,
        int chainId,
        string registry,
        IReadOnlyList<Digest> digests,
        CancellationToken cancellationToken)
    {
        var handler = web3.Eth.GetContractTransactionHandler<AnchorBatchFunction>();
        var sender = web3.TransactionManager.Account.Address;
        var sendLock = _sendLocks.GetOrAdd(chainId, _ => new SemaphoreSlim(1, 1));

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string transactionHash;
                await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Il nonce si legge dal nodo a ogni invio, mai da un contatore in
                    // memoria: dopo una transazione scartata dalla mempool il contatore
                    // resterebbe avanti, e ogni invio successivo finirebbe in coda dietro
                    // un nonce mancante. Il lock evita che due invii di questa istanza
                    // leggano lo stesso nonce. Con il nonce nel messaggio Nethereum non usa
                    // il suo InMemoryNonceService, né con Account né con ExternalAccount
                    // (Key Vault).
                    var nonce = await web3.Eth.Transactions.GetTransactionCount
                        .SendRequestAsync(sender, BlockParameter.CreatePending())
                        .ConfigureAwait(false);

                    // Messaggio nuovo a ogni tentativo: Nethereum vi scrive il gas stimato.
                    var message = new AnchorBatchFunction
                    {
                        Digests = digests.Select(digest => digest.ToArray()).ToList(),
                        Nonce = nonce.Value,
                    };
                    transactionHash = await handler
                        .SendRequestAsync(registry, message)
                        .ConfigureAwait(false);
                }
                finally
                {
                    sendLock.Release();
                }

                logger.LogInformation(
                    "anchorBatch inviata su chain {ChainId}: {TransactionHash} con {DigestCount} digest",
                    chainId, transactionHash, digests.Count);
                return transactionHash;
            }
            catch (Exception ex) when (ex is SmartContractRevertException or SmartContractCustomErrorRevertException)
            {
                // La stima del gas ha simulato un revert: è deterministico, ritentare non serve.
                throw new BlockchainTransactionException(
                    $"anchorBatch verrebbe revertita sulla chain {chainId}: {ex.Message}", ex);
            }
            catch (RpcResponseException ex) when (attempt < MaxSendAttempts)
            {
                // Errori applicativi del nodo (es. nonce too low): retry con backoff esponenziale.
                var delay = BaseRetryDelay * Math.Pow(2, attempt - 1);
                logger.LogWarning(
                    "Broadcast rifiutato dal nodo RPC su chain {ChainId} (tentativo {Attempt}/{MaxAttempts}), retry tra {DelaySeconds}s: {RpcError}",
                    chainId, attempt, MaxSendAttempts, delay.TotalSeconds, ex.RpcError?.Message ?? ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcResponseException ex)
            {
                throw new BlockchainTransactionException(
                    $"Broadcast rifiutato dal nodo RPC (chain {chainId}) dopo {MaxSendAttempts} tentativi: {ex.RpcError?.Message ?? ex.Message}",
                    ex);
            }
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
            {
                throw new BlockchainUnavailableException(
                    $"Impossibile comunicare con il nodo RPC della chain {chainId}.", ex);
            }
        }
    }

    /// <summary>
    /// Attende la receipt fino al timeout configurato. Null se non arriva in tempo:
    /// la transazione è già partita, quindi un errore del nodo durante l'attesa non
    /// è un fallimento e il polling continua fino alla scadenza.
    /// </summary>
    private async Task<TransactionReceipt?> WaitForReceiptAsync(
        IWeb3 web3,
        int chainId,
        string transactionHash,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ReceiptTimeoutSeconds));

        try
        {
            while (true)
            {
                try
                {
                    // WaitAsync: una richiesta appesa non deve sforare il timeout.
                    var receipt = await web3.Eth.Transactions.GetTransactionReceipt
                        .SendRequestAsync(transactionHash)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                    if (receipt is not null)
                    {
                        return receipt;
                    }
                }
                catch (Exception ex) when (
                    ex is RpcResponseException || IsConnectionFailure(ex, timeout.Token))
                {
                    logger.LogWarning(
                        "Lettura della receipt di {TransactionHash} fallita su chain {ChainId}, nuovo tentativo: {ExceptionType}",
                        transactionHash, chainId, ex.GetType().Name);
                }

                await Task.Delay(ReceiptPollingInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Dictionary<Digest, DateTimeOffset> DecodeAnchoredEvents(
        TransactionReceipt receipt,
        string registry,
        string anchorer)
    {
        var written = new Dictionary<Digest, DateTimeOffset>();
        foreach (var anchored in receipt.DecodeAllEvents<AnchoredEventDTO>())
        {
            if (anchored.Log.Address.IsTheSameAddress(registry)
                && anchored.Event.Anchorer.IsTheSameAddress(anchorer))
            {
                written.TryAdd(Digest.FromBytes(anchored.Event.Digest), ToDateTimeOffset(anchored.Event.Timestamp));
            }
        }

        return written;
    }

    private static DateTimeOffset ToDateTimeOffset(BigInteger timestamp) =>
        DateTimeOffset.FromUnixTimeSeconds((long)timestamp);

    private static bool IsConnectionFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is RpcClientUnknownException or RpcClientTimeoutException or HttpRequestException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
