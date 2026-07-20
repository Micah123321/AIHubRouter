using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public interface IAIHubApiClient : IDisposable
{
    Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default);
    Task<AuthSession> LoginAsync(LoginCredentials credentials, CancellationToken cancellationToken = default);
    Task<AuthSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default);
    Task<ApiKeyInfo> UpdateKeyGroupAsync(long keyId, long groupId, CancellationToken cancellationToken = default);
}

public sealed class AIHubClient : IAIHubApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _origin;
    private readonly string _bearerToken;
    private readonly string _cookie;
    private readonly string _userAgent;
    private readonly Func<DateTimeOffset> _utcNow;

    public AIHubClient(
        string baseUrl,
        string? bearerToken = null,
        string? cookie = null,
        string? userAgent = null,
        TimeSpan? timeout = null,
        HttpMessageHandler? messageHandler = null,
        Func<DateTimeOffset>? utcNow = null,
        bool allowInsecureLoopback = false)
    {
        _origin = NormalizeOrigin(baseUrl, allowInsecureLoopback);
        _bearerToken = CredentialParser.NormalizeBearerToken(bearerToken);
        _cookie = CredentialParser.NormalizeCookie(cookie);
        _userAgent = CredentialParser.NormalizeUserAgent(userAgent);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(_bearerToken))
        {
            _bearerToken = CredentialParser.TryExtractTokenFromCookie(_cookie);
        }

        HttpMessageHandler handler;
        if (messageHandler is not null)
        {
            handler = messageHandler;
        }
        else
        {
            var cookieContainer = new CookieContainer();
            if (!string.IsNullOrEmpty(_cookie))
            {
                cookieContainer.SetCookies(_origin, _cookie);
            }

            handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = cookieContainer
            };
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
    }

    public async Task<MonitorSummary> GetProviderSummaryAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<MonitorSummary>(HttpMethod.Get, "/api/v1/public/monitor/summary", null, cancellationToken);
    }

    public async Task<JsonElement> ValidateLoginAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/auth/me", null, cancellationToken);
    }

    public async Task<AuthSession> LoginAsync(
        LoginCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
        {
            throw new ArgumentException("登录邮箱和密码不能为空。", nameof(credentials));
        }

        var response = await SendAsync<AuthTokenResponse>(
            HttpMethod.Post,
            "/api/v1/auth/login",
            new { email = credentials.Email.Trim(), password = credentials.Password },
            cancellationToken);
        return CreateSession(response);
    }

    public async Task<AuthSession> RefreshSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("Refresh token 不能为空。", nameof(refreshToken));
        }

        var response = await SendAsync<AuthTokenResponse>(
            HttpMethod.Post,
            "/api/v1/auth/refresh",
            new { refresh_token = refreshToken },
            cancellationToken);
        var session = CreateSession(response);
        return string.IsNullOrWhiteSpace(session.RefreshToken)
            ? session with { RefreshToken = refreshToken }
            : session;
    }

    public async Task<IReadOnlyList<GroupInfo>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<List<GroupInfo>>(HttpMethod.Get, "/api/v1/groups/available", null, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, double>> GetUserGroupRatesAsync(CancellationToken cancellationToken = default)
    {
        var rates = await SendAsync<Dictionary<long, double>?>(
            HttpMethod.Get,
            "/api/v1/groups/rates",
            null,
            cancellationToken);
        return rates ?? new Dictionary<long, double>();
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> GetAllKeysAsync(CancellationToken cancellationToken = default)
    {
        const int pageSize = 50;
        var page = 1;
        var result = new List<ApiKeyInfo>();

        while (true)
        {
            var response = await SendAsync<PaginatedResponse<ApiKeyInfo>>(
                HttpMethod.Get,
                $"/api/v1/keys?page={page}&page_size={pageSize}&sort_by=created_at&sort_order=desc",
                null,
                cancellationToken);

            result.AddRange(response.Items);
            if (page >= Math.Max(response.Pages, 1) || response.Items.Count == 0)
            {
                return result;
            }

            page++;
        }
    }

    public async Task<ApiKeyInfo> UpdateKeyGroupAsync(
        long keyId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<ApiKeyInfo>(
            HttpMethod.Put,
            $"/api/v1/keys/{keyId}",
            new { group_id = groupId },
            cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        var isAuthenticationEndpoint = path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(method, new Uri(_origin, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");
        request.Headers.Referrer = _origin;
        request.Headers.TryAddWithoutValidation("Origin", _origin.GetLeftPart(UriPartial.Authority));

        if (!string.IsNullOrEmpty(_bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrEmpty(_userAgent) ? "AIHubRouter/1.0 (Windows)" : _userAgent);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument? document = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                document = JsonDocument.Parse(body);
            }
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            // Non-JSON gateway errors are handled below without reflecting response HTML.
        }

        using (document)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException(response.StatusCode, document?.RootElement, isAuthenticationEndpoint);
            }

            if (document is null)
            {
                throw new AIHubApiException("服务器返回了空响应。", response.StatusCode);
            }

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var codeElement))
            {
                var code = ReadCode(codeElement);
                if (code != "0")
                {
                    throw CreateApiException(response.StatusCode, root, isAuthenticationEndpoint);
                }

                if (!root.TryGetProperty("data", out root))
                {
                    throw new AIHubApiException("服务器响应缺少 data 字段。", response.StatusCode, code);
                }
            }

            if (root.ValueKind == JsonValueKind.Null)
            {
                return default!;
            }

            try
            {
                return root.Deserialize<T>(JsonOptions)
                    ?? throw new AIHubApiException("无法读取服务器响应。", response.StatusCode);
            }
            catch (JsonException exception)
            {
                throw new AIHubApiException($"服务器响应格式不兼容：{exception.Message}", response.StatusCode);
            }
        }
    }

    private static AIHubApiException CreateApiException(
        HttpStatusCode statusCode,
        JsonElement? root,
        bool isAuthenticationEndpoint)
    {
        var message = isAuthenticationEndpoint
            ? "认证请求被服务器拒绝。"
            : statusCode switch
            {
                HttpStatusCode.Unauthorized => "认证失败，请检查登录 Token、Cookie 和浏览器 User-Agent。",
                HttpStatusCode.Forbidden => "当前账号没有执行该操作的权限。",
                HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
                _ => $"AIHub 请求失败（HTTP {(int)statusCode}）。"
            };
        string? apiCode = null;

        if (root is { ValueKind: JsonValueKind.Object } value)
        {
            if (value.TryGetProperty("code", out var codeElement))
            {
                apiCode = ReadCode(codeElement);
            }
        }

        return new AIHubApiException(message, statusCode, apiCode);
    }

    private static string ReadCode(JsonElement codeElement)
    {
        return codeElement.ValueKind switch
        {
            JsonValueKind.Number => codeElement.GetRawText(),
            JsonValueKind.String => codeElement.GetString() ?? string.Empty,
            _ => codeElement.GetRawText()
        };
    }

    internal static Uri NormalizeOrigin(string baseUrl, bool allowInsecureLoopback = false)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("站点地址必须是有效的 HTTP 或 HTTPS 地址。", nameof(baseUrl));
        }

        if (uri.Scheme != Uri.UriSchemeHttps &&
            !(allowInsecureLoopback && IsLoopback(uri)))
        {
            throw new ArgumentException(
                "站点地址必须使用 HTTPS；只有显式启用开发选项时才允许 loopback HTTP。",
                nameof(baseUrl));
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private AuthSession CreateSession(AuthTokenResponse response)
    {
        if (response.RequiresTwoFactor)
        {
            throw new InteractiveAuthenticationRequiredException();
        }

        if (string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new AIHubApiException("认证响应缺少 access token。");
        }

        return new AuthSession(
            response.AccessToken,
            response.RefreshToken ?? string.Empty,
            _utcNow().AddSeconds(Math.Max(response.ExpiresIn, 0)));
    }

    private sealed class AuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("requires_2fa")]
        public bool RequiresTwoFactor { get; init; }
    }
}
