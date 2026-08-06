using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

/// <summary>P/Invoke + JSON-over-the-wire client for claude_switch.dll.</summary>
public sealed class Engine : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public Engine(string? isolatedRoot = null)
    {
        int err = 0;
        _handle = isolatedRoot is null
            ? Native.cs_engine_create(null, ref err)
            : Native.cs_engine_create(isolatedRoot, ref err);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"cs_engine_create failed: {err} ({Native.ErrorName(err)})");
    }

    public JsonNode Call(string method, object? paramsObj = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var paramsJson = paramsObj is null ? "{}" : JsonSerializer.Serialize(paramsObj);
        IntPtr outPtr = IntPtr.Zero;
        int rc = Native.cs_engine_call(_handle, method, paramsJson, ref outPtr);
        string json = outPtr == IntPtr.Zero ? "{}" : Marshal.PtrToStringUTF8(outPtr) ?? "{}";
        if (outPtr != IntPtr.Zero)
            Native.cs_string_free(outPtr);
        if (rc != 0)
            throw new EngineException(rc, json);
        return JsonNode.Parse(json) ?? new JsonObject();
    }

    public JsonNode Snapshot() => Call("snapshot");

    public JsonNode SwitchTo(string id) => Call("switch_to", new { id });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            Native.cs_engine_free(_handle);
            _handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class EngineException : Exception
{
    public int Code { get; }
    public string Json { get; }

    public EngineException(int code, string json)
        : base($"Engine call failed ({code} {Native.ErrorName(code)}): {json}")
    {
        Code = code;
        Json = json;
    }
}

internal static class Native
{
    private const string Dll = "claude_switch";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cs_engine_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? rootUtf8,
        ref int outErr);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void cs_engine_free(IntPtr eng);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int cs_engine_call(
        IntPtr eng,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string methodUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string paramsJsonUtf8,
        ref IntPtr outJsonUtf8);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void cs_string_free(IntPtr s);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cs_error_code_string(int code);

    public static string ErrorName(int code)
    {
        var p = cs_error_code_string(code);
        return p == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(p) ?? "unknown";
    }
}
