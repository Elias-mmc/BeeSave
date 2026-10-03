# Contribuir

Gracias por tu interés en Minecraft Saves. Los cambios deben conservar las partidas locales y evitar sobrescribir mundos durante una restauración.

## Preparar el entorno

- Windows 10 u 11 x64.
- .NET 10 SDK.
- PowerShell.

Restaura, compila y ejecuta:

```powershell
dotnet restore .\MinecraftSaves.csproj
dotnet build .\MinecraftSaves.csproj --configuration Release --no-restore
dotnet run --project .\MinecraftSaves.csproj
```

## Antes de abrir un pull request

1. Mantén los cambios enfocados y sigue los patrones existentes del proyecto.
2. Compila el proyecto y prueba manualmente los flujos afectados.
3. No incluyas mundos, tokens, credenciales, certificados ni archivos generados.
4. Describe qué cambió y cómo lo verificaste.
5. Si el cambio afecta el uso de la app, actualiza el README.

No hay todavía una suite automatizada de pruebas configurada; indica con claridad qué validaciones ejecutaste.
