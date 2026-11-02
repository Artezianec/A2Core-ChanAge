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

Typical in-session sizes (playtest): ~664 B, ~408 B, ~536 B per burst before disconnect when TLS to bladnoch fails.

## Official game-server behaviour (capture 2026-10-24/25)

Live official server traces show **no `0xe25a` reply** for each `0xe259` (4–7 requests per long session). Replaying a captured `0xe25a` on the private server made the client **flood** hundreds of `0xe259` and drop 10–50 s later. **Silent accept** (empty reply list) matches official behaviour.

## bladnoch.ncsoft.com (HTTPS, NCGuard port)

Mid-session the client **POST**s a binary body (commonly **2082 B**) to `/` on `bladnoch.ncsoft.com` (CloudFront).

Observed official response (POST `/`, `application/octet-stream` body, 2026-11-02):

- **Status:** `200`
- **Content-Type:** `text/html`
- **Body:** empty (`Content-Length: 0`)

A private sink must not answer `404 text/plain`; that pattern correlates with NCGuard closing the session (`0xE0000015`).
