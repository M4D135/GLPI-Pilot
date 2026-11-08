using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>One process per Windows user/session. The only accepted IPC command opens the existing window.</summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _disposed;
    public bool IsPrimary { get; }

    public SingleInstanceCoordinator()
    {
        var identity = Environment.UserDomainName + "\\" + Environment.UserName;
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        _mutex = new Mutex(false, "Local\\GLPIPilot_" + userKey);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        _pipeName = "GLPIPilot_" + userKey + "_" + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
    }

    public void Listen(Action activate, Action<Exception>? report = null)
    {
        if (!IsPrimary || _listener != null || _disposed) return;
        _listener = ListenAsync(activate, report);
    }

    private async Task ListenAsync(Action activate, Action<Exception>? report)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await WriteLine(server, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), deadline.Token).ConfigureAwait(false);
                    var command = await ReadLine(server, deadline.Token).ConfigureAwait(false);
                    if (command == "ACTIVATE")
                    {
                        activate();
                        await WriteLine(server, "OK", deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (!_stop.IsCancellationRequested) report?.Invoke(error); }
    }

    public async Task<bool> ActivateExistingAsync(TimeSpan? timeout = null)
    {
        if (IsPrimary || _disposed) return false;
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var identity = await ReadLine(client, deadline.Token).ConfigureAwait(false);
            if (!int.TryParse(identity, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0) return false;
            if (OperatingSystem.IsWindows()) _ = AllowSetForegroundWindow((uint)processId);
            await WriteLine(client, "ACTIVATE", deadline.Token).ConfigureAwait(false);
            return await ReadLine(client, deadline.Token).ConfigureAwait(false) == "OK";
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private static async Task<string> ReadLine(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1]; var text = new StringBuilder(32);
        while (text.Length < 32)
        {
            if (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) != 1) throw new IOException("Signal d’activation incomplet.");
            if (buffer[0] == '\n') return text.ToString();
            if (buffer[0] < 32 || buffer[0] > 126) throw new InvalidDataException("Signal d’activation invalide.");
            text.Append((char)buffer[0]);
        }
        throw new InvalidDataException("Signal d’activation trop long.");
    }

    private static async Task WriteLine(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _stop.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        // Release the token source after the asynchronous pipe wait has observed cancellation.
        if (_listener == null) _stop.Dispose();
        else _ = _listener.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
