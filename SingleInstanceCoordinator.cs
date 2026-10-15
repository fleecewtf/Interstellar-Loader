using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Interstellar.Loader;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string DefaultIdentity = "Interstellar.Loader.Public.v1";
    private readonly Mutex ownership;
    private readonly MemoryMappedFile ownerProcess;
    private readonly EventWaitHandle activation;
    private readonly RegisteredWaitHandle listener;
    private int disposed;

    private SingleInstanceCoordinator(Mutex ownership, string identity, Action activationRequested)
    {
        this.ownership = ownership;
        MemoryMappedFile? processResource = null;
        EventWaitHandle? activationResource = null;
        try
        {
            processResource = MemoryMappedFile.CreateOrOpen($"Local\\{identity}.Owner", sizeof(int));
            using (var view = processResource.CreateViewAccessor(0, sizeof(int))) view.Write(0, unchecked((int)GetCurrentProcessId()));
            activationResource = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{identity}.Activate");
            listener = ThreadPool.RegisterWaitForSingleObject(activationResource, (_, _) =>
            {
                if (Volatile.Read(ref disposed) == 0) activationRequested();
            }, null, Timeout.Infinite, false);
            ownerProcess = processResource;
            activation = activationResource;
        }
        catch
        {
            activationResource?.Dispose();
            processResource?.Dispose();
            throw;
        }
    }

    internal static SingleInstanceCoordinator? AcquireOrActivate(Action activationRequested, string identity = DefaultIdentity, bool allowForeground = true)
    {
        var ownership = new Mutex(false, $"Local\\{identity}.Instance");
        var ownsMutex = false;
        try
        {
            var elapsed = Stopwatch.StartNew();
            do
            {
                try
                {
                    ownsMutex = ownership.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                if (ownsMutex) return new SingleInstanceCoordinator(ownership, identity, activationRequested);
                try
                {
                    using var ownerProcess = MemoryMappedFile.OpenExisting($"Local\\{identity}.Owner", MemoryMappedFileRights.Read);
                    using var view = ownerProcess.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Read);
                    var ownerId = view.ReadInt32(0);
                    using var activation = EventWaitHandle.OpenExisting($"Local\\{identity}.Activate");
                    if (allowForeground && ownerId > 0) AllowSetForegroundWindow((uint)ownerId);
                    activation.Set();
                    ownership.Dispose();
                    return null;
                }
                catch (FileNotFoundException)
                {
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                Thread.Sleep(15);
            } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
            ownership.Dispose();
            return null;
        }
        catch
        {
            if (ownsMutex) ownership.ReleaseMutex();
            ownership.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        listener.Unregister(null);
        activation.Dispose();
        ownerProcess.Dispose();
        ownership.ReleaseMutex();
        ownership.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
}
