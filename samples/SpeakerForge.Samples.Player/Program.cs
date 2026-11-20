// SpeakerForge sample player - demonstrates discovery, playback, and PCM broadcast.
//
// Usage:
//   SpeakerForge.Samples.Player discover              list network speakers
//   SpeakerForge.Samples.Player tone <name> [secs]    stream a 440Hz test tone
//   SpeakerForge.Samples.Player stop <name>           stop a speaker
//
// <name> is a case-insensitive substring of the device's friendly name.

using SpeakerForge;

const int SampleRate = 48000;
const int Channels = 2;
const int BlockFrames = 480; // 10ms blocks

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

using var service = new NetworkSpeakerService();
service.Start();

switch (args[0].ToLowerInvariant())
{
    case "discover":
    case "devices":
    {
        var devices = await service.DiscoverAsync();
        if (devices.Count == 0) Console.WriteLine("No network speakers found.");
        foreach (var d in devices)
            Console.WriteLine($"{d.FriendlyName,-40} [{d.Protocol,-6}] {d.Address}");
        return 0;
    }

    case "tone":
    {
        if (args.Length < 2) { PrintUsage(); return 1; }
        int seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 10;
        var device = await FindDevice(service, args[1]);
        if (device is null) return 2;

        Console.WriteLine($"Playing test tone to '{device.FriendlyName}' for {seconds}s...");
        if (!await service.PlayToDeviceAsync(device.Id))
        {
            Console.WriteLine("Device refused the stream.");
            return 3;
        }
        await Task.Delay(500); // let the device connect to /stream

        var pcm = new float[BlockFrames * Channels];
        double phase = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            for (int i = 0; i < BlockFrames; i++)
            {
                float sample = (float)(0.25 * Math.Sin(phase));
                phase += 2 * Math.PI * 440 / SampleRate;
                pcm[i * Channels] = sample;
                pcm[i * Channels + 1] = sample;
            }
            service.Server.Broadcast(pcm, BlockFrames);
            await Task.Delay(10);
        }
        await service.StopDeviceAsync(device.Id);
        Console.WriteLine("Done.");
        return 0;
    }

    case "stop":
    {
        if (args.Length < 2) { PrintUsage(); return 1; }
        var device = await FindDevice(service, args[1]);
        if (device is null) return 2;
        await service.StopDeviceAsync(device.Id);
        Console.WriteLine($"Stopped '{device.FriendlyName}'.");
        return 0;
    }

    default:
        PrintUsage();
        return 1;
}

static async Task<INetworkRenderer?> FindDevice(NetworkSpeakerService service, string match)
{
    var devices = await service.DiscoverAsync();
    var device = devices.FirstOrDefault(d =>
        d.FriendlyName.Contains(match, StringComparison.OrdinalIgnoreCase));
    if (device is null)
        Console.WriteLine($"No speaker matching '{match}'. Run 'discover' to list devices.");
    return device;
}

static void PrintUsage()
{
    Console.WriteLine("""
        SpeakerForge sample player

          discover              list network speakers (UPnP/DLNA, Cast, AirPlay)
          tone <name> [secs]    stream a 440Hz test tone to a speaker
          stop <name>           stop playback on a speaker

        <name> is a case-insensitive part of the speaker's friendly name.
        """);
}
