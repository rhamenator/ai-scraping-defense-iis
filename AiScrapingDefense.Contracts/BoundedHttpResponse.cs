using System.Text;

namespace AiScrapingDefense.Contracts;

/// <summary>A response body exceeded the limit set for the client that requested it.</summary>
public sealed class ResponseTooLargeException : IOException
{
    public ResponseTooLargeException(long limitBytes, long? declaredBytes)
        : base(declaredBytes is null
            ? $"The response body exceeded the {limitBytes}-byte limit."
            : $"The response declared {declaredBytes} bytes, over the {limitBytes}-byte limit.")
    {
        LimitBytes = limitBytes;
        DeclaredBytes = declaredBytes;
    }

    public long LimitBytes { get; }

    public long? DeclaredBytes { get; }
}

/// <summary>
/// Reads outbound HTTP responses without letting a hostile or broken server make the process buffer or parse an
/// unbounded body (#200). Send with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, then read through these
/// methods: an oversized declared length is rejected before any body byte is read, and the bytes actually decoded are
/// counted, so a body without a length, a chunked body and a compressed body that expands are all stopped at the limit.
/// </summary>
public static class BoundedHttpResponse
{
    private const int ChunkSize = 16 * 1024;

    public static Task<HttpResponseMessage> SendWithHeadersOnlyAsync(
        this HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <summary>The whole body, as a seekable stream, or <see cref="ResponseTooLargeException"/>.</summary>
    public static async Task<MemoryStream> ReadBoundedAsync(
        this HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var declared = content.Headers.ContentLength;
        if (declared is > 0 && declared > maxBytes)
        {
            throw new ResponseTooLargeException(maxBytes, declared);
        }

        var buffer = new MemoryStream();
        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken);
            var chunk = new byte[ChunkSize];
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new ResponseTooLargeException(maxBytes, declaredBytes: null);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// The whole body decoded as text, or <see cref="ResponseTooLargeException"/>. Decoding follows what
    /// <c>ReadAsStringAsync</c> did: a byte-order mark wins, then the declared charset, then UTF-8; the BOM is not
    /// part of the text.
    /// </summary>
    public static async Task<string> ReadBoundedStringAsync(
        this HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var buffer = await content.ReadBoundedAsync(maxBytes, cancellationToken);
        using var reader = new StreamReader(
            buffer,
            ResolveEncoding(content.Headers.ContentType?.CharSet),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"', '\'', ' '));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
