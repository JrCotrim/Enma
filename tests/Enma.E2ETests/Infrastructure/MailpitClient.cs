using System.Text.Json;
using System.Text.RegularExpressions;

namespace Enma.E2ETests.Infrastructure;

public sealed class MailpitClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Regex LinkPattern = new(
        "https://[^\\s<>\"']+",
        RegexOptions.CultureInvariant);

    private readonly HttpClient httpClient;

    public MailpitClient(Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        httpClient = new HttpClient { BaseAddress = apiBaseAddress };
    }

    // Polls until the recipient's newest message links to the expected
    // application page and returns that link. An older message for the same
    // recipient (e.g. the invitation before the verification e-mail) keeps
    // the poll going instead of failing.
    public async Task<Uri> WaitForLinkAsync(
        string recipient,
        Uri expectedPage,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(expectedPage);

        TimeSpan limit = timeout ?? DefaultTimeout;
        using var deadline = new CancellationTokenSource(limit);

        try
        {
            while (true)
            {
                string? messageId = await FindLatestMessageIdAsync(
                    recipient,
                    deadline.Token);

                if (messageId is not null)
                {
                    string text = await GetMessageTextAsync(
                        messageId,
                        deadline.Token);

                    if (FindLink(text, expectedPage) is Uri link)
                    {
                        return link;
                    }
                }

                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "No e-mail with a link to the expected page reached Mailpit " +
                $"for the E2E recipient within {limit}.");
        }
    }

    public static string ReadFragmentValue(Uri link, string name)
    {
        ArgumentNullException.ThrowIfNull(link);

        foreach (string pair in link.Fragment.TrimStart('#').Split('&'))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException(
            $"The e-mail link has no '{name}' fragment value.");
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<string?> FindLatestMessageIdAsync(
        string recipient,
        CancellationToken cancellationToken)
    {
        string query = Uri.EscapeDataString($"to:\"{recipient}\"");
        using HttpResponseMessage response = await httpClient.GetAsync(
            $"api/v1/search?query={query}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement messages = document.RootElement.GetProperty("messages");

        // Mailpit lists the newest message first.
        return messages.GetArrayLength() == 0
            ? null
            : messages[0].GetProperty("ID").GetString();
    }

    private async Task<string> GetMessageTextAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            $"api/v1/message/{Uri.EscapeDataString(messageId)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        return document.RootElement.GetProperty("Text").GetString()
            ?? string.Empty;
    }

    private static Uri? FindLink(string text, Uri expectedPage)
    {
        string expectedPrefix = expectedPage.GetLeftPart(UriPartial.Path);

        foreach (Match match in LinkPattern.Matches(text))
        {
            if (match.Value.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                return new Uri(match.Value, UriKind.Absolute);
            }
        }

        return null;
    }
}
