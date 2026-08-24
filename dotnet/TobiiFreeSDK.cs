using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Net.WebSockets;
using System.Text;

namespace TobiiFree.DotNet;

public readonly record struct TobiiVector2(double X, double Y);
public readonly record struct TobiiVector3(double X, double Y, double Z);
public readonly record struct TobiiDisplayArea(TobiiVector3 TopLeft, TobiiVector3 TopRight, TobiiVector3 BottomLeft);
public readonly record struct TobiiDisplayRect(double WidthMm, double HeightMm, double OffsetXMm, double OffsetYMm, double DepthMm);

public sealed class TobiiGazeSample
{
    public uint PresentMask { get; internal set; }
    public uint? FrameCounter { get; internal set; }
    public uint? LeftValidity { get; internal set; }
    public uint? RightValidity { get; internal set; }
    public long? TimestampMicroseconds { get; internal set; }
    public double? LeftPupilDiameterMm { get; internal set; }
    public double? RightPupilDiameterMm { get; internal set; }
    public TobiiVector2? GazePoint { get; internal set; }
    public TobiiVector2? LeftGazePoint { get; internal set; }
    public TobiiVector2? RightGazePoint { get; internal set; }
    public TobiiVector3? LeftEyeOriginMm { get; internal set; }
    public TobiiVector3? RightEyeOriginMm { get; internal set; }
    public TobiiVector3? LeftTrackboxPosition { get; internal set; }
    public TobiiVector3? RightTrackboxPosition { get; internal set; }
    public TobiiVector3? LeftGazePointMm { get; internal set; }
    public TobiiVector3? RightGazePointMm { get; internal set; }
    public TobiiVector3? LeftDisplayEyeOriginMm { get; internal set; }
    public TobiiVector3? RightDisplayEyeOriginMm { get; internal set; }
    public TobiiVector3? LeftDisplayTrackboxPosition { get; internal set; }
    public TobiiVector3? RightDisplayTrackboxPosition { get; internal set; }
    public TobiiVector3? LeftRawEyeOriginMm { get; internal set; }
    public TobiiVector3? RightRawEyeOriginMm { get; internal set; }
    public TobiiVector2? UnfilteredGazePoint { get; internal set; }
}

public interface ITobiiFreeSource : IAsyncDisposable
{
    event EventHandler<TobiiGazeSample>? GazeReceived;

    TobiiDisplayArea? DisplayArea { get; }

    Task<TobiiDisplayArea> GetDisplayAreaAsync(CancellationToken cancellationToken = default);
    Task SetDisplayAreaAsync(TobiiDisplayRect rect, CancellationToken cancellationToken = default);
    Task SetDisplayAreaCornersAsync(TobiiDisplayArea area, CancellationToken cancellationToken = default);
    Task StartCalibrationAsync(CancellationToken cancellationToken = default);
    Task AddCalibrationPointAsync(double x, double y, CancellationToken cancellationToken = default);
    Task<byte[]> FinishCalibrationAsync(CancellationToken cancellationToken = default);
    Task ApplyCalibrationAsync(ReadOnlyMemory<byte> calibrationBlob, CancellationToken cancellationToken = default);
}

public static class TobiiFreeSDK
{
    public static async Task<ITobiiFreeSource> ConnectUsbAsync(
        int vendorId = TobiiFreeDriver.DefaultVendorId,
        IReadOnlyList<int>? productIds = null,
        CancellationToken cancellationToken = default)
    {
        var driver = await TobiiFreeDriver.ConnectAsync(vendorId, productIds, cancellationToken).ConfigureAwait(false);
        return new TobiiUsbSource(driver);
    }

    public static async Task<ITobiiFreeSource> ConnectDaemonAsync(
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        var source = await TobiiDaemonSource.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return source;
    }

    private sealed class TobiiUsbSource : ITobiiFreeSource
    {
        private readonly TobiiFreeDriver _driver;

        public TobiiUsbSource(TobiiFreeDriver driver)
        {
            _driver = driver;
            _driver.GazeReceived += RelayGaze;
        }

        public event EventHandler<TobiiGazeSample>? GazeReceived;

        public TobiiDisplayArea? DisplayArea => _driver.DisplayArea;

        public Task<TobiiDisplayArea> GetDisplayAreaAsync(CancellationToken cancellationToken = default) =>
            _driver.GetDisplayAreaAsync(cancellationToken);

        public Task SetDisplayAreaAsync(TobiiDisplayRect rect, CancellationToken cancellationToken = default) =>
            _driver.SetDisplayAreaAsync(rect, cancellationToken);

        public Task SetDisplayAreaCornersAsync(TobiiDisplayArea area, CancellationToken cancellationToken = default) =>
            _driver.SetDisplayAreaCornersAsync(area, cancellationToken);

        public Task StartCalibrationAsync(CancellationToken cancellationToken = default) =>
            _driver.StartCalibrationAsync(cancellationToken);

        public Task AddCalibrationPointAsync(double x, double y, CancellationToken cancellationToken = default) =>
            _driver.AddCalibrationPointAsync(x, y, cancellationToken);

        public Task<byte[]> FinishCalibrationAsync(CancellationToken cancellationToken = default) =>
            _driver.FinishCalibrationAsync(cancellationToken);

        public Task ApplyCalibrationAsync(ReadOnlyMemory<byte> calibrationBlob, CancellationToken cancellationToken = default) =>
            _driver.ApplyCalibrationAsync(calibrationBlob, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _driver.GazeReceived -= RelayGaze;
            await _driver.DisposeAsync().ConfigureAwait(false);
        }

        private void RelayGaze(object? sender, TobiiGazeSample sample) => GazeReceived?.Invoke(this, sample);
    }

    private sealed class TobiiDaemonSource : ITobiiFreeSource
    {
        private const int HeaderSize = 5;
        private const int GazePayloadSize = 392;

        private static class Command
        {
            public const byte Subscribe = 0x01;
            public const byte GetDisplayArea = 0x02;
            public const byte SetDisplayArea = 0x03;
            public const byte SetDisplayAreaCorners = 0x04;
            public const byte StartCalibration = 0x20;
            public const byte AddCalibrationPoint = 0x21;
            public const byte FinishCalibration = 0x22;
            public const byte ApplyCalibration = 0x23;
        }

        private static class ServerMessage
        {
            public const byte Gaze = 0x01;
            public const byte Response = 0x02;
            public const byte DisplayArea = 0x03;
            public const byte Error = 0xFF;
        }

        private readonly ClientWebSocket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _pending = new();
        private readonly Task _receiveLoop;
        private TobiiDaemonSource(ClientWebSocket socket)
        {
            _socket = socket;
            _receiveLoop = ReceiveLoopAsync(_cts.Token);
        }

        public event EventHandler<TobiiGazeSample>? GazeReceived;

        public TobiiDisplayArea? DisplayArea { get; private set; }

        public static async Task<TobiiDaemonSource> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var source = new TobiiDaemonSource(socket);
            await source.SendAsync(Command.Subscribe, null, cancellationToken).ConfigureAwait(false);
            source.DisplayArea = await source.GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
            return source;
        }

        public async Task<TobiiDisplayArea> GetDisplayAreaAsync(CancellationToken cancellationToken = default)
        {
            var payload = await RequestAsync(Command.GetDisplayArea, null, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var area = TobiiFreeDriver.DecodeDisplayAreaPayload(payload);
            DisplayArea = area;
            return area;
        }

        public async Task SetDisplayAreaAsync(TobiiDisplayRect rect, CancellationToken cancellationToken = default)
        {
            await SendAsync(Command.SetDisplayArea, EncodeF64s(rect.WidthMm, rect.HeightMm, rect.OffsetXMm, rect.OffsetYMm, rect.DepthMm), cancellationToken).ConfigureAwait(false);
            DisplayArea = await GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SetDisplayAreaCornersAsync(TobiiDisplayArea area, CancellationToken cancellationToken = default)
        {
            await SendAsync(Command.SetDisplayAreaCorners, EncodeF64s(
                area.TopLeft.X, area.TopLeft.Y, area.TopLeft.Z,
                area.TopRight.X, area.TopRight.Y, area.TopRight.Z,
                area.BottomLeft.X, area.BottomLeft.Y, area.BottomLeft.Z), cancellationToken).ConfigureAwait(false);
            DisplayArea = await GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task StartCalibrationAsync(CancellationToken cancellationToken = default) =>
            _ = await RequestAsync(Command.StartCalibration, null, cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        public async Task AddCalibrationPointAsync(double x, double y, CancellationToken cancellationToken = default) =>
            _ = await RequestAsync(Command.AddCalibrationPoint, EncodeF64s(x, y), cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        public Task<byte[]> FinishCalibrationAsync(CancellationToken cancellationToken = default) =>
            RequestAsync(Command.FinishCalibration, null, cancellationToken, TimeSpan.FromSeconds(30));

        public async Task ApplyCalibrationAsync(ReadOnlyMemory<byte> calibrationBlob, CancellationToken cancellationToken = default) =>
            _ = await RequestAsync(Command.ApplyCalibration, calibrationBlob, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetCanceled(_cts.Token);
            }

            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            _socket.Dispose();
            _cts.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var receiveBuffer = new List<byte>();

            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException exception)
                {
                    FailPending(exception);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    FailPending(new IOException("The TobiiFree daemon closed the WebSocket connection."));
                    break;
                }

                receiveBuffer.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                if (!result.EndOfMessage)
                {
                    continue;
                }

                ParseMessages(receiveBuffer);
                receiveBuffer.Clear();
            }
        }

        private void ParseMessages(List<byte> receiveBuffer)
        {
            var offset = 0;
            while (offset + HeaderSize <= receiveBuffer.Count)
            {
                var messageType = receiveBuffer[offset];
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(CollectionsMarshal.AsSpan(receiveBuffer).Slice(offset + 1, 4));
                var totalLength = checked((int)payloadLength + HeaderSize);
                if (offset + totalLength > receiveBuffer.Count)
                {
                    break;
                }

                var payload = CollectionsMarshal.AsSpan(receiveBuffer).Slice(offset + HeaderSize, (int)payloadLength).ToArray();
                Dispatch(messageType, payload);
                offset += totalLength;
            }
        }

        private void Dispatch(byte messageType, byte[] payload)
        {
            switch (messageType)
            {
                case ServerMessage.Gaze:
                    if (payload.Length >= GazePayloadSize)
                    {
                        GazeReceived?.Invoke(this, TobiiFreeDriver.DecodeDaemonGazeSample(payload));
                    }
                    break;
                case ServerMessage.Response:
                    if (payload.Length > 0 && _pending.TryRemove(payload[0], out var response))
                    {
                        response.TrySetResult(payload[1..]);
                    }
                    break;
                case ServerMessage.DisplayArea:
                    if (_pending.TryRemove(Command.GetDisplayArea, out var displayArea))
                    {
                        displayArea.TrySetResult(payload);
                    }
                    break;
                case ServerMessage.Error:
                    var errorCode = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0U;
                    FailPending(new IOException($"Daemon reported error 0x{errorCode:x}."));
                    break;
            }
        }

        private async Task SendAsync(byte commandType, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken)
        {
            var payloadLength = payload?.Length ?? 0;
            var message = new byte[HeaderSize + payloadLength];
            message[0] = commandType;
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1, 4), (uint)payloadLength);
            if (payload is { Length: > 0 } body)
            {
                body.Span.CopyTo(message.AsSpan(HeaderSize));
            }

            await _socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte[]> RequestAsync(byte commandType, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(commandType, completion))
            {
                throw new InvalidOperationException($"Command 0x{commandType:x2} is already in flight.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            timeoutCts.CancelAfter(timeout);
            using var registration = timeoutCts.Token.Register(() =>
            {
                if (_pending.TryRemove(commandType, out var pending))
                {
                    pending.TrySetCanceled(timeoutCts.Token);
                }
            });

            await SendAsync(commandType, payload, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }

        private void FailPending(Exception exception)
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var pending))
                {
                    pending.TrySetException(exception);
                }
            }
        }

        private static byte[] EncodeF64s(params double[] values)
        {
            var payload = new byte[values.Length * sizeof(double)];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(i * sizeof(double), sizeof(double)), BitConverter.DoubleToInt64Bits(values[i]));
            }

            return payload;
        }
    }
}
