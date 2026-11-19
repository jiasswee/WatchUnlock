using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchUnlock.BleClient;

internal static class PairingProtocol
{
    internal static byte[] ReadKey()
    {
        while (true)
        {
            Console.Write("请输入与手机相同的配对 Key（16～128 位英文、数字或符号，无空格）：");
            var input = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (input.Count > 0) { input[^1] = '\0'; input.RemoveAt(input.Count - 1); Console.Write("\b \b"); }
                }
                else if (key.KeyChar is >= '!' and <= '~' && input.Count < 128)
                { input.Add(key.KeyChar); Console.Write('*'); }
            }
            var chars = input.ToArray();
            for (var i = 0; i < input.Count; i++) input[i] = '\0';
            input.Clear();
            byte[]? raw = null;
            try
            {
                if (chars.Length < 16) { Console.WriteLine("Key 至少需要 16 位。"); continue; }
                raw = Encoding.UTF8.GetBytes(chars);
                return Rfc2898DeriveBytes.Pbkdf2(raw, Encoding.UTF8.GetBytes("WatchUnlock pairing v2"),
                    210000, HashAlgorithmName.SHA256, 32);
            }
            finally { Array.Clear(chars); if (raw is not null) CryptographicOperations.ZeroMemory(raw); }
        }
    }

    internal static byte[] Canonical(params string[] parts) => Encoding.UTF8.GetBytes(
        string.Join("\n", new[] { "watchunlock-v2" }.Concat(parts)));
    internal static byte[] RawSign(byte[] key, params string[] parts) => HMACSHA256.HashData(key, Canonical(parts));
    internal static string Sign(byte[] key, params string[] parts) => Convert.ToBase64String(RawSign(key, parts));
    internal static byte[] Base64Bytes(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != 32 || Convert.ToBase64String(bytes) != value)
            throw new InvalidDataException("消息中的随机数或签名格式无效。");
        return bytes;
    }
    internal static void Verify(byte[] key, string signature, params string[] parts)
    {
        if (!CryptographicOperations.FixedTimeEquals(Base64Bytes(signature), RawSign(key, parts)))
            throw new UnauthorizedAccessException("签名校验失败，请确认两端使用相同 Key。");
    }
    internal static JsonElement Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
        var body = document.RootElement;
        if (body.ValueKind != JsonValueKind.Object || body.GetProperty("v").GetInt32() != 2)
            throw new InvalidDataException("BLE 协议不兼容，请同时更新手机和 Windows 程序。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!names.Add(field.Name) || field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                throw new InvalidDataException("BLE 消息包含重复或无效字段。");
        return body.Clone();
    }
    internal static void Fields(JsonElement body, params string[] names)
    {
        var expected = new HashSet<string>(names.Concat(new[] { "v", "type" }), StringComparer.Ordinal);
        if (!expected.SetEquals(body.EnumerateObject().Select(field => field.Name)))
            throw new InvalidDataException("BLE 消息字段不兼容。");
    }
    internal static string Text(JsonElement body, string name) => body.GetProperty(name).GetString()
        ?? throw new InvalidDataException("BLE 消息缺少必要字段。");
    internal static byte[] Frame(Dictionary<string, object> fields) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n");
}

internal static class ErrorInfo
{
    internal static string Describe(Exception error)
    {
        var detail = string.IsNullOrWhiteSpace(error.Message) ? "Windows 蓝牙接口未返回说明" : error.Message.Trim();
        return $"{detail}（{error.GetType().Name}，错误码 0x{unchecked((uint)error.HResult):X8}）";
    }
}
