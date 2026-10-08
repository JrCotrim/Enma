using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Enma.E2ETests.Infrastructure;

public sealed class SeededSession : IDisposable
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";

    private readonly CookieContainer cookies = new();
    private readonly HttpClient httpClient;

    public SeededSession(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        BaseAddress = baseAddress;
        httpClient = new HttpClient(new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false
        })
        {
            BaseAddress = baseAddress
        };
    }

    public Uri BaseAddress { get; }

    public Cookie SessionCookie =>
        cookies.GetCookies(BaseAddress)[SessionCookieName]
        ?? throw new InvalidOperationException(
            "The seeded session has not signed in.");

    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        HttpStatusCode expectedStatus,
        bool withCsrfToken = false)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };

        if (withCsrfToken)
        {
            request.Headers.Add(CsrfHeaderName, await GetCsrfTokenAsync());
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != expectedStatus)
        {
            throw new InvalidOperationException(
                $"{method} {path} returned {(int)response.StatusCode}, " +
                $"expected {(int)expectedStatus}.");
        }

        return content;
    }

    public async Task<T> GetFromJsonAsync<T>(string path)
    {
        return await httpClient.GetFromJsonAsync<T>(path)
            ?? throw new InvalidOperationException(
                $"GET {path} returned an empty body.");
    }

    public async Task<ObservedResponse> ObserveAsync(string path)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();

        return ObservedResponse.From(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    // Antiforgery tokens are bound to the signed-in identity, so a fresh one
    // is requested for each protected write.
    private async Task<string> GetCsrfTokenAsync()
    {
        JsonObject response =
            await httpClient.GetFromJsonAsync<JsonObject>("api/auth/csrf")
            ?? throw new InvalidOperationException("The CSRF response was empty.");

        return response["requestToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The CSRF response was invalid.");
    }
}

// What a client can observe from a response. The per-request traceId, when a
// problem body carries one, is the only field excluded from the comparison.
public sealed record ObservedResponse(
    HttpStatusCode Status,
    string? MediaType,
    string? ProblemCode,
    string ComparableBody)
{
    public static ObservedResponse From(
        HttpStatusCode status,
        string? mediaType,
        string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new ObservedResponse(status, mediaType, null, string.Empty);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return new ObservedResponse(status, mediaType, null, body);
        }

        if (node is not JsonObject problem)
        {
            return new ObservedResponse(status, mediaType, null, body);
        }

        problem.Remove("traceId");
        string? code = problem["code"]?.GetValue<string>();

        return new ObservedResponse(
            status,
            mediaType,
            code,
            problem.ToJsonString());
    }

    public override string ToString()
    {
        return $"{(int)Status} code={ProblemCode ?? "(none)"} " +
            $"type={MediaType ?? "(none)"} body={ComparableBody}";
    }
}
