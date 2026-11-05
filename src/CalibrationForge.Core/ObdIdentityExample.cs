namespace CalibrationForge.Core;

public static class ObdIdentityExample
{
    public const string Json = """
        {
          "format": "CalibrationForge.CanTrace.v1",
          "source": "Synthetic identification exchange - not E92 vehicle traffic",
          "isSimulation": true,
          "frames": [
            { "direction": "tx", "id": "7E0", "data": "02 09 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "06 49 00 54 00 00 00 00" },
            { "direction": "tx", "id": "7E0", "data": "02 09 02 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "10 14 49 02 01 54 45 53" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 54 30 30 30 30 30 30" },
            { "direction": "rx", "id": "7E8", "data": "22 30 30 30 30 30 30 31" },
            { "direction": "tx", "id": "7E0", "data": "02 09 04 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "10 13 49 04 01 45 58 41" },
            { "direction": "tx", "id": "7E0", "data": "30 08 00 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "21 4D 50 4C 45 2D 43 41" },
            { "direction": "rx", "id": "7E8", "data": "22 4C 00 00 00 00 00 00" },
            { "direction": "tx", "id": "7E0", "data": "02 09 06 00 00 00 00 00" },
            { "direction": "rx", "id": "7E8", "data": "07 49 06 01 12 34 56 78" }
          ]
        }
        """;
}
