using System.Text.Json.Serialization;

namespace JevMcp;

/// <summary>
/// One typed question to evaluate against the state.
/// Set <see cref="Type"/> to "choice", "score", or "noul" and fill the matching fields:
/// choice -> <see cref="Options"/>; score -> <see cref="Levels"/>; noul -> <see cref="Statement"/>.
/// </summary>
public sealed class McpQuestion
{
    /// <summary>
    /// Identifier YOU choose for this question. It is echoed back in the result so you can map
    /// answers to questions unambiguously. Must be unique within the call. Example: "is_relevant".
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>
    /// Question type: "choice" | "score" | "noul".
    ///  - choice: pick the best option from a list. Returns choice + full probabilities + confidence.
    ///  - score:  rate the state along an ordered rubric. Returns a 0..(N-1) score + confidence.
    ///  - noul:   is a statement true? Returns noul, a 0..1 probability that the statement is true.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>
    /// The natural-language question, phrased as ONE specific judgment. Keep it atomic:
    /// do not combine factors ("relevant AND recent"). Ask several atomic questions instead and
    /// combine the results yourself. Example: "Which category best fits this document?"
    /// </summary>
    [JsonPropertyName("question")]
    public string? Question { get; set; }

    /// <summary>
    /// (choice only) The candidate options the model may pick, 1-255 entries.
    /// Example: ["billing","technical","sales"].
    /// </summary>
    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    /// <summary>
    /// (score only) Ordered rubric level descriptions from lowest to highest, 2-10 entries.
    /// Example: ["irrelevant","somewhat relevant","highly relevant"] -> score ranges 0..2.
    /// </summary>
    [JsonPropertyName("levels")]
    public List<string>? Levels { get; set; }

    /// <summary>
    /// (noul only) The statement to check for truth against the state.
    /// Example: "The source explicitly supports the claim that X."
    /// </summary>
    [JsonPropertyName("statement")]
    public string? Statement { get; set; }
}

/// <summary>One item (a document, record, or search result) for batch evaluation.</summary>
public sealed class BatchItem
{
    /// <summary>
    /// Identifier YOU choose for this item; results are keyed by it. Example: a URL, filename, or "item-7".
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>The item's content to evaluate (truncated at the per-item character limit; see warnings).</summary>
    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

// ---- Responses ----

/// <summary>Result for one evaluated question. Only the fields relevant to its type are populated.</summary>
public sealed class QuestionResult
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>Echo of the question type: "choice" | "score" | "noul".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>(choice) The selected option.</summary>
    [JsonPropertyName("choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Choice { get; set; }

    /// <summary>(choice) Probability of every option; values sum to 1. Use it to set your own thresholds.</summary>
    [JsonPropertyName("probabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? Probabilities { get; set; }

    /// <summary>(choice, score) Model confidence 0..1, derived from the probability distribution.</summary>
    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; set; }

    /// <summary>(score) Probability-weighted score across the rubric levels; can land between levels.</summary>
    [JsonPropertyName("score")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; set; }

    /// <summary>(score) Rubric legend: level index (as string) -> its description.</summary>
    [JsonPropertyName("legend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Legend { get; set; }

    /// <summary>(noul) Probability 0..1 that the statement is true. Raw value; NOT reduced to a boolean.</summary>
    [JsonPropertyName("noul")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Noul { get; set; }
}

/// <summary>Token usage for the underlying Jev call.</summary>
public sealed class TokenUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

/// <summary>Result of an <c>evaluate</c> call.</summary>
public class EvaluateResponse
{
    /// <summary>Server-generated id for this request; quote it when reporting issues.</summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; set; }

    [JsonPropertyName("model")]
    public required string Model { get; set; }

    /// <summary>One result per question, in the same order as submitted, keyed by each question's id.</summary>
    [JsonPropertyName("results")]
    public required List<QuestionResult> Results { get; set; }

    /// <summary>True when the state was truncated to fit the size limit; judgments may be less reliable.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>Non-fatal warnings, e.g. "state truncated from 12000 to 6000 characters".</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }

    [JsonPropertyName("usage")]
    public required TokenUsage Usage { get; set; }

    /// <summary>Wall-clock latency of the underlying Jev call, in milliseconds.</summary>
    [JsonPropertyName("latency_ms")]
    public double LatencyMs { get; set; }
}

/// <summary>Result of evaluating all questions against one batch item.</summary>
public sealed class BatchItemResult
{
    [JsonPropertyName("item_id")]
    public required string ItemId { get; set; }

    /// <summary>True when this item's evaluation failed; inspect <see cref="Error"/>.</summary>
    [JsonPropertyName("is_error")]
    public bool IsError { get; set; }

    /// <summary>Structured error detail when <see cref="IsError"/> is true.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpError? Error { get; set; }

    /// <summary>One result per question, in the same order as submitted.</summary>
    [JsonPropertyName("results")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuestionResult>? Results { get; set; }

    /// <summary>True when this item's content was truncated to fit the per-item size limit.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsage? Usage { get; set; }

    [JsonPropertyName("latency_ms")]
    public double LatencyMs { get; set; }
}

/// <summary>Result of an <c>evaluate_batch</c> call.</summary>
public sealed class BatchResponse
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; set; }

    [JsonPropertyName("model")]
    public required string Model { get; set; }

    /// <summary>One entry per item, in the same order as submitted, keyed by each item's id.</summary>
    [JsonPropertyName("items")]
    public required List<BatchItemResult> Items { get; set; }

    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("total_latency_ms")]
    public double TotalLatencyMs { get; set; }
}

/// <summary>Structured error payload: tells you exactly what to fix before retrying.</summary>
public sealed class McpError
{
    /// <summary>
    /// Error category:
    ///  - malformed_question  : a question is invalid (bad type, missing options/levels/statement, duplicate id).
    ///  - malformed_argument  : a tool argument is invalid (empty state/items, batch too large).
    ///  - state_too_large     : state exceeded the hard size cap even after truncation rules.
    ///  - unauthorized        : the server's API key is missing/invalid; not fixable by the caller.
    ///  - rate_limited        : upstream rate limit; wait briefly and retry the identical call.
    ///  - overloaded          : upstream overload; wait briefly and retry the identical call.
    ///  - upstream_error      : other failure from the Jev API; inspect detail.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    /// <summary>Human-readable detail including which question/argument is wrong and why.</summary>
    [JsonPropertyName("detail")]
    public required string Detail { get; set; }

    /// <summary>True when retrying the identical call (after any fix described) may succeed.</summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }
}

/// <summary>Response shape when a tool call fails.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public required McpError Error { get; set; }
}

// ---- validate_questions ----

/// <summary>Validation outcome for one question.</summary>
public sealed class QuestionValidation
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>Problems found with this question (empty when valid).</summary>
    [JsonPropertyName("issues")]
    public required List<string> Issues { get; set; }

    /// <summary>Suggested rewritten questions (atomic decompositions) when the question looks compound.</summary>
    [JsonPropertyName("suggested_rewrites")]
    public required List<string> SuggestedRewrites { get; set; }
}

/// <summary>Result of a <c>validate_questions</c> call.</summary>
public sealed class ValidateResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("results")]
    public required List<QuestionValidation> Results { get; set; }
}

/// <summary>Result of a <c>health</c> call. Free — makes no upstream call.</summary>
public sealed class HealthResponse
{
    /// <summary>True when an API key is configured (not verified against the API).</summary>
    [JsonPropertyName("api_key_configured")]
    public bool ApiKeyConfigured { get; set; }

    /// <summary>The configured model name.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    /// <summary>The configured API base address.</summary>
    [JsonPropertyName("base_address")]
    public required string BaseAddress { get; set; }

    /// <summary>Non-fatal setup warnings, e.g. a missing API key.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }
}
