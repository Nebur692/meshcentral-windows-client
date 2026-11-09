# Plan

## English

A modern Windows app for technicians that works with **any** MeshCentral server, through the same channels as the
official website and `meshctrl` (`control.ashx` for the session, `meshrelay.ashx` for tunnels). Nothing is installed
on the server.

### Principles

- **Generic**: no assumption about a particular server, domain, login provider or branding.
- **Tested against the real thing**: every protocol feature has integration tests against a throwaway MeshCentral
  and a real agent in Docker (`tests/server/run-integration.sh`), run on every push.
- **Plain-language errors** with a reference code ("burburito" code), the technical detail folded under *Details*.
- **Bilingual**: English and Spanish (Spain), in the interface and in the repository.

### Goal: everything the MeshCentral website does

The target is feature parity with the official website, plus what MeshCentral Router adds (port mapping).

| # | Phase | State |
|---|-------|-------|
| 1 | Sign-in (password, two-factor by app or email), certificate trust with fingerprint pinning, live device list, saved servers, open device in the browser | **done (0.1)** |
| 2 | Relay tunnels; **terminal** (shell, cmd, PowerShell) with xterm.js; light / dark / Windows theme | **done (0.2)** |
| 3 | **Single sign-on** (OpenID Connect, SAML, Microsoft, Google, GitHub…) in the Windows browser; self-update | **done (0.3–0.4)** |
| 4 | **Files**: browse, upload, download, rename, delete, create folders | **done (0.5)** |
| 5 | **Remote desktop**: native viewer, keyboard and mouse, one window per remote display, quality, type the clipboard, Ctrl+Alt+Del, agent messages (consent) | **done (0.6)**; copy and paste of everything with Windows devices **done (0.12)**; files with Windows' own copy window, full screen, the device's menu and keys in the viewer, locking the screen and blocking the input of the person there **done (0.13)**; blanking the screen later |
| 6 | **Device page**: general info, hardware and agent inventory, network interfaces, notes, tags, description, history | **done (0.7)** |
| 7 | **Device actions**: wake-on-LAN, sleep, restart, power off, message box, toast, run commands — on several devices at once | **done (0.8)**; chat, agent update and Intel AMT power later |
| 8 | **Device groups**: create, edit, delete, invitations (agent install links), group users and rights, move devices | **done (0.9)**; group settings (consent, notifications, auto-remove) and Intel AMT / local groups later |
| 9 | **Users and accounts**: users, user groups, rights, my account (two-factor, keys, login tokens) | **done (0.10)**; adding a security key (needs the browser's WebAuthn), email/SMS two-factor and active sessions later |
| 10 | **Server**: events, statistics, session recordings, reports, notifications | **done (0.11)**; session recordings (list, download and playback) later |
| 11 | **Port mapping** (to replace MeshCentral Router), Windows Remote Desktop and SSH inside the app | **done (0.12)**; UDP mapping, and RDP/SSH through the server alone (devices without an agent) later |
| 12 | Signed release, installer (winget) and automatic updates (updates already done in 0.3) | **in progress**: free signing by SignPath Foundation requested ([code signing policy](CODE_SIGNING.md)); installer and winget next |

---

## Español

Una app moderna de Windows para técnicos que funciona con **cualquier** servidor MeshCentral, por los mismos canales
que la web oficial y `meshctrl` (`control.ashx` para la sesión, `meshrelay.ashx` para los túneles). No se instala nada
en el servidor.

### Principios

- **Genérica**: nada da por hecho un servidor, dominio, proveedor de inicio de sesión o marca concretos.
- **Probada contra lo de verdad**: cada función del protocolo tiene pruebas de integración contra un MeshCentral
  desechable y un agente real en Docker (`tests/server/run-integration.sh`), que se pasan en cada subida.
- **Errores en cristiano** con un código de referencia (código «burburito») y el detalle técnico plegado en *Detalles*.
- **Bilingüe**: inglés y español de España, en la interfaz y en el repositorio.

### Objetivo: todo lo que hace la web de MeshCentral

La meta es hacer lo mismo que la web oficial, más lo que añade MeshCentral Router (redirección de puertos).

| # | Fase | Estado |
|---|------|--------|
| 1 | Entrada (contraseña, doble factor por app o por correo), confianza en el certificado con huella fijada, lista de equipos en vivo, servidores guardados, abrir el equipo en el navegador | **hecha (0.1)** |
| 2 | Túneles de relé; **terminal** (shell, cmd, PowerShell) con xterm.js; tema claro, oscuro o como Windows | **hecha (0.2)** |
| 3 | **Inicio de sesión único** (OpenID Connect, SAML, Microsoft, Google, GitHub…) en el navegador de Windows; autoactualización | **hecha (0.3–0.4)** |
| 4 | **Ficheros**: explorar, subir, bajar, renombrar, borrar, crear carpetas | **hecha (0.5)** |
| 5 | **Escritorio remoto**: visor nativo, teclado y ratón, una ventana por pantalla remota, calidad, escribir el portapapeles, Ctrl+Alt+Supr, mensajes del agente (consentimiento) | **hecha (0.6)**; copiar y pegar de todo con equipos Windows **hecho (0.12)**; ficheros con la ventana de copia de Windows, pantalla completa, el menú del equipo y las teclas en el visor, bloquear la pantalla y bloquear la entrada de quien está allí **hecho (0.13)**; apagar la pantalla, más adelante |
| 6 | **Página del equipo**: datos generales, inventario de hardware y del agente, interfaces de red, notas, etiquetas, descripción, historial | **hecha (0.7)** |
| 7 | **Acciones sobre el equipo**: encender por red, suspender, reiniciar, apagar, ventana de mensaje, notificación, ejecutar órdenes, en varios equipos a la vez | **hecha (0.8)**; chat, actualizar el agente y energía por Intel AMT, más adelante |
| 8 | **Grupos de equipos**: crear, editar, borrar, invitaciones (enlaces de instalación del agente), usuarios y permisos del grupo, mover equipos | **hecha (0.9)**; ajustes del grupo (consentimiento, avisos, borrado automático) y grupos de Intel AMT o locales, más adelante |
| 9 | **Usuarios y cuentas**: usuarios, grupos de usuarios, permisos, mi cuenta (doble factor, llaves, tokens de acceso) | **hecha (0.10)**; añadir una llave de seguridad (necesita el WebAuthn del navegador), doble factor por correo o SMS y sesiones abiertas, más adelante |
| 10 | **Servidor**: sucesos, estadísticas, grabaciones de sesiones, informes, avisos | **hecha (0.11)**; grabaciones de sesiones (lista, descarga y reproducción), más adelante |
| 11 | **Redirección de puertos** (para sustituir a MeshCentral Router), Escritorio remoto de Windows y SSH dentro de la app | **hecha (0.12)**; redirección UDP, y RDP/SSH solo por el servidor (equipos sin agente), más adelante |
| 12 | Versión firmada, instalador (winget) y actualizaciones automáticas (las actualizaciones ya están desde la 0.3) | **en marcha**: firma gratuita de SignPath Foundation solicitada ([política de firma](CODE_SIGNING.md)); después, instalador y winget |
