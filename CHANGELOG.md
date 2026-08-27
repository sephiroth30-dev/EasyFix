# Changelog

Todas las versiones de EasyFix, de la más nueva a la más vieja.

**Convención de versionado** — SemVer adaptado a que esto no es una librería:

| Parte | Cuándo sube |
|---|---|
| MAJOR | Cambio que rompe `appsettings.json` o el formato del journal |
| MINOR | Capacidad nueva: un fix, una sonda, una pantalla |
| PATCH | Corrección de algo que estaba mal |

La versión vive en un solo lugar: `Directory.Build.props`. Se ve en la barra de título de la app y en
Propiedades → Detalles del `.exe`.

Cada versión probada en un equipo real lleva su resultado anotado. Lo que no se probó se dice.

---

## [0.4.0] — 2026-08-27

Correcciones salidas del log de la primera prueba real. **La mayor parte de v0.3.0 funcionaba**; lo
que estaba mal era el reporte: la app decía «falló» donde había éxito.

### Corregido

- **El punto de restauración se creaba y la app decía que no.** `SRSetRestorePoint` es asíncrona: el
  punto no aparece en WMI hasta unos segundos después. La verificación anterior enumeraba al instante
  y perdía la carrera siempre — cinco falsos negativos en cinco corridas, mientras las secuencias
  avanzaban 210 → 213 → 214. Ahora se sondea hasta 60 s y la señal principal es la **fecha** del punto
  más nuevo, no la secuencia.
- **`HighestSequence()` devolvía 0 tanto si no había puntos como si la consulta fallaba**, y 0 se
  interpretaba como «no hay». Un fallo de lectura se estaba tratando como un hecho. Ahora distingue
  «no hay» de «no pude leer».
- **Límite de 24 h de Windows.** Impedía crear el punto en cualquier equipo con actividad ese día. Se
  neutraliza vía `SystemRestorePointCreationFrequency`, registrando el valor anterior en el journal.
  Configurable con `Thresholds.DisableRestorePointThrottle`.
- **Dos constantes de código de salida de winget estaban mal**, y produjeron los fallos falsos:
  `0x8A15002B` es `UPDATE_NOT_APPLICABLE` (no `NO_APPLICATIONS_FOUND`) y `0x8A150014` es
  `NO_APPLICATIONS_FOUND` (no un error de fuentes). **`UPDATE_NOT_APPLICABLE` sobre un `install`
  significa «ya está instalado»** — por eso 7-Zip y Visual C++ Redistributable aparecían como «no
  encontrado» después de que la propia app los instalara.
- **«Acceso denegado» a mitad de la tanda.** winget se autoactualiza y la carpeta de su paquete cambia
  de nombre: en la prueba pasó de `_1.29.280.0` a `_1.29.290.0`. La ruta se resolvía una vez por tanda.
  Ahora se resuelve antes de cada paquete, con reintento ante `Win32Exception` 5 o 2.
- **VLC devolvía 1 arrancando en el mismo segundo que terminó VCRedist.** Se espera a que el mutex
  `_MSIExecute` de Windows Installer esté libre: instalar en serie no alcanza si el anterior sigue
  finalizando.
- **DISM detectaba corrupción comparando texto en inglés** contra un Windows en español, así que
  `RestoreHealth` corría siempre: ~4 min perdidos por corrida. Se pasa `/English`.
- **`net stop` con código 2 se reportaba como advertencia.** Significa que el servicio ya estaba
  detenido, que es el caso normal.
- **`netsh int ip reset` devuelve 1 con frecuencia aunque funcione.** Ya no se cuenta como fallo salvo
  que winsock también falle.
- **El directorio de trabajo de los procesos hijos se heredaba** de donde se lanzó el `.exe`. Ahora se
  fija en `System32`.

### Agregado

- **Tabla de códigos leída del propio winget** con `winget error --output`. El mapeo lo provee el
  winget instalado en vez de constantes que ya estuvieron mal una vez.
- **El log registra el diagnóstico completo**: cada campo medido y cada hallazgo. Antes solo hablaba
  de fallos, así que no servía para confirmar que el análisis funcionó — y eso era justo lo que
  faltaba verificar.
- **Los primeros `IUndoHandler` concretos**: valor de registro, borrado de valor y reactivación de
  BitLocker. Antes el journal registraba pasos que nadie podía ejecutar.

### Estado de las pruebas

| Componente | Estado |
|---|---|
| Reparaciones (DISM, sfc, chkdsk, WU, red) | ✅ ejecutadas y aplicadas en equipo real |
| `WingetLocator` y instalación con winget | ✅ ejecutados; los fallos eran de reporte |
| Descarga directa desde GitHub | ⚠️ resolvió y descargó; falta el desenlace del instalador |
| `RestorePointService` | ⚠️ se ejecutó; su corrección **sin reprobar** |
| Diagnóstico completo | ❌ **sin verificar** — el log no lo registraba. Ahora sí |
| `BitLockerService` | ❌ el equipo de prueba no tenía BitLocker |
| Análisis de pantallazos | ❌ **sin ejecutar** contra un equipo con pantallazos |

---

## [0.3.0] — 2026-08-25

Primera versión que **corrige**, no solo diagnostica. Y la primera que se corrigió a partir de una
prueba en un equipo de verdad.

### Agregado

- **Análisis de pantallazos azules.** Catálogo de 23 códigos de parada con su primer sospechoso
  (RAM, disco, driver, video, archivos de sistema, hardware). Lee el bugcheck de los eventos 1001,
  los eventos WHEA, los apagones Kernel-Power 41, los errores de disco y los volcados.
- **Cruce con actualizaciones.** Para cada pantallazo, qué se instaló en los 7 días anteriores. Si
  ninguno cae en esa ventana, se dice que la hipótesis no se sostiene: descartar también es un dato.
- **Seis reparaciones:** DISM `/RestoreHealth` + `sfc`, `chkdsk`, reset de Windows Update, reset de
  la pila de red, prueba de memoria programada, y desinstalar la actualización correlacionada.
- **`FixRunner` con cuatro compuertas de seguridad**, todas en un solo lugar para que ningún fix las
  esquive: disco fallando aborta, sin punto de restauración aborta, BitLocker sin confirmar bloquea
  lo que toca arranque o disco, equipo en dominio entra en modo restringido.
- **Punto de restauración verificado.** Windows aplica un límite de 24 h: `CreateRestorePoint`
  devuelve éxito y no crea nada. Se compara la secuencia antes y después.
- **Instalación de programas con winget**, en serie, en silencio, siempre la última versión.
- **RustDesk**, y con él la descarga directa desde el origen oficial.
- **Marca del técnico**: barra de título, metadata del ejecutable y `appsettings.json`.
- **Versión visible** en la ventana.

### Corregido — de la prueba en un equipo real

- **El punto de restauración bloqueaba todo.** En muchos equipos Restaurar sistema viene
  deshabilitado de fábrica. Se agrega un override explícito, nunca por defecto: el journal sigue
  registrando cada cambio, así que «Deshacer todo» funciona igual; lo que se pierde es el respaldo
  del sistema completo.
- **«Falló» sin decir por qué.** El motivo estaba en el log y nadie abre un log en la casa de un
  cliente. Ahora el código y la explicación van en la fila, y hay un botón «Diagnosticar winget».
- **winget elevado no ve su propio catálogo.** `Microsoft.Winget.Source` se instala por usuario y la
  sesión de administrador no lo tiene → `0x8A15000F`. Se detecta y se auto-repara con
  `source reset --force`. Ver [winget-cli#698](https://github.com/microsoft/winget-cli/issues/698).
- **RustDesk fue removido de winget** en 2026 porque ESET lo marcó como `RemoteAdmin.RustDesk`, un
  falso positivo. Ver [winget-pkgs#368229](https://github.com/microsoft/winget-pkgs/issues/368229).
  Se baja de la API de releases de GitHub.

### Estado de las pruebas

| Componente | Estado |
|---|---|
| Diagnóstico (solo lectura) | ⚠️ probado una vez; el resumen del equipo hay que verificarlo |
| Instalación de programas | ❌ falló en la primera prueba; corregido, **sin reprobar** |
| Las seis reparaciones | ❌ **nunca ejecutadas** |
| `RestorePointService`, `BitLockerService` | ❌ **nunca ejecutados** |
| Análisis de pantallazos | ❌ **nunca ejecutado** contra un equipo con pantallazos |

---

## [0.2.0] — 2026-08-25

### Agregado

- **`SystemProbe`**: la única clase que habla con Windows. Lee WMI, registro y Event Log, y devuelve
  un `SystemSnapshot`. Todo el resto del diagnóstico queda como lógica pura, testeable en cualquier
  sistema.
- **Motor de recomendación de hardware.** Equipo sano devuelve lista vacía; un dato no medido nunca
  genera recomendación.
- **Motor de diagnóstico** en paralelo, con timeout por chequeo: un chequeo colgado no impide que
  salga el reporte.
- **Evaluador de condiciones de servicio.** Existe por `SysMain`, que en disco mecánico *mejora* el
  rendimiento.
- **Parser de resultados de winget.** «Ya estaba instalado» no es un fallo.
- **UI en WPF**: inicio, analizando, reporte, instalar programas.
- **Módulo winget** con 17 tests.

### Corregido

- `DiagnosticEngine` se comía la cancelación del usuario: el `catch` general atrapaba también
  `OperationCanceledException` y devolvía un reporte falso lleno de «no se pudo determinar».
- `FileJournalSink` usaba `Path.GetInvalidFileNameChars()`, que depende del sistema anfitrión. En
  Unix no incluye `:`, así que un `runId` con timestamp ISO generaba un nombre inválido en Windows.
- El tipo `Classification` chocaba con el namespace del mismo nombre. Renombrado a `StartupVerdict`.
- `InMemoryFileTree` pisaba el atributo `ReparsePoint` del directorio padre, y el test del junction
  estaba probando otra cosa mientras pasaba en verde.

---

## [0.1.0] — 2026-08-24

Núcleo de lógica pura, todo verificable sin Windows.

### Agregado

- **Clasificador de 3 capas** para decidir qué se puede desactivar: por certificado Authenticode, no
  por nombre de archivo. Un malware llamado `MyVPN` no queda protegido; `PulseSecure` sí.
- **Journal en formato JSON Lines** y motor de deshacer. Una escritura truncada por un crash pierde
  una línea, no el archivo.
- **Limpiador de temporales que no sigue junctions.** Verificado con un symlink real.
- **`SafeProcessRunner`**: `ArgumentList`, sin shell, ruta absoluta obligatoria.
- `PathGuard`, parser de certificados, validación de IDs de winget, loader de configuración.
- Spike de verificación de contratos de Windows (`tools/spike`), sin correr.

### Descubierto

- **WPF sí compila fuera de Windows** con `EnableWindowsTargeting=true`. La documentación previa
  afirmaba lo contrario y bloqueaba el desarrollo de la UI detrás de una VM innecesaria.
- Los contadores de rendimiento están **localizados**: `\Memory\Committed Bytes` no existe en un
  Windows en español. Se usan las clases `Win32_PerfFormattedData_*`.
