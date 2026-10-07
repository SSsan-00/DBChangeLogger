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
 [TestMethod]
 public void ColumnDefinitions_ResolveActualNamesConvertValuesAndRejectUnknownColumns() {
  TableColumn[] definition=[new("id","Int32","int"),new("Code","String","varchar"),new("Amount","Decimal","numeric"),new("RegisterDate","DateTime","datetime2")];
  foreach(var pg in new[]{true,false}) {
   using System.Data.Common.DbConnection db=pg?new Npgsql.NpgsqlConnection():new Microsoft.Data.SqlClient.SqlConnection();
   var spec=new TableSpec("public","table",["id"],[],Columns:["code"],Filters:[new("code","=","00123","数値"),new("amount","範囲","1.25","数値","2.50")],Definition:definition);
   using var command=Engine.CreateSelectCommand(db,pg,spec);
   StringAssert.Contains(command.CommandText,pg?"\"Code\"":"[Code]");
   Assert.AreEqual("00123",command.Parameters[0].Value);
   Assert.AreEqual(1.25m,command.Parameters[1].Value);Assert.AreEqual(2.50m,command.Parameters[2].Value);
   foreach(var invalid in new[]{spec with{Columns=["missing"]},spec with{Ignored=["missing"]},spec with{Filters=[new("missing","=","1")]}})
    Assert.ThrowsExactly<InvalidOperationException>(()=>Engine.CreateCountCommand(db,pg,invalid));
   Assert.ThrowsExactly<FormatException>(()=>Engine.CreateSelectCommand(db,pg,spec with{Filters=[new("Amount","=","abc")]}));
  }
  Assert.ThrowsExactly<OverflowException>(()=>new TableColumn("id","Int32","integer").Parse("2147483648"));
  Assert.AreEqual(DateTimeKind.Unspecified,((DateTime)definition[3].Parse("2026-10-07 12:00:00.1234567")).Kind);
  Assert.AreEqual(new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),new TableColumn("d","DateTime","timestamp with time zone").Parse("2026-10-07T09:00:00+09:00"));
  Assert.AreEqual(true,new TableColumn("flag","Boolean","bit").Parse("1"));
  Assert.AreEqual(Guid.Empty,new TableColumn("key","Guid","uuid").Parse(Guid.Empty.ToString()));
  var common=TableCatalog.CommonColumns(definition,[new("ID","Int64","bigint"),new("code","String","text"),new("only_sql","String","text")]);
  CollectionAssert.AreEqual(new[]{"id","Code"},common.Select(c=>c.Name).ToArray());
 }
}
