# Headless native test client

The headless client is a console .NET program that speaks the same native protocol as the real AION2 client when connected to **our** server (`Aion.Host` native login and game listeners). It does not talk to NCSoft, Steam, NCGuard, or any official endpoint.

## Layout

| Path | Role |
|------|------|
| `tools/Aion.HeadlessClient` | CLI: `dotnet run --project tools/Aion.HeadlessClient -- run ...` |
| `tools/Aion.HeadlessClient.Protocol` | TCP handshake, RC4, login/world flows, metrics |
| `server/Aion.Protocol/Native/ClientPackets.cs` | Shared client-direction packet builders |
| `tests/Aion.HeadlessClient.Tests` | Unit tests and host integration tests |

Protocol building blocks (varint framing, LZ4 `0xffff` envelopes, RC4, RSA greeting) live in `server/Aion.Protocol` and are reused on both sides.

## Server configuration

Production defaults keep headless authentication **off**:

```json
"Aion": {
  "HeadlessClients": {
    "Enabled": false,
    "SharedSecret": "",
    "AllowedNetworks": [ "127.0.0.1", "::1" ]
  }
}
```

When `Enabled` is `true`, connections from `AllowedNetworks` may present an authn token `headless:<SharedSecret>:<index>` (index `0..9999`). Login and game ports accept these as ephemeral test accounts (no platform ticket, characters not stored). Account logins and real tickets are unchanged when this block stays disabled.

Development login (`Aion:Native:Logins=development`) remains the usual choice for local automation; the headless CLI defaults to `mode: development` scenarios.

## Running scenarios

```bash
# Start the host (development logins), then:
dotnet run --project server/Aion.Host/Aion.Host.csproj
dotnet run --project tools/Aion.HeadlessClient -- run \
  --server 127.0.0.1:13328 \
  --accounts 4 \
  --scenario tools/Aion.HeadlessClient/scenarios/login-and-idle.json \
  --report /tmp/headless-report.json
```

Scenario files may be JSON or YAML. Steps include `login-and-idle`, `walk-route`, `kill-mob`, `talk-npc`, `chat`, and `quest-accept`. The command prints and optionally writes an aggregate JSON report with login/world-entry/ping latency, disconnects, unparsed opcodes, and errors.

Example walk scenario: `tools/Aion.HeadlessClient/scenarios/walk-route.yaml`.

## What is verified (v1)

- RSA handshake and RC4 session on login and game ports
- Development path: game login, name check, create, select, `0x3622` world entry
- Keepalive / `0x3602` ping and RTT measurement
- Position reports (`0x3700` / `0x3701`) along a route
- Basic combat cast (`0x3800`), NPC interact (`0x8c02`), chat (`0xe28f`), quest accept (`0x8d0a`) — replies are recorded; full quest flow is not asserted
- Unparsed server opcodes are counted in the report
- Integration tests start `Aion.Host` with development logins and run `login-and-idle` plus `walk-route`

## What is not verified (v1)

- Full account + ticket login against Postgres (headless token path is covered in options tests only)
- NCGuard, Steam, or platform bootstrap
- Exact parity of every server opcode body (only a small set is parsed; the rest are named when listed in `tools/data/opcode-names.json`)
- Multi-world routing, party, dungeons, or chat server (Lime) integration
- Load-test scale beyond modest concurrent scenario runs

## Related docs

- `docs/CAPTURE.md` — how official captures relate to our opcode layouts
- `tools/load_test.py` — older Python load bots (same development game port)
- `tests/test_dotnet_native_host.py` — Python handshake regression against the host