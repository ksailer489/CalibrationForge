namespace CalibrationForge.Core;

public static class E92ReadKernelExample
{
    public const uint Address = 0x100;
    public const int Length = 8;
    public const int ChunkSize = 4;
    public const string Json = """
        {
          "format": "CalibrationForge.CanTrace.v1",
          "source": "Synthetic fixture derived from the public SCPB wire contract - NOT captured E92 traffic",
          "isSimulation": true,
          "frames": [
            { "direction": "tx", "id": "7E0", "data": "02 1A BB 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "07 5A BB 53 43 50 42 00" },
            { "direction": "tx", "id": "7E0", "data": "07 23 00 00 01 00 00 04" },
            { "direction": "rx", "id": "7E8", "data": "10 09 63 00 00 01 00 AA" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 BB CC DD 00 00 00 00" },
            { "direction": "tx", "id": "7E0", "data": "07 23 00 00 01 04 00 04" },
            { "direction": "rx", "id": "7E8", "data": "10 09 63 00 00 01 04 11" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 22 33 44 00 00 00 00" },
            { "direction": "tx", "id": "7E0", "data": "07 23 00 00 01 00 00 04" },
            { "direction": "rx", "id": "7E8", "data": "10 09 63 00 00 01 00 AA" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 BB CC DD 00 00 00 00" },
            { "direction": "tx", "id": "7E0", "data": "07 23 00 00 01 04 00 04" },
            { "direction": "rx", "id": "7E8", "data": "10 09 63 00 00 01 04 11" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 22 33 44 00 00 00 00" }
          ]
        }
        """;
}
