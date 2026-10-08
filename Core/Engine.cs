using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Npgsql;
using Microsoft.Data.SqlClient;
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]
namespace DbEvidence;
public record TableSpec(string Schema, string Name, string[] Keys, string[] Ignored,TableFilter? Filter=null,string[]? Columns=null,TableFilter[]? Filters=null,string FilterJoin="AND",string[]? FilterJoins=null,TableColumn[]? Definition=null,string[]? MatchKeys=null,string[]? ComparisonIgnored=null,bool BusinessIdentity=false,bool AutoMatch=false,string[]? AutoMatchExcluded=null) {
 public TableFilter[] Conditions=>Filters??(Filter is {} f?[f]:[]);
 public string JoinBefore(int index)=>FilterJoins is {} joins?joins[index-1]:FilterJoin;
 public TableSpec WithAutomaticMetadata(TableSpec other)=>this with{AutoMatchExcluded=Keys.Concat(other.Keys)
  .Concat((Definition??[]).Concat(other.Definition??[]).Where(c=>c.UnstableForMatching).Select(c=>c.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()};
}
// UIに表示してよい、自前の検証理由だけを扱う。ドライバー例外やレコード値は含めない。
public class ComparisonConfigurationException(string message):InvalidOperationException(message);
public record TableFilter(string Column,string Operator,string Value,string Type="文字列",string Upper="");
// 取得後は行配列も辞書も変更しない。同値の行を操作前後で共有し、参照一致で比較を省略するため。
public record Snapshot(string[] Columns, Dictionary<string,string?[]> Rows, DateTimeOffset At,bool Keyless=false);
public record Change(string Key, string Operation, string?[]? Before, string?[]? After, int[] Changed,bool Uncertain=false);
public record Evidence(string Key, Change Pg, Change Sql, bool Match, int[] Different,string[]? InferredIdentity=null);
public static class Engine {
 public const string ExcelLimitError="Excelの上限（1セル32,767文字、1,048,576行、16,384列）を超えるため、エビデンスをコピーできません。対象を絞ってください。";
 static string Q(string s, bool pg) => pg ? "\""+s.Replace("\"","\"\"")+"\"" : "["+s.Replace("]","]]")+"]";
 public static string? Normalize(object v) => v switch { DBNull => null, byte[] b => Convert.ToHexString(b), DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture), DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture), decimal d => d.ToString("G29",CultureInfo.InvariantCulture), IFormattable f => f.ToString(null,CultureInfo.InvariantCulture), _ => v.ToString() };
 public static async Task<Snapshot> Capture(bool pg,string connection,TableSpec spec,Snapshot? previous=null,CancellationToken cancellationToken=default,IProgress<int>? progress=null,long expectedRows=0) {
 if(spec.Keys.Length==0&&!spec.AutoMatch) throw new InvalidOperationException("主キーを指定してください。");
 await using DbConnection db=pg?new NpgsqlConnection(connection):new SqlConnection(connection);
 await db.OpenAsync(cancellationToken);
 // 1回の取得内で整合性を保つ。SQL Serverでは更新を待たせる可能性があり、両DB間の同時点性は保証しない。
 await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,cancellationToken);
 await using var cmd=CreateSelectCommand(db,pg,spec);cmd.Transaction=tx;
 await using var reader=await cmd.ExecuteReaderAsync(cancellationToken);
 var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();
 if(columns.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=columns.Length) throw new InvalidOperationException("重複する列名があります。");
 var keys=spec.Keys.Select(k=>Array.FindIndex(columns,c=>string.Equals(c,k,StringComparison.OrdinalIgnoreCase))).ToArray();
 if(keys.Any(k=>k<0)) throw new InvalidOperationException("指定した主キー列がありません。");
 if(spec.Columns is not {Length:>0}&&spec.Ignored.Any(k=>!columns.Contains(k,StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("除外列がありません。");
 if(spec.Keys.Intersect(spec.Ignored,StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidOperationException("主キーは除外できません。");
 var matchIndexes=ComparisonIndexes(columns,spec);
 var matchingRows=matchIndexes.Length>0&&!matchIndexes.SequenceEqual(keys)?new HashSet<string?[]>((int)Math.Clamp(expectedRows,0,1_000_000),new RowKeyComparer(matchIndexes)):null;
 // ponytail: snapshots stay in RAM; disk partitions are needed beyond available memory.
 // COUNTは取得と別時点なので容量のヒントにだけ使う。巨大な見積りで取得前にメモリを使い切らないよう初期確保は100万件まで。
 var rows=new Dictionary<string,string?[]>(previous?.Rows.Count??(int)Math.Clamp(expectedRows,0,1_000_000),StringComparer.Ordinal);
 // 主キーなしの通番は取得内だけの識別子。DBの返却順を前後の行対応と解釈しない。
 var reuse=spec.Keys.Length>0&&previous!=null&&!previous.Keyless&&columns.SequenceEqual(previous.Columns,StringComparer.OrdinalIgnoreCase);
 var readers=Enumerable.Range(0,columns.Length).Select(i=>ValueReader(reader.GetFieldType(i))).ToArray();
 var keyValues=new string?[keys.Length];
 while(await reader.ReadAsync(cancellationToken)) {
 var row=new string?[columns.Length];for(var i=0;i<row.Length;i++)row[i]=reader.IsDBNull(i)?null:readers[i](reader,i);
 if(matchIndexes.Length>0)ValidateMatchingRow(row,matchIndexes,matchingRows,spec,pg?"PostgreSQL":"SQL Server");
 for(var i=0;i<keys.Length;i++){keyValues[i]=row[keys[i]];if(keyValues[i]==null)throw new InvalidOperationException("主キーがNULLです。");}
 // 複合キーを区切り文字で連結すると値に区切り文字がある場合に衝突するため、JSON配列で識別する。
 var key=keys.Length==0?"row:"+rows.Count.ToString(CultureInfo.InvariantCulture):System.Text.Json.JsonSerializer.Serialize(keyValues);
 if(reuse&&previous!.Rows.TryGetValue(key,out var old)) {
 var same=true;for(var i=0;i<row.Length;i++)if(row[i]==old[i])row[i]=old[i];else same=false;
 if(same)row=old;
 }
 if(!rows.TryAdd(key,row)){if(spec.BusinessIdentity)throw ConfigurationError(spec,$"{(pg?"PostgreSQL":"SQL Server")}の操作前後の識別列（{string.Join(", ",spec.Keys)}）が重複しています。別の対応列を選択してください。");throw new InvalidOperationException("主キーが一意ではありません。");}
 if(rows.Count%10000==0)progress?.Report(rows.Count);
 }
 progress?.Report(rows.Count);await reader.DisposeAsync(); await tx.CommitAsync(cancellationToken); return new(columns,rows,DateTimeOffset.UtcNow,keys.Length==0);
 }
 // 行配列をコピーせず、指定列の値だけで一意性を検証する。検証用集合は取得終了時に解放する。
 sealed class RowKeyComparer(int[] indexes):IEqualityComparer<string?[]> {
 public bool Equals(string?[]? x,string?[]? y){if(ReferenceEquals(x,y))return true;if(x==null||y==null)return false;foreach(var i in indexes)if(x[i]!=y[i])return false;return true;}
 public int GetHashCode(string?[] row){var hash=new HashCode();foreach(var i in indexes)hash.Add(row[i],StringComparer.Ordinal);return hash.ToHashCode();}
 }
 static ComparisonConfigurationException ConfigurationError(TableSpec spec,string reason)=>new($"{spec.Name}: {reason}");
 public static string[] AutomaticMatchingExcluded(TableSpec spec)=>spec.Keys.Concat(spec.AutoMatchExcluded??[])
  .Concat((spec.Definition??[]).Where(c=>c.UnstableForMatching).Select(c=>c.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 static (Change[] Changes,string? Identity) KeylessChanges(Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap,int[] active,int[] stable,CancellationToken token) {
  var beforeAligned=beforeMap.Select((value,index)=>value==index).All(v=>v);var afterAligned=afterMap.Select((value,index)=>value==index).All(v=>v);
  string?[] Align(string?[] row,int[] map,bool aligned)=>aligned?row:map.Select(i=>row[i]).ToArray();
  var b=before.Rows.Select(r=>(r.Key,Row:Align(r.Value,beforeMap,beforeAligned))).ToArray();
  var counts=new Dictionary<string?[],int>(Math.Min(b.Length,1_000_000),new RowKeyComparer(active));
  foreach(var (_,row) in b){token.ThrowIfCancellationRequested();counts[row]=counts.GetValueOrDefault(row)+1;}
  var added=new List<(string Key,string?[] Row)>();
  foreach(var (key,row) in after.Rows){token.ThrowIfCancellationRequested();var aligned=Align(row,afterMap,afterAligned);if(counts.TryGetValue(aligned,out var count)&&count>0)counts[aligned]=count-1;else added.Add((key,aligned));}
  var removed=new List<(string Key,string?[] Row)>();
  foreach(var item in b){token.ThrowIfCancellationRequested();if(counts[item.Row]>0){counts[item.Row]--;removed.Add(item);}}
  // 重複行は集合に潰さず件数差を残す。変更なしの行を除いてから識別列候補を調べる。
  Dictionary<string,(string Key,string?[] Row)>? best=null;var bestIndex=-1;var tied=false;
  foreach(var index in stable) {
   Dictionary<string,(string Key,string?[] Row)?> Unique(List<(string Key,string?[] Row)> rows) {
    var values=new Dictionary<string,(string Key,string?[] Row)?>(StringComparer.Ordinal);
    foreach(var row in rows){token.ThrowIfCancellationRequested();if(row.Row[index] is {} value){if(!values.TryAdd(value,row))values[value]=null;}}
    return values;
   }
   var old=Unique(removed);var next=Unique(added);var candidate=new Dictionary<string,(string Key,string?[] Row)>(StringComparer.Ordinal);
   foreach(var (value,row) in old){token.ThrowIfCancellationRequested();if(row is {} source&&next.TryGetValue(value,out var target)&&target is {} destination)candidate.Add(source.Key,destination);}
   if(candidate.Count==0)continue;
   if(best==null||candidate.Count>best.Count){best=candidate;bestIndex=index;tied=false;}
   else if(candidate.Count==best.Count&&!candidate.All(pair=>best.TryGetValue(pair.Key,out var other)&&other.Key==pair.Value.Key))tied=true;
  }
  // 同点で異なる対応がある場合は推定しない。複合キーを総当たりで探索しないため、単一列で決まらない変更は削除＋追加として残す。
  if(tied){best=null;bestIndex=-1;}
  var changes=new List<Change>();var used=new HashSet<string>(StringComparer.Ordinal);
  foreach(var (key,row) in removed)if(best!=null&&best.TryGetValue(key,out var next)){token.ThrowIfCancellationRequested();used.Add(next.Key);changes.Add(new(key,"更新",row,next.Row,active.Where(i=>row[i]!=next.Row[i]).ToArray()));}
  var uncertain=removed.Count>(best?.Count??0)&&added.Count>used.Count;
  foreach(var (key,row) in removed){token.ThrowIfCancellationRequested();if(best==null||!best.ContainsKey(key))changes.Add(new("before:"+key,"削除",row,null,active,uncertain));}
  foreach(var (key,row) in added){token.ThrowIfCancellationRequested();if(!used.Contains(key))changes.Add(new("after:"+key,"追加",null,row,active,uncertain));}
  return(changes.ToArray(),bestIndex<0?null:before.Columns[beforeMap[bestIndex]]);
 }
 static int[] ComparisonIndexes(string[] columns,TableSpec spec) {
 var match=spec.MatchKeys??[];var excluded=spec.ComparisonIgnored??[];
 if(spec.MatchKeys is {Length:0}||match.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=match.Length)
 throw ConfigurationError(spec,"DB間の対応列を選択してください。重複した列は指定できません。");
 if(match.Intersect(spec.Ignored,StringComparer.OrdinalIgnoreCase).Any())throw ConfigurationError(spec,"DB間の対応列は変更検出から除外できません。");
 foreach(var name in match.Concat(excluded))if(!columns.Contains(name,StringComparer.OrdinalIgnoreCase))throw ConfigurationError(spec,"比較設定に取得対象外の列があります。取得列・比較設定を見直してください。");
 return match.Select(k=>Array.FindIndex(columns,c=>string.Equals(c,k,StringComparison.OrdinalIgnoreCase))).ToArray();
 }
 static void ValidateMatchingRow(string?[] row,int[] indexes,HashSet<string?[]>? seen,TableSpec spec,string database) {
 foreach(var i in indexes)if(row[i]==null)throw ConfigurationError(spec,$"{database}のDB間の対応列（{string.Join(", ",spec.MatchKeys!)}）にNULLがあります。別の対応列を選択してください。");
 if(seen!=null&&!seen.Add(row))throw ConfigurationError(spec,$"{database}のDB間の対応列（{string.Join(", ",spec.MatchKeys!)}）が重複しています。複数列の組み合わせなどを選択してください。");
 }
 // 型は列ごとに一度だけ判定。主要な値型はobjectへのボックス化を避け、Normalizeと同じ書式を使う。
 internal static Func<DbDataReader,int,string?> ValueReader(Type type)=>Type.GetTypeCode(type) switch {
 TypeCode.Int32=>static(r,i)=>r.GetInt32(i).ToString(CultureInfo.InvariantCulture),
 TypeCode.Int64=>static(r,i)=>r.GetInt64(i).ToString(CultureInfo.InvariantCulture),
 TypeCode.Decimal=>static(r,i)=>r.GetDecimal(i).ToString("G29",CultureInfo.InvariantCulture),
 TypeCode.DateTime=>static(r,i)=>r.GetDateTime(i).ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture),
 TypeCode.Boolean=>static(r,i)=>r.GetBoolean(i).ToString(),
 TypeCode.String=>static(r,i)=>r.GetString(i),
 _=>static(r,i)=>Normalize(r.GetValue(i))
 };
 public static async Task<(Snapshot Pg,Snapshot Sql)> CapturePair(string pgConnection,string sqlConnection,TableSpec pgSpec,TableSpec sqlSpec,
 Snapshot? pgPrevious=null,Snapshot? sqlPrevious=null,(long Pg,long Sql) expectedCounts=default,CancellationToken cancellationToken=default,
 IProgress<(bool Pg,int Count)>? progress=null) {
 using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
 async Task<Snapshot> Read(bool pg) {
 try {return await Task.Run(()=>Capture(pg,pg?pgConnection:sqlConnection,pg?pgSpec:sqlSpec,pg?pgPrevious:sqlPrevious,
 linked.Token,progress==null?null:new CaptureProgress(pg,progress),pg?expectedCounts.Pg:expectedCounts.Sql),linked.Token);}
 catch{linked.Cancel();throw;}
 }
 // 最大2接続。片側が失敗したらもう片側も中断し、両タスクの解放完了まで待ってから例外を返す。
 var p=Read(true);var s=Read(false);await Task.WhenAll(p,s);return(await p,await s);
 }
 sealed class CaptureProgress(bool pg,IProgress<(bool Pg,int Count)> progress):IProgress<int> {
 public void Report(int value)=>progress.Report((pg,value));
 }
 // 件数確認はCaptureとは別クエリ。間のDB更新でずれるため、最終的な件数表示にはSnapshot.Rows.Countを使う。
 public static async Task<long> CountRows(bool pg,string connection,TableSpec spec,CancellationToken cancellationToken=default) {
 await using DbConnection db=pg?new NpgsqlConnection(connection):new SqlConnection(connection);
 await db.OpenAsync(cancellationToken);
 await using var command=CreateCountCommand(db,pg,spec);
 return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),CultureInfo.InvariantCulture);
 }
 public static DbCommand CreateCountCommand(DbConnection db,bool pg,TableSpec spec)=>CreateQueryCommand(db,pg,spec,true);
 public static DbCommand CreateSelectCommand(DbConnection db,bool pg,TableSpec spec)=>CreateQueryCommand(db,pg,spec,false);
 static DbCommand CreateQueryCommand(DbConnection db,bool pg,TableSpec spec,bool count) {
 var cmd=db.CreateCommand();
 try {
 if(spec.FilterJoin is not ("AND" or "OR"))throw new InvalidOperationException("条件の結合方法が正しくありません。");
 string Resolve(string name)=>spec.Definition==null?name:spec.Definition.SingleOrDefault(c=>string.Equals(c.Name,name,StringComparison.OrdinalIgnoreCase))?.Name??throw new InvalidOperationException("DBに存在しない列は指定できません。");
 foreach(var name in spec.Keys.Concat(spec.Ignored).Concat(spec.MatchKeys??[]).Concat(spec.ComparisonIgnored??[]))Resolve(name);
 var selected=spec.Columns is {Length:>0} columns?columns.Concat(spec.Keys).Concat(spec.MatchKeys??[]).Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray():[];
 if(selected.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("取得列が正しくありません。");
 var projection=count?(pg?"COUNT(*)":"COUNT_BIG(*)"):selected.Length==0?"*":string.Join(", ",selected.Select(c=>Q(c,pg)));
 cmd.CommandTimeout=120;cmd.CommandText=$"SELECT {projection} FROM {Q(spec.Schema,pg)}.{Q(spec.Name,pg)}";
 var clauses=new List<string>();var conditions=spec.Conditions;
 if(spec.FilterJoins is {} joins&&(joins.Length!=Math.Max(0,conditions.Length-1)||joins.Any(j=>j is not ("AND" or "OR"))))throw new InvalidOperationException("条件の結合方法が正しくありません。");
 for(var i=0;i<conditions.Length;i++) {
 var filter=conditions[i];
 if(string.IsNullOrWhiteSpace(filter.Column)||!new[]{"=",">=","<=","範囲"}.Contains(filter.Operator))throw new InvalidOperationException("検索条件が正しくありません。");
 object Parse(string value)=>filter.Type switch {
 "文字列"=>value,
 "整数"=>long.Parse(value,CultureInfo.InvariantCulture),
 "小数"=>decimal.Parse(value,CultureInfo.InvariantCulture),
 "日時"=>DateTime.SpecifyKind(DateTime.ParseExact(value,new[]{"yyyy-MM-dd","yyyy-MM-dd HH:mm:ss","yyyy-MM-ddTHH:mm:ss","yyyy-MM-dd HH:mm:ss.FFFFFFF","yyyy-MM-ddTHH:mm:ss.FFFFFFF"},CultureInfo.InvariantCulture,DateTimeStyles.None),DateTimeKind.Unspecified),
 "GUID"=>Guid.Parse(value),
 _=>throw new InvalidOperationException("検索値の型が正しくありません。")};
 var columnName=Resolve(filter.Column);var definition=spec.Definition?.Single(c=>c.Name==columnName);
 void Add(string name,string value){var parameter=cmd.CreateParameter();parameter.ParameterName=name;parameter.Value=definition==null?Parse(value):definition.Parse(value);if(definition?.DatabaseType=="date")parameter.DbType=System.Data.DbType.Date;if(!pg&&definition?.DatabaseType=="datetime2")parameter.DbType=System.Data.DbType.DateTime2;cmd.Parameters.Add(parameter);}
 var suffix=i==0?"":i.ToString(CultureInfo.InvariantCulture);var valueName="filterValue"+suffix;var upperName="filterUpper"+suffix;
 Add(valueName,filter.Value);var column=Q(columnName,pg);
 if(filter.Operator=="範囲"){Add(upperName,filter.Upper);clauses.Add($"({column} BETWEEN @{valueName} AND @{upperName})");}
 else clauses.Add($"({column} {filter.Operator} @{valueName})");
 }
 // AND優先というSQLの規則をそのまま使う。左から順に全体へ括弧を付けるとUIと異なる意味になる。
 if(clauses.Count>0)cmd.CommandText+=" WHERE "+string.Concat(clauses.Select((clause,i)=>(i==0?"":" "+spec.JoinBefore(i)+" ")+clause));
 return cmd;
 }catch{cmd.Dispose();throw;}
 }
 public static List<Evidence> Compare(Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,TableSpec spec,CancellationToken cancellationToken=default) {
 var columns=pb.Columns;
 var names=columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
 int[] Map(Snapshot snapshot) {
 if(snapshot.Columns.Length!=columns.Length||!names.SetEquals(snapshot.Columns)||snapshot.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=columns.Length)throw new InvalidOperationException("列構成が一致しません。");
 var indexes=snapshot.Columns.Select((name,index)=>(name,index)).ToDictionary(x=>x.name,x=>x.index,StringComparer.OrdinalIgnoreCase);
 return columns.Select(c=>indexes[c]).ToArray();
 }
 var pm=Map(pa);var bm=Map(sb);var sm=Map(sa);var identity=Enumerable.Range(0,columns.Length).ToArray();
 var ignored=spec.Ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);
 var active=identity.Where(i=>!ignored.Contains(columns[i])).ToArray();
 var comparisonIgnored=(spec.ComparisonIgnored??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);
 var judged=active.Where(i=>!comparisonIgnored.Contains(columns[i])).ToArray();
 var matchIndexes=ComparisonIndexes(columns,spec);var businessMatching=matchIndexes.Length>0;
 if(businessMatching) {
 // 保存データや直接Compareする呼び出しにも同じ検証を適用する。行値はコピーしない。
 foreach(var (snapshot,map,database) in new[]{(pb,identity,"PostgreSQL 操作前"),(pa,pm,"PostgreSQL 操作後"),(sb,bm,"SQL Server 操作前"),(sa,sm,"SQL Server 操作後")}) {
 var indexes=matchIndexes.Select(i=>map[i]).ToArray();var seen=new HashSet<string?[]>(Math.Min(snapshot.Rows.Count,1_000_000),new RowKeyComparer(indexes));
 foreach(var row in snapshot.Rows.Values){cancellationToken.ThrowIfCancellationRequested();ValidateMatchingRow(row,indexes,seen,spec,database);}
 }
 }
 // 全件の集合・ソート・行コピーを避け、変更した主キーだけを保持する。
 var candidates=new HashSet<string>(StringComparer.Ordinal);
 void FindChanges(Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap,HashSet<string> changedKeys) {
 if(before.Keyless||after.Keyless)return;
 var sameOrder=beforeMap.AsSpan().SequenceEqual(afterMap);
 var beforeActive=active.Select(i=>beforeMap[i]).ToArray();var afterActive=active.Select(i=>afterMap[i]).ToArray();
 foreach(var (key,row) in before.Rows) {
 cancellationToken.ThrowIfCancellationRequested();
 if(!after.Rows.TryGetValue(key,out var next)){changedKeys.Add(key);continue;}
 if(businessMatching)foreach(var i in matchIndexes)if(row[beforeMap[i]]!=next[afterMap[i]])
 throw ConfigurationError(spec,"操作前後でDB間の対応列が変わっています。変更されない列を選び、操作前から取得し直してください。");
 if(sameOrder&&ReferenceEquals(row,next))continue;
 if(sameOrder){foreach(var i in beforeActive)if(row[i]!=next[i]){changedKeys.Add(key);break;}}
 else{for(var i=0;i<beforeActive.Length;i++)if(row[beforeActive[i]]!=next[afterActive[i]]){changedKeys.Add(key);break;}}
 }
 foreach(var key in after.Rows.Keys){cancellationToken.ThrowIfCancellationRequested();if(!before.Rows.ContainsKey(key))changedKeys.Add(key);}
 }
 var sqlCandidates=new HashSet<string>(StringComparer.Ordinal);
 if((long)pb.Rows.Count+sb.Rows.Count>=1_000_000&&Environment.ProcessorCount>1) {
 try {
 Parallel.Invoke(new ParallelOptions{CancellationToken=cancellationToken,MaxDegreeOfParallelism=2},
 ()=>FindChanges(pb,pa,identity,pm,candidates),()=>FindChanges(sb,sa,bm,sm,sqlCandidates));
 }catch(AggregateException e) when(e.Flatten().InnerExceptions.All(error=>error is ComparisonConfigurationException)){throw e.Flatten().InnerExceptions[0];}
 }else{FindChanges(pb,pa,identity,pm,candidates);FindChanges(sb,sa,bm,sm,sqlCandidates);}
 var automatic=spec.AutoMatch&&!businessMatching;
 if(!businessMatching&&!automatic)candidates.UnionWith(sqlCandidates);
 var pgAfterAligned=pm.AsSpan().SequenceEqual(identity);var sqlBeforeAligned=bm.AsSpan().SequenceEqual(identity);var sqlAfterAligned=sm.AsSpan().SequenceEqual(identity);
 string?[]? AlignRow(Snapshot snapshot,string key,int[] map,bool alignedOrder) {
 if(!snapshot.Rows.TryGetValue(key,out var row))return null;
 if(alignedOrder)return row;
 var aligned=new string?[columns.Length];for(var i=0;i<aligned.Length;i++)aligned[i]=row[map[i]];return aligned;
 }
 Change Get(string key,Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap,bool beforeAligned,bool afterAligned) {
 var b=AlignRow(before,key,beforeMap,beforeAligned);var a=AlignRow(after,key,afterMap,afterAligned);
 var changed=active.Where(i=>b==null||a==null||b[i]!=a[i]).ToArray();
 return new(key,b==null?(a==null?"変更なし":"追加"):a==null?"削除":changed.Length>0?"更新":"変更なし",b,a,changed);
 }
 var result=new List<Evidence>(candidates.Count);
 void Add(string key,Change p,Change s) {
 var diff=p.Operation!=s.Operation?judged:judged.Where(i=>p.After?[i]!=s.After?[i]).ToArray();
 result.Add(new(key,p,s,p.Operation==s.Operation&&diff.Length==0,diff));
 }
 if(automatic) {
 // 推定は変更行だけで行う。主キー・日時の差を対応付けから外しても、最終判定では必ず比較する。
 var excluded=AutomaticMatchingExcluded(spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
 var stable=active.Where(i=>!excluded.Contains(columns[i])&&!comparisonIgnored.Contains(columns[i])).ToArray();
 var pgKeyless=pb.Keyless||pa.Keyless;var sqlKeyless=sb.Keyless||sa.Keyless;
 var pk=pgKeyless?KeylessChanges(pb,pa,identity,pm,active,stable,cancellationToken):(Changes:Array.Empty<Change>(),Identity:(string?)null);
 var sk=sqlKeyless?KeylessChanges(sb,sa,bm,sm,active,stable,cancellationToken):(Changes:Array.Empty<Change>(),Identity:(string?)null);
 // 推定した文字列の業務識別列はDB間の照合にも使う。採番候補となる数値・GUIDだけを照合材料から外す。
 var inferred=new[]{pk.Identity,sk.Identity}.Where(n=>n!=null&&(spec.Definition??[]).Any(c=>string.Equals(c.Name,n,StringComparison.OrdinalIgnoreCase)&&(c.Numeric||c.ValueType=="Guid"))).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 stable=stable.Where(i=>!inferred.Contains(columns[i],StringComparer.OrdinalIgnoreCase)).ToArray();
 var p=pgKeyless?pk.Changes:candidates.Order(StringComparer.Ordinal).Select(k=>Get(k,pb,pa,identity,pm,true,pgAfterAligned)).ToArray();
 var s=sqlKeyless?sk.Changes:sqlCandidates.Order(StringComparer.Ordinal).Select(k=>Get(k,sb,sa,bm,sm,sqlBeforeAligned,sqlAfterAligned)).ToArray();
 var pUsed=new HashSet<Change>();var sUsed=new HashSet<Change>();
 Dictionary<string,List<Change>> GroupAutomatic(Change[] changes) {
 var groups=new Dictionary<string,List<Change>>(StringComparer.Ordinal);
 foreach(var change in changes) {
 if(change.Uncertain)continue;
 cancellationToken.ThrowIfCancellationRequested();var row=change.After??change.Before!;
 // 照合材料なし・全NULLでは根拠がない。候補重複も後段で拒否し、任意のペアを一致にしない。
 if(stable.Length==0||stable.All(i=>row[i]==null))continue;
 var key=change.Operation+System.Text.Json.JsonSerializer.Serialize(stable.Select(i=>row[i]).ToArray());
 if(!groups.TryGetValue(key,out var list))groups.Add(key,list=[]);list.Add(change);
 }return groups;
 }
 var pgGroups=GroupAutomatic(p);var sqlGroups=GroupAutomatic(s);
 foreach(var (key,group) in pgGroups) {
 cancellationToken.ThrowIfCancellationRequested();
 if(group.Count!=1||!sqlGroups.TryGetValue(key,out var other)||other.Count!=1)continue;
 var pg=group[0];var sql=other[0];pUsed.Add(pg);sUsed.Add(sql);
 var pr=pg.After??pg.Before!;var sr=sql.After??sql.Before!;
 var diff=judged.Where(i=>pr[i]!=sr[i]).ToArray();result.Add(new(pg.Key,pg,sql,diff.Length==0,diff,inferred));
 }
 var remainingPg=p.Where(c=>!pUsed.Contains(c)).ToArray();var remainingSql=s.Where(c=>!sUsed.Contains(c)).ToArray();
 var missing=new Change("","変更なし",null,null,[]);
 // 未対応行は表示用にまとめるだけで、対応したとは解釈しない。曖昧さは全対象列×として残す。
 for(var i=0;i<Math.Max(remainingPg.Length,remainingSql.Length);i++) {
 cancellationToken.ThrowIfCancellationRequested();var pg=i<remainingPg.Length?remainingPg[i]:missing;var sql=i<remainingSql.Length?remainingSql[i]:missing;
 result.Add(new(pg.Operation=="変更なし"?sql.Key:pg.Key,pg,sql,false,judged,inferred));
 }
 }else if(businessMatching) {
 Dictionary<string,List<Change>> Group(HashSet<string> keys,Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap,bool beforeAligned,bool afterAligned) {
 var groups=new Dictionary<string,List<Change>>(StringComparer.Ordinal);
 foreach(var key in keys) {
 cancellationToken.ThrowIfCancellationRequested();var change=Get(key,before,after,beforeMap,afterMap,beforeAligned,afterAligned);
 var values=change.After??change.Before!;var matchingKey=System.Text.Json.JsonSerializer.Serialize(matchIndexes.Select(i=>values[i]).ToArray());
 if(!groups.TryGetValue(matchingKey,out var list))groups.Add(matchingKey,list=[]);list.Add(change);
 }return groups;
 }
 var pgGroups=Group(candidates,pb,pa,identity,pm,true,pgAfterAligned);var sqlGroups=Group(sqlCandidates,sb,sa,bm,sm,sqlBeforeAligned,sqlAfterAligned);
 var missing=new Change("","変更なし",null,null,[]);
 foreach(var key in pgGroups.Keys.Concat(sqlGroups.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
 cancellationToken.ThrowIfCancellationRequested();var p=pgGroups.GetValueOrDefault(key)??[];var s=sqlGroups.GetValueOrDefault(key)??[];
 // 同じ業務キーで削除＋再追加が起きても両方を残す。まず同種の操作を照合し、残りは種別不一致として扱う。
 foreach(var change in p.ToArray()) {var index=s.FindIndex(c=>c.Operation==change.Operation);if(index<0)continue;Add(key,change,s[index]);p.Remove(change);s.RemoveAt(index);}
 for(var i=0;i<Math.Max(p.Count,s.Count);i++)Add(key,i<p.Count?p[i]:missing,i<s.Count?s[i]:missing);
 }
 }else foreach(var key in candidates.Order(StringComparer.Ordinal)) {
 cancellationToken.ThrowIfCancellationRequested();
 var p=Get(key,pb,pa,identity,pm,true,pgAfterAligned);var s=Get(key,sb,sa,bm,sm,sqlBeforeAligned,sqlAfterAligned);
 if(p.Operation=="変更なし"&&s.Operation=="変更なし")continue;
 // 変更検出とDB間の判定は独立。更新前や更新列が異なっても、操作後の取得値が同じなら一致。
 // 片側だけの操作は比較する変更行がないため、全対象列を不一致にする。
 Add(key,p,s);
 }
 // 件数が異なる表のサマリーと最終判定を揃える。差分行だけを走査し、全スナップショットは再走査しない。
 if(result.Count(e=>e.Pg.Operation!="変更なし")!=result.Count(e=>e.Sql.Operation!="変更なし"))
 for(var i=0;i<result.Count;i++){cancellationToken.ThrowIfCancellationRequested();result[i]=result[i] with{Match=false,Different=judged};}
 return result;
 }
 static string Visible(string? value) => value==null?"〈NULL〉":value.Length==0?"〈空文字〉":value.Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t");
 public static (string Html,string Text,string SpreadsheetXml) Render(string[] columns,List<Evidence> evidence,TableSpec spec,Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,bool spreadsheetOnly=false,CancellationToken cancellationToken=default) {
 var pgCount=evidence.Count(e=>e.Pg.Operation!="変更なし");var sqlCount=evidence.Count(e=>e.Sql.Operation!="変更なし");
 var dataRows=evidence.Count==0?0:(long)Math.Max(1,pgCount)+Math.Max(1,sqlCount);
 if(dataRows+3+(spec.Ignored.Length>0?1:0)+(spec.Columns is {Length:>0}?1:0)+(spec.MatchKeys is {Length:>0}?1:0)+(spec.ComparisonIgnored is {Length:>0}?1:0)+(spec.AutoMatch&&spec.MatchKeys==null?1:0)>1048576||columns.Length+2>16384)throw new InvalidOperationException(ExcelLimitError);
 // Excelの推測による日付・数値・数式への変換を防ぐため、DataのString型と書式@を両方維持する。
 // ここに追加した上部の行は、直前のExcel行数上限チェックにも反映する。
 XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
 // XML宣言も最初から同じバッファへ書き、大きな完成文字列の連結コピーを避ける。
 var xml=new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
 using var writer=XmlWriter.Create(xml,new XmlWriterSettings{OmitXmlDeclaration=true,Indent=false});
 var styles=new XElement(ss+"Styles",new[]{"ffffff","d9e2f3","e2f0d9","dddddd","ffc7ce","fff2cc","ddebf7"}.Select(color=>new XElement(ss+"Style",new XAttribute(ss+"ID","C"+color),new XElement(ss+"NumberFormat",new XAttribute(ss+"Format","@")),new XElement(ss+"Alignment",new XAttribute(ss+"Vertical","Top")),new XElement(ss+"Interior",new XAttribute(ss+"Color","#"+color),new XAttribute(ss+"Pattern","Solid")),new XElement(ss+"Borders",new[]{"Bottom","Left","Right","Top"}.Select(side=>new XElement(ss+"Border",new XAttribute(ss+"Position",side),new XAttribute(ss+"LineStyle","Continuous"),new XAttribute(ss+"Weight",1)))))));
 var html=new StringBuilder("<html><head><meta charset='utf-8'></head><body><table xmlns:x='urn:schemas-microsoft-com:office:excel' border='1' style='border-collapse:collapse'>");var text=new StringBuilder();
 writer.WriteStartElement("Workbook",ss.NamespaceName);writer.WriteAttributeString("xmlns","ss",null,ss.NamespaceName);
 styles.WriteTo(writer);writer.WriteStartElement("Worksheet",ss.NamespaceName);writer.WriteAttributeString("ss","Name",ss.NamespaceName,"Evidence");writer.WriteStartElement("Table",ss.NamespaceName);
 void Row(IEnumerable<(string Value,string Color)> cells) {
 cancellationToken.ThrowIfCancellationRequested();
 writer.WriteStartElement("Row",ss.NamespaceName);
 if(!spreadsheetOnly)html.Append("<tr>");var first=true;
 foreach(var c in cells) {
 if(c.Value.Length>32767)throw new InvalidOperationException(ExcelLimitError);
 if(!spreadsheetOnly){html.Append("<td x:str style='mso-number-format:\"\\@\";white-space:pre-wrap;background-color:").Append(c.Color).Append("'><span style='mso-spacerun:yes'>").Append(WebUtility.HtmlEncode(c.Value)).Append("</span></td>");if(!first)text.Append('\t');text.Append(c.Value.Length>0&&"=+-@".Contains(c.Value[0])?"'"+c.Value:c.Value);first=false;}
 // 色は固定7種。セルごとに部分文字列とStyleID文字列を生成しない。
 var style=c.Color switch {"#ffffff"=>"Cffffff","#d9e2f3"=>"Cd9e2f3","#e2f0d9"=>"Ce2f0d9","#dddddd"=>"Cdddddd","#ffc7ce"=>"Cffc7ce","#fff2cc"=>"Cfff2cc","#ddebf7"=>"Cddebf7",_=>"C"+c.Color[1..]};
 writer.WriteStartElement("Cell",ss.NamespaceName);writer.WriteAttributeString("ss","StyleID",ss.NamespaceName,style);
 writer.WriteStartElement("Data",ss.NamespaceName);writer.WriteAttributeString("ss","Type",ss.NamespaceName,"String");writer.WriteAttributeString("xml","space",null,"preserve");writer.WriteString(c.Value);writer.WriteEndElement();writer.WriteEndElement();
 }
 writer.WriteEndElement();if(!spreadsheetOnly){html.Append("</tr>");text.AppendLine();}
 }

 var condition=spec.Conditions.Length==0?"":" / 条件: "+string.Concat(spec.Conditions.Select((f,i)=>(i==0?"":" "+spec.JoinBefore(i)+" ")+$"({f.Column} {f.Operator} {f.Value}"+(f.Operator=="範囲"?$"〜{f.Upper}":"")+")"));
 Row(new[]{($"対象: {spec.Name}"+condition,"#ffffff")});
 if(spec.Columns is {Length:>0})Row(new[]{("取得列: "+string.Join(", ",columns),"#ffffff")});
 if(spec.Ignored.Length>0)Row(new[]{("除外列: "+string.Join(", ",spec.Ignored),"#ffffff")});
 if(spec.AutoMatch&&spec.MatchKeys==null)Row(new[]{("DB間の自動対応: 同じ操作の変更行を値で照合 / 照合材料から除外: "+string.Join(", ",AutomaticMatchingExcluded(spec).Concat(evidence.SelectMany(e=>e.InferredIdentity??[])).Distinct(StringComparer.OrdinalIgnoreCase).Where(n=>columns.Contains(n,StringComparer.OrdinalIgnoreCase)))+"（値の判定は対象） / 未対応・曖昧な行は×","#ffffff")});
 if(spec.MatchKeys is {Length:>0})Row(new[]{("DB間の対応列: "+string.Join(", ",spec.MatchKeys)+(spec.BusinessIdentity?"（操作前後の識別にも使用）":""),"#ffffff")});
 if(spec.ComparisonIgnored is {Length:>0})Row(new[]{("DB間判定の除外列: "+string.Join(", ",spec.ComparisonIgnored),"#ffffff")});
 Row(new[]{"DB","操作"}.Concat(columns).Select(c=>(c,"#d9e2f3")));
 // 数百列の更新で各セルから変更列配列を再走査すると列数の二乗になる。行ごとに印を再利用する。
 var changedCells=new bool[columns.Length];
 foreach(var (db,pg,count) in new[]{("PostgreSQL",true,pgCount),("SQL Server",false,sqlCount)}) {
 foreach(var e in evidence) {
 var c=pg?e.Pg:e.Sql;
 // DBごとに変更行だけ出力する。変更が0件のDBだけ空欄の代表行を1行残し、行数は揃えない。
 if(c.Operation=="変更なし"){if(count>0)continue;Row(new[]{(db,"#ffffff"),(c.Operation,"#ffffff")}.Concat(columns.Select(_=>("","#ffffff"))));break;}
 // 削除行の表示は全列「行なし」。主キーによる対応付けはCompareで済ませ、削除前の値は出力しない。
 var values=c.After;
 Array.Clear(changedCells);foreach(var i in c.Changed)if((uint)i<(uint)changedCells.Length)changedCells[i]=true;
 Row(new[]{(db,"#ffffff"),(c.Operation,c.Operation=="追加"?"#ddebf7":c.Operation=="削除"?"#dddddd":"#ffffff")}.Concat(columns.Select((col,i)=>(values==null?"〈行なし〉":Visible(values[i]),c.Operation=="追加"?"#ddebf7":c.Operation=="削除"?"#dddddd":changedCells[i]?"#fff2cc":"#ffffff")))); }
 }
 // 行数が異なる表は全列×。同数なら変更行の操作後を列ごとに集約する。
 var different=evidence.SelectMany(e=>e.Different).ToHashSet();
 Row(new[]{("判定","#d9e2f3"),("","#ffffff")}.Concat(columns.Select((col,i)=>pgCount!=sqlCount?("×","#ffc7ce"):spec.Ignored.Concat(spec.ComparisonIgnored??[]).Contains(col,StringComparer.OrdinalIgnoreCase)?("除外","#ffffff"):different.Contains(i)?("×","#ffc7ce"):("◯","#e2f0d9"))));
 if(!spreadsheetOnly)html.Append("</table></body></html>");
 writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();writer.Flush();
 return(spreadsheetOnly?"":html.ToString(),text.ToString(),xml.ToString());

 }
 // 同じRenderで生成した表を1枚へ連結する。大量行をXMLツリーに展開せず、行単位でコピーする。
 public static string CombineSpreadsheetXml(IEnumerable<string> tables,CancellationToken cancellationToken=default) {
 const string ns="urn:schemas-microsoft-com:office:spreadsheet";var output=new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
 using var writer=XmlWriter.Create(output,new XmlWriterSettings{OmitXmlDeclaration=true});
 writer.WriteStartElement("Workbook",ns);writer.WriteAttributeString("xmlns","ss",null,ns);
 var first=true;long rows=0;
 foreach(var xml in tables) {
 cancellationToken.ThrowIfCancellationRequested();
 if(!first){if(++rows>1048576)throw new InvalidOperationException(ExcelLimitError);writer.WriteStartElement("Row",ns);writer.WriteEndElement();}
 using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{IgnoreWhitespace=true});
 while(!reader.EOF) {
 cancellationToken.ThrowIfCancellationRequested();
 if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="Styles") {
 if(first){writer.WriteNode(reader,true);writer.WriteStartElement("Worksheet",ns);writer.WriteAttributeString("ss","Name",ns,"Evidence");writer.WriteStartElement("Table",ns);first=false;}else reader.Skip();
 }else if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="Row") {
 if(++rows>1048576)throw new InvalidOperationException(ExcelLimitError);writer.WriteNode(reader,true);
 }else reader.Read();
 }
 }
 if(first)throw new InvalidOperationException("出力対象のテーブルがありません。");
 writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();writer.Flush();
 return output.ToString();
 }
 public static string ClipboardHtml(string fragment) {
 const string start="<!--StartFragment-->";const string end="<!--EndFragment-->";
 if(fragment.StartsWith("<html>",StringComparison.Ordinal)) fragment=fragment[(fragment.IndexOf("<body>")+6)..fragment.LastIndexOf("</body>")];
 var body="<html><body>"+start+fragment+end+"</body></html>";
 const string template="Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
 var offset=Encoding.UTF8.GetByteCount(string.Format(template,0,0,0,0));
 return string.Format(template,offset,offset+Encoding.UTF8.GetByteCount(body),offset+Encoding.UTF8.GetByteCount(body[..(body.IndexOf(start)+start.Length)]),offset+Encoding.UTF8.GetByteCount(body[..body.IndexOf(end)]))+body;
 }
}
