# Histórico de versões

Este arquivo registra somente as versões distribuídas a partir da 1.0.0. Alterações anteriores foram consolidadas na primeira versão oficial.

## 1.0.4.1

- No fluxo Softcomshop Online, a verificacao de vinculos consulta somente o cliente selecionado; a API SelfHost e consultada apenas quando o vinculo SelfHost esta selecionado.
- O Getnet DX8000 com Smart 8.0 captura o Device ID na tela de selecao do modulo, antes da etapa de cadastro.
- O atualizador e a publicacao passaram a aceitar versoes com quatro componentes.

## 1.0.4

- Interface reorganizada para destacar o fluxo Online e reduzir informações técnicas no uso normal.
- Removida a página Android duplicada; seleção, atualização e scrcpy permanecem na tela Provisionar.
- O modo VPN local foi removido da interface; o acesso direto usa somente o Docker isolado.
- No modo Docker, basta informar o cliente: o Provisioner procura automaticamente em AWS 1 e AWS 2.
- Credenciais, VPN, package do Smart e diagnóstico local foram agrupados em **Opções avançadas**.
- URLs dos manifestos de atualização passaram a ficar recolhidas, mantendo canal e automação como preferências principais.
- Removido o botão diagnóstico **Gerar URL**; **Validar** e **Preparar Smart** continuam montando a configuração necessária.
- Removidos valores fixos dos campos do Smart TEF; os dados devem ser informados para cada configuração e o token é mascarado.
- Adicionada identificação de adquirente e modelo pelo catálogo de UDIDs dos testes, com prioridade sobre o modelo Android reportado.
- Adicionado o Totem K2 de autoatendimento ao catálogo, preservando SUNMI K2_MINI como informação Android complementar.
- Adicionada opção explícita de multidispositivo, com seleção de ADBs e um cadastro diferente por maquineta.
- Cliente e empresa são recolhidos após a seleção, mantendo o foco nas próximas etapas.
- Adicionados package, status, tempo e logs isolados por dispositivo, além de cancelamento individual e geral.
- Expandido o diagnóstico por aparelho com Android/SDK, resolução, densidade, package/versão do Smart, Activity e permissões relevantes.
- No SelfHost 4.0, a relação multidispositivo usa a configuração raiz do Config2.json e serializa os jobs, mantendo um dispositivo filho distinto para cada Android.

## 1.0.3

- Adicionada compatibilidade do fluxo de provisionamento com SelfHost 4.1+.
- Mantida a detecção da geração instalada do SelfHost para selecionar o fluxo compatível.
- Atualizadas listagem, criação, desvinculação e validação de dispositivos SelfHost.
- Adicionado suporte aos fluxos do Smart 8.0 e 8.1+, com seleção automática conforme a versão detectada no Android.
- Incluídos tratamentos específicos para telas, módulos e fatores de forma distintos.
- Adicionadas pesquisa de dispositivos e administração dos clientes Online salvos.
- Melhorados diagnósticos ADB, repetição controlada de consultas e mensagens do resultado final.

## 1.0.2

- Adicionado o fluxo de publicação automática por GitHub Actions.
- Criado o script `PUBLICAR-ATUALIZACAO.bat` para atualizar versões, enviar a branch e criar a tag de release.
- Automatizadas a compilação self-contained, a geração do ZIP, o cálculo de SHA-256, a GitHub Release e a atualização de `update/latest.json`.
- Melhorado o Updater para preservar a instalação anterior e restaurá-la quando uma atualização falhar.
- Centralizada a versão usada pelos metadados da aplicação e pelas chamadas do Softcomshop.
- Adicionados os manifestos padrão de atualização aos arquivos publicados.

## 1.0.1

- Simplificada a escolha de vínculo por SelfHost para os módulos comuns com a opção compacta **Usar SelfHost**.
- Mantido o SelfHost obrigatório para Comanda e Autopagamento e o fluxo próprio do Smart TEF.
- Adicionado o botão **+ Novo** ao seletor de dispositivos SelfHost.
- Implementada a criação de dispositivo SelfHost com nome, série NFC-e e numeração inicial informados pelo usuário.
- Adicionado o vínculo opcional de NF-e ao mesmo dispositivo.
- Melhorado o recarregamento da lista após a criação para selecionar o novo cadastro mesmo com atraso da API.

## 1.0.0

- Primeira versão oficial distribuída do Softcom Smart Provisioner.
- Fluxo Online pelo Softcomshop para selecionar cliente, empresa e dispositivo sem VPN ou acesso direto ao banco.
- Criação e reutilização de dispositivos, séries fiscais e validação do vínculo.
- Preparação automatizada do Smart e do Smart TEF por ADB, com scrcpy integrado.
- Suporte inicial ao vínculo por SelfHost já instalado e configurado.
- Modos avançados de acesso direto ao banco por Docker isolado e VPN local.
- Armazenamento local protegido de configurações e credenciais.
- Mecanismo inicial de atualização automática com verificação de integridade do pacote.
