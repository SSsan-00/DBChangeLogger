using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Npgsql;
using Microsoft.Data.SqlClient;
namespace DbEvidence;
public record TableSpec(string Schema, string Name, string[] Keys, string[] Ignored,TableFilter? Filter=null,string[]? Columns=null,TableFilter[]? Filters=null,string FilterJoin="AND",string[]? FilterJoins=null,TableColumn[]? Definition=null) {
 public TableFilter[] Conditions=>Filters??(Filter is {} f?[f]:[]);
 public string JoinBefore(int index)=>FilterJoins is {} joins?joins[index-1]:FilterJoin;
}
public record TableFilter(string Column,string Operator,string Value,string Type="文字列",string Upper="");
// 取得後は行配列も辞書も変更しない。同値の行を操作前後で共有し、参照一致で比較を省略するため。
public record Snapshot(string[] Columns, Dictionary<string,string?[]> Rows, DateTimeOffset At);
public record Change(string Key, string Operation, string?[]? Before, string?[]? After, int[] Changed);
public record Evidence(string Key, Change Pg, Change Sql, bool Match, int[] Different);
public static class Engine {
 public const string ExcelLimitError="Excelの上限（1セル32,767文字、1,048,576行、16,384列）を超えるため、エビデンスをコピーできません。対象を絞ってください。";
 static string Q(string s, bool pg) => pg ? "\""+s.Replace("\"","\"\"")+"\"" : "["+s.Replace("]","]]")+"]";
 public static string? Normalize(object v) => v switch { DBNull => null, byte[] b => Convert.ToHexString(b), DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture), DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture), decimal d => d.ToString("G29",CultureInfo.InvariantCulture), IFormattable f => f.ToString(null,CultureInfo.InvariantCulture), _ => v.ToString() };
 public static async Task<Snapshot> Capture(bool pg,string connection,TableSpec spec,Snapshot? previous=null,CancellationToken cancellationToken=default,IProgress<int>? progress=null) {
 if(spec.Keys.Length==0) throw new InvalidOperationException("主キーを指定してください。");
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
 // ponytail: snapshots stay in RAM; disk partitions are needed beyond available memory.
 var rows=new Dictionary<string,string?[]>(previous?.Rows.Count??0,StringComparer.Ordinal);
 var reuse=previous!=null&&columns.SequenceEqual(previous.Columns,StringComparer.OrdinalIgnoreCase);
 var keyValues=new string?[keys.Length];
 while(await reader.ReadAsync(cancellationToken)) {
 var row=new string?[columns.Length];for(var i=0;i<row.Length;i++)row[i]=Normalize(reader.GetValue(i));
 for(var i=0;i<keys.Length;i++){keyValues[i]=row[keys[i]];if(keyValues[i]==null)throw new InvalidOperationException("主キーがNULLです。");}
 // 複合キーを区切り文字で連結すると値に区切り文字がある場合に衝突するため、JSON配列で識別する。
 var key=System.Text.Json.JsonSerializer.Serialize(keyValues);
 if(reuse&&previous!.Rows.TryGetValue(key,out var old)) {
 var same=true;for(var i=0;i<row.Length;i++)if(row[i]==old[i])row[i]=old[i];else same=false;
 if(same)row=old;
 }
 if(!rows.TryAdd(key,row))throw new InvalidOperationException("主キーが一意ではありません。");
 if(rows.Count%10000==0)progress?.Report(rows.Count);
 }
 progress?.Report(rows.Count);await reader.DisposeAsync(); await tx.CommitAsync(cancellationToken); return new(columns,rows,DateTimeOffset.UtcNow);
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
 foreach(var name in spec.Keys.Concat(spec.Ignored))Resolve(name);
 var selected=spec.Columns is {Length:>0} columns?columns.Concat(spec.Keys).Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray():[];
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
 // 全件の集合・ソート・行コピーを避け、変更した主キーだけを保持する。
 var candidates=new HashSet<string>(StringComparer.Ordinal);
 void FindChanges(Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap,HashSet<string> changedKeys) {
 var sameOrder=beforeMap.AsSpan().SequenceEqual(afterMap);
 var beforeActive=active.Select(i=>beforeMap[i]).ToArray();var afterActive=active.Select(i=>afterMap[i]).ToArray();
 foreach(var (key,row) in before.Rows) {
 cancellationToken.ThrowIfCancellationRequested();
 if(!after.Rows.TryGetValue(key,out var next)){changedKeys.Add(key);continue;}
 if(sameOrder&&ReferenceEquals(row,next))continue;
 if(sameOrder){foreach(var i in beforeActive)if(row[i]!=next[i]){changedKeys.Add(key);break;}}
 else{for(var i=0;i<beforeActive.Length;i++)if(row[beforeActive[i]]!=next[afterActive[i]]){changedKeys.Add(key);break;}}
 }
 foreach(var key in after.Rows.Keys){cancellationToken.ThrowIfCancellationRequested();if(!before.Rows.ContainsKey(key))changedKeys.Add(key);}
 }
 if((long)pb.Rows.Count+sb.Rows.Count>=1_000_000&&Environment.ProcessorCount>1) {
 var sqlCandidates=new HashSet<string>(StringComparer.Ordinal);
 Parallel.Invoke(new ParallelOptions{CancellationToken=cancellationToken,MaxDegreeOfParallelism=2},
 ()=>FindChanges(pb,pa,identity,pm,candidates),()=>FindChanges(sb,sa,bm,sm,sqlCandidates));
 candidates.UnionWith(sqlCandidates);
 }else{FindChanges(pb,pa,identity,pm,candidates);FindChanges(sb,sa,bm,sm,candidates);}
 string?[]? AlignRow(Snapshot snapshot,string key,int[] map) {
 if(!snapshot.Rows.TryGetValue(key,out var row))return null;
 if(map.SequenceEqual(identity))return row;
 var aligned=new string?[columns.Length];for(var i=0;i<aligned.Length;i++)aligned[i]=row[map[i]];return aligned;
 }
 Change Get(string key,Snapshot before,Snapshot after,int[] beforeMap,int[] afterMap) {
 var b=AlignRow(before,key,beforeMap);var a=AlignRow(after,key,afterMap);
 var changed=active.Where(i=>b==null||a==null||b[i]!=a[i]).ToArray();
 return new(key,b==null?(a==null?"変更なし":"追加"):a==null?"削除":changed.Length>0?"更新":"変更なし",b,a,changed);
 }
 var result=new List<Evidence>(candidates.Count);
 foreach(var key in candidates.Order(StringComparer.Ordinal)) {
 cancellationToken.ThrowIfCancellationRequested();
 var p=Get(key,pb,pa,identity,pm);var s=Get(key,sb,sa,bm,sm);
 if(p.Operation=="変更なし"&&s.Operation=="変更なし")continue;
 // 変更検出とDB間の判定は独立。更新前や更新列が異なっても、操作後の取得値が同じなら一致。
 // 片側だけの操作は比較する変更行がないため、全対象列を不一致にする。
 var diff=p.Operation!=s.Operation?active:active.Where(i=>p.After?[i]!=s.After?[i]).ToArray();
 result.Add(new(key,p,s,p.Operation==s.Operation&&diff.Length==0,diff));
 }
 // 件数が異なる表のサマリーと最終判定を揃える。差分行だけを走査し、全スナップショットは再走査しない。
 if(result.Count(e=>e.Pg.Operation!="変更なし")!=result.Count(e=>e.Sql.Operation!="変更なし"))
 for(var i=0;i<result.Count;i++){cancellationToken.ThrowIfCancellationRequested();result[i]=result[i] with{Match=false,Different=active};}
 return result;
 }
 static string Visible(string? value) => value==null?"〈NULL〉":value.Length==0?"〈空文字〉":value.Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t");
 public static (string Html,string Text,string SpreadsheetXml) Render(string[] columns,List<Evidence> evidence,TableSpec spec,Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,bool spreadsheetOnly=false,CancellationToken cancellationToken=default) {
 var pgCount=evidence.Count(e=>e.Pg.Operation!="変更なし");var sqlCount=evidence.Count(e=>e.Sql.Operation!="変更なし");
 var dataRows=evidence.Count==0?0:(long)Math.Max(1,pgCount)+Math.Max(1,sqlCount);
 if(dataRows+3+(spec.Ignored.Length>0?1:0)+(spec.Columns is {Length:>0}?1:0)>1048576||columns.Length+2>16384)throw new InvalidOperationException(ExcelLimitError);
 // Excelの推測による日付・数値・数式への変換を防ぐため、DataのString型と書式@を両方維持する。
 // ここに追加した上部の行は、直前のExcel行数上限チェックにも反映する。
 XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
 var xml=new StringBuilder();
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
 writer.WriteStartElement("Cell",ss.NamespaceName);writer.WriteAttributeString("ss","StyleID",ss.NamespaceName,"C"+c.Color[1..]);
 writer.WriteStartElement("Data",ss.NamespaceName);writer.WriteAttributeString("ss","Type",ss.NamespaceName,"String");writer.WriteAttributeString("xml","space",null,"preserve");writer.WriteString(c.Value);writer.WriteEndElement();writer.WriteEndElement();
 }
 writer.WriteEndElement();if(!spreadsheetOnly){html.Append("</tr>");text.AppendLine();}
 }

 var condition=spec.Conditions.Length==0?"":" / 条件: "+string.Concat(spec.Conditions.Select((f,i)=>(i==0?"":" "+spec.JoinBefore(i)+" ")+$"({f.Column} {f.Operator} {f.Value}"+(f.Operator=="範囲"?$"〜{f.Upper}":"")+")"));
 Row(new[]{($"対象: {spec.Name}"+condition,"#ffffff")});
 if(spec.Columns is {Length:>0})Row(new[]{("取得列: "+string.Join(", ",columns),"#ffffff")});
 if(spec.Ignored.Length>0)Row(new[]{("除外列: "+string.Join(", ",spec.Ignored),"#ffffff")});
 Row(new[]{"DB","操作"}.Concat(columns).Select(c=>(c,"#d9e2f3")));
 foreach(var (db,pg,count) in new[]{("PostgreSQL",true,pgCount),("SQL Server",false,sqlCount)}) {
 foreach(var e in evidence) {
 var c=pg?e.Pg:e.Sql;
 // DBごとに変更行だけ出力する。変更が0件のDBだけ空欄の代表行を1行残し、行数は揃えない。
 if(c.Operation=="変更なし"){if(count>0)continue;Row(new[]{(db,"#ffffff"),(c.Operation,"#ffffff")}.Concat(columns.Select(_=>("","#ffffff"))));break;}
 // 削除行の表示は全列「行なし」。主キーによる対応付けはCompareで済ませ、削除前の値は出力しない。
 var values=c.After;
 Row(new[]{(db,"#ffffff"),(c.Operation,c.Operation=="追加"?"#ddebf7":c.Operation=="削除"?"#dddddd":"#ffffff")}.Concat(columns.Select((col,i)=>(values==null?"〈行なし〉":Visible(values[i]),c.Operation=="追加"?"#ddebf7":c.Operation=="削除"?"#dddddd":c.Changed.Contains(i)?"#fff2cc":"#ffffff")))); }
 }
 // 行数が異なる表は全列×。同数なら変更行の操作後を列ごとに集約する。
 var different=evidence.SelectMany(e=>e.Different).ToHashSet();
 Row(new[]{("判定","#d9e2f3"),("","#ffffff")}.Concat(columns.Select((col,i)=>pgCount!=sqlCount?("×","#ffc7ce"):spec.Ignored.Contains(col,StringComparer.OrdinalIgnoreCase)?("除外","#ffffff"):different.Contains(i)?("×","#ffc7ce"):("◯","#e2f0d9"))));
 if(!spreadsheetOnly)html.Append("</table></body></html>");
 writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();writer.Flush();
 return(spreadsheetOnly?"":html.ToString(),text.ToString(),"<?xml version=\"1.0\" encoding=\"utf-8\"?>"+xml);

 }
 // 同じRenderで生成した表を1枚へ連結する。大量行をXMLツリーに展開せず、行単位でコピーする。
 public static string CombineSpreadsheetXml(IEnumerable<string> tables,CancellationToken cancellationToken=default) {
 const string ns="urn:schemas-microsoft-com:office:spreadsheet";var output=new StringBuilder();
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
 return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"+output;
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
