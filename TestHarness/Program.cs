using MCPSharp;
using System.Text.Json;

// Integration harness for the JevMcp stdio server (spawns it as a child process
// and speaks MCP over stdio, exactly like a real MCP client).
//
// Usage:
//   JevHarness [path-to-JevMcp.exe] [--live]
//
// Default (keyless): initialize -> tools/list -> health -> validate_questions -> examples.
// --live additionally spends one evaluation (noul + choice + score against a demo state).
// Exit code 0 = all checks passed, 1 = a check failed.

var live = args.Contains("--live");
var explicitPath = args.FirstOrDefault(a => !a.StartsWith("--"));
var envPath = Environment.GetEnvironmentVariable("JEVMCP_EXE");

string? server = explicitPath ?? envPath ?? FindDefaultServer();
if (server is null)
{
    Console.Error.WriteLine("JevMcp server not found. Pass its path as arg 1 or set JEVMCP_EXE.");
    return 1;
}
if (!File.Exists(server))
{
    Console.Error.WriteLine($"JevMcp server not found at: {server}");
    return 1;
}

Console.WriteLine($"[harness] server: {server}");
Console.WriteLine($"[harness] mode:   {(live ? "LIVE (spends evaluations)" : "keyless")}");
Console.WriteLine();

MCPClient client = new("JevHarness", "1.0", server);
var failures = 0;

// ---- tools/list ----------------------------------------------------------
Console.WriteLine("[check] tools/list");
var tools = await client.GetToolsAsync();
foreach (var t in tools)
    Console.WriteLine($"        - {t.Name}");
var expected = new[] { "evaluate", "evaluate_batch", "validate_questions", "examples", "health" };
var missing = expected.Where(e => tools.All(t => t.Name != e)).ToList();
if (missing.Count > 0)
{
    Console.WriteLine($"        FAIL: missing tools: {string.Join(", ", missing)}");
    failures++;
}
else
    Console.WriteLine($"        OK: all {expected.Length} tools present");

// ---- health --------------------------------------------------------------
Console.WriteLine("[check] health");
var healthJson = await Call(client, "health", []);
Console.WriteLine($"        {healthJson}");
using (var doc = JsonDocument.Parse(healthJson))
{
    if (doc.RootElement.GetProperty("api_key_configured").GetBoolean())
        Console.WriteLine("        OK: API key configured");
    else
        Console.WriteLine("        NOTE: no API key configured (evaluate/evaluate_batch will return 'unauthorized')");
}

// ---- validate_questions --------------------------------------------------
Console.WriteLine("[check] validate_questions (noul with 'and' must PASS; compound choice must FAIL)");
var validateJson = await Call(client, "validate_questions", new Dictionary<string, object>
{
    ["questions"] = new List<Dictionary<string, object>>
    {
        // Noul statements often quote source phrasing verbatim — conjunctions there are fine.
        new() { ["id"] = "claim_ok", ["type"] = "noul",
                ["statement"] = "The source explicitly states that the payout failed and that support was notified." },
        // Compound question — the validator should flag this one.
        new() { ["id"] = "bad", ["type"] = "choice",
                ["question"] = "Is this relevant and recent and cheap?", ["options"] = new[] { "yes", "no" } },
    }
});
Console.WriteLine($"        {validateJson}");
using (var doc = JsonDocument.Parse(validateJson))
{
    var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
    var noulValid = results[0].GetProperty("valid").GetBoolean();
    var choiceValid = results[1].GetProperty("valid").GetBoolean();
    if (noulValid && !choiceValid)
        Console.WriteLine("        OK: noul accepted, compound flagged");
    else
    {
        Console.WriteLine($"        FAIL: expected noul valid=true / choice valid=false, got {noulValid}/{choiceValid}");
        failures++;
    }
}

// ---- list-envelope tolerance (interop regression guard) -------------------
// Some MCP clients wrap list arguments in {"contents": [...]} per their advertised schema.
// The server must accept both the bare array and the envelope. Keyless: uses validate_questions.
Console.WriteLine("[check] list argument shapes: bare array AND {contents:[...]} envelope both accepted");
var envelopeJson = await Call(client, "validate_questions", new Dictionary<string, object>
{
    ["questions"] = new Dictionary<string, object>
    {
        ["contents"] = new List<Dictionary<string, object>>
        {
            new() { ["id"] = "env_ok", ["type"] = "noul",
                    ["statement"] = "The source explicitly states X." },
        }
    }
});
using (var doc = JsonDocument.Parse(envelopeJson))
{
    if (doc.RootElement.TryGetProperty("valid", out var valid) && valid.GetBoolean())
        Console.WriteLine("        OK: contents-envelope accepted");
    else
    {
        Console.WriteLine($"        FAIL: contents-envelope rejected: {envelopeJson}");
        failures++;
    }
}

// ---- examples ------------------------------------------------------------
Console.WriteLine("[check] examples");
var examplesJson = await Call(client, "examples", []);
using (var doc = JsonDocument.Parse(examplesJson))
{
    var keys = string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name));
    Console.WriteLine($"        patterns: {keys}");
    Console.WriteLine("        OK");
}

// ---- live evaluation (optional) ------------------------------------------
if (live)
{
    Console.WriteLine("[check] evaluate (LIVE — one call, three question types)");
    const string state = "Acme Corp reported Q3 revenue of $12.4 million on Tuesday, " +
                         "citing strong demand in the European market.";
    var evalJson = await Call(client, "evaluate", new Dictionary<string, object>
    {
        ["state"] = state,
        ["questions"] = new List<Dictionary<string, object>>
        {
            new() { ["id"] = "rev_check", ["type"] = "noul",
                    ["statement"] = "The source explicitly states that Acme Corp reported Q3 revenue of $12.4 million." },
            new() { ["id"] = "topic", ["type"] = "choice",
                    ["question"] = "What kind of content is this?",
                    ["options"] = new[] { "earnings report", "product announcement", "opinion piece" } },
            new() { ["id"] = "positivity", ["type"] = "score",
                    ["question"] = "How positive is the tone of this statement?",
                    ["levels"] = new[] { "negative", "neutral", "positive" } },
        }
    });
    using (var doc = JsonDocument.Parse(evalJson))
    {
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            Console.WriteLine($"        FAIL: {err.GetProperty("code")} — {err.GetProperty("detail")}");
            failures++;
        }
        else
        {
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                Console.WriteLine($"        {r.GetProperty("id")}: {r}");
            Console.WriteLine($"        latency_ms: {doc.RootElement.GetProperty("latency_ms")}");
            Console.WriteLine("        OK");
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "[harness] ALL CHECKS PASSED" : $"[harness] {failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static async Task<string> Call(MCPClient client, string name, Dictionary<string, object> args)
{
    var result = await client.CallToolAsync(name, args);
    return result.Content[0].Text;
}

static string? FindDefaultServer()
{
    // Walk up from the harness output dir to the repo root, then into the MCP project's build output.
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(dir, "MCP", "bin", cfg, "net10.0", "JevMcp.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        dir = Directory.GetParent(dir)?.FullName;
    }
    return null;
}
