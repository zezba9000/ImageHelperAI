using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ImageHelperAI;

internal sealed record GeneratedSideImage(ImageSide Side, byte[] Png);

internal sealed class ViewSheet
{
    private const int ViewSize = 1024;
    private const int GutterSearchRadius = ViewSize / 4;
    private const float CloseFramingZoom = 1.25f;
    private readonly ImageSide[] _sides;
    private readonly bool _frameCloser;
    private readonly int _columns;
    private readonly int _rows;

    public ViewSheet(IReadOnlyList<ImageSide> sides, bool frameCloser)
    {
        (_columns, _rows) = sides.Count switch
        {
            1 => (1, 1),
            2 => (2, 1),
            3 or 4 => (2, 2),
            5 or 6 => (3, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(sides))
        };

        _frameCloser = frameCloser;
        _sides = new ImageSide[sides.Count];
        for (var i = 0; i < _sides.Length; i++)
            _sides[i] = sides[i];
    }

    private int Width => _columns * ViewSize;
    private int Height => _rows * ViewSize;

    public string ImageSize => FormattableString.Invariant($"{Width}x{Height}");

    public string DescribeCell(int index) =>
        FormattableString.Invariant($"row {index / _columns + 1}, column {index % _columns + 1}");

    public string BuildPrompt(string? referenceCell = null)
    {
        var cells = new StringBuilder();
        for (var i = 0; i < _columns * _rows; i++)
        {
            var contents = i < _sides.Length ? DescribeCamera(_sides[i]) : "Leave this cell fully transparent.";
            cells.AppendLine($"{DescribeCell(i)}: {contents}");
        }

        var padding = _frameCloser ? 32 : 64;
        var output = _sides.Length == 1
            ? $"""
              Render exactly one full subject on a {ImageSize} canvas. {DescribeCamera(_sides[0])}
              Fit all held objects and appendages inside the frame, with clear padding on every edge.
              """
            : FormattableString.Invariant($"""
              Render a {Width} by {Height} pixel sheet with {_columns} equal columns and {_rows} equal rows.
              Use exactly one view per assigned {ViewSize} by {ViewSize} cell, in the following arrangement:
              {cells}
              Use the same orthographic scale across every cell, with the same height of the ground plane
              and body landmarks across the level views. Fit the whole subject and its held objects and
              appendages inside each assigned cell. Leave at least {padding} pixels of clear padding on
              each edge so there are continuous empty gutters between rows and between neighboring views.
              """);
        var framing = _frameCloser ? $"""
            Make the subject's main body prominent, using the original reference's framing as a guide.
            Minimize empty space in each working view while keeping the whole silhouette and at least
            {padding} pixels of clear padding for a consistent close crop after rendering.
            """ : "";
        var references = referenceCell is null ? "" : $"""
            Image 1 is the original reference for the subject, pose, and lighting. Image 2 is a turnaround
            of that subject. Use it to match the inferred geometry, scale, and framing of {referenceCell}.
            Render the requested camera angle as a single view with one subject on the entire canvas.
            """;

        return FormattableString.Invariant($"""
            Create an orthographic turnaround of the main subject in the attached reference image for
            multi-view 3D reconstruction. Treat the subject as one frozen 3D scene. Keep the exact
            reference pose and change only the camera between views.
            {references}

            Preserve the subject's proportions, colors, markings, clothing, and asymmetry. Lock all joint
            angles, limb and hand positions, the order and spacing of hands on any shared grip, and every
            attachment and point of contact. Keep each held object's position and angle fixed relative to
            the body, including in rear views. Preserve the positions and angles of any wings or tails.
            Infer occluded or cropped geometry once and use that same geometry in every view. Let objects
            and body parts occlude one another as they would from each camera. Subject left and right
            are anatomical directions. Keep the camera upright for level views and preserve handedness.

            {output}
            {framing}

            Preserve the lighting on the subject from the reference, including light direction, color,
            intensity, contrast, rim light, highlights, and self-shadowing. Keep the lights fixed relative
            to the subject as the camera moves, so the lighting on newly visible surfaces follows the
            same setup. Retain the character's shading and the shadows cast onto its own surfaces.
            Make only the background and gutters fully transparent alpha for compositing on pure white
            (#FFFFFF). Omit scenery, floor shadows, checkerboards, labels, and dividers.
            """);
    }

    public GeneratedSideImage[]? TryExtractViews(byte[] png, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width != Width || codec.Info.Height != Height)
            throw new InvalidDataException($"OpenAI did not return a usable {ImageSize} image.");

        using var sheet = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException("OpenAI returned an image that could not be decoded.");
        var pixels = sheet.Pixels;
        var crops = TryFindCrops(pixels, rowsFirst: true, cancellationToken);
        if (crops is null && _rows > 1)
            crops = TryFindCrops(pixels, rowsFirst: false, cancellationToken);
        if (crops is null)
            return null;

        var largestCrop = ViewSize;
        foreach (var crop in crops)
            largestCrop = Math.Max(largestCrop, Math.Max(crop.Width, crop.Height));

        // Apply the same zoom around each view's center after separating the sheet.
        var zoom = _frameCloser ? CloseFramingZoom : 1f;
        var scale = ViewSize * zoom / largestCrop;
        using var sheetImage = SKImage.FromBitmap(sheet);
        var images = new GeneratedSideImage[_sides.Length];
        for (var i = 0; i < _sides.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crop = crops[i];
            var side = _sides[i];
            var scaledWidth = crop.Width * scale;
            var scaledHeight = crop.Height * scale;
            var x = (ViewSize - scaledWidth) / 2;
            var y = (ViewSize - scaledHeight) / 2;

            using var view = new SKBitmap(ViewSize, ViewSize, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(view);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(sheetImage, crop, SKRect.Create(x, y, scaledWidth, scaledHeight),
                new SKSamplingOptions(SKFilterMode.Linear));
            using var encoded = view.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException($"The {side.ToString().ToLowerInvariant()} view could not be encoded.");
            images[i] = new GeneratedSideImage(side, encoded.ToArray());
        }

        return images;
    }

    private SKRectI[]? TryFindCrops(SKColor[] pixels, bool rowsFirst, CancellationToken cancellationToken)
    {
        // Staggered views can have a clean gap within each column even when the full rows overlap.
        var sheet = new SKRectI(0, 0, Width, Height);
        var outerCount = rowsFirst ? _rows : _columns;
        var innerCount = rowsFirst ? _columns : _rows;
        var outerEdges = outerCount == 1
            ? new[] { 0, rowsFirst ? Height : Width }
            : TryFindEdges(Project(pixels, sheet, projectRows: rowsFirst), outerCount);
        if (outerEdges is null)
            return null;

        var crops = new SKRectI[_sides.Length];
        for (var outer = 0; outer < outerCount; outer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = outerEdges[outer];
            var end = outerEdges[outer + 1];
            var band = rowsFirst ? new SKRectI(0, start, Width, end) : new SKRectI(start, 0, end, Height);
            var occupied = Project(pixels, band, projectRows: !rowsFirst);
            var innerEdges = TryFindEdges(occupied, innerCount);
            if (innerEdges is null)
                return null;

            for (var inner = 0; inner < innerCount; inner++)
            {
                var index = rowsFirst ? outer * _columns + inner : inner * _columns + outer;
                if (index >= _sides.Length)
                    continue;

                var left = innerEdges[inner];
                var right = innerEdges[inner + 1];
                if (!occupied.AsSpan(left, right - left).Contains(true))
                    return null;

                crops[index] = rowsFirst ? new SKRectI(left, start, right, end) : new SKRectI(start, left, end, right);
            }
        }

        return crops;
    }

    private bool[] Project(SKColor[] pixels, SKRectI area, bool projectRows)
    {
        var occupied = new bool[projectRows ? area.Height : area.Width];
        var width = Width;
        for (var y = 0; y < area.Height; y++)
        {
            var line = pixels.AsSpan((area.Top + y) * width + area.Left, area.Width);
            for (var x = 0; x < line.Length; x++)
            {
                var coordinate = projectRows ? y : x;
                occupied[coordinate] |= !IsBackground(line[x]);
            }
        }

        return occupied;
    }

    private static int[]? TryFindEdges(bool[] occupied, int count)
    {
        var edges = new int[count + 1];
        edges[^1] = occupied.Length;
        for (var i = 1; i < count; i++)
        {
            edges[i] = FindGutter(occupied, i * ViewSize);
            if (edges[i] < 0)
                return null;
        }

        return edges;
    }

    private static int FindGutter(bool[] occupied, int expected)
    {
        // Find a cut with a clear scan line on each side, nearest the intended boundary.
        for (var distance = 0; distance <= GutterSearchRadius; distance++)
        {
            var before = expected - distance;
            if (!occupied[before - 1] && !occupied[before])
                return before;

            var after = expected + distance;
            if (!occupied[after - 1] && !occupied[after])
                return after;
        }

        return -1;
    }

    private static bool IsBackground(SKColor pixel) => pixel.Alpha <= 8 || pixel == SKColors.White;

    private static string DescribeCamera(ImageSide side) => side switch
    {
        ImageSide.Front => "Front. Camera directly in front, looking level at the subject.",
        ImageSide.Back => "Back. Camera directly behind, looking level at the subject's back.",
        ImageSide.Left => "Left profile. Camera level with the subject's anatomical left side. The subject's front points toward the left edge.",
        ImageSide.Right => "Right profile. Camera level with the subject's anatomical right side. The subject's front points toward the right edge.",
        ImageSide.Top => "Top. Camera directly above, looking straight down. The subject's front points toward the bottom edge.",
        ImageSide.Bottom => "Bottom. Camera directly below, looking straight up. The subject's front points toward the bottom edge.",
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };
}
