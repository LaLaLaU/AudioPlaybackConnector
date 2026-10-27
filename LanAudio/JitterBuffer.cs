using System.Buffers.Binary;
using NAudio.Wave;

namespace AudioPlaybackConnector.Lan;

// Sample positions, rather than receive order, determine playback. A small variable
// read rate compensates for independent sender/receiver clocks (maximum +/-0.2%).
internal sealed class JitterBuffer : IWaveProvider
{
    private readonly object gate = new();
    private readonly SortedList<long, AudioPacket> packets = new();
    private readonly int targetFrames;
    private readonly int maxFrames;
    private double position;
    private double step = 1;
    private bool playing;
    private bool everPlayed;
    private long missingFrames, latePackets, duplicatePackets, resyncs, underruns;
    private float volume = 1;
    internal float Volume { get { lock (gate) return volume; } set { lock (gate) volume = Math.Clamp(value, 0, 1); } }
    public WaveFormat WaveFormat { get; } = new(AudioProtocol.SampleRate, 16, AudioProtocol.Channels);

    internal JitterBuffer(int milliseconds)
    {
        if (milliseconds is < 20 or > 500) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        targetFrames = AudioProtocol.SampleRate * milliseconds / 1000;
        maxFrames = targetFrames + AudioProtocol.SampleRate / 4;
    }

    internal void Push(AudioPacket packet)
    {
        lock (gate)
        {
            if (everPlayed && packet.End <= position) { latePackets++; return; }
            if (packets.ContainsKey(packet.Frame)) { duplicatePackets++; return; }
            // Sender frame ranges must not overlap; reject malformed authenticated input.
            int before = FindPacketIndex(packet.Frame);
            if (before >= 0 && packets.Values[before].End > packet.Frame) { duplicatePackets++; return; }
            if (before + 1 < packets.Count && packet.End > packets.Keys[before + 1]) { duplicatePackets++; return; }
            packets.Add(packet.Frame, packet);
            long latest = packets.Values[^1].End;
            double start = playing ? position : packets.Keys[0];
            if (latest - start > maxFrames || packets.Count > 400)
            {
                position = latest - targetFrames;
                playing = everPlayed = true;
                step = 1;
                resyncs++;
                TrimConsumed();
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > buffer.Length || count % AudioProtocol.BlockAlign != 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Array.Clear(buffer, offset, count);
        lock (gate)
        {
            if (!playing)
            {
                if (packets.Count == 0) return count;
                long start = packets.Keys[0];
                if (packets.Values[^1].End - start < targetFrames) return count;
                position = Math.Max(everPlayed ? position : start, start);
                playing = everPlayed = true;
                step = 1;
            }
            double available = packets.Count == 0 ? 0 : packets.Values[^1].End - position;
            double desiredStep = Math.Clamp(1 + (available - targetFrames) / (AudioProtocol.SampleRate * 8d), .998, 1.002);
            step += (desiredStep - step) * .02;
            int frameCount = count / AudioProtocol.BlockAlign;
            for (int f = 0; f < frameCount; f++)
            {
                long frame = (long)position;
                if (packets.Count == 0 || frame >= packets.Values[^1].End)
                {
                    playing = false;
                    underruns++;
                    break;
                }
                int index = FindPacketIndex(frame);
                AudioPacket? packet = index >= 0 && packets.Values[index].End > frame ? packets.Values[index] : null;
                if (packet is null) missingFrames++;
                else
                {
                    double fraction = position - frame;
                    int nextIndex = FindPacketIndex(frame + 1);
                    AudioPacket? next = nextIndex >= 0 && packets.Values[nextIndex].End > frame + 1 ? packets.Values[nextIndex] : null;
                    for (int ch = 0; ch < AudioProtocol.Channels; ch++)
                    {
                        short a = Sample(packet, frame, ch);
                        short b = next is null ? a : Sample(next, frame + 1, ch);
                        short sample = (short)Math.Clamp((a + (b - a) * fraction) * volume, short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + f * AudioProtocol.BlockAlign + ch * 2, 2), sample);
                    }
                }
                position += step;
            }
            TrimConsumed();
        }
        return count;
    }

    private static short Sample(AudioPacket packet, long frame, int channel) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet.Pcm.AsSpan(checked((int)(frame - packet.Frame) * AudioProtocol.BlockAlign + channel * 2), 2));

    private int FindPacketIndex(long frame)
    {
        int low = 0, high = packets.Count - 1, result = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (packets.Keys[mid] <= frame) { result = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return result;
    }

    private void TrimConsumed()
    {
        while (packets.Count > 0 && packets.Values[0].End <= (long)position - 1) packets.RemoveAt(0);
    }

    internal BufferSnapshot Snapshot()
    {
        lock (gate)
        {
            double start = everPlayed ? position : packets.Count > 0 ? packets.Keys[0] : 0;
            double queued = packets.Count > 0 ? Math.Max(0, packets.Values[^1].End - start) : 0;
            return new(queued * 1000 / AudioProtocol.SampleRate, missingFrames, latePackets, duplicatePackets, resyncs, underruns, step);
        }
    }
}

internal sealed record BufferSnapshot(double Milliseconds, long MissingFrames, long LatePackets, long DuplicatePackets, long Resyncs, long Underruns, double Rate);
