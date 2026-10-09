using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using MCPSharp;
using TypeSafe.Client;

namespace JevMcp;

/// <summary>
/// MCP tools wrapping the Jev (TypeSafe System One) evaluation API.
/// Every tool returns JSON: either a typed result object or { "error": { code, detail, retryable } }.
/// Calls are idempotent and stateless — safe to retry, reorder, or run in parallel.
/// </summary>
public sealed class JevMcpTools
{
    private readonly JevEvaluator _evaluator;

    /// <summary>Used by MCPSharp's <c>Register&lt;T&gt;()</c>, which constructs the tool class itself.</summary>
    public JevMcpTools()
        : this(JevMcpRuntime.Evaluator)
    {
    }

    /// <summary>DI-friendly constructor.</summary>
    public JevMcpTools(JevEvaluator evaluator) => _evaluator = evaluator;

    [McpTool("evaluate",
        "Judge ONE piece of content (the 'state') with 1-20 atomic typed questions, evaluated in parallel in a single call. " +
        "USE WHEN you need a structured, probability-bearing judgment about one document/message/record: verifying a claim (noul), " +
        "routing or classifying (choice), or rating along a rubric (score). Mix types freely in one call at no extra latency. " +
        "USE WHEN you need to check whether a specific claim is supported by a source before writing it into a report: add a noul " +
        "question with statement='The source explicitly states that <claim>'. " +
        "DO NOT USE for more than ~5 separate items — call evaluate_batch instead. DO NOT USE for compound judgments " +
        "('is it relevant AND recent AND cheap?') — decompose into separate atomic questions and combine results yourself. " +
        "STATE LIMIT: state is truncated at 6000 characters (a warning is returned); keep the relevant part near the front. " +
        "Returns results in question order with full probability distributions and confidence — never reduced to booleans.")]
    public async Task<string> EvaluateAsync(
        [McpParameter(true, "The content to judge: plain text or JSON. Truncated at 6000 chars (warning returned when that happens).")]
        string state,
        [McpParameter(true,
            "1-20 typed questions as a JSON ARRAY, each: { id (your unique key, echoed back), type (choice|score|noul), question (one specific " +
            "judgment) } plus per-type fields: choice -> options: string[] (1-255); score -> levels: string[] (2-10, low to high); " +
            "noul -> statement: string. Example noul: { id:\"claim_ok\", type:\"noul\", statement:\"The source explicitly states X\" }. " +
            "Example score: { id:\"relevance\", type:\"score\", question:\"How relevant is this to topic T?\", levels:[\"irrelevant\",\"somewhat\",\"highly relevant\"] }. " +
            "Example choice: { id:\"category\", type:\"choice\", question:\"Which category fits best?\", options:[\"a\",\"b\",\"c\"] }.")]
        JsonElement questions)
    {
        Diag("evaluate", "questions", questions);
        try
        {
            if (string.IsNullOrWhiteSpace(state))
                throw JevEvaluator.BadArgument("malformed_argument", "'state' is required and must be non-empty.");

            var questionList = ParseList<McpQuestion>(questions, "questions");
            _evaluator.ValidateQuestionList(questionList);

            var (text, warning) = JevEvaluator.Truncate(state, _evaluator.Options.MaxStateCharacters);
            var (results, raw, latencyMs) = await _evaluator.EvaluateAsync(text, questionList, CancellationToken.None);

            var response = new EvaluateResponse
            {
                RequestId = NewRequestId(),
                Model = raw.Model,
                Results = results,
                Truncated = warning is not null,
                Warnings = warning is null ? null : [warning],
                Usage = new TokenUsage { InputTokens = raw.Usage.InputTokens, OutputTokens = raw.Usage.OutputTokens },
                LatencyMs = Math.Round(latencyMs, 1),
            };
            return JsonSerializer.Serialize(response, McpJsonContext.Default.EvaluateResponse);
        }
        catch (Exception ex)
        {
            return SerializeError(ex);
        }
    }

    [McpTool("evaluate_batch",
        "Run the SAME question set across MANY items (up to 50 per call) and return results keyed by each item's id — " +
        "the right tool for triage, classification, and pre-filtering large candidate sets before deep processing. " +
        "USE WHEN you need to rank or filter more than ~5 documents, search results, or records: e.g. score 30 candidate sources for " +
        "relevance with a score question (levels: irrelevant..highly relevant), then keep the top N by score/confidence. " +
        "USE WHEN every item gets the same judgment; items are evaluated independently and concurrently. " +
        "DO NOT USE for a single item — call evaluate. DO NOT USE when each item needs different questions. " +
        "SIZE LIMITS: each item's content is truncated at 4000 characters (per-item 'truncated' flag set); split huge sets into " +
        "multiple calls of <= 50 items. Per-item failures are returned inline (is_error + error) so one bad item never sinks the batch.")]
    public async Task<string> EvaluateBatchAsync(
        [McpParameter(true,
            "Up to 50 items as a JSON ARRAY: { id (your key; results are keyed by it), content (text to judge; truncated at 4000 chars) }. " +
            "Example: [ { id:\"src-1\", content:\"...\" }, { id:\"src-2\", content:\"...\" } ].")]
        JsonElement items,
        [McpParameter(true,
            "1-20 typed questions applied identically to every item; same schema as evaluate. For relevance triage use one score " +
            "question, e.g. { id:\"relevance\", type:\"score\", question:\"How relevant is this source to topic X?\", " +
            "levels:[\"irrelevant\",\"marginally relevant\",\"somewhat relevant\",\"highly relevant\"] }.")]
        JsonElement questions)
    {
        Diag("evaluate_batch", "items", items);
        Diag("evaluate_batch", "questions", questions);
        try
        {
            var itemList = ParseList<BatchItem>(items, "items");
            var questionList = ParseList<McpQuestion>(questions, "questions");

            if (itemList is null || itemList.Count == 0)
                throw JevEvaluator.BadArgument("malformed_argument", "'items' must contain at least one item.");
            if (itemList.Count > _evaluator.Options.MaxBatchItems)
                throw JevEvaluator.BadArgument("state_too_large",
                    $"too many items ({itemList.Count}); the limit is {_evaluator.Options.MaxBatchItems} per call. Split the set and make multiple calls.");
            if (itemList.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id)))
                throw JevEvaluator.BadArgument("malformed_argument", "every item needs a non-empty 'id' so results can be mapped back.");
            if (itemList.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != itemList.Count)
                throw JevEvaluator.BadArgument("malformed_argument", "item ids must be unique within a batch.");

            _evaluator.ValidateQuestionList(questionList);

            var overall = System.Diagnostics.Stopwatch.StartNew();
            var results = new List<BatchItemResult>(itemList.Count);
            var gate = new SemaphoreSlim(Math.Max(1, _evaluator.Options.BatchParallelism));

            var tasks = itemList.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    var (text, _) = JevEvaluator.Truncate(item.Content ?? string.Empty, _evaluator.Options.MaxItemCharacters);
                    var (questionResults, raw, latencyMs) = await _evaluator.EvaluateAsync(text, questionList, CancellationToken.None);
                    return (Model: raw.Model, Result: new BatchItemResult
                    {
                        ItemId = item.Id,
                        Results = questionResults,
                        Truncated = text.Length < (item.Content?.Length ?? 0),
                        Usage = new TokenUsage { InputTokens = raw.Usage.InputTokens, OutputTokens = raw.Usage.OutputTokens },
                        LatencyMs = Math.Round(latencyMs, 1),
                    });
                }
                catch (Exception ex)
                {
                    return (Model: (string?)null, Result: new BatchItemResult
                    {
                        ItemId = item.Id,
                        IsError = true,
                        Error = ToError(ex),
                    });
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            var completed = await Task.WhenAll(tasks);
            results.AddRange(completed.Select(c => c.Result));
            var model = completed.Select(c => c.Model).FirstOrDefault(m => m is not null) ?? _evaluatorModelFallback;
            overall.Stop();

            var response = new BatchResponse
            {
                RequestId = NewRequestId(),
                Model = model,
                Items = results,
                ItemCount = results.Count,
                Succeeded = results.Count(r => !r.IsError),
                Failed = results.Count(r => r.IsError),
                TotalLatencyMs = Math.Round(overall.Elapsed.TotalMilliseconds, 1),
            };
            return JsonSerializer.Serialize(response, McpJsonContext.Default.BatchResponse);
        }
        catch (Exception ex)
        {
            return SerializeError(ex);
        }
    }

    private const string _evaluatorModelFallback = "jev-latest";

    [McpTool("validate_questions",
        "Dry-run a question list BEFORE spending an evaluation: checks phrasing against Jev's atomicity guidance " +
        "(one judgment per question), required fields per type, and duplicate ids. USE WHEN you are about to call evaluate or " +
        "evaluate_batch with hand-written questions and want to catch compound/ambiguous phrasing early — especially for long " +
        "question lists or batch calls where a malformed question wastes the most. Returns per-question issues plus suggested " +
        "atomic rewrites you can paste straight back into evaluate. DO NOT USE for questions already known-good; it makes no " +
        "upstream calls and is free.")]
    public Task<string> ValidateQuestionsAsync(
        [McpParameter(true, "The same question ARRAY you would pass to evaluate or evaluate_batch.")]
        JsonElement questions)
    {
        Diag("validate_questions", "questions", questions);
        try
        {
            var questionList = ParseList<McpQuestion>(questions, "questions");
            var results = questionList.Select(q => QuestionValidator.Validate(q)).ToList();
            var response = new ValidateResponse
            {
                Valid = results.All(r => r.Valid),
                Results = results,
            };
            return Task.FromResult(JsonSerializer.Serialize(response, McpJsonContext.Default.ValidateResponse));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SerializeError(ex));
        }
    }

    [McpTool("examples",
        "Return 2-3 canonical, copy-paste-ready call patterns for this server (single verification, batch triage, decomposed " +
        "multi-factor scoring). USE WHEN you are unsure how to structure an evaluate or evaluate_batch call, or you want the exact " +
        "question JSON shape. Free — makes no upstream calls. Read this once, then call evaluate/evaluate_batch directly.")]
    public Task<string> ExamplesAsync()
    {
        // Built as JsonNode (no reflection) to stay AOT/trimming safe.
        var examples = new JsonObject
        {
            ["single_verification"] = new JsonObject
            {
                ["when"] = "Verify one specific claim against one source before citing it.",
                ["tool"] = "evaluate",
                ["arguments"] = new JsonObject
                {
                    ["state"] = "The payout failed because the bank rejected the account number...",
                    ["questions"] = new JsonArray(
                        new JsonObject { ["id"] = "claim_ok", ["type"] = "noul", ["statement"] = "The source explicitly states that the payout failed due to a rejected account number." }),
                },
                ["interpret"] = "results[0].noul is 0..1; cite only when above your threshold (e.g. >= 0.8).",
            },
            ["batch_triage"] = new JsonObject
            {
                ["when"] = "Pre-filter ~30 candidate sources down to the top few by relevance.",
                ["tool"] = "evaluate_batch",
                ["arguments"] = new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject { ["id"] = "src-1", ["content"] = "first candidate source text..." },
                        new JsonObject { ["id"] = "src-2", ["content"] = "second candidate source text..." }),
                    ["questions"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "relevance",
                            ["type"] = "score",
                            ["question"] = "How relevant is this source to topic X?",
                            ["levels"] = new JsonArray("irrelevant", "marginally relevant", "somewhat relevant", "highly relevant"),
                        }),
                },
                ["interpret"] = "sort items by results[0].score (and confidence) descending; keep the top N.",
            },
            ["decomposed_multifactor"] = new JsonObject
            {
                ["when"] = "Judge a document on several independent factors at once (not a compound question).",
                ["tool"] = "evaluate",
                ["arguments"] = new JsonObject
                {
                    ["state"] = "...document text...",
                    ["questions"] = new JsonArray(
                        new JsonObject { ["id"] = "relevant", ["type"] = "noul", ["statement"] = "This document is about topic X." },
                        new JsonObject { ["id"] = "recency", ["type"] = "score", ["question"] = "How recent is this document?", ["levels"] = new JsonArray("outdated", "older", "current") },
                        new JsonObject { ["id"] = "kind", ["type"] = "choice", ["question"] = "What kind of document is this?", ["options"] = new JsonArray("news", "research", "opinion") }),
                },
                ["interpret"] = "three independent results in one call; combine them with your own logic (e.g. relevant.noul>=0.8 AND recency.score>=1).",
            },
        };
        return Task.FromResult(examples.ToJsonString());
    }

    [McpTool("health",
        "Check server configuration WITHOUT spending an evaluation or calling the Jev API. USE WHEN setting up or " +
        "troubleshooting: confirms whether an API key is configured, which model and base address are in effect, and the " +
        "active size limits. USE THIS FIRST after configuring credentials so a bad/missing key surfaces here instead of as an " +
        "'unauthorized' error on your first real evaluate call. Free — no upstream call; it does not verify the key against the API.")]
    public Task<string> HealthAsync()
    {
        var warnings = _evaluator.HasApiKey
            ? null
            : new List<string> { "no API key configured — set TypeSafe:ApiKey in appsettings.json or the TYPESAFE__ApiKey environment variable; evaluate/evaluate_batch will return 'unauthorized'." };

        var response = new HealthResponse
        {
            ApiKeyConfigured = _evaluator.HasApiKey,
            Model = _evaluator.Model,
            BaseAddress = _evaluator.BaseAddress,
            Warnings = warnings,
        };
        return Task.FromResult(JsonSerializer.Serialize(response, McpJsonContext.Default.HealthResponse));
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Temporary interop diagnostic: appends the RAW tool arguments (as received after client
    /// transport and MCPSharp binding) to diag_args.log next to the executable. Lets us see
    /// exactly what shape a given MCP client puts on the wire. Must never throw.
    /// </summary>
    private static void Diag(string toolName, string paramName, JsonElement element)
    {
        try
        {
            var raw = element.ValueKind == JsonValueKind.Undefined ? "(undefined)" : element.GetRawText();
            if (raw.Length > 2000)
                raw = raw[..2000] + "...[truncated]";
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "diag_args.log"),
                $"{DateTime.UtcNow:O} [{toolName}] {paramName} (kind={element.ValueKind}): {raw}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics must never break the tool
        }
    }

    /// <summary>
    /// Tolerant list parser for tool arguments. MCP clients disagree on how to pass a list:
    /// the wire format is a bare JSON array, but some clients/generators wrap it in an object
    /// envelope (e.g. { "contents": [...] }) per their advertised schema. Accept both, plus
    /// System.Text.Json's $values ref-preservation shape, an envelope keyed by the parameter
    /// name itself ({ "questions": [...] } — some clients re-wrap the value under the parameter
    /// name, or a framework hands the tool the whole arguments object), and a single object as
    /// a 1-element list.
    /// Throws <see cref="McpToolException"/> (malformed_argument) when nothing usable arrives.
    /// </summary>
    private static List<T> ParseList<T>(JsonElement element, string paramName)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw JevEvaluator.BadArgument("malformed_argument", $"'{paramName}' is required and must be an array.");

        var target = element;
        if (target.ValueKind == JsonValueKind.Object &&
            (target.TryGetProperty("contents", out var inner) || target.TryGetProperty("$values", out inner) ||
             target.TryGetProperty(paramName, out inner)))
        {
            target = inner;
        }

        List<T>? list;
        try
        {
            list = target.ValueKind switch
            {
                JsonValueKind.Array => JsonSerializer.Deserialize<List<T>>(target.GetRawText(),
                    (JsonTypeInfo<List<T>>)McpJsonContext.Default.GetTypeInfo(typeof(List<T>))!),
                JsonValueKind.Object => new List<T> { JsonSerializer.Deserialize<T>(target.GetRawText(),
                    (JsonTypeInfo<T>)McpJsonContext.Default.GetTypeInfo(typeof(T))!)! },
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            throw JevEvaluator.BadArgument("malformed_argument", $"'{paramName}' contains an element that is not a valid {typeof(T).Name}: {ex.Message}");
        }

        if (list is null)
            throw JevEvaluator.BadArgument("malformed_argument",
                $"'{paramName}' must be a JSON array of objects (an object with a 'contents' array is also accepted); got JSON {target.ValueKind}.");

        return list;
    }

    private static string SerializeError(Exception ex) =>
        JsonSerializer.Serialize(new ErrorResponse { Error = ToError(ex) }, McpJsonContext.Default.ErrorResponse);

    private static McpError ToError(Exception ex) => ex switch
    {
        McpToolException mcp => mcp.Error,
        TypeSafeApiException api => new McpError
        {
            Code = api.IsUnauthorized ? "unauthorized"
                : api.IsRateLimited ? "rate_limited"
                : api.IsOverloaded ? "overloaded"
                : api.IsValidationError ? "malformed_question"
                : "upstream_error",
            Detail = api.IsUnauthorized
                ? "The server's TypeSafe API key is missing or invalid (401). This is a server configuration problem, not a problem with your call."
                : api.IsRateLimited
                    ? "The Jev API rate limit was hit (429) even after retries. Wait a few seconds and retry the identical call."
                    : api.IsOverloaded
                        ? "The Jev API is temporarily overloaded (529) even after retries. Wait a few seconds and retry the identical call."
                        : $"Jev API error {(int)api.StatusCode}: {api.ResponseBody}",
            Retryable = api.IsRateLimited || api.IsOverloaded,
        },
        HttpRequestException http => new McpError
        {
            Code = "upstream_error",
            Detail = $"Network failure reaching the Jev API: {http.Message}. Retry the identical call.",
            Retryable = true,
        },
        _ => new McpError
        {
            Code = "upstream_error",
            Detail = $"Unexpected server error: {ex.Message}",
            Retryable = false,
        },
    };
}

/// <summary>Heuristic question-phrasing validator: atomicity, required fields, duplicates.</summary>
internal static class QuestionValidator
{
    private static readonly (string Marker, string Hint)[] CompoundMarkers =
    [
        (" and ", "looks compound — split into one question per factor and combine results yourself"),
        (" or ", "asks a disjunction — consider separate questions per branch"),
        (" as well as ", "looks compound — split into one question per factor"),
        (", and ", "lists multiple factors — split into one question per factor"),
        (" both ", "asks about two things at once — split it"),
    ];

    public static QuestionValidation Validate(McpQuestion question)
    {
        var issues = new List<string>();
        var rewrites = new List<string>();
        var id = question?.Id ?? "(missing)";

        if (question is null)
            return new QuestionValidation { Id = id, Valid = false, Issues = ["question is null"], SuggestedRewrites = [] };
        if (string.IsNullOrWhiteSpace(question.Id))
            issues.Add("missing 'id' — supply a unique key so you can map the result back.");
        if (string.IsNullOrWhiteSpace(question.Question) && question.Type?.Trim().ToLowerInvariant() is not "noul")
            issues.Add("missing 'question' — a natural-language prompt is required for choice and score questions.");

        // Only check the 'question' field for compound phrasing. Noul statements often quote
        // source phrasing verbatim ("X failed and support was notified") which is one atomic
        // claim; conjunctions there are not a signal of a compound judgment.
        if (!string.IsNullOrWhiteSpace(question.Question))
        {
            var text = question.Question.ToLowerInvariant();
            var hit = CompoundMarkers.FirstOrDefault(m => text.Contains(m.Marker, StringComparison.Ordinal));
            if (hit.Marker is not null)
            {
                issues.Add($"phrasing contains '{hit.Marker.Trim()}' — {hit.Hint}.");
                rewrites.AddRange(SuggestSplit(question.Question));
            }
        }

        switch (question.Type?.Trim().ToLowerInvariant())
        {
            case "choice":
                if (question.Options is null || question.Options.Count is 0 or > 255)
                    issues.Add("choice questions need 'options': a list of 1-255 candidate strings.");
                break;
            case "score":
                if (question.Levels is null || question.Levels.Count is < 2 or > 10)
                    issues.Add("score questions need 'levels': 2-10 ordered rubric descriptions, lowest to highest.");
                break;
            case "noul":
                if (string.IsNullOrWhiteSpace(question.Statement))
                    issues.Add("noul questions need 'statement': the claim to check for truth.");
                break;
            default:
                issues.Add($"unknown type '{question.Type}' — use \"choice\", \"score\", or \"noul\".");
                break;
        }

        return new QuestionValidation
        {
            Id = question.Id ?? id,
            Valid = issues.Count == 0,
            Issues = issues,
            SuggestedRewrites = rewrites,
        };
    }

    private static IEnumerable<string> SuggestSplit(string text)
    {
        foreach (var separator in new[] { " and ", " or ", " as well as " })
        {
            var parts = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
                return parts.Select((p, i) => $"Atomic question {i + 1}: \"{char.ToUpperInvariant(p[0])}{p[1..]}\"");
        }
        return [$"Split \"{text}\" into one question per factor, then combine the results with your own logic."];
    }
}
