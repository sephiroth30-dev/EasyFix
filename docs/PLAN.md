# EasyFix — Optimizador y Reparador de Windows

> **Este es el documento de trabajo del proyecto.** Estado actual, decisiones tomadas y qué sigue.
> Versionado con el código, así que viaja con el repo. El `README.md` es el resumen para quien llega
> nuevo; esto es el detalle para seguir trabajando.

> Al retomar, leer en este orden: **v0.4.0 — Plan activo** → **Estado actual** → la sección del
> componente que se vaya a tocar.

## v0.4.0 — Plan activo: hacer que funcione

### Context

Se probó v0.3.0 en un equipo real (log del 2026-08-25, 96 líneas). La lectura inicial fue «todo
error», pero el log dice algo distinto y mucho más útil: **la mayor parte funcionó**, y los fallos
tienen cuatro causas concretas, todas identificadas con evidencia.

Lo grave no es que fallara: es que **la app reportó fallo donde hubo éxito**. Dos programas que ya
estaban instalados se reportaron como «no encontrado», y el punto de restauración que sí se creaba se
reportaba como no creado — lo que llevó al usuario a trabajar sin red de seguridad cuando la red
existía. Un diagnóstico que miente es peor que uno que falta.

### Lo que el log prueba que YA FUNCIONA

No hay que tocar nada de esto:

| Componente | Evidencia en el log |
|---|---|
| `WingetLocator` bajo `Program Files\WindowsApps` | líneas 6, 26, 85 — lo encontró siempre |
| Instalación con winget | líneas 9, 12 — 7-Zip y VCRedist instalados, código 0 |
| Descarga directa desde GitHub | líneas 94-96 — resolvió RustDesk 1.4.9, bajó el asset, lanzó el instalador |
| Override del punto de restauración | líneas 36, 61 — continuó por decisión explícita |
| `SystemFileRepairFix` (DISM + sfc) | líneas 37-40 — reparó de verdad, 10 min |
| `DiskCheckFix` | línea 42 — `NothingToDo`, sin errores |
| `WindowsUpdateResetFix` | línea 54 — reconstruyó `SoftwareDistribution` y `catroot2` |
| `NetworkStackResetFix` | línea 59 — aplicado, pide reinicio |
| Journal y logging | el log entero es legible y suficiente para diagnosticar |

### Los cuatro fallos, con su causa

**F1 · El punto de restauración se reportaba como no creado, y sí se creaba.** (P0)

Líneas 2, 4, 33, 35, 60: *«reportó éxito pero la secuencia no aumentó (210 → 210)»*. Pero entre
corridas la secuencia avanzó **210 → 213 → 214**: los puntos se estaban creando.

Causa: `SRSetRestorePoint` —la API detrás de `CreateRestorePoint`— es **asíncrona**. Cuando
`RestorePointService.CreateVerified` enumera inmediatamente después, el punto nuevo todavía no
aparece. La verificación es una carrera que casi siempre pierde.

Segundo defecto en la misma función: la línea 33 muestra `(0 → 0)`, y veinte segundos después la
enumeración devolvió 213. `HighestSequence()` devuelve `0` tanto cuando no hay puntos como cuando la
consulta falla, y `0` se interpreta como «no hay». Un fallo de lectura se está tratando como un hecho.

**F2 · Constantes de código de salida de winget equivocadas → fallos falsos.** (P0)

Verificado contra la documentación de winget-cli:

| Código | Lo que yo tenía | Lo que es de verdad |
|---|---|---|
| `0x8A15002B` | `NO_APPLICATIONS_FOUND` | **`UPDATE_NOT_APPLICABLE`** |
| `0x8A150014` | `FAILED_TO_OPEN_ALL_SOURCES` | **`NO_APPLICATIONS_FOUND`** |

Consecuencia en el log: a las 13:56, 7-Zip y VCRedist devolvieron `0x8A15002B` y se reportaron como
«NotFound» (líneas 89, 93). Pero la propia app los había instalado a las 11:58. `UPDATE_NOT_APPLICABLE`
sobre un paquete instalado significa **«ya está y no hay nada que actualizar»** — o sea, éxito.

Y `0x8A150014` en RustDesk (línea 31) era el diagnóstico correcto —el ID ya no existe en el catálogo—
pero etiquetado como problema de fuentes.

**Descubrimiento que evita que esto vuelva a pasar:** `winget error --output <archivo>` exporta la
tabla completa de códigos del winget instalado en el equipo. Se puede leer en runtime en vez de
hardcodear constantes que pueden estar mal.

**F3 · «Acceso denegado» al lanzar winget a mitad de la tanda.** (P1)

Líneas 19-25: `Win32Exception (5)` al arrancar `winget.exe`, después de que dos instalaciones
funcionaran con ese mismo binario.

Causa, visible en el log: la ruta **cambió entre corridas**, de
`Microsoft.DesktopAppInstaller_1.29.280.0` (línea 6) a `_1.29.290.0` (línea 26). winget se
autoactualizó y la carpeta del paquete viejo dejó de existir. La ruta se resuelve una vez por tanda y
después se usa una que ya no es válida.

El mensaje también revela que el directorio de trabajo heredado era `C:\Users\Andres\Downloads`, que
no se está fijando explícitamente.

**F4 · VLC devolvió 1 justo después de VCRedist.** (P1)

Líneas 13-16: VLC arrancó a las 11:58:59, exactamente cuando VCRedist terminó, y falló con código 1.
El stderr vino vacío y stdout mostraba *«Encontrado VLC media player [VideoLAN.VLC] Versión 3.0.23»* —
o sea, winget encontró el paquete y el instalador falló.

Sospecha principal: contención del mutex `_MSIExecute` de Windows Installer. La instalación es serial,
pero «serial» no alcanza: el instalador anterior puede seguir finalizando cuando arranca el siguiente.

### Defectos secundarios que el log también expone

**F5 · Detección de corrupción de DISM dependiente del idioma.** (P2) Líneas 37-38: dos llamadas a
DISM, o sea que `RestoreHealth` corrió aunque `ScanHealth` probablemente no encontró daño. La detección
compara texto en inglés contra un Windows en español. Cuesta ~4 minutos por corrida. Se arregla
pasando `/English` a DISM, que fuerza salida determinista.

**F6 · `net stop bits` código 2 tratado como advertencia.** (P2) Línea 45: *«El servicio BITS no se ha
iniciado»*. Es el caso normal —el servicio ya estaba detenido— y ensucia el log.

**F7 · `netsh int ip reset` devuelve 1.** (P2) Líneas 57, 82. Habitualmente benigno, pero hoy se
registra como advertencia sin distinguirlo de un fallo real.

**F8 · Sin resultado de RustDesk.** (P2) El log termina en la línea 96 con el instalador lanzado y
nunca registra el desenlace. Hay que confirmar que `--silent-install` es el flag correcto de RustDesk
1.4.9 y capturar el resultado.

**F9 · Reparación repetida sin memoria.** (P2) Se corrió el mismo ciclo DISM+sfc dos veces (13:06 y
13:18): veinte minutos repetidos. La app debería avisar que ya se hizo hace poco.

**F10 · El log no registra qué diagnosticó.** (P1, observabilidad) No hay ninguna línea del
diagnóstico: `SystemProbe` solo escribe cuando algo falla. No se puede saber si el análisis corrió ni
qué midió. **Nivel 1 de `docs/PRUEBAS.md` sigue sin verificarse**, y el log no ayuda a saberlo.

---

### Los arreglos

Todo lo que sigue es lógica pura o cambios acotados. Nada requiere rediseño.

#### P0 · `RestorePointService` — verificación con espera

`src/EasyFix.Core/Rollback/RestorePointService.cs`

1. **Distinguir «no hay puntos» de «no pude leer».** `HighestSequence()` pasa a devolver `long?`:
   `null` cuando la consulta falla, `0` cuando genuinamente no hay ninguno. Un `null` en la lectura
   *previa* no impide continuar; un `null` en la *posterior* no se interpreta como fracaso.
2. **Esperar a que aparezca.** Después de `CreateRestorePoint`, sondear la secuencia con reintentos
   —cada 2 s hasta 60 s— y considerarlo creado en cuanto supere el valor previo. La API es asíncrona:
   esto no es un parche, es cómo hay que consumirla.
3. **Neutralizar el límite de 24 h.** Es un caso real y frecuente: cualquier equipo que ya tuvo
   actividad ese día. Poner `SystemRestorePointCreationFrequency = 0` en
   `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore`.

   Es la única parte del plan que **modifica una configuración del sistema** para que la herramienta
   funcione, así que va con tres condiciones: se registra en el journal **con su valor anterior**,
   `Deshacer todo` lo revierte, y queda **configurable en `appsettings.json`**
   (`Thresholds.DisableRestorePointThrottle`, por defecto `true`) para poder apagarlo sin recompilar.

   El costo real es más espacio en copias de sombra, acotado por el límite que ya tiene configurado
   Restaurar sistema. El beneficio es que el punto de restauración deja de ser una lotería, que es
   justamente lo que hizo falta apagar en la primera prueba.
4. Si después de todo eso no hay punto, recién ahí ofrecer el override que ya existe.

#### P0 · Códigos de winget — tabla real, no constantes adivinadas

`src/EasyFix.Core/Apps/WingetResultParser.cs`, `WingetService.cs`

1. Corregir las dos constantes equivocadas y agregar `UPDATE_NOT_APPLICABLE`.
2. **`UPDATE_NOT_APPLICABLE` sobre un `install` → `AlreadyInstalled`**, no fallo. Es el arreglo que
   convierte dos fallos falsos en dos éxitos correctos.
3. **Cargar la tabla desde el propio winget** con `winget error --output <archivo>` la primera vez que
   se usa, y cachearla por corrida. Las constantes quedan como respaldo si el comando no existe en esa
   versión. Así el mapeo lo provee el winget instalado y no mi memoria.
4. Nuevo `WingetOutcome.NotInCatalog` para `NO_APPLICATIONS_FOUND`, con el mensaje que apunta a
   revisar el ID en `appsettings.json` — que es lo que efectivamente pasó con RustDesk.

#### P1 · Invocación de winget resistente a la autoactualización

`src/EasyFix.Core/Apps/WingetLocator.cs`, `WingetService.cs`, `Processes/SafeProcessRunner.cs`

1. **Resolver la ruta antes de cada invocación**, no una vez por tanda.
2. **Reintentar una vez** ante `Win32Exception` con código 5 (acceso denegado) o 2 (no encontrado),
   re-resolviendo primero. Es exactamente el escenario del log.
3. **Fijar `WorkingDirectory` explícito** a `System32` en `SafeProcessRunner`. Hoy se hereda de donde
   se lanzó el `.exe` —`Downloads` en la prueba—, y eso no debería influir en nada.

#### P1 · Espera del mutex de Windows Installer

`src/EasyFix.Core/Apps/WingetService.cs`

Antes de cada instalación, esperar a que el mutex global `_MSIExecute` esté libre, con tope de unos
minutos. Si sigue tomado, reintentar una vez tras una pausa antes de declarar fallo. Es la explicación
más probable del código 1 de VLC, y es barato de implementar contra `Mutex.OpenExisting`.

#### P1 · Que el log registre el diagnóstico

`src/EasyFix.Core/Diagnostics/SystemProbe.cs`, `App/ViewModels/MainViewModel.cs`

Loguear en `Information` el `SystemSnapshot` completo y cada hallazgo del reporte. Hoy el log solo
habla cuando algo falla, así que **no sirve para confirmar que el diagnóstico funcionó** — que es
justamente lo que falta verificar. Sin esto, cada prueba necesita capturas de pantalla.

#### P2 · El resto

| # | Arreglo | Archivo |
|---|---|---|
| F5 | Pasar `/English` a DISM y ajustar la detección de corrupción | `Fixes/RepairFixes.cs` |
| F6 | `net stop` con código 2 = servicio ya detenido = OK | `Fixes/RepairFixes.cs` |
| F7 | Distinguir el código 1 benigno de `netsh int ip reset` | `Fixes/RepairFixes.cs` |
| F8 | Verificar el flag silencioso de RustDesk y registrar el desenlace | `appsettings.json`, `Apps/DirectDownloadInstaller.cs` |
| F9 | Recordar la última reparación y avisar si fue hace menos de 30 min | `Rollback/`, `MainViewModel` |

### Verificación

**Tests nuevos, todos sin Windows** (la lógica está aislada a propósito):

- `RestorePointService`: no se puede testear sin WMI, pero **sí la política**. Se extrae la decisión
  «¿está creado?» a una función pura sobre `(secuenciaAntes, secuenciaDespués, intentos)` y se testea:
  aparece al tercer intento → creado · nunca aparece → no creado · lectura previa fallida → no
  bloquea · lectura posterior fallida → no cuenta como fracaso.
- `WingetResultParser`: los tres códigos reales del log — `0x8A15002B` → `AlreadyInstalled`,
  `0x8A150014` → `NotInCatalog`, `1` con stdout de VLC → fallo del instalador. Y que la tabla cargada
  desde `winget error` gane sobre las constantes de respaldo.
- `WingetService`: la ruta se re-resuelve por paquete · un `Win32Exception(5)` dispara un reintento ·
  el mutex tomado espera y no falla de inmediato.
- `RepairFixes`: salida de DISM en inglés con y sin corrupción · `net stop` código 2 = OK.

**En Windows, con este orden:**

1. **Nivel 1 de `docs/PRUEBAS.md` primero** — es lo único que sigue sin verificarse, y ahora el log
   va a mostrar qué midió. Confirmar que el resumen del equipo dice bien modelo, RAM y tipo de disco.
2. Reintentar la instalación de los 10 programas en un equipo **donde ya estén algunos**: es el caso
   que producía los fallos falsos. Esperado: `Ya estaba` en verde, no `NotFound` en rojo.
3. Reparar **sin** marcar el override, para confirmar que ahora el punto de restauración se crea y se
   verifica. Comprobarlo en `rstrui.exe`.
4. `Deshacer todo`, y verificar que revierte también el cambio de
   `SystemRestorePointCreationFrequency`.

---

## Context
Necesitas una herramienta de técnico: la llevas en USB a los equipos que reparas, das un click y el
equipo queda limpio, rápido y con el software base instalado. Hoy ese trabajo es manual y repetitivo
(msconfig, temporales, SFC, instalar Chrome/Reader/etc.) y depende de que recuerdes cada paso.

EasyFix automatiza ese flujo con tres acciones: **mejorar rendimiento**, **instalar programas**,
**reparar errores**. Además diagnostica lo que el software *no puede* arreglar y lo dice explícito
(disco mecánico, RAM insuficiente, disco muriendo) para que la recomendación de hardware sea un dato
medido, no una corazonada.

**Alcance decidido:** uso propio / técnico en sitio · Windows 10 + 11 · C# .NET 8 + WPF ·
un click con undo total · portable .exe (sin instalador, sin firma de código, sin telemetría).

---

## Estado actual
> Última actualización: después de la primera prueba real de v0.3.0 (log del 2026-08-25).
> Rama `main`, árbol limpio, tag `v0.3.0`. **282 tests pasan, 2 se omiten** (los que exigen Windows).
> `dotnet build` limpio en los tres proyectos, incluido el de WPF.
>
> Los arreglos pendientes están en **v0.4.0 — Plan activo**, arriba.

### Lo que ya funciona y está verificado

| Componente | Dónde | Tests |
|---|---|---|
| Clasificador de 3 capas | `Core/Classification/StartupClassifier.cs` | 20 |
| Journal JSONL + `UndoEngine` | `Core/Rollback/` | 25 |
| Limpiador junction-safe de temporales | `Core/Cleaning/JunctionSafeCleaner.cs` | 15, uno con symlink real |
| Motor de diagnóstico (paralelo, timeout por chequeo) | `Core/Diagnostics/DiagnosticEngine.cs` | 13 |
| Motor de recomendación de hardware | `Core/Recommendations/HardwareAdvisor.cs` | 26 |
| Condiciones de servicio (el caso `SysMain` en HDD) | `Core/Fixes/ServiceConditionEvaluator.cs` | 15 |
| Módulo winget: instalar programas | `Core/Apps/WingetService.cs` | 17 |
| Runner de procesos endurecido | `Core/Processes/SafeProcessRunner.cs` | 5 |
| `PathGuard`, parser de certificados, IDs de winget, loader de config | `Core/Safety/`, `Core/Apps/`, `Core/Configuration/` | resto |
| UI en WPF: inicio, analizando, reporte, instalar | `App/Views/MainWindow.xaml` | compila; **sin ejecutar** |

### Verificado en un equipo real (log del 2026-08-25)

- **`WingetLocator`** encuentra winget bajo `Program Files\WindowsApps`, incluso elevado. La hipótesis
  de que el alias por usuario fallaría al elevar era correcta y la solución funciona.
- **Instalación con winget**: 7-Zip y VCRedist instalados con código 0.
- **Descarga directa desde GitHub**: resolvió RustDesk 1.4.9 y bajó el asset correcto.
- **`SystemFileRepairFix`**: DISM `/ScanHealth` → `/RestoreHealth` → `sfc` corrió completo y reparó.
- **`DiskCheckFix`, `WindowsUpdateResetFix`, `NetworkStackResetFix`**: aplicados.
- **El override del punto de restauración** funciona y queda registrado.
- **`RustDesk.RustDesk` ya NO existe en winget** — confirmado por `NO_APPLICATIONS_FOUND`. La decisión
  de bajarlo de GitHub era necesaria, no precautoria.

### Todavía NO verificado

- **`Core/Diagnostics/SystemProbe.cs`** — el log no tiene ninguna línea del diagnóstico, así que no se
  sabe si corrió ni qué midió. Es el defecto **F10** del plan activo: hasta que el log registre el
  snapshot, cada prueba necesita capturas. **Es lo primero a verificar.**
- **`RestorePointService`** — se ejecuta, pero su verificación es defectuosa (**F1**).
- **`BitLockerService`** — el equipo de prueba no tenía BitLocker.
- **El análisis de pantallazos** contra un equipo que efectivamente los tenga.
- **`ScheduleMemoryTestFix` y `RemoveCorrelatedUpdateFix`** — nunca se ofrecieron, porque el equipo no
  tenía pantallazos que los justificaran.
- **`tools/spike/Verify-WindowsApis.ps1`** — escrito, nunca corrido. Sigue siendo la vía más rápida
  para validar de una todos los contratos de WMI. Es solo lectura.

### Lo que falta implementar

1. **Los fixes Tier A** y el botón «Mejorar rendimiento» de verdad. Bloqueados por el punto 2.
2. **`RestorePointService`** — `SystemRestore.CreateRestorePoint` en `root\default`. Es el
   prerrequisito de todo lo demás: sin punto de restauración verificado, la app no toca nada.
3. **Los `IUndoHandler`** concretos (registro, servicios, plan de energía, BitLocker). `UndoEngine` ya
   existe y está testeado; le faltan los handlers que hacen el trabajo.
4. **«Reparar errores»**: DISM, SFC, chkdsk, resets de red y de Windows Update, más la reanudación
   por `RunOnce --resume` tras el reinicio.
5. **Wizard de perfil**, los tres niveles.
6. **Medición antes/después** del arranque: el baseline ya se lee, falta guardarlo en el journal y
   compararlo tras el reinicio.

### Errores corregidos que conviene no repetir

- **WPF sí compila fuera de Windows** con `EnableWindowsTargeting=true`. Una versión anterior de este
  plan afirmaba lo contrario y bloqueaba el desarrollo de la UI detrás de una VM innecesaria.
- **Los contadores de rendimiento están localizados.** `\Memory\Committed Bytes` no existe en un
  Windows en español. Se usan las clases `Win32_PerfFormattedData_*`, cuyas propiedades tienen el
  mismo nombre en cualquier idioma.
- **`Path.GetInvalidFileNameChars()` depende del sistema anfitrión.** En Unix no incluye `:`, así que
  un `runId` con timestamp ISO habría generado un nombre inválido al llegar a Windows. El set de
  Windows va explícito.
- **`OperationCanceledException` también es `Exception`.** Un `catch` general se comía la cancelación
  del usuario y devolvía un reporte falso lleno de «no se pudo determinar».
- **winget se instala por usuario.** Al elevar, `%LOCALAPPDATA%` puede resolver al perfil del
  administrador, donde el alias de `winget.exe` no existe. Ver `WingetLocator`.
- **Un test double puede mentir.** `InMemoryFileTree` pisaba el atributo `ReparsePoint` del directorio
  padre, y el test estrella del junction estaba probando otra cosa mientras pasaba en verde.

---

## Objetivo: "dejar el equipo en estado estándar"

Meta declarada por el usuario: EasyFix deja el equipo **sin ningún error, como en modo estándar**. No
es una promesa al cliente — es el objetivo de diseño de la herramienta.

Traducirlo a algo construible exige partirlo en dos, porque las dos mitades tienen destinos distintos.

### Mitad 1 — Deriva de configuración: SÍ se puede devolver a estándar

Windows se ensucia acumulando cambios respecto de su estado de fábrica. Eso es reversible, y hay
mecanismos oficiales para casi todo:

| Qué se desvió | Cómo se vuelve a estándar |
|---|---|
| Archivos de sistema modificados o corruptos | `DISM /RestoreHealth` + `sfc /scannow` — restaura contra la imagen de referencia. Es *literalmente* volver a estándar. |
| Pila de red alterada | `netsh winsock reset` + `netsh int ip reset` + `ipconfig /flushdns` |
| Componentes de Windows Update roto | Detener servicios, renombrar `SoftwareDistribution` y `catroot2` |
| Planes de energía manoseados | `powercfg -restoredefaultschemes` |
| Tipos de inicio de servicios cambiados | Comparar contra la línea base de esa versión de Windows y restaurar los que difieran |
| Asociaciones de archivo secuestradas | Restaurar las predeterminadas por extensión |
| Store / Apps UWP rotas | `wsreset`, re-registro de paquetes |
| Firewall y Defender con políticas raras | Restaurar directivas predeterminadas |
| `hosts`, proxy y DNS secuestrados | Restaurar a contenido y valores por defecto |
| GPO local con basura (equipo sin dominio) | Reset de las directivas locales |
| Sistema de archivos con errores | `chkdsk` |

Esto es un modo nuevo, distinto de "Mejorar rendimiento": **Restablecer a estado estándar**. Mejorar
optimiza; restablecer *deshace la deriva*. Se pisan en algunas acciones pero la intención es otra, y
el riesgo también: restablecer toca más cosas.

### Mitad 2 — Daño real: NO se puede, y hay que decirlo

Ninguna cantidad de software arregla esto:

- **Disco fallando** (SMART, eventos WHEA, errores `disk`/`Ntfs`) → respaldar y reemplazar.
- **RAM defectuosa** → probar y reemplazar. Es la causa #1 de varios pantallazos azules.
- **Driver con bug** → se puede *revertir* a una versión anterior, no arreglar.
- **Hardware con fallas reportadas por firmware** (WHEA) → reemplazo.
- **Sobrecalentamiento** → limpieza física y pasta térmica.

Acá el trabajo de EasyFix no es reparar: es **identificar y nombrar** el problema con la evidencia
medida, para que el técnico no pierda tres horas reinstalando algo que iba a fallar igual.

### La opción nuclear: el propio reset de Windows

Cuando la deriva es demasiada, `systemreset` («Restablecer este PC conservando mis archivos») deja el
equipo en estado estándar de verdad, más rápido y con más garantías que cincuenta arreglos uno por uno.

**Una herramienta seria tiene que saber cuándo recomendarlo** en vez de insistir con parches. Criterios
candidatos: `DISM /RestoreHealth` falla o no puede reparar · más de N servicios fuera de su línea base ·
perfil corrupto sin arreglo de nivel 1 o 2 · pantallazos que persisten después de revertir drivers y
actualizaciones. Recomendarlo con honestidad es más valioso que un botón que promete lo imposible.

### Cómo se vuelve medible: la lista de verificación

«Sin ningún error» no significa nada si no es falsable. Se define como una **lista concreta de
condiciones, todas en verde**, que se vuelve a evaluar *después* de reparar y se muestra tal cual:

```
ESTADO DEL EQUIPO                                  antes    después
Integridad de archivos de sistema (DISM/SFC)         ✗         ✓
Sistema de archivos sin errores (chkdsk)             ✓         ✓
Salud del disco (SMART)                              ✓         ✓
Sin dispositivos con problema                        ✗         ✓
Un solo antivirus activo                             ✗         ✓
Servicios en su tipo de inicio estándar              ✗         ✓
Pila de red en valores predeterminados               ✓         ✓
Windows Update funcional                             ✗         ✓
Sin errores de disco en el registro de eventos       ✓         ✓
Sin pantallazos en los últimos 30 días               ✗         ✗   <- persiste
Espacio libre suficiente en C:                       ✗         ✓
Perfil de usuario sano                               ✓         ✓
```

Las que quedan en rojo se muestran **con el motivo**, y si el motivo es hardware, se dice. Un ítem que
no se pudo evaluar aparece como «no determinado», nunca como verde: una comprobación que falló no es
evidencia de que el equipo esté sano.

Esa tabla es el entregable real de la herramienta. Es lo que se le deja al cliente, y es lo que
convierte «te lo dejé sin errores» en algo verificable en vez de una frase de vendedor.

### Fases nuevas que esto agrega

| # | Entrega | Depende de |
|---|---|---|
| 19 | **Lista de verificación**: las ~12 condiciones como reglas puras sobre el snapshot, evaluadas antes y después | ampliar `SystemProbe` |
| 20 | Línea base de tipos de inicio de servicios por versión de Windows, y restauración de los que difieran | 12 (handlers de deshacer) |
| 21 | Modo **Restablecer a estado estándar**: red, Windows Update, energía, asociaciones, Store, políticas | 11, 12 |
| 22 | **Análisis de pantallazos azules**: bugcheck del registro de eventos, WHEA, volcados, y cruce con la fecha de las actualizaciones | ampliar `SystemProbe` |
| 23 | Recomendador del reset de Windows, con criterios explícitos | 19, 21 |

El prototipo del punto 22 ya existe como script suelto: `tools/Diagnose-BlueScreen.ps1`. Es solo
lectura y sirve para validar los contratos del Event Log antes de escribir el C#.

---

## Modelo honesto de rendimiento
Esto define qué promete la app. No se inventa un "+200%".

| Intervención | Ganancia real | Quién la hace |
|---|---|---|
| HDD → SSD | Boot 300–1000%. La mejora #1, sin competencia | Hardware (app lo **recomienda**) |
| RAM 4→8/16 GB en equipo que swapea | Elimina congelamientos | Hardware (app lo **recomienda**) |
| Quitar antivirus duplicado / PUP / bloatware | 20–60% en equipo infectado de basura | App |
| Desactivar startup pesado | 10–40% del tiempo de boot | App |
| Temporales + caché WU + WinSxS | Espacio en disco. Perf solo si `C:` estaba <10% libre | App |
| Plan de energía, TRIM, defrag correcto | 0–15% | App |
| Equipo ya sano | **0–5%. Se dice y ya.** | — |

La app reporta **milisegundos y GB medidos**, no porcentajes inventados. Métrica de boot real:
Event ID 100 de `Microsoft-Windows-Diagnostics-Performance/Operational` → campo `MainPathBootTime`.

---

## Entorno de desarrollo
> **Corrección (verificada por experimento).** Una versión anterior de este plan afirmaba que WPF no
> compila en macOS y que la VM era un blocker para todo. **Es falso.** Con
> `<EnableWindowsTargeting>true</EnableWindowsTargeting>` el SDK baja el reference pack de
> `Microsoft.WindowsDesktop.App` y compila el proyecto WPF, XAML incluido, desde macOS. Comprobado:
> los tres proyectos compilan y 119 tests pasan en esta Mac (Intel, macOS 12.7.6, .NET SDK 8.0.424
> instalado en `~/.dotnet`).
>
> Lo que la VM **sí** sigue habilitando, y no es poco: ejecutar el `.exe`, y probar todo lo que toca
> WMI, registro, servicios, puntos de restauración y el antes/después del arranque. Las fases de
> diagnóstico y de fixes la necesitan; el desarrollo de la lógica y de la UI, no.

Setup para probar los fixes (no para compilar):
1. **VMware Fusion Pro** + **Windows 11 x64** VM.
   ⚠️ *Fusion **Player** ya no existe* — Broadcom lo discontinuó en mayo 2024 y liberó **Fusion Pro**
   gratis (personal desde mayo 2024, también comercial desde noviembre 2024). Descarga vía portal de
   Broadcom, requiere cuenta. Alternativa sin cuenta: VirtualBox 7.
   **A verificar en el paso 0:** que la versión actual de Fusion siga soportando macOS 12.7.6 — las
   releases recientes suben el mínimo de macOS. Si no, usar VirtualBox 7 o una Fusion 13.x anterior.
2. Dentro de la VM: .NET 8 SDK + VS 2022 Community (o VS Code + `dotnet` CLI).
3. **Segunda VM "conejillo"** — Windows sucio a propósito (muchos startup, temporales, bloatware)
   con snapshots. Es el único lugar seguro para probar los fixes destructivos.
4. Código en carpeta compartida Mac↔VM, git en la Mac. `git init` — el directorio hoy no es repo.

Sin las VMs el proyecto no se puede verificar. Es el paso 0.

---

## Estructura
```
EasyFix/
├─ EasyFix.sln
├─ appsettings.json            # umbrales, listas curadas, paquetes de winget
├─ src/
│  ├─ EasyFix.App/             # WPF. Ventana, XAML, ViewModels. Cero lógica de negocio.
│  │  ├─ App.xaml(.cs)         # DI, Serilog, carga de configuración, handler global de excepciones
│  │  ├─ Views/MainWindow.xaml # las cuatro pantallas, por Visibility según CurrentScreen
│  │  ├─ ViewModels/           # MainViewModel, AppChoice, Converters
│  │  ├─ Styles/Theme.xaml     # tokens de color y estilos
│  │  └─ app.manifest          # requireAdministrator + PerMonitorV2
│  └─ EasyFix.Core/            # Toda la lógica. Sin referencia a WPF (test de arquitectura).
│     ├─ Abstractions/         # IFileTree, IProcessRunner
│     ├─ Apps/                 # WingetService, WingetLocator, WingetResultParser, WingetPackageId
│     ├─ Classification/       # StartupClassifier, StartupCandidate, CertificateSubject
│     ├─ Cleaning/             # JunctionSafeCleaner, PhysicalFileTree
│     ├─ Configuration/        # EasyFixOptions, OptionsLoader
│     ├─ Diagnostics/          # SystemProbe, SystemSnapshot, SoftwareFindings, DiagnosticEngine
│     ├─ Fixes/                # ServiceConditionEvaluator
│     ├─ Processes/            # SafeProcessRunner
│     ├─ Recommendations/      # HardwareAdvisor
│     ├─ Rollback/             # RunJournal, JournalReader, UndoEngine, FileJournalSink
│     └─ Safety/               # PathGuard
├─ tests/EasyFix.Core.Tests/   # xUnit + fakes propios
│  └─ Fakes/                   # InMemoryFileTree, TestDoubles, WindowsOnlyFact, TestPaths
├─ tools/spike/                # Verify-WindowsApis.ps1
└─ scripts/bootstrap.ps1       # recrea el .sln y baja autorunsc
```

**Dependencias NuGet** — superficie deliberadamente chica, solo APIs muy conocidas:

| Proyecto | Paquetes |
|---|---|
| `Core` | `System.Management`, `System.Diagnostics.EventLog`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` |
| `App` | `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging`, `Serilog` + `Sinks.File` + `Extensions.Logging` |
| Tests | `xunit`, `NSubstitute` |

**Desviaciones conscientes del plan original**, ambas para reducir superficie de API sin verificar:
la configuración se lee con `System.Text.Json` en vez de `Microsoft.Extensions.Configuration`, y el
acceso a archivos usa `IFileTree` —un puerto propio de 9 métodos, **sin borrado recursivo**— en vez de
`System.IO.Abstractions`. Lo segundo terminó siendo mejor que el plan: la operación peligrosa no
existe en el puerto, así que nadie la puede invocar por descuido.

### Config, no hardcode

Umbrales y listas curadas viven en `appsettings.json`, embebido en el `.exe` como recurso. Al arrancar
se busca primero un `appsettings.json` **junto al ejecutable** —para poder ajustar algo en el equipo
del cliente sin recompilar— y si no está, se usa el embebido.

```jsonc
{
  "Thresholds": { "LowDiskFreePercent": 15, "MinRamGb": 8, "TempFileMinAgeMinutes": 60,
                  "CommitPressurePercent": 85, "QuickScanBudgetSeconds": 10,
                  "PerCheckTimeoutSeconds": 5, "MaxConcurrentChecks": 8, ... },
  "Classifier": { "Layer1_HardBlock": { ... }, "Layer2_AutoDisableAllowlist": [ ... ] },
  "Bloatware": { "Candidates": [ ... ] },
  "Services": { "OfferToDisable": [ ... ] },
  "WingetPackages": [ ... ]
}
```

## Abstracciones centrales
### La decisión de arquitectura más importante

**`SystemProbe` es la única clase que habla con Windows.** Lee WMI, el registro y el Event Log, y
devuelve un `SystemSnapshot`. Todo lo demás —reglas, recomendaciones, decisiones— es lógica pura
sobre ese snapshot.

Esto no estaba en el plan original y salió de una restricción real: los contratos de Windows no se
pueden verificar sin Windows. Concentrarlos da dos cosas:

1. Cuando aparezca un contrato equivocado, hay **un solo archivo** donde corregirlo.
2. Todo el resto del diagnóstico se **testea desde cualquier sistema**, y agregar una regla nueva no
   requiere una VM.

```csharp
// La capa que toca Windows. Cada sonda falla sola: un null significa "no se pudo medir",
// jamás "cero", y suma una entrada a Failures que el reporte muestra como "no determinado".
[SupportedOSPlatform("windows")]
public sealed class SystemProbe
{
    Task<ProbeResult> ProbeAsync(IProgress<string>?, CancellationToken);
}

public sealed record ProbeResult(
    SystemSnapshot Snapshot,
    MachineIdentity Identity,
    IReadOnlyList<CheckFailure> Failures);

// Todo opcional a propósito. null = no medido.
public sealed record SystemSnapshot
{
    DiskMedia PrimaryDiskMedia { get; init; }     // Unknown | Hdd | Ssd | Scm
    DiskHealth PrimaryDiskHealth { get; init; }   // Unknown | Healthy | Warning | Failing
    double? TotalRamGb { get; init; }
    int? FreeMemorySlots { get; init; }
    string? MemoryType { get; init; }             // "DDR4"
    double? CommitUsedPercent { get; init; }
    int? MainPathBootTimeMs { get; init; }
    bool IsDomainJoined { get; init; }
    bool BitLockerActive { get; init; }
    // ... y el resto
}
```

Las reglas puras encima del snapshot:

```csharp
// Hallazgos de software: lo que se arregla sin comprar nada.
public static class SoftwareFindings
{
    static IReadOnlyList<Finding> Evaluate(SystemSnapshot, ThresholdOptions);
}

// Recomendaciones. NeedsHardware separa las dos secciones del reporte.
public sealed record Recommendation(
    string Id, RecommendationPriority Priority, string Title, string Detail,
    Metric? Evidence = null, bool BlocksFixes = false, bool NeedsHardware = false);
```

### Reversibilidad

```csharp
// Journal append-only en formato JSON Lines. Una escritura truncada por un crash pierde UNA
// línea, no el archivo: con un objeto JSON único se perdería la posibilidad de deshacer.
public sealed class RunJournal
{
    static RunJournal Start(JournalHeader, IJournalSink, TimeProvider?);
    void Append(JournalAction);   // <-- ANTES de tocar el sistema, nunca después
    void SetState(RunState, IReadOnlyList<string>? pendingFixIds);
}

public interface IUndoHandler   // <-- FALTAN LOS CONCRETOS
{
    UndoKind Kind { get; }
    Task<bool> UndoAsync(UndoStep step, CancellationToken ct);
}
```

Regla dura: **escribir en el journal antes de actuar.** Si la app muere entre el cambio y el
registro, queda un cambio invisible para el undo — exactamente el caso que arruina el equipo de un
cliente.

### `IFix`: todavía no existe

El plan definía una interfaz `IFix` con `CanApplyAsync` / `ApplyAsync`. No se implementó porque los
fixes están bloqueados por `RestorePointService`. Cuando se escriba, el contrato sigue siendo válido,
y `ApplyAsync` recibe el `RunJournal` para poder cumplir la regla de arriba.

## Catálogo de diagnóstico (la investigación)
Todo con API real, no shell-out cuando se puede evitar.

### Dos velocidades — obligatorio separarlas

Un solo "Analizar" que corre todo tardaría 15+ minutos y parecería colgado. Se parte en dos:

**Escaneo rápido — objetivo <10 s, es el que corre al abrir la app.** Solo WMI, registro y Event Log.
Los ~30 chequeos corren **en paralelo** (`Task.WhenAll` con `SemaphoreSlim` limitado a ~8 — cada query
WMI cuesta 100–500 ms, en serie son 15 s de UI congelada). Cada chequeo con timeout individual de 5 s:
un chequeo colgado no puede bloquear el reporte, se marca "no determinado" y se sigue.

**Escaneo profundo — minutos, opt-in, vive detrás del botón "Reparar errores".** `DISM /ScanHealth`
(5–15 min), `chkdsk /scan`, `DISM /AnalyzeComponentStore`, `sfc /verifyonly`. Con barra de progreso y
cancelable. **Nunca** en la ruta rápida.

Todo lo que sigue es del escaneo rápido salvo lo marcado 🐌 (profundo).

### Hardware
| Chequeo | Fuente | Umbral / acción |
|---|---|---|
| Tipo de disco | `MSFT_PhysicalDisk.MediaType` (3=HDD, 4=SSD) + `BusType` (17=NVMe) | HDD → recomendación #1 |
| Salud del disco | `MSFT_PhysicalDisk.HealthStatus` + `MSStorageDriver_FailurePredictStatus` | Failing → **detener todo, respaldar** |
| RAM total y slots | `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray.MemoryDevices` | <8 GB → recomendar, con tipo y MHz exactos |
| Presión de memoria | Contadores `\Memory\Committed Bytes` vs `Commit Limit`, `\Paging File(_Total)\% Usage` | Commit >85% → RAM insuficiente confirmada |
| CPU y throttling | `Win32_Processor`, contador `\Processor Information(_Total)\% of Maximum Frequency` | <70% sostenido → throttle térmico |
| Latencia de disco | `\PhysicalDisk(_Total)\Avg. Disk sec/Transfer` | >25 ms → disco es el cuello de botella |
| Espacio libre `C:` | `Win32_LogicalDisk` | <15% → problema de perf y de updates |
| Desgaste de batería | `powercfg /batteryreport` (XML) | >30% → throttling en batería |
| Drivers con problema | `Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0` | Reportar, **no instalar drivers** |

### Arranque
| Chequeo | Fuente |
|---|---|
| Entradas de inicio — **5 ubicaciones** | HKCU/HKLM `...\CurrentVersion\Run` (+`Wow6432Node`), carpetas `shell:startup` y `shell:common startup`, Task Scheduler con trigger de logon, servicios `Automatic` no-Microsoft |
| Estado habilitado/deshabilitado | `HKCU\...\Explorer\StartupApproved\{Run,Run32,StartupFolder}` — byte[12] es el flag |
| **Costo real por app en ms** | Event Log `Diagnostics-Performance/Operational` IDs 101/103. Es de donde el Task Manager saca "Impacto de inicio". Dato duro para el reporte. |
| Tiempo de boot | Mismo log, Event 100 → `MainPathBootTime`, `BootPostBootTime` |
| Fast Startup | `HKLM\SYSTEM\CCS\Control\Session Manager\Power\HiberbootEnabled` |

### Software y bloat
- Programas instalados: leer claves `Uninstall` (HKLM + Wow6432Node + HKCU).
  **Nunca `Win32_Product`** — dispara reconfiguración MSI de cada paquete, tarda minutos y puede romper instalaciones.
- Antivirus duplicados: namespace `root\SecurityCenter2`, clase `AntiVirusProduct`. Dos AV = lentitud brutal.
- Estado de Defender: `Get-MpComputerStatus` (real-time apagado, firmas viejas, último scan).
- Bloatware/PUP identificado por `(publisher, producto)` — no por glob de nombre. Ver el clasificador
  de 3 capas. Siempre Capa 3: se lista con su tamaño, nunca se desinstala en automático.

### Salud del sistema
- 🐌 Integridad de archivos: `DISM /Online /Cleanup-Image /ScanHealth` (read-only) — solo si encuentra
  daño se corre `/RestoreHealth` (lento).
- 🐌 Sistema de archivos: `chkdsk C: /scan` (online, read-only, sin reboot).
  Rápido en cambio: `fsutil dirty query C:` (instantáneo).
- **Estado de BitLocker** — `Win32_EncryptableVolume` (namespace `root\CIMV2\Security\MicrosoftVolumeEncryption`),
  campos `ProtectionStatus` y `ConversionStatus`. Crítico: condiciona qué fixes se permiten (ver Seguridad).
- **Equipo unido a dominio** — `Win32_ComputerSystem.PartOfDomain`. Condiciona el modo de la app.
- Event Log crítico: `disk` 7/11/51/153, `Ntfs` 55 (errores de disco), `Kernel-Power` 41 (apagones),
  `Application Error` 1000, y dumps en `C:\Windows\Minidump`.
- Windows Update: reboot pendiente, updates fallando en loop, estado de `wuauserv`.
- Espacio recuperable: 🐌 `DISM /AnalyzeComponentStore` (WinSxS), `Windows.old` + antigüedad,
  `hiberfil.sys` (~40% de la RAM), caché de Delivery Optimization,
  `vssadmin list shadowstorage` (puntos de restauración), temporales.
- Plan de energía: `powercfg /getactivescheme`.
- TRIM: `fsutil behavior query DisableDeleteNotify` (0 = activo).
- Defrag programado sobre SSD (tarea `Optimize-Volume`) → desgasta celdas.
- Fragmentación: `Optimize-Volume -Analyze` — **solo si es HDD**.
- Heurística de malware: autoruns sin firma digital, `Image File Execution Options` con debuggers,
  `hosts` alterado, proxy/DNS secuestrado (`HKCU\...\Internet Settings\ProxyServer`).
- Salud de perfil: `HKLM\...\ProfileList` con sufijo `.bak`, `State` con bit de perfil temporal,
  `ProfileImagePath` inconsistente, carpetas shell rotas.

---

## Catálogo de fixes
### Tier A — automático, con el click de "Mejorar rendimiento"

0. **Punto de restauración** (`SystemRestore.CreateRestorePoint`, namespace `root\default`).
   Ojo con dos trampas: System Restore puede estar deshabilitado (`Enable-ComputerRestore`) y hay un
   throttle de 24 h (`SystemRestorePointCreationFrequency`). Verificar que el punto **existe** antes
   de continuar; si no se pudo crear, **abortar** y decírselo al usuario.
1. Temporales: `%TEMP%`, `C:\Windows\Temp`. Saltar archivos en uso y los de <60 min (puede haber un
   instalador corriendo).
2. Caché de Windows Update: detener `wuauserv`+`bits` → limpiar `SoftwareDistribution\Download` → reiniciar servicios.
3. Caché de Delivery Optimization.
4. Caché de miniaturas e iconos (se reconstruye).
5. `DISM /Online /Cleanup-Image /StartComponentCleanup` — **sin `/ResetBase`**, que impediría desinstalar updates.
6. `Windows.old` si tiene >10 días (fuera de la ventana de rollback de Windows).
7. Plan de energía → Alto rendimiento en desktop. En laptop, ofrecer, no imponer (afecta batería).
8. SSD: activar TRIM, desactivar defrag programado, `Optimize-Volume -ReTrim`.
9. HDD: programar `Optimize-Volume -Defrag` (en background, no bloquea la UI).
10. `ipconfig /flushdns`.
11. Desactivar startup de la **lista blanca curada** vía flag `StartupApproved` — exactamente el
    mecanismo del Task Manager, 100% reversible. Nada de borrar claves.

### Tier B — checkbox, requiere tu OK
- **Servicios**, cada uno con "qué pierdes" en lenguaje simple: `DiagTrack` (telemetría),
  `WSearch` (búsqueda), `Fax`, `RemoteRegistry`, `Spooler` (si no hay impresora), `MapsBroker`,
  `RetailDemo`, `bthserv` (si no hay Bluetooth).
  ⚠️ `SysMain`/Superfetch: en HDD **ayuda**, no lo toques. Solo ofrecerlo si es SSD + ≥8 GB RAM.
- Desinstalar bloatware/PUP detectado (lista con tamaños, tú eliges).
- Quitar apps UWP basura (lista curada).
- Startup "zona gris" — nunca automático: OneDrive, componentes de antivirus, paneles de GPU,
  VPN, clientes de sincronización.
- Efectos visuales → modo rendimiento. Transparencias y animaciones off.
- `powercfg /h off` (libera GB, pero mata Fast Startup — se avisa).
- Redimensionar/mover page file.
- Limitar espacio de puntos de restauración (`vssadmin resize shadowstorage`).
- Vaciar papelera de reciclaje (**es data del usuario** — jamás automático).
- Reset de red: `netsh winsock reset` + `netsh int ip reset` (requiere reboot).

### Tier C — NO construir. Mitos y cosas dañinas.
Documentado en el README para que no se cuele después:
borrar Prefetch (empeora el boot), limpiadores de registro (cero ganancia, riesgo real),
"optimizadores de RAM" (recortar working sets hace todo más lento), defragmentar SSD,
desactivar el page file, `/ResetBase`, listas de servicios copiadas de foros (rompen Update, Store,
audio, impresión), desactivar Defender, "tweaks gamer" de timer resolution,
borrar `WinSxS` a mano, instalar drivers automáticamente (driver equivocado = equipo muerto).

---

## Botón "Reparar errores"
Se mantiene separado de "Mejorar" por una razón práctica: esto tarda 20–60 min, lo otro tarda 2.
Secuencia, con progreso y cancelable:

1. `DISM /ScanHealth` → si hay daño: `DISM /RestoreHealth` → `sfc /scannow`.
2. `chkdsk C: /scan` (online). Si reporta errores → ofrecer `chkdsk /f` programado en reboot.
3. Reset completo de Windows Update: detener servicios, renombrar `SoftwareDistribution` y
   `catroot2`, reiniciar.
4. Reset de red (winsock, ip, DNS, firewall a default si corresponde).
5. Reconstruir índice de búsqueda si está corrupto.
6. Reparar asociaciones de archivo y componentes de Store roto (`wsreset`).
7. Diagnóstico de perfil → deriva al wizard.

### Wizard de perfil (lo que mencionaste)
**No es un click.** Es guiado, con confirmación en cada paso:
- **Nivel 1** — perfil temporal por entrada `.bak` en `ProfileList`: corregir el swap de claves y
  reparar permisos. Resuelve la mayoría de casos sin tocar datos.
- **Nivel 2** — carpetas shell rotas: reconstruir `User Shell Folders`.
- **Nivel 3, último recurso** — crear perfil nuevo y migrar:
  `robocopy` selectivo de Desktop, Documents, Downloads, Pictures, Videos, Music, Favorites y
  `AppData\Roaming` filtrado. **Nunca** copiar `NTUSER.DAT` (es la corrupción misma) ni
  `AppData\Local\Temp`. Avisar explícito: passwords guardados y sesiones de navegador no se
  transfieren. Requiere confirmación manual y un checklist en pantalla.

---

## Botón "Instalar programas"
**Estado: implementado y testeado** (`Core/Apps/WingetService.cs`, 17 tests).

**Todo se descarga en el momento.** winget baja cada paquete del repositorio oficial de Microsoft en
el instante de instalar, así que siempre entra la última versión publicada. Nada viene empaquetado en
el `.exe`: no hay instaladores que envejezcan ni que haya que actualizar a mano. El equipo del cliente
necesita internet.

Argumentos exactos, y por qué cada uno:

```
install --id <ID> --exact --source winget --silent --disable-interactivity
        --accept-package-agreements --accept-source-agreements
```

- `--exact` — sin él, un ID parcial puede traer otro paquete.
- `--source winget` — solo el repositorio oficial, no fuentes que el usuario haya agregado.
- `--disable-interactivity` — ningún instalador puede quedarse esperando un Enter.
- **En serie, nunca en paralelo** — dos instaladores de Windows se pelean por el mutex de Windows
  Installer: uno falla, o peor, queda a medias. Verificado por test (`MaxConcurrent == 1`).

| Programa | ID | Por defecto |
|---|---|:-:|
| Google Chrome | `Google.Chrome` | ✓ |
| Adobe Acrobat Reader | `Adobe.Acrobat.Reader.64-bit` | ✓ |
| 7-Zip | `7zip.7zip` | ✓ |
| Visual C++ Redist x64 / x86 | `Microsoft.VCRedist.2015+.x64` / `.x86` | ✓ |
| RustDesk | `RustDesk.RustDesk` | ✓ |
| VLC · Notepad++ · Firefox · AnyDesk | | |

7-Zip en lugar de WinRAR porque WinRAR es shareware con nag eterno. RustDesk en lugar de AnyDesk como
opción por defecto porque es libre y se puede autohospedar; AnyDesk queda desmarcado por si el cliente
ya lo usa. Los Visual C++ Redistributables merecen estar en la lista: arreglan la mayoría de los
«falta tal DLL» después de reinstalar Windows.

### Encontrar `winget.exe` en un proceso elevado

Es el detalle no obvio de todo el módulo. winget se instala como paquete MSIX **por usuario** y se
invoca por un alias en `%LOCALAPPDATA%\Microsoft\WindowsApps`. EasyFix corre elevado, y al elevar esa
variable puede resolver al perfil del administrador, donde el alias **no existe**. Buscar solo ahí
falla justo en el escenario real: equipo de cliente, cuenta de usuario normal, app elevada por UAC.

`WingetLocator` busca en este orden:

1. `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe` — la instalación real
   del paquete. Es global y la carpeta, aunque tiene ACL restrictiva, la puede leer un administrador.
   Si hay varias versiones, gana la más nueva.
2. El alias por usuario, como respaldo.

Nunca por `PATH`: un `winget.exe` puesto en el directorio actual secuestraría la instalación, y el
equipo que estamos reparando puede estar comprometido.

### Interpretación del resultado

`WingetResultParser` distingue *instalado*, *ya estaba instalado*, *no encontrado* y *sin instalador
compatible*. **«Ya estaba instalado» no es un fallo** y el resumen lo separa: decirle al técnico que
instaló siete programas cuando instaló cuatro es mentirle sobre lo que hizo.

Los códigos de salida salen de la documentación y están **pendientes de verificar**. El diseño hace
que eso no sea grave: un código no reconocido cae en `Failed` mostrando el valor en crudo, así que el
peor caso es un fallo honesto y diagnosticable, nunca un éxito falso.

### Pendiente

Si winget falta (Win10 anterior a 1809), hoy se informa y nada más. Falta el bootstrap del
`Microsoft.DesktopAppInstaller` msixbundle.

## Seguridad y reversibilidad
**Journal por corrida** en `%ProgramData%\EasyFix\runs\<runId>.json`:

```jsonc
{
  "runId": "2026-08-24T20-14-33",
  "restorePointSequence": 42,
  "actions": [
    { "fixId": "startup.disable", "target": "HKCU\\...\\StartupApproved\\Run\\Spotify",
      "undo": { "type": "registryBinary", "path": "...", "value": "020000000000000000000000" } },
    { "fixId": "power.plan", "undo": { "type": "powercfg", "scheme": "381b4222-f694-41f0-9685-ff5bb260df2e" } },
    { "fixId": "temp.clean", "reversible": false, "freedBytes": 4509715660 }
  ]
}
```

Reglas no negociables:
1. Punto de restauración **verificado** antes del primer cambio. Si no se crea → abortar.
2. Escribir en el journal **antes** de aplicar cada cambio.
3. La Capa 1 de bloqueo (seguridad, Microsoft, dependencias, drivers) se evalúa primero y gana sobre
   cualquier otra lista. Desconocido = Capa 3 = pedir permiso.
4. Borrado de archivos se marca `reversible: false` y se dice en el reporte. No se miente sobre el undo.
5. Cero acción sobre datos del usuario: nada de Documents, Downloads, Desktop ni papelera sin OK explícito.
6. Si SMART reporta disco fallando → **modo respaldo**: se bloquean los fixes y se muestra solo la
   advertencia. Optimizar un disco muriendo es la peor jugada posible.
7. Logging estructurado con Serilog a `%ProgramData%\EasyFix\logs\` — niveles info/warn/error.

### BitLocker — el peor escenario posible

`chkdsk /f`, `netsh int ip reset` y cambios de configuración de arranque en un volumen con BitLocker
activo pueden alterar la medición de integridad del TPM. Resultado: al reiniciar, Windows pide la
**clave de recuperación de 48 dígitos**. Si el cliente no la tiene, quedó fuera de su propio equipo —
y tú tampoco puedes entrar. Es el daño irreversible más grave que esta app podría causar.

Protocolo obligatorio:
1. Consultar `Win32_EncryptableVolume.ProtectionStatus` en `C:` **antes de cualquier fix**.
2. Si BitLocker está activo, la app exige confirmación explícita: *"Este equipo tiene BitLocker.
   ¿Tienes a mano la clave de recuperación?"* — con el botón de continuar deshabilitado hasta marcar
   la casilla. Mostrar dónde buscarla (cuenta Microsoft, Azure AD, impresa, archivo `.BEK`).
3. Los fixes que tocan arranque o sistema de archivos se ejecutan solo tras
   `Suspend-BitLocker -MountPoint C: -RebootCount 1`, y se registra en el journal.
4. Sin confirmación → esos fixes quedan bloqueados con el motivo visible. El resto (temporales, plan
   de energía, startup) corre normal: no toca la medición del TPM.

### Reboot en medio del flujo

SFC, DISM `/RestoreHealth`, `chkdsk /f` y los resets de red requieren reinicio. El journal ya está en
disco, pero falta el flujo de reanudación:
- El journal lleva `pendingActions[]` y `state: "awaiting-reboot"`.
- La app se auto-registra en `HKCU\...\RunOnce` (entrada de un solo uso, se borra al ejecutarse) con
  `--resume <runId>`.
- Al arrancar, si detecta un journal `awaiting-reboot`, ofrece continuar o abandonar. Abandonar deja
  el journal intacto para que el undo siga funcionando.
- **Nunca** reiniciar sin avisar. Botón explícito "Reiniciar ahora / Reiniciar después".

### Equipos de dominio

`Win32_ComputerSystem.PartOfDomain == true` → **modo restringido**, avisado en pantalla:
las GPO revierten muchos cambios en el siguiente `gpupdate` (el fix parece funcionar y luego se
deshace solo), y desactivar agentes de gestión o VPN corporativa rompe el equipo para TI. En modo
restringido: solo temporales, cachés y diagnóstico. Servicios y startup quedan bloqueados.

### Invocación de procesos externos — argument injection

La app llama a `powercfg`, `DISM`, `chkdsk`, `netsh`, `winget`, `robocopy`, `autorunsc` y `fsutil` con
argumentos que provienen del registro, de rutas de perfil y de `appsettings.json`. Reglas:
- **`ProcessStartInfo.ArgumentList`**, nunca la propiedad `Arguments` con strings concatenados.
  `ArgumentList` hace el escaping por argumento y elimina la clase entera de bugs de quoting.
- **`UseShellExecute = false`** siempre. Nada pasa por `cmd.exe`.
- Ruta absoluta al ejecutable (`%SystemRoot%\System32\...`), nunca resolución por `PATH` —
  evita secuestro por un binario homónimo en el directorio actual.
- IDs de winget validados contra `^[A-Za-z0-9][A-Za-z0-9._+-]*$` antes de pasarlos.
- Toda ruta destinada a borrado o `robocopy` se canonicaliza (`Path.GetFullPath`) y se verifica que
  esté **bajo la raíz esperada** antes de actuar. Fuera de la raíz → excepción, no advertencia.
- Timeout y cancelación en cada proceso hijo; capturar stdout/stderr, parsear exit code.
  Nunca asumir éxito porque el proceso terminó.

### Borrado de temporales — junctions y symlinks

`Directory.Delete(path, recursive: true)` puede seguir junctions y reparse points, y `%TEMP%` es un
lugar donde cualquier instalador los deja. Un junction hacia `C:\Users\...\Documents` dentro de
`%TEMP%` convertiría la limpieza en borrado de datos del cliente. Implementación:
- Enumeración manual, comprobando `FileAttributes.ReparsePoint` en cada directorio.
- Un reparse point se **borra como enlace** (`Directory.Delete` no recursivo) o se salta. Jamás se entra.
- Nunca seguir a un objetivo fuera de la raíz canonicalizada.
- Saltar archivos en uso (`IOException`) y continuar; contarlos en el reporte.

---

## UI minimalista
**Estado: implementado** (`App/Views/MainWindow.xaml`). Cuatro paneles en la misma ventana, que se
muestran u ocultan según `CurrentScreen` — sin navegación, sin ventanas nuevas.

Ventana de 460 × 620, no redimensionable, chrome propio, tema oscuro fijo, un solo color de acento
(`#4C9AFF`), `Segoe UI Variable`. Cero gradientes, cero iconos decorativos. Todo en español,
redactado para que el cliente lo entienda.

```
Inicio                                   Reporte
┌──────────────────────────────────┐    ┌──────────────────────────────────┐
│  EasyFix                  — ✕    │    │  EasyFix                  — ✕    │
│                                  │    │  Windows 10 · HP 240 · i5-8250U  │
│  Windows 10 · HP 240 · i5-8250U  │    │  8 GB · Disco mecánico 500 GB    │
│  8 GB · Disco mecánico 500 GB    │    │                                  │
│                                  │    │  LO QUE PUEDO ARREGLAR           │
│  ┌────────────────────────────┐  │    │  ▍14 programas en el inicio      │
│  │ Analizar el equipo         │  │    │    Retrasan 31,4 s               │
│  │ Solo lectura. No modifica  │  │    │  ▍Dos antivirus activos          │
│  └────────────────────────────┘  │    │                                  │
│  ┌────────────────────────────┐  │    │  LO QUE NECESITA HARDWARE        │
│  │ Reparar errores            │  │    │  ▍Cambiar el disco por un SSD    │
│  └────────────────────────────┘  │    │    Arranque actual 94,0 s        │
│  ┌────────────────────────────┐  │    │  ▍Ampliar la RAM a 16 GB         │
│  │ Instalar programas         │  │    │                                  │
│  └────────────────────────────┘  │    │  NO SE PUDO DETERMINAR           │
│                                  │    │                                  │
└──────────────────────────────────┘    │  [ Volver ] [ Mejorar rendim. ]  │
                                        └──────────────────────────────────┘
```

Las otras dos: **Analizando** (barra indeterminada + la sonda en curso + Cancelar) e **Instalar
programas** (lista de casillas con estado por programa: *Instalado* / *Ya estaba* / *Falló*).

Detalles que importan:

- Cada fila del reporte lleva una **franja de color** a la izquierda según gravedad, para que lo grave
  se lea de un vistazo sin leer el texto.
- Los números medidos van en monoespaciada y en el color de acento: son la evidencia, no decoración.
- **«NO SE PUDO DETERMINAR» se muestra siempre que haya algo.** Una sonda que falló no es «todo bien».
- El botón «Mejorar rendimiento» se **deshabilita** cuando SMART reporta falla.
- Los botones sin implementar dicen que no están implementados. Nunca una barra de progreso que se
  mueve sin hacer nada: en el equipo de un cliente, eso hace creer que se aplicó algo.
- Las maquetas navegables de las siete pantallas, con los colores reales, están publicadas como
  artifact aparte.

---

## Motor de recomendación de hardware
**Estado: implementado y testeado** (`Core/Recommendations/HardwareAdvisor.cs`, 26 tests).

Es la parte honesta de la app. Cada recomendación sale de una métrica, con el número al lado. Dos
reglas de diseño que los tests protegen:

- **Equipo sano → lista vacía.** Cuando no hay nada que vender, la respuesta correcta es no vender nada.
- **Dato en `null` → no se recomienda nada sobre él.** Un `null` significa «no medido», nunca «cero».
  Inventar una recomendación sobre un dato ausente hace que el cliente gaste plata al aire.

Además, la estimación del SSD tiene **piso de 15 s**: una regla de tres sobre un arranque ya rápido
daría «2 s», y un número absurdo quema la credibilidad del reporte entero.

- HDD detectado → *"Disco mecánico. Hoy tarda 94 s en arrancar; con un SSD SATA quedaría cerca de
  23,5 s. Es la mejora #1 y ninguna optimización de software se le acerca. Buscá uno de 480 GB."*
- RAM <8 GB y commit >85% → *"8 GB insuficientes: el equipo está usando disco como memoria. Subir a
  16 GB DDR4-2666. Tienes 1 slot libre."* (tipo y velocidad salen de `Win32_PhysicalMemory`).
- SMART failing → *"🔴 El disco está fallando. Respalda hoy. No tiene sentido optimizar."*
- CPU de generación vieja + todo lo demás sano → *"El equipo está al límite de su arquitectura."*
- Batería >30% desgastada → *"Batería degradada: el CPU se limita al andar sin cargador."*

---

## Fases de implementación
### Hecho

| # | Entrega | Cómo se verificó |
|---|---|---|
| 0 | Repo, `.sln`, tres proyectos, .NET 8 SDK en `~/.dotnet` | `dotnet build` limpio |
| 1 | Scaffold: WPF shell, manifest admin, Serilog, DI, tema | Compila; test de arquitectura verifica que `Core` no referencia WPF |
| 2 | Journal + `UndoEngine` | Journal completo, truncado por crash, acción no reversible, handler que revienta, orden inverso |
| 3 | Clasificador de 3 capas | Malware llamado `MyVPN` → Capa 3; «Microsoft Windows» sin firma válida no otorga protección; `System32Evil` no matchea `System32` |
| 4 | Limpiador junction-safe | Symlink **real** en el filesystem: el enlace se borra, el archivo del destino sobrevive con su contenido |
| 5 | Motor de diagnóstico | Chequeo colgado → timeout y el reporte sale; chequeo que revienta → el resto sigue; orden estable; tope de concurrencia respetado |
| 6 | Motor de recomendación | Equipo sano → lista vacía; dato en `null` → no recomienda; piso de 15 s en la estimación del SSD |
| 7 | Condiciones de servicio | `SysMain` en HDD no se ofrece; condición desconocida no se ofrece; toda condición del `appsettings.json` real está implementada |
| 8 | `SystemProbe` + UI de diagnóstico | **Sin verificar** — compila y publica |
| 9 | Módulo winget + RustDesk | Argumentos exactos, serialización real, un fallo no corta la tanda, ID con `&&` rechazado |

### Pendiente, en orden de dependencia

| # | Entrega | Bloqueado por | Tests que la cierran |
|---|---|---|---|
| 10 | **Correr el spike en Windows** y corregir los contratos que estén mal | acceso a un PC con Windows | Las dos salidas (Win10 y Win11) coinciden con lo documentado |
| 11 | `RestorePointService` | 10 | Sin System Restore habilitado → **aborta**; punto creado y verificado antes del primer cambio; throttle de 24 h contemplado |
| 12 | Los `IUndoHandler` concretos | 11 | Cada uno: se aplica, se deshace, el estado original vuelve exacto |
| 13 | Fixes Tier A + «Mejorar rendimiento» de verdad | 11, 12 | Ciclo de snapshot en VM sucia. Junction en `%TEMP%` no se sigue. Ruta fuera de la raíz → excepción |
| 14 | Medición antes/después | 13 | Baseline en el journal; tras reiniciar, el delta contra el evento 100 coincide con el Visor de eventos |
| 15 | Lista Tier B con casillas | 12 | Servicio desactivado y restaurado con su tipo de arranque exacto; modo dominio → bloqueado |
| 16 | Escaneo profundo + «Reparar errores» + reanudación tras reboot | 11 | Corrupción inducida y reparada; ciclo `RunOnce --resume`; cancelar a mitad de DISM deja el sistema consistente |
| 17 | Wizard de perfil | 16 | Perfil temporal inducido: nivel 1 lo resuelve. `robocopy` **no** copia `NTUSER.DAT` ni `AppData\Local\Temp` |
| 18 | Bootstrap de winget si falta | — | Win10 sin winget: se instala el App Installer y después el paquete |

**El orden importa por una razón:** nada que modifique el sistema se implementa antes de
`RestorePointService`. Sin punto de restauración verificado la app no toca nada, así que un fix sin
esa pieza no se podría ni ejecutar.

## Verificación
### Lo que corre hoy, en cualquier sistema

```bash
dotnet test                                        # 210 pasan, 2 se omiten fuera de Windows
dotnet test --filter 'Category!=RequiresWindows'   # solo lo portable
dotnet test --filter 'Category!=Integration'       # solo lógica pura, sin tocar el disco
```

Los tests marcados `[WindowsOnlyFact]` se **omiten** en vez de fallar fuera de Windows: un rojo
permanente entrena a ignorar los rojos.

**Fakes propios** en `tests/Fakes/`: `InMemoryFileTree` (con reparse points, archivos bloqueados y
directorios ilegibles), `FixedTimeProvider`, `ListJournalSink` (que puede truncar la última línea
para simular un crash), `RecordingUndoHandler`.

**Tests de integración contra el filesystem real** (`Category=Integration`), que corren en macOS y en
Windows: crean un **symlink de verdad** —.NET marca los symlinks con `ReparsePoint` también en Unix— y
verifican que el cleaner no lo sigue. Un fake que coincide con una implementación equivocada da
confianza falsa; esto es lo que lo descarta sin una VM.

### Lo que hace falta un Windows para verificar

**Primera pasada: el spike.** Es solo lectura, no instala nada, y sirve cualquier PC con Windows —no
hace falta VM.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\spike\Verify-WindowsApis.ps1
```

Correrlo **elevado**, en Win10 **y** Win11, y diffear las dos salidas: las diferencias son casos
condicionales que el código tiene que manejar.

**Segunda pasada: correr el `.exe`.** El diagnóstico es de solo lectura, así que se puede probar sin
riesgo. Lo que hay que mirar:

- ¿El resumen del equipo dice bien el modelo, la RAM y el tipo de disco?
- ¿Aparece algo bajo **«NO SE PUDO DETERMINAR»**? Eso es una sonda con el contrato equivocado.
- El log en `C:\ProgramData\EasyFix\logs\` tiene el detalle de cada sonda que falló.

**Tercera pasada: la VM conejillo, recién para los fixes.** Ciclo obligatorio de cada fix:

1. Snapshot de la VM sucia.
2. Correr «Mejorar rendimiento».
3. Verificar: temporales borrados, inicio deshabilitado (confirmar en el Administrador de tareas),
   punto de restauración presente, journal escrito.
4. **Reiniciar** y comparar el evento 100 `MainPathBootTime` contra el baseline. Es la única prueba de
   que la app hace lo que promete.
5. `Deshacer todo` → verificar que el estado volvió al original.
6. Restaurar snapshot. Repetir.

**Escenarios negativos que hay que probar explícitamente:**
System Restore deshabilitado · disco `C:` lleno (no cabe el punto de restauración) · sin red ·
winget ausente · winget instalado pero invisible al elevar · usuario no-admin · SMART fallando ·
dos antivirus instalados · perfil temporal · **BitLocker activo sin confirmación** ·
**equipo unido a dominio** · **junction dentro de `%TEMP%` apuntando a `Documents`** ·
journal truncado por crash · la app corriendo desde el USB y el USB se retira a mitad ·
antivirus bloqueando la app · proceso hijo (DISM) que nunca termina → debe cortar por timeout.

## Cómo correr el proyecto
### Compilar y testear — macOS, Linux o Windows

Solo hace falta el .NET 8 SDK. En macOS/Linux, sin `sudo`:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"     # no queda persistente: agregalo al perfil si lo querés fijo
```

```bash
cd ~/Proyectos/EasyFix
dotnet build      # los tres proyectos, incluido el de WPF
dotnet test       # 210 pasan, 2 se omiten
```

### Generar el `.exe` portable

```bash
dotnet publish src/EasyFix.App -c Release -o publish
```

Sale un único `publish/EasyFix.exe` — `PE32+ executable (GUI) x86-64`, **69 MiB**. Trae el runtime de
.NET adentro comprimido (`EnableCompressionInSingleFile`; sin comprimir son 148 MB). No hay instalador
ni nada que desinstalar: se copia al pendrive y listo.

Sin trimming a propósito: recorta por análisis estático y rompe todo lo que usa reflexión —
`System.Management`, el binder de `System.Text.Json` y los generadores de MVVM.

### Ejecutar

Solo en Windows. Pide administrador (va en el manifiesto): sin elevación no se puede leer el estado
SMART ni consultar los servicios. Y como el `.exe` no está firmado, SmartScreen lo marca como
desconocido → «Más información» → «Ejecutar de todas formas».

```powershell
dotnet run --project src\EasyFix.App    # desde el repo
EasyFix.exe                              # el portable
```

Logs en `C:\ProgramData\EasyFix\logs\`, journals en `C:\ProgramData\EasyFix\runs\`.

### La VM Windows: para qué sigue haciendo falta

No para compilar ni para desarrollar la UI. Para **probar los fixes**, que es lo que puede romper el
equipo de un cliente, y para medir el antes/después del arranque, que necesita reiniciar de verdad.

- **VMware Fusion Pro** (gratis; *Fusion Player* fue discontinuado por Broadcom en mayo 2024) o
  VirtualBox 7, con Windows 11 x64.
- Una **segunda VM «conejillo»** con Windows sucio a propósito y snapshots.

## Próximos pasos recomendados (fuera de este alcance)
1. **Exportar el reporte a PDF** — para dejarle al cliente constancia de qué se hizo y qué necesita
   comprar. Es lo que más valor agrega por menos trabajo, y ya están todos los datos.
2. **Modo desatendido por CLI** (`EasyFix.exe --auto --report ruta.json`) para varios equipos.
3. **Firma de código** si algún día se distribuye: sin certificado, SmartScreen y Defender lo van a
   marcar como riskware por tocar claves `Run` y servicios. Entre 100 y 300 dólares al año.
4. **`autorunsc.exe` de Sysinternals** como fuente primaria de entradas de autoarranque:
   `scripts/bootstrap.ps1` ya lo descarga, pero `SystemProbe` todavía lee el registro a mano y por eso
   solo cubre las ubicaciones principales.

