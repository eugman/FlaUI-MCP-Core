using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core;

public sealed record TextObservation(string? Text, string Pattern)
{
    public string Verify(string expected) =>
        Text == null ? "unavailable"
        : Text.ReplaceLineEndings("\n") == expected.ReplaceLineEndings("\n") ? "verified"
        : "mismatch";
}

public static class ControlText
{
    // Observe asynchronous UI updates after one input dispatch. Never retype.
    public static TextObservation WaitForExpected(
        string expected,
        Func<TextObservation> read,
        Action validate,
        Action wait
    )
    {
        TextObservation observed = new(null, "unavailable");
        for (var attempt = 0; attempt <= 10; attempt++)
        {
            validate();
            observed = read();
            if (observed.Verify(expected) == "verified" || observed.Text == null || attempt == 10)
            {
                break;
            }

            wait();
        }

        return observed;
    }

    // Names describe controls, not editor contents; never use Name as text evidence.
    public static TextObservation Read(AutomationElement element)
    {
        if (element.Properties.IsPassword.ValueOrDefault)
        {
            return new(null, "password");
        }

        if (element.Patterns.Text.IsSupported)
        {
            return new(element.Patterns.Text.Pattern.DocumentRange.GetText(-1), "Text");
        }

        if (element.Patterns.Value.IsSupported)
        {
            return new(element.Patterns.Value.Pattern.Value.ValueOrDefault, "Value");
        }

        return new(null, "unsupported");
    }
}
