# Minecraft Saves

Aplicación de escritorio para Windows que ayuda a guardar y recuperar mundos de **Minecraft Java Edition** usando un repositorio privado de GitHub. La interfaz está en español.

> Minecraft Saves es un proyecto independiente y no está afiliado, aprobado ni mantenido por Mojang o Microsoft.

## Funciones

- Detecta mundos en la carpeta de guardados de Minecraft Java y permite configurar otra ubicación.
- Pregunta antes de respaldar un mundo nuevo y al terminar una sesión de juego.
- Guarda cambios en GitHub con commits que incluyen el nombre del mundo y la fecha y hora.
- Muestra mundos locales y copias de la nube, con los iconos `icon.png` de cada mundo.
- Restaura copias sin sobrescribir mundos locales.
- Permite quitar una copia del estado actual de la nube. El historial anterior de GitHub y la partida local se conservan.
- Puede permanecer en segundo plano desde la bandeja del sistema de Windows.

Las preferencias y la copia Git local se guardan en `%LOCALAPPDATA%\MinecraftSaves`. La autenticación con GitHub se realiza mediante GitHub CLI.

## Descargar e instalar

Descarga `MinecraftSaves-Windows-x64.exe` desde **Releases**. El ejecutable autocontenido incluye .NET, Git y GitHub CLI; no necesitas instalarlos por separado. Al abrirlo por primera vez, instala la aplicación en el perfil de Windows del usuario, sin permisos de administrador.

1. Abre Minecraft Saves y selecciona **Conectar GitHub** para autorizar tu cuenta.
2. En **Preferencias**, crea o elige el repositorio privado para tus mundos.
3. Cuando se detecte un mundo nuevo, confirma si quieres protegerlo.
4. Al terminar de jugar, decide si guardar los cambios en GitHub.
5. Para recuperar una partida, selecciónala en **Mundos en la nube** y pulsa **Traer este mundo al ordenador**.

Si ya hay un mundo con el mismo nombre en el ordenador, la copia se restaura con un nombre alternativo para no sobrescribirlo. Al quitar una copia de la nube, se oculta del estado actual del repositorio; las versiones históricas permanecen en GitHub.

La guía completa para subir el código y publicar el ejecutable está en [PUBLICAR_EN_GITHUB.md](./PUBLICAR_EN_GITHUB.md).

## Requisitos para desarrollar

- Windows 10 u 11 de 64 bits.
- .NET 10 SDK.
- PowerShell.
- Conexión a Internet para generar el paquete autocontenido: el script descarga GitHub CLI y MinGit oficiales.

## Compilar y ejecutar

Desde PowerShell, en la carpeta del proyecto:

```powershell
dotnet restore .\MinecraftSaves.csproj
dotnet build .\MinecraftSaves.csproj --configuration Release --no-restore
dotnet run --project .\MinecraftSaves.csproj
```

Para generar el ejecutable autocontenido:

```powershell
.\publish.ps1
```

El script produce `MinecraftSaves-Windows-x64.exe` en la carpeta del proyecto. Comprueba la suma SHA-256 de GitHub CLI antes de incluirla en el paquete. El `.gitignore` excluye el ejecutable para mantener el repositorio de código ligero; distribúyelo mediante **GitHub Releases**.

## Seguridad y datos

- Usa un repositorio privado para las copias de tus mundos: los archivos de una partida pueden contener datos personales.
- No incluyas tokens, contraseñas, certificados ni mundos personales en commits o Issues.
- Antes de restaurar, la app evita sobrescribir una carpeta de mundo que ya exista.
- Minecraft Saves excluye `session.lock`, enlaces a otras carpetas y metadatos Git internos al respaldar.
- GitHub rechaza archivos individuales de más de 100 MB en Git normal.

## Estado y límites

- Esta versión es para Minecraft Java Edition en Windows; Bedrock y otros sistemas operativos no están incluidos.
- La carpeta predeterminada de mundos es `%APPDATA%\.minecraft\saves`; launchers con una carpeta distinta requieren configurar **Ubicación**.
- Una copia antigua que solo sea una referencia incompleta no puede restaurarse hasta volver a guardarla desde una copia local íntegra.
- Quitar una copia del estado actual del repositorio no borra sus versiones históricas ni reduce el tamaño del historial.

## Licencia

Este repositorio no incluye todavía una licencia. Antes de hacerlo público, el autor debe elegir y añadir una licencia; sin una, se aplican las restricciones de copyright por defecto.
