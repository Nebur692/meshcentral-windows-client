// Throwaway OpenID Connect provider for the tests: any username logs in (development login form of oidc-provider).
// "alice" and "bob" are in the group "mesh-users"; "denied" is in no group, so MeshCentral must refuse it.
// Proveedor OpenID Connect desechable para las pruebas: cualquier usuario entra (formulario de desarrollo de
// oidc-provider). «alice» y «bob» están en el grupo «mesh-users»; «denied» no está en ninguno y MeshCentral lo tiene que rechazar.
//
// ISSUER (e.g. http://oidc:3000), PORT (3000), REDIRECT_URI (e.g. https://mesh.test/auth-oidc-callback)
import Provider from 'oidc-provider';

const port = Number(process.env.PORT || 3000);
const issuer = process.env.ISSUER || `http://localhost:${port}`;
const redirect = (process.env.REDIRECT_URI || 'https://mesh.test/auth-oidc-callback').split(',');

const provider = new Provider(issuer, {
  clients: [{
    client_id: 'meshcentral-test', client_secret: 'test-secret-not-for-production',
    redirect_uris: redirect, post_logout_redirect_uris: redirect.map((r) => r.replace(/auth-oidc-callback$/, 'login')),
    grant_types: ['authorization_code'], response_types: ['code'],
  }],
  async findAccount(ctx, id) {
    return {
      accountId: id,
      async claims() {
        return { sub: id, name: id, preferred_username: id, email: `${id}@example.test`, email_verified: true,
                 groups: id === 'denied' ? [] : ['mesh-users'] };
      },
    };
  },
  claims: { openid: ['sub'], profile: ['name', 'preferred_username'], email: ['email', 'email_verified'], groups: ['groups'] },
  scopes: ['openid', 'profile', 'email', 'groups', 'offline_access'],
  features: { devInteractions: { enabled: true } },
  pkce: { required: () => false },
  cookies: { keys: ['throwaway-test-key'] },
  conformIdTokenClaims: false,
});
provider.listen(port, () => console.log(`oidc ready ${issuer}`));
