using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Thetis.Desktop.Tests;

// Own the thread before publishing readiness. Avalonia 12.1.2's manual
// HeadlessUnitTestSession.StartNew captures its Task before assignment; a fast
// worker can publish a session with a null task, failing later during disposal.
// Keep one headless application per test process and explicitly join its UI
// thread. Individual tests still own and close every window/controller.
internal static class UiTestHost
{
    private static readonly TaskCompletionSource ReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource<Exception?> Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly CancellationTokenSource Stop = new();
    private static readonly SemaphoreSlim Gate = new(1,1);
    private static readonly Thread Worker = Start();
    internal static Task Ready => ReadySource.Task;
    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                HeadlessBootstrap.BuildAvaloniaApp().SetupWithoutStarting();
                ReadySource.TrySetResult();
                Dispatcher.UIThread.MainLoop(Stop.Token);
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            catch (Exception ex) { failure = ex; ReadySource.TrySetException(ex); }
            finally { Exited.TrySetResult(failure); }
        }) { IsBackground = true, Name = "Kymo test UI" };
        thread.Start(); return thread;
    }
    internal static async Task RunAsync(Func<Task> body)
    {
        await Ready.ConfigureAwait(false); await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try { await body(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            if (await Task.WhenAny(completion.Task,Exited.Task).ConfigureAwait(false) == Exited.Task)
                throw new InvalidOperationException("The test UI thread stopped unexpectedly.",await Exited.Task.ConfigureAwait(false));
            await completion.Task.ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
    internal static async Task StopAsync()
    {
        Stop.Cancel();
        await Task.Run(() =>
        {
            if (!Worker.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test UI thread did not stop.");
        }).ConfigureAwait(false);
        Stop.Dispose(); Gate.Dispose();
        if (await Exited.Task.ConfigureAwait(false) is { } failure)
            throw new InvalidOperationException("The test UI thread failed.",failure);
    }
}

[TestClass]
public sealed class UiTestLifetime
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext _) => UiTestHost.Ready;
    [AssemblyCleanup]
    public static Task Cleanup() => UiTestHost.StopAsync();
}
