namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

internal interface IEvdevDeviceReader : IAsyncDisposable
{
    // ReSharper disable once UnusedMemberInSuper.Global -- reader-contract member; the device
    // path identifies the reader for diagnostics and is asserted through the fake in tests.
    string Path { get; }

    bool TryStart();
}

internal interface IEvdevDeviceReaderFactory
{
    IEvdevDeviceReader Create(
        string path,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    );
}

internal interface IEvdevKeyboardEnumerator
{
    IEnumerable<string> EnumerateKeyboards();

    bool Exists(string path);
}

internal sealed class EvdevDeviceReaderFactory : IEvdevDeviceReaderFactory
{
    public IEvdevDeviceReader Create(
        string path,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    )
    {
        return new EvdevDeviceReader(path, onKeyEvent, onFailure);
    }
}

internal sealed class EvdevKeyboardEnumerator : IEvdevKeyboardEnumerator
{
    public IEnumerable<string> EnumerateKeyboards()
    {
        return KeyboardDeviceDiscovery.EnumerateKeyboards();
    }

    public bool Exists(string path)
    {
        return File.Exists(path);
    }
}
