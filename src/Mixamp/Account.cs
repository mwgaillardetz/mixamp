using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mixamp.Core;

namespace Mixamp;

sealed class PlexSignInRequired(string message) : IOException(message);

sealed class Account(Store store, Func<string?>? readLogin = null, Action<string?>? writeLogin = null)
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

    public async Task<(string AccountId, JsonNode[] Servers)> Discover(CancellationToken ct, bool interactive = true,bool freshLogin = false)
    {
        var saved = store.Get(Global, "login");
        string? token = freshLogin ? null : readLogin?.Invoke();
        if (!freshLogin && readLogin == null && OperatingSystem.IsWindows() && !string.IsNullOrEmpty(saved))
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
        bool signingIn = token == null;
        using var cloud = new Plex(new Uri("https://plex.tv"), Identifier);
        if (token == null)
        {
            if (!interactive) throw new PlexSignInRequired("Connect to Plex to resume syncing.");
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
            var userRequest = auth.Call("/api/v2/user", ct: ct);
            var resourcesRequest = auth.Call("/api/v2/resources?includeHttps=1&includeRelay=1", ct: ct);
            await Task.WhenAll(userRequest, resourcesRequest);
            var user = await userRequest;
            var servers = (JsonArray)await resourcesRequest;
            if (writeLogin != null)
            {
                if (signingIn) writeLogin(token);
            }
            else if (OperatingSystem.IsWindows()) store.Set(
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
            if (writeLogin == null) store.Set(Global, "login", ""); else writeLogin(null);
            throw new PlexSignInRequired("Plex revoked this sign-in. Connect again.");
        }
    }

    public async Task<Plex> Connect(JsonNode server, CancellationToken ct)
    {
        string scope = "server/" + server.Str("clientIdentifier");
        string? previous = store.Get(scope,"lastEndpoint");
        foreach (
            var c in server
                .Items("connections")
                .Where(c => c.Str("protocol") == "https")
                .OrderBy(c => c["relay"]?.GetValue<bool>() == true)
                .ThenByDescending(c => string.Equals(c.Str("uri").TrimEnd('/'),previous,StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c["local"]?.GetValue<bool>() == true)
        )
        {
            if (!Uri.TryCreate(c.Str("uri"), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0) continue;
            var api = new Plex(endpoint, Identifier, server.Str("accessToken"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                if (
                    (await api.Call("/identity", ct: timeout.Token)).Str("machineIdentifier")
                    == server.Str("clientIdentifier")
                )
                {
                    string origin = endpoint.GetLeftPart(UriPartial.Authority);
                    if (previous != origin) store.Set(scope,"lastEndpoint",origin);
                    return api;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                System.Text.Json.JsonException or System.Xml.XmlException or InvalidDataException)
            {
            }
            api.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        throw new IOException("No reachable secure connection matched the selected server.");
    }
}
