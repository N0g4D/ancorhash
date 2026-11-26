using Ancorhash.Api.Middleware;
using Ancorhash.Core;
using Ancorhash.Infrastructure;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Anti-draining: rate limiting per IP su /notarize, prima di ogni function.
builder.UseMiddleware<RateLimitingMiddleware>();

// Azure Key Vault come configuration provider per i segreti dell'app: il segreto
// Sezione--Chiave diventa la chiave di configurazione Sezione:Chiave.
// La chiave del relayer non è un segreto ma una chiave secp256k1 che firma senza
// uscire dal vault (Blockchain:Relayer): un segreto Evm--RelayerPrivateKey non va
// creato, e in Azure farebbe fallire l'avvio.
// Auth via DefaultAzureCredential: Managed Identity in cloud, az login in locale.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential());
}

// host.json imposta telemetryMode=OpenTelemetry: senza un exporter registrato
// i log entrano nella pipeline OTel e non vengono emessi da nessuna parte
// (nessun output su console durante `func start`). Il console exporter li rende
// visibili in sviluppo; in cloud si riattiva .UseAzureMonitorExporter().
builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithLogging(logging => logging.AddConsoleExporter());
// .UseAzureMonitorExporter();

builder.Services.AddMemoryCache();

builder.Services
    .AddAncorhashCore()
    .AddAncorhashInfrastructure(builder.Configuration);

builder.Build().Run();
