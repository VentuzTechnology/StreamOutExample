//-------------------------------------------------------------------
//
// Ventuz Encoded Stream Out Receiver example
// (C) 2024 Ventuz Technology, licensed under the MIT license
//
//-------------------------------------------------------------------

using FFmpeg.AutoGen;

using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;

namespace StreamOutTest
{
    /// <summary>
    /// Simplest "video player" possible
    /// Using ffmpeg, it takes in raw frame data and copies into a BGRA32 image buffer 
    /// Not latency optimized, not synchronized.
    /// </summary>
    internal sealed unsafe class VideoDecoder : IDisposable
    {
        // public interface

        public static void FindFfmpeg()
        {
            var dllName = "avcodec-" + ffmpeg.LibraryVersionMap["avcodec"] + ".dll";

            bool Test(string dir) => File.Exists(Path.Combine(dir, dllName));

            // find ffmpeg
            string folder = ".";
            bool hasFF = Test(folder);
            if (!hasFF)
            {
                if (File.Exists("ffmpegdir.txt"))
                    folder = File.ReadAllText("ffmpegdir.txt");

                while (!(hasFF = Test(folder)))
                {
                    var dlg = new OpenFolderDialog
                    {
                        Title = $"Please select a folder with FFMpeg in it ({dllName} etc.)",
                    };

                    if (dlg.ShowDialog() == false)
                        break;

                    folder = dlg.FolderName;
                }

                File.WriteAllText("ffmpegdir.txt", folder);
            }

            if (hasFF)
            {
                ffmpeg.RootPath = folder;
                HasFFmpeg = true;
            }
        }


        public static bool HasFFmpeg { get; private set; }

        readonly uint fourCC;

        public VideoDecoder(uint fourCC)
        {
            this.fourCC = fourCC;
            if (HasFFmpeg)
                new TaskFactory().StartNew(Runner, TaskCreationOptions.LongRunning);
        }

        ~VideoDecoder() => Dispose(false);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        public void OnVideoFrame(Span<byte> span, bool idr)
        {
            if (!HasFFmpeg) return;

            // copy data into an AVPacket and enqueue

            var data = ffmpeg.av_malloc((ulong)span.Length);
            span.CopyTo(new Span<byte>(data, span.Length));

            var packet = ffmpeg.av_packet_alloc();
            ffmpeg.av_packet_from_data(packet, (byte*)data, span.Length); 
            if (idr)
                packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            packets.Enqueue(new Packet { pkt = packet });
            frameEvent.Set();
        }


        public event Func<IntPtr>? Lock;
        public event Action? Unlock;

        // Internals

        void Dispose(bool disposing)
        {
            exit = true;
            frameEvent.Set();

            if (disposing)
            {
                frameEvent.Dispose();
            }

            while (packets.TryDequeue(out var packet))
                ffmpeg.av_packet_free(&packet.pkt);
        }


        void Runner()
        {
            AVDictionary* options = null;
            SwsContext* sws = null;

            // open video decoder

            AVCodec* codec;
            if (fourCC == Ventuz.StreamOut.Client.FourCC('h', 'e', 'v', 'c'))
                codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
            else if (fourCC == Ventuz.StreamOut.Client.FourCC('h', '2', '6', '4'))
                codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            else
                throw new Exception("unknown codec");

            var context = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_open2(context, codec, &options);

            while (!exit)
            {
                frameEvent.WaitOne(10);

                while (packets.TryDequeue(out var packet))
                {
                    // send packet to decoder
                    int ret = ffmpeg.avcodec_send_packet(context, packet.pkt);
                    ffmpeg.av_packet_free(&packet.pkt);
                  
                    while (true)
                    {
                        // get next frame out of the decoder
                        var frame = ffmpeg.av_frame_alloc();
                        ret = ffmpeg.avcodec_receive_frame(context, frame);
                        if (ret < 0)
                        {
                            ffmpeg.av_frame_free(&frame);
                            break;
                        }

                        // set up scaler on first received frame
                        if (sws == null)
                        {
                            sws = ffmpeg.sws_getContext(context->width, context->height, context->pix_fmt, 
                                context->width, context->height, AVPixelFormat.AV_PIX_FMT_BGRA, 
                                ffmpeg.SWS_POINT, null, null, null);
                        }

                        // Lock output surface and scale/convert frame right into it
                        if (Lock != null)
                        {
                            var destptr = Lock();
                            if (destptr != IntPtr.Zero)
                            {
                                try
                                {
                                    ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, context->height, 
                                        [(byte*)destptr.ToPointer()], [context->width * 4]);
                                }
                                finally
                                {
                                    Unlock?.Invoke();
                                }
                            }
                        }

                        ffmpeg.av_frame_free(&frame);
                    }
                }
            }

            ffmpeg.avcodec_free_context(&context);
            ffmpeg.sws_freeContext(sws);
        }

        // fields

        struct Packet { public AVPacket* pkt; };
               
        readonly ConcurrentQueue<Packet> packets = [];
        readonly AutoResetEvent frameEvent = new(false);
        bool exit = false;
    }
}
