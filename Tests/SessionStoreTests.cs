using DbEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Runtime.Versioning;
namespace DbEvidenceTests;
[TestClass]
public class SessionStoreTests {
 [TestMethod]
 [SupportedOSPlatform("windows")]
 public void OldAndTwoTableEvidenceRestoreWithoutReformatting() {
  if(!OperatingSystem.IsWindows()){Assert.Inconclusive("Windows DPAPIの検証です。");return;}
  var d=DemoEvidence.CreateSingleUpdate();var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".bin");
  var current=Engine.Render(d.PgBefore.Columns,Engine.Compare(d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,d.Spec),d.Spec,d.PgBefore,d.PgAfter,d.SqlBefore,d.SqlAfter,true).SpreadsheetXml;
  const string old="<Workbook xmlns='urn:schemas-microsoft-com:office:spreadsheet'><Worksheet><Table><Row><Cell><Data>旧形式</Data></Cell></Row></Table></Worksheet></Workbook>";
  try {
   foreach(var xml in new[]{old,current}) {
    var state=new SavedSession(5,"hash",d.Spec.Name,"","",[],d.Spec,d.Spec,d.PgBefore,d.SqlBefore,xml,[],"","",Targets:[new(d.Spec,d.Spec,d.PgBefore,d.SqlBefore)]);
    SessionStore.Save(path,state);var restored=SessionStore.Load(path);Assert.AreEqual(xml,restored.SpreadsheetXml);
    CollectionAssert.AreEqual(d.PgBefore.Rows["[\"1\"]"],restored.Targets![0].PgBefore!.Rows["[\"1\"]"]);
    var preview=Engine.ReadPreview(restored.SpreadsheetXml!);
    Assert.AreEqual(xml==old?0:1,preview.Rows.Count(r=>r.FirstOrDefault()?.Value=="操作前"));
    Assert.AreEqual(xml==old?0:1,preview.Rows.Count(r=>r.FirstOrDefault()?.Value=="操作後"));
   }
  }finally{File.Delete(path);}
 }
 [TestMethod]
 [SupportedOSPlatform("windows")]
 public void SessionRoundTripPreservesSnapshotsFiltersAndResultAndRejectsTampering() {
  if(!OperatingSystem.IsWindows()){Assert.Inconclusive("Windows DPAPIの検証です。");return;}
  var d=DemoEvidence.Create();var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".bin");
  var spec=d.Spec with{Definition=[new("id","Int32","integer"),new("name","String","text")]};
  var other=spec with{Name="orders",Filters=[new("id",">=","100","数値")],Columns=["id","amount"],Ignored=[],MatchKeys=["name"],ComparisonIgnored=["id"],BusinessIdentity=true};
  spec=spec with{AutoMatch=true,AutoMatchExcluded=["id","registered"]};
  var state=new SavedSession(5,"hash",d.Spec.Name,"name,amount","stamp",[new(new("name","=","  tester  "),"OR")],spec,spec,d.PgBefore with{Keyless=true},d.SqlBefore,"<xml>00123=1+1</xml>",[new("1","更新","変更なし","amount","不一致",spec.Name)],"7件","結果","demo",[new(spec,spec,d.PgBefore,d.SqlBefore,"7件"),new(other,other,d.PgBefore,d.SqlBefore,"2件")],["name"],["id"],true);
  try {
   SessionStore.Save(path,state);var restored=SessionStore.Load(path);
   Assert.AreEqual(state.SpreadsheetXml,restored.SpreadsheetXml);Assert.AreEqual(state.Filters[0],restored.Filters[0]);
   Assert.IsTrue(restored.PgBefore!.Keyless);
   CollectionAssert.AreEqual(state.PgBefore!.Columns,restored.PgBefore!.Columns);
   foreach(var row in state.PgBefore.Rows)CollectionAssert.AreEqual(row.Value,restored.PgBefore.Rows[row.Key]);
   Assert.AreEqual(state.Results[0],restored.Results[0]);
   Assert.IsTrue(restored.AutoMatch);Assert.IsTrue(restored.Targets![0].PgSpec.AutoMatch);CollectionAssert.AreEqual(spec.AutoMatchExcluded!,restored.Targets[0].PgSpec.AutoMatchExcluded!);
   Assert.AreEqual("demo",restored.TableSearch);CollectionAssert.AreEqual(spec.Definition!,restored.PgSpec!.Definition!);
   Assert.AreEqual(2,restored.Targets!.Length);Assert.AreEqual("orders",restored.Targets[1].PgSpec.Name);
   CollectionAssert.AreEqual(other.Conditions,restored.Targets[1].PgSpec.Conditions);CollectionAssert.AreEqual(other.Columns!,restored.Targets[1].PgSpec.Columns!);
   CollectionAssert.AreEqual(state.MatchKeys!,restored.MatchKeys!);CollectionAssert.AreEqual(state.ComparisonIgnored!,restored.ComparisonIgnored!);
   CollectionAssert.AreEqual(other.MatchKeys!,restored.Targets[1].PgSpec.MatchKeys!);CollectionAssert.AreEqual(other.ComparisonIgnored!,restored.Targets[1].PgSpec.ComparisonIgnored!);Assert.IsTrue(restored.Targets[1].PgSpec.BusinessIdentity);
   CollectionAssert.AreEqual(d.PgBefore.Rows["[\"1\"]"],restored.Targets[1].PgBefore!.Rows["[\"1\"]"]);
   SessionStore.Save(path,state with{Version=1,Targets=null});Assert.IsNull(SessionStore.Load(path).Targets);
   SessionStore.Save(path,state with{Version=2,MatchKeys=null,ComparisonIgnored=null});Assert.IsNull(SessionStore.Load(path).MatchKeys);
   SessionStore.Save(path,state with{Version=3,AutoMatch=false});Assert.IsFalse(SessionStore.Load(path).AutoMatch);
   SessionStore.Save(path,state with{Version=4});Assert.AreEqual(4,SessionStore.Load(path).Version);
   SessionStore.Save(path,state);
   var bytes=File.ReadAllBytes(path);bytes[^40]^=1;File.WriteAllBytes(path,bytes);
   Assert.ThrowsExactly<CryptographicException>(()=>SessionStore.Load(path));
  }finally{File.Delete(path);}
 }
}
