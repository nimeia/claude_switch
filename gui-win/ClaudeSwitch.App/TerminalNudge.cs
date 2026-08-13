using System.Runtime.InteropServices;

namespace ClaudeSwitch.App;

/// <summary>How an attempt to type into a terminal ended.</summary>
internal enum NudgeOutcome
{
    /// <summary>The keystrokes reached the process's input.</summary>
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

/// <summary>What a process is actually reading as stdin.</summary>
internal enum RemoteStdinKind
{
    Unknown,
    Pipe,
    Character,
    Disk,
}

/// <summary>One synthesised key, for tests that check the plan without attaching.</summary>
internal readonly record struct KeyStroke(char Char, ushort VirtualKey, uint Mods);

/// <summary>
/// Types into the console — or the stdin pipe — of a Claude Code the user
/// started themselves.
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
/// <b>Two input shapes, measured.</b> A process hosted by conhost, <c>cmd</c>,
/// Windows Terminal, or Warp's own OpenConsole reads a real console
/// (<c>GetFileType = CHAR</c>): key events go in through <c>CONIN$</c>. A
/// process whose stdin is a pipe holds the read end, so we cannot push bytes
/// into it — those hosts fall through to a takeover. Warp's GUI shell prompt
/// is a related miss: stdin is a console nobody is reading (Warp types into
/// the PTY from the GUI). A Claude TUI in that window is passthrough and
/// follows the OpenConsole path, which was verified; a shell prompt falls
/// through to a takeover.
/// </para>
/// <para>
/// <b>AttachConsole is process-wide.</b> Every call is serialised. A second
/// sweep overlapping a nudge would otherwise detach the first mid-write.
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, out IntPtr buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int klass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFrom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const ushort KeyEventType = 0x0001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const ushort VkReturn = 0x0D;
    private const uint ShiftPressed = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 2;
    private const uint Th32csSnapProcess = 2;
    private const uint FileTypeDisk = 1;
    private const uint FileTypeChar = 2;
    private const uint FileTypePipe = 3;
    private const int ErrorAccessDenied = 5;
    private const int PebProcessParameters = 0x20;
    private const int ParamsStandardInput = 0x20;

    /// <summary>Records written per <c>WriteConsoleInputW</c> call.</summary>
    private const int WriteChunk = 64;

    /// <summary>How often a partially written chunk is retried before giving up.</summary>
    private const int WriteAttempts = 3;

    /// <summary>
    /// <c>AttachConsole</c> mutates this process; two overlapping nudges would
    /// detach each other.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// GUI hosts that own windows, not the console the session is reading.
    /// Walking into them would aim <c>AttachConsole</c> at the wrong process.
    /// </summary>
    private static readonly HashSet<string> SkipHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "warp", "WindowsTerminal", "explorer", "dwm", "winlogon", "services",
        "csrss", "smss", "svchost", "RuntimeBroker", "ApplicationFrameHost",
        "System", "Idle",
    };

    /// <summary>
    /// Ancestors worth attaching to when the recorded pid has no console of
    /// its own. A wrapper (<c>cmd</c> launching <c>claude</c>) shares one;
    /// a test host or IDE does not, and typing into those would land in the
    /// wrong window.
    /// </summary>
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "zsh", "fish",
        "node", "claude", "OpenConsole",
    };

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
    /// Type <paramref name="text"/> followed by Enter into
    /// <paramref name="processId"/> — or a child / parent that actually owns
    /// the input the session is reading.
    /// </summary>
    /// <remarks>
    /// Delivering the keystrokes is not the same as the session acting on them:
    /// the caller must confirm from the transcript that something happened. This
    /// only reports that the input accepted them.
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

        lock (Gate)
        {
            NudgeResult last = new(NudgeOutcome.AttachFailed, "no candidate");
            foreach (int pid in ConsoleCandidates(processId))
            {
                // A pipe stdin is the *read* end — WriteFile on it cannot
                // deliver bytes (measured). The process is either reading a
                // real console (CHAR) or a PTY we cannot write; try the
                // console, then the next candidate.
                last = WriteConsole(pid, text);
                if (last.Delivered) return last;
            }
            return last;
        }
    }

    /// <summary>
    /// The process, then its children (a wrapper that spawned the reader),
    /// then ancestors — stopping before GUI hosts and the system.
    /// </summary>
    internal static IReadOnlyList<int> ConsoleCandidates(int processId)
    {
        var seen = new HashSet<int>();
        var list = new List<int>();
        void Add(int pid)
        {
            if (pid <= 4 || !seen.Add(pid)) return;
            if (IsSkippedHost(pid)) return;
            list.Add(pid);
        }

        Add(processId);
        foreach (int child in ChildrenOf(processId)) Add(child);
        int current = processId;
        for (int i = 0; i < 8; i++)
        {
            int parent = ParentOf(current);
            if (parent <= 4 || parent == current) break;
            if (IsSkippedHost(parent)) break;
            if (!IsShellHost(parent)) break;
            Add(parent);
            current = parent;
        }
        return list;
    }

    /// <summary>The key plan a console write would use, including virtual keys.</summary>
    internal static KeyStroke[] PlanKeys(string text)
    {
        var plan = new KeyStroke[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var (vk, mods) = MapKey(text[i]);
            plan[i] = new KeyStroke(text[i], vk, mods);
        }
        return plan;
    }

    /// <summary>What the process's PEB names as stdin, without attaching.</summary>
    internal static RemoteStdinKind PeekStdin(int processId)
    {
        if (!TryDuplicateStdin(processId, out var handle, out _))
            return RemoteStdinKind.Unknown;
        try
        {
            return GetFileType(handle) switch
            {
                FileTypeDisk => RemoteStdinKind.Disk,
                FileTypeChar => RemoteStdinKind.Character,
                FileTypePipe => RemoteStdinKind.Pipe,
                _ => RemoteStdinKind.Unknown,
            };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static NudgeResult WriteConsole(int processId, string text)
    {
        FreeConsole();
        if (!TryAttach(processId, out int attachErr))
            return new(NudgeOutcome.AttachFailed, $"AttachConsole pid={processId} err={attachErr}");

        var handle = new IntPtr(-1);
        try
        {
            handle = CreateFileW(
                "CONIN$", GenericRead | GenericWrite, ShareRead | ShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1))
                return new(NudgeOutcome.WriteFailed, $"CONIN$ err={Marshal.GetLastWin32Error()}");

            var records = Records(text + "\r");
            for (int offset = 0; offset < records.Length;)
            {
                int chunkSize = Math.Min(WriteChunk, records.Length - offset);
                int sent = 0;
                for (int attempt = 1; attempt <= WriteAttempts && sent < chunkSize; attempt++)
                {
                    var rest = new INPUT_RECORD[chunkSize - sent];
                    Array.Copy(records, offset + sent, rest, 0, rest.Length);
                    if (!WriteConsoleInputW(handle, rest, (uint)rest.Length, out uint written))
                        return new(NudgeOutcome.WriteFailed, $"write err={Marshal.GetLastWin32Error()}");
                    sent += (int)written;
                    if (sent < chunkSize && attempt < WriteAttempts) Thread.Sleep(150);
                }
                if (sent != chunkSize)
                    return new(NudgeOutcome.WriteFailed, $"partial write {offset + sent}/{records.Length}");
                offset += chunkSize;
            }
            return new(NudgeOutcome.Delivered, $"console {records.Length} events pid={processId}");
        }
        finally
        {
            if (handle != new IntPtr(-1) && handle != IntPtr.Zero) CloseHandle(handle);
            FreeConsole();
        }
    }

    private static bool TryAttach(int processId, out int error)
    {
        error = 0;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (AttachConsole((uint)processId)) return true;
            error = Marshal.GetLastWin32Error();
            if (error != ErrorAccessDenied || attempt == 3) return false;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool TryDuplicateStdin(int processId, out IntPtr local, out string detail)
    {
        local = IntPtr.Zero;
        var process = OpenProcess(
            ProcessQueryInformation | ProcessVmRead | ProcessDupHandle, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            detail = $"OpenProcess err={Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(
                process, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0)
            {
                detail = $"NtQuery status=0x{status:X}";
                return false;
            }
            if (!ReadProcessMemory(
                    process, info.PebBaseAddress + PebProcessParameters,
                    out var parameters, nint.Size, out _))
            {
                detail = $"PEB err={Marshal.GetLastWin32Error()}";
                return false;
            }
            if (!ReadProcessMemory(
                    process, parameters + ParamsStandardInput,
                    out var remote, nint.Size, out _))
            {
                detail = $"params err={Marshal.GetLastWin32Error()}";
                return false;
            }
            if (remote == IntPtr.Zero)
            {
                detail = "stdin is null";
                return false;
            }
            if (!DuplicateHandle(
                    process, remote, GetCurrentProcess(), out local, 0, false, DuplicateSameAccess))
            {
                detail = $"DuplicateHandle err={Marshal.GetLastWin32Error()}";
                return false;
            }
            detail = "ok";
            return true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsSkippedHost(int pid) => NameIs(pid, SkipHosts);

    private static bool IsShellHost(int pid) => NameIs(pid, ShellHosts);

    private static bool NameIs(int pid, HashSet<string> names)
    {
        try
        {
            return names.Contains(System.Diagnostics.Process.GetProcessById(pid).ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static int ParentOf(int pid)
    {
        foreach (var (id, parent, _) in Snapshot())
        {
            if (id == pid) return parent;
        }
        return 0;
    }

    private static IEnumerable<int> ChildrenOf(int pid)
    {
        foreach (var (id, parent, _) in Snapshot())
        {
            if (parent == pid) yield return id;
        }
    }

    private static IEnumerable<(int Id, int Parent, string Name)> Snapshot()
    {
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == new IntPtr(-1)) yield break;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) yield break;
            do
            {
                yield return ((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile);
            } while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
    }

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

    private static INPUT_RECORD Key(char c, bool down)
    {
        var (vk, mods) = MapKey(c);
        return new()
        {
            EventType = KeyEventType,
            KeyEvent = new KEY_EVENT_RECORD
            {
                KeyDown = down ? 1 : 0,
                RepeatCount = 1,
                VirtualKeyCode = vk,
                VirtualScanCode = vk == 0 ? (ushort)0 : (ushort)MapVirtualKeyW(vk, 0),
                UnicodeChar = c,
                ControlKeyState = mods,
            },
        };
    }

    /// <summary>
    /// Virtual key and shift state for a character. Ctrl/Alt from
    /// <c>VkKeyScanW</c> are dropped: they would fire shortcuts rather than
    /// type the glyph, and <see cref="KEY_EVENT_RECORD.UnicodeChar"/> already
    /// carries the character.
    /// </summary>
    private static (ushort Vk, uint Mods) MapKey(char c)
    {
        if (c == '\r') return (VkReturn, 0);
        short mapped = VkKeyScanW(c);
        if (mapped == -1) return (0, 0);
        ushort vk = (ushort)(mapped & 0xFF);
        uint mods = (mapped & 0x100) != 0 ? ShiftPressed : 0;
        return (vk, mods);
    }
}
