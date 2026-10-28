using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Xunit;

namespace PcCleaner.Tests;

/// <summary>Runs view-model tests the way the app runs them: on one UI thread with a dispatcher.</summary>
public static class UiThread
{
    /// <summary>
    /// Runs an async test body on one thread with a dispatcher, like the app's UI thread: every step after an await comes back to it.
    /// Page lists are WPF CollectionViews, which refuse to be changed from another thread (the test runner's continuations hop between
    /// threads). An exception of the body is rethrown on the calling thread.
    /// </summary>
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;
        var thread = Start(body, ex => failure = ex);
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the test body did not finish");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// Like <see cref="Run"/>, but the test awaits the body instead of blocking a thread of the test runner. For tests whose view model
    /// parks a flow (a held request, the wait behind the list of computers) and resumes it under a deadline: started on the test's own
    /// thread, every step of that flow would be queued on xUnit's synchronization context, which has one thread per core and is shared
    /// by all test classes running at the time — on a 2-core CI runner other classes' long synchronous tests hold both threads for
    /// seconds and the flow misses its deadline. Here the flow comes back to this UI thread only, in order, as in the app.
    /// </summary>
    public static Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(body, ex =>
        {
            if (ex is null)
                done.TrySetResult();
            else
                done.TrySetException(ex);
        });
        return done.Task;
    }

    private static Thread Start(Func<Task> body, Action<Exception?> finished)
    {
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
                finished(failure);
            });
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }
}
