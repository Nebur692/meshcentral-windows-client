# Code signing policy

[English](#english) · [Español](#español)

## English

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

### What is signed

Only `MeshCentralClient.exe`, built by this repository's GitHub Actions workflow
([`.github/workflows/build.yml`](../.github/workflows/build.yml)) from the source code of the tagged commit. Nothing built
anywhere else is signed. A release is signed only after every test of that workflow has passed and an approver has
approved its signing request in SignPath.

### Team roles

| Role | Who |
|------|-----|
| Committers and reviewers | [Nebur692](https://github.com/Nebur692) |
| Approvers | [Nebur692](https://github.com/Nebur692) |

Changes from anyone else come as pull requests and are reviewed before they are merged. Every member uses multi-factor
authentication on GitHub and on SignPath.

### Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or
the person installing or operating it. It connects only to:

- **The MeshCentral server the user enters**, to sign in and to manage that server's devices, as the user asks.
- **GitHub** (`api.github.com` and the release downloads of this repository), to check whether a newer version exists and,
  if the user accepts, to download it. No personal data is sent. Setting the environment variable
  `MESHCLIENT_UPDATE_FEED=off` turns this check off.
- **The single sign-on provider the MeshCentral server uses**, in the user's own browser, when the user chooses to sign in
  that way.

There is no telemetry, analytics or advertising.

---

## Español

Firma de código gratuita de [SignPath.io](https://about.signpath.io/), certificado de [SignPath Foundation](https://signpath.org/).

### Qué se firma

Solo `MeshCentralClient.exe`, compilado por el flujo de GitHub Actions de este repositorio
([`.github/workflows/build.yml`](../.github/workflows/build.yml)) a partir del código fuente del commit etiquetado. No se
firma nada compilado en otro sitio. Una versión solo se firma cuando han pasado todas las pruebas de ese flujo y un
aprobador ha aprobado su petición de firma en SignPath.

### Funciones del equipo

| Función | Quién |
|---------|-------|
| Autores y revisores | [Nebur692](https://github.com/Nebur692) |
| Aprobadores | [Nebur692](https://github.com/Nebur692) |

Los cambios de cualquier otra persona llegan como pull requests y se revisan antes de incorporarlos. Todos los miembros
usan autenticación de varios factores en GitHub y en SignPath.

### Política de privacidad

Este programa no envía ninguna información a otros sistemas de la red salvo que lo pida expresamente el usuario o quien
lo instala o lo maneja. Solo se conecta a:

- **El servidor MeshCentral que escribe el usuario**, para entrar y gestionar los equipos de ese servidor, cuando el
  usuario lo pide.
- **GitHub** (`api.github.com` y las descargas de versiones de este repositorio), para ver si hay una versión más nueva y,
  si el usuario acepta, descargarla. No se envía ningún dato personal. La variable de entorno
  `MESHCLIENT_UPDATE_FEED=off` desactiva esta comprobación.
- **El proveedor de inicio de sesión único que usa el servidor MeshCentral**, en el propio navegador del usuario, cuando
  elige entrar así.

No hay telemetría, analítica ni publicidad.
