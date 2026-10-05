using System.Text.Json.Serialization;

namespace TypeSafe.Client;

/// <summary>The answer for a single question. The concrete type matches the question type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract record Answer
{
    /// <summary>The question type this answer is for ("noul", "choice", or "score").</summary>
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>Answer to a <see cref="NoulQuestion"/>: the yes/no answer on a scale from 0 (no) to 1 (yes).</summary>
public sealed record NoulAnswer : Answer
{
    [JsonPropertyName("type")]
    [JsonIgnore]
    public override string Type => "noul";

    /// <summary>The yes/no answer on a scale from 0 (no) to 1 (yes).</summary>
    [JsonPropertyName("noul")]
    public required double Noul { get; init; }

    /// <summary>True when <see cref="Noul"/> is greater than or equal to <paramref name="threshold"/>.</summary>
    public bool IsYes(double threshold = 0.5) => Noul >= threshold;
}

/// <summary>Answer to a <see cref="ChoiceQuestion"/>: the chosen option and the full probability distribution.</summary>
public sealed record ChoiceAnswer : Answer
{
    [JsonPropertyName("type")]
    [JsonIgnore]
    public override string Type => "choice";

    /// <summary>The highest-probability option.</summary>
    [JsonPropertyName("choice")]
    public required string Choice { get; init; }

    /// <summary>Every option mapped to its probability (floats that sum to 1).</summary>
    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    /// <summary>How certain the model is, between 0 and 1, derived from probabilities.</summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

/// <summary>Answer to a <see cref="ScoreQuestion"/>: a probability-weighted value across the rubric levels.</summary>
public sealed record ScoreAnswer : Answer
{
    [JsonPropertyName("type")]
    [JsonIgnore]
    public override string Type => "score";

    /// <summary>The probability-weighted answer across the levels; can land between levels.</summary>
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    /// <summary>Each level number mapped back to its description.</summary>
    [JsonPropertyName("legend")]
    public required IReadOnlyDictionary<string, string> Legend { get; init; }

    /// <summary>Each level (string key matching <see cref="Legend"/>) mapped to its probability (floats that sum to 1).</summary>
    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    /// <summary>How certain the model is, between 0 and 1, derived from probabilities.</summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    /// <summary>The nearest whole level, clamped to the rubric range.</summary>
    public int NearestLevel
    {
        get
        {
            var max = Legend.Keys.Select(k => int.TryParse(k, out var i) ? i : 0).DefaultIfEmpty(0).Max();
            return Math.Clamp((int)Math.Round(Score), 0, max);
        }
    }

    /// <summary>The description of <see cref="NearestLevel"/>, or null when unavailable.</summary>
    public string? NearestLevelDescription =>
        Legend.TryGetValue(NearestLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), out var description)
            ? description
            : null;
}

/// <summary>Token usage for a request.</summary>
public sealed record Usage
{
    /// <summary>Number of input tokens consumed.</summary>
    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }

    /// <summary>Number of output tokens produced.</summary>
    [JsonPropertyName("output_tokens")]
    public required int OutputTokens { get; init; }
}

/// <summary>Result of an evaluation: one answer per question, keyed by the ids used in the request.</summary>
public sealed record EvaluationResult
{
    /// <summary>The model that performed the evaluation.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>One answer per question, keyed by the same ids used in the request.</summary>
    [JsonPropertyName("answers")]
    public required IReadOnlyDictionary<string, Answer> Answers { get; init; }

    /// <summary>Token usage for the request.</summary>
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }

    /// <summary>Gets the answer for the given question id, or null when absent.</summary>
    public Answer? GetAnswer(string questionId) =>
        Answers.TryGetValue(questionId, out var answer) ? answer : null;

    /// <summary>Gets the answer for the given question id cast to <typeparamref name="TAnswer"/>, or null when absent or of a different type.</summary>
    public TAnswer? GetAnswer<TAnswer>(string questionId) where TAnswer : Answer =>
        Answers.TryGetValue(questionId, out var answer) ? answer as TAnswer : null;
}
