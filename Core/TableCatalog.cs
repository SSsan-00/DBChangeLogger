using System.Data.Common;
using Npgsql;
using Microsoft.Data.SqlClient;
using System.Globalization;
namespace DbEvidence;

public record TableColumn(string Name,string ValueType,string DatabaseType) {
 // SQL Serverのtimestampは日時ではなくrowversion。どちらも行の対応付けには使わず、値の判定には残す。
 public bool UnstableForMatching=>ValueType is "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly" or "TimeSpan"||DatabaseType is "timestamp" or "rowversion";
 public bool Numeric=>ValueType is "Byte" or "Int16" or "Int32" or "Int64" or "UInt32" or "Decimal" or "Double" or "Single";
 public bool Searchable=>Numeric||ValueType is "String" or "Boolean" or "DateTime" or "DateTimeOffset" or "Guid" or "TimeSpan";
 // 表示上の文字列／数値にかかわらずDBの型へ変換する。文字列列の先頭ゼロは数値化してはいけない。
 public object Parse(string value)=>ValueType switch {
  "String"=>value,"Byte"=>byte.Parse(value,CultureInfo.InvariantCulture),"Int16"=>short.Parse(value,CultureInfo.InvariantCulture),
  "Int32"=>int.Parse(value,CultureInfo.InvariantCulture),"Int64"=>long.Parse(value,CultureInfo.InvariantCulture),"UInt32"=>uint.Parse(value,CultureInfo.InvariantCulture),
  "Decimal"=>decimal.Parse(value,CultureInfo.InvariantCulture),"Double"=>double.Parse(value,CultureInfo.InvariantCulture),"Single"=>float.Parse(value,CultureInfo.InvariantCulture),
  "Boolean"=>value=="1"?true:value=="0"?false:bool.Parse(value),"Guid"=>Guid.Parse(value),"TimeSpan"=>TimeSpan.Parse(value,CultureInfo.InvariantCulture),
  "DateTimeOffset"=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal).ToUniversalTime(),
  "DateTime" when DatabaseType is "timestamp with time zone" or "timestamptz"=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal).UtcDateTime,
  "DateTime"=>DateTime.SpecifyKind(DateTime.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.None),DateTimeKind.Unspecified),
  _=>throw new InvalidOperationException("この列の型は検索条件に対応していません。")};
}
public record DatabaseTable(string Name, string[] Keys);
public record CommonTable(DatabaseTable Postgres, DatabaseTable SqlServer) {
 public override string ToString()=>Postgres.Name;
 public (TableSpec Pg,TableSpec Sql) AutomaticSpecs(string[] ignored,TableFilter? filter=null) {
  var sqlKeys=SqlServer.Keys;
  if(Postgres.Keys.Length==sqlKeys.Length&&Postgres.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sqlKeys))
   sqlKeys=Postgres.Keys.Select(k=>sqlKeys.Single(s=>string.Equals(s,k,StringComparison.OrdinalIgnoreCase))).ToArray();
  return(new("public",Postgres.Name,Postgres.Keys,ignored,filter,AutoMatch:true),new("dbo",SqlServer.Name,sqlKeys,ignored,filter,AutoMatch:true));
 }
 public string? KeyError=>Postgres.Keys.Length==0||SqlServer.Keys.Length==0
  ? "主キーがありません。「比較設定」で一意なDB間の対応列を選択してください。"
  : Postgres.Keys.Length!=SqlServer.Keys.Length||!Postgres.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(SqlServer.Keys)
   ? "両DBの主キー列が異なるため比較できません。主キーの定義を確認してください。" : null;
 public (TableSpec Pg,TableSpec Sql) Specs(string[] ignored,TableFilter? filter,string[]? matchKeys=null,string[]? comparisonIgnored=null) {
  if(matchKeys is {Length:0}||matchKeys?.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=matchKeys?.Length)
   throw new ComparisonConfigurationException(Postgres.Name+": DB間の対応列を選択してください。");
  if(matchKeys==null&&KeyError is {} error)throw new ComparisonConfigurationException(Postgres.Name+": "+error);
  if(matchKeys!=null) {
   var pKeys=Postgres.Keys.Length==0?matchKeys:Postgres.Keys;
   var sKeys=SqlServer.Keys.Length==0?matchKeys:SqlServer.Keys;
   if(pKeys.Length==sKeys.Length&&pKeys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sKeys))
    sKeys=pKeys.Select(k=>sKeys.Single(s=>string.Equals(k,s,StringComparison.OrdinalIgnoreCase))).ToArray();
   if(pKeys.Concat(sKeys).Concat(matchKeys).Intersect(ignored,StringComparer.OrdinalIgnoreCase).Any())
    throw new ComparisonConfigurationException(Postgres.Name+": 前後の識別列・DB間の対応列は変更検出から除外できません。");
   return(new("public",Postgres.Name,pKeys,ignored,filter,MatchKeys:matchKeys,ComparisonIgnored:comparisonIgnored,BusinessIdentity:Postgres.Keys.Length==0),
    new("dbo",SqlServer.Name,sKeys,ignored,filter,MatchKeys:matchKeys,ComparisonIgnored:comparisonIgnored,BusinessIdentity:SqlServer.Keys.Length==0));
  }
  // 両DBの主キー定義順が違っても同じJSONキーになるよう、SQL Server側もPG側の列順へそろえる。
  return (new("public",Postgres.Name,Postgres.Keys,ignored,filter,ComparisonIgnored:comparisonIgnored),
   new("dbo",SqlServer.Name,Postgres.Keys.Select(k=>SqlServer.Keys.Single(s=>string.Equals(k,s,StringComparison.OrdinalIgnoreCase))).ToArray(),ignored,filter,ComparisonIgnored:comparisonIgnored));
 }
}
public static class TableCatalog {
 public static TableColumn[] CommonColumns(TableColumn[] pg,TableColumn[] sql) {
  var names=sql.GroupBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).Select(g=>g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return pg.GroupBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1&&names.Contains(g.Key)).Select(g=>g.Single()).ToArray();
 }
 public static async Task<TableColumn[]> ReadColumns(bool pg,string connection,string table,CancellationToken token=default) {
  await using DbConnection db=pg?new NpgsqlConnection(connection):new SqlConnection(connection);await db.OpenAsync(token);
  await using var cmd=Engine.CreateSelectCommand(db,pg,new(pg?"public":"dbo",table,[],[]));
  // 選んだテーブルの定義だけ取得する。行データや全1000テーブル分の列定義を先読みしない。
  cmd.CommandText+=" WHERE 1=0";
  await using var reader=await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly,token);
  return reader.GetColumnSchema().Select(c=>new TableColumn(c.ColumnName!,c.DataType?.Name??"",(c.DataTypeName??"").ToLowerInvariant())).ToArray();
 }
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
