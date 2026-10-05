# JevApi

.NET tooling for the [TypeSafe AI](https://docs.typesafe.ai/api) **Jev** System One model. Jev evaluates typed **questions** — noul (yes/no), choice (pick an option), and score (rate on a rubric) — against a **state** and returns structured answers with probability distributions and confidence values.

Targets **.NET 10**, with source-generated JSON throughout for trimming and native AOT compatibility.

## Projects

### TypeSafe.Client

A user-friendly, AOT-safe .NET client library for the Jev evaluation API (`POST https://api.typesafe.ai/v1/systemone`).

- Typed question/answer models: `NoulAnswer` (0–1 probability), `ChoiceAnswer` (choice + full probabilities + confidence), `ScoreAnswer` (weighted score + legend + confidence)
- Fluent `EvaluationRequestBuilder` and convenience methods (`AskAsync`, `ChooseAsync`, `RateAsync`, `EvaluateAsync`)
- Automatic retries with exponential backoff on 429 (rate limited) and 529 (overloaded), honoring `Retry-After`
- Structured `TypeSafeApiException` with `IsUnauthorized` / `IsValidationError` / `IsRateLimited` / `IsOverloaded` helpers
- DI registration via `services.AddTypeSafeClient(...)` (options delegate or `IConfiguration`)

See [TypeSafe.Client/README.md](TypeSafe.Client/README.md) for usage examples.

### JevMcp (`MCP`)

A stdio [MCP](https://modelcontextprotocol.io) server (built on [MCPSharp](https://github.com/afrise/MCPSharp)) that wraps `TypeSafe.Client` and exposes Jev to LLM agents as tools, with schemas and descriptions designed for agent consumption:

| Tool | Purpose |
| --- | --- |
| `evaluate(state, questions)` | Judge one item with 1–20 atomic typed questions in a single parallel call |
| `evaluate_batch(items, questions)` | Run the same questions across many items (≤ 50) for triage/pre-filtering |
| `validate_questions(questions)` | Dry-run phrasing checks (atomicity, required fields, duplicates) — free, no upstream call |
| `examples()` | Canonical call patterns (single verification, batch triage, multi-factor scoring) |

Results preserve raw probabilities and confidence (never reduced to booleans), include `request_id` / `latency_ms` / token usage, surface truncation warnings, and return structured errors (`malformed_question`, `state_too_large`, `rate_limited`, ...).

Configuration via `appsettings.json` or environment variables (`TYPESAFE__ApiKey`). See [MCP/README.md](MCP/README.md).

## Getting started

```bash
dotnet build JevApi.slnx
```

Set your TypeSafe API key in `MCP/appsettings.json` (`TypeSafe:ApiKey`) or via the `TYPESAFE__ApiKey` environment variable, then point your MCP client at the built `JevMcp` executable.

## Testing

Two harnesses verify the server end-to-end (both find `JevMcp.exe` automatically; override with arg 1 or `JEVMCP_EXE`):

- **`TestHarness`** — C# integration test built on MCPSharp's own `MCPClient`, so it exercises exactly what a real MCP client does. Keyless by default (`tools/list`, `health`, `validate_questions`, `examples`); `--live` spends one evaluation (noul + choice + score) against the Jev API.
  ```bash
  dotnet run --project TestHarness -- --live
  ```
- **`test/mcp_stdio_smoke.py`** — framework-free raw MCP-over-stdio client. It speaks newline-delimited JSON-RPC directly, so it runs anywhere Python does and works as a keyless transport-level sanity check (and as the reference client for debugging transport issues).
  ```bash
  python test/mcp_stdio_smoke.py --live
  ```

Note: the `MCP` project must not set `PublishAot=true` — that flag disables reflection-based JSON serialization at runtime (even in plain `dotnet build` output), which silently breaks MCPSharp's stdio JSON-RPC layer. `InvariantGlobalization` is fine.
