using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using DbEvidence;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace DbEvidenceTests;

[TestClass]
public class CapturePerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TypedReadersKeepExistingNormalization()
    {
        object[] values=[int.MinValue,long.MaxValue,1.2300m,new DateTime(2026,10,8,1,2,3).AddTicks(1234567),true,false,"00123","日本語",short.MinValue,byte.MaxValue,1.25f,1.25d,Guid.Empty,new byte[]{0,255},DateTimeOffset.Parse("2026-10-08T12:00:00+09:00")];
        foreach(var value in values) {
            var table=new System.Data.DataTable();table.Columns.Add("v",value.GetType());table.Rows.Add(value);
            using var reader=table.CreateDataReader();Assert.IsTrue(reader.Read());
            Assert.AreEqual(Engine.Normalize(value),Engine.ValueReader(value.GetType())(reader,0),value.GetType().Name);
        }
    }

    [TestMethod]
    public async Task PairCaptureHonorsCancellationBeforeOpeningConnections()
    {
        var spec=new TableSpec("public","x",["id"],[]);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>Engine.CapturePair("invalid","invalid",spec,spec,cancellationToken:new CancellationToken(true)));
    }

    [TestMethod, TestCategory("Integration"), TestCategory("Performance")]
    public async Task MeasureRealCaptureAndSessionSave()
    {
        var report=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_REPORT");
        if(report is null)Assert.Inconclusive("専用の検証DBで明示的に実行します。");
        var pg=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_PG")??throw new InvalidOperationException("PG検証接続先が必要です。");
        var sql=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_SQL")??throw new InvalidOperationException("SQL検証接続先が必要です。");
        var count=int.TryParse(Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_ROWS"),out var rows)?rows:1_000_000;
        if(count<1000||count>3_000_000)throw new InvalidOperationException("1000〜300万件を指定してください。");
        var name="capture_perf_"+Guid.NewGuid().ToString("N");
        var folder=Path.Combine(Path.GetTempPath(),name);Directory.CreateDirectory(folder);
        async Task Execute(bool p,string query) {
            await using DbConnection db=p?new NpgsqlConnection(pg):new SqlConnection(sql);await db.OpenAsync();
            await using var cmd=db.CreateCommand();cmd.CommandText=query;cmd.CommandTimeout=300;await cmd.ExecuteNonQueryAsync();
        }
        var measurements=new List<object>();
        async Task<T> Measure<T>(string stage,Func<Task<T>> work) {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            var allocated=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();
            var result=await work();watch.Stop();
            measurements.Add(new{Stage=stage,Seconds=watch.Elapsed.TotalSeconds,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated});
            TestContext.WriteLine(stage+": "+watch.Elapsed.TotalSeconds.ToString("F3")+"s");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report+".progress",stage+": "+watch.Elapsed.TotalSeconds.ToString("F3")+"s");return result;
        }
        try {
            await Execute(true,$"CREATE TABLE public.{name}(id int PRIMARY KEY, code varchar(20), amount decimal(12,2), stamp timestamp, flag boolean, note varchar(40), category int, revision bigint); INSERT INTO public.{name} SELECT i, lpad(i::text,10,'0'), i/100.0, timestamp '2026-10-08 01:02:03.123456', i%2=0, CASE WHEN i%3=0 THEN NULL ELSE 'test' END,i%10,i::bigint FROM generate_series(1,{count}) i;");
            await Execute(false,$"CREATE TABLE dbo.{name}(id int PRIMARY KEY, code varchar(20), amount decimal(12,2), stamp datetime2(6), flag bit, note varchar(40), category int, revision bigint); WITH d(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)), numbers AS (SELECT TOP ({count}) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) i FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e CROSS JOIN d f CROSS JOIN d g CROSS JOIN d h) INSERT INTO dbo.{name} SELECT i,RIGHT('0000000000'+CAST(i AS varchar(10)),10),i/100.0,CAST('2026-10-08T01:02:03.123456' AS datetime2(6)),CASE WHEN i%2=0 THEN 1 ELSE 0 END,CASE WHEN i%3=0 THEN NULL ELSE 'test' END,i%10,i FROM numbers;");
            var p=new TableSpec("public",name,["id"],[]);var s=p with{Schema="dbo"};
            // 接続・JITの初回コストを小さな取得で済ませる。シード作成時間は含めない。
            await Engine.Capture(true,pg,p with{Filter=new("id","<=","100","整数")});
            await Engine.Capture(false,sql,s with{Filter=new("id","<=","100","整数")});
            Assert.AreEqual(count,await Measure("PG count",()=>Engine.CountRows(true,pg,p)));
            Assert.AreEqual(count,await Measure("SQL count",()=>Engine.CountRows(false,sql,s)));
            var pb=await Measure("PG capture",()=>Engine.Capture(true,pg,p,expectedRows:count));
            var sb=await Measure("SQL capture",()=>Engine.Capture(false,sql,s,expectedRows:count));
            Assert.AreEqual(count,pb.Rows.Count);Assert.AreEqual(count,sb.Rows.Count);
            foreach(var (key,value) in pb.Rows)CollectionAssert.AreEqual(value,sb.Rows[key],key);
            var expected=Fingerprint(pb);Assert.AreEqual(expected,Fingerprint(sb));
            if(OperatingSystem.IsWindows()) {
                await Measure("Session save",()=>Task.Run(()=>{
                    if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
                    var state=new SavedSession(2,"test",name,"","",[],null,null,null,null,null,[],"","",Targets:[new(p,s,pb,sb)]);
                    var path=Path.Combine(folder,"session.bin");SessionStore.Save(path,state);return new FileInfo(path).Length;
                }));
            }
            // 参照用の巨大スナップショットを保持したまま次を測ると実アプリ以上のメモリ圧迫になる。
            // 全件を主キー順でストリームハッシュ化し、以降はこの固定サイズの署名と照合する。
            pb=null!;sb=null!;
            // 並行取得でも同じ主キー・値が得られ、COUNTのヒントが行数制限にならないことを確認する。
            var pair=await Measure("Parallel capture",()=>Engine.CapturePair(pg,sql,p,s,expectedCounts:(count,count)));
            Assert.AreEqual(expected,Fingerprint(pair.Pg));Assert.AreEqual(expected,Fingerprint(pair.Sql));
            pair=default;
            for(var round=0;round<3;round++) {
                foreach(var optimized in round%2==0?new[]{false,true}:new[]{true,false}) {
                    async Task<(Snapshot Pg,Snapshot Sql)> Read() {
                        if(optimized)return await Engine.CapturePair(pg,sql,p,s,expectedCounts:(count,count));
                        return(await OriginalCapture(true,pg,p),await OriginalCapture(false,sql,s));
                    }
                    var measured=await Measure((optimized?"Paired optimized ":"Paired original ")+round,Read);
                    Assert.AreEqual(count,measured.Pg.Rows.Count);Assert.AreEqual(count,measured.Sql.Rows.Count);
                    Assert.AreEqual(expected,Fingerprint(measured.Pg));Assert.AreEqual(expected,Fingerprint(measured.Sql));
                    measured=default;
                }
            }
            var small=await Engine.Capture(true,pg,p with{Filter=new("id","<=","100","整数")},expectedRows:7);
            Assert.AreEqual(100,small.Rows.Count);
            using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15))) {
                var progress=new CancelProgress(cancel);
                await Assert.ThrowsAsync<OperationCanceledException>(()=>Engine.CapturePair(pg,sql,p,s,cancellationToken:cancel.Token,progress:progress));
                Assert.IsTrue(progress.Received,"取得途中の進捗から中断できること。");
            }
            await Assert.ThrowsAsync<DbException>(()=>Engine.CapturePair(pg,sql,p,s with{Name=name+"_missing"}));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report,JsonSerializer.Serialize(new{RowsPerDatabase=count,Columns=8,Measurements=measurements},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally {
            // 自分が今回作成した一意な名前のテーブルだけを片付ける。
            await Execute(true,$"DROP TABLE IF EXISTS public.{name}");
            await Execute(false,$"DROP TABLE IF EXISTS dbo.{name}");
            Directory.Delete(folder,true);
        }
    }
    sealed class CancelProgress(CancellationTokenSource source):IProgress<(bool Pg,int Count)> {
        public bool Received;
        public void Report((bool Pg,int Count) value){if(value.Count>=10000){Received=true;source.Cancel();}}
    }
    static string Fingerprint(Snapshot snapshot) {
        using var hash=System.Security.Cryptography.SHA256.Create();
        using var stream=new System.Security.Cryptography.CryptoStream(Stream.Null,hash,System.Security.Cryptography.CryptoStreamMode.Write);
        using(var writer=new Utf8JsonWriter(stream)) {
            writer.WriteStartArray();JsonSerializer.Serialize(writer,snapshot.Columns);
            foreach(var key in snapshot.Rows.Keys.Order(StringComparer.Ordinal)) {
                writer.WriteStartArray();writer.WriteStringValue(key);JsonSerializer.Serialize(writer,snapshot.Rows[key]);writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        stream.FlushFinalBlock();return Convert.ToHexString(hash.Hash!);
    }
    // c0ae7ebの操作前取得を固定した測定用参照。正常系の同一テーブルを旧方式で順次取得する。
    // SQL・分離レベル・値の書式・キー形式を変えず、現在の実装と同じ実DBで比較する。
    static async Task<Snapshot> OriginalCapture(bool pg,string connection,TableSpec spec) {
        await using DbConnection db=pg?new NpgsqlConnection(connection):new SqlConnection(connection);
        await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await using var cmd=Engine.CreateSelectCommand(db,pg,spec);cmd.Transaction=tx;
        await using var reader=await cmd.ExecuteReaderAsync();
        var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();
        var keys=spec.Keys.Select(k=>Array.FindIndex(columns,c=>string.Equals(c,k,StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows=new Dictionary<string,string?[]>(StringComparer.Ordinal);var keyValues=new string?[keys.Length];
        while(await reader.ReadAsync()) {
            var row=new string?[columns.Length];for(var i=0;i<row.Length;i++)row[i]=Engine.Normalize(reader.GetValue(i));
            for(var i=0;i<keys.Length;i++)keyValues[i]=row[keys[i]];
            if(!rows.TryAdd(JsonSerializer.Serialize(keyValues),row))throw new InvalidOperationException("重複キー");
        }
        await reader.DisposeAsync();await tx.CommitAsync();return new(columns,rows,DateTimeOffset.UtcNow);
    }
}
