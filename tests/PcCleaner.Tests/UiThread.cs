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
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
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
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the test body did not finish");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
