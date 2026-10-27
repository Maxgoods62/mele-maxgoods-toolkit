using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using LecImage = LegendaryExplorerCore.Textures.Image;
using LecTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace MeleMcp.Core;

/// <param name="Png">128×128 RGBA, transparent background; null when no element could be drawn (Skip says why).</param>
/// <param name="Elements">Per mesh element: the texture and tint it was painted with, or why it was not drawn.</param>
internal sealed record Thumbnail(byte[]? Png, string? Skip, IReadOnlyList<string> Elements);

/// <summary>
/// Offline thumbnails of StaticMeshes for the in-game editor's prop menu (docs/07 §3.5): a small CPU rasterizer over LOD0, the
/// diffuse texture each element's material most likely uses (with its diffuse colours), Lambert light, 2×2 supersampling. The
/// camera is orthographic, a 3/4 view from above at 45° yaw, framing the mesh to 92% of the image. Triangles are not culled
/// (cooked winding is not checked). Masked materials are cut where their opacity mask is under its clip value, when the mask
/// could be traced (<see cref="MaterialDiffuse"/>); otherwise they are drawn solid.
/// </summary>
internal sealed class ThumbnailRenderer(PackageCache cache)
{
    public const int Size = 128;
    private const int Samples = 2;
    private const int S = Size * Samples;

    /// <summary>Texture mips at least this wide are enough for a 256-pixel render.</summary>
    private const int WantedMip = S;

    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.62f, 1);

    public Thumbnail Render(ExportEntry meshExport)
    {
        var mesh = ObjectBinary.From<StaticMesh>(meshExport);
        var lod = mesh.LODModels[0];
        var count = (int)lod.NumVertices;
        var positions = lod.PositionVertexBuffer.VertexData;
        var vertices = lod.VertexBuffer;
        var uvs = new Vector2[count];
        var normals = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var v = vertices.VertexData[i];
            uvs[i] = vertices.bUseFullPrecisionUVs ? v.FullPrecisionUVs[0] : new Vector2(v.HalfPrecisionUVs[0].X, v.HalfPrecisionUVs[0].Y);
            normals[i] = (Vector3)v.TangentZ;
        }

        // One look per element; null: not drawn (see-through or distortion material).
        var looks = new Paint?[lod.Elements.Length];
        var notes = new List<string>();
        for (var e = 0; e < lod.Elements.Length; e++)
        {
            var entry = meshExport.FileRef.GetEntry(lod.Elements[e].Material);
            var material = MaterialDiffuse.Resolve(entry, cache);
            if (material is null)
            {
                looks[e] = new Paint(null, false, Vector3.One, Vector3.Zero, 0, null, false, 0);
                notes.Add($"e{e}: no material{(entry is null ? "" : $" ({entry.InstancedFullPath} not found)")}, grey");
                continue;
            }
            var (look, skip) = MaterialDiffuse.Read(material, cache);
            if (look is null)
            {
                notes.Add($"e{e}: not drawn, {skip}");
                continue;
            }
            Texture? texture = null;
            if (look.Texture is { } t)
            {
                try
                {
                    texture = Load(t);
                }
                catch (Exception ex)
                {
                    notes.Add($"e{e}: {t.ObjectName.Instanced} unreadable ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}), grey");
                }
            }
            // A channel mask (BioWare's packed region masks: saturated reds, greens, blues) under region colours: its intensity
            // times the material's main colour reads better than the mask's own colours.
            var mask = look.MaskColour is not null && texture is { MaskLike: true };
            // The opacity mask is most often the diffuse's own alpha: the texture already loaded.
            Texture? cut = null;
            if (look.Cut is { } c)
            {
                try
                {
                    cut = ReferenceEquals(c.Texture, look.Texture) ? texture : Load(c.Texture);
                }
                catch (Exception ex)
                {
                    notes.Add($"e{e}: opacity mask {c.Texture.ObjectName.Instanced} unreadable ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}), drawn solid");
                }
            }
            looks[e] = new Paint(texture, mask, Display(mask ? look.MaskColour!.Value : look.Multiply), Display(look.Override), look.OverrideAmount,
                cut, look.Cut?.Alpha ?? false, look.Cut?.Clip ?? 0);
            notes.Add($"e{e}: {look.How}{(mask ? ", texture is a mask: drawn as its intensity times the main colour" : "")}");
        }
        if (looks.All(l => l is null)) return new Thumbnail(null, "no element with an opaque material", notes);

        // Orthographic 3/4 view from above. Unreal is left-handed with Z up.
        float yaw = MathF.PI * 0.25f, pitch = MathF.PI / 7f;
        var toCamera = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var right = Vector3.Normalize(Vector3.Cross(toCamera, Vector3.UnitZ));
        var up = Vector3.Cross(right, toCamera);
        var light = Vector3.Normalize(toCamera * 0.7f + up * 0.6f - right * 0.4f);
        var center = mesh.Bounds.Origin;
        var sx = new float[count];
        var sy = new float[count];
        var sz = new float[count];
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        // Framed on the vertices the drawn elements use, so a skipped glass pane does not shrink the rest.
        var used = new bool[count];
        for (var e = 0; e < lod.Elements.Length; e++)
        {
            if (looks[e] is null) continue;
            var el = lod.Elements[e];
            for (var k = 0u; k < el.NumTriangles * 3; k++) used[lod.IndexBuffer[el.FirstIndex + k]] = true;
        }
        for (var i = 0; i < count; i++)
        {
            var p = positions[i] - center;
            sx[i] = Vector3.Dot(p, right);
            sy[i] = Vector3.Dot(p, up);
            sz[i] = Vector3.Dot(p, toCamera);
            if (!used[i]) continue;
            minX = MathF.Min(minX, sx[i]);
            maxX = MathF.Max(maxX, sx[i]);
            minY = MathF.Min(minY, sy[i]);
            maxY = MathF.Max(maxY, sy[i]);
        }
        var scale = S * 0.92f / MathF.Max(MathF.Max(maxX - minX, maxY - minY), 1e-3f);
        float ox = S * 0.5f - (minX + maxX) * 0.5f * scale, oy = S * 0.5f + (minY + maxY) * 0.5f * scale;
        for (var i = 0; i < count; i++)
        {
            sx[i] = sx[i] * scale + ox;
            sy[i] = oy - sy[i] * scale;
        }

        var raster = new Raster(sx, sy, sz, uvs, normals, positions, toCamera, light);
        for (var e = 0; e < lod.Elements.Length; e++)
        {
            if (looks[e] is not { } look) continue;
            var el = lod.Elements[e];
            for (var t = 0; t < el.NumTriangles; t++)
            {
                var k = (int)el.FirstIndex + t * 3;
                raster.Triangle(lod.IndexBuffer[k], lod.IndexBuffer[k + 1], lod.IndexBuffer[k + 2], look);
            }
        }
        if (!raster.Drawn) return new Thumbnail(null, "nothing drawn (all cut by opacity masks, or no visible triangle)", notes);
        return new Thumbnail(raster.Png(), null, notes);
    }

    /// <summary>
    /// A linear material colour in the textures' space (sRGB, gamma about 2.2), so that multiplying texels by it darkens them as
    /// the game's shader does: (a·b)^(1/2.2) = a^(1/2.2)·b^(1/2.2).
    /// </summary>
    private static Vector3 Display(Vector3 linear) =>
        new(MathF.Pow(MathF.Max(linear.X, 0), 1 / 2.2f), MathF.Pow(MathF.Max(linear.Y, 0), 1 / 2.2f), MathF.Pow(MathF.Max(linear.Z, 0), 1 / 2.2f));

    /// <summary>
    /// The smallest stored mip at least <see cref="WantedMip"/> wide (or the largest there is), decoded to BGRA. A mip in a
    /// texture cache file that is missing falls back to the largest mip stored in the package.
    /// </summary>
    private static Texture Load(ExportEntry export)
    {
        var texture = new LecTexture2D(export);
        var format = LecImage.getPixelFormatType(texture.TextureFormat);
        var mips = texture.Mips.Where(m => m.storageType != StorageTypes.empty).ToList(); // largest first
        var mip = mips.LastOrDefault(m => Math.Max(m.width, m.height) >= WantedMip) ?? mips[0];
        byte[] raw;
        try
        {
            raw = LecTexture2D.GetTextureData(mip, export.Game);
        }
        catch (FileNotFoundException)
        {
            mip = mips.First(m => m.IsPackageStored);
            raw = LecTexture2D.GetTextureData(mip, export.Game);
        }
        int w = mip.width, h = mip.height;
        return new Texture(w, h, LecImage.convertRawToARGB(raw, ref w, ref h, format));
    }

    /// <summary>
    /// An element's texture (null: grey), drawn as is or as its intensity (a mask), and its material's colours, in display space;
    /// Cut (null: none) leaves out the pixels whose alpha (CutAlpha) or red is under Clip.
    /// </summary>
    private sealed record Paint(Texture? Texture, bool Intensity, Vector3 Multiply, Vector3 Override, float OverrideAmount, Texture? Cut, bool CutAlpha, float Clip);

    /// <summary>BGRA pixels with bilinear, wrapping sampling (returns RGBA).</summary>
    private sealed class Texture(int width, int height, byte[] bgra)
    {
        /// <summary>
        /// Most lit texels are one saturated channel or two (a packed mask), where a diffuse map is mostly greys and soft colours:
        /// over half of the texels brighter than 15% have their weakest channel under 30% of their strongest.
        /// </summary>
        public bool MaskLike { get; } = Saturated(bgra) > 0.5f;

        private static float Saturated(byte[] bgra)
        {
            int lit = 0, saturated = 0;
            for (var i = 0; i < bgra.Length; i += 4)
            {
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 38) continue;
                lit++;
                if (min * 10 < max * 3) saturated++;
            }
            return lit == 0 ? 0 : (float)saturated / lit;
        }

        public Vector4 Sample(Vector2 uv)
        {
            float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
            float x = u * width - 0.5f, y = v * height - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            return Vector4.Lerp(Vector4.Lerp(Pixel(x0, y0), Pixel(x0 + 1, y0), fx), Vector4.Lerp(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), fx), fy);
        }

        private Vector4 Pixel(int x, int y)
        {
            x = (x % width + width) % width;
            y = (y % height + height) % height;
            var i = (y * width + x) * 4;
            return new Vector4(bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]) / 255f;
        }
    }

    /// <summary>The supersampled image: edge functions, a depth buffer (larger is nearer), premultiplied colour.</summary>
    private sealed class Raster(float[] sx, float[] sy, float[] sz, Vector2[] uvs, Vector3[] normals, Vector3[] positions, Vector3 toCamera, Vector3 light)
    {
        private readonly Vector4[] color = new Vector4[S * S];
        private readonly float[] depth = Enumerable.Repeat(float.MinValue, S * S).ToArray();

        /// <summary>At least one sample was drawn.</summary>
        public bool Drawn => depth.Any(d => d > float.MinValue);

        public void Triangle(int a, int b, int c, Paint paint)
        {
            float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
            var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (MathF.Abs(area) < 1e-8f) return;
            var face = Vector3.Normalize(Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]));
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))), maxX = Math.Min(S - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))), maxY = Math.Min(S - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
            var inverse = 1f / area;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * inverse;
                    var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * inverse;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                    var index = y * S + x;
                    if (z <= depth[index]) continue;
                    var uv = w0 * uvs[a] + w1 * uvs[b] + w2 * uvs[c];
                    var sample = paint.Texture?.Sample(uv) ?? Grey;
                    if (paint.Cut is { } cut && (ReferenceEquals(cut, paint.Texture) ? sample : cut.Sample(uv)) is var opacity
                        && (paint.CutAlpha ? opacity.W : opacity.X) < paint.Clip) continue;
                    var texel = paint.Intensity ? new Vector3(MathF.Max(sample.X, MathF.Max(sample.Y, sample.Z))) : new Vector3(sample.X, sample.Y, sample.Z);
                    var albedo = Vector3.Lerp(texel * paint.Multiply, paint.Override, paint.OverrideAmount);
                    var n = w0 * normals[a] + w1 * normals[b] + w2 * normals[c];
                    n = n.LengthSquared() < 0.25f ? face : Vector3.Normalize(n);
                    if (Vector3.Dot(n, toCamera) < 0) n = -n; // two-sided
                    var shade = 0.38f + 0.62f * MathF.Max(0, Vector3.Dot(n, light));
                    var rgb = Vector3.Clamp(albedo * shade, Vector3.Zero, Vector3.One);
                    depth[index] = z;
                    color[index] = new Vector4(rgb, 1);
                }
            }
        }

        /// <summary>Box-filtered down to <see cref="Size"/>, straight alpha (covered samples / all), as PNG.</summary>
        public byte[] Png()
        {
            var pixels = new Rgba32[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var sum = Vector4.Zero;
                    for (var dy = 0; dy < Samples; dy++)
                    for (var dx = 0; dx < Samples; dx++)
                        sum += color[(y * Samples + dy) * S + x * Samples + dx];
                    sum /= Samples * Samples;
                    var rgb = sum.W > 0 ? new Vector3(sum.X, sum.Y, sum.Z) / sum.W : Vector3.Zero;
                    pixels[y * Size + x] = new Rgba32(rgb.X, rgb.Y, rgb.Z, sum.W);
                }
            }
            using var image = Image.LoadPixelData<Rgba32>(pixels, Size, Size);
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }
    }
}
