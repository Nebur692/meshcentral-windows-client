using System.IO;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

/// <summary>Remembered mappings, SSH profiles and trusted SSH identities. / Redirecciones recordadas, perfiles SSH e identidades de confianza.</summary>
public sealed class RelayStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mcc-relays-");
    private const string A = "https://a.example/", B = "https://b.example/";

    public void Dispose() => _dir.Delete(true);

    private RelayStore Store() => new RelayStore(_dir.FullName).Reload();

    [Fact]
    public void An_identity_is_new_then_known_and_a_different_one_is_a_change()
    {
        var s = Store();
        var k1 = new SshHostKey("ssh-ed25519", "SHA256:one");
        Assert.Equal(HostKeyVerdict.New, s.Check(A, "node/1", null, 22, k1));
        s.Trust(A, "node/1", null, 22, k1);
        Assert.Equal(HostKeyVerdict.Known, Store().Check(A, "node/1", "", 22, k1)); // saved, and "" is the device itself / guardado, y "" es el propio equipo
        Assert.Equal(HostKeyVerdict.Changed, Store().Check(A, "node/1", null, 22, new SshHostKey("ssh-ed25519", "SHA256:two")));
    }

    [Fact]
    public void Trusting_the_new_identity_replaces_the_old_one()
    {
        var s = Store();
        s.Trust(A, "node/1", null, 22, new SshHostKey("ssh-ed25519", "SHA256:one"));
        s.Trust(A, "node/1", null, 22, new SshHostKey("ssh-ed25519", "SHA256:two"));
        Assert.Equal(HostKeyVerdict.Changed, Store().Check(A, "node/1", null, 22, new SshHostKey("ssh-ed25519", "SHA256:one")));
        Assert.Equal(HostKeyVerdict.Known, Store().Check(A, "node/1", null, 22, new SshHostKey("ssh-ed25519", "SHA256:two")));
    }

    [Fact]
    public void Identities_are_per_server_device_machine_and_port()
    {
        var s = Store();
        var k = new SshHostKey("ssh-ed25519", "SHA256:one");
        s.Trust(A, "node/1", null, 22, k);
        Assert.Equal(HostKeyVerdict.New, s.Check(B, "node/1", null, 22, k));
        Assert.Equal(HostKeyVerdict.New, s.Check(A, "node/2", null, 22, k));
        Assert.Equal(HostKeyVerdict.New, s.Check(A, "node/1", "10.0.0.5", 22, k));
        Assert.Equal(HostKeyVerdict.New, s.Check(A, "node/1", null, 2222, k));
    }

    [Fact]
    public void Remembered_mappings_come_back_per_server_and_go_when_forgotten()
    {
        var s = Store();
        var t = new PortTarget("node/1", "PC1", 3389, null, 0);
        s.Remember(A, t, 50123);
        s.Remember(A, t, 50124); // the same target again replaces it / el mismo destino otra vez lo sustituye
        s.Remember(B, new PortTarget("node/9", "PC9", 22, "10.0.0.5"), 0);
        var back = Store().MappingsOf(A);
        Assert.Equal(50124, Assert.Single(back).LocalPort);
        Assert.Equal(new PortTarget("node/1", "PC1", 3389, null, 50124), back[0].Target);
        s.Forget(A, t);
        Assert.Empty(Store().MappingsOf(A));
        Assert.Single(Store().MappingsOf(B));
    }

    [Fact]
    public void The_ssh_profile_keeps_how_to_sign_in_but_never_a_password()
    {
        var s = Store();
        s.RememberSsh(new SshProfile { Server = A, NodeId = "node/1", Host = "10.0.0.5", Port = 2222, User = "ana", KeyPath = "C:\\k\\id_ed25519" });
        var p = Store().SshOf(A, "node/1")!;
        Assert.Equal(("10.0.0.5", 2222, "ana", "C:\\k\\id_ed25519"), (p.Host, p.Port, p.User, p.KeyPath));
        Assert.Null(Store().SshOf(B, "node/1"));
        Assert.DoesNotContain("assword", File.ReadAllText(Path.Combine(_dir.FullName, "relays.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_file_starts_empty_instead_of_failing()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "relays.json"), "{ not json");
        Assert.Empty(Store().MappingsOf(A));
    }
}
