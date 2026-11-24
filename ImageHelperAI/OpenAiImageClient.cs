using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ImageHelperAI;

internal enum ImageSide
{
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom
}

internal sealed class OpenAiImageClient(HttpClient httpClient)
{
    private const string Model = "gpt-image-2.6-sunburst";
    private const string EditsEndpoint = "https://api.openai.com/v1/images/edits";

    public async Task<GeneratedSideImage[]> GenerateSidesAsync(
        string apiKey, byte[] source, string extension, string mediaType, IReadOnlyList<ImageSide> sides,
        CancellationToken cancellationToken)
    {
        var layout = new ViewSheet(sides);
        using var request = new HttpRequestMessage(HttpMethod.Post, EditsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var form = new MultipartFormDataContent();
        request.Content = form;
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent(layout.BuildPrompt()), "prompt");
        form.Add(new StringContent("png"), "output_format");
        form.Add(new StringContent("transparent"), "background");
        form.Add(new StringContent(layout.ImageSize), "size");
        form.Add(new StringContent("medium"), "quality");

        var image = new ByteArrayContent(source);
        image.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(image, "image[]", $"source{extension}");

        // The default completion option keeps HttpClient.Timeout in effect while the image downloads.
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorAsync(response, apiKey, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 ||
            data[0].ValueKind != JsonValueKind.Object ||
            !data[0].TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String ||
            !encoded.TryGetBytesFromBase64(out var bytes) || bytes is not { Length: > 0 })
        {
            throw new InvalidDataException("OpenAI returned no usable image.");
        }

        return layout.ExtractViews(bytes, cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response, string apiKey, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return "OpenAI rejected the API key. Check OPENAI_API_KEY.";

        var fallback = $"OpenAI returned HTTP {(int)response.StatusCode} ({response.StatusCode}).";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                message.GetString() is { Length: > 0 } detail)
            {
                detail = detail.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
                return detail.Length > 450 ? $"{detail[..450]}…" : detail;
            }
        }
        catch (JsonException)
        {
            // Proxies and gateways can return a non-JSON error page.
        }

        return fallback;
    }
}
