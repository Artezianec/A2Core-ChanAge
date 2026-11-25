# NCGuard game packets (0xe259 / 0xe25a) and bladnoch TLS

## Wire opcodes (game channel)

| Opcode | Dev struct (usmap ordinal) | Role |
|--------|---------------------------|------|
| `0xe259` | `DevPacketData_GameServer_NCGuardData_RQ` (`0x3B5E`) | Client → game server: opaque NCGuard / telemetry blob |
| `0xe25a` | `DevPacketData_GameServer_NCGuardData_RS` (`0x3B5F`) | Game server → client: `_result` (u16) + `_data` list (schema) |

Names come from schema adjacency and the observed envelope; ordinals are **not** wire opcodes.

## 0xe259 body (metadata only)

After the 2-byte opcode:

1. Varint `N` = length of the trailing blob.
2. Blob must satisfy `N == body.Length - offset_after_varint`.

When the blob matches the **observed five-field layout** (fixed 24-byte header prefix, inner length fields, 32-byte trailing tag vector), diagnostics may report `payload_bytes`, `trailing_vector_bytes`, and block rounding — never nonces, tags, or ciphertext (`tools/protocol_knowledge.py` mirrors this).

Typical in-session sizes (playtest): ~664 B, ~408 B, ~536 B per burst before disconnect when NCGuard **HTTPS**
fails (bladnoch `POST /` 404, lagavulin `GET /def.ec` 404, or GPA `/api/device/*` 404). The game server stays
**silent** on `0xe259` (official behaviour); do not answer `0xe25a`.

## Official game-server behaviour (capture 2026-10-24/25)

Live official server traces show **no `0xe25a` reply** for each `0xe259` (4–7 requests per long session). Replaying a captured `0xe25a` on the private server made the client **flood** hundreds of `0xe259` and drop 10–50 s later. **Silent accept** (empty reply list) matches official behaviour.

## lagavulin / atp-json (fixture)

Live clients usually fetch **`GET /win/<guid>/def.ec`** (multi-kB) from **lagavulin**; local fixtures supply those
paths. Sink builtins only answer exact **`GET /def.ec`** when no fixture rule exists (fixtures always win).

## nccr-api.global.nc.com (NCCR /cr/, NCGuard port)

Boot traffic hits **`GET/POST /cr/…/properties`** and **`/cr/…/run-info`**. On the elevated NCGuard-only host
(`start_all` port 443) there is often **no** platform bootstrap port; requests stay on **NcGuardSink**. With
`Aion:Platform:NccrMode` **NotFound** (old Development default), **/cr/** returned **404** while SNI still logged —
correlates with **0xe259** bursts. Local dev should use **Json** (or **Empty200**). Combined hosts serve **/cr/** via
`UsePlatformBootstrap` when `Platform.Port` is set; the sink duplicate uses the same `NccrReply` when bootstrap is not wired.

## SNI=(none) idle TLS (~150 s watchdog)

Playtest 2026-11-24: at login the client opens several **no-SNI** TLS sockets (localhost-fallback cert). Most close in
**2–11 ms** (probes); **one** stays up ~**151 s** with **no completed HTTP exchange**, then the **`0xe259` quad burst**
and exit (`ElapsedMs` ~148169–151264). Longer no-SNI idle (~185 s) did **not** crash alone — the watchdog targets the
long-lived login socket, not every idle TLS close.

**#642 logging:** `NCGuard TLS negotiated … ALPN=…`; `NCGuard first application bytes … hex=…` when anything arrives
after TLS (raw heartbeat vs HTTP/2 preface). **CANDIDATE mitigations:** echo small non-HTTP payloads on no-SNI sockets;
HTTP/1.1 200 poke after 60 s if still zero application bytes (refine from captures).

## bladnoch.ncsoft.com (HTTPS, NCGuard port)

Mid-session the client **POST**s a binary body (commonly **2082 B**) to `/` on `bladnoch.ncsoft.com` (CloudFront).

Observed official response (POST `/`, `application/octet-stream` body, 2026-11-02):

- **Status:** `200`
- **Content-Type:** `text/html`
- **Body:** empty (`Content-Length: 0`)

A private sink must not answer `404 text/plain`; that pattern correlates with NCGuard closing the session (`0xE0000015`).

## sdk.gpa.ncsoft.com (GPA SDK, NCGuard port)

After `POST /authz/token` the client refreshes the token about **every 60 s** and sends **`POST /api/device/data`**
with JSON telemetry (often once early in the session; a later retry may open TLS without completing HTTP — see logging).
The GPA SDK treats **`access_token` as a JWT**: `exp`/`iat` must line up with `expires_in`. An opaque string still
returns HTTP 200 but correlates with a timed self-disconnect (~7–8 min).
When the sink answered **404** on that path (playtest 2026-11-23), the session ended about **seven minutes**
after login: a new TLS handshake to `sdk.gpa.ncsoft.com`, the chat **/stomp** WebSocket aborted, then the game TCP
connection closed (`0xe259` NCGuardData_RQ bursts precede the drop; those stay **silent** — see above).

Built-in sink replies (**CANDIDATE** until an official capture is in-repo):

- `/authz/token` and `/authn/token`: OAuth JSON with HS256 JWT `access_token` / `refresh_token` (`GpaOAuthTokens`).
- `/api/device/*`: `200` `application/json` `{"errorCode":0,"message":"success"}`.
- **lagavulin** `GET /def.ec` only (fixture paths `/win/…` are not built-ins).
- **atp-json** `GET /legacy/*`: `200` `text/plain` `legacy`.
- **nccr-api** `GET|POST|HEAD /cr/*`: per `NccrMode` (`Json` recommended locally).

**Logging:** every `sdk.gpa.ncsoft.com` HTTP exchange logs grant type (from `grant_type=` only), protocol, sizes, and
status; TLS closes without a completed HTTP exchange log as **TLS closed without a completed HTTP exchange** (the
22:52:09 handshake-with-no-HTTP pattern). A socket that only handshakes and sits idle for **~150 s** until
disconnect is usually a pooled GPA TLS connection torn down when the client exits — not a missing HTTP reply on
that socket (authz refresh uses separate connections). Correlate with native **unanswered opcodes** (e.g. in-world
**0x569F** `CharCustomizingStatusCheck_RQ`).

**Fixtures:** do not pin a static `/authz/token` body in `fixture.json` — that bypasses the JWT builtin and restores
the 159-byte opaque token.

## STOMP vs game disconnect

When the client self-disconnects after GPA failure, **/stomp** is torn down in the same moment (`the application aborted
the connection` on the server). That is the client exiting, not an independent chat idle timeout (negotiated heart-beats
keep the socket open when GPA is healthy).
