using DbEvidence;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
namespace DbEvidenceApp;
static class Program {
 internal static readonly uint ActivateMessage=RegisterWindowMessage("DBChangeLogger.Activate");
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
 [DllImport("user32.dll")] static extern bool SendNotifyMessage(nint window,uint message,nuint wParam,nint lParam);
 [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
 [STAThread] static void Main(string[] args){
  // Windowsログオンセッションごとに1画面。同じPCの別ユーザーの起動までは妨げない。
  using var instance=new Mutex(true,"Local\\DBChangeLogger",out var first);
  if(!first){AllowSetForegroundWindow(-1);SendNotifyMessage((nint)0xffff,ActivateMessage,0,0);return;}
  ApplicationConfiguration.Initialize();Application.Run(new MainForm(args.Contains("--demo")||args.Contains("--demo-multi"),args.Contains("--demo-multi")));
 }
}
class MainForm:Form {
 readonly string pgConnection="",sqlConnection="";
 readonly SearchColumnCombo table=new(){Width=570,AccessibleName="テーブル一覧"};
 readonly TextBox ignore=new(){Width=620,ReadOnly=true};
 readonly Button pickIgnored=new(){Text="列を選択",AutoSize=true},pickColumns=new(){Text="列を選択",AutoSize=true};
 bool filteringTables;CommonTable[] allTables=[];TableColumn[] pgColumns=[],sqlColumns=[],availableColumns=[];
 readonly Button reload=new(){Text="テーブル一覧を再読込",AutoSize=true};
 readonly TextBox selectedColumns=new(){Width=620,ReadOnly=true};
 readonly Button comparison=new(){Text="比較設定",AutoSize=true};
 readonly Label comparisonSummary=new(){Width=610,Height=27,TextAlign=ContentAlignment.MiddleLeft};
 string[]? matchKeys;string[] comparisonIgnored=[];string comparisonTable="";bool autoMatch=true;
 readonly FlowLayoutPanel filterRows=new(){Width=880,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
 readonly FlowLayoutPanel panel=new(){Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(6)};
 readonly Button addFilter=new(){Text="＋ 条件を追加",AutoSize=true};
 readonly Button before=new(){Text="操作前を取得",AutoSize=true},after=new(){Text="操作後を取得・比較",AutoSize=true},copy=new(){Text="エビデンスをコピー",AutoSize=true,Enabled=false};
 readonly Button cancel=new(){Text="中断",AutoSize=true,Enabled=false};
 readonly Label rowCounts=new(){Text="取得対象件数: 未確認",AutoSize=true,MaximumSize=new Size(870,0)};CancellationTokenSource? execution;string? captureStage;
 readonly Label status=new(){AutoSize=true,MaximumSize=new Size(870,0)};readonly DataGridView grid=new(){Dock=DockStyle.Fill,Visible=false,ReadOnly=true,AllowUserToAddRows=false,BackgroundColor=SystemColors.Window,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
 readonly bool demo;bool closingAllowed,stateBusy;string selectedTable="";ResultSummary[] results=[];
 // 各追跡対象の操作前は最初の比較基準。操作後を再取得しても置き換えない。
 readonly List<TrackedTable> targets=[];
 readonly ListBox trackedList=new(){Width=735,Height=24,HorizontalScrollbar=true,IntegralHeight=false};
 readonly Button addTarget=new(){Text="追跡対象に追加・更新",AutoSize=true},removeTarget=new(){Text="対象から削除",AutoSize=true};
 bool refreshingTargets;
 bool HasBaseline=>targets.Count>0&&targets.All(t=>t.PgBefore!=null&&t.SqlBefore!=null);
 string? spreadsheetXml;readonly Control[] settings;
 public MainForm(bool demo=false,bool multipleDemo=false){this.demo=demo;Text="DBChangeLogger";ClientSize=new Size(900,300);BackColor=SystemColors.Window;
 void Field(string label,Control input){var line=new FlowLayoutPanel(){Width=870,Height=30};line.Controls.Add(new Label(){Text=label,Width=100,Height=27,TextAlign=ContentAlignment.MiddleLeft});line.Controls.Add(input);panel.Controls.Add(line);}
 var tableInputs=new FlowLayoutPanel(){Width=750,Height=28};tableInputs.Controls.AddRange(new Control[]{table,reload});Field("テーブル一覧",tableInputs);
 var projectionInputs=new FlowLayoutPanel(){Width=750,Height=28};projectionInputs.Controls.AddRange(new Control[]{selectedColumns,pickColumns});Field("取得列",projectionInputs);
 var ignoreInputs=new FlowLayoutPanel(){Width=750,Height=28};ignoreInputs.Controls.AddRange(new Control[]{ignore,pickIgnored});Field("除外列",ignoreInputs);
 var comparisonInputs=new FlowLayoutPanel(){Width=750,Height=28};comparisonInputs.Controls.AddRange(new Control[]{comparison,comparisonSummary});Field("DB間比較",comparisonInputs);
 comparison.Click+=(_,_)=>ChooseComparison();
 pickColumns.Click+=(_,_)=>ChooseColumns(false);pickIgnored.Click+=(_,_)=>ChooseColumns(true);
 // 入力中の候補移動ではDBを読まず、一覧を閉じて選択を確定したときに読む。
 table.SelectionConfirmed+=async(_,_)=>{if(filteringTables)return;UpdateTableSelection();await LoadColumnDefinitions();};
 reload.Click+=async(_,_)=>await LoadTables();
 var conditionHeader=new FlowLayoutPanel(){Width=870,Height=30};conditionHeader.Controls.AddRange(new Control[]{new Label(){Text="検索条件",Width=100,Height=27,TextAlign=ContentAlignment.MiddleLeft},addFilter});panel.Controls.Add(conditionHeader);
 panel.Controls.Add(filterRows);AddFilterRow();addFilter.Click+=(_,_)=>AddFilterRow();
 cancel.Click+=(_,_)=>{execution?.Cancel();status.Text="中断しています…";};FormClosing+=async(_,e)=>{if(closingAllowed)return;e.Cancel=true;if(stateBusy){status.Text="保存・復元が完了するまでお待ちください。";return;}if(execution!=null){execution.Cancel();status.Text="処理を中断してから、もう一度閉じてください。";return;}try{Enabled=false;await SaveSession();closingAllowed=true;Close();}catch{Enabled=true;status.Text="前回の内容を保存できませんでした。保存先の空き容量・権限を確認してください。";}};
 var targetButtons=new FlowLayoutPanel(){Width=750,Height=30,WrapContents=false};targetButtons.Controls.AddRange(new Control[]{addTarget,removeTarget});
 var targetInputs=new FlowLayoutPanel(){Width=750,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};targetInputs.Controls.AddRange(new Control[]{trackedList,targetButtons});
 var targetLine=new FlowLayoutPanel(){Width=870,AutoSize=true,WrapContents=false};targetLine.Controls.Add(new Label(){Text="追跡対象",Width=100,Height=27,TextAlign=ContentAlignment.MiddleLeft});targetLine.Controls.Add(targetInputs);panel.Controls.Add(targetLine);
 addTarget.Click+=(_,_)=>{try{var target=BuildTarget();var index=targets.FindIndex(t=>string.Equals(t.PgSpec.Name,target.PgSpec.Name,StringComparison.OrdinalIgnoreCase));if(index<0)targets.Add(target);else targets[index]=target;RefreshTargets();status.Text=$"追跡対象 {targets.Count}テーブル。操作前をまとめて取得できます。";}catch(ComparisonConfigurationException e){status.Text=e.Message;}catch(InvalidOperationException){status.Text="有効なテーブルと列を選択してください。";}};
 removeTarget.Click+=(_,_)=>{if(trackedList.SelectedIndex>=0){targets.RemoveAt(trackedList.SelectedIndex);RefreshTargets();UpdateTableSelection();}};
 trackedList.SelectedIndexChanged+=(_,_)=>{if(!refreshingTargets&&execution==null&&trackedList.SelectedItem is TrackedTable target)ShowTarget(target);};
 settings=new Control[]{table,reload,ignoreInputs,projectionInputs,comparison,filterRows,addFilter,addTarget,removeTarget};
 var reset=new Button(){Text="設定・取得結果をリセット",AutoSize=true};
 reset.Click+=(_,_)=>{ClearCaptured();foreach(var c in settings)c.Enabled=true;after.Enabled=copy.Enabled=false;UpdateTableSelection();grid.DataSource=null;grid.Visible=false;rowCounts.Text="取得対象件数: 未確認";reload.Enabled=!string.IsNullOrEmpty(pgConnection);status.Text=(matchKeys==null?(table.SelectedItem as CommonTable)?.KeyError:null)??(!string.IsNullOrEmpty(pgConnection)?"設定を変更できます。操作前から取得してください。":"接続先が設定されていません。作成者に設定済みのアプリを依頼してください。");};
 var buttons=new FlowLayoutPanel(){Width=870,Height=34};buttons.Controls.AddRange(new Control[]{before,after,copy,reset,cancel});panel.Controls.Add(buttons);panel.Controls.Add(rowCounts);panel.Controls.Add(status);
 Controls.Add(grid);Controls.Add(panel);after.Enabled=false;
 Shown+=(_,_)=>{ResizeTargets();FitWindow();};grid.VisibleChanged+=(_,_)=>QueueFitWindow();status.SizeChanged+=(_,_)=>QueueFitWindow();rowCounts.SizeChanged+=(_,_)=>QueueFitWindow();
 before.Click+=async(_,_)=>await Execute(CaptureBefore);
 after.Click+=async(_,_)=>await Execute(CaptureAfter);
 copy.Click+=(_,_)=>{try{var data=new DataObject();data.SetData("XML Spreadsheet",false,new MemoryStream(Encoding.UTF8.GetBytes(spreadsheetXml!+"\0")));Clipboard.SetDataObject(data,true);status.Text="コピーしました。Excelの貼り付け先セルで Ctrl+V を押してください。";}catch{MessageBox.Show("コピーできませんでした。もう一度お試しください。");}};
 if(demo){
 var cases=multipleDemo?DemoEvidence.CreateMultiple():[DemoEvidence.CreateSingleUpdate()];var tables=new List<string>();var summaries=new List<ResultSummary>();
 foreach(var d in cases){var result=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
 tables.Add(Engine.Render(d.PgBefore.Columns,result,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true).SpreadsheetXml);
 var spec=d.Spec with{Definition=d.PgBefore.Columns.Select(c=>new TableColumn(c,"String","text")).ToArray()};
 targets.Add(new(spec,spec,d.PgBefore,d.SqlBefore,$"PG 前 {d.PgBefore.Rows.Count}件 → 後 {d.PgAfter.Rows.Count}件 / SQL Server 前 {d.SqlBefore.Rows.Count}件 → 後 {d.SqlAfter.Rows.Count}件"));
 summaries.AddRange(result.Select(e=>new ResultSummary(e.Key,e.Pg.Operation,e.Sql.Operation,string.Join(", ",e.Pg.Changed.Union(e.Sql.Changed).Select(i=>d.PgBefore.Columns[i])),e.Match?"一致":"不一致",spec.Name)));}
 spreadsheetXml=Engine.CombineSpreadsheetXml(tables);results=summaries.ToArray();RefreshResults();RefreshTargets();trackedList.SelectedIndex=0;
 Text+="（検証用データ・DB接続なし）";
 foreach(var c in settings)c.Enabled=false;before.Enabled=after.Enabled=reset.Enabled=false;copy.Enabled=true;
 rowCounts.Text=string.Join(Environment.NewLine,targets.Select(t=>t.PgSpec.Name+" / "+t.Counts));
 status.Text=$"検証用データ: {targets.Count}テーブル / 変更行 {results.Length}件。実DBには接続しません。";
 }else{
 try{var config=ConnectionSettings.Load(Path.Combine(AppContext.BaseDirectory,ConnectionSettings.FileName));pgConnection=config.Postgres;sqlConnection=config.SqlServer;before.Enabled=false;status.Text="共通テーブルを読み込みます。";}
 catch{before.Enabled=reload.Enabled=false;status.Text="接続設定がないか読み取れません。作成者に設定済みの配布フォルダーを依頼してください。";}
 }
 if(!demo)Shown+=async(_,_)=>{await RestoreSession();if(!string.IsNullOrEmpty(pgConnection))await LoadTables(true);};


 }
 protected override void WndProc(ref Message m){if((uint)m.Msg==Program.ActivateMessage){if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Show();Activate();BringToFront();}base.WndProc(ref m);}
 string ConnectionId=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pgConnection+"\0"+sqlConnection)));
 async Task SaveSession() {
 if(demo)return;
 var finalStatus=status.Text;
 var state=new SavedSession(4,ConnectionId,selectedTable,selectedColumns.Text,ignore.Text,filterRows.Controls.OfType<FilterRow>().Select(r=>r.Saved).ToArray(),
  null,null,null,null,spreadsheetXml,results,rowCounts.Text,finalStatus,"",targets.ToArray(),matchKeys,comparisonIgnored,autoMatch);
 stateBusy=true;Enabled=false;status.Text="前回の内容を暗号化して保存中…";
 try{await Task.Run(()=>SessionStore.Save(SessionStore.DefaultPath,state));status.Text=finalStatus;}
 finally{stateBusy=false;Enabled=true;}
 }
 async Task RestoreSession() {
 if(!File.Exists(SessionStore.DefaultPath))return;
 stateBusy=true;Enabled=false;status.Text="前回の内容を復元中…";
 try {
 var restored=await Task.Run(()=>SessionStore.Load(SessionStore.DefaultPath));selectedTable=restored.Table;selectedColumns.Text=restored.Columns;ignore.Text=restored.Ignored;
 matchKeys=restored.MatchKeys;comparisonIgnored=restored.ComparisonIgnored??[];comparisonTable=selectedTable;
 autoMatch=restored.Version>=4?restored.AutoMatch:restored.MatchKeys==null;
 filterRows.Controls.Clear();foreach(var filter in restored.Filters){AddFilterRow();((FilterRow)filterRows.Controls[^1]).Restore(filter);}if(filterRows.Controls.Count==0)AddFilterRow();ResizeFilterRows();status.Text=restored.Status+"（前回の入力を復元）";
 // 旧単一テーブルの保存データも、追跡対象1件として引き継ぐ。
 var savedTargets=restored.Targets??(restored.PgSpec!=null&&restored.SqlSpec!=null?[new TrackedTable(restored.PgSpec,restored.SqlSpec,restored.PgBefore,restored.SqlBefore,restored.Counts)]:[]);
 // 保存済みの取得・結果は従来の判定を保つ。旧版で未取得の標準設定だけ自動対応へ移す。
 if(restored.Version<4)savedTargets=savedTargets.Select(t=>t.PgBefore==null&&t.PgSpec.MatchKeys==null?t with{PgSpec=t.PgSpec with{AutoMatch=true},SqlSpec=t.SqlSpec with{AutoMatch=true}}:t).ToArray();
 targets.AddRange(savedTargets.Select(t=>restored.ConnectionId==ConnectionId?t:t with{PgBefore=null,SqlBefore=null,Counts=""}));RefreshTargets();
 if(restored.ConnectionId==ConnectionId){spreadsheetXml=restored.SpreadsheetXml;results=restored.Results;rowCounts.Text=restored.Counts;}
 if(HasBaseline)foreach(var c in settings)c.Enabled=false;
 RefreshResults();after.Enabled=HasBaseline;copy.Enabled=spreadsheetXml!=null;
 var selected=targets.FirstOrDefault(t=>t.PgSpec.Name==selectedTable);
 if(selected!=null){table.Items.Add(new CommonTable(new(selected.PgSpec.Name,selected.PgSpec.Keys),new(selected.SqlSpec.Name,selected.SqlSpec.Keys)));table.SelectedIndex=0;}
 status.Text=restored.Status+"（前回の内容を復元）";
 }catch{status.Text="前回の保存データを復元できませんでした。操作前から取得してください。";}
 finally{stateBusy=false;Enabled=true;}
 }
 void RefreshResults(){grid.DataSource=results.Select(r=>new{r.テーブル,r.PG操作,r.SQL操作,r.変更列,r.判定}).ToArray();grid.Visible=results.Length>0;}
 void RefreshTargets(){refreshingTargets=true;trackedList.Items.Clear();trackedList.Items.AddRange(targets.ToArray());ResizeTargets();refreshingTargets=false;}
 // 一覧は最大8行まで伸ばす。それ以上はスクロールし、少数のときも固定高の空白を残さない。
 void ResizeTargets(){trackedList.HorizontalExtent=trackedList.Items.Cast<object>().Select(t=>TextRenderer.MeasureText(t.ToString(),trackedList.Font).Width+8).DefaultIfEmpty(0).Max();trackedList.Height=Math.Max(1,Math.Min(8,trackedList.Items.Count))*trackedList.ItemHeight+4+(trackedList.HorizontalExtent>trackedList.ClientSize.Width?SystemInformation.HorizontalScrollBarHeight:0);QueueFitWindow();}
 void ClearCaptured(){for(var i=0;i<targets.Count;i++)targets[i]=targets[i] with{PgBefore=null,SqlBefore=null,Counts=""};spreadsheetXml=null;results=[];RefreshTargets();}
 TrackedTable BuildTarget(){
 if(table.SelectedItem is not CommonTable selected||availableColumns.Length==0)throw new InvalidOperationException("テーブルを選択してください。");
 string[] Split(string text)=>text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
 var rows=filterRows.Controls.OfType<FilterRow>().Where(r=>r.Condition!=null).ToArray();var filters=rows.Select(r=>r.Condition!).ToArray();var joins=rows.Skip(1).Select(r=>r.Join.Text).ToArray();
 var (p,s)=selected.Specs(Split(ignore.Text),null,matchKeys,comparisonIgnored);var columns=Split(selectedColumns.Text);
 foreach(var name in (matchKeys??[]).Concat(comparisonIgnored))if(!availableColumns.Any(c=>string.Equals(c.Name,name,StringComparison.OrdinalIgnoreCase)))
 throw new ComparisonConfigurationException(selected.Postgres.Name+": 比較設定に存在しない共通列があります。「比較設定」を見直してください。");
 // 両DBの識別列を取得に含める。対応列はCreateSelectCommandも自動追加する。
 if(columns.Length>0)columns=columns.Concat(p.Keys).Concat(s.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 p=p with{Columns=columns,Filters=filters,FilterJoins=joins,Definition=pgColumns,AutoMatch=autoMatch};s=s with{Columns=columns,Filters=filters,FilterJoins=joins,Definition=sqlColumns,AutoMatch=autoMatch};
 return new(p.WithAutomaticMetadata(s),s.WithAutomaticMetadata(p));
 }
 void ShowTarget(TrackedTable target){
 filteringTables=true;table.BeginUpdate();table.Items.Clear();table.Items.AddRange(allTables);
 var selected=allTables.FirstOrDefault(t=>t.Postgres.Name==target.PgSpec.Name)??new CommonTable(new(target.PgSpec.Name,target.PgSpec.Keys),new(target.SqlSpec.Name,target.SqlSpec.Keys));
 if(!table.Items.Contains(selected))table.Items.Add(selected);table.SelectedItem=selected;table.EndUpdate();filteringTables=false;selectedTable=target.PgSpec.Name;
 pgColumns=target.PgSpec.Definition??[];sqlColumns=target.SqlSpec.Definition??[];availableColumns=TableCatalog.CommonColumns(pgColumns,sqlColumns);
 selectedColumns.Text=string.Join(", ",target.PgSpec.Columns??[]);ignore.Text=string.Join(", ",target.PgSpec.Ignored);
 matchKeys=target.PgSpec.MatchKeys;comparisonIgnored=target.PgSpec.ComparisonIgnored??[];comparisonTable=target.PgSpec.Name;
 autoMatch=target.PgSpec.AutoMatch;
 filterRows.Controls.Clear();var conditions=target.PgSpec.Conditions;
 for(var i=0;i<conditions.Length;i++){AddFilterRow();var row=(FilterRow)filterRows.Controls[^1];row.Restore(new(conditions[i],i==0?"AND":target.PgSpec.JoinBefore(i)));row.BindColumns(SearchColumns());}
 if(conditions.Length==0)AddFilterRow();ResizeFilterRows();if(target.Counts.Length>0)rowCounts.Text=target.PgSpec.Name+" / "+target.Counts;
 UpdateTableSelection();
 }
 async Task CaptureBefore(){
 if(targets.Count==0)targets.Add(BuildTarget());ClearCaptured();RefreshResults();
 var captured=new List<TrackedTable>();var token=execution!.Token;
 // 全テーブルが成功してから基準を差し替える。途中で失敗した一部データを比較に使用しない。
 foreach(var target in targets){
 var p=target.PgSpec with{Definition=await TableCatalog.ReadColumns(true,pgConnection,target.PgSpec.Name,token)};
 var s=target.SqlSpec with{Definition=await TableCatalog.ReadColumns(false,sqlConnection,target.SqlSpec.Name,token)};
 p=p.WithAutomaticMetadata(s);s=s.WithAutomaticMetadata(p);
 var counts=await CheckCounts(p,s,token);var (pb,sb)=await CaptureWithProgress(p,s,null,null,"操作前",counts,token);
 captured.Add(new(p,s,pb,sb,$"PG 前 {pb.Rows.Count:N0}件 / SQL Server 前 {sb.Rows.Count:N0}件"));
 }
 targets.Clear();targets.AddRange(captured);RefreshTargets();foreach(var c in settings)c.Enabled=false;
 rowCounts.Text=string.Join(Environment.NewLine,targets.Select(t=>t.PgSpec.Name+" / "+t.Counts));status.Text=$"{targets.Count}テーブルの操作前を取得しました。両システムで操作してください。";
 }
 async Task CaptureAfter(){
 if(!HasBaseline)throw new InvalidOperationException("操作前を取得してください。");
 var token=execution!.Token;spreadsheetXml=null;results=[];RefreshResults();var summaries=new List<ResultSummary>();var tables=new List<string>();var completed=new List<TrackedTable>();
 foreach(var target in targets){
 var p=target.PgSpec;var s=target.SqlSpec;var pb=target.PgBefore!;var sb=target.SqlBefore!;
 var counts=await CheckCounts(p,s,token);var (pa,sa)=await CaptureWithProgress(p,s,pb,sb,"操作後",counts,token);status.Text=p.Name+" / 変更行を解析中…";
 var output=await Task.Run(()=>{var changes=Engine.Compare(pb,pa,sb,sa,p,token);return(Changes:changes,Xml:Engine.Render(pb.Columns,changes,p,pb,pa,sb,sa,true,token).SpreadsheetXml);});
 tables.Add(output.Xml);summaries.AddRange(output.Changes.Select(e=>new ResultSummary(e.Key,e.Pg.Operation,e.Sql.Operation,string.Join(", ",e.Pg.Changed.Union(e.Sql.Changed).Select(i=>pb.Columns[i])),e.Match?"一致":"不一致",p.Name)));
 completed.Add(target with{Counts=$"PG 前 {pb.Rows.Count:N0}件 → 後 {pa.Rows.Count:N0}件 / SQL Server 前 {sb.Rows.Count:N0}件 → 後 {sa.Rows.Count:N0}件"});
 }
 spreadsheetXml=await Task.Run(()=>Engine.CombineSpreadsheetXml(tables,token));results=summaries.ToArray();targets.Clear();targets.AddRange(completed);RefreshTargets();RefreshResults();
 rowCounts.Text=string.Join(Environment.NewLine,targets.Select(t=>t.PgSpec.Name+" / "+t.Counts));status.Text=$"{targets.Count}テーブル / 変更行 {results.Length}件 / 不一致 {results.Count(r=>r.判定=="不一致")}件。コピー後、Excelに貼り付けてください。";
 }
 void AddFilterRow() {
 var row=new FilterRow();row.BindColumns(SearchColumns());row.Remove.Click+=(_,_)=>{filterRows.Controls.Remove(row);row.Dispose();if(filterRows.Controls.Count==0)AddFilterRow();ResizeFilterRows();};
 filterRows.Controls.Add(row);ResizeFilterRows();
 }
 void ResizeFilterRows() {
 var rows=filterRows.Controls.OfType<FilterRow>().ToArray();for(var i=0;i<rows.Length;i++)rows[i].Join.Visible=i>0;
 filterRows.Height=Math.Min(190,rows.Length*34+6);QueueFitWindow();
 }
 void QueueFitWindow(){if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)FitWindow);}
 void FitWindow(){if(IsDisposed)return;panel.PerformLayout();var height=panel.PreferredSize.Height+(grid.Visible?Math.Min(220,grid.ColumnHeadersHeight+grid.RowTemplate.Height*Math.Max(1,grid.Rows.Count)+8):0)+4;if(ClientSize.Height!=height)ClientSize=new Size(ClientSize.Width,height);}

 void UpdateTableSelection() {
 var selected=table.SelectedItem as CommonTable;
 if(selected!=null)selectedTable=selected.Postgres.Name;
 before.Enabled=execution==null&&!string.IsNullOrEmpty(pgConnection)&&(targets.Count>0||selected!=null&&(selected.KeyError==null||matchKeys is {Length:>0})&&availableColumns.Length>0);
 comparison.Enabled=execution==null&&!HasBaseline&&selected!=null&&availableColumns.Length>0;
 comparisonSummary.Text=$"対応: {(autoMatch&&matchKeys==null?"自動（主キー・日時以外の値）":matchKeys==null?"主キー":string.Join(", ",matchKeys))} / 判定除外: {(comparisonIgnored.Length==0?"なし":string.Join(", ",comparisonIgnored))}";
 if(matchKeys==null&&selected?.KeyError is {} error)status.Text=error;
 else if(selected!=null&&!HasBaseline)status.Text="テーブルを選択しました。必要なら検索列と条件を指定し、操作前を取得してください。";
  if(!HasBaseline)rowCounts.Text="取得対象件数: 未確認";
 }
 void RefreshTables() {
 var current=table.SelectedItem as CommonTable;filteringTables=true;table.BeginUpdate();table.Items.Clear();
 table.Items.AddRange(allTables);
 if(current!=null&&table.Items.Contains(current))table.SelectedItem=current;
 table.EndUpdate();filteringTables=false;if(table.SelectedItem==null)availableColumns=[];UpdateTableSelection();
 }
 TableColumn[] SearchColumns()=>availableColumns.Where(c=>c.Searchable&&sqlColumns.Any(s=>string.Equals(s.Name,c.Name,StringComparison.OrdinalIgnoreCase)&&s.Searchable)).ToArray();
 void ChooseColumns(bool excluded) {
 var input=excluded?ignore:selectedColumns;
 var selectedTable=table.SelectedItem as CommonTable;var keys=(selectedTable?.Postgres.Keys??[]).Concat(selectedTable?.SqlServer.Keys??[]).Concat(matchKeys??[]).ToArray();
 var names=availableColumns.Where(c=>!excluded||!keys.Contains(c.Name,StringComparer.OrdinalIgnoreCase)).Select(c=>c.Name).ToArray();
 var selected=ElementPicker.Choose(this,excluded?"除外列を選択":"取得列を選択",names,input.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries));
 if(selected!=null)input.Text=string.Join(", ",selected);
 }
 void ChooseComparison() {
 if(table.SelectedItem is not CommonTable selected||availableColumns.Length==0)return;
 var chosen=ComparisonDialog.Choose(this,selected,availableColumns.Select(c=>c.Name).ToArray(),matchKeys,comparisonIgnored,ignore.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries),autoMatch);
 if(chosen is {} value){matchKeys=value.Keys;comparisonIgnored=value.Ignored;autoMatch=value.Automatic;comparisonTable=selected.Postgres.Name;UpdateTableSelection();}
 }
 async Task LoadColumnDefinitions() {
 if(demo||stateBusy||execution!=null||string.IsNullOrEmpty(pgConnection))return;
 if(table.SelectedItem is not CommonTable selected){pgColumns=sqlColumns=availableColumns=[];before.Enabled=false;return;}
 if(comparisonTable!=selected.Postgres.Name) {
 var saved=targets.FirstOrDefault(t=>t.PgSpec.Name==selected.Postgres.Name);
 matchKeys=saved?.PgSpec.MatchKeys;comparisonIgnored=saved?.PgSpec.ComparisonIgnored??[];comparisonTable=selected.Postgres.Name;
 autoMatch=saved?.PgSpec.AutoMatch??true;
 }
 pgColumns=sqlColumns=availableColumns=[];
 await Execute(async()=>{
 status.Text="選択したテーブルの列定義を読み込み中…";
 pgColumns=await TableCatalog.ReadColumns(true,pgConnection,selected.Postgres.Name,execution!.Token);
 sqlColumns=await TableCatalog.ReadColumns(false,sqlConnection,selected.SqlServer.Name,execution.Token);
 availableColumns=TableCatalog.CommonColumns(pgColumns,sqlColumns);
 foreach(var input in new[]{selectedColumns,ignore})input.Text=string.Join(", ",input.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Where(n=>availableColumns.Any(c=>string.Equals(c.Name,n,StringComparison.OrdinalIgnoreCase))));
 foreach(var row in filterRows.Controls.OfType<FilterRow>())row.BindColumns(SearchColumns());

 status.Text=$"列定義を取得しました。共通列 {availableColumns.Length}列。列を選択できます。";
 });
 UpdateTableSelection();
 }
 async Task LoadTables(bool preserve=false) {
 if(execution!=null||string.IsNullOrEmpty(pgConnection))return;
 if(!preserve){ClearCaptured();grid.DataSource=null;grid.Visible=false;}
 await Execute(async()=>{
 var previousSelection=selectedTable;table.Items.Clear();
 var token=execution!.Token;
 status.Text="PostgreSQLのテーブル・主キーを読み込み中…（中断できます）";
 var pg=await TableCatalog.Read(true,pgConnection,token);
 status.Text="SQL Serverのテーブル・主キーを読み込み中…（中断できます）";
 var sql=await TableCatalog.Read(false,sqlConnection,token);
 var common=TableCatalog.Common(pg,sql);allTables=common;RefreshTables();
 var selected=common.FirstOrDefault(t=>t.Postgres.Name==previousSelection);if(selected!=null)table.SelectedItem=selected;
 status.Text=common.Length==0?"両DBで読み取り可能な共通テーブルがありません（public / dbo）。":$"共通テーブル {common.Length:N0}件。テーブルを選択してください。";
 });
 await LoadColumnDefinitions();
 }
 async Task<(Snapshot Pg,Snapshot Sql)> CaptureWithProgress(TableSpec pg,TableSpec sql,Snapshot? pgPrevious,Snapshot? sqlPrevious,string phase,(long Pg,long Sql) counts,CancellationToken token) {
 // ProgressはUIキューへ遅れて届く。完了した段階の通知で次段階の表示を上書きしないようstageも照合する。
 var stage=$"{pg.Name} / {phase}";captureStage=stage;var pgRead=0;var sqlRead=0;
 void ShowProgress()=>status.Text=$"{stage} 取得中: PG {pgRead:N0}件 / SQL Server {sqlRead:N0}件（中断できます）";
 ShowProgress();
 var progress=new Progress<(bool Pg,int Count)>(value=>{if(IsDisposed||token.IsCancellationRequested||captureStage!=stage)return;if(value.Pg)pgRead=value.Count;else sqlRead=value.Count;ShowProgress();});
 try{return await Engine.CapturePair(pgConnection,sqlConnection,pg,sql,pgPrevious,sqlPrevious,counts,token,progress);}
 finally{captureStage=null;}
 }
 async Task<(long Pg,long Sql)> CheckCounts(TableSpec pg,TableSpec sql,CancellationToken token) {
 status.Text=pg.Name+" / PostgreSQLの対象件数を確認中…（中断できます）";rowCounts.Text=pg.Name+" / 取得対象件数: PG 確認中 / SQL Server 未確認";
 var p=await Task.Run(()=>Engine.CountRows(true,pgConnection,pg,token));
 rowCounts.Text=$"{pg.Name} / 今回の取得対象: PG {p:N0}件 / SQL Server 確認中";
 status.Text="SQL Serverの対象件数を確認中…（中断できます）";
 var s=await Task.Run(()=>Engine.CountRows(false,sqlConnection,sql,token));
 rowCounts.Text=$"{pg.Name} / 今回の取得対象: PG {p:N0}件 / SQL Server {s:N0}件（必要に応じて中断できます）";
 return(p,s);
 }
 // DBドライバーの例外本文には接続先等が入り得る。UIへそのまま表示せず、共有時も詳細を漏らさない。
 async Task Execute(Func<Task> action){
 if(execution!=null)return;execution=new CancellationTokenSource();trackedList.Enabled=false;
 before.Enabled=after.Enabled=copy.Enabled=false;foreach(Control c in before.Parent!.Controls)c.Enabled=false;cancel.Enabled=true;foreach(var c in settings)c.Enabled=false;
 try{await action();}
 catch(OperationCanceledException){ClearCaptured();grid.DataSource=null;grid.Visible=false;status.Text="中断しました。条件を調整し、操作前から取得してください。";}
 catch(FormatException){spreadsheetXml=null;status.Text="検索値を列の型へ変換できません。数値・日時・GUIDなどの入力形式を確認してください。";MessageBox.Show(status.Text,"検索条件エラー");}
 catch(OverflowException){spreadsheetXml=null;status.Text="検索値が列の型で扱える数値の範囲を超えています。";MessageBox.Show(status.Text,"検索条件エラー");}
 catch(ComparisonConfigurationException e){spreadsheetXml=null;status.Text=e.Message;MessageBox.Show(status.Text,"比較設定エラー");}
 catch(InvalidOperationException e) when(e.Message==Engine.ExcelLimitError){spreadsheetXml=null;status.Text=Engine.ExcelLimitError;MessageBox.Show(status.Text,"Excel出力エラー");}
 catch(Exception){spreadsheetXml=null;status.Text="取得に失敗しました。検索条件・接続設定・ネットワーク・読み取り権限・テーブル・主キー・列構成を確認してください。";MessageBox.Show(status.Text,"実行エラー");}
 finally{captureStage=null;execution.Dispose();execution=null;if(!HasBaseline)foreach(var c in settings)c.Enabled=true;trackedList.Enabled=true;foreach(Control c in before.Parent!.Controls)c.Enabled=true;cancel.Enabled=false;before.Enabled=!string.IsNullOrEmpty(pgConnection)&&(targets.Count>0||table.SelectedItem is CommonTable selected&&(selected.KeyError==null||matchKeys is {Length:>0})&&availableColumns.Length>0);after.Enabled=HasBaseline;copy.Enabled=spreadsheetXml!=null;try{await SaveSession();}catch{status.Text="処理は完了しましたが、前回の内容を保存できませんでした。空き容量・保存先の権限を確認してください。";}}
 }
}

class FilterRow:FlowLayoutPanel {
 readonly SearchColumnCombo column=new(){Width=190};
 readonly TextBox value=new(){Width=160},upper=new(){Width=130};
 TableColumn[] definitions=[];string pendingColumn="";
 readonly ComboBox op=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=55};
 public ComboBox Join {get;}=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=65};
 public Button Remove {get;}=new(){Text="削除",Width=55,Height=25};
 string ValueType=>definitions.FirstOrDefault(c=>c.Name==column.Selection)?.Numeric==true?"数値":"文字列";
 public SavedFilter Saved=>new(new(column.Selection,op.Text,value.Text,ValueType,upper.Text),Join.Text);
 public void Restore(SavedFilter saved){pendingColumn=saved.Filter.Column;column.SelectedItem=pendingColumn;op.SelectedItem=saved.Filter.Operator;value.Text=saved.Filter.Value;upper.Text=saved.Filter.Upper;Join.SelectedItem=saved.Join;}
 public TableFilter? Condition=>column.SelectedItem is not string name||name.Length==0?null:new(name,op.Text,value.Text,ValueType,upper.Text);
 public void BindColumns(TableColumn[] columns){var previous=column.Selection.Length>0?column.Selection:pendingColumn;definitions=columns;column.Items.Clear();column.Items.Add("");column.Items.AddRange(columns.Select(c=>c.Name).ToArray());column.SelectedItem=column.Items.Contains(previous)?previous:"";pendingColumn="";}
 public FilterRow() {
 Width=850;Height=28;WrapContents=false;Margin=new Padding(3);
 Label Caption(string text,int width)=>new(){Text=text,Width=width,Height=25,TextAlign=ContentAlignment.MiddleLeft};
 Join.Items.AddRange(new object[]{"AND","OR"});Join.SelectedIndex=0;
 var joinSlot=new Panel(){Width=65,Height=25};Join.Dock=DockStyle.Fill;joinSlot.Controls.Add(Join);
 op.Items.AddRange(new object[]{"=",">=","<=","範囲"});op.SelectedIndex=0;
 // 保存DTOのTypeは旧版との互換用。実際のパラメーター型はDB定義から決める。
 column.SelectedIndexChanged+=(_,_)=>{var definition=definitions.FirstOrDefault(c=>c.Name==column.Selection);value.PlaceholderText=definition?.ValueType switch{"DateTime" or "DateTimeOffset"=>"yyyy-MM-dd HH:mm:ss","Boolean"=>"true / false","Guid"=>"GUID",_=>""};upper.PlaceholderText=value.PlaceholderText;};
 var upperLabel=Caption("〜",14);upper.Visible=upperLabel.Visible=false;
 op.SelectedIndexChanged+=(_,_)=>upper.Visible=upperLabel.Visible=op.Text=="範囲";
 Controls.AddRange(new Control[]{joinSlot,Caption("検索列",44),column,Caption("条件",36),op,Caption("値",18),value,upperLabel,upper,Remove});
 foreach(Control c in Controls)c.Margin=new Padding(2,1,2,0);
 }
}

// 候補を絞り込まず、部分一致へジャンプする。編集文字は表示専用でDB識別子として使わない。
class SearchColumnCombo:ComboBox {
 string query="";int original=-1;bool canceled;
 public string Selection=>SelectedItem?.ToString()??"";
 public event EventHandler? SelectionConfirmed;
 public SearchColumnCombo(){DropDownStyle=ComboBoxStyle.DropDownList;DrawMode=DrawMode.OwnerDrawFixed;IntegralHeight=false;DropDownHeight=260;AccessibleName="検索列";}
 protected override void OnDropDown(EventArgs e){original=SelectedIndex;query="";canceled=false;base.OnDropDown(e);}
 protected override void OnSelectionChangeCommitted(EventArgs e){query="";base.OnSelectionChangeCommitted(e);if(!DroppedDown)SelectionConfirmed?.Invoke(this,e);}
 protected override void OnDropDownClosed(EventArgs e){var confirmed=!canceled&&(query.Length==0||Selection.Contains(query,StringComparison.OrdinalIgnoreCase));if(!confirmed)SelectedIndex=original;query="";Invalidate();base.OnDropDownClosed(e);if(confirmed)SelectionConfirmed?.Invoke(this,e);}
 protected override void OnKeyPress(KeyPressEventArgs e){
 if(!char.IsControl(e.KeyChar)){if(!DroppedDown)DroppedDown=true;query+=e.KeyChar;Jump(0);e.Handled=true;Invalidate();}base.OnKeyPress(e);
 }
 void Jump(int direction){
 for(var offset=direction==0?0:1;offset<Items.Count+(direction==0?0:1);offset++){
 var index=direction==0?offset:(SelectedIndex+(direction*offset)+Items.Count*2)%Items.Count;
 if((Items[index]?.ToString()??"").Contains(query,StringComparison.OrdinalIgnoreCase)){SelectedIndex=index;return;}}
 }
 protected override bool ProcessCmdKey(ref Message msg,Keys keyData){
 if(DroppedDown){
 if(keyData==Keys.Escape){canceled=true;DroppedDown=false;return true;}
 if(keyData==Keys.Enter){DroppedDown=false;return true;}
 if(keyData==Keys.Back){if(query.Length>0)query=query[..^1];Jump(0);Invalidate();return true;}
 if(query.Length>0&&keyData is Keys.Down or Keys.Up){Jump(keyData==Keys.Down?1:-1);Invalidate();return true;}
 }return base.ProcessCmdKey(ref msg,keyData);
 }
 protected override void OnDrawItem(DrawItemEventArgs e){
 e.DrawBackground();var edit=(e.State&DrawItemState.ComboBoxEdit)!=0;
 var text=edit&&DroppedDown&&query.Length>0?query:e.Index>=0?Items[e.Index]?.ToString()??"":"";
 TextRenderer.DrawText(e.Graphics,text,e.Font,e.Bounds,e.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);e.DrawFocusRectangle();base.OnDrawItem(e);
 }
}

// 候補はDB定義から渡す。フィルターで一時的に隠れたチェックも保持し、検索のたびに選択を失わない。
static class ComparisonDialog {
 public static (string[]? Keys,string[] Ignored,bool Automatic)? Choose(IWin32Window owner,CommonTable table,string[] names,string[]? keys,string[] ignored,string[] changeIgnored,bool automatic) {
 using var dialog=new Form(){Text=table.Postgres.Name+" の比較設定",ClientSize=new Size(760,270),FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
 var panel=new FlowLayoutPanel(){Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(12)};
 var usePrimary=new CheckBox(){Text="主キーでDB間の行を対応付ける",AutoSize=true,Checked=!automatic&&keys==null&&table.KeyError==null,Enabled=table.KeyError==null};
 var useAutomatic=new CheckBox(){Text="主キー・日時以外の値で自動対応（値の違いは×）",AutoSize=true,Checked=automatic&&keys==null,Enabled=table.KeyError==null};
 var matching=keys??[];var excluded=ignored.ToArray();
 var keyText=new TextBox(){Width=450,ReadOnly=true,AccessibleName="DB間の対応列"};var ignoredText=new TextBox(){Width=450,ReadOnly=true,AccessibleName="DB間判定の除外列"};
 var pickKeys=new Button(){Text="列を選択",AutoSize=true};var pickIgnored=new Button(){Text="列を選択",AutoSize=true};
 void Field(string text,TextBox input,Button button){var row=new FlowLayoutPanel(){Width=730,Height=32};row.Controls.AddRange(new Control[]{new Label(){Text=text,Width=175,Height=27,TextAlign=ContentAlignment.MiddleLeft},input,button});panel.Controls.Add(row);}
 void Refresh(){keyText.Text=useAutomatic.Checked?"自動":string.Join(", ",usePrimary.Checked?table.Postgres.Keys:matching);ignoredText.Text=string.Join(", ",excluded);pickKeys.Enabled=!usePrimary.Checked&&!useAutomatic.Checked;usePrimary.Enabled=table.KeyError==null&&!useAutomatic.Checked;}
 pickKeys.Click+=(_,_)=>{var chosen=ElementPicker.Choose(dialog,"DB間の対応列を選択",names.Where(n=>!changeIgnored.Contains(n,StringComparer.OrdinalIgnoreCase)).ToArray(),matching);if(chosen!=null){matching=chosen;Refresh();}};
 pickIgnored.Click+=(_,_)=>{var chosen=ElementPicker.Choose(dialog,"DB間判定の除外列を選択",names,excluded);if(chosen!=null){excluded=chosen;Refresh();}};
 usePrimary.CheckedChanged+=(_,_)=>Refresh();useAutomatic.CheckedChanged+=(_,_)=>Refresh();panel.Controls.Add(useAutomatic);panel.Controls.Add(usePrimary);Field("DB間の対応列",keyText,pickKeys);Field("DB間判定の除外列",ignoredText,pickIgnored);
 panel.Controls.Add(new Label(){Text="対応列は、両DBで同じ行を表す一意な値を選びます。複数列も指定できます。\n主キーがないDBでは操作前後の識別にも使います。採番IDは判定除外へ指定できます。",AutoSize=true,MaximumSize=new Size(725,0)});
 var message=new Label(){AutoSize=true,MaximumSize=new Size(725,0)};panel.Controls.Add(message);
 var buttons=new FlowLayoutPanel(){Dock=DockStyle.Bottom,Height=38,FlowDirection=FlowDirection.RightToLeft};var cancel=new Button(){Text="キャンセル",AutoSize=true,DialogResult=DialogResult.Cancel};var ok=new Button(){Text="決定",AutoSize=true};
 ok.Click+=(_,_)=>{try{table.Specs(changeIgnored,null,useAutomatic.Checked||usePrimary.Checked?null:matching,excluded);dialog.DialogResult=DialogResult.OK;}catch(ComparisonConfigurationException e){message.Text=e.Message;}};
 buttons.Controls.AddRange(new Control[]{cancel,ok});dialog.Controls.Add(panel);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;Refresh();
 return dialog.ShowDialog(owner)==DialogResult.OK?(useAutomatic.Checked||usePrimary.Checked?null:matching,excluded,useAutomatic.Checked):null;
 }
}
static class ElementPicker {
 public static string[]? Choose(IWin32Window owner,string title,string[] names,string[] selected,bool multiple=true) {
 using var dialog=new Form(){Text=title,Width=620,Height=460,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
 var search=new TextBox(){Dock=DockStyle.Top,PlaceholderText="列名で絞り込み"};
 var list=new ListView(){Dock=DockStyle.Fill,View=View.List,CheckBoxes=multiple,MultiSelect=false,HideSelection=false};
 var chosen=selected.Where(n=>names.Contains(n,StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);bool loading=false;
 void Refresh(){loading=true;list.BeginUpdate();list.Items.Clear();foreach(var name in names.Where(n=>n.Contains(search.Text,StringComparison.OrdinalIgnoreCase)))list.Items.Add(new ListViewItem(name){Checked=chosen.Contains(name)});list.EndUpdate();loading=false;}
 list.ItemChecked+=(_,e)=>{if(loading)return;if(e.Item.Checked)chosen.Add(e.Item.Text);else chosen.Remove(e.Item.Text);};
 search.TextChanged+=(_,_)=>Refresh();
 var buttons=new FlowLayoutPanel(){Dock=DockStyle.Bottom,Height=38,FlowDirection=FlowDirection.RightToLeft};
 var ok=new Button(){Text="決定",AutoSize=true};var cancel=new Button(){Text="キャンセル",AutoSize=true,DialogResult=DialogResult.Cancel};
 ok.Click+=(_,_)=>{if(!multiple){if(list.SelectedItems.Count==0)return;chosen.Clear();chosen.Add(list.SelectedItems[0].Text);}dialog.DialogResult=DialogResult.OK;};
 if(!multiple)list.DoubleClick+=(_,_)=>ok.PerformClick();
 buttons.Controls.AddRange(new Control[]{cancel,ok});
 if(multiple){var clear=new Button(){Text="選択解除",AutoSize=true};clear.Click+=(_,_)=>{chosen.Clear();Refresh();};var all=new Button(){Text="表示列を選択",AutoSize=true};all.Click+=(_,_)=>{foreach(ListViewItem item in list.Items)chosen.Add(item.Text);Refresh();};buttons.Controls.AddRange(new Control[]{clear,all});}
 dialog.Controls.Add(list);dialog.Controls.Add(search);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;Refresh();
 return dialog.ShowDialog(owner)==DialogResult.OK?names.Where(chosen.Contains).ToArray():null;
 }
}
