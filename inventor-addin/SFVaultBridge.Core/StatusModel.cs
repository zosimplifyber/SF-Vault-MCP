namespace SFVaultBridge.Core;

public enum BridgeState { Ready, SignInNeeded, Stopped }

public sealed record BridgeStatusView(BridgeState State, string Title, string Details);

/// <summary>Pure mapping from bridge facts to the ribbon button's state and text.</summary>
public static class StatusModel
{
    public static BridgeStatusView Evaluate(bool listening, StatusInfo? status, DateTime? lastRequestUtc, string prefix)
    {
        var signedIn = status?.SignedIn == true;
        var (state, title) = !listening ? (BridgeState.Stopped, "Stopped")
            : signedIn ? (BridgeState.Ready, "Ready")
            : (BridgeState.SignInNeeded, "Sign in to Vault");
        var last = lastRequestUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "none yet";
        var lines = new List<string>
        {
            title,
            $"User: {(string.IsNullOrWhiteSpace(status?.User) ? "none" : status!.User)}",
            $"Vault: {(string.IsNullOrWhiteSpace(status?.Vault) ? "none" : status!.Vault)}",
            $"Address: {prefix}",
            $"Last request: {last}",
        };
        if (state == BridgeState.Stopped) lines.Add("See bridge.log for the reason.");
        else if (state == BridgeState.SignInNeeded) lines.Add("Sign in on the Vault tab (Log In).");
        return new BridgeStatusView(state, title, string.Join(Environment.NewLine, lines));
    }
}
