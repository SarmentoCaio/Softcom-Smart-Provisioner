namespace SoftcomSmartProvisioner.Models;

public sealed class MirrorOptions
{
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; }
    public bool NoAudio { get; set; }
    public int MaxSize { get; set; } = 0;
    public int MaxFps { get; set; } = 0;
}
