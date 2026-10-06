namespace DbEvidence;

// DBに接続せず、本番と同じ比較・描画処理を検証するデータ。
public static class DemoEvidence
{
    public static (TableSpec Spec, Snapshot PgBefore, Snapshot PgAfter, Snapshot SqlBefore, Snapshot SqlAfter) CreateSingleUpdate()
    {
        var before = new Snapshot(["id", "name", "amount"], new() { ["[\"1\"]"] = ["1", "商品A", "10"] }, DateTimeOffset.UtcNow);
        var after = before with { Rows = new() { ["[\"1\"]"] = ["1", "商品A", "15"] } };
        return (new("public", "evidence_demo", ["id"], []), before, after, before, after);
    }

    public static (TableSpec Spec, Snapshot PgBefore, Snapshot PgAfter, Snapshot SqlBefore, Snapshot SqlAfter) Create()
    {
        string[] columns = ["id", "name", "amount", "code", "formula_text", "stamp", "date_text", "long_number", "scientific_text", "space_text"];
        var time = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        Snapshot Make(params string?[][] rows) => new(columns,
            rows.ToDictionary(r => System.Text.Json.JsonSerializer.Serialize(new[] { r[0] }),
                r => r.Concat(new string?[] { "2026-10-06", "12345678901234567890", "1E10", "  前後空白  " }).ToArray()), time);
        var before = Make(
            ["1", "更新前", "10", "00123", "=1+1", "0"],
            ["2", "削除", "20", "00002", "+1+1", "0"],
            ["3", "変更なし", "30", "00003", "@文字列", "0"],
            ["4", "不一致", "40", "00004", "-1+1", "0"],
            ["5", "片側更新", "50", "00005", "=SUM(A1:A2)", "0"],
            ["6", null, "60", "00006", "改行\nタブ\t<&>", "0"],
            ["7", "除外列のみ", "70", "00007", "通常", "0"]);
        Snapshot After(bool pg) => Make(
            ["1", "更新後", "12", "00123", "=1+1", "0"],
            ["3", "変更なし", "30", "00003", "@文字列", "0"],
            ["4", "不一致", pg ? "41" : "42", "00004", "-1+1", "0"],
            ["5", "片側更新", pg ? "51" : "50", "00005", "=SUM(A1:A2)", "0"],
            ["6", "", "60", "00006", "改行\nタブ\t<&>", "0"],
            ["7", "除外列のみ", "70", "00007", "通常", pg ? "1" : "2"],
            ["8", "追加（日本語）", "80", "00008", "=1+1", "0"]) with { At = time.AddMinutes(1) };
        return (new("public", "evidence_demo", ["id"], ["stamp"]), before, After(true), before, After(false));
    }
}
