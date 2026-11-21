using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PulseGuard.Modules.Discord;

internal enum DiscordIpcOpcode : int
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4
}

/// <summary>
/// Lightweight local Discord RPC IPC client over Windows named pipes (\\.\pipe\discord-ipc-0..9).
/// </summary>
internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int MaxPipeSlots = 10;
    private const int HeaderSize = 8;
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new(StringComparer.Ordinal);

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _disposed;

    public bool IsConnected => _pipe is { IsConnected: true };

    public event Action<string, JsonElement>? DispatchReceived;
    public event Action<string>? Disconnected;

    /// <summary>
    /// Checks without throwing whether any local Discord IPC pipe is currently open.
    /// </summary>
    public static bool HasActiveDiscordPipe()
    {
        try
        {
            foreach (string _ in Directory.EnumerateFiles(@"\\.\pipe\", "discord-ipc-*"))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    public async Task<bool> ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await DisconnectInternalAsync(notify: false).ConfigureAwait(false);

        for (int i = 0; i < MaxPipeSlots; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NamedPipeClientStream? candidate = null;
            try
            {
                candidate = new NamedPipeClientStream(
                    ".",
                    $"discord-ipc-{i}",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await candidate.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                _pipe = candidate;
                break;
            }
            catch (OperationCanceledException)
            {
                candidate?.Dispose();
                throw;
            }
            catch
            {
                candidate?.Dispose();
            }
        }

        if (_pipe is null || !_pipe.IsConnected)
        {
            return false;
        }

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReadyDispatch(string evt, JsonElement _)
        {
            if (string.Equals(evt, "READY", StringComparison.Ordinal))
            {
                readyTcs.TrySetResult(true);
            }
        }

        DispatchReceived += OnReadyDispatch;
        try
        {
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

            string handshakeJson = JsonSerializer.Serialize(new
            {
                v = 1,
                client_id = clientId
            });

            await WriteFrameAsync(DiscordIpcOpcode.Handshake, handshakeJson, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var reg = timeoutCts.Token.Register(() => readyTcs.TrySetCanceled(timeoutCts.Token));

            await readyTcs.Task.ConfigureAwait(false);
            return true;
        }
        catch
        {
            await DisconnectInternalAsync(notify: false).ConfigureAwait(false);
            return false;
        }
        finally
        {
            DispatchReceived -= OnReadyDispatch;
        }
    }

    public async Task<JsonElement?> SendCommandAsync(
        object payloadWithoutNonce,
        string nonce,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return null;
        }

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[nonce] = tcs;

        try
        {
            string json = JsonSerializer.Serialize(payloadWithoutNonce);
            await WriteFrameAsync(DiscordIpcOpcode.Frame, json, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(nonce, out _);
        }
    }

    private async Task WriteFrameAsync(DiscordIpcOpcode opcode, string json, CancellationToken cancellationToken)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
        int totalLength = HeaderSize + payloadBytes.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), (int)opcode);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), payloadBytes.Length);
            payloadBytes.CopyTo(buffer, HeaderSize);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pipe = _pipe;
                if (pipe is null || !pipe.IsConnected)
                {
                    throw new IOException("Discord IPC pipe is not connected.");
                }

                await pipe.WriteAsync(buffer.AsMemory(0, totalLength), cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] headerBuffer = new byte[HeaderSize];
        string disconnectReason = "Соединение с Discord закрыто.";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = _pipe;
                if (pipe is null || !pipe.IsConnected)
                {
                    break;
                }

                await pipe.ReadExactlyAsync(headerBuffer.AsMemory(0, HeaderSize), cancellationToken).ConfigureAwait(false);

                var opcode = (DiscordIpcOpcode)BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(0, 4));
                int length = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(4, 4));

                if (length < 0 || length > MaxPayloadBytes)
                {
                    disconnectReason = "Некорректный размер пакета Discord IPC.";
                    break;
                }

                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (length > 0)
                    {
                        await pipe.ReadExactlyAsync(payloadBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    }

                    switch (opcode)
                    {
                        case DiscordIpcOpcode.Ping:
                            string pingPayload = length > 0 ? Encoding.UTF8.GetString(payloadBuffer, 0, length) : "{}";
                            await WriteFrameAsync(DiscordIpcOpcode.Pong, pingPayload, cancellationToken).ConfigureAwait(false);
                            break;

                        case DiscordIpcOpcode.Close:
                            disconnectReason = "Discord закрыл IPC-канал.";
                            return;

                        case DiscordIpcOpcode.Frame when length > 0:
                            ProcessFrame(payloadBuffer.AsSpan(0, length));
                            break;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }
            }
        }
        catch (EndOfStreamException)
        {
            disconnectReason = "Discord отключился.";
        }
        catch (IOException)
        {
            disconnectReason = "Связь с Discord прервана.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            FailAllPendingRequests();
            if (!cancellationToken.IsCancellationRequested)
            {
                Disconnected?.Invoke(disconnectReason);
            }
        }
    }

    private void ProcessFrame(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            var root = doc.RootElement.Clone();

            string? nonce = root.TryGetProperty("nonce", out var nonceProp) && nonceProp.ValueKind == JsonValueKind.String
                ? nonceProp.GetString()
                : null;

            if (!string.IsNullOrEmpty(nonce) && _pendingRequests.TryRemove(nonce, out var tcs))
            {
                tcs.TrySetResult(root);
                return;
            }

            if (root.TryGetProperty("cmd", out var cmdProp)
                && string.Equals(cmdProp.GetString(), "DISPATCH", StringComparison.Ordinal)
                && root.TryGetProperty("evt", out var evtProp)
                && evtProp.ValueKind == JsonValueKind.String)
            {
                string evt = evtProp.GetString() ?? string.Empty;
                DispatchReceived?.Invoke(evt, root);
            }
        }
        catch
        {
            // Ignore malformed JSON frames.
        }
    }

    private void FailAllPendingRequests()
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private async Task DisconnectInternalAsync(bool notify)
    {
        _readCts?.Cancel();

        var pipe = _pipe;
        _pipe = null;
        if (pipe is not null)
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore pipe disposal errors.
            }
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore read loop cancellation.
            }

            _readTask = null;
        }

        _readCts?.Dispose();
        _readCts = null;
        FailAllPendingRequests();

        if (notify)
        {
            Disconnected?.Invoke("Отключено от Discord.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectInternalAsync(notify: false).ConfigureAwait(false);
        _writeLock.Dispose();
    }
}

