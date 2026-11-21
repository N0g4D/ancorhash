# Ancorhash

Ancorhash proves what an automated process produced, and when. A bot, an RPA
flow or an agent computes the SHA-256 of an output; Ancorhash anchors that
digest on a public EVM chain through `AncorhashRegistry`, so that anyone who
holds the output can check it later without trusting Ancorhash.

## Repository map

- `src/Ancorhash.Contracts/`: Foundry project with `AncorhashRegistry`. Read
  its README before touching anything that anchors: its invariants are the spec.
- `src/Ancorhash.Core/`: domain models, abstractions and services. No Azure or
  Nethereum dependencies.
- `src/Ancorhash.Infrastructure/`: Nethereum, Azure SDK and other external
  services.
- `src/Ancorhash.Api/`: Azure Functions (.NET 10, isolated worker), HTTP
  endpoints only.
- `src/Ancorhash.Web/`: React, TypeScript, Vite front end.
- `docs/tasks/`: planned work, one file per task. Implement a task only when
  asked to.
- `legacy_python/`: the original FastAPI proof of concept. Do not change it.

## Commands

- Build: `dotnet build Ancorhash.slnx`
- Test: `dotnet test Ancorhash.slnx`
- Format: `dotnet format Ancorhash.slnx`
- Contracts: `cd src/Ancorhash.Contracts && forge build && forge test`
  (`forge fmt --check` must pass too, as in CI)
- Web: `cd src/Ancorhash.Web && npm ci && npm run build && npm run lint`

## Hard rules

1. Documents never reach the backend. It accepts only digests computed by the
   client (32 bytes, as 64 hex characters or Base64) and never computes or
   stores document content.
2. Clean Architecture: domain entities and interfaces in Core, Nethereum and
   Azure SDK calls in Infrastructure, Azure Functions in Api.
3. Configuration goes through `IOptions<T>`, validated at startup
   (`ValidateOnStart`).
4. No private keys in app settings or Key Vault secrets in the cloud: signing
   keys are non-exportable secp256k1 keys in Azure Key Vault. A raw private key
   is allowed only for local development against anvil.
5. Anchoring is idempotent end to end: a digest that is already anchored is a
   success that returns the original time, never an error.
6. The `ethrome-2026` branch is frozen. Never merge it, and bring code over
   from it only when asked to.

## Conventions

- C# comments in Italian, as in the existing code. Solidity NatSpec and public
  docs in English, because auditors and block explorers read them.
- Commit messages in English, Conventional Commits: `feat(contracts): …`,
  `fix: …`, `chore: …`.
- Contract changes come with tests (unit, fuzz, and invariant where it fits).
