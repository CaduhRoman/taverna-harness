# CLI verificadas em 14/10/2026 (Windows)

Ajuda consultada: `claude --help`, `codex --help`, `codex exec --help`.

## Claude Code — instalado

Execução programada: `claude --print --output-format stream-json --include-partial-messages --verbose --restricted --permission-mode dontAsk -- <prompt>`.

Flags usadas, confirmadas pelo `--help`:

- `--print`: modo não interativo.
- `--output-format stream-json`: stream JSON por linha.
- `--include-partial-messages`: chunks incrementais; exige print/stream-json.
- `--verbose`: saída detalhada.
- `--restricted`: limita ferramentas e arquivos acessíveis aos diretórios de trabalho.
- `--permission-mode dontAsk`: nega operações que pediriam autorização, sem ignorar proteções.

O formato de eventos foi implementado conforme as estruturas públicas de `stream-json`; a execução autenticada ainda precisa de teste funcional.
Tokens apenas quando o evento `result.usage` os reportar. Imagens não são enviadas nesta etapa.

## Codex CLI — instalado

Execução programada: `codex exec --json --sandbox workspace-write -C <workingDirectory> <prompt>`.

Flags usadas, confirmadas pelo `codex exec --help`:

- `exec`: modo não interativo.
- `--json`: eventos JSONL para stdout.
- `--sandbox workspace-write`: restringe escrita ao workspace da CLI.
- `-C`: seleciona diretório de trabalho.

Tokens apenas quando `turn.completed.usage` os reportar. O `--help` também confirma `--image`, mas não é usado neste MVP. Execução autenticada ainda precisa de teste funcional.

## GitHub Copilot CLI — ausente

`copilot` não foi encontrado no PATH. Não foi possível consultar `copilot --help`, validar flags, streaming, permissões, imagens ou tokens. Integração permanece detectável e explicitamente inativa até verificação em ambiente com a CLI instalada.

Nenhuma integração estima tokens nem desativa permissões globais.
