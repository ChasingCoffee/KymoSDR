using System.Runtime.InteropServices;

namespace Thetis.Engine;

internal static class TransportNative
{
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisTransportOpen(int abi,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string remote, int remotePort,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string local, int localPort,
        int protocol, int model, int relocate, ChannelMasterNative.Checkpoint checkpoint, nint context);

    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisTransportClose();

    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisTransportGetState([Out] int[] values, int capacity);
}
