# Distribuição Windows

Execute na raiz do projeto:

```powershell
dotnet publish src/GeoAutoDocs.App/GeoAutoDocs.App.csproj -p:PublishProfile=Windows-x64
```

A pasta final será `artifacts/GeoAutoDocs-win-x64`. Distribua a pasta completa e execute `GeoAutoDocs.App.exe`.

A publicação é autocontida: inclui .NET 8, LibVLC, SQLite e o modelo de relatório. Não requer SDK ou runtime .NET instalado.

## Instalador

Com Inno Setup 6 instalado, execute:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\GeoAutoDocs.iss
```

O instalador será criado em `artifacts/installer`.
