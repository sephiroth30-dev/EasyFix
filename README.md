# EasyFix — por Andrés Hernández

Herramienta de técnico para Windows 10 y 11. Un `.exe` portable que se lleva en USB: analiza el
equipo del cliente, aplica las mejoras seguras, repara los errores comunes del sistema, actualiza
controladores e instala el software base. Todo reversible.

**Descarga:** [última versión](https://github.com/sephiroth30-dev/EasyFix/releases/latest) ·
[todas las versiones](https://github.com/sephiroth30-dev/EasyFix/releases)

No se instala. Se copia al pendrive, se ejecuta, se retira el pendrive. No hay instalador ni nada
que desinstalar.

---

## Estado — v0.17.0

| | |
|---|---|
| Pruebas | **619 pasan, 2 se omiten** (599 Core + 20 App) |
| Build | `Release` limpio, **0 warnings** |
| CI | `windows-latest` en cada push; release automático al empujar un tag `v*.*.*` |
| `.exe` | ~69 MiB, portable, self-contained |
| Verificado en Windows | parcialmente — ver [«Lo que no se verificó»](#lo-que-no-se-verificó) |

### Las cinco acciones

| Acción | Qué hace | Modifica el equipo |
|---|---|:-:|
| **Analizar** | Corre sola al abrir. Disco, RAM, arranque, antivirus, pantallazos azules, programas de inicio, reputación del software instalado | no |
| **Mejorar rendimiento** | Temporales, programas de inicio, plan de energía, TRIM | sí |
| **Reparar errores** | DISM/SFC, chkdsk, red, Windows Update | sí |
| **Buscar controladores** | Windows Update, por clase de riesgo | sí |
| **Instalar programas** | Diez programas, de su origen oficial, en silencio | sí |
| **Deshacer** | Revierte la última corrida, incluso entre sesiones | sí |

---

## La regla que gobierna el proyecto

> **Un dato que no se pudo medir nunca se cuenta como verde.
> Un éxito falso es peor que un fallo falso.**

Sale de cuatro versiones seguidas (v0.5.0 – v0.8.0) en que la app reportaba diagnósticos que no
había medido. En un equipo de cliente, un «todo bien» inventado hace que el técnico cierre el caso
con el problema adentro.

En la práctica: los campos que pueden no leerse son `nullable`, el checklist tiene **tres** estados
(verde / rojo / **no determinado**) y el consumo **falla cerrado** — BitLocker desconocido bloquea,
dominio desconocido restringe, impresora desconocida protege el Spooler.

---

## Lo que esta app promete — y lo que no

No existe el «+200% de rendimiento» por software. Lo que sí se puede medir:

| Intervención | Ganancia real | Quién la hace |
|---|---|---|
| HDD → SSD | Boot 300–1000%. La mejora #1, sin competencia | Hardware (EasyFix lo **recomienda**) |
| RAM 4→8/16 GB en equipo que swapea | Elimina los congelamientos | Hardware (EasyFix lo **recomienda**) |
| Quitar antivirus duplicado / PUP / bloatware | 20–60% en un equipo lleno de basura | EasyFix |
| Desactivar startup pesado | 10–40% del tiempo de arranque | EasyFix |
| Temporales + caché de WU + WinSxS | Espacio en disco. Perf solo si `C:` estaba por debajo del 10% libre | EasyFix |
| Plan de energía, TRIM, defrag correcto | 0–15% | EasyFix |
| Equipo que ya está sano | **0–5%. Se dice y listo.** | — |

EasyFix reporta **milisegundos y GB medidos**, no porcentajes inventados. La métrica de arranque
sale del Event ID 100 de `Microsoft-Windows-Diagnostics-Performance/Operational`, campo
`MainPathBootTime`, comparando antes y después de un reinicio real.

### Lo que EasyFix se niega a hacer

Mitos del «optimizador de PC», **excluidos por diseño** y anotados acá para que no se cuelen más
adelante:

- Borrar Prefetch — **empeora** el arranque, Windows lo reconstruye.
- Limpiadores de registro — cero ganancia medible, riesgo real.
- «Optimizadores de RAM» — recortar working sets hace que todo se vuelva más lento.
- Desfragmentar un SSD — desgasta celdas sin ningún beneficio.
- Desactivar el archivo de paginación.
- `DISM /ResetBase` — impide desinstalar actualizaciones.
- Listas de servicios copiadas de foros — rompen Windows Update, Store, audio e impresión.
- Desactivar Defender.
- «Tweaks gamer» de resolución de timer, CPU unparking.
- Borrar `WinSxS` a mano.
- Instalar firmware o BIOS. Un flasheo fallido es daño físico, no «reinstalar».

---

## Cómo se decide qué se toca

El punto más delicado del proyecto. **No se usa match por nombre de archivo**: `*VPN*` protegería a
un malware llamado *MyVPN* y no protegería a *Pulse Secure*. La decisión es de tres capas, y la
primera que diga «no» gana:

1. **Bloqueo duro** — producto de seguridad registrado en `root\SecurityCenter2`, firmado por
   Microsoft Windows, con servicios dependientes en ejecución, o con un driver asociado. Intocable.
2. **Lista blanca positiva** — solo lo que está en `appsettings.json` como par
   *(publisher Authenticode, producto)* **con firma válida** se desactiva en automático.
3. **Zona gris** — todo lo demás. Checkbox, nunca automático. **Desconocido = pedir permiso.**

Los binarios sin firmar caen en Capa 3 y se marcan en rojo como posible malware.

### Software de mala reputación

`InstalledProgramsReader` lee las claves `Uninstall` (**jamás** `Win32_Product`, que dispara
reparaciones de MSI) y las cruza contra `Reputation.KnownBad` en `appsettings.json` — **51 entradas**,
match por publisher + producto, separando *confirmado dañino* de *dudoso*.

**EasyFix nunca desinstala solo.** Lo reporta y el técnico decide. Un desinstalador silencioso que
se equivoca en el equipo de un cliente borra una herramienta de trabajo.

**Antivirus duplicados:** si hay varios y ninguno con licencia paga detectable, se recomienda dejar
solo Windows Defender. Recomendación, no acción.

### Controladores — tres clases de riesgo

| Clase | Qué incluye | Qué hace EasyFix |
|---|---|---|
| **Safe** | Red, GPU, audio, USB, Bluetooth, impresora, teclado/mouse/cámara | Se instala solo. Se revierte desde el Administrador de dispositivos o el punto de restauración |
| **NeedsConfirmation** | Controladores de almacenamiento (SCSI/RAID/HDC) y todo lo que no matcheó la lista blanca | Se nombra, no se instala |
| **Firmware** | BIOS, firmware de placa | **Nunca.** Se nombra y se manda a la página del fabricante |

El catálogo sale de Windows Update por COM (`Microsoft.Update.Session`), no de un repositorio de
terceros.

---

## Reversibilidad

- **Punto de restauración verificado** antes del primer cambio. Si no se puede crear, se aborta.
- **Journal por corrida** en `%ProgramData%\EasyFix\runs\<runId>.jsonl`, escrito **antes** de cada
  cambio. Si la app muere a mitad de camino, el undo sigue funcionando.
- Botón **«Deshacer la última reparación»**, que funciona entre sesiones.
- El borrado de archivos se marca `reversible: false` y se dice en el reporte. **No se miente sobre
  el undo.**

### BitLocker

`chkdsk /f` y los resets de red pueden alterar la medición de integridad del TPM y provocar que
Windows pida la clave de recuperación de 48 dígitos al reiniciar. Si el cliente no la tiene, queda
fuera de su propio equipo. EasyFix detecta BitLocker, exige confirmación explícita de que la clave
está a mano, y suspende la protección antes de esos fixes. Sin confirmación, quedan bloqueados.

### Equipos de dominio

Con `PartOfDomain = true` la app entra en **modo restringido**: solo temporales, cachés y
diagnóstico. Las GPO revierten los cambios de servicios y startup, y desactivar agentes corporativos
rompe el equipo para el área de TI.

---

## Instalar programas

**Ningún programa del catálogo depende de winget.** Los diez se bajan de su origen oficial, y lo
verifica un test sobre la configuración real: agregar uno sin origen propio rompe el build.

| Programa | Origen | Por defecto |
|---|---|:-:|
| Google Chrome | MSI empresarial de `dl.google.com` | ✓ |
| Adobe Acrobat Reader | API de Adobe → `ardownload3.adobe.com` | ✓ |
| 7-Zip | GitHub `ip7z/7zip` | ✓ |
| Visual C++ Redist x64 / x86 | `aka.ms/vs/17/release/` | ✓ |
| RustDesk | GitHub `rustdesk/rustdesk` | ✓ |
| VLC | listado de `get.videolan.org` | |
| Notepad++ | GitHub `notepad-plus-plus` | |
| Firefox | `download.mozilla.org` | |
| AnyDesk | `download.anydesk.com` | |

7-Zip en lugar de WinRAR porque WinRAR es shareware con nag eterno. RustDesk en lugar de AnyDesk
como opción por defecto porque es libre y se puede autohospedar. Los Visual C++ Redistributables
arreglan la mayoría de los «falta tal DLL» después de reinstalar Windows.

**Nada viene empaquetado en el `.exe`** — los 69 MiB son el runtime de .NET. Cada programa se baja
en el momento de instalar, así que siempre entra la última versión publicada. El equipo del cliente
necesita internet.

Agregar o cambiar un programa: **[`docs/CATALOGO.md`](docs/CATALOGO.md)**, que incluye el
procedimiento de verificación de la fuente. Nunca se configura una URL sin pedirla antes.

### Mientras instala, cuenta una historia

Cada programa tiene un hecho real sobre su origen que se muestra mientras se descarga, con su logo
—sacado del instalador recién bajado— y una barra de avance propia con los MB. Una instalación de
varios minutos con una barra quieta se siente eterna y deja al técnico sin nada que decirle al
cliente.

### winget sigue estando

Si falta, se hace su bootstrap (`Microsoft.DesktopAppInstaller`). Se usa para **«Actualizar lo
instalado»**, que no tiene sustituto. Pero ya no es el único camino para ningún programa del
catálogo.

### Programas predeterminados

Chrome como navegador y Acrobat Reader para los PDF, por `DISM /Import-DefaultAppAssociations`, que
es la vía sancionada por Microsoft. **Su alcance real son los perfiles de usuario creados después.**

Para el perfil que ya está abierto no hay vía automática: desde febrero de 2024 el driver
`UCPD.sys` impide escribir las claves de `UserChoice`, y las herramientas que lo logran igual
necesitan desactivarlo — pelearse con una protección del sistema es exactamente lo que este
proyecto no hace.

---

## Desarrollo

### Compilar y probar (macOS, Linux o Windows)

Solo hace falta el **.NET 8 SDK**. En macOS/Linux, sin `sudo`:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

```bash
git clone https://github.com/sephiroth30-dev/EasyFix.git && cd EasyFix
dotnet build -c Release     # los tres proyectos, incluido el de WPF
dotnet test  -c Release     # 619 pasan, 2 se omiten fuera de Windows
```

**WPF compila fuera de Windows** gracias a `<EnableWindowsTargeting>true</EnableWindowsTargeting>`:
el SDK baja el reference pack y el XAML se compila y valida igual. Lo que **no** se puede fuera de
Windows es **ejecutar** el `.exe` ni probar nada que toque WMI, registro, servicios o puntos de
restauración.

### Generar el `.exe` portable

```bash
dotnet publish src/EasyFix.App -c Release -o publish
```

Sale un único `publish/EasyFix.exe`. Trae el runtime de .NET adentro comprimido, así que no hay que
instalar nada en el equipo del cliente.

Al abrirlo, Windows pide permiso de administrador (va declarado en el manifiesto): sin elevación no
se puede leer el estado SMART del disco ni consultar los servicios. Y como el `.exe` no está
firmado, SmartScreen lo va a marcar como desconocido — «Más información» → «Ejecutar de todas
formas».

### Publicar una versión

Todo automático. Tres pasos y el resto lo hace el workflow:

1. Anotar la versión en `CHANGELOG.md`.
2. Subir `<Version>` en `Directory.Build.props` — **un solo lugar**; de ahí salen la barra de título
   y las propiedades del `.exe`.
3. `git commit`, `git tag vX.Y.Z`, `git push --follow-tags`.

`release.yml` verifica que el tag coincida con `Directory.Build.props` (si no, falla), corre los
tests en Windows, publica el `.exe` y crea el Release con las notas de esa sección del CHANGELOG.
Sube dos archivos: `EasyFix-vX.Y.Z.exe` —para saber cuál es cuál en un USB con varias descargas— y
`EasyFix.exe`, que mantiene vivo el link estable `/releases/latest/download/EasyFix.exe`.

### Antes de entregar algo: la skill `verify`

`.claude/skills/verify/SKILL.md` es el procedimiento obligatorio antes de publicar. Tres reglas que
no se negocian:

1. **Nunca se modifica una prueba para que pase.**
2. **Tres intentos y se para**, avisando qué falló.
3. **Se declara lo que no se pudo verificar.**

### Los tres agentes

`.claude/agents/` — `arquitecto` (diseña y revisa, no escribe código de producción), `desarrollador`
(implementa un plan ya decidido), `verificador` (corre builds, consulta datos, confirma hechos).

---

## Arquitectura

```
src/EasyFix.App/      WPF. Ventana, XAML, ViewModels. Cero lógica de negocio.
src/EasyFix.Core/     Toda la lógica. Sin referencia a WPF (verificado por test).
  Diagnostics/        DiagnosticEngine, SystemProbe, análisis de pantallazos azules
  Fixes/              IFix — CanApply / Apply, con journal. FixRunner y sus compuertas
  Drivers/            Catálogo de Windows Update por COM + clasificador de riesgo
  Apps/               Instalación: winget, descarga directa, predeterminados
  Classification/     El clasificador de 3 capas de programas de inicio
  Reputation/         Cruce contra el catálogo de PUPs
  Verification/       Checklist de 12 condiciones, antes y después
  Rollback/           Journal, puntos de restauración, motor de deshacer
  Recommendations/    Hardware, saturación, software según el equipo
  Cleaning/           Limpieza junction-safe de temporales
  Safety/             PathGuard
  Network/            Detección de conectividad
  Abstractions/       IProcessRunner, IFileTree — para poder mockear
tests/                619 pruebas
tools/spike/          Verify-WindowsApis.ps1 — contratos de Windows, solo lectura
scripts/              bootstrap.ps1, extract-changelog.sh
appsettings.json      Umbrales y listas curadas. Nada de esto va hardcodeado en C#.
```

Detalle completo en **[`docs/ARQUITECTURA.md`](docs/ARQUITECTURA.md)**.

**Procesos externos:** siempre `ProcessStartInfo.ArgumentList` (nunca strings concatenados),
`UseShellExecute = false`, ruta absoluta al ejecutable, y toda ruta canonicalizada y verificada bajo
su raíz esperada antes de borrar o copiar.

**Borrado de temporales:** enumeración manual comprobando `FileAttributes.ReparsePoint`. Un junction
dentro de `%TEMP%` apuntando a `Documents` convertiría la limpieza en borrado de datos del cliente.

---

## Lo que no se verificó

Honestidad sobre el estado real, con el mismo criterio que la app aplica a sus diagnósticos:

| Qué | Por qué sigue sin verificar |
|---|---|
| **Cómo se ve la interfaz** | Ningún build la renderiza: WPF compila fuera de Windows pero no corre |
| **El catálogo de controladores** | `Microsoft.Update.Session` es COM por late-binding; nunca se consultó Windows Update real |
| **Los ProgId de los predeterminados** | `ChromeHTML` y `AcroExch.Document.DC` salen de la documentación, no del registro de un equipo |
| **Que los instaladores corran callados** | Los argumentos salen del manifiesto de winget, pero acá los ejecuta EasyFix directo |
| **El logo sacado del instalador** | Es P/Invoke a `shell32`; si falla se ve el monograma, nunca rompe nada |
| **`SystemProbe`** | Concentra WMI, registro y Event Log en un archivo, a propósito: correr el `.exe` en un equipo real lo valida entero de una vez |

Guía de qué probar y en qué orden: **[`docs/PRUEBAS.md`](docs/PRUEBAS.md)**.

---

## Documentación

| Documento | Para qué |
|---|---|
| [`docs/ARQUITECTURA.md`](docs/ARQUITECTURA.md) | Módulo por módulo: qué hace cada uno y por qué está así |
| [`docs/CATALOGO.md`](docs/CATALOGO.md) | Agregar o cambiar un programa, con el procedimiento de verificación de la fuente |
| [`docs/PRUEBAS.md`](docs/PRUEBAS.md) | Qué probar en Windows, en qué orden, y qué capturar cuando algo falla |
| [`docs/PLAN.md`](docs/PLAN.md) | Documento de trabajo: estado, decisiones, qué falta y en qué orden |
| [`CHANGELOG.md`](CHANGELOG.md) | Versiones, con el resultado de cada prueba real |
| [`docs/RESUMEN-PARA-WEB.md`](docs/RESUMEN-PARA-WEB.md) | Resumen autocontenido para alguien que no conoce el proyecto |
