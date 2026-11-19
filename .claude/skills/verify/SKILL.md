---
name: verify
description: Verifica EasyFix como lo haría un usuario antes de publicar — build, tests, y recorrido end-to-end de los flujos reales (analizar, instalar, reparar, deshacer) sobre el ViewModel. Usar SIEMPRE antes de un commit que toque comportamiento, y obligatoriamente antes de taggear una versión.
---

# verify — probar EasyFix como usuario, no como compilador

## Por qué existe

Esta skill nació de una serie de fallos que **todos se podían evitar** y que llegaron al equipo de
un cliente:

| Fallo | Por qué no lo detectó nada |
|---|---|
| RustDesk abrió la ventana de Windows Installer y falló con 0x667 | Se le pasaban al `.msi` los argumentos del `.exe`. Compilaba perfecto. |
| Tras ese fallo, «Buscar controladores» dejó de funcionar | Estado de la UI inconsistente. Ningún test tocaba el ViewModel. |
| Chrome se reinstalaba aunque ya estuviera | La detección existía solo en una de las dos rutas de instalación. |
| 7-Zip nunca se instaló | El bootstrap de winget se colgó y bloqueó la tanda entera. |
| La app quedó ilegible | Se adoptó una librería de UI sin poder verla. |

El patrón es siempre el mismo: **`dotnet build` y los tests unitarios pasaban en verde.** Lo que
falta no es más cobertura de unidades — es recorrer el flujo completo como lo recorre el técnico.

## Reglas no negociables

1. **NUNCA modificar una prueba para que pase.** Si una prueba falla, o el código está mal, o la
   prueba describe mal lo esperado — y en ese segundo caso hay que entender POR QUÉ antes de
   tocarla, y dejarlo escrito. Ajustar el assert hasta que pase verde es destruir la única
   herramienta que avisa.
2. **Tres intentos y se para.** Si después de tres intentos de arreglar algo sigue fallando, se
   detiene el trabajo y se le avisa al usuario con: qué falla, qué se intentó en cada intento, y
   cuál es la hipótesis actual. Insistir una cuarta vez es cuando se empiezan a romper otras cosas.
3. **Lo que no se pudo verificar se dice.** Esta Mac compila WPF pero no lo ejecuta. Todo lo que
   toca Windows de verdad (WMI, registro, COM, instaladores, render) queda SIN VERIFICAR, y eso va
   explícito en el commit y en el CHANGELOG. «Compila» nunca es «funciona».

## El procedimiento

### 1. Build y pruebas, en Release

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build EasyFix.sln -c Release    # 0 warnings es el estándar, no una meta
dotnet test EasyFix.sln -c Release --nologo
```

Los 2 omitidos son normales: exigen Windows.

### 2. Recorrido end-to-end como usuario

`tests/EasyFix.App.Tests/` maneja el `MainViewModel` igual que lo maneja una persona: aprieta los
comandos en el orden real y verifica el estado que queda. Es la capa donde vivían los bugs que se
escaparon.

Flujos que tienen que estar cubiertos siempre:

- **Analizar → reporte**: el análisis corre, llena el reporte, y los comandos quedan habilitados.
- **Instalar con un paquete que falla**: el resto de la tanda sigue, y **al terminar la UI queda
  usable** — `IsBusy == false` y todos los comandos con `CanExecute() == true`. Este es el test
  que habría atajado «después del fallo no funciona ningún botón».
- **Cancelar a mitad**: no se reporta como fallo, y la UI queda usable.
- **Una corrida que se aborta**: el remedio (casilla + reintentar) queda alcanzable desde la
  pantalla donde aparece el error.
- **Correr un solo fix** (drivers): no arrastra el reporte completo de otra cosa.

### 3. Preguntas que hay que poder responder antes de publicar

- Si esta operación **falla a la mitad**, ¿la app queda usable? ¿Qué ve el técnico?
- Si tarda **mucho más de lo esperado**, ¿hay progreso visible y un tope de tiempo?
- Si el equipo **ya tiene** lo que estoy por instalar, ¿me doy cuenta antes de hacer el trabajo?
- Lo que le muestro al usuario, ¿es algo que **medí**, o lo estoy deduciendo?

### 4. Antes del commit

El mensaje tiene que explicar **por qué el fallo era posible**, no solo qué se cambió. Un commit
que dice «arregla RustDesk» no evita el próximo; uno que dice «los argumentos del .exe se le
pasaban al .msi, que responde con 1639 y abre su ventana de ayuda» sí.

Y tiene que declarar qué quedó sin verificar en Windows.
