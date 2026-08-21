using Diary.App;
using Diary.Update;

namespace Diary.AppTests;

[TestClass]
public sealed class AppStartupOptionsTests
{
    [TestMethod]
    public void Parse_ReadsCoreOnlyAndUpdateTransaction()
    {
        var options = AppStartupOptions.Parse(
        [
            AppStartupOptions.CoreOnlyArgument,
            UpdateProtocol.StartupTransactionArgument,
            "/tmp/updates/transaction.json",
        ]);

        Assert.IsTrue(options.CoreOnly);
        Assert.AreEqual("/tmp/updates/transaction.json", options.UpdateTransactionPath);
    }

    [TestMethod]
    public void Parse_WhenUpdateTransactionHasNoValue_IgnoresIt()
    {
        var options = AppStartupOptions.Parse([UpdateProtocol.StartupTransactionArgument]);

        Assert.IsNull(options.UpdateTransactionPath);
    }

    [TestMethod]
    public void Parse_IsCaseInsensitive()
    {
        var options = AppStartupOptions.Parse(
        ["--CORE-ONLY", "--UPDATE-TRANSACTION", "transaction.json"]);

        Assert.IsTrue(options.CoreOnly);
        Assert.AreEqual("transaction.json", options.UpdateTransactionPath);
    }
}
