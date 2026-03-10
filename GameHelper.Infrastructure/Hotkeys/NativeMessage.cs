namespace GameHelper.Infrastructure.Hotkeys;

public struct NativeMessage
{
    public nint Handle { get; set; }

    public uint Message { get; set; }

    public nuint WParam { get; set; }

    public nint LParam { get; set; }

    public uint Time { get; set; }
}
