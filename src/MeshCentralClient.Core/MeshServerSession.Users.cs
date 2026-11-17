using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>Server-wide rights of an account (siteadmin), as the website's "Edit User Permissions" dialog.
/// Permisos de una cuenta en todo el servidor (siteadmin), como el diálogo «Permisos del usuario» de la web.</summary>
[Flags]
public enum SiteRight : long
{
    None = 0,
    ServerBackup = 1,
    ManageUsers = 2,
    ServerRestore = 4,
    ServerFiles = 8,
    ServerUpdates = 16,
    Locked = 32,
    NoNewDeviceGroups = 64,
    NoTools = 128,
    ManageUserGroups = 256,
    ManageRecordings = 512,
    LockSettings = 1024,
    AllEvents = 2048,
    NoNewDevices = 4096,
    FullAdministrator = 0xFFFFFFFF,
}

/// <summary>A group of user accounts; its members get the rights given to the group. / Un grupo de cuentas; sus miembros reciben los permisos dados al grupo.</summary>
public sealed record UserGroup(string Id, string Name, string? Description)
{
    /// <summary>Member account id → name. / Id de la cuenta miembro → nombre.</summary>
    public IReadOnlyDictionary<string, string> Members { get; init; } = new Dictionary<string, string>();

    /// <summary>Device groups and devices this group has rights on. / Grupos de equipos y equipos donde tiene permisos.</summary>
    public IReadOnlyDictionary<string, long> Rights { get; init; } = JsonExtensions.NoLinks;

    /// <summary>Its members come from elsewhere (single sign-on, LDAP) and cannot be changed here.
    /// Sus miembros vienen de fuera (inicio único, LDAP) y no se pueden cambiar aquí.</summary>
    public bool ManagedElsewhere { get; init; }

    internal static UserGroup From(string id, JsonObject o)
    {
        var members = new Dictionary<string, string>();
        var rights = new Dictionary<string, long>();
        if (o["links"] is JsonObject links)
            foreach (var (k, v) in links)
            {
                if (v is not JsonObject l) continue;
                if (k.StartsWith("user/", StringComparison.Ordinal)) members[k] = l.Str("name") ?? k.Split('/')[^1];
                else if (l.Long("rights") is long r) rights[k] = r;
            }
        return new(id, o.Str("name") ?? "", o.Str("desc")) { Members = members, Rights = rights, ManagedElsewhere = o["membershipType"] is not null };
    }
}

public enum UserChangeKind { Added, Changed, Removed }

public sealed class UserChangedEventArgs(UserChangeKind kind, string userId, UserInfo? user) : EventArgs
{
    public UserChangeKind Kind { get; } = kind;
    public string UserId { get; } = userId;
    /// <summary>Null when removed. / Nulo cuando se ha quitado.</summary>
    public UserInfo? User { get; } = user;
}

public sealed class UserGroupChangedEventArgs(UserChangeKind kind, string groupId, UserGroup? group) : EventArgs
{
    public UserChangeKind Kind { get; } = kind;
    public string GroupId { get; } = groupId;
    public UserGroup? Group { get; } = group;
}

/// <summary>A new account. / Una cuenta nueva.</summary>
/// <param name="EmailVerified">The email is already known to be theirs, as the website's "Email verified" box: with a mail
/// server, MeshCentral asks for a verified email to sign in. / El correo ya se sabe suyo, como la casilla de la web: con
/// servidor de correo, MeshCentral pide el correo verificado para entrar.</param>
public sealed record NewUser(string UserName, string Password, string? Email = null, string? RealName = null,
    long? SiteRights = null, bool MustChangePassword = false, bool EmailVerified = false);

/// <summary>One of the ten backup codes; each works once. / Uno de los diez códigos de respaldo; cada uno sirve una vez.</summary>
public sealed record BackupCode(string Code, bool Used);

public enum BackupCodeAction { Show = 0, New = 1, Clear = 2 }

/// <summary>Index: MeshCentral's keyIndex, an unsigned 32-bit number (did not fit an int). / Índice: el keyIndex de MeshCentral, de 32 bits sin signo.</summary>
public sealed record SecurityKey(long Index, string Name, string? Type)
{
    internal static SecurityKey From(JsonObject k) => new(k.Long("i") ?? 0, k.Str("name") ?? "", k.Str("type"));
}

/// <summary>A login token: a second user name and password for scripts and tools, tied to this account.
/// Un token de acceso: otro usuario y contraseña para guiones y herramientas, atado a esta cuenta.</summary>
public sealed record LoginToken(string Name, string TokenUser, DateTimeOffset Created, DateTimeOffset? Expires)
{
    /// <summary>Only right after creating it; the server keeps just a hash. / Solo justo al crearlo; el servidor guarda solo un resumen.</summary>
    public string? Password { get; init; }

    internal static LoginToken From(JsonObject o) => new(o.Str("name") ?? "", o.Str("tokenUser") ?? "",
        DateTimeOffset.FromUnixTimeMilliseconds(o.Long("created") ?? 0),
        o.Long("expire") is long e and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e) : null) { Password = o.Str("tokenPass") };
}

/// <summary>The authenticator app to add: its secret and the otpauth:// address a phone reads from a QR code.
/// La app de autenticación por añadir: su secreto y la dirección otpauth:// que lee un móvil de un código QR.</summary>
public sealed record AuthenticatorSetup(string Secret, string Uri);

/// <summary>
/// Users, user groups and the own account, measured on MeshCentral 1.3.1 (meshuser.js). Several requests are dropped
/// without any answer when they are not allowed (edituser outside the rights mask, changeuserpass, editusergroup,
/// otpauth-getpasswords without two-factor), so the rights are checked first, as the website does, and the ones that never
/// answer are confirmed by the live event they cause.
/// Usuarios, grupos de usuarios y la propia cuenta, medido en MeshCentral 1.3.1. Varias peticiones se tiran sin contestar
/// cuando no se pueden hacer, así que los permisos se comprueban antes, como la web, y las que nunca contestan se
/// confirman con el aviso en vivo que provocan.
/// </summary>
public sealed partial class MeshServerSession
{
    private const long Full = 0xFFFFFFFF;

    /// <summary>Raised for every account this session can see: created, changed, removed. / Para cada cuenta que esta sesión ve.</summary>
    public event EventHandler<UserChangedEventArgs>? UserChanged;
    public event EventHandler<UserGroupChangedEventArgs>? UserGroupChanged;

    private static bool HasSite(long rights, SiteRight r) => rights == Full || (rights & (long)r) != 0;

    public bool CanManageUsers => HasSite(User.SiteRights, SiteRight.ManageUsers);
    public bool CanManageUserGroups => HasSite(User.SiteRights, SiteRight.ManageUserGroups);
    /// <summary>"Lock account settings": the server ignores password and two-factor changes. / El servidor ignora cambios de contraseña y doble factor.</summary>
    public bool OwnSettingsLocked => User.SiteRights != Full && (User.SiteRights & (long)SiteRight.LockSettings) != 0;

    /// <summary>This account may change <paramref name="target"/>: managing users, and only a full administrator touches another one.
    /// Esta cuenta puede cambiar a <paramref name="target"/>: gestiona usuarios, y solo un administrador completo toca a otro.</summary>
    public bool CanEditUser(UserInfo target) => CanEditUser(User, target);

    public static bool CanEditUser(UserInfo me, UserInfo target) =>
        HasSite(me.SiteRights, SiteRight.ManageUsers) && (!target.IsFullAdministrator || me.IsFullAdministrator);

    /// <summary>
    /// The server-wide rights <paramref name="me"/> may set on another account: a full administrator, any; someone who
    /// manages users only locks, "no new groups", "no tools", and the user-group and recording rights they hold
    /// themselves (meshuser.js edituser mask). Outside that the server answers "ok" and changes nothing.
    /// Los permisos de servidor que <paramref name="me"/> puede poner a otra cuenta: un administrador completo, todos; quien
    /// gestiona usuarios solo bloquear, «sin grupos nuevos», «sin herramientas» y los de grupos de usuarios y grabaciones que
    /// tenga él. Fuera de eso el servidor contesta «ok» y no cambia nada.
    /// </summary>
    public static bool CanSetSiteRights(UserInfo me, UserInfo target, long newRights)
    {
        if (!CanEditUser(me, target) || target.Id == me.Id) return false;
        if (me.IsFullAdministrator) return true;
        long mask = 0xFFFFFF1D;
        if ((me.SiteRights & (long)SiteRight.ManageUserGroups) != 0) mask -= (long)SiteRight.ManageUserGroups;
        if ((me.SiteRights & (long)SiteRight.ManageRecordings) != 0) mask -= (long)SiteRight.ManageRecordings;
        return ((target.SiteRights ^ newRights) & mask) == 0;
    }

    /// <summary>The rights a new account may get from <paramref name="me"/> (adduser: only locked, no new groups and no tools unless full administrator).
    /// Los permisos que una cuenta nueva puede recibir de <paramref name="me"/>.</summary>
    public static bool CanGiveNewUserRights(UserInfo me, long rights) => me.IsFullAdministrator || (rights & 0xFFFFFF1F) == 0;

    private static MeshClientException UserError(string key, string message, string? said = null) =>
        new(MeshFailure.Rejected, key, message) { ServerMessage = said };

    private Task<JsonObject> AnsweredRequest(JsonObject cmd, string timeoutKey, string lostKey, CancellationToken ct)
    {
        var id = NewId();
        cmd["responseid"] = id;
        cmd["tag"] = id;
        var verb = cmd.Str("action");
        return RequestAsync(cmd, o => o.Str("action") == verb && (o.Str("responseid") == id || o.Str("tag") == id), timeoutKey, lostKey, ct);
    }

    /// <summary>For requests the server never answers: sent, then confirmed by the live event they cause.
    /// Para las peticiones que el servidor nunca contesta: se mandan y se confirman con el aviso que provocan.</summary>
    private Task<JsonObject> ConfirmedByEvent(JsonObject cmd, Func<JsonObject, bool> isTheEvent, string timeoutKey, string lostKey, CancellationToken ct) =>
        RequestAsync(cmd, o => o.Str("action") == "event" && o["event"] is JsonObject e && isTheEvent(e), timeoutKey, lostKey, ct);

    private string DomainOfUsers => User.Id.Split('/') is { Length: 3 } p ? p[1] : "";

    // ------------------------------------------------------------------ users / usuarios

    public async Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default)
    {
        if (!CanManageUsers) throw UserError("usuarios/gestionar-usuarios/no-autorizado", "This account cannot manage users.");
        var r = await AnsweredRequest(new JsonObject { ["action"] = "users" }, "usuarios/gestionar-usuarios/tiempo-agotado", "usuarios/gestionar-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r["users"] is not JsonArray users)
            throw UserError("usuarios/gestionar-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
        return users.OfType<JsonObject>().Select(UserInfo.From).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Creates an account and returns its id. / Crea una cuenta y devuelve su id.</summary>
    public async Task<string> CreateUserAsync(NewUser n, CancellationToken ct = default)
    {
        if (!CanManageUsers) throw UserError("usuarios/gestionar-usuarios/no-autorizado", "This account cannot manage users.");
        var name = n.UserName.Trim();
        if (name.Length is 0 or > 64 || name.StartsWith('~') || name.Contains('/') || name.Any(char.IsWhiteSpace) || n.Password.Length is 0 or > 256)
            throw UserError("usuarios/gestionar-usuarios/dato-invalido", "The user name needs 1 to 64 characters without spaces or \"/\", and a password.");
        if (n.SiteRights is long sr && !CanGiveNewUserRights(User, sr))
            throw UserError("usuarios/gestionar-usuarios/no-autorizado", "Only a full administrator gives those server rights.");
        var cmd = new JsonObject { ["action"] = "adduser", ["username"] = name, ["pass"] = n.Password, ["resetNextLogin"] = n.MustChangePassword };
        if (!string.IsNullOrWhiteSpace(n.Email)) { cmd["email"] = n.Email.Trim(); if (n.EmailVerified) cmd["emailVerified"] = true; }
        if (!string.IsNullOrWhiteSpace(n.RealName)) cmd["realname"] = n.RealName.Trim();
        if (n.SiteRights is long rights) cmd["siteadmin"] = rights;
        var r = await AnsweredRequest(cmd, "usuarios/gestionar-usuarios/tiempo-agotado", "usuarios/gestionar-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        var said = r.Str("result");
        if (said == "ok") return $"user/{DomainOfUsers}/{name.ToLowerInvariant()}";
        throw said == "User already exists"
            ? UserError("usuarios/gestionar-usuarios/conflicto-estado", "There is already an account with that name.", said)
            : UserError("usuarios/gestionar-usuarios/rechazo-remoto", $"The server said: {said}.", said);
    }

    /// <summary>Changes email, real name and, for another account, its server rights (null leaves each as it is).
    /// Cambia correo, nombre real y, en otra cuenta, sus permisos de servidor (null deja cada uno como está).</summary>
    public async Task EditUserAsync(UserInfo target, string? email, string? realName, long? siteRights, CancellationToken ct = default)
    {
        if (!CanEditUser(target)) throw UserError("usuarios/gestionar-usuarios/no-autorizado", "This account cannot change that one.");
        if (siteRights is long sr && sr != target.SiteRights && !CanSetSiteRights(User, target, sr))
            throw UserError("usuarios/gestionar-usuarios/no-autorizado",
                target.Id == User.Id ? "Nobody changes their own server rights." : "This account cannot give or take those server rights.");
        if (email is { Length: > 0 } && !email.Contains('@')) throw UserError("usuarios/gestionar-usuarios/dato-invalido", "That is not an email address.");
        var cmd = new JsonObject { ["action"] = "edituser", ["id"] = target.Id };
        if (email is not null) cmd["email"] = email.Trim();
        if (realName is not null) cmd["realname"] = realName.Trim();
        if (siteRights is long rights && rights != target.SiteRights) cmd["siteadmin"] = rights;
        var r = await AnsweredRequest(cmd, "usuarios/gestionar-usuarios/tiempo-agotado", "usuarios/gestionar-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("usuarios/gestionar-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
    }

    /// <summary>Locks (it cannot sign in, its sessions are closed) or unlocks an account. / Bloquea o desbloquea una cuenta.</summary>
    public Task SetLockedAsync(UserInfo target, bool locked, CancellationToken ct = default) =>
        EditUserAsync(target, null, null, locked ? target.SiteRights | (long)SiteRight.Locked : target.SiteRights & ~(long)SiteRight.Locked, ct);

    /// <summary>
    /// Sets another account's password. The server never answers this request (changeuserpass): it is confirmed by the
    /// "Changed account credentials" event; a password the server's rules refuse is dropped and ends in the time-out.
    /// Pone la contraseña de otra cuenta. El servidor nunca contesta: se confirma con el aviso «Changed account credentials»;
    /// una contraseña que sus reglas rechazan se tira y acaba en el tiempo agotado.
    /// </summary>
    public async Task SetUserPasswordAsync(UserInfo target, string password, bool mustChange, bool removeTwoFactor, CancellationToken ct = default)
    {
        if (!CanEditUser(target)) throw UserError("usuarios/gestionar-usuarios/no-autorizado", "This account cannot change that one.");
        if (password.Length is 0 or > 256) throw UserError("usuarios/gestionar-usuarios/dato-invalido", "The password needs 1 to 256 characters.");
        var cmd = new JsonObject
        {
            ["action"] = "changeuserpass", ["userid"] = target.Id, ["pass"] = password,
            ["removeMultiFactor"] = removeTwoFactor, ["resetNextLogin"] = mustChange,
        };
        await ConfirmedByEvent(cmd, e => e.Str("action") == "accountchange" && e.Long("msgid") == 75 && e["account"] is JsonObject a && a.Str("_id") == target.Id,
            "usuarios/gestionar-usuarios/tiempo-agotado", "usuarios/gestionar-usuarios/conexion-cortada", ct).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(UserInfo target, CancellationToken ct = default)
    {
        if (!CanEditUser(target)) throw UserError("usuarios/gestionar-usuarios/no-autorizado", "This account cannot delete that one.");
        if (target.Id == User.Id) throw UserError("usuarios/gestionar-usuarios/conflicto-estado", "An account cannot delete itself from here.");
        var r = await AnsweredRequest(new JsonObject { ["action"] = "deleteuser", ["userid"] = target.Id },
            "usuarios/gestionar-usuarios/tiempo-agotado", "usuarios/gestionar-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("usuarios/gestionar-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
    }

    // ------------------------------------------------------------------ user groups / grupos de usuarios

    /// <summary>Without "manage user groups" the server only gives their names. / Sin ese permiso el servidor solo da sus nombres.</summary>
    public async Task<IReadOnlyList<UserGroup>> GetUserGroupsAsync(CancellationToken ct = default)
    {
        var r = await AnsweredRequest(new JsonObject { ["action"] = "usergroups" },
            "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r["ugroups"] is not JsonObject groups) return [];
        return groups.Where(g => g.Value is JsonObject).Select(g => UserGroup.From(g.Key, (JsonObject)g.Value!))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void CheckUserGroups()
    {
        if (!CanManageUserGroups) throw UserError("usuarios/gestionar-grupos-usuarios/no-autorizado", "This account cannot manage user groups.");
    }

    private static void CheckUserGroupText(string name, string? description)
    {
        if (name.Trim().Length is 0 or > 64 || description is { Length: > 1024 })
            throw UserError("usuarios/gestionar-grupos-usuarios/dato-invalido", "The name needs 1 to 64 characters and the description up to 1024.");
    }

    public async Task<string> CreateUserGroupAsync(string name, string? description = null, CancellationToken ct = default)
    {
        CheckUserGroups();
        CheckUserGroupText(name, description);
        var cmd = new JsonObject { ["action"] = "createusergroup", ["name"] = name.Trim(), ["desc"] = description ?? "" };
        var r = await AnsweredRequest(cmd, "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok" || r.Str("ugrpid") is not { } id)
            throw UserError("usuarios/gestionar-grupos-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
        return id;
    }

    /// <summary>The server never answers editusergroup: confirmed by its "usergroupchange" event.
    /// El servidor nunca contesta editusergroup: se confirma con su aviso «usergroupchange».</summary>
    public async Task EditUserGroupAsync(UserGroup group, string name, string? description, CancellationToken ct = default)
    {
        CheckUserGroups();
        CheckUserGroupText(name, description);
        name = name.Trim();
        description ??= "";
        if (group.ManagedElsewhere && name != group.Name)
            throw UserError("usuarios/gestionar-grupos-usuarios/no-autorizado", "This group comes from single sign-on or LDAP: its name is kept there.");
        if (name == group.Name && description == (group.Description ?? "")) return; // nothing to change: no event would come / nada que cambiar: no llegaría aviso
        var cmd = new JsonObject { ["action"] = "editusergroup", ["ugrpid"] = group.Id, ["name"] = name, ["desc"] = description };
        await ConfirmedByEvent(cmd, e => e.Str("action") == "usergroupchange" && e.Str("ugrpid") == group.Id && e.Str("name") == name && (e.Str("desc") ?? "") == description,
            "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
    }

    public async Task DeleteUserGroupAsync(UserGroup group, CancellationToken ct = default)
    {
        CheckUserGroups();
        var r = await AnsweredRequest(new JsonObject { ["action"] = "deleteusergroup", ["ugrpid"] = group.Id },
            "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("usuarios/gestionar-grupos-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
    }

    /// <summary>Adds accounts by user name; names the server does not know are named back, not dropped.
    /// Añade cuentas por nombre de usuario; los que el servidor no conoce se dicen, no se pierden.</summary>
    public async Task AddUsersToUserGroupAsync(UserGroup group, IReadOnlyList<string> userNames, CancellationToken ct = default)
    {
        CheckUserGroups();
        if (group.ManagedElsewhere) throw UserError("usuarios/gestionar-grupos-usuarios/no-autorizado", "This group's members come from single sign-on or LDAP.");
        var names = userNames.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0 || names.Any(x => x.Length > 128)) throw UserError("usuarios/gestionar-grupos-usuarios/dato-invalido", "Write one or more user names.");
        var cmd = new JsonObject { ["action"] = "addusertousergroup", ["ugrpid"] = group.Id, ["usernames"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray()) };
        var r = await AnsweredRequest(cmd, "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("usuarios/gestionar-grupos-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
        if (r.Long("failed") is > 0)
            throw UserError("usuarios/gestionar-grupos-usuarios/no-encontrado",
                $"{r.Long("failed")} of {names.Count} user name(s) are not accounts of this server; {r.Long("added") ?? 0} added.");
    }

    public async Task RemoveUserFromUserGroupAsync(UserGroup group, string userId, CancellationToken ct = default)
    {
        CheckUserGroups();
        if (group.ManagedElsewhere) throw UserError("usuarios/gestionar-grupos-usuarios/no-autorizado", "This group's members come from single sign-on or LDAP.");
        var r = await AnsweredRequest(new JsonObject { ["action"] = "removeuserfromusergroup", ["ugrpid"] = group.Id, ["userid"] = userId },
            "usuarios/gestionar-grupos-usuarios/tiempo-agotado", "usuarios/gestionar-grupos-usuarios/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("result") != "ok") throw UserError("usuarios/gestionar-grupos-usuarios/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
    }

    // ------------------------------------------------------------------ own account / la propia cuenta

    private void CheckOwnSettings()
    {
        if (OwnSettingsLocked) throw UserError("usuarios/cambiar-ajustes/sin-permiso", "The settings of this account are locked by an administrator.");
    }

    /// <summary>
    /// Changes the own password. The server answers with a notification (msgid 20 changed, 21 wrong current password,
    /// 17 used before, 18 too common); a new password its rules refuse gets no answer at all.
    /// Cambia la contraseña propia. El servidor contesta con una notificación; una nueva que sus reglas rechazan no recibe
    /// respuesta ninguna.
    /// </summary>
    public async Task ChangePasswordAsync(string current, string newPassword, CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (current.Length is 0 or > 256 || newPassword.Length is 0 or > 256)
            throw UserError("usuarios/cambiar-ajustes/dato-invalido", "Both passwords need 1 to 256 characters.");
        var cmd = new JsonObject { ["action"] = "changepassword", ["oldpass"] = current, ["newpass"] = newPassword };
        var r = await RequestAsync(cmd, o => o.Str("action") == "msg" && o.Str("type") == "notify" && o.Long("titleid") == 4 && o.Long("msgid") is >= 17 and <= 21,
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        switch (r.Long("msgid"))
        {
            case 20: return;
            case 21: throw UserError("usuarios/cambiar-ajustes/credencial-invalida", "The current password is not right.", r.Str("value"));
            case 17 or 18: throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The server does not accept that new password (used before or too common).", r.Str("value"));
            default: throw UserError("usuarios/cambiar-ajustes/error-remoto", $"The server said: {r.Str("value")}.", r.Str("value"));
        }
    }

    /// <summary>Asks the server for a new authenticator secret; nothing is saved until <see cref="ConfirmAuthenticatorAsync"/>.
    /// Pide al servidor un secreto nuevo; no se guarda nada hasta confirmarlo.</summary>
    public async Task<AuthenticatorSetup> StartAuthenticatorAsync(CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (User.HasAuthenticator) throw UserError("usuarios/cambiar-ajustes/conflicto-estado", "This account already has an authenticator app; remove it first.");
        var r = await RequestAsync(new JsonObject { ["action"] = "otpauth-request" }, o => o.Str("action") == "otpauth-request",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("secret") is { } secret && r.Str("url") is { } url) return new(secret, url);
        throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", r.Long("err") switch
        {
            1 or 4 => "This server does not allow authenticator apps.",
            5 => "The settings of this account are locked by an administrator.",
            _ => $"The server refused (error {r.Long("err")}).",
        });
    }

    /// <summary>Saves the authenticator once a code from it is right. / Guarda la app cuando un código suyo es correcto.</summary>
    public async Task ConfirmAuthenticatorAsync(AuthenticatorSetup setup, string code, CancellationToken ct = default)
    {
        CheckOwnSettings();
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != 6) throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The code has 6 digits.");
        var r = await RequestAsync(new JsonObject { ["action"] = "otpauth-setup", ["secret"] = setup.Secret, ["token"] = code }, o => o.Str("action") == "otpauth-setup",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (!r.Bool("success")) throw UserError("usuarios/cambiar-ajustes/credencial-invalida", "That code is not right; check the phone's clock and try the next one.");
    }

    public async Task RemoveAuthenticatorAsync(CancellationToken ct = default)
    {
        CheckOwnSettings();
        var r = await RequestAsync(new JsonObject { ["action"] = "otpauth-clear" }, o => o.Str("action") == "otpauth-clear",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (!r.Bool("success")) throw UserError("usuarios/cambiar-ajustes/conflicto-estado", "This account has no authenticator app.");
    }

    /// <summary>
    /// Shows, makes new or clears the backup codes. The server only answers while the account has another second factor,
    /// so without one it is not sent.
    /// Enseña, hace nuevos o borra los códigos de respaldo. El servidor solo contesta mientras la cuenta tiene otro segundo
    /// factor, así que sin él no se manda.
    /// </summary>
    public async Task<IReadOnlyList<BackupCode>> BackupCodesAsync(BackupCodeAction action, CancellationToken ct = default)
    {
        if (action != BackupCodeAction.Show) CheckOwnSettings();
        if (!User.HasTwoFactor) throw UserError("usuarios/cambiar-ajustes/conflicto-estado", "Backup codes need an authenticator app or a security key first.");
        var r = await RequestAsync(new JsonObject { ["action"] = "otpauth-getpasswords", ["subaction"] = (int)action }, o => o.Str("action") == "otpauth-getpasswords",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (r["passwords"] is not JsonArray list) return [];
        // "u" is true while the code is still unused. / «u» es true mientras el código no se ha usado.
        return list.OfType<JsonObject>().Select(k => new BackupCode((k.Long("p") ?? 0).ToString("D8"), !k.Bool("u"))).ToList();
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSecurityKeysAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["action"] = "otp-hkey-get" }, o => o.Str("action") == "otp-hkey-get",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        return r["keys"] is JsonArray keys
            ? keys.OfType<JsonObject>().Select(SecurityKey.From).ToList()
            : [];
    }

    /// <summary>The server never answers otp-hkey-remove: confirmed by its "Removed security key" event.
    /// El servidor nunca contesta: se confirma con su aviso «Removed security key».</summary>
    // ------------------------------------------------------------------ email and SMS / correo y SMS (the same as the Android app's)

    private bool OwnAccountEvent(JsonObject e, long msgid) => e.Str("action") == "accountchange" && e.Long("msgid") == msgid && e["account"] is JsonObject a && a.Str("_id") == User.Id;

    /// <summary>Codes by email at sign-in on or off (otpemail), as the website's "Email verification". The server must offer
    /// it and the account have an email. / Códigos por correo al entrar, sí o no. El servidor tiene que ofrecerlo y la cuenta tener correo.</summary>
    public async Task SetEmailTwoFactorAsync(bool enabled, CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (!ServerInfo.EmailTwoFactorAllowed) throw UserError("usuarios/cambiar-ajustes/no-autorizado", "This server does not offer codes by email.");
        if (enabled && string.IsNullOrEmpty(User.Email)) throw UserError("usuarios/cambiar-ajustes/falta-dato", "This account has no email for the codes.");
        if (User.HasEmailTwoFactor == enabled) return;
        var r = await RequestAsync(new JsonObject { ["action"] = "otpemail", ["enabled"] = enabled }, o => o.Str("action") == "otpemail",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (!r.Bool("success")) throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", "The server did not change the codes by email.");
        User = User with { HasEmailTwoFactor = enabled, HasOtherTwoFactor = enabled || User.HasOtherTwoFactor || User.Phone is not null };
    }

    /// <summary>What MeshCentral takes as a phone: an optional +, then 10 or more digits, spaces, dashes or dots. / Lo que admite como teléfono.</summary>
    public static bool ValidPhone(string phone)
    {
        var p = phone.Trim().TrimStart('+');
        return p.Length is >= 10 and <= 17 && p.All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '.');
    }

    /// <summary>Sends a code by SMS to the phone (verifyPhone); then <see cref="ConfirmPhoneAsync"/> with it.
    /// Manda un código por SMS al teléfono; luego se confirma con él.</summary>
    public async Task<PhoneCheck> StartPhoneAsync(string phone, CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (!ServerInfo.SmsTwoFactorAllowed) throw UserError("usuarios/cambiar-ajustes/no-autorizado", "This server does not send SMS.");
        if (!ValidPhone(phone)) throw UserError("usuarios/cambiar-ajustes/dato-invalido", $"\"{phone}\" is not a phone number (+ and 10 digits or more).");
        var r = await RequestAsync(new JsonObject { ["action"] = "verifyPhone", ["phone"] = phone.Trim() }, o => o.Str("action") == "verifyPhone",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (!r.Bool("success") || r.Str("cookie") is not { } cookie) throw UserError("usuarios/cambiar-ajustes/error-remoto", "The server could not send the SMS.");
        return new PhoneCheck(phone.Trim(), cookie);
    }

    /// <summary>Confirms the phone with the code it got. A right code is answered only by the account's event (msgid 96); a
    /// wrong one by verifyPhone again, 2 to 6 s later. / Un código bueno solo lo contesta el aviso de la cuenta; uno malo, verifyPhone otra vez.</summary>
    public async Task ConfirmPhoneAsync(PhoneCheck check, string code, CancellationToken ct = default)
    {
        CheckOwnSettings();
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6) throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The code has 6 digits.");
        var r = await RequestAsync(new JsonObject { ["action"] = "confirmPhone", ["cookie"] = check.Cookie, ["code"] = digits },
            o => (o.Str("action") == "event" && o["event"] is JsonObject e && OwnAccountEvent(e, 96)) || (o.Str("action") == "verifyPhone" && o.Str("cookie") == check.Cookie),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct, TimeSpan.FromSeconds(Math.Max(Timeout.TotalSeconds, 10))).ConfigureAwait(false);
        if (r.Str("action") != "event")
            throw new MeshClientException(MeshFailure.InvalidTwoFactorCode, "usuarios/cambiar-ajustes/credencial-invalida", "That code is not the one sent by SMS.");
        User = User with { Phone = check.Phone, HasOtherTwoFactor = true };
    }

    /// <summary>Removes the phone (removePhone); confirmed by the account's event (msgid 97). / Quita el teléfono; lo confirma su aviso.</summary>
    public async Task RemovePhoneAsync(CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (User.Phone is null) return;
        await ConfirmedByEvent(new JsonObject { ["action"] = "removePhone" }, e => OwnAccountEvent(e, 97),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        User = User with { Phone = null };
    }

    /// <summary>Sends a check code to an ntfy topic (verifyMessaging, service 64); then <see cref="ConfirmMessagingAsync"/> with it.
    /// A server without ntfy among its messaging services answers nothing, so the wait says so. The Android app does this with
    /// its own UnifiedPush topic. / Manda un código a un tema de ntfy; un servidor sin ntfy no contesta, y la espera lo dice.</summary>
    public async Task<MessagingCheck> StartNtfyAsync(string topic, CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (!ServerInfo.MessagingTwoFactorAllowed) throw UserError("usuarios/cambiar-ajustes/no-autorizado", "This server does not send codes by messaging.");
        var t = topic.Trim();
        if (t.Length is 0 or > 64 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw UserError("usuarios/cambiar-ajustes/dato-invalido", $"\"{topic}\" is not an ntfy topic.");
        JsonObject r;
        try
        {
            r = await RequestAsync(new JsonObject { ["action"] = "verifyMessaging", ["service"] = 64, ["handle"] = t }, o => o.Str("action") == "verifyMessaging",
                "usuarios/cambiar-ajustes/no-autorizado", "usuarios/cambiar-ajustes/conexion-cortada", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (MeshClientException e) when (e.Failure == MeshFailure.Timeout)
        {
            throw UserError("usuarios/cambiar-ajustes/no-autorizado", "This server does not have ntfy among its messaging services.");
        }
        if (!r.Bool("success") || r.Str("cookie") is not { } cookie) throw UserError("usuarios/cambiar-ajustes/error-remoto", "The server could not send to ntfy.");
        return new MessagingCheck("ntfy:" + t, cookie);
    }

    /// <summary>Confirms the handle with the code it got: right = the account's event (msgid 156), wrong = verifyMessaging again.
    /// / Confirma el canal: bueno = el aviso 156 de la cuenta; malo = verifyMessaging otra vez.</summary>
    public async Task ConfirmMessagingAsync(MessagingCheck check, string code, CancellationToken ct = default)
    {
        CheckOwnSettings();
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6) throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The code has 6 digits.");
        var r = await RequestAsync(new JsonObject { ["action"] = "confirmMessaging", ["cookie"] = check.Cookie, ["code"] = digits },
            o => (o.Str("action") == "event" && o["event"] is JsonObject e && OwnAccountEvent(e, 156)) || (o.Str("action") == "verifyMessaging" && o.Str("cookie") == check.Cookie),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct, TimeSpan.FromSeconds(Math.Max(Timeout.TotalSeconds, 10))).ConfigureAwait(false);
        if (r.Str("action") != "event")
            throw new MeshClientException(MeshFailure.InvalidTwoFactorCode, "usuarios/cambiar-ajustes/credencial-invalida", "That code is not the one sent.");
        User = User with { MessagingHandle = check.Handle, HasOtherTwoFactor = true };
    }

    /// <summary>Removes the messaging handle (removeMessaging); confirmed by the account's event (msgid 157). / Quita el canal de mensajería.</summary>
    public async Task RemoveMessagingAsync(CancellationToken ct = default)
    {
        CheckOwnSettings();
        if (User.MessagingHandle is null) return;
        await ConfirmedByEvent(new JsonObject { ["action"] = "removeMessaging" }, e => OwnAccountEvent(e, 157),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        User = User with { MessagingHandle = null };
    }

    /// <summary>
    /// Adds a security key or passkey (webauthn-startregister, then -endregister with the key's attestation; the server
    /// answers otp-hkey-setup-response). A server that does not allow keys (no two-factor, no dotted name, keys locked or
    /// the maximum reached) answers nothing, so the wait says so.
    /// Añade una llave de seguridad. Un servidor que no las permite no contesta, y la espera lo dice.
    /// </summary>
    public async Task AddSecurityKeyAsync(string name, ISecurityKey key, CancellationToken ct = default)
    {
        CheckOwnSettings();
        var n = name.Trim();
        if (n.Length is 0 or > 64) throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The key needs a name of 1 to 64 characters.");
        var before = User.SecurityKeys;
        JsonObject start;
        try
        {
            start = await RequestAsync(new JsonObject { ["action"] = "webauthn-startregister", ["name"] = n }, o => o.Str("action") == "webauthn-startregister",
                "usuarios/cambiar-ajustes/no-autorizado", "usuarios/cambiar-ajustes/conexion-cortada", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (MeshClientException e) when (e.Failure == MeshFailure.Timeout)
        {
            throw UserError("usuarios/cambiar-ajustes/no-autorizado", "This server does not let this account add security keys.");
        }
        if (start["request"] is not JsonObject req || req.Str("challenge") is not { } challenge || req["user"] is not JsonObject u)
            throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", "The server's key request was not understood.");
        var (rpId, origin) = SecurityKeys.Site(Server);
        var clientData = SecurityKeys.ClientData("webauthn.create", Convert.FromBase64String(challenge), origin);
        var made = await key.MakeCredentialAsync(new KeyCreation(rpId, (req["rp"] as JsonObject)?.Str("name") ?? rpId,
            Convert.FromBase64String(u.Str("id") ?? ""), u.Str("name") ?? User.Id, u.Str("displayName") ?? User.Name, clientData,
            Array.Empty<byte[]>(), req.Str("userVerification") ?? "preferred", TimeSpan.FromMilliseconds(req.Long("timeout") ?? 60_000)), ct).ConfigureAwait(false);
        var r = await RequestAsync(new JsonObject
            {
                ["action"] = "webauthn-endregister", ["name"] = n,
                ["response"] = new JsonObject
                {
                    ["rawId"] = Convert.ToBase64String(made.CredentialId),
                    ["response"] = new JsonObject { ["attestationObject"] = Convert.ToBase64String(made.AttestationObject), ["clientDataJSON"] = Convert.ToBase64String(clientData) },
                },
            }, o => o.Str("action") == "otp-hkey-setup-response",
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (!r.Bool("result")) throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", "The server did not take the key.");
        // Set, not added: the account's event (msgid 95) may have counted it already. / Se pone, no se suma: el aviso puede haberla contado ya.
        User = User with { SecurityKeys = before + 1 };
    }

    /// <summary>
    /// The server's "remember the second factor" cookie (twoFactorCookie; 30 days unless the server says otherwise): given
    /// as the code "cookie=&lt;it&gt;" it signs in without asking again. The app keeps it in memory only, to reconnect after a
    /// dropped connection without asking the person for a new code. Null if the server gives none. The same as Android's.
    /// La cookie de «recordar el doble factor»; dada como código «cookie=&lt;ella&gt;» entra sin volver a pedirlo. Solo en memoria.
    /// </summary>
    public async Task<string?> TwoFactorCookieAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await RequestAsync(new JsonObject { ["action"] = "twoFactorCookie" }, o => o.Str("action") == "twoFactorCookie",
                "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return r.Str("cookie") is { Length: > 0 } c ? c : null;
        }
        catch (MeshClientException e) when (e.Failure == MeshFailure.Timeout) { return null; }
    }

    public async Task RemoveSecurityKeyAsync(SecurityKey key, CancellationToken ct = default)
    {
        CheckOwnSettings();
        var cmd = new JsonObject { ["action"] = "otp-hkey-remove", ["index"] = key.Index };
        var before = (await GetSecurityKeysAsync(ct).ConfigureAwait(false)).Count;
        var m = await ConfirmedByEvent(cmd, e => e.Str("action") == "accountchange" && e.Long("msgid") == 94 && e["account"] is JsonObject a && a.Str("_id") == User.Id,
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        // The server sends the event even when no key has that index: the account's count says whether it went.
        // El servidor manda el aviso aunque ninguna llave tenga ese índice: el número de la cuenta dice si se fue.
        var left = ((m["event"] as JsonObject)?["account"] as JsonObject)?.Long("otphkeys") ?? 0;
        if (left >= before) throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", "The server kept the key.");
        User = User with { SecurityKeys = (int)left };
    }

    public async Task<IReadOnlyList<LoginToken>> GetLoginTokensAsync(CancellationToken ct = default) =>
        await LoginTokensAsync(null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LoginToken>> RemoveLoginTokenAsync(LoginToken token, CancellationToken ct = default) =>
        await LoginTokensAsync(token.TokenUser, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<LoginToken>> LoginTokensAsync(string? remove, CancellationToken ct)
    {
        var cmd = new JsonObject { ["action"] = "loginTokens" };
        if (remove is not null) cmd["remove"] = new JsonArray(remove);
        var r = await RequestAsync(cmd, o => o.Str("action") == "loginTokens" && o["loginTokens"] is JsonArray,
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        var list = ((JsonArray)r["loginTokens"]!).OfType<JsonObject>().Select(LoginToken.From).ToList();
        if (remove is not null && list.Any(t => t.TokenUser == remove))
            throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", "The server kept the token.");
        return list;
    }

    /// <summary>Creates a login token; its password comes only now. / Crea un token; su contraseña llega solo ahora.</summary>
    public async Task<LoginToken> CreateLoginTokenAsync(string name, TimeSpan? lifetime, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw UserError("usuarios/cambiar-ajustes/dato-invalido", "The name needs 1 to 100 characters.");
        var id = NewId();
        var cmd = new JsonObject { ["action"] = "createLoginToken", ["name"] = name, ["expire"] = (long)(lifetime?.TotalMinutes ?? 0), ["responseid"] = id };
        // Success comes without the responseid, errors with it. / El éxito llega sin el responseid; los errores, con él.
        var r = await RequestAsync(cmd, o => o.Str("action") == "createLoginToken" && (o.Str("responseid") == id || (o.Str("tokenUser") is not null && o.Str("name") == name)),
            "usuarios/cambiar-ajustes/tiempo-agotado", "usuarios/cambiar-ajustes/conexion-cortada", ct).ConfigureAwait(false);
        if (r.Str("tokenUser") is null || r.Str("tokenPass") is null)
            throw UserError("usuarios/cambiar-ajustes/rechazo-remoto", $"The server said: {r.Str("result")}.", r.Str("result"));
        return LoginToken.From(r);
    }

    // ------------------------------------------------------------------ live events / avisos en vivo

    private void OnUserEvent(JsonObject e)
    {
        try
        {
            switch (e.Str("action"))
            {
                case "accountcreate" when e["account"] is JsonObject a:
                    UserChanged?.Invoke(this, new(UserChangeKind.Added, a.Str("_id") ?? "", UserInfo.From(a)));
                    break;
                case "accountchange" when e["account"] is JsonObject a:
                    UserChanged?.Invoke(this, new(UserChangeKind.Changed, a.Str("_id") ?? "", UserInfo.From(a)));
                    break;
                case "accountremove" when e.Str("userid") is { } id:
                    UserChanged?.Invoke(this, new(UserChangeKind.Removed, id, null));
                    break;
                case "createusergroup" when e.Str("ugrpid") is { } id:
                    UserGroupChanged?.Invoke(this, new(UserChangeKind.Added, id, UserGroup.From(id, e)));
                    break;
                case "usergroupchange" when e.Str("ugrpid") is { } id:
                    UserGroupChanged?.Invoke(this, new(UserChangeKind.Changed, id, UserGroup.From(id, e)));
                    break;
                case "deleteusergroup" when e.Str("ugrpid") is { } id:
                    UserGroupChanged?.Invoke(this, new(UserChangeKind.Removed, id, null));
                    break;
            }
        }
        catch (Exception) { /* a subscriber must not kill the session / un suscriptor no puede tumbar la sesión */ }
    }
}
