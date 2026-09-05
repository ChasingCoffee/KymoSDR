using Thetis.Headless;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

return args.FirstOrDefault() == "dsp-selftest"
    ? DspCli.Run(args, Console.Out, Console.Error, cancellation.Token)
    : DiscoveryCli.Run(args, Console.Out, Console.Error, new DiscoveryBackend(), cancellation.Token);
