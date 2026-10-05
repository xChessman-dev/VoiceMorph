namespace VoiceMorph.App.Models;

public sealed class AudioMeterEventArgs(float input, float output) : EventArgs
{
    public float Input { get; } = Math.Clamp(input, 0f, 1f);

    public float Output { get; } = Math.Clamp(output, 0f, 1f);
}
