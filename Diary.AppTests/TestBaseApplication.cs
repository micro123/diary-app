using System.Reflection;
using Diary.Core.Data.AppConfig;
using Diary.Database;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;

namespace Diary.AppTests;

internal class TestBaseApplication : BaseApp
{
    public override AllConfig AppConfig => throw new NotSupportedException();
    public override bool SurveyEnabled { get; protected set; }
    public override bool DatabaseOk { get; protected set; }
    public override string DatabaseStatusMessage { get; protected set; } = string.Empty;
    public override IServiceProvider Services { get; protected set; } = new EmptyServiceProvider();
    public override IDbFactory? UseFactory { get; protected set; }
    public override DbInterfaceBase? UseDb { get; protected set; }

    public override SettingItemModel CreateModelFor(
        string caption,
        string help,
        string key,
        object obj,
        PropertyInfo property)
        => throw new NotSupportedException();

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
