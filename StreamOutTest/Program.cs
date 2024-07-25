//-------------------------------------------------------------------
//
// Ventuz Encoded Stream Out Receiver example
// (C) 2024 Ventuz Technology, licensed under the MIT license
//
//-------------------------------------------------------------------

using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;


namespace StreamOutTest
{
    class Program
    {
        static Image image = new();
        static Ventuz.StreamOut.Client? client;
        static VideoDecoder? player;

        static readonly MenuItem outputMenu = new()
        {
            Header = "Output",
        };

        //-------------------------------------------------------------------
        // Client state management
        //-------------------------------------------------------------------

        static void ConnectClient(int outputIdx)
        {
            image.Source = null;
            player?.Dispose();
            player = null;
            client?.Dispose();

            client = new Ventuz.StreamOut.Client(outputIdx);

            client.Connected += () => Application.Current.Dispatcher.Invoke(OnConnect);
            client.Disconnected += () => Application.Current.Dispatcher.Invoke(OnDisconnect);
            client.ConnectionError += (ex) => Application.Current.Dispatcher.Invoke(OnError, ex);
            client.VideoFrameReceived += (mem, idr) => player?.OnVideoFrame(mem.Span, idr);

            client.Run();

            for (int i = 0; i < outputMenu.Items.Count; i++)
                ((MenuItem)outputMenu.Items[i]).IsChecked = i == outputIdx;
        }

        static void OnConnect()
        {
            var hdr = client!.Header;
            var writeableBitmap = new WriteableBitmap((int)hdr.videoWidth, (int)hdr.videoHeight, 96, 96, PixelFormats.Bgr32, null);
            image.Source = writeableBitmap;

            player = new VideoDecoder(hdr.videoCodecFourCC);

            player.Lock += () => Application.Current?.Dispatcher.Invoke(() =>
            {
                writeableBitmap.Lock();
                return writeableBitmap.BackBuffer;
            }) ?? IntPtr.Zero;

            player.Unlock += () => Application.Current?.Dispatcher.Invoke(() =>
            {
                writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, writeableBitmap.PixelWidth, writeableBitmap.PixelHeight));
                writeableBitmap.Unlock();
            });
        }

        static void OnDisconnect()
        {
            player?.Dispose();
            player = null;
            image.Source = null;
        }

        static void OnError(Exception ex)
        {
            MessageBox.Show(ex.ToString());
            Application.Current.Shutdown();
        }

        //-------------------------------------------------------------------
        // Mouse input
        //-------------------------------------------------------------------

        static void OnMouseMove(object source, MouseEventArgs e)
        {
            if (image.Source is null || e.StylusDevice is not null )
                return;

            var pos = e.GetPosition(image);
            var para = new Ventuz.StreamOut.MouseXYPara
            {
                x = (int)Math.Round(pos.X * image.Source.Width / image.ActualWidth),
                y = (int)Math.Round(pos.Y * image.Source.Height / image.ActualHeight)
            };

            client?.SendMouseMove(para);
            e.Handled = true;
        }

        static void OnMouseLeave(object source, MouseEventArgs e)
        {
            if (image.Source is null || e.StylusDevice is not null)
                return;

            client?.SendMouseMove(new Ventuz.StreamOut.MouseXYPara { x = -1, y = -1 });
            e.Handled = true;
        }

        static void OnMouseButtons(object source, MouseButtonEventArgs e)
        {
            if (image.Source is null || e.StylusDevice is not null)
                return;

            var buttons = Ventuz.StreamOut.MouseButtonEnum.None;
            if (e.LeftButton == MouseButtonState.Pressed) buttons |= Ventuz.StreamOut.MouseButtonEnum.Left;
            if (e.RightButton == MouseButtonState.Pressed) buttons |= Ventuz.StreamOut.MouseButtonEnum.Right;
            if (e.MiddleButton == MouseButtonState.Pressed) buttons |= Ventuz.StreamOut.MouseButtonEnum.Middle;
            if (e.XButton1 == MouseButtonState.Pressed) buttons |= Ventuz.StreamOut.MouseButtonEnum.X1;
            if (e.XButton2 == MouseButtonState.Pressed) buttons |= Ventuz.StreamOut.MouseButtonEnum.X2;
            client?.SendMouseButtons(buttons);
            e.Handled = true;
        }

        static void OnMouseWheel(object source, MouseWheelEventArgs e)
        {
            if (image.Source is null || e.StylusDevice is not null)
                return;

            client?.SendMouseWheel(new Ventuz.StreamOut.MouseXYPara { y = e.Delta });
            e.Handled = true;
        }

        //-------------------------------------------------------------------
        // Keyboard input
        //-------------------------------------------------------------------

        static void OnTextInput(object source, TextCompositionEventArgs e)
        {
            if (e.Text.Length == 1)
                client?.SendKey((uint)e.Text[0]);
            e.Handled = true;
        }

        //-------------------------------------------------------------------
        // Touch input
        //-------------------------------------------------------------------

        static void OnTouch(object? source, TouchEventArgs e)
        {
            if (image.Source is null)
                return;

            var tp = e.GetTouchPoint(image);

            var para = new Ventuz.StreamOut.TouchPara
            {
                id = (uint)tp.TouchDevice.Id,
                x = (int)Math.Round(tp.Position.X * image.Source.Width / image.ActualWidth),
                y = (int)Math.Round(tp.Position.Y * image.Source.Height / image.ActualHeight),
            };

            switch (tp.Action)
            {
                case TouchAction.Down: client?.SendTouchBegin(para); break;
                case TouchAction.Move: client?.SendTouchMove(para); break;
                case TouchAction.Up: client?.SendTouchEnd(para); break;
            }
            e.Handled = true;
        }

        //-------------------------------------------------------------------
        // Other commands
        //-------------------------------------------------------------------

        static void OnDecoderReset(object? source, EventArgs e)
        {
            client?.SendRequestIDRFrame();
        }

        //-------------------------------------------------------------------
        // Main
        //-------------------------------------------------------------------

        [STAThread]
        static void Main()
        {
            VideoDecoder.FindFfmpeg();

            // create preview image

            image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);

            image.MouseMove += OnMouseMove;
            image.MouseLeave += OnMouseLeave;
            image.MouseDown += OnMouseButtons;
            image.MouseUp += OnMouseButtons;
            image.MouseWheel += OnMouseWheel;
            image.TextInput += OnTextInput;
            image.TouchDown += OnTouch;
            image.TouchMove += OnTouch;
            image.TouchUp += OnTouch;

            image.Loaded += (s, e) => image.Focus(); 

            // create menu

            for (int i=0; i<8; i++)
            {
                var item = new MenuItem { Header = $"Stream Out {"ABCDEFGH"[i]}" };
                var i2 = i;
                item.Click += (s, e) => ConnectClient(i2);
                outputMenu.Items.Add(item);
            }

            var resetButton = new Button { Content = "Reset" };
            resetButton.Click += OnDecoderReset;

            var menu = new Menu { Height = 20 };
            menu.Items.Add(outputMenu);
            menu.Items.Add(resetButton);
            
            // create window

            var dock = new DockPanel();
            dock.Children.Add(menu);
            DockPanel.SetDock(menu, Dock.Top);
            dock.Children.Add(image);

            new Window
            {
                Title = "Ventuz Stream Out test app (WPF)",
                Background = Brushes.DarkGray,
                Width = 1280,
                Height = 720,
                Content = dock,
            }.Show();

            // let's go

            ConnectClient(0);
            new Application().Run();
        }
    }
}
