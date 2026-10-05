namespace VoiceMorph.App.Models;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}
