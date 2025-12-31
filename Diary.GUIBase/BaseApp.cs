using System.Reflection;
using Avalonia;
using Diary.Core.Data.AppConfig;
using Diary.Database;
using Diary.GUIBase.ViewModels;

namespace Diary.GUIBase;

/// <summary>
/// 这个类需要定义程序各个部分需要用到的东西，实现放在 Diary.App 项目中的 App 类中
/// </summary>
public abstract class BaseApp: Application
{
    /// <summary>
    /// 程序配置
    /// </summary>
    public abstract AllConfig AppConfig { get; }
    
    /// <summary>
    /// 设置模型生成器
    /// </summary>
    /// <param name="caption">标题</param>
    /// <param name="help">帮助说明</param>
    /// <param name="key">配置的KEY，这个是唯一的</param>
    /// <param name="obj">配置值的父对象</param>
    /// <param name="property">可以通过此属性信息获取和设置配置的值</param>
    /// <returns>设置模型</returns>
    public abstract SettingItemModel CreateModelFor(string caption, string help, string key, object obj, PropertyInfo property);
    
    /// <summary>
    /// 是否启用了调查服务
    /// </summary>
    public abstract bool SurveyEnabled { get; protected set; }
    
    /// <summary>
    /// 数据库是否已经启用
    /// </summary>
    public abstract bool DatabaseOk { get; protected set; }
    
    /// <summary>
    /// 获取 DI 容器
    /// </summary>
    public abstract IServiceProvider Services { get; protected set; }
    
    /// <summary>
    /// 获取当前使用的数据库工厂
    /// </summary>
    public abstract IDbFactory? UseFactory { get; protected set; }
    
    /// <summary>
    /// 获取当前使用的数据库
    /// </summary>
    public abstract DbInterfaceBase? UseDb { get; protected set; }

    /// <summary>
    /// 获取当前的实例
    /// </summary>
    public static BaseApp Instance { get; } = (BaseApp)Current!;
}