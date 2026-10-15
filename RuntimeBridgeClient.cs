using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Interstellar.Loader;

internal sealed class BridgeReply
{
    public bool Available { get; internal set; }
    public bool Loaded { get; internal set; }
    public bool Loading { get; internal set; }
    public string MenuVersion { get; internal set; } = "";
    public string Error { get; internal set; } = "";
}

internal static class RuntimeBridgeClient
{
    private const int RequestTimeoutMilliseconds = 1500;
    private const int MaximumReplyBytes = 256;

    public static Task<BridgeReply> QueryAsync(int processId, CancellationToken cancellationToken = default) =>
        SendAsync(processId, "STATUS", cancellationToken);

    public static async Task<BridgeReply> LoadAsync(int processId, CancellationToken cancellationToken = default)
    {
        BridgeReply reply = await SendAsync(processId, "LOAD", cancellationToken).ConfigureAwait(false);
        int started = Environment.TickCount;
        while (reply.Available && reply.Loading && unchecked((uint)(Environment.TickCount - started)) < 20000)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            reply = await QueryAsync(processId, cancellationToken).ConfigureAwait(false);
        }
        if (reply.Loading && string.IsNullOrEmpty(reply.Error))
            reply.Error = "Interstellar is still starting. Refresh its status before trying again.";
        return reply;
    }

    private static async Task<BridgeReply> SendAsync(int processId, string command, CancellationToken cancellationToken)
    {
        if (processId <= 0) return new BridgeReply { Error = "No running Gorilla Tag process was selected." };
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeoutMilliseconds);
        using var pipe = new NamedPipeClientStream(".", "Interstellar.Loader." + processId,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var abort = timeout.Token.Register(() => pipe.Dispose());
        try
        {
            await pipe.ConnectAsync(RequestTimeoutMilliseconds, timeout.Token).ConfigureAwait(false);

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != (uint)processId)
                return new BridgeReply { Error = "The menu connection does not belong to the selected game." };

            byte[] request = Encoding.ASCII.GetBytes(command + "\n");
            await pipe.WriteAsync(request, 0, request.Length, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[MaximumReplyBytes];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await pipe.ReadAsync(buffer, length, buffer.Length - length, timeout.Token).ConfigureAwait(false);
                if (read == 0) return new BridgeReply { Error = "The menu connection closed before replying." };
                int end = length + read;
                for (int index = length; index < end; index++)
                {
                    if (buffer[index] == (byte)'\n') return ParseReply(Encoding.ASCII.GetString(buffer, 0, index));
                    if (buffer[index] < 32 || buffer[index] > 126)
                        return new BridgeReply { Error = "The menu returned an invalid response." };
                }
                length = end;
            }
            return new BridgeReply { Error = "The menu response exceeded its permitted size." };
        }
        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is UnauthorizedAccessException ||
                                   ex is OperationCanceledException || ex is ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BridgeReply { Error = "The running game has no responding Interstellar connection." };
        }
    }

    private static BridgeReply ParseReply(string reply)
    {
        int separator = reply.IndexOf('|');
        if (separator <= 0) return new BridgeReply { Error = "The menu returned an invalid response." };
        string state = reply.Substring(0, separator);
        string value = reply.Substring(separator + 1);
        if (state == "ERROR") return new BridgeReply { Available = true, Error = value };
        if ((state != "READY" && state != "LOADING" && state != "LOADED") || !Version.TryParse(value, out _))
            return new BridgeReply { Error = "The menu returned an invalid response." };
        return new BridgeReply
        {
            Available = true,
            Loaded = state == "LOADED",
            Loading = state == "LOADING",
            MenuVersion = value
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
