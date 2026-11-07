using System.IO;
using MeshCentralClient.App;
using Xunit;

// Changes process-wide variables: kept in one class, so its tests do not run at the same time as each other.
// Cambia variables de todo el proceso: van en una sola clase para que sus pruebas no corran a la vez entre sí.
public class AppPathsTests
{
    [Fact]
    public void Honours_the_variables_and_falls_back_to_windows()
    {
        var roaming = Environment.GetEnvironmentVariable("APPDATA");
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", @"C:\portable\roaming");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", @"C:\portable\local");
            Assert.Equal(@"C:\portable\roaming\MeshCentralClient", AppPaths.Roaming);
            Assert.Equal(@"C:\portable\local\MeshCentralClient", AppPaths.Local);

            // A relative or empty value is ignored. / Un valor relativo o vacío se ignora.
            Environment.SetEnvironmentVariable("APPDATA", @"relative\path");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", "");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeshCentralClient"), AppPaths.Roaming);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCentralClient"), AppPaths.Local);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", roaming);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
        }
    }
}
