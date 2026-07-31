using Microsoft.Win32;

namespace ZhongwenLens.App.Services;

/// <summary>
/// Run-at-login, via the per-user registry Run key.
/// </summary>
/// <remarks>
/// The MSIX build declared a startup task in its manifest and Windows offered a toggle under
/// Settings → Apps → Startup. An MSI has no equivalent, so the app registers itself. HKCU rather
/// than HKLM: it needs no elevation, and matches the per-user install.
///
/// Windows still shows the entry in Task Manager's Startup tab, where the user can disable it
/// independently — which is why <see cref="IsEnabled"/> reads the registry rather than trusting
/// a stored flag.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZhongwenLens";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the Run entry. Returns false if the registry refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            // Quoted: the install path contains a space ("Zhongwen Lens"), and without quotes
            // Windows would try to run "...\Zhongwen" and pass "Lens\..." as an argument.
            key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("startup registration", ex);
            return false;
        }
    }

    private static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;

            return Path.Combine(AppContext.BaseDirectory, "ZhongwenLens.App.exe");
        }
    }
}
