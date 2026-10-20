namespace StudyHarness.Application;

public static class ModelJson
{
    // Accept one complete Markdown wrapper, never extract or repair partial JSON.
    public static string Unwrap(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) && text.EndsWith("```", StringComparison.Ordinal))
            return text[8..^3].Trim();
        if (text.StartsWith("```\n", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
            return text[4..^3].Trim();
        return text;
    }
}
