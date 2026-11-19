using System.Security.Cryptography;
using System.Text;

namespace WatchUnlock.BleClient;

internal static class PasswordInput
{
    internal static byte[] Read()
    {
        Console.Write("请输入当前本地账户的 Windows 密码（不是 PIN）：");
        var chars = new char[256];
        var count = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (count > 0) { chars[--count] = '\0'; Console.Write("\b \b"); }
                }
                else if (!char.IsControl(key.KeyChar) && count < chars.Length)
                { chars[count++] = key.KeyChar; Console.Write('*'); }
            }
            if (count == 0) throw new InvalidOperationException("本地账户密码不能为空。");
            return Encoding.Unicode.GetBytes(chars, 0, count);
        }
        finally { Array.Clear(chars); }
    }
}
