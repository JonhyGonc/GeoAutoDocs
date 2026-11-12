# Dados e persistência

## Pastas

Raiz: `%LOCALAPPDATA%\GeoAutoDocs`. Pode ser trocada pela variável `GEOAUTODOCS_DATA_ROOT`, útil em testes. Os caminhos ficam em `AppDataPaths`.

| Pasta | Conteúdo |
|---|---|
| `database\GeoAutoDocs.db` | Banco SQLite com projetos, relatórios, locais, trechos, vídeos (referências), ocorrências, capturas, evidências e catálogo de anomalias |
| `projects\<pasta>\` | Arquivos gerenciados dos projetos **locais**: `captures\`, `evidence\` |
| `Cache\<projectId>\` | Projetos **compartilhados**: cópia local do `project.geoadocs.json`, `captures\`, `evidence\` |
| `backups\` | Backups automáticos (`GeoAutoDocs_*.zip`) e `status.json` |
| `logs\GeoAutoDocs.log` | Log local |

Ficam **fora** dessas pastas:

- **Vídeos:** o projeto guarda o caminho absoluto, local ou de rede (UNC). Num projeto compartilhado, use caminhos de rede para que todos acessem.
- **Relatórios `.docx`:** são gravados onde o usuário escolhe, com o arquivo `*.geoadocs.json` ao lado.

## Banco SQLite

- A gravação é feita por `ProjectStore` (EF Core 8), sempre pelo **agregado completo** do projeto: itens ausentes do projeto em memória são removidos do banco.
- Os identificadores são GUIDs gerados pelo próprio aplicativo e estáveis entre edições. São os mesmos enviados ao GeoAutoEngine.

### Evolução do esquema

O esquema é criado com `EnsureCreated` e completado por rotinas idempotentes `Ensure*SchemaAsync` (`CREATE TABLE IF NOT EXISTS` / `ALTER TABLE … ADD COLUMN`). A versão fica registrada na tabela `GeoAutoDocsSchemaVersion`, e a constante que a define é `ProjectStore.CurrentSchemaVersion`.

| Versão | Esquema |
|---|---|
| 0 | Banco anterior ao controle de versão. É carimbado automaticamente ao abrir. |
| 1 | Esquema da 1.1.0, ou seja, todas as rotinas `Ensure*` existentes. |

Para mudar o esquema:

1. Crie uma rotina `Ensure*` **idempotente** e apenas aditiva, chamada em `InitializeAsync`.
2. Incremente `CurrentSchemaVersion` e acrescente uma linha na tabela acima.
3. Escreva um teste que parte de um banco da versão anterior e verifica a atualização sem perda de dados.

Se o banco tiver uma versão **maior** que a suportada (por exemplo, depois de voltar para uma build mais antiga), o aplicativo se recusa a abrir e orienta a instalar a versão mais recente.

## Projeto compartilhado

Um projeto pode ser publicado numa pasta de rede (`SharedProjectService`):

- `project.geoadocs.json` é o arquivo **oficial**. As capturas e evidências são replicadas para `captures\` e `evidence\` nessa pasta.
- `lock.json` garante um único editor por vez. O heartbeat é renovado a cada minuto e o lock é considerado abandonado depois de 5 minutos, quando pode ser recuperado com a confirmação do usuário.
- A cada 15 segundos, o aplicativo grava o checkpoint no servidor, com controle de versão otimista (`ProjectDataVersion`).

**Limitação conhecida:** ao abrir um projeto compartilhado, o arquivo oficial prevalece sobre a cópia local. Alterações que não chegaram ao servidor (por queda de rede, conflito ou lock perdido) podem ser descartadas na reabertura. Antes de fechar, confira se o status indica sincronização concluída.

## Backup

`BackupService` gera um backup ao iniciar o aplicativo, se algo mudou desde o último.

- **Conteúdo:** cópia consistente do SQLite + `projects\` + `Cache\`, sem os temporários `*.tmp.*`.
- **Retenção:** os 5 mais recentes.
- **Restauração:** é oferecida na inicialização quando o banco não existe ou não abre. O banco atual é preservado antes (`GeoAutoDocs_pre_restore_*.db`). Backups antigos, sem `Cache\`, continuam restauráveis.
- **Fora do backup:** vídeos, relatórios `.docx` gerados e a pasta de rede dos projetos compartilhados.
