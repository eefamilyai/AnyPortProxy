using System.Runtime.InteropServices;

namespace AnyPortProxy.CatchAll;

/// <summary>Mirrors WINDIVERT_ADDRESS (WinDivert 2.x, 80 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Size = 80)]
internal struct WinDivertAddress
{
    public long Timestamp;
    public uint Bits;      // Layer:8 Event:8 Sniffed:1 Outbound:1 Loopback:1 Impostor:1 IPv6:1 IPChecksum:1 TCPChecksum:1 UDPChecksum:1
    public uint Reserved2;
    public uint IfIdx;
    public uint SubIfIdx;

    public readonly bool Outbound => (Bits & (1u << 17)) != 0;

    /// <summary>The packet's TCP checksum is complete and valid (not left to NIC offload).</summary>
    public readonly bool TcpChecksumValid => (Bits & (1u << 22)) != 0;

    /// <summary>The packet's UDP checksum is complete and valid.</summary>
    public readonly bool UdpChecksumValid => (Bits & (1u << 23)) != 0;
}

internal static unsafe class WinDivert
{
    private const string Dll = "WinDivert.dll";
    public const int LayerNetwork = 0;
    private const int ShutdownBoth = 3;

    // WINDIVERT_PARAM_* and their maximums.
    private const int ParamQueueLength = 0, ParamQueueTime = 1, ParamQueueSize = 2;
    private const ulong MaxQueueLength = 16384, MaxQueueTime = 16000, MaxQueueSize = 33554432;

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertRecvEx(IntPtr handle, byte* packet, uint packetLen, uint* recvLen, ulong flags,
        WinDivertAddress* addr, uint* addrLen, IntPtr overlapped);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertSendEx(IntPtr handle, byte* packet, uint sendLen, uint* sendLenOut, ulong flags,
        WinDivertAddress* addr, uint addrLen, IntPtr overlapped);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertHelperCalcChecksums(byte* packet, uint packetLen, WinDivertAddress* addr, ulong flags);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertHelperParsePacket(byte* packet, uint packetLen, void* ipHdr, void* ipv6Hdr, byte* protocol,
        void* icmpHdr, void* icmpv6Hdr, void* tcpHdr, void* udpHdr, void* data, uint* dataLen, byte** next, uint* nextLen);

    [DllImport(Dll, SetLastError = true)]
    private static extern bool WinDivertSetParam(IntPtr handle, int param, ulong value);

    [DllImport(Dll, SetLastError = true)]
    private static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport(Dll, SetLastError = true)]
    private static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern bool WinDivertHelperCompileFilter(string filter, int layer, IntPtr obj, uint objLen, out IntPtr errorStr, out uint errorPos);

    public static IntPtr Open(string filter)
    {
        if (!WinDivertHelperCompileFilter(filter, LayerNetwork, IntPtr.Zero, 0, out var errPtr, out var errPos))
        {
            var msg = Marshal.PtrToStringAnsi(errPtr) ?? "unknown error";
            throw new InvalidOperationException($"WinDivert rejected the filter at position {errPos}: {msg}\nFilter: {filter}");
        }

        var h = WinDivertOpen(filter, LayerNetwork, 0, 0);
        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"WinDivertOpen failed (error {err}): {Describe(err)}");
        }

        // Big queues so bursts are buffered in the driver instead of dropped.
        WinDivertSetParam(h, ParamQueueLength, MaxQueueLength);
        WinDivertSetParam(h, ParamQueueTime, MaxQueueTime);
        WinDivertSetParam(h, ParamQueueSize, MaxQueueSize);
        return h;
    }

    public static void Shutdown(IntPtr h) => WinDivertShutdown(h, ShutdownBoth);

    public static void Close(IntPtr h) => WinDivertClose(h);

    private static string Describe(int err) => err switch
    {
        2 => "WinDivert64.sys not found next to WinDivert.dll / the exe. Run build.ps1.",
        5 => "access denied. Run as Administrator (the Windows service runs as LocalSystem, which is fine).",
        87 => "invalid parameter (bad filter?).",
        577 => "the driver signature could not be verified. Check Secure Boot / antivirus.",
        654 => "a different WinDivert driver version is already loaded by another program. Close it or reboot.",
        1275 => "the driver was blocked from loading (antivirus or Windows driver blocklist). Whitelist WinDivert64.sys.",
        1753 => "the Base Filtering Engine (BFE) service is not running.",
        _ => new System.ComponentModel.Win32Exception(err).Message,
    };
}
