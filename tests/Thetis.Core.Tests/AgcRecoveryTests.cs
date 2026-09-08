using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class AgcRecoveryTests
{
    [TestMethod, DataRow(30), DataRow(32), DataRow(39)]
    public void RecoveryStartsAtObservedDropNotSourceTimestamp(int drop)
    {
        double[] rms = Enumerable.Repeat(.694,95).ToArray();
        rms[drop] = .2; rms[drop+1] = .1; rms[drop+2] = .3;
        rms[drop+3] = .7; rms[drop+4] = .4; // transient crossing is not recovery
        var result = ReceiveGainSelfTest.AnalyzeRecovery(rms);
        Assert.AreEqual(drop,result.DropIndex);
        Assert.AreEqual(500,result.RecoveryMilliseconds);
        Assert.AreEqual(.2,result.EarlyRms,1e-12);
    }
    [TestMethod]
    public void MissingDropUnboundedRecoveryAndMalformedWindowsFail()
    {
        double[] rms = Enumerable.Repeat(.694,95).ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => ReceiveGainSelfTest.AnalyzeRecovery(rms));
        Array.Fill(rms,.01,32,40);
        Assert.ThrowsExactly<InvalidOperationException>(() => ReceiveGainSelfTest.AnalyzeRecovery(rms));
        rms[0] = double.NaN;
        Assert.ThrowsExactly<InvalidDataException>(() => ReceiveGainSelfTest.AnalyzeRecovery(rms));
        Assert.ThrowsExactly<InvalidDataException>(() => ReceiveGainSelfTest.AnalyzeRecovery([]));
    }
}
