using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Documents.Storage;

public sealed class DocumentStorageOptionsValidator
    : IValidateOptions<DocumentStorageOptions>
{
    private const int MaximumCredentialLength = 512;
    private const int MaximumRegionLength = 64;

    public ValidateOptionsResult Validate(
        string? name,
        DocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateServiceUrl(options, failures);
        ValidateBucketName(options.BucketName, failures);
        ValidateRegion(options.Region, failures);
        ValidateCredential(options.AccessKey, "AccessKey", failures);
        ValidateCredential(options.SecretKey, "SecretKey", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateServiceUrl(
        DocumentStorageOptions options,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:ServiceUrl is required.");
            return;
        }

        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 1
                && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:ServiceUrl must be an absolute HTTP(S) storage endpoint without credentials, path, query, or fragment.");
            return;
        }

        if (options.RequireTls && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:ServiceUrl must use HTTPS when RequireTls is enabled.");
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp
            && (!uri.IsLoopback || options.RequireTls))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:Plain HTTP storage is allowed only for loopback development endpoints with RequireTls disabled.");
        }
    }

    private static void ValidateBucketName(
        string bucketName,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:BucketName is required.");
            return;
        }

        if (bucketName.Length is < 3 or > 63
            || !IsAsciiLowerLetterOrDigit(bucketName[0])
            || !IsAsciiLowerLetterOrDigit(bucketName[^1]))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:BucketName must be 3-63 characters and start/end with a lowercase ASCII letter or digit.");
            return;
        }

        foreach (char character in bucketName)
        {
            if (!IsAsciiLowerLetterOrDigit(character) && character != '-')
            {
                failures.Add(
                    $"{DocumentStorageOptions.SectionName}:BucketName may contain only lowercase ASCII letters, digits, and hyphens.");
                return;
            }
        }
    }

    private static void ValidateRegion(
        string region,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:Region is required.");
            return;
        }

        if (region.Length > MaximumRegionLength
            || !IsAsciiLowerLetterOrDigit(region[0])
            || !IsAsciiLowerLetterOrDigit(region[^1]))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:Region has an invalid format.");
            return;
        }

        foreach (char character in region)
        {
            if (!IsAsciiLowerLetterOrDigit(character) && character != '-')
            {
                failures.Add(
                    $"{DocumentStorageOptions.SectionName}:Region has an invalid format.");
                return;
            }
        }
    }

    private static void ValidateCredential(
        string credential,
        string fieldName,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:{fieldName} is required.");
            return;
        }

        if (credential.Length > MaximumCredentialLength)
        {
            failures.Add(
                $"{DocumentStorageOptions.SectionName}:{fieldName} exceeds the maximum supported length.");
        }
    }

    private static bool IsAsciiLowerLetterOrDigit(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= '0' and <= '9';
    }
}
