using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SimulatorCliTests
{
    [TestMethod]
    public async Task HelpAndInvalidArgumentsDoNotStartServer()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, await SimulatorCli.RunAsync(["--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "loopback ONLY");
        foreach (string[] args in new string[][]
        {
            ["serve", "--bind", "0.0.0.0"], ["serve", "--tx", "true"], ["serve", "--base-port", "65516"],
            ["serve", "--tone-hz", "NaN"], ["serve", "--noise", "1"], ["serve", "--base-port"],
            ["serve", "--base-port", "0", "--base-port", "1"], ["selftest", "--base-port", "1024"]
        }) Assert.AreEqual(2, await SimulatorCli.RunAsync(args, TextWriter.Null, TextWriter.Null));
        Assert.AreEqual(130, await SimulatorCli.RunAsync(["serve"], TextWriter.Null, TextWriter.Null, new(true)));
    }

    [TestMethod]
    public void AllServeOptionsAreValidatedWithInvariantNumbers()
    {
        var (options, duration) = SimulatorCli.Parse(["serve", "--base-port", "51024", "--tone-hz", "14000123.5",
            "--amplitude", "0.1", "--noise", "0.01", "--seed", "123", "--drop-every", "7", "--duration-seconds", "0"]);
        Assert.AreEqual(14_000_123.5, options.ToneFrequencyHz);
        Assert.AreEqual(0.1, options.Amplitude); Assert.AreEqual(0.01, options.NoiseAmplitude);
        Assert.AreEqual(123u, options.Seed); Assert.AreEqual(7, options.DropEvery); Assert.AreEqual(0, duration);
    }

    [TestMethod]
    public async Task FiniteServeReportsActualBoundPortAndClosesCleanly()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, await SimulatorCli.RunAsync(["serve", "--base-port", "0", "--duration-seconds", "1"], output, TextWriter.Null));
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines);
        using var ready = JsonDocument.Parse(lines[0]);
        Assert.AreEqual("ready", ready.RootElement.GetProperty("eventType").GetString());
        Assert.AreEqual("127.0.0.1", ready.RootElement.GetProperty("address").GetString());
        Assert.IsFalse(ready.RootElement.GetProperty("transmitSupported").GetBoolean());
        using var stopped = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("stopped", stopped.RootElement.GetProperty("eventType").GetString());
        Assert.IsFalse(stopped.RootElement.GetProperty("state").GetProperty("running").GetBoolean());
    }
}
