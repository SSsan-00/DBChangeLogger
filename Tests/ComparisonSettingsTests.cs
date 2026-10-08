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
 static Snapshot Heap(string[] columns,params string?[][] rows)=>new(columns,rows.Select((row,i)=>(row,i)).ToDictionary(r=>"row:"+r.i,r=>r.row),DateTimeOffset.UnixEpoch,true);

 [TestMethod]
 public void KeylessTablesCancelIdenticalRowsByCountRegardlessOfOrderAndIgnoredValues() {
  var columns=new[]{"code","value","stamp"};var spec=new TableSpec("public","heap",[],["stamp"],AutoMatch:true);
  var before=Heap(columns,["A","10","old"],[null,"",null],["A","10","duplicate"]);
  var after=Heap(columns,[null,"","different"],["A","10","new"],["A","10",null]);
  Assert.AreEqual(0,Engine.Compare(before,after,before,after,spec).Count);
  var fewer=Heap(columns,[null,"","different"],["A","10","new"]);
  var change=Engine.Compare(before,fewer,before,fewer,spec).Single();Assert.AreEqual("削除",change.Pg.Operation);Assert.IsTrue(change.Match);
  Assert.IsTrue(Engine.Compare(fewer,before,fewer,before,spec).Single().Match);
  Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(before,after,before,after,spec,new CancellationToken(true)));
 }

 [TestMethod]
 public void KeylessTablesInferUpdatesAndKeepSequenceAndDateDifferences() {
  var columns=new[]{"id","code","amount","date"};var spec=new TableSpec("public","heap",[],[],AutoMatch:true,
   Definition:[new("id","Int32","integer"),new("code","String","text"),new("amount","Decimal","numeric"),new("date","DateTime","timestamp")]);
  var pb=Heap(columns,["1","A","10","old"],["2","B","20","old"]);var pa=Heap(columns,["2","B","30","new"],["1","A","20","new"]);
  var sb=Heap(columns,["98","A","10","other"],["99","B","20","other"]);var sa=Heap(columns,["99","B","30","othernew"],["98","A","20","othernew"]);
  var result=Engine.Compare(pb,pa,sb,sa,spec);Assert.AreEqual(2,result.Count);Assert.IsTrue(result.All(c=>c.Pg.Operation=="更新"&&c.Sql.Operation=="更新"&&c.Different.SequenceEqual(new[]{0,3})));
  Assert.IsTrue(result.All(c=>c.Pg.After![1]==c.Sql.After![1]));
  var rows=Render(pb,pa,sb,sa,spec);CollectionAssert.AreEqual(new[]{"判定","","×","◯","◯","×"},Values(rows[^1]));
  Assert.AreEqual("ソート: code",Values(rows[1])[0]);
  var sqlSpec=spec with{Keys=["id"]};var mixed=spec.WithAutomaticMetadata(sqlSpec);
  Snapshot Primary(Snapshot snapshot)=>snapshot with{Keyless=false,Rows=snapshot.Rows.Values.ToDictionary(r=>JsonSerializer.Serialize(new[]{r[0]}),r=>r)};
  Assert.IsTrue(Engine.Compare(pb,pa,Primary(sb),Primary(sa),mixed).All(c=>c.Pg.Operation=="更新"&&c.Different.SequenceEqual(new[]{0,3})&&c.Sql.Before![0]==c.Sql.After![0]));
  var reversed=pa with{Columns=columns.Reverse().ToArray(),Rows=pa.Rows.ToDictionary(r=>r.Key,r=>r.Value.Reverse().ToArray())};
  CollectionAssert.AreEqual(result[0].Different,Engine.Compare(pb,reversed,sb,sa,spec)[0].Different);
 }

 [TestMethod]
 public void KeylessAmbiguousUpdatesCompareSortedFinalRows() {
  var columns=new[]{"id","name","amount"};var spec=new TableSpec("public","heap",[],[],AutoMatch:true,Definition:[new("id","Int32","integer"),new("name","String","text"),new("amount","Int32","integer")]);
  var pb=Heap(columns,["1","A","10"],["2","B","10"]);var pa=Heap(columns,["1","B","20"],["2","A","20"]);
  var result=Engine.Compare(pb,pa,pb,pa,spec);Assert.AreEqual(2,result.Count);Assert.IsTrue(result.All(c=>c.Match&&c.Different.Length==0));
  Assert.IsTrue(result.All(c=>c.Pg.Operation=="更新"&&c.Sql.Operation=="更新"&&c.Pg.Before==null&&c.Pg.Uncertain));
  CollectionAssert.AreEqual(pa.Rows.Values.Select(r=>string.Join("/",r)).ToArray(),result.Select(c=>string.Join("/",c.Pg.After!)).ToArray());
  var duplicate=Heap(columns,[null,"A","10"],[null,"A","10"]);var updated=Heap(columns,[null,"A","20"],[null,"A","20"]);
  Assert.AreEqual(2,Engine.Compare(duplicate,updated,duplicate,updated,spec).Count);
  Assert.IsTrue(Engine.Compare(duplicate,updated,duplicate,updated,spec).All(c=>c.Match));
  Assert.IsTrue(Values(Render(pb,pa,pb,pa,spec)[^1]).Skip(2).All(v=>v=="◯"));
  var rows=Render(pb,pa,pb,pa,spec);Assert.AreEqual(8,rows.Length);
  Assert.IsTrue(rows.Skip(3).Take(4).All(r=>Values(r)[1]=="更新"&&r.Elements(Ss+"Cell").Skip(2).All(c=>(string?)c.Attribute(Ss+"StyleID")=="Cfff2cc")));
  var more=Heap(columns,[null,"A","20"],[null,"A","20"],[null,"A","20"]);
  CollectionAssert.AreEquivalent(new[]{"更新","更新","追加"},Engine.Compare(duplicate,more,duplicate,more,spec).Select(c=>c.Pg.Operation).ToArray());
  CollectionAssert.AreEquivalent(new[]{"更新","更新","削除"},Engine.Compare(more,duplicate,more,duplicate,spec).Select(c=>c.Pg.Operation).ToArray());
  var allChanged=Heap(columns,["8","X","30"],["9","Y","40"]);
  Assert.IsTrue(Engine.Compare(pb,allChanged,pb,allChanged,spec).All(c=>c.Pg.Operation=="更新"&&c.Match));
 }

 [TestMethod]
 public void AutomaticSortingSkipsSingleRowsAndCapsWideTableKeysAtThree() {
  var spec=Spec with{MatchKeys=null,ComparisonIgnored=null,AutoMatch=true};
  var pb=Snap(["1","A","1","name","10","0"]);var pa=Snap(["1","A","1","name","20","0"]);
  var sb=Snap(["99","A","1","name","10","0"]);var sa=Snap(["99","A","1","name","21","0"]);
  var single=Engine.Compare(pb,pa,sb,sa,spec).Single();Assert.AreEqual(0,single.SortColumns!.Length);CollectionAssert.AreEqual(new[]{0,4},single.Different);
  foreach(var (p0,p1,s0,s1) in new[]{(pb,pa,sb,sa),(pb,pa,sb,sb),(pb,pb,sb,sa),(pb,pb,sb,sb)}) {
   Assert.IsTrue(Engine.Compare(p0,p1,s0,s1,spec).All(c=>c.SortColumns!.Length==0));
   Assert.IsFalse(Render(p0,p1,s0,s1,spec).Any(r=>Values(r)[0].StartsWith("ソート:")));
  }
  var columns=Enumerable.Range(0,400).Select(i=>"col"+i).ToArray();var wide=new TableSpec("public","wide",[],[],AutoMatch:true,Definition:columns.Select(n=>new TableColumn(n,"Int32","integer")).ToArray());
  string?[][] Rows(string amount)=>Enumerable.Range(0,16).Select(i=>Enumerable.Range(0,400).Select(c=>c<4?((i>>c)&1).ToString():c==4?amount:null).ToArray()).ToArray();
  var before=Heap(columns,Rows("0"));var after=Heap(columns,Rows("1"));var reversed=Heap(columns,Rows("1").Reverse().ToArray());
  var result=Engine.Compare(before,after,before,reversed,wide);Assert.AreEqual(16,result.Count);Assert.IsTrue(result.All(c=>c.Match));
  CollectionAssert.AreEqual(new[]{"col0","col1","col2"},result[0].SortColumns!);
  Assert.AreEqual("ソート: col0 → col1 → col2",Values(Render(before,after,before,reversed,wide)[1])[0]);
 }
 [TestMethod]
 public void AutomaticSortSelectsSingleAndCompositeKeysAndComparesPositions() {
  var columns=new[]{"id","order","line","amount","date"};
  var spec=new TableSpec("public","x",["id"],[],AutoMatch:true,Definition:[new("id","Int32","integer"),new("order","String","text"),new("line","Int32","integer"),new("amount","Decimal","numeric"),new("date","DateTime","date")]);
  Snapshot Rows(params string?[][] rows)=>new(columns,rows.ToDictionary(r=>r[0]!,r=>r),DateTimeOffset.UnixEpoch);
  var pb=Rows(["1","A","10","1","2026-01-01"],["2","B","2","1","2026-01-01"],["3","A","2","1","2026-01-01"],["4","B","10","1","2026-01-01"]);
  var pa=pb with{Rows=pb.Rows.ToDictionary(r=>r.Key,r=>new[]{r.Value[0],r.Value[1],r.Value[2],"2","2026-02-01"})};
  var sb=Rows(["98","B","10","1","2026-01-02"],["99","A","2","1","2026-01-02"],["96","B","2","1","2026-01-02"],["97","A","10","1","2026-01-02"]);
  var sa=sb with{Rows=sb.Rows.ToDictionary(r=>r.Key,r=>new[]{r.Value[0],r.Value[1],r.Value[2],r.Value[1]=="B"&&r.Value[2]=="10"?"9":"2","2026-02-02"})};
  var result=Engine.Compare(pb,pa,sb,sa,spec);
  CollectionAssert.AreEqual(new[]{"order","line"},result[0].SortColumns!);
  CollectionAssert.AreEqual(new[]{"A/2","A/10","B/2","B/10"},result.Select(c=>c.Pg.After![1]+"/"+c.Pg.After[2]).ToArray());
  Assert.IsTrue(result.Take(3).All(c=>c.Different.SequenceEqual(new[]{0,4})));CollectionAssert.AreEqual(new[]{0,3,4},result[^1].Different);
  Assert.AreEqual("ソート: order → line",Values(Render(pb,pa,sb,sa,spec)[1])[0]);
  var single=spec with{Definition=[new("line","Int32","integer")],Ignored=["order","amount","date"]};
  var p=Rows(["1","", "10","1",""],["2","",null,"1",""],["3","","2","1",""]);
  var s=Rows(["98","","2","1",""],["99","","10","1",""],["97","",null,"1",""]);
  var sorted=Engine.Compare(Rows(),p,Rows(),s,single);
  CollectionAssert.AreEqual(new[]{"line"},sorted[0].SortColumns!);CollectionAssert.AreEqual(new string?[]{null,"2","10"},sorted.Select(c=>c.Pg.After![2]).ToArray());
  var strings=single with{Ignored=["line","amount","date"],Definition=[new("order","String","text")]};
  var text=Rows(["1","A","","",""],["2","","","",""],["3",null,"","",""]);
  CollectionAssert.AreEqual(new string?[]{null,"","A"},Engine.Compare(Rows(),text,Rows(),text,strings).Select(c=>c.Pg.After![1]).ToArray());
  Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(pb,pa,sb,sa,spec,new CancellationToken(true)));
 }
 [TestMethod]
 public void AutomaticMatchingKeepsSequenceAndDateDifferencesInJudgment() {
  var columns=new[]{"id","name","amount","registered","version"};
  Snapshot Rows(params string?[][] rows)=>new(columns,rows.ToDictionary(r=>JsonSerializer.Serialize(new[]{r[0]}),r=>r),DateTimeOffset.UnixEpoch);
  var p=new TableSpec("public","auto",["id"],[],AutoMatch:true,Definition:[new("id","Int32","integer"),new("name","String","text"),new("amount","Decimal","numeric"),new("registered","DateTime","timestamp without time zone"),new("version","Byte[]","bytea")]);
  var s=p with{Schema="dbo",Definition=[new("version","Byte[]","timestamp")]};p=p.WithAutomaticMetadata(s);
  var pb=Rows(["1","A","10","old","01"],["2","B","10","old","01"]);var sb=Rows(["99","B","11","other","02"],["98","A","12","other","02"]);
  var pa=Rows(["1","A","20","date1","03"],["2","B","30","date1","03"]);var sa=Rows(["99","B","30","date2","04"],["98","A","20","date2","04"]);
  var changes=Engine.Compare(pb,pa,sb,sa,p);Assert.AreEqual(2,changes.Count);
  CollectionAssert.AreEqual(new[]{"[\"98\"]","[\"99\"]"},changes.Select(c=>c.Sql.Key).ToArray());
  Assert.IsTrue(changes.All(c=>!c.Match&&c.Different.SequenceEqual(new[]{0,3,4})));
  var rows=XDocument.Parse(Engine.Render(columns,changes,p,pb,pa,sb,sa,true).SpreadsheetXml).Descendants(Ss+"Row").ToArray();
  CollectionAssert.AreEqual(new[]{"判定","","×","◯","◯","×","×"},Values(rows[^1]));
  Assert.AreEqual("Cffc7ce",(string?)rows[^1].Elements(Ss+"Cell").ElementAt(2).Attribute(Ss+"StyleID"));
  Assert.AreEqual("ソート: name",Values(rows[1])[0]);
  var reversed=sa with{Columns=columns.Reverse().ToArray(),Rows=sa.Rows.ToDictionary(r=>r.Key,r=>r.Value.Reverse().ToArray())};
  CollectionAssert.AreEqual(changes[0].Different,Engine.Compare(pb,pa,sb,reversed,p)[0].Different);
  Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.Compare(pb,pa,sb,sa,p,new CancellationToken(true)));
 }

 [TestMethod]
 public void AutomaticSortingComparesDuplicateNullAndFallbackValuesByColumn() {
  var spec=Spec with{MatchKeys=null,ComparisonIgnored=null,AutoMatch=true};
  var pb=Snap(["1","A","1","same","10","0"],["2","A","1","same","10","0"]);
  var pa=Snap(["1","A","1","same","20","0"],["2","A","1","same","20","0"]);
  var sb=Snap(["98","A","1","same","10","0"],["99","A","1","same","10","0"]);
  var sa=Snap(["98","A","1","same","20","0"],["99","A","1","same","20","0"]);
  var result=Engine.Compare(pb,pa,sb,sa,spec);Assert.AreEqual(2,result.Count);Assert.IsTrue(result.All(c=>!c.Match&&c.Different.SequenceEqual(new[]{0})));
  sa.Rows["[\"99\"]"][4]="21";Assert.IsTrue(Engine.Compare(pb,pa,sb,sa,spec).All(c=>!c.Match));
  Assert.IsTrue(Engine.Compare(pb,pa,sb,sb,spec).All(c=>!c.Match&&c.Sql.Operation=="変更なし"));
  Assert.IsTrue(Values(Render(pb,pa,sb,sb,spec)[^1]).Skip(2).All(v=>v=="×"));
  var noMaterial=spec with{AutoMatchExcluded=Columns};var fallback=Engine.Compare(Snap(),pa,Snap(),sa,noMaterial);Assert.IsTrue(fallback.All(c=>!c.Match));CollectionAssert.AreEquivalent(new[]{0,4},fallback.SelectMany(c=>c.Different).Distinct().ToArray());
  var nulls=Snap(["1",null,null,null,null,"0"]);var nullOther=Snap(["9",null,null,null,null,"0"]);
  Assert.IsTrue(Engine.Compare(Snap(),nulls,Snap(),nullOther,spec).All(c=>!c.Match));
 }

 [TestMethod]
 public void AutomaticMatchingHandlesAddsDeletesAndDoesNotTreatDifferentOperationsAsEqual() {
  var spec=Spec with{MatchKeys=null,ComparisonIgnored=null,AutoMatch=true};
  var pg=Snap(["1","A","1","same","20","0"]);var sql=Snap(["9","A","1","same","20","0"]);
  foreach(var (pb,pa,sb,sa) in new[]{(Snap(),pg,Snap(),sql),(pg,Snap(),sql,Snap())}) {
   var change=Engine.Compare(pb,pa,sb,sa,spec).Single();CollectionAssert.AreEqual(new[]{0},change.Different);
   if(change.Pg.Operation=="削除")Assert.IsTrue(Render(pb,pa,sb,sa,spec).Where(r=>Values(r).ElementAtOrDefault(1)=="削除").All(r=>Values(r).Skip(2).All(v=>v=="〈行なし〉")));
  }
  Assert.IsTrue(Engine.Compare(pg,Snap(),Snap(),sql,spec).All(c=>!c.Match&&c.Different.Length==5));
  Assert.IsFalse(Engine.Compare(pg,Snap(),Snap(),sql,spec with{Ignored=Columns}).Single().Match);
  Assert.IsTrue(Engine.Compare(pg,Snap(),pg,Snap(),spec).Single().Match);
  Assert.IsTrue(Engine.Compare(Snap(),pg,Snap(),pg,spec).Single().Match);
 }

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

 [TestMethod,TestCategory("Integration")]
 public async Task RealDatabasesAutomaticallyPairRowsButRejectSequenceDateAndRowversionDifferences() {
  if(Environment.GetEnvironmentVariable("EVIDENCE_RUN_BUSINESS_INTEGRATION")!="1")Assert.Inconclusive("明示した検証DBだけで実行します。");
  var pg=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_PG")??throw new InvalidOperationException();
  var sql=Environment.GetEnvironmentVariable("EVIDENCE_CAPTURE_SQL")??throw new InvalidOperationException();var name="auto_match_"+Guid.NewGuid().ToString("N");
  async Task Execute(bool p,string query) {
   await using System.Data.Common.DbConnection db=p?new Npgsql.NpgsqlConnection(pg):new Microsoft.Data.SqlClient.SqlConnection(sql);
   await db.OpenAsync();await using var cmd=db.CreateCommand();cmd.CommandText=query;await cmd.ExecuteNonQueryAsync();
  }
  try {
   foreach(var p in new[]{true,false}) {
    var table=(p?"public.":"dbo.")+name;var version=p?"bytea DEFAULT decode('01','hex')":"rowversion";
    foreach(var suffix in new[]{"","_heap"})await Execute(p,$"CREATE TABLE {table}{suffix}(id int {(suffix.Length==0?"PRIMARY KEY":"")},name varchar(40),amount decimal(12,2),registered date,version {version}); INSERT INTO {table}{suffix}(id,name,amount,registered) VALUES({(p?1:99)},'A',10,'2026-10-{(p?8:9):00}'),({(p?2:98)},'B',10,'2026-10-{(p?8:9):00}');");
    await Execute(p,$"INSERT INTO {table}_heap(id,name,amount,registered) VALUES(NULL,NULL,NULL,NULL),(NULL,NULL,NULL,NULL);");
   }
   var pSpec=new TableSpec("public",name,["id"],[],AutoMatch:true,Definition:await TableCatalog.ReadColumns(true,pg,name));
   var sSpec=new TableSpec("dbo",name,["id"],[],AutoMatch:true,Definition:await TableCatalog.ReadColumns(false,sql,name));pSpec=pSpec.WithAutomaticMetadata(sSpec);sSpec=sSpec.WithAutomaticMetadata(pSpec);
   var before=await Engine.CapturePair(pg,sql,pSpec,sSpec);
   foreach(var p in new[]{true,false})await Execute(p,$"UPDATE {(p?"public.":"dbo.")}{name} SET amount=CASE name WHEN 'A' THEN 20 ELSE 30 END;");
   var after=await Engine.CapturePair(pg,sql,pSpec,sSpec,before.Pg,before.Sql);var result=Engine.Compare(before.Pg,after.Pg,before.Sql,after.Sql,pSpec);
   Assert.AreEqual(2,result.Count);Assert.IsTrue(result.All(c=>c.Different.SequenceEqual(new[]{0,3,4})));
   CollectionAssert.AreEqual(new[]{"[\"99\"]","[\"98\"]"},result.Select(c=>c.Sql.Key).ToArray());
   foreach(var p in new[]{true,false})await Execute(p,$"UPDATE {(p?"public.":"dbo.")}{name} SET name='SAME',amount=40;");
   var ambiguous=await Engine.CapturePair(pg,sql,pSpec,sSpec,before.Pg,before.Sql);
   Assert.IsTrue(Engine.Compare(before.Pg,ambiguous.Pg,before.Sql,ambiguous.Sql,pSpec).All(c=>c.Different.SequenceEqual(new[]{0,3,4})));
   foreach(var p in new[]{true,false})await Execute(p,$"DELETE FROM {(p?"public.":"dbo.")}{name};");
   var deleted=await Engine.CapturePair(pg,sql,pSpec,sSpec);
   Assert.IsTrue(Engine.Compare(after.Pg,deleted.Pg,after.Sql,deleted.Sql,pSpec).All(c=>c.Pg.Operation=="削除"&&c.Different.SequenceEqual(new[]{0,3,4})));
   var (hp,hs)=new CommonTable(new(name+"_heap",[]),new(name+"_heap",[])).AutomaticSpecs([]);
   hp=hp with{Definition=await TableCatalog.ReadColumns(true,pg,name+"_heap")};hs=hs with{Definition=await TableCatalog.ReadColumns(false,sql,name+"_heap")};hp=hp.WithAutomaticMetadata(hs);hs=hs.WithAutomaticMetadata(hp);
   var heapBefore=await Engine.CapturePair(pg,sql,hp,hs);Assert.AreEqual(4,heapBefore.Pg.Rows.Count);Assert.AreEqual(4,heapBefore.Sql.Rows.Count);Assert.IsTrue(heapBefore.Pg.Keyless&&heapBefore.Sql.Keyless);
   foreach(var p in new[]{true,false})await Execute(p,$"UPDATE {(p?"public.":"dbo.")}{name}_heap SET amount=CASE name WHEN 'A' THEN 20 ELSE 30 END WHERE id IS NOT NULL;");
   var heapAfter=await Engine.CapturePair(pg,sql,hp,hs,heapBefore.Pg,heapBefore.Sql);
   var heapChanges=Engine.Compare(heapBefore.Pg,heapAfter.Pg,heapBefore.Sql,heapAfter.Sql,hp);Assert.AreEqual(2,heapChanges.Count);Assert.IsTrue(heapChanges.All(c=>c.Pg.Operation=="更新"&&c.Sql.Operation=="更新"&&c.Different.SequenceEqual(new[]{0,3,4})));
  }finally{foreach(var p in new[]{true,false})await Execute(p,$"DROP TABLE IF EXISTS {(p?"public.":"dbo.")}{name}_heap;DROP TABLE IF EXISTS {(p?"public.":"dbo.")}{name};");}
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
  var results=new List<object>();var automaticResults=new List<object>();var keylessResults=new List<object>();
  for(var run=0;run<3;run++) {
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var allocated=GC.GetTotalAllocatedBytes(true);var timer=System.Diagnostics.Stopwatch.StartNew();
   var changes=Engine.Compare(pb,pa,sb,sa,Spec);timer.Stop();var bytes=GC.GetTotalAllocatedBytes(true)-allocated;
   Assert.AreEqual((count+999)/1000,changes.Count);Assert.IsTrue(changes.All(e=>e.Match));
   results.Add(new{Seconds=timer.Elapsed.TotalSeconds,AllocatedBytes=bytes});
   allocated=GC.GetTotalAllocatedBytes(true);timer.Restart();
   var automatic=Engine.Compare(pb,pa,sb,sa,Spec with{MatchKeys=null,ComparisonIgnored=null,AutoMatch=true});timer.Stop();bytes=GC.GetTotalAllocatedBytes(true)-allocated;
   Assert.AreEqual((count+999)/1000,automatic.Count);Assert.IsTrue(automatic.All(e=>e.Different.SequenceEqual(new[]{0})));
   automaticResults.Add(new{Seconds=timer.Elapsed.TotalSeconds,AllocatedBytes=bytes});
   var keylessSpec=Spec with{Keys=[],MatchKeys=null,ComparisonIgnored=null,AutoMatch=true,Definition=Columns.Select(c=>new TableColumn(c,c=="id"?"Int32":"String",c=="id"?"integer":"text")).ToArray()};
   allocated=GC.GetTotalAllocatedBytes(true);timer.Restart();
   var keyless=Engine.Compare(pb with{Keyless=true},pa with{Keyless=true},sb with{Keyless=true},sa with{Keyless=true},keylessSpec);timer.Stop();bytes=GC.GetTotalAllocatedBytes(true)-allocated;
   Assert.AreEqual((count+999)/1000,keyless.Count);Assert.IsTrue(keyless.All(e=>e.Pg.Operation=="更新"&&e.Different.SequenceEqual(new[]{0})));
   keylessResults.Add(new{Seconds=timer.Elapsed.TotalSeconds,AllocatedBytes=bytes});
  }
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
  File.WriteAllText(report,JsonSerializer.Serialize(new{RowsPerDatabase=count,Columns=Columns.Length,ChangedRowsPerDatabase=(count+999)/1000,Measurements=results,AutomaticMeasurements=automaticResults,KeylessMeasurements=keylessResults},new JsonSerializerOptions{WriteIndented=true}));
  // 百万件を超える並列経路でも、設定エラーの理由をAggregateExceptionに隠さず返す。
  var changed=pNext["[\"0\"]"].ToArray();changed[1]="CHANGED";pNext["[\"0\"]"]=changed;
  Assert.ThrowsExactly<ComparisonConfigurationException>(()=>Engine.Compare(pb,pa,sb,sa,Spec));
 }
}
