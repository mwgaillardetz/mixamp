using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mixamp.Core;

namespace Mixamp;

sealed class Account(Store store)
{
    const string Global = "installation";
    public string Identifier { get; } = StableId(store);

    static string StableId(Store store)
    {
        var id = store.Get(Global, "client");
        if (id != null)
            return id;
        id = Guid.NewGuid().ToString();
        store.Set(Global, "client", id);
        return id;
    }

    public async Task<(string AccountId, JsonNode[] Servers)> Discover(CancellationToken ct)
    {
        var saved = store.Get(Global, "login");
        string? token = null;
        if (!string.IsNullOrEmpty(saved))
        {
            try
            {
                token = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        Convert.FromBase64String(saved),
                        null,
                        DataProtectionScope.CurrentUser
                    )
                );
            }
            catch (CryptographicException)
            {
                store.Set(Global, "login", "");
            }
        }
        using var cloud = new Plex(new Uri("https://plex.tv"), Identifier);
        if (token == null)
        {
            var pin = await cloud.Call("/api/v2/pins?strong=true", HttpMethod.Post, ct);
            var code = Plex.E(pin.Str("code"));
            var id = Plex.E(pin.Str("id"));
            Process.Start(
                new ProcessStartInfo(
                    $"https://app.plex.tv/auth#?clientID={Identifier}&code={code}&context%5Bdevice%5D%5Bproduct%5D=Mixamp"
                )
                {
                    UseShellExecute = true,
                }
            );
            var expiry = DateTimeOffset.TryParse(pin.Str("expiresAt"), out var at)
                ? at
                : DateTimeOffset.UtcNow.AddMinutes(5);
            while (string.IsNullOrEmpty(token) && DateTimeOffset.UtcNow < expiry)
            {
                await Task.Delay(1500, ct);
                token = (await cloud.Call($"/api/v2/pins/{id}?code={code}", ct: ct)).Str(
                    "authToken"
                );
            }
            if (string.IsNullOrEmpty(token))
                throw new TimeoutException("Plex sign-in expired.");
        }
        using var auth = new Plex(new Uri("https://plex.tv"), Identifier, token);
        try
        {
            var user = await auth.Call("/api/v2/user", ct: ct);
            var servers = (JsonArray)
                await auth.Call("/api/v2/resources?includeHttps=1&includeRelay=1", ct: ct);
            store.Set(
                Global,
                "login",
                Convert.ToBase64String(
                    ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(token),
                        null,
                        DataProtectionScope.CurrentUser
                    )
                )
            );
            return (
                user.Str("id"),
                servers
                    .OfType<JsonNode>()
                    .Where(s => s.Str("provides").Split(',').Contains("server"))
                    .ToArray()
            );
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            store.Set(Global, "login", "");
            throw new IOException("Plex revoked this sign-in. Connect again.");
        }
    }

    public async Task<Plex> Connect(JsonNode server, CancellationToken ct)
    {
        foreach (
            var c in server
                .Items("connections")
                .Where(c => c.Str("protocol") == "https")
                .OrderBy(c => c["relay"]?.GetValue<bool>() == true)
                .ThenByDescending(c => c["local"]?.GetValue<bool>() == true)
        )
        {
            var api = new Plex(new Uri(c.Str("uri")), Identifier, server.Str("accessToken"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                if (
                    (await api.Call("/identity", ct: timeout.Token)).Str("machineIdentifier")
                    == server.Str("clientIdentifier")
                )
                    return api;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
            api.Dispose();
        }
        throw new IOException("No reachable secure connection matched the selected server.");
    }
}
