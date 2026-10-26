using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChaoChaoKvm.Services
{
    public sealed class ScreenInfo
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Machine { get; set; }
    }

    public sealed class ScreenCaptureService : IDisposable
    {
        private readonly object _sync = new object();
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        private Bitmap _bitmap;
        private Graphics _graphics;
        public ScreenInfo GetInfo()
        {
            var rect = SystemInformation.VirtualScreen;
            return new ScreenInfo
            {
                Left = rect.Left,
                Top = rect.Top,
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Machine = Environment.MachineName
            };
        }

        public byte[] CaptureJpeg(int quality)
        {
            ScreenInfo info = GetInfo();
            lock (_sync)
            {
                if (_bitmap == null || _bitmap.Width != info.Width || _bitmap.Height != info.Height)
                {
                    ReleaseSurface();
                    _bitmap = new Bitmap(info.Width, info.Height, PixelFormat.Format24bppRgb);
                    _graphics = Graphics.FromImage(_bitmap);
                }
                _graphics.CopyFromScreen(info.Left, info.Top, 0, 0, _bitmap.Size, CopyPixelOperation.SourceCopy);
                using (MemoryStream output = new MemoryStream())
                using (EncoderParameters parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(25, Math.Min(85, quality)));
                    _bitmap.Save(output, JpegCodec, parameters);
                    return output.ToArray();
                }
            }
        }
        private void ReleaseSurface() { if (_graphics != null) _graphics.Dispose(); if (_bitmap != null) _bitmap.Dispose(); _graphics = null; _bitmap = null; }
        public void Dispose() { lock (_sync) ReleaseSurface(); }
    }
}
