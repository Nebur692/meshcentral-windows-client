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

### Phases

| # | Phase | State |
|---|-------|-------|
| 1 | Sign-in (password, two-factor by app or email), certificate trust with fingerprint pinning, live device list, saved servers, open device in the browser | **done (0.1)** |
| 2 | Single sign-on (OIDC, e.g. ZITADEL, Keycloak, Entra ID) through an embedded browser (WebView2), and login keys | next |
| 3 | Terminal (command prompt and PowerShell) through the relay tunnel, in an embedded terminal | |
| 4 | Files: browse, upload, download, rename, delete | |
| 5 | Remote desktop: native viewer (JPEG tiles), keyboard and mouse, multiple displays, clipboard | |
| 6 | Device actions (wake, sleep, restart, message) and port mapping (to replace MeshCentral Router) | |
| 7 | Signed release, installer (winget) and automatic updates | |

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

### Fases

| # | Fase | Estado |
|---|------|--------|
| 1 | Entrada (contraseña, doble factor por app o por correo), confianza en el certificado con huella fijada, lista de equipos en vivo, servidores guardados, abrir el equipo en el navegador | **hecha (0.1)** |
| 2 | Inicio de sesión único (OIDC, p. ej. ZITADEL, Keycloak, Entra ID) con un navegador integrado (WebView2), y llaves de inicio de sesión | siguiente |
| 3 | Terminal (símbolo del sistema y PowerShell) por el túnel de relé, en un terminal integrado | |
| 4 | Ficheros: explorar, subir, bajar, renombrar, borrar | |
| 5 | Escritorio remoto: visor nativo (teselas JPEG), teclado y ratón, varias pantallas, portapapeles | |
| 6 | Acciones sobre el equipo (encender, suspender, reiniciar, mensaje) y redirección de puertos (para sustituir a MeshCentral Router) | |
| 7 | Versión firmada, instalador (winget) y actualizaciones automáticas | |
