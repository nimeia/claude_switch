using System.Runtime.InteropServices;

namespace ClaudeSwitch.App;

/// <summary>How an attempt to type into a terminal ended.</summary>
internal enum NudgeOutcome
{
    /// <summary>The keystrokes reached the console's input buffer.</summary>
    Delivered,
    /// <summary>No process to talk to, or it is gone.</summary>
    NoProcess,
    /// <summary>The text would not survive the console's code page.</summary>
    NotAscii,
    /// <summary>Could not attach to the target's console.</summary>
    AttachFailed,
    /// <summary>Attached, but the input buffer could not be opened or written.</summary>
    WriteFailed,
}

internal readonly record struct NudgeResult(NudgeOutcome Outcome, string Detail)
{
    public bool Delivered => Outcome == NudgeOutcome.Delivered;
}

/// <summary>
/// Types into the console of a Claude Code the user started themselves.
/// </summary>
/// <remarks>
/// <para>
/// The alternative to taking a stalled session over in a separate agent. A
/// takeover works, but it leaves the original terminal showing a screen that is
/// no longer true, and anything the user types there afterwards races the agent
/// for the same transcript. Prodding the terminal it is already in keeps the
/// session where the user put it.
/// </para>
/// <para>
/// <b>Why the console input buffer and not SendInput.</b> <c>SendInput</c>
/// delivers to whatever holds the foreground, so it means stealing focus and
/// trusting that the right window — and, in Windows Terminal, the right
/// <i>tab</i> — is in front. Aiming at the wrong tab types a sentence into
/// somebody else's session. <c>AttachConsole</c> addresses a process, not a
/// window, so there is nothing to aim.
/// </para>
/// <para>
/// <b>Measured, not assumed.</b> Verified against a node process running under
/// Windows Terminal — the topology the app actually launches — where the
/// injected line arrived intact on stdin. Two findings shaped this code:
/// <c>GetStdHandle</c> after attaching still answers with the handle this
/// process inherited at startup (ERROR_INVALID_HANDLE), so the buffer is opened
/// through <c>CONIN$</c>; and non-ASCII text arrives mangled, one replacement
/// character per glyph, because the target console's input code page is not
/// ours to change. Hence <see cref="IsSendable"/>.
/// </para>
/// </remarks>
internal static class TerminalNudge
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(
        IntPtr handle, INPUT_RECORD[] buffer, uint length, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    private const ushort KeyEventType = 0x0001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const ushort VkReturn = 0x0D;

    /// <summary>
    /// Whether this text can be typed into a console without being corrupted.
    /// </summary>
    /// <remarks>
    /// The target's input code page decides how bytes become characters, and it
    /// belongs to the user's session — changing it to suit us would alter how
    /// their own terminal renders. So anything outside ASCII is simply refused,
    /// and the caller sends the English message instead. Measured: a Chinese
    /// sentence arrived as one replacement character per glyph.
    /// </remarks>
    public static bool IsSendable(string text) => text.All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// Type <paramref name="text"/> followed by Enter into the console of
    /// <paramref name="processId"/>.
    /// </summary>
    /// <remarks>
    /// Delivering the keystrokes is not the same as the session acting on them:
    /// the caller must confirm from the transcript that something happened. This
    /// only reports that the input buffer accepted them.
    /// </remarks>
    public static NudgeResult Send(int processId, string text)
    {
        if (processId <= 0) return new(NudgeOutcome.NoProcess, "no pid");
        if (!IsSendable(text)) return new(NudgeOutcome.NotAscii, "text is not ASCII");

        try
        {
            using var _ = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new(NudgeOutcome.NoProcess, "process is gone");
        }

        // This process has no console of its own, but detaching first is what
        // makes a second attach legal if one was ever inherited.
        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return new(NudgeOutcome.AttachFailed, $"AttachConsole err={Marshal.GetLastWin32Error()}");
        }

        var handle = new IntPtr(-1);
        try
        {
            handle = CreateFileW(
                "CONIN$", GenericRead | GenericWrite, ShareRead | ShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                return new(NudgeOutcome.WriteFailed, $"CONIN$ err={Marshal.GetLastWin32Error()}");
            }

            var records = Records(text + "\r");
            if (!WriteConsoleInputW(handle, records, (uint)records.Length, out uint written))
            {
                return new(NudgeOutcome.WriteFailed, $"write err={Marshal.GetLastWin32Error()}");
            }
            if (written != records.Length)
            {
                return new(NudgeOutcome.WriteFailed, $"partial write {written}/{records.Length}");
            }
            return new(NudgeOutcome.Delivered, $"wrote {written} events");
        }
        finally
        {
            if (handle != new IntPtr(-1) && handle != IntPtr.Zero) CloseHandle(handle);
            // Leaving ourselves attached would tie this process's lifetime and
            // std handles to somebody else's console.
            FreeConsole();
        }
    }

    /// <summary>A key-down and key-up pair per character, as a console expects.</summary>
    private static INPUT_RECORD[] Records(string text)
    {
        var records = new INPUT_RECORD[text.Length * 2];
        int i = 0;
        foreach (char c in text)
        {
            records[i++] = Key(c, down: true);
            records[i++] = Key(c, down: false);
        }
        return records;
    }

    private static INPUT_RECORD Key(char c, bool down) => new()
    {
        EventType = KeyEventType,
        KeyEvent = new KEY_EVENT_RECORD
        {
            KeyDown = down ? 1 : 0,
            RepeatCount = 1,
            // Only Enter needs a virtual key: the rest are delivered by their
            // character, which is what a console reader consumes.
            VirtualKeyCode = c == '\r' ? VkReturn : (ushort)0,
            VirtualScanCode = 0,
            UnicodeChar = c,
            ControlKeyState = 0,
        },
    };
}
