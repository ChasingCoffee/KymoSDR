using Thetis.Headless;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

return args.FirstOrDefault() switch
{
    "g2-soak" => await G2SoakCli.RunAsync(args,Console.Out,Console.Error,cancellation.Token),
    "g2-listen" => await G2ListenCli.RunAsync(args,Console.Out,Console.Error,cancellation.Token),
    "g2-receive" => await G2ReceiveCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    "playback-selftest" => await PlaybackCli.RunAsync(args,Console.Out,Console.Error,cancellation.Token),
    "audio-devices" => await PlaybackCli.RunAsync(args,Console.Out,Console.Error,cancellation.Token),
    "playback-listen" => await PlaybackCli.RunAsync(args,Console.Out,Console.Error,cancellation.Token),
    "receive-gain-selftest" => await ReceiveGainCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    "p1-receive-selftest" => await P1ReceiveCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    "dsp-selftest" => DspCli.Run(args, Console.Out, Console.Error, cancellation.Token),
    "session-selftest" => SessionCli.Run(args, Console.Out, Console.Error, cancellation.Token),
    "transport-selftest" => TransportCli.Run(args, Console.Out, Console.Error, cancellation.Token),
    "receive-selftest" => await ReceiveCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    "receive-controls-selftest" => await ReceiveControlsCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    "receive-soak" => await ReceiveSoakCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token),
    _ => DiscoveryCli.Run(args, Console.Out, Console.Error, new DiscoveryBackend(), cancellation.Token)
};
