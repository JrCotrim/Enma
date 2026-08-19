using Enma.Infrastructure.Documents.Storage;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Documents;

public sealed class DocumentStorageOptionsValidatorTests
{
    private readonly DocumentStorageOptionsValidator validator = new();

    [Fact]
    public void Validate_HttpsEndpointWithRequiredTls_Succeeds()
    {
        DocumentStorageOptions options = CreateValidOptions();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_LoopbackHttpWithTlsDisabled_Succeeds()
    {
        DocumentStorageOptions options = CreateValidOptions(
            serviceUrl: "http://127.0.0.1:9000",
            requireTls: false);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RemoteHttpWithTlsDisabled_Fails()
    {
        DocumentStorageOptions options = CreateValidOptions(
            serviceUrl: "http://storage.example.test",
            requireTls: false);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Plain HTTP storage", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_LoopbackHttpWithTlsRequired_Fails()
    {
        DocumentStorageOptions options = CreateValidOptions(
            serviceUrl: "http://localhost:9000",
            requireTls: true);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("RequireTls", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://user:secret@storage.example.test")]
    [InlineData("https://storage.example.test/documents")]
    [InlineData("https://storage.example.test?bucket=enma-documents")]
    [InlineData("https://storage.example.test#fragment")]
    [InlineData("ftp://storage.example.test")]
    public void Validate_UnsafeServiceUrl_Fails(string serviceUrl)
    {
        DocumentStorageOptions options = CreateValidOptions(serviceUrl: serviceUrl);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("ServiceUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ENMA-documents")]
    [InlineData("enma.documents")]
    [InlineData("-enma-documents")]
    [InlineData("enma-documents-")]
    [InlineData("ab")]
    public void Validate_UnsafeBucketName_Fails(string bucketName)
    {
        DocumentStorageOptions options = CreateValidOptions(bucketName: bucketName);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("BucketName", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US-EAST-1")]
    [InlineData("-us-east-1")]
    [InlineData("us_east_1")]
    public void Validate_InvalidRegion_Fails(string region)
    {
        DocumentStorageOptions options = CreateValidOptions(region: region);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Region", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingCredentials_FailsWithoutSecretDisclosure()
    {
        const string syntheticSecret = "must-never-appear-in-validation-output";

        DocumentStorageOptions options = CreateValidOptions(
            accessKey: string.Empty,
            secretKey: syntheticSecret);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);

        string combinedFailures = string.Join(Environment.NewLine, result.Failures);
        Assert.Contains("AccessKey", combinedFailures, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticSecret, combinedFailures, StringComparison.Ordinal);
    }

    private static DocumentStorageOptions CreateValidOptions(
        string serviceUrl = "https://storage.example.test",
        string bucketName = "enma-documents",
        string region = "us-east-1",
        string accessKey = "synthetic-access-key",
        string secretKey = "synthetic-secret-key",
        bool requireTls = true)
    {
        return new DocumentStorageOptions
        {
            ServiceUrl = serviceUrl,
            BucketName = bucketName,
            Region = region,
            ForcePathStyle = true,
            AccessKey = accessKey,
            SecretKey = secretKey,
            RequireTls = requireTls
        };
    }
}
