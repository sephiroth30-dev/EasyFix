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

## [0.9.3] — 2026-11-18

Reporte de uso real: «Instalar programas» falló en un equipo nuevo porque faltaba winget, y
el único recurso era un botón de diagnóstico que explica el problema sin resolverlo. Era la deuda
#8 del plan («Bootstrap de winget si falta»), pendiente desde v0.7.0.

### Agregado

- **Instalación automática de winget cuando falta** (`WingetBootstrapper`, nuevo en
  `Core/Apps/`). Al apretar «Instalar» en un equipo sin winget, EasyFix ya no se bloquea: descarga
  el instalador oficial (`microsoft/winget-cli`, último release de GitHub) y su dependencia
  (`Microsoft.VCLibs.x64.14.00.Desktop.appx`, redirect oficial de Microsoft), e instala con
  `Add-AppxPackage` — un solo intento por corrida, mismo patrón que la reparación de fuentes de
  winget. **La única prueba de éxito es volver a encontrar `winget.exe` después**: no se confía en
  el código de salida de `Add-AppxPackage`, mismo principio que ya regía para «fuentes reparadas».
  Si falla, el resultado explica qué se intentó y por qué no se pudo — nunca un bloqueo mudo.
- **Límite declarado, no adivinado**: algunos equipos además necesitan `Microsoft.UI.Xaml`, que no
  tiene un origen oficial estable para bajar suelto. Si `Add-AppxPackage` falla por eso, el mensaje
  lo dice en vez de intentar una segunda descarga sin probar. El botón «Diagnosticar winget» queda
  para ese caso: ya no es el primer paso, es el recurso cuando el automático no alcanzó.
- **Release versionado sin ambigüedad**: el `.exe` del Release ahora se sube también con el nombre
  `EasyFix-v{versión}.exe`, además del `EasyFix.exe` sin versión (que sigue funcionando para el
  link estable `.../releases/latest/download/EasyFix.exe`). Antes el archivo descargado se llamaba
  igual sin importar la versión.

**Probado:** 516 tests (514 + 2 omitidos). Build Release, 0 warnings. El propio bootstrap —
descargar e instalar winget de verdad— **sin correr en Windows**: es la pieza de mayor riesgo de
esta versión (instala software real, invoca PowerShell) y queda en `docs/PRUEBAS.md` como la
primera prueba a hacer en el próximo equipo que lo necesite.

---

## [0.9.2] — 2026-11-16

Reporte de uso real en dos equipos: «siento que no hizo nada» al usar «Mejorar rendimiento», y
«no pidió reiniciar» al desactivar programas de inicio. Las dos sensaciones tenían causa concreta.

### El hallazgo

`StartupDisableFix` desactiva programas de arranque escribiendo el flag de `StartupApproved` —
**nunca borra la entrada de la clave `Run`** (si la borrara, el usuario no podría volver a
habilitarla desde el Administrador de tareas). Pero `SystemProbe.ProbeStartupEntries`, la sonda que
alimenta el checklist «programas de inicio», contaba las entradas de `Run` **sin mirar ese flag**.
Resultado medido: desactivar seis programas y el checklist mostraba el mismo número antes y
después. La herramienta trabajaba; la medición no se enteraba.

### Corregido

- **`StartupApproval.IsEnabled(byte[]?)`** — única fuente de la regla del bit, antes duplicada (y
  en `SystemProbe`, ausente). `ProbeStartupEntries` ahora abre la clave `StartupApproved`
  correspondiente a cada `Run` y solo cuenta lo que sigue habilitado.
- **El mensaje de «Mejorar rendimiento» explica que el efecto se nota recién en el próximo
  arranque** — no hacía falta reiniciar para *guardar* el cambio, pero tampoco había efecto
  inmediato en la sesión, y eso no se decía.

### Agregado

- **Vista previa de inicio durante el análisis** (`StartupPreview`, nuevo en `Core/Classification/`):
  antes de apretar «Mejorar», el reporte dice con nombres qué se desactiva solo, qué necesita
  confirmación (y cómo quitarlo a mano hoy mismo desde el Administrador de tareas) y qué es
  intocable. En un equipo de oficina típico, la lista blanca curada (Spotify, Discord, Adobe…) no
  tiene match: antes eso se vivía como «no hizo nada», ahora se dice explícito.
- **La vista previa respeta la compuerta de dominio** de `StartupDisableFix.CanApplyAsync`
  (`IsDomainJoined != false` bloquea el fix entero) — encontrado por una revisión adversarial sobre
  este mismo cambio: sin esto, la vista previa hubiese prometido «se desactiva solo: Spotify» en un
  equipo donde el botón real no toca nada. Va a una categoría separada con el motivo explicado.

**Probado:** 512 tests (510 + 2 omitidos). Build Release, 0 warnings.

---

## [0.9.1] — 2026-11-16

El release de v0.9.0 falló en el CI — y ese fallo fue **la primera corrida de los tests en un
Windows real**, que encontró en el acto lo que 497 tests verdes en macOS no podían ver.

### El hallazgo

`FileJournalSinkIntegrationTests.CadaLineaQuedaEnDiscoAlEscribirse_SinEsperarElDispose` reventó en
`windows-latest` con `IOException: being used by another process`. Causa: el share mode declara qué
pueden hacer **los demás** con el archivo. El sink ya concedía `FileShare.Read`, pero
`File.ReadAllLines`/`File.ReadLines` piden `FileShare.Read` al abrir — es decir, **le niegan la
escritura** al sink que ya la tiene, y Windows rechaza la apertura. En Unix no hay locks
obligatorios de archivo, así que macOS nunca pudo ver el defecto.

### Corregido

- **`JournalStore` lee con `FileShare.ReadWrite`** (`ReadLinesShared`): un journal abierto por la
  corrida en curso —otra instancia, o el resume futuro— ya no desaparece de la lista de deshacer
  por una `IOException` silenciada.
- El test lee con el share correcto, que es además la documentación ejecutable del contrato.

### Agregado

- **CI en Windows en cada push y PR** (`.github/workflows/ci.yml`). Hasta ahora los tests solo
  corrían en Windows al taggear un release — o sea, el peor momento para enterarse. Esto además
  ejecuta en cada push los 2 tests que macOS omite.

**Probado:** 498 tests en macOS (496 + 2 omitidos) y, con este cambio, 498/498 en el runner de
Windows del CI — la primera verificación en Windows que tiene el proyecto desde v0.5.0.

---

## [0.9.0] — 2026-11-15

El estado del equipo se vuelve **verificable**: la lista de condiciones evaluada antes y después de
reparar, en dos columnas. Y se cierra la deuda de diseño de las sospechas 1–5 del plan: **el snapshot
ya distingue «no medido» de un valor** en todos los campos.

### Agregado

- **Lista de verificación antes/después** (`Core/Verification/`): 12 condiciones puras sobre el
  snapshot (salud de disco, espacio, memoria, un solo antivirus, dispositivos, pantallazos, errores
  de disco, inicio liviano, tiempo de arranque, internet, DISM/SFC y chkdsk como pendientes del
  escaneo profundo). Tres estados: verde, rojo y **no determinado** — una sonda caída jamás sale
  verde. Tras «Mejorar rendimiento» o «Reparar errores», la app **vuelve a medir todo** y muestra
  cada condición como `antes ✗ → después ✓`. Las que persisten en rojo se muestran con su motivo, y
  si es hardware, se dice.
- **Reputación de programas instalados** (`Core/Reputation/` + `InstalledProgramsReader`): se leen
  las claves `Uninstall` (nunca `Win32_Product`) y se cruzan contra un catálogo de ~50 PUPs
  documentados en `appsettings.json` (scareware, optimizadores falsos, driver updaters fraudulentos,
  secuestradores de navegador, bundlers). Match por **(publisher, producto)**, no por nombre solo.
  Dos niveles: «confirmado-dañino» (Warning) y «dudoso» (Info). **EasyFix no desinstala programas**:
  lista, con tamaño y categoría, para que el técnico decida. Catálogo curado con referencia de
  winutil y Win11Debloat.
- **Antivirus con nombres**: `SecurityCenter2` ahora reporta cuáles, no solo cuántos. Con más de uno
  activo, la recomendación nombra al de terceros y aplica la regla del técnico: sin licencia de pago
  vigente, queda solo Microsoft Defender. La licencia no se puede medir → se pregunta, no se afirma.
- **Sonda de batería** (`battery`): presencia por `Win32_Battery`, desgaste por
  `BatteryStaticData.DesignedCapacity` vs `BatteryFullChargedCapacity.FullChargedCapacity` en
  `root\wmi`. Antes `bateria.desgaste = no medido` mentía por omisión: no existía ninguna sonda.
- **Predicción de saturación** (`SaturationAdvisor`): memoria en franja de alerta (70–85 %) y
  proyección de llenado de disco — la única estimación de la app, con el supuesto (~2 GB/mes de
  updates) escrito dentro del texto.
- **Software según la máquina** (`SoftwareSelectionAdvisor`): con RAM < 8 GB, disco mecánico o
  batería presente, el reporte dice qué criterio usar al instalar (Defender solo, evitar residentes,
  plan equilibrado en laptop). Solo con datos medidos.

### Corregido

- **`disco.salud = Healthy` sin medir** (sospecha #1 del plan): un SMART negativo ascendía
  `Unknown` a `Healthy`, así que un timeout de `disk.physical` terminaba declarando salud. SMART
  ahora viaja en su propio campo `SmartPredictsFailure`.
- **Bools y contadores que afirmaban en timeout** (sospechas 3–5): `IsDomainJoined`,
  `BitLockerActive`, `HasPrinters`, `HasBluetoothAdapter`, `ActiveAntivirusCount` pasaron a
  nullable. La regla de consumo es **fallar cerrado**: BitLocker desconocido bloquea como si
  estuviera activo; dominio desconocido restringe; impresora desconocida protege el Spooler.
- **`disco.tipo = Unknown` ya no es «no es SSD»**: `IsSsd` es `bool?`. Un disco sin identificar ya
  no suprime la recomendación de SSD ni habilita mal la condición de SysMain.
- **`SlowProbes` ampliado**: `disk.smart`, `boot`, `bitlocker`, `antivirus` y `battery` con 20 s
  (sospecha #6; presupuesto por analogía con el Pentium G2020, sin verificar en equipo lento).

### Corregido en la revisión adversarial (ronda 2, antes de publicar)

- **Event Log ilegible producía checklist verde** (crítico): la sonda de inestabilidad traga
  excepciones por bloque a propósito, así que «lista vacía» no distinguía «cero pantallazos» de «no
  se pudo leer». `CrashData` lleva ahora flags de lectura por bloque y el checklist degrada esas
  condiciones a «no determinado».
- **BitLocker `ProtectionStatus = 2` se leía como «sin BitLocker»** — la única falla-abierta que
  quedaba en la compuerta del peor daño. Ahora 2 → null → bloquea.
- **Batería con firmware incoherente afirmaba «0 % de desgaste»** — ahora queda sin medir; y las dos
  clases WMI se correlacionan por `InstanceName` (laptops con dos baterías), reportando la más
  desgastada.
- **`HasBattery` ya no afirma «es una laptop»** (un UPS también expone batería); el texto dice
  «batería presente».
- **Carrera del `SystemProbe` singleton**: una sonda abandonada por timeout podía pisar los datos de
  la corrida siguiente (el antes/después se evaluaba con datos viejos). El estado ahora vive en una
  sesión por llamada.
- Volcados de memoria acotados a la ventana analizada; entradas de catálogo menores a 4 caracteres
  se ignoran (editable a mano → contains corto condenaría de más); regresiones del checklist
  (verde→rojo) se cuentan aparte y disparan aviso; `SystemComponent` como texto `"1"` también filtra.

### Sin verificar en Windows (deuda que esta versión agrega)

- La sonda de batería completa (contratos WMI de `root\wmi`).
- `InstalledProgramsReader` (nombres de valor de las claves `Uninstall`).
- El flujo UI del checklist y la re-medición tras reparar.
- Los publishers del catálogo de reputación contra registros reales.

**Probado:** 498 tests en macOS (496 + 2 omitidos que exigen Windows). Nada de esta versión corrió
en un Windows real todavía — ver `docs/PRUEBAS.md`.

---

## [0.8.0] — 2026-08-31

Detección de red. Cierra el «simplemente no instaló nada» de los logs del 2026-08-29, cuya causa era
que **el equipo no tenía internet** y EasyFix no lo comprobaba en ningún momento.

Lo grave no era el fallo: era que la herramienta **inventó una causa**. La evidencia de que no había
DNS estaba en el log 365 ms antes de la primera llamada a winget, y nadie la consumió.

### La secuencia real, del log

1. `06:36:02.800` — Chrome falla con `SocketException (11001)` = `WSAHOST_NOT_FOUND` resolviendo
   `dl.google.com`. Se reportó como «No se pudo descargar Google Chrome: Host desconocido», que es
   cierto pero deja al técnico el trabajo de traducirlo.
2. `06:36:03.165` — arranca winget. Devuelve `0x8A15000F`, error real, pero cuyo motivo era la misma
   falta de red.
3. EasyFix lo atribuye **tres veces** a la sesión elevada, una causa que nunca midió.
4. `06:36:11.322` — declara «Fuentes de winget reparadas».
5. `06:36:19.560` — vuelve el mismo error, 8,2 s después de haberlo declarado resuelto.

### Agregado

- **Compuerta de red antes de instalar.** `IConnectivityCheck` se evalúa **una vez por tanda** y, sin
  conexión, **no se lanza ningún proceso**: ni winget ni las descargas directas. Es lo que garantiza
  que el diagnóstico no pueda equivocarse — no hay error ajeno que malinterpretar.

  Los tests lo verifican con un runner y un descargador que **revientan si alguien los invoca**: que
  el test pase es la prueba de que la compuerta cortó antes.

- **Cinco estados de fallo, no uno.** `NoAdapter`, `DnsFailed`, `Unreachable`, `CaptivePortal`,
  `Unknown` — cada uno lleva a una acción distinta del técnico. «Sin conexión» a secas manda a revisar
  el cable cuando el problema puede ser el portal cautivo de un wifi de hotel.

  `Unknown` **no autoriza a intentar**: una comprobación que no se pudo hacer no es evidencia de que
  haya internet. Misma regla que el resto del diagnóstico.

- **Detección de portal cautivo.** La comprobación va por **HTTP a propósito**, y está documentado en
  el código y en `appsettings.json` para que no se «arregle» por error: sobre HTTPS un portal produce
  un error de certificado indistinguible de un firewall. No se descarga nada ejecutable — son 22 bytes
  de texto contra una constante. Los instaladores siguen exigiendo HTTPS sin excepción.

- **El reporte lo dice antes de apretar el botón.** `SystemSnapshot.Network` y el hallazgo
  `network.offline`, con el motivo concreto y la aclaración de que analizar, mejorar y reparar **sí
  funcionan** sin internet. La comprobación corre en paralelo con las sondas, así que no alarga el
  análisis. `null` = no comprobado: no genera hallazgo, no se afirma nada.

- **`Network` en `appsettings.json`** — endpoint, cuerpo esperado y timeout. Configurable porque una
  red corporativa puede bloquear el endpoint por defecto, y entonces EasyFix diría «sin internet» en
  un equipo que sí tiene.

### Corregido

- **«Fuentes de winget reparadas» era mentira.** Se declaraba mirando el código de salida de
  `winget source reset`. Que el comando corra no significa que el catálogo quedó legible. Ahora la
  única prueba admitida es que **el reintento funcione**, y se registra el resultado real en los dos
  casos.

- **Un fallo de catálogo confirmado no se repite paquete por paquete.** Cuando la reparación se hizo y
  no sirvió, los paquetes de winget restantes se resuelven sin lanzar el proceso. En el log eran tres
  párrafos idénticos recomendando la reparación que ya había fallado. Los de descarga directa **sí**
  se siguen intentando: no usan el catálogo.

- **El log no tenía ni una palabra de winget.** Solo se registraba `stderr`, y winget escribe sus
  diagnósticos en `stdout`: por eso cuatro líneas del log terminaban literalmente en «stderr: ».
  Ahora se registran las dos. DISM y sfc hacen lo mismo.

  Con eso apareció el problema opuesto —la animación de progreso son cientos de cuadros del mismo
  renglón reescrito con `\r`, y truncar a 2000 caracteres se quedaba con puro ruido—, así que
  `ProcessOutput` conserva el último cuadro de cada línea y descarta los renglones que son solo
  animación. **El texto real no se toca.**

- **Una constante equivocada ya no puede producir un éxito falso.** «Ya estaba instalado» cuenta como
  paquete disponible, o sea que es el único camino por el que un código de error se convierte en
  éxito reportado. Dos constantes de esta clase ya estuvieron mal una vez, y las de «ya instalado»
  nunca se pudieron verificar contra un Windows.

  Ahora ese desenlace exige confirmación independiente: la tabla del propio winget del equipo (por el
  **nombre** del símbolo, que sí es estable entre versiones) o que winget lo diga por texto, en
  español o en inglés. Sin confirmación se reporta el fallo con el código en crudo, explicando que
  podría estar instalado y no se pudo confirmar. Un fallo falso se ve y se corrige; un éxito falso no
  deja rastro.

- **Tres constantes marcadas como sin verificar**, una por una, con el efecto concreto si están mal.
  El comentario de la clase afirmaba que todas estaban verificadas contra la documentación, lo que era
  precisamente el tipo de afirmación no medida que este proyecto trata de evitar.

- **`FileVersion` y `AssemblyVersion` estaban clavados en 0.6.0.0** mientras `Version` iba en 0.8.0:
  la barra de título decía una versión y Propiedades → Detalles del `.exe` decía otra. Ahora se
  derivan de `Version`. Verificado en el binario publicado: `0.8.0` presente, `0.6.0.0` ausente.

### Decisión tomada y descartada

**No se bloquea «Reparar errores» sin internet.** Era el uso obvio de `FixBlockReason.NoNetwork`, y
está mal: `DISM /ScanHealth` es de solo lectura y no necesita conexión, y `SystemFileRepairFix` lo
corre primero. Bloquearlo rompería algo que hoy funciona. El enum sigue sin usarse, a propósito.

### Todavía sin verificar en Windows

Toda esta versión. La causa raíz se estableció leyendo los logs, no reproduciéndola. Y quedan
pendientes de comprobar tres cosas que los logs dejan sospechosas y que **no** se tocaron acá: que el
reporte diga `disco.salud = Healthy` con la sonda de disco en timeout, que concluya «0 hallazgos de
hardware» con la sonda de pantallazos en timeout, y los tres códigos de winget sin verificar.

Para cerrar los códigos hace falta la salida de `winget error --output <archivo>` de un equipo con
winget 1.6 o superior. En winget 1.24 y anteriores ese comando devuelve `INVALID_CL_ARGUMENTS` —es lo
que pasó en el equipo del log—, así que ahí las constantes son la única fuente.

---

## [0.7.0] — 2026-08-29

Barra de progreso real y mensajes en castellano de persona. El defecto que había detrás del síntoma
«la barra se oculta» no era la barra: era que la ventana se congelaba.

### Corregido

- **La ventana se congelaba durante «Mejorar rendimiento».** `TempCleanupFix` y `StartupDisableFix`
  hacían todo su trabajo de forma **síncrona** y devolvían `Task.FromResult`. Como nunca esperaban
  nada, el cuerpo entero corría en el hilo de la interfaz: Windows dejaba de recibir mensajes de esa
  ventana, la atenuaba y la marcaba como «no responde». Eso es lo que se veía como una barra que se
  esconde. Los dos pasan a `Task.Run`.

  El peor de los dos era el de arranque: verificar la firma de cada ejecutable con `X509Chain.Build`
  accede al almacén de certificados una vez por binario y es la operación más lenta de toda la
  herramienta.

- **Las sondas lentas daban timeout en equipos lentos.** En el Pentium G2020 del log del 2026-08-29,
  `disk.physical` y `crash` no alcanzaban a terminar en 5 s y el reporte salía sin tipo de disco y
  sin análisis de pantallazos. Suben a 30 s y 40 s. El resto de las sondas mantiene su tope, que se
  cumple de sobra.

- **`disco.latencia = 0 ms` se mostraba como una medición.** Un cero en ese contador es un contador
  sin inicializar, no un disco infinitamente rápido. Ahora se informa «no medido», que es la verdad.

### Agregado

- **Barra de progreso determinada en las cuatro operaciones largas** — diagnóstico, mejorar
  rendimiento, reparar errores e instalar programas. Muestra paso, porcentaje y la línea de detalle
  de lo que está pasando ahora mismo.

  `FixProgress` y `ProbeProgress` calculan el porcentaje. Dos decisiones que hacen que se sienta
  honesta: un paso sin fracción conocida se dibuja a la mitad —así arranca moviéndose en vez de
  quedarse en cero— y el porcentaje **nunca retrocede**, ni siquiera cuando un fix queda bloqueado o
  se salta. Un paso que no se ejecuta igual avanza el contador, porque una barra quieta se lee como
  una aplicación colgada.

  Las sondas corren en paralelo, así que el avance se cuenta al **terminar** cada una con
  `Interlocked.Increment`, no al empezarlas.

- **Tests del cálculo del porcentaje** (`ProgressTests.cs`): monotonía a lo largo de una corrida
  completa, el rango acotado a 0–100 con un contador fuera de rango, y total en cero sin dividir por
  cero.

### Cambiado

- **Los mensajes de la pantalla se reescribieron para el cliente, no para el técnico.** Ejemplo:
  «Revisando la integridad de los archivos del sistema (puede tardar varios minutos)…» pasa a
  «Revisando si Windows tiene archivos dañados. Esto tarda varios minutos.»

  **El log no cambió**: sigue registrando el comando exacto, el código de salida y la salida cruda.
  La pantalla es para entender qué está pasando; el log es para diagnosticar por qué falló.

### Todavía sin verificar en Windows

Nada de esta versión se probó en un equipo real. El congelamiento se diagnosticó leyendo el código,
no reproduciéndolo.

---

## [0.6.0] — 2026-08-27

Se conecta «Mejorar rendimiento». Los tres componentes que estaban escritos y probados pero sin usar
—50 tests entre ellos— ahora tienen un `IFix` que los invoca.

### Agregado

- **Botón «Mejorar rendimiento»**, separado de «Reparar errores». `FixCategory` divide los fixes en
  dos familias porque son cosas distintas: mejorar tarda minutos, reparar puede tardar una hora.
  Meterlos en un botón obligaría a esperar DISM para limpiar temporales.
- **`TempCleanupFix`** — conecta `JunctionSafeCleaner`. Limpia los temporales del usuario y del
  sistema saltando los archivos de menos de una hora, por si hay un instalador corriendo. Es la única
  acción de esta categoría que **no se puede deshacer**, y se dice así.
- **`StartupDisableFix`** — conecta `StartupClassifier`. Desactiva los programas de arranque que la
  lista blanca autoriza, escribiendo el flag de `StartupApproved`: el mismo mecanismo del
  Administrador de tareas. **No borra la clave `Run`**, así el usuario puede volver a habilitarlo
  desde Windows y el cambio es visible.
- **`StartupEntryReader`** — la pieza que le faltaba al clasificador: lee las claves `Run`, extrae la
  ruta del ejecutable de la línea de comandos, obtiene el publisher del certificado y valida su
  cadena, y consulta `root\SecurityCenter2` para la Capa 1.
- **`PowerPlanFix`** — pasa a Alto rendimiento, guardando el GUID anterior. Requiere aprobación
  porque en un portátil afecta la batería.
- **`DiskOptimizationFix`** — TRIM en SSD, desfragmentación programada en mecánico. **Se niega a
  actuar si no pudo determinar el tipo de disco**: el tratamiento correcto es opuesto en cada caso, y
  desfragmentar un SSD desgasta celdas sin ningún beneficio.
- **Dos `IUndoHandler` nuevos**: flag binario de `StartupApproved` y plan de energía. Sin ellos los
  fixes nuevos habrían registrado pasos de deshacer que nadie podía ejecutar.

### Sobre la validación de firma

`StartupEntryReader` obtiene el certificado del binario y valida su cadena con `X509Chain`. Eso **no
es exactamente la política Authenticode** —para eso haría falta `WinVerifyTrust` por P/Invoke— pero
verifica lo que importa: que el certificado encadene a una raíz de confianza del equipo y no esté
vencido. Un binario sin firma válida cae en Capa 3 y **nunca se desactiva solo**, que es la garantía
que sostiene todo el diseño.

### Estado de las pruebas

| Componente | Estado |
|---|---|
| Los 4 fixes de rendimiento | ❌ **nunca ejecutados** |
| `StartupEntryReader` | ❌ la lectura del registro y de firmas, **sin ejecutar** |
| Botón de deshacer | ❌ agregado en 0.5.0, **sin probar** |
| Chrome por MSI oficial | ❌ **sin probar** |
| RustDesk por MSI | ❌ **sin probar** |

Nada de esta versión se ejecutó todavía. La lógica de decisión está probada; lo que toca Windows, no.

---

## [0.5.0] — 2026-08-27

Se conecta el botón de deshacer. Antes el journal registraba todo y `UndoEngine` funcionaba, pero
**no había forma de invocarlo desde la interfaz** — la red de seguridad existía y era inalcanzable.

### Agregado

- **Botón «Deshacer la última reparación»**, con pantalla de confirmación que lista qué se va a
  revertir **antes** de tocar nada: deshacer también modifica el equipo.
  - Aparece solo si hay una corrida anterior con cambios reversibles.
  - **Funciona entre sesiones.** Los journals viven en `%ProgramData%`, así que se puede volver al
    equipo la semana siguiente y revertir.
  - Distingue lo que se revierte de lo que **no se puede recuperar**, y dice cuántos MB de archivos
    borrados no vuelven.
  - Informa si esa corrida tuvo punto de restauración o no: cambia la decisión del técnico.
- **`JournalStore`**: encuentra y lee corridas anteriores. Una corrida solo con acciones
  irreversibles **no se ofrece** para deshacer — ofrecerlo sería mentir. Una ya deshecha tampoco. Una
  interrumpida por un crash **sí**, que es justo la que más probablemente haya que revertir.
- Marcar una corrida como deshecha **agrega una línea al journal, no lo borra**: el journal es la
  constancia de lo que se le hizo al equipo.

### Corregido

- **Chrome fallaba siempre con `INSTALLER_HASH_MISMATCH`.** El reintento de v0.4.1 se ejecutó y falló
  igual, lo que confirma que no era una descarga cortada: el manifiesto de winget tiene un hash
  desactualizado respecto del instalador que publica Google. Ahora se baja el **MSI empresarial
  oficial** de `dl.google.com`, que se instala con `msiexec /qn`.

### Confirmado de v0.4.1

- **`winget error --output` funcionó: 163 códigos cargados** del winget del equipo. La tabla real
  está en uso, no las constantes.
- El mensaje de hash mismatch se muestra completo; ya no aparece «-» como detalle.
- El reintento automático se ejecuta.

### Todavía sin conectar

`JunctionSafeCleaner` (15 tests), `StartupClassifier` (20 tests) y `ServiceConditionEvaluator`
(15 tests) siguen escritos y probados pero sin ningún `IFix` que los use. No existe «Mejorar
rendimiento»: los temporales nunca se limpian y los programas de inicio se detectan pero no se
desactivan.

---

## [0.4.1] — 2026-08-27

Correcciones de dos logs de equipos distintos: un Latitude E5530 con Windows 10 y un Latitude 3410 con
Windows 11.

### Confirmado que v0.4.0 arregló

- **Punto de restauración verificado**, en 2 intentos de sondeo la primera vez y 1 la segunda. El
  falso negativo que bloqueaba todo está resuelto.
- **DISM con `/English` detectó «íntegro»** y se salteó `RestoreHealth`: ~4 minutos ahorrados por
  corrida, tal como se esperaba.
- **`netsh int ip reset` código 1** ya se registra como informativo.
- **El diagnóstico completo queda en el log.** Los dos equipos se pueden auditar sin capturas.
- **La ruta de winget se re-resuelve antes de cada paquete.**

### Corregido

- **El retraso de arranque daba números absurdos.** Un equipo reportó «814 s de retraso» sobre un
  arranque de 27 s. Dos causas: se sumaba `TotalTime` (el total de cada app) en lugar de
  `DegradationTime` (cuánto retrasó), y se contaban eventos de **todo el historial** en vez de solo el
  último arranque. Ahora se acota a los eventos posteriores al evento 100 más reciente. Si no se puede
  determinar la ventana, no se informa: mejor callar que informar mal.
- **`disk.latency` y `memory.pressure` daban timeout en los dos equipos.** Las clases
  `Win32_PerfFormattedData_*` tienen que inicializar el subsistema de contadores en la primera
  consulta y no alcanzan 5 s. Ahora esas dos sondas tienen 25 s propios.
- **Chrome fallaba con `0x8A150011`** = `INSTALLER_HASH_MISMATCH`: el instalador se descargó dañado.
  Se mapea con su explicación y **se reintenta una vez**, que resuelve la mayoría de los casos.
- **El detalle de un fallo de winget era literalmente «-».** winget dibuja un spinner en stdout y se
  tomaba como la primera línea útil. Ahora se descartan las líneas que son solo animación, sin
  recortar el contenido de las que sí tienen mensaje.
- **El instalador de RustDesk se colgó 40 minutos** y bloqueó la tanda con el timeout de 30 min, que
  está pensado para DISM. Dos cambios: **timeout propio por paquete** (8 min por defecto), y se
  prefiere el **MSI** sobre el EXE, lanzado con `msiexec /qn` — la documentación de RustDesk lo
  recomienda porque su instalación silenciosa por EXE tiene problemas conocidos.
- **`net stop` con código 2 seguía apareciendo como advertencia.** El fix anterior lo manejaba en el
  fix, pero la advertencia la emitía el runner. Ahora quien invoca puede declarar códigos benignos.

### Estado de las pruebas

| Componente | Estado |
|---|---|
| Diagnóstico completo | ✅ verificado en 2 equipos; el resumen coincide con el hardware real |
| `RestorePointService` | ✅ crea y verifica correctamente |
| DISM, `sfc`, `chkdsk`, WU, red | ✅ aplicados en equipo real |
| Instalación con winget | ✅ Adobe, 7-Zip, VCRedist x64 y x86 instalados |
| Descarga directa desde GitHub | ⚠️ resuelve y descarga; el MSI **sin reprobar** |
| Chrome | ⚠️ falló por hash; el reintento **sin reprobar** |
| Retraso de arranque | ⚠️ corregido, **sin reprobar** |
| `BitLockerService` | ❌ ninguno de los equipos tenía BitLocker |
| Análisis de pantallazos | ❌ **sin ejecutar** contra un equipo con pantallazos |

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
