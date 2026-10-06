using DbEvidence;
using DbEvidenceConfigure;

string? temporary = null;
try
{
    if (args.Length != 1) throw new InvalidOperationException();
    var settings = new ConnectionSettings(PrivateConnections.Postgres, PrivateConnections.SqlServer);
    settings.Validate();
    async Task<bool> Check(bool pg)
    {
        var name = pg ? "PostgreSQL" : "SQL Server";
        Console.WriteLine(name + "の接続とテーブル一覧の読み取りを確認中…（最大30秒）");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tables = await TableCatalog.Read(pg, pg ? settings.Postgres : settings.SqlServer, timeout.Token);
            Console.WriteLine(name + $": 接続成功（読み取り可能なテーブル {tables.Length}件）");
            return true;
        }
        catch
        {
            // ドライバーの例外には接続情報が入り得るため、公開ログへ本文を出さない。
            Console.Error.WriteLine(name + ": 接続確認失敗。接続設定・ネットワーク・認証・証明書・読み取り権限を確認してください。");
            return false;
        }
    }
    // 片側が失敗しても両DBの結果を伝える。どちらか失敗したら既存設定を置き換えない。
    var pgOk = await Check(true);
    var sqlOk = await Check(false);
    if (!pgOk || !sqlOk) return 1;
    var bytes = settings.Encrypt();
    var destination = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
    File.WriteAllBytes(temporary, bytes);
    File.Move(temporary, destination, true);
    Console.WriteLine("暗号化接続設定を生成しました。接続情報は表示しません。");
    return 0;
}
catch
{
    Console.Error.WriteLine("設定生成に失敗しました。PrivateConnections.local.cs の両DBの接続文字列を確認してください。");
    return 1;
}
finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
