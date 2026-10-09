using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevMcp;

/// <summary>Source-generated serialization for MCP tool payloads (AOT/trimming safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EvaluateResponse))]
[JsonSerializable(typeof(BatchResponse))]
[JsonSerializable(typeof(ValidateResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(QuestionResult))]
[JsonSerializable(typeof(BatchItemResult))]
[JsonSerializable(typeof(QuestionValidation))]
[JsonSerializable(typeof(TokenUsage))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(McpQuestion))]
[JsonSerializable(typeof(List<McpQuestion>))]
[JsonSerializable(typeof(BatchItem))]
[JsonSerializable(typeof(List<BatchItem>))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
