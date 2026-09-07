using Thetis.Simulator;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
return await SimulatorCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
