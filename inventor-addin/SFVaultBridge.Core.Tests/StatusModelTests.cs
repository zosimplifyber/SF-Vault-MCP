using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class StatusModelTests
{
    private const string Prefix = "http://localhost:8790/";
    private static readonly StatusInfo In = new(true, "zak", "Vault1", "srv");
    private static readonly StatusInfo Out = new(false, null, null, null);

    [Fact]
    public void Ready_when_listening_and_signed_in()
    {
        var v = StatusModel.Evaluate(true, In, null, Prefix);
        Assert.Equal(BridgeState.Ready, v.State);
        Assert.Equal("Ready", v.Title);
        Assert.Contains("User: zak", v.Details);
        Assert.Contains("Vault: Vault1", v.Details);
        Assert.Contains("Address: " + Prefix, v.Details);
        Assert.Contains("Last request: none yet", v.Details);
    }

    [Fact]
    public void SignInNeeded_when_listening_and_signed_out()
    {
        var v = StatusModel.Evaluate(true, Out, null, Prefix);
        Assert.Equal(BridgeState.SignInNeeded, v.State);
        Assert.Equal("Sign in to Vault", v.Title);
        Assert.Contains("User: none", v.Details);
        Assert.Contains("Vault: none", v.Details);
        Assert.Contains("Sign in on the Vault tab (Log In).", v.Details);
    }

    [Fact]
    public void Null_status_while_listening_is_SignInNeeded()
        => Assert.Equal(BridgeState.SignInNeeded, StatusModel.Evaluate(true, null, null, Prefix).State);

    [Fact]
    public void Not_listening_wins_over_signed_in()
    {
        var v = StatusModel.Evaluate(false, In, null, Prefix);
        Assert.Equal(BridgeState.Stopped, v.State);
        Assert.Equal("Stopped", v.Title);
        Assert.Contains("See bridge.log for the reason.", v.Details);
    }

    [Fact]
    public void Last_request_is_formatted_in_local_time()
    {
        var utc = new DateTime(2026, 10, 12, 15, 4, 5, DateTimeKind.Utc);
        var v = StatusModel.Evaluate(true, In, utc, Prefix);
        Assert.Contains("Last request: " + utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), v.Details);
    }

    [Fact]
    public void No_em_dashes()
    {
        foreach (var l in new[] { true, false })
        {
            var v = StatusModel.Evaluate(l, In, DateTime.UtcNow, Prefix);
            Assert.DoesNotContain('—', v.Details);
        }
    }
}
