namespace ArknightsPainter.Core.Adb;

public static class AdbPathResolver
{
    public static string? Find(string? configuredPath = null)
    {
        var candidates = new List<string?> { configuredPath };
        candidates.Add(@"C:\Program Files\Netease\MuMuPlayer-12.0\shell\adb.exe");
        candidates.Add(@"C:\Program Files\Netease\MuMuPlayerGlobal-12.0\shell\adb.exe");
        candidates.Add(@"C:\Program Files\Netease\MuMu Player 12\shell\adb.exe");
        candidates.Add(@"C:\LDPlayer\LDPlayer9\adb.exe");
        candidates.Add(@"C:\Program Files\BlueStacks_nxt\HD-Adb.exe");
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            candidates.Add(Path.Combine(androidHome, "platform-tools", "adb.exe"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\Windows\platform-tools-latest-windows\platform-tools\adb.exe");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory.Trim(), "adb.exe"));
        }

        // Keep the bundled copy last so emulator-specific ADB builds remain
        // preferred, while portable installs still have a reliable fallback.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "Adb", "adb.exe"));

        return candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);
    }
}
