# Distribuição Windows

Versão atual: **1.1.0**. O número fica em dois lugares, que devem andar juntos:

- `src/GeoAutoDocs.App/GeoAutoDocs.App.csproj` (`<Version>`);
- `installer/GeoAutoDocs.iss` (`#define AppVersion`).

## Pré-requisitos (só na máquina que gera o instalador)

- Windows 10/11 x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --list-sdks` deve listar uma versão 8.x).
- [Git](https://git-scm.com/download/win).
- [Inno Setup 6](https://jrsoftware.org/isdl.php).

Quem só vai instalar o GeoAutoDocs não precisa de nenhum desses itens: a publicação é autocontida e já inclui .NET 8, LibVLC, SQLite e o modelo de relatório.

## 1. Obter o código da versão

```powershell
git clone https://github.com/JonhyGonc/GeoAutoDocs.git
cd GeoAutoDocs
git checkout main
git pull
```

Para gerar uma versão já marcada, troque `main` pela tag, por exemplo `git checkout v1.1.0`.

## 2. Conferir build e testes (recomendado)

```powershell
dotnet test GeoAutoDocs.sln -c Release
```

Todos os testes devem passar. A CI do GitHub executa o mesmo comando em cada pull request.

## 3. Publicar o aplicativo

```powershell
dotnet publish src/GeoAutoDocs.App/GeoAutoDocs.App.csproj -p:PublishProfile=Windows-x64
```

A pasta final será `artifacts/GeoAutoDocs-win-x64`. Ela pode ser distribuída inteira e executada por `GeoAutoDocs.App.exe`, sem instalador.

## 4. Gerar o instalador

Execute o comando que corresponder ao local onde o Inno Setup foi instalado:

```powershell
# Instalação do Inno Setup só para o usuário
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\GeoAutoDocs.iss

# Instalação do Inno Setup para todos os usuários
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\GeoAutoDocs.iss
```

O instalador será criado em `artifacts/installer/GeoAutoDocs-Setup-<versão>-x64.exe`.

## 5. Instalar ou atualizar

1. Feche o GeoAutoDocs.
2. Por segurança, copie a pasta `%LOCALAPPDATA%\GeoAutoDocs` (banco, capturas, cache e backups) para outro local.
3. Execute `GeoAutoDocs-Setup-<versão>-x64.exe`. Não precisa de administrador: o aplicativo é instalado em `%LOCALAPPDATA%\Programs\GeoAutoDocs`.
4. A atualização usa o mesmo `AppId`, por isso substitui a versão anterior no lugar. Os dados em `%LOCALAPPDATA%\GeoAutoDocs` não são apagados.
5. Para conferir a versão, abra *Configurações › Aplicativos › Aplicativos instalados* e procure "GeoAutoDocs". Também dá para ver em *Propriedades › Detalhes* de `GeoAutoDocs.App.exe`.

## Lançamento de uma versão

1. PR alterando a versão nos dois arquivos acima.
2. Merge com CI verde.
3. Geração do instalador (passos 1–4) e homologação.
4. Só depois da homologação, criar a tag `vX.Y.Z` no commit homologado.
