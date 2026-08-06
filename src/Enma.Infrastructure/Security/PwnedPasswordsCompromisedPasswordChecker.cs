using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Enma.Application.Security;

namespace Enma.Infrastructure.Security;

public sealed class PwnedPasswordsCompromisedPasswordChecker
    : ICompromisedPasswordChecker
{
    private const int HashPrefixLength = 5;
    private const int HashSuffixLength = 35;
    private const long MaximumResponseSize = 1_048_576;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly HttpClient httpClient;

    public PwnedPasswordsCompromisedPasswordChecker(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        this.httpClient = httpClient;
    }

    public async Task<bool> IsCompromisedAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[]? passwordBytes = null;
        byte[]? hashBytes = null;

        try
        {
            passwordBytes = StrictUtf8.GetBytes(password);
            hashBytes = SHA1.HashData(passwordBytes);
            string completeHash = Convert.ToHexString(hashBytes);
            string prefix = completeHash[..HashPrefixLength];
            string suffix = completeHash[HashPrefixLength..];

            return await QueryRangeAsync(prefix, suffix, cancellationToken);
        }
        catch (CompromisedPasswordCheckUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (HttpRequestException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (InvalidDataException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (IOException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (EncoderFallbackException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (DecoderFallbackException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (CryptographicException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (ObjectDisposedException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        catch (NotSupportedException)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            if (hashBytes is not null)
            {
                CryptographicOperations.ZeroMemory(hashBytes);
            }
        }
    }

    private async Task<bool> QueryRangeAsync(
        string prefix,
        string expectedSuffix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"range/{prefix}");
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }

        if (response.Content.Headers.ContentLength > MaximumResponseSize)
        {
            throw new CompromisedPasswordCheckUnavailableException();
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var limitedStream = new ResponseSizeLimitedStream(
            responseStream,
            MaximumResponseSize);
        using var reader = new StreamReader(
            limitedStream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        bool isCompromised = false;
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseResponseLine(line, out string responseSuffix, out ulong count))
            {
                throw new InvalidDataException(
                    "The compromised-password provider returned a malformed response.");
            }

            if (count > 0 && string.Equals(
                    responseSuffix,
                    expectedSuffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                isCompromised = true;
            }
        }

        return isCompromised;
    }

    private static bool TryParseResponseLine(
        string line,
        out string suffix,
        out ulong count)
    {
        suffix = string.Empty;
        count = 0;

        int separatorIndex = line.IndexOf(':');

        if (separatorIndex != HashSuffixLength ||
            line.LastIndexOf(':') != separatorIndex)
        {
            return false;
        }

        ReadOnlySpan<char> suffixSpan = line.AsSpan(0, separatorIndex);
        ReadOnlySpan<char> countSpan = line.AsSpan(separatorIndex + 1);

        if (!IsUpperOrLowerHexadecimal(suffixSpan) ||
            countSpan.IsEmpty ||
            !ulong.TryParse(
                countSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out count))
        {
            return false;
        }

        suffix = suffixSpan.ToString();

        return true;
    }

    private static bool IsUpperOrLowerHexadecimal(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            bool isHexadecimal = character is >= '0' and <= '9' or
                >= 'A' and <= 'F' or
                >= 'a' and <= 'f';

            if (!isHexadecimal)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ResponseSizeLimitedStream(
        Stream innerStream,
        long maximumBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => innerStream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int currentBytesRead = innerStream.Read(buffer, offset, count);
            EnsureWithinLimit(currentBytesRead);

            return currentBytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            int currentBytesRead = innerStream.Read(buffer);
            EnsureWithinLimit(currentBytesRead);

            return currentBytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int currentBytesRead = await innerStream.ReadAsync(
                buffer,
                cancellationToken);
            EnsureWithinLimit(currentBytesRead);

            return currentBytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private void EnsureWithinLimit(int currentBytesRead)
        {
            bytesRead += currentBytesRead;

            if (bytesRead > maximumBytes)
            {
                throw new InvalidDataException(
                    "The compromised-password provider response exceeded the allowed size.");
            }
        }
    }
}
