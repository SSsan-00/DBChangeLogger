using System.Text.Json;
using System.Xml.Linq;
using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DbEvidenceTests;

[TestClass]
public class ComparisonSettingsTests {
 static readonly string[] Columns=["id","order_no","line","name","amount","stamp"];
 static readonly TableSpec Spec=new("public","orders",["id"],["stamp"],MatchKeys:["order_no","line"],ComparisonIgnored:["id"]);
 static Snapshot Snap(params string?[][] rows)=>new(Columns,rows.ToDictionary(r=>JsonSerializer.Serialize(new[]{r[0]}),r=>r),DateTimeOffset.UnixEpoch);
 static (Snapshot Pb,Snapshot Pa,Snapshot Sb,Snapshot Sa) Updates()=>
  (Snap(["123","ORD-001","1","商品A","10","0"],["124","ORD-001","2","商品B","30","0"]),
   Snap(["123","ORD-001","1","商品A","20","1"],["124","ORD-001","2","商品B","40","1"]),
   Snap(["987","ORD-001","1","旧名","15","8"],["988","ORD-001","2","商品B","35","8"]),
   Snap(["987","ORD-001","1","商品A","20","9"],["988","ORD-001","2","商品B","40","9"]));
 static XElement[] Render(Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,TableSpec? spec=null) {
  spec??=Spec;return XDocument.Parse(Engine.Render(pb.Columns,Engine.Compare(pb,pa,sb,sa,spec),spec,pb,pa,sb,sa,true).SpreadsheetXml).Descendants(Ss+"Row").ToArray();
 }
 static readonly XNamespace Ss="urn:schemas-microsoft-com:office:spreadsheet";
 static string[] Values(XElement row)=>row.Descendants(Ss+"Data").Select(c=>c.Value).ToArray();

 [TestMethod]
 public void DifferentSequencesMatchByCompositeBusinessKeyAndKeepColumnColors() {
  var (pb,pa,sb,sa)=Updates();var result=Engine.Compare(pb,pa,sb,sa,Spec);
  Assert.AreEqual(2,result.Count);Assert.IsTrue(result.All(e=>e.Match));
  Assert.AreEqual("[\"123\"]",result[0].Pg.Key);Assert.AreEqual("[\"987\"]",result[0].Sql.Key);
  var rows=Render(pb,pa,sb,sa);CollectionAssert.AreEqual(new[]{"判定","","除外","◯","◯","◯","◯","除外"},Values(rows[^1]));
  Assert.AreEqual(1,rows.Count(r=>Values(r).FirstOrDefault()=="判定"));
  Assert.AreEqual("Cfff2cc",(string?)rows.Single(r=>Values(r).ElementAtOrDefault(2)=="123").Elements(Ss+"Cell").ElementAt(6).Attribute(Ss+"StyleID"));
  Assert.AreEqual("Cffffff",(string?)rows.Single(r=>Values(r).ElementAtOrDefault(2)=="123").Elements(Ss+"Cell").ElementAt(5).Attribute(Ss+"StyleID"));
  Assert.IsTrue(rows.Any(r=>Values(r)[0].StartsWith("DB間の対応列:")));
 }
 [TestMethod]
 public void OneMismatchingValueAggregatesOnlyItsColumnAcrossMultipleRows() {
  var (pb,pa,sb,sa)=Updates();sa.Rows["[\"988\"]"][4]="41";
  var result=Engine.Compare(pb,pa,sb,sa,Spec);Assert.AreEqual(1,result.Count(e=>!e.Match));CollectionAssert.AreEqual(new[]{4},result.Single(e=>!e.Match).Different);
  CollectionAssert.AreEqual(new[]{"判定","","除外","◯","◯","◯","×","除外"},Values(Render(pb,pa,sb,sa)[^1]));
  var reordered=sa with{Columns=Columns.Reverse().ToArray(),Rows=sa.Rows.ToDictionary(r=>r.Key,r=>r.Value.Reverse().ToArray())};
  CollectionAssert.AreEqual(result.Single(e=>!e.Match).Different,Engine.Compare(pb,pa,sb,reordered,Spec).Single(e=>!e.Match).Different);
 }
 [TestMethod]
 public void MissingBusinessTargetOrDifferentOperationRejectsAllJudgedColumns() {
  var (pb,pa,sb,sa)=Updates();var other=Snap(["987","OTHER","1","商品A","20","9"],["988","OTHER","2","商品B","40","9"]);
  var otherBefore=other with{Rows=other.Rows.ToDictionary(r=>r.Key,r=>{var row=r.Value.ToArray();row[4]="10";return row;})};
  Assert.IsTrue(Engine.Compare(pb,pa,otherBefore,other,Spec).All(e=>!e.Match&&e.Different.SequenceEqual(new[]{1,2,3,4})));
  Assert.IsTrue(Engine.Compare(Snap(),pa,sb,sa,Spec).All(e=>!e.Match));
  var judgment=Values(Render(pb,pa,sb,sb)[^1]);Assert.IsTrue(judgment.Skip(2).All(v=>v=="×"));
  var row=Render(pb,pa,sb,sb).Single(r=>Values(r).FirstOrDefault()=="SQL Server");Assert.IsTrue(Values(row).Skip(2).All(string.IsNullOrEmpty));
 }
 [TestMethod]
 public void DeletionAndReplacementPreserveBothOperationsWithoutDisplayingOldIds() {
  var (pb,_,sb,_)=Updates();var pa=Snap(["555","ORD-001","1","商品A","20","1"],["124","ORD-001","2","商品B","30","0"]);
  var sa=Snap(["777","ORD-001","1","商品A","20","9"],["988","ORD-001","2","商品B","35","8"]);
  var changes=Engine.Compare(pb,pa,sb,sa,Spec);Assert.AreEqual(2,changes.Count);Assert.IsTrue(changes.All(e=>e.Match));
  CollectionAssert.AreEquivalent(new[]{"追加","削除"},changes.Select(e=>e.Pg.Operation).ToArray());
  var rows=Render(pb,pa,sb,sa);Assert.IsTrue(rows.Where(r=>Values(r).ElementAtOrDefault(1)=="削除").All(r=>Values(r).Skip(2).All(v=>v=="〈行なし〉")));
  Assert.IsTrue(Engine.Compare(pb,Snap(),sb,Snap(),Spec).All(e=>e.Match));
 }
 [TestMethod]
 public void BusinessKeysRejectNullDuplicatesUnknownColumnsAndKeyChanges() {
  var (pb,pa,sb,sa)=Updates();
  foreach(var invalid in new[]{
   Snap(["1",null,"1","a","1","0"]),
   Snap(["1","A","1","a","1","0"],["2","A","1","b","2","0"])})
   Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(invalid,invalid,sb,sa,Spec));
  foreach(var spec in new[]{Spec with{MatchKeys=[]},Spec with{MatchKeys=["missing"]},Spec with{MatchKeys=["line","LINE"]},Spec with{Ignored=["order_no"]},Spec with{ComparisonIgnored=["missing"]}})
   Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(pb,pa,sb,sa,spec));
  pa.Rows["[\"123\"]"][1]="changed";
  Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(pb,pa,sb,sa,Spec));
  Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(pb,pa,sb,sa,Spec,new CancellationToken(true)));
 }
 [TestMethod]
 public void KeylessTablesUseBusinessIdentityAndPrimaryKeysCanDifferInDefinition() {
  foreach(var (pgKeys,sqlKeys) in new[]{(Array.Empty<string>(),Array.Empty<string>()),(new[]{"id"},Array.Empty<string>()),(new[]{"id"},new[]{"sql_id"})}) {
   var table=new CommonTable(new("orders",pgKeys),new("orders",sqlKeys));var (p,s)=table.Specs([],null,["order_no","line"],["id"]);
   Assert.AreEqual(pgKeys.Length==0,p.BusinessIdentity);Assert.AreEqual(sqlKeys.Length==0,s.BusinessIdentity);
   CollectionAssert.AreEqual(pgKeys.Length==0?new[]{"order_no","line"}:pgKeys,p.Keys);
   CollectionAssert.AreEqual(sqlKeys.Length==0?new[]{"order_no","line"}:sqlKeys,s.Keys);
  }
  var keyless=new CommonTable(new("orders",[]),new("orders",[])).Specs([],null,["order_no","line"]);
  Snapshot Keyless(string amount)=>new(["order_no","line","amount"],new(){["[\"A\",\"1\"]"]=["A","1",amount]},DateTimeOffset.UnixEpoch);
  Assert.IsTrue(Engine.Compare(Keyless("1"),Keyless("2"),Keyless("1"),Keyless("2"),keyless.Pg).Single().Match);
  Assert.AreEqual(2,Engine.Compare(Keyless("1"),Keyless("2") with{Rows=new(){["[\"B\",\"1\"]"]=["B","1","2"]}},Keyless("1"),Keyless("1"),keyless.Pg).Count);
 }
 [TestMethod]
 public void ProjectionAddsMatchingColumnsAndLegacySpecsKeepDefaultBehavior() {
  foreach(var pg in new[]{true,false}) {
   using System.Data.Common.DbConnection db=pg?new Npgsql.NpgsqlConnection():new Microsoft.Data.SqlClient.SqlConnection();
   using var query=Engine.CreateSelectCommand(db,pg,Spec with{Columns=["amount"]});
   foreach(var name in new[]{"amount","id","order_no","line"})StringAssert.Contains(query.CommandText,pg?'"'+name+'"':'['+name+']');
  }
  var spec=JsonSerializer.Deserialize<TableSpec>("{\"Schema\":\"public\",\"Name\":\"x\",\"Keys\":[\"id\"],\"Ignored\":[]}")!;
  Assert.IsNull(spec.MatchKeys);Assert.IsNull(spec.ComparisonIgnored);Assert.IsFalse(spec.BusinessIdentity);
  var (pb,pa,sb,sa)=Updates();Assert.IsTrue(Engine.Compare(pb,pa,sb,sa,Spec with{MatchKeys=null,ComparisonIgnored=null}).All(e=>!e.Match));
 }

 [TestMethod,TestCategory("Integration")]
 public async Task RealDatabasesSupportDifferentSequencesAndKeylessTables() {
  if(Environment.GetEnvironmentVariable("EVIDENCE_RUN_BUSINESS_INTEGRATION")!="1")Assert.Inconclusive("明示した検証DBだけで実行します。");
  var pg=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_PG")??throw new InvalidOperationException("PG検証接続先が必要です。");
  var sql=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_SQL")??throw new InvalidOperationException("SQL検証接続先が必要です。");
  var name="business_keys_"+Guid.NewGuid().ToString("N");
  async Task Execute(bool p,string query) {
   await using System.Data.Common.DbConnection db=p?new Npgsql.NpgsqlConnection(pg):new Microsoft.Data.SqlClient.SqlConnection(sql);
   await db.OpenAsync();await using var command=db.CreateCommand();command.CommandText=query;await command.ExecuteNonQueryAsync();
  }
  try {
   foreach(var p in new[]{true,false}) {
    var table=(p?"public.":"dbo.")+name;
    await Execute(p,$"CREATE TABLE {table}(id int PRIMARY KEY,order_no varchar(40),line int,name varchar(40),amount decimal(12,2),stamp int); INSERT INTO {table} VALUES({(p?123:987)},'ORD-001',1,'A',10,0),({(p?124:988)},'ORD-001',2,'B',30,0);");
    await Execute(p,$"CREATE TABLE {table}_heap(id int,order_no varchar(40),line int,name varchar(40),amount decimal(12,2),stamp int); INSERT INTO {table}_heap SELECT * FROM {table};");
   }
   var common=TableCatalog.Common(await TableCatalog.Read(true,pg),await TableCatalog.Read(false,sql));
   var (pSpec,sSpec)=common.Single(t=>t.Postgres.Name==name).Specs(["stamp"],null,["order_no","line"],["id"]);
   pSpec=pSpec with{Definition=await TableCatalog.ReadColumns(true,pg,name)};sSpec=sSpec with{Definition=await TableCatalog.ReadColumns(false,sql,name)};
   var before=await Engine.CapturePair(pg,sql,pSpec,sSpec);
   var primaryBefore=before;
   foreach(var p in new[]{true,false})await Execute(p,$"UPDATE {(p?"public.":"dbo.")}{name} SET amount=amount+10,stamp=stamp+1;");
   var after=await Engine.CapturePair(pg,sql,pSpec,sSpec,before.Pg,before.Sql);
   Assert.IsTrue(Engine.Compare(before.Pg,after.Pg,before.Sql,after.Sql,pSpec).All(e=>e.Match));
   await Execute(false,$"UPDATE dbo.{name} SET amount=41 WHERE line=2;");
   after=await Engine.CapturePair(pg,sql,pSpec,sSpec,before.Pg,before.Sql);
   CollectionAssert.AreEqual(new[]{4},Engine.Compare(before.Pg,after.Pg,before.Sql,after.Sql,pSpec).Single(e=>!e.Match).Different);
   foreach(var p in new[]{true,false})await Execute(p,$"DELETE FROM {(p?"public.":"dbo.")}{name} WHERE line=1;");
   after=await Engine.CapturePair(pg,sql,pSpec,sSpec,before.Pg,before.Sql);
   Assert.IsTrue(Engine.Compare(before.Pg,after.Pg,before.Sql,after.Sql,pSpec).Single(e=>e.Pg.Operation=="削除").Match);
   var primaryAfter=after;
   var (heapPg,heapSql)=common.Single(t=>t.Postgres.Name==name+"_heap").Specs(["stamp"],null,["order_no","line"],["id"]);
   before=await Engine.CapturePair(pg,sql,heapPg,heapSql);
   foreach(var p in new[]{true,false})await Execute(p,$"UPDATE {(p?"public.":"dbo.")}{name}_heap SET amount=20 WHERE line=1;");
   after=await Engine.CapturePair(pg,sql,heapPg,heapSql,before.Pg,before.Sql);
   Assert.IsTrue(Engine.Compare(before.Pg,after.Pg,before.Sql,after.Sql,heapPg).Single().Match);
   await Execute(true,$"INSERT INTO public.{name}_heap VALUES(999,'ORD-001',1,'duplicate',20,0);");
   await Assert.ThrowsExactlyAsync<ComparisonConfigurationException>(()=>Engine.Capture(true,pg,heapPg));
   await Execute(false,$"INSERT INTO dbo.{name}_heap VALUES(999,NULL,3,'null',20,0);");
   await Assert.ThrowsExactlyAsync<ComparisonConfigurationException>(()=>Engine.Capture(false,sql,heapSql));
   await Execute(false,$"UPDATE dbo.{name} SET order_no='CHANGED' WHERE line=2;");
   // 主キーがある側では、対応列の更新を通常の値変更として照合しない。
   var current=await Engine.Capture(false,sql,sSpec);
   Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(primaryBefore.Pg,primaryAfter.Pg,primaryBefore.Sql,current,pSpec));
  }finally {
   foreach(var p in new[]{true,false})await Execute(p,$"DROP TABLE IF EXISTS {(p?"public.":"dbo.")}{name}_heap; DROP TABLE IF EXISTS {(p?"public.":"dbo.")}{name};");
  }
 }

 [TestMethod,TestCategory("Performance")]
 public void MeasureMillionRowBusinessComparisonIncludingValidation() {
  var report=Environment.GetEnvironmentVariable("EVIDENCE_BUSINESS_REPORT");if(report==null)Assert.Inconclusive("百万件の明示的な測定です。");
  var count=int.TryParse(Environment.GetEnvironmentVariable("EVIDENCE_BUSINESS_ROWS"),out var requested)?requested:1_000_000;
  if(count<1_000_000||count>3_000_000)throw new InvalidOperationException("100万〜300万件を指定してください。");
  var pRows=new Dictionary<string,string?[]>(count);var sRows=new Dictionary<string,string?[]>(count);
  for(var i=0;i<count;i++) {
   var id=i.ToString(System.Globalization.CultureInfo.InvariantCulture);var other=(i+count).ToString(System.Globalization.CultureInfo.InvariantCulture);
   var order="ORD-"+id;var p=new string?[]{id,order,"1","商品A","10","0"};var s=new string?[]{other,order,"1","商品A","10","0"};
   pRows.Add(JsonSerializer.Serialize(new[]{id}),p);sRows.Add(JsonSerializer.Serialize(new[]{other}),s);
  }
  var pNext=new Dictionary<string,string?[]>(pRows);var sNext=new Dictionary<string,string?[]>(sRows);
  foreach(var id in Enumerable.Range(0,count).Where(i=>i%1000==0)) {
   foreach(var (rows,next,key) in new[]{(pRows,pNext,JsonSerializer.Serialize(new[]{id.ToString()})),(sRows,sNext,JsonSerializer.Serialize(new[]{(id+count).ToString()}))}) {
    var row=rows[key].ToArray();row[4]="20";next[key]=row;
   }
  }
  var pb=new Snapshot(Columns,pRows,DateTimeOffset.UnixEpoch);var sb=new Snapshot(Columns,sRows,DateTimeOffset.UnixEpoch);var pa=pb with{Rows=pNext};var sa=sb with{Rows=sNext};
  var results=new List<object>();
  for(var run=0;run<3;run++) {
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var allocated=GC.GetTotalAllocatedBytes(true);var timer=System.Diagnostics.Stopwatch.StartNew();
   var changes=Engine.Compare(pb,pa,sb,sa,Spec);timer.Stop();var bytes=GC.GetTotalAllocatedBytes(true)-allocated;
   Assert.AreEqual((count+999)/1000,changes.Count);Assert.IsTrue(changes.All(e=>e.Match));
   results.Add(new{Seconds=timer.Elapsed.TotalSeconds,AllocatedBytes=bytes});
  }
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
  File.WriteAllText(report,JsonSerializer.Serialize(new{RowsPerDatabase=count,Columns=Columns.Length,ChangedRowsPerDatabase=(count+999)/1000,Measurements=results},new JsonSerializerOptions{WriteIndented=true}));
  // 百万件を超える並列経路でも、設定エラーの理由をAggregateExceptionに隠さず返す。
  var changed=pNext["[\"0\"]"].ToArray();changed[1]="CHANGED";pNext["[\"0\"]"]=changed;
  Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(pb,pa,sb,sa,Spec));
 }
}
