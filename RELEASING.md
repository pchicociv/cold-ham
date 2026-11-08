# Versiones y actualizaciones de Cold Ham

Cold Ham usa Velopack 1.2.161 para instalar y actualizar la aplicación Windows x64. La dependencia NuGet y la herramienta local `vpk` están fijadas a la misma versión. El origen estable es `https://github.com/pchicociv/cold-ham`, con paquetes del canal `win`. El repositorio y sus Releases deben ser públicos; no se distribuyen tokens en la aplicación.

## Crear una entrega local

Requisitos: Windows, SDK .NET 10 y conexión para restaurar las dependencias y herramientas.

```powershell
./scripts/Build-Release.ps1 -Version 1.0.0
```

El script compila la solución, ejecuta las pruebas, publica una aplicación autónoma y crea los paquetes en una carpeta nueva bajo `bin/release/`. Muestra la ruta exacta al terminar. No instala, publica ni cambia el archivo hosts o las políticas del equipo.

Los archivos de salida incluyen `ColdHam-win-Setup.exe`, el paquete completo `.nupkg`, el ZIP portátil y `releases.win.json`. Distribuir preferentemente el instalador. El perfil anterior `Portable-win-x64` sigue disponible, pero su ejecutable suelto no dispone de instalación administrada y muestra que las actualizaciones no están disponibles.

### Idioma de instalación

`ColdHam-win-Setup.exe` abre un selector **Español / English** antes de instalar. La ventana cambia al idioma elegido y permite cancelar sin instalar ni guardar preferencias. Si Cold Ham está abierto, pide salir desde la bandeja antes de reintentar. Durante la copia se deshabilitan los controles y el cierre para no interrumpir el instalador interno.

`ColdHam.Setup` es una ventana WPF autónoma de .NET 10, sin nuevas dependencias NuGet. El script incrusta el instalador original de Velopack y sustituye únicamente el EXE de instalación en los archivos de entrega. Mantiene `.nupkg`, ZIP portátil y manifiestos de actualización; las actualizaciones automáticas no pasan por el selector. El ejecutable de instalación es mayor porque incorpora su propio runtime de escritorio. Compilar la solución permite revisar la ventana; publicar un instalador utilizable exige `Build-Release.ps1`, que aporta el paquete incrustado.

Tras instalar correctamente para el usuario actual, se guarda `%LOCALAPPDATA%/ColdHam/installation-language.txt` y se abre la aplicación. Cold Ham consume esa preferencia al cargar la configuración, guarda `Language` mediante el almacén habitual y elimina la preferencia solo después del guardado. Conserva reglas, sesiones, horario y recuperación de archivos dañados; un fallo de guardado deja la elección pendiente para reintentar. Una actualización posterior conserva el idioma que se haya elegido dentro de la aplicación.

El EXE distribuido ofrece instalación interactiva; no expone los parámetros de línea de comandos del instalador interno. Los errores propios de Windows pueden aparecer en el idioma del sistema. La implementación utiliza las opciones documentadas de [Setup.exe](https://docs.velopack.io/reference/cli/content/setup-windows) y [Update.exe](https://docs.velopack.io/reference/cli/content/update-windows).

Para usar otro repositorio público, pasar `-ReleaseRepository propietario/repositorio`. El destino se incorpora al ensamblado mediante `UpdateRepositoryUrl`; no es una preferencia editable del usuario. El workflow de este repositorio publica en `pchicociv/cold-ham`.

## Publicar en GitHub

1. Subir los cambios revisados, incluido `.github/workflows/release.yml`.
2. Crear y subir una etiqueta estable nueva, por ejemplo `v1.0.0` para la primera entrega o `v1.0.1` para la siguiente.
3. El workflow compila, prueba y empaqueta antes de crear la Release y subir los archivos con `GITHUB_TOKEN`. Requiere que Actions esté habilitado y que la política del repositorio permita `contents: write` para este job. Las etiquetas deben ser únicas y crecientes; no reutilizar una versión publicada.
4. Instalar `ColdHam-win-Setup.exe` una vez. Las versiones antiguas que no contienen el actualizador requieren este paso manual.

`workflow_dispatch` acepta una versión y solo genera un artefacto descargable de Actions, sin publicar una Release. Las Releases en borrador y las preversiones no llegan a los clientes del canal estable. Se publican paquetes completos; generar deltas no es necesario para funcionar.

## Comportamiento de la aplicación

- Comprueba al arrancar y cada seis horas mientras esté abierta. Descarga y verifica el paquete con Velopack. Un fallo de red o descarga no detiene la vigilancia: informa en Ayuda y reintenta tras treinta minutos; también admite reintento manual.
- Ayuda muestra la versión, estado, «Buscar actualizaciones» y «Actualizar y reiniciar», en español e inglés.
- La instalación automática espera a que la ventana esté oculta en la bandeja. La manual permite reiniciar desde la ventana. Ambas requieren ausencia de sesiones y horarios activos, retirada confirmada de reglas web, ausencia de errores/recuperación pendiente y de operaciones o campos sin guardar.
- No reinicia durante los cinco minutos anteriores al siguiente horario. Este margen reduce el riesgo de coincidir con una activación; no garantiza la duración de una instalación en un equipo lento. La vigilancia sigue siendo interna al proceso, sin servicio.
- Se desactiva la aplicación automática de paquetes durante el arranque de Velopack. Cold Ham carga y evalúa primero su configuración, incluso si quedó una descarga de una ejecución anterior.
- Después de descargar, la política se vuelve a evaluar inmediatamente antes de iniciar el actualizador. Solo entonces se cierra la instancia y se libera su mutex. Un fallo al arrancar el actualizador conserva la aplicación abierta y permite reintentar.
- Los ajustes, idioma, reglas y diario de recuperación permanecen en `%LocalAppData%/ColdHam`, separados de los binarios reemplazables. No se cambia el formato de configuración en esta entrega.

## Integridad, firma y recuperación

Las consultas y descargas de GitHub usan HTTPS. Velopack verifica los hashes del paquete frente a los metadatos de la Release. Esto comprueba integridad; no equivale a una firma de editor independiente del canal de publicación.

Los paquetes se generan sin firma Authenticode mientras no se configure un certificado de firma. Antes de distribuir a terceros, configurar firma mediante las opciones `--signParams` o `--azureTrustedSignFile` de `vpk pack`, almacenando las credenciales fuera del repositorio. La firma no está simulada ni se incorpora un certificado de prueba.

Las descargas incompletas o rechazadas no habilitan el reinicio. Para una versión defectuosa ya distribuida, publicar una corrección con número superior; no activar descensos automáticos. Antes de una migración futura del formato JSON, probar también su compatibilidad y recuperación. La copia de seguridad existente no permite por sí sola abrir un formato nuevo desde un binario antiguo.

## Validación antes de la primera distribución

Las pruebas automáticas cubren el coordinador con un origen simulado, la política de reinicio, horarios diurnos/nocturnos, sesiones, errores, cancelaciones, exclusión de comprobaciones simultáneas y reintentos. No descargan ni instalan software ni tocan bloqueos reales.

En una máquina de prueba, instalar la primera versión y publicar una segunda. Comprobar detección desde GitHub, descarga, reinicio, conservación de reglas/idioma, fallo de red, funcionamiento desde una instancia elevada y aplazamiento durante sesiones y horarios. Confirmar también el comportamiento tras interrumpir una instalación. El empaquetado y los tests de dominio no sustituyen esta prueba completa entre dos versiones publicadas.

Documentación: [integración WPF](https://docs.velopack.io/getting-started/csharp), [publicación en GitHub](https://docs.velopack.io/distributing/self-hosting), [API de actualización](https://docs.velopack.io/reference/cs/Velopack/UpdateManager).
