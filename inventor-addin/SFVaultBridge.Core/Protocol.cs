using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>An expected failure with a stable code the Python client acts on.</summary>
public sealed class BridgeException : Exception
{
    public string Code { get; }

    public BridgeException(string code, string message) : base(message) => Code = code;
}

public delegate JsonNode? Operation(JsonObject body);

/// <summary>Token check, routing and error mapping for POST /op/&lt;name&gt;.</summary>
public sealed class Router
{
    private readonly Dictionary<string, Operation> _ops = new(StringComparer.Ordinal);
    private readonly byte[] _token;

    public Router(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
        _token = Encoding.UTF8.GetBytes(token);
    }

    public void Register(string name, Operation op) => _ops[name] = op;

    public (int Status, string Json) Handle(string method, string path, string? token, string body)
    {
        if (!TokenMatches(token)) return Error(401, "unauthorized", "Missing or wrong X-Bridge-Token.");
        if (method != "POST" || !path.StartsWith("/op/", StringComparison.Ordinal))
            return Error(404, "not_found", "Use POST /op/<name>.");
        var name = path["/op/".Length..].TrimEnd('/');
        if (!_ops.TryGetValue(name, out var op)) return Error(404, "unknown_operation", $"No operation named '{name}'.");

        JsonObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(body)
                ? new JsonObject()
                : JsonNode.Parse(body) as JsonObject ?? throw new BridgeException("bad_request", "Body must be a JSON object.");
        }
        catch (JsonException ex) { return Error(400, "bad_request", "Body is not valid JSON: " + ex.Message); }
        catch (BridgeException ex) { return Error(400, ex.Code, ex.Message); }

        try
        {
            var result = op(args) ?? new JsonObject();
            return (200, result.ToJsonString());
        }
        catch (BridgeException ex) { return Error(400, ex.Code, ex.Message); }
        catch (Exception ex) { return Error(500, "internal", ex.GetType().Name + ": " + ex.Message); }
    }

    private bool TokenMatches(string? token) =>
        !string.IsNullOrEmpty(token) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token);

    private static (int, string) Error(int status, string code, string message) =>
        (status, new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString());
}
