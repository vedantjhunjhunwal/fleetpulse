using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FleetPulse.Api.Native;

/// <summary>Blittable mirror of <c>j1939_event</c> in decoder/include/j1939.h (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct J1939Event
{
    public double Ts;
    public double Value;
    public uint Spn;
    public int Kind;
    public int Signal;
    public byte Fmi;
    public byte Oc;
    public byte Sa;
    public byte Lamp;
    public fixed byte Vehicle[16];

    public const int SignalKind = 1, DtcKind = 2, DtcNoneKind = 3;
}

[StructLayout(LayoutKind.Sequential)]
public struct J1939Stats
{
    public ulong Lines, Frames, ParseErrors, TpErrors, Events, TpMessages;
}

/// <summary>P/Invoke bindings for the C decoder library (libj1939.so / j1939.dll).</summary>
public static unsafe partial class J1939Native
{
    private const string Lib = "j1939";

    static J1939Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(J1939Native).Assembly, Resolve);
    }

    // FLEETPULSE_J1939_LIB can point at the library explicitly; otherwise the
    // default probing (app folder, then system paths) is used.
    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var explicitPath = Environment.GetEnvironmentVariable("FLEETPULSE_J1939_LIB");
        if (!string.IsNullOrEmpty(explicitPath) && NativeLibrary.TryLoad(explicitPath, out var h)) return h;
        return NativeLibrary.TryLoad(name, asm, path, out var handle) ? handle : IntPtr.Zero;
    }

    [DllImport(Lib, EntryPoint = "j1939_session_new")] public static extern IntPtr SessionNew();
    [DllImport(Lib, EntryPoint = "j1939_session_free")] public static extern void SessionFree(IntPtr s);
    [DllImport(Lib, EntryPoint = "j1939_session_feed_line")]
    public static extern int FeedLine(IntPtr s, byte* line, J1939Event* events, int max);
    [DllImport(Lib, EntryPoint = "j1939_session_stats")] public static extern void Stats(IntPtr s, out J1939Stats stats);
    [DllImport(Lib, EntryPoint = "j1939_version")] private static extern IntPtr VersionPtr();

    public static string Version() => Marshal.PtrToStringAnsi(VersionPtr()) ?? "?";

    public static string VehicleOf(in J1939Event e)
    {
        fixed (byte* p = e.Vehicle)
        {
            int n = 0;
            while (n < 16 && p[n] != 0) n++;
            return Encoding.ASCII.GetString(p, n);
        }
    }
}

/// <summary>Decodes a candump log (UTF-8 bytes) into J1939 events using the C library.</summary>
public sealed unsafe class J1939Decoder : IDisposable
{
    private const int MaxEvents = 128;
    private IntPtr _session = J1939Native.SessionNew();
    private readonly J1939Event[] _events = new J1939Event[MaxEvents];
    private byte[] _line = new byte[256];

    public J1939Stats Stats
    {
        get { J1939Native.Stats(_session, out var s); return s; }
    }

    /// <summary>Feeds every line of <paramref name="log"/> and calls <paramref name="onEvent"/> per event.</summary>
    public void Decode(ReadOnlySpan<byte> log, Action<J1939Event> onEvent)
    {
        if (_session == IntPtr.Zero) throw new ObjectDisposedException(nameof(J1939Decoder));
        while (!log.IsEmpty)
        {
            int nl = log.IndexOf((byte)'\n');
            var line = nl < 0 ? log : log[..nl];
            log = nl < 0 ? ReadOnlySpan<byte>.Empty : log[(nl + 1)..];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (line.IsEmpty) continue;
            if (line.Length + 1 > _line.Length) _line = new byte[line.Length * 2];
            line.CopyTo(_line);
            _line[line.Length] = 0;
            int n;
            fixed (byte* lp = _line)
            fixed (J1939Event* ep = _events)
            {
                n = J1939Native.FeedLine(_session, lp, ep, MaxEvents);
            }
            for (int i = 0; i < n; i++) onEvent(_events[i]);
        }
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero) J1939Native.SessionFree(_session);
        _session = IntPtr.Zero;
    }
}
