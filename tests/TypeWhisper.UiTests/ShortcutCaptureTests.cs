using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ShortcutCaptureTests(UiSession ui)
{
    [Theory]
    [InlineData("detach")]
    [InlineData("focus")]
    [InlineData("hide")]
    public async Task LeavingCaptureResumesShortcuts(string exit)
    {
        await ui.Session.Dispatch(() =>
        {
            var vm = ui.Services.GetRequiredService<ShortcutsSectionViewModel>();
            var hotkey = ui.Services.GetRequiredService<HotkeyService>();
            var section = new ShortcutsSection { DataContext = vm };
            var other = new Button { Content = "Other" };
            var panel = new StackPanel { Children = { section, other } };
            var window = new Window { Content = panel };
            window.Show();
            section.Focus();
            vm.StartHotkeyCaptureCommand.Execute(null);
            Assert.True(vm.IsCapturingHotkey);
            Assert.True(hotkey.IsSuspended);
            if (exit == "detach") panel.Children.Remove(section);
            if (exit == "focus") other.Focus();
            if (exit == "hide") window.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsCapturingHotkey);
            Assert.False(hotkey.IsSuspended);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SocketStartAndToggleReturnCaptureErrorWithoutRecording()
    {
        var services = ui.Services;
        var hotkey = services.GetRequiredService<HotkeyService>();
        using var server = new ControlSocketServer(services.GetRequiredService<DictationOrchestrator>(), hotkey, null);
        var path = SocketPathResolver.ResolveControlSocketPath();
        try
        {
            hotkey.IsSuspended = true;
            server.Start();
            foreach (var command in new[] { "record.start", "record.toggle" })
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
                using var stream = new NetworkStream(client);
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"v\":1,\"cmd\":\"{command}\"}}\n"));
                using var reader = new StreamReader(stream);
                var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
                using var json = JsonDocument.Parse(response!);
                Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("shortcut_capture_active", json.RootElement.GetProperty("error").GetString());
                Assert.False(services.GetRequiredService<DictationOrchestrator>().IsRecording);
            }
        }
        finally
        {
            hotkey.IsSuspended = false;
        }
    }
}
