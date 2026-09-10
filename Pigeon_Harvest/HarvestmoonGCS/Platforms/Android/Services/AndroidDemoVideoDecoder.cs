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
/// Decodes the bundled stream_v7c_final.mp4 asset frame-by-frame using
/// MediaExtractor + MediaCodec, converts each frame to JPEG, and fires the
/// FrameDecoded event so DashboardPage can feed it into VideoStreamControl.
/// Loops the video continuously until StopAsync() is called.
/// </summary>
public sealed class AndroidDemoVideoDecoder : IDisposable
{
/// Decodes the bundled stream_v7c_final.mp4 asset frame-by-frame using
    private const double PlaybackSpeed = 1.0;
    private const int JpegQuality = 72;
    // Decode at half-resolution to keep Realme Pad Mini CPU load low
    private const int MaxWidth = 640;

    private readonly Context _context;
    private readonly string _assetPath;
    private const int TargetFps = 10;

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
    /// Copy the asset to a temp file (needed because MediaExtractor requires a seekable file
    /// descriptor, which AssetFileDescriptor provides but we open it safely via CreateFd).
    /// </summary>
    private async Task<string?> ExtractAssetAsync(CancellationToken ct)
    {
        try
        {
            var dir = _context.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
            var fileName = System.IO.Path.GetFileName(_assetPath);
            var dest = System.IO.Path.Combine(dir, fileName);

            // Always refresh the cached copy so the app cannot keep replaying an older asset.
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            using var assetStream = _context.Assets?.Open(_assetPath);
            if (assetStream == null)
            {
                Serilog.Log.Warning("[DemoDecoder] Asset not found: {Path}", _assetPath);
                return null;
            }

            using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
            await assetStream.CopyToAsync(fs, ct);
            return dest;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DemoDecoder] Failed to extract asset");
            return null;
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _tempVideoPath = await ExtractAssetAsync(ct);
        if (_tempVideoPath == null) return;

        _decodeTask = Task.Run(() => DecodeLoop(_tempVideoPath, ct), ct);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_decodeTask != null)
        {
            try { await _decodeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
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
                Serilog.Log.Error(ex, "[DemoDecoder] Decode error, retrying");
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

        // Create decoder with YUV output so we can convert to Bitmap
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
                // Feed input
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
                            // EOS — signal end of stream and loop
                            codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        }
                        else
                        {
                            long pts = extractor.SampleTime;
                            if (firstSampleTimeUs < 0)
                            {
                                firstSampleTimeUs = pts;
                            }
                            codec.QueueInputBuffer(inIdx, 0, sampleSize, pts, 0);
                            extractor.Advance();
                        }
                    }
                }

                // Drain output
                int outIdx = codec.DequeueOutputBuffer(bufferInfo, 10_000);
                if (outIdx >= 0)
                {
                    bool isEos = (bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;

                    if (bufferInfo.Size > 0)
                    {
                        // Get decoded frame as Image (YUV/RGBA depending on device)
                        var image = codec.GetOutputImage(outIdx);
                        if (image != null)
                        {
                            try
                            {
                                var jpeg = ImageToJpeg(image, videoFormat);
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

                    if (isEos) break; // restart loop = video loops

                    if (firstSampleTimeUs >= 0)
                    {
                        var expectedMs = (bufferInfo.PresentationTimeUs - firstSampleTimeUs) / 1000.0 / PlaybackSpeed;
                        var remainingMs = expectedMs - playbackTimer.ElapsedMilliseconds;
                        if (remainingMs > 2)
                        {
                            Thread.Sleep((int)remainingMs);
                        }
                    }
                }
                else if (outIdx == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    // Format changed — safe to ignore, codec handles it internally
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

    private static byte[]? ImageToJpeg(global::Android.Media.Image image, MediaFormat videoFormat)
    {
        try
        {
            int width  = image.Width;
            int height = image.Height;

            // Scale down if too large
            int outW = width, outH = height;
            if (width > MaxWidth)
            {
                outH = height * MaxWidth / width;
                outW = MaxWidth;
            }

            var format = image.Format;
            Bitmap? bmp = null;

            if (format == ImageFormatType.Yuv420888)
            {
                bmp = YuvToBitmap(image, width, height);
            }
            else if (format == ImageFormatType.Jpeg)
            {
                var plane = image.GetPlanes()?[0];
                if (plane == null) return null;
                var buf = plane.Buffer;
                if (buf == null) return null;
                var bytes = new byte[buf.Remaining()];
                buf.Get(bytes);
                // Already JPEG — just return it
                return bytes;
            }
            else
            {
                // Fallback: try to read first plane as raw bytes and wrap
                var plane = image.GetPlanes()?[0];
                if (plane == null) return null;
                var buf = plane.Buffer;
                if (buf == null) return null;
                var bytes = new byte[buf.Remaining()];
                buf.Get(bytes);
                bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            }

            if (bmp == null) return null;

            // Resize if needed
            if (outW != width || outH != height)
            {
                var scaled = Bitmap.CreateScaledBitmap(bmp, outW, outH, true)!;
                bmp.Recycle();
                bmp = scaled;
            }

            using var ms = new MemoryStream();
            bmp.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, ms);
            bmp.Recycle();
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DemoDecoder] ImageToJpeg failed");
            return null;
        }
    }

    private static Bitmap? YuvToBitmap(global::Android.Media.Image image, int width, int height)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return null;

            var yPlane  = planes[0].Buffer;
            var uPlane  = planes[1].Buffer;
            var vPlane  = planes[2].Buffer;
            if (yPlane == null || uPlane == null || vPlane == null) return null;

            int ySize = yPlane.Remaining();
            int uSize = uPlane.Remaining();
            int vSize = vPlane.Remaining();

            byte[] yBytes = new byte[ySize];
            byte[] uBytes = new byte[uSize];
            byte[] vBytes = new byte[vSize];
            yPlane.Get(yBytes);
            uPlane.Get(uBytes);
            vPlane.Get(vBytes);

            // Build NV21 (YCrCb) byte array for YuvImage
            byte[] nv21 = new byte[width * height * 3 / 2];
            Array.Copy(yBytes, 0, nv21, 0, ySize);

            int uvOffset = width * height;
            int uvLen = Math.Min(uSize, vSize);
            for (int i = 0; i < uvLen; i++)
            {
                nv21[uvOffset + i * 2]     = vBytes[i]; // Cr
                nv21[uvOffset + i * 2 + 1] = uBytes[i]; // Cb
            }

            var yuvImage = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
            using var ms = new MemoryStream();
            yuvImage.CompressToJpeg(new Rect(0, 0, width, height), 85, ms);
            var jpegBytes = ms.ToArray();
            return BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DemoDecoder] YUV→Bitmap failed");
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
