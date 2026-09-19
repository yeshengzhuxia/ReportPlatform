namespace ReportPlatform.Infrastructure;

public sealed record PlatformSettings(string DataDirectory, string EncryptionKey, string AdminPassword, bool CookieSecure)
{
    public static PlatformSettings Load(WebApplicationBuilder builder)
    {
        // Source checkout compatibility only. Published applications use their own configuration.
        var root = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../.."));
        var sourceCheckout = File.Exists(Path.Combine(root, "ReportPlatform.sln"));
        var legacy = new Dictionary<string, string>();
        var envFile = Path.Combine(root, ".env");
        if (sourceCheckout && File.Exists(envFile))
            foreach (var line in File.ReadAllLines(envFile))
            {
                var index = line.IndexOf('=');
                if (index > 0 && !line.TrimStart().StartsWith('#'))
                    legacy[line[..index].Trim()] = line[(index + 1)..].Trim().Trim('"', '\'');
            }
        string Value(string name, string oldName) =>
            !string.IsNullOrEmpty(builder.Configuration[$"Platform:{name}"])
                ? builder.Configuration[$"Platform:{name}"]!
                : legacy.GetValueOrDefault(oldName, "");
        var configuredDirectory = builder.Configuration["Platform:DataDirectory"] ?? "App_Data";
        var directory = sourceCheckout && configuredDirectory == "App_Data" && File.Exists(Path.Combine(root, "data/platform.sqlite"))
            ? Path.Combine(root, "data")
            : Path.GetFullPath(configuredDirectory, builder.Environment.ContentRootPath);
        return new(directory, Value("EncryptionKey", "ENCRYPTION_KEY"), Value("AdminPassword", "ADMIN_PASSWORD"),
            builder.Configuration.GetValue("Platform:CookieSecure", true));
    }
}
