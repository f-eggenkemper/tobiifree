using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TobiiFree.DotNet;

public sealed class TobiiFreeDriver : IAsyncDisposable
{
    public const int DefaultVendorId = 0x2104;
    public static readonly IReadOnlyList<int> DefaultProductIds = new[] { 0x0313, 0x0127 };

    private const byte EndpointIn = 0x83;
    private const byte EndpointOut = 0x05;
    private const byte InterfaceNumber = 0;
    private const ushort SessionOpenRequest = 0x41;
    private const ushort SessionCloseRequest = 0x42;
    private const uint TtpMagicRequest = 0x51;
    private const uint TtpMagicResponse = 0x52;
    private const uint TtpMagicNotify = 0x53;
    private const uint OpHello = 0x3E8;
    private const uint OpSubscribe = 0x4C4;
    private const uint OpGetDisplayArea = 0x596;
    private const uint OpSetDisplayArea = 0x5A0;
    private const uint OpQueryRealm = 0x640;
    private const uint OpOpenRealm = 0x76C;
    private const uint OpRealmResponse = 0x776;
    private const uint OpCloseRealm = 0x77B;
    private const uint OpCalibrationAddPoint = 0x408;
    private const uint OpCalibrationCompute = 0x42F;
    private const uint OpCalibrationRetrieve = 0x44C;
    private const uint OpCalibrationApply = 0x456;
    private const ushort GazeStream = 0x0500;
    private static readonly byte[] HelloPayload =
    {
        0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x09,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x02,
        0x00, 0x01, 0x00, 0x03, 0x00, 0x01, 0x00, 0x04, 0x00, 0x01, 0x00, 0x05,
        0x00, 0x01, 0x00, 0x06, 0x00, 0x01, 0x00, 0x07, 0x00, 0x01, 0x00, 0x08,
    };
    private static readonly byte[] SubscribePayloadTemplate =
    {
        0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x00, 0x17, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00,
    };
    private static readonly byte[] RealmKey = Encoding.ASCII.GetBytes("IS2LJC6GIRBBEK2K\0");

    private readonly LibUsbTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly List<byte> _incoming = new();
    private readonly object _incomingGate = new();
    private readonly object _sequenceGate = new();
    private readonly Task _receiveLoop;
    private uint _nextSequence = 1;
    private uint? _calibrationRealmId;

    private TobiiFreeDriver(LibUsbTransport transport)
    {
        _transport = transport;
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
    }

    public event EventHandler<TobiiGazeSample>? GazeReceived;

    public TobiiDisplayArea? DisplayArea { get; private set; }

    public static async Task<TobiiFreeDriver> ConnectAsync(
        int vendorId = DefaultVendorId,
        IReadOnlyList<int>? productIds = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await LibUsbTransport.OpenAsync(vendorId, productIds ?? DefaultProductIds, cancellationToken).ConfigureAwait(false);
        var driver = new TobiiFreeDriver(transport);

        try
        {
            await driver.InitializeAsync(cancellationToken).ConfigureAwait(false);
            driver.DisplayArea = await driver.GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TobiiDisplayArea> GetDisplayAreaAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RoundTripAsync(OpGetDisplayArea, TwoBytePrefix(), cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var area = DecodeDisplayAreaPayload(payload);
        DisplayArea = area;
        return area;
    }

    public async Task SetDisplayAreaAsync(TobiiDisplayRect rect, CancellationToken cancellationToken = default)
    {
        var payload = BuildDisplayAreaPayload(
            new TobiiDisplayArea(
                new TobiiVector3(rect.OffsetXMm, rect.OffsetYMm + rect.HeightMm, rect.DepthMm),
                new TobiiVector3(rect.OffsetXMm + rect.WidthMm, rect.OffsetYMm + rect.HeightMm, rect.DepthMm),
                new TobiiVector3(rect.OffsetXMm, rect.OffsetYMm, rect.DepthMm)));
        await SendOnlyAsync(OpSetDisplayArea, payload, cancellationToken).ConfigureAwait(false);
        DisplayArea = await GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDisplayAreaCornersAsync(TobiiDisplayArea area, CancellationToken cancellationToken = default)
    {
        await SendOnlyAsync(OpSetDisplayArea, BuildDisplayAreaPayload(area), cancellationToken).ConfigureAwait(false);
        DisplayArea = await GetDisplayAreaAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartCalibrationAsync(CancellationToken cancellationToken = default)
    {
        _calibrationRealmId = await OpenCalibrationRealmAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddCalibrationPointAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        if (_calibrationRealmId is null)
        {
            throw new InvalidOperationException("Calibration has not been started.");
        }

        var payload = new ArrayBufferWriter<byte>();
        payload.Write(TwoBytePrefix());
        payload.WriteTlvQ42(x);
        payload.WriteTlvQ42(y);
        payload.WriteTlvU32(0);
        _ = await RoundTripAsync(OpCalibrationAddPoint, payload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public async Task<byte[]> FinishCalibrationAsync(CancellationToken cancellationToken = default)
    {
        if (_calibrationRealmId is null)
        {
            throw new InvalidOperationException("Calibration has not been started.");
        }

        try
        {
            _ = await RoundTripAsync(OpCalibrationCompute, TwoBytePrefix(), cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return await RoundTripAsync(OpCalibrationRetrieve, TwoBytePrefix(), cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        finally
        {
            await CloseCalibrationRealmAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ApplyCalibrationAsync(ReadOnlyMemory<byte> calibrationBlob, CancellationToken cancellationToken = default)
    {
        if (_calibrationRealmId is not null)
        {
            throw new InvalidOperationException("Finish or close the active calibration session before applying a saved calibration blob.");
        }

        await OpenCalibrationRealmAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = new byte[2 + calibrationBlob.Length];
            payload[0] = 0;
            payload[1] = 0;
            calibrationBlob.Span.CopyTo(payload.AsSpan(2));
            _ = await RoundTripAsync(OpCalibrationApply, payload, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        finally
        {
            await CloseCalibrationRealmAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled(_cts.Token);
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    internal static TobiiDisplayArea DecodeDisplayAreaPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new InvalidDataException("Display-area payload was truncated.");
        }

        var reader = new TlvReader(payload[2..]);
        return new TobiiDisplayArea(reader.ReadPoint3D(), reader.ReadPoint3D(), reader.ReadPoint3D());
    }

    internal static TobiiGazeSample DecodeDaemonGazeSample(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 392)
        {
            throw new InvalidDataException("Daemon gaze payload was truncated.");
        }

        return DecodeStructuredGazeSample(payload);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _ = await RoundTripAsync(OpHello, HelloPayload, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        var realmInfo = await RoundTripAsync(OpQueryRealm, TwoBytePrefix(), cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var realmType = ReadFirstTlvU32(realmInfo);
        var openRealmPayload = new ArrayBufferWriter<byte>();
        openRealmPayload.Write(TwoBytePrefix());
        openRealmPayload.WriteTlvU32(realmType);
        openRealmPayload.WriteByte(0);

        var openRealmResponse = await RoundTripAsync(OpOpenRealm, openRealmPayload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (realmType != 0)
        {
            var realmId = ReadTlvU32At(openRealmResponse, 0);
            var field210 = ReadTlvU32At(openRealmResponse, 1);
            var challenge = ExtractChallenge(openRealmResponse);
            var digest = HMACMD5.HashData(RealmKey, challenge);

            var authPayload = new ArrayBufferWriter<byte>();
            authPayload.Write(TwoBytePrefix());
            authPayload.WriteTlvU32(realmId);
            authPayload.WriteTlvU32(field210);
            authPayload.Write(digest);
            _ = await RoundTripAsync(OpRealmResponse, authPayload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        await SendOnlyAsync(OpSubscribe, BuildSubscribePayload(GazeStream), cancellationToken).ConfigureAwait(false);
    }

    private async Task<uint> OpenCalibrationRealmAsync(CancellationToken cancellationToken)
    {
        var realmInfo = await RoundTripAsync(OpQueryRealm, TwoBytePrefix(), cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var realmType = ReadFirstTlvU32(realmInfo);

        var openPayload = new ArrayBufferWriter<byte>();
        openPayload.Write(TwoBytePrefix());
        openPayload.WriteTlvU32(realmType);
        openPayload.WriteByte(0);
        var response = await RoundTripAsync(OpOpenRealm, openPayload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        uint realmId;
        if (realmType == 0)
        {
            realmId = ReadFirstTlvU32(response);
        }
        else
        {
            realmId = ReadTlvU32At(response, 0);
            var field210 = ReadTlvU32At(response, 1);
            var challenge = ExtractChallenge(response);
            var digest = HMACMD5.HashData(RealmKey, challenge);

            var authPayload = new ArrayBufferWriter<byte>();
            authPayload.Write(TwoBytePrefix());
            authPayload.WriteTlvU32(realmId);
            authPayload.WriteTlvU32(field210);
            authPayload.Write(digest);
            _ = await RoundTripAsync(OpRealmResponse, authPayload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        _calibrationRealmId = realmId;
        return realmId;
    }

    private async Task CloseCalibrationRealmAsync(CancellationToken cancellationToken)
    {
        if (_calibrationRealmId is not { } realmId)
        {
            return;
        }

        try
        {
            var payload = new ArrayBufferWriter<byte>();
            payload.Write(TwoBytePrefix());
            payload.WriteTlvU32(realmId);
            _ = await RoundTripAsync(OpCloseRealm, payload.WrittenMemory, cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            _calibrationRealmId = null;
        }
    }

    private async Task<byte[]> RoundTripAsync(uint operation, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, TimeSpan timeout)
    {
        var sequence = NextSequence();
        var frame = BuildOutboundFrame(sequence, operation, payload);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion))
        {
            throw new InvalidOperationException($"Sequence {sequence} is already in flight.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(sequence, out var pending))
            {
                pending.TrySetCanceled(timeoutCts.Token);
            }
        });

        await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    private Task SendOnlyAsync(uint operation, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var frame = BuildOutboundFrame(sequence, operation, payload);
        return _transport.SendAsync(frame, cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] chunk;
            try
            {
                chunk = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                FailPending(exception);
                break;
            }

            List<TobiiGazeSample> gazeSamples;
            lock (_incomingGate)
            {
                _incoming.AddRange(chunk);
                gazeSamples = ParseIncomingFrames();
            }

            foreach (var sample in gazeSamples)
            {
                GazeReceived?.Invoke(this, sample);
            }
        }
    }

    private List<TobiiGazeSample> ParseIncomingFrames()
    {
        var gazeSamples = new List<TobiiGazeSample>();
        while (_incoming.Count >= 8)
        {
            var span = CollectionsMarshal.AsSpan(_incoming);
            var envelopeLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
            if (envelopeLength < 32)
            {
                throw new InvalidDataException($"Invalid inbound envelope length: {envelopeLength}.");
            }

            if (_incoming.Count < envelopeLength)
            {
                return gazeSamples;
            }

            var frame = span.Slice(0, envelopeLength).ToArray();
            _incoming.RemoveRange(0, envelopeLength);
            if (DispatchInboundFrame(frame) is { } sample)
            {
                gazeSamples.Add(sample);
            }
        }

        return gazeSamples;
    }

    private TobiiGazeSample? DispatchInboundFrame(ReadOnlySpan<byte> frame)
    {
        var header = frame.Slice(8, 24);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4));
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        var operation = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4));
        var payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        var payload = frame.Slice(32, payloadLength);

        if (magic == TtpMagicResponse)
        {
            if (_pending.TryRemove(sequence, out var pending))
            {
                pending.TrySetResult(payload.ToArray());
            }

            return null;
        }

        if (magic == TtpMagicNotify && operation == GazeStream)
        {
            return DecodeTlvGazeSample(payload);
        }

        return null;
    }

    private void FailPending(Exception exception)
    {
        foreach (var sequence in _pending.Keys)
        {
            if (_pending.TryRemove(sequence, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private uint NextSequence()
    {
        lock (_sequenceGate)
        {
            var next = _nextSequence;
            _nextSequence = _nextSequence == uint.MaxValue ? 1 : _nextSequence + 1;
            return next;
        }
    }

    private static byte[] TwoBytePrefix() => [0, 0];

    private static byte[] BuildOutboundFrame(uint sequence, uint operation, ReadOnlyMemory<byte> payload)
    {
        var frame = new byte[8 + 24 + payload.Length];
        frame[0] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)(24 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), TtpMagicRequest);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), operation);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(28, 4), (uint)payload.Length);
        payload.Span.CopyTo(frame.AsSpan(32));
        return frame;
    }

    private static byte[] BuildDisplayAreaPayload(TobiiDisplayArea area)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(TwoBytePrefix());
        writer.WriteTlvPoint3D(area.TopLeft);
        writer.WriteTlvPoint3D(area.TopRight);
        writer.WriteTlvPoint3D(area.BottomLeft);
        writer.WriteTlvTag(0x10100);
        writer.WriteTlvU32(0x3039);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildSubscribePayload(ushort streamId)
    {
        var payload = SubscribePayloadTemplate.ToArray();
        payload[9] = (byte)(streamId >> 8);
        payload[10] = (byte)streamId;
        return payload;
    }

    private static uint ReadFirstTlvU32(ReadOnlySpan<byte> payload)
    {
        var reader = new FlatTlvReader(payload);
        return reader.ReadFirstU32();
    }

    private static uint ReadTlvU32At(ReadOnlySpan<byte> payload, int index)
    {
        var reader = new FlatTlvReader(payload);
        return reader.ReadU32At(index);
    }

    private static byte[] ExtractChallenge(ReadOnlySpan<byte> payload)
    {
        var reader = new FlatTlvReader(payload);
        return reader.ExtractFirstBlobLongerThanFourBytes();
    }

    private static TobiiGazeSample DecodeTlvGazeSample(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new InvalidDataException("Gaze payload was truncated.");
        }

        var sample = new TobiiGazeSample();
        var reader = new TlvReader(payload[2..]);
        var columnCount = reader.ReadXdsRow();
        for (var i = 0; i < columnCount && reader.HasRemaining; i++)
        {
            var columnId = reader.ReadXdsColumn();
            switch (columnId)
            {
                case 0x01:
                    sample.TimestampMicroseconds = reader.ReadInt64();
                    sample.PresentMask |= 1u << 0;
                    break;
                case 0x02:
                    sample.LeftEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 9;
                    break;
                case 0x03:
                    sample.LeftTrackboxPosition = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 11;
                    break;
                case 0x04:
                    sample.LeftGazePointMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 13;
                    break;
                case 0x05:
                    sample.LeftGazePoint = reader.ReadPoint2D();
                    sample.PresentMask |= 1u << 7;
                    break;
                case 0x06:
                    sample.LeftPupilDiameterMm = reader.ReadFixed16x16();
                    sample.PresentMask |= 1u << 4;
                    break;
                case 0x07:
                    sample.LeftValidity = reader.ReadUInt32();
                    sample.PresentMask |= 1u << 2;
                    break;
                case 0x08:
                    sample.RightEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 10;
                    break;
                case 0x09:
                    sample.RightTrackboxPosition = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 12;
                    break;
                case 0x0A:
                    sample.RightGazePointMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 14;
                    break;
                case 0x0B:
                    sample.RightGazePoint = reader.ReadPoint2D();
                    sample.PresentMask |= 1u << 8;
                    break;
                case 0x0C:
                    sample.RightPupilDiameterMm = reader.ReadFixed16x16();
                    sample.PresentMask |= 1u << 5;
                    break;
                case 0x0D:
                    sample.RightValidity = reader.ReadUInt32();
                    sample.PresentMask |= 1u << 3;
                    break;
                case 0x14:
                    sample.FrameCounter = reader.ReadUInt32();
                    sample.PresentMask |= 1u << 1;
                    break;
                case 0x17:
                    sample.LeftRawEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 19;
                    break;
                case 0x18:
                    sample.RightRawEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 20;
                    break;
                case 0x1C:
                    sample.GazePoint = reader.ReadPoint2D();
                    sample.PresentMask |= 1u << 6;
                    break;
                case 0x20:
                    sample.UnfilteredGazePoint = reader.ReadPoint2D();
                    sample.PresentMask |= 1u << 21;
                    break;
                case 0x22:
                    sample.LeftDisplayEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 15;
                    break;
                case 0x24:
                    sample.RightDisplayEyeOriginMm = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 16;
                    break;
                case 0x25:
                    sample.LeftDisplayTrackboxPosition = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 17;
                    break;
                case 0x27:
                    sample.RightDisplayTrackboxPosition = reader.ReadPoint3D();
                    sample.PresentMask |= 1u << 18;
                    break;
                default:
                    reader.SkipByColumnKind(columnId);
                    break;
            }
        }

        return sample;
    }

    private static TobiiGazeSample DecodeStructuredGazeSample(ReadOnlySpan<byte> payload)
    {
        var sample = new TobiiGazeSample
        {
            PresentMask = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4)),
        };

        if ((sample.PresentMask & (1u << 0)) != 0) sample.TimestampMicroseconds = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(16, 8));
        if ((sample.PresentMask & (1u << 1)) != 0) sample.FrameCounter = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        if ((sample.PresentMask & (1u << 2)) != 0) sample.LeftValidity = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        if ((sample.PresentMask & (1u << 3)) != 0) sample.RightValidity = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        if ((sample.PresentMask & (1u << 4)) != 0) sample.LeftPupilDiameterMm = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(24, 8)));
        if ((sample.PresentMask & (1u << 5)) != 0) sample.RightPupilDiameterMm = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(32, 8)));
        if ((sample.PresentMask & (1u << 6)) != 0) sample.GazePoint = ReadVector2(payload, 40);
        if ((sample.PresentMask & (1u << 7)) != 0) sample.LeftGazePoint = ReadVector2(payload, 56);
        if ((sample.PresentMask & (1u << 8)) != 0) sample.RightGazePoint = ReadVector2(payload, 72);
        if ((sample.PresentMask & (1u << 9)) != 0) sample.LeftEyeOriginMm = ReadVector3(payload, 88);
        if ((sample.PresentMask & (1u << 10)) != 0) sample.RightEyeOriginMm = ReadVector3(payload, 112);
        if ((sample.PresentMask & (1u << 11)) != 0) sample.LeftTrackboxPosition = ReadVector3(payload, 136);
        if ((sample.PresentMask & (1u << 12)) != 0) sample.RightTrackboxPosition = ReadVector3(payload, 160);
        if ((sample.PresentMask & (1u << 13)) != 0) sample.LeftGazePointMm = ReadVector3(payload, 184);
        if ((sample.PresentMask & (1u << 14)) != 0) sample.RightGazePointMm = ReadVector3(payload, 208);
        if ((sample.PresentMask & (1u << 15)) != 0) sample.LeftDisplayEyeOriginMm = ReadVector3(payload, 232);
        if ((sample.PresentMask & (1u << 16)) != 0) sample.RightDisplayEyeOriginMm = ReadVector3(payload, 256);
        if ((sample.PresentMask & (1u << 17)) != 0) sample.LeftDisplayTrackboxPosition = ReadVector3(payload, 280);
        if ((sample.PresentMask & (1u << 18)) != 0) sample.RightDisplayTrackboxPosition = ReadVector3(payload, 304);
        if ((sample.PresentMask & (1u << 19)) != 0) sample.LeftRawEyeOriginMm = ReadVector3(payload, 328);
        if ((sample.PresentMask & (1u << 20)) != 0) sample.RightRawEyeOriginMm = ReadVector3(payload, 352);
        if ((sample.PresentMask & (1u << 21)) != 0) sample.UnfilteredGazePoint = ReadVector2(payload, 376);
        return sample;
    }

    private static TobiiVector2 ReadVector2(ReadOnlySpan<byte> payload, int offset) =>
        new(
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8))),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset + 8, 8))));

    private static TobiiVector3 ReadVector3(ReadOnlySpan<byte> payload, int offset) =>
        new(
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8))),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset + 8, 8))),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset + 16, 8))));

    private ref struct FlatTlvReader
    {
        private ReadOnlySpan<byte> _payload;

        public FlatTlvReader(ReadOnlySpan<byte> payload)
        {
            _payload = payload;
        }

        public uint ReadFirstU32()
        {
            var offset = 2;
            while (offset + 5 <= _payload.Length)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(offset + 1, 4));
                offset += 5;
                if (size == 4 && offset + 4 <= _payload.Length)
                {
                    return BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(offset, 4));
                }

                offset += (int)size;
            }

            return 0;
        }

        public uint ReadU32At(int index)
        {
            var offset = 2;
            var found = 0;
            while (offset + 5 <= _payload.Length)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(offset + 1, 4));
                offset += 5;
                if (size == 4 && offset + 4 <= _payload.Length)
                {
                    if (found == index)
                    {
                        return BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(offset, 4));
                    }

                    found++;
                }

                offset += (int)size;
            }

            return 0;
        }

        public byte[] ExtractFirstBlobLongerThanFourBytes()
        {
            var offset = 2;
            while (offset + 5 <= _payload.Length)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(offset + 1, 4));
                offset += 5;
                if (size > 4 && offset + size <= _payload.Length)
                {
                    return _payload.Slice(offset, (int)size).ToArray();
                }

                offset += (int)size;
            }

            throw new InvalidDataException("Realm challenge was missing from the response.");
        }
    }

    private ref struct TlvReader
    {
        private ReadOnlySpan<byte> _buffer;
        private int _offset;

        public TlvReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public bool HasRemaining => _offset < _buffer.Length;

        public uint ReadXdsRow()
        {
            var tag = ReadPrologTag();
            if ((tag & 0xFFFF) != 0x0BB8)
            {
                throw new InvalidDataException($"Unexpected XDS row tag 0x{tag:x}.");
            }

            return (tag >> 16) & 0x0FFF;
        }

        public uint ReadXdsColumn()
        {
            var tag = ReadPrologTag();
            if (tag != 0x020BB9)
            {
                throw new InvalidDataException($"Unexpected XDS column tag 0x{tag:x}.");
            }

            return ReadUInt32();
        }

        public uint ReadUInt32()
        {
            EnsureFieldHeader(2, 4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public long ReadInt64()
        {
            EnsureFieldHeader(6, 8);
            var value = BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_offset, 8));
            _offset += 8;
            return value;
        }

        public double ReadFixed16x16()
        {
            EnsureFieldHeader(3, 4);
            var raw = BinaryPrimitives.ReadInt32BigEndian(_buffer.Slice(_offset, 4));
            _offset += 4;
            return raw / 65536d;
        }

        public double ReadQ42()
        {
            EnsureFieldHeader(4, 8);
            var raw = BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_offset, 8));
            _offset += 8;
            return raw / 4398046511104d;
        }

        public TobiiVector2 ReadPoint2D()
        {
            var tag = ReadPrologTag();
            if (tag != 0x021F40)
            {
                throw new InvalidDataException($"Unexpected point2d tag 0x{tag:x}.");
            }

            return new TobiiVector2(ReadQ42(), ReadQ42());
        }

        public TobiiVector3 ReadPoint3D()
        {
            var tag = ReadPrologTag();
            if (tag != 0x031F41)
            {
                throw new InvalidDataException($"Unexpected point3d tag 0x{tag:x}.");
            }

            return new TobiiVector3(ReadQ42(), ReadQ42(), ReadQ42());
        }

        public void SkipByColumnKind(uint columnId)
        {
            switch (columnId)
            {
                case 0x01:
                    _ = ReadInt64();
                    break;
                case 0x06:
                case 0x0C:
                case 0x29:
                case 0x2B:
                    _ = ReadFixed16x16();
                    break;
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x17:
                case 0x18:
                case 0x22:
                case 0x24:
                case 0x25:
                case 0x27:
                    _ = ReadPoint3D();
                    break;
                case 0x05:
                case 0x0B:
                case 0x19:
                case 0x1A:
                case 0x1C:
                case 0x20:
                    _ = ReadPoint2D();
                    break;
                default:
                    _ = ReadUInt32();
                    break;
            }
        }

        private uint ReadPrologTag()
        {
            EnsureFieldHeader(5, 4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        private void EnsureFieldHeader(byte expectedType, int expectedSize)
        {
            if (_offset + 5 > _buffer.Length)
            {
                throw new InvalidDataException("TLV field header was truncated.");
            }

            var type = _buffer[_offset++];
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_offset, 4));
            _offset += 4;
            if (type != expectedType)
            {
                throw new InvalidDataException($"Unexpected TLV field type {type}; expected {expectedType}.");
            }

            if (size != expectedSize || _offset + size > _buffer.Length)
            {
                throw new InvalidDataException($"Unexpected TLV field size {size}; expected {expectedSize}.");
            }
        }
    }

    private sealed class LibUsbTransport : IAsyncDisposable
    {
        private const int TransferTimeoutMs = 5000;
        private IntPtr _context;
        private IntPtr _deviceHandle;

        private LibUsbTransport(IntPtr context, IntPtr deviceHandle)
        {
            _context = context;
            _deviceHandle = deviceHandle;
        }

        public static Task<LibUsbTransport> OpenAsync(int vendorId, IReadOnlyList<int> productIds, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LibUsb.libusb_init(out var context) != 0)
                {
                    throw new IOException("libusb initialization failed.");
                }

                IntPtr handle = IntPtr.Zero;
                try
                {
                    foreach (var productId in productIds)
                    {
                        handle = LibUsb.libusb_open_device_with_vid_pid(context, (ushort)vendorId, (ushort)productId);
                        if (handle != IntPtr.Zero)
                        {
                            break;
                        }
                    }

                    if (handle == IntPtr.Zero)
                    {
                        throw new IOException("Unable to find a supported Tobii eye-tracking device matching the supplied vendor/product IDs.");
                    }

                    if (LibUsb.libusb_kernel_driver_active(handle, InterfaceNumber) == 1)
                    {
                        _ = LibUsb.libusb_detach_kernel_driver(handle, InterfaceNumber);
                    }

                    ThrowIfNegative(LibUsb.libusb_claim_interface(handle, InterfaceNumber), "Claiming the Tobii USB interface failed.");
                    var controlResult = LibUsb.libusb_control_transfer(handle, 0x41, SessionOpenRequest, 0, 0, IntPtr.Zero, 0, 1000);
                    if (controlResult < 0)
                    {
                        throw new IOException("Opening a Tobii USB session failed.");
                    }

                    return new LibUsbTransport(context, handle);
                }
                catch
                {
                    if (handle != IntPtr.Zero)
                    {
                        LibUsb.libusb_release_interface(handle, InterfaceNumber);
                        LibUsb.libusb_close(handle);
                    }

                    LibUsb.libusb_exit(context);
                    throw;
                }
            }, cancellationToken);
        }

        public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rented = ArrayPool<byte>.Shared.Rent(frame.Length);
                try
                {
                    frame.Span.CopyTo(rented);
                    ThrowIfNegative(LibUsb.libusb_bulk_transfer(_deviceHandle, EndpointOut, rented, frame.Length, out var transferred, TransferTimeoutMs), "USB bulk OUT transfer failed.");
                    if (transferred != frame.Length)
                    {
                        throw new IOException($"USB bulk OUT wrote {transferred} of {frame.Length} bytes.");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }, cancellationToken);
        }

        public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                    try
                    {
                        var result = LibUsb.libusb_bulk_transfer(_deviceHandle, EndpointIn, buffer, 16 * 1024, out var transferred, 100);
                        if (result == LibUsb.TimeoutError)
                        {
                            continue;
                        }

                        ThrowIfNegative(result, "USB bulk IN transfer failed.");
                        if (transferred <= 0)
                        {
                            continue;
                        }

                        return buffer.AsSpan(0, transferred).ToArray();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _ = LibUsb.libusb_control_transfer(_deviceHandle, 0x41, SessionCloseRequest, 0, 0, IntPtr.Zero, 0, 500);
            }
            catch
            {
            }

            try
            {
                LibUsb.libusb_release_interface(_deviceHandle, InterfaceNumber);
            }
            catch
            {
            }

            LibUsb.libusb_close(_deviceHandle);
            LibUsb.libusb_exit(_context);
            return ValueTask.CompletedTask;
        }

        private static void ThrowIfNegative(int value, string message)
        {
            if (value < 0)
            {
                throw new IOException($"{message} libusb={value}.");
            }
        }
    }

    private static class LibUsb
    {
        public const int TimeoutError = -7;

        [DllImport("libusb-1.0", EntryPoint = "libusb_init")]
        public static extern int libusb_init(out IntPtr context);

        [DllImport("libusb-1.0", EntryPoint = "libusb_exit")]
        public static extern void libusb_exit(IntPtr context);

        [DllImport("libusb-1.0", EntryPoint = "libusb_open_device_with_vid_pid")]
        public static extern IntPtr libusb_open_device_with_vid_pid(IntPtr context, ushort vendorId, ushort productId);

        [DllImport("libusb-1.0", EntryPoint = "libusb_kernel_driver_active")]
        public static extern int libusb_kernel_driver_active(IntPtr deviceHandle, byte interfaceNumber);

        [DllImport("libusb-1.0", EntryPoint = "libusb_detach_kernel_driver")]
        public static extern int libusb_detach_kernel_driver(IntPtr deviceHandle, byte interfaceNumber);

        [DllImport("libusb-1.0", EntryPoint = "libusb_claim_interface")]
        public static extern int libusb_claim_interface(IntPtr deviceHandle, byte interfaceNumber);

        [DllImport("libusb-1.0", EntryPoint = "libusb_release_interface")]
        public static extern int libusb_release_interface(IntPtr deviceHandle, byte interfaceNumber);

        [DllImport("libusb-1.0", EntryPoint = "libusb_close")]
        public static extern void libusb_close(IntPtr deviceHandle);

        [DllImport("libusb-1.0", EntryPoint = "libusb_control_transfer")]
        public static extern int libusb_control_transfer(
            IntPtr deviceHandle,
            byte requestType,
            ushort request,
            ushort value,
            ushort index,
            IntPtr data,
            ushort length,
            uint timeout);

        [DllImport("libusb-1.0", EntryPoint = "libusb_bulk_transfer")]
        public static extern int libusb_bulk_transfer(
            IntPtr deviceHandle,
            byte endpoint,
            byte[] data,
            int length,
            out int transferred,
            uint timeout);
    }
}

internal static class TobiiFreeBufferWriterExtensions
{
    public static void Write(this ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        var span = writer.GetSpan(value.Length);
        value.CopyTo(span);
        writer.Advance(value.Length);
    }

    public static void WriteByte(this ArrayBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    public static void WriteTlvTag(this ArrayBufferWriter<byte> writer, uint value)
    {
        writer.WriteByte(5);
        WriteUInt32BigEndian(writer, 4);
        WriteUInt32BigEndian(writer, value);
    }

    public static void WriteTlvU32(this ArrayBufferWriter<byte> writer, uint value)
    {
        writer.WriteByte(2);
        WriteUInt32BigEndian(writer, 4);
        WriteUInt32BigEndian(writer, value);
    }

    public static void WriteTlvQ42(this ArrayBufferWriter<byte> writer, double value)
    {
        writer.WriteByte(4);
        WriteUInt32BigEndian(writer, 8);
        WriteInt64BigEndian(writer, (long)Math.Round(value * 4398046511104d));
    }

    public static void WriteTlvPoint3D(this ArrayBufferWriter<byte> writer, TobiiVector3 value)
    {
        writer.WriteTlvTag(0x031F41);
        writer.WriteTlvQ42(value.X);
        writer.WriteTlvQ42(value.Y);
        writer.WriteTlvQ42(value.Z);
    }

    private static void WriteUInt32BigEndian(ArrayBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteInt64BigEndian(ArrayBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        writer.Advance(8);
    }
}
