using System.Text;
using System.Xml.Linq;
using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace DbEvidenceTests;

[TestClass]
public sealed class EvidenceTests
{
    [TestMethod]
    public void PreviewReadsExactClipboardValuesColorsAndMultipleTableRows()
    {
        var cases=DemoEvidence.CreateMultiple().Append(DemoEvidence.Create()).ToArray();
        var xml=Engine.CombineSpreadsheetXml(cases.Select(d=>Engine.Render(d.PgBefore.Columns,Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec),d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true).SpreadsheetXml));
        XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
        var document=XDocument.Parse(xml);var rows=document.Descendants(ss+"Row").ToArray();
        var colors=document.Descendants(ss+"Style").ToDictionary(s=>(string)s.Attribute(ss+"ID")!,s=>(string)s.Element(ss+"Interior")!.Attribute(ss+"Color")!);
        var preview=Engine.ReadPreview(xml);Assert.AreEqual(rows.Length,preview.Rows.Length);
        Assert.AreEqual(rows.Max(r=>r.Elements(ss+"Cell").Count()),preview.ColumnCount);
        for(var r=0;r<rows.Length;r++) {
            var cells=rows[r].Elements(ss+"Cell").ToArray();Assert.AreEqual(cells.Length,preview.Rows[r].Length);
            for(var c=0;c<cells.Length;c++) {
                Assert.AreEqual(cells[c].Element(ss+"Data")!.Value,preview.Rows[r][c].Value);
                Assert.AreEqual(colors[(string)cells[c].Attribute(ss+"StyleID")!],preview.Rows[r][c].Color);
            }
        }
        var values=preview.Rows.SelectMany(r=>r).Select(c=>c.Value).ToArray();
        Assert.IsTrue(values.Contains("00123")&&values.Contains("=1+1")&&values.Contains("〈行なし〉"));
        Assert.AreEqual(3,preview.Rows.Count(r=>r.FirstOrDefault()?.Value=="判定"));Assert.IsTrue(preview.Rows.Any(r=>r.Length==0));
        var columns=Enumerable.Range(0,400).Select(i=>"col"+i).ToArray();
        var wideBefore=new Snapshot(columns,new(){["1"]=columns.Select(_=>(string?)"00123").ToArray()},DateTimeOffset.UnixEpoch);
        var wideAfter=wideBefore with{Rows=new(){["1"]=columns.Select(_=>(string?)"=1+1").ToArray()}};
        var spec=new TableSpec("public","wide",["col0"],[]);
        var wide=Engine.ReadPreview(Engine.Render(columns,Engine.Compare(wideBefore,wideAfter,wideBefore,wideAfter,spec),spec,wideBefore,wideAfter,wideBefore,wideAfter,true).SpreadsheetXml);
        Assert.AreEqual(402,wide.ColumnCount);Assert.AreEqual("=1+1",wide.Rows[2][401].Value);Assert.AreEqual("#fff2cc",wide.Rows[2][401].Color);
    }

    [TestMethod]
    public void Comparison_FiltersRowsAndDetectsOperationsAndMismatches()
    {
        var d = DemoEvidence.Create();
        var result = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        Assert.AreEqual(6, result.Count);
        Assert.AreEqual(6, result.Count(e => !e.Match));
        Assert.AreEqual("削除", result.Single(e => e.Key == "[\"2\"]").Pg.Operation);
        Assert.AreEqual("追加", result.Single(e => e.Key == "[\"8\"]").Pg.Operation);
        Assert.AreEqual("変更なし", result.Single(e => e.Key == "[\"5\"]").Sql.Operation);
        Assert.AreEqual("更新", result.Single(e => e.Key == "[\"6\"]").Pg.Operation);
        Assert.IsFalse(result.Any(e => e.Key is "[\"3\"]" or "[\"7\"]"));
    }

    [TestMethod]
    public void Comparison_AlignsColumnsByName()
    {
        var d = DemoEvidence.Create();
        var reordered = d.SqlAfter with
        {
            Columns = d.SqlAfter.Columns.Reverse().ToArray(),
            Rows = d.SqlAfter.Rows.ToDictionary(r => r.Key, r => r.Value.Reverse().ToArray())
        };
        var actual = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, reordered, d.Spec);
        Assert.AreEqual(6, actual.Count);
        Assert.AreEqual(6, actual.Count(e => !e.Match));
    }

    [TestMethod]
    public void Comparison_IgnoresDifferentBeforeValuesForChangedColumn()
    {
        var d = DemoEvidence.CreateSingleUpdate();
        var rows = d.SqlBefore.Rows.ToDictionary(r => r.Key, r => r.Value.ToArray());
        rows["[\"1\"]"][2] = "11";
        var actual = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore with { Rows = rows }, d.SqlAfter, d.Spec);
        Assert.IsTrue(actual.Single(e => e.Key == "[\"1\"]").Match);
    }

    [TestMethod]
    public void Render_PreservesTextAndIncludesColorsAndMissingValueMarkers()
    {
        var d = DemoEvidence.Create();
        var result = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        var output = Engine.Render(d.PgBefore.Columns, result, d.Spec, d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter);
        foreach (var required in new[] { "#fff2cc", "#ffc7ce", "#ddebf7", "#dddddd", "00123", "=1+1", "〈空文字〉", "〈行なし〉", "&lt;&amp;&gt;", "\\n", "\\t", "mso-number-format" })
            StringAssert.Contains(output.Html, required);
        StringAssert.Contains(output.Text, "'=1+1");
        StringAssert.Contains(output.Text,"対象: "+d.Spec.Name);
        Assert.IsFalse(output.Text.Contains("対象: "+d.Spec.Schema+"."));
        Assert.IsFalse(output.Text.Contains("黄色=変更 / 赤=DB間不一致 / NULL・空文字は明示"));
        Assert.IsFalse(output.Text.Contains("PG 前")||output.Text.Contains("SQL Server 前"));
        Assert.AreEqual(15, output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public void Render_ChangedKeysIncludeBothDatabasesAfterOperationAndDeletedKeys()
    {
        var d=DemoEvidence.Create();var changes=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
        var xml=XDocument.Parse(Engine.Render(d.PgBefore.Columns,changes,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter).SpreadsheetXml);
        XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
        var allRows=xml.Descendants(ss+"Row").Skip(3).Select(r=>r.Descendants(ss+"Data").Select(c=>c.Value).ToArray()).ToArray();
        var rows=allRows.Where(r=>r[0]!="判定").ToArray();
        var headers=xml.Descendants(ss+"Row").ElementAt(2).Descendants(ss+"Data").Select(c=>c.Value).ToArray();
        CollectionAssert.AreEqual(new[]{"DB","操作"}.Concat(d.PgBefore.Columns).ToArray(),headers);
        Assert.IsFalse(headers.Contains("主キー")||headers.Contains("時点"));
        Assert.AreEqual(11,rows.Length);
        Assert.AreEqual(1,rows.Count(r=>r[2]=="5"));
        Assert.IsFalse(rows.Any(r=>r[1]=="変更なし"));
        CollectionAssert.AreEqual(Enumerable.Repeat("PostgreSQL",6).Concat(Enumerable.Repeat("SQL Server",5)).ToArray(),rows.Select(r=>r[0]).ToArray());
        Assert.IsFalse(rows.Any(r=>r[2] is "3" or "7"));
        Assert.IsTrue(rows.Where(r=>r[1]=="削除").All(r=>r.Skip(2).All(v=>v=="〈行なし〉")));
        var judgments=allRows.Where(r=>r[0]=="判定").ToArray();
        Assert.AreEqual(1,judgments.Length);
        Assert.AreEqual("判定",allRows[^1][0]);
        Assert.IsTrue(judgments[0].Skip(2).All(v=>v=="×"),"変更行数が違う場合は除外列も含め全列×。");
        foreach(var cell in xml.Descendants(ss+"Row").Where(r=>r.Element(ss+"Cell")?.Element(ss+"Data")?.Value=="判定").SelectMany(r=>r.Elements(ss+"Cell").Skip(2)))
            Assert.AreEqual(cell.Element(ss+"Data")!.Value switch {"◯"=>"Ce2f0d9","×"=>"Cffc7ce",_=>"Cffffff"},(string?)cell.Attribute(ss+"StyleID"));
    }

    [TestMethod]
    public void Render_OmitsSummaryCellsAndUnspecifiedColumns()
    {
        var d=DemoEvidence.CreateSingleUpdate();
        var after=d.PgAfter with { Rows=new(d.PgAfter.Rows) { ["[\"2\"]"]=["2","商品B","25"] } };
        var before=d.PgBefore with { Rows=new(d.PgBefore.Rows) { ["[\"2\"]"]=["2","商品B","20"] } };
        var changes=Engine.Compare(before,after,before,d.SqlAfter with { Rows=new(d.SqlAfter.Rows) { ["[\"2\"]"]=["2","商品B","20"] } },d.Spec);
        var output=Engine.Render(before.Columns,changes,d.Spec,before,after,before,d.SqlAfter);
        var firstRow=XDocument.Parse(output.SpreadsheetXml).Descendants(XName.Get("Row","urn:schemas-microsoft-com:office:spreadsheet")).First();
        Assert.AreEqual(1,firstRow.Elements().Count(),"先頭行には対象だけを出力する。");
        Assert.IsFalse(output.Text.Contains("除外列:"));
        Assert.IsFalse(output.Text.Contains("取得列:"));
        Assert.IsFalse(output.Text.Contains("変更行:")||output.Text.Contains("不一致:"));
        Assert.AreEqual(6,XDocument.Parse(output.SpreadsheetXml).Descendants(XName.Get("Row","urn:schemas-microsoft-com:office:spreadsheet")).Count());
        var selected=Engine.Render(before.Columns,changes,d.Spec with {Columns=["name","amount"]},before,after,before,d.SqlAfter);
        StringAssert.Contains(selected.Text,"取得列: id, name, amount");
        Assert.AreEqual(7,XDocument.Parse(selected.SpreadsheetXml).Descendants(XName.Get("Row","urn:schemas-microsoft-com:office:spreadsheet")).Count());
    }

    [TestMethod]
    public void Render_OneSidedChangesRetainUnchangedAndAbsentCounterpartRows()
    {
        var d=DemoEvidence.CreateSingleUpdate();var empty=d.PgBefore with{Rows=new()};
        foreach(var (pb,pa,sb,sa,unchangedDb) in new[]{
            (d.PgBefore,d.PgBefore,d.SqlBefore,d.SqlAfter,"PostgreSQL"),
            (empty,d.PgAfter,empty,empty,"SQL Server")})
        {
            var changes=Engine.Compare(pb,pa,sb,sa,d.Spec);
            XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
            var rows=XDocument.Parse(Engine.Render(pb.Columns,changes,d.Spec,pb,pa,sb,sa).SpreadsheetXml)
                .Descendants(ss+"Row").Skip(2).Take(2).Select(r=>r.Descendants(ss+"Data").Select(c=>c.Value).ToArray()).ToArray();
            Assert.AreEqual(2,rows.Length);
            var counterpart=rows.Single(r=>r[0]==unchangedDb);
            CollectionAssert.AreEqual(new[]{unchangedDb,"変更なし"},counterpart.Take(2).ToArray());
            Assert.IsTrue(counterpart.Skip(2).All(string.IsNullOrEmpty));
        }
    }

    [TestMethod]
    public void CombineSpreadsheetXml_KeepsTablesWithSeparateColumnsKeysAndJudgments()
    {
        var d=DemoEvidence.CreateSingleUpdate();
        var first=Engine.Render(d.PgBefore.Columns,Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec),d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true).SpreadsheetXml;
        var spec=new TableSpec("public","orders",["id"],[]);
        var before=new Snapshot(["id","status"],new(){["[\"1\"]"]=["1","受付"]},DateTimeOffset.UtcNow);
        var after=before with{Rows=new(){["[\"1\"]"]=["1","完了"]}};
        var second=Engine.Render(before.Columns,Engine.Compare(before,after,before,after,spec),spec,before,after,before,after,true).SpreadsheetXml;
        XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
        var xml=XDocument.Parse(Engine.CombineSpreadsheetXml([first,second]));
        Assert.AreEqual(1,xml.Descendants(ss+"Worksheet").Count());Assert.AreEqual(1,xml.Descendants(ss+"Styles").Count());
        var rows=xml.Descendants(ss+"Row").Select(r=>r.Descendants(ss+"Data").Select(c=>c.Value).ToArray()).ToArray();
        Assert.AreEqual(11,rows.Length);Assert.AreEqual(0,rows[5].Length);
        Assert.AreEqual("対象: "+d.Spec.Name,rows[0][0]);Assert.AreEqual("対象: orders",rows[6][0]);
        CollectionAssert.AreEqual(new[]{"DB","操作","id","status"},rows[7]);
        Assert.AreEqual(2,rows.Count(r=>r.FirstOrDefault()=="判定"));
        Assert.AreEqual("商品A",rows[2][3]);Assert.AreEqual("完了",rows[8][3]);Assert.AreEqual("受付",before.Rows["[\"1\"]"][1]);
        Assert.IsTrue(xml.Descendants(ss+"Data").All(c=>(string?)c.Attribute(ss+"Type")=="String"));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>Engine.CombineSpreadsheetXml([first,second],canceled.Token));
        Assert.ThrowsExactly<InvalidOperationException>(()=>Engine.CombineSpreadsheetXml([]));
    }

    [TestMethod]
    public void ClipboardHtml_OffsetsReferToUtf8Bytes()
    {
        const string html = "<html><body><table><tr><td>日本語</td></tr></table></body></html>";
        var clipboard = Engine.ClipboardHtml(html);
        var bytes = Encoding.UTF8.GetBytes(clipboard);
        int Offset(string name) => int.Parse(clipboard.Split("\r\n").Single(l => l.StartsWith(name + ":")).Split(':')[1]);
        Assert.AreEqual("<table><tr><td>日本語</td></tr></table>", Encoding.UTF8.GetString(bytes[Offset("StartFragment")..Offset("EndFragment")]));
        Assert.AreEqual(bytes.Length, Offset("EndHTML"));
    }

    [TestMethod]
    public void SpreadsheetXml_DeclaresStringsAndPreservesWhitespaceWithoutFormulas()
    {
        var d = DemoEvidence.Create();
        var evidence = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        var output = Engine.Render(d.PgBefore.Columns, evidence, d.Spec, d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter);
        var document = XDocument.Parse(output.SpreadsheetXml);
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        Assert.IsTrue(document.Descendants(ss + "Data").All(d => (string?)d.Attribute(ss + "Type") == "String"));
        Assert.IsFalse(document.Descendants().Attributes(ss + "Formula").Any());
        Assert.IsTrue(document.Descendants(ss + "Data").Any(d => d.Value == "  前後空白  "));
        Assert.AreEqual(15, document.Descendants(ss + "Row").Count());
    }

    [TestMethod]
    public void Render_RejectsOverlongCellsInsteadOfSilentlyTruncating()
    {
        var spec = new TableSpec("public", "length_test", ["id"], []);
        var empty = new Snapshot(["id", "value"], new(), DateTimeOffset.UtcNow);
        var after = empty with { Rows = new() { ["[\"1\"]"] = ["1", new string('x', 32768)] } };
        var evidence = Engine.Compare(empty, after, empty, after, spec);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Engine.Render(empty.Columns, evidence, spec, empty, after, empty, after));
        Assert.AreEqual(Engine.ExcelLimitError, error.Message);
    }

    [TestMethod]
    public void FinalValuesAndOperationColorsAreIndependent()
    {
        var d=DemoEvidence.CreateSingleUpdate();
        // PGはamount、SQLはnameを更新するが、同じ主キーの操作後は同値。
        var sb=d.SqlBefore with{Rows=new(){["[\"1\"]"]=["1","旧名","15"]}};
        var changes=Engine.Compare(d.PgBefore,d.PgAfter,sb,d.SqlAfter,d.Spec);
        Assert.IsTrue(changes.Single().Match);
        Assert.AreEqual(0,changes.Single().Different.Length);
        var rows=Rows(d.PgBefore,d.PgAfter,sb,d.SqlAfter,d.Spec);
        Assert.AreEqual("Cfff2cc",Style(rows[2],4));Assert.AreEqual("Cffffff",Style(rows[2],3));
        Assert.AreEqual("Cfff2cc",Style(rows[3],3));Assert.AreEqual("Cffffff",Style(rows[3],4));
        Assert.IsTrue(rows[^1].Elements(ss+"Cell").Skip(2).All(c=>Style(c)=="Ce2f0d9"));
        // 更新していないnameにも操作後の差があれば判定対象。
        var sa=d.SqlAfter with{Rows=new(){["[\"1\"]"]=["1","別名","15"]}};
        changes=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,sa,d.Spec);
        CollectionAssert.AreEqual(new[]{1},changes.Single().Different);
        rows=Rows(d.PgBefore,d.PgAfter,d.SqlBefore,sa,d.Spec);
        Assert.AreEqual("Cffffff",Style(rows[2],3));Assert.AreEqual("Cfff2cc",Style(rows[3],3));
        Assert.AreEqual("Cffc7ce",Style(rows[^1],3));
        var empty=d.PgBefore with{Rows=new()};
        rows=Rows(empty,d.PgAfter,empty,d.SqlAfter,d.Spec);
        Assert.IsTrue(rows[2].Elements(ss+"Cell").Skip(2).All(c=>Style(c)=="Cddebf7"));
        rows=Rows(d.PgBefore,empty,d.SqlBefore,empty,d.Spec);
        Assert.IsTrue(rows[2].Elements(ss+"Cell").Skip(2).All(c=>Style(c)=="Cdddddd"));
        // 同数でも主キーが違う変更行は同一レコードに対応しない。
        var two=d.PgBefore with{Rows=new(d.PgBefore.Rows){["[\"2\"]"]=["2","商品A","10"]}};
        var pg=two with{Rows=new(two.Rows){["[\"1\"]"]=["1","商品A","15"]}};
        var sql=two with{Rows=new(two.Rows){["[\"2\"]"]=["2","商品A","15"]}};
        Assert.IsTrue(Engine.Compare(two,pg,two,sql,d.Spec).All(e=>!e.Match));
    }
    static readonly XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
    static XElement[] Rows(Snapshot pb,Snapshot pa,Snapshot sb,Snapshot sa,TableSpec spec)=>XDocument.Parse(Engine.Render(pb.Columns,Engine.Compare(pb,pa,sb,sa,spec),spec,pb,pa,sb,sa).SpreadsheetXml).Descendants(ss+"Row").ToArray();
    static string? Style(XElement row,int index)=>(string?)row.Elements(ss+"Cell").ElementAt(index).Attribute(ss+"StyleID");
    static string? Style(XElement cell)=>(string?)cell.Attribute(ss+"StyleID");

}
