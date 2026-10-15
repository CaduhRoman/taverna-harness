# Chat: Tasks e permissões

O Chat fica permanentemente montado no painel direito. Tasks reutiliza a mesma
`TasksPanelView`, `TasksViewModel` e instância do orquestrador em uma sobreposição
local. Fechar o popover não cancela tarefas, descarta o formulário nem interrompe logs.
Os dois popovers compartilham posicionamento limitado ao painel, rolagem, clique
externo e Esc; nenhuma janela nativa é criada para eles.

## Modos realmente disponíveis

| Runner | Manual | Auto | Plan |
| --- | --- | --- | --- |
| Claude | Indisponível | Comandos e edição | Indisponível |
| Codex | Indisponível | Comandos e edição, política `workspace-write` | Indisponível |
| Copilot | Indisponível | Comandos e edição | Indisponível |
| API externa | Indisponível | Indisponível | Apenas texto, sem ferramentas locais |

Manual depende de um canal bidirecional de aprovação que os runners atuais não
implementam. Não é substituído por aprovação automática nem por negação silenciosa.
Plan nos CLIs depende de garantir ausência de efeitos colaterais em todos os
caminhos configurados, incluindo ferramentas externas, extensões e hooks.
`--permission-mode plan` ou `--sandbox read-only`, isoladamente, não demonstram essa
garantia para toda a integração. Os modos indisponíveis ficam desabilitados e são
rejeitados no backend antes de iniciar processos/requisições.

Na API, não há executor de tool calls, acesso ao sistema de arquivos ou comandos;
Plan também evita o resumidor CLI. A persistência normal do histórico do Taverna
continua funcionando. A API não recebe acesso direto aos arquivos do projeto.

## Aplicação de Auto

- Claude: preserva `--restricted` e `acceptEdits`; explicita
  `--tools Read,Glob,Grep,Edit,Write,Bash,PowerShell` e a mesma lista em
  `--allowedTools`. `--permission-prompts none` nega operações que ainda exijam
  aprovação, em vez de bloquear aguardando um host inexistente. Esses argumentos
  foram confirmados em `claude --help` local. Comandos não têm isolamento de SO;
  não se usa `bypassPermissions` ou `--dangerously-skip-permissions`.
- Codex: preserva `--sandbox workspace-write` e usa
  `-c approval_policy="never"`. Não concede escalonamento ou desativa o sandbox.
  O isolamento efetivo continua sendo responsabilidade da configuração/suporte do
  CLI na máquina; o Taverna não implementa um sandbox próprio.
- Copilot: `--allow-tool read,write,shell`, preservando verificações de caminhos,
  URLs e regras de negação. Não usa `--allow-all` ou `--allow-all-paths`.
  Confirmado na [referência oficial programática](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference).
  Copilot não está instalado neste ambiente, portanto não houve validação com provedor real.

As mudanças de Auto aplicam-se ao Chat, preservando o comportamento anterior de
Tasks e Summary. Modelo, effort, anexos e cancelamento continuam nos mesmos runners.
Nenhum hook é tratado como isolamento de segurança.

## Preferência e validação

Preferências são salvas por agente em `%APPDATA%/Taverna/permissions.json`.
Sem escolha salva, os agentes CLI começam em Auto (pedido do usuário: evita
reselecionar a cada sessão; lembre que Auto não tem sandbox do SO). A API usa Plan. Preferências
inválidas/não suportadas são descartadas. Trocar de agente restaura a escolha;
mudanças não alteram uma execução em andamento.

Testes cobrem o encaminhamento dos modos/modelo/effort, rejeição de modos
indisponíveis, persistência, ausência de resumidor CLI em Plan, abertura/fechamento,
seleção, digitação após fechar e redimensionamento dos popovers. Os testes de UI
usam Avalonia Headless com a árvore real de controles e renderização Skia.

```powershell
dotnet build Taverna.sln -c Release
dotnet test Taverna.sln -c Release
```

Release permite validar enquanto a cópia Debug está aberta. Os testes não enviam
prompts a provedores reais. A captura nativa do Windows falhou com
`SetIsBorderRequired / 0x80004002`; a inspeção visual foi feita nas renderizações
dos testes, além da leitura da árvore de acessibilidade da janela real.
