using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class SessionTests
{
    private static string NativeDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return directory;
    }

    [TestMethod]
    public void InvalidOptionsAndPreCancellationDoNotLoadNativeCode()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OfflineRadioSession.Open("unused", new(12345)));
        Assert.ThrowsExactly<NotSupportedException>(() => OfflineRadioSession.Open("unused", new(AudioMode: (SessionAudioMode)1)));
        Assert.ThrowsExactly<OperationCanceledException>(() => OfflineRadioSession.Open("unused", cancellationToken: new(true)));
    }

    [TestMethod, TestCategory("Native")]
    public void TopologyIsExclusiveAndDisposalIsIdempotent()
    {
        string directory = NativeDirectory();
        using var session = OfflineRadioSession.Open(directory);
        Assert.AreEqual(new OfflineSessionState(8, 5, 2, 1, 2, 192000, 48000, 192000, 18, 1, 5, 5), session.State);
        Assert.ThrowsExactly<InvalidOperationException>(() => OfflineRadioSession.Open(directory));
        Assert.ThrowsExactly<InvalidOperationException>(() => DspDiagnostics.Run(directory));
        GC.Collect(); GC.WaitForPendingFinalizers(); // live owner/callbacks must remain valid
        Assert.AreEqual(18, session.State.ChannelMasterWorkers);
        session.Dispose(); session.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.State);
        AssertClosed();
        using var reopened = OfflineRadioSession.Open(directory, new(96000));
        Assert.AreEqual(96000, reopened.State.ReceiverInputRate);
    }

    [TestMethod, TestCategory("Native")]
    public void CancellationAndExceptionsRollbackEveryStartupStage()
    {
        string directory = NativeDirectory();
        for (int target = 1; target <= 3; ++target)
        {
            using var cts = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => OfflineRadioSession.OpenCore(directory, null, cts.Token, stage =>
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                if (stage == target) { cts.Cancel(); return 1; }
                return 0;
            }));
            AssertClosed();
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => OfflineRadioSession.OpenCore(directory, null, default,
                stage => stage == target ? throw new IOException("injected startup failure") : 0));
            Assert.IsInstanceOfType<IOException>(error.InnerException);
            AssertClosed();
            using var next = OfflineRadioSession.Open(directory);
            Assert.AreEqual(8, next.State.Streams);
        }
    }

    [TestMethod, TestCategory("Native")]
    public void DefaultAndRelocatedP2PortsUseExplicitFlag()
    {
        DspRuntime.Initialize(NativeDirectory());
        Assert.AreEqual(1025, ChannelMasterNative.ThetisCmP2PortBase(1024, 0));
        Assert.AreEqual(1025, ChannelMasterNative.ThetisCmP2PortBase(1024, 1));
        Assert.AreEqual(1025, ChannelMasterNative.ThetisCmP2PortBase(5000, 0));
        Assert.AreEqual(5001, ChannelMasterNative.ThetisCmP2PortBase(5000, 1));
        Assert.AreEqual(-1, ChannelMasterNative.ThetisCmP2PortBase(65535, 1));
    }

    private static void AssertClosed()
    {
        var state = OfflineRadioSession.ReadNativeState();
        Assert.AreEqual(0, state[1]);
        Assert.AreEqual(0, state[10]);
    }
}
