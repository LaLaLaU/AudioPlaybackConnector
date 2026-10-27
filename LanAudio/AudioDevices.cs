using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPlaybackConnector.Lan;

internal sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class AudioDevices
{
    internal static List<AudioDevice> ListOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(device =>
        {
            using (device) return new AudioDevice(device.ID, device.FriendlyName + (device.ID == defaultDevice.ID ? "（系统默认）" : ""));
        }).OrderByDescending(device => device.Id == defaultDevice.ID).ToList();
    }

    internal static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }
}

internal sealed class LoopbackCapture : WasapiCapture
{
    internal LoopbackCapture(MMDevice device) : base(device, false, 20)
    {
        // Shared-mode conversion produces a predictable network format even when
        // the physical device uses float32, 44.1 kHz, or surround sound.
        WaveFormat = new WaveFormat(AudioProtocol.SampleRate, 16, AudioProtocol.Channels);
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}
