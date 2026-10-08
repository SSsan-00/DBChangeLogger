using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;
namespace DbEvidence;
public record SavedFilter(TableFilter Filter,string Join);
public record ResultSummary(string 主キー,string PG操作,string SQL操作,string 変更列,string 判定,string テーブル="");
public record TrackedTable(TableSpec PgSpec,TableSpec SqlSpec,Snapshot? PgBefore=null,Snapshot? SqlBefore=null,string Counts="") {
 public override string ToString()=>PgSpec.Name+(Counts.Length==0?"":"  "+Counts);
}
// DTO変更時はVersionとLoadの受け入れ条件も見直す。無条件に旧データを新仕様として解釈しない。
public record SavedSession(int Version,string ConnectionId,string Table,string Columns,string Ignored,SavedFilter[] Filters,
 TableSpec? PgSpec,TableSpec? SqlSpec,Snapshot? PgBefore,Snapshot? SqlBefore,string? SpreadsheetXml,ResultSummary[] Results,string Counts,string Status,string TableSearch="",TrackedTable[]? Targets=null,string[]? MatchKeys=null,string[]? ComparisonIgnored=null,bool AutoMatch=false);
[SupportedOSPlatform("windows")]
public static class SessionStore {
 public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DBChangeLogger","session.bin");
 // 形式: magic / DPAPI鍵長 / DPAPI鍵 / IV / 暗号化したBrotli(JSON) / HMAC-SHA256。
 // 大量スナップショットを丸ごと別バッファへ複製しないよう、圧縮・暗号化をストリームで行う。
 public static void Save(string path,SavedSession session) {
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);var temporary=path+".tmp";
  var key=RandomNumberGenerator.GetBytes(64);
  try {
   using(var file=new FileStream(temporary,FileMode.Create,FileAccess.ReadWrite,FileShare.None,65536)) {
    using var aes=Aes.Create();aes.Key=key[..32];aes.GenerateIV();
    // 接続設定の共通鍵とは分離。取得データは同じWindowsユーザーだけが復号できる。
    var protectedKey=ProtectedData.Protect(key,null,DataProtectionScope.CurrentUser);
    using(var writer=new BinaryWriter(file,System.Text.Encoding.UTF8,true)){writer.Write(0x314C4244);writer.Write(protectedKey.Length);writer.Write(protectedKey);writer.Write(aes.IV);}
    using(var crypto=new CryptoStream(file,aes.CreateEncryptor(),CryptoStreamMode.Write,true))
     using(var compressed=new BrotliStream(crypto,CompressionLevel.Fastest,true))JsonSerializer.Serialize(compressed,session);
    file.Position=0;using var mac=new HMACSHA256(key[32..]);var tag=mac.ComputeHash(file);file.Write(tag);file.Flush(true);
   }
   // 書き込み完了後に同一フォルダー内で置換し、保存失敗で前回のファイルを壊さない。
   File.Move(temporary,path,true);
  } finally {CryptographicOperations.ZeroMemory(key);if(File.Exists(temporary))File.Delete(temporary);}
 }
 public static SavedSession Load(string path) {
  using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536);
  using var reader=new BinaryReader(file,System.Text.Encoding.UTF8,true);
  if(reader.ReadInt32()!=0x314C4244)throw new InvalidDataException();
  var length=reader.ReadInt32();if(length<=0||length>4096)throw new InvalidDataException();
  var wrapped=reader.ReadBytes(length);var iv=reader.ReadBytes(16);var start=file.Position;
  var key=ProtectedData.Unprotect(wrapped,null,DataProtectionScope.CurrentUser);
  try {
   if(key.Length!=64||iv.Length!=16||file.Length-start<=32)throw new InvalidDataException();
   // CBCには改ざん検出がないため、ヘッダーを含むHMACを復号・展開より先に検証する。
   var end=file.Length-32;file.Position=0;
   using var mac=IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256,key[32..]);var buffer=new byte[65536];
   while(file.Position<end){var count=file.Read(buffer,0,(int)Math.Min(buffer.Length,end-file.Position));if(count==0)throw new EndOfStreamException();mac.AppendData(buffer,0,count);}
   var tag=reader.ReadBytes(32);if(!CryptographicOperations.FixedTimeEquals(mac.GetHashAndReset(),tag))throw new CryptographicException();
   file.Position=start;using var limited=new LimitedReadStream(file,end-start);using var aes=Aes.Create();aes.Key=key[..32];aes.IV=iv;
   using var crypto=new CryptoStream(limited,aes.CreateDecryptor(),CryptoStreamMode.Read);using var compressed=new BrotliStream(crypto,CompressionMode.Decompress);
   var session=JsonSerializer.Deserialize<SavedSession>(compressed)??throw new InvalidDataException();compressed.CopyTo(Stream.Null);
   if(session.Version is not (1 or 2 or 3 or 4 or 5))throw new InvalidDataException();return session;
  }finally{CryptographicOperations.ZeroMemory(key);}
 }
 // 末尾のHMACを暗号文としてCryptoStreamへ渡すとパディング検証が失敗するため、読み取り範囲を制限する。
 sealed class LimitedReadStream(Stream inner,long remaining):Stream {
  public override int Read(byte[] buffer,int offset,int count){var n=inner.Read(buffer,offset,(int)Math.Min(count,remaining));remaining-=n;return n;}
  public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
  public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
  public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
  public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 }
}
