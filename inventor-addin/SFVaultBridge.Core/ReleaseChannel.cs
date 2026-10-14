using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>The Vault folder that holds released bridge packages ($/Tools/SF Vault Bridge).</summary>
public interface IReleaseChannel
{
    /// <summary>File names currently in the release folder (empty when the folder does not exist yet).</summary>
    IReadOnlyList<string> ListNames();

    /// <summary>Creates the release folder if needed and checks the zip in as a new file, category Documents. Returns the Vault path.</summary>
    string Publish(string localZip, string fileName, string comment);
}

public static class ReleaseChannelOps
{
    /// <summary>Registers publish_release: {zipPath, version?} -> {name, vaultPath, version}. Refuses a name that is already in Vault.</summary>
    public static void Register(Router router, IReleaseChannel channel, PathGuard guard, Func<Func<JsonNode?>, JsonNode?> onUi)
    {
        router.Register("publish_release", body => onUi(() => Publish(body, channel, guard)));
    }

    public static JsonNode Publish(JsonObject body, IReleaseChannel channel, PathGuard guard)
    {
        var zip = guard.Check(Args.Str(body, "zipPath"), "zipPath");
        if (!File.Exists(zip)) throw new BridgeException("not_found", $"{zip} does not exist.");
        var given = Args.OptStr(body, "version");
        SemVer version;
        if (!string.IsNullOrWhiteSpace(given))
        {
            if (!SemVer.TryParse(given, out version)) throw new BridgeException("bad_request", $"'{given}' is not a version like 1.2.3.");
        }
        else if (!AppVersion.TryParseFileName(Path.GetFileName(zip), out version))
            throw new BridgeException("bad_request", "Pass 'version', or name the zip 'SF Vault Bridge <x.y.z>.zip'.");
        var name = AppVersion.FileName(version);
        if (channel.ListNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("exists", $"{AppVersion.ReleaseFolder}/{name} is already in Vault. Bump inventor-addin/VERSION and rebuild.");
        var path = channel.Publish(zip, name, $"SF Vault Bridge {version}");
        return new JsonObject { ["name"] = name, ["vaultPath"] = path, ["version"] = version.ToString() };
    }
}
