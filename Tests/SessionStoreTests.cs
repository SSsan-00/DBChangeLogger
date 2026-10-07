using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Runtime.Versioning;
namespace DbEvidenceTests;
[TestClass]
public class SessionStoreTests {
 [TestMethod]
 [SupportedOSPlatform("windows")]
 public void SessionRoundTripPreservesSnapshotsFiltersAndResultAndRejectsTampering() {
  if(!OperatingSystem.IsWindows()){Assert.Inconclusive("Windows DPAPIの検証です。");return;}
  var d=DemoEvidence.Create();var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".bin");
  var spec=d.Spec with{Definition=[new("id","Int32","integer"),new("name","String","text")]};
  var state=new SavedSession(1,"hash",d.Spec.Name,"name,amount","stamp",[new(new("name","=","  tester  "),"OR")],spec,spec,d.PgBefore,d.SqlBefore,"<xml>00123=1+1</xml>",[new("1","更新","変更なし","amount","不一致")],"7件","結果","demo");
  try {
   SessionStore.Save(path,state);var restored=SessionStore.Load(path);
   Assert.AreEqual(state.SpreadsheetXml,restored.SpreadsheetXml);Assert.AreEqual(state.Filters[0],restored.Filters[0]);
   CollectionAssert.AreEqual(state.PgBefore!.Columns,restored.PgBefore!.Columns);
   foreach(var row in state.PgBefore.Rows)CollectionAssert.AreEqual(row.Value,restored.PgBefore.Rows[row.Key]);
   Assert.AreEqual(state.Results[0],restored.Results[0]);
   Assert.AreEqual("demo",restored.TableSearch);CollectionAssert.AreEqual(spec.Definition!,restored.PgSpec!.Definition!);
   var bytes=File.ReadAllBytes(path);bytes[^40]^=1;File.WriteAllBytes(path,bytes);
   Assert.ThrowsExactly<CryptographicException>(()=>SessionStore.Load(path));
  }finally{File.Delete(path);}
 }
}
