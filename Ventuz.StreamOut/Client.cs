//-------------------------------------------------------------------
//
// Ventuz Encoded Stream Out Receiver example
// (C) 2024 Ventuz Technology, licensed under the MIT license
//
//-------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Ventuz.StreamOut
{
    /// <summary>
    /// Parameters for Touch* commands   
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TouchPara
    {
        /// <summary>numerical id. Must be unique per touch (eg. finger # or something incremental)</summary>
        public uint id;
        /// <summary>x coordinate in pixels from the left side of the viewport</summary>
        public int x;
        /// <summary>y coordinate in pixels from the upper side of the viewport</summary>
        public int y;
    };

    /// <summary>
    /// Parameters for the MouseMove and MouseWheel commands
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseXYPara
    {
        /// <summary>x coordinate in pixels from the left side of the viewport, or horizontal wheel delta</summary>
        public int x;
        /// <summary>y coordinate in pixels from the upper side of the viewport, or vertical wheel delta</summary>
        public int y;
    }

    /// <summary>
    /// Parameters for the MouseButtons command
    /// </summary>
    [Flags]
    public enum MouseButtonEnum : uint
    {
        None = 0x00,
        Left = 0x01,
        Right = 0x02,
        Middle = 0x04,
        X1 = 0x08,
        X2 = 0x10,
    };

    /// <summary>
    /// Parameters for the SetEnodePara PipeCommand
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EncodePara
    {
        /// <summary>
        /// Rate control mode
        /// </summary>
        public enum RateControlMode : uint
        {
            /// <summary>Constant QP mode; BitrateOrQP is QP (0..51)</summary>
            ConstQP = 0,
            /// <summary>Constant bit rate mode; BitrateOrQP is rate in kBits/s</summary>
            ConstRate = 1,
        }

        /// <summary>Rate control mode</summary>
        public RateControlMode Mode;

        /// <summary>Rate control parameter (see RateControl mode for details)</summary>
        public uint BitrateOrQP;
    };

    /// <summary>
    /// Stream information header
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamHeader
    {
        /// <summary>Current header version</summary>
        public static readonly int VERSION = 2;

        /// <summary>Header version, should be == VERSION</summary>
        public uint hdrVersion;

        /// <summary>used video codec, currently 'h264' or 'hevc'</summary>
        public uint videoCodecFourCC;

        /// <summary>video width in pixels</summary>
        public uint videoWidth;

        /// <summary>video height in pixels</summary>
        public uint videoHeight;

        /// <summary>frame rate numerator</summary>
        public uint videoFrameRateNum;

        /// <summary>frame rate denominator</summary>
        public uint videoFrameRateDen;

        /// <summary>used audio codec, currently 'pc16' aka 16 bit signed little endian PCM</summary>
        public uint audioCodecFourCC;

        /// <summary>audio sample rate in Hz, currently fixed at 48000</summary>
        public uint audioRate;

        /// <summary>audio channel count, currently fixed at 2</summary>
        public uint audioChannels;
    }

    /// <summary>
    /// Client for Ventuz Stream Out pipes
    /// Fully async - make sure you marshal all callbacks to proper threads if necessary
    /// </summary>
    public sealed class Client : IDisposable
    {
       
        /// <summary>Is the client connected to Ventuz?</summary>
        public bool IsConnected => pipe?.IsConnected ?? false;

        /// <summary>Header information of current stream</summary>
        public StreamHeader Header { get; private set; }

        /// <summary>Fires when client has connected to Ventuz</summary>
        public event Action? Connected;

        /// <summary>Fires when client has disconnected from Ventuz</summary>
        public event Action? Disconnected;

        /// <summary>
        /// Fires when there was an error with the connection.
        /// After this the client will be in an invalid state and need to be disposed and recreated.
        /// </summary>
        public event Action<Exception>? ConnectionError;

        /// <summary>
        /// Callback for video payload data. 
        /// The bool parameter specifies if the current frame is an IDR frame.
        /// </summary>
        public event Action<Memory<byte>, bool>? VideoFrameReceived;

        /// <summary>Callback for audio payload data</summary>
        public event Action<Memory<byte>>? AudioFrameReceived;

        /// <summary>
        /// Creates a new client for the given output (0 is output A, 1 is B, etc)
        /// </summary>
        public Client(int outputno = 0)
        {
            pipeName = "VentuzOut" + (char)('A' + outputno);            
        }

        public void Run()
        {
            Task.Run(RunAsync);
        }

        /// <summary>
        /// Dispose of the client.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Sends a "Request IDR Frame" command to Ventuz</summary>
        public void SendRequestIDRFrame() => SendCommand(PipeCommand.RequestIDRFrame);

        /// <summary>Sends a "Touch Begin" command to Ventuz</summary>
        public void SendTouchBegin(TouchPara para) => SendCommand(PipeCommand.TouchBegin, para);

        /// <summary>Sends a "Touch Move" command to Ventuz</summary>
        public void SendTouchMove(TouchPara para) => SendCommand(PipeCommand.TouchMove, para);

        /// <summary>Sends a "Touch End" command to Ventuz</summary>
        public void SendTouchEnd(TouchPara para) => SendCommand(PipeCommand.TouchEnd, para);

        /// <summary>Sends a "Touch Cancel" command to Ventuz</summary>
        public void SendTouchCancel(TouchPara para) => SendCommand(PipeCommand.TouchCancel, para);

        /// <summary>Sends a "Key" command to Ventuz</summary>
        public void SendKey(uint code) => SendCommand(PipeCommand.Char, code);

        /// <summary>Sends a "Key Down" command to Ventuz</summary>
        public void SendKeyDown(uint vk) => SendCommand(PipeCommand.KeyDown, vk);

        /// <summary>Sends a "Key Down" command to Ventuz</summary>
        public void SendKeyUp(uint vk) => SendCommand(PipeCommand.KeyUp, vk);

        /// <summary>Sends a "Mouse Position" command to Ventuz</summary>
        public void SendMouseMove(MouseXYPara para) => SendCommand(PipeCommand.MouseMove, para);

        /// <summary>Sends a "Mouse Wheel" command to Ventuz</summary>
        public void SendMouseWheel(MouseXYPara para) => SendCommand(PipeCommand.MouseWheel, para);

        /// <summary>Sends a "Mouse Buttons" command to Ventuz</summary>
        public void SendMouseButtons(MouseButtonEnum buttons) => SendCommand(PipeCommand.MouseButtons, buttons);

        /// <summary>
        /// Make uint FourCC from four characters
        /// </summary>
        public static uint FourCC(char a, char b, char c, char d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | ((uint)d);


        // The data coming from the pipe is organized in chunks. every chunk starts with this, followed by the chunk data
        [StructLayout(LayoutKind.Sequential)]
        private struct ChunkHeader
        {
            public uint fourCC;            // chunk type (four character code)
            public int size;               // size of chunk data
        }

        // Frame header, gets sent every frame, followed by video and then audio data (fhdr chunk)
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameHeader
        {
            [Flags]
            public enum FrameFlags : uint
            {
                IDR_FRAME = 0x01,           // frame is IDR frame (aka key frame / stream restart/sync point)
            }

            public uint frameIndex;         // frame index. If this isn't perfectly contiguous there was a frame drop in Ventuz
            public FrameFlags flags;        // flags, see above
        }

        // Commands to send back to the encoder. 
        private enum PipeCommand : byte
        {
            Nop = 0x00, // Do nothing

            RequestIDRFrame = 0x01, // Request an IDR frame. it may take a couple of frames until the IDR frame arrives due to latency reasons.

            TouchBegin = 0x10, // Start a touch. Must be followed by a TouchPara structure
            TouchMove = 0x11, // Move a touch. Must be followed by a TouchPara structure        
            TouchEnd = 0x12, // Release a touch. Must be followed by a TouchPara structure        
            TouchCancel = 0x13, // Cancal a touch if possible. Must be followed by a TouchPara structure

            Char = 0x20, // Send a keystroke. Must be followed by a KeyPara structure
            KeyDown = 0x21, // Send a key down event
            KeyUp = 0x22, // Send a key down event

            MouseMove = 0x28, // Mouse positon update. Must be followed by a MouseXYPara structure
            MouseButtons = 0x29, // Mouse buttons update. Must be followed by a MouseButtonsPara structure
            MouseWheel = 0x2a, // Mouse wheel update. Must be followed by a MouseXYPara structure
                               // NOTE: Currently only the Y value of the wheel is supported

            SetEncodePara = 0x30, // Send encoder parameters. Must be followed by an EncodePara structure
        }

        private NamedPipeClientStream? pipe;
        private readonly CancellationTokenSource cts = new();
        private readonly byte[] buffer = new byte[256];
        private readonly string pipeName;
        private readonly ConcurrentQueue<byte[]> commands = [];
        private bool disposed;

        private void Dispose(bool disposing)
        {
            if (disposed) return;

            cts.Cancel();
            if (disposing)
            {
                pipe?.Dispose();
            }
            disposed = true;
        }

        ~Client() { Dispose(false); }

        private async Task<byte[]> ReadBytesAsync(int size)
        {
            var mem = new byte[size];
            await pipe!.ReadExactlyAsync(mem, cts.Token);
            return mem;
        }

        private async Task<T> ReadStructAsync<T>(int size = 0) where T : struct
        {
            var tsize = Marshal.SizeOf(typeof(T));
            await pipe!.ReadExactlyAsync(buffer, 0, (size > 0 ? size : tsize), cts.Token);
            return MemoryMarshal.Cast<byte, T>(buffer.AsSpan()[0..tsize])[0];
        }

        private void SendCommand(PipeCommand cmd)
        {
            if (IsConnected) commands.Enqueue([(byte)cmd]);
        }

        private void SendCommand<T>(PipeCommand cmd, in T para) where T : struct
        {
            if (IsConnected)
            {
                var srcspan = MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateReadOnlySpan(in para, 1));
                commands.Enqueue([(byte)cmd, .. srcspan]);
            }
        }

        private async Task RunAsync()
        {
            var ct = cts.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (pipe == null)
                    {
                        pipe = new NamedPipeClientStream(pipeName);
                    }
                    else if (!pipe.IsConnected)
                    {
                        Debug.WriteLine($"connecting to {pipeName}");

                        try
                        {
                            await pipe.ConnectAsync(TimeSpan.FromSeconds(1), ct);
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }

                        var chunk = await ReadStructAsync<ChunkHeader>();
                        if (chunk.fourCC != FourCC('V', 'V', 'S', 'P'))
                            throw new Exception("invalid header from pipe");

                        // get header
                        Header = await ReadStructAsync<StreamHeader>(chunk.size);
                        if (Header.hdrVersion != StreamHeader.VERSION)
                            throw new Exception("wrong protocol version");

                        commands.Clear();
                        Connected?.Invoke();
                    }
                    else
                    {
                        // send off commands asynchronously
                        while (commands.TryDequeue(out var cmdbuf))
                            await pipe.WriteAsync(cmdbuf.AsMemory(), ct);

                        // find and read frame header
                        var chunk = await ReadStructAsync<ChunkHeader>();
                        while (chunk.fourCC != FourCC('f', 'h', 'd', 'r'))
                        {
                            // skip unknown chunks
                            var _ = ReadBytesAsync(chunk.size);
                        }

                        var frameheader = await ReadStructAsync<FrameHeader>(chunk.size);

                        // read video data
                        chunk = await ReadStructAsync<ChunkHeader>();
                        if (chunk.fourCC != FourCC('f', 'v', 'i', 'd'))
                            throw new Exception("video frame expected");
                        byte[] videoFrame = await ReadBytesAsync(chunk.size);
                        VideoFrameReceived?.Invoke(videoFrame, frameheader.flags.HasFlag(FrameHeader.FrameFlags.IDR_FRAME));

                        // read audio data
                        chunk = await ReadStructAsync<ChunkHeader>();
                        if (chunk.fourCC != FourCC('f', 'a', 'u', 'd'))
                            throw new Exception("audio frame expected");
                        byte[] audioFrame = await ReadBytesAsync(chunk.size);
                        AudioFrameReceived?.Invoke(audioFrame);
                    }
                }
                catch (EndOfStreamException)
                {
                    Disconnected?.Invoke();
                    Debug.WriteLine("eos");
                    pipe?.Close();
                    pipe = null;
                }
                catch (OperationCanceledException)
                {
                    if (pipe != null && pipe.IsConnected)
                        Disconnected?.Invoke();
                    pipe?.Close();
                    pipe = null;
                    return;
                }
                catch (Exception e)
                {
                    ConnectionError?.Invoke(e);
                    pipe?.Close();
                    return;
                }
            }
        }
    }
}
