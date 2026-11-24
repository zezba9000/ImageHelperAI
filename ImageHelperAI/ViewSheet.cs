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
    private readonly ImageSide[] _sides;
    private readonly int _columns;
    private readonly int _rows;

    public ViewSheet(IReadOnlyList<ImageSide> sides)
    {
        (_columns, _rows) = sides.Count switch
        {
            1 => (1, 1),
            2 => (2, 1),
            3 or 4 => (2, 2),
            5 or 6 => (3, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(sides))
        };

        _sides = new ImageSide[sides.Count];
        for (var i = 0; i < _sides.Length; i++)
            _sides[i] = sides[i];
    }

    private int Width => _columns * ViewSize;
    private int Height => _rows * ViewSize;

    public string ImageSize => FormattableString.Invariant($"{Width}x{Height}");

    public string BuildPrompt()
    {
        var cells = new StringBuilder();
        for (var i = 0; i < _columns * _rows; i++)
        {
            var contents = i < _sides.Length ? DescribeCamera(_sides[i]) : "Leave this cell fully transparent.";
            cells.AppendLine(FormattableString.Invariant($"Row {i / _columns + 1}, column {i % _columns + 1}: {contents}"));
        }

        return FormattableString.Invariant($"""
            Create an orthographic turnaround of the main subject in the attached reference image for
            multi-view 3D reconstruction. Treat the subject as one frozen 3D scene. Keep the exact
            reference pose and change only the camera between views.

            Preserve the subject's proportions, colors, markings, clothing, and asymmetry. Lock all joint
            angles, limb and hand positions, the order and spacing of hands on any shared grip, and every
            attachment and point of contact. Keep each held object's position and angle fixed relative to
            the body, including in rear views. Preserve the positions and angles of any wings or tails.
            Infer occluded or cropped geometry once and use that same geometry in every view. Let objects
            and body parts occlude one another as they would from each camera. Subject left and right
            are anatomical directions. Keep the camera upright for level views and preserve handedness.

            Render a {Width} by {Height} pixel sheet with {_columns} equal columns and {_rows} equal rows.
            Use exactly one view per assigned {ViewSize} by {ViewSize} cell, in the following arrangement:
            {cells}
            Use the same orthographic scale across every cell, with the same height of the ground plane
            and body landmarks across the level views. Fit the whole subject and its held objects and
            appendages inside each assigned cell. Leave at least 64 pixels of clear padding on each edge
            so there are continuous empty gutters between rows and between neighboring views.

            Preserve the lighting on the subject from the reference, including light direction, color,
            intensity, contrast, rim light, highlights, and self-shadowing. Keep the lights fixed relative
            to the subject as the camera moves, so the lighting on newly visible surfaces follows the
            same setup. Retain the character's shading and the shadows cast onto its own surfaces.
            Make only the background and gutters fully transparent alpha for compositing on pure white
            (#FFFFFF). Omit scenery, floor shadows, checkerboards, labels, and dividers.
            """);
    }

    public GeneratedSideImage[] ExtractViews(byte[] png, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width != Width || codec.Info.Height != Height)
            throw new InvalidDataException($"OpenAI did not return a usable {ImageSize} image.");

        using var sheet = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException("OpenAI returned an image that could not be decoded.");
        var crops = FindCrops(sheet.Pixels, cancellationToken);
        var largestCrop = ViewSize;
        foreach (var crop in crops)
            largestCrop = Math.Max(largestCrop, Math.Max(crop.Width, crop.Height));

        // Keep one scale across the set, even when the separator has moved in one row.
        var scale = (float)ViewSize / largestCrop;
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

    private SKRectI[] FindCrops(SKColor[] pixels, CancellationToken cancellationToken)
    {
        var width = Width;
        var height = Height;
        var rowEdges = new int[_rows + 1];
        rowEdges[^1] = height;
        if (_rows == 2)
        {
            var occupiedRows = new bool[height];
            for (var y = 0; y < height; y++)
            {
                foreach (var pixel in pixels.AsSpan(y * width, width))
                {
                    if (IsBackground(pixel))
                        continue;

                    occupiedRows[y] = true;
                    break;
                }
            }

            rowEdges[1] = FindGutter(occupiedRows, ViewSize, "rows");
        }

        var crops = new SKRectI[_sides.Length];
        for (var row = 0; row < _rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var top = rowEdges[row];
            var bottom = rowEdges[row + 1];
            var occupiedColumns = new bool[width];
            for (var y = top; y < bottom; y++)
            {
                var line = pixels.AsSpan(y * width, width);
                for (var x = 0; x < width; x++)
                    occupiedColumns[x] |= !IsBackground(line[x]);
            }

            var first = row * _columns;
            var count = Math.Min(_columns, _sides.Length - first);
            var left = 0;
            for (var column = 0; column < count; column++)
            {
                var right = column == _columns - 1
                    ? width
                    : FindGutter(occupiedColumns, (column + 1) * ViewSize, "views");
                var index = first + column;
                if (!occupiedColumns.AsSpan(left, right - left).Contains(true))
                {
                    var side = _sides[index].ToString().ToLowerInvariant();
                    throw new InvalidDataException($"OpenAI returned an empty {side} view. Try generating again.");
                }

                crops[index] = new SKRectI(left, top, right, bottom);
                left = right;
            }
        }

        return crops;
    }

    private static int FindGutter(bool[] occupied, int expected, string between)
    {
        // Find two clear scan lines on either side of the cut, nearest the intended boundary.
        for (var distance = 0; distance <= GutterSearchRadius; distance++)
        {
            var before = expected - distance;
            if (!occupied[before - 1] && !occupied[before])
                return before;

            var after = expected + distance;
            if (!occupied[after - 1] && !occupied[after])
                return after;
        }

        throw new InvalidDataException($"OpenAI returned a sheet without a clear gap between {between}. Try generating again.");
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
