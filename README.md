# RustArchon.Api

The RustArchon RESTful API: the `RustServer` entity/repository/controller, JWT bearer
authentication, multi-tenant data isolation, invitation-code issuance, and the internal
service-to-service endpoints used by
[RustArchon.Worker](https://github.com/RustArchon/RustArchon.Worker) and
[RustArchon.Panel](https://github.com/RustArchon/RustArchon.Panel) (credential handoff, queued
email). Owns the `RustArchon` database (distinct from Panel's separate Identity database).

Part of the [RustArchon](https://github.com/RustArchon/RustArchon) system - see that repo for the
full architecture and how to run the whole stack locally or via Docker Compose.

## Key files

- `Controllers/RustServersController.cs` - the core resource: register/list/update/delete a Rust
  server, tenant-scoped and permission-gated via JumpStart's `ApiControllerBase<TEntity,...>`.
- `Infrastructure/Security/RconCredentialProtector.cs` - encrypts/decrypts RCON passwords via
  ASP.NET Core Data Protection; `RustServerDto` never exposes the plaintext value.
- `Controllers/{Invitations,InvitationCodes}Controller.cs`,
  `Infrastructure/AdminInvitationSeeder.cs` - the invitation-gated sign-up mechanism (soft-launch
  access control) and its bootstrap-a-first-admin path.
- `Controllers/InternalController.cs`,
  `Infrastructure/Authentication/InternalApiKeyAuthenticationHandler.cs` - the shared-secret
  (non-JWT) endpoints Worker and Panel call for things no end-user JWT applies to (queued email).
- `Data/ApiDbContext.cs`, `Migrations/` - this API's own database, separate from Panel's Identity
  database.
- `Program.cs` - MassTransit/RabbitMQ registration, JWT bearer setup, and the persisted Data
  Protection key ring used for RCON password encryption (see its comments for why that's a
  *different* key ring from the one Panel/Web share for session cookies).

## License

AGPL-3.0-or-later - see [`LICENSE`](LICENSE). This project also depends on
[JumpStart](https://github.com/cyberknet/JumpStart), a separate GPL-3.0-or-later project - see
[`NOTICE.md`](NOTICE.md) for how the two combine.

## Building standalone

**This repo cannot be built on its own.** It reaches JumpStart via a `ProjectReference` to
`../JumpStart/JumpStart/JumpStart.csproj`, a path that only resolves inside the
[umbrella repo's](https://github.com/RustArchon/RustArchon) submodule layout. Clone that instead:

```bash
git clone --recurse-submodules https://github.com/RustArchon/RustArchon.git
cd RustArchon/RustArchon.Api
dotnet ef database update   # first run only - see the umbrella README's "Running locally"
dotnet run
# Swagger at https://localhost:7130/swagger
```
