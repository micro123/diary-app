using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
public sealed class GetUserInfo
{
    [TestMethod]
    public void GetCurrentUser()
    {
        Assert.IsTrue(RedMineApis.GetUserInfo(out _));
    }
}