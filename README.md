# Softcom Smart Provisioner

Aplicativo Windows usado pela equipe para preparar, vincular e validar dispositivos do Softcom Smart. A interface reúne o acesso ao Softcomshop, a seleção da empresa e do dispositivo, a identificação do Android por ADB e a automação do Smart.

Versão atual: **1.0.4.2**. O histórico está no [CHANGELOG.md](CHANGELOG.md).

## Fluxos suportados

- Softcomshop Online, fluxo padrão, sem acesso direto a MySQL ou VPN.
- Vínculo por SelfHost para os módulos compatíveis, incluindo Comanda e Autopagamento.
- Identificação de adquirente/modelo por UDID e seleção de vários Androids para provisionamento.
- Criação e seleção de dispositivos, com configuração de séries NFC-e e NF-e quando aplicável.
- Preparação do Smart padrão e do Smart TEF por ADB.
- Detecção das versões do Smart e do SelfHost para escolher o fluxo de automação compatível.
- Validação do vínculo depois da preparação.
- Abertura do Android selecionado no scrcpy.
- Atualização automática do Provisioner.
- Acesso direto ao banco pelo Docker isolado, com descoberta automática entre AWS 1 e AWS 2.

## Requisitos de uso

- Windows 10 ou 11 x64.
- Microsoft Edge WebView2 Runtime.
- Android com depuração USB habilitada ou emulador acessível por ADB.
- SelfHost instalado quando o vínculo dos módulos compatíveis utilizar esse fluxo.
- Docker Desktop para o modo avançado Docker isolado.
- OpenVPN e perfil válido para a conexão isolada dentro do Docker.

Os binários de ADB e scrcpy necessários à aplicação são distribuídos com o pacote.

## Uso rápido

1. Abra o Provisioner e mantenha o modo **Online** no fluxo normal.
2. Informe o cliente e conclua o login no Softcomshop caso a sessão tenha expirado.
3. Selecione a empresa e o módulo do Smart.
4. Escolha um dispositivo existente ou crie um novo.
5. Ative **Usar SelfHost** somente quando o vínculo precisar passar pelo SelfHost.
6. Conecte os Androids e clique em **Atualizar**. Para vários aparelhos, ative **Provisionar vários dispositivos** e associe um cadastro diferente a cada Android.
7. Use **Validar** para conferir o cenário.
8. Use **Preparar Smart** para executar a automação e validar o vínculo final.

Para Smart TEF, informe na própria tela o nome do dispositivo, CNPJ, empresa e token fornecidos para a configuração. Esses dados não possuem valores fixos na interface.

### Primeiro acesso aos testes

Na aba **Testes do Smart**, se o projeto Automation ainda não existir no computador, use **Baixar Automation dev**. O Provisioner usa o Git e o acesso individual ao repositório privado para criar um clone em `%LOCALAPPDATA%\Softcom\SmartProvisioner\automation`. Se o projeto já estiver disponível localmente, ele é reutilizado. O Provisioner não instala Git, uv ou Appium automaticamente; a aba mostra os pré-requisitos ausentes.

O `.env` não acompanha o clone nem a atualização pública. Importe um `.env` já configurado pelo botão da aba ou abra a pasta do Automation e crie um a partir de `.env.example`, preenchendo as credenciais e o UDID do dispositivo localmente. Um `.env` existente nunca é sobrescrito pela importação ou pela atualização do código.

Para atualizar os testes, informe ou escolha uma branch remota e clique em **Fazer pull da branch**. O Provisioner busca as branches, muda para a escolhida e aceita somente atualização por avanço rápido. Se houver alterações locais ou divergência, ele interrompe a operação sem descartá-las. O código privado e o `.env` não são incluídos no pacote de releases do Provisioner.

## Organização da interface

- **Provisionar:** fluxo principal de cliente, empresa, dispositivo, Android e preparação.
- **Logs:** eventos técnicos da sessão, sem expor credenciais.
- **Configurações:** clientes Online salvos e preferências de atualização.
- **Opções avançadas:** credenciais de banco, perfil VPN, package do Smart e diagnóstico local.

O modo de acesso fica recolhido. O fluxo normal usa Online; quando for necessário banco direto, o Docker mantém a VPN fora do Windows e localiza o cliente em AWS 1 ou AWS 2.

## Dados locais e segurança

Os dados do usuário ficam em `%LOCALAPPDATA%\Softcom\SmartProvisioner`, incluindo configurações, logs, perfil do WebView2 e segredos do Provisioner protegidos com DPAPI para o usuário atual do Windows. O `.env` do Automation é uma exceção: o runner precisa lê-lo em texto legível; mantenha-o somente no computador do usuário e não o distribua.

- Credenciais, tokens e cookies não devem ser incluídos em logs ou documentação.
- A sessão Online usa o perfil local do WebView2 e os mecanismos de autenticação do próprio Softcomshop.
- A atualização preserva configurações, sessão e logs locais.
- O Provisioner não deve ser distribuído apenas como executável; use o pacote completo publicado.

## Desenvolvimento

Requisitos:

- .NET 8 SDK.
- Windows x64.

Para restaurar e compilar:

```powershell
dotnet restore
dotnet build Softcom-Smart-Provisioner.sln
```

Para compilar e executar a aplicação localmente, use `EXECUTAR.bat`.

Projetos principais:

- `src/SoftcomSmartProvisioner`: aplicação WinForms com interface WebView2.
- `src/SoftcomSmartProvisioner.Updater`: atualizador externo.
- `tests`: testes do catálogo de dispositivos, ADB e execução multi-dispositivo.
- `tools`: ADB, scrcpy e componentes auxiliares empacotados.
- `tools/db-bridge`: suporte ao modo Docker isolado.

## Publicação

### Pacote local

Execute `PUBLICAR-WINDOWS.bat`. O resultado será criado em `publish\win-x64`. Distribua a pasta inteira.

### Atualização pelo GitHub

Execute `PUBLICAR-ATUALIZACAO.bat` e informe a nova versão no formato `X.Y.Z`. O script atualiza os metadados, envia a branch e cria a tag usada pelo workflow `.github/workflows/release.yml`.

O workflow compila o Provisioner e o Updater, gera o ZIP, calcula o SHA-256, publica a GitHub Release e atualiza `update/latest.json`.

O manifesto do canal estável é fornecido em `src/SoftcomSmartProvisioner/update-defaults.json`. A edição manual dos manifestos na tela fica disponível apenas como opção avançada.

## Histórico

Consulte [CHANGELOG.md](CHANGELOG.md) para as alterações a partir da versão 1.0.0.
