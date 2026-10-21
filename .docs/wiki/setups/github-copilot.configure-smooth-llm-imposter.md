# GitHub Copilot (opt-in) — configure the imposter

Opt-in integration that lets the router send Anthropic Messages and OpenAI Responses traffic to a GitHub Copilot
account, authenticated by a browser device login. The feature is **off unless `ENABLE_GITHUB_COPILOT=true`**; when
off it opens no database, exposes no endpoints and adds no documentation page.

- **Design:** [HLD 013](../../hlds/013-github-copilot/README.md)
- **Tracker:** [#126](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/126)
- **Ports in this guide:** `5080` for the Conductor sandbox container. Substitute the host port of your run mode
  (`docker-compose.yml` publishes `5066`; a local `dotnet run` defaults to the launch profile's port).

## 1. Register your own GitHub OAuth application

The router ships **no** hardcoded application identity. Create an OAuth app under your GitHub account and enable
**device flow**:

1. GitHub → *Settings → Developer settings → OAuth Apps → New OAuth App*.
2. Enable device flow (the app needs no callback URL for this flow).
3. Copy the **Client ID**. Device flow needs **no client secret** — never paste a secret into this workflow.

The client ID is a public identifier. Each prefix can use a different application; one prefix's provider pair
always shares the client ID used at its login.

## 2. Enable the feature with a dedicated volume

The database (`copilot.db`) and an isolated Data Protection key ring (`keys/`) live under `/data/github-copilot`.
Use the compose overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.github-copilot.yml up -d
```

or plain Docker:

```bash
docker run -d --name smooth-llm-imposter --restart unless-stopped \
  -p 5080:5066 \
  -e ASPNETCORE_URLS=http://+:5066 \
  -e ENABLE_GITHUB_COPILOT=true \
  -e GithubCopilot__DatabasePath=/data/github-copilot/copilot.db \
  -e Admin__ApiKey="$ADMIN_KEY" \
  -v smooth-llm-imposter-copilot:/data/github-copilot \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
```

`GithubCopilot__DatabasePath` is required when the feature is enabled — there is deliberately no generated
fallback, because a path that changes between restarts strands saved connections. The flag can enable
application behavior; it cannot instruct Docker to create a mount, so the volume must be supplied here.

Optional: `-e GITHUB_COPILOT_CLIENT_ID=Ov...` sets a default application. Operators may still supply their own
`clientId` on each login.

## 3. The ordered workflow (curl)

Every response carries a `message` field naming the next step, so curl alone is enough. Scalar mirrors the same
order at `/scalar/v1` (numbered operations `github-copilot-01-…` through `05-…`, with the `X-Admin-Api-Key`
scheme); OpenAPI is at `/openapi/v1.json`.

```bash
BASE=http://127.0.0.1:5080
ADMIN_KEY=...            # your Admin:ApiKey
CLIENT_ID=Ov...          # your OAuth app's client ID
```

### 01 — Start GitHub login

```bash
curl -s -X POST "$BASE/admin/github-copilot/logins" \
  -H "X-Admin-Api-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"prefix":"company1","clientId":"'"$CLIENT_ID"'"}'
```

Omit `prefix` for the unprefixed pair (`github-anthropic` + `github-openai`). Prefixes are lowercase slugs
(`a-z`, `0-9`, single hyphens) and isolate accounts — one login supplies both keys of its prefix.

The response includes `loginId`, `clientId` (the effective ID), `providerKeys`, `verificationUri`, `userCode`,
`expiresAtUtc`, `intervalSeconds`, `completeUrl`, and `message`:

> Open https://github.com/login/device in a browser, enter ABCD-1234, then poll POST
> /admin/github-copilot/logins/<loginId>/complete every 5s.

The GitHub `device_code` stays server-side; no response returns it or a token.

### 02 — Complete GitHub login (poll)

```bash
curl -si -X POST "$BASE/admin/github-copilot/logins/<loginId>/complete" \
  -H "X-Admin-Api-Key: $ADMIN_KEY"
```

Poll no faster than `intervalSeconds`; the server enforces the interval itself and honours GitHub's `slow_down`.

| Status | Meaning | `message` |
|---|---|---|
| `202` | Pending; `Retry-After` header set | "Authorization still pending — open … and enter … if you have not, then retry … after Ns." |
| `200` | Saved **and** published (`publicationReady:true`) | "Connection … saved and published. Configure routing: PUT /admin/providers/… with a From/To model mapping." |
| `503` | Saved but not published, or a retryable GitHub/store failure | "Credential … is saved but not yet published — a restart re-materializes missing provider pairs without another token exchange." |
| `404` | Unknown login ID (completed, expired, or the process restarted) | Start a new login. |
| `400` | Terminal: denied, expired, not entitled, invalid origin | A safe explanation; start a new login. |

A `200` does **not** require a mapping: empty mappings and deliberately disabled providers still complete the
login. A retried Complete after success returns the same connection ID and revision without contacting GitHub.

### 03 — Discover supported models

```bash
curl -s "$BASE/admin/github-copilot/connections/<connectionId>/models" \
  -H "X-Admin-Api-Key: $ADMIN_KEY"
```

Returns account-advertised models with the endpoint surfaces they support (`messages`, `responses`). Chat-only
entries are filtered out because Copilot Chat is out of scope. Discovery never creates a mapping or enables a
provider.

### 04 — Configure imposter mappings

Use the existing secret-free provider endpoint. Models and `Enabled` are editable; identity (dialect, Bearer
auth, origin) is fixed.

```bash
curl -s -X PUT "$BASE/admin/providers/company1-github-anthropic" \
  -H "X-Admin-Api-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{
        "name": null,
        "dialect": "anthropic",
        "baseUrl": "https://api.githubcopilot.com",
        "authScheme": "Bearer",
        "authHeader": null,
        "isDefault": false,
        "enabled": true,
        "anthropicVersion": null,
        "openAiUpstreamApi": "responses",
        "requestNormalization": "none",
        "sessionForwarding": null,
        "stripEncryptedContent": null,
        "timeoutSeconds": null,
        "models": [{"from":"claude-*","to":"claude-haiku-4.5","caching":false}]
      }'
```

Repeat for `company1-github-openai` with `"dialect":"openai"` and a GPT mapping. Runtime mappings are
ephemeral (HLD 008): after a restart, config/env mappings return and API-only mappings must be reapplied.

### 05 — Verify connection and route

```bash
curl -s "$BASE/admin/github-copilot/connections/<connectionId>" -H "X-Admin-Api-Key: $ADMIN_KEY"
curl -s "$BASE/admin/github-copilot/connections"        -H "X-Admin-Api-Key: $ADMIN_KEY"
```

Each connection reports `savedRevision`, `publishedRevision`, `publicationReady`, `reauthenticationRequired`
and per-provider `enabled` / `routingReady`. The `message` distinguishes:

- `publicationReady:false` → restart hint (publication recovery),
- `routingReady:false` with a published revision → missing mapping or disabled provider; add a mapping with
  `PUT /admin/providers/<key>` — a restart is **not** required.

A real inference smoke test is a normal request through the dialect prefix, e.g. `POST /anthropic/v1/messages`
with a mapped model, or the in-band probe (`/imposter-who` skill / non-streaming `--who?`).

## 4. Credential removal and the threat boundary

V1 has **no** in-app logout/revoke endpoint, and **deleting a provider is not a logout** — a connected prefix's
keys return 409 until its connection is gone.

| Action | Effect |
|---|---|
| `docker rm -f smooth-llm-imposter` / image deletion | Named volume data stays |
| `docker compose down` (no `-v`) | Named volume data stays |
| Recreate the container against the same volume | Credentials survive; decryptable |
| Disable `ENABLE_GITHUB_COPILOT` | Data stays; re-enabling reopens it |
| `docker volume rm smooth-llm-imposter-copilot` | Deletes **every** saved prefix and the key ring |

Stop/remove the service first, then remove the exact dedicated volume by its verified name. Do **not** use
`docker compose down -v` as feature cleanup — it can remove unrelated project volumes. Removing the volume is
logical deletion, not forensic secure erasure, and it does not revoke anything on GitHub: revoke the OAuth
application's authorization separately in GitHub settings.

The key ring sits beside the ciphertext in the same volume. That protects an isolated copy of the database; it
does **not** protect against a host/Docker administrator or theft of the whole volume. This feature is not a
vault.

## 5. Troubleshooting

| Symptom | Cause / action |
|---|---|
| `409` on Start Login | A login for that prefix is already pending; complete it or let it expire. |
| `400 clientId` from Start | Missing/blank/malformed client ID, or GitHub rejected the application (device flow off, wrong ID). |
| `503` after Complete with `saved_not_published` | Publication failed after the row was committed; retry Complete once, then restart — no new login needed. |
| `GET …/connections` empty after image replacement | The volume was not mounted, or the database path differs between runs. |
| Route returns `503` | No usable connection for that prefix (expired/revoked/refresh failed); start a new login. |
| Route returns `400` | A Copilot profile only forwards `/v1/messages` (Anthropic) or `/responses` (OpenAI). Chat Completions is not supported. |
| `502` from Start/Complete | GitHub was unreachable; retry the same call. |
