using System.Runtime.InteropServices;
using System.Text;

namespace FlaUI.Mcp.Core;

public static partial class Win32Desktop
{
    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    // Keep this lease on one thread; its native owner window belongs to that thread.
    public sealed class TextClipboardLease : IDisposable
    {
        private readonly nint owner;
        private readonly string? previous;
        private readonly uint writtenSequence;
        private bool disposed;
        public string RestoreStatus { get; private set; } = "pending";

        internal TextClipboardLease(nint owner, string? previous, uint sequence) =>
            (this.owner, this.previous, writtenSequence) = (owner, previous, sequence);

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                RestoreStatus = ClipboardRestore.Run(
                    writtenSequence,
                    () => OpenClipboard(owner),
                    GetClipboardSequenceNumber,
                    () =>
                    {
                        if (!EmptyClipboard())
                        {
                            throw new InvalidOperationException("Cannot restore clipboard");
                        }

                        if (previous != null)
                        {
                            SetClipboardBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(previous + "\0"));
                        }
                    },
                    () => CloseClipboard()
                );
            }
            catch (InvalidOperationException)
            {
                RestoreStatus = "restore_failed";
            }
            finally
            {
                DestroyWindow(owner);
            }
        }
    }

    public static TextClipboardLease PreserveAndSetClipboardText(string text)
    {
        var owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        if (owner == 0)
        {
            throw new InvalidOperationException("Cannot create clipboard owner; no input sent.");
        }

        var opened = false;
        try
        {
            if (!(opened = OpenClipboard(owner)))
            {
                throw new InvalidOperationException("Clipboard is busy; no input sent.");
            }

            var exclusion = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            var hasText = false;
            var hasAny = false;
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                hasAny = true;
                if (format == CF_UNICODETEXT)
                {
                    hasText = true;
                }
                else if (format is not (1 or 7 or 16) && format != exclusion)
                {
                    throw new InvalidOperationException(
                        "Clipboard contains unsupported non-text formats; no replacement sent."
                    );
                }
            }

            if (hasAny && !hasText)
            {
                throw new InvalidOperationException("Clipboard has no preservable Unicode text; no replacement sent.");
            }

            string? previous = null;
            if (hasText)
            {
                var memory = GetClipboardData(CF_UNICODETEXT);
                var pointer = GlobalLock(memory);
                if (pointer == 0)
                {
                    throw new InvalidOperationException("Cannot preserve clipboard text; no input sent.");
                }

                try
                {
                    previous = Marshal.PtrToStringUni(pointer) ?? "";
                }
                finally
                {
                    GlobalUnlock(memory);
                }
            }

            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("Cannot write clipboard; no input sent.");
            }

            try
            {
                SetClipboardBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text.ReplaceLineEndings("\r\n") + "\0"));
                SetClipboardBytes(exclusion, new byte[4]);
            }
            catch
            {
                EmptyClipboard();
                if (previous != null)
                {
                    SetClipboardBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(previous + "\0"));
                }

                throw;
            }

            var writtenSequence = GetClipboardSequenceNumber();
            CloseClipboard();
            opened = false;
            return new(owner, previous, writtenSequence);
        }
        catch
        {
            if (opened)
            {
                CloseClipboard();
            }

            DestroyWindow(owner);
            throw;
        }
    }
}
