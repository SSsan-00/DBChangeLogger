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
  ApplicationConfiguration.Initialize();Application.Run(new MainForm(args.Contains("--demo")));
 }
}
class MainForm:Form {
 readonly string pgConnection="",sqlConnection="";
 readonly ComboBox table=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=300};
 readonly TextBox ignore=new(){Width=450};
 readonly Button reload=new(){Text="テーブル一覧を再読込",AutoSize=true};
 readonly TextBox selectedColumns=new(){Width=450};
 readonly FlowLayoutPanel filterRows=new(){Width=880,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
 readonly FlowLayoutPanel panel=new(){Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(6)};
 readonly Button addFilter=new(){Text="＋ 条件を追加",AutoSize=true};
 readonly Button before=new(){Text="1. 操作前を取得",AutoSize=true},after=new(){Text="2. 操作後を取得・比較",AutoSize=true},copy=new(){Text="3. エビデンスをコピー",AutoSize=true,Enabled=false};
 readonly Button cancel=new(){Text="中断",AutoSize=true,Enabled=false};
 readonly Label rowCounts=new(){Text="取得対象件数: 未確認",AutoSize=true,MaximumSize=new Size(870,0)};CancellationTokenSource? execution;string? captureStage;
 readonly Label status=new(){AutoSize=true,MaximumSize=new Size(870,0)};readonly DataGridView grid=new(){Dock=DockStyle.Fill,Visible=false,ReadOnly=true,AllowUserToAddRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells};
 readonly bool demo;bool closingAllowed,stateBusy;string selectedTable="";ResultSummary[] results=[];
 // pb/sbは最初の比較基準。操作後を再取得しても置き換えず、途中の更新をまとめた最終差分にする。
 Snapshot? pb,sb;TableSpec? pSpec,sSpec;string? spreadsheetXml;readonly Control[] settings;
 public MainForm(bool demo=false){this.demo=demo;Text="DBChangeLogger";ClientSize=new Size(900,300);BackColor=SystemColors.Window;
 void Field(string label,Control input){var line=new FlowLayoutPanel(){Width=870,Height=30};line.Controls.Add(new Label(){Text=label,Width=180,Height=23,TextAlign=ContentAlignment.MiddleLeft});line.Controls.Add(input);panel.Controls.Add(line);}
 var tableInputs=new FlowLayoutPanel(){Width=675,Height=28};tableInputs.Controls.AddRange(new Control[]{table,reload});Field("テーブル一覧",tableInputs);
 Field("取得列（カンマ区切り）",selectedColumns);
 Field("除外列（カンマ区切り）",ignore);
 table.SelectedIndexChanged+=(_,_)=>UpdateTableSelection();
 reload.Click+=async(_,_)=>await LoadTables();
 var conditionHeader=new FlowLayoutPanel(){Width=870,Height=30};conditionHeader.Controls.AddRange(new Control[]{new Label(){Text="検索条件",Width=180,Height=23,TextAlign=ContentAlignment.MiddleLeft},addFilter});panel.Controls.Add(conditionHeader);
 panel.Controls.Add(filterRows);AddFilterRow();addFilter.Click+=(_,_)=>AddFilterRow();
 cancel.Click+=(_,_)=>{execution?.Cancel();status.Text="中断しています…";};FormClosing+=async(_,e)=>{if(closingAllowed)return;e.Cancel=true;if(stateBusy){status.Text="保存・復元が完了するまでお待ちください。";return;}if(execution!=null){execution.Cancel();status.Text="処理を中断してから、もう一度閉じてください。";return;}try{Enabled=false;await SaveSession();closingAllowed=true;Close();}catch{Enabled=true;status.Text="前回の内容を保存できませんでした。保存先の空き容量・権限を確認してください。";}};
 settings=new Control[]{table,reload,ignore,selectedColumns,filterRows,addFilter};
 var reset=new Button(){Text="設定・取得結果をリセット",AutoSize=true};
 reset.Click+=(_,_)=>{pb=sb=null;pSpec=sSpec=null;spreadsheetXml=null;results=[];foreach(var c in settings)c.Enabled=true;after.Enabled=copy.Enabled=false;UpdateTableSelection();grid.DataSource=null;grid.Visible=false;rowCounts.Text="取得対象件数: 未確認";reload.Enabled=!string.IsNullOrEmpty(pgConnection);status.Text=(table.SelectedItem as CommonTable)?.KeyError??(!string.IsNullOrEmpty(pgConnection)?"設定を変更できます。操作前から取得してください。":"接続先が設定されていません。作成者に設定済みのアプリを依頼してください。");};
 var buttons=new FlowLayoutPanel(){Width=870,Height=34};buttons.Controls.AddRange(new Control[]{before,after,copy,reset,cancel});panel.Controls.Add(buttons);panel.Controls.Add(rowCounts);panel.Controls.Add(status);
 Controls.Add(grid);Controls.Add(panel);after.Enabled=false;
 Shown+=(_,_)=>FitWindow();grid.VisibleChanged+=(_,_)=>QueueFitWindow();status.SizeChanged+=(_,_)=>QueueFitWindow();rowCounts.SizeChanged+=(_,_)=>QueueFitWindow();
 before.Click+=async(_,_)=>await Execute(async()=>{pb=sb=null;spreadsheetXml=null;results=[];grid.DataSource=null;grid.Visible=false;if(table.SelectedItem is not CommonTable selected)throw new InvalidOperationException("テーブルを選択してください。");string[] Split(string s)=>s.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);var rows=filterRows.Controls.OfType<FilterRow>().Where(row=>row.Condition!=null).ToArray();var filters=rows.Select(row=>row.Condition!).ToArray();var joins=rows.Skip(1).Select(row=>row.Join.Text).ToArray();(pSpec,sSpec)=selected.Specs(Split(ignore.Text),null);var columns=Split(selectedColumns.Text);pSpec=pSpec with{Columns=columns,Filters=filters,FilterJoins=joins};sSpec=sSpec with{Columns=columns,Filters=filters,FilterJoins=joins};var token=execution!.Token;await CheckCounts(pSpec,sSpec,token);var p=await CaptureWithProgress(true,pSpec,null,"操作前",token);var s=await CaptureWithProgress(false,sSpec,null,"操作前",token);pb=p;sb=s;rowCounts.Text=$"操作前: PG {p.Rows.Count:N0}件 / SQL Server {s.Rows.Count:N0}件";foreach(var c in settings)c.Enabled=false;spreadsheetXml=null;copy.Enabled=false;grid.DataSource=null;grid.Visible=false;status.Text=$"操作前を取得しました。PG {p.Rows.Count}行 / SQL Server {s.Rows.Count}行。両システムで操作してください。";});
 after.Click+=async(_,_)=>await Execute(async()=>{var token=execution!.Token;await CheckCounts(pSpec!,sSpec!,token);var pa=await CaptureWithProgress(true,pSpec!,pb,"操作後",token);var sa=await CaptureWithProgress(false,sSpec!,sb,"操作後",token);status.Text="変更行を解析中…";var output=await Task.Run(()=>{var result=Engine.Compare(pb!,pa,sb!,sa,pSpec!,token);return (Result:result,Xml:Engine.Render(pb!.Columns,result,pSpec!,pb,pa,sb!,sa,true,token).SpreadsheetXml);});var result=output.Result;spreadsheetXml=output.Xml;rowCounts.Text=$"比較対象: PG 前 {pb!.Rows.Count:N0}件 → 後 {pa.Rows.Count:N0}件 / SQL Server 前 {sb!.Rows.Count:N0}件 → 後 {sa.Rows.Count:N0}件";results=result.Select(e=>new ResultSummary(e.Key,e.Pg.Operation,e.Sql.Operation,string.Join(", ",e.Pg.Changed.Union(e.Sql.Changed).Select(i=>pb.Columns[i])),e.Match?"一致":"不一致")).ToArray();grid.DataSource=results.Select(r=>new{r.PG操作,r.SQL操作,r.変更列,r.判定}).ToArray();grid.Visible=result.Count>0;status.Text=$"変更行 {result.Count}件 / 不一致 {result.Count(e=>!e.Match)}件。コピー後、Excelに貼り付けてください。";copy.Enabled=true;});
 copy.Click+=(_,_)=>{try{var data=new DataObject();data.SetData("XML Spreadsheet",false,new MemoryStream(Encoding.UTF8.GetBytes(spreadsheetXml!+"\0")));Clipboard.SetDataObject(data,true);status.Text="コピーしました。Excelの貼り付け先セルで Ctrl+V を押してください。";}catch{MessageBox.Show("コピーできませんでした。もう一度お試しください。");}};
 if(demo){
 var d=DemoEvidence.CreateSingleUpdate();var result=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
 spreadsheetXml=Engine.Render(d.PgBefore.Columns,result,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true).SpreadsheetXml;
 Text+="（検証用データ・DB接続なし）";table.Items.Add(new CommonTable(new(d.Spec.Name,d.Spec.Keys),new(d.Spec.Name,d.Spec.Keys)));table.SelectedIndex=0;ignore.Text=string.Join(",",d.Spec.Ignored);
 grid.DataSource=result.Select(e=>new{PG操作=e.Pg.Operation,SQL操作=e.Sql.Operation,変更列=string.Join(", ",e.Pg.Changed.Union(e.Sql.Changed).Select(i=>d.PgBefore.Columns[i])),判定=e.Match?"一致":"不一致"}).ToList();grid.Visible=result.Count>0;
 foreach(var c in settings)c.Enabled=false;before.Enabled=after.Enabled=reset.Enabled=false;copy.Enabled=true;
 rowCounts.Text=$"比較対象: PG 前 {d.PgBefore.Rows.Count:N0}件 → 後 {d.PgAfter.Rows.Count:N0}件 / SQL Server 前 {d.SqlBefore.Rows.Count:N0}件 → 後 {d.SqlAfter.Rows.Count:N0}件";
 status.Text=$"検証用データ: 変更行 {result.Count}件 / 不一致 {result.Count(e=>!e.Match)}件。実DBには接続しません。";
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
 var state=new SavedSession(1,ConnectionId,selectedTable,selectedColumns.Text,ignore.Text,filterRows.Controls.OfType<FilterRow>().Select(r=>r.Saved).ToArray(),
  pSpec,sSpec,pb,sb,spreadsheetXml,results,rowCounts.Text,finalStatus);
 stateBusy=true;Enabled=false;status.Text="前回の内容を暗号化して保存中…";
 try{await Task.Run(()=>SessionStore.Save(SessionStore.DefaultPath,state));status.Text=finalStatus;}
 finally{stateBusy=false;Enabled=true;}
 }
 async Task RestoreSession() {
 if(!File.Exists(SessionStore.DefaultPath))return;
 stateBusy=true;Enabled=false;status.Text="前回の内容を復元中…";
 try {
 var restored=await Task.Run(()=>SessionStore.Load(SessionStore.DefaultPath));selectedTable=restored.Table;selectedColumns.Text=restored.Columns;ignore.Text=restored.Ignored;
 filterRows.Controls.Clear();foreach(var filter in restored.Filters){AddFilterRow();((FilterRow)filterRows.Controls[^1]).Restore(filter);}if(filterRows.Controls.Count==0)AddFilterRow();ResizeFilterRows();status.Text=restored.Status+"（前回の入力を復元）";
 // 入力は引き継げるが、接続先の変わったデータを新しいDBの比較基準として使ってはいけない。
 if(restored.ConnectionId==ConnectionId&&restored.PgSpec!=null&&restored.SqlSpec!=null){
 pSpec=restored.PgSpec;sSpec=restored.SqlSpec;pb=restored.PgBefore;sb=restored.SqlBefore;spreadsheetXml=restored.SpreadsheetXml;results=restored.Results;
 table.Items.Add(new CommonTable(new(pSpec.Name,pSpec.Keys),new(sSpec.Name,sSpec.Keys)));table.SelectedIndex=0;
 if(pb!=null&&sb!=null)foreach(var c in settings)c.Enabled=false;
 grid.DataSource=results.Select(r=>new{r.PG操作,r.SQL操作,r.変更列,r.判定}).ToArray();grid.Visible=results.Length>0;after.Enabled=pb!=null&&sb!=null;copy.Enabled=spreadsheetXml!=null;
 rowCounts.Text=restored.Counts;status.Text=restored.Status+"（前回の内容を復元）";
 }
 }catch{status.Text="前回の保存データを復元できませんでした。操作前から取得してください。";}
 finally{stateBusy=false;Enabled=true;}
 }
 void AddFilterRow() {
 var row=new FilterRow();row.Remove.Click+=(_,_)=>{filterRows.Controls.Remove(row);row.Dispose();if(filterRows.Controls.Count==0)AddFilterRow();ResizeFilterRows();};
 filterRows.Controls.Add(row);ResizeFilterRows();
 }
 void ResizeFilterRows() {
 var rows=filterRows.Controls.OfType<FilterRow>().ToArray();for(var i=0;i<rows.Length;i++)rows[i].Join.Visible=i>0;
 filterRows.Height=Math.Min(190,rows.Length*34+6);QueueFitWindow();
 }
 void QueueFitWindow(){if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)FitWindow);}
 void FitWindow(){if(IsDisposed)return;panel.PerformLayout();var height=panel.PreferredSize.Height+(grid.Visible?280:0)+4;if(ClientSize.Height!=height)ClientSize=new Size(ClientSize.Width,height);}

 void UpdateTableSelection() {
 var selected=table.SelectedItem as CommonTable;
 if(selected!=null)selectedTable=selected.Postgres.Name;
 before.Enabled=execution==null&&selected?.KeyError==null&&selected!=null&&!string.IsNullOrEmpty(pgConnection);
 if(selected?.KeyError is {} error)status.Text=error;
 else if(selected!=null&&pb==null)status.Text="テーブルを選択しました。必要なら検索列と条件を指定し、操作前を取得してください。";
 rowCounts.Text="取得対象件数: 未確認";
 }
 async Task LoadTables(bool preserve=false) {
 if(execution!=null||string.IsNullOrEmpty(pgConnection))return;
 if(preserve&&pb!=null&&sb!=null)return;
 pb=sb=null;pSpec=sSpec=null;spreadsheetXml=null;results=[];grid.DataSource=null;grid.Visible=false;
 await Execute(async()=>{
 var previousSelection=selectedTable;table.Items.Clear();
 var token=execution!.Token;
 status.Text="PostgreSQLのテーブル・主キーを読み込み中…（中断できます）";
 var pg=await TableCatalog.Read(true,pgConnection,token);
 status.Text="SQL Serverのテーブル・主キーを読み込み中…（中断できます）";
 var sql=await TableCatalog.Read(false,sqlConnection,token);
 var common=TableCatalog.Common(pg,sql);table.Items.AddRange(common);
 var selected=common.FirstOrDefault(t=>t.Postgres.Name==previousSelection);if(selected!=null)table.SelectedItem=selected;
 status.Text=common.Length==0?"両DBで読み取り可能な共通テーブルがありません（public / dbo）。":$"共通テーブル {common.Length:N0}件。テーブルを選択してください。";
 });
 }
 async Task<Snapshot> CaptureWithProgress(bool pg,TableSpec spec,Snapshot? previous,string phase,CancellationToken token) {
 // ProgressはUIキューへ遅れて届く。完了した段階の通知で次段階の表示を上書きしないようstageも照合する。
 var stage=$"{(pg?"PostgreSQL":"SQL Server")} {phase}";captureStage=stage;
 status.Text=stage+"を取得中…（中断できます）";
 var progress=new Progress<int>(count=>{if(!IsDisposed&&!token.IsCancellationRequested&&captureStage==stage)status.Text=$"{stage} 取得中: {count:N0}件（中断できます）";});
 try{return await Task.Run(()=>Engine.Capture(pg,pg?pgConnection:sqlConnection,spec,previous,token,progress));}
 finally{captureStage=null;}
 }
 async Task CheckCounts(TableSpec pg,TableSpec sql,CancellationToken token) {
 status.Text="PostgreSQLの対象件数を確認中…（中断できます）";rowCounts.Text="取得対象件数: PG 確認中 / SQL Server 未確認";
 var p=await Task.Run(()=>Engine.CountRows(true,pgConnection,pg,token));
 rowCounts.Text=$"今回の取得対象: PG {p:N0}件 / SQL Server 確認中";
 status.Text="SQL Serverの対象件数を確認中…（中断できます）";
 var s=await Task.Run(()=>Engine.CountRows(false,sqlConnection,sql,token));
 rowCounts.Text=$"今回の取得対象: PG {p:N0}件 / SQL Server {s:N0}件（必要に応じて中断できます）";
 }
 // DBドライバーの例外本文には接続先等が入り得る。UIへそのまま表示せず、共有時も詳細を漏らさない。
 async Task Execute(Func<Task> action){
 execution=new CancellationTokenSource();
 before.Enabled=after.Enabled=copy.Enabled=false;foreach(Control c in before.Parent!.Controls)c.Enabled=false;cancel.Enabled=true;foreach(var c in settings)c.Enabled=false;
 try{await action();}
 catch(OperationCanceledException){pb=sb=null;pSpec=sSpec=null;spreadsheetXml=null;results=[];grid.DataSource=null;grid.Visible=false;status.Text="中断しました。条件を調整し、操作前から取得してください。";}
 catch(InvalidOperationException e) when(e.Message==Engine.ExcelLimitError){spreadsheetXml=null;status.Text=Engine.ExcelLimitError;MessageBox.Show(status.Text,"Excel出力エラー");}
 catch(Exception){spreadsheetXml=null;status.Text="取得に失敗しました。検索条件・接続設定・ネットワーク・読み取り権限・テーブル・主キー・列構成を確認してください。";MessageBox.Show(status.Text,"実行エラー");}
 finally{captureStage=null;execution.Dispose();execution=null;if(pb==null||sb==null)foreach(var c in settings)c.Enabled=true;foreach(Control c in before.Parent!.Controls)c.Enabled=true;cancel.Enabled=false;before.Enabled=table.SelectedItem is CommonTable {KeyError:null}&&!string.IsNullOrEmpty(pgConnection);after.Enabled=pb!=null&&sb!=null;copy.Enabled=spreadsheetXml!=null;try{await SaveSession();}catch{status.Text="処理は完了しましたが、前回の内容を保存できませんでした。空き容量・保存先の権限を確認してください。";}}
 }
}

class FilterRow:FlowLayoutPanel {
 readonly TextBox column=new(){Width=125},value=new(){Width=105},upper=new(){Width=100};
 readonly ComboBox op=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=55},type=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=75};
 public ComboBox Join {get;}=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=65};
 public Button Remove {get;}=new(){Text="削除",Width=55,Height=23};
 public SavedFilter Saved=>new(new(column.Text,op.Text,value.Text,type.Text,upper.Text),Join.Text);
 public void Restore(SavedFilter saved){column.Text=saved.Filter.Column;op.SelectedItem=saved.Filter.Operator;value.Text=saved.Filter.Value;type.SelectedItem=saved.Filter.Type;upper.Text=saved.Filter.Upper;Join.SelectedItem=saved.Join;}
 public TableFilter? Condition=>string.IsNullOrWhiteSpace(column.Text)?null:new(column.Text.Trim(),op.Text,value.Text,type.Text,upper.Text);
 public FilterRow() {
 Width=850;Height=28;WrapContents=false;Margin=new Padding(3);
 Label Caption(string text,int width)=>new(){Text=text,Width=width,Height=23,TextAlign=ContentAlignment.MiddleLeft};
 Join.Items.AddRange(new object[]{"AND","OR"});Join.SelectedIndex=0;
 var joinSlot=new Panel(){Width=65,Height=23};Join.Dock=DockStyle.Fill;joinSlot.Controls.Add(Join);
 op.Items.AddRange(new object[]{"=",">=","<=","範囲"});op.SelectedIndex=0;
 type.Items.AddRange(new object[]{"文字列","整数","小数","日時","GUID"});type.SelectedIndex=0;
 var upperLabel=Caption("〜",14);upper.Visible=upperLabel.Visible=false;
 op.SelectedIndexChanged+=(_,_)=>upper.Visible=upperLabel.Visible=op.Text=="範囲";
 Controls.AddRange(new Control[]{joinSlot,Caption("検索列",44),column,Caption("条件",36),op,type,Caption("値",18),value,upperLabel,upper,Remove});
 foreach(Control c in Controls)c.Margin=new Padding(2,2,2,0);
 }
}
