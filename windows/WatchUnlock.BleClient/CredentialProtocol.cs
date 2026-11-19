using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WatchUnlock.BleClient;

internal static class CredentialProtocol
{
    internal const string LoginPipe = "WatchUnlock.Credential.v1";
    internal const string SetupPipe = "WatchUnlock.Setup.v1";
    internal const uint LoginMagic = 0x31554357; // WCU1, little endian
    internal const uint SetupMagic = 0x31534357; // WCS1
    internal const int MaximumPasswordBytes = 512;

    internal static async Task<byte[]> ReadAsync(Stream pipe, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        try { await pipe.ReadExactlyAsync(bytes, token); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    internal static byte[] SetupHeader(uint operation, int size)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SetupMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), operation);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)size));
        return header;
    }
}
