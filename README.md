# StreamOutExample

This repository contains example C# code for connecting to Ventuz Encoded Stream Out outputs using the API described here: https://www.ventuz.com/support/help/latest/DevelopmentStreamOut.html

## Building 

The code should build with .NET 8 or later. The StreamOutTest project also needs the FFmpeg.AutoGen NuGet package, and to run StreamOutTest with video you'll need to get hold of a binary shared release of FFmpeg 6.x. A good starting point for that is [here](https://www.ffmpeg.org/download.html#build-windows).

## Projects

### StreamOutPipeExample

This is basically the same source code that's in the User Manual - it's a minimal command line program that connect to the first Stream Outputs and dumps video and audio to sidk.

### Ventuz.StreamOut

This library contains a class for connecting to a Stream Out output, receiving raw video and audio payloads via callbacks, and sending Keyboard/Mouse/Touch interaction back to Ventuz.

### StreamOutTest 

This is a full test application using WPF and FFmpeg that can display the video coming from Ventuz, and has full Keyboard, Mouse and Touch interaction capabilities.

NOTE: As this app is designed for simplicity, it lacks features like fullscreen output and proper video synchronization, and also has a bit more latency than necessary. This is on purpose in order to keep the code as readable as possible.
