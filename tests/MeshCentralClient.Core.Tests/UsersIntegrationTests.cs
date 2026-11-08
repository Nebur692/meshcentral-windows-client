using System.Collections.Concurrent;
using MeshCentralClient.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Users, user groups and the own account against the real server. Every test makes its own accounts (never "user":
/// other test classes, running at the same time, count on it having no rights) and deletes them at the end.
/// Each change is checked where it lands: the list the server gives afterwards, the live event, and signing in.
/// Usuarios, grupos de usuarios y la propia cuenta contra el servidor real. Cada prueba hace sus propias cuentas (nunca
/// «user»: otras clases de prueba, a la vez, cuentan con que no tenga permisos) y las borra al final. Cada cambio se
/// comprueba donde llega: la lista que da luego el servidor, el aviso en vivo y el entrar con la cuenta.
/// </summary>
[Trait("Category", "Integration")]
public class UsersIntegrationTests(ITestOutputHelper output)
{
    private static string Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"{n} is not set: run tests/server/run-integration.sh");

    private static ServerAddress Server => ServerAddress.TryParse(Env("MESHTEST_URL"), out var a) ? a! : throw new InvalidOperationException();
    private static CertificateTrust Pinned => new(Env("MESHTEST_CERT_SHA256"));
    private static string Tag() => Guid.NewGuid().ToString("N")[..6];

    private static async Task<MeshServerSession> As(string user, string pass, string? code = null)
    {
        var s = await MeshServerSession.ConnectAsync(Server, new(user, pass, code), Pinned);
        await s.GetDeviceGroupsAsync(); // the session's groups, kept live from here / los grupos de la sesión, al día desde aquí
        return s;
    }

    private static async Task<UserInfo> Listed(MeshServerSession admin, string id) => (await admin.GetUsersAsync()).Single(u => u.Id == id);

    private static async Task DeleteIfThere(MeshServerSession admin, string id)
    {
        if ((await admin.GetUsersAsync()).FirstOrDefault(u => u.Id == id) is { } u) await admin.DeleteUserAsync(u);
    }

    [Fact]
    public async Task An_administrator_creates_edits_locks_resets_and_deletes_an_account()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var seen = new ConcurrentQueue<UserChangedEventArgs>();
        admin.UserChanged += (_, e) => seen.Enqueue(e);
        var name = "ana" + Tag();
        var id = await admin.CreateUserAsync(new(name, "Ana-Pass-1234", "ana@example.org", "Ana Pérez", (long)SiteRight.NoNewDeviceGroups));
        try
        {
            var added = await GroupsIntegrationTests.Until(() => seen.FirstOrDefault(e => e.Kind == UserChangeKind.Added && e.UserId == id), "the accountcreate event");
            Assert.Equal(name, added.User!.Name);
            var u = await Listed(admin, id);
            Assert.Equal(("ana@example.org", "Ana Pérez", (long)SiteRight.NoNewDeviceGroups, false), (u.Email, u.RealName, u.SiteRights, u.IsLocked));
            Assert.NotNull(u.Created);
            var dup = await Assert.ThrowsAsync<MeshClientException>(() => admin.CreateUserAsync(new(name, "Other-Pass-1234")));
            Assert.Equal("usuarios/gestionar-usuarios/conflicto-estado", dup.BurburitoKey);

            // The account signs in, and it cannot create device groups (the right just given). / Entra, y no puede crear grupos.
            await using (var ana = await As(name, "Ana-Pass-1234"))
            {
                Assert.False(ana.CanCreateGroups);
                Assert.False(ana.CanManageUsers);
                var denied = await Assert.ThrowsAsync<MeshClientException>(() => ana.GetUsersAsync());
                Assert.Equal("usuarios/gestionar-usuarios/no-autorizado", denied.BurburitoKey);
            }

            await admin.EditUserAsync(u, "ana.p@example.org", "Ana P.", (long)SiteRight.ManageUserGroups);
            var changed = await GroupsIntegrationTests.Until(() => seen.FirstOrDefault(e => e.Kind == UserChangeKind.Changed && e.UserId == id && e.User!.Email == "ana.p@example.org"), "the accountchange event");
            Assert.Equal(("Ana P.", (long)SiteRight.ManageUserGroups), (changed.User!.RealName, changed.User.SiteRights));
            u = await Listed(admin, id);
            Assert.Equal(("ana.p@example.org", "Ana P.", (long)SiteRight.ManageUserGroups), (u.Email, u.RealName, u.SiteRights));

            // Locked: it cannot sign in; unlocked: it can again. / Bloqueada no entra; desbloqueada vuelve a entrar.
            await admin.SetLockedAsync(u, true);
            u = await Listed(admin, id);
            Assert.True(u.IsLocked);
            var locked = await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Ana-Pass-1234"));
            output.WriteLine($"locked sign-in: {locked.Failure} {locked.BurburitoKey}");
            await admin.SetLockedAsync(u, false);
            u = await Listed(admin, id);
            Assert.False(u.IsLocked);

            // A new password, confirmed by the server's event: the old one no longer works, the new one does.
            // Contraseña nueva, confirmada por el aviso del servidor: la vieja ya no sirve y la nueva sí.
            await admin.SetUserPasswordAsync(u, "Ana-New-5678", mustChange: false, removeTwoFactor: true);
            await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Ana-Pass-1234"));
            await using (var again = await As(name, "Ana-New-5678")) Assert.Equal(id, again.User.Id);

            await admin.DeleteUserAsync(await Listed(admin, id));
            await GroupsIntegrationTests.Until(() => seen.FirstOrDefault(e => e.Kind == UserChangeKind.Removed && e.UserId == id), "the accountremove event");
            Assert.DoesNotContain(await admin.GetUsersAsync(), x => x.Id == id);
            await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Ana-New-5678"));

            var self = await Assert.ThrowsAsync<MeshClientException>(() => admin.DeleteUserAsync(admin.User));
            Assert.Equal("usuarios/gestionar-usuarios/conflicto-estado", self.BurburitoKey);
        }
        finally { await DeleteIfThere(admin, id); }
    }

    [Fact]
    public async Task Someone_who_manages_users_but_is_not_a_full_administrator_is_stopped_where_the_server_would_say_ok_and_do_nothing()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var t = Tag();
        var managerId = await admin.CreateUserAsync(new("jefe" + t, "Jefe-Pass-1234", SiteRights: (long)SiteRight.ManageUsers));
        var otherId = await admin.CreateUserAsync(new("otro" + t, "Otro-Pass-1234"));
        try
        {
            await using var manager = await As("jefe" + t, "Jefe-Pass-1234");
            Assert.True(manager.CanManageUsers);
            var other = (await manager.GetUsersAsync()).Single(u => u.Id == otherId);

            // Allowed for them: lock. / Lo que sí puede: bloquear.
            await manager.SetLockedAsync(other, true);
            Assert.True((await Listed(admin, otherId)).IsLocked);

            // Not allowed: server backup. Sent anyway, the server answers "ok" and changes nothing (measured here);
            // the app stops it before. / No puede: copia del servidor. Si se manda, el servidor dice «ok» y no cambia nada.
            other = (await manager.GetUsersAsync()).Single(u => u.Id == otherId);
            Assert.False(MeshServerSession.CanSetSiteRights(manager.User, other, other.SiteRights | (long)SiteRight.ServerBackup));
            var denied = await Assert.ThrowsAsync<MeshClientException>(() => manager.EditUserAsync(other, null, null, other.SiteRights | (long)SiteRight.ServerBackup));
            Assert.Equal("usuarios/gestionar-usuarios/no-autorizado", denied.BurburitoKey);

            // Nor a full administrator's account, nor their own rights. / Ni la cuenta de un administrador completo, ni sus propios permisos.
            var adminSeen = (await manager.GetUsersAsync()).Single(u => u.Id == admin.User.Id);
            Assert.False(manager.CanEditUser(adminSeen));
            denied = await Assert.ThrowsAsync<MeshClientException>(() => manager.SetUserPasswordAsync(adminSeen, "Whatever-1234", false, false));
            Assert.Equal("usuarios/gestionar-usuarios/no-autorizado", denied.BurburitoKey);
            denied = await Assert.ThrowsAsync<MeshClientException>(() => manager.EditUserAsync(manager.User, null, null, (long)SiteRight.FullAdministrator));
            Assert.Equal("usuarios/gestionar-usuarios/no-autorizado", denied.BurburitoKey);
            Assert.Equal((long)SiteRight.ManageUsers, (await Listed(admin, managerId)).SiteRights);
        }
        finally
        {
            await DeleteIfThere(admin, otherId);
            await DeleteIfThere(admin, managerId);
        }
    }

    [Fact]
    public async Task A_user_group_gets_members_and_its_rights_reach_them_live()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var t = Tag();
        var memberId = await admin.CreateUserAsync(new("miembro" + t, "Miembro-Pass-1234"));
        var seen = new ConcurrentQueue<UserGroupChangedEventArgs>();
        admin.UserGroupChanged += (_, e) => seen.Enqueue(e);
        string? groupId = null;
        try
        {
            groupId = await admin.CreateUserGroupAsync("Técnicos " + t, "made by a test");
            var created = await GroupsIntegrationTests.Until(() => seen.FirstOrDefault(e => e.Kind == UserChangeKind.Added && e.GroupId == groupId), "the createusergroup event");
            Assert.Equal("Técnicos " + t, created.Group!.Name);
            var g = (await admin.GetUserGroupsAsync()).Single(x => x.Id == groupId);
            Assert.Equal(("made by a test", 0), (g.Description, g.Members.Count));

            // One known name and one unknown: the known one is added, the other is named back. / Uno conocido y uno no.
            var unknown = await Assert.ThrowsAsync<MeshClientException>(() => admin.AddUsersToUserGroupAsync(g, ["miembro" + t, "nadie" + t]));
            Assert.Equal("usuarios/gestionar-grupos-usuarios/no-encontrado", unknown.BurburitoKey);
            Assert.Contains("1 added", unknown.Message, StringComparison.Ordinal);
            g = (await admin.GetUserGroupsAsync()).Single(x => x.Id == groupId);
            Assert.Equal(["miembro" + t], g.Members.Values);
            Assert.Contains(groupId, (await Listed(admin, memberId)).UserGroupIds);

            // Renamed: the server never answers, the app waits for the event. / Renombrado: el servidor no contesta, se espera el aviso.
            await admin.EditUserGroupAsync(g, "Soporte " + t, "renamed");
            g = (await admin.GetUserGroupsAsync()).Single(x => x.Id == groupId);
            Assert.Equal(("Soporte " + t, "renamed"), (g.Name, g.Description));

            // Rights for the group on the test device group reach the member live. / Los permisos del grupo llegan al miembro en vivo.
            await using var member = await As("miembro" + t, "Miembro-Pass-1234");
            var testGroup = admin.GroupById(Env("MESHTEST_GROUP_ID"))!;
            Assert.Null(member.GroupById(testGroup.Id));
            var said = await admin.AddUserGroupAsync(testGroup, g, (long)(MeshRights.DeviceDetails | MeshRights.Wake));
            output.WriteLine(said);
            var hers = await GroupsIntegrationTests.Until(() => member.GroupById(testGroup.Id), "the device group reaching the member through the user group");
            Assert.Equal((long)(MeshRights.DeviceDetails | MeshRights.Wake), MeshServerSession.GroupRights(member.User, member.ServerInfo, hers));

            // Out of the user group, out of the device group. / Fuera del grupo de usuarios, fuera del grupo de equipos.
            await admin.RemoveUserFromUserGroupAsync(g, memberId);
            await GroupsIntegrationTests.Until(() => member.GroupById(testGroup.Id) is null ? "gone" : null, "the device group leaving the member");
            Assert.Empty((await admin.GetUserGroupsAsync()).Single(x => x.Id == groupId).Members);

            await admin.DeleteUserGroupAsync(g);
            await GroupsIntegrationTests.Until(() => seen.FirstOrDefault(e => e.Kind == UserChangeKind.Removed && e.GroupId == groupId), "the deleteusergroup event");
            Assert.DoesNotContain(await admin.GetUserGroupsAsync(), x => x.Id == groupId);
            Assert.DoesNotContain(admin.GroupById(testGroup.Id)!.Links.Keys, k => k == groupId);
            groupId = null;
        }
        finally
        {
            if (groupId is not null && (await admin.GetUserGroupsAsync()).FirstOrDefault(x => x.Id == groupId) is { } left) await admin.DeleteUserGroupAsync(left);
            await DeleteIfThere(admin, memberId);
        }
    }

    [Fact]
    public async Task A_person_changes_their_password_adds_an_authenticator_backup_codes_and_a_login_token()
    {
        await using var admin = await As("admin", "Admin-Pass-1234");
        var name = "yo" + Tag();
        var id = await admin.CreateUserAsync(new(name, "Mine-Pass-1234"));
        try
        {
            await using (var me = await As(name, "Mine-Pass-1234"))
            {
                var wrong = await Assert.ThrowsAsync<MeshClientException>(() => me.ChangePasswordAsync("not-it-1234", "Mine-New-5678"));
                Assert.Equal("usuarios/cambiar-ajustes/credencial-invalida", wrong.BurburitoKey);
                await me.ChangePasswordAsync("Mine-Pass-1234", "Mine-New-5678");
            }
            await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Mine-Pass-1234"));
            await using var s = await As(name, "Mine-New-5678");

            // Backup codes need a second factor first: the server would not answer. / Los códigos piden antes otro factor.
            var none = await Assert.ThrowsAsync<MeshClientException>(() => s.BackupCodesAsync(BackupCodeAction.New));
            Assert.Equal("usuarios/cambiar-ajustes/conflicto-estado", none.BurburitoKey);
            Assert.Empty(await s.GetSecurityKeysAsync());

            var setup = await s.StartAuthenticatorAsync();
            output.WriteLine(setup.Uri);
            Assert.StartsWith("otpauth://totp/", setup.Uri, StringComparison.Ordinal);
            var bad = await Assert.ThrowsAsync<MeshClientException>(() => s.ConfirmAuthenticatorAsync(setup, "000000" == Totp.Code(setup.Secret) ? "111111" : "000000"));
            Assert.Equal("usuarios/cambiar-ajustes/credencial-invalida", bad.BurburitoKey);
            await s.ConfirmAuthenticatorAsync(setup, Totp.Code(setup.Secret));
            await GroupsIntegrationTests.Until(() => s.User.HasAuthenticator ? "yes" : null, "the own account showing the authenticator");
            Assert.True((await Listed(admin, id)).HasAuthenticator);

            // Signing in now asks for the code. / Entrar ahora pide el código.
            var asked = await Assert.ThrowsAsync<MeshClientException>(() => As(name, "Mine-New-5678"));
            Assert.Equal(MeshFailure.TwoFactorRequired, asked.Failure);
            await using (var withCode = await As(name, "Mine-New-5678", Totp.Code(setup.Secret))) Assert.Equal(id, withCode.User.Id);

            var codes = await s.BackupCodesAsync(BackupCodeAction.New);
            Assert.Equal(10, codes.Count);
            Assert.All(codes, c => Assert.Matches("^[0-9]{8}$", c.Code));
            // The account only says that some code is unused, not how many. / La cuenta solo dice que queda alguno, no cuántos.
            await GroupsIntegrationTests.Until(() => s.User.HasBackupCodes ? "yes" : null, "the own account showing backup codes");
            Assert.Equal(codes.Select(c => c.Code), (await s.BackupCodesAsync(BackupCodeAction.Show)).Select(c => c.Code));
            // A backup code signs in instead of the app's code. / Un código de respaldo sirve para entrar en vez del de la app.
            await using (var withBackup = await As(name, "Mine-New-5678", codes[0].Code)) Assert.Equal(id, withBackup.User.Id);
            Assert.True((await s.BackupCodesAsync(BackupCodeAction.Show))[0].Used);
            Assert.Empty(await s.BackupCodesAsync(BackupCodeAction.Clear));
            await GroupsIntegrationTests.Until(() => s.User.HasBackupCodes ? null : "gone", "the backup codes leaving the own account");

            var token = await s.CreateLoginTokenAsync("script " + Tag(), TimeSpan.FromHours(1));
            Assert.False(string.IsNullOrEmpty(token.Password));
            Assert.NotNull(token.Expires);
            Assert.Contains(await s.GetLoginTokensAsync(), x => x.TokenUser == token.TokenUser && x.Password is null);
            // A login token signs in without the second factor, as the account. / Un token entra sin segundo factor, como la cuenta.
            await using (var viaToken = await As(token.TokenUser, token.Password!)) Assert.Equal(id, viaToken.User.Id);
            Assert.DoesNotContain(await s.RemoveLoginTokenAsync(token), x => x.TokenUser == token.TokenUser);
            await Assert.ThrowsAsync<MeshClientException>(() => As(token.TokenUser, token.Password!));

            await s.RemoveAuthenticatorAsync();
            await GroupsIntegrationTests.Until(() => s.User.HasAuthenticator ? null : "gone", "the authenticator leaving the own account");
            var nothing = await Assert.ThrowsAsync<MeshClientException>(() => s.RemoveAuthenticatorAsync());
            Assert.Equal("usuarios/cambiar-ajustes/conflicto-estado", nothing.BurburitoKey);
            await using (var plain = await As(name, "Mine-New-5678")) Assert.Equal(id, plain.User.Id);
        }
        finally { await DeleteIfThere(admin, id); }
    }
}
