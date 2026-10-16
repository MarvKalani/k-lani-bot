using System.Globalization;
using System.Text.Json.Nodes;

namespace KLaniBot.Advisory;

// Use case: callers put atomic, typed questions to an advisor and need answers they can read without trusting
// the provider: a probability distribution over the allowed labels, a score inside the allowed range, or
// "no answer". This is the contract between callers and any provider, which is why it lives apart from
// the provider that happens to be installed. An answer that does not fit the questions (an invented
// label, a distribution that does not add up, a missing question) is rejected as a whole.
public static class AdvisoryQuestionSchema
{
    // Throws ArgumentException when the rubric has no or too many questions, or a question is not bounded.
    public static void ValidateQuestions(JsonObject questions)
    {
        if (questions.Count is < 1 or > 32)
        {
            throw new ArgumentException("Provide 1 to 32 atomic evaluation questions.");
        }
        foreach (var (_, value) in questions)
        {
            if (value is not JsonObject question
                || question.Any(property => property.Key is not ("type" or "instructions" or "criteria" or "labels")))
            {
                throw new ArgumentException("Invalid classifier question.");
            }
            if (question["instructions"] is not JsonValue instruction
                || !instruction.TryGetValue<string>(out var text)
                || text.Length is < 1 or > 8192)
            {
                throw new ArgumentException("Bounded question instructions required.");
            }
            if (!HasBoundedAnswerSpace(question))
            {
                throw new ArgumentException("Expected a bounded choice, score, or noul question.");
            }
        }
    }

    // Throws InvalidDataException when the classifier's answers do not fit the rubric.
    public static void ValidateAnswers(JsonObject questions, JsonObject result)
    {
        var answers = result["answers"] as JsonObject ?? throw new InvalidDataException("Classifier answers missing.");
        if (answers.Count != questions.Count || answers.Any(property => !questions.ContainsKey(property.Key)))
        {
            throw new InvalidDataException("Classifier answer set does not match the rubric.");
        }
        foreach (var (key, question) in questions)
        {
            ValidateAnswer(answers[key]!.AsObject(), question!.AsObject());
        }
    }

    private static bool HasBoundedAnswerSpace(JsonObject question)
    {
        string type = question["type"]?.ToString() ?? "";
        return type switch
        {
            "choice" => question["criteria"] is JsonObject options && options.Count is >= 2 and <= 255,
            "score" => question["criteria"] is JsonArray levels && levels.Count is >= 2 and <= 10,
            "noul" => true,
            _ => false
        };
    }

    // Throws InvalidDataException when the answer has the wrong type, an invalid distribution or an invented label.
    private static void ValidateAnswer(JsonObject answer, JsonObject question)
    {
        string type = question["type"]!.ToString();
        if (answer["type"]?.ToString() != type)
        {
            throw new InvalidDataException("Classifier answer type mismatch.");
        }
        Number(answer["confidence"]);
        if (type == "noul")
        {
            Number(answer["noul"]);
            return;
        }
        var probabilities = answer["probabilities"] as JsonObject ?? throw new InvalidDataException("Probability distribution missing.");
        string[] labels = LabelsOf(question, type);
        bool completeDistribution = probabilities.Count == labels.Length && labels.All(probabilities.ContainsKey);
        if (!completeDistribution
            || Math.Abs(probabilities.Sum(property => Number(property.Value)) - 1) > labels.Length * 0.0001 + 0.000001)
        {
            throw new InvalidDataException("Invalid classifier distribution.");
        }
        if (type == "choice" && !labels.Contains(answer["choice"]?.ToString()))
        {
            throw new InvalidDataException("Classifier invented a label.");
        }
        if (type == "score")
        {
            Number(answer["score"], 0, labels.Length - 1);
        }
    }

    private static string[] LabelsOf(JsonObject question, string type)
    {
        if (type == "choice")
        {
            return question["criteria"]!.AsObject().Select(property => property.Key).ToArray();
        }
        return Enumerable.Range(0, question["criteria"]!.AsArray().Count).Select(index => index.ToString()).ToArray();
    }

    // Throws InvalidDataException for a missing number or one outside its range.
    private static double Number(JsonNode? node, double low = 0, double high = 1)
    {
        bool parsed = double.TryParse(node?.ToJsonString(), CultureInfo.InvariantCulture, out double number);
        if (node == null || !parsed || !double.IsFinite(number) || number < low || number > high)
        {
            throw new InvalidDataException("Invalid classifier probability or score.");
        }
        return number;
    }
}
