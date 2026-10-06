using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DbEvidenceTests;

[TestClass]
[TestCategory("ExcelArtifact")]
public sealed class ExcelArtifactTests
{
    [TestMethod]
    public void PastedWorkbook_PreservesAllTextColorsAndDoesNotContainFormulas()
    {
        var path = Environment.GetEnvironmentVariable("EVIDENCE_EXCEL_FILE");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("DBChangeLogger.exe --demoからExcelへ貼り付けて保存したxlsxをEVIDENCE_EXCEL_FILEで指定してください。");
        using var file = File.Open(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        XDocument Read(string file)
        {
            using var stream = zip.GetEntry(file)!.Open();
            return XDocument.Load(stream);
        }
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = Read("xl/worksheets/sheet1.xml");
        var strings = Read("xl/sharedStrings.xml").Descendants(ns + "si")
            .Select(s => string.Concat(s.Descendants(ns + "t").Select(t => t.Value))).ToArray();
        var styles = Read("xl/styles.xml");
        var formats = styles.Root!.Element(ns + "cellXfs")!.Elements(ns + "xf").ToArray();
        var fills = styles.Root!.Element(ns + "fills")!.Elements(ns + "fill").ToArray();
        var cells = sheet.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
        Assert.AreEqual(0, sheet.Descendants(ns + "f").Count(), "貼り付け値が数式になっています。");

        var d = DemoEvidence.CreateSingleUpdate();
        var evidence = Engine.Compare(d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter, d.Spec);
        var html = Engine.Render(d.PgBefore.Columns, evidence, d.Spec, d.PgBefore, d.PgAfter, d.SqlBefore, d.SqlAfter).Html;
        var rows = Regex.Matches(html, "<tr>(.*?)</tr>", RegexOptions.Singleline);
        for (var r = 0; r < rows.Count; r++)
        {
            var expected = Regex.Matches(rows[r].Groups[1].Value, "<td.*?background-color:(#[0-9a-f]+)'.*?<span[^>]*>(.*?)</span></td>", RegexOptions.Singleline);
            for (var c = 0; c < expected.Count; c++)
            {
                var address = ((char)('A' + c)).ToString() + (r + 1);
                Assert.IsTrue(cells.TryGetValue(address, out var cell), address + "がありません。");
                Assert.AreEqual("s", (string?)cell!.Attribute("t"), address + "が文字列型ではありません。");
                Assert.AreEqual(WebUtility.HtmlDecode(expected[c].Groups[2].Value), strings[int.Parse(cell.Element(ns + "v")!.Value)], address + "の値が変わっています。");
                var format = formats[int.Parse((string?)cell.Attribute("s") ?? "0")];
                var numberFormat = (string?)format.Attribute("numFmtId");
                var custom = styles.Descendants(ns + "numFmt").SingleOrDefault(f => (string?)f.Attribute("numFmtId") == numberFormat);
                Assert.IsTrue(numberFormat == "49" || (string?)custom?.Attribute("formatCode") == "@", address + "の書式が文字列ではありません。");
                var color = fills[int.Parse((string)format.Attribute("fillId")!)].Descendants(ns + "fgColor").SingleOrDefault();
                var wanted = expected[c].Groups[1].Value[1..].ToUpperInvariant();
                if (wanted != "FFFFFF") Assert.AreEqual("FF" + wanted, (string?)color?.Attribute("rgb"), address + "の色が違います。");
            }
        }
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual(rows.Cast<System.Text.RegularExpressions.Match>().Sum(row=>Regex.Matches(row.Groups[1].Value,"<td").Count),cells.Count(c=>c.Value.Element(ns+"v")!=null||c.Value.Element(ns+"is")!=null));
    }
}
