using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class WindowsShellIdentityServiceTests
{
    [Fact]
    public void AppUserModelId_is_stable_and_shell_safe()
    {
        Assert.Equal("Hbpos.Client.Wpf", WindowsShellIdentityService.AppUserModelId);
        Assert.DoesNotContain(' ', WindowsShellIdentityService.AppUserModelId);
    }

    [Fact]
    public void GetApplicationExecutablePath_returns_current_process_path()
    {
        var executablePath = WindowsShellIdentityService.GetApplicationExecutablePath();

        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        Assert.True(Path.IsPathFullyQualified(executablePath));
    }

    [Fact]
    public void ResolveApplicationExecutablePath_prefers_wpf_exe_next_to_entry_dll()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var appExePath = Path.Combine(tempDirectory.FullName, "Hbpos.Client.Wpf.exe");
            File.WriteAllText(appExePath, string.Empty);

            var resolvedPath = WindowsShellIdentityService.ResolveApplicationExecutablePath(
                @"C:\Program Files\dotnet\dotnet.exe",
                Path.Combine(tempDirectory.FullName, "Hbpos.Client.Wpf.dll"),
                tempDirectory.FullName);

            Assert.Equal(appExePath, resolvedPath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveApplicationExecutablePath_prefers_base_directory_wpf_exe_over_entry_assembly_path()
    {
        var baseDirectory = Directory.CreateTempSubdirectory();
        var entryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var appExePath = Path.Combine(baseDirectory.FullName, "Hbpos.Client.Wpf.exe");
            var entryExePath = Path.Combine(entryDirectory.FullName, "Hbpos.Client.Wpf.exe");
            File.WriteAllText(appExePath, string.Empty);
            File.WriteAllText(entryExePath, string.Empty);

            var resolvedPath = WindowsShellIdentityService.ResolveApplicationExecutablePath(
                @"C:\Program Files\dotnet\dotnet.exe",
                Path.Combine(entryDirectory.FullName, "Hbpos.Client.Wpf.dll"),
                baseDirectory.FullName);

            Assert.Equal(appExePath, resolvedPath);
        }
        finally
        {
            baseDirectory.Delete(recursive: true);
            entryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveApplicationExecutablePath_uses_wpf_exe_from_base_directory_when_entry_is_host()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var appExePath = Path.Combine(tempDirectory.FullName, "Hbpos.Client.Wpf.exe");
            File.WriteAllText(appExePath, string.Empty);

            var resolvedPath = WindowsShellIdentityService.ResolveApplicationExecutablePath(
                @"C:\Program Files\dotnet\dotnet.exe",
                @"C:\Program Files\dotnet\dotnet.dll",
                tempDirectory.FullName);

            Assert.Equal(appExePath, resolvedPath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildRelaunchCommand_quotes_executable_path()
    {
        var command = WindowsShellIdentityService.BuildRelaunchCommand(
            @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe");

        Assert.Equal("\"C:\\Program Files\\HB POS\\Hbpos.Client.Wpf.exe\"", command);
    }

    [Fact]
    public void BuildRelaunchIconResource_uses_first_exe_icon()
    {
        var iconResource = WindowsShellIdentityService.BuildRelaunchIconResource(
            @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe");

        Assert.Equal(@"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe,0", iconResource);
    }
}
