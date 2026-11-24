using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ImageHelperAI;

public partial class SidesTab : UserControl, IDisposable
{
    private const string ApiKeyVariable = "OPENAI_API_KEY";
    private const int MaximumSourceBytes = 50_000_000;

    private static readonly FilePickerFileType SourceFileType = new("Source images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
        MimeTypes = ["image/png", "image/jpeg", "image/webp"],
        AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp"]
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly OpenAiImageClient _imageClient;
    private readonly ObservableCollection<GeneratedView> _results = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly (ImageSide Side, CheckBox Option)[] _sideOptions;
    private SourceImage? _source;
    private bool _busy;
    private bool _disposed;

    public SidesTab() : this(new OpenAiImageClient(Http))
    {
    }

    internal SidesTab(OpenAiImageClient imageClient)
    {
        _imageClient = imageClient;
        InitializeComponent();
        _sideOptions =
        [
            (ImageSide.Front, FrontOption),
            (ImageSide.Back, BackOption),
            (ImageSide.Left, LeftOption),
            (ImageSide.Right, RightOption),
            (ImageSide.Top, TopOption),
            (ImageSide.Bottom, BottomOption)
        ];

        foreach (var (_, option) in _sideOptions)
            option.IsCheckedChanged += (_, _) => UpdateControls();

        ResultsGrid.ItemsSource = _results;
        UpdateControls();
    }

    private async void ChooseSource(object? sender, RoutedEventArgs e)
    {
        if (_busy || _disposed || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var cancellationToken = _lifetime.Token;
        SetBusy(true);
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a source image",
                FileTypeFilter = [SourceFileType]
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count == 0)
                return;

            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            var bytes = await ReadSourceAsync(stream, cancellationToken);
            var (extension, mediaType) = DetectSourceFormat(bytes);
            using var imageStream = new MemoryStream(bytes, writable: false);
            var preview = new Bitmap(imageStream);

            var previousPreview = SourcePreview.Source as Bitmap;
            SourcePreview.Source = preview;
            previousPreview?.Dispose();
            SourcePlaceholder.IsVisible = false;
            SourceName.Text = file.Name;
            SourceName.IsVisible = true;
            ToolTip.SetTip(SourceName, file.Name);
            _source = new SourceImage(bytes, extension, mediaType);
            ClearResults();
            GenerationStatus.Text = "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (!_disposed)
                GenerationStatus.Text = $"Could not open the source image. {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Generate(object? sender, RoutedEventArgs e)
    {
        if (_busy || _disposed || _source is not { } source)
            return;

        var sides = _sideOptions.Where(item => item.Option.IsChecked == true).Select(item => item.Side).ToArray();
        if (sides.Length == 0)
            return;

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable)?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            GenerationStatus.Text = $"Set {ApiKeyVariable} and restart ImageHelperAI to generate views.";
            return;
        }

        var cancellationToken = _lifetime.Token;
        SetBusy(true);
        GenerationProgress.IsVisible = true;
        GenerationStatus.Text = sides.Length == 1
            ? $"Generating the {sides[0].ToString().ToLowerInvariant()} view…"
            : $"Generating {sides.Length} views together…";
        try
        {
            var images = await _imageClient.GenerateSidesAsync(
                apiKey, source.Bytes, source.Extension, source.MediaType, sides, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetResults(images);
            GenerationStatus.Text = images.Length == 1 ? "Generated 1 view." : $"Generated {images.Length} views.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            GenerationStatus.Text = "OpenAI timed out while generating the requested views.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or
                                   InvalidOperationException or ArgumentException)
        {
            if (!_disposed)
                GenerationStatus.Text = $"Could not generate the requested views. {ex.Message}";
        }
        finally
        {
            if (!_disposed)
                GenerationProgress.IsVisible = false;
            SetBusy(false);
        }
    }

    private async void SaveResults(object? sender, RoutedEventArgs e)
    {
        if (_busy || _disposed || _results.Count == 0 || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var results = _results.ToArray();
        var cancellationToken = _lifetime.Token;
        var saved = 0;
        SetBusy(true);
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Save generated views"
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (folders.Count == 0)
                return;

            using var folder = folders[0];
            if (folder.TryGetLocalPath() is not { } folderPath)
            {
                GenerationStatus.Text = "Choose a local folder to save the generated views.";
                return;
            }

            foreach (var result in results)
            {
                await SavePngAsync(folderPath, result, cancellationToken);
                saved++;
            }

            GenerationStatus.Text = saved == 1
                ? $"Saved 1 PNG to {folderPath}."
                : $"Saved {saved} PNGs to {folderPath}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (!_disposed)
                GenerationStatus.Text = $"Saved {saved} of {results.Length} views. {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<byte[]> ReadSourceAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var contents = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (contents.Length + read >= MaximumSourceBytes)
                throw new InvalidDataException("Choose an image smaller than 50 MB.");

            contents.Write(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return contents.ToArray();
    }

    private static (string Extension, string MediaType) DetectSourceFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return (".png", "image/png");

        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
            return (".jpg", "image/jpeg");

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8))
            return (".webp", "image/webp");

        throw new InvalidDataException("Choose a PNG, JPEG, or WebP image.");
    }

    private static async Task SavePngAsync(string directory, GeneratedView result, CancellationToken cancellationToken)
    {
        // Finish writing before giving the file its final name so interrupted saves leave no partial PNG.
        var tempPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, result.Png, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var stem = result.Name.ToLowerInvariant();
            for (var suffix = 1; ; suffix++)
            {
                var name = suffix == 1 ? $"{stem}.png" : $"{stem}-{suffix}.png";
                var path = Path.Combine(directory, name);
                try
                {
                    File.Move(tempPath, path);
                    return;
                }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private void SetResults(GeneratedSideImage[] images)
    {
        var previews = new List<GeneratedView>(images.Length);
        try
        {
            foreach (var image in images)
            {
                using var stream = new MemoryStream(image.Png, writable: false);
                previews.Add(new GeneratedView(image.Side, image.Png, new Bitmap(stream)));
            }
        }
        catch
        {
            foreach (var preview in previews)
                preview.Preview.Dispose();
            throw;
        }

        // Save always exports one set generated from the same sheet.
        ClearResults();
        foreach (var preview in previews)
            _results.Add(preview);
    }

    private void ClearResults()
    {
        var previous = _results.ToArray();
        _results.Clear();
        foreach (var result in previous)
            result.Preview.Dispose();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (!_disposed)
            UpdateControls();
    }

    private void UpdateControls()
    {
        SourceButton.IsEnabled = !_busy;
        SideOptions.IsEnabled = !_busy;
        GenerateButton.IsEnabled = !_busy && _source is not null &&
                                   _sideOptions.Any(item => item.Option.IsChecked == true);
        SaveButton.IsEnabled = !_busy && _results.Count > 0;
        ResultsPlaceholder.IsVisible = _results.Count == 0;
        ResultsScroll.IsVisible = _results.Count > 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        var sourcePreview = SourcePreview.Source as Bitmap;
        SourcePreview.Source = null;
        sourcePreview?.Dispose();
        _source = null;
        ClearResults();
    }

    private sealed record SourceImage(byte[] Bytes, string Extension, string MediaType);
}

internal sealed record GeneratedView(ImageSide Side, byte[] Png, Bitmap Preview)
{
    public string Name => Side.ToString();
}
