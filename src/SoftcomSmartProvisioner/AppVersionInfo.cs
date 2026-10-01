using System.Reflection;

namespace SoftcomSmartProvisioner;

public static class AppVersionInfo
{
    public static string Current
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version is null)
            {
                return "0.0.0";
            }

            var build = version.Build < 0 ? 0 : version.Build;
            var release = $"{version.Major}.{version.Minor}.{build}";
            return version.Revision > 0 ? $"{release}.{version.Revision}" : release;
        }
    }
}
