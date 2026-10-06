using DbEvidence;
using Npgsql;
using Microsoft.Data.SqlClient;
using System.Data.Common;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DbEvidenceTests;

[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseIntegrationTests
{
    [TestMethod]
    public async Task RealDatabases_CompareAndRenderEvidence()
    {
        if (Environment.GetEnvironmentVariable("EVIDENCE_RUN_INTEGRATION") != "1")
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive("専用テストDBを起動し、EVIDENCE_RUN_INTEGRATION=1を指定してください。");
var pg="Host=localhost;Port=55432;Database=evidence;Username=postgres;Password=EvidenceTest123";
var sql="Server=localhost,51433;Database=master;User ID=sa;Password=EvidenceTest123!;Encrypt=true;TrustServerCertificate=true";
async Task Run(bool p,string query){await using DbConnection c=p?new NpgsqlConnection(pg):new SqlConnection(sql);await c.OpenAsync();await using var cmd=c.CreateCommand();cmd.CommandText=query;await cmd.ExecuteNonQueryAsync();}
void Assert(bool ok,string message) => Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(ok, message);
foreach(var p in new[]{true,false}) {
 var schema=p?"public":"dbo";var txt=p?"varchar(100)":"nvarchar(100)";
 await Run(p,$"DROP TABLE IF EXISTS {schema}.evidence_test; CREATE TABLE {schema}.evidence_test(id int NOT NULL PRIMARY KEY,name {txt},amount decimal(18,2),stamp int); INSERT INTO {schema}.evidence_test VALUES(1,'same',10,0),(2,'delete',20,0),(3,'unchanged',30,0),(4,'mismatch',40,0),(5,'onesided',50,0),(6,NULL,60,0),(7,'ignore',70,0);");
}
var common=TableCatalog.Common(await TableCatalog.Read(true,pg),await TableCatalog.Read(false,sql));
var selected=common.Single(t=>t.Postgres.Name=="evidence_test");
Assert(selected.KeyError==null&&selected.Postgres.Keys.SequenceEqual(new[]{"id"})&&selected.SqlServer.Keys.SequenceEqual(new[]{"id"}),"実DBから共通テーブルと主キーを取得");
var (ps,ss)=selected.Specs(new[]{"stamp"},null);
var pb=await Engine.Capture(true,pg,ps);var sb=await Engine.Capture(false,sql,ss);
foreach(var p in new[]{true,false}){var t=(p?"public":"dbo")+".evidence_test";await Run(p,$"UPDATE {t} SET amount=12 WHERE id=1; DELETE FROM {t} WHERE id=2; INSERT INTO {t} VALUES(8,{(p?"":"N")}'追加',80,0); UPDATE {t} SET amount={(p?41:42)} WHERE id=4; UPDATE {t} SET name='' WHERE id=6; UPDATE {t} SET stamp={(p?1:2)} WHERE id=7;");}
await Run(true,"UPDATE public.evidence_test SET amount=51 WHERE id=5;");
var pa=await Engine.Capture(true,pg,ps,pb);var sa=await Engine.Capture(false,sql,ss,sb);var result=Engine.Compare(pb,pa,sb,sa,ps);
Assert(result.Count==6,"変更行のみ（変更なし・除外列のみの変更は出力しない）");
Assert(result.Count(x=>!x.Match)==2,"更新値不一致・片側だけ更新を検出");
Assert(result.Single(x=>x.Key=="[\"8\"]").Pg.Operation=="追加","追加を識別");
Assert(result.Single(x=>x.Key=="[\"2\"]").Pg.Operation=="削除","削除を識別");
Assert(result.Single(x=>x.Key=="[\"1\"]").Pg.Changed.SequenceEqual(new[]{2}),"変更列を特定");
Assert(result.Single(x=>x.Key=="[\"6\"]").Pg.Operation=="更新","NULLと空文字を区別");
var output=Engine.Render(pb.Columns,result,ps,pb,pa,sb,sa);Directory.CreateDirectory("artifacts");await File.WriteAllTextAsync("artifacts/sample-evidence.html",output.Html);await File.WriteAllTextAsync("artifacts/sample-evidence.tsv",output.Text);
Assert(output.Html.Contains("#ffc7ce")&&output.Html.Contains("#fff2cc"),"差分・不一致セルに色付け");
var cf=Engine.ClipboardHtml(output.Html);var bytes=Encoding.UTF8.GetBytes(cf);
int Offset(string label)=>int.Parse(cf.Split("\r\n").Single(x=>x.StartsWith(label+":")).Split(':')[1]);
Assert(Encoding.UTF8.GetString(bytes[Offset("StartFragment")..Offset("EndFragment")])==output.Html[(output.Html.IndexOf("<body>")+6)..output.Html.LastIndexOf("</body>")],"日本語HTMLのクリップボードUTF-8オフセット");
var weird=new Snapshot(new[]{"id","name"},new(){["k"]=new[]{"1","=SUM(A1:A2)<&\n"}},DateTimeOffset.UtcNow);
var empty=weird with{Rows=new()};var spec=new TableSpec("public","x",new[]{"id"},Array.Empty<string>());
var safe=Engine.Render(weird.Columns,Engine.Compare(empty,weird,empty,weird,spec),spec,empty,weird,empty,weird);
Assert(safe.Html.Contains("&lt;&amp;")&&safe.Html.Contains("mso-number-format"),"特殊文字をHTMLエスケープしExcelで文字列指定");
try{await Engine.Capture(true,pg,ps with{Keys=new[]{"missing"}});throw new Exception("missing validation");}catch(InvalidOperationException){Assert(true,"存在しない主キーを拒否");}
var reordered=sa with{Columns=sa.Columns.Reverse().ToArray(),Rows=sa.Rows.ToDictionary(x=>x.Key,x=>x.Value.Reverse().ToArray())};
Assert(Engine.Compare(pb,pa,sb,reordered,ps).Count==6,"DB間の列順の違いに対応");
Assert(ReferenceEquals(pb.Rows["[\"3\"]"],pa.Rows["[\"3\"]"]),"変更なし行は前のスナップショットを再利用");
foreach(var p in new[]{true,false}){
 var filtered=await Engine.Capture(p,p?pg:sql,(p?ps:ss) with{Filter=new("id","範囲","1","整数","4")});
 Assert(await Engine.CountRows(p,p?pg:sql,(p?ps:ss) with{Filter=new("id","範囲","1","整数","4")})==3,"取得対象の実件数を数える");
 Assert(filtered.Rows.Count==3,"DB側の範囲条件で転送行数を絞る");
 Assert(filtered.Rows.Keys.All(k=>k is "[\"1\"]" or "[\"3\"]" or "[\"4\"]"),"範囲外の行は取得しない");
}
Console.WriteLine("実DB統合検証 完了");

    }
}
