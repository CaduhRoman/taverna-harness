# CLIs verificadas em 14/10/2026 (Windows 10)

O controle de permissões do Chat e seus argumentos adicionais estão descritos em
[chat-permissions.md](chat-permissions.md). As invocações abaixo registram o
comportamento base; Tasks mantém esse comportamento.

Ajuda consultada: `claude --help` (2.1.296), `codex exec --help` (codex-cli 0.154.0), `gh copilot --help`.

## Comum a todos os runners

- O prompt vai por **stdin** (Claude, Codex), nunca na linha de comando: evita o limite de ~32k caracteres do Windows e problemas de quoting.
- stdin é sempre redirecionado e fechado; o Codex bloqueia esperando stdin quando ele fica aberto.
- stderr é tratado como diagnóstico (`Log`), não como erro: as duas CLIs escrevem avisos ali. O resultado vem do código de saída.
- Modelo e effort só passam se forem identificadores simples (`[A-Za-z0-9._:/\[\]-]`).
- Prompts nunca são passados como argumento para shims `.cmd`/`.bat`, pois o `cmd.exe` reinterpreta `&`, `|` etc.
- No Windows, os processos dos agentes ficam num Job Object com `KILL_ON_JOB_CLOSE`: se o Taverna morrer, a árvore inteira morre junto. No Linux não há equivalente; um encerramento abrupto pode deixar o agente órfão.
- Cancelar mata a árvore de processos (`Process.Kill(entireProcessTree: true)`).

## Claude Code — instalado, autenticado, testado

```
claude --print --input-format stream-json --output-format stream-json --include-partial-messages --verbose
       --no-session-persistence --restricted --permission-mode acceptEdits [--model M] [--effort E]
```

- stdin recebe uma mensagem `{"type":"user","message":{"role":"user","content":[...]}}`.
- **Imagens confirmadas**: blocos `{"type":"image","source":{"type":"base64",...}}` no mesmo conteúdo. Testado: o ícone da Taverna foi identificado como "T".
- `--restricted` remove as ferramentas que executam comandos (Bash, PowerShell, REPL) e o WebFetch; `acceptEdits` aprova edições de arquivos no diretório de trabalho. No resumo de contexto usa `dontAsk`.
- Modelos (`--help`): aliases `fable`, `opus`, `sonnet`, `haiku` ou nome completo. Effort: `low|medium|high|xhigh|max`.
- O seletor usa o catálogo que o próprio Claude Code guarda para a conta (`~/.claude/cache/model-catalog/*.json`, ou em `CLAUDE_CONFIG_DIR`; dá preferência ao arquivo `-cc` mais recente): seção `main` primeiro, depois os modelos antigos, com o nome de exibição ("Opus 5.5") e os efforts de cada um. Variantes extras como `claude-fable-5-1[1m]` vêm de `additionalModelOptionsCache` em `~/.claude.json`. Sem catálogo, voltam os aliases.
- Efforts são filtrados por modelo (Haiku 4.5 não aceita effort); uma combinação inválida é recusada antes de iniciar o processo.
- Tokens vêm de `result.usage` (`input_tokens`, `output_tokens`, `cache_read_input_tokens`).
- Testes reais: chat com streaming, Stop no meio de uma resposta (sem processo órfão), tarefa em worktree → Done, Merge e Discard.

## Codex CLI — instalado; invocação testada, modelo indisponível neste ambiente

```
codex exec --json --skip-git-repo-check --sandbox workspace-write -C <dir> [-m M]
           [-c model_reasoning_effort="E"] [--image <file>]... -
```

- `-` lê o prompt do stdin (documentado no `--help`).
- Imagens via `--image` (documentado); o envio de ponta a ponta **não** pôde ser validado.
- Modelos e efforts vêm de `CODEX_HOME` (padrão `~/.codex`): `models_cache.json` (só `visibility: "list"`, mantido pelo próprio Codex) e o `model` de `config.toml`, que aparece primeiro. Isso inclui os modelos `chatgpt-web/*` da bridge. Sem cache, os efforts voltam a `minimal|low|medium|high|xhigh`.
- Cada modelo aceita só os próprios efforts (`chatgpt-web/gpt-6-sol`: `medium|high`; por isso `low` era rejeitado). Um effort incompatível é recusado antes de iniciar o processo.
- Se `openai_base_url` aponta para loopback (bridge local), o Taverna testa a porta antes de iniciar; se nada escuta ali, falha na hora em vez de ficar em "Reconnecting..." / "waiting for network".
- Eventos `type:"error"` com "Reconnecting..." são transitórios e vão para o log; `turn.failed` é a falha real.
- **Limitação do ambiente**: o `~/.codex/config.toml` desta máquina usa `chatgpt-web/gpt-6-sol` por um bridge local (`127.0.0.1:17841`), que exige uma aba do ChatGPT aberta. Nos testes o turno sempre terminou em "ChatGPT stopped responding". O Taverna exibe essa falha corretamente, mas nenhuma resposta do Codex foi obtida.

## GitHub Copilot CLI — ausente, NÃO verificado

- `copilot` não está no PATH. `gh copilot` apenas baixaria a CLI; isso não foi feito.
- O runner segue a documentação pública: `copilot -p <prompt> --no-color [--allow-tool write] [--model M]`, com saída em texto puro. **Nenhuma dessas flags foi validada.**
- Imagens desabilitadas: não há mecanismo de entrada confirmado.
- Usado também para resumir o contexto antigo do chat. Sem a CLI, o contexto é truncado (comportamento testado).

## API externa (compatível com OpenAI) — testada só contra um servidor local falso

- Configuração por variáveis de ambiente: `TAVERNA_API_BASE_URL` (ex.: `https://api.openai.com/v1`), `TAVERNA_API_KEY` e `TAVERNA_API_MODELS` (separados por vírgula; o primeiro é o padrão).
- `POST {base}/chat/completions` com `stream: true`, SSE, `reasoning_effort` opcional. Apenas chat: não acessa arquivos e não roda tarefas.
- Sem chave real neste ambiente, nenhum provedor real foi testado.

## Contexto do chat

- Limite padrão de 24.000 caracteres, configurável por `TAVERNA_CONTEXT_MAX_CHARS` (mínimo 1.000).
