using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Data.Common;
using DbEvidence;
using Npgsql;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DbEvidenceTests;

[TestClass]
public sealed class LargeTableTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod,TestCategory("Performance")]
    public void MeasureCurrentComparisonAndWideExport()
    {
        var reportPath=Environment.GetEnvironmentVariable("EVIDENCE_TUNING_REPORT");
        if(reportPath is null)Assert.Inconclusive("改善前後を同じ条件で測るときだけ実行します。");
        var measurements=new List<object>();
        var signatures=new Dictionary<string,string>();
        string Hash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        void Measure(string name,Func<string> work)
        {
            signatures[name]=Hash(work()); // JITの初回実行を計測に含めない。
            var times=new List<double>();var allocations=new List<long>();
            for(var i=0;i<3;i++) {
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                var allocated=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();
                var output=work();watch.Stop();var bytes=GC.GetTotalAllocatedBytes(true)-allocated;
                Assert.AreEqual(signatures[name],Hash(output),"反復実行で結果が変わらないこと。");
                times.Add(watch.Elapsed.TotalMilliseconds);allocations.Add(bytes);
            }
            measurements.Add(new{Name=name,Milliseconds=times.Order().ElementAt(1),AllocatedBytes=allocations.Order().ElementAt(1)});
        }
        foreach(var (name,count,width,stride) in new[]{("sparse",300_000,16,1000),("wide",1000,300,1)}) {
            var columns=Enumerable.Range(0,width).Select(i=>i==0?"id":"col"+i).ToArray();
            var rows=new Dictionary<string,string?[]>(count,StringComparer.Ordinal);
            var next=new Dictionary<string,string?[]>(count,StringComparer.Ordinal);
            for(var i=0;i<count;i++) {
                var id=i.ToString(CultureInfo.InvariantCulture);var key="[\""+id+"\"]";
                var row=Enumerable.Repeat<string?>("old",width).ToArray();row[0]=id;rows.Add(key,row);
                var after=row.ToArray();if(i%stride==0)for(var col=1;col<width;col++)after[col]="new";
                next.Add(key,after);
            }
            var before=new Snapshot(columns,rows,DateTimeOffset.UnixEpoch);var afterSnapshot=before with{Rows=next};
            var spec=new TableSpec("public",name,["id"],[]);
            // 比較結果のシリアライズ時間も双方に含む。DB取得・COUNT・Excel貼付は計測対象外。
            Measure(name+"-compare",()=>JsonSerializer.Serialize(Engine.Compare(before,afterSnapshot,before,afterSnapshot,spec)));
            if(name=="wide") {
                var changes=Engine.Compare(before,afterSnapshot,before,afterSnapshot,spec);
                Measure("wide-export",()=>Engine.Render(columns,changes,spec,before,afterSnapshot,before,afterSnapshot,true).SpreadsheetXml);
            }
        }
        foreach(var d in new[]{DemoEvidence.Create(),DemoEvidence.CreateSingleUpdate()}.Concat(DemoEvidence.CreateMultiple())) {
            var changes=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
            var output=Engine.Render(d.PgBefore.Columns,changes,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter);
            signatures["demo-"+signatures.Count]=Hash(output.Html+"\0"+output.Text+"\0"+output.SpreadsheetXml);
        }
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new{Measurements=measurements,Signatures=signatures},new JsonSerializerOptions{WriteIndented=true}));
        if(Environment.GetEnvironmentVariable("EVIDENCE_TUNING_BASELINE") is {} baseline) {
            using var original=JsonDocument.Parse(File.ReadAllText(baseline));
            foreach(var (key,value) in signatures)Assert.AreEqual(original.RootElement.GetProperty("Signatures").GetProperty(key).GetString(),value,key+"の出力が改善前と完全一致すること。");
        }
        TestContext.WriteLine(File.ReadAllText(reportPath));
    }

    [TestMethod]
    public void OptimizedComparisonMatchesOriginalForMixedChangesAndColumnOrders()
    {
        var random = new Random(5729);
        for (var run = 0; run < 30; run++)
        {
            string[] columns = ["id", "value", "ignored", "nullable"];
            Snapshot Make(bool reverse)
            {
                var rows = new Dictionary<string,string?[]>();
                for (var id = 0; id < 50; id++)
                {
                    if (random.Next(5) == 0) continue;
                    string?[] row = [id.ToString(), random.Next(3).ToString(), random.Next(2).ToString(), random.Next(3) == 0 ? null : ""];
                    rows.Add("[\"" + id + "\"]", reverse ? row.Reverse().ToArray() : row);
                }
                return new(reverse ? columns.Reverse().ToArray() : columns, rows, DateTimeOffset.UtcNow);
            }
            var pb=Make(false);var pa=Make(run%2==0);var sb=Make(true);var sa=Make(false);
            var spec=new TableSpec("public","large",["id"],["ignored"]);
            Assert.AreEqual(JsonSerializer.Serialize(OriginalCompare(pb,pa,sb,sa,spec)),JsonSerializer.Serialize(Engine.Compare(pb,pa,sb,sa,spec)));
        }
    }

    [TestMethod]
    public void SearchConditionsQuoteIdentifiersAndBindTypedValues()
    {
        foreach(var pg in new[]{true,false})
        {
            using DbConnection connection=pg?new NpgsqlConnection():new SqlConnection();
            var spec=new TableSpec("schema","table",["id"],[],new("odd\"column]","=","x'; DELETE FROM test; --"));
            using var command=Engine.CreateSelectCommand(connection,pg,spec);
            Assert.IsFalse(command.CommandText.Contains("DELETE"));
            Assert.AreEqual("x'; DELETE FROM test; --",command.Parameters[0].Value);
            StringAssert.Contains(command.CommandText,pg?"\"odd\"\"column]\"":"[odd\"column]]]");
            using var range=Engine.CreateSelectCommand(connection,pg,spec with{Filter=new("id","範囲","10","整数","20")});
            Assert.AreEqual(10L,range.Parameters[0].Value);
            Assert.AreEqual(20L,range.Parameters[1].Value);
            StringAssert.Contains(range.CommandText,"BETWEEN @filterValue AND @filterUpper");
            Assert.ThrowsExactly<InvalidOperationException>(()=>Engine.CreateSelectCommand(connection,pg,spec with{Filter=new("id","= 1; DELETE","10")}));
            Assert.ThrowsExactly<FormatException>(()=>Engine.CreateSelectCommand(connection,pg,spec with{Filter=new("id","=","abc","整数")}));
            using var all=Engine.CreateSelectCommand(connection,pg,spec with{Filter=null});
            Assert.IsFalse(all.CommandText.Contains("WHERE"));
        }
    }

    [TestMethod]
    public void ProjectionAndMultipleFiltersUseSamePredicateForCaptureAndCount()
    {
        foreach(var pg in new[]{true,false})foreach(var join in new[]{"AND","OR"}) {
            using DbConnection db=pg?new NpgsqlConnection():new SqlConnection();
            var spec=new TableSpec("schema","table",["id","detail_id"],[],Columns:["value","id","value"],
                Filters:[new("RegisterDate","範囲","2026-10-01","日時","2026-10-07"),new("AdminUser","=","x'; DROP TABLE test; --")],FilterJoin:join);
            using var select=Engine.CreateSelectCommand(db,pg,spec);using var count=Engine.CreateCountCommand(db,pg,spec);
            StringAssert.StartsWith(select.CommandText,pg?"SELECT \"value\", \"id\", \"detail_id\" FROM":"SELECT [value], [id], [detail_id] FROM");
            Assert.AreEqual(select.CommandText[select.CommandText.IndexOf(" WHERE ")..],count.CommandText[count.CommandText.IndexOf(" WHERE ")..]);
            StringAssert.Contains(select.CommandText," "+join+" ");StringAssert.Contains(select.CommandText,"@filterValue1");
            Assert.AreEqual(3,select.Parameters.Count);Assert.AreEqual(3,count.Parameters.Count);
            Assert.IsInstanceOfType<DateTime>(select.Parameters[0].Value);Assert.AreEqual("x'; DROP TABLE test; --",select.Parameters[2].Value);
            Assert.IsFalse(select.CommandText.Contains("DROP"));
            Assert.ThrowsExactly<InvalidOperationException>(()=>Engine.CreateSelectCommand(db,pg,spec with{FilterJoin="OR 1=1"}));
            using var all=Engine.CreateSelectCommand(db,pg,spec with{Columns=[],Filters=[]});
            StringAssert.StartsWith(all.CommandText,"SELECT * FROM ");Assert.IsFalse(all.CommandText.Contains("WHERE"));
        }
    }

    [TestMethod]
    public void EvidenceIncludesEveryFilterAndJoin()
    {
        var d=DemoEvidence.Create();var spec=d.Spec with{Filters=[new("RegisterDate",">=","2026-10-01","日時"),new("AdminUser","=","tester")],FilterJoin="OR"};
        var output=Engine.Render(d.PgBefore.Columns,[],spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter);
        StringAssert.Contains(output.Text,"(RegisterDate >= 2026-10-01) OR (AdminUser = tester)");
    }

    [TestMethod]
    public void MixedAndOrUseSqlPrecedenceAndSameCountPredicate()
    {
        foreach(var pg in new[]{true,false}) {
            using DbConnection db=pg?new NpgsqlConnection():new SqlConnection();
            var spec=new TableSpec("public","table",["id"],[],Filters:[new("a","=","1","整数"),new("b","範囲","2","整数","3"),new("c","=","4","整数")],FilterJoins:["OR","AND"]);
            using var select=Engine.CreateSelectCommand(db,pg,spec);using var count=Engine.CreateCountCommand(db,pg,spec);
            var where=select.CommandText[select.CommandText.IndexOf(" WHERE ")..];
            Assert.AreEqual(where,count.CommandText[count.CommandText.IndexOf(" WHERE ")..]);
            Assert.AreEqual(pg?" WHERE (\"a\" = @filterValue) OR (\"b\" BETWEEN @filterValue1 AND @filterUpper1) AND (\"c\" = @filterValue2)":" WHERE ([a] = @filterValue) OR ([b] BETWEEN @filterValue1 AND @filterUpper1) AND ([c] = @filterValue2)",where);
            Assert.AreEqual(4,select.Parameters.Count);
            foreach(var joins in new[]{new[]{"OR"},new[]{"OR","AND 1=1"}})
                Assert.ThrowsExactly<InvalidOperationException>(()=>Engine.CreateSelectCommand(db,pg,spec with{FilterJoins=joins}));
            var d=DemoEvidence.Create();var rendered=Engine.Render(d.PgBefore.Columns,[],spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter);
            StringAssert.Contains(rendered.Text,"(a = 1) OR (b 範囲 2〜3) AND (c = 4)");
        }
    }

    [TestMethod]
    public void SpreadsheetOnlyRenderingHasSameCellsAndStylesWithoutAuxiliaryFormats()
    {
        var d=DemoEvidence.Create();var rows=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
        var full=Engine.Render(d.PgBefore.Columns,rows,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter);
        var xmlOnly=Engine.Render(d.PgBefore.Columns,rows,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true);
        Assert.AreEqual(full.SpreadsheetXml,xmlOnly.SpreadsheetXml);
        Assert.AreEqual("",xmlOnly.Html);Assert.AreEqual("",xmlOnly.Text);
    }

    [TestMethod]
    public void CountsUseExactAggregatesAndKeepFilterParameters()
    {
        foreach(var pg in new[]{true,false})
        {
            using DbConnection connection=pg?new NpgsqlConnection():new SqlConnection();
            var spec=new TableSpec("public","table",["id"],[],new("id","範囲","1","整数","99"));
            using var command=Engine.CreateCountCommand(connection,pg,spec);
            Assert.AreEqual(2,command.Parameters.Count);
            Assert.AreEqual(1L,command.Parameters["filterValue"].Value);
            Assert.AreEqual(99L,command.Parameters["filterUpper"].Value);
            StringAssert.Contains(command.CommandText,pg?"SELECT COUNT(*) FROM":"SELECT COUNT_BIG(*) FROM");
            Assert.IsFalse(command.CommandText.Contains("LIMIT"));Assert.IsFalse(command.CommandText.Contains("TOP"));
            StringAssert.Contains(command.CommandText,"WHERE");
        }
    }

    [TestMethod]
    public void ComparisonAndRenderingCanBeCancelled()
    {
        var d=DemoEvidence.Create();var token=new CancellationToken(true);
        Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec,token));
        var rows=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
        Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Render(d.PgBefore.Columns,rows,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true,token));
    }

    [TestMethod]
    public void LargeParallelComparisonPreservesOneSidedChangesAndCancellation()
    {
        var rows=new Dictionary<string,string?[]>(600000,StringComparer.Ordinal);
        for(var i=0;i<600000;i++){var id=i.ToString();rows.Add("[\""+id+"\"]",[id,"old"]);}
        var before=new Snapshot(["id","value"],rows,DateTimeOffset.UtcNow);
        var pgRows=new Dictionary<string,string?[]>(rows,StringComparer.Ordinal);
        var sqlRows=new Dictionary<string,string?[]>(rows,StringComparer.Ordinal);
        pgRows["[\"0\"]"]=["0","new"];pgRows.Remove("[\"1\"]");sqlRows.Remove("[\"2\"]");
        pgRows.Add("[\"600001\"]",["600001","PG"]);sqlRows.Add("[\"600002\"]",["600002","SQL"]);
        var spec=new TableSpec("public","parallel",["id"],[]);
        var pg=before with{Rows=pgRows};var sql=before with{Rows=sqlRows};
        var result=Engine.Compare(before,pg,before,sql,spec);
        Assert.AreEqual(5,result.Count);Assert.IsTrue(result.All(e=>!e.Match));
        Assert.AreEqual("更新",result.Single(e=>e.Key=="[\"0\"]").Pg.Operation);
        Assert.AreEqual("変更なし",result.Single(e=>e.Key=="[\"0\"]").Sql.Operation);
        Assert.AreEqual("削除",result.Single(e=>e.Key=="[\"1\"]").Pg.Operation);
        Assert.AreEqual("削除",result.Single(e=>e.Key=="[\"2\"]").Sql.Operation);
        Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(before,pg,before,sql,spec,new CancellationToken(true)));
    }

    [TestMethod,TestCategory("Performance")]
    public void MeasureLargeComparisonAgainstPreviousImplementation()
    {
        if(Environment.GetEnvironmentVariable("EVIDENCE_RUN_PERFORMANCE")!="1")Assert.Inconclusive("明示的な性能検証時だけ実行します。");
        var count=int.TryParse(Environment.GetEnvironmentVariable("EVIDENCE_PERFORMANCE_ROWS"),out var configured)?configured:1_000_000;
        if(count<10_000||count>3_000_000)throw new InvalidOperationException("性能検証は1万〜300万行です。");
        var columns=Enumerable.Range(0,16).Select(i=>i==0?"id":"column"+i).ToArray();
        var rows=new Dictionary<string,string?[]>(count,StringComparer.Ordinal);
        for(var i=0;i<count;i++)
        {
            var id=i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var row=new string?[columns.Length];row[0]=id;for(var c=1;c<row.Length;c++)row[c]="unchanged";
            rows.Add("[\""+id+"\"]",row);
        }
        var before=new Snapshot(columns,rows,DateTimeOffset.UtcNow);
        var afterRows=new Dictionary<string,string?[]>(rows,StringComparer.Ordinal);
        for(var i=0;i<1000;i++){var key="[\""+i+"\"]";var changed=rows[key].ToArray();changed[1]="changed";afterRows[key]=changed;}
        for(var i=1000;i<2000;i++)afterRows.Remove("[\""+i+"\"]");
        for(var i=count;i<count+1000;i++){var row=rows["[\"0\"]"].ToArray();row[0]=i.ToString();afterRows.Add("[\""+i+"\"]",row);}
        var after=before with{Rows=afterRows};var spec=new TableSpec("public","benchmark",["id"],[]);
        (List<Evidence> Result,double Seconds,long Bytes) Measure(Func<List<Evidence>> work)
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            var allocated=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();var result=work();watch.Stop();
            return(result,watch.Elapsed.TotalSeconds,GC.GetTotalAllocatedBytes(true)-allocated);
        }
        // 双方のDBで同じ読み取り専用データを共有。DB転送・スナップショット保持量は計測しない。
        var original=Measure(()=>OriginalCompare(before,after,before,after,spec));
        var optimized=Measure(()=>Engine.Compare(before,after,before,after,spec));
        Assert.AreEqual(JsonSerializer.Serialize(original.Result),JsonSerializer.Serialize(optimized.Result));
        Assert.AreEqual(3000,optimized.Result.Count);
        Assert.IsTrue(optimized.Bytes<original.Bytes/2,"比較中の割り当てを少なくとも半減すること。");
        var report=$"Rows per snapshot: {count:N0}; Columns: 16; Changed keys: 3000\nPrevious: {original.Seconds:F3}s, allocated {original.Bytes/1048576d:F1} MiB\nOptimized: {optimized.Seconds:F3}s, allocated {optimized.Bytes/1048576d:F1} MiB\nSpeedup: {original.Seconds/optimized.Seconds:F2}x\nSynthetic comparison only; excludes DB capture, retained snapshots, Excel rendering.\n";
        TestContext.WriteLine(report);
        var reportPath=Environment.GetEnvironmentVariable("EVIDENCE_PERFORMANCE_REPORT");
        if(reportPath is not null)File.WriteAllText(reportPath,report);
    }
    [TestMethod,TestCategory("HundredMillion")]
    public void MeasureOneHundredMillionDistinctKeysInBoundedBatches()
    {
        if(Environment.GetEnvironmentVariable("EVIDENCE_RUN_HUNDRED_MILLION")!="1")Assert.Inconclusive("1億件性能検証は明示的な実行時だけ有効です。");
        const int total=100_000_000,batchSize=1_000_000;
        var columns=Enumerable.Range(0,16).Select(i=>i==0?"id":"column"+i).ToArray();
        var spec=new TableSpec("public","hundred_million",["id"],[]);
        var whole=Stopwatch.StartNew();double generation=0,comparison=0;
        long allocated=0,compared=0,changed=0,peakWorkingSet=0,peakManaged=0;
        for(var start=0;start<total;start+=batchSize)
        {
            var generate=Stopwatch.StartNew();
            var beforeRows=new Dictionary<string,string?[]>(batchSize,StringComparer.Ordinal);
            var afterRows=new Dictionary<string,string?[]>(batchSize,StringComparer.Ordinal);
            for(var i=0;i<batchSize;i++)
            {
                var id=(start+i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var key="[\""+id+"\"]";var row=new string?[16];row[0]=id;
                for(var c=1;c<16;c++)row[c]="unchanged";
                beforeRows.Add(key,row);
                if(i>=10&&i<20)continue;
                var next=row.ToArray();if(i<10)next[1]="changed";
                afterRows.Add(key,next);
            }
            for(var i=0;i<10;i++)
            {
                var id=(total+start/batchSize*10+i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var row=new string?[16];row[0]=id;for(var c=1;c<16;c++)row[c]="added";
                afterRows.Add("[\""+id+"\"]",row);
            }
            var before=new Snapshot(columns,beforeRows,DateTimeOffset.UtcNow);
            var after=before with{Rows=afterRows};generate.Stop();generation+=generate.Elapsed.TotalSeconds;
            var allocationStart=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();
            var result=Engine.Compare(before,after,before,after,spec);watch.Stop();
            comparison+=watch.Elapsed.TotalSeconds;allocated+=GC.GetTotalAllocatedBytes(true)-allocationStart;
            Assert.AreEqual(30,result.Count);Assert.IsTrue(result.All(e=>e.Match));
            Assert.AreEqual(10,result.Count(e=>e.Pg.Operation=="追加"));
            Assert.AreEqual(10,result.Count(e=>e.Pg.Operation=="削除"));
            Assert.AreEqual(10,result.Count(e=>e.Pg.Operation=="更新"));
            compared+=batchSize;changed+=result.Count;
            peakManaged=Math.Max(peakManaged,GC.GetTotalMemory(false));
            peakWorkingSet=Math.Max(peakWorkingSet,Process.GetCurrentProcess().PeakWorkingSet64);
            if(compared%10_000_000==0){
                var message=$"Compared {compared:N0}/{total:N0}; comparison {comparison:F3}s; elapsed {whole.Elapsed.TotalSeconds:F1}s";
                TestContext.WriteLine(message);
                var progressPath=Environment.GetEnvironmentVariable("EVIDENCE_HUNDRED_MILLION_REPORT");
                if(progressPath is not null)File.WriteAllText(progressPath+".progress",message);
            }
        }
        Assert.AreEqual(100_000_000L,compared);Assert.AreEqual(3000L,changed);
        var report=$"Runtime processors: {Environment.ProcessorCount}\nActual distinct before keys processed: {compared:N0} per DB\nLogical snapshots: PG before/after + SQL Server before/after (400 million logical records); PG/SQL reuse the same immutable fixture dictionaries\nBatch size: {batchSize:N0}; batches: {total/batchSize}; columns: 16\nAfter rows use separate arrays (no unchanged-row reference shortcut); equal cell strings are shared\nChanged keys: {changed:N0} (1000 updates, 1000 deletes, 1000 adds)\nData generation: {generation:F3}s\nComparison sum: {comparison:F3}s\nTotal elapsed: {whole.Elapsed.TotalSeconds:F3}s\nComparison allocated: {allocated/1048576d:F3} MiB\nProcess peak working set: {peakWorkingSet/1048576d:F1} MiB\nSampled managed-memory maximum: {peakManaged/1048576d:F1} MiB\nMeasured every batch; no extrapolation. In-memory partition comparison only. Excludes DB capture/count/network, partition creation/spill/merge, retained 100-million-row snapshots, and Excel rendering. Does NOT establish full-table application support for 100 million rows.\n";
        TestContext.WriteLine(report);
        var path=Environment.GetEnvironmentVariable("EVIDENCE_HUNDRED_MILLION_REPORT");
        if(path is not null)File.WriteAllText(path,report);
    }
 private static List<Evidence> OriginalCompare(Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,TableSpec spec) {
 var columns=pb.Columns;
 foreach(var s in new[]{pa,sb,sa}) if(!columns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(s.Columns)) throw new InvalidOperationException("列構成が一致しません。");
 Snapshot Align(Snapshot s) => new(columns,s.Rows.ToDictionary(x=>x.Key,x=>columns.Select(c=>x.Value[Array.FindIndex(s.Columns,v=>string.Equals(v,c,StringComparison.OrdinalIgnoreCase))]).ToArray()),s.At);
 pa=Align(pa); sb=Align(sb); sa=Align(sa);
 var active=Enumerable.Range(0,columns.Length).Where(i=>!spec.Ignored.Contains(columns[i],StringComparer.OrdinalIgnoreCase)).ToArray();
 Change Get(string k,Snapshot b,Snapshot a) {b.Rows.TryGetValue(k,out var before);a.Rows.TryGetValue(k,out var after);var changed=active.Where(i=>before==null||after==null||before[i]!=after[i]).ToArray();return new(k,before==null?(after==null?"変更なし":"追加"):after==null?"削除":changed.Length>0?"更新":"変更なし",before,after,changed);}
 var result=new List<Evidence>();
 foreach(var k in pb.Rows.Keys.Concat(pa.Rows.Keys).Concat(sb.Rows.Keys).Concat(sa.Rows.Keys).Distinct().Order(StringComparer.Ordinal)) {
 var p=Get(k,pb,pa); var s=Get(k,sb,sa); if(p.Operation=="変更なし"&&s.Operation=="変更なし") continue;
 var diff=p.Operation!=s.Operation?active:active.Where(i=>p.After?[i]!=s.After?[i]).ToArray();
 result.Add(new(k,p,s,p.Operation==s.Operation&&diff.Length==0,diff)); }
 if(result.Count(e=>e.Pg.Operation!="変更なし")!=result.Count(e=>e.Sql.Operation!="変更なし"))
 for(var i=0;i<result.Count;i++)result[i]=result[i] with{Match=false,Different=active};
 return result;
 }
}
