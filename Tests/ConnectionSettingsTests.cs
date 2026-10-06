using System.Text;
using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DbEvidenceTests;

[TestClass]
public class ConnectionSettingsTests
{
    private static ConnectionSettings Sample() => new(
        "Host=pg.example.invalid;Database=sample;Username=test;Password=\"private;日本語\";SSL Mode=VerifyFull",
        "Server=sql.example.invalid;Database=sample;User ID=test;Password=\"private;日本語\";Encrypt=True");

    [TestMethod]
    public void RoundTripPreservesBothConnectionsWithoutPlaintext()
    {
        var settings = Sample();
        var bytes = settings.Encrypt();
        Assert.AreEqual(settings, ConnectionSettings.Decrypt(bytes));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.IsFalse(text.Contains("pg.example.invalid"));
        Assert.IsFalse(text.Contains("sql.example.invalid"));
        Assert.IsFalse(text.Contains("private"));
        Assert.IsFalse(bytes.SequenceEqual(settings.Encrypt()));
    }

    [TestMethod]
    public void CorruptionAndInvalidFormatNeverReturnConnections()
    {
        var original = Sample().Encrypt();
        foreach (var index in new[] { 0, 4, 16, 32, original.Length - 1 })
        {
            var bytes = original.ToArray();
            bytes[index] ^= 1;
            AssertSafeFailure(() => ConnectionSettings.Decrypt(bytes));
        }
        AssertSafeFailure(() => ConnectionSettings.Decrypt([]));
        AssertSafeFailure(() => ConnectionSettings.Decrypt(original[..^1]));
        AssertSafeFailure(() => ConnectionSettings.Decrypt(new byte[65537]));
    }

    [TestMethod]
    public void InvalidConnectionsProduceOnlyGenericErrors()
    {
        foreach (var settings in new[] {
            new ConnectionSettings("", Sample().SqlServer),
            new ConnectionSettings(Sample().Postgres, ""),
            new ConnectionSettings("Host=pg;Database=db;privateSecret=secret", Sample().SqlServer),
            new ConnectionSettings(Sample().Postgres, "Server=sql;Password=secret") })
            AssertSafeFailure(() => settings.Encrypt());
    }

    [TestMethod]
    public void FileLoadingRejectsMissingAndBrokenFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".enc");
        try
        {
            AssertSafeFailure(() => ConnectionSettings.Load(path));
            File.WriteAllBytes(path, Sample().Encrypt());
            Assert.AreEqual(Sample(), ConnectionSettings.Load(path));
            File.WriteAllText(path, "Password=private-secret");
            AssertSafeFailure(() => ConnectionSettings.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void AssertSafeFailure(Action action)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(action);
        Assert.AreEqual(ConnectionSettings.Error, error.Message);
        Assert.IsNull(error.InnerException);
    }
}
