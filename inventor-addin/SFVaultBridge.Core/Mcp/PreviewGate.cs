using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Remembers requests that were previewed, so an apply is only accepted for the same request within the time limit. Each preview unlocks one apply.</summary>
public sealed class PreviewGate
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PreviewGate(TimeSpan? ttl = null, Func<DateTime>? clock = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Ttl => _ttl;

    /// <summary>SHA-256 of the request with keys sorted at every level and the ignored top-level keys removed, prefixed by the family so one tool's preview never unlocks another's apply.</summary>
    public static string Hash(string family, JsonObject request, params string[] ignoreKeys)
    {
        var clone = (JsonObject)request.DeepClone();
        foreach (var k in ignoreKeys) clone.Remove(k);
        var canonical = family + "\n" + Canonical(clone);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonValue.Create(p.Key)!.ToJsonString() + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    public void Record(string hash)
    {
        lock (_gate)
        {
            Purge();
            _seen[hash] = _clock();
        }
    }

    /// <summary>True once per recorded hash while it is still fresh.</summary>
    public bool Consume(string hash)
    {
        lock (_gate)
        {
            Purge();
            return _seen.Remove(hash);
        }
    }

    public int Pending
    {
        get { lock (_gate) { Purge(); return _seen.Count; } }
    }

    private void Purge()
    {
        var now = _clock();
        foreach (var k in _seen.Where(p => now - p.Value > _ttl).Select(p => p.Key).ToList()) _seen.Remove(k);
    }
}

/// <summary>Short-lived tokens for payloads that must be applied exactly as previewed. A token is used once and expires.</summary>
public sealed class PreviewStore<T> where T : class
{
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, (DateTime At, T Payload)> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PreviewStore(TimeSpan? ttl = null, Func<DateTime>? clock = null)
    {
        _ttl = ttl ?? PreviewGate.DefaultTtl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Ttl => _ttl;

    public string Put(T payload)
    {
        lock (_gate)
        {
            Purge();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_');
            _items[token] = (_clock(), payload);
            return token;
        }
    }

    /// <summary>Stores the payload under a caller-chosen key (for example a hash of what was previewed).</summary>
    public void PutAs(string key, T payload)
    {
        lock (_gate)
        {
            Purge();
            _items[key] = (_clock(), payload);
        }
    }

    public T? Take(string? token)
    {
        lock (_gate)
        {
            Purge();
            if (token is null || !_items.Remove(token, out var item)) return null;
            return item.Payload;
        }
    }

    private void Purge()
    {
        var now = _clock();
        foreach (var k in _items.Where(p => now - p.Value.At > _ttl).Select(p => p.Key).ToList()) _items.Remove(k);
    }
}
