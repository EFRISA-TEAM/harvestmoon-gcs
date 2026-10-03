using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Services;
using HarvestmoonGCS.ViewModels;
using Xunit;

namespace HarvestmoonGCS.Tests.Services;

public class LoRaViewModelTests
{
    [Fact]
    public void RawTelemetryPayload_UpdatesMatchingNodeAndStatistics()
    {
        var loraService = new FakeLoRaService();
        var viewModel = CreateViewModel(loraService);

        var payload = "\u001b[32m[debug] {\"id\":\"TX2\",\"temp\":42.5,\"hum\":33.2,\"rssi\":-140,\"snr\":6.2,\"lat\":-6.2,\"lng\":106.8,\"alt\":12.3,\"sat\":9,\"speed\":15.5,\"vibration\":123,\"packet\":77}\u001b[0m";

        loraService.RaiseData(payload);

        var node = Assert.Single(viewModel.Nodes, n => n.NodeId == 2);
        Assert.True(node.IsOnline);
        Assert.Equal(42.5, node.Temperature, 1);
        Assert.Equal(33.2, node.Humidity, 1);
        Assert.Equal(sbyte.MinValue, node.RSSI);
        Assert.Equal(6.2, node.SNR, 1);
        Assert.Equal(-6.2, node.Latitude, 6);
        Assert.Equal(106.8, node.Longitude, 6);
        Assert.Equal(12.3, node.Altitude, 1);
        Assert.Equal(9, node.Satellites);
        Assert.Equal(15.5, node.Speed, 1);
        Assert.Equal(123, node.Vibration);
        Assert.Equal(77, node.PacketNumber);
        Assert.Equal(1, viewModel.PacketCount);
        Assert.Equal("1/5", viewModel.OnlineNodesText);
        Assert.Contains("TX2", viewModel.EventLog);
    }

    [Fact]
    public void TextPayload_UpdatesSenderLinkAndCountsPacket()
    {
        var loraService = new FakeLoRaService();
        var viewModel = CreateViewModel(loraService);

        loraService.RaiseData("{\"type\":\"text\",\"from\":\"TX3\",\"text\":\"hello gcs\",\"rssi\":-87,\"snr\":4.5}");

        var node = Assert.Single(viewModel.Nodes, n => n.NodeId == 3);
        Assert.True(node.IsOnline);
        Assert.Equal(-87, node.RSSI);
        Assert.Equal(4.5, node.SNR, 1);
        Assert.Equal(1, viewModel.PacketCount);
        Assert.Contains("hello gcs", viewModel.EventLog);
    }

    [Fact]
    public void RawDebugLine_IsDiscardedWithoutChangingStatistics()
    {
        var loraService = new FakeLoRaService();
        var viewModel = CreateViewModel(loraService);
        viewModel.SelectedDevice = new LoRaDevice
        {
            Name = "Raw",
            PortName = "COM1",
            SupportsAtCommands = false
        };

        loraService.RaiseData("Meshtastic debug line without json");

        Assert.Equal(0, viewModel.PacketCount);
        Assert.Equal("--", viewModel.LastUpdateText);
        Assert.DoesNotContain("Meshtastic debug", viewModel.EventLog);
    }

    private static LoRaViewModel CreateViewModel(FakeLoRaService loraService)
    {
        return new LoRaViewModel(new ImmediateDispatcherService(), loraService);
    }

    private sealed class ImmediateDispatcherService : IDispatcherService
    {
        public bool IsUIThread => true;

        public void Enqueue(Action action) => action();

        public Task RunOnUIThreadAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLoRaService : ILoRaService
    {
        public bool IsConnected { get; private set; }

        public event EventHandler<LoRaDevice>? DeviceDiscovered;
        public event EventHandler<bool>? ConnectionStatusChanged;
        public event EventHandler<byte[]>? DataReceived;

        public Task<List<LoRaDevice>> ScanDevicesAsync()
        {
            return Task.FromResult(new List<LoRaDevice>());
        }

        public Task<bool> ConnectAsync(LoRaDevice device)
        {
            IsConnected = true;
            ConnectionStatusChanged?.Invoke(this, true);
            DeviceDiscovered?.Invoke(this, device);
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        public Task<bool> SendDataAsync(byte[] data) => Task.FromResult(IsConnected);

        public Task<bool> ConfigureAsync(LoRaConfig config) => Task.FromResult(IsConnected);

        public void RaiseData(string line)
        {
            DataReceived?.Invoke(this, Encoding.UTF8.GetBytes(line));
        }
    }
}
