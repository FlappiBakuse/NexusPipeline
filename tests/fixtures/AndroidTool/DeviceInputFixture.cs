namespace NexusPipeline.AndroidToolFixture;

internal static class DeviceInputFixture
{
    internal static string? Action(string[] arguments)
    {
        if (arguments is not ["shell", var command]) return null;
        return command switch
        {
            "input tap 1 1" or "input tap 123 45" or "input swipe 10 20 50 60 20" or "input keyevent 4" or "input text 'nexus%sdevice'" => "input",
            "device-fixture-output" => "device-output",
            "device-fixture-hang" => "device-hang",
            _ => null
        };
    }
}
