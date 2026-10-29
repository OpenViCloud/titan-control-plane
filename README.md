# titan-control-plane — OpenViCloud's API

`titan-control-plane` is the part of [OpenViCloud](https://github.com/OpenViCloud) that customers and the portals talk
to. It owns identity, tenancy, quota, billing and the wallet, and it records what each customer asked for. On Incus it
only reads, and relays SSH sessions. It writes provider commands to a table, and
[titan-incus-worker](https://github.com/OpenViCloud/titan-incus-worker) leases them over the `/internal` API, carries them out and
reports back what Incus shows.

The public API is `/v1`. Its contract is [`api/openapi-v1.json`](api/openapi-v1.json): a test renders the spec from
the running host and fails when it differs from that file.

## Build and test

Needs the .NET SDK pinned in [`global.json`](global.json).

```sh
dotnet build
./test-all.sh
```

`test-all.sh` runs the suite the way CI does, as three `dotnet test` processes (unit, full host, integration). The full
host and integration tests need a real PostgreSQL: `TITAN_IT_POSTGRES`, the OpenViCloud dev cluster, or Docker; the
script says which it used, and reports `INCOMPLETE` when it found none.

## Layout

Clean Architecture, one assembly per layer:

| Project | Role |
|---|---|
| `Titan.Host` | `Program.cs` only: wiring, middleware, `app.Run()` |
| `Titan.Application` | Command and query handlers, and the ports they need |
| `Titan.Domain` | Aggregates, domain errors, repository interfaces |
| `Titan.Infrastructure` | PostgreSQL (EF Core), the provider command table, Incus reads, HTTP endpoints |
| `Titan.Tests` | Unit, endpoint, full-host and integration tests |

## Docs

Design docs and the rules this code follows live in the titan-ops repository (private): `docs/architecture/` and
`docs/features/`. Code comments name one of them as `titan-ops <path under docs/>#<anchor>`.

## License

[MPL-2.0](LICENSE)
