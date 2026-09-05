using Thetis.Headless;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

return DiscoveryCli.Run(args, Console.Out, Console.Error, new DiscoveryBackend(), cancellation.Token);
