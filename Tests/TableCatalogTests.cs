using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DbEvidenceTests;
[TestClass]
public class TableCatalogTests {
 [TestMethod]
 public void CommonTables_KeepOnlySharedUnambiguousNames() {
  var p=new[]{new DatabaseTable("orders",["id"]),new("pg_only",["id"]),new("Mixed",["id"]),new("mixed",["id"])};
  var s=new[]{new DatabaseTable("Orders",["ID"]),new("sql_only",["id"]),new("mixed",["id"])};
  var common=TableCatalog.Common(p,s);
  Assert.AreEqual(1,common.Length);Assert.AreEqual("orders",common[0].Postgres.Name);Assert.AreEqual("Orders",common[0].SqlServer.Name);
 }
 [TestMethod]
 public void Specs_AlignCompositeKeyOrderAndPreserveFilter() {
  var table=new CommonTable(new("orders",["id","detail_id"]),new("Orders",["DETAIL_ID","ID"]));
  var filter=new TableFilter("id","範囲","1","整数","5");var (p,s)=table.Specs(["stamp"],filter);
  Assert.AreEqual("public",p.Schema);Assert.AreEqual("dbo",s.Schema);CollectionAssert.AreEqual(new[]{"ID","DETAIL_ID"},s.Keys);
  Assert.AreEqual(filter,p.Filter);Assert.AreEqual(filter,s.Filter);
  var before=new Snapshot(["id","detail_id"],new(){["[\"1\",\"2\"]"]=["1","2"]},DateTimeOffset.UtcNow);
  var after=before with {Rows=new()};
  Assert.AreEqual("削除",Engine.Compare(before,after,before,after,p).Single().Pg.Operation);
 }
 [TestMethod]
 public void Specs_RejectMissingOrDifferentPrimaryKeys() {
  foreach(var table in new[]{new CommonTable(new("x",[]),new("x",["id"])),new CommonTable(new("x",["id"]),new("x",["other"]))}) {
   Assert.IsNotNull(table.KeyError);Assert.ThrowsExactly<InvalidOperationException>(()=>table.Specs([],null));
  }
 }
}
