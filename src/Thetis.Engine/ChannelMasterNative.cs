using System.Runtime.InteropServices;

namespace Thetis.Engine;

internal static class ChannelMasterNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Checkpoint(int stage, nint context);

    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisCmOpen(int abi, int receiverRate, int audioMode, int allowTransmit,
        Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisCmClose();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisCmGetState([Out] int[] values, int capacity);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisCmP2PortBase(int discoveryPort, int useRelocatedPorts);
}
