using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Diary.Utils;

public static class SysInfo
{
    public static string GetHostname()
    {
        return Environment.MachineName;
    }

    public static string GetUsername()
    {
        return Environment.UserName;
    }
}