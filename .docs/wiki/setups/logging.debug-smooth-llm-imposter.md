# Logging → message debug: dump the full inbound request

## TL;DR

The router logs at **`Information`** by default — operation lines only, no request contents. To troubleshoot
routing/auth/body issues you can flip the **`SmoothLlmImposter.Routing`** logger to **`Debug`**, which dumps the
**full inbound request** for every routed call: HTTP method, upstream path, query string, **all headers**, and the
**raw body**. Auth secrets are masked. It is off until you turn it on and needs **no rebuild** — the level is read
from configuration, so an env var is enough.

> Scope: this is the **inbound** request as received by the router (before model rewrite / auth swap). The
> outbound request to the upstream is logged separately by `UpstreamForwarder` (`Forwarding to {Provider} at
> {Target}`), also at `Debug`.

## What it logs

One `Debug` entry per routed request, e.g.:

```
[12:01:33 DBG] Inbound OpenAi request POST /v1/chat/completions
Headers:
  Host: localhost:5066
  content-type: application/json
  authorization: Bearer ***k2.7
  x-some-client-header: value
Body: { "model": "gpt5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }
```

- **Body-less requests** (e.g. `GET /v1/models`) log `Body: (empty)`.
- **Secret masking** — `Authorization` and `x-api-key` values are reduced to their scheme (if any) plus the last 4
  characters (`Bearer ***k2.7`); secrets of 4 chars or fewer are fully masked (`***`). The raw key is **never**
  written to the log. Every other header and the entire body are shown verbatim — do not enable `Debug` against a
  shared sink if your request bodies carry sensitive content.

## Enable it

The minimum level comes from the **`Serilog`** configuration section. The default (`appsettings.json`) is:

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

Raise just the router category to `Debug`. Two equivalent ways, depending on how you run:

| Run mode | How to set it |
|---|---|
| Env var (Docker / GHCR / Compose / shell) | `Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug` |
| `appsettings.json` / `appsettings.Development.json` | add `"SmoothLlmImposter.Routing": "Debug"` under `Serilog:MinimumLevel:Override` |
| Local dev secrets (`Development` only) | `dotnet user-secrets set "Serilog:MinimumLevel:Override:SmoothLlmImposter.Routing" "Debug"` |

> Scoping to the `SmoothLlmImposter.Routing` category keeps the dump on while everything else stays quiet. Setting
> `Serilog__MinimumLevel__Default=Debug` instead turns on **all** Debug logging (including the forwarder line) at
> the cost of much noisier output.

### Per run mode

**Local (`dotnet run`) / local-debug** — export before launching, or use a dev secret (above):

```bash
export Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug
dotnet run --project src/SmoothLlmImposter.Host
```

**Docker / GHCR** — add a `-e` flag:

```bash
docker run --rm -p 5080:5080 \
  -e "Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug" \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

**Compose** — add the override to the service's `environment:` block (or your `./.env`) in `docker-compose.yml`:

```yaml
environment:
  - Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug
```

## Verify

Enable `Debug` (any method above), start the router, then send a routed request and watch the logs:

```bash
# Compose port is 5066; local/docker default is 5080 — adjust to your run mode.
curl -fsS http://localhost:5066/openai/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'

docker compose logs -f          # podman-compose logs -f / docker logs / console
```

You should see the `Inbound OpenAi request …` dump above the forwarder's `Forwarding to …` line. Remove the
override (or set it back to `Information`) to return to clean production output.
