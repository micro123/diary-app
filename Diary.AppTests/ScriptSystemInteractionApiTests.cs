using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Constants;
using Diary.GUIBase.Events;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptSystemInteractionApiTests
{
    [TestMethod]
    public async Task RequestMainWindowActivationAsync_DispatchesRaiseWindowCommand()
    {
        var recipient = new object();
        string? command = null;
        WeakReferenceMessenger.Default.Register<RunCommandEvent>(
            recipient,
            (_, message) => command = message.Value);
        try
        {
            var api = new Diary.App.AppUserInteractionScriptApi();

            await api.RequestMainWindowActivationAsync();

            Assert.AreEqual(CommandNames.RaiseMainWindow, command);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }
}
