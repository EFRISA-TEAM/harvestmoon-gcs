using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;

namespace HarvestmoonGCS.Controls;

public sealed class VideoDetectionOverlay
{
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Video stream control with SkiaSharp rendering for high-performance video display.
/// </summary>
public sealed partial class VideoStreamControl : UserControl
{
    private SKBitmap _currentFrame;
    private readonly object _frameLock = new object();
    private readonly Stopwatch _fpsStopwatch = new Stopwatch();
    private int _frameCount;
    private double _currentFps;
    private bool _showFps;
    private bool _isStreaming;
    private List<VideoDetectionOverlay> _detectionOverlays = new();
    // Frame drop: skip decode jika frame sebelumnya belum selesai di-render
    private volatile bool _framePending;
    // High-quality rendering mode: use bilinear for demo video, nearest-neighbour for live stream
    private bool _highQualityMode = false;

    public VideoStreamControl()
    {
        this.InitializeComponent();
        _fpsStopwatch.Start();
        
        BackgroundImage.Visibility = Visibility.Visible;
        VideoCanvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Sets the background image source (e.g., logo shown when not streaming).
    /// </summary>
    public void SetBackgroundImage(string imageUri)
    {
        try
        {
            BackgroundImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imageUri));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error setting background image: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets or sets whether to show the FPS counter.
    /// </summary>
    public bool ShowFps
    {
        get => _showFps;
        set
        {
            _showFps = value;
            FpsCounter.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Gets the current frames per second.
    /// </summary>
    public double CurrentFps => _currentFps;

    /// <summary>
    /// Updates the video frame from byte array (JPEG encoded).
    /// Decode dilakukan di background thread — UI thread hanya Invalidate().
    /// Frame drop otomatis jika frame sebelumnya belum selesai di-render.
    /// </summary>
    public void UpdateFrame(byte[] frameData)
    {
        if (frameData == null || frameData.Length == 0)
            return;

        // Jika frame sebelumnya masih menunggu di antrian UI, drop frame ini
        if (_framePending)
            return;

        _framePending = true;

        try
        {
            // Decode JPEG di background thread (bukan UI thread)
            using var stream = new MemoryStream(frameData);
            var newBitmap = SKBitmap.Decode(stream);

            if (newBitmap != null)
            {
                lock (_frameLock)
                {
                    _currentFrame?.Dispose();
                    _currentFrame = newBitmap;
                }

                UpdateFpsCounter();

                var enqueued = DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isStreaming)
                    {
                        _isStreaming = true;
                        BackgroundImage.Visibility = Visibility.Collapsed;
                        VideoCanvas.Visibility = Visibility.Visible;

                    }
                    VideoCanvas.Invalidate();
                    _framePending = false;
                });
                if (!enqueued)
                {
                    _framePending = false;
                }
            }
            else
            {
                _framePending = false;
            }
        }
        catch (Exception ex)
        {
            _framePending = false;
            Debug.WriteLine($"Error updating frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the video frame from SKBitmap.
    /// </summary>
    public void UpdateFrame(SKBitmap bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = bitmap.Copy();
        }

        UpdateFpsCounter();

        DispatcherQueue.TryEnqueue(() =>
        {
            VideoCanvas.Invalidate();
        });
    }

    /// <summary>
    /// Shows a status message (and optional loading spinner) over the video/background area.
    /// </summary>
    public void ShowStatus(string message, bool showLoading = false)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = message ?? string.Empty;
            StatusLoadingRing.IsActive = showLoading;
            StatusLoadingRing.Visibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
            StatusOverlay.Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Hides the status overlay shown by <see cref="ShowStatus"/>.
    /// </summary>
    public void HideOverlay()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusLoadingRing.IsActive = false;
            StatusOverlay.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Switches between high-quality bilinear rendering (for demo video)
    /// and fast nearest-neighbour rendering (for live streams).
    /// </summary>
    public void SetHighQualityMode(bool enabled)
    {
        _highQualityMode = enabled;
    }

    /// <summary>
    /// Force the video canvas to be visible and repaint the last cached frame.
    /// Useful when the host page returns from a navigation stack and the SkiaSharp
    /// canvas needs to re-raise PaintSurface to draw the in-memory frame.
    /// </summary>
    public void EnsureStreamingVisible()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            lock (_frameLock)
            {
                if (_currentFrame != null)
                {
                    _isStreaming = true;
                    BackgroundImage.Visibility = Visibility.Collapsed;
                    VideoCanvas.Visibility = Visibility.Visible;
                    VideoCanvas.Invalidate();
                }
            }
        });
    }

    public void SetDetectionOverlays(IEnumerable<VideoDetectionOverlay> overlays)
    {
        lock (_frameLock)
        {
            _detectionOverlays = overlays.ToList();
        }
        DispatcherQueue.TryEnqueue(() => VideoCanvas.Invalidate());
    }

    /// <summary>
    /// Clears the current frame and shows background image.
    /// </summary>
    public void ClearFrame()
    {
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }

        _isStreaming = false;

        DispatcherQueue.TryEnqueue(() =>
        {
            VideoCanvas.Invalidate();
            VideoCanvas.Visibility = Visibility.Collapsed;
            BackgroundImage.Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Gets the current frame as SKBitmap (for screenshot).
    /// </summary>
    public SKBitmap GetCurrentFrame()
    {
        lock (_frameLock)
        {
            return _currentFrame?.Copy();
        }
    }

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        lock (_frameLock)
        {
            if (_currentFrame != null)
            {
                // Uniform: pertahankan aspect ratio asli, center di canvas
                var canvasWidth = e.Info.Width;
                var canvasHeight = e.Info.Height;
                var frameWidth = _currentFrame.Width;
                var frameHeight = _currentFrame.Height;

                var scaleX = (float)canvasWidth / frameWidth;
                var scaleY = (float)canvasHeight / frameHeight;
                var scale = Math.Min(scaleX, scaleY); // Uniform — no distortion

                var scaledWidth = frameWidth * scale;
                var scaledHeight = frameHeight * scale;

                var x = (canvasWidth - scaledWidth) / 2;
                var y = (canvasHeight - scaledHeight) / 2;

                var destRect = new SKRect(x, y, x + scaledWidth, y + scaledHeight);

                // None = nearest-neighbour — paling cepat untuk live stream
                // High/Medium = bilinear — digunakan untuk demo video agar jernih
                using var paint = new SKPaint
                {
                    FilterQuality = _highQualityMode ? SKFilterQuality.High : SKFilterQuality.None,
                    IsAntialias = _highQualityMode
                };

                canvas.DrawBitmap(_currentFrame, destRect, paint);

                DrawDetectionOverlays(canvas, destRect, frameWidth, frameHeight);
            }
        }
    }

    /// <summary>
    /// Draw detection overlays after UniformToFill crop.
    /// srcRect = area of original frame that was drawn; coordinates remapped accordingly.
    /// </summary>
    private void DrawDetectionOverlaysCropped(SKCanvas canvas, SKRect destRect, SKRect srcRect, int frameWidth, int frameHeight)
    {
        if (_detectionOverlays.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
            return;

        // Scale from srcRect pixel space to destRect canvas space
        var scaleX = destRect.Width / srcRect.Width;
        var scaleY = destRect.Height / srcRect.Height;

        using var boxPaint = new SKPaint { Color = SKColors.LimeGreen, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var labelBgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 190), Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { Color = SKColors.White, TextSize = 18, IsAntialias = true };

        foreach (var detection in _detectionOverlays)
        {
            // Remap from full-frame coords to cropped canvas coords
            var left   = (detection.X - srcRect.Left) * scaleX + destRect.Left;
            var top    = (detection.Y - srcRect.Top)  * scaleY + destRect.Top;
            var right  = left + detection.Width  * scaleX;
            var bottom = top  + detection.Height * scaleY;

            // Skip boxes completely outside visible area
            if (right < destRect.Left || left > destRect.Right || bottom < destRect.Top || top > destRect.Bottom)
                continue;

            var rect = new SKRect(left, top, right, bottom);
            canvas.DrawRect(rect, boxPaint);

            var label = $"{detection.Label} {detection.Confidence:P0}";
            var labelWidth = textPaint.MeasureText(label) + 10;
            var labelRect = new SKRect(left, Math.Max(destRect.Top, top - 24), left + labelWidth, Math.Max(destRect.Top + 22, top));
            canvas.DrawRect(labelRect, labelBgPaint);
            canvas.DrawText(label, left + 5, labelRect.Bottom - 5, textPaint);
        }
    }

    private void DrawDetectionOverlays(SKCanvas canvas, SKRect destRect, int frameWidth, int frameHeight)
    {
        if (_detectionOverlays.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        var scaleX = destRect.Width / frameWidth;
        var scaleY = destRect.Height / frameHeight;
        using var boxPaint = new SKPaint { Color = SKColors.LimeGreen, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var labelBgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 190), Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { Color = SKColors.White, TextSize = 18, IsAntialias = true };

        foreach (var detection in _detectionOverlays)
        {
            var left = destRect.Left + detection.X * scaleX;
            var top = destRect.Top + detection.Y * scaleY;
            var right = left + detection.Width * scaleX;
            var bottom = top + detection.Height * scaleY;
            var rect = new SKRect(left, top, right, bottom);

            canvas.DrawRect(rect, boxPaint);
            var label = $"{detection.Label} {detection.Confidence:P0}";
            var labelWidth = textPaint.MeasureText(label) + 10;
            var labelRect = new SKRect(left, Math.Max(destRect.Top, top - 24), left + labelWidth, Math.Max(destRect.Top + 22, top));
            canvas.DrawRect(labelRect, labelBgPaint);
            canvas.DrawText(label, left + 5, labelRect.Bottom - 5, textPaint);
        }
    }

    private void UpdateFpsCounter()
    {
        _frameCount++;

        if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
        {
            _currentFps = _frameCount / (_fpsStopwatch.ElapsedMilliseconds / 1000.0);
            _frameCount = 0;
            _fpsStopwatch.Restart();

            if (_showFps)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    FpsCounter.Text = $"FPS: {_currentFps:F1}";
                });
            }
        }
    }

    /// <summary>
    /// Saves the current frame to a file.
    /// </summary>
    public bool SaveCurrentFrame(string filename)
    {
        lock (_frameLock)
        {
            if (_currentFrame == null)
            {
                return false;
            }

            try
            {
                using var image = SKImage.FromBitmap(_currentFrame);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
                using var stream = File.OpenWrite(filename);
                data.SaveTo(stream);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving frame: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
    }
}
