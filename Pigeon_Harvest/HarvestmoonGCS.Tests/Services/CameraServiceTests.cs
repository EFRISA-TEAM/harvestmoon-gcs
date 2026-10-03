using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Tests.Services;

/// <summary>
/// Tests for camera service functionality
/// Note: These tests use a mock implementation since CameraService
/// has platform-specific dependencies
/// </summary>
public class CameraServiceTests
{
    private class MockCameraService : ICameraService
    {
        public bool IsStreaming { get; private set; }
        public bool IsRecording { get; private set; }
        public bool IsClassificationStream { get; private set; }
        public bool IsInitialized { get; private set; }
        public string CurrentSource { get; private set; } = "";
        public string LastPictureFilename { get; private set; } = "";
        public string LastRecordingFilename { get; private set; } = "";
        public CameraControlCommand LastCommand { get; private set; }
        public float LastCommandValue { get; private set; }

        public event EventHandler<byte[]>? FrameReceived;
        public event EventHandler<bool>? StreamingStatusChanged;
        public event EventHandler<bool>? RecordingStatusChanged;
        public event EventHandler<string>? ConnectionError;

        public Task InitializeAsync()
        {
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public Task<List<CameraSource>> GetAvailableSourcesAsync()
        {
            var sources = new List<CameraSource>
            {
                new CameraSource { Id = "0", Name = "Built-in Camera" },
                new CameraSource { Id = "1", Name = "USB Camera" }
            };
            return Task.FromResult(sources);
        }

        public Task<bool> StartCameraAsync(string source)
        {
            if (!IsInitialized)
            {
                ConnectionError?.Invoke(this, "Camera not initialized");
                return Task.FromResult(false);
            }

            CurrentSource = source;
            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);
            
            // Simulate frame capture
            var frame = new byte[1920 * 1080 * 3];
            FrameReceived?.Invoke(this, frame);
            
            return Task.FromResult(true);
        }

        public Task<bool> StartHsvStreamAsync(string source, string? modelPath = null,
            float maxFps = 15f, bool showOverlay = true, bool demo = true, float playbackRate = 1.0f,
            bool yoloEnabled = false)
            => StartCameraAsync(source);

        public Task StopCameraAsync()
        {
            IsStreaming = false;
            StreamingStatusChanged?.Invoke(this, false);
            CurrentSource = "";
            return Task.CompletedTask;
        }

        public Task<bool> TakePictureAsync(string filename = null)
        {
            if (!IsStreaming)
                return Task.FromResult(false);

            LastPictureFilename = filename ?? $"picture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            return Task.FromResult(true);
        }

        public Task<bool> StartRecordingAsync(string filename = null)
        {
            if (!IsStreaming)
                return Task.FromResult(false);

            if (IsRecording)
                return Task.FromResult(false);

            LastRecordingFilename = filename ?? $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            IsRecording = true;
            RecordingStatusChanged?.Invoke(this, true);
            return Task.FromResult(true);
        }

        public Task<bool> StopRecordingAsync()
        {
            if (!IsRecording)
                return Task.FromResult(false);

            IsRecording = false;
            RecordingStatusChanged?.Invoke(this, false);
            return Task.FromResult(true);
        }

        public Task SendCameraControlAsync(CameraControlCommand command, float value)
        {
            LastCommand = command;
            LastCommandValue = value;
            return Task.CompletedTask;
        }

        public Task<bool> StartHsvStreamAsync(
            string source,
            string? modelPath = null,
            float maxFps = 15f,
            bool showOverlay = true,
            bool demo = true,
            float playbackRate = 1.0f)
        {
            if (!IsInitialized)
            {
                ConnectionError?.Invoke(this, "Camera not initialized");
                return Task.FromResult(false);
            }

            CurrentSource = source;
            IsStreaming = true;
            IsClassificationStream = true;
            StreamingStatusChanged?.Invoke(this, true);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task InitializeAsync_ShouldInitializeCamera()
    {
        // Arrange
        var service = new MockCameraService();

        // Act
        await service.InitializeAsync();

        // Assert
        Assert.True(service.IsInitialized);
    }

    [Fact]
    public async Task GetAvailableSourcesAsync_ShouldReturnSources()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();

        // Act
        var sources = await service.GetAvailableSourcesAsync();

        // Assert
        Assert.NotNull(sources);
        Assert.NotEmpty(sources);
    }

    [Fact]
    public async Task StartCameraAsync_WithoutInitialize_ShouldFail()
    {
        // Arrange
        var service = new MockCameraService();
        bool errorReceived = false;
        service.ConnectionError += (s, e) => errorReceived = true;

        // Act
        var result = await service.StartCameraAsync("0");

        // Assert
        Assert.False(result);
        Assert.True(errorReceived);
    }

    [Fact]
    public async Task StartCameraAsync_ShouldStartStreaming()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        bool streamingChanged = false;
        service.StreamingStatusChanged += (s, e) => streamingChanged = e;

        // Act
        var result = await service.StartCameraAsync("0");

        // Assert
        Assert.True(result);
        Assert.True(service.IsStreaming);
        Assert.True(streamingChanged);
        Assert.Equal("0", service.CurrentSource);
    }

    [Fact]
    public async Task StartCameraAsync_ShouldEmitFrames()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        byte[]? receivedFrame = null;
        service.FrameReceived += (s, frame) => receivedFrame = frame;

        // Act
        await service.StartCameraAsync("0");

        // Assert
        Assert.NotNull(receivedFrame);
        Assert.NotEmpty(receivedFrame);
    }

    [Fact]
    public async Task StopCameraAsync_ShouldStopStreaming()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");
        bool streamingStopped = false;
        service.StreamingStatusChanged += (s, e) => streamingStopped = !e;

        // Act
        await service.StopCameraAsync();

        // Assert
        Assert.False(service.IsStreaming);
        Assert.True(streamingStopped);
        Assert.Empty(service.CurrentSource);
    }

    [Fact]
    public async Task TakePictureAsync_WithoutStreaming_ShouldFail()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();

        // Act
        var result = await service.TakePictureAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task TakePictureAsync_WhileStreaming_ShouldSucceed()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");

        // Act
        var result = await service.TakePictureAsync("test.jpg");

        // Assert
        Assert.True(result);
        Assert.Equal("test.jpg", service.LastPictureFilename);
    }

    [Fact]
    public async Task TakePictureAsync_WithoutFilename_ShouldGenerateFilename()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");

        // Act
        var result = await service.TakePictureAsync();

        // Assert
        Assert.True(result);
        Assert.NotEmpty(service.LastPictureFilename);
        Assert.Contains("picture_", service.LastPictureFilename);
    }

    [Fact]
    public async Task StartRecordingAsync_WithoutStreaming_ShouldFail()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();

        // Act
        var result = await service.StartRecordingAsync();

        // Assert
        Assert.False(result);
        Assert.False(service.IsRecording);
    }

    [Fact]
    public async Task StartRecordingAsync_WhileStreaming_ShouldSucceed()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");
        bool recordingStarted = false;
        service.RecordingStatusChanged += (s, e) => recordingStarted = e;

        // Act
        var result = await service.StartRecordingAsync("test.mp4");

        // Assert
        Assert.True(result);
        Assert.True(service.IsRecording);
        Assert.True(recordingStarted);
        Assert.Equal("test.mp4", service.LastRecordingFilename);
    }

    [Fact]
    public async Task StartRecordingAsync_WhileAlreadyRecording_ShouldFail()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");
        await service.StartRecordingAsync("first.mp4");

        // Act
        var result = await service.StartRecordingAsync("second.mp4");

        // Assert
        Assert.False(result);
        Assert.Equal("first.mp4", service.LastRecordingFilename);
    }

    [Fact]
    public async Task StopRecordingAsync_WhileRecording_ShouldSucceed()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");
        await service.StartRecordingAsync();
        bool recordingStopped = false;
        service.RecordingStatusChanged += (s, e) => recordingStopped = !e;

        // Act
        var result = await service.StopRecordingAsync();

        // Assert
        Assert.True(result);
        Assert.False(service.IsRecording);
        Assert.True(recordingStopped);
    }

    [Fact]
    public async Task StopRecordingAsync_WithoutRecording_ShouldFail()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();
        await service.StartCameraAsync("0");

        // Act
        var result = await service.StopRecordingAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SendCameraControlAsync_ShouldSendCommand()
    {
        // Arrange
        var service = new MockCameraService();
        await service.InitializeAsync();

        // Act
        await service.SendCameraControlAsync(CameraControlCommand.Zoom, 2.0f);

        // Assert
        Assert.Equal(CameraControlCommand.Zoom, service.LastCommand);
        Assert.Equal(2.0f, service.LastCommandValue);
    }

    [Fact]
    public async Task CameraStateTransitions_ShouldFollowCorrectSequence()
    {
        // Arrange
        var service = new MockCameraService();

        // Act & Assert - Initialize
        await service.InitializeAsync();
        Assert.True(service.IsInitialized);
        Assert.False(service.IsStreaming);
        Assert.False(service.IsRecording);

        // Act & Assert - Start streaming
        await service.StartCameraAsync("0");
        Assert.True(service.IsStreaming);
        Assert.False(service.IsRecording);

        // Act & Assert - Start recording
        await service.StartRecordingAsync();
        Assert.True(service.IsStreaming);
        Assert.True(service.IsRecording);

        // Act & Assert - Stop recording
        await service.StopRecordingAsync();
        Assert.True(service.IsStreaming);
        Assert.False(service.IsRecording);

        // Act & Assert - Stop streaming
        await service.StopCameraAsync();
        Assert.False(service.IsStreaming);
        Assert.False(service.IsRecording);
    }
}
