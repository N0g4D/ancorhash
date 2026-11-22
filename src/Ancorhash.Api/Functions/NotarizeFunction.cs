using System.Text.Json;
using Ancorhash.Api.Contracts;
using Ancorhash.Api.Http;
using Ancorhash.Core.Abstractions;
using Ancorhash.Core.Exceptions;
using Ancorhash.Core.Models;
using Ancorhash.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Ancorhash.Api.Contracts.ApiResults;

namespace Ancorhash.Api.Functions;

/// <summary>
/// POST /api/v1/notarize e POST /api/v1/notarize/batch — ancorano su
/// AncorhashRegistry digest calcolati lato client.
/// Idempotenti: un digest già ancorato è un 200 con l'orario originale, mai un
/// errore. 202 se la transazione è partita ma la receipt non è arrivata in tempo:
/// ripetere la stessa richiesta è sicuro e restituisce l'orario di ancoraggio.
/// Autenticazione: function key nativa di Azure Functions più Turnstile, su
/// entrambi gli endpoint.
/// </summary>
public sealed class NotarizeFunction(
    INotarizationService notarizationService,
    ITurnstileValidator turnstileValidator,
    IOptions<BlockchainOptions> blockchainOptions,
    ILogger<NotarizeFunction> logger)
{
    [Function("Notarize")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/notarize")]
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var (payload, invalidBody) = await ReadJsonAsync<NotarizeHttpRequest>(request, cancellationToken);
        if (payload is null)
        {
            return invalidBody!;
        }

        var rejected = await RejectUnverifiedAsync(request, payload.TurnstileToken, cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

        var command = new NotarizationCommand(
            DocumentId: payload.DocumentId ?? string.Empty,
            DocumentHash: payload.DocumentHash ?? string.Empty,
            ChainId: payload.ChainId ?? 0);

        return await NotarizeAsync(
            "Notarize",
            () => notarizationService.NotarizeAsync(command, cancellationToken),
            (result, explorerUrl) => NotarizeHttpResponse.From(result, result.Documents[0], explorerUrl));
    }

    [Function("NotarizeBatch")]
    public async Task<IActionResult> RunBatchAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/notarize/batch")]
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var (payload, invalidBody) = await ReadJsonAsync<NotarizeBatchHttpRequest>(request, cancellationToken);
        if (payload is null)
        {
            return invalidBody!;
        }

        var rejected = await RejectUnverifiedAsync(request, payload.TurnstileToken, cancellationToken);
        if (rejected is not null)
        {
            return rejected;
        }

        var command = new BatchNotarizationCommand(
            Items: payload.Items?
                .Select(item => new NotarizationItem(
                    item?.DocumentId ?? string.Empty,
                    item?.DocumentHash ?? string.Empty))
                .ToList() ?? [],
            ChainId: payload.ChainId ?? 0);

        return await NotarizeAsync(
            "NotarizeBatch",
            () => notarizationService.NotarizeBatchAsync(command, cancellationToken),
            NotarizeBatchHttpResponse.From);
    }

    /// <summary>
    /// Esegue la notarizzazione e mappa l'esito: 200 se tutto è on-chain, 202 se
    /// qualcosa attende la conferma, e gli errori di dominio sui codici HTTP.
    /// </summary>
    private async Task<IActionResult> NotarizeAsync(
        string operation,
        Func<Task<NotarizationResult>> notarize,
        Func<NotarizationResult, string?, object> toResponse)
    {
        try
        {
            var result = await notarize();
            var body = toResponse(result, ExplorerUrl(result.ChainId));
            return result.IsPending
                ? new ObjectResult(body) { StatusCode = StatusCodes.Status202Accepted }
                : new OkObjectResult(body);
        }
        catch (NotarizationValidationException ex)
        {
            logger.LogWarning("{Operation} rifiutata: {Reason}", operation, ex.Message);
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (UnsupportedChainException ex)
        {
            logger.LogWarning(
                "{Operation} rifiutata: chain {ChainId} non supportata", operation, ex.ChainId);
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (BlockchainUnavailableException ex)
        {
            logger.LogError(ex, "{Operation}: nodo RPC non raggiungibile", operation);
            return Error(StatusCodes.Status502BadGateway,
                "Nodo blockchain non raggiungibile. Riprovare più tardi.");
        }
        catch (BlockchainTransactionException ex)
        {
            logger.LogError(ex, "{Operation}: ancoraggio on-chain fallito", operation);
            return Error(StatusCodes.Status500InternalServerError, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Difesa anti-draining: la verifica anti-bot avviene PRIMA di ogni
    /// validazione di dominio e di ogni interazione con la blockchain.
    /// </summary>
    private async Task<IActionResult?> RejectUnverifiedAsync(
        HttpRequest request,
        string? turnstileToken,
        CancellationToken cancellationToken)
    {
        var clientIp = ClientIpResolver.Resolve(request.HttpContext);
        if (await turnstileValidator.ValidateAsync(turnstileToken ?? string.Empty, clientIp, cancellationToken))
        {
            return null;
        }

        logger.LogWarning(
            "Notarize bloccata: verifica Turnstile fallita per IP {ClientIp}", clientIp);
        return Error(StatusCodes.Status403Forbidden,
            "Verifica anti-bot fallita. Aggiorna la pagina e riprova.");
    }

    private static async Task<(T? Payload, IActionResult? Error)> ReadJsonAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return (null, Error(StatusCodes.Status400BadRequest,
                "Il body deve essere JSON (Content-Type: application/json)."));
        }

        try
        {
            var payload = await request.ReadFromJsonAsync<T>(cancellationToken);
            return payload is null
                ? (null, Error(StatusCodes.Status400BadRequest, "Body della richiesta mancante."))
                : (payload, null);
        }
        catch (JsonException)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "Body JSON non valido."));
        }
    }

    private string? ExplorerUrl(int chainId) =>
        blockchainOptions.Value.Networks.TryGetValue(chainId, out var network)
            ? network.ExplorerUrl
            : null;
}
