namespace FlaUI.Mcp.Core;

internal static class ClipboardRestore
{
    internal static string Run(uint writtenSequence, Func<bool> open, Func<uint> sequence, Action restore, Action close)
    {
        if (!open())
        {
            return "clipboard_busy";
        }

        try
        {
            if (sequence() != writtenSequence)
            {
                return "external_change_preserved";
            }

            restore();
            return "restored";
        }
        finally
        {
            close();
        }
    }
}
