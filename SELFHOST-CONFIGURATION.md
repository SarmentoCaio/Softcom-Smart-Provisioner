# Configuração do SelfHost pelo Provisioner

## SelfHost 4.0 validado

A implementação foi conferida na instalação `4.0.0.11`. A geração é definida pela versão do `SelfHost.exe`, mesmo quando a pasta contém artefatos residuais de outras versões.

O fluxo oficial do `Selfhost.Gerenciador` é:

```text
URL de vínculo do dispositivo raiz
→ SoftcomshopDeviceHelper.LoadUrlData
→ SoftcomshopDeviceHelper.AddRootDevice
→ SoftcomshopService.AddRootDevice
→ POST /softauth/device/add
→ ConfiguracoesController.CarregarConfig
→ alteração do objeto existente
→ ConfiguracoesController.GravarConfig
→ Config.json + Config2.json
```

O cadastro inicial do dispositivo raiz continua sendo feito pela página oficial do Softcomshop em `/softauth/device/salvar`. O vínculo posterior do dispositivo com o computador do SelfHost usa `/softauth/device/add`, `Environment.MachineName` e recebe a credencial gerada pelo servidor.

O dispositivo raiz é um cadastro normal. Dispositivos cujo nome começa com `SELFHOST_` são filhos administrados pelo SelfHost e não são oferecidos como raiz.

## Arquitetura

```text
Provisioner .NET 8
→ JSON por STDIN/STDOUT, sem segredo em argumentos
→ SelfHostBridge .NET 10 isolado
→ SelfHost.API.dll da instalação real
→ ConfiguracoesController.CarregarConfig/GravarConfig
```

O Bridge não recebe nem devolve `client_secret`. Na configuração real, ele chama `SoftcomshopService.AddRootDevice`, mantém a credencial somente em memória, grava pelo controller oficial, relê e valida os campos persistidos.

Antes de um `configure`, o Provisioner atualiza o cadastro raiz no Softcomshop. Se ele já possuir `device_id`, chama o fluxo oficial `/softauth/device/desvincular`, consulta novamente e só continua depois de confirmar que o vínculo anterior desapareceu. O Bridge então executa `AddRootDevice` mesmo quando o mesmo `client_id` já estava salvo localmente, pois o novo vínculo devolve uma nova credencial.

## Diferença de armazenamento por geração

- SelfHost 4.0: o salvamento oficial cria/atualiza `Config.json` e `Config2.json` na raiz da instalação. Em uma instalação nunca configurada esses arquivos podem ainda não existir; nesse caso o Provisioner registra um ponto de restauração sem estado anterior e permite a primeira configuração.
- SelfHost 4.1+: o salvamento oficial usa `data\\selfhost-config.db`; o Provisioner não reutiliza `Config2.json` nessa geração.

Arquivos `data\\selfhost-config.db` ou `binaries\\appsettings.json` eventualmente presentes em um pacote cuja versão executável ainda é 4.0 não mudam sua classificação: a versão do binário define o fluxo.

## Campos alterados

- `TipoBancoDados`, com o valor oficial `Softcomshop (Web)`;
- `PortaHTTP`;
- `SoftcomShopUrl` e `SoftcomShopUrlBase`;
- `SoftcomShopEmpresa`;
- `SoftcomShopDevice` e `SoftcomShopDeviceId`;
- `SoftcomShopClientId` e `SoftcomShopSecretId`;
- `SoftcomShopSituacao`;
- `DevicesEnabled`;
- `SmartEnabled`.

Quando o módulo selecionado é **Smart Comanda**, o Provisioner também exige e grava,
pelos mesmos métodos oficiais, os campos usados pela tela “Configuração da Comanda”
do Gerenciador:

- `MysqlServidor`;
- `MysqlPorta`;
- `MysqlUsuario`;
- `MysqlSenha`;
- `MysqlDatabase`.

A senha nunca volta para a interface. A leitura informa apenas se ela está presente;
se o campo ficar vazio em uma reconfiguração, o valor existente é preservado. Para
outros módulos, esses cinco campos não são enviados nem alterados.

Todos os demais campos do objeto carregado são preservados, incluindo relay e módulos não solicitados.

## Retaguarda Softshop Desktop

Na versão `4.1.0.16`, o Gerenciador oferece oficialmente duas retaguardas por meio
do enum `ERPType`: `Softcomshop (Web)` e `Softshop (Desktop)`. No modo Desktop,
`MainHomeViewModel` usa `DatabaseConnectionControlViewModel` e persiste:

- `TipoBancoDados = Softshop (Desktop)`;
- `Servidor`;
- `Porta` (opcional quando a instância já está no nome do servidor);
- `Usuario`;
- `Senha`;
- `BancoDados`.

O Provisioner permite escolher a retaguarda no painel expansível. A senha SQL é
enviada apenas por STDIN ao Bridge, não é salva nas configurações do Provisioner,
não volta na resposta e pode ser deixada vazia para preservar uma senha já gravada.
O nome padrão do usuário pode ser sugerido pela interface, mas nenhuma senha é
embutida no código.

No modo Desktop não existe dispositivo raiz do Softcomshop. Por isso o Bridge não
executa registro/desvinculação remota e valida a inicialização pelos dois healthchecks
locais. A criação/listagem dos dispositivos Desktop continua sendo um fluxo separado,
feito pelo Gerenciador no banco Softshop por `ContextoRemoto` e `SH_Dispositivos`.

## Segurança operacional

Antes de configurar, o Provisioner exige o Gerenciador fechado, para o MonitorService, encerra somente o `SelfHost.exe` da instalação detectada e cria backup de `Config.json`, `Config2.json` e configurações auxiliares existentes. Depois grava, relê, reinicia o serviço com timeout e valida sem registrar tokens ou credenciais.

No SelfHost 4.0, as credenciais raiz autenticam diretamente no Softcomshop. Por isso, a validação final usa os healthchecks locais e confirma autenticação/empresa pela API do Softcomshop. A autenticação local em `/authentication/token` é reservada aos dispositivos filhos. No SelfHost 4.1+, permanece a validação local prevista para essa geração.

## Ações

- `read`: leitura oficial e resposta sanitizada;
- `preview`: cálculo sanitizado, sem gravação e sem vínculo remoto;
- `configure`: vínculo raiz, merge, gravação oficial e releitura;
- `validate`: healthchecks e validação operacional sem devolver token.

Nenhuma ação imprime `client_secret`, token, autorização, cookie, senha ou chave de banco.
