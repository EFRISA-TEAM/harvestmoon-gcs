/*
The MIT License (MIT)

Copyright (c) 2013, David Suarez
Modified for UDP Client mode - 2024

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
using System;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MavLinkNet
{
    /// <summary>
    /// UDP Client Transport for MAVLink - connects as a client to a remote endpoint
    /// Unlike MavLinkUdpTransport which acts as a server/listener, this class
    /// actively connects to a specified target IP and port.
    /// </summary>
    public class MavLinkUdpClientTransport : MavLinkGenericTransport
    {
        public int LocalPort = 0;  // 0 = Any available port
        public int TargetPort = 14550;
        public IPAddress TargetIpAddress = new IPAddress(new byte[] { 127, 0, 0, 1 });
        public int HeartBeatUpdateRateMs = 1000;

        /// <summary>
        /// Listen mode: bind LocalPort and accept MAVLink from ANY sender, learning the
        /// peer endpoint from the first packet.
        ///
        /// Wajib untuk UAV yang MENDORONG telemetri ke GCS (mis. Orange Pi drone
        /// companion -> GCS:14550). Di setup router/hotspot, IP UAV ditentukan DHCP
        /// sehingga GCS tidak bisa tahu IP/port tujuan lebih dulu.
        /// </summary>
        public bool ListenMode = false;

        private ConcurrentQueue<byte[]> mReceiveQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<UasMessage> mSendQueue = new ConcurrentQueue<UasMessage>();
        private AutoResetEvent mReceiveSignal = new AutoResetEvent(true);
        private AutoResetEvent mSendSignal = new AutoResetEvent(true);
        private MavLinkAsyncWalker mMavLink = new MavLinkAsyncWalker();
        private UdpClient mUdpClient;
        private IPEndPoint mRemoteEndPoint;
        private bool mIsActive = true;
        private Thread mReceiveThread;

        public override void Initialize()
        {
            InitializeMavLink();
            InitializeUdpClient();
            StartReceiveThread();
            StartSendThread();
        }

        public override void Dispose()
        {
            mIsActive = false;
            
            try
            {
                mUdpClient?.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP Client] Error closing socket: {ex.Message}");
            }
            
            mReceiveSignal.Set();
            mSendSignal.Set();
        }

        private void InitializeMavLink()
        {
            mMavLink.PacketReceived += HandlePacketReceived;
        }

        private void InitializeUdpClient()
        {
            int bindPort = LocalPort > 0 ? LocalPort : TargetPort;
            try
            {
                mUdpClient = new UdpClient(bindPort);
                mUdpClient.EnableBroadcast = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP Client] Bind to port {bindPort} failed ({ex.Message}), fallback to ephemeral port");
                mUdpClient = new UdpClient();
                mUdpClient.EnableBroadcast = true;
            }

            if (ListenMode || TargetIpAddress == null || TargetIpAddress.Equals(IPAddress.Any))
            {
                ListenMode = true;
                mRemoteEndPoint = null;
                Debug.WriteLine($"[UDP Client] Listen mode di port {bindPort} (menerima dari pengirim mana pun)");
                return;
            }

            // Set up the remote endpoint we want to communicate with
            mRemoteEndPoint = new IPEndPoint(TargetIpAddress, TargetPort);

            Debug.WriteLine($"[UDP Client] Initialized - Target: {mRemoteEndPoint}, LocalPort: {bindPort}");
        }

        private void StartReceiveThread()
        {
            mReceiveThread = new Thread(ReceiveLoop)
            {
                Name = "MavLink UDP Client Receive",
                IsBackground = true
            };
            mReceiveThread.Start();

            ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessReceiveQueue), null);
        }

        private void StartSendThread()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessSendQueue), null);
        }

        // __ Receive _________________________________________________________

        private void ReceiveLoop()
        {
            while (mIsActive)
            {
                try
                {
                    IPEndPoint remoteEp = null;
                    byte[] data = mUdpClient.Receive(ref remoteEp);

                    if (remoteEp != null)
                    {
                        // Pelajari/perbarui endpoint UAV dari paket yang masuk
                        mRemoteEndPoint = remoteEp;
                    }

                    if (data != null && data.Length > 0)
                    {
                        mReceiveQueue.Enqueue(data);
                        mReceiveSignal.Set();
                    }
                }
                catch (SocketException ex)
                {
                    if (mIsActive)
                    {
                        Debug.WriteLine($"[UDP Client] Socket error: {ex.Message}");
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Socket was closed, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UDP Client] Receive error: {ex.Message}");
                }
            }

            HandleReceptionEnded(this);
        }

        private void ProcessReceiveQueue(object state)
        {
            while (mIsActive)
            {
                byte[] buffer;

                if (mReceiveQueue.TryDequeue(out buffer))
                {
                    try
                    {
                        mMavLink.ProcessReceivedBytes(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[UDP Client] Error processing packet: {ex.Message}");
                    }
                }
                else
                {
                    // Empty queue, sleep until signalled
                    mReceiveSignal.WaitOne(1000); // Timeout to check mIsActive periodically
                }
            }
        }

        // __ Send ____________________________________________________________

        private void ProcessSendQueue(object state)
        {
            while (mIsActive)
            {
                UasMessage msg;

                if (mSendQueue.TryDequeue(out msg))
                {
                    SendMavlinkMessage(msg);
                }
                else
                {
                    // Queue is empty, sleep until signalled
                    mSendSignal.WaitOne(1000); // Timeout to check mIsActive periodically
                }
            }
        }

        private void SendMavlinkMessage(UasMessage msg)
        {
            try
            {
                if (mRemoteEndPoint == null)
                {
                    return;
                }

                byte[] buffer = mMavLink.SerializeMessage(msg, MavlinkSystemId, MavlinkComponentId, true);
                mUdpClient.Send(buffer, buffer.Length, mRemoteEndPoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP Client] Error sending message: {ex.Message}");
            }
        }

        // __ Heartbeat _______________________________________________________

        public void BeginHeartBeatLoop()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback(HeartBeatLoop), null);
        }

        private void HeartBeatLoop(object state)
        {
            while (mIsActive)
            {
                try
                {
                    foreach (UasMessage m in UavState.GetHeartBeatObjects())
                    {
                        SendMessage(m);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UDP Client] Error in heartbeat loop: {ex.Message}");
                }

                Thread.Sleep(HeartBeatUpdateRateMs);
            }
        }

        // __ API _____________________________________________________________

        public override void SendMessage(UasMessage msg)
        {
            mSendQueue.Enqueue(msg);

            // Signal send thread
            mSendSignal.Set();
        }

        /// <summary>
        /// Check if the UDP client is connected (has remote endpoint set)
        /// </summary>
        public bool IsConnected
        {
            get
            {
                try
                {
                    if (mUdpClient == null || mUdpClient.Client == null)
                    {
                        return false;
                    }

                    // Listen mode tidak pernah "Connected" secara socket, tapi tetap aktif.
                    if (ListenMode)
                    {
                        return true;
                    }

                    return mUdpClient.Client.Connected;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
