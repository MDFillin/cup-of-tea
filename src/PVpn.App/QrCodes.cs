using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Multi;
using ZXing.QrCode;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PVpn.App;

/// <summary>QR-коды со ссылками серверов: чтение с экрана или из картинки и показ ссылки сервера кодом.</summary>
internal static class QrCodes
{
    /// <summary>Все QR-коды на картинке (их может быть несколько — например, по коду на сервер).</summary>
    public static IReadOnlyList<string> Decode(BitmapSource image)
    {
        var bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return Decode(pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    private static IReadOnlyList<string> Decode(byte[] bgra, int width, int height)
    {
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        };
        var reader = new GenericMultipleBarcodeReader(new MultiFormatReader());
        var results = reader.decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints);
        return results?.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList() ?? [];
    }

    /// <summary>Снимок всех мониторов (QR в браузере, мессенджере, на картинке).</summary>
    public static IReadOnlyList<string> DecodeScreens()
    {
        var area = Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Drawing.Bitmap(area.Width, area.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, area.Width, area.Height), Drawing.Imaging.ImageLockMode.ReadOnly,
            Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * area.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return Decode(pixels, area.Width, area.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>QR-код текста: чёрные модули на белом, с полем в 4 модуля.</summary>
    public static BitmapSource Encode(string text, int modulePixels = 6)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
        var size = matrix.Width * modulePixels;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            pixels[y * size + x] = matrix[x / modulePixels, y / modulePixels] ? (byte)0 : (byte)255;
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
