using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
namespace DbEvidence;

public sealed record ConnectionSettings(string Postgres, string SqlServer)
{
    public const string FileName = "connections.enc";
    public const string Error = "接続設定を読み取れません。作成者が設定を再生成してください。";
    // クライアント単体で復号する共通鍵。解析に対する秘密保持は保証しない。
    private static readonly byte[] Key = Convert.FromHexString("B372F06DA87117CD445BC93E0C05DB8B9A63E1CF0295BEA966930645FAA874D2");
    private static readonly byte[] Header = "DBE1"u8.ToArray();
    private const int MaximumBytes = 65536;

    public void Validate()
    {
        try
        {
            var pg = new NpgsqlConnectionStringBuilder(Postgres);
            var sql = new SqlConnectionStringBuilder(SqlServer);
            if (string.IsNullOrWhiteSpace(pg.Host) || string.IsNullOrWhiteSpace(pg.Database)
                || pg.Port < 1 || pg.Port > 65535 || string.IsNullOrWhiteSpace(sql.DataSource)
                || string.IsNullOrWhiteSpace(sql.InitialCatalog)) throw new InvalidOperationException();
        }
        catch { throw new InvalidOperationException(Error); }
    }

    public byte[] Encrypt()
    {
        Validate();
        var plain = JsonSerializer.SerializeToUtf8Bytes(this);
        try
        {
            if (plain.Length > MaximumBytes - 32) throw new InvalidOperationException(Error);
            var result = new byte[32 + plain.Length];
            Header.CopyTo(result, 0);
            RandomNumberGenerator.Fill(result.AsSpan(4, 12));
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(result.AsSpan(4, 12), plain, result.AsSpan(32), result.AsSpan(16, 16), Header);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static ConnectionSettings Decrypt(byte[] encrypted)
    {
        byte[]? plain = null;
        try
        {
            if (encrypted.Length < 33 || encrypted.Length > MaximumBytes
                || !encrypted.AsSpan(0, 4).SequenceEqual(Header)) throw new InvalidOperationException();
            plain = new byte[encrypted.Length - 32];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(encrypted.AsSpan(4, 12), encrypted.AsSpan(32), encrypted.AsSpan(16, 16), plain, Header);
            var settings = JsonSerializer.Deserialize<ConnectionSettings>(plain) ?? throw new InvalidOperationException();
            settings.Validate();
            return settings;
        }
        catch { throw new InvalidOperationException(Error); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public static ConnectionSettings Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 33 || stream.Length > MaximumBytes) throw new InvalidOperationException();
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return Decrypt(bytes);
        }
        catch { throw new InvalidOperationException(Error); }
    }
}
