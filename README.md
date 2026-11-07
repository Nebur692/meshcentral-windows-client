<div align="center">

# meshcentral-windows-client

*A modern Windows app for technicians, for any MeshCentral server: sign in, see every device live and connect — no browser needed*

![Release](https://img.shields.io/github/v/release/Nebur692/meshcentral-windows-client?label=release&color=blue)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Build](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml/badge.svg)](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml)
[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-30363D?logo=githubsponsors&logoColor=EA4AAA)](https://github.com/sponsors/Nebur692)
[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/nebur69265723)
[![PayPal](https://img.shields.io/badge/PayPal-donate-00457C?logo=paypal&logoColor=white)](https://paypal.me/0SkillS)

🇬🇧 [English](#english) · 🇪🇸 [Español](#español)

</div>

---

## English

### What it is

A native Windows app (Windows 10/11, Windows 11 look and feel, light and dark) for the person who **manages** devices
with [MeshCentral](https://github.com/Ylianst/MeshCentral). It talks to the server through the same channels as the
official website and `meshctrl`, so it works with **any** MeshCentral installation, with no plugin and no server change.

> Unofficial project, not affiliated with the MeshCentral authors. The device side still uses the official MeshCentral agent.

### Features (version 0.1)

- **Sign in** with username and password, with **two-factor** codes (authenticator app, or email code when the server offers it).
- **Certificates**: a certificate Windows trusts is always accepted; for a server with its own certificate the app shows
  the SHA-256 fingerprint, asks once and remembers it — and warns loudly if it ever changes.
- **Devices live**: every device group and device the account can see, online status updated in real time, search by
  name, system, address, tag or group.
- **Saved servers**: address and user remembered; the password only if you ask, encrypted for your Windows user (DPAPI).
- **Open in the browser**: double-click a device to open it in the MeshCentral website.
- **English and Spanish** interface, following the Windows language.

Coming next: single sign-on (OIDC), terminal, files, remote desktop and port mapping — see [the plan](docs/PLAN.md).

### Install

Download `MeshCentralClient.exe` from the latest [release](https://github.com/Nebur692/meshcentral-windows-client/releases)
(or from the latest successful [build](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml))
and run it. It is a single self-contained file: nothing else to install.

### Requirements on the server

Any recent MeshCentral (tested with 1.3.1). The account must not have the "no tools" restriction (`NOMESHCMD`),
which blocks external tools such as `meshctrl`, MeshCentral Router and this app.

### Build and test

```bash
dotnet build MeshCentralClient.slnx -c Release        # Windows, or Linux/macOS with the .NET 10 SDK
tests/server/run-integration.sh                       # needs Docker: real throwaway MeshCentral + Linux agent
```

The integration tests start a disposable MeshCentral server and a real Linux agent in Docker, test every login path
(right and wrong password, two-factor, untrusted and pinned certificate), the device list, live events and a
server-side disconnection, and remove everything when they end.

### License

[Apache 2.0](LICENSE).

---

## Español

### Qué es

Una app nativa de Windows (Windows 10/11, con el aspecto de Windows 11, en claro y en oscuro) para quien **administra**
equipos con [MeshCentral](https://github.com/Ylianst/MeshCentral). Habla con el servidor por los mismos canales que la
web oficial y `meshctrl`, así que funciona con **cualquier** instalación de MeshCentral, sin plugins y sin tocar el servidor.

> Proyecto no oficial, sin relación con los autores de MeshCentral. En los equipos se sigue usando el agente oficial de MeshCentral.

### Qué hace (versión 0.1)

- **Entrar** con usuario y contraseña, con código de **doble factor** (app de autenticación, o código por correo si el servidor lo ofrece).
- **Certificados**: uno en el que confía Windows se acepta siempre; con un servidor con certificado propio la app enseña
  la huella SHA-256, pregunta una vez y la recuerda, y avisa bien claro si algún día cambia.
- **Equipos en vivo**: todos los grupos y equipos que ve la cuenta, con el estado de conexión al momento y buscador por
  nombre, sistema, dirección, etiqueta o grupo.
- **Servidores guardados**: recuerda la dirección y el usuario; la contraseña solo si se lo pides, cifrada para tu usuario de Windows (DPAPI).
- **Abrir en el navegador**: doble clic en un equipo para abrirlo en la web de MeshCentral.
- Interfaz en **inglés y español**, según el idioma de Windows.

Lo siguiente: inicio de sesión único (OIDC), terminal, ficheros, escritorio remoto y redirección de puertos; está en [el plan](docs/PLAN.md).

### Instalar

Descarga `MeshCentralClient.exe` de la última [versión publicada](https://github.com/Nebur692/meshcentral-windows-client/releases)
(o de la última [compilación](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml) correcta)
y ábrelo. Es un único fichero autónomo: no hay que instalar nada más.

### Qué necesita el servidor

Cualquier MeshCentral reciente (probado con la 1.3.1). La cuenta no puede tener la restricción «sin herramientas»
(`NOMESHCMD`), que bloquea las herramientas externas como `meshctrl`, MeshCentral Router y esta app.

### Compilar y probar

```bash
dotnet build MeshCentralClient.slnx -c Release        # en Windows, o en Linux/macOS con el SDK de .NET 10
tests/server/run-integration.sh                       # necesita Docker: MeshCentral desechable de verdad + agente Linux
```

Las pruebas de integración arrancan en Docker un servidor MeshCentral desechable y un agente Linux de verdad, prueban
todas las formas de entrar (contraseña buena y mala, doble factor, certificado no fiable y aceptado), la lista de
equipos, los avisos en vivo y un corte desde el servidor, y lo quitan todo al acabar.

### Licencia

[Apache 2.0](LICENSE).
