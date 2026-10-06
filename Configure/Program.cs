using DbEvidence;
using DbEvidenceConfigure;

string? temporary = null;
try
{
    if (args.Length != 1) throw new InvalidOperationException();
    var bytes = new ConnectionSettings(PrivateConnections.Postgres, PrivateConnections.SqlServer).Encrypt();
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
