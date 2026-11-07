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
| 5 | **Remote desktop** (next): native viewer, keyboard and mouse, multiple displays, quality and scaling, clipboard, Ctrl+Alt+Del, lock and blank screen, user consent | |
| 6 | **Device page**: general info, hardware and software inventory (Details), network interfaces, notes, tags, description, events and history | |
| 7 | **Device actions**: wake-on-LAN, sleep, restart, power off, message box, toast, chat, run commands and scripts, agent update, Intel AMT power | |
| 8 | **Device groups**: create, edit, delete, invitations (agent install links), group users and rights, move devices | |
| 9 | **Users and accounts** (administrators): users, user groups, rights, my account (two-factor, keys, sessions) | |
| 10 | **Server**: events, statistics, session recordings, reports, notifications | |
| 11 | **Port mapping** (to replace MeshCentral Router) and RDP/SSH relays | |
| 12 | Signed release, installer (winget) and automatic updates | |

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
| 5 | **Escritorio remoto** (siguiente): visor nativo, teclado y ratón, varias pantallas, calidad y escala, portapapeles, Ctrl+Alt+Supr, bloquear y apagar la pantalla, consentimiento del usuario | |
| 6 | **Página del equipo**: datos generales, inventario de hardware y software (Detalles), interfaces de red, notas, etiquetas, descripción, sucesos e historial | |
| 7 | **Acciones sobre el equipo**: encender por red, suspender, reiniciar, apagar, mensaje, aviso, chat, ejecutar órdenes y guiones, actualizar el agente, energía por Intel AMT | |
| 8 | **Grupos de equipos**: crear, editar, borrar, invitaciones (enlaces de instalación del agente), usuarios y permisos del grupo, mover equipos | |
| 9 | **Usuarios y cuentas** (administradores): usuarios, grupos de usuarios, permisos, mi cuenta (doble factor, llaves, sesiones) | |
| 10 | **Servidor**: sucesos, estadísticas, grabaciones de sesiones, informes, avisos | |
| 11 | **Redirección de puertos** (para sustituir a MeshCentral Router) y relés RDP/SSH | |
| 12 | Versión firmada, instalador (winget) y actualizaciones automáticas | |
