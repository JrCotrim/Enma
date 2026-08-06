using System.Net;
using System.Security.Cryptography;
using System.Text;
using Enma.Application.Security;
using Enma.Infrastructure.Security;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class PwnedPasswordsCompromisedPasswordCheckerTests
{
    private const string SafeUnavailableMessage =
        "Password compromise screening is temporarily unavailable.";

    [Fact]
    public async Task IsCompromisedAsync_WithPositiveMatchingSuffix_ReturnsTrue()
    {
        const string syntheticPassword = "Synthetic-Compromised-Test-Only!";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        string nonMatchingSuffix = CreateNonMatchingSuffix(hashParts.Suffix);
        string responseBody = string.Join(
            '\n',
            $"{nonMatchingSuffix}:12",
            $"{hashParts.Suffix}:7",
            $"{nonMatchingSuffix}:0");
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, responseBody)));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        bool isCompromised = await checker.IsCompromisedAsync(syntheticPassword);

        Assert.True(isCompromised);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithoutMatchingSuffix_ReturnsFalse()
    {
        const string syntheticPassword = "Synthetic-Unlisted-Test-Only!";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        string nonMatchingSuffix = CreateNonMatchingSuffix(hashParts.Suffix);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                $"{nonMatchingSuffix}:12")));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        bool isCompromised = await checker.IsCompromisedAsync(syntheticPassword);

        Assert.False(isCompromised);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithMatchingZeroCountPadding_ReturnsFalse()
    {
        const string syntheticPassword = "Synthetic-Padding-Test-Only!";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                $"{hashParts.Suffix}:0")));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        bool isCompromised = await checker.IsCompromisedAsync(syntheticPassword);

        Assert.False(isCompromised);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithLowercaseMatchingSuffix_ReturnsTrue()
    {
        const string syntheticPassword = "Synthetic-Lowercase-Test-Only!";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                $"{hashParts.Suffix.ToLowerInvariant()}:3")));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        bool isCompromised = await checker.IsCompromisedAsync(syntheticPassword);

        Assert.True(isCompromised);
    }

    [Fact]
    public async Task IsCompromisedAsync_SendsOnlyKAnonymityPrefixAndPrivacyHeaders()
    {
        const string syntheticPassword = "Synthetic-Privacy-Test-Only!";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        string completeHash = hashParts.Prefix + hashParts.Suffix;
        string nonMatchingSuffix = CreateNonMatchingSuffix(hashParts.Suffix);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                $"{nonMatchingSuffix}:1")));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        await checker.IsCompromisedAsync(syntheticPassword);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal($"/range/{hashParts.Prefix}", handler.AbsolutePath);
        Assert.Matches("^/range/[0-9A-F]{5}$", handler.AbsolutePath);
        Assert.Equal(string.Empty, handler.Query);
        Assert.False(handler.HasContent);
        Assert.Equal(new[] { "true" }, handler.AddPaddingValues);
        Assert.Equal("ENMA/1.0", handler.UserAgent);
        Assert.Contains("text/plain", handler.AcceptMediaTypes);
        Assert.False(handler.RequestUriText.Contains(
            syntheticPassword,
            StringComparison.Ordinal));
        Assert.False(handler.RequestUriText.Contains(
            completeHash,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(handler.RequestUriText.Contains(
            hashParts.Suffix,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IsCompromisedAsync_WithNonSuccessStatus_ThrowsSafeUnavailableException()
    {
        const string syntheticPassword = "Synthetic-Unavailable-Test-Only!";
        const string providerResponseBody = "synthetic-provider-detail";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.ServiceUnavailable,
                providerResponseBody)));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        CompromisedPasswordCheckUnavailableException exception =
            await Assert.ThrowsAsync<CompromisedPasswordCheckUnavailableException>(
                () => checker.IsCompromisedAsync(syntheticPassword));

        Assert.Equal(SafeUnavailableMessage, exception.Message);
        Assert.False(exception.Message.Contains("503", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains(
            "api.pwnedpasswords.com",
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exception.Message.Contains(
            syntheticPassword,
            StringComparison.Ordinal));
        Assert.False(exception.Message.Contains(
            providerResponseBody,
            StringComparison.Ordinal));
        Assert.False(exception.Message.Contains(
            hashParts.Prefix,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exception.Message.Contains(
            hashParts.Suffix,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IsCompromisedAsync_WithMalformedResponse_ThrowsSafeUnavailableException()
    {
        const string syntheticPassword = "Synthetic-Malformed-Test-Only!";
        const string malformedLine = "malformed-synthetic-provider-line";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                malformedLine)));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        CompromisedPasswordCheckUnavailableException exception =
            await Assert.ThrowsAsync<CompromisedPasswordCheckUnavailableException>(
                () => checker.IsCompromisedAsync(syntheticPassword));

        Assert.Equal(SafeUnavailableMessage, exception.Message);
        Assert.False(exception.Message.Contains(
            malformedLine,
            StringComparison.Ordinal));
        Assert.False(exception.Message.Contains(
            syntheticPassword,
            StringComparison.Ordinal));
        Assert.False(exception.Message.Contains(
            hashParts.Prefix,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exception.Message.Contains(
            hashParts.Suffix,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exception.Message.Contains(
            "api.pwnedpasswords.com",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IsCompromisedAsync_WhenCallerCancels_PropagatesCancellation()
    {
        const string syntheticPassword = "Synthetic-Cancellation-Test-Only!";
        using var handler = new RecordingHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                return CreateResponse(HttpStatusCode.OK, string.Empty);
            });
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);
        using var cancellationTokenSource = new CancellationTokenSource();

        Task<bool> checkTask = checker.IsCompromisedAsync(
            syntheticPassword,
            cancellationTokenSource.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkTask);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithNullPassword_ThrowsBeforeSendingRequest()
    {
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, string.Empty)));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        ArgumentNullException exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => checker.IsCompromisedAsync(null!));

        Assert.Equal("password", exception.ParamName);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithTransportException_DoesNotExposeLookupMaterialInExceptionChain()
    {
        const string syntheticPassword = "Synthetic-Transport-Failure-Test-Only!";
        const string syntheticProviderDetail = "synthetic-provider-transport-detail";
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        string completeHash = hashParts.Prefix + hashParts.Suffix;
        using var handler = new RecordingHttpMessageHandler(
            (request, _) =>
            {
                string requestUri = request.RequestUri?.ToString() ?? string.Empty;
                var transportException = new HttpRequestException(
                    $"{requestUri}|{hashParts.Prefix}|{syntheticProviderDetail}");

                return Task.FromException<HttpResponseMessage>(transportException);
            });
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        CompromisedPasswordCheckUnavailableException exception =
            await Assert.ThrowsAsync<CompromisedPasswordCheckUnavailableException>(
                () => checker.IsCompromisedAsync(syntheticPassword));
        string exceptionText = exception.ToString();

        Assert.Equal(SafeUnavailableMessage, exception.Message);
        Assert.False(exceptionText.Contains(
            syntheticPassword,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exceptionText.Contains(
            completeHash,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exceptionText.Contains(
            hashParts.Prefix,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exceptionText.Contains(
            hashParts.Suffix,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exceptionText.Contains(
            handler.RequestUriText,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(exceptionText.Contains(
            syntheticProviderDetail,
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithInvalidUtf16Password_ThrowsSafelyBeforeRequest()
    {
        string invalidUtf16Password = "Synthetic-\uD800-Invalid-Test-Only";
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, string.Empty)));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        CompromisedPasswordCheckUnavailableException exception =
            await Assert.ThrowsAsync<CompromisedPasswordCheckUnavailableException>(
                () => checker.IsCompromisedAsync(invalidUtf16Password));

        Assert.Equal(SafeUnavailableMessage, exception.Message);
        Assert.False(exception.ToString().Contains(
            invalidUtf16Password,
            StringComparison.Ordinal));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task IsCompromisedAsync_WithUnrelatedInvalidOperationException_PropagatesOriginalException()
    {
        const string syntheticPassword = "Synthetic-Programming-Failure-Test-Only!";
        var expectedException = new InvalidOperationException(
            "Synthetic unrelated invalid operation.");
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(expectedException));
        using HttpClient httpClient = CreateHttpClient(handler);
        var checker = new PwnedPasswordsCompromisedPasswordChecker(httpClient);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => checker.IsCompromisedAsync(syntheticPassword));

        Assert.Same(expectedException, exception);
        Assert.Equal(1, handler.RequestCount);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.pwnedpasswords.com/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ENMA/1.0");
        httpClient.DefaultRequestHeaders.Add("Add-Padding", "true");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/plain");

        return httpClient;
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        string responseBody)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/plain")
        };
    }

    private static HashParts CalculateHashParts(string password)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[]? hashBytes = null;

        try
        {
            hashBytes = SHA1.HashData(passwordBytes);
            string completeHash = Convert.ToHexString(hashBytes);

            return new HashParts(completeHash[..5], completeHash[5..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);

            if (hashBytes is not null)
            {
                CryptographicOperations.ZeroMemory(hashBytes);
            }
        }
    }

    private static string CreateNonMatchingSuffix(string suffix)
    {
        char replacement = suffix[0] == '0' ? '1' : '0';

        return replacement + suffix[1..];
    }

    private sealed record HashParts(string Prefix, string Suffix);

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            responseFactory)
        : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string AbsolutePath { get; private set; } = string.Empty;

        public string Query { get; private set; } = string.Empty;

        public string RequestUriText { get; private set; } = string.Empty;

        public bool HasContent { get; private set; }

        public string[] AddPaddingValues { get; private set; } = [];

        public string UserAgent { get; private set; } = string.Empty;

        public string[] AcceptMediaTypes { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            AbsolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
            Query = request.RequestUri?.Query ?? string.Empty;
            RequestUriText = request.RequestUri?.ToString() ?? string.Empty;
            HasContent = request.Content is not null;
            AddPaddingValues = request.Headers.TryGetValues(
                "Add-Padding",
                out IEnumerable<string>? values)
                ? values.ToArray()
                : [];
            UserAgent = request.Headers.UserAgent.ToString();
            AcceptMediaTypes = request.Headers.Accept
                .Select(value => value.MediaType)
                .OfType<string>()
                .ToArray();
            RequestStarted.TrySetResult(true);

            return responseFactory(request, cancellationToken);
        }
    }
}
