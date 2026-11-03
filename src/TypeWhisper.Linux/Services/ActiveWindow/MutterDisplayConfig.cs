using Tmds.DBus.Protocol;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Reads the logical monitor layout from Mutter's <c>org.gnome.Mutter.DisplayConfig</c>, the
///     coordinate space Window Calls reports frames in.
/// </summary>
internal static class MutterDisplayConfig
{
    private const string Service = "org.gnome.Mutter.DisplayConfig";
    private const string ObjectPath = "/org/gnome/Mutter/DisplayConfig";

    // "layout-mode" value whose logical monitors are sized mode / scale.
    private const uint LogicalLayoutMode = 1;

    internal readonly record struct LogicalMonitor(int X, int Y, double Scale, uint Transform, int ModeWidth, int ModeHeight);

    private static readonly MessageValueReader<AtSpiScreenRect?> s_readLogicalDesktop = static (message, _) =>
    {
        var reader = message.GetBodyReader();
        reader.ReadUInt32(); // serial

        // monitors: a((ssss)a(siiddada{sv})a{sv}) — remember each connector's current mode size.
        var currentModes = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        var monitorsEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(monitorsEnd))
        {
            reader.AlignStruct();
            var connector = ReadMonitorSpec(ref reader);
            var modesEnd = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(modesEnd))
            {
                reader.AlignStruct();
                reader.ReadString(); // mode id
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                reader.ReadDouble(); // refresh rate
                reader.ReadDouble(); // preferred scale
                var scalesEnd = reader.ReadArrayStart(DBusType.Double);
                while (reader.HasNext(scalesEnd))
                {
                    reader.ReadDouble();
                }

                var modeProperties = reader.ReadDictionaryOfStringToVariantValue();
                if (modeProperties.TryGetValue("is-current", out var current) && current.GetBool())
                {
                    currentModes[connector] = (width, height);
                }
            }

            reader.ReadDictionaryOfStringToVariantValue();
        }

        // logical monitors: a(iiduba(ssss)a{sv})
        var logical = new List<LogicalMonitor>();
        var logicalEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(logicalEnd))
        {
            reader.AlignStruct();
            var x = reader.ReadInt32();
            var y = reader.ReadInt32();
            var scale = reader.ReadDouble();
            var transform = reader.ReadUInt32();
            reader.ReadBool(); // primary
            string? connector = null;
            var specsEnd = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(specsEnd))
            {
                reader.AlignStruct();
                var spec = ReadMonitorSpec(ref reader);
                connector ??= spec;
            }

            reader.ReadDictionaryOfStringToVariantValue();
            if (connector is not null && currentModes.TryGetValue(connector, out var mode))
            {
                logical.Add(new LogicalMonitor(x, y, scale, transform, mode.Width, mode.Height));
            }
        }

        var properties = reader.ReadDictionaryOfStringToVariantValue();
        var logicalLayout = properties.TryGetValue("layout-mode", out var layoutMode)
            && layoutMode.GetUInt32() == LogicalLayoutMode;
        return ComputeDesktop(logical, logicalLayout);
    };

    public static async Task<AtSpiScreenRect?> TryGetLogicalDesktopAsync(CancellationToken ct)
    {
        try
        {
            var address = DBusAddress.Session;
            if (address is null)
            {
                return null;
            }

            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);

            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: Service,
                    path: ObjectPath,
                    @interface: Service,
                    member: "GetCurrentState"
                );
                message = writer.CreateMessage();
            }

            return await connection
                .CallMethodAsync(message, s_readLogicalDesktop)
                .WaitAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Placement hint only: no DisplayConfig (not Mutter), a timeout or a malformed reply
            // just leaves the caller on its fallback spot.
            return null;
        }
    }

    /// <summary>Union of the logical monitors' boxes, or null when there are none.</summary>
    internal static AtSpiScreenRect? ComputeDesktop(IReadOnlyList<LogicalMonitor> monitors, bool logicalLayout)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var monitor in monitors)
        {
            // Odd transforms (90°/270°, flipped or not) swap the mode's width and height.
            var rotated = monitor.Transform % 2 == 1;
            var width = rotated ? monitor.ModeHeight : monitor.ModeWidth;
            var height = rotated ? monitor.ModeWidth : monitor.ModeHeight;
            if (logicalLayout && monitor.Scale > 0)
            {
                width = (int)Math.Round(width / monitor.Scale);
                height = (int)Math.Round(height / monitor.Scale);
            }

            left = Math.Min(left, monitor.X);
            top = Math.Min(top, monitor.Y);
            right = Math.Max(right, monitor.X + width);
            bottom = Math.Max(bottom, monitor.Y + height);
        }

        return right > left && bottom > top
            ? new AtSpiScreenRect(left, top, right - left, bottom - top)
            : null;
    }

    private static string ReadMonitorSpec(ref Reader reader)
    {
        reader.AlignStruct();
        var connector = reader.ReadString();
        reader.ReadString(); // vendor
        reader.ReadString(); // product
        reader.ReadString(); // serial
        return connector;
    }
}
