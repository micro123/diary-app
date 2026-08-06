using Diary.Script.Runtime;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptCreationPolicyTests
{
    [TestMethod]
    [DataRow("daily-summary")]
    [DataRow("v1.editor_script")]
    public void IsValidId_AcceptsSafeIds(string id) => Assert.IsTrue(ScriptCreationPolicy.IsValidId(id));

    [TestMethod]
    [DataRow("")]
    [DataRow("-unsafe")]
    [DataRow("a")]
    [DataRow("contains space")]
    public void IsValidId_RejectsInvalidIds(string id) => Assert.IsFalse(ScriptCreationPolicy.IsValidId(id));

    [TestMethod]
    public void IsInsideDirectory_RejectsSiblingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "scripts");
        Assert.IsFalse(ScriptCreationPolicy.IsInsideDirectory(Path.Combine(root + "-backup", "script.cs"), root));
        Assert.IsTrue(ScriptCreationPolicy.IsInsideDirectory(Path.Combine(root, "script.cs"), root));
    }
}
