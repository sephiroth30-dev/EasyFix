# EasyFix — Optimizador y Reparador de Windows

> **Este es el documento de trabajo del proyecto.** Estado actual, decisiones tomadas y qué sigue.
> Versionado con el código, así que viaja con el repo. El `README.md` es el resumen para quien llega
> nuevo; esto es el detalle para seguir trabajando.

> Al retomar, leer en este orden: **v0.8.0 — Estado y plan activo** → la sección del componente que
> se vaya a tocar. El resto del documento es material de referencia que cambia poco: objetivo,
> arquitectura, catálogos de diagnóstico y de fixes.

## v0.8.0 — Estado y plan activo

> Última actualización: 2026-09-01, después de v0.8.0.
> Rama `main`, árbol limpio, tag `v0.8.0`. **432 tests pasan, 2 se omiten** (los que exigen Windows).
> `dotnet build` limpio en los tres proyectos, incluido el de WPF. `.exe` portable de 69 MiB.

### Dónde está el proyecto

Las tres acciones están conectadas y funcionan de punta a punta: **analizar**, **mejorar
rendimiento**, **reparar errores**, más **instalar programas** y **deshacer todo**. Lo que falta no es
capacidad — es **verificación en Windows**. Ver «Lo que nunca se ejecutó en Windows», abajo.

### El hilo conductor de las últimas cuatro versiones

Las cuatro salieron del mismo tipo de defecto, y conviene tenerlo presente antes de escribir código
nuevo: **la herramienta afirmaba cosas que no había medido.**

| Versión | El síntoma que reportó el usuario | La causa real |
|---|---|---|
| 0.5.0 | «todo error» en el log del 2026-08-25 | La mayor parte funcionó. El bug era que la app **reportaba fallo donde hubo éxito**: dos constantes de winget mal, y el punto de restauración se creaba pero se verificaba con una carrera perdida |
| 0.6.0 | «conecta mejorar rendimiento» | El botón no existía. Tres componentes escritos y testeados —50 tests— sin nadie que los invocara |
| 0.7.0 | «la barra de progreso se oculta» | No era la barra: dos fixes hacían trabajo síncrono y devolvían `Task.FromResult`, así que corrían en el hilo de la UI y Windows congelaba la ventana |
| 0.8.0 | «simplemente no instaló nada» | **El equipo no tenía internet.** La evidencia estaba en el log 365 ms antes de la primera llamada a winget, y nadie la consumía. La app inventó una causa —«sesión elevada»— y declaró «Fuentes reparadas» 8,2 s antes de que el mismo error volviera |

La regla que salió de ahí, y que ya está aplicada en todo el código nuevo: **un dato que no se pudo
medir nunca se cuenta como verde**, y **un éxito falso es peor que un fallo falso** porque no deja
rastro que haga sospechar.

### v0.8.0 — detección de red

Cierra el «no instaló nada». Lo que se agregó:

- **`Core/Network/Connectivity.cs`** — `ConnectivityStatus` con **cinco** estados de fallo distintos
  (`NoAdapter`, `DnsFailed`, `Unreachable`, `CaptivePortal`, `Unknown`), porque cada uno lleva a una
  acción distinta del técnico. `NetworkDiagnosis` traduce excepciones a estados: lógica pura, testeada
  sin red, incluido el caso exacto del log (`SocketException 11001` dentro de un
  `HttpRequestException`, que hay que buscar recorriendo las excepciones internas).
- **`Core/Network/ConnectivityCheck.cs`** — dos etapas: `NetworkInterface.GetIsNetworkAvailable()`
  como descarte instantáneo del cable desenchufado, y después una petición real, que es la única forma
  de saber si hay internet.
- **La compuerta en `WingetService.InstallAsync`** — se evalúa **una vez por tanda**, antes de todo.
  Sin conexión no se lanza **ningún** proceso: ni winget ni las descargas directas. Los tests usan un
  runner y un descargador que **revientan si alguien los invoca**, así que el test pasando *es* la
  verificación.
- **El hallazgo `network.offline` en el reporte**, con el motivo concreto y la aclaración de que
  analizar, mejorar y reparar **sí** funcionan sin internet. Corre en paralelo con las sondas.
- **Sección `Network` en `appsettings.json`.**

**Decisión no obvia, documentada en tres lugares para que nadie la «arregle»:** la comprobación va por
**HTTP y no HTTPS**. Es lo que permite detectar un portal cautivo — sobre HTTPS el portal produce un
error de certificado indistinguible de un firewall. No se descarga nada ejecutable: son 22 bytes de
texto comparados contra una constante. Los instaladores siguen exigiendo HTTPS sin excepción.

**Decisión tomada y descartada:** no se bloquea «Reparar errores» sin internet. Era el uso obvio de
`FixBlockReason.NoNetwork` y está mal — `DISM /ScanHealth` es de solo lectura y `SystemFileRepairFix`
lo corre primero. El enum sigue sin usarse, a propósito.

### v0.8.0 — lo que se corrigió de paso

- **«Fuentes de winget reparadas» era mentira.** Se declaraba mirando el código de salida de
  `winget source reset`. Ahora la única prueba admitida es que **el reintento funcione**.
- **Un fallo de catálogo confirmado no se repite paquete por paquete.** Eran tres párrafos idénticos
  recomendando la reparación que ya había fallado. Los de descarga directa sí se siguen intentando.
- **El log no tenía ni una palabra de winget** — solo se registraba `stderr`, y winget escribe en
  `stdout`. Ahora las dos, con `Core/Processes/ProcessOutput.cs` colapsando la animación de progreso
  (cientos de cuadros del mismo renglón reescrito con `\r`) sin tocar el texto real.
- **Una constante equivocada ya no puede producir un éxito falso.** Ver abajo.
- **`FileVersion`/`AssemblyVersion` estaban clavados en `0.6.0.0`** con `Version` en 0.8.0.

### La trampa de las constantes de winget, y cómo quedó desactivada

`WingetErrorCodes` transcribe códigos a mano. **Dos ya estuvieron mal una vez** y produjeron fallos
falsos en la primera prueba real. Tres siguen sin verificar y están marcadas `SIN VERIFICAR` una por
una, con el efecto concreto si están mal:

| Constante | Estado | Si está mal |
|---|---|---|
| `NoApplicationsFound` `0x8A150014` | **Verificado** en log real | — |
| `UpdateNotApplicable` `0x8A15002B` | **Verificado** en log real | — |
| `SourceDataMissing` `0x8A15000F` | **Verificado** en log real | — |
| `InstallerHashMismatch` `0x8A150011` | **Verificado** en log real | — |
| `PackageAlreadyInstalled` `0x8A150056` | SIN VERIFICAR | Era la vía al éxito falso. Ya neutralizada |
| `NoApplicableInstaller` `0x8A150061` | SIN VERIFICAR | Fallo genérico con el código a la vista. Molesto, no peligroso |
| `FailedToOpenAllSources` `0x8A150019` | SIN VERIFICAR, y sospechoso: podría ser `NOT_ALL_QUERIES_FOUND_SINGLE` | Se intenta reparar fuentes cuando no hacía falta. El reset es idempotente |

**Por qué ya no es grave.** Un código sin reconocer cae en `Failed` con su valor en crudo, así que el
peor caso es un fallo honesto y diagnosticable. La **única** vía por la que un código de error se
volvía un *éxito* era «ya estaba instalado», porque cuenta como paquete disponible. Ahora ese
desenlace exige confirmación independiente (`WingetErrorCodes.ConfirmsAlreadyInstalled`): la tabla del
propio winget del equipo —por el **nombre** del símbolo, que sí es estable entre versiones— o que
winget lo diga por texto, en español o en inglés. Sin confirmación se reporta el fallo explicando que
podría estar instalado y no se pudo confirmar.

**Para cerrarlo hace falta un dato de Windows:** la salida de `winget error --output <archivo>` de un
equipo con **winget 1.6 o superior**. En winget 1.24 y anteriores ese comando devuelve
`INVALID_CL_ARGUMENTS` — es lo que pasó en el equipo del 2026-08-29, y por eso ahí las constantes
fueron la única fuente. Está pedido en `docs/PRUEBAS.md`, Nivel 1.

### Sospechas abiertas, sin verificar y sin tocar

Salieron del análisis multiagente de los logs del 2026-08-29. Los refutadores de estas dos **no
llegaron a correr** (límite de sesión), así que son hallazgos sin verificación adversarial: hay que
comprobarlos antes de cambiar código.

1. **`disco.salud = Healthy` con la sonda de disco en timeout.** El log dice `disco.tipo = Unknown` y
   `disk.physical` en timeout, pero `disco.salud = Healthy`. ¿De dónde sale ese `Healthy`? Si es el
   valor por defecto del enum, el reporte está afirmando salud que no midió — el mismo pecado que
   v0.8.0 arregló en otro lado. Mirar `ProbeSmart` vs `ProbePhysicalDisk` y el default de
   `DiskHealth`.
2. **«0 hallazgos de hardware» con la sonda de pantallazos en timeout.** Mismo patrón: concluir
   ausencia de problemas a partir de una comprobación que no terminó.
3. **`bateria.desgaste = no medido` no es una medición fallida** — según el análisis, **no existe
   ninguna sonda de batería** en el código. Si es así, el campo miente por omisión: dice «no medido»
   como si se hubiera intentado.
4. **Bools y contadores sin estado «no medido»** — `IsDomainJoined`, `BitLockerActive`, `HasPrinters`,
   `HasBluetoothAdapter`, `ActiveAntivirusCount` son `bool`/`int`, no `bool?`/`int?`. Un timeout se
   reporta como `False`/`0`, que es una afirmación. `Network` se agregó como `ConnectivityStatus?`
   justamente para no repetirlo.
5. **`disco.tipo = Unknown` se consume como si fuera un dato** — `IsSsd` devuelve `false` para
   `Unknown`, así que un disco no identificado se trata como «no es SSD». Suprime la recomendación más
   valiosa de la herramienta (cambiar a SSD) y afecta la condición de `SysMain`.
6. **`SlowProbes` incompleto** — `disk.smart`, `boot`, `bitlocker` y `antivirus` siguen con 5 s. En el
   Pentium G2020, `disk.physical` y `crash` no alcanzaban con 5 s; es razonable que estas tampoco.

Los puntos 1 a 5 son todos la misma clase de defecto: **el snapshot no distingue «no medido» de un
valor**. Vale considerarlos como un solo trabajo.

### Lo que nunca se ejecutó en Windows

Es la deuda real del proyecto. Todo lo de abajo compila, tiene tests y **nunca corrió en el sistema
operativo al que apunta**:

- **Todo v0.7.0 y v0.8.0.** El congelamiento de la ventana y la falta de red se diagnosticaron
  leyendo código y logs, no reproduciéndolos.
- **Los cuatro fixes de rendimiento** y `StartupEntryReader`.
- **El botón «Deshacer todo»** y los `IUndoHandler` concretos.
- **`BitLockerService`** — ningún equipo de prueba tenía BitLocker. Es el camino al daño
  irreversible más grave que la app podría causar, así que es el que más falta verificar.
- **El análisis de pantallazos** contra un equipo que efectivamente los tenga.
- **`ScheduleMemoryTestFix` y `RemoveCorrelatedUpdateFix`** — nunca se ofrecieron.
- **Chrome y RustDesk por MSI.**
- **`tools/spike/Verify-WindowsApis.ps1`** — escrito, nunca corrido. Sigue siendo la vía más rápida
  para validar de una todos los contratos de WMI. Es solo lectura.

`docs/PRUEBAS.md` tiene los niveles ordenados por riesgo. **El Nivel 1.5 (sin internet) es el más
valioso ahora**: es solo lectura, se hace en cualquier equipo desenchufando el cable, y reproduce a
propósito el fallo del 2026-08-29. Su chequeo clave es negativo — en el log **no** puede aparecer
ninguna línea de `winget.exe`.

### Lo que falta implementar

En orden de valor, no de dependencia:

1. **Distinguir «no medido» de un valor en todo el snapshot** — los puntos 1 a 5 de las sospechas
   abiertas. Es la deuda de diseño que queda del mismo problema que produjo las últimas tres
   versiones.
2. **La lista de verificación antes/después** — las ~12 condiciones del objetivo «estado estándar»,
   evaluadas dos veces y mostradas en dos columnas. Es el entregable que convierte «te lo dejé sin
   errores» en algo verificable. Ver «Cómo se vuelve medible», más abajo.
3. **Medición antes/después del arranque** — el baseline ya se lee; falta guardarlo en el journal y
   comparar tras el reinicio contra el evento 100.
4. **Tier B con casillas** — servicios, bloatware, apps UWP, efectos visuales.
5. **Modo «Restablecer a estado estándar»** — red, Windows Update, energía, asociaciones, Store,
   políticas locales.
6. **Wizard de perfil**, los tres niveles.
7. **Reanudación tras reinicio** (`RunOnce --resume`).
8. **Bootstrap de winget** si falta (Win10 anterior a 1809).

### Errores corregidos que conviene no repetir

- **WPF sí compila fuera de Windows** con `EnableWindowsTargeting=true`. Una versión anterior de este
  plan afirmaba lo contrario y bloqueaba el desarrollo de la UI detrás de una VM innecesaria.
- **Los contadores de rendimiento están localizados.** `\Memory\Committed Bytes` no existe en un
  Windows en español. Se usan las clases `Win32_PerfFormattedData_*`, cuyas propiedades tienen el
  mismo nombre en cualquier idioma. Lo mismo con DISM: se le pasa `/English`.
- **`Path.GetInvalidFileNameChars()` depende del sistema anfitrión.** En Unix no incluye `:`, así que
  un `runId` con timestamp ISO habría generado un nombre inválido al llegar a Windows.
- **`OperationCanceledException` también es `Exception`.** Un `catch` general se comía la cancelación
  del usuario y devolvía un reporte falso lleno de «no se pudo determinar».
- **winget se instala por usuario.** Al elevar, `%LOCALAPPDATA%` puede resolver al perfil del
  administrador, donde el alias de `winget.exe` no existe. Ver `WingetLocator`.
- **winget se autoactualiza a mitad de una tanda.** La carpeta de su paquete cambia de nombre y la
  ruta cacheada deja de existir. Se re-resuelve antes de cada paquete.
- **`SRSetRestorePoint` es asíncrona.** Enumerar inmediatamente después es una carrera que casi
  siempre se pierde. Se sondea con reintentos y se verifica por **fecha**, no por secuencia.
- **Un test double puede mentir.** `InMemoryFileTree` pisaba el atributo `ReparsePoint` del directorio
  padre, y el test estrella del junction estaba probando otra cosa mientras pasaba en verde.
- **Trabajo síncrono + `Task.FromResult` congela la UI.** Si el método nunca espera nada, el cuerpo
  entero corre en el hilo que llamó. `X509Chain.Build` por binario es lo más lento de la app.
- **Un cero puede ser un contador sin inicializar.** `disco.latencia = 0 ms` no es un disco
  infinitamente rápido.
- **Registrar solo `stderr` no alcanza.** winget, DISM y sfc escriben sus diagnósticos en `stdout`.

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

## Componentes y dónde viven

Conteo de tests por área, para saber qué está respaldado y qué no. **432 en total, 2 omitidos.**

| Componente | Dónde | Respaldo |
|---|---|---|
| Clasificador de 3 capas | `Core/Classification/StartupClassifier.cs` | 20 tests |
| Journal JSONL + `UndoEngine` + handlers | `Core/Rollback/` | 25 tests. Handlers **sin correr en Windows** |
| Limpiador junction-safe de temporales | `Core/Cleaning/JunctionSafeCleaner.cs` | 15 tests, uno con symlink real |
| Motor de diagnóstico (paralelo, timeout por sonda) | `Core/Diagnostics/DiagnosticEngine.cs` | 13 tests |
| Motor de recomendación de hardware | `Core/Recommendations/HardwareAdvisor.cs` | 26 tests |
| Condiciones de servicio (`SysMain` en HDD) | `Core/Fixes/ServiceConditionEvaluator.cs` | 15 tests |
| Módulo winget | `Core/Apps/WingetService.cs`, `WingetResultParser.cs`, `WingetErrorCodes.cs` | 17 + los de códigos. **3 constantes sin verificar** |
| Descarga directa (Chrome MSI, RustDesk de GitHub) | `Core/Apps/DirectDownloadInstaller.cs` | **Sin correr en Windows** |
| **Detección de red** | `Core/Network/` | 30 tests. **Sin correr en Windows** |
| Runner de procesos endurecido + limpieza de salida | `Core/Processes/` | 5 + 9 tests |
| Punto de restauración y su política | `Core/Rollback/RestorePointPolicy.cs`, `RestorePointService.cs` | La política es pura y testeada; el servicio corrió en un equipo real |
| Política de seguridad de los fixes (4 compuertas) | `Core/Fixes/FixRunner.cs` | Toda la política de seguridad en un solo archivo |
| Fixes de rendimiento | `Core/Fixes/PerformanceFixes.cs`, `StartupDisableFix.cs` | **Sin correr en Windows** |
| Fixes de reparación | `Core/Fixes/RepairFixes.cs` | DISM/SFC/chkdsk/red/WU corrieron en un equipo real |
| Análisis de pantallazos azules | `Core/Diagnostics/CrashAnalysis.cs` | Catálogo de 23 bugchecks. **Sin equipo que tenga pantallazos** |
| Cálculo del progreso | `Core/Fixes/IFix.cs`, `Core/Diagnostics/SystemProbe.cs` | 17 tests: monotonía, rango acotado, total en cero |
| `PathGuard`, certificados, IDs de winget, config | `Core/Safety/`, `Core/Apps/`, `Core/Configuration/` | el resto |
| UI en WPF: 7 pantallas | `App/Views/MainWindow.xaml` | compila; **ejecutada solo hasta v0.6.0** |

**La única clase que habla con Windows es `Core/Diagnostics/SystemProbe.cs`** (más los servicios de
punto de restauración, BitLocker y el runner de procesos). Todo lo demás es lógica pura sobre el
snapshot, y por eso se testea desde macOS. Esa decisión es la que permite que el proyecto avance sin
una VM; ver «La decisión de arquitectura más importante».

### Verificado en un equipo real

Del log del 2026-08-25 (v0.5.0) y los del 2026-08-27 y 2026-08-29:

- **`WingetLocator`** encuentra winget bajo `Program Files\WindowsApps`, incluso elevado.
- **Instalación con winget**: 7-Zip y VCRedist instalados con código 0.
- **Descarga directa desde GitHub**: resolvió RustDesk 1.4.9 y bajó el asset correcto.
- **`SystemFileRepairFix`**: DISM `/ScanHealth` → `/RestoreHealth` → `sfc` corrió completo y reparó.
- **`DiskCheckFix`, `WindowsUpdateResetFix`, `NetworkStackResetFix`**: aplicados.
- **El punto de restauración** se crea (secuencia 210 → 213 → 214 entre corridas).
- **`RustDesk.RustDesk` ya NO existe en winget** — confirmado por `NO_APPLICATIONS_FOUND`. La decisión
  de bajarlo de GitHub era necesaria, no precautoria.
- **El diagnóstico corre y mide** — desde v0.5.0 el log vuelca el snapshot completo, así que se puede
  auditar qué midió sin capturas de pantalla. Es lo que permitió encontrar los defectos de v0.7.0 y
  v0.8.0 leyendo logs desde macOS.

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
> los tres proyectos compilan y los 432 tests pasan en esta Mac (Intel, macOS 12.7.6, .NET SDK 8.0.424
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
├─ Directory.Build.props       # FUENTE ÚNICA de la versión y la marca. FileVersion deriva de Version
├─ EasyFix.sln
├─ appsettings.json            # umbrales, listas curadas, paquetes de winget, comprobación de red
├─ CHANGELOG.md                # una entrada por versión, con qué se probó y qué NO
├─ src/
│  ├─ EasyFix.App/             # WPF. Ventana, XAML, ViewModels. Cero lógica de negocio.
│  │  ├─ App.xaml(.cs)         # DI, Serilog, carga de configuración, handler global de excepciones
│  │  ├─ Views/MainWindow.xaml # las 7 pantallas, por Visibility según CurrentScreen
│  │  ├─ ViewModels/           # MainViewModel, AppChoice, Converters
│  │  ├─ Styles/Theme.xaml     # tokens de color, estilos, ProgressBarStyle
│  │  └─ app.manifest          # requireAdministrator + PerMonitorV2
│  └─ EasyFix.Core/            # Toda la lógica. Sin referencia a WPF (test de arquitectura).
│     ├─ Abstractions/         # IFileTree, IProcessRunner
│     ├─ Apps/                 # WingetService, WingetLocator, WingetResultParser, WingetErrorCodes,
│     │                        #   WingetPackageId, DirectDownloadInstaller
│     ├─ Classification/       # StartupClassifier, StartupCandidate, CertificateSubject
│     ├─ Cleaning/             # JunctionSafeCleaner, PhysicalFileTree
│     ├─ Configuration/        # EasyFixOptions, OptionsLoader
│     ├─ Diagnostics/          # SystemProbe (LA ÚNICA que habla con Windows), SystemSnapshot,
│     │                        #   SoftwareFindings, DiagnosticEngine, CrashAnalysis
│     ├─ Fixes/                # IFix, FixRunner (toda la política de seguridad), PerformanceFixes,
│     │                        #   RepairFixes, StartupDisableFix, ServiceConditionEvaluator
│     ├─ Network/              # Connectivity, ConnectivityCheck  ← v0.8.0
│     ├─ Processes/            # SafeProcessRunner, ProcessOutput
│     ├─ Recommendations/      # HardwareAdvisor
│     ├─ Rollback/             # RunJournal, JournalReader, UndoEngine, FileJournalSink,
│     │                        #   RestorePointService, RestorePointPolicy, los IUndoHandler
│     └─ Safety/               # PathGuard
├─ tests/EasyFix.Core.Tests/   # xUnit + fakes propios. 432 tests, 2 omitidos
│  └─ Fakes/                   # InMemoryFileTree, TestDoubles, WindowsOnlyFact, TestPaths,
│                              #   FakeConnectivityCheck
├─ docs/PLAN.md                # este archivo
├─ docs/PRUEBAS.md             # niveles de prueba ordenados por riesgo. Nivel 1.5 = sin internet
├─ tools/spike/                # Verify-WindowsApis.ps1 (nunca corrido)
├─ tools/Diagnose-BlueScreen.ps1
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

### `IFix`: el contrato de un arreglo

`Core/Fixes/IFix.cs`. Cada fix declara qué es capaz de hacer y qué riesgo tiene, y `FixRunner` decide
si lo deja correr. Lo que hay que saber al escribir uno nuevo:

- **`CanApplyAsync` se evalúa antes que nada** y devuelve un `FixApplicability` con su
  `FixBlockReason`. Un fix que no aplica no es un error: es información para el reporte.
- **`TouchesBootOrDisk`** es lo que hace que `FixRunner` lo bloquee sin confirmación de la clave de
  BitLocker. Marcarlo mal es el camino al daño irreversible.
- **`IsReversible = false`** obliga a decirlo en pantalla. Borrar temporales es el único de esta
  categoría.
- **`ApplyAsync` recibe el `RunJournal`** para poder escribir **antes** de tocar el sistema.
- **Todo el trabajo va a un hilo del pool.** Un `ApplyAsync` que hace trabajo síncrono y devuelve
  `Task.FromResult` corre en el hilo de la interfaz y congela la ventana. Pasó de verdad en v0.6.0 con
  dos fixes; se arregló en v0.7.0.
- **`FixProgress`** reporta paso, total y fracción. El porcentaje **nunca retrocede**, y un fix
  bloqueado igual avanza el contador: una barra quieta se lee como una aplicación colgada.

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
| 8 | `SystemProbe` + UI de diagnóstico | Corrió en tres equipos reales; el log vuelca el snapshot completo |
| 9 | Módulo winget + RustDesk | Argumentos exactos, serialización real, un fallo no corta la tanda, ID con `&&` rechazado |
| 10 | `RestorePointService` + política verificable | La política es pura y testeada; el punto se crea en equipo real (secuencia 210 → 213 → 214) |
| 11 | `IUndoHandler` concretos + botón «Deshacer todo» | Se aplican y se deshacen en test; **sin correr en Windows** |
| 12 | Fixes de rendimiento + botón «Mejorar rendimiento» | 50 tests entre los tres componentes; **sin correr en Windows** |
| 13 | Fixes de reparación + botón «Reparar errores» | DISM/SFC/chkdsk/red/WU corrieron completos en equipo real |
| 14 | Análisis de pantallazos azules | Catálogo de 23 bugchecks mapeados a primer sospechoso; **sin equipo que los tenga** |
| 15 | Barra de progreso determinada en las 4 operaciones largas | 17 tests: monotonía, rango acotado, total en cero |
| 16 | **Detección de red y compuerta antes de instalar** | 30 tests; el runner y el descargador revientan si se los invoca sin red |

### Pendiente, en orden de dependencia

**Lo pendiente ya no está bloqueado por código, sino por verificación.** El orden de abajo es por
valor, no por dependencia; ver también «Lo que falta implementar» en el plan activo.

| # | Entrega | Qué la cierra |
|---|---|---|
| 17 | **Probar v0.7.0 y v0.8.0 en Windows** — `docs/PRUEBAS.md`, Nivel 1.5 primero | Sin red: el log no tiene ni una línea de `winget.exe`. Con red: el aviso desaparece |
| 18 | **«No medido» ≠ un valor** en todo el snapshot | Un timeout de `bitlocker` reporta «no determinado», no `False`. `Unknown` en el tipo de disco no suprime la recomendación de SSD |
| 19 | **Lista de verificación antes/después** | Las ~12 condiciones evaluadas dos veces, en dos columnas. Lo que no se pudo evaluar sale «no determinado», nunca verde |
| 20 | Medición antes/después del arranque | Baseline en el journal; tras reiniciar, el delta contra el evento 100 coincide con el Visor de eventos |
| 21 | Tier B con casillas | Servicio desactivado y restaurado con su tipo de arranque exacto; modo dominio → bloqueado |
| 22 | Modo «Restablecer a estado estándar» | Red, Windows Update, energía, asociaciones, Store, políticas locales |
| 23 | Wizard de perfil | Perfil temporal inducido: nivel 1 lo resuelve. `robocopy` **no** copia `NTUSER.DAT` ni `AppData\Local\Temp` |
| 24 | Reanudación tras reinicio | Ciclo `RunOnce --resume`; cancelar a mitad de DISM deja el sistema consistente |
| 25 | Bootstrap de winget si falta | Win10 sin winget: se instala el App Installer y después el paquete |
| 26 | Recomendador del reset de Windows | Criterios explícitos, medidos |

**La regla de orden que sigue vigente:** nada que modifique el sistema corre sin punto de restauración
verificado. `FixRunner` lo impone en código, con cuatro compuertas evaluadas en orden — disco
fallando, punto de restauración, BitLocker sin confirmar, dominio.

## Verificación
### Lo que corre hoy, en cualquier sistema

```bash
dotnet test                                        # 432 pasan, 2 se omiten fuera de Windows
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
dotnet test       # 432 pasan, 2 se omiten
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

