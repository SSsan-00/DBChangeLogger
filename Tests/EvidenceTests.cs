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
    public void Comparison_FiltersRowsAndDetectsOperationsAndMismatches()
    {
        var d = DemoEvidence.Create();
        var result = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        Assert.AreEqual(6, result.Count);
        Assert.AreEqual(2, result.Count(e => !e.Match));
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
        Assert.AreEqual(2, actual.Count(e => !e.Match));
    }

    [TestMethod]
    public void Comparison_DetectsDifferentBeforeValuesForChangedColumn()
    {
        var d = DemoEvidence.Create();
        var rows = d.SqlBefore.Rows.ToDictionary(r => r.Key, r => r.Value.ToArray());
        rows["[\"1\"]"][2] = "11";
        var actual = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore with { Rows = rows }, d.SqlAfter, d.Spec);
        Assert.IsFalse(actual.Single(e => e.Key == "[\"1\"]").Match);
    }

    [TestMethod]
    public void Render_PreservesTextAndIncludesColorsAndMissingValueMarkers()
    {
        var d = DemoEvidence.Create();
        var result = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        var output = Engine.Render(d.PgBefore.Columns, result, d.Spec, d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter);
        foreach (var required in new[] { "#fff2cc", "#ffc7ce", "#e2f0d9", "#dddddd", "00123", "=1+1", "〈空文字〉", "〈行なし〉", "&lt;&amp;&gt;", "\\n", "\\t", "mso-number-format" })
            StringAssert.Contains(output.Html, required);
        StringAssert.Contains(output.Text, "'=1+1");
        StringAssert.Contains(output.Text,"対象: "+d.Spec.Name);
        Assert.IsFalse(output.Text.Contains("対象: "+d.Spec.Schema+"."));
        Assert.IsFalse(output.Text.Contains("黄色=変更 / 赤=DB間不一致 / NULL・空文字は明示"));
        Assert.IsFalse(output.Text.Contains("PG 前")||output.Text.Contains("SQL Server 前"));
        Assert.AreEqual(15, output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public void Render_OnlyChangedDatabaseRowsAfterOperationIncludingDeletedKeys()
    {
        var d=DemoEvidence.Create();var changes=Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec);
        var xml=XDocument.Parse(Engine.Render(d.PgBefore.Columns,changes,d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter).SpreadsheetXml);
        XNamespace ss="urn:schemas-microsoft-com:office:spreadsheet";
        var allRows=xml.Descendants(ss+"Row").Skip(3).Select(r=>r.Descendants(ss+"Data").Select(c=>c.Value).ToArray()).ToArray();
        var rows=allRows.Where(r=>r[0]!="判定").ToArray();
        var headers=xml.Descendants(ss+"Row").ElementAt(2).Descendants(ss+"Data").Select(c=>c.Value).ToArray();
        CollectionAssert.AreEqual(new[]{"DB","操作"}.Concat(d.PgBefore.Columns).ToArray(),headers);
        Assert.IsFalse(headers.Contains("主キー")||headers.Contains("時点"));
        Assert.AreEqual(11,rows.Length);Assert.IsTrue(rows.All(r=>r[1]!="変更なし"));
        Assert.AreEqual(1,rows.Count(r=>r[2]=="5"));
        Assert.IsTrue(rows.Where(r=>r[1]=="削除").All(r=>r[2]=="2"&&r.Skip(3).All(v=>v=="〈行なし〉")));
        var judgments=allRows.Where(r=>r[0]=="判定").ToArray();
        Assert.AreEqual(1,judgments.Length);
        Assert.AreEqual("判定",allRows[^1][0]);
        CollectionAssert.AreEqual(new[]{"◯","◯","×"},judgments[0].Skip(2).Take(3).ToArray());
        Assert.AreEqual("×",judgments[0][4]); // 全レコードのamountの不一致を集約。
        Assert.IsTrue(judgments.All(r=>r[7]=="除外")); // stamp。
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

}
