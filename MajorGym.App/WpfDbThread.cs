using System.Windows;
using MajorGym.Data;

namespace MajorGym.App;

/// <summary>
/// Runs <see cref="SyncManager"/>'s database work on the WPF UI thread — the thread that owns the
/// app's single shared SQLite connection (see BackupViewModel's threading note). Only the slow,
/// database-free work (mDNS, sockets, crypto, JSON) happens off-thread.
/// </summary>
internal sealed class WpfDbThread : IDbThread
{
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess()
            ? Task.FromResult(work())
            : dispatcher.InvokeAsync(work).Task;
    }
}
