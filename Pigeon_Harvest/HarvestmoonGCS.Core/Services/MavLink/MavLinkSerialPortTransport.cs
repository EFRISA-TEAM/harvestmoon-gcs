/*
The MIT License (MIT)

Copyright (c) 2014, Håkon K. Olafsen

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
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;

namespace MavLinkNet
{
    public class MavLinkSerialPortTransport : MavLinkGenericTransport
    {
        public string SerialPortName = "COM9";
        public int BaudRate = 115200;
        public int HeartBeatUpdateRateMs = 1000;
        public WireProtocolVersion WireProtocolVersion;

        private ConcurrentQueue<byte[]> mReceiveQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<UasMessage> mSendQueue = new ConcurrentQueue<UasMessage>();
        
        private AutoResetEvent mReceiveSignal = new AutoResetEvent(true);
        private AutoResetEvent mSendSignal = new AutoResetEvent(true);

        private MavLinkAsyncWalker mMavLink = new MavLinkAsyncWalker();

        public new event OtherPacketReceivedDelegate OtherPacketReceived;

        private SerialPort mSerialPort;
        
        private bool mIsActive = true;

        public override void Initialize()
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] Initialize called: Port={SerialPortName}, Baud={BaudRate}");
            InitializeProtocolVersion(WireProtocolVersion);
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] Protocol version initialized");
            InitializeMavLink();
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] MavLink initialized");
            InitializeSerialPort(SerialPortName);
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] Serial port initialized");
        }

        public override void ProcessReceivedBytes(byte[] buffer, int offset, int count)
        {
            mMavLink.ProcessReceivedBytes(buffer, offset, count);
        }

        public override void Dispose()
        {
            mIsActive = false;

            mReceiveSignal.Set();
            mSendSignal.Set();

            mSerialPort.Close();
        }

        private void InitializeMavLink()
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeMavLink: Subscribing to mMavLink events...");
            Console.WriteLine($"[MavLinkSerialPortTransport] InitializeMavLink: Subscribing to mMavLink events...");
            
            mMavLink.PacketReceived += HandlePacketReceived;
            mMavLink.OtherPacketReceived += HandleOtherPacketReceived;
            
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeMavLink: Event subscriptions complete");
            Console.WriteLine($"[MavLinkSerialPortTransport] InitializeMavLink: Event subscriptions complete");
        }

        private void InitializeSerialPort(string serialPortName)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeSerialPort: Starting threads...");
            
            // Start receive queue worker
            ThreadPool.QueueUserWorkItem(
                new WaitCallback(ProcessReceiveQueue), null);

            // Start send queue worker
            ThreadPool.QueueUserWorkItem(
                new WaitCallback(ProcessSendQueue));

            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeSerialPort: Opening serial port {serialPortName} @ {BaudRate} baud...");
            mSerialPort = new SerialPort(serialPortName) { BaudRate = BaudRate };
            mSerialPort.DataReceived += DataReceived;
            mSerialPort.Open();
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeSerialPort: Serial port opened successfully");
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeSerialPort: IsOpen={mSerialPort.IsOpen}");
            
            // Start continuous read loop as backup (more reliable than DataReceived event)
            ThreadPool.QueueUserWorkItem(new WaitCallback(ContinuousReadLoop));
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] InitializeSerialPort: Continuous read loop started");
        }

        // __ Receive _________________________________________________________
        
        
        /// <summary>
        /// Continuous read loop - more reliable than DataReceived event
        /// This ensures data is read continuously even if DataReceived event fails
        /// </summary>
        private void ContinuousReadLoop(object state)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Started");
            
            while (mIsActive)
            {
                try
                {
                    if (mSerialPort == null || !mSerialPort.IsOpen)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Port closed, exiting loop");
                        break;
                    }
                    
                    int bytesToRead = mSerialPort.BytesToRead;
                    
                    if (bytesToRead > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: {bytesToRead} bytes available");
                        
                        if (bytesToRead > 4096)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Buffer overflow, discarding {bytesToRead} bytes");
                            mSerialPort.DiscardInBuffer();
                        }
                        else
                        {
                            byte[] buffer = new byte[bytesToRead];
                            int bytesRead = mSerialPort.Read(buffer, 0, bytesToRead);
                            
                            if (bytesRead > 0)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Read {bytesRead} bytes, processing...");
                                mMavLink.ProcessReceivedBytes(buffer, 0, bytesRead);
                            }
                        }
                    }
                    
                    // Small delay to prevent CPU spinning
                    Thread.Sleep(10);
                }
                catch (TimeoutException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Timeout (recoverable): {ex.Message}");
                    Thread.Sleep(100);
                }
                catch (InvalidOperationException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Invalid operation: {ex.Message}");
                    if (mSerialPort == null || !mSerialPort.IsOpen)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Port closed, exiting");
                        break;
                    }
                    Thread.Sleep(100);
                }
                catch (System.IO.IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: IO error (recoverable): {ex.Message}");
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Unexpected error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ContinuousReadLoop: Exited");
        }
        
        
        private void DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var serialPort = (SerialPort)sender;
                
                // Check if port is still open
                if (!serialPort.IsOpen)
                {
                    Console.WriteLine($"[MavLinkSerialPortTransport] Serial port is closed, skipping read");
                    return;
                }
                
                var bytesToRead = serialPort.BytesToRead;

                if (bytesToRead == 0)
                {
                    // No data available, this is normal
                    return;
                }

                Console.WriteLine($"[MavLinkSerialPortTransport] DataReceived: {bytesToRead} bytes available");

                byte[] buffer;

                if (bytesToRead > 4096)
                {
                    Console.WriteLine($"[MavLinkSerialPortTransport] WARNING: Buffer overflow, discarding {bytesToRead} bytes");
                    serialPort.DiscardInBuffer();
                    return;
                }
                else
                {
                    buffer = new byte[bytesToRead];
                    int bytesRead = serialPort.Read(buffer, 0, bytesToRead);
                    Console.WriteLine($"[MavLinkSerialPortTransport] Read {bytesRead} bytes from serial port");
                    
                    if (bytesRead > 0)
                    {
                        Console.WriteLine($"[MavLinkSerialPortTransport] Processing {bytesRead} bytes with MavLinkAsyncWalker...");
                        mMavLink.ProcessReceivedBytes(buffer, 0, bytesRead);
                    }
                }

                // Signal processReceive thread (for monitoring)
                mReceiveSignal.Set();
            }
            catch (TimeoutException ex)
            {
                // Timeout is recoverable, don't stop the transport
                Console.WriteLine($"[MavLinkSerialPortTransport] Timeout exception (recoverable): {ex.Message}");
                // DON'T set mIsActive = false here!
            }
            catch (InvalidOperationException ex)
            {
                // Port might be closed or in invalid state
                Console.WriteLine($"[MavLinkSerialPortTransport] Invalid operation (port may be closed): {ex.Message}");
                // Only stop if port is actually closed
                if (mSerialPort != null && !mSerialPort.IsOpen)
                {
                    Console.WriteLine($"[MavLinkSerialPortTransport] Port is closed, stopping transport");
                    mIsActive = false;
                }
            }
            catch (System.IO.IOException ex)
            {
                // IO error, might be recoverable
                Console.WriteLine($"[MavLinkSerialPortTransport] IO exception (recoverable): {ex.Message}");
                // DON'T set mIsActive = false here!
            }
            catch (Exception ex)
            {
                // Unexpected error, log but try to continue
                Console.WriteLine($"[MavLinkSerialPortTransport] Unexpected error (continuing): {ex.Message}\n{ex.StackTrace}");
                // DON'T set mIsActive = false here!
            }
        }

        private void ProcessReceiveQueue(object state)
        {
            while (true)
            {
                byte[] buffer;

                mReceiveSignal.WaitOne();

                if (!mIsActive) break;

                if (mReceiveQueue.TryDequeue(out buffer))
                {
                    mMavLink.ProcessReceivedBytes(buffer, 0, buffer.Length);
                }
            }

            HandleReceptionEnded(this);
        }


        // __ Send ____________________________________________________________


        private void ProcessSendQueue(object state)
        {
            while (true)
            {
                UasMessage msg;

                if (mSendQueue.TryDequeue(out msg))
                {
                    // Unplugging/bumping the USB cable makes the next Write() throw on this
                    // ThreadPool thread; previously nothing here caught it, which crashes the
                    // entire process (unhandled ThreadPool-thread exceptions are fatal in .NET
                    // and are NOT caught by Application.Current.UnhandledException). Mirror the
                    // same recoverable-vs-fatal handling already used by ContinuousReadLoop/DataReceived.
                    try
                    {
                        SendMavlinkMessage(msg);
                    }
                    catch (TimeoutException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ProcessSendQueue: Timeout (recoverable): {ex.Message}");
                    }
                    catch (System.IO.IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ProcessSendQueue: IO error — port likely unplugged: {ex.Message}");
                        if (mSerialPort == null || !mSerialPort.IsOpen)
                        {
                            mIsActive = false;
                            break;
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ProcessSendQueue: Port access lost — likely unplugged: {ex.Message}");
                        mIsActive = false;
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ProcessSendQueue: Invalid operation (port may be closed): {ex.Message}");
                        if (mSerialPort == null || !mSerialPort.IsOpen)
                        {
                            mIsActive = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MavLinkSerialPortTransport] ProcessSendQueue: Unexpected error (continuing): {ex.Message}");
                    }
                }
                else
                {
                    // Queue is empty, sleep until signalled
                    mSendSignal.WaitOne();

                    if (!mIsActive) break;
                }
            }

            HandleReceptionEnded(this);
        }

        private void SendMavlinkMessage(UasMessage msg)
        {
            byte[] buffer = mMavLink.SerializeMessage(msg, MavlinkSystemId, MavlinkComponentId, true);

            mSerialPort.Write(buffer, 0, buffer.Length);
        }


        // __ Heartbeat _______________________________________________________


        public void BeginHeartBeatLoop()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback(HeartBeatLoop), null);
        }

        private void HeartBeatLoop(object state)
        {
            while (true)
            {
                foreach (UasMessage m in UavState.GetHeartBeatObjects())
                {
                    SendMessage(m);
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

    }
}
