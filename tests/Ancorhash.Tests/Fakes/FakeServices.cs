using Ancorhash.Core.Abstractions;

namespace Ancorhash.Tests.Fakes;

/// <summary>Turnstile finto: accetta solo <see cref="ValidToken"/>.</summary>
internal sealed class FakeTurnstileValidator : ITurnstileValidator
{
    public const string ValidToken = "turnstile-ok";

    public Task<bool> ValidateAsync(string token, string? remoteIp, CancellationToken cancellationToken = default) =>
        Task.FromResult(token == ValidToken);
}

/// <summary>Oracolo prezzi finto con un prezzo fisso (null = non disponibile).</summary>
internal sealed class FixedPriceOracle(decimal? usdPrice) : IPriceOracleService
{
    public Task<decimal?> GetUsdPriceAsync(string coinId, CancellationToken cancellationToken = default) =>
        Task.FromResult(usdPrice);
}
