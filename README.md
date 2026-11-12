# GeoAutoDocs

Aplicativo desktop para Windows de **gestão de vídeo-inspeção de redes**: organiza projetos, locais e trechos inspecionados, reproduz os vídeos, registra anomalias com capturas e evidências, revisa os dados e gera o relatório técnico em Word (`.docx`).

Faz parte do ecossistema **GeoEcosystem**, junto com o [GeoAutoEngine](https://github.com/JonhyGonc/GeoAutoEngine), plugin do AutoCAD Civil 3D, e o GeoMap. A governança, o roadmap e os contratos entre os projetos ficam no repositório [GeoEcosystem](https://github.com/JonhyGonc/GeoEcosystem).

## Stack

| Item | Tecnologia |
|---|---|
| Aplicativo | .NET 8, WPF (`net8.0-windows`), Windows x64 |
| Persistência local | SQLite via Entity Framework Core 8 |
| Vídeo | LibVLCSharp + LibVLC (inclui AVI) |
| Relatórios | DocumentFormat.OpenXml |
| Testes | xUnit |
| Instalador | Inno Setup 6 |

## Estrutura

```
GeoAutoDocs.sln
src/
  GeoAutoDocs.Core/            Domínio e regras puras (ocorrências, numeração/ordenação de trechos,
                               revisão de relatório, contrato de intercâmbio e correções)
  GeoAutoDocs.Infrastructure/  SQLite (ProjectStore), backup, projeto compartilhado, captura de quadros,
                               geração do Word, cliente do Named Pipe com o GeoAutoEngine
  GeoAutoDocs.App/             Interface WPF (MainWindow e janelas de diálogo)
tests/GeoAutoDocs.Tests/       Testes automatizados (xUnit)
installer/GeoAutoDocs.iss      Script do instalador
MODELO DE RELATORIO.docx       Modelo base do relatório (copiado para Templates/ na saída)
docs/                          Documentação técnica
```

## Compilar e testar

Requisitos: Windows e [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build GeoAutoDocs.sln -c Release
dotnet test GeoAutoDocs.sln -c Release
dotnet run --project src/GeoAutoDocs.App
```

A [CI](.github/workflows/ci.yml) executa restore, build e testes em Release no `windows-latest` a cada pull request e a cada push na `main`.

Para publicar, gerar o instalador e instalar, veja **[DISTRIBUICAO.md](DISTRIBUICAO.md)**.

## Onde ficam os dados

Tudo fica em `%LOCALAPPDATA%\GeoAutoDocs`, ou na pasta indicada pela variável de ambiente `GEOAUTODOCS_DATA_ROOT`. Os vídeos **não** são copiados: o projeto guarda só o caminho deles. Os detalhes estão em [docs/DADOS_E_PERSISTENCIA.md](docs/DADOS_E_PERSISTENCIA.md).

## Integração com o GeoAutoEngine

Não existe banco de dados compartilhado entre os dois aplicativos. A troca acontece por:

1. **Named Pipe local** `GeoAutoEngine.Inspection` (protocolo `GADX` v1, schema `GeoAutoDocs.Inspection` v1). O GeoAutoDocs é o cliente: envia a inspeção e recebe correções para revisar.
2. **Arquivo `*.geoadocs.json`**, gerado ao lado de cada `.docx`, que o GeoAutoEngine pode importar.

Os detalhes estão em [docs/INTEGRACAO_GEOAUTOENGINE.md](docs/INTEGRACAO_GEOAUTOENGINE.md). Mudanças nesse contrato são coordenadas no GeoEcosystem.

## Como contribuir

- Uma mudança por branch e por pull request, com a CI verde antes do merge.
- Mudanças de contrato com o GeoAutoEngine ou de escopo passam pela coordenação do GeoEcosystem.
- Mudanças no esquema do banco seguem o procedimento descrito em [docs/DADOS_E_PERSISTENCIA.md](docs/DADOS_E_PERSISTENCIA.md#evolução-do-esquema).
