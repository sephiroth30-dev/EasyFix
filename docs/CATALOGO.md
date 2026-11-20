# Catálogo de programas — agregar, cambiar y verificar

Cómo se agrega un programa a «Instalar programas», cómo se verifica su origen, y qué guardas
impiden los errores que no dan la cara hasta estar en la casa de un cliente.

---

## La regla

**Nunca se configura una URL sin pedirla antes.**

Una fuente sacada de memoria o de un blog parece funcionar hasta el día en que un técnico está en
sitio, con el cliente mirando, y la descarga devuelve 404 o —peor— un archivo que no es lo que
dice. Toda fuente de este catálogo se verificó con una petición real, y las de Adobe, VLC y AnyDesk
se contrastaron además contra el manifiesto de winget.

---

## Estado actual — los diez

**Ningún programa depende de winget.** Lo verifica el test
`NingunProgramaDelCatalogoDependeDeWinget`: agregar uno sin origen propio **rompe el build**, no la
visita al cliente.

| Programa | Forma | Origen | Por defecto |
|---|---|---|:-:|
| Google Chrome | URL fija | MSI empresarial de `dl.google.com` | ✓ |
| Adobe Acrobat Reader | índice + plantilla | API de Adobe → `ardownload3.adobe.com` | ✓ |
| 7-Zip | GitHub | `ip7z/7zip` | ✓ |
| Visual C++ Redist x64 | URL fija | `aka.ms/vs/17/release/vc_redist.x64.exe` | ✓ |
| Visual C++ Redist x86 | URL fija | `aka.ms/vs/17/release/vc_redist.x86.exe` | ✓ |
| RustDesk | GitHub | `rustdesk/rustdesk` | ✓ |
| VLC | índice | listado de `get.videolan.org/vlc/last/win64/` | |
| Notepad++ | GitHub | `notepad-plus-plus/notepad-plus-plus` | |
| Firefox | URL fija | `download.mozilla.org` | |
| AnyDesk | URL fija | `download.anydesk.com/AnyDesk.exe` | |

---

## Las tres formas de resolver un instalador

`DirectDownloadInstaller` las prueba en este orden.

### 1 · Release de GitHub

La mejor cuando el programa vive ahí: la API devuelve siempre el release más reciente, así que no
queda ninguna versión clavada envejeciendo.

```json
"direct": {
  "githubRepository": "ip7z/7zip",
  "preferredAssetPattern": "-x64.msi",
  "assetPattern": "-x64.exe",
  "detectDisplayName": "7-Zip",
  "timeoutMinutes": 8
}
```

`preferredAssetPattern` se intenta primero y sirve para **preferir el MSI sobre el EXE**. No es un
detalle estético: el instalador EXE de RustDesk con `--silent-install` se quedó colgado 40 minutos
en un equipo real, y su propia documentación recomienda el MSI, que con `/qn` es determinista.

### 2 · URL fija

Cuando el fabricante publica una URL estable que siempre apunta a la última versión.

```json
"direct": {
  "url": "https://download.anydesk.com/AnyDesk.exe",
  "silentArgs": ["--install", "%ProgramFiles(x86)%\\AnyDesk", "--silent", "--start-with-win"],
  "detectDisplayName": "AnyDesk",
  "timeoutMinutes": 8
}
```

Los `silentArgs` **expanden variables de entorno**. Escribir `C:\Program Files (x86)` a mano rompe
en un Windows en otro idioma o instalado en otra unidad.

### 3 · Índice consultado al vuelo

Para los que no tienen URL fija ni viven en GitHub. Se pide una página o una respuesta JSON, se
saca la versión con una expresión regular, y se arma la URL.

**Caso VLC — listado de directorio, sin plantilla.** Lo capturado es un nombre de archivo que se
resuelve relativo al índice:

```json
"direct": {
  "indexUrl": "https://get.videolan.org/vlc/last/win64/",
  "matchPattern": "vlc-[0-9.]+-win64\\.msi(?![\\w.])",
  "detectDisplayName": "VLC media player",
  "timeoutMinutes": 10
}
```

> **El `(?![\w.])` no es decoración.** El listado real de VideoLAN tiene, al lado de cada
> instalador, un `.msi.asc`, `.msi.md5`, `.msi.sha1` y `.msi.sha256`. Sin esa guarda el patrón
> matchea el `.asc` — 200 bytes de firma PGP que EasyFix ejecutaría como si fueran el instalador.
> Hay un test que lo fija: `ElPatronNoSeQuedaConLaFirmaNiElHash`.

**Caso Adobe — versión en la ruta, con plantilla:**

```json
"direct": {
  "indexUrl": "https://rdc.adobe.io/reader/products?lang=es&site=landing&os=Windows%2010&api_key=dc-get-adobereader-cdn",
  "matchPattern": "\"reader\"\\s*:\\s*\\[.*?\"version\"\\s*:\\s*\"([0-9.]+)\"",
  "urlTemplate": "https://ardownload3.adobe.com/pub/adobe/acrobat/win/AcrobatDC/{vdigits}/AcroRdrDCx64{vdigits}_MUI.exe",
  "silentArgs": ["-sfx_nu", "/sAll", "/rs", "/msi", "EULA_ACCEPT=YES"],
  "detectDisplayName": "Adobe Acrobat",
  "timeoutMinutes": 20
}
```

| Token | Qué pone |
|---|---|
| `{v}` | Lo capturado tal cual — `26.003.22110` |
| `{vdigits}` | Lo capturado sin puntos — `2600322110` |

Adobe escribe la versión con puntos en su API pero su carpeta se llama sin ellos; de ahí los dos
tokens.

Si el patrón tiene **grupo de captura**, se usa el grupo 1; si no, el match completo. El patrón de
Adobe está anclado a la sección `"reader"` a propósito: la misma respuesta trae también la versión
de Acrobat Pro, y sin el ancla un reordenamiento del JSON haría bajar el instalador equivocado.

### Por qué nunca se clava la versión en la configuración

Funciona el día que se escribe y después se pudre sola. Adobe publica una versión por mes: la vieja
termina devolviendo 404 — o, peor, **sigue descargándose e instala un Reader con vulnerabilidades
conocidas**.

### Falla cerrado

Si el origen cambia de formato y el patrón deja de coincidir, **no se inventa ninguna URL ni se baja
lo que haya**. El resultado es «no se encontró un instalador», nombrando el campo del
`appsettings.json` que hay que revisar.

---

## Procedimiento para agregar un programa

### 1 · Encontrar el origen oficial, y confirmarlo

El atajo más confiable es mirar **qué URL usa winget**, que es la que el fabricante publica:

```bash
# Listar las versiones que tiene el manifiesto
curl -s "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/v/VideoLAN/VLC" \
  | python3 -c "import sys,json;print('\n'.join(x['name'] for x in json.load(sys.stdin)))"

# Leer la URL y los argumentos silenciosos de una versión
curl -s "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/v/VideoLAN/VLC/3.0.24/VideoLAN.VLC.installer.yaml" \
  | grep -E "InstallerUrl|InstallerType|Silent|Architecture"
```

De ahí salen tres cosas: la URL, el tipo de instalador y **los argumentos de instalación
silenciosa**. No se inventan.

### 2 · Verificar que responde

```bash
curl -sIL --max-time 30 -o /dev/null \
  -w "%{http_code}  %{url_effective}  len=%header{content-length}\n" "<url>"
```

Mirar las tres cosas: **200**, a dónde redirige de verdad, y que el tamaño tenga sentido. Un
`content-length` de 200 bytes en algo que debería pesar 60 MB es una página de error.

### 3 · Si lleva índice, probar el patrón contra el contenido real

```bash
curl -s --max-time 30 -A 'EasyFix/1.0' '<indexUrl>' -o /tmp/index.txt
python3 -c "
import re
t=open('/tmp/index.txt').read()
m=re.search(r'<matchPattern>', t, re.I|re.S)
print(m.group(1) if m and m.groups() else (m.group(0) if m else 'NO MATCH'))"
```

Hasta que esto imprima lo esperado, el patrón no se configura.

### 4 · Escribir la entrada

```json
{
  "id": "VideoLAN.VLC",
  "name": "VLC",
  "default": false,
  "note": "…",
  "story": "…",
  "brandColor": "#E85E00",
  "brandInitial": "V",
  "direct": { … }
}
```

| Campo | Qué es |
|---|---|
| `id` | ID de winget. Sigue sirviendo para «Actualizar lo instalado» |
| `name` | Lo que ve el técnico |
| `default` | Si viene marcado |
| `note` | Por qué este programa y no otro |
| `story` | Lo que se le muestra al cliente mientras se instala |
| `brandColor` / `brandInitial` | Monograma de respaldo cuando no hay logo |
| `direct` | El origen — alguna de las tres formas |

**`detectDisplayName`** es el nombre a buscar en las claves `Uninstall` para no reinstalar lo que ya
está. Tiene que ser el nombre **tal como aparece en «Aplicaciones instaladas»**, no el del catálogo.
Un valor demasiado corto o genérico haría que EasyFix crea que algo está instalado cuando no lo
está y lo saltee: el fallo sería silencioso y en la dirección peligrosa — el cliente se queda sin el
programa.

> Adobe usa `"Adobe Acrobat"` y no `"Adobe Acrobat Reader"` a propósito: las versiones recientes se
> registran como *Adobe Acrobat (64-bit)*. Que también matchee Acrobat Pro es correcto — si el
> cliente ya tiene Pro, instalarle Reader encima no aporta nada.

### 5 · La historia

Es lo que ve el cliente mientras ese programa se instala. Una instalación de varios minutos con una
barra quieta se siente eterna y deja al técnico sin nada que decir; además justifica la elección del
programa frente al cliente.

**Reglas:** un hecho verificable sobre el origen del programa, **nunca una anécdota inventada**, y
entre **120 y 600 caracteres** — menos no llena el tiempo de espera, más no se lee. Lo verifican
`TodoProgramaTieneSuHistoria` y `LasHistoriasTienenLargoUtil`.

### 6 · Correr las pruebas

```bash
dotnet test -c Release
```

Las guardas del catálogo corren sobre el `appsettings.json` **real**, así que un error de
configuración falla acá y no en el equipo de un cliente.

---

## Las guardas del catálogo

Todas sobre la configuración real, en `WingetServiceTests` y `DirectDownloadResolveTests`:

| Guarda | Qué impide |
|---|---|
| `NingunProgramaDelCatalogoDependeDeWinget` | Que un programa quede sin origen propio |
| `TodaDescargaDirectaSabeComoDetectarSiYaEstaInstalada` | Reinstalar lo que ya está |
| `LosNombresDeDeteccionSonEspecificos` | Un nombre genérico que saltee el programa por error |
| `TodoOrigenConIndiceTieneSuPatron` | `indexUrl` sin patrón: la rama se saltea **en silencio** |
| `TodosLosOrigenesSonHttps` | Que alguien en el camino elija qué `.exe` se corre como administrador |
| `LasPlantillasUsanUnTokenQueExiste` | Un `{version}` en vez de `{v}`: 404 garantizado |
| `TodoProgramaTieneSuHistoria` / `LasHistoriasTienenLargoUtil` | Pantalla vacía justo cuando hay que mirarla |
| `TodoProgramaTieneMonograma` / `LosColoresDeMarcaSonHexValidos` | Que todos los programas se vean iguales |
| `NoHayIdsDuplicadosEnLaConfiguracion` | Instalar dos veces lo mismo |

---

## Errores que ya pasaron — y qué los evita hoy

### RustDesk abrió una ventana y devolvió `0x667`

Los `silentArgs` son los modificadores del instalador **EXE** de cada programa
(RustDesk usa `--silent-install`). Se estaban agregando también a los MSI, y `msiexec` no los
entiende: con un argumento inválido devuelve 1639 (`ERROR_INVALID_COMMAND_LINE`) y, peor, **abre su
ventana de ayuda** — en el equipo de un cliente eso aparece como «el instalador abrió una ventana
rara y falló».

Hoy, para un MSI se usa solo `/i <archivo> /qn /norestart`, que ya es todo el modo silencioso que
hace falta. Si alguna vez hay que pasarle propiedades a un MSI (`PROP=valor`), va un campo nuevo y
explícito, no este.

### Chrome se reinstalaba aunque ya estuviera

La detección de «ya instalado» existía solo en el camino de winget, leyendo su salida. Los paquetes
de descarga directa no pasan por ahí: bajaban el instalador y lo corrían **siempre**. Son varios
minutos y varios MB tirados, y encima el resumen decía «instalado» como si hubiera hecho falta.

Hoy `IsAlreadyInstalled` se comprueba **antes de bajar nada**. Falla hacia instalar: si la lista no
se puede leer, se instala igual — reinstalar cuesta tiempo, saltear por error deja al cliente sin el
programa.

### 7-Zip nunca se instaló

El bootstrap de winget se colgó más de diez minutos y bloqueó la tanda entera. Dos causas: el avance
se mandaba a un sumidero que lo descartaba —así que en pantalla no pasaba nada— y los timeouts
sumaban 23 minutos.

Hoy hay presupuesto total de 6 minutos para el bootstrap, avance real en bytes, y 7-Zip se baja de
GitHub sin depender de winget.

### Las descargas grandes se cortaban a los 10 minutos

`HttpClient.Timeout` cubre **toda** la operación, incluido leer el cuerpo — no solo abrir la
conexión. Adobe Reader pesa unos 800 MB: por debajo de ~11 Mbps se cancelaba sola, y en pantalla se
veía como una cancelación sin motivo.

Hoy son 40 minutos. Cancelar sigue funcionando y los MB se informan cada segundo, así que una
conexión muerta se nota mucho antes.

### Cancelar no cancelaba

`ResetCancellation` **descartaba** el `CancellationTokenSource` anterior sin cancelarlo: al apretar
otro programa, los 207 MB de VLC que ya estaban bajando seguían bajando. Hoy se cancela y después se
descarta.

---

## Programas predeterminados

`DefaultAppsConfigurator` deja Chrome como navegador y Acrobat Reader para los PDF por
`DISM /Online /Import-DefaultAppAssociations`, el mecanismo documentado de Microsoft para imágenes
corporativas y OEM. Funciona en todas las ediciones, incluida Home, sin desactivar nada.

**Su alcance real son los perfiles de usuario creados después.** Para un técnico que prepara un
equipo antes de entregarlo, o que crea el perfil del cliente, es justo lo que hace falta.

**Para el perfil que ya está abierto no hay vía automática.** El valor de `UserChoice` va firmado
con un hash por usuario + equipo que Microsoft no documenta, y desde febrero de 2024 el driver
`UCPD.sys` impide escribir esas claves aunque se tenga el hash. Las herramientas que lo logran igual
necesitan **desactivar ese driver** — pelearse con una protección del sistema, frágil ante cada
actualización grande, y de paso hacer que EasyFix se parezca a lo que un antivirus marca.

Los ProgId (`ChromeHTML`, `AcroExch.Document.DC`) **están sin verificar contra un equipo real**:
salen de la documentación de esos instaladores. Si un ProgId no existe en el equipo, Windows ignora
la asociación en silencio — falla hacia «no pasó nada», no hacia una asociación rota, que es la
dirección correcta para equivocarse.

---

## Qué sigue dependiendo de winget

Solo **«Actualizar lo instalado»** (`winget upgrade --all`), que no tiene sustituto razonable: haría
falta conocer la versión instalada de cada programa y compararla con la publicada, programa por
programa.

Si winget falta, se hace su bootstrap. Si ni eso funciona, se dice — y la instalación de los diez
programas sigue andando igual.

> `--include-unknown` queda **afuera a propósito**: actualiza paquetes cuya versión instalada winget
> no pudo determinar, o sea sin saber desde dónde se parte.
