using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace loxone.smart.gateway.Api.Tuya;

// Tuya local LAN protocol (versions 3.4 and 3.5) over TCP port 6668:
// a session key is negotiated with the device's local key, then commands are
// sent AES-encrypted (ECB+HMAC framing for 3.4, GCM "6699" framing for 3.5).
// Wire format ported from tinytuya (https://github.com/jasonacox/tinytuya).
public sealed class TuyaLocalConnection : IDisposable
{
    private const int Port = 6668;
    private const uint Prefix55Aa = 0x000055AA;
    private const uint Suffix55Aa = 0x0000AA55;
    private const uint Prefix6699 = 0x00006699;
    private const uint Suffix6699 = 0x00009966;

    private const uint CmdSessKeyNegStart = 3;
    private const uint CmdSessKeyNegResp = 4;
    private const uint CmdSessKeyNegFinish = 5;
    private const uint CmdControlNew = 13;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    private readonly TuyaDeviceConfiguration _device;
    private readonly byte[] _localKey;
    private readonly byte[] _versionHeader;
    private readonly bool _isV35;
    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;
    private byte[] _sessionKey = null!;
    private uint _seqNo = 1;

    public TuyaLocalConnection(TuyaDeviceConfiguration device)
    {
        _device = device;
        _localKey = Encoding.UTF8.GetBytes(device.LocalKey);
        _isV35 = device.Version == "3.5";
        // 15-byte header ("3.4"/"3.5" + 12 zero bytes) prepended to command payloads
        _versionHeader = new byte[15];
        Encoding.ASCII.GetBytes(device.Version, _versionHeader);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(OperationTimeout);

        await _tcp.ConnectAsync(_device.IP, Port, cts.Token);
        _stream = _tcp.GetStream();
        await NegotiateSessionKey(cts.Token);
    }

    public async Task SetDataPointAsync(int dp, JsonElement value, CancellationToken cancellationToken)
    {
        var command = new
        {
            protocol = 5,
            t = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = new { dps = new Dictionary<string, JsonElement> { [dp.ToString()] = value } }
        };
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(OperationTimeout);
            await SendFrame(CmdControlNew, [.. _versionHeader, .. payload], _sessionKey, cts.Token);
        }

        // Best effort: read the acknowledgement so device-side errors surface,
        // but a quiet device is not a failure (the command was already accepted).
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(AckTimeout);
            var (_, retCode, ack) = await ReceiveFrame(_sessionKey, cts.Token);

            if (retCode != 0)
            {
                throw new ApplicationException(
                    $"Device {_device.Name} rejected dp {dp}: code {retCode} {Encoding.UTF8.GetString(ack)}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("No acknowledgement from {device} within {timeout}", _device.Name, AckTimeout);
        }
    }

    public void Dispose()
    {
        _tcp.Dispose();
    }

    // Three-step handshake: exchange 16-byte nonces authenticated with
    // HMAC-SHA256(local key), then derive the session key from their XOR.
    private async Task NegotiateSessionKey(CancellationToken cancellationToken)
    {
        var localNonce = RandomNumberGenerator.GetBytes(16);
        await SendFrame(CmdSessKeyNegStart, localNonce, _localKey, cancellationToken);

        var (cmd, _, payload) = await ReceiveFrame(_localKey, cancellationToken);

        if (cmd != CmdSessKeyNegResp || payload.Length < 48)
        {
            throw new ApplicationException($"Unexpected session key response from {_device.Name}: cmd {cmd}, {payload.Length} bytes");
        }

        var remoteNonce = payload[..16];
        if (!HMACSHA256.HashData(_localKey, localNonce).SequenceEqual(payload[16..48]))
        {
            throw new ApplicationException($"Session key HMAC mismatch from {_device.Name} - check the LocalKey");
        }

        await SendFrame(CmdSessKeyNegFinish, HMACSHA256.HashData(_localKey, remoteNonce), _localKey, cancellationToken);

        var xored = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            xored[i] = (byte)(localNonce[i] ^ remoteNonce[i]);
        }

        if (_isV35)
        {
            _sessionKey = new byte[16];
            var tag = new byte[16];
            using var gcm = new AesGcm(_localKey, 16);
            gcm.Encrypt(localNonce.AsSpan(0, 12), xored, _sessionKey, tag);
        }
        else
        {
            using var aes = Aes.Create();
            aes.Key = _localKey;
            _sessionKey = aes.EncryptEcb(xored, PaddingMode.None);
        }
    }

    private async Task SendFrame(uint cmd, byte[] payload, byte[] key, CancellationToken cancellationToken)
    {
        byte[] frame;

        if (_isV35)
        {
            // 6699 framing: prefix(4) unknown(2) seqno(4) cmd(4) length(4),
            // then iv(12) + AES-GCM ciphertext + tag(16), suffix(4).
            // The 14 header bytes after the prefix are the GCM associated data.
            var header = new byte[18];
            BinaryPrimitives.WriteUInt32BigEndian(header, Prefix6699);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), _seqNo);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10), cmd);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14), (uint)(12 + payload.Length + 16));

            var iv = RandomNumberGenerator.GetBytes(12);
            var cipherText = new byte[payload.Length];
            var tag = new byte[16];
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(iv, payload, cipherText, tag, header.AsSpan(4));

            frame = new byte[18 + 12 + cipherText.Length + 16 + 4];
            header.CopyTo(frame, 0);
            iv.CopyTo(frame, 18);
            cipherText.CopyTo(frame, 30);
            tag.CopyTo(frame, 30 + cipherText.Length);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), Suffix6699);
        }
        else
        {
            // 55AA framing: prefix(4) seqno(4) cmd(4) length(4), then AES-ECB
            // ciphertext + HMAC-SHA256(32) over everything before it, suffix(4).
            using var aes = Aes.Create();
            aes.Key = key;
            var cipherText = aes.EncryptEcb(payload, PaddingMode.PKCS7);

            frame = new byte[16 + cipherText.Length + 32 + 4];
            BinaryPrimitives.WriteUInt32BigEndian(frame, Prefix55Aa);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), _seqNo);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), cmd);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12), (uint)(cipherText.Length + 32 + 4));
            cipherText.CopyTo(frame, 16);
            HMACSHA256.HashData(key, frame.AsSpan(0, 16 + cipherText.Length)).CopyTo(frame, 16 + cipherText.Length);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), Suffix55Aa);
        }

        _seqNo++;
        await _stream.WriteAsync(frame, cancellationToken);
    }

    private async Task<(uint Cmd, uint RetCode, byte[] Payload)> ReceiveFrame(byte[] key, CancellationToken cancellationToken)
    {
        var prefixBytes = await ReadExact(4, cancellationToken);
        var prefix = BinaryPrimitives.ReadUInt32BigEndian(prefixBytes);

        if (prefix == Prefix55Aa)
        {
            var header = await ReadExact(12, cancellationToken); // seqno, cmd, length
            var cmd = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var body = await ReadExact((int)length, cancellationToken); // retcode + ciphertext + hmac(32) + suffix(4)

            var signed = new byte[4 + 12 + body.Length - 36];
            prefixBytes.CopyTo(signed, 0);
            header.CopyTo(signed, 4);
            body.AsSpan(0, body.Length - 36).CopyTo(signed.AsSpan(16));

            if (!HMACSHA256.HashData(key, signed).SequenceEqual(body[^36..^4]))
            {
                throw new ApplicationException($"Frame HMAC mismatch from {_device.Name}");
            }

            var retCode = BinaryPrimitives.ReadUInt32BigEndian(body);
            var cipherText = body[4..^36];
            var payload = Array.Empty<byte>();

            if (cipherText.Length > 0)
            {
                using var aes = Aes.Create();
                aes.Key = key;
                payload = aes.DecryptEcb(cipherText, PaddingMode.PKCS7);
            }

            return (cmd, retCode, StripVersionHeader(payload));
        }

        if (prefix == Prefix6699)
        {
            var header = await ReadExact(14, cancellationToken); // unknown, seqno, cmd, length
            var cmd = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(10));
            var body = await ReadExact((int)length + 4, cancellationToken); // iv(12) + ciphertext + tag(16) + suffix(4)

            var payload = new byte[body.Length - 12 - 16 - 4];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(body.AsSpan(0, 12), body.AsSpan(12, payload.Length), body.AsSpan(body.Length - 20, 16), payload, header);

            // The return code is the first 4 bytes of the decrypted payload
            uint retCode = 0;
            if (payload.Length >= 4)
            {
                retCode = BinaryPrimitives.ReadUInt32BigEndian(payload);
                payload = payload[4..];
            }

            return (cmd, retCode, StripVersionHeader(payload));
        }

        throw new ApplicationException($"Unknown frame prefix 0x{prefix:X8} from {_device.Name}");
    }

    private byte[] StripVersionHeader(byte[] payload)
    {
        return payload.Length >= 15 && payload.AsSpan(0, 3).SequenceEqual(_versionHeader.AsSpan(0, 3))
            ? payload[15..]
            : payload;
    }

    private async Task<byte[]> ReadExact(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }
}
