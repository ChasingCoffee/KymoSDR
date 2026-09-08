using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SignalLevelTests
{
    [TestMethod]
    public void ProfileIsImmutableBoundedAndUsesExactSampleClockBoundaries()
    {
        SignalLevelStep[] steps = [new(100,.005),new(200,.25),new(300,0)];
        var profile = new SignalLevelProfile(steps); steps[0] = new(0,.9);
        foreach (int rate in new[] {48000,96000,192000,384000})
        {
            Assert.AreEqual(.1,profile.AtSample(rate/10-1,rate,.1));
            Assert.AreEqual(.005,profile.AtSample(rate/10,rate,.1));
            Assert.AreEqual(.005,profile.AtSample(rate/5-1,rate,.1));
            Assert.AreEqual(.25,profile.AtSample(rate/5,rate,.1));
            Assert.AreEqual(0,profile.AtSample(rate*3/10,rate,.1));
        }
        foreach (SignalLevelStep[] bad in new SignalLevelStep[][] {[],[new(-1,.1)],[new(600001,.1)],[new(1,.1),new(1,.2)],
            [new(10,.1),new(0,.1)],[new(0,double.NaN)],[new(0,double.PositiveInfinity)],[new(0,-.1)],[new(0,1)]})
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SignalLevelProfile(bad));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SignalLevelProfile(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SignalLevelProfile([null!]));
        foreach (double bad in new[] {double.NaN,double.PositiveInfinity,-.1,1})
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1Simulator.Open(new(Amplitude:bad)));
    }
}
