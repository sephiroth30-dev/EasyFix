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

## [0.14.0] — 2026-11-19

Rediseño de la interfaz, con un plan previo en vez de a ojo. Más seis correcciones reportadas
desde un equipo real.

### Cambiado — se eliminó la barra lateral

La barra de 248 px ocupaba casi un tercio del ancho con cinco botones, y desde que el análisis
corre solo al abrir (v0.13.0) el de «Analizar» era vestigial. El técnico lo pidió claro: *«el
mini resumen central recuperando el espacio de la izquierda»*.

Se eliminó entera — no se colapsa ni se reduce a iconos. Dibujar iconos a mano en XAML es
exactamente el tipo de cosa que no se puede verificar desde macOS y que ya salió mal una vez.
La navegación pasa a ser contextual: **un** botón principal y el resto como enlaces discretos.

### Cambiado — el inicio es el inventario del equipo

Antes: una pantalla de bienvenida sin información, y el reporte con cuatro secciones donde había
que leer las 12 condiciones para encontrar lo que importaba.

Ahora, al abrir: identidad del equipo, un titular que dice qué pasa (*«Encontramos 3 cosas para
mejorar»*), el botón de la acción que corresponde, y a la derecha **solo lo que necesita
atención**. El detalle completo queda detrás de «Ver todo (12 revisado, 9 bien)».

**Lo que no se pudo medir entra siempre en ese resumen, sin filtrar.** Es lo primero que uno
recortaría para que la pantalla de entrada quede limpia, y es justo lo que no se puede recortar.
Con cosas sin medir el titular nunca dice «está todo bien» — dice cuántas quedaron sin medir.
Hay pruebas que lo fijan.

**Con el disco fallando no se ofrece ninguna acción principal**, igual que ya hacía el motor:
optimizar un disco que se muere es la peor jugada posible. La pantalla respeta la misma compuerta.

### Cambiado — de cinco botones a dos

En «Instalar programas» había cinco botones del mismo peso compitiendo en el momento en que solo
hay una cosa que hacer. Quedan **Volver** e **Instalar**; «Actualizar lo instalado»,
«Predeterminados» y «Diagnosticar winget» pasan a enlaces discretos a la derecha.

### Agregado — monograma animado en la historia

Cada programa muestra un círculo con el color de su marca y su inicial, que entra con una
animación corta al cambiar de programa.

**No es el logo real, y es deliberado:** cuando se cuenta la historia el programa todavía se está
instalando, así que no hay ejecutable del que sacar el icono; y empaquetar logos ajenos traería
binarios de marcas registradas al repo. El color y la inicial salen de `appsettings.json`,
editables sin recompilar, con tests que validan el formato.

### Corregido

- **«Buscar controladores» devolvía el informe de una reparación completa.** Volvía a medir las 12
  condiciones y mostraba el antes/después de cosas que ni se tocaron. Ahora una corrida de un solo
  fix no dispara esa re-medición — que además tardaba de más para nada.
- **Los predeterminados abrían la ventana de Windows sin que nadie la pidiera.** Ahora se
  **comprueba primero** si Chrome ya es el navegador y Acrobat el lector de PDF (leer el registro
  sí se puede; lo que Windows bloquea es escribirlo). Si ya está, no se hace nada ni se abre nada.
  Si falta, se dice en el resumen y queda el enlace «Predeterminados» para cuando el técnico
  decida — no se le interrumpe el trabajo con una ventana.

**Probado:** 612 pruebas (592 Core + 20 App), build Release 0 warnings, `.exe` generado.
**SIN VERIFICAR en Windows:** todo lo visual. Es un rediseño de layout que nadie vio renderizado —
el ancho de la columna de atención a 860 px, el wrapping del titular con nombres de equipo largos,
y la animación del monograma son los puntos más expuestos.

---

## [0.13.0] — 2026-11-19

### Corregido — RustDesk abría una ventana de Windows Installer y fallaba (0x667)

`0x667` = 1639 = `ERROR_INVALID_COMMAND_LINE`. RustDesk declara `silentArgs`
`["--silent-install"]`, que es un modificador de su instalador **.EXE**. Pero el paquete prefiere
el **.MSI**, y esos argumentos se agregaban igual después de `/i <ruta> /qn /norestart`. `msiexec`
recibe algo que no entiende, devuelve 1639 **y abre su ventana de ayuda** — eso es la «ventana
rara» que aparecía. Un solo bug explicaba los dos síntomas.

Ahora los `silentArgs` se aplican **solo a instaladores .EXE**. Para un MSI, `/qn /norestart` ya
es todo el modo silencioso.

### Corregido — «Buscar controladores» parecía no responder

Exigía haber analizado antes y contestaba «Primero hay que analizar el equipo», lo que se lee
como que el botón está roto. **El análisis ahora corre solo al abrir la app**, como un inventario:
es de solo lectura, tarda segundos, y no hay motivo para pedirlo a mano.

**Nota honesta sobre el diagnóstico.** La primera hipótesis fue que `IsBusy` quedaba trabado
(`AnalyzeCommand` es el único comando sin `CanExecute`, y era el único que seguía respondiendo —
encajaba perfecto). Se escribieron 8 pruebas end-to-end para reproducirlo y **todas pasaron**: no
estaba trabado. Se reporta la hipótesis fallida en vez de ocultarla, y no se tocó ninguna prueba
para que diera el resultado esperado.

### Agregado — la capa de pruebas que faltaba

Los cuatro fallos que llegaron a equipos de clientes (RustDesk, Chrome reinstalándose, 7-Zip
bloqueado, botones que parecían muertos) **compilaban limpio y con todos los tests en verde**. El
patrón era siempre el mismo: los tests cubrían cada pieza por separado y nada recorría el flujo
como lo recorre una persona.

- **`tests/EasyFix.App.Tests`** maneja el `MainViewModel` apretando comandos en orden real. La
  invariante que fija: *pase lo que pase, al terminar una operación todos los comandos quedan
  ejecutables*. Cubre instalación que falla, que revienta, sin internet, sin nada marcado, y dos
  operaciones seguidas.
- **No referencia el proyecto WPF**, a propósito: eso arrastra `Microsoft.WindowsDesktop.App`, que
  no existe en macOS, y las pruebas morían al arrancar — habrían corrido solo en el CI de Windows,
  cuando ya es tarde para corregir. Compila los fuentes del ViewModel directamente, que se puede
  porque no tocan WPF. Dos pruebas mantienen esa condición.
- **`.claude/skills/verify`**: el procedimiento antes de publicar, con tres reglas — nunca
  modificar una prueba para que pase, tres intentos y se para avisando, y lo que no se pudo
  verificar se dice.

**Probado:** 598 pruebas (587 Core + 11 App), build Release 0 warnings.
**SIN VERIFICAR en Windows:** que el MSI de RustDesk instale con `/qn`, y que el análisis
automático al abrir no demore la aparición de la ventana.

---

## [0.12.2] — 2026-11-19

Reporte real: «lleva más de 10 minutos instalando winget» y, al revisar, **7-Zip nunca se
instaló**. Un solo problema con tres causas, las tres mías.

### Corregido — se podía colgar 23 minutos sin decir nada

- **El progreso del bootstrap de winget se tiraba a la basura.** Se le pasaba un
  `Progress<string>` que descartaba todo, así que la pantalla decía «Instalando winget…» y después
  silencio: imposible distinguir «bajando» de «colgado». Ahora cada mensaje llega a la pantalla.
- **Las descargas no reportaban avance.** Se informaba el tamaño una sola vez y nada más hasta
  terminar. Ahora hay progreso real por bytes, una vez por segundo: `12 MB de 28 MB (43 %)`.
- **No había tope de tiempo total.** Los timeouts se sumaban —10 min por descarga × 2, más 3 de
  `Add-AppxPackage`— hasta 23 minutos. Ahora el bootstrap entero tiene **6 minutos**: un paquete
  de ~30 MB que no terminó en seis no va a terminar, y es mejor fallar con un motivo claro
  (conexión lenta, firewall bloqueando GitHub) que seguir esperando.

### Corregido — 7-Zip ya no depende de que winget exista

Era el efecto dominó: 7-Zip disparaba el bootstrap, el bootstrap se colgaba, y **la tanda entera
quedaba bloqueada ahí**. Ahora 7-Zip se baja del repositorio oficial de su autor
(`ip7z/7zip` en GitHub, el de Igor Pavlov), igual que Chrome y RustDesk.

El patrón del instalador (`-x64.msi`) se **verificó contra la API real de GitHub**, no se
adivinó: el último release publica `7z2606-x64.msi` y es el único asset que matchea. Se instala
con `msiexec /qn /norestart`, que ya era el camino para MSI.

### Agregado

- `detectDisplayName` en los tres paquetes de descarga directa, con dos tests que lo protegen:
  uno falla si una descarga directa no sabe detectarse (se reinstalaría siempre, el bug de
  v0.12.1), y otro si el nombre es demasiado corto para identificar sin matchear otra cosa —
  ese fallo sería silencioso y en la dirección peligrosa: el cliente se queda sin el programa.

**Probado:** 587 tests (585 + 2 omitidos), build Release 0 warnings. El patrón del asset de 7-Zip
está verificado contra la API; **que el MSI instale bien sigue sin probarse en Windows**.

---

## [0.12.1] — 2026-11-19

### Corregido — Chrome se reinstalaba aunque ya estuviera

Reporte real. La detección de «ya estaba instalado» existía **solo en el camino de winget**,
leyendo su salida. Chrome y RustDesk no pasan por ahí: usan descarga directa del sitio oficial,
y ese camino bajaba el instalador y lo corría **siempre**, incluso en un equipo que ya los
tenía. En la casa de un cliente son varios minutos y varios MB tirados — y encima el resumen
decía «instalado» como si hubiera hecho falta.

Ahora la descarga directa consulta las claves `Uninstall` **antes de bajar nada** y reporta «ya
estaba instalado». Falla hacia instalar: si la lista no se puede leer, instala igual —
reinstalar cuesta tiempo, saltear por error dejaría al cliente sin el programa.

### Corregido — la historia aparecía y desaparecía en un parpadeo

Reporte real: «7-Zip no me contó nada». Tenía historia, pero se borraba apenas el programa
terminaba: uno rápido —o uno que ya estaba instalado— la mostraba una fracción de segundo.
Ahora cada historia **se reemplaza recién cuando arranca la siguiente**, nunca se borra al
terminar, y la última queda en pantalla al final de la tanda.

### Cambiado — la historia es la protagonista

La pantalla de instalación pasó a dos columnas, con la estructura que usan las herramientas del
rubro que funcionan bien:

- **Izquierda, el protagonista.** Antes de instalar: qué va a pasar, en tipografía grande y
  liviana. Durante: el **nombre del programa a 34 px** y su historia a 15 px con interlineado
  generoso, en un ancho de lectura cómodo. El progreso va debajo, fino y discreto — está siempre
  a la vista pero no compite con el texto.
- **Derecha, la lista** con el estado de cada programa.
- Más aire en todo: márgenes de 36 px, 44 px entre columnas.

### Agregado — dos tests para que esto no se degrade

`TodoProgramaTieneSuHistoria` falla si se agrega un programa sin historia (es facilísimo
olvidarla), y `LasHistoriasTienenLargoUtil` exige entre 120 y 600 caracteres: más corta no llena
la espera, más larga no se lee.

**Probado:** 585 tests (583 + 2 omitidos), build Release 0 warnings. Sin verificar en Windows.

---

## [0.12.0] — 2026-11-19

### Corregido — el mensaje mandaba a marcar una casilla invisible

Reporte real: al buscar controladores, no se pudo crear el punto de restauración y la app dijo
«se puede continuar marcando la casilla correspondiente»… pero esa casilla **vivía solo en la
pantalla de Reporte**. Entrando por «Buscar controladores» —que va de Inicio directo a
Reparando— nunca se pasaba por ahí: el remedio era literalmente inalcanzable.

Ahora, cuando una corrida se aborta, la casilla **y un botón «Reintentar»** aparecen en la
pantalla del resultado, donde está el error. El error y su solución, en el mismo lugar.

### Agregado — «Actualizar lo instalado»

`winget install` sobre algo ya presente no actualiza: devuelve «ya estaba instalado» y listo.
Esto es lo otro — `winget upgrade --all`, que es el trabajo manual de ir programa por programa
buscando versiones nuevas.

No instala nada que no esté ya, y **`--include-unknown` queda afuera a propósito**: ese flag
actualiza paquetes cuya versión instalada winget no pudo determinar, o sea sin saber desde qué
versión se parte. Sin el dato, no se toca.

Detalle que importa: **un código de salida 0 NO se traduce como «todo estaba al día»**. Un 0
significa «el comando terminó bien», y eso cubre tanto «no había nada» como «actualicé cuatro
programas». Solo se afirma que estaba al día si winget lo dice por texto. Es la misma regla de
v0.5.0 aplicada a un caso nuevo, y tiene tests.

### Cambiado — pase minimalista

- **Barra lateral sin recuadros.** Cinco botones con borde apilados en una columna angosta son
  cinco líneas de ruido; el hover ya comunica que son clickeables y el espacio los separa.
- **Fuera las barras de acento decorativas** que reemplazaron a los iconos en v0.10.1: cinco
  marcas idénticas no distinguen nada, solo llenan.
- **Los paneles de estado perdieron el marco permanente.** El fondo propio ya los separa. El
  borde aparece solo cuando hay una advertencia — así deja de ser decoración y significa algo.

**Probado:** 583 tests (581 + 2 omitidos), build Release 0 warnings. Sin verificar en Windows.

---

## [0.11.1] — 2026-11-19

Reporte de uso: «no me dejó predeterminado Chrome después de la instalación aunque la cancelé».
Eran dos fallos distintos, los dos míos.

### Corregido

- **Cancelar la tanda se saltaba los predeterminados por completo.** El paso vivía después de
  `InstallAsync` dentro del mismo `try`, así que al cancelar la excepción lo salteaba — y lo que
  ya se había instalado quedaba instalado pero sin asociar. Ahora los resultados se acumulan
  paquete por paquete y los predeterminados se aplican igual a lo que alcanzó a instalarse:
  cancelar corta la tanda, no deshace lo hecho.
- **El paso no se notaba aunque funcionara.** DISM escribe las asociaciones solo para los
  perfiles de usuario que se creen después, así que en el perfil abierto —el caso real del
  técnico— no cambiaba nada visible. Ahora, además del DISM, se **abre Configuración directo en
  la página del programa** (`registeredAppUser` en Windows 11) para que confirmarlo sea un clic
  en vez de navegar el menú. Sigue sin haber vía totalmente automática para el perfil actual:
  Windows lo bloquea con el hash de `UserChoice` y el driver `UCPD.sys`, y saltearlo exige
  desactivar ese driver — el «Tier C» del plan.

### Sobre los programas que ya están instalados

Vale dejarlo escrito porque es una duda razonable: **winget los reporta como «ya estaba
instalado», no como error.** El resumen los cuenta aparte (`N ya estaba(n) instalado(s)`),
`PackageAvailable` es `true`, y por lo tanto **sí entran en la configuración de predeterminados**
— que es lo correcto para un equipo que ya tenía Chrome. Lo que EasyFix **no** hace es
actualizarlos: `winget install` sobre algo ya presente no actualiza nada. Si hiciera falta,
sería otra acción distinta y explícita.

**Probado:** 573 tests (571 + 2 omitidos), build Release 0 warnings. Sin verificar en Windows.

---

## [0.11.0] — 2026-11-19

Cinco mejoras de interfaz que salieron de usar la app de verdad.

### Corregido — había que scrollear para ver el progreso

La barra de progreso estaba **dentro** del mismo scroll que la lista de programas: con varios
marcados, había que bajar para ver justo lo que uno mira mientras espera. Ahora la lista es lo
único que scrollea y el progreso vive fijo abajo.

### Agregado

- **Pantalla completa.** La ventana es redimensionable (mínimo 860×560), con botón de maximizar y
  doble clic en la barra de título. Maximizar respeta el área de trabajo: con `WindowStyle="None"`
  Windows no lo hace solo y la ventana taparía la barra de tareas.
- **Tema claro y oscuro automático**, según el «Modo de aplicación» de Windows
  (`AppsUseLightTheme`). Los colores se mudaron de `Theme.xaml` a `Palette.Dark.xaml` /
  `Palette.Light.xaml`, que definen las mismas claves; los estilos las consumen por nombre y no
  saben cuál está cargada. La paleta clara no es la oscura invertida: el texto y el acento se
  eligieron con contraste suficiente sobre fondo claro (AA de WCAG para texto normal). **Si la
  lectura del registro falla, queda el tema oscuro** — el conocido, el que se vio funcionando.
- **Historia de cada programa mientras se instala.** Una instalación de varios minutos con una
  barra quieta se siente eterna y deja al técnico sin nada que decirle al cliente que mira la
  pantalla. Cada paquete tiene un texto en `appsettings.json` (campo `story`, editable sin
  recompilar) que cuenta algo real del programa **y por qué se eligió ese y no otro** — eso
  último es lo que justifica el trabajo frente al cliente. Son hechos verificables: inventar
  anécdotas sería el mismo pecado que el proyecto persigue en los diagnósticos.
- **Marcar y desmarcar programas mientras la tanda corre.** La lista ya no se bloquea al instalar:
  lo que marques se suma a la cola en curso, y lo que desmarques se cae si todavía no arrancó. El
  paquete en curso no se puede sacar — ya está corriendo.

### Agregado — una verificación que sí se puede hacer sin ver la pantalla

`PaletteParityTests` comprueba que las dos paletas declaren exactamente las mismas claves, y que
`Theme.xaml` no tenga ningún color literal. Una clave faltante en una sola paleta dejaría algo
ilegible **solo** en ese tema, en el equipo del cliente, y nada más lo detectaría: compila igual y
desde macOS no se puede ver renderizado. Es la lección de v0.9.5 convertida en test.

**Probado:** 573 tests (571 + 2 omitidos), build Release 0 warnings, JSON válido. **Nada de esto se
vio renderizado** — misma limitación de siempre. El tema claro es lo más expuesto: es una paleta
nueva que nadie miró todavía.

---

## [0.10.1] — 2026-11-19

Tres cosas que salieron de probar v0.10.0 en un equipo real.

### Corregido — la app estaba ilegible

v0.9.5 agregó WPF-UI y llamó a `ApplicationThemeManager.Apply(this)`. Sin un tema registrado en
`App.xaml`, esa llamada aplica el tema **claro** de la librería: estilos implícitos de texto oscuro
encima del fondo oscuro propio. Resultado en pantalla: no se leía nada.

**Se quitó WPF-UI por completo.** El layout horizontal con barra lateral y dos columnas —que era lo
que de verdad hacía falta— es XAML propio y se mantiene intacto; lo que se fue es la librería. Las
esquinas redondeadas eran cosmética y no valían el riesgo, y los iconos se reemplazaron por barras
de acento simples.

La lección quedó anotada en el `.csproj` para que no se repita: **una dependencia visual no se
puede adoptar sin ver el resultado**, y desde macOS no se puede ejecutar WPF. Este fue el costo de
intentarlo igual.

### Agregado — «Buscar controladores» tiene su propio botón

Los drivers estaban implementados pero enterrados dentro de «Reparar errores», que tarda hasta una
hora con DISM. Ahora hay una acción propia en la barra lateral que corre **solo** ese fix — por el
mismo `FixRunner`, con las mismas compuertas de seguridad, sin duplicar nada.

### Agregado — predeterminados automáticos, por la vía oficial

Chrome como navegador y Acrobat Reader para los PDF se configuran **solos** al terminar de
instalar, sin preguntar, vía `DISM /Online /Import-DefaultAppAssociations` — el mecanismo
documentado de Microsoft para imágenes corporativas y OEM, que funciona en todas las ediciones
(incluida Home) sin desactivar nada.

**El alcance real, dicho sin vueltas:** aplica a los perfiles de usuario que se creen **después**.
Para el perfil que ya está abierto no hay vía automática — el valor va firmado con un hash no
documentado y el driver `UCPD.sys` bloquea la escritura desde febrero de 2024. Las herramientas que
lo logran igual desactivan ese driver, que es justo lo que «Tier C» prohíbe. Para ese caso queda el
botón «Predeterminados», que abre la pantalla exacta de Windows.

Los ProgId (`ChromeHTML`, `AcroExch.Document.DC`) están **SIN VERIFICAR** contra un registro real.
Si un ProgId no existe en el equipo, Windows ignora esa asociación en silencio — falla hacia «no
pasó nada», no hacia una asociación rota.

**Probado:** 570 tests (568 + 2 omitidos), build Release 0 warnings. El contraste corregido y el
botón de drivers **no se vieron renderizados** — misma limitación de siempre.

---

## [0.10.0] — 2026-11-19

Controladores, cancelar la instalación, y el atajo a los predeterminados de Windows.

### Agregado — búsqueda e instalación de controladores

Pedido del técnico. El plan tenía «instalar drivers automáticamente» en **Tier C — NO construir**
(«driver equivocado = equipo muerto»), así que se construyó con tres niveles de riesgo en vez de
saltearse esa barrera:

| Clase | Qué incluye | Qué hace EasyFix |
|---|---|---|
| `Safe` | Red, video, audio, USB, Bluetooth, impresora, entrada | **Se instala solo** |
| `NeedsConfirmation` | Almacenamiento (SCSI/RAID/HDC) y **todo lo que no se pudo clasificar** | Se informa con nombre; nunca se toca |
| `Firmware` | Firmware, BIOS, UEFI | Nunca se ofrece. Se nombra y se manda al fabricante |

- **El catálogo es el de Windows Update** (`Microsoft.Update.Session` por COM, sin NuGet nuevo) —
  la misma fuente que «Buscar automáticamente» del Administrador de dispositivos, ya validada y
  firmada para el hardware exacto de ese equipo. Es lo contrario de los «actualizadores de
  drivers» que el propio `appsettings.json` lista como PUP: esos adivinan, esto usa la fuente
  oficial. Se fuerza el catálogo público (`ServerSelection = 2`) para no consultar el WSUS
  corporativo de un equipo de dominio.
- **`DriverRiskClassifier` es una lista blanca, no negra**: solo las clases conocidas de bajo
  impacto salen `Safe`; lo que no matchea —incluido un título con formato inesperado— cae en
  `NeedsConfirmation`. La asimetría es deliberada: un falso negativo molesta, un falso positivo
  instala el driver equivocado solo.
- **Primer uso real de `FixBlockReason.NoNetwork`**, que existía desde v0.8.0 sin consumidores.

### Corregido

- **Instalar programas no se podía cancelar — nunca.** Se pasaba `CancellationToken.None`: era la
  única operación larga de la app sin salida, y la que más tarda. Ahora usa el token real y el
  botón «Instalar» se convierte en «Cancelar» mientras corre. Cancelar no se reporta como fallo:
  lo ya instalado quedó instalado, y el mensaje lo dice.

### Agregado — predeterminados

- **Botón «Predeterminados»** que abre `ms-settings:defaultapps` con la instrucción de qué marcar.
  **Windows no permite que un programa ponga el navegador o el lector de PDF por su cuenta**: el
  valor va firmado con un hash no documentado y, desde febrero de 2024, el driver `UCPD.sys` impide
  escribir esas claves aunque se tenga el hash. Las herramientas que igual lo logran necesitan
  desactivar ese driver — justo lo que «Tier C» prohíbe. Se hace lo honesto: llevar al lugar exacto
  en dos clics en vez de prometer una automatización que el sistema bloquea.

**Probado:** 570 tests (568 + 2 omitidos), build Release 0 warnings. **El catálogo de drivers nunca
corrió contra Windows Update real**: usa COM tardío (`dynamic`), así que ni el compilador valida
que los miembros de la API existan — es el código menos verificable del proyecto, y está dicho en
la remark de la clase. Toda la lógica de decisión (qué instalar, qué informar, qué no tocar) sí
está testeada, porque vive detrás de `IDriverCatalog`.

---

## [0.9.5] — 2026-11-18

Rediseño de la interfaz: horizontal con barra lateral, en vez del panel vertical único donde todo
quedaba apretado en una sola columna angosta.

### Agregado

- **`ui:FluentWindow`** de WPF-UI (`lepoco/wpfui` en GitHub, MIT, ~9800 estrellas) en vez de
  `Window` — único uso real de la librería: esquinas redondeadas nativas de Windows 11
  (`WindowCornerPreference`). No se tocó Mica/Acrylic: exige fondo translúcido y no hay forma de
  verificar el resultado sin verlo renderizado.
- **Ventana 1000×640** con **barra lateral persistente** (248 px): identidad del equipo (antes se
  repetía arriba de Inicio y de Reporte) + las 4 acciones como navegación + «Deshacer» al pie. La
  pantalla de Inicio deja los 4 botones grandes, que ahora viven solo en la barra.
- **Reporte y Resultado de la reparación en dos columnas** — el cambio que ataca directamente lo
  apretado: izquierda lo medible/arreglable, derecha lo que necesita hardware y lo no determinado.
- **Iconos funcionales** (`ui:SymbolIcon`) en la barra lateral — la única excepción a «cero iconos
  decorativos», documentada: ayudan a reconocer las 4 acciones de un vistazo en una lista angosta.
- Todos los estilos existentes (`ActionButton`, `PrimaryButton`, `AppCheckBox`,
  `ProgressBarStyle`…) se mantuvieron intactos; se agregó un estilo nuevo (`SidebarNavButton`) y
  se reorganizó el layout alrededor de ellos, en vez de reescribir lo que ya estaba probado.

**Probado:** build Release 0 warnings — valida XAML y C#, incluidos los nombres de símbolo de
WPF-UI (son un enum: un nombre inválido no compila). 531 tests de `EasyFix.Core` sin cambios (la UI
no tiene tests, nunca los tuvo). **Lo que esto NO prueba: cómo se ve.** No hay forma de renderizar
WPF desde macOS — es la primera vez en todo el proyecto que se entrega una pantalla sin haberla
visto ni una vez. Ver Nivel 0.5 de `docs/PRUEBAS.md`, el primer chequeo a hacer en Windows real.

---

## [0.9.4] — 2026-11-18

Revisión adversarial del bootstrap de winget de v0.9.3 (el cambio de mayor riesgo hasta ahora:
instala software real sin confirmación, invoca PowerShell). Dos hallazgos importantes, corregidos
antes de que llegaran a un equipo de cliente.

### Corregido

- **Una excepción de `Add-AppxPackage` podía abortar toda la tanda.** Si `powershell.exe` no
  llegaba a correr (ej. bloqueado por AppLocker/WDAC/EDR en un equipo corporativo gestionado —
  el escenario más plausible), la excepción escapaba sin capturar y el técnico se quedaba sin
  instalar *ningún* programa de la tanda por un fallo que solo afectaba a winget. Ahora se trata
  igual que cualquier otro paquete que falla: ese paquete reporta `WingetMissing`, el resto sigue.
- **La instalación automática de winget era invisible en pantalla.** Cuando funcionaba, no había
  ningún indicio en el reporte de que EasyFix instaló algo además de lo pedido — la fila del
  paquete exitoso no muestra detalle (el motivo solo se ve ante un fallo). `WingetService` expone
  ahora `LastBootstrapNote`, y el resumen final en pantalla lo antepone: el técnico sabe que pasó,
  no solo el log.
- **Se sacó un heurístico sin fuente.** `BuildFailureDetail` adivinaba «falta Microsoft.UI.Xaml»
  también si el texto de error contenía «9007» — un número sin ningún HRESULT real de Appx que lo
  respalde, ni una sola verificación en un equipo real. Queda solo el match por el nombre real de
  la dependencia, que Windows sí imprime.
- **El chequeo de HTTPS no cubría un downgrade por redirect.** Validaba el esquema de la URL de
  entrada, pero `HttpClient` sigue redirects solo — si algún salto intermedio bajara a HTTP, no se
  detectaba. Ahora se valida el esquema de la URL *final* de la cadena de redirects.

### Agregado

- **Tests directos de `WingetBootstrapper`** (antes solo se probaba indirecto, a través de un
  doble, dentro de `WingetServiceTests`): 13 tests nuevos con dobles de descarga/proceso/
  localización — incluido uno que fuerza una comilla simple en una ruta para confirmar que el
  escaping de PowerShell la duplica en vez de romper el script.

**Probado:** 531 tests (529 + 2 omitidos). Build Release, 0 warnings.

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
