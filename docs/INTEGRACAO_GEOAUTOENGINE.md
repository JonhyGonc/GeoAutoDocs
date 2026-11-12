# Integração com o GeoAutoEngine

O GeoAutoDocs e o GeoAutoEngine (plugin do Civil 3D) **não** compartilham banco de dados. Eles se integram por dois canais locais e pelos GUIDs do GeoAutoDocs.

> O contrato está definido em código nos **dois** repositórios: `InspectionPipeProtocol.cs` e `InspectionExchange.cs` aqui, e `src/GeoAutoEngine.Application/VideoInspection/*` lá. Qualquer mudança precisa ser coordenada no [GeoEcosystem](https://github.com/JonhyGonc/GeoEcosystem). A versão e a negociação de capacidades do contrato são tratadas no marco M3.

## 1. Named Pipe `GeoAutoEngine.Inspection`

- **Papéis:** o GeoAutoEngine é o servidor; o GeoAutoDocs é o cliente (`NamedPipeInspectionTransport`).
- **Quadro:** cabeçalho de 16 bytes little-endian + payload JSON (`JsonSerializerDefaults.Web`), com no máximo 16 MiB.

| Bytes | Campo |
|---|---|
| 0–3 | `GADX` (ASCII) |
| 4–5 | versão do protocolo (`1`) |
| 6–7 | tipo da mensagem |
| 8–11 | tamanho do payload |
| 12–15 | `requestId` |

| Tipo | Mensagem | Direção |
|---|---|---|
| 1 | `Hello` | GeoAutoDocs → GeoAutoEngine |
| 2 | `HelloAck` | GeoAutoEngine → GeoAutoDocs |
| 3 | `InspectionPush` (documento `GeoAutoDocs.Inspection` v1) | GeoAutoDocs → GeoAutoEngine |
| 4 | `InspectionAck` | GeoAutoEngine → GeoAutoDocs |
| 5 | `Error` | ambos |
| 6 | `CorrectionPush` (correções de ocorrências) | GeoAutoEngine → GeoAutoDocs |
| 7 | `CorrectionAck` (aplicadas/rejeitadas) | GeoAutoDocs → GeoAutoEngine |

- **Fluxo:**
  1. O GeoAutoDocs conecta (timeout de 1,5 s) e faz o handshake `Hello`/`HelloAck`.
  2. Envia a inspeção e espera o `InspectionAck` (5 s).
  3. Mantém a conexão aberta para receber correções e reconecta automaticamente se ela cair.
  4. Cada `CorrectionPush` abre a janela **"Revisar correções do GeoAutoEngine"**. O usuário aplica ou cancela, e o GeoAutoDocs responde com `CorrectionAck`.
- **Atenção:** uma mensagem de tipo desconhecido gera erro de leitura, e o handshake não negocia capacidades. Builds antigas do GeoAutoDocs (1.0.4 da `main` antiga) não conhecem os tipos 6 e 7.

## 2. Arquivo `*.geoadocs.json`

Ao gerar um relatório, o GeoAutoDocs grava ao lado do `.docx` um arquivo com o mesmo documento `GeoAutoDocs.Inspection` v1 (`FileInspectionTransport`). No Civil 3D, o GeoAutoEngine pode importá-lo pelo comando de vídeo-inspeção. É o caminho alternativo quando o pipe não está disponível.

## Identificadores

`ProjectId`, `ReportId`, `LocationId`, `SectionId`, `VideoReferenceId` e `OccurrenceId` são GUIDs gerados pelo GeoAutoDocs. O GeoAutoEngine os usa para associar trechos à geometria no desenho e para devolver correções.

O GeoAutoDocs **não** guarda coordenadas nem handles do CAD. `StartPoint`, `EndPoint` e `Reference` são texto livre.
