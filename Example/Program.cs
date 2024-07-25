using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

/// <summary>
/// 
/// Copyright (C) Ventuz 2019-2024. All rights reserved.
/// 
/// Example for reading from the Ventuz StreamOut pipe. 
/// Opens the pipe and dumps the audio and video streams into files. 
/// 
/// Changelog
/// 2024-07-23 [TH] Added mouse event messages
/// 2019-08-29 [TH] Initial public version
/// 
/// </summary>
namespace StreamOutPipeExample
{
    // The data coming from the pipe is organized in chunks. every chunk starts with this, followed by the chunk data
    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkHeader
    {
        public uint fourCC;            // chunk type (four character code)
        public int size;               // size of chunk data
    }

    // Pipe header with general information (VVSP chunk)
    [StructLayout(LayoutKind.Sequential)]
    public struct PipeHeader
    {
        public static readonly int VERSION = 2;

        public uint hdrVersion;         // should be VERSION

        public uint videoCodecFourCC;   // used video codec, currently 'h264' aka h.264
        public uint videoWidth;         // width of image
        public uint videoHeight;        // height of image
        public uint videoFrameRateNum;  // frame rate numerator
        public uint videoFrameRateDen;  // frame rate denominator

        public uint audioCodecFourCC;   // used audio codec, currently 'pc16' aka 16 bit signed little endian PCM
        public uint audioRate;          // audio sample rate, currently fixed at 48000
        public uint audioChannels;      // audio channel count, currently 2
    }

    // Frame header, gets sent every frame, followed by video and then audio data (fhdr chunk)
    [StructLayout(LayoutKind.Sequential)]
    public struct FrameHeader
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
    enum PipeCommand : byte
    {
        Nop = 0x00, // Do nothing

        RequestIDRFrame = 0x01, // Request an IDR frame. it may take a couple of frames until the IDR frame arrives due to latency reasons.

        TouchBegin = 0x10, // Start a touch. Must be followed by a TouchPara structure
        TouchMove = 0x11, // Move a touch. Must be followed by a TouchPara structure        
        TouchEnd = 0x12, // Release a touch. Must be followed by a TouchPara structure        
        TouchCancel = 0x13, // Cancal a touch if possible. Must be followed by a TouchPara structure

        Key = 0x20, // Send a keystroke. Must be followed by a KeyPara structure

        MouseMove = 0x28, // Mouse positon update. Must be followed by a MouseXYPara structure
        MouseButtons = 0x29, // Mouse buttons update. Must be followed by a MouseButtonsPara structure
        MouseWheel = 0x2a, // Mouse wheel update. Must be followed by a MouseXYPara structure
                           // NOTE: Currently only the Y value of the wheel is supported

        SetEncodePara = 0x30, // Send encoder parameters. Must be followed by an EncodePara structure
    }

    // Parameters for Touch* PipeCommands    
    [StructLayout(LayoutKind.Sequential)]
    public struct TouchPara
    {
        public uint id;   // numerical id. Must be unique per touch (eg. finger # or something incremental)
        public int x;     // x coordinate in pixels from the left side of the viewport
        public int y;     // y coordinate in pixels from the upper side of the viewport
    };

    // Parameters for the Key PipeCommand
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyPara
    {
        public uint Code;   // UTF32 codepoint for pressed key. Control characters like LF and Backspace work.
    };

    // Parameters for the MouseMove and MouseWheel PipeCommands
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseXYPara
    {
        public int x;     // x coordinate in pixels from the left side of the viewport, or horizontal wheel delta
        public int y;     // y coordinate in pixels from the upper side of the viewport, or vertical wheel delta
    }

    // Mouse buttons bitfield
    [Flags]
    public enum MouseButtons : uint
    {
        Left = 0x01,
        Right = 0x02,
        Middle = 0x04,
        X1 = 0x08,
        X2 = 0x10,
    };

    // Parameters for the MouseButtons PipeCommand
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseButtonsPara
    {
        public MouseButtons Buttons; // bit field of all buttons pressed at the time
    };

    // Parameters for the SetEnodePara PipeCommand
    [StructLayout(LayoutKind.Sequential)]
    public struct EncodePara
    {
        public enum RateControlMode : int
        {
            ConstQP = 0,  // BitrateOrQP is QP (0..51)
            ConstRate = 1, // BitrateOrQP is rate in kBits/s
        }

        public RateControlMode Mode;
        public uint BitrateOrQP;
    };

    class Program
    {
        // The following helper functions are only meant as a reference.
        // In a full implementation these should be asynchronous or
        // at least have some kind of timeout/error handling.

        /// <summary>
        /// Read a number of bytes from a stream and return as array
        /// </summary>
        public static byte[] ReadBytes(Stream stream, int length)
        {
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>
        /// Read a struct from a stream by just blitting the data
        /// </summary>
        public static T ReadStruct<T>(Stream stream, int size = 0) where T : struct
        {
            var tsize = Marshal.SizeOf(typeof(T));
            if (size <= 0) size = tsize;
            if (size < tsize) throw new ArgumentException("size is too small");

            var bytes = ReadBytes(stream, size);
            return MemoryMarshal.Cast<byte, T>(bytes.AsSpan(0, tsize))[0];
        }

        /// <summary>
        /// Send a command to the pipe
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public static void SendCommand(Stream stream, PipeCommand cmd)
        {
            stream.Write([(byte)cmd]);
        }

        /// <summary>
        /// Send a command to the pipe, with data struct
        /// </summary>
        public static void SendCommand<T>(Stream stream, PipeCommand cmd, in T para) where T : struct
        {
            var srcspan = MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateReadOnlySpan(in para, 1));
            stream.Write([(byte)cmd, .. srcspan]);
        }

        /// <summary>
        /// Make uint FourCC from four characters
        /// </summary>
        public static uint FourCC(char a, char b, char c, char d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | ((uint)d);

        /// <summary>
        /// Entry point here
        /// </summary>
        static void Main()
        {
            Console.WriteLine("Ventuz video stream pipe receiver example\n");

            // The pipe name is "VentuzOutA" (or B etc for additional outputs), local only.
            // You can connect to the pipe as many times as you wish (eg. once per client you serve),
            // or you can connect once and handle distribution yourself.
            using var stream = new NamedPipeClientStream("VentuzOutA");

            // connect to the pipe
            Console.WriteLine("Connecting...\n");
            stream.Connect();

            // try to get first chunk
            var chunk = ReadStruct<ChunkHeader>(stream);
            if (chunk.fourCC != FourCC('V', 'V', 'S', 'P'))
                throw new Exception("invalid header from pipe");

            // get header
            var header = ReadStruct<PipeHeader>(stream, chunk.size);
            if (header.hdrVersion != PipeHeader.VERSION)
                throw new Exception("wrong protocol version");

            // check codecs
            if (header.videoCodecFourCC != FourCC('h', '2', '6', '4') || header.audioCodecFourCC != FourCC('p', 'c', '1', '6'))
                throw new Exception("unsupported video or audio codec");

            Console.WriteLine($"video: {header.videoWidth}x{header.videoHeight} @ {(float)header.videoFrameRateNum / header.videoFrameRateDen}fps");
            Console.WriteLine($"audio: {header.audioChannels}ch @ {header.audioRate}Hz");
            Console.WriteLine("Press ESC to quit.\n");

            // video should always start with a full IDR frame (key frame with decoder reset,
            // contains metadata such as picture and sequence information). Let's check if this is true.
            bool expectIDRFrame = true;
            float bytespersec = 0;

            // let's open some files to dump the streams into. You can open the .264 file with 
            // VLC or Media Player Classic, or mux video and audio using FFMpeg.                 
            FileStream videoFile = null;
            FileStream audioFile = null;
            try
            {
                var temppath = Path.GetTempPath();
                videoFile = File.Create(temppath + "test.264");
                audioFile = File.Create(temppath + "test.pcm");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Can't create output file(s): {ex.Message}\n");
                return;
            }

            // now let's receive some stuff! Forever!
            bool quit = false;
            while (!quit)
            {
                while (Console.KeyAvailable)
                {
                    var rk = Console.ReadKey();
                    var key = rk.Key;

                    // On second thought, quit if esc is pressed.
                    if (key == ConsoleKey.Escape)
                    {
                        quit = true;
                        break;
                    }
                    else if (key == ConsoleKey.Spacebar)
                    {
                        // If the space bar is pressed, request an IDR frame
                        // (not setting expectIDRFrame here; it might take a frame or two until the IDR frame arrives)
                        SendCommand(stream, PipeCommand.RequestIDRFrame);
                    }
                    else if (key == ConsoleKey.F1)
                    {
                        // simulate a singular touch
                        var para = new TouchPara
                        {
                            id = 12345,
                            x = 100,
                            y = 100,
                        };
                        SendCommand(stream, PipeCommand.TouchBegin, para);
                        SendCommand(stream, PipeCommand.TouchEnd, para);
                    }
                    else if (rk.KeyChar >= '1' && rk.KeyChar <= '9')
                    {
                        // numeric keys: switch encoder bitrate to 1 mbit per key :)
                        SendCommand(stream, PipeCommand.SetEncodePara, new EncodePara
                        {
                            Mode = EncodePara.RateControlMode.ConstRate,
                            BitrateOrQP = ((uint)rk.KeyChar - '0') * 1000,
                        });
                    }
                    else
                    {
                        // forward all other keys to Ventuz
                        SendCommand(stream, PipeCommand.Key, new KeyPara
                        {
                            Code = rk.KeyChar
                        });
                    }
                }

                try
                {
                    // frame header first
                    chunk = ReadStruct<ChunkHeader>(stream);
                    if (chunk.fourCC != FourCC('f', 'h', 'd', 'r'))
                    {
                        // skip unknown chunks
                        var _ = ReadBytes(stream, chunk.size);
                        continue;
                    }

                    var frameheader = ReadStruct<FrameHeader>(stream, chunk.size);
                    if (expectIDRFrame)
                    {
                        if (!frameheader.flags.HasFlag(FrameHeader.FrameFlags.IDR_FRAME))
                            throw new Exception("IDR frame expected");
                        expectIDRFrame = false;
                    }

                    // read video data
                    chunk = ReadStruct<ChunkHeader>(stream);
                    if (chunk.fourCC != FourCC('f', 'v', 'i', 'd'))
                        throw new Exception("video frame expected");
                    byte[] videoFrame = ReadBytes(stream, chunk.size);

                    // read audio data
                    chunk = ReadStruct<ChunkHeader>(stream);
                    if (chunk.fourCC != FourCC('f', 'a', 'u', 'd'))
                        throw new Exception("audio frame expected");
                    byte[] audioFrame = ReadBytes(stream, chunk.size);

                    // calc video bitrate (exponential moving average over packet size times frame rate)
                    bytespersec += 0.1f * ((float)videoFrame.Length * header.videoFrameRateNum / header.videoFrameRateDen - bytespersec);

                    // print stuff and dump streams into files    
                    if (frameheader.flags.HasFlag(FrameHeader.FrameFlags.IDR_FRAME))
                        Console.WriteLine($"Received IDR frame {frameheader.frameIndex}                    ");
                    Console.Write($"got frame {frameheader.frameIndex}, {bytespersec * 8 / 1000.0f}kbits/s       \r");
                    videoFile?.Write(videoFrame, 0, videoFrame.Length);
                    audioFile?.Write(audioFrame, 0, audioFrame.Length);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"pipe read failed: {e.Message}");
                    break;
                }
            }

            Console.WriteLine("good bye.");
            videoFile?.Close();
            audioFile?.Close();
        }
    }
}
