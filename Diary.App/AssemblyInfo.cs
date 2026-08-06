using System.Reflection;
using System.Runtime.CompilerServices;
using Diary.App;
using Diary.Core;

[assembly: AssemblyVersion(DataVersion.VersionString)]
[assembly: AssemblyFileVersion(DataVersion.VersionString)]
[assembly: AssemblyDescription(AppInfo.AppName)]
[assembly: InternalsVisibleTo("Diary.DbTests")]
[assembly: InternalsVisibleTo("Diary.UtilTests")]
