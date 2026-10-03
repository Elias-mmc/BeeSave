# Guía para publicar Minecraft Saves en GitHub

Esta guía publica el **código fuente** en un repositorio y el ejecutable en una **Release** separada. No subas partidas, credenciales ni el ejecutable de 124 MB como parte del commit inicial.

## 1. Revisa el contenido

Desde PowerShell, abre la carpeta del proyecto y revisa el estado:

```powershell
Get-Location
Get-ChildItem -Force
```

El repositorio debe incluir el código, `MinecraftSaves.csproj`, `publish.ps1`, `README.md`, esta guía y los archivos de icono `icono.ico` e `icono.png`.

El `.gitignore` excluye `bin/`, `obj/`, el ejecutable autocontenido y la carpeta local `files/` con apuntes históricos. No agregues mundos reales, tokens, contraseñas ni archivos de autenticación.

## 2. Crea el repositorio remoto

1. En GitHub, pulsa **New repository**.
2. Elige un nombre, por ejemplo `minecraft-saves`.
3. Decide si el repositorio será público o privado.
4. Para evitar conflictos en el primer envío, créalo vacío: no marques las opciones para añadir README, `.gitignore` o licencia.
5. Copia la URL HTTPS del repositorio.

Si el código se hará público, elige y añade una licencia antes de publicarlo. No publiques partidas ni datos personales.

## 3. Inicializa y sube el código

En PowerShell, desde la carpeta del proyecto, reemplaza la URL de ejemplo por la URL que copiaste:

```powershell
git init
git add .
git status
```

Antes de continuar, revisa la lista de `git status`: no debe mostrar el ejecutable, carpetas `bin`/`obj`, datos locales, credenciales ni mundos reales. Si está correcta:

```powershell
git commit -m "Publica Minecraft Saves"
git branch -M main
git remote add origin https://github.com/USUARIO/REPOSITORIO.git
git push -u origin main
```

Git puede pedir iniciar sesión o autorizar la operación en el navegador.

### Si ya existe un repositorio Git

No vuelvas a ejecutar `git init` ni reemplaces el remoto sin revisar su estado:

```powershell
git status
git remote -v
```

Si `origin` ya apunta al repositorio correcto, revisa los cambios, crea el commit y usa `git push`. Si apunta a otro lugar, detente y confirma la URL antes de cambiarlo.

## 4. Genera y publica el ejecutable

En Windows, con .NET 10 SDK instalado, ejecuta:

```powershell
.\publish.ps1
```

El script descarga las herramientas oficiales necesarias y genera:

```text
MinecraftSaves-Windows-x64.exe
```

Comprueba que el archivo existe y su tamaño antes de publicarlo:

```powershell
Get-Item .\MinecraftSaves-Windows-x64.exe | Select-Object Name, Length
```

Para adjuntarlo a una Release:

1. Abre el repositorio en GitHub y entra en **Releases**.
2. Pulsa **Draft a new release**.
3. Crea una etiqueta de versión, por ejemplo `v1.0.0`.
4. Escribe notas breves con los cambios y requisitos.
5. Adjunta `MinecraftSaves-Windows-x64.exe` como archivo binario.
6. Publica la Release y verifica que el archivo se pueda descargar.

No añadas el ejecutable al repositorio con `git add -f`: el `.gitignore` lo excluye intencionalmente para evitar un historial de Git muy pesado.

## 5. Comprobación final

- El README explica cómo instalar, configurar y usar la aplicación.
- El código y los iconos fuente están en el repositorio.
- Las partidas, credenciales y notas locales no están en el repositorio.
- El ejecutable descargable está adjunto a una Release, no en el commit de código.
- La Release indica que la aplicación es para Windows x64 y Minecraft Java Edition.

## Nota sobre licencia

No se debe escoger una licencia por otra persona. Añade un archivo `LICENSE` con la licencia elegida antes de presentar el código como software de código abierto.
