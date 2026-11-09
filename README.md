<div align="center">

# meshcentral-windows-client

*A modern Windows app for technicians, for any MeshCentral server: sign in, see every device live and connect — no browser needed*

![Release](https://img.shields.io/github/v/release/Nebur692/meshcentral-windows-client?label=release&color=blue)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Build](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml/badge.svg)](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml)
[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-30363D?logo=githubsponsors&logoColor=EA4AAA)](https://github.com/sponsors/Nebur692)
[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/nebur69265723)
[![PayPal](https://img.shields.io/badge/PayPal-donate-00457C?logo=paypal&logoColor=white)](https://paypal.me/0SkillS)

🇬🇧 [English](#english) · 🇪🇸 [Español](#español) · [Code signing policy](docs/CODE_SIGNING.md)

</div>

---

## English

### What it is

A native Windows app (Windows 10/11, Windows 11 look and feel, light and dark) for the person who **manages** devices
with [MeshCentral](https://github.com/Ylianst/MeshCentral). It talks to the server through the same channels as the
official website and `meshctrl`, so it works with **any** MeshCentral installation, with no plugin and no server change.

> Unofficial project, not affiliated with the MeshCentral authors. The device side still uses the official MeshCentral agent.

### Features (version 0.13)

- **Sign in** with username and password, with **two-factor** codes (authenticator app, or email code when the server offers it).
- **Single sign-on**: if the server offers it (OpenID Connect, SAML, Microsoft, Google, GitHub…), a button signs in on
  the provider's own page, in the Windows browser — the app never sees that password. Leaving username and password
  empty and pressing Connect does the same.
- **Certificates**: a certificate Windows trusts is always accepted; for a server with its own certificate the app shows
  the SHA-256 fingerprint, asks once and remembers it — and warns loudly if it ever changes.
- **Devices live**: every device group and device the account can see, online status updated in real time, search by
  name, system, address, tag or group.
- **Saved servers**: address and user remembered; the password only if you ask, encrypted for your Windows user (DPAPI).
- **Terminal**: shell, cmd or PowerShell on the device, with a full terminal (xterm.js, the same engine as the
  website); copy and paste as in PuTTY (select to copy, right click to paste). Double-click a device to open it.
- **Remote desktop**: see and control the device's screen with mouse and keyboard, choose the display or all of them,
  **one window per remote display on your monitors**, quality presets, Ctrl+Alt+Del and Windows-key buttons, and
  **type the clipboard** on the device (Unicode, whatever its keyboard layout). With Windows devices, **copy and paste
  of everything — text, images, files and folders — both ways**, as in Remote Desktop, with Windows' own copy window.
  **Full screen** with a floating bar, and the device's whole menu and the usual keys (lock the screen, block the input of
  the person there) without leaving the viewer.
- **Port mapping** (what MeshCentral Router does, inside the app): a port on this PC (127.0.0.1 only) whose connections
  go, through the device's agent, to a port of the device or of another machine on its network; remembered mappings
  come back at every sign-in. **Windows Remote Desktop** to a device in one click, through a private port that closes
  with it.
- **SSH inside the app**, to the device or to a machine its network reaches, with a password or an OpenSSH key, the
  server's identity checked like OpenSSH does, and the app's own terminal.
- **Device page**: general data (description and tags editable, model, serial number, memory with its modules, disks and volumes), network interfaces, the whole hardware and agent
  inventory with a filter, notes and history — also for devices that are offline.
- **Files**: browse the device's drives and folders, create folders, rename, delete, upload (also by dropping files
  from Explorer) and download, with progress and cancel; **Ctrl+C / Ctrl+X / Ctrl+V with the Windows clipboard**:
  copy from the device and paste in Explorer, copy in Explorer and paste on the device, folders included, and copy or
  move between folders of the device.
- **Device actions**, on one or several selected devices: wake up, restart, power off and sleep (always asked first), a
  notification or a message window for whoever uses the device, and **run commands** in cmd, PowerShell or sh with each
  device's output. Only what the account is allowed to do is offered.
- **Device groups**: create, rename and delete them; give other accounts rights on a group (every right of the
  website, as checkboxes), change them or take them away; **move devices** between groups; and add new devices with an
  **invitation link** (a public page with the installers), the **Windows agent** with the group built in, or the
  **Linux / macOS command**. Changes made anywhere else appear at once.
- **Users** (for administrators): create accounts, change their email, name and server rights, set their password, lock
  and delete them; **user groups** with their members, and rights on device groups for a whole user group.
- **My account**: change the password, add an **authenticator app** (QR code), backup codes, see and remove security
  keys, and **login tokens** for scripts and tools.
- **Server** (menu "Server"): the **event log**, live and filterable, also per account; the website's **reports**
  (remote sessions, traffic, sign-ins, database) between two dates; the **server's live state** and error log for its
  administrators; and **notifications** to everyone connected or to one account, which the app also shows when they
  arrive. Everything exports to CSV.
- **Open in the browser** from the device's menu.
- **Light, dark or like Windows**, remembered.
- **Updates itself**: when a new version is published, it offers it, checks its SHA-256 fingerprint and restarts as
  the new version; a file that does not match is never installed.
- **English and Spanish** interface, following the Windows language.

The goal is everything the MeshCentral website does, plus MeshCentral Router. Coming next: a signed release and an installer, then session recordings — see [the plan](docs/PLAN.md).

### Install

Download `MeshCentralClient.exe` from the latest [release](https://github.com/Nebur692/meshcentral-windows-client/releases)
(or from the latest successful [build](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml))
and run it. It is a single self-contained file: nothing else to install, no administrator rights. Your servers and
settings live in `%APPDATA%\MeshCentralClient`, so replacing or updating the exe keeps them. Keep the exe in a
folder of yours (not Program Files) so it can update itself.

[Code signing policy](docs/CODE_SIGNING.md): what is signed, by whom, and what the app connects to.

### Requirements on the server

Any recent MeshCentral (tested with 1.3.1). The account must not have the "no tools" restriction (`NOMESHCMD`),
which blocks external tools such as `meshctrl`, MeshCentral Router and this app.

### Build and test

```bash
dotnet build MeshCentralClient.slnx -c Release        # Windows, or Linux/macOS with the .NET 10 SDK
tests/server/run-integration.sh                       # needs Docker: real throwaway MeshCentral + Linux agent
```

The integration tests start a disposable MeshCentral server and a real Linux agent in Docker, test every login path
(right and wrong password, two-factor, untrusted and pinned certificate), the device list, live events, a
server-side disconnection, the terminal, the files, the remote desktop, port mapping, SSH (against a throwaway SSH server)
and single sign-on (against a throwaway OpenID Connect provider), and remove everything when they end.

### License

[Apache 2.0](LICENSE).

---

## Español

### Qué es

Una app nativa de Windows (Windows 10/11, con el aspecto de Windows 11, en claro y en oscuro) para quien **administra**
equipos con [MeshCentral](https://github.com/Ylianst/MeshCentral). Habla con el servidor por los mismos canales que la
web oficial y `meshctrl`, así que funciona con **cualquier** instalación de MeshCentral, sin plugins y sin tocar el servidor.

> Proyecto no oficial, sin relación con los autores de MeshCentral. En los equipos se sigue usando el agente oficial de MeshCentral.

### Qué hace (versión 0.13)

- **Entrar** con usuario y contraseña, con código de **doble factor** (app de autenticación, o código por correo si el servidor lo ofrece).
- **Inicio de sesión único**: si el servidor lo ofrece (OpenID Connect, SAML, Microsoft, Google, GitHub…), un botón
  entra en la propia página del proveedor, en el navegador de Windows; la app no ve nunca esa contraseña. Dejar usuario y
  contraseña vacíos y pulsar Conectar hace lo mismo.
- **Certificados**: uno en el que confía Windows se acepta siempre; con un servidor con certificado propio la app enseña
  la huella SHA-256, pregunta una vez y la recuerda, y avisa bien claro si algún día cambia.
- **Equipos en vivo**: todos los grupos y equipos que ve la cuenta, con el estado de conexión al momento y buscador por
  nombre, sistema, dirección, etiqueta o grupo.
- **Servidores guardados**: recuerda la dirección y el usuario; la contraseña solo si se lo pides, cifrada para tu usuario de Windows (DPAPI).
- **Terminal**: shell, cmd o PowerShell en el equipo, con una terminal completa (xterm.js, el mismo motor que la
  web); copiar y pegar como en PuTTY (seleccionar copia, clic derecho pega). Doble clic en un equipo para abrirla.
- **Escritorio remoto**: ver y manejar la pantalla del equipo con ratón y teclado, elegir la pantalla o todas, **una
  ventana por cada pantalla remota en tus monitores**, calidades predefinidas, botones de Ctrl+Alt+Supr y tecla Windows, y
  **escribir el portapapeles** en el equipo (Unicode, sea cual sea la distribución de su teclado). Con equipos Windows,
  **copiar y pegar de todo —texto, imágenes, ficheros y carpetas— en los dos sentidos**, como en Escritorio remoto, con la
  ventana de copia de Windows. **Pantalla completa** con barra flotante, y todo el menú del equipo y las teclas habituales
  (bloquear la pantalla, bloquear la entrada de quien está allí) sin salir del visor.
- **Redirección de puertos** (lo que hace MeshCentral Router, dentro de la app): un puerto de este PC (solo 127.0.0.1)
  cuyas conexiones van, por el agente del equipo, a un puerto del equipo o de otra máquina de su red; las que se
  recuerdan vuelven en cada entrada. **Escritorio remoto de Windows** a un equipo con un clic, por un puerto privado que
  se cierra con él.
- **SSH dentro de la app**, al equipo o a una máquina a la que llega su red, con contraseña o clave de OpenSSH, la
  identidad del servidor comprobada como hace OpenSSH, y la terminal de la propia app.
- **Ficha del equipo**: datos generales (descripción y etiquetas editables, modelo, número de serie, memoria con sus
  módulos, discos y volúmenes), interfaces de red, todo el inventario de
  hardware y del agente con filtro, notas e historial; también de equipos desconectados.
- **Ficheros**: explorar las unidades y carpetas del equipo, crear carpetas, renombrar, borrar, subir (también
  soltando ficheros desde el Explorador) y bajar, con progreso y botón de cancelar; **Ctrl+C / Ctrl+X / Ctrl+V con el
  portapapeles de Windows**: copiar del equipo y pegar en el Explorador, copiar en el Explorador y pegar en el equipo,
  carpetas incluidas, y copiar o mover entre carpetas del equipo.
- **Acciones sobre equipos**, en uno o varios equipos elegidos: encender, reiniciar, apagar y suspender (siempre
  pregunta antes), una notificación o una ventana de mensaje para quien usa el equipo, y **ejecutar órdenes** en cmd,
  PowerShell o sh con la salida de cada equipo. Solo se ofrece lo que la cuenta tiene permiso para hacer.
- **Grupos de equipos**: crearlos, renombrarlos y borrarlos; dar permisos en un grupo a otras cuentas (todos los de la
  web, como casillas), cambiarlos o quitarlos; **mover equipos** de un grupo a otro; y añadir equipos nuevos con un
  **enlace de invitación** (una página pública con los instaladores), el **agente de Windows** con el grupo dentro o la
  **orden de Linux / macOS**. Lo que se cambie en otro sitio aparece al momento.
- **Usuarios** (para administradores): crear cuentas, cambiarles el correo, el nombre y los permisos de servidor,
  ponerles contraseña, bloquearlas y borrarlas; **grupos de usuarios** con sus miembros, y permisos en grupos de equipos
  para todo un grupo de usuarios.
- **Mi cuenta**: cambiar la contraseña, añadir una **app de autenticación** (código QR), códigos de respaldo, ver y
  quitar llaves de seguridad, y **tokens de acceso** para guiones y herramientas.
- **Servidor** (menú «Servidor»): el **registro de sucesos**, en vivo y con filtro, también por cuenta; los **informes**
  de la web (sesiones remotas, tráfico, entradas, base de datos) entre dos fechas; el **estado del servidor en vivo** y su
  registro de errores para sus administradores; y **avisos** a todas las personas conectadas o a una cuenta, que la app
  también enseña cuando llegan. Todo se exporta a CSV.
- **Abrir en el navegador** desde el menú del equipo.
- **Claro, oscuro o como Windows**, y lo recuerda.
- **Se actualiza sola**: cuando se publica una versión nueva, la ofrece, comprueba su huella SHA-256 y se reabre ya
  actualizada; un fichero que no coincide no se instala nunca.
- Interfaz en **inglés y español**, según el idioma de Windows.

La meta es todo lo que hace la web de MeshCentral, más MeshCentral Router. Lo siguiente: versión firmada e instalador,
y después las grabaciones de sesiones; está en [el plan](docs/PLAN.md).

### Instalar

Descarga `MeshCentralClient.exe` de la última [versión publicada](https://github.com/Nebur692/meshcentral-windows-client/releases)
(o de la última [compilación](https://github.com/Nebur692/meshcentral-windows-client/actions/workflows/build.yml) correcta)
y ábrelo. Es un único fichero autónomo: no hay que instalar nada más ni ser administrador. Tus servidores y ajustes
se guardan en `%APPDATA%\MeshCentralClient`, así que al cambiar o actualizar el exe se mantienen. Ten el exe en una
carpeta tuya (no en Archivos de programa) para que pueda actualizarse sola.

[Code signing policy](docs/CODE_SIGNING.md) (política de firma de código): qué se firma, quién lo firma y a qué se conecta la app.

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
equipos, los avisos en vivo, un corte desde el servidor, la terminal, los ficheros, el escritorio remoto, la redirección
de puertos, SSH (contra un servidor SSH desechable) y el inicio de sesión único (contra un proveedor OpenID Connect
desechable), y lo quitan todo al acabar.

### Licencia

[Apache 2.0](LICENSE).
