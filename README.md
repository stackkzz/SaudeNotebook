# Saúde do Notebook

Monitor de saúde, desempenho e segurança para Windows 10/11.

## Instalar

1. Baixe o `SaudeNotebook.exe` da [versão mais recente](https://github.com/stackkzz/SaudeNotebook/releases/latest).
2. Dê dois cliques. Se o Windows mostrar "O Windows protegeu o computador", clique em **Mais informações** > **Executar assim mesmo** (o app ainda não tem assinatura digital paga).
3. Responda **Sim** para instalar. Ele vai para `%LOCALAPPDATA%\Programs\SaudeNotebook` e cria atalhos.

Depois disso o app se atualiza sozinho.

## O que ele faz

- **Agora:** CPU, RAM, GPU, disco, rede e bateria ao vivo, gráfico e alertas
- **Processos:** quem consome mais, agrupado por programa, com marcação de mineradores e vazamentos de memória
- **Engasgos:** o que pode estar travando o PC agora, com a memória em detalhe
- **Segurança e saúde:** antivírus, firewall, mineradores, sinais de invasão, inicialização, discos, bateria e travamentos
- **Claude:** botões que copiam o problema pronto para colar numa conversa com o Claude

Nada é enviado para a internet, exceto a checagem de versão nova no GitHub.

## Publicar uma versão nova

1. Mude o número no arquivo `VERSAO` (ex.: `1.0.1`).
2. Escreva as novidades em `notas/v1.0.1.md`.
3. Faça commit e push na `main`.

O GitHub Actions compila o `.exe`, cria a tag e a Release. Quem já tem o app recebe o aviso de atualização.
