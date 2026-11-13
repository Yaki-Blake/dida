using System.Security.Cryptography;

namespace DidaLite.Core.Models;

/// <summary>
/// 极简 ULID：48 位毫秒时间戳 + 80 位随机数，Crockford Base32 编码为 26 位字符。
/// 天然按时间有序，双端离线生成也不会撞号。
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewId() => NewId(DateTimeOffset.UtcNow);

    public static string NewId(DateTimeOffset time)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        var timestamp = (ulong)time.ToUnixTimeMilliseconds() & 0xFFFFFFFFFFFFUL;

        // 128 位 = 时间戳高 48 位 + 随机低 80 位，转为 26 个 Base32 字符
        Span<char> chars = stackalloc char[26];
        chars[0] = Alphabet[(int)((timestamp >> 45) & 0x1F)];
        chars[1] = Alphabet[(int)((timestamp >> 40) & 0x1F)];
        chars[2] = Alphabet[(int)((timestamp >> 35) & 0x1F)];
        chars[3] = Alphabet[(int)((timestamp >> 30) & 0x1F)];
        chars[4] = Alphabet[(int)((timestamp >> 25) & 0x1F)];
        chars[5] = Alphabet[(int)((timestamp >> 20) & 0x1F)];
        chars[6] = Alphabet[(int)((timestamp >> 15) & 0x1F)];
        chars[7] = Alphabet[(int)((timestamp >> 10) & 0x1F)];
        chars[8] = Alphabet[(int)((timestamp >> 5) & 0x1F)];
        chars[9] = Alphabet[(int)(timestamp & 0x1F)];

        // 80 位随机拆成 16 个 5 位
        for (var i = 0; i < 16; i++)
        {
            var bitOffset = i * 5;                 // 0..75
            var byteIndex = bitOffset / 8;         // 0..9
            var bitInByte = bitOffset % 8;         // 0..7

            var value = random[byteIndex] << 8;
            if (byteIndex + 1 < 10) value |= random[byteIndex + 1];

            var shifted = (value >> (11 - bitInByte)) & 0x1F;
            chars[10 + i] = Alphabet[shifted];
        }

        return new string(chars);
    }
}
