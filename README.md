# RustArchon.Api

The RustArchon RESTful API: the `RustServer` entity/repository/controller, JWT bearer
authentication, multi-tenant data isolation, invitation-code issuance, and the internal
service-to-service endpoints used by
[RustArchon.Worker](https://github.com/RustArchon/RustArchon.Worker) and
[RustArchon.Panel](https://github.com/RustArchon/RustArchon.Panel) (credential handoff, queued
email). Owns the `RustArchon` database (distinct from Panel's separate Identity database).

Part of the [RustArchon](https://github.com/RustArchon/RustArchon) system - see that repo for the
full architecture and how to run the whole stack locally or via Docker Compose.

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
```
