using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Headless;
using Thetis.Preview;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class G2SoakCliTests
{
    private static string NewReport() => Path.Combine(Path.GetTempPath(),$"g2-soak-{Guid.NewGuid():N}.json");
    private static string[] Args(string report,int seconds = 60,int reconnects = 0) => ["g2-soak","--native-dir",Path.GetTempPath(),
        "--nic","169.254.47.65","--target","169.254.187.120","--mac","2c:cf:67:fc:a3:df","--confirm-ant1-rx",
        "--confirm-extended-rx","--duration-seconds",seconds.ToString(),"--report",report,"--reconnects",reconnects.ToString()];

    [TestMethod]
    public async Task HelpAndStrictGrammarNeverReachHardware()
    {
        var services = new G2SoakServices { CreateSession = () => throw new AssertFailedException("Reached hardware factory.") };
        string report = NewReport(); var args = Args(report);
        Assert.AreEqual(0,await G2SoakCli.RunAsync(["g2-soak","--help"],TextWriter.Null,TextWriter.Null,services:services));
        foreach (string[] bad in new string[][]
        {
            args.Where(s => s != "--confirm-ant1-rx").ToArray(), args.Where(s => s != "--confirm-extended-rx").ToArray(),
            Args(report,59), Args(report,3601), Args(report,60,11), Args(report,60,-1),
            [..args,"--confirm-extended-rx"], [..args,"--duration-seconds","60"], [..args,"--reconnects","1"],
            [..args,"--ptt"], [..args,"--tx"], [..args,"--capture",NewReport()], [..args,"--first-channel","2"],
            [..args,"--device","0","--first-channel","1"], [..args,"--device","0","--first-channel","128"],
            Args("relative.json"), [..args,"--native-dir","other"], ["g2-soak"]
        }) Assert.AreEqual(2,await G2SoakCli.RunAsync(bad,TextWriter.Null,TextWriter.Null,services:services));
        Assert.IsFalse(File.Exists(report));
        var valid = G2SoakCli.Parse(Args(report,3600,10));
        Assert.IsNull(valid.Listen.Device); Assert.IsFalse(valid.Listen.Unmute);
        Assert.AreEqual(14_074_000,valid.Request(3600).FrequencyHz);
        Assert.IsTrue(valid.Request(3600).ConfirmExtendedReceive); valid.Request(3600).Validate();
        Assert.IsFalse(valid.Request(10).ConfirmExtendedReceive);
    }

    [TestMethod]
    public void ExtendedOptInDoesNotChangeShortHardwareDefaultsOrEndpointPolicy()
    {
        var normal = new G2ReceiveOptions("169.254.187.120","169.254.47.65","255.255.0.0",DurationSeconds:61);
        Assert.ThrowsExactly<ArgumentException>(normal.Validate);
        var extended = normal with { DurationSeconds = 3600,ConfirmExtendedReceive = true }; extended.Validate();
        foreach (var invalid in new[] { extended with { DurationSeconds = 3601 },extended with { DurationSeconds = 0 },
            extended with { RadioAddress = "127.0.0.1" },extended with { FrequencyHz = 7000000 } })
            Assert.ThrowsExactly<ArgumentException>(invalid.Validate);
        var request = new G2HardwareRequest(new(normal.LocalAddress,normal.RadioAddress,"2c:cf:67:fc:a3:df"),DurationSeconds:3600,
            ConfirmAnt1ReceiveOnly:true);
        Assert.ThrowsExactly<ArgumentException>(request.Validate);
        (request with { ConfirmExtendedReceive = true }).Validate();
        Assert.ThrowsExactly<ArgumentException>((request with { ConfirmAnt1ReceiveOnly = false,ConfirmExtendedReceive = true }).Validate);
        var nic = new NicRadioScanResult { LocalMaskIPv4 = System.Net.IPAddress.Parse(normal.SubnetMask) };
        Assert.IsTrue(G2Preflight.Options(request with { ConfirmExtendedReceive = true },nic).ConfirmExtendedReceive);
    }

    [TestMethod]
    public async Task VirtualHourAndTenReconnectsRetainSeparatePhasesAndExplicitUnmute()
    {
        string path = NewReport(); var fake = new Fixture();
        try
        {
            int code = await G2SoakCli.RunAsync([..Args(path,3600,10),"--unmute"],TextWriter.Null,TextWriter.Null,services:fake.Services);
            Assert.AreEqual(0,code); Assert.IsTrue(fake.Disposed); Assert.AreEqual(11,fake.IdleChecks);
            CollectionAssert.AreEqual(new[] { 3600,10,10,10,10,10,10,10,10,10,10 },fake.Requested.ToArray());
            Assert.AreEqual(11,fake.Unmutes);
            using var report = JsonDocument.Parse(File.ReadAllText(path)); var root = report.RootElement;
            Assert.IsTrue(root.GetProperty("passed").GetBoolean()); Assert.IsFalse(root.GetProperty("hardwareAttempted").GetBoolean());
            Assert.IsFalse(root.GetProperty("transmitAllowed").GetBoolean());
            Assert.AreEqual(11,root.GetProperty("phases").GetArrayLength());
            Assert.AreEqual(3600,root.GetProperty("phases")[0].GetProperty("requestedSeconds").GetInt32());
            Assert.IsFalse(File.ReadAllText(path).Contains("169.254.",StringComparison.Ordinal));
            Assert.IsFalse(File.ReadAllText(path).Contains("2C-CF",StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [TestMethod,DataRow("network"),DataRow("overrun"),DataRow("underrun"),DataRow("key"),DataRow("nonfinite"),
        DataRow("early"),DataRow("stalled"),DataRow("deadline"),DataRow("busy"),DataRow("driver")]
    public async Task FaultsFailClosedRetainReportAndNeverContinueReconnectCampaign(string fault)
    {
        string path = NewReport(); var fake = new Fixture { Fault = fault };
        try
        {
            Assert.AreEqual(4,await G2SoakCli.RunAsync(Args(path,60,10),TextWriter.Null,TextWriter.Null,services:fake.Services));
            Assert.IsTrue(fake.Disposed); Assert.AreEqual(1,fake.Requested.Count);
            using var report = JsonDocument.Parse(File.ReadAllText(path)); var phase = report.RootElement.GetProperty("phases")[0];
            Assert.IsFalse(report.RootElement.GetProperty("passed").GetBoolean());
            Assert.IsFalse(phase.GetProperty("passed").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null,phase.GetProperty("idleAfterStop").ValueKind);
            if (fault == "network") Assert.AreEqual(1,phase.GetProperty("lastObserved").GetProperty("receive").GetProperty("missingPackets").GetInt64());
            if (fault == "overrun") Assert.AreEqual(1,phase.GetProperty("lastObserved").GetProperty("receive").GetProperty("inputOverruns").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [TestMethod,DataRow("cancel",130),DataRow("pre-cancel",130),DataRow("native",3),DataRow("startup",4),DataRow("cleanup",4)]
    public async Task CancellationStartupAndCleanupFailuresAreReported(string fault,int exit)
    {
        string path = NewReport(); using var cancel = new CancellationTokenSource();
        var fake = new Fixture { Fault = fault,Cancel = cancel };
        if (fault == "pre-cancel") cancel.Cancel();
        try
        {
            Assert.AreEqual(exit,await G2SoakCli.RunAsync(Args(path,60,10),TextWriter.Null,TextWriter.Null,cancel.Token,fake.Services));
            using var report = JsonDocument.Parse(File.ReadAllText(path));
            Assert.IsFalse(report.RootElement.GetProperty("passed").GetBoolean());
            if (fault == "pre-cancel") Assert.AreEqual(0,fake.Requested.Count);
            else Assert.IsTrue(fake.Disposed);
            if (fault == "cancel") Assert.AreEqual(1,fake.Requested.Count);
            if (fault == "cleanup") Assert.AreEqual("IOException",report.RootElement.GetProperty("cleanupFailureType").GetString());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ExistingReportIsPreservedWithoutOpeningAReceiver()
    {
        string path = NewReport(); var fake = new Fixture();
        try
        {
            File.WriteAllText(path,"keep me");
            Assert.AreEqual(2,await G2SoakCli.RunAsync(Args(path),TextWriter.Null,TextWriter.Null,services:fake.Services));
            Assert.AreEqual("keep me",File.ReadAllText(path)); Assert.AreEqual(0,fake.Requested.Count);
        }
        finally { File.Delete(path); }
    }

    private sealed class Fixture : IG2SoakSession
    {
        private double now,started; private int duration;
        internal string Fault = "";
        internal CancellationTokenSource? Cancel;
        internal bool Disposed;
        internal int IdleChecks,Unmutes;
        internal List<int> Requested { get; } = [];
        internal G2SoakServices Services => new()
        {
            Hardware = false,CreateSession = () => this,ReadSeconds = () => now,
            Delay = (_,token) => { now += 1; if (Fault == "cancel" && now >= 2) Cancel!.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            VerifyIdle = (_,_) => { Assert.IsFalse(Connected); ++IdleChecks; if (Fault == "busy") throw new InvalidOperationException("busy"); return Task.CompletedTask; }
        };
        public bool Connected => Fault == "deadline" || now-started < (Fault == "early" ? 5 : duration);
        public Exception? Error => null;
        public DiagnosticReport Diagnostics => new PreviewDiagnostics().Snapshot();
        public Task ConnectAsync(G2SoakOptions options,int seconds,CancellationToken token)
        {
            Assert.IsFalse(Disposed); options.Request(seconds).Validate();
            started = now; duration = seconds; Requested.Add(seconds);
            if (Fault == "native") throw new EntryPointNotFoundException();
            if (Fault == "startup") throw new IOException("startup failed");
            return Task.CompletedTask;
        }
        public G2SoakObservation Observation
        {
            get
            {
                long frames = Fault == "stalled" ? 0 : (long)((now-started)*48000);
                var rx = new ReceiveState(12000,1024,2,192000,Connected ? 1 : 0,frames/238,frames*4,
                    Fault == "network" ? 1 : 0,0,0,0,0,1,0,1,Fault == "overrun" ? 1 : 0,0,0,frames,0);
                var output = new PlaybackState(false,Connected,48000,true,0,frames,0,frames,0,Fault == "underrun" ? 1 : 0,
                    0,0,0,0,false,0,0,0,Fault == "driver" ? PlaybackFault.DriverError : PlaybackFault.None,0);
                var safety = new G2ReceiveSafetyState(Connected,Connected ? 0 : 1,Fault == "key" ? 1 : 0,0,Connected ? 0 : 5,0);
                return new(rx,output,safety,Fault == "stalled" ? 0 : 1+(long)((now-started)*20),Fault != "nonfinite");
            }
        }
        public Task UnmuteAsync(G2SoakOptions options,CancellationToken token)
        { Assert.IsTrue(now-started >= 1); ++Unmutes; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        { Disposed = true; if (Fault == "cleanup") throw new IOException("cleanup failed"); return ValueTask.CompletedTask; }
    }
}
