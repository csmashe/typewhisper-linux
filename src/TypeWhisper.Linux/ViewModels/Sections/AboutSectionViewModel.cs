using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AboutSectionViewModel : ObservableObject
{
    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    public string RuntimeVersion { get; } = Environment.Version.ToString();

    public string OsDescription { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    public string Architecture { get; } =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    public string ProjectUrl { get; } = "https://github.com/csmashe/typewhisper-linux";

    public string UpstreamUrl { get; } = "https://github.com/TypeWhisper/typewhisper-win";
}
