using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DbEvidenceTests;

[TestClass]
public sealed class ConfigureTests
{
    [TestMethod]
    public async Task FailedConnections_ReportBothDatabasesAndPreserveExistingSettings()
    {
        // 本物の.local.csを使うと実DBへ接続してしまうため、隔離したソースと架空の設定で実行する。
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var folder = Path.Combine(Path.GetTempPath(), "DBChangeLogger-ConfigureTest-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var name in new[] { "Core", "Configure" })
            {
                Directory.CreateDirectory(Path.Combine(folder, name));
                foreach (var file in Directory.GetFiles(Path.Combine(source, name)).Where(f => f.EndsWith(".csproj") || f.EndsWith(".cs") && !f.EndsWith(".local.cs")))
                    File.Copy(file, Path.Combine(folder, name, Path.GetFileName(file)));
            }
            File.WriteAllText(Path.Combine(folder, "Configure", "PrivateConnections.local.cs"), """
                namespace DbEvidenceConfigure;
                internal static class PrivateConnections {
                    internal static readonly string Postgres = "Host=127.0.0.1;Port=1;Database=fake;Username=fake;Password=not-for-public-logs;Timeout=1";
                    internal static readonly string SqlServer = "Server=tcp:127.0.0.1,1;Database=fake;User ID=fake;Password=not-for-public-logs;Connect Timeout=1;ConnectRetryCount=0";
                }
                """);
            var destination = Path.Combine(folder, "connections.enc");
            File.WriteAllText(destination, "previous-settings");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory=folder, RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=System.Text.Encoding.UTF8, StandardErrorEncoding=System.Text.Encoding.UTF8, UseShellExecute=false, CreateNoWindow=true };
            foreach (var argument in new[] { "run", "--project", Path.Combine(folder, "Configure", "Configure.csproj"), "-c", "Release", "--", destination }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            var output = await stdout + await stderr;
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains(output, "PostgreSQL: 接続確認失敗");
            StringAssert.Contains(output, "SQL Server: 接続確認失敗");
            Assert.IsFalse(output.Contains("not-for-public-logs"));
            Assert.AreEqual("previous-settings", File.ReadAllText(destination));
        }
        finally { Directory.Delete(folder, true); }
    }
}
