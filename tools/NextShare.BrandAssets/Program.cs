using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Package the approved artwork for Windows. Its colors and design are retained.
if (args.Length != 2) throw new ArgumentException("BrandAssets <approved.png> <output folder>");
var source = BitmapFrame.Create(new Uri(Path.GetFullPath(args[0])), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
int stride = pixels.PixelWidth * 4;
byte[] bytes = new byte[stride * pixels.PixelHeight]; pixels.CopyPixels(bytes, stride, 0);
int left = pixels.PixelWidth, top = pixels.PixelHeight, right = -1, bottom = -1;
for (int y = 0; y < pixels.PixelHeight; y++)
    for (int x = 0; x < pixels.PixelWidth; x++)
        if (bytes[y * stride + x * 4 + 3] > 8)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
if (right < left) throw new InvalidDataException("Artwork is transparent");
var artwork = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
byte[] Render(int size)
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
    double scale = size * 0.92 / Math.Max(artwork.PixelWidth, artwork.PixelHeight);
    double width = artwork.PixelWidth * scale, height = artwork.PixelHeight * scale;
    using (var drawing = visual.RenderOpen())
        drawing.DrawImage(artwork, new Rect((size - width) / 2, (size - height) / 2, width, height));
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
}
Directory.CreateDirectory(args[1]);
File.WriteAllBytes(Path.Combine(args[1], "nextshare-logo.png"), Render(512));
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(Render).ToArray();
using var icon = new BinaryWriter(File.Create(Path.Combine(args[1], "nextshare.ico")));
icon.Write((ushort)0); icon.Write((ushort)1); icon.Write((ushort)sizes.Length);
uint offset = (uint)(6 + 16 * sizes.Length);
for (int i = 0; i < sizes.Length; i++)
{
    icon.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); icon.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    icon.Write((byte)0); icon.Write((byte)0); icon.Write((ushort)1); icon.Write((ushort)32);
    icon.Write((uint)frames[i].Length); icon.Write(offset); offset += (uint)frames[i].Length;
}
foreach (var frame in frames) icon.Write(frame);
Console.WriteLine("Device Link: 512px PNG + Windows ICO (" + string.Join(", ", sizes) + ")");
