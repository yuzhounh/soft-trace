using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftTrace.App;

public sealed record FirebaseAuthSession(
    string IdToken,
    string RefreshToken,
    string UserId,
    string? Email,
    string? DisplayName,
    string? PhotoUrl,
    DateTimeOffset ExpiresUtc);

public sealed class FirebaseAuthClient(HttpClient httpClient)
{
    public async Task<FirebaseAuthSession> SignInWithGoogleAsync(
        string apiKey,
        string googleClientId,
        string googleClientSecret,
        CancellationToken cancellationToken = default)
    {
        var googleIdToken = await GetGoogleIdTokenAsync(
            googleClientId,
            googleClientSecret,
            cancellationToken);
        var requestBody = JsonSerializer.Serialize(new
        {
            postBody = $"id_token={Uri.EscapeDataString(googleIdToken)}&providerId=google.com",
            requestUri = "https://soft-trace.firebaseapp.com",
            returnIdpCredential = true,
            returnSecureToken = true
        });
        using var response = await httpClient.PostAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={Uri.EscapeDataString(apiKey)}",
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetFirebaseError(responseBody, "Google 登录 Firebase 失败"));
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        return new FirebaseAuthSession(
            root.GetProperty("idToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("localId").GetString()!,
            GetOptionalString(root, "email"),
            GetOptionalString(root, "displayName"),
            GetOptionalString(root, "photoUrl"),
            GetExpiry(GetOptionalString(root, "expiresIn")));
    }

    public async Task<FirebaseAuthSession> RefreshAsync(
        string apiKey,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var response = await httpClient.PostAsync(
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(apiKey)}",
            content,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetFirebaseError(responseBody, "Firebase 登录已失效"));
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var session = new FirebaseAuthSession(
            root.GetProperty("id_token").GetString()!,
            root.GetProperty("refresh_token").GetString()!,
            root.GetProperty("user_id").GetString()!,
            null,
            null,
            null,
            GetExpiry(GetOptionalString(root, "expires_in")));
        return await PopulateProfileAsync(apiKey, session, cancellationToken);
    }

    private async Task<FirebaseAuthSession> PopulateProfileAsync(
        string apiKey,
        FirebaseAuthSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new { idToken = session.IdToken });
            using var response = await httpClient.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={Uri.EscapeDataString(apiKey)}",
                new StringContent(requestBody, Encoding.UTF8, "application/json"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return session;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("users", out var users) || users.GetArrayLength() == 0)
            {
                return session;
            }

            var user = users[0];
            return session with
            {
                Email = GetOptionalString(user, "email"),
                DisplayName = GetOptionalString(user, "displayName"),
                PhotoUrl = GetOptionalString(user, "photoUrl")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return session;
        }
    }

    private async Task<string> GetGoogleIdTokenAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";
            var state = CreateRandomUrlToken(32);
            var verifier = CreateRandomUrlToken(64);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorizationUri = BuildAuthorizationUri(clientId, redirectUri, state, challenge);

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri,
                UseShellExecute = true
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var code = await ReceiveAuthorizationCodeAsync(listener, state, timeout.Token);

            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            });
            using var tokenResponse = await httpClient.PostAsync(
                "https://oauth2.googleapis.com/token",
                tokenContent,
                cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetOAuthError(tokenBody, "Google 登录令牌交换失败"));
            }

            using var tokenDocument = JsonDocument.Parse(tokenBody);
            return tokenDocument.RootElement.TryGetProperty("id_token", out var idToken)
                ? idToken.GetString() ?? throw new InvalidOperationException("Google 未返回身份令牌。")
                : throw new InvalidOperationException("Google 未返回身份令牌。");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string BuildAuthorizationUri(
        string clientId,
        string redirectUri,
        string state,
        string challenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account"
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static async Task<string> ReceiveAuthorizationCodeAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        string? header;
        do
        {
            header = await reader.ReadLineAsync(cancellationToken);
        }
        while (!string.IsNullOrEmpty(header));

        var target = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        var query = !string.IsNullOrWhiteSpace(target)
            ? ParseQuery(new Uri($"http://127.0.0.1{target}").Query)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expectedStateBytes = Encoding.UTF8.GetBytes(expectedState);
        var validState = query.TryGetValue("state", out var returnedState) &&
                         returnedState.Length == expectedState.Length &&
                         CryptographicOperations.FixedTimeEquals(
                             expectedStateBytes,
                             Encoding.UTF8.GetBytes(returnedState));
        var success = validState && query.ContainsKey("code") && !query.ContainsKey("error");
        var html = success
            ? "<!doctype html><meta charset='utf-8'><title>SoftTrace</title><body style='font-family:Segoe UI;padding:48px'><h2>登录成功</h2><p>可以关闭此页面并返回 SoftTrace。</p></body>"
            : "<!doctype html><meta charset='utf-8'><title>SoftTrace</title><body style='font-family:Segoe UI;padding:48px'><h2>登录未完成</h2><p>请返回 SoftTrace 后重试。</p></body>";
        var responseBytes = Encoding.UTF8.GetBytes(html);
        var responseHeader = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);

        if (!validState)
        {
            throw new InvalidOperationException("Google 登录响应校验失败，请重试。");
        }
        if (query.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException(error == "access_denied" ? "已取消 Google 登录。" : error);
        }
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Google 登录未返回授权码。");
        }
        return code;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            values[Uri.UnescapeDataString(pieces[0].Replace('+', ' '))] = pieces.Length == 2
                ? Uri.UnescapeDataString(pieces[1].Replace('+', ' '))
                : string.Empty;
        }
        return values;
    }

    private static string CreateRandomUrlToken(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset GetExpiry(string? secondsText)
    {
        var seconds = int.TryParse(secondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 3600;
        return DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    private static string GetOAuthError(string responseBody, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var error = GetOptionalString(root, "error");
            var description = GetOptionalString(root, "error_description");
            return description ?? error ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetFirebaseError(string responseBody, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
