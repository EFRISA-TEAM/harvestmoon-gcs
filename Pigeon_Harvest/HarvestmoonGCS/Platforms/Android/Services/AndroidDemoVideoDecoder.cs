#if __ANDROID__
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Media;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HarvestmoonGCS.Platforms.Android.Services;

/// <summary>
/// Decodes bundled MP4 demo video assets or files on Android frame-by-frame using
/// MediaExtractor + MediaCodec, converts each frame to JPEG taking plane strides
/// and pixel strides into account, and fires the FrameDecoded event so DashboardPage
/// can feed it into VideoStreamControl. Loops continuously until StopAsync() is called.
/// </summary>
public sealed class AndroidDemoVideoDecoder : IDisposable
{
    private const double PlaybackSpeed = 1.0;
    private const int JpegQuality = 80;
    private const int TargetFps = 20;

    private readonly Context _context;
    private readonly string _assetPath;

    private CancellationTokenSource? _cts;
    private Task? _decodeTask;
    private string? _tempVideoPath;

    public bool IsRunning => _decodeTask != null && !_decodeTask.IsCompleted;

    /// <summary>Fired on a background thread for each decoded JPEG frame.</summary>
    public event EventHandler<byte[]>? FrameDecoded;

    public AndroidDemoVideoDecoder(Context context, string assetPath = "demo_videos/stream_multisector_60s_detected.mp4")
    {
        _context = context;
        _assetPath = assetPath;
    }

    /// <summary>
    /// Ensures the video source is accessible as a local seekable file for MediaExtractor.
    /// If _assetPath is already a file on disk, uses it directly.
    /// Otherwise extracts the asset from APK to Context.CacheDir.
    /// </summary>
    private async Task<string?> ExtractAssetAsync(CancellationToken ct)
    {
        try
        {
            // If already a valid file on disk (custom video or previous cache)
            if (File.Exists(_assetPath) && new FileInfo(_assetPath).Length > 10_000)
            {
                return _assetPath;
            }

            var dir = _context.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
            var fileName = System.IO.Path.GetFileName(_assetPath);
            var dest = System.IO.Path.Combine(dir, fileName);

            // If cached file already exists with valid video size (> 1MB), reuse it
            if (File.Exists(dest) && new FileInfo(dest).Length > 1_000_000)
            {
                return dest;
            }

            System.IO.Stream? assetStream = null;
            try
            {
                assetStream = _context.Assets?.Open(_assetPath);
            }
            catch
            {
                try
                {
                    var altPath = _assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        ? _assetPath.Substring(7)
                        : "Assets/" + _assetPath;
                    assetStream = _context.Assets?.Open(altPath);
                }
                catch
                {
                    try
                    {
                        assetStream = _context.Assets?.Open(fileName);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "[DemoDecoder] Failed to locate asset {Path}", _assetPath);
                    }
                }
            }

            if (assetStream == null)
            {
                Serilog.Log.Warning("[DemoDecoder] Asset not found in APK: {Path}", _assetPath);
                return null;
            }

            var tempDest = dest + ".tmp";
            using (assetStream)
            {
                using var fs = new FileStream(tempDest, FileMode.Create, FileAccess.Write);
                await assetStream.CopyToAsync(fs, ct);
            }

            if (File.Exists(dest))
            {
                try { File.Delete(dest); } catch { }
            }
            File.Move(tempDest, dest);

            return dest;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DemoDecoder] Failed to extract asset {Path}", _assetPath);
            return null;
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _tempVideoPath = await ExtractAssetAsync(ct);
        if (_tempVideoPath == null)
        {
            Serilog.Log.Warning("[DemoDecoder] StartAsync aborted, video path is null");
            return;
        }

        _decodeTask = Task.Run(() => DecodeLoop(_tempVideoPath, ct), ct);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_decodeTask != null)
        {
            try { await _decodeTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _decodeTask = null;
        _cts = null;
    }

    private void DecodeLoop(string videoPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DecodeOnce(videoPath, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[DemoDecoder] Decode error in loop, retrying in 500ms");
                Thread.Sleep(500);
            }
        }
    }

    private void DecodeOnce(string videoPath, CancellationToken ct)
    {
        var extractor = new MediaExtractor();
        extractor.SetDataSource(videoPath);

        // Find the video track
        int videoTrack = -1;
        MediaFormat? videoFormat = null;
        for (int i = 0; i < extractor.TrackCount; i++)
        {
            var fmt = extractor.GetTrackFormat(i);
            var trackMime = fmt?.GetString(MediaFormat.KeyMime) ?? "";
            if (trackMime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                videoTrack = i;
                videoFormat = fmt;
                break;
            }
        }

        if (videoTrack < 0 || videoFormat == null)
        {
            Serilog.Log.Warning("[DemoDecoder] No video track found in {Path}", videoPath);
            extractor.Release();
            return;
        }

        extractor.SelectTrack(videoTrack);
        var mime = videoFormat.GetString(MediaFormat.KeyMime)!;

        // Create decoder with YUV output so we can convert to JPEG/Bitmap
        var codec = MediaCodec.CreateDecoderByType(mime);
        codec.Configure(videoFormat, surface: null, crypto: null, flags: 0);
        codec.Start();

        var bufferInfo = new MediaCodec.BufferInfo();
        bool inputDone = false;
        long firstSampleTimeUs = -1;
        var playbackTimer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Feed input buffer
                if (!inputDone)
                {
                    int inIdx = codec.DequeueInputBuffer(10_000); // 10ms timeout
                    if (inIdx >= 0)
                    {
                        var inputBuf = codec.GetInputBuffer(inIdx);
                        if (inputBuf == null) { inputDone = true; continue; }

                        int sampleSize = extractor.ReadSampleData(inputBuf, 0);
                        if (sampleSize < 0)
                        {
                            // EOS — signal end of stream and wait for drain
                            codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        }
                        else
                        {
                            long pts = extractor.SampleTime;
                            if (firstSampleTimeUs < 0 && pts >= 0)
                            {
                                firstSampleTimeUs = pts;
                                playbackTimer.Restart();
                            }
                            codec.QueueInputBuffer(inIdx, 0, sampleSize, pts, 0);
                            extractor.Advance();
                        }
                    }
                }

                // Drain output buffer
                int outIdx = codec.DequeueOutputBuffer(bufferInfo, 10_000);
                if (outIdx >= 0)
                {
                    bool isEos = (bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;

                    if (bufferInfo.Size > 0)
                    {
                        var image = codec.GetOutputImage(outIdx);
                        if (image != null)
                        {
                            try
                            {
                                var jpeg = ImageToJpeg(image);
                                if (jpeg != null && jpeg.Length > 0)
                                {
                                    FrameDecoded?.Invoke(this, jpeg);
                                }
                            }
                            finally
                            {
                                image.Close();
                            }
                        }
                    }

                    codec.ReleaseOutputBuffer(outIdx, false);

                    if (isEos) break; // End of this playthrough, loop restarts

                    if (firstSampleTimeUs >= 0 && bufferInfo.PresentationTimeUs >= firstSampleTimeUs)
                    {
                        var expectedMs = (bufferInfo.PresentationTimeUs - firstSampleTimeUs) / 1000.0 / PlaybackSpeed;
                        var remainingMs = expectedMs - playbackTimer.ElapsedMilliseconds;
                        var maxFrameIntervalMs = 1000.0 / TargetFps;
                        if (remainingMs > maxFrameIntervalMs)
                        {
                            remainingMs = maxFrameIntervalMs;
                        }
                        if (remainingMs > 2)
                        {
                            Thread.Sleep((int)remainingMs);
                        }
                    }
                }
                else if (outIdx == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    // Output format changed (strides/dimensions)
                }
            }
        }
        finally
        {
            try { codec.Stop(); } catch { }
            try { codec.Release(); } catch { }
            try { extractor.Release(); } catch { }
        }
    }

    private static byte[]? ImageToJpeg(global::Android.Media.Image image)
    {
        try
        {
            var format = image.Format;
            if (format == ImageFormatType.Jpeg)
            {
                var plane = image.GetPlanes()?[0];
                if (plane == null) return null;
                var buf = plane.Buffer;
                if (buf == null) return null;
                buf.Position(0);
                var bytes = new byte[buf.Remaining()];
                buf.Get(bytes);
                return bytes;
            }

            // Primary fast path: Stride-aware YUV420 to NV21 then YuvImage.CompressToJpeg
            var jpegBytes = Yuv420ToJpeg(image, JpegQuality);
            if (jpegBytes != null && jpegBytes.Length > 0)
            {
                return jpegBytes;
            }

            // Secondary fallback: Direct YUV to ARGB integer bitmap then compress
            return YuvToBitmapFallback(image, JpegQuality);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DemoDecoder] ImageToJpeg failed");
            return null;
        }
    }

    /// <summary>
    /// Accurately converts Android YUV_420_888 Image to standard NV21 byte array
    /// respecting plane.RowStride and plane.PixelStride, then encodes to JPEG via
    /// hardware-accelerated Android OS YuvImage native compressor.
    /// </summary>
    private static byte[]? Yuv420ToJpeg(global::Android.Media.Image image, int quality)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return null;

            var crop = image.CropRect;
            int width = (crop != null && crop.Width() > 0) ? crop.Width() : image.Width;
            int height = (crop != null && crop.Height() > 0) ? crop.Height() : image.Height;
            int cropLeft = crop != null ? crop.Left : 0;
            int cropTop = crop != null ? crop.Top : 0;

            // Dimensions must be even for YUV420
            width &= ~1;
            height &= ~1;

            if (width <= 0 || height <= 0) return null;

            var yPlane = planes[0];
            var uPlane = planes[1];
            var vPlane = planes[2];

            var yBuf = yPlane.Buffer;
            var uBuf = uPlane.Buffer;
            var vBuf = vPlane.Buffer;
            if (yBuf == null || uBuf == null || vBuf == null) return null;

            int yRowStride = yPlane.RowStride;
            int yPixelStride = yPlane.PixelStride;

            int uRowStride = uPlane.RowStride;
            int uPixelStride = uPlane.PixelStride;

            int vRowStride = vPlane.RowStride;
            int vPixelStride = vPlane.PixelStride;

            byte[] nv21 = new byte[width * height * 3 / 2];

            // 1. Copy Y plane row-by-row
            yBuf.Position(0);
            int yRemaining = yBuf.Remaining();
            byte[] yBytes = new byte[yRemaining];
            yBuf.Get(yBytes);

            int yDest = 0;
            for (int r = 0; r < height; r++)
            {
                int srcPos = (cropTop + r) * yRowStride + cropLeft * yPixelStride;
                if (yPixelStride == 1)
                {
                    int copyLen = Math.Min(width, Math.Max(0, yRemaining - srcPos));
                    if (copyLen > 0)
                    {
                        Buffer.BlockCopy(yBytes, srcPos, nv21, yDest, copyLen);
                    }
                    yDest += width;
                }
                else
                {
                    for (int c = 0; c < width; c++)
                    {
                        int p = srcPos + c * yPixelStride;
                        nv21[yDest++] = (p >= 0 && p < yRemaining) ? yBytes[p] : (byte)0;
                    }
                }
            }

            // 2. Interleave UV planes into NV21 (V then U)
            uBuf.Position(0);
            int uRemaining = uBuf.Remaining();
            byte[] uBytes = new byte[uRemaining];
            uBuf.Get(uBytes);

            vBuf.Position(0);
            int vRemaining = vBuf.Remaining();
            byte[] vBytes = new byte[vRemaining];
            vBuf.Get(vBytes);

            int uvDest = width * height;
            int uvHeight = height / 2;
            int uvWidth = width / 2;
            int cropUvTop = cropTop / 2;
            int cropUvLeft = cropLeft / 2;

            for (int r = 0; r < uvHeight; r++)
            {
                int srcRow = cropUvTop + r;
                int vRowBase = srcRow * vRowStride;
                int uRowBase = srcRow * uRowStride;

                for (int c = 0; c < uvWidth; c++)
                {
                    int srcCol = cropUvLeft + c;
                    int vIdx = vRowBase + srcCol * vPixelStride;
                    int uIdx = uRowBase + srcCol * uPixelStride;

                    nv21[uvDest++] = (vIdx >= 0 && vIdx < vRemaining) ? vBytes[vIdx] : (byte)128; // Cr
                    nv21[uvDest++] = (uIdx >= 0 && uIdx < uRemaining) ? uBytes[uIdx] : (byte)128; // Cb
                }
            }

            var yuvImage = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
            using var ms = new MemoryStream();
            yuvImage.CompressToJpeg(new Rect(0, 0, width, height), quality, ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DemoDecoder] Yuv420ToJpeg failed");
            return null;
        }
    }

    /// <summary>
    /// Direct fallback that converts YUV420 to ARGB pixel integers in C# and compresses via Android Bitmap.
    /// Guarantees video playback even on custom devices where YuvImage might fail.
    /// </summary>
    private static byte[]? YuvToBitmapFallback(global::Android.Media.Image image, int quality)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return null;

            int width = image.Width & ~1;
            int height = image.Height & ~1;
            if (width <= 0 || height <= 0) return null;

            var yBuf = planes[0].Buffer;
            var uBuf = planes[1].Buffer;
            var vBuf = planes[2].Buffer;
            if (yBuf == null || uBuf == null || vBuf == null) return null;

            yBuf.Position(0);
            uBuf.Position(0);
            vBuf.Position(0);

            byte[] yBytes = new byte[yBuf.Remaining()];
            byte[] uBytes = new byte[uBuf.Remaining()];
            byte[] vBytes = new byte[vBuf.Remaining()];
            yBuf.Get(yBytes);
            uBuf.Get(uBytes);
            vBuf.Get(vBytes);

            int yRowStride = planes[0].RowStride;
            int yPixelStride = planes[0].PixelStride;
            int uRowStride = planes[1].RowStride;
            int uPixelStride = planes[1].PixelStride;
            int vRowStride = planes[2].RowStride;
            int vPixelStride = planes[2].PixelStride;

            int[] argb = new int[width * height];
            int outIdx = 0;

            for (int r = 0; r < height; r++)
            {
                int yRow = r * yRowStride;
                int uvRow = (r / 2);
                int uRow = uvRow * uRowStride;
                int vRow = uvRow * vRowStride;

                for (int c = 0; c < width; c++)
                {
                    int yIdx = yRow + c * yPixelStride;
                    int uvCol = c / 2;
                    int uIdx = uRow + uvCol * uPixelStride;
                    int vIdx = vRow + uvCol * vPixelStride;

                    int yVal = (yIdx < yBytes.Length ? yBytes[yIdx] : 0) & 0xFF;
                    int uVal = (uIdx < uBytes.Length ? uBytes[uIdx] : 128) & 0xFF;
                    int vVal = (vIdx < vBytes.Length ? vBytes[vIdx] : 128) & 0xFF;

                    int y1192 = 1192 * Math.Max(0, yVal - 16);
                    int rVal = y1192 + 1634 * (vVal - 128);
                    int gVal = y1192 - 833 * (vVal - 128) - 400 * (uVal - 128);
                    int bVal = y1192 + 2066 * (uVal - 128);

                    rVal = Math.Clamp(rVal >> 10, 0, 255);
                    gVal = Math.Clamp(gVal >> 10, 0, 255);
                    bVal = Math.Clamp(bVal >> 10, 0, 255);

                    argb[outIdx++] = (unchecked((int)0xFF000000)) | (rVal << 16) | (gVal << 8) | bVal;
                }
            }

            using var bmp = Bitmap.CreateBitmap(argb, width, height, Bitmap.Config.Argb8888!);
            using var ms = new MemoryStream();
            bmp.Compress(Bitmap.CompressFormat.Jpeg!, quality, ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DemoDecoder] YuvToBitmapFallback failed");
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
#endif
