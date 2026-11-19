using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchUnlock.BleClient;

internal sealed record ServiceCommand(int V, string Operation, string RequestId, string? Key = null,
    ulong Address = 0, int AddressType = 0, string? Name = null, bool? Value = null);
internal sealed record ServiceReply(int V, string RequestId, string Kind, string Message, string? Decision = null,
    bool? Enabled = null, bool? AutoReconnect = null, string? Phone = null);

/// <summary>One command per local pipe connection; replies are bound to that command's ID.</summary>
internal static class ServiceProtocol
{
    internal const string PipeName = "WatchUnlock.v1";
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static ServiceCommand Command(string operation) => new(1, operation, Guid.NewGuid().ToString("D"));
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("后台服务消息过大。");
        await stream.WriteAsync(bytes, token);
        await stream.WriteAsync(new byte[] { 10 }, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token) where T : class
    {
        using var frame = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException("后台服务连接已关闭。");
            if (one[0] == 10) break;
            if (frame.Length >= MaximumBytes) throw new InvalidDataException("后台服务消息过大。");
            frame.WriteByte(one[0]);
        }
        using var document = JsonDocument.Parse(frame.ToArray());
        var body = document.RootElement;
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("后台服务消息格式错误。");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!fields.Add(field.Name) || field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new InvalidDataException("后台服务消息包含重复或嵌套字段。");
        return body.Deserialize<T>(Options) ?? throw new InvalidDataException("后台服务消息为空。");
    }
}
