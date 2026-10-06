using System.Data.Common;
using Npgsql;
using Microsoft.Data.SqlClient;
namespace DbEvidence;

public record DatabaseTable(string Name, string[] Keys);
public record CommonTable(DatabaseTable Postgres, DatabaseTable SqlServer) {
 public override string ToString()=>Postgres.Name;
 public string? KeyError=>Postgres.Keys.Length==0||SqlServer.Keys.Length==0
  ? "主キーがないため比較できません。DBに主キーを定義してください。"
  : Postgres.Keys.Length!=SqlServer.Keys.Length||!Postgres.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(SqlServer.Keys)
   ? "両DBの主キー列が異なるため比較できません。主キーの定義を確認してください。" : null;
 public (TableSpec Pg,TableSpec Sql) Specs(string[] ignored,TableFilter? filter) {
  if(KeyError is {} error)throw new InvalidOperationException(error);
  // 両DBの主キー定義順が違っても同じJSONキーになるよう、SQL Server側もPG側の列順へそろえる。
  return (new("public",Postgres.Name,Postgres.Keys,ignored,filter),
   new("dbo",SqlServer.Name,Postgres.Keys.Select(k=>SqlServer.Keys.Single(s=>string.Equals(k,s,StringComparison.OrdinalIgnoreCase))).ToArray(),ignored,filter));
 }
}
public static class TableCatalog {
 public static CommonTable[] Common(IEnumerable<DatabaseTable> pg,IEnumerable<DatabaseTable> sql) {
  // 大文字小文字だけが違う複数テーブルは、自動対応付けが曖昧になるため候補から除く。
  var p=pg.GroupBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).Select(g=>g.Single());
  var s=sql.GroupBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.OrdinalIgnoreCase);
  return p.Where(t=>s.ContainsKey(t.Name)).Select(t=>new CommonTable(t,s[t.Name])).OrderBy(t=>t.Postgres.Name,StringComparer.Ordinal).ToArray();
 }
 public static async Task<DatabaseTable[]> Read(bool pg,string connection,CancellationToken token=default) {
  await using DbConnection db=pg?new NpgsqlConnection(connection):new SqlConnection(connection);
  await db.OpenAsync(token);
  await using var cmd=db.CreateCommand();cmd.CommandTimeout=120;
  // LEFT JOINで主キーなしのテーブルも残し、選択後に理由を表示する。一覧はSELECT権限のある実テーブルだけ。
  cmd.CommandText=pg?"""
   SELECT t.relname, a.attname
   FROM pg_catalog.pg_class t
   JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
   LEFT JOIN pg_catalog.pg_constraint k ON k.conrelid=t.oid AND k.contype='p'
   LEFT JOIN LATERAL unnest(k.conkey) WITH ORDINALITY AS keycol(attnum,position) ON true
   LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=keycol.attnum
   WHERE n.nspname='public' AND t.relkind IN ('r','p')
     AND has_schema_privilege(n.oid,'USAGE') AND has_table_privilege(t.oid,'SELECT')
   ORDER BY t.relname,keycol.position
   """:"""
   SELECT t.name,c.name
   FROM sys.tables t
   JOIN sys.schemas s ON s.schema_id=t.schema_id
   LEFT JOIN sys.indexes k ON k.object_id=t.object_id AND k.is_primary_key=1
   LEFT JOIN sys.index_columns ic ON ic.object_id=k.object_id AND ic.index_id=k.index_id AND ic.key_ordinal>0
   LEFT JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
   WHERE s.name='dbo' AND t.is_ms_shipped=0 AND HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(t.name),'OBJECT','SELECT')=1
   ORDER BY t.name,ic.key_ordinal
   """;
  await using var reader=await cmd.ExecuteReaderAsync(token);
  var tables=new Dictionary<string,List<string>>(StringComparer.Ordinal);
  while(await reader.ReadAsync(token)) {
   var name=reader.GetString(0);if(!tables.TryGetValue(name,out var keys))tables.Add(name,keys=new());
   if(!reader.IsDBNull(1))keys.Add(reader.GetString(1));
  }
  return tables.Select(t=>new DatabaseTable(t.Key,t.Value.ToArray())).ToArray();
 }
}
