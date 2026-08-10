using System.Net;
using System.Net.Http.Json;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enma.IntegrationTests.Api;

public sealed class ProductionIngressTests
{
    private const string ResendPath =
        "/api/auth/email-verification/resend";
    private const string ProductionValidationError =
        "Production ingress configuration is invalid.";
    private const string DatabaseConnectionString =
        "Host=localhost;Database=enma-tests";

    private static readonly IPAddress TrustedProxyAddress =
        IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress UntrustedPeerAddress =
        IPAddress.Parse("192.0.2.11");
    private static readonly IPAddress FirstClientAddress =
        IPAddress.Parse("198.51.100.10");
    private static readonly IPAddress SecondClientAddress =
        IPAddress.Parse("203.0.113.10");

    [Fact]
    public async Task ForwardedHeaders_TrustedImmediatePeer_RestoresClientIpAndHttpsScheme()
    {
        RequestObservation observation = new();
        using ConfiguredApiFactory factory = CreateIngressFactory(
            TrustedProxyAddress,
            TrustedProxyAddress,
            observation);
        using HttpClient client = CreateClient(factory, "http://localhost");

        using HttpResponseMessage response = await PostResendAsync(
            client,
            FirstClientAddress,
            "https");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(FirstClientAddress, observation.RemoteIpAddress);
        Assert.Equal("https", observation.Scheme);
    }

    [Fact]
    public async Task ForwardedHeaders_UntrustedImmediatePeer_IgnoresForgedValues()
    {
        RequestObservation observation = new();
        using ConfiguredApiFactory factory = CreateIngressFactory(
            UntrustedPeerAddress,
            TrustedProxyAddress,
            observation);
        using HttpClient client = CreateClient(factory, "https://localhost");

        using HttpResponseMessage response = await PostResendAsync(
            client,
            FirstClientAddress,
            "http");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(UntrustedPeerAddress, observation.RemoteIpAddress);
        Assert.Equal("https", observation.Scheme);
    }

    [Fact]
    public async Task ResendRateLimit_TrustedProxyWithDistinctClients_UsesIndependentPartitions()
    {
        RequestObservation observation = new();
        using ConfiguredApiFactory factory = CreateIngressFactory(
            TrustedProxyAddress,
            TrustedProxyAddress,
            observation);
        using HttpClient client = CreateClient(factory, "http://localhost");

        for (int requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            await AssertResendStatusAsync(
                client,
                FirstClientAddress,
                HttpStatusCode.Accepted);
        }

        await AssertResendStatusAsync(
            client,
            FirstClientAddress,
            HttpStatusCode.TooManyRequests);
        await AssertResendStatusAsync(
            client,
            SecondClientAddress,
            HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ResendRateLimit_UntrustedPeerRotatesForgedClientIp_CannotBypassLimit()
    {
        RequestObservation observation = new();
        using ConfiguredApiFactory factory = CreateIngressFactory(
            UntrustedPeerAddress,
            TrustedProxyAddress,
            observation);
        using HttpClient client = CreateClient(factory, "https://localhost");

        for (int requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            await AssertResendStatusAsync(
                client,
                IPAddress.Parse($"198.51.100.{requestNumber}"),
                HttpStatusCode.Accepted);
        }

        await AssertResendStatusAsync(
            client,
            IPAddress.Parse("198.51.100.6"),
            HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void Startup_ProductionWithProxyDisabled_FailsClosed()
    {
        AssertProductionStartupFails(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "app.example.test",
            ["Deployment:TrustedProxy:Enabled"] = "false",
            ["Deployment:TrustedProxy:KnownProxies:0"] =
                TrustedProxyAddress.ToString()
        });
    }

    [Fact]
    public void Startup_ProductionWithNoTrustedProxyOrNetwork_FailsClosed()
    {
        AssertProductionStartupFails(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "app.example.test",
            ["Deployment:TrustedProxy:Enabled"] = "true"
        });
    }

    [Fact]
    public void Startup_ProductionWithMalformedProxyIp_FailsClosed()
    {
        const string malformedProxy = "not-an-ip-address";
        AssertProductionStartupFails(
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownProxies:0"] = malformedProxy
            },
            malformedProxy);
    }

    [Fact]
    public void Startup_ProductionWithMalformedCidr_FailsClosed()
    {
        const string malformedNetwork = "192.0.2.0/not-a-prefix";
        AssertProductionStartupFails(
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownIPNetworks:0"] = malformedNetwork
            },
            malformedNetwork);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0:0/96")]
    [InlineData("::fffe:0:0/95")]
    [InlineData("::fffc:0:0/94")]
    public void Startup_ProductionWithTrustAllCidr_FailsClosed(
        string trustAllNetwork)
    {
        AssertProductionStartupFails(
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownIPNetworks:0"] = trustAllNetwork
            },
            trustAllNetwork);
    }

    [Fact]
    public void Startup_ProductionWithValidExactProxy_Succeeds()
    {
        AssertStartupSucceeds(
            "Production",
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownProxies:0"] =
                    TrustedProxyAddress.ToString()
            });
    }

    [Fact]
    public void Startup_ProductionWithValidCidr_Succeeds()
    {
        AssertStartupSucceeds(
            "Production",
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownIPNetworks:0"] =
                    "192.0.2.0/24"
            });
    }

    [Theory]
    [InlineData("::ffff:192.0.2.0/120")]
    [InlineData("::ffff:0:0/97")]
    [InlineData("2001:db8::/64")]
    public void Startup_ProductionWithLimitedIpv6Cidr_Succeeds(
        string trustedNetwork)
    {
        AssertStartupSucceeds(
            "Production",
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "app.example.test",
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownIPNetworks:0"] = trustedNetwork
            });
    }

    [Fact]
    public void Startup_DevelopmentWithoutProxyConfiguration_Succeeds()
    {
        AssertStartupSucceeds(
            "Development",
            new Dictionary<string, string?>(),
            clearConfiguration: true);
    }

    [Fact]
    public void Startup_ProductionWithoutAllowedHosts_FailsClosed()
    {
        AssertProductionStartupFails(
            ValidProductionProxySettings(),
            clearConfiguration: true);
    }

    [Fact]
    public void Startup_ProductionWithWildcardAllowedHosts_FailsClosed()
    {
        Dictionary<string, string?> settings = ValidProductionProxySettings();
        settings["AllowedHosts"] = "*";

        AssertProductionStartupFails(settings);
    }

    [Fact]
    public void Startup_ProductionWithGlobalForwardedHeadersShortcut_FailsClosed()
    {
        Dictionary<string, string?> settings = ValidProductionProxySettings();
        settings["AllowedHosts"] = "app.example.test";
        settings["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";

        AssertProductionStartupFails(settings);
    }

    private static ConfiguredApiFactory CreateIngressFactory(
        IPAddress immediatePeer,
        IPAddress trustedProxy,
        RequestObservation observation)
    {
        return new ConfiguredApiFactory(
            "Testing",
            new Dictionary<string, string?>
            {
                ["Deployment:TrustedProxy:Enabled"] = "true",
                ["Deployment:TrustedProxy:KnownProxies:0"] =
                    trustedProxy.ToString()
            },
            services =>
            {
                services.AddSingleton<IStartupFilter>(
                    new ImmediatePeerStartupFilter(immediatePeer));
                services.AddHttpContextAccessor();
                services.AddSingleton(observation);

                services.RemoveAll<IEmailVerificationUserLookup>();
                services.RemoveAll<IEmailVerificationTokenService>();
                services.RemoveAll<IEmailVerificationChallengePersistence>();
                services.RemoveAll<IEmailVerificationDelivery>();

                services.AddSingleton<
                    IEmailVerificationUserLookup,
                    ObservingUserLookup>();
                services.AddSingleton<
                    IEmailVerificationTokenService,
                    StubTokenService>();
                services.AddSingleton<
                    IEmailVerificationChallengePersistence,
                    StubChallengePersistence>();
                services.AddSingleton<
                    IEmailVerificationDelivery,
                    StubDelivery>();
            });
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory,
        string baseAddress)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(baseAddress)
        });
    }

    private static async Task<HttpResponseMessage> PostResendAsync(
        HttpClient client,
        IPAddress forwardedClient,
        string forwardedScheme)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            ResendPath);
        request.Headers.Add("X-Forwarded-For", forwardedClient.ToString());
        request.Headers.Add("X-Forwarded-Proto", forwardedScheme);
        request.Content = JsonContent.Create(new
        {
            Email = "unknown@example.test"
        });

        return await client.SendAsync(request);
    }

    private static async Task AssertResendStatusAsync(
        HttpClient client,
        IPAddress forwardedClient,
        HttpStatusCode expectedStatus)
    {
        using HttpResponseMessage response = await PostResendAsync(
            client,
            forwardedClient,
            "https");

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    private static void AssertProductionStartupFails(
        IReadOnlyDictionary<string, string?> settings,
        string? sensitiveConfigurationValue = null,
        bool clearConfiguration = false)
    {
        using ConfiguredApiFactory factory = new(
            "Production",
            settings,
            clearConfiguration: clearConfiguration);

        Exception exception = Assert.ThrowsAny<Exception>(
            () => _ = factory.Services);
        string failure = exception.ToString();
        Assert.Contains(ProductionValidationError, failure, StringComparison.Ordinal);

        if (sensitiveConfigurationValue is not null)
        {
            Assert.DoesNotContain(
                sensitiveConfigurationValue,
                failure,
                StringComparison.Ordinal);
        }
    }

    private static void AssertStartupSucceeds(
        string environment,
        IReadOnlyDictionary<string, string?> settings,
        bool clearConfiguration = false)
    {
        using ConfiguredApiFactory factory = new(
            environment,
            settings,
            clearConfiguration: clearConfiguration);

        Assert.NotNull(factory.Services);
    }

    private static Dictionary<string, string?> ValidProductionProxySettings()
    {
        return new Dictionary<string, string?>
        {
            ["Deployment:TrustedProxy:Enabled"] = "true",
            ["Deployment:TrustedProxy:KnownProxies:0"] =
                TrustedProxyAddress.ToString()
        };
    }

    private sealed class ConfiguredApiFactory(
        string environment,
        IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? configureTestServices = null,
        bool clearConfiguration = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting(
                "ConnectionStrings:Database",
                DatabaseConnectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                if (clearConfiguration)
                {
                    configuration.Sources.Clear();
                }

                var testSettings = new Dictionary<string, string?>(settings)
                {
                    ["ConnectionStrings:Database"] = DatabaseConnectionString
                };
                configuration.AddInMemoryCollection(testSettings);
            });

            if (configureTestServices is not null)
            {
                builder.ConfigureTestServices(configureTestServices);
            }
        }
    }

    private sealed class ImmediatePeerStartupFilter(IPAddress immediatePeer)
        : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(
            Action<IApplicationBuilder> next)
        {
            return application =>
            {
                application.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = immediatePeer;
                    await nextMiddleware();
                });
                next(application);
            };
        }
    }

    private sealed class RequestObservation
    {
        public IPAddress? RemoteIpAddress { get; set; }

        public string? Scheme { get; set; }
    }

    private sealed class ObservingUserLookup(
        IHttpContextAccessor httpContextAccessor,
        RequestObservation observation)
        : IEmailVerificationUserLookup
    {
        public Task<Guid?> FindUserIdByEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default)
        {
            HttpContext? context = httpContextAccessor.HttpContext;
            observation.RemoteIpAddress = context?.Connection.RemoteIpAddress;
            observation.Scheme = context?.Request.Scheme;
            return Task.FromResult<Guid?>(null);
        }
    }

    private sealed class StubTokenService : IEmailVerificationTokenService
    {
        public string GenerateToken(out EmailVerificationTokenHash tokenHash)
        {
            tokenHash = new EmailVerificationTokenHash(new byte[32]);
            return "unused-test-token";
        }

        public bool TryHashToken(
            string? rawToken,
            out EmailVerificationTokenHash? tokenHash)
        {
            tokenHash = null;
            return false;
        }
    }

    private sealed class StubChallengePersistence
        : IEmailVerificationChallengePersistence
    {
        public Task<EmailVerificationChallengeIssuancePersistenceResult>
            TryIssueOrRotateAsync(
                Guid userId,
                EmailVerificationTokenHash tokenHash,
                TimeSpan tokenLifetime,
                TimeSpan resendCooldown,
                CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                EmailVerificationChallengeIssuancePersistenceResult.Rejected);
        }

        public Task<EmailVerificationChallengeConsumptionPersistenceResult>
            TryConsumeAsync(
                EmailVerificationTokenHash tokenHash,
                CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                EmailVerificationChallengeConsumptionPersistenceResult.Rejected);
        }
    }

    private sealed class StubDelivery : IEmailVerificationDelivery
    {
        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(EmailVerificationDeliveryResult.Failed);
        }
    }
}
